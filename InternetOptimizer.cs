using System.Net;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Fakeable boundary. Capture/probes are explicit; mutations must verify exact identity and readback.</summary>
public interface INetworkTestSystem
{
    void AssertOutsideGame();
    InternetNicSnapshot CaptureBaseline(InternetTestOptions options);
    void ApplyCandidate(InternetNicSnapshot baseline, InternetCandidate candidate);
    void RestoreBaseline(InternetCalibrationState state);
    bool VerifyBaseline(InternetNicSnapshot baseline);
    Task<InternetMeasurement> MeasureAsync(InternetNicSnapshot baseline, InternetTestOptions options, string label, CancellationToken cancellationToken);
}

/// <summary>
/// Explicit A/B/A calibration. No constructor work, automatic probing or best-setting activation.
/// Root supplies durable shared-journal persistence and its operation/game/restore-point gate.
/// </summary>
public sealed class InternetOptimizer
{
    private static readonly SemaphoreSlim Serial = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { IgnoreReadOnlyProperties = true };
    private readonly INetworkTestSystem system;
    public event Action<InternetTestProgress>? Progress;

    public InternetOptimizer(INetworkTestSystem system) => this.system = system ?? throw new ArgumentNullException(nameof(system));

    public async Task<InternetTestReport> CalibrateAsync(InternetTestOptions options, Action<InternetCalibrationState> persist,
        Action outsideGameGate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persist); ArgumentNullException.ThrowIfNull(outsideGameGate);
        options = NormalizeOptions(options);
        await Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.OverallTimeoutSeconds));
        var token = deadline.Token;
        InternetCalibrationState? state = null;
        var report = new InternetTestReport { Options = options };
        bool mutationMayHaveRun = false;
        Exception? failure = null;
        try
        {
            Gate(outsideGameGate, token);
            Publish("Discovering", "Reading the current physical wired route and supported NIC capabilities.", 0, 0);
            var baseline = Clone(system.CaptureBaseline(options));
            ValidateSnapshot(baseline);
            report.Baseline = baseline;
            report.Limitations.AddRange(baseline.Limitations);
            report.Limitations.Add("ICMP round trips include NIC, router, path and scheduling. CPU/DPC percentages are context, not NIC processing latency.");
            report.Limitations.Add("Actual NIC processing remains unknown without a valid trace. No result establishes Fortnite server ping or click-to-photon latency.");
            var candidates = InternetScoring.BuildCandidates(baseline, options.Mode).Where(c => !c.IsBaseline)
                .Take(options.MaximumCandidates).Select(Clone).ToList();
            if (options.EnableHttpsLoad)
            {
                long plannedBytes = checked((long)candidates.Count * options.Repetitions * 3 * options.HttpsLoadBytesPerMeasurement);
                if (plannedBytes > options.MaximumHttpsLoadBytesTotal)
                    throw new InvalidOperationException("The requested matched load periods exceed the total HTTPS byte budget. Reduce candidates/repetitions or bytes per measurement.");
            }
            state = new() { Baseline = Clone(baseline), Phase = "Captured", Restored = true };
            // This callback must atomically preserve originals in the shared recovery journal.
            persist(state);
            int total = candidates.Count * options.Repetitions, completed = 0;
            foreach (var candidate in candidates)
            {
                ValidateCandidate(baseline, candidate);
                for (int repetition = 0; repetition < options.Repetitions; repetition++)
                {
                    Gate(outsideGameGate, token);
                    if (!system.VerifyBaseline(baseline)) throw new InvalidOperationException("The recorded baseline settings changed; calibration stopped without replacing external changes.");
                    var trial = new InternetTrial { Candidate = Clone(candidate) };
                    report.Trials.Add(trial);
                    string label = candidate.Id + " / repetition " + (repetition + 1);
                    Publish("Baseline A", label + ": measuring the original settings.", completed, total);
                    trial.BaselineBefore = await system.MeasureAsync(baseline, options, label + " / A-before", token).ConfigureAwait(false);
                    if (!trial.BaselineBefore.Valid)
                    { trial.Error = "The baseline period was invalid; no candidate was applied."; completed++; continue; }
                    try
                    {
                        Gate(outsideGameGate, token);
                        state.PendingCandidate = Clone(candidate); state.MutationAttempted = true;
                        state.Restored = false; state.Phase = "Applying"; state.LastError = "";
                        try { persist(state); }
                        catch
                        {
                            // No driver operation follows a failed write-ahead persistence call.
                            state.MutationAttempted = false; state.Restored = true; state.Phase = "Not applied";
                            throw;
                        }
                        Gate(outsideGameGate, token);
                        Publish("Candidate B", label + ": applying supported values; the link may reconnect.", completed, total);
                        mutationMayHaveRun = true;
                        system.ApplyCandidate(baseline, candidate);
                        trial.Applied = true; state.Phase = "Measuring"; persist(state);
                        trial.Treatment = await system.MeasureAsync(baseline, options, label + " / B", token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { trial.Canceled = true; throw; }
                    catch (Exception ex) { trial.Error = ex.Message; throw; }
                    finally
                    {
                        if (mutationMayHaveRun)
                        {
                            trial.RestoreAttempted = true;
                            Publish("Restoring", label + ": restoring the exact recorded baseline before any further test.", completed, total);
                            // A failed rollback remains journaled; do not immediately replay an
                            // uncertain driver operation from the outer cleanup path.
                            mutationMayHaveRun = false;
                            RestoreCore(state, persist, outsideGameGate);
                            trial.RestoreVerified = true;
                        }
                        else if (!state.Restored)
                        {
                            // Cancellation/game start occurred after persistence but before any mutation.
                            state.Restored = true; state.Phase = "Not applied"; persist(state);
                        }
                    }
                    Gate(outsideGameGate, token);
                    trial.BaselineAfter = await system.MeasureAsync(baseline, options, label + " / A-after", token).ConfigureAwait(false);
                    completed++;
                    Publish("Compared", label + ": baseline restored; comparison recorded.", completed, total);
                }
            }
            report.Completed = true;
            report.RollbackVerified = state.Restored && system.VerifyBaseline(baseline);
            if (!report.RollbackVerified) throw new InvalidOperationException("Final original settings could not be verified.");
            if (candidates.Count == 0) report.Limitations.Add("No non-baseline candidate had sufficient advertised capabilities for a safe test.");
        }
        catch (OperationCanceledException ex)
        {
            report.Canceled = true; failure = ex;
            report.Limitations.Add(cancellationToken.IsCancellationRequested ? "Canceled by request; originals were restored where a mutation had begun."
                : "The bounded calibration deadline expired.");
        }
        catch (Exception ex) { failure = ex; report.Limitations.Add(ex.Message); }
        finally
        {
            try
            {
                if (state != null && mutationMayHaveRun)
                {
                    RestoreCore(state, persist, outsideGameGate);
                    mutationMayHaveRun = false;
                }
                if (state != null)
                {
                    report.RollbackVerified = state.Restored && system.VerifyBaseline(state.Baseline);
                    state.Phase = report.RollbackVerified ? "Restored" : "Recovery required";
                    state.LastError = failure?.Message ?? "";
                    persist(state);
                }
            }
            catch (Exception restoreError)
            {
                if (state != null)
                {
                    state.Restored = false; state.Phase = "Recovery required"; state.LastError = restoreError.Message;
                    try { persist(state); } catch { }
                }
                report.RollbackVerified = false;
                failure = new NetworkTransitionException("Internet calibration requires recovery. The saved original NIC state must be retained.", true,
                    failure == null ? restoreError : new AggregateException(failure, restoreError));
            }
            finally { Serial.Release(); }
        }
        if (failure is NetworkTransitionException { RecoveryRequired: true }) throw failure;
        InternetScoring.Score(report);
        return report;
    }

    /// <summary>Separate explicit action after a fresh review. Unknown-processing provisional results need a separate opt-in.</summary>
    public async Task<InternetCalibrationState> ApplyBestAsync(InternetTestReport report, Action<InternetCalibrationState> persist,
        Action outsideGameGate, CancellationToken cancellationToken = default, bool allowProvisional = false)
    {
        ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(persist); ArgumentNullException.ThrowIfNull(outsideGameGate);
        report = Clone(report);
        InternetScoring.Score(report);
        if (!report.Completed || !report.RollbackVerified || report.Canceled || report.CreatedUtc > DateTime.UtcNow.AddMinutes(1)
            || DateTime.UtcNow - report.CreatedUtc > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("Review a fresh, completed and restored calibration before applying a result.");
        var selected = report.CanApplyBest ? report.Winner : allowProvisional ? report.ProvisionalWinner : null;
        if (selected == null || selected.IsBaseline) throw new InvalidOperationException("No qualified tested change is available. Keep the originals, or explicitly review a disclosed provisional result.");
        ValidateCandidate(report.Baseline, selected);
        await Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        var state = new InternetCalibrationState();
        bool mutationMayHaveRun = false;
        try
        {
            Gate(outsideGameGate, cancellationToken);
            var fresh = system.CaptureBaseline(NormalizeOptions(report.Options)); ValidateSnapshot(fresh);
            if (!SameCapturedState(report.Baseline, fresh))
                throw new InvalidOperationException("The adapter, driver, route or original settings changed; run a fresh calibration.");
            selected = Clone(selected); selected.BaselineFingerprint = fresh.Fingerprint;
            ValidateCandidate(fresh, selected);
            state.Baseline = Clone(fresh); state.PendingCandidate = selected;
            state.MutationAttempted = true; state.Restored = false; state.Phase = "Applying reviewed result";
            persist(state);
            Gate(outsideGameGate, cancellationToken);
            mutationMayHaveRun = true;
            system.ApplyCandidate(fresh, selected);
            state.Phase = allowProvisional && !report.CanApplyBest ? "Applied provisional result" : "Applied reviewed result";
            persist(state);
            return state;
        }
        catch
        {
            if (mutationMayHaveRun) RestoreCore(state, persist, outsideGameGate);
            else if (state.MutationAttempted && !state.Restored)
            {
                state.MutationAttempted = false; state.Restored = true; state.Phase = "Not applied";
                persist(state);
            }
            throw;
        }
        finally { Serial.Release(); }
    }

    public async Task RestoreAsync(InternetCalibrationState state, Action<InternetCalibrationState> persist,
        Action outsideGameGate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(persist); ArgumentNullException.ThrowIfNull(outsideGameGate);
        await Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { RestoreCore(state, persist, outsideGameGate); }
        finally { Serial.Release(); }
    }

    private void RestoreCore(InternetCalibrationState state, Action<InternetCalibrationState> persist, Action outsideGameGate)
    {
        if (!state.MutationAttempted || state.Restored) return;
        try
        {
            // User cancellation cannot bypass rollback. A newly running game blocks disruptive writes;
            // the durable journal then remains pending until the client has fully exited.
            outsideGameGate(); system.AssertOutsideGame();
            state.Phase = "Restoring";
            try { persist(state); } catch { /* Existing write-ahead originals remain authoritative. */ }
            system.RestoreBaseline(state);
            if (!system.VerifyBaseline(state.Baseline)) throw new InvalidOperationException("Original NIC settings did not pass readback.");
            state.Restored = true; state.Phase = "Restored"; state.LastError = "";
            persist(state);
        }
        catch (Exception ex)
        {
            state.Restored = false; state.Phase = "Recovery required"; state.LastError = ex.Message;
            try { persist(state); } catch { }
            throw new NetworkTransitionException("Exact NIC restoration is not durably verified. Keep the recovery journal.", true, ex);
        }
    }

    private void Gate(Action gate, CancellationToken token)
    { token.ThrowIfCancellationRequested(); gate(); system.AssertOutsideGame(); token.ThrowIfCancellationRequested(); }
    private void Publish(string phase, string message, int completed, int total)
    { try { Progress?.Invoke(new() { Phase = phase, Message = message, Completed = completed, Total = total }); } catch { } }
    internal static InternetTestOptions NormalizeOptions(InternetTestOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = Clone(source);
        if (!Enum.IsDefined(options.Mode) || options.ObjectivesSchema != 1 || options.Repetitions is < 1 or > 3 || options.SamplesPerTarget is < 4 or > 40
            || options.ProbeIntervalMs is < 50 or > 2000 || options.ProbeTimeoutMs is < 100 or > 2000
            || options.MaximumCandidates is < 1 or > 12 || options.OverallTimeoutSeconds is < 30 or > 1200
            || !double.IsFinite(options.MaximumExtraCpuPercent) || options.MaximumExtraCpuPercent is < 0 or > 20)
            throw new InvalidDataException("Internet test bounds are outside the supported range.");
        options.MaximumCandidates = Math.Min(options.MaximumCandidates, options.Mode == InternetTestMode.Quick ? 4 : 12);
        if (options.Targets == null || options.Targets.Count > 3) throw new InvalidDataException("Use no more than three explicit numeric remote targets.");
        options.Targets = options.Targets.Select(ValidateTarget).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (options.HttpsLoadBytesPerMeasurement is < 65536 or > 4194304 || options.MaximumHttpsLoadBytesTotal is < 65536 or > 33554432)
            throw new InvalidDataException("HTTPS load must remain within the explicit per-period and 32 MiB total byte budgets.");
        return options;
    }
    internal static string ValidateTarget(string target)
    {
        if (!IPAddress.TryParse(target, out var address) || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)
            || address.IsIPv6Multicast || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && address.GetAddressBytes()[0] is >= 224 and <= 239))
            throw new InvalidDataException("Use an explicit unicast numeric IP address, not a hostname, broadcast or multicast target.");
        return address.ToString();
    }
    internal static void ValidateSnapshot(InternetNicSnapshot snapshot)
    {
        if (!Guid.TryParse(snapshot.AdapterGuid, out _) || snapshot.InterfaceIndex <= 0 || string.IsNullOrWhiteSpace(snapshot.PnpDeviceId)
            || string.IsNullOrWhiteSpace(snapshot.DriverVersion) || string.IsNullOrWhiteSpace(snapshot.RouteSignature)
            || string.IsNullOrWhiteSpace(snapshot.SettingsHash) || snapshot.AdvancedProperties == null || snapshot.Rss == null || snapshot.Capabilities == null)
            throw new InvalidDataException("A complete exact NIC identity, route and original settings snapshot is required.");
        if (snapshot.AdvancedProperties.Count > 4 || snapshot.AdvancedProperties.Any(p => p.Keyword is not ("*EEE" or "*InterruptModeration" or "*Rss" or "*NumRssQueues"))
            || snapshot.AdvancedProperties.Select(p => p.Keyword).Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.AdvancedProperties.Count)
            throw new InvalidDataException("The snapshot contains an unsupported or duplicate driver option.");
        foreach (var property in snapshot.AdvancedProperties)
            if (property.OriginalValues == null || property.AdvertisedValues == null || property.OriginalValues.Count != 1
                || !property.AdvertisedValues.Contains(property.OriginalValues[0], StringComparer.Ordinal))
                throw new InvalidDataException("A saved driver option lacks an exact advertised original value.");
    }
    internal static void ValidateCandidate(InternetNicSnapshot baseline, InternetCandidate candidate)
    {
        ValidateSnapshot(baseline);
        if (candidate.IsBaseline || candidate.BaselineFingerprint != baseline.Fingerprint)
            throw new InvalidDataException("The candidate is not bound to this exact baseline.");
        var caps = baseline.Capabilities;
        if (candidate.EnergyEfficientEthernet.HasValue && caps.EnergyEfficientEthernetSupported != true
            || candidate.InterruptModeration.HasValue && caps.InterruptModerationSupported != true
            || candidate.RssEnabled.HasValue && caps.RssEnabledChangeSupported != true)
            throw new InvalidDataException("The candidate includes an unadvertised driver capability.");
        if (candidate.RssProcessors.HasValue && (caps.TopologyKnown != true || caps.ProcessorGroups != 1
            || candidate.RssProcessors is not (2 or 4 or 8) || !caps.MaxRssProcessors.HasValue || !caps.MaxReceiveQueues.HasValue
            || candidate.RssProcessors > caps.MaxRssProcessors || candidate.RssProcessors > caps.MaxReceiveQueues
            || !caps.LogicalProcessors.HasValue || candidate.RssProcessors > caps.LogicalProcessors))
            throw new InvalidDataException("The RSS processor count exceeds known queue/topology capabilities.");
        if (candidate.RssQueues.HasValue && (caps.RssSupported != true || !caps.SupportedRssQueues.Contains(candidate.RssQueues.Value)
            || !caps.MaxReceiveQueues.HasValue || candidate.RssQueues > caps.MaxReceiveQueues))
            throw new InvalidDataException("The RSS receive queue count is not advertised.");
        if (candidate.RssProfile != null && (caps.RssSupported != true || !caps.SupportedRssProfiles.Contains(candidate.RssProfile, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("The RSS profile is not supported.");
        bool changes = candidate.EnergyEfficientEthernet.HasValue && candidate.EnergyEfficientEthernet != baseline.EnergyEfficientEthernet
            || candidate.InterruptModeration.HasValue && candidate.InterruptModeration != baseline.InterruptModeration
            || candidate.RssEnabled.HasValue && candidate.RssEnabled != baseline.Rss.Enabled
            || candidate.RssProcessors.HasValue && candidate.RssProcessors != baseline.Rss.MaxProcessors
            || candidate.RssQueues.HasValue && candidate.RssQueues != baseline.Rss.NumberOfReceiveQueues
            || candidate.RssProfile != null && !string.Equals(candidate.RssProfile, baseline.Rss.Profile, StringComparison.OrdinalIgnoreCase);
        if (!changes) throw new InvalidDataException("The candidate does not change a recorded setting.");
    }
    internal static bool SameCapturedState(InternetNicSnapshot expected, InternetNicSnapshot actual) =>
        string.Equals(expected.AdapterGuid, actual.AdapterGuid, StringComparison.OrdinalIgnoreCase) && expected.InterfaceIndex == actual.InterfaceIndex
        && string.Equals(expected.PnpDeviceId, actual.PnpDeviceId, StringComparison.OrdinalIgnoreCase) && expected.DriverVersion == actual.DriverVersion
        && expected.SettingsHash == actual.SettingsHash && expected.RouteSignature == actual.RouteSignature
        && expected.SourceAddresses.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(actual.SourceAddresses.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
        && expected.GatewayTargets.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(actual.GatewayTargets.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
        && JsonSerializer.Serialize(expected.Rss, Json) == JsonSerializer.Serialize(actual.Rss, Json)
        && JsonSerializer.Serialize(expected.Capabilities, Json) == JsonSerializer.Serialize(actual.Capabilities, Json);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}
