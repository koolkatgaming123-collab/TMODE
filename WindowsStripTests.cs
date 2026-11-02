using System.Text.Json;

namespace TourneyMode;

/// <summary>Pure synthetic services. Never opens SCM, queries Windows, stops a service or starts PowerShell.</summary>
internal static class WindowsStripTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        await test("strip manifest is bounded, explicit and excludes essential service categories", () =>
        {
            var features = WindowsStrip.Features; var names = features.SelectMany(group => group.Services).ToArray();
            Check(features.Count == 8 && names.Length <= 20 && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length,
                "The optional-feature allowlist is duplicated or unbounded.");
            Check(features.All(group => group.Id.Length > 0 && group.Title.Length > 0 && group.Description.Length > 0 && group.FunctionalityLost.Length > 0),
                "An optional group omitted its functionality-loss disclosure.");
            foreach (string essential in new[] { "GamingServices", "GamingServicesNet", "XboxGipSvc", "WinDefend", "wuauserv", "BITS", "Audiosrv", "AudioEndpointBuilder", "Dhcp", "Dnscache", "mpssvc", "BFE", "hidserv", "WbioSrvc", "FrameServer" })
                Check(!names.Contains(essential, StringComparer.OrdinalIgnoreCase), "A protected service entered the allowlist: " + essential);
            return Task.CompletedTask;
        });
        await test("strip captures exact originals before any mutation and restores all", () =>
        {
            var fixture = new FakeSystem(); fixture.State["WSearch"].StartType = 2; fixture.State["WSearch"].DelayedAutoStart = true;
            fixture.State["MapsBroker"].Running = false;
            var backup = fixture.Capture("search", "maps");
            Check(fixture.Mutations.Count == 0 && backup.Services.Single(item => item.Name == "WSearch").OriginalDelayedAutoStart,
                "Capture mutated Windows or lost delayed-auto state.");
            WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture);
            Check(fixture.State["WSearch"].StartType == 4 && fixture.State["WSearch"].Running == false && backup.HasChanges && !backup.FullyRestored,
                "Application did not confirm the selected disabled/stopped state.");
            WindowsStrip.Repair(backup, null, fixture.Persist, _ => { }, fixture);
            Check(backup.FullyRestored && !backup.HasChanges && fixture.State["WSearch"].StartType == 2 && fixture.State["WSearch"].DelayedAutoStart == true
                && fixture.State["WSearch"].Running == true && fixture.State["MapsBroker"].StartType == 3 && fixture.State["MapsBroker"].Running == false,
                "Repair did not preserve exact startup/delayed/running originals.");
            Check(fixture.Mutations.All(name => !name.StartsWith("Start:MapsBroker", StringComparison.Ordinal)), "Repair started a service that was originally stopped.");
            return Task.CompletedTask;
        });
        await test("strip unknown groups, names and malformed originals fail before writes", () =>
        {
            var fixture = new FakeSystem();
            Expect<ArgumentException>(() => WindowsStrip.Capture(["updates"], fixture));
            Expect<ArgumentException>(() => WindowsStrip.Capture(["search", "search"], fixture));
            var backup = fixture.Capture("search"); backup.Services[0].Name = "WinDefend";
            Expect<InvalidDataException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture));
            backup = fixture.Capture("search"); backup.Services[0].OriginalStartType = 0;
            Expect<InvalidDataException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture));
            Check(fixture.Mutations.Count == 0, "Malformed selection changed a service.");
            return Task.CompletedTask;
        });
        await test("strip Present or Unknown game presence prevents all changes", () =>
        {
            foreach (var presence in new[] { GamePresence.Present, GamePresence.Unknown })
            {
                var fixture = new FakeSystem(); var backup = fixture.Capture("search"); fixture.Presence = presence;
                Expect<InvalidOperationException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture));
                Check(fixture.Mutations.Count == 0 && !backup.ApplyAttempted, "Unproven Fortnite absence permitted mutation.");
            }
            return Task.CompletedTask;
        });
        await test("strip active or unknown print queues preserve all selected groups", () =>
        {
            foreach (int? jobs in new int?[] { null, -1, 1, 100 })
            {
                var fixture = new FakeSystem { PrintJobs = jobs }; var backup = fixture.Capture("printing", "search");
                Expect<InvalidOperationException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture));
                Check(fixture.Mutations.Count == 0, "Printing preflight failed after another selected group was mutated.");
            }
            return Task.CompletedTask;
        });
        await test("strip a freshly running external dependent blocks service changes", () =>
        {
            var fixture = new FakeSystem(); var backup = fixture.Capture("search");
            fixture.State["WSearch"].RunningDependents.Add("UnrelatedBusinessService");
            Expect<InvalidOperationException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture));
            Check(fixture.Mutations.Count == 0, "A service needed by another owner was stopped.");
            return Task.CompletedTask;
        });
        await test("strip internal dependents stop before parents and restore after parents", () =>
        {
            var fixture = new FakeSystem(); fixture.State["SSDPSRV"].RunningDependents.Add("upnphost");
            var backup = fixture.Capture("discovery"); WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture);
            Check(fixture.Mutations.IndexOf("Stop:upnphost") < fixture.Mutations.IndexOf("Stop:SSDPSRV"), "A parent stopped before its active selected dependent.");
            WindowsStrip.Repair(backup, null, fixture.Persist, _ => { }, fixture);
            Check(fixture.Mutations.IndexOf("Start:SSDPSRV") < fixture.Mutations.IndexOf("Start:upnphost"), "A dependent restarted before its original parent.");
            return Task.CompletedTask;
        });
        await test("strip changed startup, delayed flag or running state invalidates capture", () =>
        {
            foreach (Action<StripServiceSnapshot> change in new Action<StripServiceSnapshot>[]
            { value => value.StartType = 2, value => value.DelayedAutoStart = true, value => value.Running = false })
            {
                var fixture = new FakeSystem(); var backup = fixture.Capture("search"); change(fixture.State["WSearch"]);
                Expect<InvalidOperationException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture));
                Check(fixture.Mutations.Count == 0, "A stale original state was overwritten.");
            }
            return Task.CompletedTask;
        });
        await test("strip unknown service snapshots and unsafe original states fail closed", () =>
        {
            foreach (Action<StripServiceSnapshot> change in new Action<StripServiceSnapshot>[]
            { value => value.StartType = null, value => value.StartType = 1, value => value.Running = null,
              value => value.DelayedAutoStart = null, value => value.StartType = 4, value => value.Issue = "Read failed" })
            {
                var fixture = new FakeSystem(); change(fixture.State["WSearch"]);
                Expect<InvalidOperationException>(() => fixture.Capture("search"));
                Check(fixture.Mutations.Count == 0, "Unknown capability was treated as a safe service state.");
            }
            return Task.CompletedTask;
        });
        await test("strip absent and already disabled/stopped services remain untouched", () =>
        {
            var fixture = new FakeSystem(); fixture.State["WSearch"] = new() { Name = "WSearch", Exists = false };
            fixture.State["MapsBroker"].StartType = 4; fixture.State["MapsBroker"].Running = false;
            var backup = fixture.Capture("search", "maps"); WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture);
            WindowsStrip.Repair(backup, null, fixture.Persist, _ => { }, fixture);
            Check(fixture.Mutations.Count == 0 && backup.FullyRestored, "An absent or already disabled/stopped service was changed or started.");
            return Task.CompletedTask;
        });
        await test("strip partial stop failure immediately rolls back the affected group", () =>
        {
            var fixture = new FakeSystem { FailAfterStop = "WSearch" }; var backup = fixture.Capture("search");
            Expect<IOException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture));
            Check(fixture.State["WSearch"].StartType == 3 && fixture.State["WSearch"].Running == true && backup.FullyRestored
                && backup.Groups.Single().State == "Rolled back", "Partial stop failure was not repaired from its saved originals.");
            return Task.CompletedTask;
        });
        await test("strip partial configuration failure rolls back its group and preserves earlier groups", () =>
        {
            var fixture = new FakeSystem { FailAfterDisable = "MapsBroker" }; var backup = fixture.Capture("search", "maps");
            Expect<IOException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture));
            Check(fixture.State["MapsBroker"].StartType == 3 && fixture.State["MapsBroker"].Running == true
                && fixture.State["WSearch"].StartType == 4 && fixture.State["WSearch"].Running == false && backup.HasChanges,
                "Group rollback changed earlier groups or lost the failed group's originals.");
            WindowsStrip.Repair(backup, null, fixture.Persist, _ => { }, fixture);
            Check(backup.FullyRestored && fixture.State["WSearch"].Running == true, "Repair all could not finish after group rollback.");
            return Task.CompletedTask;
        });
        await test("strip rollback failure retains journal intent and permits later repair", () =>
        {
            var fixture = new FakeSystem { FailAfterDisable = "WSearch", FailRestoreStartup = true }; var backup = fixture.Capture("search");
            Expect<AggregateException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture));
            Check(backup.HasChanges && backup.Groups.Single().State == "Needs recovery" && fixture.Durable!.Services.Single().ConfigurationAttempted
                && !fixture.Durable.Services.Single().Restored, "Unverified rollback lost its recovery evidence.");
            fixture.FailRestoreStartup = false;
            var reopened = Clone(fixture.Durable!); fixture.Bind(reopened);
            WindowsStrip.Repair(reopened, null, fixture.Persist, _ => { }, fixture);
            Check(reopened.FullyRestored && fixture.State["WSearch"].StartType == 3 && fixture.State["WSearch"].Running == true,
                "Serialized originals could not recover after a controller restart.");
            return Task.CompletedTask;
        });
        await test("strip per-group repair preserves other groups and their originals", () =>
        {
            var fixture = new FakeSystem(); var backup = fixture.Capture("search", "maps"); WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture);
            string untouched = JsonSerializer.Serialize(backup.Services.Single(item => item.GroupId == "maps"));
            WindowsStrip.Repair(backup, "search", fixture.Persist, _ => { }, fixture);
            Check(fixture.State["WSearch"].StartType == 3 && fixture.State["WSearch"].Running == true
                && fixture.State["MapsBroker"].StartType == 4 && fixture.State["MapsBroker"].Running == false
                && JsonSerializer.Serialize(backup.Services.Single(item => item.GroupId == "maps")) == untouched && backup.HasChanges,
                "Per-group repair changed another group's live state or backup.");
            WindowsStrip.Repair(backup, "maps", fixture.Persist, _ => { }, fixture); Check(backup.FullyRestored, "Final group repair did not complete.");
            return Task.CompletedTask;
        });
        await test("strip repair respects external startup changes and game safety", () =>
        {
            var fixture = new FakeSystem(); var backup = fixture.Capture("search"); WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture);
            int before = fixture.Mutations.Count; fixture.Presence = GamePresence.Unknown;
            Expect<InvalidOperationException>(() => WindowsStrip.Repair(backup, null, fixture.Persist, _ => { }, fixture));
            Check(fixture.Mutations.Count == before && backup.HasChanges, "Repair proceeded during unknown game presence.");
            fixture.Presence = GamePresence.Absent; fixture.State["WSearch"].StartType = 2;
            Expect<AggregateException>(() => WindowsStrip.Repair(backup, null, fixture.Persist, _ => { }, fixture));
            Check(fixture.Mutations.Count == before && fixture.State["WSearch"].StartType == 2 && backup.HasChanges,
                "Repair silently overwrote an external startup choice.");
            return Task.CompletedTask;
        });
        await test("strip pre-cancel leaves services unchanged and mid-stop cancel rolls back uncancelled", () =>
        {
            var fixture = new FakeSystem(); var backup = fixture.Capture("search");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Expect<OperationCanceledException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture, canceled.Token));
            Check(fixture.Mutations.Count == 0 && !backup.ApplyAttempted, "Pre-cancellation changed a service.");
            fixture = new FakeSystem(); backup = fixture.Capture("search"); using var mid = new CancellationTokenSource();
            fixture.AfterMutation = value => { if (value == "Stop:WSearch") mid.Cancel(); };
            Expect<OperationCanceledException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture, mid.Token));
            Check(backup.FullyRestored && fixture.State["WSearch"].Running == true, "Canceled apply canceled its own rollback.");
            return Task.CompletedTask;
        });
        await test("strip a late Fortnite launch stops further work and retains recoverable originals", () =>
        {
            var fixture = new FakeSystem(); var backup = fixture.Capture("search");
            fixture.AfterMutation = value => { if (value == "Stop:WSearch") fixture.Presence = GamePresence.Present; };
            Expect<AggregateException>(() => WindowsStrip.Apply(backup, fixture.Persist, _ => { }, fixture));
            Check(fixture.Mutations.SequenceEqual(["Stop:WSearch"]) && backup.HasChanges && backup.Groups.Single().State == "Needs recovery",
                "A late game launch allowed additional mutations or lost recovery.");
            fixture.Presence = GamePresence.Absent; fixture.AfterMutation = null;
            WindowsStrip.Repair(backup, null, fixture.Persist, _ => { }, fixture); Check(backup.FullyRestored, "Repair after game exit did not finish.");
            return Task.CompletedTask;
        });
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown.");
    }
    private sealed class FakeSystem : IStripSystem
    {
        public readonly Dictionary<string, StripServiceSnapshot> State = WindowsStrip.Features.SelectMany(group => group.Services)
            .ToDictionary(name => name, name => new StripServiceSnapshot { Name = name, Exists = true, StartType = 3, DelayedAutoStart = false, Running = true }, StringComparer.OrdinalIgnoreCase);
        public GamePresence Presence;
        public int? PrintJobs = 0;
        public StripBackup? Current, Durable;
        public readonly List<string> Mutations = [];
        public string? FailAfterStop, FailAfterDisable;
        public bool FailRestoreStartup;
        public Action<string>? AfterMutation;
        public StripBackup Capture(params string[] groups) { var backup = WindowsStrip.Capture(groups, this); Bind(backup); return backup; }
        public void Bind(StripBackup backup) { Current = backup; Durable = Clone(backup); }
        public void Persist() { Check(Current != null, "No fixture journal was bound."); Durable = Clone(Current); }
        public GamePresence GetGamePresence() => Presence;
        public IReadOnlyList<StripServiceSnapshot> ReadServices(IReadOnlyList<string> names) => names.Select(name =>
        {
            var row = Clone(State[name]); row.RunningDependents.RemoveAll(dependent => State.TryGetValue(dependent, out var value) && value.Running == false); return row;
        }).ToList();
        public int? ActivePrintJobs() => PrintJobs;
        public void SetStartup(string name, int startType, bool delayedAutoStart)
        {
            var saved = Journal(name); Check(saved.ConfigurationAttempted || startType == saved.OriginalStartType, "Startup write lacked a journaled attempt.");
            if (startType != 4 && FailRestoreStartup) throw new IOException("Fixture startup recovery failure.");
            State[name].StartType = startType; State[name].DelayedAutoStart = delayedAutoStart; Record("Config:" + name + ":" + startType);
            if (startType == 4 && FailAfterDisable == name) { FailAfterDisable = null; throw new IOException("Fixture partial startup failure."); }
        }
        public void Stop(string name, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Check(Journal(name).StopAttempted, "Stop lacked a journaled attempt.");
            Check(ReadServices([name]).Single().RunningDependents.Count == 0, "An active dependent was force-stopped by the fixture.");
            State[name].Running = false; Record("Stop:" + name);
            if (FailAfterStop == name) { FailAfterStop = null; throw new IOException("Fixture partial stop failure."); }
        }
        public void Start(string name, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Check(Journal(name).WasRunning, "Repair started an originally stopped service."); State[name].Running = true; Record("Start:" + name); }
        private StripServiceBackup Journal(string name)
        {
            Check(Presence == GamePresence.Absent, "A service mutator ran while game presence was present or unknown.");
            Check(Durable != null && Durable.ApplyAttempted, "Mutation preceded durable session intent.");
            var saved = Durable!.Services.Single(item => item.Name == name);
            Check(saved.ApplyAttempted && saved.Existed && !saved.Restored, "Mutation preceded the exact per-service backup/attempt."); return saved;
        }
        private void Record(string action) { Mutations.Add(action); AfterMutation?.Invoke(action); }
    }
}
