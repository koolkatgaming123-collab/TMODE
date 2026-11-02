using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Explicit optional-feature groups only. Originals belong in the caller's durable recovery journal.</summary>
public static class WindowsStrip
{
    public static IReadOnlyList<StripFeatureDefinition> Features { get; } = Array.AsReadOnly(new[]
    {
        Feature("printing", "Printing", "Pause Windows print services.", "Printing, queued print jobs, print notifications and print/scan broker functions are unavailable.", "Spooler", "PrintNotify", "PrintScanBroker"),
        Feature("search", "Search indexing", "Pause the Windows Search indexer.", "Indexed Start/File Explorer and application search can be incomplete or slower. File access remains available.", "WSearch"),
        Feature("xbox-online", "Xbox online features", "Pause Xbox account, cloud-save and online-network services.", "Xbox sign-in, cloud-save synchronization and Xbox-dependent multiplayer features can fail. Controllers and Gaming Services are preserved.", "XblAuthManager", "XblGameSave", "XboxNetApiSvc"),
        Feature("maps", "Offline maps", "Pause downloaded-map maintenance.", "Offline map download, updates and applications relying on downloaded maps can be unavailable.", "MapsBroker"),
        Feature("media-sharing", "Media sharing", "Pause Windows Media Player network sharing.", "Windows Media Player/DLNA library sharing to other devices is unavailable. Local audio and playback services are preserved.", "WMPNetworkSvc"),
        Feature("discovery", "UPnP device discovery", "Pause SSDP/UPnP discovery and hosting.", "UPnP/DLNA device discovery, device hosting and applications using these services can fail. Network adapters, routing and firewall settings are preserved.", "SSDPSRV", "upnphost"),
        Feature("diagnostics", "Optional diagnostics and error reporting", "Pause the listed telemetry/message-routing and error-reporting services.", "Windows Error Reporting and connected diagnostic submission are unavailable while stripped; useful crash reports may be lost. Security and Windows Update services are preserved.", "DiagTrack", "dmwappushservice", "WerSvc"),
        Feature("scanning", "Scanners and image acquisition", "Pause Windows Image Acquisition.", "Scanner and other WIA image-acquisition workflows may fail. Camera, biometric/Hello and input services are preserved.", "stisvc")
    });
    private static StripFeatureDefinition Feature(string id, string title, string description, string lost, params string[] services)
        => new() { Id = id, Title = title, Description = description, FunctionalityLost = lost, Services = Array.AsReadOnly(services) };

    public static StripBackup Capture(IEnumerable<string> groupIds) => Capture(groupIds, new WindowsStripSystem());
    public static StripBackup Capture(IEnumerable<string> groupIds, IStripSystem system)
    {
        ArgumentNullException.ThrowIfNull(groupIds); ArgumentNullException.ThrowIfNull(system);
        var groups = SelectGroups(groupIds);
        var names = groups.SelectMany(group => group.Services).ToArray();
        var snapshots = Read(system, names);
        var backup = new StripBackup { SelectedGroups = groups.Select(group => group.Id).ToList() };
        foreach (var group in groups)
        {
            backup.Groups.Add(new() { Id = group.Id });
            foreach (string name in group.Services)
            {
                var current = snapshots[name];
                RequireKnown(current);
                backup.Services.Add(new()
                {
                    GroupId = group.Id, Name = name, Existed = current.Exists,
                    OriginalStartType = current.StartType ?? 0, OriginalDelayedAutoStart = current.DelayedAutoStart ?? false,
                    WasRunning = current.Running == true, OriginalRunningDependents = current.RunningDependents.ToList(), Restored = !current.Exists
                });
            }
        }
        return backup;
    }

