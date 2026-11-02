using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Explicit NIC/probe boundary. Constructor performs no probes or hardware operations.</summary>
public sealed class InternetNicSystem : INetworkTestSystem, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, IgnoreReadOnlyProperties = true };
    private readonly Func<string, int, string> shell;
    private bool uncertainMutation;
    public InternetNicSystem() : this(RunPowerShellInput) { }
    internal InternetNicSystem(Func<string, int, string> shell)
    {
        this.shell = shell;
    }
    public void Dispose() { }
    public void AssertOutsideGame()
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcesses();
            if (processes.Any(p => p.ProcessName.StartsWith("FortniteClient", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Close Fortnite completely before NIC calibration, application or reconnecting recovery.");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException("Fortnite absence could not be confirmed.", ex); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public InternetNicSnapshot CaptureBaseline(InternetTestOptions options)
    {
        options = InternetOptimizer.NormalizeOptions(options); AssertOutsideGame();
        string data = Encode(new { Targets = options.Targets });
        var snapshot = ReadSnapshot(CommonScript + "\n$inputData=Read-TourneyInput '" + data + "'\n" + CaptureTail);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                int groups = GetActiveProcessorGroupCount(), logical = checked((int)GetActiveProcessorCount(ushort.MaxValue));
                snapshot.Capabilities.ProcessorGroups = groups;
                snapshot.Capabilities.LogicalProcessors = logical > 0 ? logical : null;
                snapshot.Capabilities.TopologyKnown = groups > 0 && logical > 0 && snapshot.Capabilities.PhysicalCores is > 0;
                bool rssRangeKnown = groups == 1 && snapshot.Rss.BaseProcessorGroup == 0 && snapshot.Rss.MaxProcessorGroup == 0
                    && snapshot.Rss.BaseProcessorNumber is >= 0 && snapshot.Rss.MaxProcessorNumber >= snapshot.Rss.BaseProcessorNumber
                    && snapshot.Rss.MaxProcessorNumber < logical;
                snapshot.Capabilities.MaxRssProcessors = snapshot.Capabilities.TopologyKnown && rssRangeKnown && snapshot.Capabilities.MaxReceiveQueues is > 0
                    ? Math.Min(logical, Math.Min(snapshot.Capabilities.MaxReceiveQueues.Value,
                        snapshot.Rss.MaxProcessorNumber!.Value - snapshot.Rss.BaseProcessorNumber!.Value + 1)) : null;
            }
            catch { snapshot.Capabilities.TopologyKnown = false; snapshot.Capabilities.MaxRssProcessors = null; }
        }
        snapshot.SettingsHash = SettingsHash(snapshot);
        InternetOptimizer.ValidateSnapshot(snapshot);
        return snapshot;
    }
    public void ApplyCandidate(InternetNicSnapshot baseline, InternetCandidate candidate)
    { InternetOptimizer.ValidateCandidate(baseline, candidate); RequireHash(baseline); AssertOutsideGame(); Mutate(baseline, candidate, false); }
    public void RestoreBaseline(InternetCalibrationState state)
    {
        if (!state.MutationAttempted || state.Restored) return;
        if (uncertainMutation) throw new NetworkTransitionException("A timed-out native transition has an uncertain outcome. Keep the journal and use a fresh reviewed recovery attempt; no immediate driver operation is replayed.", true, new TimeoutException());
        if (state.PendingCandidate == null) throw new InvalidDataException("The pending candidate is missing; retain the original NIC snapshot for reviewed recovery.");
        InternetOptimizer.ValidateCandidate(state.Baseline, state.PendingCandidate); RequireHash(state.Baseline);
        AssertOutsideGame(); Mutate(state.Baseline, state.PendingCandidate, true);
    }
    public bool VerifyBaseline(InternetNicSnapshot baseline)
    {
        InternetOptimizer.ValidateSnapshot(baseline); RequireHash(baseline);
        string data = Encode(new { Baseline = baseline });
        var current = ReadSnapshot(CommonScript + "\n$inputData=Read-TourneyInput '" + data + "'\n" + VerifyTail);
        return SameDevice(baseline, current) && SettingsHash(current) == baseline.SettingsHash;
    }
    private void Mutate(InternetNicSnapshot baseline, InternetCandidate candidate, bool restoring)
    {
        using var ownership = new Mutex(false, @"Local\TourneyMode.InternetNicMutation");
        bool acquired = false;
        try
        {
            try { acquired = ownership.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("Another NIC transition is active.");
            try
            {
                if (shell(BuildMutationScript(baseline, candidate, restoring), 90000) != "VERIFIED")
                    throw new InvalidOperationException("The exact NIC transition was not confirmed.");
            }
            catch (TimeoutException) { uncertainMutation = true; throw; }
        }
        finally { if (acquired) ownership.ReleaseMutex(); }
    }
    public async Task<InternetMeasurement> MeasureAsync(InternetNicSnapshot baseline, InternetTestOptions options, string label, CancellationToken cancellationToken)
    {
        options = InternetOptimizer.NormalizeOptions(options); RequireHash(baseline); AssertOutsideGame();
        ValidateMeasurementRoute(baseline, options);
        var measurement = new InternetMeasurement { Label = label, StartedUtc = DateTime.UtcNow, LoadRequested = options.EnableHttpsLoad };
        var targets = baseline.GatewayTargets.Select(InternetOptimizer.ValidateTarget).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => (Target: t, Gateway: true)).ToList();
        foreach (string target in options.Targets)
            if (!targets.Any(t => t.Target.Equals(target, StringComparison.OrdinalIgnoreCase))) targets.Add((target, false));
        if (targets.Count == 0 || targets.Count > 5) throw new InvalidOperationException("Use one to five validated gateway/explicit numeric remote targets.");
        var before = CpuTimes.Read();
        using var dpc = new DpcCounter();
        using var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<(long Bytes, bool Complete, DateTime CompletedUtc)>? load = options.EnableHttpsLoad
            ? DownloadBoundedAsync(baseline, options.HttpsLoadBytesPerMeasurement, loadCancellation.Token) : null;
        try
        {
            for (int round = 0; round < options.SamplesPerTarget; round++)
            {
                AssertOutsideGame(); cancellationToken.ThrowIfCancellationRequested();
                foreach (var target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var ping = new Ping();
                    var sample = new InternetProbeSample { Target = target.Target, IsGatewayTarget = target.Gateway,
                        SourceMetricKind = target.Gateway ? "GatewayIcmpRoundTripProxy" : "RemoteIcmpRoundTrip",
                        ProcessingTraceValid = false, LocalProcessingMs = null,
                        ProcessingEvidence = "QPC send-complete ICMP RTT includes router, path and scheduler; NIC processing is unknown." };
                    long start = Stopwatch.GetTimestamp();
                    try
                    {
                        var reply = await ping.SendPingAsync(IPAddress.Parse(target.Target), TimeSpan.FromMilliseconds(options.ProbeTimeoutMs),
                            new byte[32], new PingOptions(64, false), cancellationToken).ConfigureAwait(false);
                        sample.Success = reply.Status == IPStatus.Success;
                        if (sample.Success) sample.RoundTripMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    }
                    catch (PingException) { sample.Success = false; }
                    sample.TimestampUtc = DateTime.UtcNow; measurement.Samples.Add(sample);
                }
                if (round + 1 < options.SamplesPerTarget) await Task.Delay(options.ProbeIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            if (load != null)
            {
                var loaded = await load.ConfigureAwait(false);
                measurement.LoadBytesReceived = loaded.Bytes; measurement.LoadCompleted = loaded.Complete;
                if (!loaded.Complete) { measurement.Valid = false; measurement.Warnings.Add("The bounded HTTPS load did not complete its matched byte budget."); }
                measurement.Warnings.Add("HTTPS load is one bounded payload transfer, not a saturation/bandwidth test or a Fortnite packet stream.");
                if (DateTime.UtcNow - loaded.CompletedUtc > (DateTime.UtcNow - measurement.StartedUtc) / 2)
                { measurement.Valid = false; measurement.Warnings.Add("The load budget completed before half the probe window; the requested load condition was not sustained."); }
            }
            AssertOutsideGame(); ValidateMeasurementRoute(baseline, options);
        }
        finally
        {
            loadCancellation.Cancel();
            if (load != null) { try { await load.ConfigureAwait(false); } catch { } }
            measurement.EndedUtc = DateTime.UtcNow;
            foreach (var endpoint in measurement.Samples.GroupBy(s => s.Target, StringComparer.OrdinalIgnoreCase))
                measurement.Endpoints[endpoint.Key] = InternetScoring.CalculateMetrics(endpoint);
            measurement.Metrics = InternetScoring.CalculateMetrics(measurement.Samples);
            measurement.Metrics.MachineCpuPercent = CpuTimes.Percent(before, CpuTimes.Read());
            measurement.Metrics.MachineDpcPercent = dpc.Read();
            measurement.Warnings.Add("CPU/DPC utilization are whole-machine period context, not NIC processing latency or isolated driver cost.");
        }
        return measurement;
    }
    private void ValidateMeasurementRoute(InternetNicSnapshot baseline, InternetTestOptions options)
    {
        string encoded = Encode(new { Baseline = baseline, Targets = options.Targets });
        if (shell(CommonScript + "\n$inputData=Read-TourneyInput '" + encoded + "'\nAssert-TourneyCurrentRoute $inputData.Baseline\nAssert-TourneyTargetRoutes $inputData.Baseline.InterfaceIndex @($inputData.Targets)\n'ROUTE_OK'", 15000) != "ROUTE_OK")
            throw new InvalidOperationException("The probe route no longer matches the captured physical NIC.");
    }
    private async Task<(long Bytes, bool Complete, DateTime CompletedUtc)> DownloadBoundedAsync(InternetNicSnapshot baseline, long maximumBytes, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5), MaxConnectionsPerServer = 1 };
        handler.ConnectCallback = async (context, cancellation) =>
        {
            if (!context.DnsEndPoint.Host.Equals("speed.cloudflare.com", StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != 443)
                throw new IOException("Only the fixed HTTPS load origin is allowed.");
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation).ConfigureAwait(false);
            Exception? failure = null;
            foreach (var address in addresses.Take(4))
            {
                Socket? socket = null;
                try
                {
                    string target = InternetOptimizer.ValidateTarget(address.ToString());
                    ValidateMeasurementRoute(baseline, new InternetTestOptions { Targets = [target] });
                    var source = baseline.SourceAddresses.Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
                        .FirstOrDefault(ip => ip != null && ip.AddressFamily == address.AddressFamily && !ip.IsIPv6LinkLocal);
                    if (source == null) continue;
                    socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(new IPEndPoint(address, 443), cancellation).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (OperationCanceledException) { socket?.Dispose(); throw; }
                catch (Exception ex) { socket?.Dispose(); failure = ex; }
            }
            throw new IOException("The fixed HTTPS load origin could not be reached through the captured wired route.", failure);
        };
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var uri = new Uri("https://speed.cloudflare.com/__down?bytes=" + maximumBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("The fixed HTTPS load endpoint did not return HTTP 200.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        long received = 0; byte[] buffer = new byte[16384];
        while (received < maximumBytes)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximumBytes - received)), deadline.Token).ConfigureAwait(false);
            if (count == 0) break;
            received += count;
        }
        return (received, received == maximumBytes, DateTime.UtcNow);
    }
    private InternetNicSnapshot ReadSnapshot(string script) =>
        JsonSerializer.Deserialize<InternetNicSnapshot>(shell(script, 25000), Json) ?? throw new InvalidDataException("The exact NIC snapshot was unavailable.");
    private static bool SameDevice(InternetNicSnapshot a, InternetNicSnapshot b) =>
        string.Equals(a.AdapterGuid, b.AdapterGuid, StringComparison.OrdinalIgnoreCase) && a.InterfaceIndex == b.InterfaceIndex
        && string.Equals(a.PnpDeviceId, b.PnpDeviceId, StringComparison.OrdinalIgnoreCase) && a.DriverVersion == b.DriverVersion;
    private static string Encode<T>(T value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)));
    internal static string SettingsHash(InternetNicSnapshot snapshot)
    {
        string content = JsonSerializer.Serialize(new { snapshot.Rss, Properties = snapshot.AdvancedProperties.OrderBy(p => p.Keyword, StringComparer.Ordinal)
            .Select(p => new { p.Keyword, p.OriginalValues }) }, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
    private static void RequireHash(InternetNicSnapshot snapshot)
    {
        InternetOptimizer.ValidateSnapshot(snapshot);
        if (snapshot.SettingsHash != SettingsHash(snapshot)) throw new InvalidDataException("The saved original NIC settings hash does not match.");
    }
    private static string RunPowerShellInput(string script, int timeoutMs) =>
        RunPowerShellInputCore(script, timeoutMs, @"Local\TourneyMode.InternetNicHelperExecution");
    // Fixture scripts must be inert and use their own GUID-scoped lock, never the production helper lock.
    internal static string RunPowerShellFixture(string script, int timeoutMs, string fixtureMutexName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(fixtureMutexName,
            @"\ALocal\\TourneyMode\.InternetNicHelperFixture-[a-f0-9]{32}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("Native helper fixtures require a separate GUID-scoped fixture mutex.");
        return RunPowerShellInputCore(script, timeoutMs, fixtureMutexName);
    }
    private static string RunPowerShellInputCore(string script, int timeoutMs, string executionMutexName)
    {
        if (timeoutMs is < 1000 or > 120000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        const string bootstrap = """
[Console]::InputEncoding=New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';$WarningPreference='SilentlyContinue';$InformationPreference='SilentlyContinue'
$tourneyHelperLock=$null;$tourneyHelperLockHeld=$false
try {
    # The helper itself owns this lock, so parent exit/timeout cannot make an
    # unresolved script appear idle to another instance or restarted controller.
    $tourneyHelperLock=[Threading.Mutex]::new($false,'__TMODE_HELPER_EXECUTION_MUTEX__')
    try { $tourneyHelperLockHeld=$tourneyHelperLock.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $tourneyHelperLockHeld=$true }
    if (!$tourneyHelperLockHeld) { throw 'An earlier NIC helper still owns the execution lock. Wait for its real exit; no new NIC script was executed.' }
    # The parent cannot send any script bytes until it receives this acknowledgment.
    # Owning the lock before reading input prevents a delayed old body from running
    # after a newer controller has completed recovery.
    [Console]::Out.WriteLine('__TMODE_NIC_HELPER_READY_1__')
    [Console]::Out.Flush()
    $tourneyScript=[Console]::In.ReadToEnd()
    & ([ScriptBlock]::Create($tourneyScript))
} catch { [Console]::Error.WriteLine($_.Exception.Message);exit 1 }
finally {
    if ($tourneyHelperLockHeld) { $tourneyHelperLock.ReleaseMutex() }
    if ($null -ne $tourneyHelperLock) { $tourneyHelperLock.Dispose() }
}
""";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap.Replace("__TMODE_HELPER_EXECUTION_MUTEX__", executionMutexName, StringComparison.Ordinal))) }) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("The bounded NIC helper did not start.");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task<string?> ready = process.StandardOutput.ReadLineAsync();
        bool readyCompleted;
        try { readyCompleted = ready.Wait(Math.Min(timeoutMs, 10000)); }
        catch (Exception ex)
        {
            StopHelper(process);
            throw new TimeoutException("The NIC helper readiness could not be confirmed; no script bytes or EOF execution grant were sent.", ex);
        }
        if (!readyCompleted)
        {
            StopHelper(process);
            throw new TimeoutException("The NIC helper readiness deadline expired; no script bytes or EOF execution grant were sent.");
        }
        if (!string.Equals(ready.Result?.TrimStart('\uFEFF'), "__TMODE_NIC_HELPER_READY_1__", StringComparison.Ordinal))
        {
            StopHelper(process);
            string reason = errors.Wait(3000) ? errors.Result.Trim() : "helper readiness was not acknowledged";
            throw new InvalidOperationException("NIC operation unavailable before script delivery: " +
                (reason.Length > 0 ? reason[..Math.Min(reason.Length, 1800)] : "helper readiness was not acknowledged"));
        }
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        try
        {
            Task input = process.StandardInput.WriteAsync(script);
            if (!input.Wait(Math.Min(timeoutMs, 10000)))
                throw new TimeoutException("The NIC helper did not accept its complete script; no EOF execution grant was sent.");
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            // Never close input after a failed/partial write while a live helper
            // might treat the resulting EOF as permission to execute that body.
            StopHelper(process);
            throw new TimeoutException("NIC helper script delivery failed. Its exit was confirmed before releasing the input stream; retain recovery.", ex);
        }
        if (!process.WaitForExit(timeoutMs))
        {
            StopHelper(process);
            throw new TimeoutException("The NIC helper deadline expired. Its possible driver outcome remains journaled.");
        }
        if (!Task.WhenAll(output, errors).Wait(3000)) throw new TimeoutException("NIC helper output did not complete; retain recovery.");
        string stdout = output.Result.Trim().TrimStart('\uFEFF'), stderr = errors.Result.Trim();
        if (process.ExitCode != 0 || (stderr.Length > 0 && !TournamentResources.IsOnlyProgress(stderr)))
            throw new InvalidOperationException("NIC operation unavailable: " + (stderr.Length > 0 ? stderr[..Math.Min(stderr.Length, 1800)] : "helper exit " + process.ExitCode));
        return stdout;
    }
    private static void StopHelper(Process process)
    {
        Exception? terminationError = null;
        try { process.Kill(entireProcessTree: false); }
        catch (Exception ex) { terminationError = ex; }
        try
        {
            if (process.WaitForExit(3000)) return;
        }
        catch (Exception ex) { terminationError = terminationError == null ? ex : new AggregateException(terminationError, ex); }
        throw new TimeoutException("NIC helper exit is unconfirmed. Its execution mutex blocks later scripts; do not replay the uncertain operation.", terminationError);
    }
    internal static string BuildMutationScript(InternetNicSnapshot baseline, InternetCandidate candidate, bool restoring) =>
        CommonScript + "\n$inputData=Read-TourneyInput '" + Encode(new { Baseline = baseline, Candidate = candidate, Restoring = restoring }) + "'\n" + MutationTail;

    [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort groupNumber);
    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User)
    {
        public static CpuTimes? Read()
        {
            try { return GetSystemTimes(out var idle, out var kernel, out var user) ? new(idle.Value, kernel.Value, user.Value) : null; }
            catch { return null; }
        }
        public static double? Percent(CpuTimes? a, CpuTimes? b)
        {
            if (!a.HasValue || !b.HasValue || b.Value.Kernel < a.Value.Kernel || b.Value.User < a.Value.User || b.Value.Idle < a.Value.Idle) return null;
            ulong total = b.Value.Kernel - a.Value.Kernel + b.Value.User - a.Value.User, idle = b.Value.Idle - a.Value.Idle;
            return total > 0 && idle <= total ? 100d * (total - idle) / total : null;
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeTime { public uint Low; public uint High; public readonly ulong Value => ((ulong)High << 32) | Low; }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out NativeTime idle, out NativeTime kernel, out NativeTime user);
    }
    private sealed class DpcCounter : IDisposable
    {
        private IntPtr query, counter;
        public DpcCounter()
        {
            try
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0
                    || PdhAddEnglishCounter(query, @"\Processor(_Total)\% DPC Time", IntPtr.Zero, out counter) != 0
                    || PdhCollectQueryData(query) != 0) Dispose();
            }
            catch { Dispose(); }
        }
        public double? Read()
        {
            try
            {
                if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0
                    || PdhGetFormattedCounterValue(counter, 0x200, out _, out var value) != 0
                    || value.Status > 1 || !double.IsFinite(value.Value) || value.Value is < 0 or > 100) return null;
                return value.Value;
            }
            catch { return null; }
        }
        public void Dispose() { if (query != IntPtr.Zero) { _ = PdhCloseQuery(query); query = IntPtr.Zero; } }
        [StructLayout(LayoutKind.Explicit)] private struct CounterValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    }

    private static string CommonScript => EthernetTuning.DiscoveryScript + "\n" + NativeFunctions;
    private const string NativeFunctions = """
function Read-TourneyInput([string]$encoded) { return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded)) | ConvertFrom-Json }
function Assert-OutsideFortnite {
    $clients=@(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object { [string]$_.Name -like 'FortniteClient*.exe' })
    if ($clients.Count -gt 0) { throw 'Fortnite is running; NIC changes/reconnects are deferred until it fully exits.' }
}
function Get-TourneyRouteSignature {
    $ips=@(Get-NetIPInterface -ErrorAction Stop)
    $entries=@(Get-NetRoute -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { [string]$_.State -ne 'Dead' } | ForEach-Object {
        $route=$_
        $ip=@($ips|Where-Object { $_.InterfaceIndex -eq $route.InterfaceIndex -and [string]$_.AddressFamily -eq [string]$route.AddressFamily })
        if ($ip.Count -ne 1) { throw 'A route interface identity is ambiguous.' }
        '{0}|{1}|{2}|{3}|{4}|{5}|{6}' -f [string]$route.AddressFamily,[string]$route.DestinationPrefix,[int]$route.InterfaceIndex,[string]$route.NextHop,[long]$route.RouteMetric,[long]$ip[0].InterfaceMetric,[string]$ip[0].ConnectionState
    } | Sort-Object)
    return ($entries -join [Environment]::NewLine)
}
function Assert-TourneyTargetRoutes([int]$index,$targets) {
    if (@($targets).Count -eq 0) { return }
    Get-Command Find-NetRoute -ErrorAction Stop | Out-Null
    foreach ($target in @($targets)) {
        $routes=@(Find-NetRoute -RemoteIPAddress ([string]$target) -ErrorAction Stop)
        if ($routes.Count -eq 0 -or @($routes|Where-Object { [int]$_.InterfaceIndex -ne $index }).Count -gt 0) {
            throw 'A probe target uses another interface (possibly VPN/Wi-Fi). No wired calibration is allowed.'
        }
    }
}
function Get-TourneyExactAdapter($baseline) {
    $adapter=Get-ExactWiredAdapter ([guid]$baseline.AdapterGuid)
    if ([int]$adapter.ifIndex -ne [int]$baseline.InterfaceIndex -or [string]$adapter.PnPDeviceID -ine [string]$baseline.PnpDeviceId -or [string]$adapter.DriverVersionString -cne [string]$baseline.DriverVersion) {
        throw 'The exact saved adapter/driver identity changed. No other device will be modified.'
    }
    if ([string]$adapter.Status -eq 'Disabled') { throw 'The saved adapter is disabled; it will not be enabled automatically.' }
    return $adapter
}
function Assert-TourneyCurrentRoute($baseline) {
    $adapter=Get-TourneyExactAdapter $baseline
    $current=Get-CurrentWiredAdapter
    if ($null -eq $current -or ([guid]$current.InterfaceGuid) -ne ([guid]$baseline.AdapterGuid) -or (Get-TourneyRouteSignature) -cne [string]$baseline.RouteSignature) {
        throw 'The active route changed or is not unambiguously physical Ethernet.'
    }
}
function Get-TourneyRss($adapter) {
    $name=[Management.Automation.WildcardPattern]::Escape([string]$adapter.Name)
    $found=@(Get-NetAdapterRss -Name $name -ErrorAction Stop|Where-Object { [string]$_.Name -ceq [string]$adapter.Name })
    if ($found.Count -ne 1) { throw 'The RSS setting identity is ambiguous.' }
    $rss=$found[0]
    return [pscustomobject]@{
        Enabled=if($null -ne $rss.Enabled){[bool]$rss.Enabled}else{$null};Profile=if($null -ne $rss.Profile){[string]$rss.Profile}else{$null}
        BaseProcessorGroup=$rss.BaseProcessorGroup;BaseProcessorNumber=$rss.BaseProcessorNumber
        MaxProcessorGroup=$rss.MaxProcessorGroup;MaxProcessorNumber=$rss.MaxProcessorNumber
        MaxProcessors=$rss.MaxProcessors;NumberOfReceiveQueues=$rss.NumberOfReceiveQueues;NumaNode=$rss.NumaNode
    }
}
function Get-TourneySnapshot($adapter) {
    if ([string]::IsNullOrWhiteSpace([string]$adapter.PnPDeviceID) -or [string]::IsNullOrWhiteSpace([string]$adapter.DriverVersionString)) { throw 'Exact NIC PnP/driver version unavailable.' }
    $saved=@();$properties=@(Get-ExactProperties $adapter)
    foreach ($keyword in @('*EEE','*InterruptModeration','*Rss','*NumRssQueues')) {
        $found=@($properties|Where-Object { $_.RegistryKeyword -ieq $keyword })
        if ($found.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$found[0].DisplayName)) { continue }
        $original=@($found[0].RegistryValue|ForEach-Object{[string]$_});$valid=@($found[0].ValidRegistryValues|ForEach-Object{[string]$_})
        if ($original.Count -ne 1 -or $valid.Count -eq 0 -or $valid -cnotcontains $original[0]) { continue }
        $saved += [pscustomobject]@{Keyword=$keyword;DisplayName=[string]$found[0].DisplayName;OriginalValues=@($original);AdvertisedValues=@($valid)}
    }
    $rss=[pscustomobject]@{Enabled=$null;Profile=$null;BaseProcessorGroup=$null;BaseProcessorNumber=$null;MaxProcessorGroup=$null;MaxProcessorNumber=$null;MaxProcessors=$null;NumberOfReceiveQueues=$null;NumaNode=$null}
    $rssKnown=$false
    try {
        $rss=Get-TourneyRss $adapter
        $required=@('Enabled','Profile','BaseProcessorGroup','BaseProcessorNumber','MaxProcessorGroup','MaxProcessorNumber','MaxProcessors','NumberOfReceiveQueues','NumaNode')
        $rssKnown=@($required|Where-Object { $null -eq $rss.$_ }).Count -eq 0
    } catch { }
    $binary=@{}
    foreach ($keyword in @('*EEE','*InterruptModeration','*Rss')) {
        $p=@($saved|Where-Object Keyword -eq $keyword)
        $binary[$keyword]=$p.Count -eq 1 -and $p[0].AdvertisedValues -ccontains '0' -and $p[0].AdvertisedValues -ccontains '1' -and $p[0].OriginalValues[0] -cin @('0','1')
    }
    $queues=@();$queueOption=@($properties|Where-Object { $_.RegistryKeyword -ieq '*NumRssQueues' })
    if ($rssKnown -and $queueOption.Count -eq 1) {
        $raw=@($queueOption[0].ValidRegistryValues);$display=@($queueOption[0].ValidDisplayValues)
        if ($raw.Count -eq $display.Count) {
            for ($i=0;$i -lt $raw.Count;$i++) {
                $n=0
                if ([int]::TryParse([string]$raw[$i],[ref]$n) -and $n -in @(1,2,4,8,16,32,64,128) -and [string]$display[$i] -match '^\s*(\d+)\b' -and [int]$Matches[1] -eq $n) { $queues += $n }
            }
        }
    }
    $maxQueues=if($queues.Count -gt 0){[int]($queues|Measure-Object -Maximum).Maximum}elseif($rssKnown -and [int]$rss.NumberOfReceiveQueues -gt 0){[int]$rss.NumberOfReceiveQueues}else{$null}
    $profiles=@()
    if ($rssKnown) {
        try {
            $cmd=Get-Command Set-NetAdapterRss -ErrorAction Stop
            $needed=@('Enabled','Profile','BaseProcessorGroup','BaseProcessorNumber','MaxProcessorGroup','MaxProcessorNumber','MaxProcessors','NumberOfReceiveQueues','NumaNode','NoRestart','Name')
            if (@($needed|Where-Object { !$cmd.Parameters.ContainsKey($_) }).Count -ne 0) { throw 'Typed RSS setter contract unavailable.' }
            $profiles=@($cmd.Parameters['Profile'].ParameterType.GetEnumNames()|Where-Object { $_ -in @('Closest','ClosestStatic','NUMA','NUMAStatic','Conservative') })
        } catch { $rssKnown=$false;$profiles=@();$queues=@();$maxQueues=$null }
    }
    $cpu=@(Get-CimInstance Win32_Processor -ErrorAction Stop);$cores=($cpu|Measure-Object NumberOfCores -Sum).Sum
    $gateways=@(Get-NetRoute -PolicyStore ActiveStore -ErrorAction Stop|Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and [string]$_.DestinationPrefix -in @('0.0.0.0/0','::/0') -and [string]$_.NextHop -notin @('0.0.0.0','::') } |
        ForEach-Object { if([string]$_.NextHop -like 'fe80:*' -and [string]$_.NextHop -notlike '*%*'){[string]$_.NextHop+'%'+[string]$adapter.ifIndex}else{[string]$_.NextHop} }|Select-Object -Unique)
    [pscustomobject]@{
        CapturedUtc=[DateTime]::UtcNow;AdapterGuid=([guid]$adapter.InterfaceGuid).ToString('D');InterfaceIndex=[int]$adapter.ifIndex
        Name=[string]$adapter.Name;PnpDeviceId=[string]$adapter.PnPDeviceID;DriverVersion=[string]$adapter.DriverVersionString
        RouteSignature=(Get-TourneyRouteSignature);SettingsHash='native-pending';GatewayTargets=@($gateways)
        SourceAddresses=@(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -ErrorAction Stop|Where-Object { [string]$_.AddressState -eq 'Preferred' }|Select-Object -ExpandProperty IPAddress|Sort-Object -Unique)
        Rss=$rss;AdvancedProperties=@($saved)
        InterruptModeration=if($binary['*InterruptModeration']){[bool]([string](@($saved|Where-Object Keyword -eq '*InterruptModeration')[0].OriginalValues[0]) -eq '1')}else{$null}
        EnergyEfficientEthernet=if($binary['*EEE']){[bool]([string](@($saved|Where-Object Keyword -eq '*EEE')[0].OriginalValues[0]) -eq '1')}else{$null}
        Capabilities=[pscustomobject]@{
            TopologyKnown=$false;LogicalProcessors=$null;PhysicalCores=if($cores -gt 0){[int]$cores}else{$null};ProcessorGroups=$null
            RssSupported=$rssKnown;RssEnabledChangeSupported=($rssKnown -and $binary['*Rss']);MaxRssProcessors=$null;MaxReceiveQueues=$maxQueues
            SupportedRssQueues=@($queues|Sort-Object -Unique);SupportedRssProfiles=@($profiles)
            InterruptModerationSupported=[bool]$binary['*InterruptModeration'];InterruptModerationEnabledValue='1';InterruptModerationDisabledValue='0'
            EnergyEfficientEthernetSupported=[bool]$binary['*EEE'];EeeEnabledValue='1';EeeDisabledValue='0'
        }
        Limitations=@('RSS CPU/queue/profile trials require complete typed readback and known capabilities. No CPU-0 exclusion or affinity mask is inferred.')
    }
}
""";
    private const string CaptureTail = """
Assert-OutsideFortnite
$adapter=Get-CurrentWiredAdapter
if ($null -eq $adapter) { throw 'No unambiguous current physical wired default route is available.' }
Assert-TourneyTargetRoutes ([int]$adapter.ifIndex) @($inputData.Targets)
Get-TourneySnapshot $adapter|ConvertTo-Json -Depth 12 -Compress
""";
    private const string VerifyTail = """
$adapter=Get-TourneyExactAdapter $inputData.Baseline
if ([string]$adapter.Status -ne 'Up') { throw 'The original physical link is not Up.' }
Get-TourneySnapshot $adapter|ConvertTo-Json -Depth 12 -Compress
""";
    private const string MutationTail = """
$baseline=$inputData.Baseline;$candidate=$inputData.Candidate;$restoring=[bool]$inputData.Restoring
Assert-OutsideFortnite
$adapter=Get-TourneyExactAdapter $baseline
if (!$restoring) { Assert-TourneyCurrentRoute $baseline }
$desiredRss=[pscustomobject]@{}
foreach ($name in @('Enabled','Profile','BaseProcessorGroup','BaseProcessorNumber','MaxProcessorGroup','MaxProcessorNumber','MaxProcessors','NumberOfReceiveQueues','NumaNode')) {
    $desiredRss|Add-Member -NotePropertyName $name -NotePropertyValue $baseline.Rss.$name
}
if ($null -ne $candidate.RssEnabled) { $desiredRss.Enabled=[bool]$candidate.RssEnabled }
if ($null -ne $candidate.RssProcessors) { $desiredRss.MaxProcessors=[int]$candidate.RssProcessors }
if ($null -ne $candidate.RssQueues) { $desiredRss.NumberOfReceiveQueues=[int]$candidate.RssQueues }
if ($null -ne $candidate.RssProfile) { $desiredRss.Profile=[string]$candidate.RssProfile }
$currentProperties=@(Get-ExactProperties $adapter);$changes=@()
foreach ($saved in @($baseline.AdvancedProperties)) {
    $keyword=[string]$saved.Keyword
    if ($keyword -notin @('*EEE','*InterruptModeration','*Rss','*NumRssQueues')) { throw 'Unsupported saved NIC property.' }
    $found=@($currentProperties|Where-Object { $_.RegistryKeyword -ieq $keyword })
    if ($found.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$found[0].DisplayName)) { throw 'An exact saved driver property disappeared.' }
    $current=@($found[0].RegistryValue|ForEach-Object{[string]$_});$valid=@($found[0].ValidRegistryValues|ForEach-Object{[string]$_})
    $original=@($saved.OriginalValues|ForEach-Object{[string]$_})
    if ($original.Count -ne 1 -or $current.Count -ne 1 -or $valid -cnotcontains $original[0]) { throw 'A saved original is no longer supported.' }
    $applied=$original[0]
    if ($keyword -eq '*EEE' -and $null -ne $candidate.EnergyEfficientEthernet) { $applied=if([bool]$candidate.EnergyEfficientEthernet){'1'}else{'0'} }
    if ($keyword -eq '*InterruptModeration' -and $null -ne $candidate.InterruptModeration) { $applied=if([bool]$candidate.InterruptModeration){'1'}else{'0'} }
    if ($keyword -eq '*Rss' -and $null -ne $candidate.RssEnabled) { $applied=if([bool]$candidate.RssEnabled){'1'}else{'0'} }
    if ($keyword -eq '*NumRssQueues' -and $null -ne $candidate.RssQueues) { $applied=[string]$candidate.RssQueues }
    if ($valid -cnotcontains $applied) { throw 'A candidate value is no longer advertised.' }
    if ((!$restoring -and $current[0] -cne $original[0]) -or ($restoring -and $current[0] -cnotin @($original[0],$applied))) { throw ('The driver option changed externally: '+$keyword) }
    $wanted=if($restoring){$original[0]}else{$applied}
    $changes += [pscustomobject]@{Property=$found[0];Keyword=$keyword;Value=$wanted;NeedsWrite=($current[0] -cne $wanted)}
}
$rssChanged=$null -ne $candidate.RssEnabled -or $null -ne $candidate.RssProcessors -or $null -ne $candidate.RssQueues -or $null -ne $candidate.RssProfile
$rssTarget=if($restoring){$baseline.Rss}else{$desiredRss}
if ($rssChanged) {
    $currentRss=Get-TourneyRss $adapter
    foreach ($name in @('Enabled','Profile','BaseProcessorGroup','BaseProcessorNumber','MaxProcessorGroup','MaxProcessorNumber','MaxProcessors','NumberOfReceiveQueues','NumaNode')) {
        if ($null -eq $baseline.Rss.$name -or $null -eq $desiredRss.$name) { throw 'Exact RSS originals are incomplete.' }
        $value=[string]$currentRss.$name;$original=[string]$baseline.Rss.$name;$applied=[string]$desiredRss.$name
        if ((!$restoring -and $value -cne $original) -or ($restoring -and $value -cnotin @($original,$applied))) { throw ('RSS state changed externally: '+$name) }
    }
}
# Every identity, route, capability and expected-value check precedes the first write.
if ($rssChanged) {
    Assert-OutsideFortnite
    $name=[Management.Automation.WildcardPattern]::Escape([string]$adapter.Name)
    $parameters=@{
        Name=$name;Profile=[string]$rssTarget.Profile;BaseProcessorGroup=[uint16]$rssTarget.BaseProcessorGroup;BaseProcessorNumber=[byte]$rssTarget.BaseProcessorNumber
        MaxProcessorGroup=[uint16]$rssTarget.MaxProcessorGroup;MaxProcessorNumber=[byte]$rssTarget.MaxProcessorNumber;MaxProcessors=[uint32]$rssTarget.MaxProcessors
        NumberOfReceiveQueues=[uint32]$rssTarget.NumberOfReceiveQueues;NumaNode=[uint16]$rssTarget.NumaNode;Enabled=[bool]$rssTarget.Enabled;NoRestart=$true;Confirm=$false;ErrorAction='Stop'
    }
    Set-NetAdapterRss @parameters
}
foreach ($change in $changes) {
    if (!$change.NeedsWrite -and (!$rssChanged -or $change.Keyword -notin @('*Rss','*NumRssQueues'))) { continue }
    Assert-OutsideFortnite
    Set-NetAdapterAdvancedProperty -InputObject $change.Property -RegistryValue @([string]$change.Value) -NoRestart -Confirm:$false -ErrorAction Stop
}
Assert-OutsideFortnite
$adapter=Get-TourneyExactAdapter $baseline
Restart-NetAdapter -InputObject $adapter -Confirm:$false -ErrorAction Stop
$timer=[Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Milliseconds 250
    $adapter=Get-TourneyExactAdapter $baseline
    if ([string]$adapter.Status -eq 'Up') { break }
} while ($timer.ElapsedMilliseconds -lt 20000)
if ([string]$adapter.Status -ne 'Up') { throw 'NIC did not confirm link Up; recorded recovery remains necessary.' }
$verified=@(Get-ExactProperties $adapter)
foreach ($change in $changes) {
    $found=@($verified|Where-Object { $_.RegistryKeyword -ieq $change.Keyword })
    if ($found.Count -ne 1 -or @($found[0].RegistryValue).Count -ne 1 -or [string]$found[0].RegistryValue[0] -cne [string]$change.Value) { throw ('Driver readback failed: '+$change.Keyword) }
}
if (@($baseline.Rss.PSObject.Properties | Where-Object { $null -ne $_.Value }).Count -gt 0) {
    $read=Get-TourneyRss $adapter
    foreach ($name in @('Enabled','Profile','BaseProcessorGroup','BaseProcessorNumber','MaxProcessorGroup','MaxProcessorNumber','MaxProcessors','NumberOfReceiveQueues','NumaNode')) {
        if ($null -ne $rssTarget.$name -and [string]$read.$name -cne [string]$rssTarget.$name) { throw ('Typed RSS readback failed: '+$name) }
    }
}
'VERIFIED'
""";
}
