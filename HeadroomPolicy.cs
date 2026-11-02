using System.Diagnostics;

namespace TourneyMode;

public static class HeadroomPolicy
{
    private static readonly string[] SharedHosts = ["node.exe", "node_repl.exe", "bun.exe", "python.exe", "pythonw.exe",
        "java.exe", "javaw.exe", "dotnet.exe", "pwsh.exe", "powershell.exe", "cmd.exe", "wscript.exe", "cscript.exe", "conhost.exe"];
    private static readonly string[] ProtectedSegments = ["Windows Defender", "Windows Defender Advanced Threat Protection",
        "Microsoft Defender", "ESET", "Bitdefender", "Malwarebytes", "Sophos", "Kaspersky Lab", "AVAST Software",
        "AVG", "Trend Micro", "SentinelOne", "CrowdStrike", "NVIDIA Corporation", "AMD", "Realtek", "Razer",
        "SteelSeries", "Logitech", "LGHUB", "Corsair", "Elgato", "Voicemeeter", "VB-Audio", "EasyAntiCheat", "BattlEye"];

    public static bool ProtectedPath(string path) => path.Replace('/', '\\').Split('\\')
        .Any(segment => ProtectedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    public static HashSet<int> DependencyIds(List<ProcessIdentity> snapshot)
    {
        var protectedIds = snapshot.Where(ProcessPolicy.IsGamingDependency).Select(process => process.Id).ToHashSet();
        var map = snapshot.ToDictionary(process => process.Id);
        bool added;
        do
        {
            added = false;
            foreach (var process in snapshot)
                if (protectedIds.Contains(process.ParentId) && map.TryGetValue(process.ParentId, out var parent)
                    && parent.StartTicks <= process.StartTicks && protectedIds.Add(process.Id)) added = true;
        } while (added);
        return protectedIds;
    }

    private static bool ManagedRuntime(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return ProcessPolicy.Under(path, Path.Combine(profile, ".spencer-native"))
            || ProcessPolicy.Under(path, Path.Combine(local, "CodexLanBridge"));
    }

    public static bool Eligible(ProcessIdentity process, int session, ISet<int> protectedIds)
    {
        if (ProcessPolicy.Protected(process, session, protectedIds) || ProtectedPath(process.Path) || ManagedRuntime(process.Path)) return false;
        if (process.Name.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Path.GetExtension(process.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        return !ProcessPolicy.Under(process.Path, windowsApps)
            && !ProcessPolicy.Under(process.Path, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    public static void AddCandidates(List<AppCandidate> groups, List<ProcessIdentity> snapshot, HashSet<int> ancestors, Func<string, bool> exists)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var protectedIds = DependencyIds(snapshot);
        protectedIds.UnionWith(ancestors);
        var claimed = groups.SelectMany(group => group.Processes).Select(process => process.Id).ToHashSet();
        foreach (var group in snapshot.Where(process => !claimed.Contains(process.Id) && Eligible(process, session, protectedIds))
                     .GroupBy(process => process.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!exists(group.Key)) continue;
            var app = new AppCandidate
            {
                Id = "background|" + group.Key.ToLowerInvariant(), Name = Path.GetFileNameWithoutExtension(group.Key) + " (background)",
                LaunchPath = group.Key, Processes = group.ToList(), DefaultSelected = false,
                Detail = "Unrecognized background app: review before selecting. Its executable is outside Windows; that does not prove it is unnecessary. " + group.Key
            };
            groups.Add(app);
        }
        foreach (var app in groups)
        {
            var ids = app.Processes.Select(process => process.Id).ToHashSet();
            bool added;
            do
            {
                added = false;
                foreach (var process in snapshot)
                    if (!claimed.Contains(process.Id) && !ids.Contains(process.Id) && ids.Contains(process.ParentId)
                        && app.Processes.Any(parent => parent.Id == process.ParentId && parent.StartTicks <= process.StartTicks)
                        && process.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)
                        && !ProcessPolicy.Protected(process, session, protectedIds))
                    { app.Processes.Add(process); ids.Add(process.Id); claimed.Add(process.Id); added = true; }
            } while (added);
        }
    }

    public static bool IsDedicatedApp(AppCandidate app) => app.Id.Equals("background|" + app.LaunchPath, StringComparison.OrdinalIgnoreCase)
        && !SharedHosts.Contains(Path.GetFileName(app.LaunchPath), StringComparer.OrdinalIgnoreCase)
        && Eligible(new ProcessIdentity { Id = int.MaxValue, SessionId = Process.GetCurrentProcess().SessionId,
            StartTicks = 1, Path = app.LaunchPath, Name = Path.GetFileName(app.LaunchPath) }, Process.GetCurrentProcess().SessionId, new HashSet<int>());
}
