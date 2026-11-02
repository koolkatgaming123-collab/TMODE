using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TourneyMode;

/// <summary>One exact process identity and its write-ahead, reversible priority change.</summary>
public sealed class BackgroundPriorityBackup
{
    public ProcessIdentity Process { get; set; } = new();
    public int Original { get; set; }
    public int Requested { get; set; } = (int)ProcessPriorityClass.BelowNormal;
    public bool ApplyAttempted { get; set; }
    public bool Applied { get; set; }
    public bool Restored { get; set; }
    public bool Conflict { get; set; }
    public string Status { get; set; } = "Captured";
    public string Error { get; set; } = "";
}

/// <summary>Separate from ISystemFacade.SetPriority, whose production contract is Fortnite-only.</summary>
public interface IBackgroundPrioritySystem
{
    List<ProcessIdentity> Snapshot();
    bool Alive(ProcessIdentity process);
    bool CanAdjust(ProcessIdentity process);
    int GetPriority(ProcessIdentity process);
    void SetPriority(ProcessIdentity process, int priority);
    void SetPriorityIfUnchanged(ProcessIdentity process, int expected, int requested)
    {
        if (GetPriority(process) != expected) throw new BackgroundPriorityConflictException();
        SetPriority(process, requested);
    }
}

/// <summary>Raised before a setter when the recorded expected priority is no longer current.</summary>
public sealed class BackgroundPriorityConflictException : InvalidOperationException
{
    public BackgroundPriorityConflictException() : base("Background priority changed externally before the write; the external value was preserved.") { }
}

/// <summary>
/// Explicitly approved remaining executables only. This governor runs during
/// preparation/recovery, never from a match maintenance timer. EcoQoS is not used.
/// </summary>
public static class BackgroundGovernor
{
    public const int MaximumProcesses = 32;
    public const int RequestedPriority = (int)ProcessPriorityClass.BelowNormal;

    private static readonly string[] SensitiveMarkers =
    [
        "obs-studio", "obs64", "obs32", "obs.exe", "streamlabs", "xsplit", "bandicam", "camtasia", "gamecaster",
        "twitchstudio", "medal", "outplayed", "sharex", "snagit", "screentogif", "ffmpeg", "ffplay",
        "nvidia broadcast", "gamebar", "bcastdvr", "voicemeeter", "vb-audio", "equalizerapo", "eartrumpet",
        "elgato", "wave link", "wavelink", "voicemod", "nahimic", "sonar", "ds4windows", "dualsense", "inputmapper",
        "rewasd", "joytokey", "antimicro", "hidhide", "xmousebuttoncontrol", "rawaccel", "autohotkey",
        "exitlag", "noping", "cloudflare", "wireguard", "warp.exe", "tailscale", "zerotier", "hamachi",
        "vpn", "mullvad", "rtss", "rivatuner", "afterburner", "processlasso"
    ];
    private static readonly string[] SharedHosts =
    [
        "node.exe", "node_repl.exe", "bun.exe", "python.exe", "pythonw.exe", "java.exe", "javaw.exe",
        "dotnet.exe", "pwsh.exe", "powershell.exe", "cmd.exe", "wscript.exe", "cscript.exe", "conhost.exe"
    ];

    public static List<BackgroundPriorityBackup> Capture(List<AppCandidate> eligibleApps,
        IEnumerable<string> approvedPaths, IBackgroundPrioritySystem system, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(eligibleApps);
        ArgumentNullException.ThrowIfNull(system);
        var approved = Normalize(approvedPaths);
        if (approved.Count == 0) return [];
        // Root supplies its reviewed, non-closing app list; exact paths are still
        // required for every child. Shared runtimes never inherit approval.
        var candidates = eligibleApps.Where(app => !Sensitive(app)).SelectMany(app => app.Processes)
            .Where(process => approved.Contains(process.Path) && !Sensitive(process))
            .DistinctBy(Key).Take(MaximumProcesses + 1).ToList();
        if (candidates.Count == 0) return [];
        if (candidates.Count > MaximumProcesses) SafeLog(log, "Background governor: only the first 32 approved process identities are considered.");
        var live = system.Snapshot().ToDictionary(process => process.Id);
        var result = new List<BackgroundPriorityBackup>();
        foreach (var recorded in candidates.Take(MaximumProcesses))
        {
            try
            {
                if (!live.TryGetValue(recorded.Id, out var fresh) || !Same(recorded, fresh)
                    || !system.Alive(fresh) || !system.CanAdjust(fresh)) continue;
                int original = system.GetPriority(fresh);
                // Never raise an already lower-priority app or alter Realtime.
                if (!RestorablePriority(original))
                { SafeLog(log, "Background governor kept " + fresh.Name + ": current priority is already low or outside the supported classes."); continue; }
                result.Add(new() { Process = Copy(fresh), Original = original });
            }
            catch (Exception error) { SafeLog(log, "Background governor skipped " + recorded.Name + ": " + error.Message); }
        }
        return result;
    }

