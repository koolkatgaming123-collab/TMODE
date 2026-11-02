using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Win32;

namespace TourneyMode;

/// <summary>Reversible, current-user notification settings and explicit power-plan selection.</summary>
public static class WindowsSettings
{
    private static readonly (string Key, string Name)[] NotificationValues =
    [
        (@"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled"),
        (@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "NOC_GLOBAL_SETTING_TOASTS_ENABLED")
    ];
    private static readonly object DndLock = new();
    private const uint KeyUp = 0x0002;
    private const ushort WinKey = 0x5B, NKey = 0x4E, EscapeKey = 0x1B;

    public static List<NotificationBackup> CaptureNotifications()
    {
        var result = new List<NotificationBackup>();
        foreach (var (path, name) in NotificationValues)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            bool exists = key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
            if (exists && key!.GetValueKind(name) != RegistryValueKind.DWord)
                throw new InvalidOperationException($"Cannot preserve non-DWORD notification setting: {name}.");
            result.Add(new NotificationBackup
            {
                Key = path, Name = name, Existed = exists,
                Value = exists ? (int)key!.GetValue(name)! : 0
            });
        }
        return result;
    }

    public static void SuppressNotifications()
    {
        foreach (var (path, name) in NotificationValues)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, true);
            key.SetValue(name, 0, RegistryValueKind.DWord);
            key.Flush();
        }
        BroadcastSettings();
    }

    public static void RestoreNotification(NotificationBackup item)
    {
        if (!NotificationValues.Any(x => x.Key == item.Key && x.Name == item.Name))
            throw new InvalidOperationException("Unrecognized notification recovery setting.");
        using var key = Registry.CurrentUser.CreateSubKey(item.Key, true);
        if (item.Existed) key.SetValue(item.Name, item.Value, RegistryValueKind.DWord);
        else key.DeleteValue(item.Name, false);
        key.Flush();
        BroadcastSettings();
    }

    public static string GetPowerScheme()
    {
        string output = PowerCfg("/getactivescheme");
        var match = Regex.Match(output, @"\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b");
        if (!match.Success) throw new InvalidOperationException("Windows did not return an active power-plan GUID.");
        return match.Value.ToLowerInvariant();
    }

    public static void SetPowerScheme(string guid)
    {
        if (!Guid.TryParseExact(guid, "D", out var parsed))
            throw new ArgumentException("Invalid power-plan GUID.", nameof(guid));
        PowerCfg("/setactive", parsed.ToString("D"));
        if (!string.Equals(GetPowerScheme(), parsed.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Windows did not activate the requested power plan.");
    }

    private static string PowerCfg(params string[] arguments)
    {
        var info = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start powercfg.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(8000))
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException("powercfg did not finish within eight seconds.");
        }
        string error = stderr.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException($"powercfg failed ({process.ExitCode}): {error}");
        return stdout.GetAwaiter().GetResult();
    }

    /// <summary>Reads the exact user/machine policy without changing it.</summary>
    public static bool IsNotificationCenterDisabledByPolicy()
    {
        const string path = @"Software\Policies\Microsoft\Windows\Explorer";
        const string name = "DisableNotificationCenter";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(path);
            if (key?.GetValue(name) is int value && value == 1
                && key.GetValueKind(name) == RegistryValueKind.DWord)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Reads the real Windows 11 DND toggle. Returns null without sending Win+N when
    /// Notification Center is disabled by user or machine policy. Otherwise opens and
    /// closes Notification Center temporarily; null means the toggle is unavailable
    /// or this shell/build/language does not expose a recognized readable state.
    /// Call only in response to the user's mode-control action, never in a monitor loop.
    /// </summary>
    public static bool? GetDoNotDisturb()
    {
        try
        {
            if (IsNotificationCenterDisabledByPolicy()) return null;
            return RunSta(token => WithDndToggle(null, token));
        }
        catch { return null; }
    }

    /// <summary>Sets and verifies the real shell DND state, or throws without claiming success.</summary>
    public static void SetDoNotDisturb(bool enabled)
    {
        if (IsNotificationCenterDisabledByPolicy())
            throw new InvalidOperationException("Windows Do not disturb is unavailable because DisableNotificationCenter policy disables Notification Center. The policy was not changed.");
        bool? actual = RunSta(token => WithDndToggle(enabled, token));
        if (actual != enabled)
            throw new InvalidOperationException("Windows Do not disturb could not be verified. Open Windows notification settings to check it.");
    }

    private static T RunSta<T>(Func<CancellationToken, T> action)
    {
        var cancellation = new CancellationTokenSource();
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                lock (DndLock)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    result = action(cancellation.Token);
                }
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "Tourney Mode DND" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // UIAutomation calls can occasionally stall in the shell. The background thread is
        // deliberately not aborted: aborting between key-down/key-up could leave keys held.
        if (!thread.Join(10000))
        {
            // A stalled read must not later wake up and toggle DND after its caller
            // has started recovery. Any late continuation checks this before mutation.
            cancellation.Cancel();
            throw new TimeoutException("Windows notification controls did not respond.");
        }
        if (failure != null) throw new InvalidOperationException(failure.Message, failure);
        return result;
    }

    private static bool? WithDndToggle(bool? requested, CancellationToken cancellation)
    {
        IntPtr originalForeground = GetForegroundWindow();
        AutomationElement? center = FindCenter();
        bool opened = center == null;
        try
        {
            if (opened)
            {
                cancellation.ThrowIfCancellationRequested();
                PressNotificationCenter();
                var wait = Stopwatch.StartNew();
                while (center == null && wait.ElapsedMilliseconds < 2500)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Thread.Sleep(100);
                    center = FindCenter();
                }
            }
            if (center == null) return null;
            AutomationElement? toggle = FindDndToggle(center);
            if (toggle == null) return null;
            bool? current = ReadToggle(toggle);
            if (!requested.HasValue || current == requested || current == null) return current;
            cancellation.ThrowIfCancellationRequested();
            if (toggle.TryGetCurrentPattern(TogglePattern.Pattern, out object? pattern))
            {
                cancellation.ThrowIfCancellationRequested();
                ((TogglePattern)pattern).Toggle();
            }
            else if (toggle.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            {
                cancellation.ThrowIfCancellationRequested();
                ((InvokePattern)pattern).Invoke();
            }
            else return null;
            var verify = Stopwatch.StartNew();
            while (verify.ElapsedMilliseconds < 2000)
            {
                Thread.Sleep(100);
                toggle = FindDndToggle(center);
                if (toggle != null && ReadToggle(toggle) == requested) return requested;
            }
            return null;
        }
        finally
        {
            // Do not send Escape into another app if Notification Center never appeared.
            if (opened && center != null && IsCenterForeground(center))
            {
                PressKey(EscapeKey);
                if (originalForeground != IntPtr.Zero) SetForegroundWindow(originalForeground);
            }
        }
    }

    private static AutomationElement? FindCenter()
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        foreach (AutomationElement window in windows)
        {
            try
            {
                string name = window.Current.Name;
                if ((name.Equals("Notification Center", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Notification centre", StringComparison.OrdinalIgnoreCase)) &&
                    !window.Current.IsOffscreen)
                    return window;
            }
            catch (ElementNotAvailableException) { }
        }
        return null;
    }

    private static AutomationElement? FindDndToggle(AutomationElement center)
    {
        var controls = center.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)));
        foreach (AutomationElement control in controls)
        {
            string name = control.Current.Name;
            string help = control.Current.HelpText;
            if (!control.Current.IsOffscreen && control.Current.IsEnabled &&
                (name.Contains("do not disturb", StringComparison.OrdinalIgnoreCase) ||
                 help.Contains("do not disturb", StringComparison.OrdinalIgnoreCase))) return control;
        }
        return null;
    }

    private static bool? ReadToggle(AutomationElement toggle)
    {
        if (toggle.TryGetCurrentPattern(TogglePattern.Pattern, out object? pattern))
        {
            return ((TogglePattern)pattern).Current.ToggleState switch
            {
                ToggleState.On => true, ToggleState.Off => false, _ => null
            };
        }
        // Some shell versions expose an invoke button with a state-specific accessible
        // action name. Never infer a state from an unlabeled generic button.
        string text = toggle.Current.Name + " " + toggle.Current.HelpText;
        if (text.Contains("turn off do not disturb", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("turn on do not disturb", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static bool IsCenterForeground(AutomationElement center)
    {
        int handle = center.Current.NativeWindowHandle;
        return handle != 0 && new IntPtr(handle) == GetForegroundWindow();
    }

    private static void PressNotificationCenter() => SendKeys(
        Key(WinKey), Key(NKey), Key(NKey, KeyUp), Key(WinKey, KeyUp));
    private static void PressKey(ushort key) => SendKeys(Key(key), Key(key, KeyUp));
    private static INPUT Key(ushort key, uint flags = 0) => new()
    {
        Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { VirtualKey = key, Flags = flags } }
    };
    private static void SendKeys(params INPUT[] keys)
    {
        if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<INPUT>()) != keys.Length)
            throw new InvalidOperationException("Windows rejected the notification-center key command.");
    }

    private static void BroadcastSettings() => SendMessageTimeout(
        new IntPtr(0xffff), 0x001A, IntPtr.Zero, "UserPreferences", 0x0002, 1000, out _);

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort Low, High;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
