using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace TourneyMode;

public sealed class PcFacts
{
    public string Windows { get; set; } = "Unavailable";
    public string Cpu { get; set; } = "Unavailable";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public double RamGiB { get; set; }
    public int MemorySpeedMT { get; set; }
    public string Bios { get; set; } = "Unavailable";
    public string MotherboardManufacturer { get; set; } = "Unavailable";
    public string MotherboardModel { get; set; } = "Unavailable";
    public string BiosReleaseDate { get; set; } = "Unavailable";
    public string WindowsEdition { get; set; } = "Unavailable";
    public bool? DomainJoined { get; set; }
    public bool HasBattery { get; set; }
    public bool? IsPortable { get; set; }
    public bool? AutomaticPagefile { get; set; }
    public int? OsVisibleLogicalProcessors { get; set; }
    public int? ProcessorGroups { get; set; }
    public string TopologySource { get; set; } = "Unavailable";
    public List<CpuEfficiencyFact> CpuEfficiencyClasses { get; set; } = [];
    public List<GpuFact> Gpus { get; set; } = [];
    public List<AdapterFact> Adapters { get; set; } = [];
}
public sealed class CpuEfficiencyFact { public int EfficiencyClass { get; set; } public int LogicalProcessors { get; set; } }
public sealed class GpuFact { public string Name { get; set; } = ""; public string Driver { get; set; } = ""; public ulong? DedicatedMemoryMiB { get; set; } public string MemorySource { get; set; } = "Unavailable"; }
public sealed class AdapterFact { public string Name { get; set; } = ""; public string Link { get; set; } = ""; }
public sealed class DisplayFact { public bool Primary { get; set; } public int Width { get; set; } public int Height { get; set; } public uint? RefreshHz { get; set; } }
public sealed class VolumeFact { public string Drive { get; set; } = ""; public double FreeGiB { get; set; } public double FreePercent { get; set; } }
public sealed class AppResourceSample
{
    public string ExecutablePath { get; set; } = "";
    public bool EligibleForGovernor { get; set; }
    public string Application { get; set; } = "";
    public int MeasuredProcesses { get; set; }
    public double CpuPercent { get; set; }
    public double PrivateMiB { get; set; }
}
public sealed class Advice
{
    public string Area { get; set; } = "";
    public string Finding { get; set; } = "";
    public string Action { get; set; } = "";
    public string Evidence { get; set; } = "";
}
public sealed class PcAnalysis
{
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public bool ReadOnly { get; set; } = true;
    public string Scope { get; set; } = "Settings and existing software only. CPU sample is background activity, not a Fortnite FPS/latency benchmark.";
    public PcFacts Hardware { get; set; } = new();
    public bool? AcPower { get; set; }
    public double? AvailableRamGiB { get; set; }
    public uint? MemoryLoadPercent { get; set; }
    public double? CommitUsedGiB { get; set; }
    public double? CommitLimitGiB { get; set; }
    public int? GameMode { get; set; }
    public int? BackgroundCapture { get; set; }
    public int? HagsPreference { get; set; }
    public string ActivePowerPlan { get; set; } = "Unavailable";
    public string GameSettingsFingerprint { get; set; } = "Unavailable";
    public List<string> GameSettingsChanges { get; set; } = [];
    public List<string> GameSettingsWarnings { get; set; } = [];
    public double SampleSeconds { get; set; }
    public List<DisplayFact> Displays { get; set; } = [];
    public List<VolumeFact> Volumes { get; set; } = [];
    public List<AppResourceSample> CandidateCosts { get; set; } = [];
    public List<Advice> Recommendations { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public WindowsProfileBackup? WindowsProfile { get; set; }
    public string[] Sources { get; set; } = ["https://www.nvidia.com/en-us/geforce/guides/system-latency-optimization-guide/",
        "https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics",
        "https://devblogs.microsoft.com/directx/hardware-accelerated-gpu-scheduling/"];
}

/// <summary>Explicit, read-only system check. No resident performance monitor,
/// downloads, network probes, driver changes or game control.</summary>
public static class SystemAnalysis
{
    internal sealed record ResourcePoint(long StartTicks, double CpuSeconds, long PrivateBytes);
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static async Task<PcAnalysis> CaptureAsync(AppOptions options)
    {
        var report = new PcAnalysis { AcPower = AcPowerOnline() };
        var hardware = Task.Run(() => ReadHardware(report));
        var snapshot = ProcessCatalog.Snapshot();
        var apps = ProcessPolicy.Candidates(snapshot, fortniteOnly: true, maximumHeadroom: true, protectedPaths: options.ProtectedAppPaths);
        var targets = apps.SelectMany(a => a.Processes).DistinctBy(p => p.Id).ToList();
        var clock = Stopwatch.StartNew();
        var first = ReadResources(targets);
        double firstTime = clock.Elapsed.TotalSeconds;
        await Task.Delay(1200).ConfigureAwait(false);
        var second = ReadResources(targets);
        double seconds = clock.Elapsed.TotalSeconds - firstTime;
        report.SampleSeconds = Math.Round(seconds, 3);
        report.CandidateCosts = apps.Select(a => Measure(a, first, second, seconds, Environment.ProcessorCount))
            .Where(a => a.MeasuredProcesses > 0).OrderByDescending(a => a.CpuPercent).ThenByDescending(a => a.PrivateMiB).ToList();
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory)) { report.AvailableRamGiB = Math.Round(memory.AvailablePhysical / 1073741824d, 1); report.MemoryLoadPercent = memory.Load; }
        var commitment = FrameMemoryContext.Read();
        report.CommitUsedGiB = commitment.CommitUsedGiB;
        report.CommitLimitGiB = commitment.CommitLimitGiB;
        try { report.ActivePowerPlan = WindowsSettings.GetPowerScheme(); } catch (Exception e) { lock (report.Errors) report.Errors.Add("Power plan: " + e.GetType().Name); }
        report.GameMode = ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        report.BackgroundCapture = ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
        report.HagsPreference = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>(), DeviceName = "", FormName = "" };
            bool known = EnumDisplaySettings(screen.DeviceName, -1, ref mode);
            report.Displays.Add(new DisplayFact { Primary = screen.Primary, Width = screen.Bounds.Width, Height = screen.Bounds.Height,
                RefreshHz = known && mode.Frequency > 1 ? mode.Frequency : null });
        }
        foreach (var drive in DriveInfo.GetDrives())
            try { if (drive.DriveType == DriveType.Fixed && drive.IsReady && drive.TotalSize > 0)
                report.Volumes.Add(new VolumeFact { Drive = drive.Name, FreeGiB = Math.Round(drive.AvailableFreeSpace / 1073741824d, 1), FreePercent = Math.Round(drive.AvailableFreeSpace * 100d / drive.TotalSize, 1) }); }
            catch { /* An inaccessible or disconnected volume is not guessed. */ }
        await hardware.ConfigureAwait(false);
        if (options.ApplyWindowsProfile)
        {
            try { report.WindowsProfile = TourneyMode.WindowsProfile.Capture(report.Hardware); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            { report.Errors.Add("Windows preferences/policies unavailable: " + error.GetType().Name); }
        }
        if (options.Intent?.Fps?.ApplyGameProfile == true)
        {
            try
            {
                var profile = FortniteSettings.Preview(FortniteSettings.DefaultPath, options.Intent.Fps,
                    report.Hardware.Gpus.Any(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)), options.Intent.AcceptTearing);
                report.GameSettingsFingerprint = profile.Exists ? profile.OriginalSHA256 : "Unavailable";
                report.GameSettingsChanges = profile.Changes;
                report.GameSettingsWarnings = profile.Warnings;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            { report.GameSettingsWarnings.Add("Fortnite profile unavailable: " + error.Message); }
        }
        report.Recommendations = Recommend(report);
        return report;
    }

    private static void ReadHardware(PcAnalysis report)
    {
        try
        {
            string result = TournamentResources.RunPowerShell("""
                $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
                $os = Get-CimInstance Win32_OperatingSystem
                $cs = Get-CimInstance Win32_ComputerSystem
                $bios = Get-CimInstance Win32_BIOS
                $board = Get-CimInstance Win32_BaseBoard | Select-Object -First 1
                $edition = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop).EditionID
                $ram = @(Get-CimInstance Win32_PhysicalMemory)
                $batteries = @(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)
                $portable = $null
                if ($batteries.Count -gt 0 -or $cs.PCSystemType -in @(2,8)) { $portable = $true }
                elseif ($cs.PCSystemType -in @(1,3,4,5,6,7)) { $portable = $false }
                $gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object { [pscustomobject]@{Name=$_.Name;Driver=$_.DriverVersion} })
                $adapters = @()
                if (Get-Command Get-NetAdapter -ErrorAction SilentlyContinue) {
                    $adapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up' | ForEach-Object {
                        [pscustomobject]@{Name=$_.InterfaceDescription;Link=[string]$_.LinkSpeed}
                    })
                }
                [pscustomobject]@{
                    Windows=($os.Caption + ' / build ' + $os.BuildNumber)
                    Cpu=$cpu.Name;Cores=$cpu.NumberOfCores;Threads=$cpu.NumberOfLogicalProcessors
                    RamGiB=[math]::Round($cs.TotalPhysicalMemory/1GB,1)
                    MemorySpeedMT=[int](($ram | Measure-Object ConfiguredClockSpeed -Minimum).Minimum)
                    Bios=$bios.SMBIOSBIOSVersion;HasBattery=($batteries.Count -gt 0);IsPortable=$portable
                    MotherboardManufacturer=$board.Manufacturer;MotherboardModel=$board.Product;BiosReleaseDate=[string]$bios.ReleaseDate
                    WindowsEdition=[string]$edition;DomainJoined=[bool]$cs.PartOfDomain
                    AutomaticPagefile=$cs.AutomaticManagedPagefile;Gpus=@($gpus);Adapters=@($adapters)
                } | ConvertTo-Json -Depth 4 -Compress
                """, 15000);
            report.Hardware = JsonSerializer.Deserialize<PcFacts>(result, Json) ?? new();
        }
        catch (Exception e) { lock (report.Errors) report.Errors.Add("Hardware inventory unavailable: " + e.GetType().Name); }
        ReadCpuTopology(report.Hardware);
        ReadNvidiaMemory(report);
    }

    private static void ReadCpuTopology(PcFacts facts)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint bytes, IntPtr.Zero, 0);
            if (bytes is < 32 or > 1_048_576) return;
            buffer = Marshal.AllocHGlobal((int)bytes);
            if (!GetSystemCpuSetInformation(buffer, bytes, out uint actual, IntPtr.Zero, 0) || actual > bytes) return;
            var classes = new Dictionary<byte, int>(); var groups = new HashSet<ushort>();
            int offset = 0;
            while (offset + 8 <= actual)
            {
                int size = Marshal.ReadInt32(buffer, offset), type = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || offset + size > actual) return;
                if (type == 0 && size >= 32)
                {
                    byte efficiency = Marshal.ReadByte(buffer, offset + 18);
                    classes[efficiency] = classes.GetValueOrDefault(efficiency) + 1;
                    groups.Add((ushort)Marshal.ReadInt16(buffer, offset + 12));
                }
                offset += size;
            }
            if (classes.Count == 0) return;
            facts.OsVisibleLogicalProcessors = classes.Values.Sum(); facts.ProcessorGroups = groups.Count;
            facts.CpuEfficiencyClasses = classes.OrderBy(pair => pair.Key).Select(pair => new CpuEfficiencyFact { EfficiencyClass = pair.Key, LogicalProcessors = pair.Value }).ToList();
            facts.TopologySource = "Windows CPU-set information; raw efficiency classes, not guessed P/E labels or proof of BIOS settings.";
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException or System.ComponentModel.Win32Exception) { }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    }

    private static void ReadNvidiaMemory(PcAnalysis report)
    {
        if (!report.Hardware.Gpus.Any(gpu => gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))) return;
        string[] locations = [Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")];
        string? tool = locations.FirstOrDefault(File.Exists);
        if (tool is null) return;
        try
        {
            var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--query-gpu=name,memory.total"); info.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = Process.Start(info);
            if (process is null) return;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2000)) { try { process.Kill(entireProcessTree: false); } catch { } return; }
            if (process.ExitCode != 0) return;
            string text = output.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            errors.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            if (text.Length > 16_384) return;
            var entries = new List<(string Name, ulong Memory)>();
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int comma = line.LastIndexOf(',');
                if (comma <= 0 || !ulong.TryParse(line[(comma + 1)..].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ulong mib)) continue;
                if (mib is > 0 and < 4_194_304) entries.Add((line[..comma].Trim(), mib));
            }
            foreach (var gpu in report.Hardware.Gpus)
            {
                var same = entries.Where(entry => entry.Name.Equals(gpu.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Count != 1) continue;
                gpu.DedicatedMemoryMiB = same[0].Memory; gpu.MemorySource = "Existing NVIDIA nvidia-smi, one read-only inventory query; not live frame telemetry.";
            }
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException) { }
    }

    private static Dictionary<int, ResourcePoint> ReadResources(IEnumerable<ProcessIdentity> targets)
    {
        var result = new Dictionary<int, ResourcePoint>();
        foreach (var target in targets)
            try
            {
                using var p = Process.GetProcessById(target.Id);
                if (ProcessCatalog.MatchesPinned(p, target)) result[target.Id] = new(target.StartTicks, p.TotalProcessorTime.TotalSeconds, p.PrivateMemorySize64);
            }
            catch { /* Protected or exited process: no invented zero-cost measurement. */ }
        return result;
    }

    internal static AppResourceSample Measure(AppCandidate app, IReadOnlyDictionary<int, ResourcePoint> first,
        IReadOnlyDictionary<int, ResourcePoint> second, double seconds, int processors)
    {
        var result = new AppResourceSample { Application = app.Name, ExecutablePath = app.LaunchPath,
            EligibleForGovernor = ProcessPolicy.IsMonitoredApp(app) && !ConnectionProtection.IsApp(app)
                && !ProcessPolicy.IsDiscordApp(app) && !PerformancePlanner.ShouldPreserveApp(app, new AppOptions { Intent = new() { Activity = SessionActivity.Recording } }) };
        if (seconds <= 0 || processors < 1) return result;
        double cpu = 0; long memory = 0;
        foreach (var p in app.Processes.DistinctBy(p => p.Id))
            if (first.TryGetValue(p.Id, out var a) && second.TryGetValue(p.Id, out var b)
                && a.StartTicks == p.StartTicks && b.StartTicks == p.StartTicks)
            { result.MeasuredProcesses++; cpu += Math.Max(0, b.CpuSeconds - a.CpuSeconds); memory += Math.Max(0, b.PrivateBytes); }
        result.CpuPercent = Math.Round(cpu / seconds / processors * 100, 2);
        result.PrivateMiB = Math.Round(memory / 1048576d, 1);
        return result;
    }

    internal static List<Advice> Recommend(PcAnalysis r)
    {
        var result = new List<Advice>();
        void Add(string area, string finding, string action, string evidence) => result.Add(new() { Area = area, Finding = finding, Action = action, Evidence = evidence });
        if (r.AcPower == false) Add("Power", "The PC is on battery.", "Use its existing AC supply for sustained gaming. Power overrides are skipped on battery.", "Measured power status; performance varies with the PC's thermal and power limits.");
        if (r.MemoryLoadPercent >= 85) Add("Memory", $"Physical memory load is {r.MemoryLoadPercent}%.", "Close optional measured memory users. Keep a suitable pagefile; do not run RAM cleaners.", "Measured memory pressure, not a promise of more FPS.");
        if (r.Hardware.AutomaticPagefile == false) Add("Memory", "The pagefile is manually configured.", "Review whether system-managed sizing is more suitable; do not disable it as an FPS tweak.", "Configuration only; a manual pagefile is not automatically wrong.");
        if (r.CommitUsedGiB is double used && r.CommitLimitGiB is > 0 && used / r.CommitLimitGiB.Value >= .9)
            Add("Memory commitment", $"System commit is {used:0.0}/{r.CommitLimitGiB.Value:0.0} GiB.", "Reduce optional allocations before play and retain a suitable pagefile. An optional frame diagnostic can record global pressure, but hard-fault attribution requires additional tracing.", "Observed system-wide commitment; not a measured cause of a Fortnite hitch.");
        foreach (var v in r.Volumes.Where(v => v.FreePercent < 10)) Add("Storage", $"{v.Drive} has {v.FreePercent}% free ({v.FreeGiB} GiB).", "Remove unused data through normal storage/app controls so updates and caches have room. Retain shader caches unless repairing corruption.", "Measured space; no specific free-space percentage guarantees FPS.");
        if (r.BackgroundCapture == 1) Add("Capture", "Windows background capture is enabled.", "If recording is unnecessary, the Game Mode / capture option disables it temporarily and restores the previous value on OFF.", "Observed setting; recording cost depends on actual use.");
        if (r.CandidateCosts.Any(a => a.CpuPercent > 0)) Add("Background apps", "Optional applications used CPU during the sample.", "Start with the measured CPU users below. Save work and select only apps you do not need while gaming.", "Short background CPU sample, not match FPS or 1% lows.");
        Add("Graphics", "GPU model/driver and each display's active refresh are detected locally.", "Verify the primary display's intended refresh, Fortnite Performance mode, Reflex on supported NVIDIA GPUs, and unwanted overlays. Compare a frame cap with uncapped for your latency/tearing goal.", "Vendor guidance; no automatic NVIDIA settings are applied to AMD/Intel graphics.");
        Add("Network", r.Hardware.Adapters.Count == 0 ? "No active physical adapter was identified." : "Active physical adapters are listed below.", "Check Fortnite ping/jitter/loss under normal load. Router SQM helps only if queueing is present. Keep driver offloads and affinity defaults unless a controlled test supports a change.", "Local settings cannot guarantee a shorter internet route. NIC tuning and outbound DSCP marking require explicit opt-in and a reviewed eligible experiment; driver, route and router support determine their effect.");
        Add("Stability", "The optimizer preserves core drivers, security, caches, CPU topology and required game dependencies; supported Codex tools use a separate restart lifecycle.", "Change HAGS, BIOS/core settings, power/affinity or memory timings only with a specific reason and reproducible comparison.", "No universal optimal configuration or FPS gain can be established by process count.");
        Add("Sustained frame budget", "An idle resource sample cannot establish an endgame-safe cap, bottleneck or thermal limit.", "Choose the goal and constraints first. Use optional imported frame data or an explicitly started diagnostic for p99/p99.9, budget misses and incident context. Preserve shader caches; review supported in-game Reflex and a sustainable frame cap.", "No capture is started automatically. Present intervals, CPU busy and GPU busy are distinct; missing metrics remain unknown.");
        Add("Power and heat", "Maximum CPU requests are available, but effective clocks and thermal/power throttling are not measured by this inventory.", "Prefer a sustained performance profile when its frame-time evidence is better than maximum power. Do not assume all cores unparked or minimum CPU state 100% guarantees lower latency.", "CPU-set classes describe OS-visible scheduling topology only; they do not justify disabling more cores or changing BIOS settings.");
        return result;
    }

    public static string Format(PcAnalysis r)
    {
        var b = new StringBuilder();
        b.AppendLine("THIS PC — READ-ONLY ANALYSIS").AppendLine($"{r.CapturedUtc.ToLocalTime():g}").AppendLine(r.Scope).AppendLine();
        b.AppendLine(r.Hardware.Windows).AppendLine($"CPU: {r.Hardware.Cpu} · {(r.Hardware.Cores > 0 ? r.Hardware.Cores.ToString() : "unknown")} cores / {(r.Hardware.Threads > 0 ? r.Hardware.Threads.ToString() : "unknown")} logical processors")
            .AppendLine($"RAM: {(r.Hardware.RamGiB > 0 ? r.Hardware.RamGiB.ToString() : "unknown")} GiB · {(r.Hardware.MemorySpeedMT > 0 ? r.Hardware.MemorySpeedMT.ToString() : "unknown")} MT/s · available {r.AvailableRamGiB?.ToString() ?? "unknown"} GiB")
            .AppendLine($"Power: {(r.AcPower == true ? "AC" : r.AcPower == false ? "battery" : "unknown")} · portable chassis {(r.Hardware.IsPortable == true ? "yes" : r.Hardware.IsPortable == false ? "no" : "unknown")} · BIOS {r.Hardware.Bios}")
            .AppendLine($"Current power-plan GUID: {r.ActivePowerPlan}")
            .AppendLine($"Game Mode preference: {Setting(r.GameMode)} · background capture: {Setting(r.BackgroundCapture)}")
            .AppendLine($"HAGS preference: {(r.HagsPreference == 2 ? "on" : r.HagsPreference == 1 ? "off" : "Windows/driver default; not verified active")}");
        b.AppendLine($"System commit: {r.CommitUsedGiB?.ToString("0.0") ?? "unknown"}/{r.CommitLimitGiB?.ToString("0.0") ?? "unknown"} GiB")
            .AppendLine($"CPU sets: {r.Hardware.OsVisibleLogicalProcessors?.ToString() ?? "unknown"} logical CPUs / {r.Hardware.ProcessorGroups?.ToString() ?? "unknown"} groups; {r.Hardware.TopologySource}");
        foreach (var efficiency in r.Hardware.CpuEfficiencyClasses) b.AppendLine($"CPU efficiency class {efficiency.EfficiencyClass}: {efficiency.LogicalProcessors} logical processors (raw Windows class)");
        foreach (var gpu in r.Hardware.Gpus) b.AppendLine($"GPU: {gpu.Name} · driver {gpu.Driver} · dedicated VRAM {gpu.DedicatedMemoryMiB?.ToString() ?? "unknown"} MiB");
        foreach (var d in r.Displays) b.AppendLine($"Display: {(d.Primary ? "primary" : "secondary")} · {d.Width}×{d.Height} · {(d.RefreshHz?.ToString() ?? "unknown")} Hz");
        foreach (var a in r.Hardware.Adapters) b.AppendLine($"Network: {a.Name} · {a.Link}");
        foreach (var v in r.Volumes) b.AppendLine($"Storage {v.Drive}: {v.FreeGiB} GiB free ({v.FreePercent}%)");
        b.AppendLine().AppendLine("OPTIONAL APP COSTS").AppendLine($"CPU = % of total logical CPU capacity over {r.SampleSeconds:0.00}s. Private memory is allocation, not guaranteed reclaimed RAM.");
        foreach (var a in r.CandidateCosts.Take(20)) b.AppendLine($"{a.CpuPercent,6:0.00}% CPU · {a.PrivateMiB,8:0.0} MiB private · {a.Application} ({a.MeasuredProcesses} sampled)");
        if (r.CandidateCosts.Count == 0) b.AppendLine("No optional app resource samples available.");
        b.AppendLine().AppendLine("WHAT TO REVIEW");
        foreach (var a in r.Recommendations) b.AppendLine($"{a.Area}: {a.Finding}").AppendLine(a.Action).AppendLine(a.Evidence).AppendLine();
        foreach (var e in r.Errors) b.AppendLine("Unavailable: " + e);
        return b.ToString();
    }

    private static string Setting(int? n) => n == 1 ? "on" : n == 0 ? "off" : "unspecified; verify Windows UI";
    private static int? ReadDword(RegistryKey hive, string path, string name)
    { try { using var key = hive.OpenSubKey(path); return key?.GetValue(name) is int n ? n : null; } catch { return null; } }
    public static bool? AcPowerOnline() => GetSystemPowerStatus(out var p) && p.Ac <= 1 ? p.Ac == 1 : null;
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, Battery, Percent, Reserved; public uint Life, FullLife; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort Spec, Driver, Size, Extra; public uint Fields; public int X, Y; public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TtOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels; public uint Bits, Width, Height, Flags, Frequency, IcmMethod, IcmIntent, Media, Dither, Reserved1, Reserved2, PanWidth, PanHeight;
    }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode settings);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemCpuSetInformation(IntPtr information, uint length, out uint returnedLength, IntPtr process, uint flags);
}
