using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace TourneyMode;

public sealed class CodexLaunch
{
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public List<ProcessIdentity> Processes { get; set; } = [];
    public bool StopAttempted { get; set; }
    public bool Restored { get; set; }
}
public sealed class CodexBackup
{
    public bool NativeWasRunning { get; set; }
    public bool NativeStopAttempted { get; set; }
    public bool NativeRestored { get; set; }
    public List<CodexLaunch> Launchers { get; set; } = [];
    public List<ProcessIdentity> Targets { get; set; } = [];
    public List<string> KeepPaths { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

/// <summary>Only explicit, restorable installations. Never replay arbitrary command
/// lines or restart individual MCP/tool processes: their hosts recreate them.</summary>
public static class CodexLifecycle
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string MobileLauncher => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "SpencerNativeMobile.vbs");

    internal static bool Desktop(ProcessIdentity p) =>
        (p.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
            && p.Path.Replace('/', '\\').Split('\\').Any(s => s.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("OpenAI.ChatGPT_", StringComparison.OrdinalIgnoreCase)))
        || p.Name.Equals("Codex Web GPT.exe", StringComparison.OrdinalIgnoreCase);

    internal static bool ValidBridge(string node, IReadOnlyList<string> args, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!System.IO.Path.IsPathFullyQualified(node) || !System.IO.Path.GetFileName(node).Equals("node.exe", StringComparison.OrdinalIgnoreCase)
            || args.Count != 9 || !exists(node) || !exists(args[0])
            || !ProcessPolicy.Under(args[0], System.IO.Path.Combine(Local, "CodexLanBridge", "App"))
            || !System.IO.Path.GetFileName(args[0]).Equals("lan-bridge.cjs", StringComparison.OrdinalIgnoreCase)) return false;
        return args[1] == "--role" && args[2] == "initiator" && args[3] == "--mode" && args[4] == "live"
            && args[5] == "--data-root" && ProcessPolicy.Under(args[6], System.IO.Path.Combine(Local, "CodexLanBridge"))
            && args[7] == "--codex" && exists(args[8]) && System.IO.Path.GetFileName(args[8]).Equals("codex.exe", StringComparison.OrdinalIgnoreCase)
            && ProcessPolicy.Under(args[8], System.IO.Path.Combine(Local, "OpenAI", "Codex", "bin"));
    }

    public static CodexBackup Capture(IEnumerable<string> keepPaths)
    {
        var snapshot = ProcessCatalog.Snapshot();
        int session = Process.GetCurrentProcess().SessionId;
        var backup = new CodexBackup { KeepPaths = ConnectionProtection.NormalizePaths(keepPaths) };
        var keep = ConnectionProtection.ExtraIds(snapshot, backup.KeepPaths);
        var available = snapshot.Where(p => p.SessionId == session && !keep.Contains(p.Id)).ToList();
        var roots = new HashSet<int>();
        foreach (var group in available.Where(Desktop).GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(group.Key)) continue;
            backup.Launchers.Add(new() { Kind = "Desktop", Path = group.Key, Processes = group.ToList() });
            roots.UnionWith(group.Select(p => p.Id));
        }
        foreach (var bridge in available.Where(p => p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) && NativeLifecycle.IsBridgeRuntime(p.Id)))
        {
            var args = NativeLifecycle.ScriptArguments(bridge.Id);
            if (!ValidBridge(bridge.Path, args))
            { backup.Notes.Add("Bridge kept running: its launch options are not supported by verified restart."); continue; }
            backup.Launchers.Add(new() { Kind = "Bridge", Path = bridge.Path, Arguments = args.ToList(), Processes = [bridge] });
            roots.Add(bridge.Id);
        }
        var mobile = available.Where(p => p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) && NativeLifecycle.IsKnownPhoneBridge(p.Id)).ToList();
        if (mobile.Count > 0 && File.Exists(MobileLauncher))
        {
            backup.Launchers.Add(new() { Kind = "Mobile", Path = MobileLauncher, Processes = mobile });
            roots.UnionWith(mobile.Select(p => p.Id));
        }
        else if (mobile.Count > 0) backup.Notes.Add("Phone bridge kept running: its installed Startup launcher is missing.");
        var native = available.Where(p => p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) && NativeLifecycle.IsNativeSupervisor(p.Id)).ToList();
        string nativeRoot = System.IO.Path.Combine(Profile, ".spencer-native");
        // Keeping any Native component preserves its shared supervisor.
        if (native.Count > 0 && !snapshot.Any(p => keep.Contains(p.Id) && ProcessPolicy.Under(p.Path, nativeRoot)))
        {
            backup.NativeWasRunning = true;
            roots.UnionWith(native.Select(p => p.Id));
        }
        // Only descendants of verified restartable roots are owned. Unrelated
        // CLI jobs, orphan runtimes and other users' processes remain protected.
        var ids = OwnedIds(snapshot, roots, keep);
        backup.Targets = snapshot.Where(p => ids.Contains(p.Id) && p.Id != Environment.ProcessId
            && p.SessionId == session && p.StartTicks > 0 && p.Path.Length > 0
            && !ProcessCatalog.Critical(p.Id)).ToList();
        foreach (var p in available.Where(ConnectionProtection.IsRoot).Where(p => !ids.Contains(p.Id)))
            backup.Notes.Add("Kept " + p.Name + ": no verified restartable owner (or protected by Keep apps).");
        return backup;
    }

    internal static HashSet<int> OwnedIds(List<ProcessIdentity> snapshot, IEnumerable<int> roots, ISet<int> keep)
    {
        var map = snapshot.ToDictionary(p => p.Id);
        var ids = roots.Where(id => !keep.Contains(id)).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var p in snapshot)
                if (!keep.Contains(p.Id) && ids.Contains(p.ParentId) && map.TryGetValue(p.ParentId, out var parent)
                    && parent.StartTicks <= p.StartTicks && parent.SessionId == p.SessionId && ids.Add(p.Id)) changed = true;
        } while (changed);
        return ids;
    }

    private static bool SafeTarget(ProcessIdentity p, CodexBackup b)
    {
        if (p.Id == Environment.ProcessId || p.SessionId != Process.GetCurrentProcess().SessionId
            || !ProcessCatalog.Matches(p) || ProcessCatalog.Critical(p.Id)) return false;
        var current = ProcessCatalog.Snapshot();
        if (ConnectionProtection.ExtraIds(current, b.KeepPaths).Contains(p.Id)) return false;
        // A mistakenly attached game/peripheral/security child must still survive.
        if (ProcessPolicy.IsDiscord(p) || HeadroomPolicy.ProtectedPath(p.Path)
            || (!ConnectionProtection.IsRoot(p) && ProcessPolicy.IsGamingDependency(p))) return false;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !ProcessPolicy.Under(p.Path, windows) || p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task StopAsync(CodexBackup b, bool force, Action save, Action<string> log)
    {
        foreach (string note in b.Notes.Distinct()) log(note);
        // Every launch recipe and target identity is on disk before shutdown.
        foreach (var item in b.Launchers) item.StopAttempted = true;
        save();
        var desktopIds = OwnedIds(b.Targets, b.Launchers.Where(l => l.Kind == "Desktop").SelectMany(l => l.Processes).Select(p => p.Id), new HashSet<int>());
        var desktop = b.Targets.Where(p => desktopIds.Contains(p.Id)).ToList();
        foreach (var p in desktop.Where(p => Desktop(p) && SafeTarget(p, b)))
        {
            try { using var process = Process.GetProcessById(p.Id); if (ProcessCatalog.MatchesPinned(process, p)) process.CloseMainWindow(); }
            catch (InvalidOperationException) { }
        }
        if (desktop.Any(ProcessCatalog.Matches))
            for (int i = 0; i < 20 && desktop.Any(ProcessCatalog.Matches); i++) await Task.Delay(250).ConfigureAwait(false);
        if (!force && desktop.Any(ProcessCatalog.Matches))
            throw new InvalidOperationException("Codex is still open. OFF can recover; no helper backends were stopped.");
        if (force) Terminate(desktop, b);
        foreach (var item in b.Launchers.Where(l => l.Kind == "Bridge"))
        {
            // Shutdown only the exact captured bridge that owns this API.
            if (!item.Processes.Any(ProcessCatalog.Matches)) continue;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var status = JsonDocument.Parse(await client.GetStringAsync("http://127.0.0.1:45875/status").ConfigureAwait(false));
            var root = status.RootElement;
            if (root.GetProperty("product").GetString() != "CodexLanBridge" || root.GetProperty("role").GetString() != "initiator"
                || !item.Processes.Any(p => p.Id == root.GetProperty("processId").GetInt32() && ProcessCatalog.Matches(p)))
                throw new InvalidOperationException("Bridge API ownership changed; connection helpers were left running.");
            client.DefaultRequestHeaders.Add("X-Codex-Bridge", "1");
            using var response = await client.PostAsync("http://127.0.0.1:45875/shutdown", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        if (b.NativeWasRunning)
        {
            b.NativeStopAttempted = true; save();
            // Installed control drains work and stops its supervisor. A refusal
            // aborts activation rather than forcibly destroying active jobs.
            await NativeLifecycle.StopAsync().ConfigureAwait(false);
        }
        if (force) Terminate(b.Targets, b);
        int left = b.Targets.Count(ProcessCatalog.Matches);
        log("Codex lifecycle: closed " + (b.Targets.Count - left) + "/" + b.Targets.Count + " recorded processes.");
        if (left > 0) log("Some helpers remain running; review their ownership or graceful-close result.");
    }

    private static void Terminate(List<ProcessIdentity> targets, CodexBackup b)
    {
        // Parents first; never use an unbounded Kill(entireProcessTree).
        foreach (var p in targets.OrderBy(p => targets.Any(parent => parent.Id == p.ParentId) ? 1 : 0))
        {
            if (!SafeTarget(p, b)) continue;
            try
            {
                using var process = Process.GetProcessById(p.Id);
                if (ProcessCatalog.MatchesPinned(process, p)) process.Kill(entireProcessTree: false);
            }
            catch (InvalidOperationException) { }
        }
    }

    private static bool Returned(CodexLaunch item)
    {
        var current = ProcessCatalog.Snapshot();
        if (item.Kind == "Mobile") return current.Any(p => p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) && NativeLifecycle.IsKnownPhoneBridge(p.Id));
        if (item.Kind == "Bridge") return ProcessCatalog.ProbeLocalBridge().Ready
            && current.Any(p => p.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) && NativeLifecycle.IsBridgeRuntime(p.Id)
                && NativeLifecycle.ScriptArguments(p.Id).SequenceEqual(item.Arguments, StringComparer.OrdinalIgnoreCase));
        return DesktopReturned(item, current, NativeLifecycle.ScriptArguments);
    }

    internal static bool DesktopReturned(CodexLaunch item, List<ProcessIdentity> current, Func<int, string[]> arguments)
    {
        var originalIds = item.Processes.Select(p => p.Id).ToHashSet();
        var originalHelpers = item.Processes.Where(p => originalIds.Contains(p.ParentId)).ToList();
        return current.Any(p => p.SessionId == Process.GetCurrentProcess().SessionId
            && p.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)
            && !originalHelpers.Any(old => old.Id == p.Id && old.StartTicks == p.StartTicks)
            && !arguments(p.Id).Any(arg => arg.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
            && !current.Any(parent => parent.Id == p.ParentId && parent.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase)));
    }

    public static Task RestoreAsync(CodexBackup b, Action save, Action<string> log) =>
        RestoreWithAsync(b, save, log, NativeLifecycle.StartAsync, Returned, Start, () => Task.Delay(500));

    internal static async Task RestoreWithAsync(CodexBackup b, Action save, Action<string> log,
        Func<Task> startNative, Func<CodexLaunch, bool> returned, Action<CodexLaunch> start, Func<Task> delay)
    {
        if (b.NativeStopAttempted && !b.NativeRestored)
        {
            await startNative().ConfigureAwait(false);
            b.NativeRestored = true; save(); log("Native installed start completed its readiness checks.");
        }
        // Returning providers first prevents a reopened desktop inheriting dead transports.
        foreach (var item in b.Launchers.OrderBy(l => l.Kind == "Bridge" ? 0 : l.Kind == "Mobile" ? 1 : 2))
        {
            if (!item.StopAttempted || item.Restored) continue;
            if (!returned(item))
            {
                start(item);
                for (int i = 0; i < 60 && !returned(item); i++) await delay().ConfigureAwait(false);
                if (!returned(item)) throw new InvalidOperationException(item.Kind + " restart was not observed; recovery journal retained. Retry OFF.");
            }
            item.Restored = true; save(); log(item.Kind + ": returning host observed.");
        }
    }

    private static void Start(CodexLaunch item)
    {
        if (!File.Exists(item.Path)) throw new FileNotFoundException("Recorded Codex launcher is missing.", item.Path);
        var info = new ProcessStartInfo(item.Path) { WorkingDirectory = System.IO.Path.GetDirectoryName(item.Path)!,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (item.Kind == "Bridge")
        {
            if (!ValidBridge(item.Path, item.Arguments)) throw new InvalidOperationException("Bridge restart recipe is invalid.");
            foreach (string arg in item.Arguments) info.ArgumentList.Add(arg);
        }
        else if (item.Kind == "Mobile")
        {
            if (!item.Path.Equals(MobileLauncher, StringComparison.OrdinalIgnoreCase) || item.Arguments.Count != 0)
                throw new InvalidOperationException("Unknown phone startup launcher.");
            info.FileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe");
            info.ArgumentList.Add(item.Path);
        }
        else if (item.Kind == "Desktop" && item.Arguments.Count == 0
            && Desktop(new() { Path = item.Path, Name = System.IO.Path.GetFileName(item.Path) }))
        {
            info.UseShellExecute = true;
            info.WindowStyle = ProcessWindowStyle.Normal;
        }
        else throw new InvalidOperationException("Unknown Codex restart recipe.");
        if (!info.UseShellExecute)
            foreach (string key in new[] { "NODE_OPTIONS", "NODE_PATH", "ELECTRON_RUN_AS_NODE" }) info.Environment.Remove(key);
        Process.Start(info)?.Dispose();
    }
}

