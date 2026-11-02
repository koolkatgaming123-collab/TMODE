using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TourneyMode;

/// <summary>
/// Optional, reversible gaming settings. Power settings are applied only to a new copy
/// of the user's plan. This class never changes BCD, drivers, services, or security.
/// </summary>
public static class PerformanceTuning
{
    private const string Processor = "54533251-82be-4824-96c1-47b60b740d00";
    private static readonly (string Subgroup, string Setting, uint Value, string Description)[] PowerSettings =
    [
        (Processor, "893dee8e-2bef-41e0-89c6-b55d0929964c", 100, "CPU minimum state 100%"),
        (Processor, "bc5038f7-23e0-4960-96da-33abaf5935ec", 100, "CPU maximum state 100%"),
        (Processor, "be337238-0d82-4146-a960-4f3749d470c7", 2, "Aggressive CPU boost"),
        (Processor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", 0, "CPU energy preference: performance"),
        ("501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5", 0, "PCI Express link power saving off"),
        ("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0, "USB selective suspend off")
    ];

    private static readonly (string Subgroup, string Setting, uint Value, string Description)[] FortniteOnlyPowerSettings =
    [
        // Keep logical processors unparked while the temporary AC profile is active.
        // This trades power/heat for fewer parking transitions; it is not an FPS guarantee.
        (Processor, "0cc5b647-c1df-4637-891a-dec35c318583", 100, "Fortnite-only CPU core parking minimum 100%")
    ];

    private static readonly (string Key, string Name, int Value)[] GameSettings =
    [
        (@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
        (@"Software\Microsoft\GameBar", "AllowAutoGameMode", 1),
        (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
        (@"System\GameConfigStore", "GameDVR_Enabled", 0)
    ];

    /// <summary>
    /// Caller must journal newGuid before invoking this method. The new plan is not
    /// activated here. Unsupported individual options are returned as warnings.
    /// </summary>
    public static List<string> CreatePlan(string originalGuid, string newGuid, bool fortniteOnly = false)
        => CreatePlanCore(originalGuid, newGuid, fortniteOnly ? PowerSettings.Concat(FortniteOnlyPowerSettings) : PowerSettings, fortniteOnly ? "Tourney Mode - legacy Fortnite only" : "Tourney Mode");

    public static List<string> CreateAdaptivePlan(string originalGuid, string newGuid, PowerPreference preference)
    {
        if (preference is not (PowerPreference.Sustained or PowerPreference.Maximum)) throw new InvalidOperationException("This power profile preserves the original plan; no clone is needed.");
        // Sustained retains OEM minimum state, EPP, boost and parking policy.
        // Maximum is opt-in; neither adaptive profile forces all-core unparking.
        var settings = preference == PowerPreference.Maximum ? PowerSettings : PowerSettings.Where(s => s.Setting == "bc5038f7-23e0-4960-96da-33abaf5935ec");
        return CreatePlanCore(originalGuid, newGuid, settings, "Tourney Mode - " + preference);
    }

    private static List<string> CreatePlanCore(string originalGuid, string newGuid,
        IEnumerable<(string Subgroup, string Setting, uint Value, string Description)> settings, string name)
    {
        string original = ValidateGuid(originalGuid);
        string created = ValidateGuid(newGuid);
        if (string.Equals(original, created, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The temporary power plan must differ from the original plan.");
        var plans = ListPlans();
        if (!plans.Contains(original))
            throw new InvalidOperationException("The original power plan no longer exists.");
        if (plans.Contains(created))
            throw new InvalidOperationException("The proposed temporary power-plan GUID already exists; refusing to modify it.");

        RunPowerCfg("/duplicatescheme", original, created);
        if (!ListPlans().Contains(created))
            throw new InvalidOperationException("Windows did not create the temporary power plan.");
        RunPowerCfg("/changename", created, name, "Temporary copy; original power and vendor scheduling policies are preserved for restoration.");

        var warnings = new List<string>();
        foreach (var setting in settings)
        {
            try
            {
                RunPowerCfg("/setacvalueindex", created, setting.Subgroup, setting.Setting,
                    setting.Value.ToString(CultureInfo.InvariantCulture));
                // /qh includes hidden boost/EPP options that /query omits on Windows 11.
                string query = RunPowerCfg("/qh", created, setting.Subgroup, setting.Setting);
                if (ReadAcIndex(query) != setting.Value)
                    throw new InvalidOperationException("Windows did not retain the requested AC value.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                warnings.Add($"{setting.Description}: unavailable or unverified ({ex.Message})");
            }
        }
        return warnings;
    }

    /// <summary>Caller restores the original plan first. Never deletes an active plan.</summary>
    public static void DeletePlan(string guid)
    {
        string target = ValidateGuid(guid);
        if (!ListPlans().Contains(target)) return;
        string active = ValidateGuid(WindowsSettings.GetPowerScheme());
        if (string.Equals(active, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore the original power plan before removing the Tourney Mode plan.");
        RunPowerCfg("/delete", target);
        if (ListPlans().Contains(target))
            throw new InvalidOperationException("Windows did not remove the temporary power plan.");
    }

    public static List<NotificationBackup> CaptureGameSettings()
    {
        var result = new List<NotificationBackup>();
        foreach (var (path, name, _) in GameSettings)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            bool exists = key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
            if (exists && key!.GetValueKind(name) != RegistryValueKind.DWord)
                throw new InvalidOperationException($"Cannot preserve non-DWORD gaming setting: {name}.");
            result.Add(new NotificationBackup
            {
                Key = path, Name = name, Existed = exists,
                Value = exists ? (int)key!.GetValue(name)! : 0
            });
        }
        return result;
    }

    public static void ApplyGameSettings(bool preserveCapture = false)
    {
        foreach (var (path, name, value) in GameSettings)
        {
            if (preserveCapture && (name == "AppCaptureEnabled" || name == "GameDVR_Enabled")) continue;
            using var key = Registry.CurrentUser.CreateSubKey(path, true);
            key.SetValue(name, value, RegistryValueKind.DWord);
            key.Flush();
        }
        BroadcastSettings();
    }

    public static void ApplyGameSettingsSubset(IEnumerable<NotificationBackup> originals)
    {
        var selected = originals.ToList();
        if (selected.Any(item => !GameSettings.Any(setting => setting.Key == item.Key && setting.Name == item.Name)))
            throw new InvalidOperationException("Unrecognized Windows gaming fix.");
        foreach (var (path, name, value) in GameSettings.Where(setting => selected.Any(item => item.Key == setting.Key && item.Name == setting.Name)))
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, true);
            key.SetValue(name, value, RegistryValueKind.DWord); key.Flush();
            if (key.GetValue(name) is not int actual || actual != value) throw new InvalidOperationException("Windows did not verify the selected gaming fix.");
        }
        if (selected.Count > 0) BroadcastSettings();
    }

    public static void RestoreGameSetting(NotificationBackup item)
    {
        if (!GameSettings.Any(x => x.Key == item.Key && x.Name == item.Name))
            throw new InvalidOperationException("Unrecognized gaming recovery setting.");
        using var key = Registry.CurrentUser.CreateSubKey(item.Key, true);
        if (item.Existed) key.SetValue(item.Name, item.Value, RegistryValueKind.DWord);
        else key.DeleteValue(item.Name, false);
        key.Flush();
        BroadcastSettings();
    }

    private static string ValidateGuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var guid))
            throw new ArgumentException("Invalid power-plan GUID.", nameof(value));
        return guid.ToString("D");
    }

    private static HashSet<string> ListPlans()
    {
        string output = RunPowerCfg("/list");
        return Regex.Matches(output, @"\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b")
            .Cast<Match>().Select(m => m.Value.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // The final two hexadecimal values in a setting-specific powercfg query are
    // the current AC and DC indices, independent of the Windows display language.
    internal static uint ReadAcIndex(string query)
    {
        var values = Regex.Matches(query, @"\b0x([0-9a-fA-F]+)\b");
        if (values.Count < 2)
            throw new InvalidOperationException("Windows did not expose AC/DC values for this power setting.");
        return uint.Parse(values[^2].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string RunPowerCfg(params string[] arguments)
    {
        var info = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
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
        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"powercfg failed ({process.ExitCode}): {(error.Length > 0 ? error : output.Trim())}");
        return output;
    }

    private static void BroadcastSettings() => SendMessageTimeout(
        new IntPtr(0xffff), 0x001A, IntPtr.Zero, "UserPreferences", 0x0002, 1000, out _);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
