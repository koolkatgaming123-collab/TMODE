using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Engine transactions against synthetic registry/services/INI. No live OS tuning or checkpoint creation.</summary>
internal static class OptimizationSessionTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        await test("optimization creates and journals the mandatory checkpoint before registry, profile, INI and Strip writes", async () =>
        {
            using var fixture = new Fixture();
            await fixture.StartTweak(profile: true, ini: true);
            Check(fixture.System.ProfileApplies == 1 && fixture.System.IniApplies == 1 && fixture.System.TuningApplies == 1,
                "The fixture did not exercise all Tweak performance writers.");
            var reviewed = await fixture.Engine.CaptureStripAsync(["search"]);
            await fixture.Engine.EnableStripAsync(reviewed);
            Check(fixture.System.CheckpointCreates == 1 && fixture.System.PerformanceWrites > 3 && fixture.System.ForbiddenCalls == 0,
                "Strip recreated the checkpoint or reached an out-of-scope process mutation.");
            int point = fixture.System.Events.IndexOf("Checkpoint verified");
            Check(point >= 0 && fixture.System.Events.Where(value => value.StartsWith("Performance:", StringComparison.Ordinal))
                .All(value => fixture.System.Events.IndexOf(value) > point), "A performance write preceded the verified checkpoint.");
            await fixture.Engine.RestoreAsync();
            Check(!fixture.Engine.IsActive && !File.Exists(fixture.Journal), "Mixed recovery did not remove its finished journal.");
        });
        await test("optimization refused or unverified checkpoints allow zero performance writes and retain recovery", async () =>
        {
            foreach (int failure in new[] { 0, 1, 2 })
            {
                using var fixture = new Fixture(); fixture.System.ThrowCheckpoint = failure == 0;
                fixture.System.CheckpointVerified = failure != 1; fixture.System.CheckpointSequence = failure == 2 ? 0 : 23;
                await ExpectAny(() => fixture.StartTweak(profile: true, ini: true));
                Check(fixture.System.PerformanceWrites == 0 && fixture.Engine.State?.Phase == "Needs recovery" && File.Exists(fixture.Journal)
                    && fixture.Persisted().RestorePoint?.Attempted == true && fixture.System.ForbiddenCalls == 0,
                    "A refused/unverified safety point caused performance writes or discarded its recovery journal.");
            }
        });
        await test("Strip refused checkpoints retain the reviewed journal and leave services unchanged", async () =>
        {
            using var fixture = new Fixture(); var review = await fixture.Engine.CaptureStripAsync(["printing"]); fixture.System.ThrowCheckpoint = true;
            await Expect<IOException>(() => fixture.Engine.EnableStripAsync(review));
            Check(fixture.System.PerformanceWrites == 0 && fixture.System.ServiceStops == 0 && fixture.Engine.IsActive && File.Exists(fixture.Journal),
                "Standalone Strip changed services without its required checkpoint.");
        });
        await test("Strip uses exact reviewed originals and never silently recaptures changed services", async () =>
        {
            using var fixture = new Fixture(); var review = await fixture.Engine.CaptureStripAsync(["search"]);
            Check(review.Services.Single().OriginalStartType == 3, "Wrong fixture original.");
            fixture.System.Services.Rows["WSearch"].StartType = 2;
            await Expect<InvalidOperationException>(() => fixture.Engine.EnableStripAsync(review));
            Check(fixture.System.StripCaptures == 1 && fixture.System.ServiceStops == 0 && fixture.System.PerformanceWrites == 0
                && fixture.Engine.State?.Strip?.Services.Single().OriginalStartType == 3 && fixture.Persisted().Strip?.Services.Single().OriginalStartType == 3,
                "Core recaptured a changed service or lost the approved originals.");
        });
        await test("an active protected Tweak session appends Strip and reuses its safety checkpoint", async () =>
        {
            using var fixture = new Fixture(); await fixture.StartTweak();
            string originalPoint = fixture.Engine.State!.RestorePoint!.Description;
            var review = await fixture.Engine.CaptureStripAsync(["printing", "search"]);
            await fixture.Engine.EnableStripAsync(review);
            Check(fixture.Engine.State?.Workflow == "Tweak" && fixture.Engine.State.Phase == "Active" && fixture.System.CheckpointCreates == 1
                && fixture.System.CheckpointVerifications > 0 && fixture.Engine.State.RestorePoint?.Description == originalPoint
                && fixture.Engine.State.Strip?.HasChanges == true && fixture.System.ForbiddenCalls == 0,
                "Appending Strip replaced Tweak identity, recreated the point or lost its recorded changes.");
        });
        await test("active Tweak Strip append rejects unknown workflow, game presence and unverifiable points", async () =>
        {
            foreach (int blocked in Enumerable.Range(0, 5))
            {
                using var fixture = new Fixture(); await fixture.StartTweak();
                var review = await fixture.Engine.CaptureStripAsync(["search"]); int writes = fixture.System.PerformanceWrites;
                if (blocked == 0) fixture.Engine.State!.Workflow = "Unknown";
                if (blocked == 1) fixture.Engine.State!.RestorePoint!.Verified = false;
                if (blocked == 2) fixture.System.CheckpointExists = false;
                if (blocked == 3) fixture.System.Presence = GamePresence.Present;
                if (blocked == 4) fixture.System.Presence = GamePresence.Unknown;
                await Expect<InvalidOperationException>(() => fixture.Engine.EnableStripAsync(review));
                Check(fixture.System.PerformanceWrites == writes && fixture.System.ServiceStops == 0 && fixture.System.CheckpointCreates == 1
                    && File.Exists(fixture.Journal), "A blocked append changed services, recreated a checkpoint or discarded recovery.");
            }
        });
        await test("Fix Printing repairs only its group and standalone Repair all removes the journal", async () =>
        {
            using var fixture = new Fixture(); var review = await fixture.Engine.CaptureStripAsync(["printing", "search"]);
            await fixture.Engine.EnableStripAsync(review);
            string search = Serialize(fixture.Engine.State!.Strip!.Services.Single(value => value.Name == "WSearch"));
            await fixture.Engine.RepairStripAsync("printing");
            Check(fixture.System.Services.Rows["Spooler"].Running == true && fixture.System.Services.Rows["WSearch"].Running == false
                && fixture.System.Services.Rows["WSearch"].StartType == 4 && fixture.Engine.IsActive
                && Serialize(fixture.Engine.State!.Strip!.Services.Single(value => value.Name == "WSearch")) == search,
                "Fix Printing changed Search or ended a session that still needed repair.");
            await fixture.Engine.RepairStripAsync(null);
            Check(fixture.System.Services.Rows["WSearch"].Running == true && !fixture.Engine.IsActive && !File.Exists(fixture.Journal)
                && fixture.System.ForbiddenCalls == 0, "Standalone Repair all failed to finish the shared journal.");
        });
        await test("per-group repair cancellation keeps durable recovery for the remaining service state", async () =>
        {
            using var fixture = new Fixture(); var review = await fixture.Engine.CaptureStripAsync(["search"]); await fixture.Engine.EnableStripAsync(review);
            using var cancellation = new CancellationTokenSource();
            fixture.System.Services.AfterMutation = action => { if (action == "Config:WSearch:3") cancellation.Cancel(); };
            await Expect<OperationCanceledException>(() => fixture.Engine.RepairStripAsync("search", cancellation.Token));
            Check(fixture.Engine.State?.Phase == "Needs recovery" && File.Exists(fixture.Journal) && fixture.Persisted().Strip?.HasChanges == true
                && fixture.System.Services.Rows["WSearch"].Running == false, "Canceled repair lost originals or claimed full recovery.");
            fixture.System.Services.AfterMutation = null; await fixture.Engine.RepairStripAsync(null);
            Check(!fixture.Engine.IsActive && fixture.System.Services.Rows["WSearch"].Running == true, "Repair could not resume after cancellation.");
        });
        await test("Strip apply cancellation retains its session even when immediate group rollback succeeds", async () =>
        {
            using var fixture = new Fixture(); var review = await fixture.Engine.CaptureStripAsync(["search"]);
            using var cancellation = new CancellationTokenSource();
            fixture.System.Services.AfterMutation = action => { if (action == "Stop:WSearch") cancellation.Cancel(); };
            await Expect<OperationCanceledException>(() => fixture.Engine.EnableStripAsync(review, cancellation.Token));
            Check(fixture.Engine.State?.Phase == "Needs recovery" && File.Exists(fixture.Journal) && fixture.System.Services.Rows["WSearch"].Running == true,
                "Canceled application lost the recovery record or failed its immediate group rollback.");
        });
        await test("global restore combines profile, INI and Strip recovery without app or Native operations", async () =>
        {
            using var fixture = new Fixture(); await fixture.StartTweak(profile: true, ini: true);
            var review = await fixture.Engine.CaptureStripAsync(["printing", "search"]); await fixture.Engine.EnableStripAsync(review);
            await fixture.Engine.RestoreAsync();
            Check(fixture.System.ProfileRestores == 1 && fixture.System.IniRestores == 1 && fixture.System.StripRepairs.Contains("ALL")
                && fixture.System.Services.Rows["Spooler"].Running == true && fixture.System.Services.Rows["WSearch"].Running == true
                && !fixture.System.IniActive && !fixture.System.Profile.Data.Values.Any() && !fixture.Engine.IsActive
                && !File.Exists(fixture.Journal) && fixture.System.ForbiddenCalls == 0, "Global recovery did not combine the recorded optimization families.");
        });
        await test("a game appearing after checkpoint creation blocks every performance writer", async () =>
        {
            foreach (var presence in new[] { GamePresence.Present, GamePresence.Unknown })
            {
                using var fixture = new Fixture(); fixture.System.AfterCheckpoint = () => fixture.System.Presence = presence;
                await Expect<InvalidOperationException>(() => fixture.StartTweak(profile: true, ini: true));
                Check(fixture.System.PerformanceWrites == 0 && fixture.Engine.IsActive && File.Exists(fixture.Journal),
                    "Late game presence allowed performance writes or lost its checkpoint journal.");
            }
        });
        await test("Internet core verifies one checkpoint, journals each trial, and finish clears only restored originals", async () =>
        {
            using var fixture = new Fixture();
            var report = await fixture.Engine.CalibrateInternetAsync(NetworkOptions());
            Check(report.Completed && report.RollbackVerified && fixture.System.Network.Applies == 2
                && fixture.System.Network.Restores == 2 && !fixture.System.Network.Changed
                && fixture.System.CheckpointCreates == 1 && fixture.Engine.State?.Workflow == "Internet"
                && fixture.Persisted().InternetCalibration?.Restored == true && !fixture.Engine.InternetBusy,
                "Network trials lost shared checkpoint/journal scope or did not restore every repetition.");
            await fixture.Engine.RestoreInternetAsync();
            Check(!fixture.Engine.IsActive && !File.Exists(fixture.Journal) && fixture.System.ForbiddenCalls == 0,
                "Finishing a restored network report retained recovery or touched apps.");
        });
        await test("Internet core rejects a refused checkpoint before NIC discovery or probes", async () =>
        {
            using var fixture = new Fixture(); fixture.System.ThrowCheckpoint = true;
            await Expect<IOException>(() => fixture.Engine.CalibrateInternetAsync(NetworkOptions()));
            Check(fixture.System.Network.Captures == 0 && fixture.System.Network.Applies == 0
                && fixture.Engine.State?.Phase == "Needs recovery" && !fixture.Engine.InternetBusy && File.Exists(fixture.Journal),
                "A refused safety point allowed network work or lost the failed journal.");
        });
        await test("Internet restore cancels an active treatment and rollback ignores the canceled work token", async () =>
        {
            using var fixture = new Fixture();
            fixture.System.Network.DuringTreatment = () => fixture.Engine.RequestRestore();
            var report = await fixture.Engine.CalibrateInternetAsync(NetworkOptions());
            Check(report.Canceled && report.RollbackVerified && fixture.System.Network.Applies == 1
                && fixture.System.Network.Restores == 1 && !fixture.System.Network.Changed && !fixture.Engine.InternetBusy,
                "Cancellation prevented rollback or allowed another trial to mutate the fixture.");
            await fixture.Engine.RestoreInternetAsync();
            Check(!fixture.Engine.IsActive, "Canceled calibration could not finish shared recovery.");
        });
        await test("Internet restore supersedes a checkpoint in progress without starting NIC work", async () =>
        {
            using var fixture = new Fixture();
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resume = new ManualResetEventSlim();
            fixture.System.AfterCheckpoint = () => { reached.TrySetResult(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture checkpoint release missing."); };
            var pending = fixture.Engine.CalibrateInternetAsync(NetworkOptions());
            try
            {
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var recovery = fixture.Engine.RestoreInternetAsync(); resume.Set();
                await Expect<OperationCanceledException>(() => pending); await recovery;
            }
            finally { resume.Set(); }
            Check(!fixture.Engine.InternetBusy && !fixture.Engine.IsActive && fixture.System.Network.Captures == 0
                && fixture.System.Network.Applies == 0, "A restore request allowed delayed network work after checkpoint creation.");
        });
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Serialize(value))!;
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static InternetTestOptions NetworkOptions() => new() { MaximumCandidates = 1, Repetitions = 2, Targets = ["192.0.2.1"] };
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown."); }
    private static async Task ExpectAny(Func<Task> action)
    { try { await action(); } catch { return; } throw new InvalidOperationException("Expected activation to fail."); }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "TourneyCoreSafety-" + Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(Directory, "session.json");
        public StrictSystem System { get; }
        public TourneyEngine Engine { get; }
        public PcAnalysis Report { get; }
        public Fixture()
        {
            global::System.IO.Directory.CreateDirectory(Directory); System = new(Directory);
            Report = new()
            {
                AcPower = true, ActivePowerPlan = StrictSystem.OriginalPower, GameMode = 0, BackgroundCapture = 1,
                Hardware = new() { Cpu = "Intel Core i7-13700K", Cores = 16, Threads = 24, RamGiB = 32,
                    IsPortable = false, HasBattery = false, WindowsEdition = "Enterprise", DomainJoined = false,
                    Gpus = [new() { Name = "NVIDIA GeForce RTX 4070" }] },
                WindowsProfile = new() { Values = WindowsProfile.Capture(new PcFacts { WindowsEdition = "Enterprise", DomainJoined = false }, System.Profile)
                    .Values.Where(value => value.Id == "transparency").ToList() },
                GameSettingsFingerprint = new string('A', 64), GameSettingsChanges = ["FrameRateLimit: 0 -> 240"]
            };
            Engine = new(Directory, System, () => [], _ => Task.FromResult(Clone(Report)));
        }
        public async Task StartTweak(bool profile = false, bool ini = false)
        {
            var options = new AppOptions { OptionsVersion = 9, HighPerformancePower = false, HighPriority = false, SilenceNotifications = false,
                ForceClose = false, ApplyWindowsProfile = profile, Intent = new() { Fps = new() { ApplyGameProfile = ini, FrameCap = FortniteFrameCap.Target, TargetFps = 240 } } };
            var workflow = new TweakWorkflow(Engine); var assessment = await workflow.AnalyzeAsync(options); await workflow.ApplyAsync(assessment);
        }
        public SessionState Persisted() => JsonSerializer.Deserialize<SessionState>(File.ReadAllText(Journal))!;
        public void Dispose() => global::System.IO.Directory.Delete(Directory, true);
    }

    private sealed class StrictSystem : ISystemFacade
    {
        public const string OriginalPower = "11111111-1111-1111-1111-111111111111";
        private readonly string directory;
        public readonly List<string> Events = [], StripRepairs = [];
        public readonly ServiceStore Services;
        public readonly ProfileStore Profile;
        public readonly NetworkStore Network;
        public GamePresence Presence;
        public bool ThrowCheckpoint, CheckpointVerified = true, CheckpointExists = true, IniActive;
        public long CheckpointSequence = 23;
        public int CheckpointCreates, CheckpointVerifications, PerformanceWrites, ForbiddenCalls, StripCaptures, ServiceStops, ProfileApplies, ProfileRestores, IniApplies, IniRestores, TuningApplies;
        public Action? AfterCheckpoint;
        public StrictSystem(string directory) { this.directory = directory; Services = new(this); Profile = new(this); Network = new(this); }
        public INetworkTestSystem CreateInternetTestSystem() => Network;
        private SessionState Journal()
        { Check(File.Exists(Path.Combine(directory, "session.json")), "The operation preceded its durable session."); return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(Path.Combine(directory, "session.json")))!; }
        private void Performance(string action)
        {
            var state = Journal(); Check(state.RestorePoint is { Verified: true, SequenceNumber: > 0 } && CheckpointCreates > 0,
                "A performance write preceded its durable verified checkpoint.");
            Check(Presence == GamePresence.Absent, "A performance write ran while Fortnite presence was present or unknown.");
            PerformanceWrites++; Events.Add("Performance:" + action);
        }
        public void CreateRestorePoint(SystemRestoreCheckpoint point)
        {
            var saved = Journal().RestorePoint; Check(saved?.Attempted == true && saved.Description == point.Description && !saved.Verified,
                "Checkpoint creation preceded its exact request journal.");
            CheckpointCreates++; Events.Add("Checkpoint requested");
            if (ThrowCheckpoint) throw new IOException("Fixture checkpoint creation refused.");
            point.Verified = CheckpointVerified; point.SequenceNumber = CheckpointSequence; point.CreatedUtc = DateTime.UtcNow;
            if (point.Verified && point.SequenceNumber > 0) Events.Add("Checkpoint verified"); AfterCheckpoint?.Invoke();
        }
        public bool VerifyRestorePoint(SystemRestoreCheckpoint point) { CheckpointVerifications++; return CheckpointExists && point.Verified && point.SequenceNumber > 0; }
        public void ApplyWindowsProfile(WindowsProfileBackup backup, Action save, Func<bool> absent, CancellationToken token)
        { ProfileApplies++; WindowsProfile.Apply(backup, save, absent, token, Profile); }
        public void RestoreWindowsProfile(WindowsProfileBackup backup, Action save, Func<bool> absent)
        { ProfileRestores++; WindowsProfile.Restore(backup, save, absent, Profile); }
        public StripBackup CaptureStrip(IEnumerable<string> groups) { StripCaptures++; return WindowsStrip.Capture(groups, Services); }
        public void ApplyStrip(StripBackup backup, Action save, Action<string> log, CancellationToken token) => WindowsStrip.Apply(backup, save, log, Services, token);
        public void RepairStrip(StripBackup backup, string? id, Action save, Action<string> log, CancellationToken token)
        { StripRepairs.Add(id ?? "ALL"); WindowsStrip.Repair(backup, id, save, log, Services, token); }
        public FortniteSettingsBackup? CaptureFortniteSettings(FpsLatencyOptions options, bool nvidia, bool tearing) => new()
        {
            Exists = true, OriginalSHA256 = new string('A', 64), AppliedSHA256 = new string('B', 64),
            Changes = ["FrameRateLimit: 0 -> 240"], Edits = [new() { Key = "FrameRateLimit", OriginalValue = "0", AppliedValue = "240" }]
        };
        public void ApplyFortniteSettings(FortniteSettingsBackup backup, Action save)
        { backup.ApplyAttempted = true; save(); Check(Journal().FortniteSettings?.ApplyAttempted == true, "INI intent was not saved."); Performance("INI"); IniActive = true; IniApplies++; }
        public void RestoreFortniteSettings(FortniteSettingsBackup backup, Action save)
        { Performance("INI restore"); IniActive = false; backup.Restored = true; save(); IniRestores++; }
        public List<NotificationBackup> CaptureTuning() =>
        [new() { Key = "fixture-game", Name = "AutoGameModeEnabled", Existed = true, Value = 0 }, new() { Key = "fixture-capture", Name = "AppCaptureEnabled", Existed = true, Value = 1 }];
        public void ApplyGameTuningSubset(List<NotificationBackup> values)
        { Check(Journal().Tuning.Count == values.Count && values.Count > 0, "Registry originals were not journaled."); Performance("Gaming registry"); TuningApplies++; }
        public void ApplyTuning() => Forbidden();
        public void RestoreTuning(NotificationBackup item) => Performance("Gaming registry restore:" + item.Name);
        public GamePresence GetGamePresence() => Presence;
        public List<ProcessIdentity> Snapshot() => [];
        public List<ProcessIdentity> Games() => [];
        public List<ProcessIdentity> WatchedProcesses(List<AppCandidate> apps) => [];
        public bool Alive(ProcessIdentity process) => false;
        public bool CanTouch(ProcessIdentity process) => false;
        public void CloseWindow(ProcessIdentity process) => Forbidden();
        public void Terminate(ProcessIdentity process) => Forbidden();
        public int GetPriority(ProcessIdentity process) => (int)ProcessPriorityClass.Normal;
        public void SetPriority(ProcessIdentity process, int priority) => Forbidden();
        public bool Launch(AppCandidate app) { Forbidden(); return false; }
        public List<NotificationBackup> CaptureNotifications() => [];
        public void SuppressNotifications() => Forbidden();
        public void RestoreNotification(NotificationBackup value) => Forbidden();
        public string GetPowerScheme() => OriginalPower;
        public void SetPowerScheme(string guid) => Performance("Power switch");
        public bool? GetDnd() => false;
        public void SetDnd(bool enabled) => Forbidden();
        public List<string> CreatePlan(string original, string destination, bool fortniteOnly) { Performance("Power clone"); return []; }
        public void DeletePlan(string guid) => Performance("Power delete");
        public Task StopNative() { Forbidden(); return Task.CompletedTask; }
        public Task StartNative() { Forbidden(); return Task.CompletedTask; }
        public Task StopCodex(CodexBackup backup, bool force, Action save, Action<string> log) { Forbidden(); return Task.CompletedTask; }
        public Task RestoreCodex(CodexBackup backup, Action save, Action<string> log) { Forbidden(); return Task.CompletedTask; }
        public bool NativeReady() => true;
        public bool MobileReady() => true;
        public bool AppReady(AppCandidate app) => false;
        public void SetRecoveryStartup(bool enabled) { if (enabled) _ = Journal(); }
        public void SetRecoveryStartup(bool enabled, string workflow) => SetRecoveryStartup(enabled);
        public Task Delay(int ms) => Task.CompletedTask;
        public void StopOptionalService(ServiceBackup value) => Forbidden();
        public void DisableOptionalTask(ScheduledTaskBackup value) => Forbidden();
        public void ApplyEthernetPriority(NetworkPriorityBackup value) => Forbidden();
        public void ApplyEthernetAdapter(EthernetAdapterBackup value) => Forbidden();
        public void ApplyBackgroundPriorities(List<BackgroundPriorityBackup> values, Action save, Action<string> log) => Forbidden();
        public sealed class NetworkStore(StrictSystem owner) : INetworkTestSystem
        {
            public int Captures, Applies, Restores;
            public bool Changed;
            public Action? DuringTreatment;
            private DateTime cursor = DateTime.UtcNow.AddMinutes(-2);
            private readonly InternetNicSnapshot baseline = new()
            {
                AdapterGuid = "22222222-2222-2222-2222-222222222222", InterfaceIndex = 7, Name = "Fixture wired NIC",
                PnpDeviceId = "PCI\\FIXTURE", DriverVersion = "fixture-1", RouteSignature = "fixture-route-7", SettingsHash = new string('A', 64),
                GatewayTargets = ["192.0.2.1"], SourceAddresses = ["192.0.2.2"], InterruptModeration = true,
                Capabilities = new() { InterruptModerationSupported = true, InterruptModerationEnabledValue = "1", InterruptModerationDisabledValue = "0" },
                AdvancedProperties = [new() { Keyword = "*InterruptModeration", OriginalValues = ["1"], AdvertisedValues = ["0", "1"] }]
            };
            public void AssertOutsideGame() => Check(owner.Presence == GamePresence.Absent, "Synthetic NIC used during game presence.");
            public InternetNicSnapshot CaptureBaseline(InternetTestOptions options)
            {
                AssertOutsideGame(); Captures++;
                Check(owner.Journal().RestorePoint is { Verified: true, SequenceNumber: > 0 }, "NIC discovery preceded its verified checkpoint journal.");
                return Clone(baseline);
            }
            public void ApplyCandidate(InternetNicSnapshot original, InternetCandidate candidate)
            {
                AssertOutsideGame(); var saved = owner.Journal().InternetCalibration;
                Check(saved is { MutationAttempted: true, Restored: false } && saved.PendingCandidate?.Id == candidate.Id
                    && saved.Baseline.Fingerprint == original.Fingerprint, "NIC write preceded durable exact originals.");
                owner.Performance("NIC candidate"); Changed = true; Applies++;
            }
            public void RestoreBaseline(InternetCalibrationState state)
            {
                AssertOutsideGame(); Check(owner.Journal().InternetCalibration?.MutationAttempted == true, "NIC restore lacked its journal.");
                owner.Performance("NIC restore"); Changed = false; Restores++;
            }
            public bool VerifyBaseline(InternetNicSnapshot original) => !Changed;
            public Task<InternetMeasurement> MeasureAsync(InternetNicSnapshot original, InternetTestOptions options, string label, CancellationToken token)
            {
                AssertOutsideGame(); if (label.EndsWith(" / B", StringComparison.Ordinal)) DuringTreatment?.Invoke(); token.ThrowIfCancellationRequested();
                var period = new InternetMeasurement { Label = label, StartedUtc = cursor, EndedUtc = cursor.AddSeconds(1), Valid = true,
                    Metrics = new() { MachineCpuPercent = 2 }, Endpoints = new(StringComparer.OrdinalIgnoreCase)
                    { ["192.0.2.1"] = new() { Sent = 12, Received = 12, PingMedianMs = 10, PingP95Ms = 12, JitterMs = 1, PacketLossPercent = 0 } } };
                cursor = cursor.AddSeconds(2); return Task.FromResult(period);
            }
        }
        private void Forbidden() { ForbiddenCalls++; throw new InvalidOperationException("An optimization-session fixture reached an out-of-scope process/network/service mutation."); }

        public sealed class ServiceStore : IStripSystem
        {
            private readonly StrictSystem owner;
            public readonly Dictionary<string, StripServiceSnapshot> Rows = WindowsStrip.Features.SelectMany(group => group.Services)
                .ToDictionary(name => name, name => new StripServiceSnapshot { Name = name, Exists = true, StartType = 3, DelayedAutoStart = false, Running = true }, StringComparer.OrdinalIgnoreCase);
            public Action<string>? AfterMutation;
            public ServiceStore(StrictSystem owner) => this.owner = owner;
            public GamePresence GetGamePresence() => owner.Presence;
            public IReadOnlyList<StripServiceSnapshot> ReadServices(IReadOnlyList<string> names) => names.Select(name => Clone(Rows[name])).ToList();
            public int? ActivePrintJobs() => 0;
            private void Before(string name, bool stop = false)
            {
                var saved = owner.Journal().Strip?.Services.SingleOrDefault(value => value.Name == name);
                Check(saved?.ApplyAttempted == true && !saved.Restored && (!stop || saved.StopAttempted), "Service mutation preceded its durable originals/attempt.");
                owner.Performance("Service:" + name);
            }
            public void SetStartup(string name, int start, bool delayed)
            { Before(name); Rows[name].StartType = start; Rows[name].DelayedAutoStart = delayed; AfterMutation?.Invoke("Config:" + name + ":" + start); }
            public void Stop(string name, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Before(name, true); Rows[name].Running = false; owner.ServiceStops++; AfterMutation?.Invoke("Stop:" + name); }
            public void Start(string name, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Before(name); Rows[name].Running = true; AfterMutation?.Invoke("Start:" + name); }
        }
        public sealed class ProfileStore : IWindowsProfileStore
        {
            private readonly StrictSystem owner;
            public readonly Dictionary<string, WindowsProfileStoredValue> Data = new(StringComparer.OrdinalIgnoreCase);
            public ProfileStore(StrictSystem owner) => this.owner = owner;
            public int WindowsBuild => 26200;
            public bool EdgeInstalled => true;
            private static string Address(string hive, string key, string name) => hive + "|" + key + "|" + name;
            public WindowsProfileStoredValue Read(string hive, string key, string name) => Data.TryGetValue(Address(hive, key, name), out var value) ? value : new();
            private void Before(string hive, string key, string name)
            {
                var saved = owner.Journal().WindowsProfile?.Values.SingleOrDefault(value => value.Hive == hive && value.Key == key && value.Name == name);
                Check(saved?.Attempted == true && !saved.Restored, "Windows profile write preceded its exact durable original/attempt."); owner.Performance("Windows profile:" + name);
            }
            public void Write(string hive, string key, string name, RegistryValueKind kind, string value)
            { Before(hive, key, name); Data[Address(hive, key, name)] = new() { Existed = true, Kind = kind, Value = value }; }
            public void Delete(string hive, string key, string name) { Before(hive, key, name); Data.Remove(Address(hive, key, name)); }
        }
    }
}
