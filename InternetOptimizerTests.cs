using System.Text.Json;

namespace TourneyMode;

/// <summary>Synthetic NIC snapshots, measurements and runner boundaries only. No probes, load, driver calls or host reads.</summary>
internal static class InternetOptimizerTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        await test("Internet candidates derive RSS allocations from known queues and actual logical topology", () =>
        {
            var powerful = Snapshot(16); powerful.Rss.MaxProcessors = 1;
            Check(InternetScoring.BuildCandidates(powerful, InternetTestMode.Quick).Where(value => value.RssProcessors.HasValue)
                .Select(value => value.RssProcessors!.Value).SequenceEqual([2, 4, 8]), "Known 8-queue hardware did not offer the bounded 2/4/8 comparisons.");
            var weak = Snapshot(4); weak.Rss.MaxProcessors = 1;
            Check(InternetScoring.BuildCandidates(weak, InternetTestMode.Quick).Where(value => value.RssProcessors.HasValue)
                .Select(value => value.RssProcessors!.Value).SequenceEqual([2, 4]), "A small CPU received RSS allocations above its visible topology.");
            powerful.Capabilities.MaxReceiveQueues = 2;
            Check(InternetScoring.BuildCandidates(powerful, InternetTestMode.Thorough).All(value => value.RssProcessors is null or <= 2), "A candidate exceeded the actual receive queues.");
            foreach (Action<InternetNicSnapshot> invalidate in new Action<InternetNicSnapshot>[]
            { value => value.Capabilities.TopologyKnown = false, value => value.Capabilities.ProcessorGroups = 2,
              value => value.Capabilities.MaxReceiveQueues = null, value => value.Capabilities.MaxRssProcessors = null, value => value.Rss.NumaNode = null })
            {
                var unknown = Snapshot(); invalidate(unknown);
                Check(InternetScoring.BuildCandidates(unknown, InternetTestMode.Quick).All(value => !value.RssProcessors.HasValue && value.RssProfile == null),
                    "Unknown queue/topology facts were treated as a generic RSS preset.");
            }
            return Task.CompletedTask;
        });
        await test("Internet candidates require exact identity and advertised binary features", () =>
        {
            var baseline = Snapshot(); var candidates = InternetScoring.BuildCandidates(baseline, InternetTestMode.Thorough);
            Check(candidates.All(value => value.BaselineFingerprint == baseline.Fingerprint && !value.IsBaseline) && candidates.Count <= 8,
                "Candidate scope was unbounded or not bound to its original snapshot.");
            baseline.Capabilities.InterruptModerationDisabledValue = null; baseline.Capabilities.EnergyEfficientEthernetSupported = null;
            Check(InternetScoring.BuildCandidates(baseline, InternetTestMode.Quick).All(value => !value.InterruptModeration.HasValue && !value.EnergyEfficientEthernet.HasValue),
                "A binary driver value was invented without its actual advertised capability.");
            baseline.SettingsHash = "unknown";
            Check(InternetScoring.BuildCandidates(baseline, InternetTestMode.Quick).Count == 0, "An unbound settings snapshot enabled experiments.");
            return Task.CompletedTask;
        });
        await test("Internet metrics reject nonfinite intervals and never infer local processing from ICMP", () =>
        {
            var samples = Enumerable.Range(0, 20).Select(index => new InternetProbeSample
            { Target = "192.0.2.1", TimestampUtc = DateTime.UnixEpoch.AddSeconds(index), Success = true, RoundTripMs = index % 2 == 0 ? 10 : 12,
              LocalProcessingMs = .1, ProcessingTraceValid = false, SourceMetricKind = "GatewayIcmpRoundTripProxy", IsGatewayTarget = true }).ToList();
            samples.Add(new() { Target = "192.0.2.1", Success = true, RoundTripMs = double.NaN });
            var metrics = InternetScoring.CalculateMetrics(samples);
            Check(metrics.Sent == 21 && metrics.Received == 20 && metrics.PacketLossPercent > 0 && metrics.PingMedianMs == 11
                && metrics.JitterMs == 2 && metrics.LocalProcessingMedianMs == null && !metrics.ProcessingTraceValid,
                "Invalid RTT or gateway timing was converted into NIC processing evidence.");
            foreach (var sample in samples.Take(20)) { sample.ProcessingTraceValid = true; sample.ProcessingEvidence = "Synthetic valid packet receive-to-stack trace"; }
            metrics = InternetScoring.CalculateMetrics(samples.Take(20));
            Check(metrics.ProcessingTraceValid && metrics.ProcessingSamples == 20 && metrics.LocalProcessingMedianMs == .1,
                "An explicitly valid populated processing trace could not be represented.");
            return Task.CompletedTask;
        });
        await test("Internet scoring qualifies only repeated three-objective best-tested improvement", () =>
        {
            var report = Report(); InternetScoring.Score(report);
            Check(report.CanApplyBest && report.HasQualifiedWinner && report.Winner?.Id == "rss-2" && report.AllObjectivesMeasured
                && report.Scores.Single().ValidRepeats == 2 && report.Scores.Single().ParetoEfficient,
                "A repeatable supported three-objective improvement was not qualified.");
            Check(report.Recommendation.Contains("Best tested", StringComparison.Ordinal) && report.Limitations.Any(value => value.Contains("not a Fortnite", StringComparison.Ordinal)),
                "The recommendation claimed an absolute optimum or a direct Fortnite latency result.");
            return Task.CompletedTask;
        });
        await test("Internet missing processing trace yields provisional ranking and keeps qualified winner baseline", () =>
        {
            var report = Report(); RemoveProcessing(report); InternetScoring.Score(report);
            Check(!report.CanApplyBest && !report.HasQualifiedWinner && report.Winner?.IsBaseline == true && report.ProvisionalWinner?.Id == "rss-2"
                && report.PingMeasured && report.StabilityMeasured && !report.ProcessingMeasured && !report.AllObjectivesMeasured
                && report.Scores.Single().EqualWeightScore == null && report.Scores.Single().ProvisionalRank == 1,
                "Unknown processing was hidden inside an equal-weight winner or the disclosed provisional result was lost.");
            return Task.CompletedTask;
        });
        await test("Internet noisy endpoints and any endpoint loss or tail regression keep baseline", () =>
        {
            foreach (Action<InternetTestReport> change in new Action<InternetTestReport>[]
            {
                value => { var c = value.Trials[0].BaselineAfter.Endpoints["192.0.2.1"]; c.PingMedianMs = 35; c.PingP95Ms = 37; },
                value => { var b = value.Trials[0].Treatment.Endpoints["192.0.2.1"]; b.Received = 19; b.PacketLossPercent = 5; },
                value => value.Trials[0].Treatment.Endpoints["192.0.2.1"].PingP95Ms = 30,
                value => value.Trials[0].Treatment.Endpoints["192.0.2.1"].JitterMs = 4
            })
            {
                var report = Report(); change(report); InternetScoring.Score(report);
                Check(report.Winner?.IsBaseline == true && !report.CanApplyBest && report.ProvisionalWinner == null,
                    "Noise, loss or a tail/stability regression was cherry-picked out of a candidate.");
            }
            var mixed = Report();
            foreach (var trial in mixed.Trials)
            {
                trial.BaselineBefore.Endpoints["198.51.100.1"] = Metrics(); trial.BaselineAfter.Endpoints["198.51.100.1"] = Metrics();
                trial.Treatment.Endpoints["198.51.100.1"] = Metrics(ping: 25, tail: 27);
            }
            InternetScoring.Score(mixed); Check(!mixed.CanApplyBest, "An average across endpoints concealed one regressing path.");
            return Task.CompletedTask;
        });
        await test("Internet uncertain ties and inconsistent repeats prefer baseline", () =>
        {
            var report = Report(); var second = InternetScoring.BuildCandidates(report.Baseline, report.Options.Mode).Single(value => value.Id == "rss-4");
            report.Trials.AddRange(report.Trials.ToArray().Select(trial => { var copy = Clone(trial); copy.Candidate = second; return copy; }));
            InternetScoring.Score(report); Check(!report.CanApplyBest && report.Winner?.IsBaseline == true && report.ProvisionalWinner == null && report.Confidence.Contains("tie", StringComparison.Ordinal),
                "Indistinguishable candidates produced a falsely certain best result.");
            RemoveProcessing(report); InternetScoring.Score(report);
            Check(report.ProvisionalWinner == null, "Missing processing provided a back door around the tied baseline preference.");
            report = Report(); report.Trials.RemoveAt(1); InternetScoring.Score(report);
            Check(!report.CanApplyBest && report.ProvisionalWinner == null && report.Scores.Single().ProvisionalRank == 1, "One A/B/A cycle was treated as repeatable proof rather than rank-only evidence.");
            report = Report(); report.Trials[1].Treatment.Endpoints["192.0.2.1"] = Metrics(); InternetScoring.Score(report);
            Check(!report.CanApplyBest, "A nonrepeatable apparent gain was promoted to a qualified winner.");
            return Task.CompletedTask;
        });
        await test("Internet CPU resource limits differ for weak and many-core hardware without becoming latency", () =>
        {
            var weak = Report(4); var strong = Report(16);
            foreach (var report in new[] { weak, strong }) foreach (var trial in report.Trials) trial.Treatment.Metrics.MachineCpuPercent = 5;
            InternetScoring.Score(weak); InternetScoring.Score(strong);
            Check(!weak.CanApplyBest && strong.CanApplyBest, "Hardware-dependent CPU resource caps were replaced by a generic preset.");
            foreach (var trial in strong.Trials) { trial.Treatment.Metrics.MachineCpuPercent = null; trial.Treatment.Metrics.MachineDpcPercent = .01; }
            InternetScoring.Score(strong); Check(!strong.CanApplyBest, "DPC percentage substituted for missing CPU resource or processing evidence.");
            var drift = Report(); drift.Trials[0].BaselineAfter.Metrics.MachineCpuPercent = 50;
            InternetScoring.Score(drift); Check(!drift.CanApplyBest, "An unmatched noisy CPU baseline hid candidate resource overhead.");
            return Task.CompletedTask;
        });
        await test("Internet stale candidates, invalid ordering and incomplete restoration cannot win", () =>
        {
            foreach (Action<InternetTestReport> change in new Action<InternetTestReport>[]
            {
                value => value.Trials[0].Candidate.BaselineFingerprint = new string('F', 64),
                value => value.Trials[0].Candidate.RssProcessors = 7,
                value => value.Trials[0].Treatment.StartedUtc = value.Trials[0].BaselineBefore.StartedUtc,
                value => value.Trials[0].RestoreVerified = false,
                value => value.Canceled = true,
                value => value.RollbackVerified = false
            })
            {
                var report = Report(); change(report); InternetScoring.Score(report);
                Check(!report.CanApplyBest && report.Winner?.IsBaseline == true, "A stale, out-of-order, canceled or un-restored candidate won.");
            }
            return Task.CompletedTask;
        });
        await test("Internet runner journals originals before apply and completes ordered A/B/A cycles with rollback", async () =>
        {
            var fake = new FakeNetwork(); var optimizer = new InternetOptimizer(fake);
            var report = await optimizer.CalibrateAsync(Options(), fake.Persist, fake.AssertOutsideGame);
            Check(report.Completed && report.RollbackVerified && fake.Applies == 2 && fake.Restores == 2 && !fake.Changed && fake.Durable?.Restored == true,
                "Runner did not restore between both bounded repetitions.");
            Check(fake.Actions.Where(value => value.StartsWith("Measure:", StringComparison.Ordinal)).Select(value => value.Split('/').Last().Trim())
                .SequenceEqual(["A-before", "B", "A-after", "A-before", "B", "A-after"]), "The runner did not measure matched A/B/A ordering.");
        });
        await test("Internet runner cancellation restores uncancelled and never auto-applies a winner", async () =>
        {
            using var canceled = new CancellationTokenSource(); var fake = new FakeNetwork();
            fake.BeforeTreatment = () => canceled.Cancel();
            var report = await new InternetOptimizer(fake).CalibrateAsync(Options(), fake.Persist, fake.AssertOutsideGame, canceled.Token);
            Check(report.Canceled && report.RollbackVerified && fake.Applies == 1 && fake.Restores == 1 && !fake.Changed
                && fake.Durable?.Restored == true && !report.CanApplyBest, "Cancellation prevented exact rollback or produced an active best setting.");
        });
        await test("Internet partial apply rolls back once and failed rollback retains recovery without replay", async () =>
        {
            var fake = new FakeNetwork { FailApply = true };
            var report = await new InternetOptimizer(fake).CalibrateAsync(Options(), fake.Persist, fake.AssertOutsideGame);
            Check(!report.Completed && report.RollbackVerified && fake.Restores == 1 && !fake.Changed, "Partial application was not rolled back exactly once.");
            fake = new FakeNetwork { FailApply = true, FailRestore = true };
            await Expect<NetworkTransitionException>(() => new InternetOptimizer(fake).CalibrateAsync(Options(), fake.Persist, fake.AssertOutsideGame));
            Check(fake.Restores == 1 && fake.Changed && fake.Durable is { MutationAttempted: true, Restored: false, Phase: "Recovery required" },
                "Uncertain rollback was replayed or its durable original was discarded.");
        });
        await test("Internet runner refuses mutation when write-ahead persistence or game gate fails", async () =>
        {
            var fake = new FakeNetwork { FailWriteAhead = true };
            var report = await new InternetOptimizer(fake).CalibrateAsync(Options(), fake.Persist, fake.AssertOutsideGame);
            Check(fake.Applies == 0 && fake.Restores == 0 && !fake.Changed && !report.CanApplyBest, "A failed write-ahead journal allowed driver mutation.");
            fake = new FakeNetwork { GameRunning = true };
            report = await new InternetOptimizer(fake).CalibrateAsync(Options(), fake.Persist, fake.AssertOutsideGame);
            Check(fake.Captures == 0 && fake.Applies == 0 && !report.Completed, "A game-presence gate allowed discovery/probing or mutation.");
        });
        await test("Internet apply-best requires explicit provisional consent and rechecks the exact current adapter", async () =>
        {
            var report = Report(); RemoveProcessing(report); var fake = new FakeNetwork { Baseline = Clone(report.Baseline) };
            fake.Baseline.CapturedUtc = report.Baseline.CapturedUtc.AddSeconds(1); // Fresh read time alone must not invalidate identical original settings.
            var optimizer = new InternetOptimizer(fake);
            await Expect<InvalidOperationException>(() => optimizer.ApplyBestAsync(report, fake.Persist, fake.AssertOutsideGame));
            Check(fake.Applies == 0, "Unknown processing latency silently authorized a provisional setting.");
            var state = await optimizer.ApplyBestAsync(report, fake.Persist, fake.AssertOutsideGame, allowProvisional: true);
            Check(fake.Changed && state.Phase == "Applied provisional result", "Explicitly disclosed provisional application was mislabeled as a qualified result.");
            await optimizer.RestoreAsync(state, fake.Persist, fake.AssertOutsideGame); Check(!fake.Changed && state.Restored, "Explicit applied result could not be restored.");
            fake = new FakeNetwork { Baseline = Clone(report.Baseline) }; fake.Baseline.RouteSignature = "changed-route";
            await Expect<InvalidOperationException>(() => new InternetOptimizer(fake).ApplyBestAsync(report, fake.Persist, fake.AssertOutsideGame, allowProvisional: true));
            Check(fake.Applies == 0, "A stale route used an old calibration result.");
        });
    }

    private static InternetTestOptions Options() => new() { MaximumCandidates = 1, Repetitions = 2, SamplesPerTarget = 12, Targets = ["192.0.2.1"] };
    private static InternetNicSnapshot Snapshot(int logical = 16) => new()
    {
        AdapterGuid = "11111111-1111-1111-1111-111111111111", InterfaceIndex = 7, Name = "Synthetic wired adapter", PnpDeviceId = "PCI\\SYNTHETIC",
        DriverVersion = "fixture-1", RouteSignature = "fixture-route-7", SettingsHash = new string('A', 64), SourceAddresses = ["192.0.2.2"], GatewayTargets = ["192.0.2.1"],
        Rss = new() { Enabled = true, Profile = "ClosestProcessor", NumaNode = 65535, BaseProcessorGroup = 0, BaseProcessorNumber = 0, MaxProcessorGroup = 0,
            MaxProcessorNumber = logical - 1, MaxProcessors = Math.Min(8, logical), NumberOfReceiveQueues = 8 },
        InterruptModeration = true, EnergyEfficientEthernet = true,
        Capabilities = new() { TopologyKnown = true, LogicalProcessors = logical, PhysicalCores = logical / 2, ProcessorGroups = 1,
            RssSupported = true, RssEnabledChangeSupported = true, MaxRssProcessors = 8, MaxReceiveQueues = 8, SupportedRssQueues = [1, 2, 4, 8],
            SupportedRssProfiles = ["ClosestProcessor", "ClosestStatic", "Conservative", "NUMAStatic"], InterruptModerationSupported = true,
            InterruptModerationEnabledValue = "1", InterruptModerationDisabledValue = "0", EnergyEfficientEthernetSupported = true, EeeEnabledValue = "1", EeeDisabledValue = "0" },
        AdvancedProperties = [new() { Keyword = "*EEE", OriginalValues = ["1"], AdvertisedValues = ["0", "1"] },
            new() { Keyword = "*InterruptModeration", OriginalValues = ["1"], AdvertisedValues = ["0", "1"] }]
    };
    private static InternetMetrics Metrics(double ping = 20, double tail = 22, double jitter = 2, double processing = .05) => new()
    {
        Sent = 20, Received = 20, PingMedianMs = ping, PingP95Ms = tail, JitterMs = jitter, PacketLossPercent = 0,
        LocalProcessingMedianMs = processing, ProcessingTraceValid = true, ProcessingSamples = 20, ProcessingEvidence = "Synthetic valid packet receive-to-stack trace", MachineCpuPercent = 3
    };
    private static InternetMeasurement Period(DateTime start, bool treatment) => new()
    {
        StartedUtc = start, EndedUtc = start.AddSeconds(1), Valid = true,
        Metrics = new() { MachineCpuPercent = treatment ? 3.5 : 3 },
        Endpoints = new(StringComparer.OrdinalIgnoreCase) { ["192.0.2.1"] = treatment ? Metrics(16, 18, 1, .03) : Metrics() }
    };
    private static InternetTestReport Report(int logical = 16)
    {
        var snapshot = Snapshot(logical); var candidate = InternetScoring.BuildCandidates(snapshot, InternetTestMode.Quick).Single(value => value.Id == "rss-2");
        var report = new InternetTestReport { Baseline = snapshot, Options = Options(), Completed = true, RollbackVerified = true };
        var start = DateTime.UtcNow.AddMinutes(-2);
        for (int repeat = 0; repeat < 2; repeat++) report.Trials.Add(new()
        {
            Candidate = Clone(candidate), BaselineBefore = Period(start.AddSeconds(repeat * 6), false), Treatment = Period(start.AddSeconds(repeat * 6 + 2), true),
            BaselineAfter = Period(start.AddSeconds(repeat * 6 + 4), false), Applied = true, RestoreAttempted = true, RestoreVerified = true
        });
        return report;
    }
    private static void RemoveProcessing(InternetTestReport report)
    {
        foreach (var trial in report.Trials) foreach (var period in new[] { trial.BaselineBefore, trial.Treatment, trial.BaselineAfter })
            foreach (var metric in period.Endpoints.Values) { metric.LocalProcessingMedianMs = null; metric.ProcessingTraceValid = false; metric.ProcessingSamples = 0; metric.ProcessingEvidence = "Unknown"; }
    }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, new JsonSerializerOptions { IgnoreReadOnlyProperties = true }))!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown."); }
    private sealed class FakeNetwork : INetworkTestSystem
    {
        public InternetNicSnapshot Baseline = Snapshot();
        public InternetCalibrationState? Durable;
        public readonly List<string> Actions = [];
        public int Captures, Applies, Restores;
        public bool Changed, GameRunning, FailApply, FailRestore, FailWriteAhead;
        public Action? BeforeTreatment;
        private DateTime cursor = DateTime.UtcNow.AddHours(-1);
        public void AssertOutsideGame() { if (GameRunning) throw new InvalidOperationException("Synthetic game presence blocks work."); }
        public InternetNicSnapshot CaptureBaseline(InternetTestOptions options) { AssertOutsideGame(); Captures++; return Clone(Baseline); }
        public void Persist(InternetCalibrationState state)
        {
            if (FailWriteAhead && state.Phase == "Applying") throw new IOException("Synthetic write-ahead failure.");
            Durable = Clone(state); Actions.Add("Persist:" + state.Phase);
        }
        public void ApplyCandidate(InternetNicSnapshot baseline, InternetCandidate candidate)
        {
            AssertOutsideGame(); Check(Durable is { MutationAttempted: true, Restored: false } && Durable.Baseline.Fingerprint == baseline.Fingerprint
                && Durable.PendingCandidate?.Id == candidate.Id, "Candidate applied before exact originals and pending mutation were durably recorded.");
            Changed = true; Applies++; Actions.Add("Apply:" + candidate.Id); if (FailApply) throw new IOException("Synthetic partial driver failure.");
        }
        public void RestoreBaseline(InternetCalibrationState state)
        {
            AssertOutsideGame(); Check(Durable?.MutationAttempted == true && Durable.Baseline.Fingerprint == state.Baseline.Fingerprint, "Rollback lacked durable original identity.");
            Restores++; Actions.Add("Restore"); if (FailRestore) throw new IOException("Synthetic rollback readback failure."); Changed = false;
        }
        public bool VerifyBaseline(InternetNicSnapshot snapshot) => !Changed;
        public Task<InternetMeasurement> MeasureAsync(InternetNicSnapshot baseline, InternetTestOptions options, string label, CancellationToken token)
        {
            AssertOutsideGame(); bool treatment = label.EndsWith(" / B", StringComparison.Ordinal); if (treatment) BeforeTreatment?.Invoke(); token.ThrowIfCancellationRequested();
            Actions.Add("Measure:" + label); var period = Period(cursor, treatment); period.Label = label; cursor = cursor.AddSeconds(2); return Task.FromResult(period);
        }
    }
}
