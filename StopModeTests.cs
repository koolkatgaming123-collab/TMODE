using System.Diagnostics;
using System.Text.Json;

namespace TourneyMode;

/// <summary>In-memory host facade and isolated recovery files only; never operates on real apps or settings.</summary>
internal static class StopModeTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("OFF supersedes activation awaiting its initial reviewed analysis", async () =>
        {
            var fake = new StopSystem(); var entered = Signal(); var release = new TaskCompletionSource<PcAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
            var report = Report();
            var engine = new TourneyEngine(fake.Directory, fake, () => [], _ => { entered.TrySetResult(true); return release.Task; });
            var options = Options(); options.OptionsVersion = 9;
            options = PerformancePlanner.Apply(PerformancePlanner.Build(report, options, false, false), options);
            var enable = engine.EnableAsync(options);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(engine.ActivationBusy, "Initial analysis was not exposed as activation busy.");
            engine.RequestRestore(); var restore = engine.RestoreAsync(); release.SetResult(report);
            await Canceled(enable); await restore.WaitAsync(TimeSpan.FromSeconds(5));
            Check(fake.Mutations == 0 && !engine.IsActive && !engine.ActivationBusy && engine.StopRequested && !engine.IsModeRunning,
                "A stop during analysis was cleared or reached a host mutation.");
        });
        await test("OFF before worker dispatch cancels activation without creating recovery debt", async () =>
        {
            var fake = new StopSystem(); var engine = new TourneyEngine(fake.Directory, fake, () => []);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); engine.RequestRestore();
            await Canceled(engine.EnableAsync(Options(), cancellation.Token));
            Check(!engine.ActivationBusy && !engine.IsActive && fake.Mutations == 0 && !File.Exists(fake.Journal), "Pre-dispatch cancellation created or mutated a session.");
        });
        await test("fresh explicit Tourney and Strip activations after completed OFF can run again", async () =>
        {
            var fake = new StopSystem(); var engine = new TourneyEngine(fake.Directory, fake, () => []);
            await engine.EnableAsync(Options()); await engine.RestoreAsync(); await engine.EnableAsync(Options());
            Check(engine.IsModeRunning && !engine.StopRequested && !engine.ActivationBusy, "A historical stop permanently blocked a fresh explicit start.");
            await engine.RestoreAsync();
            await engine.EnableStripAsync(new() { SelectedGroups = ["search"], Services = [new() { GroupId = "search", Name = "WSearch", Existed = true, OriginalStartType = 3 }], Groups = [new() { Id = "search" }] });
            Check(engine.IsModeRunning && !engine.StopRequested, "A fresh Strip activation retained a historical OFF label.");
            await engine.RestoreAsync();
        });
        await test("rejected duplicate activation leaves the existing session and guard state intact", async () =>
        {
            var fake = new StopSystem(); var engine = new TourneyEngine(fake.Directory, fake, () => []); await engine.EnableAsync(Options());
            bool rejected = false; try { await engine.EnableAsync(Options()); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && engine.IsModeRunning && engine.State?.Phase == "Active", "A rejected second start corrupted another operation's active session.");
            await engine.RestoreAsync();
        });
        await test("queued activation cannot rearm after a newer stop during recovery", async () =>
        {
            var fake = new StopSystem(); var engine = new TourneyEngine(fake.Directory, fake, () => []);
            var options = Options(); options.CloseCodex = true; await engine.EnableAsync(options);
            fake.RestoreCodexRelease = Signal(); var restore = engine.RestoreAsync();
            await fake.RestoreCodexEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = engine.EnableAsync(Options()); engine.RequestRestore(); fake.RestoreCodexRelease.SetResult(true);
            await restore.WaitAsync(TimeSpan.FromSeconds(5)); await Canceled(queued);
            Check(!engine.IsActive && !engine.IsModeRunning && engine.StopRequested && fake.StartupEnables == 1,
                "A queued start turned mode back on after a newer OFF request.");
        });
        await test("OFF at notification boundary prevents further suppression writes", async () =>
        {
            var fake = new StopSystem(); var engine = new TourneyEngine(fake.Directory, fake, () => []);
            fake.AfterDndWrite = () => { fake.AfterDndWrite = null; engine.RequestRestore(); };
            var options = Options(); options.SilenceNotifications = true;
            await Canceled(engine.EnableAsync(options));
            Check(fake.Suppressions == 0 && engine.StopRequested && !engine.IsModeRunning, "Notification suppression continued after OFF.");
            await engine.RestoreAsync(); Check(!fake.Dnd && !engine.IsActive, "Recorded DND original did not recover.");
        });
        await test("OFF inside graceful app closure stops the remaining process batch", async () =>
        {
            var fake = new StopSystem(); var app = fake.App(); var engine = new TourneyEngine(fake.Directory, fake, () => [app]);
            fake.AfterClose = () => engine.RequestRestore(); var options = Options(); options.SelectedApps = [app.Id]; options.ForceClose = true;
            await Canceled(engine.EnableAsync(options));
            Check(fake.Closes == 1 && fake.Kills == 0, "More app windows or force termination continued after OFF.");
            await engine.RestoreAsync(); Check(!engine.IsActive && fake.Launches == 0, "OFF relaunched ordinary apps or lost recovery.");
        });
        await test("OFF after power clone creation does not activate the clone", async () =>
        {
            var fake = new StopSystem(); var engine = new TourneyEngine(fake.Directory, fake, () => []);
            fake.AfterClone = () => engine.RequestRestore(); var options = Options(); options.HighPerformancePower = true;
            await Canceled(engine.EnableAsync(options));
            Check(fake.CloneActivations == 0 && fake.CloneCreates == 1 && File.Exists(fake.Journal), "Power clone became active after OFF or its recovery identity was lost.");
            await engine.RestoreAsync(); Check(fake.DeletedPlans == 1 && !engine.IsActive, "Unused recorded clone did not recover.");
        });
        await test("OFF during priority assignment prevents subsequent game-priority writes", async () =>
        {
            var fake = new StopSystem { GamePresent = true }; fake.AddGame(700); fake.AddGame(701);
            var engine = new TourneyEngine(fake.Directory, fake, () => []); fake.AfterPriorityWrite = () => engine.RequestRestore();
            var options = Options(); options.HighPriority = true;
            await Canceled(engine.EnableAsync(options));
            Check(fake.Priority[700] == (int)ProcessPriorityClass.High && fake.Priority[701] == (int)ProcessPriorityClass.Normal,
                "Later game identities were reprioritized after OFF.");
            fake.AfterPriorityWrite = null; await engine.RestoreAsync(); Check(fake.Priority.Values.All(value => value == (int)ProcessPriorityClass.Normal), "Safe original game priority did not restore.");
        });
        await test("quiet-match OFF restores safe originals now and defers NIC and INI recovery", async () =>
        {
            var fake = new StopSystem { GamePresent = true, Dnd = true, ToastSuppressed = true }; var game = fake.AddGame(800); fake.Priority[800] = (int)ProcessPriorityClass.High;
            var state = Saved(game); state.OriginalDoNotDisturb = false; state.EthernetAdapter = new() { ApplyAttempted = true }; state.FortniteSettings = new() { ApplyAttempted = true };
            state.Codex = new(); Write(fake, state); var engine = new TourneyEngine(fake.Directory, fake, () => []);
            Check(engine.StopRequested && !engine.IsModeRunning, "Reopening a journal resumed mode or lost the recovery-only stop latch.");
            await engine.RestoreAsync();
            Check(engine.StopRequested && !engine.IsModeRunning && engine.RecoveryDeferredUntilGameExit && File.Exists(fake.Journal), "OFF did not distinguish stopped mode from pending recovery.");
            Check(fake.Priority[800] == (int)ProcessPriorityClass.Normal && !fake.ToastSuppressed && fake.AdapterRestores == 0 && fake.IniRestores == 0 && fake.DndWrites == 0 && fake.CodexRestores == 0,
                "Safe originals were left active or game-disrupting recovery ran in game.");
            int writes = fake.PriorityWrites; await engine.MaintainAsync(); Check(fake.PriorityWrites == writes && fake.WatchedScans == 0, "Stopped guard/priority maintenance resumed.");
            fake.GamePresent = false; await engine.RestoreAsync();
            Check(!engine.IsActive && !File.Exists(fake.Journal) && fake.AdapterRestores == 1 && fake.IniRestores == 1 && fake.CodexRestores == 1 && !fake.Dnd,
                "Deferred exact originals did not finish after game exit.");
        });
        await test("quiet-match safe-only OFF can finish while Fortnite remains open", async () =>
        {
            var fake = new StopSystem { GamePresent = true, ToastSuppressed = true }; var game = fake.AddGame(810); fake.Priority[810] = (int)ProcessPriorityClass.High;
            Write(fake, Saved(game)); var engine = new TourneyEngine(fake.Directory, fake, () => []);
            await engine.RestoreAsync();
            Check(!engine.IsActive && !engine.IsModeRunning && fake.Priority[810] == (int)ProcessPriorityClass.Normal && !fake.ToastSuppressed,
                "A blanket quiet-match deferral prevented safe-only OFF.");
        });
        await test("denied priority recovery keeps the journal with mode and guard stopped", async () =>
        {
            var fake = new StopSystem { GamePresent = true, DenyNormalPriority = true }; var game = fake.AddGame(820); fake.Priority[820] = (int)ProcessPriorityClass.High;
            Write(fake, Saved(game)); var engine = new TourneyEngine(fake.Directory, fake, () => []);
            bool denied = false; try { await engine.RestoreAsync(); } catch (InvalidOperationException) { denied = true; }
            Check(denied && engine.StopRequested && !engine.IsModeRunning && engine.IsActive && File.Exists(fake.Journal), "Access denial bypassed protection or mislabeled mode as running.");
            await engine.MaintainAsync(); Check(fake.WatchedScans == 0, "Guard ran after recovery denial.");
        });
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Canceled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("The superseded activation was not canceled.");
    }
    private static AppOptions Options() => new() { OptionsVersion = 1, SilenceNotifications = false, HighPriority = false, HighPerformancePower = false, GameTuning = false, CloseCodex = false };
    private static PcAnalysis Report() => new() { AcPower = true, ActivePowerPlan = StopSystem.OriginalPlan, Hardware = new() { Cpu = "Synthetic desktop", Cores = 8, Threads = 16, IsPortable = false } };
    private static SessionState Saved(ProcessIdentity game) => new() { Phase = "Active", Options = new() { OptionsVersion = 9, KeepAppsClosed = true, Intent = new() { Fps = new() { QuietDuringGame = true } } },
        Priorities = [new() { Process = game, Original = (int)ProcessPriorityClass.Normal, Requested = (int)ProcessPriorityClass.High }], Notifications = [new() { Key = "Fixture", Name = "Toast", Existed = true, Value = 1 }] };
    private static void Write(StopSystem system, SessionState state) { Directory.CreateDirectory(system.Directory); File.WriteAllText(system.Journal, JsonSerializer.Serialize(state)); }

    private sealed class StopSystem : ISystemFacade
    {
        public const string OriginalPlan = "00000000-0000-0000-0000-000000000001";
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "TourneyMode-Stop-Fixtures", Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(Directory, "session.json");
        public bool AllowLegacyFixtureActivation => true;
        public readonly List<ProcessIdentity> Processes = [];
        public readonly Dictionary<int, int> Priority = [];
        public bool GamePresent, Dnd, ToastSuppressed, DenyNormalPriority;
        public int Mutations, StartupEnables, Suppressions, Closes, Kills, Launches, CloneCreates, CloneActivations, DeletedPlans, PriorityWrites, AdapterRestores, IniRestores, DndWrites, CodexRestores, WatchedScans;
        public Action? AfterDndWrite, AfterClose, AfterClone, AfterPriorityWrite;
        public TaskCompletionSource<bool> RestoreCodexEntered = Signal();
        public TaskCompletionSource<bool>? RestoreCodexRelease;
        private void Mutation() { Check(File.Exists(Journal), "Mutation occurred before the recovery journal."); Mutations++; }
        public ProcessIdentity AddGame(int id) { var game = new ProcessIdentity { Id = id, Name = "FortniteClient-Win64-Shipping.exe", Path = @"C:\Fixture\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", StartTicks = id, SessionId = 1 }; Processes.Add(game); Priority[id] = (int)ProcessPriorityClass.Normal; return game; }
        public AppCandidate App() { var first = new ProcessIdentity { Id = 1, Name = "chrome.exe", Path = @"C:\Fixture\chrome.exe", StartTicks = 1, SessionId = 1 }; var second = new ProcessIdentity { Id = 2, Name = first.Name, Path = first.Path, ParentId = 1, StartTicks = 2, SessionId = 1 }; Processes.AddRange([first, second]); return new() { Id = "fixture-chrome", Name = "Chrome fixture", LaunchPath = first.Path, Processes = [first, second] }; }
        public List<ProcessIdentity> Snapshot() => Processes.Where(Alive).ToList();
        public List<ProcessIdentity> Games() => GamePresent ? Processes.Where(ProcessPolicy.IsFortnite).ToList() : [];
        public GamePresence GetGamePresence() => GamePresent ? GamePresence.Present : GamePresence.Absent;
        public List<ProcessIdentity> WatchedProcesses(List<AppCandidate> apps) { WatchedScans++; return Snapshot(); }
        public bool Alive(ProcessIdentity process) => Processes.Any(item => item.Id == process.Id && item.StartTicks == process.StartTicks) && (!ProcessPolicy.IsFortnite(process) || GamePresent);
        public bool CanTouch(ProcessIdentity process) => Alive(process);
        public void CloseWindow(ProcessIdentity process) { Mutation(); Closes++; AfterClose?.Invoke(); }
        public void Terminate(ProcessIdentity process) { Mutation(); Kills++; Processes.RemoveAll(item => item.Id == process.Id); }
        public int GetPriority(ProcessIdentity process) => Priority[process.Id];
        public void SetPriority(ProcessIdentity process, int priority) { Mutation(); if (DenyNormalPriority && priority == (int)ProcessPriorityClass.Normal) throw new UnauthorizedAccessException("Synthetic access denied"); PriorityWrites++; Priority[process.Id] = priority; AfterPriorityWrite?.Invoke(); }
        public bool Launch(AppCandidate app) { Mutation(); Launches++; return true; }
        public List<NotificationBackup> CaptureNotifications() => [new() { Key = "Fixture", Name = "Toast", Existed = true, Value = 1 }];
        public void SuppressNotifications() { Mutation(); Suppressions++; ToastSuppressed = true; }
        public void RestoreNotification(NotificationBackup item) { Mutation(); ToastSuppressed = false; }
        public string GetPowerScheme() => OriginalPlan;
        public void SetPowerScheme(string guid) { Mutation(); if (guid != OriginalPlan) CloneActivations++; }
        public bool? GetDnd() => Dnd;
        public void SetDnd(bool enabled) { Mutation(); DndWrites++; Dnd = enabled; AfterDndWrite?.Invoke(); }
        public List<NotificationBackup> CaptureTuning() => [];
        public void ApplyTuning() => Mutation();
        public void RestoreTuning(NotificationBackup item) => Mutation();
        public List<string> CreatePlan(string original, string destination, bool fortniteOnly) { Mutation(); CloneCreates++; AfterClone?.Invoke(); return []; }
        public void DeletePlan(string guid) { Mutation(); DeletedPlans++; }
        public Task StopNative() { Mutation(); return Task.CompletedTask; }
        public Task StartNative() { Mutation(); return Task.CompletedTask; }
        public bool NativeReady() => true;
        public bool MobileReady() => true;
        public bool AppReady(AppCandidate app) => true;
        public void SetRecoveryStartup(bool enabled) { if (enabled) { Mutation(); StartupEnables++; } }
        public Task Delay(int ms) => Task.CompletedTask;
        public CodexBackup CaptureCodex(IEnumerable<string> keep) => new();
        public Task StopCodex(CodexBackup backup, bool force, Action save, Action<string> log) { Mutation(); return Task.CompletedTask; }
        public async Task RestoreCodex(CodexBackup backup, Action save, Action<string> log) { Mutation(); CodexRestores++; RestoreCodexEntered.TrySetResult(true); if (RestoreCodexRelease != null) await RestoreCodexRelease.Task; }
        public void RestoreEthernetAdapter(EthernetAdapterBackup backup) { Mutation(); AdapterRestores++; }
        public void RestoreFortniteSettings(FortniteSettingsBackup backup, Action save) { Mutation(); IniRestores++; backup.Restored = true; save(); }
        public void CreateRestorePoint(SystemRestoreCheckpoint point) { Mutation(); point.Verified = true; point.SequenceNumber = 1; }
        public bool VerifyRestorePoint(SystemRestoreCheckpoint point) => point.Verified && point.SequenceNumber > 0;
        public void ApplyStrip(StripBackup backup, Action save, Action<string> log, CancellationToken token) { Mutation(); token.ThrowIfCancellationRequested(); backup.ApplyAttempted = true; foreach (var item in backup.Services) item.ApplyAttempted = true; save(); }
        public void RepairStrip(StripBackup backup, string? groupId, Action save, Action<string> log, CancellationToken token) { Mutation(); foreach (var item in backup.Services.Where(item => groupId == null || item.GroupId == groupId)) item.Restored = true; save(); }
    }
}
