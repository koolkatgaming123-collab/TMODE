using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourneyMode;

public sealed class WindowsProfileStoredValue
{
    public bool Existed { get; init; }
    public RegistryValueKind Kind { get; init; }
    public string Value { get; init; } = "";
}

/// <summary>Small registry/environment seam. Pure fixtures provide all observations without host access.</summary>
public interface IWindowsProfileStore
{
    int WindowsBuild { get; }
    bool EdgeInstalled { get; }
    WindowsProfileStoredValue Read(string hive, string key, string name);
    void Write(string hive, string key, string name, RegistryValueKind kind, string value);
    void Delete(string hive, string key, string name);
}

public sealed class WindowsProfileValue
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Hive { get; set; } = "HKCU";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public RegistryValueKind Kind { get; set; }
    public bool Existed { get; set; }
    public string Original { get; set; } = "";
    public string Applied { get; set; } = "";
    public bool Attempted { get; set; }
    public bool Restored { get; set; }
    public string Note { get; set; } = "";
}
public sealed class WindowsProfileBackup
{
    public List<WindowsProfileValue> Values { get; set; } = [];
    public List<string> Notices { get; set; } = [];
    public bool HasChanges => Values.Count > 0;
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Values))));
}

/// <summary>Finite supported policy/desktop requests. No generic registry command executor.</summary>
public static class WindowsProfile
{
    private sealed record Definition(string Id, string Title, string Hive, string Key, string Name, RegistryValueKind Kind, string Value,
        int MinimumBuild = 19041, int MaximumBuild = int.MaxValue, bool EnterpriseOnly = false, bool Policy = false, bool Edge = false);
    private static readonly Definition[] Catalog =
    [
        new("transparency", "Desktop transparency off", "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", RegistryValueKind.DWord, "0"),
        new("taskbar-animation", "Taskbar animations off", "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", RegistryValueKind.DWord, "0"),
        new("window-animation", "Minimize/maximize animations off", "HKCU", @"Control Panel\Desktop\WindowMetrics", "MinAnimate", RegistryValueKind.String, "0"),
        new("menu-delay", "Desktop menu delay reduced", "HKCU", @"Control Panel\Desktop", "MenuShowDelay", RegistryValueKind.String, "0"),
        new("explorer-promotions", "Explorer provider promotions hidden", "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", RegistryValueKind.DWord, "0"),
        new("edge-background", "Edge background mode off", "HKCU", @"Software\Policies\Microsoft\Edge", "BackgroundModeEnabled", RegistryValueKind.DWord, "0", Policy: true, Edge: true),
        new("edge-startup", "Edge startup boost off", "HKCU", @"Software\Policies\Microsoft\Edge", "StartupBoostEnabled", RegistryValueKind.DWord, "0", Policy: true, Edge: true),
        new("delivery-peers", "Windows Update peer sharing off", "HKLM", @"Software\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", RegistryValueKind.DWord, "0", Policy: true),
        new("widgets", "Windows Widgets policy off", "HKLM", @"Software\Policies\Microsoft\Dsh", "AllowNewsAndInterests", RegistryValueKind.DWord, "0", MinimumBuild: 22000, Policy: true),
        new("consumer-features", "Windows consumer suggestions off", "HKLM", @"Software\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", RegistryValueKind.DWord, "1", EnterpriseOnly: true, Policy: true),
        new("tailored-experiences", "Personalized diagnostic suggestions off", "HKCU", @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", RegistryValueKind.DWord, "1", Policy: true),
        new("spotlight", "Windows Spotlight experiences off", "HKCU", @"Software\Policies\Microsoft\Windows\CloudContent", "DisableWindowsSpotlightFeatures", RegistryValueKind.DWord, "1", EnterpriseOnly: true, Policy: true)
    ];
    public static WindowsProfileBackup Capture(PcFacts facts) => Capture(facts, new WindowsRegistryProfileStore());
    public static WindowsProfileBackup Capture(PcFacts facts, IWindowsProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(facts); ArgumentNullException.ThrowIfNull(store);
        var result = new WindowsProfileBackup();
        int build = store.WindowsBuild;
        bool enterprise = new[] { "Enterprise", "EnterpriseN", "EnterpriseS", "EnterpriseSN", "Education", "EducationN" }.Contains(facts.WindowsEdition, StringComparer.OrdinalIgnoreCase);
        bool policyEdition = enterprise || new[] { "Professional", "ProfessionalN", "ProfessionalEducation", "ProfessionalEducationN", "ProfessionalWorkstation", "ProfessionalWorkstationN" }
            .Contains(facts.WindowsEdition, StringComparer.OrdinalIgnoreCase);
        foreach (var item in Catalog)
        {
            if (build < item.MinimumBuild || build > item.MaximumBuild || item.EnterpriseOnly && !enterprise || item.Edge && !store.EdgeInstalled) continue;
            if (item.Policy && (facts.DomainJoined != false || !policyEdition && !item.Edge)) { result.Notices.Add(item.Title + ": policy left unchanged because edition/management eligibility is unconfirmed."); continue; }
            var current = store.Read(item.Hive, item.Key, item.Name);
            if (current.Existed && current.Kind != item.Kind) { result.Notices.Add(item.Title + ": existing value type is unsupported; preserved."); continue; }
            if (current.Existed && current.Value == item.Value) continue;
            // Existing policy values may be enforced by management. Never override them.
            if (item.Policy && (current.Existed || store.Read(OtherHive(item.Hive), item.Key, item.Name).Existed))
            { result.Notices.Add(item.Title + ": existing policy is preserved; review its administrator/source."); continue; }
            result.Values.Add(new() { Id = item.Id, Title = item.Title, Hive = item.Hive, Key = item.Key, Name = item.Name, Kind = item.Kind,
                Existed = current.Existed, Original = current.Existed ? current.Value : "", Applied = item.Value,
                Note = item.Id == "delivery-peers" ? "Delivery Optimization HTTP-only request: peer-to-peer sharing is disabled, while Windows Update and Store downloads remain enabled. Origin bandwidth may increase. No network adapter or service is disabled."
                    : item.Id == "edge-background" ? "Edge background apps/extensions and related notifications may stop after browser windows close. Restart Edge to load the policy; current browser processes are not terminated. No in-game FPS gain is established."
                    : item.Id == "edge-startup" ? "Edge prelaunch/startup boost is disabled; the browser may take longer to open. A browser restart may be needed. Current browser processes are not terminated. No in-game FPS gain is established."
                    : item.Policy ? "Policy request; Windows edition/app support and a sign-out/browser restart may be required. Updates remain enabled. This is not a measured in-game FPS improvement."
                    : "Desktop preference; sign-out may be needed for all shell effects. This is not a measured in-game FPS improvement." });
        }
        return result;
    }
    public static void Apply(WindowsProfileBackup backup, Action persist, Func<bool> gameAbsent, CancellationToken token)
        => Apply(backup, persist, gameAbsent, token, new WindowsRegistryProfileStore());
    public static void Apply(WindowsProfileBackup backup, Action persist, Func<bool> gameAbsent, CancellationToken token, IWindowsProfileStore store)
    {
        ValidateBackup(backup); ArgumentNullException.ThrowIfNull(persist); ArgumentNullException.ThrowIfNull(gameAbsent); ArgumentNullException.ThrowIfNull(store);
        if (backup.Values.Any(item => item.Attempted)) throw new InvalidOperationException("A Windows profile application was already attempted. Restore its recorded changes before trying again.");
        // Validate every selected original before making the first registry write.
        foreach (var item in backup.Values)
        {
            RequireSafe(gameAbsent, token); CheckCurrent(store.Read(item.Hive, item.Key, item.Name), item, item.Existed, item.Original);
            if (DefinitionFor(item).Policy && store.Read(OtherHive(item.Hive), item.Key, item.Name).Existed)
                throw new InvalidOperationException(item.Title + ": another policy appeared after review; no policy override is permitted.");
        }
        foreach (var item in backup.Values)
        {
            RequireSafe(gameAbsent, token);
            CheckCurrent(store.Read(item.Hive, item.Key, item.Name), item, item.Existed, item.Original);
            item.Attempted = true; item.Restored = false; persist();
            RequireSafe(gameAbsent, token); CheckCurrent(store.Read(item.Hive, item.Key, item.Name), item, item.Existed, item.Original);
            if (DefinitionFor(item).Policy && store.Read(OtherHive(item.Hive), item.Key, item.Name).Existed)
                throw new InvalidOperationException(item.Title + ": another policy appeared before application; original remains recorded.");
            RequireSafe(gameAbsent, token);
            store.Write(item.Hive, item.Key, item.Name, item.Kind, item.Applied);
            CheckCurrent(store.Read(item.Hive, item.Key, item.Name), item, true, item.Applied);
        }
    }
    public static void Restore(WindowsProfileBackup backup, Action persist, Func<bool> gameAbsent)
        => Restore(backup, persist, gameAbsent, new WindowsRegistryProfileStore());
    public static void Restore(WindowsProfileBackup backup, Action persist, Func<bool> gameAbsent, IWindowsProfileStore store, CancellationToken token = default)
    {
        ValidateBackup(backup); ArgumentNullException.ThrowIfNull(persist); ArgumentNullException.ThrowIfNull(gameAbsent); ArgumentNullException.ThrowIfNull(store);
        var errors = new List<Exception>();
        foreach (var item in backup.Values.Where(v => v.Attempted && !v.Restored))
        {
            try
            {
                RequireSafe(gameAbsent, token); var current = store.Read(item.Hive, item.Key, item.Name);
                if (Matches(current, item, item.Existed, item.Original)) { item.Restored = true; persist(); continue; }
                CheckCurrent(current, item, true, item.Applied); // External edits are never overwritten blindly.
                persist(); RequireSafe(gameAbsent, token);
                CheckCurrent(store.Read(item.Hive, item.Key, item.Name), item, true, item.Applied);
                RequireSafe(gameAbsent, token);
                if (item.Existed) store.Write(item.Hive, item.Key, item.Name, item.Kind, item.Original); else store.Delete(item.Hive, item.Key, item.Name);
                CheckCurrent(store.Read(item.Hive, item.Key, item.Name), item, item.Existed, item.Original); item.Restored = true; persist();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException("Some Windows profile values still need recovery; originals remain recorded.", errors);
    }
    private static string OtherHive(string hive) => hive == "HKCU" ? "HKLM" : "HKCU";
    private static void RequireSafe(Func<bool> gameAbsent, CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (!gameAbsent()) throw new InvalidOperationException("Windows preferences and policy changes wait until Fortnite is definitely absent."); }
    private static Definition DefinitionFor(WindowsProfileValue value)
    {
        var definition = Catalog.SingleOrDefault(d => d.Id == value.Id && d.Hive == value.Hive && d.Key == value.Key && d.Name == value.Name && d.Kind == value.Kind && d.Value == value.Applied);
        if (definition == null)
            throw new InvalidDataException("Unknown Windows profile value; keep the recovery journal for review.");
        return definition;
    }
    private static void ValidateBackup(WindowsProfileBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (backup.Values == null || backup.Values.Count > Catalog.Length || backup.Values.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != backup.Values.Count)
            throw new InvalidDataException("Duplicate or invalid Windows profile selections.");
        foreach (var item in backup.Values)
        {
            var definition = DefinitionFor(item);
            if (definition.Policy && item.Existed) throw new InvalidDataException("An existing managed policy cannot be overridden by this profile.");
            if (item.Original == null || !item.Existed && item.Original.Length != 0 || item.Existed && item.Kind == RegistryValueKind.DWord
                && !int.TryParse(item.Original, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException("The Windows profile original value is invalid; keep the recovery journal.");
        }
    }
    private static bool Matches(WindowsProfileStoredValue current, WindowsProfileValue value, bool expectedExists, string expected)
        => current.Existed == expectedExists && (!current.Existed || current.Kind == value.Kind && current.Value == expected);
    private static void CheckCurrent(WindowsProfileStoredValue current, WindowsProfileValue value, bool expectedExists, string expected)
    {
        if (!Matches(current, value, expectedExists, expected))
            throw new InvalidOperationException(value.Title + ": value changed externally or readback failed; original remains recorded.");
    }
}

/// <summary>The production store never creates keys during capture/read; all access uses the 64-bit registry view.</summary>
internal sealed class WindowsRegistryProfileStore : IWindowsProfileStore
{
    public int WindowsBuild => Environment.OSVersion.Version.Build;
    public bool EdgeInstalled => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"))
        || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"));
    private static RegistryKey Root(string hive) => RegistryKey.OpenBaseKey(hive switch
    { "HKCU" => RegistryHive.CurrentUser, "HKLM" => RegistryHive.LocalMachine, _ => throw new InvalidDataException("Unknown Windows profile registry hive.") }, RegistryView.Registry64);
    public WindowsProfileStoredValue Read(string hive, string path, string name)
    {
        using var root = Root(hive); using var key = root.OpenSubKey(path, false);
        if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new();
        var kind = key.GetValueKind(name);
        return new() { Existed = true, Kind = kind, Value = kind is RegistryValueKind.DWord or RegistryValueKind.String
            ? Convert.ToString(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), System.Globalization.CultureInfo.InvariantCulture) ?? "" : "" };
    }
    public void Write(string hive, string path, string name, RegistryValueKind kind, string value)
    {
        if (kind is not RegistryValueKind.DWord and not RegistryValueKind.String) throw new InvalidDataException("Unsupported Windows profile value kind.");
        using var root = Root(hive); using var key = root.CreateSubKey(path, true) ?? throw new UnauthorizedAccessException("The Windows profile key could not be opened for writing.");
        key.SetValue(name, kind == RegistryValueKind.DWord ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : value, kind); key.Flush();
    }
    public void Delete(string hive, string path, string name)
    {
        using var root = Root(hive); using var key = root.OpenSubKey(path, true);
        if (key == null) return;
        key.DeleteValue(name, false); key.Flush();
    }
}
