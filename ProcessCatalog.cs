using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TourneyMode;

public static class ProcessCatalog
{
    public sealed class BridgeReadiness
    {
        public bool Ready { get; set; }
        public string Product { get; set; } = "";
        public string Version { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string ThreadId { get; set; } = "";
        public int ProcessId { get; set; }
        public string CommandHealthStatus { get; set; } = "";
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// A bridge is restored when its actual runtime and local application API return.
    /// Native's launcher and bridge share node.exe, so generic same-image parent
    /// heuristics cannot distinguish a live bridge from a surviving helper.
    /// </summary>
    public static BridgeReadiness ProbeLocalBridge(string? expectedProjectId = null, string? expectedThreadId = null)
    {
        var result = new BridgeReadiness();
        if ((!string.IsNullOrWhiteSpace(expectedProjectId) && !Guid.TryParse(expectedProjectId, out _))
            || (!string.IsNullOrWhiteSpace(expectedThreadId) && !Guid.TryParse(expectedThreadId, out _)))
        {
            result.Error = "Invalid expected bridge project or thread identity.";
            return result;
        }
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            var runtimes = Snapshot().Where(p => p.SessionId == session
                && p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
                && NativeLifecycle.IsBridgeRuntime(p.Id)).ToList();
            if (runtimes.Count == 0)
            {
                result.Error = "The known local bridge runtime has not returned.";
                return result;
            }
            using var client = new HttpClient
            {
                BaseAddress = new Uri("http://127.0.0.1:45875"),
                Timeout = TimeSpan.FromMilliseconds(1500),
                MaxResponseContentBufferSize = 16 * 1024 * 1024
            };
            client.DefaultRequestHeaders.Add("X-Codex-Bridge", "1");
            if (!string.IsNullOrWhiteSpace(expectedProjectId))
                client.DefaultRequestHeaders.Add("X-Codex-Project", expectedProjectId);
            using var response = client.GetAsync("/status").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                result.Error = "The local bridge status request was not accepted.";
                return result;
            }
            using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            JsonElement root = document.RootElement;
            static string ReadString(JsonElement value, string name) => value.TryGetProperty(name, out var field)
                && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
            result.Product = ReadString(root, "product");
            result.Version = ReadString(root, "version");
            if (root.TryGetProperty("processId", out var pid) && pid.TryGetInt32(out int processId)) result.ProcessId = processId;
            if (root.TryGetProperty("activityScope", out var scope) && scope.ValueKind == JsonValueKind.Object)
                result.ProjectId = ReadString(scope, "projectId");
            if (!root.TryGetProperty("local", out var local) || local.ValueKind != JsonValueKind.Object)
            {
                result.Error = "The local bridge did not report application readiness.";
                return result;
            }
            result.ThreadId = ReadString(local, "threadId");
            if (local.TryGetProperty("commandHealth", out var health) && health.ValueKind == JsonValueKind.Object)
                result.CommandHealthStatus = ReadString(health, "status");
            // Match the status server PID to a known runtime role as well as the product.
            // A helper with the same node.exe image is insufficient evidence of recovery.
            bool knownServer = runtimes.Any(p => p.Id == result.ProcessId && Matches(p));
            bool correctProject = string.IsNullOrWhiteSpace(expectedProjectId)
                || result.ProjectId.Equals(expectedProjectId, StringComparison.OrdinalIgnoreCase);
            bool correctThread = string.IsNullOrWhiteSpace(expectedThreadId)
                || result.ThreadId.Equals(expectedThreadId, StringComparison.OrdinalIgnoreCase);
            bool codexReady = local.TryGetProperty("codexReady", out var ready) && ready.ValueKind == JsonValueKind.True;
            result.Ready = knownServer && result.Product == "CodexLanBridge"
                && ReadString(root, "role") == "initiator" && codexReady
                && !string.IsNullOrWhiteSpace(result.ThreadId) && correctProject && correctThread;
            if (!result.Ready) result.Error = !correctProject || !correctThread
                ? "The local bridge returned a different project or conversation."
                : "The local bridge runtime or application is not ready yet.";
            // commandHealth is separate evidence: stale 'unverified' does not mean the
            // application failed to return, and Ready does not claim a command test passed.
            return result;
        }
        catch (Exception error)
        {
            result.Error = "Local bridge readiness could not be verified (" + error.GetType().Name + ").";
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size, Usage, Id;
        public IntPtr Heap;
        public uint Module, Threads, Parent;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(IntPtr handle, out bool critical);

    public static string GetPath(int id)
    {
        var h = OpenProcess(0x1000, false, id);
        if (h == IntPtr.Zero) return "";
        try { return GetHandlePath(h); }
        finally { CloseHandle(h); }
    }

    private static string GetHandlePath(IntPtr handle)
    { var b = new StringBuilder(32768); int n = b.Capacity; return QueryFullProcessImageName(handle, 0, b, ref n) ? b.ToString() : ""; }

    public static bool MatchesPinned(Process process, ProcessIdentity expected)
    {
        try
        {
            // Pin the same Process object's handle before inspecting it and mutating it.
            var handle = process.SafeHandle;
            return !process.HasExited && process.Id == expected.Id && process.SessionId == expected.SessionId
                && process.StartTime.ToUniversalTime().Ticks == expected.StartTicks
                && GetHandlePath(handle.DangerousGetHandle()).Equals(expected.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>A lightweight ancestry map: enumerates IDs without opening process handles.</summary>
    public static Dictionary<int, int> ParentIds()
    {
        var result = new Dictionary<int, int>();
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) throw new InvalidOperationException("Cannot enumerate process ancestry.");
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = "" };
            if (!Process32FirstW(handle, ref entry)) return result;
            do { result[(int)entry.Id] = (int)entry.Parent; }
            while (Process32NextW(handle, ref entry));
        }
        finally { CloseHandle(handle); }
        return result;
    }

    public static List<ProcessIdentity> Snapshot(ISet<string>? names = null)
    {
        var result = new List<ProcessIdentity>();
        var h = CreateToolhelp32Snapshot(2, 0);
        if (h == new IntPtr(-1)) throw new InvalidOperationException("Cannot enumerate processes.");
        try
        {
            var e = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = "" };
            if (!Process32FirstW(h, ref e)) return result;
            do
            {
                // App-guard sweeps enumerate once, opening handles only for
                // recorded image names; unrelated processes are not inspected.
                if (names is not null && !names.Contains(e.Name)) continue;
                try
                {
                    using var p = Process.GetProcessById((int)e.Id);
                    result.Add(new ProcessIdentity { Id = p.Id, ParentId = (int)e.Parent, Name = e.Name, SessionId = p.SessionId,
                        StartTicks = p.StartTime.ToUniversalTime().Ticks, Path = GetPath(p.Id) });
                }
                catch { /* Inaccessible/system/exited processes never become candidates. */ }
            } while (Process32NextW(h, ref e));
        }
        finally { CloseHandle(h); }
        return result;
    }