    public static void Apply(StripBackup backup, Action persist, Action<string> log, CancellationToken token = default)
        => Apply(backup, persist, log, new WindowsStripSystem(), token);
    public static void Apply(StripBackup backup, Action persist, Action<string> log, IStripSystem system, CancellationToken token = default)
    {
        Validate(backup); ArgumentNullException.ThrowIfNull(persist); ArgumentNullException.ThrowIfNull(log); ArgumentNullException.ThrowIfNull(system);
        if (backup.ApplyAttempted) throw new InvalidOperationException("This strip session was already attempted. Repair it before starting a new one.");
        RequireAbsent(system, token);
        // All selected groups are preflighted before any service is stopped or configured.
        foreach (string groupId in backup.SelectedGroups)
        {
            var items = Items(backup, groupId);
            var current = Read(system, items.Select(item => item.Name).ToArray());
            foreach (var item in items) RequireOriginal(item, current[item.Name]);
            RequireGroupSafe(groupId, current, system);
            _ = StopOrder(items);
        }
        RequireAbsent(system, token); backup.ApplyAttempted = true; persist();
        int sequence = 0;
        foreach (string groupId in backup.SelectedGroups)
        {
            var status = Status(backup, groupId); status.State = "Applying"; status.Issue = ""; persist();
            try
            {
                foreach (var item in StopOrder(Items(backup, groupId)))
                {
                    RequireAbsent(system, token);
                    var fresh = Read(system, [item.Name])[item.Name]; RequireOriginal(item, fresh);
                    if (!item.Existed || item.OriginalStartType == 4 && !item.WasRunning)
                    { item.Restored = true; log(item.Name + ": absent or already disabled/stopped; preserved."); continue; }
                    RequireGroupSafe(groupId, Read(system, Items(backup, groupId).Select(value => value.Name).ToArray()), system);
                    if (fresh.RunningDependents.Count > 0) throw new InvalidOperationException(item.Name + " has an active dependent; it was left unchanged.");
                    item.ApplyAttempted = true; item.Restored = false; item.ApplyOrder = ++sequence; item.Issue = ""; persist();
                    if (fresh.Running == true)
                    {
                        RequireAbsent(system, token); item.StopAttempted = true; persist();
                        system.Stop(item.Name, token);
                        var stopped = Read(system, [item.Name])[item.Name]; RequireKnown(stopped);
                        if (!stopped.Exists || stopped.Running != false) throw new InvalidOperationException(item.Name + ": stop readback did not confirm Stopped.");
                    }
                    RequireAbsent(system, token);
                    var beforeConfig = Read(system, [item.Name])[item.Name]; RequireKnown(beforeConfig);
                    if (!beforeConfig.Exists || beforeConfig.Running != false || beforeConfig.StartType != item.OriginalStartType
                        || beforeConfig.DelayedAutoStart != item.OriginalDelayedAutoStart || beforeConfig.RunningDependents.Count > 0)
                        throw new InvalidOperationException(item.Name + ": state changed before startup configuration; review and repair.");
                    if (groupId == "printing") RequireNoPrintJobs(system);
                    item.ConfigurationAttempted = true; persist(); RequireAbsent(system, token);
                    system.SetStartup(item.Name, 4, item.OriginalDelayedAutoStart);
                    var applied = Read(system, [item.Name])[item.Name]; RequireKnown(applied);
                    if (!applied.Exists || applied.StartType != 4 || applied.DelayedAutoStart != item.OriginalDelayedAutoStart || applied.Running != false)
                        throw new InvalidOperationException(item.Name + ": disabled/stopped readback was not confirmed.");
                    log(item.Name + ": disabled and stopped; exact originals recorded. Performance benefit is unmeasured.");
                }
                RequireAbsent(system, token); status.State = "Applied"; persist();
            }
            catch (Exception error)
            {
                status.State = "Rolling back"; status.Issue = error.Message; persist();
                try
                {
                    RepairCore(backup, groupId, persist, log, system, CancellationToken.None);
                    status.State = "Rolled back"; status.Issue = error.Message; persist();
                }
                catch (Exception rollback)
                {
                    status.State = "Needs recovery"; status.Issue = error.Message + " Recovery: " + rollback.Message; persist();
                    throw new AggregateException("The optional group failed and its rollback is unverified; keep the recovery journal.", error, rollback);
                }
                throw;
            }
        }
    }

