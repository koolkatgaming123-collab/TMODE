using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Uses the installed Native control entry points, never replayed process command lines.</summary>
public static class NativeLifecycle
{
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string NativeBase = Path.Combine(UserProfile, ".spencer-native");
    private static readonly string NativeApp = Path.Combine(NativeBase, "app");
    private static readonly string NativeRuntime = Path.Combine(NativeApp, "runtime");
    private static readonly string NativeHome = Path.Combine(NativeBase, "state");
    private static readonly string NativeNode = Path.Combine(NativeRuntime, "node.exe");
    private static readonly string NativeCli = Path.Combine(NativeRuntime, "cli.cjs");
    private static readonly string BridgeInstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexLanBridge", "App");
    // Recovery never relaunches apps. Kept empty for older helper-scope checks;
    // do not ship a path from one user's dated workspace.
    public static string PhoneRestoreLauncher => "";

    public static Task StopAsync() => RunControlAsync("stop", TimeSpan.FromSeconds(45));
    public static Task StartAsync() => RunControlAsync("start", TimeSpan.FromSeconds(60));

    private static async Task RunControlAsync(string verb, TimeSpan timeout)
    {
        if (!File.Exists(NativeNode) || !File.Exists(NativeCli))
            throw new InvalidOperationException("Installed Spencer Native control files are missing; no background processes were force-stopped.");

        var info = new ProcessStartInfo(NativeNode)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = NativeApp
        };
        info.ArgumentList.Add(NativeCli);
        info.ArgumentList.Add(verb);
        // A foreground Native caller can inherit SUPERVISED=1. The public CLI path must
        // manage the scheduled supervisor and clear its pause markers when restarting.
        info.Environment["SPENCER_NATIVE_BASE"] = NativeBase;
        info.Environment["SPENCER_NATIVE_HOME"] = NativeHome;
        info.Environment.Remove("SPENCER_NATIVE_SUPERVISED");

        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Spencer Native control could not start.");
        // Consume both streams concurrently to avoid pipe deadlocks. Their contents may
        // include private local configuration, so they are deliberately never surfaced.
        Task stdout = DrainAsync(process.StandardOutput);
        Task stderr = DrainAsync(process.StandardError);
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Only the helper created here is ours to stop; never kill its service tree.
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { }
            throw new TimeoutException($"Spencer Native {verb} timed out. Its state is uncertain; recovery remains available.");
        }
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(verb == "stop"
                ? "Spencer Native refused to stop or could not drain safely. Finish active Native work and retry; no Native runtime will be force-closed."
                : "Spencer Native could not confirm that its services restarted. Recovery remains pending; inspect the Native service logs.");
        // cli.cjs start performs the installed service readiness probes before exit 0.
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        char[] buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Read-only provider probe. StartAsync's CLI result also covers worker-only installs.</summary>
    public static bool IsServiceReady()
    {
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(NativeHome, "config.json")));
            int port = config.RootElement.GetProperty("port").GetInt32();
            if (port < 1 || port > 65535) return false;
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1200) };
            using var response = client.GetAsync($"http://127.0.0.1:{port}/healthz").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return false;
            using var health = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return health.RootElement.TryGetProperty("service", out var service)
                && service.GetString() == "spencer-native"
                && (!health.RootElement.TryGetProperty("accepting_turns", out var accepting) || accepting.ValueKind == JsonValueKind.True);
        }
        catch { return false; }
    }

    public static bool IsNativeSupervisor(int pid)
        => SamePath(ProcessCatalog.GetPath(pid), NativeNode)
            && ScriptArguments(pid).Any(arg => SamePath(arg, Path.Combine(NativeRuntime, "supervisor.cjs")));

    public static bool IsBridgeRuntime(int pid)
    {
        if (!SamePath(ProcessCatalog.GetPath(pid), NativeNode)) return false;
        return ScriptArguments(pid).Any(arg => IsAbsoluteFile(arg)
            && ProcessPolicy.Under(arg, BridgeInstallRoot)
            && Path.GetFileName(arg).Equals("lan-bridge.cjs", StringComparison.OrdinalIgnoreCase)
            && File.Exists(arg));
    }

    public static bool IsKnownPhoneBridge(int pid)
    {
        string programFilesNode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        string image = ProcessCatalog.GetPath(pid);
        if (!SamePath(image, programFilesNode) && !SamePath(image, NativeNode)) return false;
        return ScriptArguments(pid).Any(arg => IsAbsoluteFile(arg)
            && Path.GetFileName(arg).Equals("phone-bridge.cjs", StringComparison.OrdinalIgnoreCase) && File.Exists(arg));
    }

    private static bool IsAbsoluteFile(string path)
    {
        try { return Path.IsPathFullyQualified(path) && !path.StartsWith("\\\\", StringComparison.Ordinal); }
        catch { return false; }
    }

    private static bool SamePath(string left, string right)
    {
        if (!IsAbsoluteFile(left) || !IsAbsoluteFile(right)) return false;
        try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    internal static string[] ScriptArguments(int pid)
    {
        string line = ReadCommandLine(pid);
        if (line.Length == 0) return [];
        IntPtr argv = CommandLineToArgvW(line, out int count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            if (count < 2 || count > 4096) return [];
            var args = new string[count - 1];
            for (int i = 1; i < count; i++) args[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally { LocalFree(argv); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    private static string ReadCommandLine(int pid)
    {
        IntPtr process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return "";
        IntPtr buffer = IntPtr.Zero;
        try
        {
            // ProcessCommandLineInformation returns a UNICODE_STRING inside a local buffer.
            NtQueryInformationProcess(process, 60, IntPtr.Zero, 0, out int needed);
            if (needed < Marshal.SizeOf<UnicodeString>() || needed > 1024 * 1024) return "";
            buffer = Marshal.AllocHGlobal(needed);
            if (NtQueryInformationProcess(process, 60, buffer, needed, out _) < 0) return "";
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            long offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (value.Buffer == IntPtr.Zero || value.Length % 2 != 0 || offset < 0 || offset + value.Length > needed) return "";
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? "";
        }
        catch { return ""; }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
}
