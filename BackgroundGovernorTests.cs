using System.Diagnostics;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Injected in-memory fixtures only; no process or device operations.</summary>
public static class BackgroundGovernorTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("background governor requires exact explicit executable approval", () =>
        {
            var fixture = new Fixture(); var first = fixture.Add(101); var other = fixture.Add(102, "other.exe");
            var backups = BackgroundGovernor.Capture([App(first, other)], [first.Path], fixture);
            Check(backups.Count == 1 && backups[0].Process.Id == first.Id && fixture.Writes.Count == 0, "Unapproved helper inherited app approval or capture mutated priority.");
            Check(BackgroundGovernor.Capture([App(first)], [], fixture).Count == 0, "Missing approval allowed capture.");
            first.StartTicks++;
            Check(backups[0].Process.StartTicks != first.StartTicks, "Journal identity was not independently copied.");
            return Task.CompletedTask;
        });
        await test("background governor caps preparation at 32 distinct process identities", () =>
        {
            var fixture = new Fixture(); var processes = Enumerable.Range(101, 40).Select(id => fixture.Add(id)).ToArray();
            var backups = BackgroundGovernor.Capture([App(processes)], [processes[0].Path], fixture);
            Check(backups.Count == 32 && fixture.Reads == 32 && fixture.Scans == 1, "Governor exceeded its process/read/scan budget.");
            BackgroundGovernor.Apply(fixture, backups, () => { }, _ => { });
            Check(fixture.Writes.Count == 32 && fixture.Scans == 1, "Apply rescanned or exceeded the mutation limit.");
            return Task.CompletedTask;
        });
        await test("background governor preserves known game, capture, routing and connection dependencies", () =>
        {
            var fixture = new Fixture();
            var protectedProcesses = new[] { "Discord.exe", "obs64.exe", "ffmpeg.exe", "VoiceMeeter.exe", "ExitLag.exe", "DS4Windows.exe", "EasyAntiCheat_EOS.exe", "FortniteClient-Win64-Shipping.exe", "node.exe" }
                .Select((name, index) => fixture.Add(200 + index, name)).ToArray();
            foreach (var process in protectedProcesses)
                Check(BackgroundGovernor.Capture([App(process)], [process.Path], fixture).Count == 0, process.Name + " became eligible.");
            Check(fixture.Writes.Count == 0, "Capture modified a protected dependency.");
            return Task.CompletedTask;
        });
        await test("background governor preserves kept, foreground and foreground-descendant identities", () =>
        {
            var fixture = new Fixture(); var foreground = fixture.Add(301, "browser.exe");
            var child = fixture.Add(302, "renderer.exe"); child.ParentId = foreground.Id;
            var kept = fixture.Add(303); fixture.Protected.Add(kept.Id); fixture.Foreground = foreground.Id;
            var unrelated = fixture.Add(304, "other.exe");
            var paths = new[] { foreground.Path, child.Path, kept.Path, unrelated.Path };
            var backups = BackgroundGovernor.Capture(paths.Select(path => App(fixture.Processes.First(process => process.Path == path))).ToList(), paths, fixture);
            Check(backups.Count == 1 && backups[0].Process.Id == unrelated.Id, "Foreground/descendant/kept app priority became eligible.");
            return Task.CompletedTask;
        });
        await test("background governor skips low priority and Realtime without raising them", () =>
        {
            var fixture = new Fixture(); var processes = new[] { ProcessPriorityClass.Idle, ProcessPriorityClass.BelowNormal, ProcessPriorityClass.RealTime }
                .Select((priority, index) => { var process = fixture.Add(401 + index); fixture.Priorities[process.Id] = (int)priority; return process; }).ToArray();
            Check(BackgroundGovernor.Capture([App(processes)], [processes[0].Path], fixture).Count == 0 && fixture.Writes.Count == 0,
                "An already low-priority or Realtime app was changed.");
            return Task.CompletedTask;
        });
        await test("background governor rejects PID reuse before applying or restoring", () =>
        {
            var fixture = new Fixture(); var process = fixture.Add(501); var backups = Capture(fixture, process);
            fixture.Processes[0] = Identity(process.Id, process.Name, process.StartTicks + 1);
            BackgroundGovernor.Apply(fixture, backups, () => { }, _ => { });
            Check(backups[0].Restored && fixture.Writes.Count == 0, "A reused PID was lowered.");
            fixture = new Fixture(); process = fixture.Add(502); backups = Capture(fixture, process);
            BackgroundGovernor.Apply(fixture, backups, () => { }, _ => { });
            fixture.Processes[0] = Identity(process.Id, process.Name, process.StartTicks + 1); fixture.Priorities[process.Id] = (int)ProcessPriorityClass.High;
            BackgroundGovernor.Restore(fixture, backups, () => { }, _ => { });
            Check(backups[0].Restored && fixture.Writes.Count == 1 && fixture.Priorities[process.Id] == (int)ProcessPriorityClass.High, "Recovery changed a reused PID.");
            return Task.CompletedTask;
        });
        await test("background governor journals original and attempted identity before lowering priority", () =>
        {
            var fixture = new Fixture(); var process = fixture.Add(601); var backups = Capture(fixture, process); string saved = "";
            fixture.BeforeWrite = (_, requested) =>
            {
                var journal = JsonSerializer.Deserialize<List<BackgroundPriorityBackup>>(saved)!;
                Check(journal.Count == 1 && journal[0].ApplyAttempted && journal[0].Original == (int)ProcessPriorityClass.Normal
                    && journal[0].Requested == requested && BackgroundGovernor.Same(journal[0].Process, process), "Setter preceded durable original/identity/attempt metadata.");
            };
            BackgroundGovernor.Apply(fixture, backups, () => saved = JsonSerializer.Serialize(backups), _ => { });
            Check(backups[0].Applied && fixture.Priorities[process.Id] == BackgroundGovernor.RequestedPriority, "The supported priority request was not verified.");
            return Task.CompletedTask;
        });
        await test("background governor persistence failure prevents mutation and uncertain retry", () =>
        {
            var fixture = new Fixture(); var process = fixture.Add(701); var backups = Capture(fixture, process);
            Expect<IOException>(() => BackgroundGovernor.Apply(fixture, backups, () => throw new IOException("fixture disk failure"), _ => { }));
            Check(fixture.Writes.Count == 0 && backups[0].ApplyAttempted, "A failed write-ahead save allowed mutation.");
            Expect<InvalidOperationException>(() => BackgroundGovernor.Apply(fixture, backups, () => { }, _ => { }));
            Check(fixture.Writes.Count == 0, "Uncertain attempt was replayed.");
            return Task.CompletedTask;
        });
        await test("background governor rechecks protection at the actual write", () =>
        {
            var fixture = new Fixture(); var process = fixture.Add(801); var backups = Capture(fixture, process);
            fixture.BeforeWrite = (identity, _) => fixture.Protected.Add(identity.Id);
            Expect<AggregateException>(() => BackgroundGovernor.Apply(fixture, backups, () => { }, _ => { }));
            Check(fixture.Writes.Count == 0 && backups[0].ApplyAttempted && !backups[0].Applied, "Fresh protection was bypassed or uncertain journal lost.");
            BackgroundGovernor.Restore(fixture, backups, () => { }, _ => { });
            Check(backups[0].Restored && fixture.Writes.Count == 0, "Already-original priority caused an unnecessary protected write.");
            return Task.CompletedTask;
        });
        await test("background governor partial setter failure remains exactly recoverable", () =>
        {
            var fixture = new Fixture(); var process = fixture.Add(901); var backups = Capture(fixture, process); int saves = 0;
            fixture.FailAfterWrite = true;
            Expect<AggregateException>(() => BackgroundGovernor.Apply(fixture, backups, () => saves++, _ => { }));
            Check(backups[0].ApplyAttempted && !backups[0].Applied && fixture.Priorities[process.Id] == BackgroundGovernor.RequestedPriority && saves >= 2,
                "Partial application did not retain attempted originals.");
            fixture.FailAfterWrite = false;
            BackgroundGovernor.Restore(fixture, backups, () => saves++, _ => { });
            Check(backups[0].Restored && fixture.Priorities[process.Id] == (int)ProcessPriorityClass.Normal, "Partial application did not restore the exact original.");
            int count = fixture.Writes.Count; BackgroundGovernor.Restore(fixture, backups, () => saves++, _ => { });
            Check(fixture.Writes.Count == count, "Completed recovery was replayed.");
            return Task.CompletedTask;
        });
        await test("background governor restores each original supported priority exactly", () =>
        {
            foreach (var original in new[] { ProcessPriorityClass.Normal, ProcessPriorityClass.AboveNormal, ProcessPriorityClass.High })
            {
                var fixture = new Fixture(); var process = fixture.Add(1001); fixture.Priorities[process.Id] = (int)original;
                var backups = Capture(fixture, process); BackgroundGovernor.Apply(fixture, backups, () => { }, _ => { });
                BackgroundGovernor.Restore(fixture, backups, () => { }, _ => { });
                Check(backups[0].Restored && fixture.Priorities[process.Id] == (int)original, "Original " + original + " was not exactly restored.");
            }
            return Task.CompletedTask;
        });
        await test("background governor preserves external edits and retains a clear recovery conflict", () =>
        {
            var fixture = new Fixture(); var process = fixture.Add(1101); var backups = Capture(fixture, process); string saved = "";
            BackgroundGovernor.Apply(fixture, backups, () => saved = JsonSerializer.Serialize(backups), _ => { });
            fixture.Priorities[process.Id] = (int)ProcessPriorityClass.AboveNormal;
            Expect<AggregateException>(() => BackgroundGovernor.Restore(fixture, backups, () => saved = JsonSerializer.Serialize(backups), _ => { }));
            var persisted = JsonSerializer.Deserialize<List<BackgroundPriorityBackup>>(saved)!;
            Check(fixture.Writes.Count == 1 && fixture.Priorities[process.Id] == (int)ProcessPriorityClass.AboveNormal
                && persisted[0].Conflict && !persisted[0].Restored && persisted[0].Status == "Conflict", "External priority was overwritten or conflict lost.");
            fixture.Priorities[process.Id] = backups[0].Original;
            BackgroundGovernor.Restore(fixture, backups, () => { }, _ => { });
            Check(backups[0].Restored && !backups[0].Conflict && fixture.Writes.Count == 1, "Already-original conflict resolution forced another write.");
            return Task.CompletedTask;
        });
        await test("background governor defers recovery of newly foreground or protected apps", () =>
        {
            var fixture = new Fixture(); var process = fixture.Add(1201); var backups = Capture(fixture, process);
            BackgroundGovernor.Apply(fixture, backups, () => { }, _ => { }); fixture.Foreground = process.Id;
            Expect<AggregateException>(() => BackgroundGovernor.Restore(fixture, backups, () => { }, _ => { }));
            Check(backups[0].Status == "Deferred" && !backups[0].Restored && fixture.Writes.Count == 1, "Foreground recovery changed priority.");
            fixture.Foreground = 99000; BackgroundGovernor.Restore(fixture, backups, () => { }, _ => { });
            Check(backups[0].Restored && fixture.Writes.Count == 2, "Deferred recovery could not finish once eligible.");
            return Task.CompletedTask;
        });
        await test("background governor preserves a priority edit between recovery check and setter", () =>
        {
            var fixture = new Fixture(); var process = fixture.Add(1251); var backups = Capture(fixture, process);
            BackgroundGovernor.Apply(fixture, backups, () => { }, _ => { });
            fixture.BeforeWrite = (identity, _) => fixture.Priorities[identity.Id] = (int)ProcessPriorityClass.AboveNormal;
            Expect<AggregateException>(() => BackgroundGovernor.Restore(fixture, backups, () => { }, _ => { }));
            Check(backups[0].Conflict && backups[0].Status == "Conflict" && fixture.Writes.Count == 1
                && fixture.Priorities[process.Id] == (int)ProcessPriorityClass.AboveNormal, "A late external edit was overwritten by recovery.");
            return Task.CompletedTask;
        });
        await test("background governor rejects malformed and duplicate restoration entries before any write", () =>
        {
            var fixture = new Fixture(); var process = fixture.Add(1301); var backups = Capture(fixture, process);
            backups.Add(new() { Process = Identity(1302, "invalid.exe"), Original = (int)ProcessPriorityClass.RealTime });
            Expect<InvalidDataException>(() => BackgroundGovernor.Apply(fixture, backups, () => { }, _ => { }));
            Check(fixture.Writes.Count == 0, "Malformed later entry was discovered only after earlier mutation.");
            backups = Capture(fixture, process); backups.Add(backups[0]);
            Expect<InvalidDataException>(() => BackgroundGovernor.Apply(fixture, backups, () => { }, _ => { }));
            return Task.CompletedTask;
        });
    }

    private static List<BackgroundPriorityBackup> Capture(Fixture fixture, ProcessIdentity process) => BackgroundGovernor.Capture([App(process)], [process.Path], fixture);
    private static AppCandidate App(params ProcessIdentity[] processes) => new() { Id = "background|" + processes[0].Path.ToLowerInvariant(), Name = processes[0].Name, LaunchPath = processes[0].Path, Processes = processes.ToList() };
    private static ProcessIdentity Identity(int id, string name, long? start = null) => new() { Id = id, Name = name, Path = @"C:\FixtureApps\Remaining\" + name, StartTicks = start ?? id, SessionId = 1 };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Fixture : IBackgroundPrioritySystem
    {
        public List<ProcessIdentity> Processes = [];
        public Dictionary<int, int> Priorities = [];
        public HashSet<int> Protected = [];
        public int Foreground = 99000;
        public int Scans, Reads;
        public bool FailAfterWrite;
        public Action<ProcessIdentity, int>? BeforeWrite;
        public List<(int Id, int Priority)> Writes = [];
        public ProcessIdentity Add(int id, string name = "remaining.exe") { var process = Identity(id, name); Processes.Add(process); Priorities[id] = (int)ProcessPriorityClass.Normal; return process; }
        public List<ProcessIdentity> Snapshot() { Scans++; return Processes.ToList(); }
        public bool Alive(ProcessIdentity process) => Processes.Any(current => BackgroundGovernor.Same(current, process));
        public bool CanAdjust(ProcessIdentity process)
        {
            var parents = Processes.ToDictionary(item => item.Id, item => item.ParentId);
            return Alive(process) && !Protected.Contains(process.Id) && Foreground != 0
                && !BackgroundGovernor.DescendantOrSelf(process.Id, Foreground, parents)
                && !BackgroundGovernor.DescendantOrSelf(Foreground, process.Id, parents);
        }
        public int GetPriority(ProcessIdentity process) { Reads++; if (!Alive(process)) throw new InvalidOperationException("fixture identity mismatch"); return Priorities[process.Id]; }
        public void SetPriority(ProcessIdentity process, int priority)
        {
            BeforeWrite?.Invoke(process, priority);
            Write(process, priority);
        }
        public void SetPriorityIfUnchanged(ProcessIdentity process, int expected, int requested)
        {
            BeforeWrite?.Invoke(process, requested);
            if (GetPriority(process) != expected) throw new BackgroundPriorityConflictException();
            Write(process, requested);
        }
        private void Write(ProcessIdentity process, int priority)
        {
            if (!CanAdjust(process)) throw new InvalidOperationException("fixture fresh protection");
            Writes.Add((process.Id, priority)); Priorities[process.Id] = priority;
            if (FailAfterWrite) throw new InvalidOperationException("fixture partial setter failure");
        }
    }
}
