using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TourneyMode;

public sealed class ServiceBackup
{
    public string Name { get; set; } = "";
    public bool WasRunning { get; set; }
    public bool StopAttempted { get; set; }
    public bool Restored { get; set; }
    public int? OriginalStartType { get; set; }
    public int? OriginalDelayedAutoStart { get; set; }
    public bool HoldDisabled { get; set; }
    public bool ConfigurationAttempted { get; set; }
}

public sealed class NetworkPriorityBackup
{
    public string PolicyName { get; set; } = "";
    // Eligibility at capture time only. DSCP policy matching remains app-wide,
    // rather than being bound to this adapter. Empty GUIDs in old journals
    // remain supported for removing their owned policies during recovery.
    public string InterfaceGuid { get; set; } = "";
    public string InterfaceName { get; set; } = "";
    public bool ApplyAttempted { get; set; }
    public bool Restored { get; set; }
}

/// <summary>
/// Explicit optional resources only. The engine journals backups before invoking
/// these methods. No persistent QoS store or global TCP setting changes.
/// </summary>
public static class TournamentResources
{
    private static readonly string[] ServiceNames =
    [
        "WSearch", "Fax", "MapsBroker", "RetailDemo", "PhoneSvc", "TrkWks",
        "XblAuthManager", "XblGameSave", "XboxNetApiSvc",
        "edgeupdate", "edgeupdatem", "gupdate", "gupdatem", "AdobeARMservice"
    ];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] HeadroomServiceNames = ["DiagTrack", "dmwappushservice", "SSDPSRV", "upnphost",
        "WMPNetworkSvc", "WerSvc", "wisvc", "CDPSvc", "MicrosoftEdgeElevationService",
        "MicrosoftCopilotElevationService", "GoogleChromeElevationService", "logi_lamparray_service",
        // Reviewed feature services only: inventory, MIDI, storage management and
        // hypervisor counters. These are not storage/input/audio kernel drivers.
        "InventorySvc", "smphost", "HvHost"];
    // Older sessions may already hold these. Keep exact recovery support, but
    // preserve caching and application compatibility in new gaming sessions.
    private static readonly string[] LegacyServiceNames = ["SysMain", "PcaSvc", "Spooler", "StiSvc", "midisrv", "RstMwService"];

    internal static string[] CaptureNames(bool maximumHeadroom) =>
        (maximumHeadroom ? ServiceNames.Concat(HeadroomServiceNames) : ServiceNames).ToArray();

    public static bool IsAllowedServiceName(string name) =>
        ServiceNames.Concat(HeadroomServiceNames).Concat(LegacyServiceNames).Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsOwnedPolicyName(string name) => name != null &&
        Regex.IsMatch(name, @"\ATourneyMode-[a-f0-9]{32}\z", RegexOptions.CultureInvariant);

    private static string RequireServiceName(ServiceBackup item)
    {
        if ((!item.WasRunning && !item.HoldDisabled) || !IsAllowedServiceName(item.Name)
            || (item.HoldDisabled && item.OriginalStartType is not (2 or 3 or 4))
            || (item.ConfigurationAttempted && (!item.HoldDisabled || item.OriginalDelayedAutoStart is not (null or 0 or 1))))
            throw new InvalidOperationException("Only recorded, previously running optional services may be changed.");
        return ServiceNames.Concat(HeadroomServiceNames).Concat(LegacyServiceNames).Single(name => name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static string RequirePolicyName(NetworkPriorityBackup item)
    {
        if (!IsOwnedPolicyName(item.PolicyName))
            throw new InvalidOperationException("The network policy is not an owned Tourney Mode policy.");
        return item.PolicyName;
    }

    public static List<ServiceBackup> CaptureOptionalServices(bool maximumHeadroom = false)
    {
        string names = string.Join(",", CaptureNames(maximumHeadroom).Select(name => "'" + name + "'"));
        string script = "$hold = $" + maximumHeadroom.ToString().ToLowerInvariant() + "\n" + """
            $services = @(Get-Service -ErrorAction Stop)
            $items = @(
                foreach ($name in @(
            """ + names + """
                )) {
                    $service = $services | Where-Object Name -eq $name | Select-Object -First 1
                    if ($null -ne $service -and ($service.Status -eq 'Running' -or $hold)) {
                        # Keep management available if real Storage Spaces pools exist.
                        # A failed query skips this optional cleanup rather than guessing.
                        if ($name -eq 'smphost') {
                            try {
                                if (@(Get-StoragePool -ErrorAction Stop | Where-Object { -not $_.IsPrimordial }).Count -gt 0) { continue }
                            } catch { continue }
                        }
                        $start = [int](Get-ItemPropertyValue -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $service.Name) -Name Start -ErrorAction Stop)
                        $delayed = (Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $service.Name) -ErrorAction Stop).DelayedAutoStart
                        if ($start -notin @(2,3)) { continue }
                        [pscustomobject]@{Name=$service.Name;WasRunning=($service.Status -eq 'Running');StopAttempted=$false;Restored=$false;OriginalStartType=$start;OriginalDelayedAutoStart=$delayed;HoldDisabled=$hold;ConfigurationAttempted=$false}
                    }
                }
            )
            ConvertTo-Json -InputObject $items -Compress
            """;
        return JsonSerializer.Deserialize<List<ServiceBackup>>(RunPowerShell(script, 10000), Json) ?? [];
    }

    public static void StopOptionalService(ServiceBackup item)
    {
        string name = RequireServiceName(item);
        if ((item.WasRunning && !item.StopAttempted) || (item.HoldDisabled && !item.ConfigurationAttempted))
            throw new InvalidOperationException("The service stop must be journaled before it is attempted.");
        string script = "$service = Get-Service -Name '" + name + "' -ErrorAction Stop\n"
            + "$hold = $" + item.HoldDisabled.ToString().ToLowerInvariant() + "\n"
            + "$wasRunning = $" + item.WasRunning.ToString().ToLowerInvariant() + "\n"
            + "$originalStart = " + (item.OriginalStartType ?? 0) + "\n" + """
            $service.Refresh()
            $runningDependents = @($service.DependentServices | Where-Object Status -ne 'Stopped')
            if ($runningDependents.Count -gt 0) { throw 'The optional service has active dependents and was left running.' }
            if ($service.Status -notin @('Running','Stopped') -or (!$wasRunning -and $service.Status -eq 'Running')) { throw 'The optional service changed state after capture.' }
            if ($hold) {
                $start = [int](Get-ItemPropertyValue -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $service.Name) -Name Start -ErrorAction Stop)
                if ($start -ne $originalStart) { throw 'The optional service startup configuration changed after capture.' }
                & "$env:SystemRoot\System32\sc.exe" config $service.Name start= disabled | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Unable to temporarily hold the optional service disabled.' }
                if ((Get-ItemPropertyValue -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $service.Name) -Name Start) -ne 4) { throw 'The optional service hold was not verified.' }
            }
            if ($service.Status -eq 'Stopped') { Write-Output 'UNCHANGED'; return }
            $service.Stop()
            $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(8))
            $service.Refresh()
            if ($service.Status -ne 'Stopped') { throw 'The optional service did not confirm stopped status.' }
            Write-Output 'STOPPED'
            """;
        string result = RunPowerShell(script);
        if (result == "UNCHANGED") item.StopAttempted = false;
        else if (result != "STOPPED") throw new InvalidOperationException("Optional service shutdown returned an unrecognized result.");
    }

    public static void RestoreOptionalService(ServiceBackup item)
    {
        if (!item.StopAttempted && !item.ConfigurationAttempted) return;
        string name = RequireServiceName(item);
        string script = "$service = Get-Service -Name '" + name + "' -ErrorAction Stop\n"
            + (item.ConfigurationAttempted ? "$originalStart = " + item.OriginalStartType + "\n"
            + "$delayed = " + (item.OriginalDelayedAutoStart?.ToString() ?? "$null") + "\n" + """
            $start = [int](Get-ItemPropertyValue -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $service.Name) -Name Start -ErrorAction Stop)
            if ($start -ne 4 -and $start -ne $originalStart) { throw 'Service startup was changed externally; recovery requires review.' }
            if ($start -ne $originalStart) {
                $mode = @{2='auto';3='demand';4='disabled'}[$originalStart]
                if ($originalStart -eq 2 -and $delayed -eq 1) { $mode = 'delayed-auto' }
                & "$env:SystemRoot\System32\sc.exe" config $service.Name start= $mode | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Unable to restore the optional service startup configuration.' }
            }
            if ((Get-ItemPropertyValue -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $service.Name) -Name Start) -ne $originalStart) { throw 'Original service startup configuration was not verified.' }
            $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $service.Name
            $currentDelayed = (Get-ItemProperty -LiteralPath $key -ErrorAction Stop).DelayedAutoStart
            if ($null -eq $delayed) {
                if ($null -ne $currentDelayed) { Remove-ItemProperty -LiteralPath $key -Name DelayedAutoStart -ErrorAction Stop }
            } elseif ($currentDelayed -ne $delayed) {
                New-ItemProperty -LiteralPath $key -Name DelayedAutoStart -PropertyType DWord -Value $delayed -Force -ErrorAction Stop | Out-Null
            }
            if ((Get-ItemProperty -LiteralPath $key -ErrorAction Stop).DelayedAutoStart -ne $delayed) { throw 'Original delayed startup setting was not verified.' }
            """ + "\n" : "")
            + ((!item.WasRunning || !item.StopAttempted) ? "Write-Output 'RUNNING'; return\n" : "") + """
            $service.Refresh()
            if ($service.Status -eq 'Running') { Write-Output 'RUNNING'; return }
            if ($service.Status -eq 'StopPending') {
                $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(8))
                $service.Refresh()
            }
            if ($service.Status -ne 'Stopped') {
                throw 'The optional service is in an unexpected state; no startup configuration was changed.'
            }
            $service.Start()
            $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(8))
            $service.Refresh()
            if ($service.Status -ne 'Running') { throw 'The optional service did not confirm running status.' }
            Write-Output 'RUNNING'
            """;
        if (RunPowerShell(script) != "RUNNING")
            throw new InvalidOperationException("Optional service recovery did not confirm running status.");
    }

    public static NetworkPriorityBackup? PrepareEthernetPriority()
    {
        string script = EthernetTuning.DiscoveryScript + "\n" + """
            Get-Command Get-NetQosPolicy,New-NetQosPolicy,Remove-NetQosPolicy -ErrorAction Stop | Out-Null
            # Select across physical, Wi-Fi and virtual defaults before checking
            # wired eligibility. Ambiguous or mixed-family defaults are skipped.
            $selected = Get-CurrentWiredAdapter
            if ($null -eq $selected) { Write-Output 'null'; return }
            [pscustomobject]@{
                PolicyName=('TourneyMode-' + [Guid]::NewGuid().ToString('N'))
                InterfaceGuid=([guid]$selected.InterfaceGuid).ToString('D')
                InterfaceName=[string]$selected.Name
                ApplyAttempted=$false
                Restored=$false
            } | ConvertTo-Json -Compress
            """;
        return JsonSerializer.Deserialize<NetworkPriorityBackup>(RunPowerShell(script, 10000), Json);
    }

    internal const string QosGamePresenceGuardScript = """
        function Get-TourneyQosFortniteProcesses {
            [Diagnostics.Process]::GetProcessesByName('FortniteClient-Win64-Shipping')
        }
        function Assert-TourneyQosGameAbsent {
            $clients = @()
            try {
                try { $clients = @(Get-TourneyQosFortniteProcesses) }
                catch { throw 'Fortnite absence could not be confirmed; no QoS policy was changed.' }
                if ($clients.Count -gt 0) {
                    throw 'Fortnite is running; outbound DSCP changes are deferred until the client fully exits.'
                }
            } finally {
                foreach ($client in $clients) {
                    if ($client -is [IDisposable]) { $client.Dispose() }
                }
            }
        }
        """;

    public static void ApplyEthernetPriority(NetworkPriorityBackup item)
    {
        string name = RequirePolicyName(item);
        if (!item.ApplyAttempted)
            throw new InvalidOperationException("Network priority must be journaled before it is attempted.");
        if (!Guid.TryParse(item.InterfaceGuid, out var expectedInterface))
            throw new InvalidOperationException("Capture a fresh preferred-route identity before creating an outbound DSCP request. Legacy policies remain restorable.");
        string script = EthernetTuning.DiscoveryScript + "\n" + QosGamePresenceGuardScript + "\n$name = '" + name + "'\n$expectedInterface = [guid]'" + expectedInterface.ToString("D") + "'\n" + """
            $current = Get-CurrentWiredAdapter
            if ($null -eq $current -or ([guid]$current.InterfaceGuid) -ne $expectedInterface) {
                throw 'The preferred default route changed or is not unambiguously wired. No outbound marking policy was created.'
            }
            $existing = @(Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction Stop | Where-Object Name -eq $name)
            if ($existing.Count -ne 0) { throw 'The runtime QoS policy already exists; it was not replaced.' }
            # This matches the application's outbound traffic on all interfaces.
            # It does not reserve bandwidth or bind the policy to an adapter;
            # router and network support determine any effect of DSCP 46.
            Assert-TourneyQosGameAbsent
            New-NetQosPolicy -Name $name -PolicyStore ActiveStore -NetworkProfile All -AppPathNameMatchCondition 'FortniteClient-Win64-Shipping.exe' -DSCPAction 46 -ErrorAction Stop | Out-Null
            $policy = @(Get-NetQosPolicy -Name $name -PolicyStore ActiveStore -ErrorAction Stop)
            if ($policy.Count -ne 1 -or $policy[0].AppPathNameMatchCondition -ne 'FortniteClient-Win64-Shipping.exe' -or [int]$policy[0].DSCPAction -ne 46) {
                throw 'Fortnite runtime QoS policy could not be verified; its recorded identity remains available for recovery.'
            }
            Write-Output 'APPLIED'
            """;
        if (RunPowerShell(script) != "APPLIED")
            throw new InvalidOperationException("Fortnite runtime QoS policy was not confirmed.");
    }

    public static void RestoreEthernetPriority(NetworkPriorityBackup item)
    {
        if (!item.ApplyAttempted) return;
        string name = RequirePolicyName(item);
        string script = QosGamePresenceGuardScript + "\n$name = '" + name + "'\n" + """
            $policy = @(Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction Stop | Where-Object Name -eq $name)
            if ($policy.Count -eq 0) { Write-Output 'REMOVED'; return }
            if ($policy.Count -ne 1 -or $policy[0].AppPathNameMatchCondition -ne 'FortniteClient-Win64-Shipping.exe' -or [int]$policy[0].DSCPAction -ne 46) {
                throw 'The recorded QoS policy was changed externally; no unrelated policy was removed.'
            }
            Assert-TourneyQosGameAbsent
            Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction Stop
            if (@(Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction Stop | Where-Object Name -eq $name).Count -ne 0) {
                throw 'The owned runtime QoS policy is still present.'
            }
            Write-Output 'REMOVED'
            """;
        if (RunPowerShell(script) != "REMOVED")
            throw new InvalidOperationException("Fortnite runtime QoS policy removal was not confirmed.");
    }

    /// <summary>Bounded helper shared by explicit driver-property tuning.</summary>
    internal static bool IsOnlyProgress(string stderr)
    {
        if (!stderr.StartsWith("#< CLIXML", StringComparison.Ordinal)) return false;
        try
        {
            var document = XDocument.Parse(stderr[9..].Trim());
            XNamespace xmlNamespace = "http://schemas.microsoft.com/powershell/2004/04";
            var entries = document.Root?.Elements().ToList();
            return document.Root?.Name == xmlNamespace + "Objs" && entries?.Count > 0
                && entries.All(entry => entry.Name == xmlNamespace + "Obj" && (string?)entry.Attribute("S") == "progress");
        }
        catch (System.Xml.XmlException) { return false; }
    }

    internal static string RunPowerShell(string script, int timeoutMs = 20000)
    {
        if (timeoutMs is < 1000 or > 120000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        string wrapper = """
            [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $WarningPreference = 'SilentlyContinue'
            $InformationPreference = 'SilentlyContinue'
            try {
            """ + "\n" + script + "\n" + """
            } catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper)) })
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The Windows resource helper did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
                process.WaitForExit(1000);
                throw new TimeoutException("The Windows resource helper exceeded its " + timeoutMs + "ms deadline. The recorded resource state was retained.");
            }
            if (!Task.WhenAll(output, errors).Wait(2000))
                throw new TimeoutException("The Windows resource helper output could not be collected within its deadline.");
            string stdout = output.Result.Trim().TrimStart('\uFEFF');
            string stderr = errors.Result.Trim();
            if (process.ExitCode != 0 || (stderr.Length > 0 && !IsOnlyProgress(stderr)))
                throw new InvalidOperationException("Windows resource change unavailable: " +
                    (stderr.Length > 0 ? stderr[..Math.Min(stderr.Length, 1500)] : "helper exit " + process.ExitCode));
            return stdout;
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException("Windows resource helpers are unavailable: " + error.Message, error);
        }
    }
}
