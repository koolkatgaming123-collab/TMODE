using System.Diagnostics;

namespace TourneyMode;

/// <summary>Generic cleanup never severs connections. Explicit Codex shutdown
/// uses a separately journaled, restartable lifecycle.</summary>
public static class ConnectionProtection
{
    private static readonly string[] Names = ["ChatGPT.exe", "codex.exe", "Codex Web GPT.exe",
        "codex-code-mode-host.exe", "codex-computer-use-swift.exe", "codex-windows-sandbox-service.exe",
        "BridgeSilentHost.exe", "SpencerNative.exe", "SpencerGateway.exe"];

    public static bool IsRoot(ProcessIdentity process)
    {
        if (Names.Contains(process.Name, StringComparer.OrdinalIgnoreCase)) return true;
        string[] segments = process.Path.Replace('/', '\\').Split('\\');
        if (segments.Any(s => s.Equals(".spencer-native", StringComparison.OrdinalIgnoreCase)
            || s.Equals("CodexLanBridge", StringComparison.OrdinalIgnoreCase)
            || s.Equals(".codex", StringComparison.OrdinalIgnoreCase)
            || s.Equals("codex-runtimes", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Codex Web GPT", StringComparison.OrdinalIgnoreCase))) return true;
        for (int i = 0; i + 1 < segments.Length; i++)
            if (segments[i].Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                && segments[i + 1].Equals("Codex", StringComparison.OrdinalIgnoreCase)) return true;
        return process.Id > 0 && process.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
            && NativeLifecycle.IsKnownPhoneBridge(process.Id);
    }

    public static bool IsApp(AppCandidate app) => app.Id.Equals("spencer-native", StringComparison.OrdinalIgnoreCase)
        || app.Id.Equals("codex-bridge", StringComparison.OrdinalIgnoreCase)
        || app.Id.StartsWith("codex-desktop|", StringComparison.OrdinalIgnoreCase)
        || app.Id.StartsWith("codex-web|", StringComparison.OrdinalIgnoreCase)
        || app.Id.StartsWith("chatgpt-desktop|", StringComparison.OrdinalIgnoreCase)
        || IsRoot(new ProcessIdentity { Name = Path.GetFileName(app.LaunchPath), Path = app.LaunchPath })
        || app.Processes.Any(IsRoot);

    public static List<string> NormalizePaths(IEnumerable<string>? paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths ?? [])
        {
            if (result.Count >= 128) break;
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                    && !path.StartsWith("\\\\", StringComparison.Ordinal) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    result.Add(Path.GetFullPath(path));
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        return result.ToList();
    }

    public static HashSet<int> ExtraIds(List<ProcessIdentity> snapshot, IEnumerable<string>? paths)
    {
        var keep = NormalizePaths(paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = snapshot.Where(p => keep.Contains(p.Path)).Select(p => p.Id).ToHashSet();
        var map = snapshot.ToDictionary(p => p.Id);
        bool added;
        do
        {
            added = false;
            foreach (var p in snapshot)
                if (ids.Contains(p.ParentId) && map.TryGetValue(p.ParentId, out var parent)
                    && parent.StartTicks <= p.StartTicks && ids.Add(p.Id)) added = true;
        } while (added);
        return ids;
    }
}

public static class ProfileDefaults
{
    public static AppOptions Create() => new()
    {
        OptionsVersion = 9, CloseCodex = true, FortniteOnly = true, MaximumHeadroom = true, KeepAppsClosed = true,
        PauseOptionalServices = false, GameTuning = true, SilenceNotifications = true, AutoRestoreOnGameExit = true,
        HighPerformancePower = false, HighPriority = true, PrioritizeNetwork = false, TuneEthernet = false,
        Intent = new UserIntent { Fps = new() { ApplyGameProfile = true } }
    };

    public static AppOptions Upgrade(AppOptions options)
    {
        options.Intent ??= new UserIntent();
        if (options.OptionsVersion < 8)
        {
            // Preserve app/keep choices, but require a newly reviewed per-PC plan.
            // Old all-ON preferences are never promoted into new driver mutations.
            options.Intent = new UserIntent();
            options.HighPerformancePower = false; options.PrioritizeNetwork = false; options.TuneEthernet = false;
            options.ReviewedPlanFingerprint = "";
            options.OptionsVersion = 8;
        }
        if (options.OptionsVersion < 9)
        {
            options.Intent.Fps ??= new();
            options.Intent.Fps.ApplyGameProfile = true;
            options.ReviewedPlanFingerprint = "";
            options.OptionsVersion = 9;
        }
        options.ProtectedAppPaths = ConnectionProtection.NormalizePaths(options.ProtectedAppPaths);
        return options;
    }
}
