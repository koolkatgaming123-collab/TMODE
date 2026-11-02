using System.Text;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Isolated NIC transactions and the real route selector with shadowed, fixture-only cmdlets.</summary>
public static class NetworkSafetyTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        await test("QoS write guard requires fresh confirmed Fortnite absence", () =>
        {
            Check(QosGuardFixture("Absent") == "ALLOWED:2", "Confirmed absence did not permit the fixture-only policy writes.");
            Check(QosGuardFixture("Present") == "BLOCKED:0", "A visible Fortnite client allowed a policy write.");
            Check(QosGuardFixture("Unknown") == "BLOCKED:0", "A failed process query was treated as confirmed absence.");
            return Task.CompletedTask;
        });
        await test("NIC transaction refuses an unjournaled or replayed experiment", () =>
        {
            var system = new NetworkFixture();
            var backup = Backup(); backup.ApplyAttempted = false;
            Expect<InvalidOperationException>(() => NetworkSafety.Apply(system, backup, () => { }, _ => { }));
            backup.ApplyAttempted = true; backup.Restored = true;
            Expect<InvalidOperationException>(() => NetworkSafety.Apply(system, backup, () => { }, _ => { }));
            Check(system.Calls.Count == 0, "An invalid experiment reached the NIC facade.");
            return Task.CompletedTask;
        });
        await test("NIC transaction persists before applying and leaves recovery originals intact", () =>
        {
            var system = new NetworkFixture(); var backup = Backup();
            NetworkSafety.Apply(system, backup, () => system.Calls.Add("persist"), _ => { });
            Check(system.Calls.SequenceEqual(new[] { "persist", "apply", "persist" }), "Apply was not bounded by durable state writes.");
            Check(!backup.Restored && backup.Properties[0].OriginalValues[0] == "1", "Successful apply lost its recovery state.");
            return Task.CompletedTask;
        });
        await test("NIC transaction does not touch a driver when initial journal persistence fails", () =>
        {
            var system = new NetworkFixture();
            Expect<IOException>(() => NetworkSafety.Apply(system, Backup(), () => throw new IOException("fixture disk full"), _ => { }));
            Check(system.Calls.Count == 0, "A failed journal write caused an apply or reconnect.");
            return Task.CompletedTask;
        });
        await test("partial NIC apply failure rolls back and durably records verified recovery", () =>
        {
            var system = new NetworkFixture(); var backup = Backup(); bool applied = false;
            system.ApplyCallback = () => { applied = true; throw new InvalidOperationException("fixture reconnect failure"); };
            system.RestoreCallback = () => applied = false;
            var saved = new List<bool>();
            var error = Expect<NetworkTransitionException>(() => NetworkSafety.Apply(system, backup, () => saved.Add(backup.Restored), _ => { }));
            Check(!error.RecoveryRequired && !applied && backup.Restored, "A verified rollback was not recorded.");
            Check(saved.SequenceEqual(new[] { false, true }) && system.Calls.SequenceEqual(new[] { "apply", "restore" }), "Rollback ordering or persistence was wrong.");
            return Task.CompletedTask;
        });
        await test("failed NIC rollback requires recovery and preserves the original backup", () =>
        {
            var system = new NetworkFixture(); var backup = Backup();
            system.ApplyCallback = () => throw new InvalidOperationException("fixture partial apply");
            system.RestoreCallback = () => throw new InvalidOperationException("fixture link still down");
            var error = Expect<NetworkTransitionException>(() => NetworkSafety.Apply(system, backup, () => { }, _ => { }));
            Check(error.RecoveryRequired && !backup.Restored && backup.ApplyAttempted && backup.Properties[0].OriginalValues[0] == "1", "Unverified rollback discarded recovery state.");
            Check(error.InnerException is AggregateException && system.Calls.SequenceEqual(new[] { "apply", "restore" }), "Both failure causes or rollback attempt were lost.");
            return Task.CompletedTask;
        });
        await test("post-apply journal failure also rolls the NIC experiment back", () =>
        {
            var system = new NetworkFixture(); var backup = Backup(); int saves = 0;
            var error = Expect<NetworkTransitionException>(() => NetworkSafety.Apply(system, backup,
                () => { if (++saves == 2) throw new IOException("fixture post-apply save failed"); }, _ => { }));
            Check(!error.RecoveryRequired && backup.Restored && saves == 3 && system.Calls.SequenceEqual(new[] { "apply", "restore" }), "Applied values were kept after a journal failure.");
            return Task.CompletedTask;
        });
        await test("unpersisted NIC rollback is conservatively left pending", () =>
        {
            var system = new NetworkFixture(); var backup = Backup(); int saves = 0;
            system.ApplyCallback = () => throw new InvalidOperationException("fixture apply failure");
            var error = Expect<NetworkTransitionException>(() => NetworkSafety.Apply(system, backup,
                () => { if (++saves > 1) throw new IOException("fixture recovery save failed"); }, _ => { }));
            Check(error.RecoveryRequired && !backup.Restored && saves >= 2, "Unpersisted recovery was reported complete.");
            return Task.CompletedTask;
        });
        await test("NIC rollback does not depend on a successful log callback", () =>
        {
            var system = new NetworkFixture(); var backup = Backup();
            system.ApplyCallback = () => throw new InvalidOperationException("fixture apply failure");
            var error = Expect<NetworkTransitionException>(() => NetworkSafety.Apply(system, backup, () => { }, _ => throw new InvalidOperationException("fixture log failure")));
            Check(!error.RecoveryRequired && backup.Restored && system.Calls.Contains("restore"), "A log failure prevented safety rollback.");
            return Task.CompletedTask;
        });
        await test("empty NIC experiment and legacy QoS backup do not replay changes", () =>
        {
            var system = new NetworkFixture(); var backup = Backup(); backup.Properties.Clear(); backup.ApplyAttempted = false;
            NetworkSafety.Apply(system, backup, () => throw new Exception("No-op should not persist an attempted mutation."), _ => { });
            Check(system.Calls.Count == 0, "A no-op caused a NIC mutation.");
            var legacy = JsonSerializer.Deserialize<NetworkPriorityBackup>("{\"PolicyName\":\"TourneyMode-0123456789abcdef0123456789abcdef\",\"InterfaceName\":\"Ethernet\",\"ApplyAttempted\":true}")!;
            Check(legacy.InterfaceGuid == "" && TournamentResources.IsOwnedPolicyName(legacy.PolicyName), "Legacy owned QoS recovery identity was rejected.");
            Expect<InvalidOperationException>(() => TournamentResources.ApplyEthernetPriority(legacy));
            return Task.CompletedTask;
        });

        await test("preferred default route fixture selects wired when both families agree", () =>
        {
            Check(SelectRoute([
                Route(2, "IPv4", 10), Route(9, "IPv4", 40), Route(2, "IPv6", 10), Route(9, "IPv6", 40)
            ], [Ip(2, "IPv4", 10), Ip(9, "IPv4", 10), Ip(2, "IPv6", 10), Ip(9, "IPv6", 10)], [Adapter(2, true), Adapter(9, false)]) == WiredGuid,
                "An unambiguous wired preferred route was not selected.");
            return Task.CompletedTask;
        });
        await test("preferred Wi-Fi default wins over an available Ethernet link", () =>
        {
            Check(SelectRoute([Route(2, "IPv4", 30), Route(9, "IPv4", 1)], [Ip(2, "IPv4", 10), Ip(9, "IPv4", 1)], [Adapter(2, true), Adapter(9, false)]) == "",
                "Ethernet was selected after ignoring a preferred Wi-Fi default.");
            return Task.CompletedTask;
        });
        await test("preferred virtual VPN default wins before physical-adapter filtering", () =>
        {
            Check(SelectRoute([Route(2, "IPv4", 30), Route(51, "IPv4", 1)], [Ip(2, "IPv4", 10), Ip(51, "IPv4", 1)], [Adapter(2, true), Adapter(51, false, hardware: false)]) == "",
                "A preferred virtual adapter was ignored.");
            return Task.CompletedTask;
        });
        await test("connected VPN route missing from Get-NetAdapter still prevents wired tuning", () =>
        {
            Check(SelectRoute([Route(2, "IPv4", 30), Route(51, "IPv4", 1)], [Ip(2, "IPv4", 10), Ip(51, "IPv4", 1)], [Adapter(2, true)]) == "",
                "An unmapped but connected preferred VPN route was ignored.");
            return Task.CompletedTask;
        });
        await test("mixed-family and equal-cost default adapters are conservatively skipped", () =>
        {
            Check(SelectRoute([Route(2, "IPv4", 10), Route(9, "IPv6", 10)], [Ip(2, "IPv4", 1), Ip(9, "IPv6", 1)], [Adapter(2, true), Adapter(9, false)]) == "",
                "Different IPv4/IPv6 preferred adapters were guessed as Ethernet.");
            Check(SelectRoute([Route(2, "IPv4", 10), Route(9, "IPv4", 10)], [Ip(2, "IPv4", 1), Ip(9, "IPv4", 1)], [Adapter(2, true), Adapter(9, false)]) == "",
                "Equal-cost different adapters were not treated as ambiguous.");
            return Task.CompletedTask;
        });
        await test("dead and disconnected defaults do not defeat a usable wired default", () =>
        {
            Check(SelectRoute([Route(2, "IPv4", 20), Route(51, "IPv4", 1, "Dead")], [Ip(2, "IPv4", 1), Ip(51, "IPv4", 1)], [Adapter(2, true)]) == WiredGuid,
                "A dead route defeated the active wired route.");
            Check(SelectRoute([Route(2, "IPv4", 20), Route(51, "IPv4", 1)], [Ip(2, "IPv4", 1), Ip(51, "IPv4", 1, "Disconnected")], [Adapter(2, true)]) == WiredGuid,
                "A disconnected interface defeated the active wired route.");
            return Task.CompletedTask;
        });
    }

    private static string QosGuardFixture(string mode)
    {
        string query = mode switch
        {
            "Absent" => "return",
            "Present" => "[pscustomobject]@{ProcessName='FortniteClient-Win64-Shipping';Id=12345}",
            "Unknown" => "throw 'fixture process-query failure'",
            _ => throw new ArgumentException("Unsupported fixture mode.")
        };
        // Replace the named-client query and both mutators before invoking the
        // real guard. No real process query or network operation is performed.
        string script = TournamentResources.QosGamePresenceGuardScript
            + "\nfunction Get-TourneyQosFortniteProcesses { " + query + " }\n" + """
            $script:writes = 0
            function New-NetQosPolicy { $script:writes++ }
            function Remove-NetQosPolicy { $script:writes++ }
            foreach ($command in @('Get-TourneyQosFortniteProcesses','New-NetQosPolicy','Remove-NetQosPolicy')) {
                if ((Get-Command $command -ErrorAction Stop).CommandType -ne 'Function') { throw 'Fixture operation was not safely shadowed.' }
            }
            try {
                Assert-TourneyQosGameAbsent
                New-NetQosPolicy
                Assert-TourneyQosGameAbsent
                Remove-NetQosPolicy
                'ALLOWED:' + $script:writes
            } catch { 'BLOCKED:' + $script:writes }
            """;
        return TournamentResources.RunPowerShell(script, 10000);
    }

    private const string WiredGuid = "33333333-3333-3333-3333-333333333333";
    private static object Route(int index, string family, int metric, string state = "Alive") => new
    { InterfaceIndex = index, AddressFamily = family, RouteMetric = metric, State = state, DestinationPrefix = family == "IPv4" ? "0.0.0.0/0" : "::/0" };
    private static object Ip(int index, string family, int metric, string connection = "Connected") => new
    { InterfaceIndex = index, AddressFamily = family, InterfaceMetric = metric, ConnectionState = connection };
    private static object Adapter(int index, bool wired, bool hardware = true) => new
    { ifIndex = index, Name = "Fixture adapter " + index, InterfaceGuid = index == 2 ? WiredGuid : "99999999-9999-9999-9999-999999999999", HardwareInterface = hardware, MediaType = wired ? "802.3" : "Native 802.11", PhysicalMediaType = wired ? "802.3" : "Native 802.11", Status = "Up" };

    private static string SelectRoute(object[] routes, object[] interfaces, object[] adapters)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Routes = routes, Interfaces = interfaces, Adapters = adapters })));
        // These three functions shadow every networking query used by the real
        // selector. Assert their command types before invoking it: no live NIC
        // queries, property writes, QoS policies or reconnects occur in fixtures.
        string script = "$script:routeFixture = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "')) | ConvertFrom-Json\n" + """