    public static void Apply(IBackgroundPrioritySystem system, List<BackgroundPriorityBackup> backups,
        Action persist, Action<string> log)
    {
        ValidateInputs(system, backups, persist);
        var failures = new List<Exception>();
        foreach (var backup in backups)
        {
            Validate(backup);
            if (backup.Restored || backup.Applied) continue;
            if (backup.ApplyAttempted)
                throw new InvalidOperationException("A background priority attempt is uncertain; restore it before applying again.");
            if (!system.Alive(backup.Process)) { Complete(backup, "Exited", "Original identity is absent; no priority change.", persist); continue; }
            if (!system.CanAdjust(backup.Process)) { Complete(backup, "Skipped", "Process is foreground, kept, protected or no longer eligible.", persist); continue; }
            int current = system.GetPriority(backup.Process);
            if (current != backup.Original)
            { Complete(backup, "Skipped", "Priority changed after capture; the external value was preserved.", persist); continue; }
            backup.ApplyAttempted = true; backup.Status = "Pending"; backup.Error = "";
            persist(); // Must succeed before the setter, including setters that partially fail.
            try
            {
                system.SetPriorityIfUnchanged(backup.Process, backup.Original, backup.Requested);
                if (!system.Alive(backup.Process) || system.GetPriority(backup.Process) != backup.Requested)
                    throw new InvalidOperationException("Requested background priority was not verified on the original process.");
                backup.Applied = true; backup.Status = "Applied";
            }
            catch (BackgroundPriorityConflictException error)
            {
                backup.Restored = true; backup.Status = "Skipped"; backup.Error = error.Message;
            }
            catch (Exception error)
            {
                backup.Status = "Failed"; backup.Error = error.Message;
                failures.Add(new InvalidOperationException(backup.Process.Name + ": " + error.Message, error));
            }
            persist(); // A persistence failure aborts further mutations; pre-write recovery remains valid.
            SafeLog(log, "Background governor " + backup.Process.Name + ": " + backup.Status + (backup.Error.Length > 0 ? " - " + backup.Error : " (Below normal; benefit unmeasured)."));
        }
        if (failures.Count > 0) throw new AggregateException("Some background priority requests failed; originals remain journaled.", failures);
    }

    public static void Restore(IBackgroundPrioritySystem system, List<BackgroundPriorityBackup> backups,
        Action persist, Action<string> log)
    {
        ValidateInputs(system, backups, persist);
        var failures = new List<Exception>();
        foreach (var backup in backups)
        {
            Validate(backup);
            if (backup.Restored) continue;
            if (!backup.ApplyAttempted) { Complete(backup, "Recovered", "No setter was attempted.", persist); continue; }
            if (!system.Alive(backup.Process)) { Complete(backup, "Recovered", "Original process exited; a reused PID was not changed.", persist); continue; }
            try
            {
                int current = system.GetPriority(backup.Process);
                if (current == backup.Original) { Complete(backup, "Recovered", "Exact original priority is already present.", persist); continue; }
                if (current != backup.Requested)
                {
                    backup.Conflict = true; backup.Status = "Conflict";
                    backup.Error = "An external priority change was preserved. Recovery requires review; no original value was forced.";
                    failures.Add(new InvalidOperationException(backup.Process.Name + ": " + backup.Error));
                }
                else if (!system.CanAdjust(backup.Process))
                {
                    backup.Status = "Deferred"; backup.Conflict = false;
                    backup.Error = "Process is foreground, kept/protected, or game preparation is locked. Retry recovery when eligible; priority was not changed.";
                    failures.Add(new InvalidOperationException(backup.Process.Name + ": " + backup.Error));
                }
                else
                {
                    system.SetPriorityIfUnchanged(backup.Process, backup.Requested, backup.Original);
                    if (system.Alive(backup.Process) && system.GetPriority(backup.Process) != backup.Original)
                        throw new InvalidOperationException("Exact original background priority was not verified.");
                    backup.Restored = true; backup.Conflict = false; backup.Status = "Recovered"; backup.Error = "";
                }
            }
            catch (BackgroundPriorityConflictException error)
            {
                backup.Conflict = true; backup.Status = "Conflict"; backup.Error = error.Message;
                failures.Add(new InvalidOperationException(backup.Process.Name + ": " + error.Message, error));
            }
            catch (Exception error)
            {
                backup.Status = "Failed"; backup.Error = error.Message;
                failures.Add(new InvalidOperationException(backup.Process.Name + ": " + error.Message, error));
            }
            persist();
            SafeLog(log, "Background priority recovery " + backup.Process.Name + ": " + backup.Status + (backup.Error.Length > 0 ? " - " + backup.Error : "."));
        }
        if (failures.Count > 0) throw new AggregateException("Background priority recovery remains pending; no external edits were overwritten.", failures);
    }