    public static bool Matches(ProcessIdentity expected)
    {
        try
        {
            using var p = Process.GetProcessById(expected.Id);
            return !p.HasExited && p.SessionId == expected.SessionId && p.StartTime.ToUniversalTime().Ticks == expected.StartTicks
                && string.Equals(GetPath(p.Id), expected.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool Critical(int id)
    {
        var h = OpenProcess(0x1000, false, id);
        if (h == IntPtr.Zero) return true;
        try { return !IsProcessCritical(h, out bool critical) || critical; }
        finally { CloseHandle(h); }
    }
}

public static class ProcessPolicy
{
    private sealed record AppSpec(string Id, string Name, string Exe, bool Selected, string[]? InstallationMarkers = null);

    // Keep the original profile's apps and defaults unchanged. The broader profile
    // is still an explicit list of restartable user apps, not a process-name denylist.
    private static readonly AppSpec[] StandardApps = [
        new("chrome", "Google Chrome", "chrome.exe", true), new("edge", "Microsoft Edge", "msedge.exe", true),
        new("brave", "Brave", "brave.exe", true), new("firefox", "Firefox", "firefox.exe", true),
        new("spotify", "Spotify", "Spotify.exe", true),
        new("slack", "Slack", "slack.exe", true), new("telegram", "Telegram", "Telegram.exe", true),
        new("onedrive", "OneDrive (sync pauses)", "OneDrive.exe", true),
        new("codex-web", "Codex Web GPT", "Codex Web GPT.exe", true),
        new("steam", "Steam (may affect Steam games/controllers)", "steam.exe", false)
    ];

    // Installation markers are checked only below normal Program Files/AppData
    // locations. A renamed executable, portable script runtime, service host,
    // hardware controller or an unrelated game never becomes a new candidate.
    private static readonly AppSpec[] FortniteOnlyApps = [
        new("copilot", "Microsoft Copilot", "copilotapp.exe", true, ["\\Microsoft\\Copilot\\Application\\"]),
        new("codex-desktop", "Codex desktop", "ChatGPT.exe", true, ["\\WindowsApps\\OpenAI.Codex_"]),
        new("chatgpt-desktop", "ChatGPT desktop", "ChatGPT.exe", true, ["\\WindowsApps\\OpenAI.ChatGPT_"]),
        new("opera", "Opera", "opera.exe", true, ["\\Opera\\", "\\Opera GX\\"]),
        new("vivaldi", "Vivaldi", "vivaldi.exe", true, ["\\Vivaldi\\"]),
        new("teams", "Microsoft Teams", "ms-teams.exe", true, ["\\WindowsApps\\MSTeams_"]),
        new("teams-classic", "Microsoft Teams classic", "Teams.exe", true, ["\\Microsoft\\Teams\\"]),
        new("signal", "Signal", "Signal.exe", true, ["\\Programs\\signal-desktop\\", "\\Signal\\"]),
        new("whatsapp", "WhatsApp", "WhatsApp.exe", true, ["\\WhatsApp\\", "\\WindowsApps\\5319275A.WhatsAppDesktop_"]),
        new("zoom", "Zoom", "Zoom.exe", true, ["\\Zoom\\"]),
        new("skype", "Skype", "Skype.exe", true, ["\\Microsoft\\Skype for Desktop\\", "\\Skype\\"]),
        new("battlenet", "Battle.net launcher", "Battle.net.exe", true, ["\\Battle.net\\"]),
        new("ea", "EA app", "EADesktop.exe", true, ["\\Electronic Arts\\EA Desktop\\"]),
        new("ubisoft", "Ubisoft Connect", "UbisoftConnect.exe", true, ["\\Ubisoft\\Ubisoft Game Launcher\\"]),
        new("ubisoft-upc", "Ubisoft Connect client", "upc.exe", true, ["\\Ubisoft\\Ubisoft Game Launcher\\"]),
        new("gog", "GOG Galaxy", "GalaxyClient.exe", true, ["\\GOG Galaxy\\"]),
        new("rockstar", "Rockstar Games Launcher", "Launcher.exe", true, ["\\Rockstar Games\\Launcher\\"]),
        new("riot", "Riot client", "RiotClientServices.exe", true, ["\\Riot Games\\Riot Client\\"]),
        new("playnite", "Playnite", "Playnite.DesktopApp.exe", true, ["\\Playnite\\"]),
        new("word", "Microsoft Word (save documents first)", "WINWORD.EXE", true, ["\\Microsoft Office\\"]),
        new("excel", "Microsoft Excel (save workbooks first)", "EXCEL.EXE", true, ["\\Microsoft Office\\"]),
        new("powerpoint", "Microsoft PowerPoint (save presentations first)", "POWERPNT.EXE", true, ["\\Microsoft Office\\"]),
        new("outlook", "Microsoft Outlook", "OUTLOOK.EXE", true, ["\\Microsoft Office\\"]),
        new("onenote", "Microsoft OneNote", "ONENOTE.EXE", true, ["\\Microsoft Office\\"]),
        new("access", "Microsoft Access (save databases first)", "MSACCESS.EXE", true, ["\\Microsoft Office\\"]),
        new("vscode", "Visual Studio Code (save work first)", "Code.exe", true, ["\\Microsoft VS Code\\"]),
        new("vscode-insiders", "Visual Studio Code Insiders (save work first)", "Code - Insiders.exe", true, ["\\Microsoft VS Code Insiders\\"]),
        new("cursor", "Cursor (save work first)", "Cursor.exe", true, ["\\Cursor\\"]),
        new("windsurf", "Windsurf (save work first)", "Windsurf.exe", true, ["\\Windsurf\\"]),
        new("visualstudio", "Visual Studio (save work first)", "devenv.exe", true, ["\\Microsoft Visual Studio\\"]),
        new("idea", "IntelliJ IDEA (save work first)", "idea64.exe", true, ["\\JetBrains\\"]),
        new("pycharm", "PyCharm (save work first)", "pycharm64.exe", true, ["\\JetBrains\\"]),
        new("rider", "JetBrains Rider (save work first)", "rider64.exe", true, ["\\JetBrains\\"]),
        new("notepad-plus-plus", "Notepad++ (save work first)", "notepad++.exe", true, ["\\Notepad++\\"]),
        new("sublime", "Sublime Text (save work first)", "sublime_text.exe", true, ["\\Sublime Text\\", "\\Sublime Text 3\\"]),
        new("obsidian", "Obsidian (save work first)", "Obsidian.exe", true, ["\\Obsidian\\"]),
        new("notion", "Notion", "Notion.exe", true, ["\\Notion\\"]),
        new("acrobat", "Adobe Acrobat (save documents first)", "Acrobat.exe", true, ["\\Adobe\\Acrobat\\", "\\Adobe\\Acrobat DC\\"]),
        new("reader", "Adobe Acrobat Reader", "AcroRd32.exe", true, ["\\Adobe\\Acrobat Reader DC\\", "\\Adobe\\Reader 11.0\\"]),
        new("dropbox", "Dropbox (sync pauses)", "Dropbox.exe", true, ["\\Dropbox\\"]),
        new("googledrive", "Google Drive (sync pauses)", "GoogleDriveFS.exe", true, ["\\Google\\Drive File Stream\\"]),
        new("boxdrive", "Box Drive (sync pauses)", "Box.exe", true, ["\\Box\\Box\\"]),
        new("vlc", "VLC media player", "vlc.exe", true, ["\\VideoLAN\\VLC\\"]),
        new("foobar", "foobar2000", "foobar2000.exe", true, ["\\foobar2000\\"]),
        new("musicbee", "MusicBee", "MusicBee.exe", true, ["\\MusicBee\\"]),
        new("itunes", "iTunes", "iTunes.exe", true, ["\\iTunes\\"]),
        new("tidal", "TIDAL", "TIDAL.exe", true, ["\\TIDAL\\"]),
        new("plexamp", "Plexamp", "Plexamp.exe", true, ["\\Plexamp\\"]),
        new("obs", "OBS Studio (recording stops)", "obs64.exe", true, ["\\obs-studio\\"]),
        new("obs32", "OBS Studio 32-bit (recording stops)", "obs32.exe", true, ["\\obs-studio\\"]),
        new("streamlabs", "Streamlabs Desktop (recording stops)", "Streamlabs Desktop.exe", true, ["\\Streamlabs Desktop\\", "\\slobs-client\\"]),
        new("medal", "Medal (recording stops)", "Medal.exe", true, ["\\Medal\\"]),
        new("overwolf", "Overwolf (overlays and recording stop)", "Overwolf.exe", true, ["\\Overwolf\\"]),
        new("gamebar", "Xbox Game Bar (recording stops)", "GameBar.exe", true, ["\\WindowsApps\\Microsoft.XboxGamingOverlay_"])
    ];

    public static bool Under(string path, string directory) => !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(directory)
        && path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ProtectedNames = ["fortnite", "unrealeditor", "easyanticheat", "battleye", "beservice", "epic", "exitlag", "nvidia", "nvcontainer", "nvdisplay", "amd", "radeon", "logi", "lghub", "razer", "steelseries", "corsair", "icue", "audiodg", "realtek", "rtkaudio", "voicemeeter", "antimalware", "msmpeng", "securityhealth", "msascui", "windefend", "sentinel", "crowdstrike", "csfalcon", "vgc", "vgtray"];

    // Discord is a permanent exclusion, including release channels and helpers.
    public static bool IsDiscord(ProcessIdentity p)
    {
        if (p.Name.StartsWith("discord", StringComparison.OrdinalIgnoreCase)) return true;
        string path = p.Path.Replace('/', '\\');
        return path.Split('\\').Any(segment =>
            segment.Equals("Discord", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("DiscordCanary", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("DiscordPTB", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("DiscordDevelopment", StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith("Discord.Discord_", StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith("DiscordInc.Discord_", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDiscordApp(AppCandidate app) => app.Id.StartsWith("discord", StringComparison.OrdinalIgnoreCase)
        || IsDiscord(new ProcessIdentity { Name = System.IO.Path.GetFileName(app.LaunchPath), Path = app.LaunchPath })
        || app.Processes.Any(IsDiscord);

    public static bool IsGamingDependency(ProcessIdentity process) => IsDiscord(process) || ConnectionProtection.IsRoot(process)
        || ProtectedNames.Any(process.Name.ToLowerInvariant().Contains) || HeadroomPolicy.ProtectedPath(process.Path)
        || process.Path.Contains("\\Epic Games\\", StringComparison.OrdinalIgnoreCase)
        || process.Path.Contains("\\ExitLag\\", StringComparison.OrdinalIgnoreCase);

    public static bool Protected(ProcessIdentity p, int session, ISet<int>? ancestors = null)
    {
        if (p.Id <= 4 || p.Id == Environment.ProcessId || p.SessionId != session || p.StartTicks <= 0 || string.IsNullOrWhiteSpace(p.Path) || ancestors?.Contains(p.Id) == true) return true;
        if (IsGamingDependency(p) || p.Name.Equals("Tourney Mode.exe", StringComparison.OrdinalIgnoreCase)) return true;
        string name = p.Name.ToLowerInvariant();
        if (ProtectedNames.Any(name.Contains)) return true;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // Only console host descendants may be closed, never other Windows components.
        if (Under(p.Path, windows) && !name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Path.Contains("\\Epic Games\\", StringComparison.OrdinalIgnoreCase) || p.Path.Contains("\\ExitLag\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool IsFortnite(ProcessIdentity p) => p.Name.Equals("FortniteClient-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
        && p.Path.EndsWith("\\FortniteGame\\Binaries\\Win64\\FortniteClient-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase);

    public static HashSet<int> Ancestors(List<ProcessIdentity> snapshot, int id)
    {
        var map = snapshot.ToDictionary(p => p.Id);
        var result = new HashSet<int> { id };
        while (map.TryGetValue(id, out var p) && p.ParentId != 0 && result.Add(p.ParentId)) id = p.ParentId;
        return result;
    }

    public static List<AppCandidate> Candidates(List<ProcessIdentity> snapshot, string? userProfile = null, string? startup = null, string? desktop = null, Func<string, bool>? exists = null, bool fortniteOnly = false, bool maximumHeadroom = false, IEnumerable<string>? protectedPaths = null)
    {
        exists ??= File.Exists;
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        startup ??= Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        desktop ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        int session = Process.GetCurrentProcess().SessionId;
        var ancestors = Ancestors(snapshot, Environment.ProcessId);
        // Protect Discord's descendants even when a shared helper lives elsewhere.
        var discordIds = HeadroomPolicy.DependencyIds(snapshot);
        ancestors.UnionWith(discordIds);
        ancestors.UnionWith(ConnectionProtection.ExtraIds(snapshot, protectedPaths));
        var eligible = snapshot.Where(p => !Protected(p, session, ancestors)).ToList();
        var groups = new List<AppCandidate>();
        IEnumerable<AppSpec> specs = fortniteOnly || maximumHeadroom ? StandardApps.Concat(FortniteOnlyApps) : StandardApps;
        foreach (var spec in specs)
        {
            foreach (var group in eligible.Where(p => p.Name.Equals(spec.Exe, StringComparison.OrdinalIgnoreCase)
                && InstalledAppPath(p.Path, userProfile, spec.InstallationMarkers)).GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (!exists(group.Key)) continue;
                if (snapshot.Any(p => p.Path.Equals(group.Key, StringComparison.OrdinalIgnoreCase) && Protected(p, session, ancestors))) continue;
                var app = new AppCandidate { Id = spec.Id + "|" + group.Key.ToLowerInvariant(), Name = spec.Name, LaunchPath = group.Key,
                    DefaultSelected = spec.Selected, Detail = "Graceful close, then force close if selected. Stays closed when mode is turned off; save work first.", Processes = group.ToList() };
                ExpandSafeChildren(app, snapshot, session, ancestors, fortniteOnly || maximumHeadroom);
                groups.Add(app);
            }
        }
        // Codex/Native/bridge are connections, not cleanup candidates. There is
        // no dependency on a particular user's desktop shortcut or workspace.
        if (maximumHeadroom) HeadroomPolicy.AddCandidates(groups, snapshot, ancestors, exists);
        var claimed = new HashSet<int>();
        foreach (var app in groups.OrderBy(a => a.Id == "codex-bridge" ? 0 : 1))
            app.Processes.RemoveAll(p => !claimed.Add(p.Id));
        return groups.Where(a => a.Processes.Count > 0 && !ConnectionProtection.IsApp(a)).OrderBy(a => a.Name).ToList();
    }

    private static bool InstalledAppPath(string path, string userProfile, string[]? markers)
    {
        if (markers is null) return true; // Original standard profile compatibility.
        bool installedLocation = Under(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || Under(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
            || Under(path, System.IO.Path.Combine(userProfile, "AppData", "Local"))
            || Under(path, System.IO.Path.Combine(userProfile, "AppData", "Roaming"))
            || (markers.Contains("\\Riot Games\\Riot Client\\", StringComparer.OrdinalIgnoreCase)
                && Under(path, System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "", "Riot Games", "Riot Client")));
        return installedLocation && markers.Any(marker => path.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Only direct, known app executables can be watched by exact recorded path.
    /// Native and the bridge must use their staged stop/start lifecycle instead.</summary>
    public static bool IsMonitoredApp(AppCandidate app)
    {
        if (IsDiscordApp(app) || ConnectionProtection.IsApp(app) || string.IsNullOrWhiteSpace(app.LaunchPath) || !string.IsNullOrWhiteSpace(app.Arguments)
            || app.AdditionalLaunchPaths.Count > 0) return false;
        if (HeadroomPolicy.IsDedicatedApp(app)) return true;
        return StandardApps.Concat(FortniteOnlyApps).Any(spec =>
            app.Id.Equals(spec.Id + "|" + app.LaunchPath, StringComparison.OrdinalIgnoreCase)
            && System.IO.Path.GetFileName(app.LaunchPath).Equals(spec.Exe, StringComparison.OrdinalIgnoreCase)
            && InstalledAppPath(app.LaunchPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), spec.InstallationMarkers));
    }

    public static bool SameApp(AppCandidate left, AppCandidate right) =>
        left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase)
        && left.LaunchPath.Equals(right.LaunchPath, StringComparison.OrdinalIgnoreCase)
        && left.Arguments.Equals(right.Arguments, StringComparison.Ordinal);

    private static bool KnownInstalledChild(AppCandidate app, ProcessIdentity child)
    {
        string directory = System.IO.Path.GetDirectoryName(app.LaunchPath) ?? "";
        if (string.IsNullOrWhiteSpace(directory)) return false;
        string[] helpers = ["steamwebhelper.exe", "QtWebEngineProcess.exe", "crashpad_handler.exe", "crashreporter.exe",
            "CefSharp.BrowserSubprocess.exe", "CefSharp.BrowserSubprocess.Core.exe", "obs-browser-page.exe", "OverwolfBrowser.exe",
            "GalaxyClient Helper.exe", "RiotClientUx.exe", "RiotClientUxRender.exe", "MedalEncoder.exe"];
        if (helpers.Contains(child.Name, StringComparer.OrdinalIgnoreCase) && Under(child.Path, directory)) return true;
        // Official Codex app tools are recognized only as descendants of that
        // GUI group, never by a global codex/node/python process-name scan.
        if (app.Id.StartsWith("codex-desktop|", StringComparison.OrdinalIgnoreCase)
            && new[] { "codex.exe", "codex-code-mode-host.exe", "node.exe", "node_repl.exe", "codex-computer-use-swift.exe" }.Contains(child.Name, StringComparer.OrdinalIgnoreCase)
            && Under(child.Path, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex"))) return true;
        // WebView2 is a shared runtime: only descendants of a selected known app,
        // at its known installation path, are eligible. Never target it globally.
        return child.Name.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)
            && (Under(child.Path, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"))
                || Under(child.Path, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "EdgeWebView", "Application")));
    }

    private static void ExpandSafeChildren(AppCandidate app, List<ProcessIdentity> snapshot, int session, HashSet<int> ancestors, bool fortniteOnly = false)
    {
        var ids = app.Processes.Select(p => p.Id).ToHashSet();
        bool added;
        do
        {
            added = false;
            foreach (var p in snapshot)
            {
                if (ids.Contains(p.Id) || !ids.Contains(p.ParentId) || Protected(p, session, ancestors)) continue;
                // Recognized app descendants only. No unknown command shells, arbitrary user scripts, services or games.
                bool known = app.Processes.Any(root => p.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase))
                    || p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase);
                if (fortniteOnly) known |= KnownInstalledChild(app, p);
                else known |= p.Name.Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase)
                        || Under(p.Path, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spencer-native", "app", "runtime"))
                        || Under(p.Path, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex"));
                if (known) { ids.Add(p.Id); app.Processes.Add(p); added = true; }
            }
        } while (added);
    }
}
