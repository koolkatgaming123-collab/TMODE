using System.Text;
using System.Text.Json;

namespace TourneyMode;

public sealed class EthernetPropertyBackup
{
    public string Keyword { get; set; } = "";
    public string[] OriginalValues { get; set; } = [];
    public string[] AppliedValues { get; set; } = [];
}

public sealed class EthernetAdapterBackup
{
    public string InterfaceGuid { get; set; } = "";
    public string InterfaceName { get; set; } = "";
    public List<EthernetPropertyBackup> Properties { get; set; } = [];
    public bool ApplyAttempted { get; set; }
    public bool Restored { get; set; }
}

/// <summary>Reversible driver settings on the current physical Ethernet route.</summary>
public static class EthernetTuning
{
    private static readonly string[] Keywords = ["*EEE", "*InterruptModeration", "*Rss"];
    // Shared with the QoS eligibility check. The policy itself is app-wide;
    // this only establishes whether the current preferred defaults are wired.
    internal static string DiscoveryScript => Discovery;

    public static bool IsAllowedKeyword(string keyword) =>
        Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase);

    internal static void ValidateBackup(EthernetAdapterBackup item)
    {
        if (item == null || !Guid.TryParse(item.InterfaceGuid, out _))
            throw new InvalidDataException("The saved Ethernet interface identity is invalid.");
        if (item.Properties == null || item.Properties.Count > Keywords.Length
            || item.Properties.Select(p => p.Keyword).Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Properties.Count)
            throw new InvalidDataException("The saved Ethernet property list is invalid.");
        foreach (var property in item.Properties)
        {
            string desired = property.Keyword.Equals("*Rss", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
            if (!IsAllowedKeyword(property.Keyword)
                || property.OriginalValues == null || property.OriginalValues.Length != 1
                || property.OriginalValues[0] is not ("0" or "1")
                || property.AppliedValues == null || property.AppliedValues.Length != 1
                || property.AppliedValues[0] != desired)
                throw new InvalidDataException("An unsupported Ethernet property was found in the saved session.");
        }
    }

    public static EthernetAdapterBackup? Capture()
    {
        string output = TournamentResources.RunPowerShell(Discovery + "\n" + """
$adapter = Get-CurrentWiredAdapter
if ($null -eq $adapter) { 'null'; exit 0 }
$properties = @(Get-ExactProperties $adapter)
$changes = @()
foreach ($keyword in @('*EEE', '*InterruptModeration', '*Rss')) {
    $found = @($properties | Where-Object { $_.RegistryKeyword -ieq $keyword })
    if ($found.Count -ne 1) { continue }
    $property = $found[0]
    # Only exposed driver options with an advertised binary target are eligible.
    if ([string]::IsNullOrWhiteSpace([string]$property.DisplayName)) { continue }
    $desired = if ($keyword -ieq '*Rss') { '1' } else { '0' }
    $original = @($property.RegistryValue | ForEach-Object { [string]$_ })
    $valid = @($property.ValidRegistryValues | ForEach-Object { [string]$_ })
    if ($original.Count -ne 1 -or $original[0] -notin @('0','1') -or $valid -notcontains $desired -or $valid -notcontains $original[0]) { continue }
    if ($original[0] -eq $desired) { continue }
    $changes += [pscustomobject]@{
        Keyword = [string]$property.RegistryKeyword
        OriginalValues = @([string]$original[0])
        AppliedValues = @([string]$desired)
    }
}
[pscustomobject]@{
    InterfaceGuid = ([guid]$adapter.InterfaceGuid).ToString('D')
    InterfaceName = [string]$adapter.Name
    Properties = @($changes)
    ApplyAttempted = $false
    Restored = $false
} | ConvertTo-Json -Depth 5 -Compress
""", 15000);
        var result = JsonSerializer.Deserialize<EthernetAdapterBackup>(output);
        if (result != null) ValidateBackup(result);
        return result;
    }

    public static void Apply(EthernetAdapterBackup item)
    {
        ValidateBackup(item);
        if (item.Properties.Count == 0) return;
        // The engine must persist ApplyAttempted before entering this method.
        if (!item.ApplyAttempted)
            throw new InvalidOperationException("Ethernet changes require a journaled apply attempt before mutation.");
        TournamentResources.RunPowerShell(BuildMutationScript(item, restoring: false), 30000);
    }

    public static void Restore(EthernetAdapterBackup item)
    {
        ValidateBackup(item);
        if (!item.ApplyAttempted || item.Properties.Count == 0) return;
        TournamentResources.RunPowerShell(BuildMutationScript(item, restoring: true), 30000);
    }

    private static string BuildMutationScript(EthernetAdapterBackup item, bool restoring)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item)));
        return Discovery + "\n$backup = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
            + encoded + "')) | ConvertFrom-Json\n$restoring = " + (restoring ? "$true" : "$false") + "\n" + """
$adapter = Get-ExactWiredAdapter ([guid]$backup.InterfaceGuid)
if ([string]$adapter.Status -eq 'Disabled') { throw 'The saved Ethernet adapter was disabled; it will not be enabled automatically.' }
Assert-OutsideFortnite
if (!$restoring) {
    $current = Get-CurrentWiredAdapter
    if ($null -eq $current -or ([guid]$current.InterfaceGuid) -ne ([guid]$adapter.InterfaceGuid)) {
        throw 'The active default Ethernet route changed. No driver settings were applied.'
    }
}
# Preflight every property before changing any of them.
$pending = @()
$properties = @(Get-ExactProperties $adapter)
foreach ($entry in @($backup.Properties)) {
    $keyword = [string]$entry.Keyword
    if ($keyword -notin @('*EEE','*InterruptModeration','*Rss')) { throw 'Unsupported saved Ethernet property.' }
    $found = @($properties | Where-Object { $_.RegistryKeyword -ieq $keyword })
    if ($found.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$found[0].DisplayName)) {
        throw ('The saved Ethernet driver option is no longer available: ' + $keyword)
    }
    $property = $found[0]
    $currentValues = @($property.RegistryValue | ForEach-Object { [string]$_ })
    $original = @($entry.OriginalValues | ForEach-Object { [string]$_ })
    $applied = @($entry.AppliedValues | ForEach-Object { [string]$_ })
    $valid = @($property.ValidRegistryValues | ForEach-Object { [string]$_ })
    if ($currentValues.Count -ne 1 -or $original.Count -ne 1 -or $applied.Count -ne 1 -or $original[0] -notin @('0','1') -or $applied[0] -notin @('0','1') -or $valid -notcontains $original[0] -or $valid -notcontains $applied[0]) {
        throw ('The Ethernet driver value is no longer supported: ' + $keyword)
    }
    $desired = if ($restoring) { $original[0] } else { $applied[0] }
    if ($currentValues[0] -eq $desired) { continue }
    $expected = if ($restoring) { $applied[0] } else { $original[0] }
    if ($currentValues[0] -ne $expected) {
        throw ('The Ethernet option changed outside Tourney Mode: ' + $keyword)
    }
    $pending += [pscustomobject]@{ Property = $property; Value = [string]$desired; Keyword = $keyword }
}
if ($pending.Count -eq 0 -and !$restoring) { 'No Ethernet driver changes or reconnect were needed.'; exit 0 }
# A recovery retry can see original registry values after an interrupted earlier
# restore wrote them with -NoRestart. A nonempty attempted backup still needs
# one reconnect to make the restored settings effective before completion.
Assert-OutsideFortnite
foreach ($change in $pending) {
    # InputObject is the exact property CIM object belonging to the GUID-checked adapter.
    Set-NetAdapterAdvancedProperty -InputObject $change.Property -RegistryValue @($change.Value) -NoRestart -Confirm:$false -ErrorAction Stop
}
$adapter = Get-ExactWiredAdapter ([guid]$backup.InterfaceGuid)
Assert-OutsideFortnite
Restart-NetAdapter -InputObject $adapter -Confirm:$false -ErrorAction Stop
$wait = [Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Milliseconds 250
    $adapter = Get-ExactWiredAdapter ([guid]$backup.InterfaceGuid)
    if ([string]$adapter.Status -eq 'Up') { break }
} while ($wait.ElapsedMilliseconds -lt 20000)
if ([string]$adapter.Status -ne 'Up') { throw 'Ethernet settings were written, but the link did not return within 20 seconds. Saved recovery remains necessary.' }
$verified = @(Get-ExactProperties $adapter)
foreach ($entry in @($backup.Properties)) {
    $found = @($verified | Where-Object { $_.RegistryKeyword -ieq [string]$entry.Keyword })
    $desired = if ($restoring) { [string]$entry.OriginalValues[0] } else { [string]$entry.AppliedValues[0] }
    if ($found.Count -ne 1 -or @($found[0].RegistryValue).Count -ne 1 -or [string]$found[0].RegistryValue[0] -ne $desired) {
        throw ('Ethernet setting verification failed: ' + [string]$entry.Keyword)
    }
}
'Verified Ethernet driver settings; link is Up.'
""";
    }

    private const string Discovery = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Assert-OutsideFortnite {
    if (@(Get-Process -Name 'FortniteClient-Win64-Shipping' -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'Ethernet driver changes are deferred while the Fortnite client is running. Close the client before applying or restoring a reconnecting NIC experiment.'
    }
}
function Test-WiredAdapter($adapter) {
    return $null -ne $adapter -and $adapter.HardwareInterface -eq $true -and
        ([string]$adapter.MediaType -eq '802.3' -or [string]$adapter.PhysicalMediaType -eq '802.3')
}
function Get-ExactWiredAdapter([guid]$identity) {
    $found = @(Get-NetAdapter -Physical -IncludeHidden -ErrorAction Stop |
        Where-Object { ([guid]$_.InterfaceGuid) -eq $identity })
    if ($found.Count -ne 1 -or !(Test-WiredAdapter $found[0])) {
        throw 'The exact saved physical Ethernet adapter is unavailable. No other adapter will be changed.'
    }
    return $found[0]
}
function Get-ExactProperties($adapter) {
    # Escape wildcard syntax in user-editable adapter aliases, then verify identity.
    $name = [Management.Automation.WildcardPattern]::Escape([string]$adapter.Name)
    return @(Get-NetAdapterAdvancedProperty -Name $name -ErrorAction Stop |
        Where-Object { [string]$_.Name -ceq [string]$adapter.Name })
}
function Get-CurrentWiredAdapter {
    # Do not filter to Ethernet before ranking. A virtual/VPN or Wi-Fi default
    # can be preferred even though a working physical Ethernet link also exists.
    $routes = @(Get-NetRoute -PolicyStore ActiveStore -ErrorAction Stop |
        Where-Object { ([string]$_.DestinationPrefix -eq '0.0.0.0/0' -or [string]$_.DestinationPrefix -eq '::/0') -and [string]$_.State -ne 'Dead' })
    if ($routes.Count -eq 0) { return $null }
    $interfaces = @(Get-NetIPInterface -ErrorAction Stop)
    $adapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop)
    $ranked = @($routes | ForEach-Object {
        $route = $_
        $ip = @($interfaces | Where-Object {
            $_.InterfaceIndex -eq $route.InterfaceIndex -and [string]$_.AddressFamily -eq [string]$route.AddressFamily -and [string]$_.ConnectionState -eq 'Connected'
        })
        # Some connected RAS/VPN interfaces do not appear in Get-NetAdapter.
        # They still participate in route ranking and must not be ignored.
        if ($ip.Count -eq 1) {
            [pscustomobject]@{ Index = [int]$route.InterfaceIndex; Family = [string]$route.AddressFamily; Metric = [long]$route.RouteMetric + [long]$ip[0].InterfaceMetric }
        }
    })
    if ($ranked.Count -eq 0) { return $null }
    $preferred = @()
    foreach ($family in @($ranked | Group-Object Family)) {
        $ordered = @($family.Group | Sort-Object Metric, Index)
        $best = @($ordered | Where-Object { $_.Metric -eq $ordered[0].Metric } | Select-Object -ExpandProperty Index -Unique)
        # Equal-cost routes to different adapters are not a safe basis for a
        # reconnecting experiment. IPv4/IPv6 must agree when both are present.
        if ($best.Count -ne 1) { return $null }
        $preferred += [int]$best[0]
    }
    $selected = @($preferred | Select-Object -Unique)
    if ($selected.Count -ne 1) { return $null }
    $found = @($adapters | Where-Object { $_.ifIndex -eq $selected[0] -and [string]$_.Status -eq 'Up' })
    if ($found.Count -ne 1 -or !(Test-WiredAdapter $found[0])) { return $null }
    return $found[0]
}
""";
}