    public static void Repair(StripBackup backup, string? groupId, Action persist, Action<string> log, CancellationToken token = default)
        => Repair(backup, groupId, persist, log, new WindowsStripSystem(), token);
    public static void Repair(StripBackup backup, string? groupId, Action persist, Action<string> log, IStripSystem system, CancellationToken token = default)
    {
        Validate(backup); ArgumentNullException.ThrowIfNull(persist); ArgumentNullException.ThrowIfNull(log); ArgumentNullException.ThrowIfNull(system);
        if (groupId != null && !backup.SelectedGroups.Contains(groupId, StringComparer.Ordinal)) throw new ArgumentException("This group is not part of the saved strip session.", nameof(groupId));
        RequireAbsent(system, token); backup.RepairAttempted = true; persist();
        var errors = new List<Exception>();
        foreach (string id in groupId == null ? backup.SelectedGroups : [groupId])
            try { RepairCore(backup, id, persist, log, system, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Some optional services still need repair. Keep the recovery journal.", errors);
    }

    private static void RepairCore(StripBackup backup, string groupId, Action persist, Action<string> log, IStripSystem system, CancellationToken token)
    {
        var status = Status(backup, groupId); var items = Items(backup, groupId).Where(item => item.ApplyAttempted && !item.Restored).ToList();
        if (items.Count == 0) { status.State = "Repaired"; status.Issue = ""; persist(); return; }
        status.State = "Repairing"; persist();
        try
        {
            // Restore all startup modes before restarting parents and then their original dependents.
            foreach (var item in items)
            {
                RequireAbsent(system, token); var current = Read(system, [item.Name])[item.Name]; RequireKnown(current);
                if (!current.Exists) throw new InvalidOperationException(item.Name + ": saved service is now absent; automatic repair cannot be verified.");
                if (current.DelayedAutoStart != item.OriginalDelayedAutoStart || current.StartType != 4 && current.StartType != item.OriginalStartType)
                    throw new InvalidOperationException(item.Name + ": startup configuration changed outside this session; the external choice was preserved.");
                if (!item.WasRunning && current.Running == true) throw new InvalidOperationException(item.Name + ": another owner started this service; it was not stopped by repair.");
                if (current.StartType != item.OriginalStartType)
                {
                    persist(); RequireAbsent(system, token); system.SetStartup(item.Name, item.OriginalStartType, item.OriginalDelayedAutoStart);
                }
                var restored = Read(system, [item.Name])[item.Name]; RequireKnown(restored);
                if (!restored.Exists || restored.StartType != item.OriginalStartType || restored.DelayedAutoStart != item.OriginalDelayedAutoStart)
                    throw new InvalidOperationException(item.Name + ": original startup readback failed.");
            }
            foreach (var item in items.OrderByDescending(item => item.ApplyOrder))
            {
                RequireAbsent(system, token); var current = Read(system, [item.Name])[item.Name]; RequireKnown(current);
                if (item.WasRunning && current.Running == false)
                { persist(); RequireAbsent(system, token); system.Start(item.Name, token); }
                var repaired = Read(system, [item.Name])[item.Name]; RequireKnown(repaired);
                if (!repaired.Exists || repaired.StartType != item.OriginalStartType || repaired.DelayedAutoStart != item.OriginalDelayedAutoStart || repaired.Running != item.WasRunning)
                    throw new InvalidOperationException(item.Name + ": original startup/running state was not confirmed.");
                item.Restored = true; item.Issue = ""; persist(); log(item.Name + ": original startup and running state restored.");
            }
            status.State = "Repaired"; status.Issue = ""; persist();
        }
        catch (Exception error)
        {
            status.State = "Needs recovery"; status.Issue = error.Message;
            foreach (var item in items.Where(item => !item.Restored)) item.Issue = error.Message;
            persist(); throw;
        }
    }

    private static IReadOnlyList<StripFeatureDefinition> SelectGroups(IEnumerable<string> ids)
    {
        var selected = ids.ToList();
        if (selected.Count == 0 || selected.Count > Features.Count || selected.Distinct(StringComparer.Ordinal).Count() != selected.Count
            || selected.Any(id => !Features.Any(feature => feature.Id == id)))
            throw new ArgumentException("Choose one or more distinct, known optional-feature groups.", nameof(ids));
        return Features.Where(feature => selected.Contains(feature.Id, StringComparer.Ordinal)).ToList();
    }
    private static List<StripServiceBackup> Items(StripBackup backup, string id) => backup.Services.Where(item => item.GroupId == id).ToList();
    private static StripGroupStatus Status(StripBackup backup, string id) => backup.Groups.Single(group => group.Id == id);
    private static void Validate(StripBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (backup.Schema != 1 || backup.SelectedGroups == null || backup.Services == null || backup.Groups == null) throw new InvalidDataException("Unsupported optional-service recovery data.");
        var definitions = SelectGroups(backup.SelectedGroups);
        if (backup.Groups.Count != definitions.Count || backup.Groups.Any(group => !definitions.Any(feature => feature.Id == group.Id))
            || backup.Groups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != definitions.Count
            || backup.Services.Count != definitions.Sum(group => group.Services.Count)) throw new InvalidDataException("Optional-service group membership changed.");
        var expected = definitions.SelectMany(group => group.Services.Select(name => (group.Id, Name: name))).ToList();
        if (backup.Services.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != backup.Services.Count
            || backup.Services.Any(item => !expected.Any(value => value.Id == item.GroupId && value.Name == item.Name)
                || item.Existed && (item.OriginalStartType is < 2 or > 4 || item.OriginalStartType == 4 && item.WasRunning)
                || !item.Existed && (item.ApplyAttempted || item.StopAttempted || item.ConfigurationAttempted)))
            throw new InvalidDataException("Unknown or invalid optional-service identity/original state.");
    }
    internal static void ValidateNames(IEnumerable<string> names)
    {
        if (names.Any(name => !Features.Any(group => group.Services.Contains(name, StringComparer.Ordinal))))
            throw new InvalidDataException("Unknown service name; optional-service operations were refused.");
    }
    private static Dictionary<string, StripServiceSnapshot> Read(IStripSystem system, IReadOnlyList<string> names)
    {
        ValidateNames(names);
        var rows = system.ReadServices(names);
        if (rows.Count != names.Count || rows.Any(row => !names.Contains(row.Name, StringComparer.Ordinal))
            || rows.Select(row => row.Name).Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new InvalidOperationException("The optional-service snapshot is incomplete or has unexpected identities.");
        return rows.ToDictionary(row => row.Name, StringComparer.Ordinal);
    }
    private static void RequireKnown(StripServiceSnapshot state)
    {
        if (!string.IsNullOrEmpty(state.Issue) || state.RunningDependents == null || state.Exists && (state.StartType is < 2 or > 4 or null || state.DelayedAutoStart == null || state.Running == null))
            throw new InvalidOperationException(state.Name + ": service state is unknown or unsupported. " + state.Issue);
        if (state.Exists && state.StartType == 4 && state.Running == true)
            throw new InvalidOperationException(state.Name + ": running with Disabled startup cannot be restored safely by this feature.");
    }
    private static void RequireOriginal(StripServiceBackup saved, StripServiceSnapshot current)
    {
        RequireKnown(current);
        if (saved.Existed != current.Exists || current.Exists && (current.StartType != saved.OriginalStartType || current.DelayedAutoStart != saved.OriginalDelayedAutoStart || current.Running != saved.WasRunning))
            throw new InvalidOperationException(saved.Name + ": state changed since review; capture a fresh selection before stripping.");
    }
    private static void RequireAbsent(IStripSystem system, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (system.GetGamePresence() != GamePresence.Absent) throw new InvalidOperationException("Close Fortnite before stripping or repairing Windows features; unknown game presence also blocks changes.");
    }
    private static void RequireNoPrintJobs(IStripSystem system)
    {
        int? jobs = system.ActivePrintJobs();
        if (jobs == null || jobs < 0 || jobs > 0) throw new InvalidOperationException("Printing has active jobs or its queue could not be verified empty. Printing services were preserved.");
    }
    private static void RequireGroupSafe(string groupId, Dictionary<string, StripServiceSnapshot> current, IStripSystem system)
    {
        foreach (var service in current.Values)
        {
            RequireKnown(service);
            if (service.RunningDependents.Any(name => !current.ContainsKey(name)))
                throw new InvalidOperationException(service.Name + ": an active dependent outside this optional group requires the service.");
        }
        if (groupId == "printing") RequireNoPrintJobs(system);
    }
    private static List<StripServiceBackup> StopOrder(List<StripServiceBackup> items)
    {
        var result = new List<StripServiceBackup>(); var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(StripServiceBackup item)
        {
            if (complete.Contains(item.Name)) return;
            if (!visiting.Add(item.Name)) throw new InvalidOperationException("Optional-service dependencies contain a cycle; no service was changed.");
            foreach (string dependent in item.OriginalRunningDependents)
            { var child = items.SingleOrDefault(value => value.Name.Equals(dependent, StringComparison.OrdinalIgnoreCase)); if (child != null) Visit(child); }
            visiting.Remove(item.Name); complete.Add(item.Name); result.Add(item);
        }
        foreach (var item in items) Visit(item);
        return result;
    }
}

/// <summary>Bounded read-only PowerShell snapshots plus normal Windows SCM setters. No force-stop or elevation bypass.</summary>
internal sealed class WindowsStripSystem : IStripSystem
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public GamePresence GetGamePresence()
    {
        try
        {
            var games = Process.GetProcessesByName("FortniteClient-Win64-Shipping");
            try { return games.Length == 0 ? GamePresence.Absent : GamePresence.Present; }
            finally { foreach (var game in games) game.Dispose(); }
        }
        catch { return GamePresence.Unknown; }
    }
    public IReadOnlyList<StripServiceSnapshot> ReadServices(IReadOnlyList<string> names)
    {
        WindowsStrip.ValidateNames(names);
        string literalNames = "@(" + string.Join(",", names.Select(name => "'" + name + "'")) + ")";
        string script = "$names = " + literalNames + "\n" + """
            $all = @(Get-CimInstance Win32_Service -ErrorAction Stop)
            $rows = @()
            foreach ($name in $names) {
                $entry = @($all | Where-Object Name -CEQ $name)
                if ($entry.Count -eq 0) { $rows += [pscustomobject]@{Name=$name;Exists=$false;RunningDependents=@();Issue=''}; continue }
                if ($entry.Count -ne 1) { throw 'Ambiguous optional-service identity.' }
                $service = Get-Service -Name $name -ErrorAction Stop
                $properties = Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $name) -ErrorAction Stop
                if ($properties.Start -isnot [int]) { throw 'Unknown optional-service startup type.' }
                $delayed = $properties.PSObject.Properties['DelayedAutoStart']
                if ($null -ne $delayed -and $delayed.Value -isnot [int]) { throw 'Unknown optional-service delayed startup type.' }
                $running = $null
                if ($service.Status -eq 'Running') { $running = $true }
                elseif ($service.Status -eq 'Stopped') { $running = $false }
                $dependents = @($service.DependentServices | Where-Object Status -ne 'Stopped' | ForEach-Object Name)
                $rows += [pscustomobject]@{Name=$name;Exists=$true;StartType=[int]$properties.Start;DelayedAutoStart=($null -ne $delayed -and $delayed.Value -ne 0);Running=$running;RunningDependents=$dependents;Issue=''}
            }
            ConvertTo-Json -InputObject @($rows) -Depth 4 -Compress
            """;
        string result = TournamentResources.RunPowerShell(script, 10000);
        return JsonSerializer.Deserialize<List<StripServiceSnapshot>>(result, Json) ?? throw new InvalidDataException("No optional-service snapshot was returned.");
    }
    public int? ActivePrintJobs()
    {
        try
        {
            string result = TournamentResources.RunPowerShell("@(Get-CimInstance Win32_PrintJob -ErrorAction Stop).Count", 10000);
            return int.TryParse(result.Trim(), out int count) && count >= 0 ? count : null;
        }
        catch { return null; }
    }
    private void Guard(string name, CancellationToken token)
    {
        WindowsStrip.ValidateNames([name]); token.ThrowIfCancellationRequested();
        if (GetGamePresence() != GamePresence.Absent) throw new InvalidOperationException("Fortnite presence is present or unknown; service changes are blocked.");
    }
    public void SetStartup(string name, int startType, bool delayedAutoStart)
    {
        Guard(name, CancellationToken.None);
        if (startType is < 2 or > 4) throw new InvalidDataException("Only Automatic, Manual or Disabled startup may be restored.");
        WithService(name, 0x0001 | 0x0002 | 0x0004, handle =>
        {
            if (!ChangeServiceConfig(handle, uint.MaxValue, (uint)startType, uint.MaxValue, null, null, IntPtr.Zero, null, null, null, null)) ThrowScm("Service startup change failed");
            var delayed = new DelayedInfo { Delayed = delayedAutoStart };
            if (!ChangeServiceConfig2(handle, 3, ref delayed)) ThrowScm("Service delayed-start preservation failed");
        });
    }
    public void Stop(string name, CancellationToken token)
    {
        Guard(name, token);
        var fresh = ReadServices([name]).Single();
        if (fresh.RunningDependents.Count != 0) throw new InvalidOperationException(name + " has an active dependent; force-stop is forbidden.");
        if (WindowsStrip.Features.Single(feature => feature.Id == "printing").Services.Contains(name, StringComparer.Ordinal) && ActivePrintJobs() != 0)
            throw new InvalidOperationException("The print queue is active or unknown; printing was preserved.");
        Guard(name, token);
        WithService(name, 0x0004 | 0x0020, handle =>
        {
            var current = Query(handle);
            if (current.State == 1) return;
            if (current.State != 4) throw new InvalidOperationException(name + " is not in a stable running/stopped state.");
            if (!ControlService(handle, 1, out _)) ThrowScm("Service stop failed");
            WaitFor(handle, 1, token);
        });
    }
    public void Start(string name, CancellationToken token)
    {
        Guard(name, token);
        WithService(name, 0x0004 | 0x0010, handle =>
        {
            var current = Query(handle);
            if (current.State == 4) return;
            if (current.State != 1) throw new InvalidOperationException(name + " is not in a stable running/stopped state.");
            if (!StartService(handle, 0, IntPtr.Zero)) ThrowScm("Service start failed");
            WaitFor(handle, 4, token);
        });
    }
    private static void WithService(string name, uint access, Action<IntPtr> action)
    {
        IntPtr manager = OpenSCManager(null, null, 1); if (manager == IntPtr.Zero) ThrowScm("SCM access failed");
        try
        {
            IntPtr service = OpenService(manager, name, access); if (service == IntPtr.Zero) ThrowScm("Service access failed");
            try { action(service); } finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private static ServiceStatus Query(IntPtr handle)
    {
        if (!QueryServiceStatusEx(handle, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _)) ThrowScm("Service status readback failed");
        return status;
    }
    private static void WaitFor(IntPtr handle, uint wanted, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            token.ThrowIfCancellationRequested();
            if (Query(handle).State == wanted) return;
            if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
        }
        throw new TimeoutException("The service did not confirm its requested state within 15 seconds.");
    }
    private static void ThrowScm(string message) => throw new Win32Exception(Marshal.GetLastWin32Error(), message + ". Normal administrator/service permissions are required; no bypass is attempted.");
    [StructLayout(LayoutKind.Sequential)] private struct DelayedInfo { [MarshalAs(UnmanagedType.Bool)] public bool Delayed; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, ExitCode, SpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string? binaryPath, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2(IntPtr service, uint level, ref DelayedInfo info);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(IntPtr service, int argc, IntPtr argv);
}