    private static void Complete(BackgroundPriorityBackup item, string status, string detail, Action persist)
    { item.Restored = true; item.Conflict = false; item.Status = status; item.Error = detail; persist(); }
    private static void ValidateInputs(IBackgroundPrioritySystem system, List<BackgroundPriorityBackup> backups, Action persist)
    {
        ArgumentNullException.ThrowIfNull(system); ArgumentNullException.ThrowIfNull(backups); ArgumentNullException.ThrowIfNull(persist);
        if (backups.Count > MaximumProcesses) throw new InvalidDataException("Background governor recovery exceeds its 32-process bound.");
        if (backups.Select(item => Key(item.Process)).Distinct().Count() != backups.Count) throw new InvalidDataException("Duplicate background process identities.");
        foreach (var backup in backups) Validate(backup);
    }
    private static void Validate(BackgroundPriorityBackup item)
    {
        if (item.Requested != RequestedPriority || !RestorablePriority(item.Original) || item.Process.Id <= 4
            || item.Process.StartTicks <= 0 || !Path.IsPathFullyQualified(item.Process.Path) || Sensitive(item.Process))
            throw new InvalidDataException("Unsupported or protected background priority recovery entry.");
    }
    private static bool RestorablePriority(int value) => value == (int)ProcessPriorityClass.Normal || value == (int)ProcessPriorityClass.AboveNormal || value == (int)ProcessPriorityClass.High;
    internal static bool Sensitive(ProcessIdentity process) => SharedHosts.Contains(process.Name, StringComparer.OrdinalIgnoreCase)
        || SensitiveMarkers.Any(marker => (process.Name + "|" + process.Path).Contains(marker, StringComparison.OrdinalIgnoreCase))
        || ProcessPolicy.IsGamingDependency(process);
    private static bool Sensitive(AppCandidate app) => SharedHosts.Contains(Path.GetFileName(app.LaunchPath), StringComparer.OrdinalIgnoreCase)
        || ProcessPolicy.IsDiscordApp(app) || ConnectionProtection.IsApp(app)
        || SensitiveMarkers.Any(marker => (app.Name + "|" + app.LaunchPath).Contains(marker, StringComparison.OrdinalIgnoreCase));
    internal static HashSet<string> Normalize(IEnumerable<string>? paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths ?? [])
        {
            try { if (Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) result.Add(Path.GetFullPath(path)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
        }
        return result;
    }
    internal static bool Same(ProcessIdentity first, ProcessIdentity second) => first.Id == second.Id && first.StartTicks == second.StartTicks
        && first.SessionId == second.SessionId && first.Path.Equals(second.Path, StringComparison.OrdinalIgnoreCase);
    private static string Key(ProcessIdentity process) => process.Id + ":" + process.StartTicks + ":" + process.SessionId + ":" + process.Path.ToLowerInvariant();
    private static ProcessIdentity Copy(ProcessIdentity process) => new() { Id = process.Id, ParentId = process.ParentId, SessionId = process.SessionId, StartTicks = process.StartTicks, Name = process.Name, Path = process.Path };
    private static void SafeLog(Action<string>? log, string text) { try { log?.Invoke(text); } catch { } }
    internal static bool DescendantOrSelf(int process, int root, IReadOnlyDictionary<int, int> parents)
    {
        var seen = new HashSet<int>();
        while (process > 0 && seen.Add(process)) { if (process == root) return true; if (!parents.TryGetValue(process, out process)) return false; }
        return false;
    }
}

/// <summary>Ordinary Windows access only; no privilege bypass, EcoQoS or global scheduling changes.</summary>
public sealed class WindowsBackgroundPrioritySystem : IBackgroundPrioritySystem
{
    private readonly ISystemFacade protection;
    private readonly HashSet<string> excluded;
    public WindowsBackgroundPrioritySystem(ISystemFacade protection, IEnumerable<string>? excludedPaths = null)
    { this.protection = protection ?? throw new ArgumentNullException(nameof(protection)); excluded = BackgroundGovernor.Normalize(excludedPaths); }
    public List<ProcessIdentity> Snapshot()
    {
        if (GamePresent()) throw new InvalidOperationException("Background priority preparation is deferred while Fortnite runs; no application scan was performed.");
        return protection.Snapshot();
    }
    public bool Alive(ProcessIdentity process) => protection.Alive(process);
    public bool CanAdjust(ProcessIdentity process)
    {
        try
        {
            if (GamePresent() || excluded.Contains(process.Path) || BackgroundGovernor.Sensitive(process) || !protection.CanTouch(process)) return false;
            IntPtr window = GetForegroundWindow(); if (window == IntPtr.Zero) return false;
            GetWindowThreadProcessId(window, out uint foreground); if (foreground == 0) return false;
            var parents = ProcessCatalog.ParentIds();
            if (!parents.ContainsKey(process.Id) || !parents.ContainsKey((int)foreground)) return false;
            string foregroundPath = ProcessCatalog.GetPath((int)foreground);
            if (string.IsNullOrWhiteSpace(foregroundPath)) return false;
            // Also preserve foreground ancestors and same-image helpers, a
            // conservative extension of the required foreground-descendant rule.
            return !BackgroundGovernor.DescendantOrSelf(process.Id, (int)foreground, parents)
                && !BackgroundGovernor.DescendantOrSelf((int)foreground, process.Id, parents)
                && !foregroundPath.Equals(process.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    public int GetPriority(ProcessIdentity identity)
    {
        using var process = Process.GetProcessById(identity.Id);
        if (!ProcessCatalog.MatchesPinned(process, identity)) throw new InvalidOperationException("Background process identity changed.");
        return (int)process.PriorityClass;
    }
    public void SetPriority(ProcessIdentity identity, int priority)
        => WritePriority(identity, priority, null);
    public void SetPriorityIfUnchanged(ProcessIdentity identity, int expected, int requested)
        => WritePriority(identity, requested, expected);
    private void WritePriority(ProcessIdentity identity, int priority, int? expected)
    {
        if (priority != (int)ProcessPriorityClass.BelowNormal && priority != (int)ProcessPriorityClass.Normal
            && priority != (int)ProcessPriorityClass.AboveNormal && priority != (int)ProcessPriorityClass.High)
            throw new InvalidOperationException("Only the reversible supported priority classes are allowed.");
        using var process = Process.GetProcessById(identity.Id);
        if (!ProcessCatalog.MatchesPinned(process, identity)) throw new InvalidOperationException("Background process identity changed.");
        if (!CanAdjust(identity)) throw new InvalidOperationException("Background process became foreground, protected, excluded, or game preparation locked.");
        if (expected.HasValue && (int)process.PriorityClass != expected.Value) throw new BackgroundPriorityConflictException();
        process.PriorityClass = (ProcessPriorityClass)priority;
        process.Refresh();
        if (!ProcessCatalog.MatchesPinned(process, identity) || (int)process.PriorityClass != priority)
            throw new InvalidOperationException("Windows did not verify the background priority change on the pinned identity.");
    }
    private static bool GamePresent()
    {
        var games = Process.GetProcessesByName("FortniteClient-Win64-Shipping");
        try { return games.Length > 0; } finally { foreach (var game in games) game.Dispose(); }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
