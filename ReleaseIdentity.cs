using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TourneyMode;

internal static class ReleaseIdentity
{
    internal static string Version => typeof(ReleaseIdentity).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    internal static string Title => "Tourney Mode v" + Version;
    private const string ControllerProperty = "Spencer.TourneyMode.MainController.v1";
    private delegate bool WindowVisitor(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowVisitor visitor, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    internal static void MarkController(IntPtr window)
    {
        if (!SetProp(window, ControllerProperty, new IntPtr(1)))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not register the recovery window.");
    }

    internal static IntPtr FindController()
    {
        // Older releases have this exact title. New releases identify only their
        // real controller window; hidden bitmap previews never receive the marker.
        var legacy = FindWindow(null, "Tourney Mode");
        if (legacy != IntPtr.Zero) return legacy;
        IntPtr found = IntPtr.Zero;
        int session = Process.GetCurrentProcess().SessionId;
        EnumWindows((window, _) =>
        {
            if (GetProp(window, ControllerProperty) != new IntPtr(1)) return true;
            GetWindowThreadProcessId(window, out uint id);
            if (id == 0 || id == Environment.ProcessId) return true;
            try
            {
                using var owner = Process.GetProcessById(checked((int)id));
                if (owner.SessionId != session) return true;
                string path = ProcessCatalog.GetPath(owner.Id);
                var metadata = FileVersionInfo.GetVersionInfo(path);
                if (metadata.ProductName != "Tourney Mode") return true;
                found = window; return false;
            }
            catch { return true; }
        }, IntPtr.Zero);
        return found;
    }
}