function Get-NetRoute { [CmdletBinding()] param($PolicyStore) @($script:routeFixture.Routes) }
function Get-NetIPInterface { [CmdletBinding()] param() @($script:routeFixture.Interfaces) }
function Get-NetAdapter { [CmdletBinding()] param([switch]$IncludeHidden, [switch]$Physical) @($script:routeFixture.Adapters) }
foreach ($command in @('Get-NetRoute','Get-NetIPInterface','Get-NetAdapter')) {
    if ((Get-Command $command -ErrorAction Stop).CommandType -ne 'Function') { throw 'Fixture network query was not safely shadowed.' }
}
""" + "\n" + EthernetTuning.DiscoveryScript + "\n" + """
$selected = Get-CurrentWiredAdapter
if ($null -eq $selected) { 'NONE' } else { ([guid]$selected.InterfaceGuid).ToString('D') }
""";
        string result = TournamentResources.RunPowerShell(script, 10000);
        return result == "NONE" ? "" : Guid.Parse(result).ToString("D");
    }

    private static EthernetAdapterBackup Backup() => new()
    {
        InterfaceGuid = WiredGuid, InterfaceName = "Fixture Ethernet", ApplyAttempted = true,
        Properties = [new() { Keyword = "*EEE", OriginalValues = ["1"], AppliedValues = ["0"] }]
    };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown.");
    }

    private sealed class NetworkFixture : ISystemFacade
    {
        public List<string> Calls { get; } = [];
        public Action? ApplyCallback { get; set; }
        public Action? RestoreCallback { get; set; }
        public void ApplyEthernetAdapter(EthernetAdapterBackup item) { Calls.Add("apply"); ApplyCallback?.Invoke(); }
        public void RestoreEthernetAdapter(EthernetAdapterBackup item) { Calls.Add("restore"); RestoreCallback?.Invoke(); }
        public List<ProcessIdentity> Snapshot() => throw new NotSupportedException();
        public List<ProcessIdentity> Games() => [];
        public List<ProcessIdentity> WatchedProcesses(List<AppCandidate> apps) => throw new NotSupportedException();
        public bool Alive(ProcessIdentity p) => throw new NotSupportedException();
        public bool CanTouch(ProcessIdentity p) => throw new NotSupportedException();
        public void CloseWindow(ProcessIdentity p) => throw new NotSupportedException();
        public void Terminate(ProcessIdentity p) => throw new NotSupportedException();
        public int GetPriority(ProcessIdentity p) => throw new NotSupportedException();
        public void SetPriority(ProcessIdentity p, int priority) => throw new NotSupportedException();
        public bool Launch(AppCandidate app) => throw new NotSupportedException();
        public List<NotificationBackup> CaptureNotifications() => throw new NotSupportedException();
        public void SuppressNotifications() => throw new NotSupportedException();
        public void RestoreNotification(NotificationBackup item) => throw new NotSupportedException();
        public string GetPowerScheme() => throw new NotSupportedException();
        public void SetPowerScheme(string guid) => throw new NotSupportedException();
        public bool? GetDnd() => throw new NotSupportedException();
        public void SetDnd(bool enabled) => throw new NotSupportedException();
        public List<NotificationBackup> CaptureTuning() => throw new NotSupportedException();
        public void ApplyTuning() => throw new NotSupportedException();
        public void RestoreTuning(NotificationBackup item) => throw new NotSupportedException();
        public List<string> CreatePlan(string original, string destination, bool fortniteOnly) => throw new NotSupportedException();
        public void DeletePlan(string guid) => throw new NotSupportedException();
        public Task StopNative() => throw new NotSupportedException();
        public Task StartNative() => throw new NotSupportedException();
        public bool NativeReady() => throw new NotSupportedException();
        public bool MobileReady() => throw new NotSupportedException();
        public bool AppReady(AppCandidate app) => throw new NotSupportedException();
        public void SetRecoveryStartup(bool enabled) => throw new NotSupportedException();
        public Task Delay(int ms) => throw new NotSupportedException();
    }
}
