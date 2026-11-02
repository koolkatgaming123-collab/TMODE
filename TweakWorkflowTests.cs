using System.Diagnostics;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Synthetic analysis and a journal-checking facade only. Never queries or tunes this PC.</summary>
internal static class TweakWorkflowTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        await test("tweak preserves unknown hardware and unavailable AC power", () =>
        {
            foreach (bool? ac in new bool?[] { false, null })
            foreach (var requested in new[] { PowerPreference.Sustained, PowerPreference.Maximum })
            {
                var report = Desktop(); report.AcPower = ac;
                var options = Options(); options.Intent.Power = requested; options.Intent.AllowHigherHeat = true;
                var assessment = Assess(report, options);
                Check(!assessment.Options.HighPerformancePower && assessment.Plan.EffectivePower == PowerPreference.Original,
                    "Battery/unknown AC enabled a power override.");
            }
            var unknown = Desktop(); unknown.Hardware = new();
            var maximum = Options(); maximum.Intent.Power = PowerPreference.Maximum; maximum.Intent.AllowHigherHeat = true;
            Check(!Assess(unknown, maximum).Options.HighPerformancePower, "Unknown hardware received maximum power.");
            var batteryDesktop = Desktop(); batteryDesktop.Hardware.HasBattery = true;
            Check(!Assess(batteryDesktop, maximum).Options.HighPerformancePower, "A battery-equipped PC received desktop maximum power.");
            return Task.CompletedTask;
        });
        await test("tweak uses explicit desktop power preferences without inventing a frame cap", () =>
        {
            foreach (var goal in Enum.GetValues<OptimizationGoal>())
            {
                var options = Options(); options.Intent.Goal = goal;
                var assessment = Assess(Desktop(), options);
                Check(assessment.Plan.EffectivePower == PowerPreference.Original && !assessment.Options.HighPerformancePower,
                    "Automatic power was promoted to a maximum-performance request.");
                Check(assessment.Options.Intent.Fps.FrameCap == FortniteFrameCap.KeepCurrent && assessment.Options.Intent.TargetFps == 0,
                    "Static inventory invented a sustainable endgame cap.");
            }
            var sustained = Options(); sustained.Intent.Power = PowerPreference.Sustained;
            Check(Assess(Desktop(), sustained).Plan.EffectivePower == PowerPreference.Sustained, "Explicit sustained power was lost.");
            var maximum = Options(); maximum.Intent.Power = PowerPreference.Maximum;
            Check(!Assess(Desktop(), maximum).Options.HighPerformancePower, "Maximum power bypassed higher-heat consent.");
            maximum.Intent.AllowHigherHeat = true;
            var accepted = Assess(Desktop(), maximum);
            Check(accepted.Plan.EffectivePower == PowerPreference.Maximum && accepted.Options.HighPerformancePower,
                "An eligible explicit desktop experiment was lost.");
            return Task.CompletedTask;
        });
        await test("tweak clears destructive and recurring operations while preserving the caller", () =>
        {
            var options = Options();
            options.CloseCodex = options.FortniteOnly = options.MaximumHeadroom = options.KeepAppsClosed = true;
            options.PauseOptionalServices = options.TuneEthernet = options.PrioritizeNetwork = options.ForceClose = true;
            options.Intent.AllowNetworkExperiment = true; options.Intent.MatchSafe = false;
            options.Intent.Fps.QuietDuringGame = false; options.Intent.Fps.BackgroundAppPaths = [@"C:\FixtureApps\chrome.exe"];
            options.SelectedApps = ["chrome|fixture", "native-mobile", "bridge|fixture"]; options.ExcludedApps = ["fixture-keep"];
            string original = Serialize(options);
            var assessment = Assess(Desktop(), options); var effective = assessment.Options;
            Check(!effective.CloseCodex && !effective.MaximumHeadroom && !effective.KeepAppsClosed && !effective.PauseOptionalServices
                && !effective.TuneEthernet && !effective.PrioritizeNetwork && !effective.ForceClose && effective.SelectedApps.Count == 0,
                "The Tweak flow retained app, service, task, Native/Codex or network mutation switches.");
            Check(effective.Intent.MatchSafe && effective.Intent.Fps.QuietDuringGame && effective.Intent.Fps.BackgroundAppPaths.Count == 0,
                "A recurring game-time governor survived Tweak normalization.");
            Check(Serialize(options) == original, "Read-only policy changed caller preferences.");
            return Task.CompletedTask;
        });
        await test("tweak only enables Game Mode and capture work for observed differences", () =>
        {
            foreach (int? gameMode in new int?[] { null, 0, 1 })
            foreach (int? capture in new int?[] { null, 0, 1 })
            {
                var report = Desktop(); report.GameMode = gameMode; report.BackgroundCapture = capture;
                var assessment = Assess(report, Options());
                Check(assessment.Options.GameTuning == (gameMode == 0 || capture == 1),
                    "Unknown or already-matching Game Mode/capture state produced an automatic write.");
                var expected = new List<string>();
                if (gameMode == 0) expected.Add("game-mode");
                if (capture == 1) expected.Add("capture");
                Check(assessment.Options.TweakWindowsSettings.SequenceEqual(expected), "A settings group without observed differences was selected.");
            }
            return Task.CompletedTask;
        });
        await test("tweak streaming and recording keep capture preferences", () =>
        {
            foreach (var activity in new[] { SessionActivity.Streaming, SessionActivity.Recording })
            {
                var report = Desktop(); report.GameMode = 1; report.BackgroundCapture = 1;
                var options = Options(); options.Intent.Activity = activity;
                var assessment = Assess(report, options);
                Check(!assessment.Options.GameTuning && assessment.Options.Intent.Activity == activity,
                    "A stream/record session disabled an existing capture preference.");
                report.GameMode = 0;
                Check(Assess(report, options).Options.GameTuning, "A separate observed Game Mode fix was incorrectly dropped.");
            }
            return Task.CompletedTask;
        });
        await test("tweak game profile requires consent, supported differences and a valid original hash", () =>
        {
            var report = Desktop(); report.GameSettingsFingerprint = new string('A', 64); report.GameSettingsChanges = ["sg.ShadowQuality: 3 -> 0"];
            var options = Options();
            Check(!Assess(report, options).Options.Intent.Fps.ApplyGameProfile, "Detected game differences bypassed profile consent.");
            options.Intent.Fps.ApplyGameProfile = true;
            Check(Assess(report, options).Options.Intent.Fps.ApplyGameProfile, "An explicit supported game profile was lost.");
            report.GameSettingsChanges.Clear();
            Check(!Assess(report, options).Options.Intent.Fps.ApplyGameProfile, "A profile without changes was treated as automatic work.");
            report.GameSettingsChanges.Add("sg.ShadowQuality: 3 -> 0");
            foreach (string invalid in new[] { "Unavailable", "", new string('G', 64), new string('A', 63) })
            {
                report.GameSettingsFingerprint = invalid;
                Check(!Assess(report, options).Options.Intent.Fps.ApplyGameProfile, "An invalid original game hash enabled profile writes.");
            }
            return Task.CompletedTask;
        });
        await test("tweak blocks game-present and pending-recovery assessments", () =>
        {
            Check(!TweakPolicy.Create(Desktop(), Options(), true, false).CanApply, "A running game allowed Tweak writes.");
            Check(!TweakPolicy.Create(Desktop(), Options(), false, true).CanApply, "Pending recovery allowed a new Tweak session.");
            Check(!TweakPolicy.Create(Desktop(), Options(), true, true).CanApply, "Combined game/recovery blockers were bypassed.");
            return Task.CompletedTask;
        });
        await test("tweak approval expires and rejects edited findings, choices and analysis", () =>
        {
            var assessment = Assess(Desktop(), Options());
            TweakPolicy.ValidateApproval(assessment, assessment.CreatedUtc.AddMinutes(9));
            Expect<InvalidOperationException>(() => TweakPolicy.ValidateApproval(assessment, assessment.CreatedUtc.AddMinutes(11)));
            Expect<InvalidOperationException>(() => TweakPolicy.ValidateApproval(assessment, assessment.CreatedUtc.AddMinutes(-2)));
            foreach (Action<TweakAssessment> edit in new Action<TweakAssessment>[]
            {
                a => a.Options.Intent.Fps.FrameCap = FortniteFrameCap.Uncapped,
                a => a.Options.SelectedApps.Add("chrome|unreviewed"),
                a => a.Analysis.BackgroundCapture = 0,
                a => a.Analysis.GameSettingsFingerprint = new string('B', 64),
                a => a.Findings[0].ProposedAction = "Unreviewed action",
                a => a.Plan.EffectivePower = PowerPreference.Maximum,
                a => a.ApprovalFingerprint = ""
            })
            {
                var changed = Clone(assessment); edit(changed);
                Expect<InvalidOperationException>(() => TweakPolicy.ValidateApproval(changed, assessment.CreatedUtc));
            }
            return Task.CompletedTask;
        });
        await test("tweak analysis and decline leave all settings and recovery untouched", async () =>
        {
            using var fixture = new WorkflowFixture();
            var options = Options(); options.SelectedApps = ["chrome|fixture"]; options.CloseCodex = true;
            string original = Serialize(options);
            var review = await fixture.Workflow.AnalyzeAsync(options);
            Check(review.CanApply && review.Findings.Any(f => f.Automated), "The fixture did not reach a reviewable proposed change.");
            // Decline means the caller does not invoke ApplyAsync.
            Check(fixture.System.Writes == 0 && fixture.System.ForbiddenWrites == 0 && !fixture.Engine.IsActive && !File.Exists(fixture.Journal),
                "Read-only analysis/decline wrote settings, closed an app or created recovery.");
            Check(Serialize(options) == original, "Analysis changed saved caller preferences.");
        });
        await test("tweak Present and Unknown game presence block apply with zero writes", async () =>
        {
            foreach (var presence in new[] { GamePresence.Present, GamePresence.Unknown })
            {
                using var fixture = new WorkflowFixture(); fixture.System.Presence = presence;
                var review = await fixture.Workflow.AnalyzeAsync(Options());
                Check(!review.CanApply && review.GameRunning, "Unproven game absence allowed a reviewable activation.");
                await ExpectAsync<InvalidOperationException>(() => fixture.Workflow.ApplyAsync(review));
                Check(fixture.System.Writes == 0 && !fixture.Engine.IsActive && !File.Exists(fixture.Journal), "Blocked game state caused writes.");
            }
        });
        await test("tweak game launch after review is rejected before any mutation", async () =>
        {
            using var fixture = new WorkflowFixture();
            var review = await fixture.Workflow.AnalyzeAsync(Options()); fixture.System.Presence = GamePresence.Present;
            await ExpectAsync<InvalidOperationException>(() => fixture.Workflow.ApplyAsync(review));
            Check(fixture.System.Writes == 0 && !File.Exists(fixture.Journal), "A late game launch was ignored.");
        });
        await test("tweak pending recovery preserves the original journal and performs no writes", async () =>
        {
            using var fixture = new WorkflowFixture(new SessionState { Phase = "Needs recovery", Workflow = "Tourney", Options = Options() });
            string original = File.ReadAllText(fixture.Journal);
            var review = await fixture.Workflow.AnalyzeAsync(Options());
            Check(review.RecoveryPending && !review.CanApply, "Existing recovery did not block the workflow.");
            await ExpectAsync<InvalidOperationException>(() => fixture.Workflow.ApplyAsync(review));
            Check(fixture.System.Writes == 0 && File.ReadAllText(fixture.Journal) == original, "A blocked Tweak run replaced existing recovery.");
        });
        await test("tweak changed hardware or Windows observations require a fresh review", async () =>
        {
            foreach (Action<PcAnalysis> edit in new Action<PcAnalysis>[]
            {
                r => r.Hardware.Cpu = "Changed fixture CPU",
                r => r.ActivePowerPlan = "22222222-2222-2222-2222-222222222222",
                r => r.GameMode = 1,
                r => r.BackgroundCapture = 0
            })
            {
                using var fixture = new WorkflowFixture();
                var review = await fixture.Workflow.AnalyzeAsync(Options()); edit(fixture.Report);
                await ExpectAsync<InvalidOperationException>(() => fixture.Workflow.ApplyAsync(review));
                Check(fixture.System.Writes == 0 && !File.Exists(fixture.Journal), "Changed inventory/settings reused stale approval.");
            }
        });
        await test("tweak explicit cancellation before apply causes zero writes", async () =>
        {
            using var fixture = new WorkflowFixture(); var review = await fixture.Workflow.AnalyzeAsync(Options());
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => fixture.Workflow.ApplyAsync(review, canceled.Token));
            Check(fixture.System.Writes == 0 && !File.Exists(fixture.Journal), "A canceled Tweak apply created a session.");
        });
        await test("tweak cancellation during fresh revalidation stops before settings writes", async () =>
        {
            using var fixture = new WorkflowFixture(); var review = await fixture.Workflow.AnalyzeAsync(Options());
            using var canceled = new CancellationTokenSource();
            fixture.OnAnalysis = _ => canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => fixture.Workflow.ApplyAsync(review, canceled.Token));
            Check(fixture.System.TuningCalls == 0 && fixture.System.ForbiddenWrites == 0,
                "Cancellation during revalidation was cleared before settings application.");
        });
        await test("tweak a client appearing during settings capture blocks the settings write", async () =>
        {
            foreach (var presence in new[] { GamePresence.Present, GamePresence.Unknown })
            {
                using var fixture = new WorkflowFixture(); var review = await fixture.Workflow.AnalyzeAsync(Options());
                fixture.System.BeforeCaptureTuning = () => fixture.System.Presence = presence;
                await ExpectAsync<InvalidOperationException>(() => fixture.Workflow.ApplyAsync(review));
                Check(fixture.System.TuningCalls == 0 && fixture.Engine.IsActive && File.Exists(fixture.Journal),
                    "The settings writer ignored lost game-absence evidence or lost its recovery journal.");
            }
        });
        await test("tweak cancellation during inactive power preparation never switches the active plan", async () =>
        {
            using var fixture = new WorkflowFixture(); fixture.Report.GameMode = 1; fixture.Report.BackgroundCapture = 0;
            var options = Options(); options.Intent.Power = PowerPreference.Sustained;
            var review = await fixture.Workflow.AnalyzeAsync(options);
            using var canceled = new CancellationTokenSource(); fixture.System.AfterCreatePlan = canceled.Cancel;
            await ExpectAsync<OperationCanceledException>(() => fixture.Workflow.ApplyAsync(review, canceled.Token));
            Check(fixture.System.Power == FixtureSystem.OriginalPower && fixture.Engine.IsActive && File.Exists(fixture.Journal),
                "A canceled inactive-plan preparation switched power or discarded clone recovery.");
            await fixture.Engine.RestoreAsync();
            Check(!fixture.Engine.IsActive && !File.Exists(fixture.Journal), "Cancellation stranded inactive-plan cleanup.");
        });
        await test("tweak apply uses the shared journal and restore undoes only its recorded settings", async () =>
        {
            using var fixture = new WorkflowFixture();
            var review = await fixture.Workflow.AnalyzeAsync(Options());
            var result = await fixture.Workflow.ApplyAsync(review);
            Check(result.Success && !result.NeedsRecovery && fixture.Engine.State?.Workflow == "Tweak" && fixture.System.TuningActive,
                "Tweak did not produce an active, identified recoverable session.");
            var journal = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(fixture.Journal))!;
            Check(journal.Workflow == "Tweak" && journal.Tuning.Count > 0 && journal.Apps.Count == 0 && journal.Codex is null
                && journal.Services.Count == 0 && journal.ScheduledTasks.Count == 0 && journal.BackgroundPriorities.Count == 0
                && journal.EthernetAdapter is null && journal.NetworkPriority is null && journal.Priorities.Count == 0,
                "The Tweak journal includes work outside the reviewed safe scope.");
            await fixture.Engine.RestoreAsync();
            Check(!fixture.Engine.IsActive && !File.Exists(fixture.Journal) && !fixture.System.TuningActive && fixture.System.TuningRestores > 0,
                "Restoring Tweak left settings or an active recovery session behind.");
            Check(fixture.System.ForbiddenWrites == 0 && fixture.System.Power == FixtureSystem.OriginalPower,
                "Tweak touched applications, Native, optional tasks/services, NICs, priorities or an unrequested power plan.");
        });
        await test("tweak streaming apply excludes capture values from both mutation and recovery", async () =>
        {
            foreach (var activity in new[] { SessionActivity.Streaming, SessionActivity.Recording })
            {
                using var fixture = new WorkflowFixture(); var options = Options(); options.Intent.Activity = activity;
                var review = await fixture.Workflow.AnalyzeAsync(options); await fixture.Workflow.ApplyAsync(review);
                Check(fixture.System.PreserveCapture == true && fixture.Engine.State!.Tuning.All(value => value.Name is not "AppCaptureEnabled" and not "GameDVR_Enabled"),
                    "Streaming apply did not preserve capture values and originals.");
                await fixture.Engine.RestoreAsync();
            }
        });
        await test("tweak selected Windows fixes leave unknown or matching values out of writes and recovery", async () =>
        {
            foreach (var states in new (int? Game, int? Capture)[] { (null, 1), (0, null), (0, 0), (1, 1) })
            {
                using var fixture = new WorkflowFixture(); fixture.Report.GameMode = states.Game; fixture.Report.BackgroundCapture = states.Capture;
                var review = await fixture.Workflow.AnalyzeAsync(Options()); await fixture.Workflow.ApplyAsync(review);
                var expected = new List<string>();
                if (states.Game == 0) expected.Add("AutoGameModeEnabled");
                if (states.Capture == 1) expected.AddRange(["AppCaptureEnabled", "GameDVR_Enabled"]);
                Check(fixture.System.AppliedNames.SequenceEqual(expected) && fixture.Engine.State!.Tuning.Select(t => t.Name).SequenceEqual(expected),
                    "An unknown or matching setting was written or journaled for restore.");
                await fixture.Engine.RestoreAsync();
                Check(fixture.System.RestoredNames.SequenceEqual(expected), "Restoration wrote an unselected setting.");
            }
        });
        await test("tweak partial apply and failed restore retain originals until recovery succeeds", async () =>
        {
            using var fixture = new WorkflowFixture(); fixture.System.FailTuning = true;
            var review = await fixture.Workflow.AnalyzeAsync(Options());
            await ExpectAsync<IOException>(() => fixture.Workflow.ApplyAsync(review));
            Check(fixture.Engine.State?.Phase == "Needs recovery" && fixture.System.TuningActive && File.Exists(fixture.Journal),
                "Partial application discarded recoverability.");
            var saved = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(fixture.Journal))!;
            Check(saved.Workflow == "Tweak" && saved.Tuning.Any(t => t.Name == "AutoGameModeEnabled" && t.Existed && t.Value == 0),
                "The failed apply did not retain recorded original settings.");
            fixture.System.FailRestore = true;
            await ExpectAsync<InvalidOperationException>(fixture.Engine.RestoreAsync);
            Check(fixture.Engine.IsActive && File.Exists(fixture.Journal), "Failed rollback deleted recovery evidence.");
            fixture.System.FailRestore = false;
            var reopened = new TourneyEngine(fixture.Directory, fixture.System, () => [], _ => Task.FromResult(Clone(fixture.Report)));
            await reopened.RestoreAsync();
            Check(!reopened.IsActive && !fixture.System.TuningActive && !File.Exists(fixture.Journal) && fixture.System.ForbiddenWrites == 0,
                "Durable Tweak recovery could not finish after a controller restart.");
        });
    }

    private static TweakAssessment Assess(PcAnalysis report, AppOptions options) => TweakPolicy.Create(report, options, false, false);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Serialize(value))!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown.");
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown.");
    }
    private static PcAnalysis Desktop() => new()
    {
        CapturedUtc = DateTime.UtcNow, AcPower = true, ActivePowerPlan = FixtureSystem.OriginalPower,
        AvailableRamGiB = 16, GameMode = 0, BackgroundCapture = 1,
        Hardware = new PcFacts
        {
            Cpu = "Intel Core i7-13700K", Cores = 16, Threads = 24, RamGiB = 32, HasBattery = false, IsPortable = false,
            Gpus = [new() { Name = "NVIDIA GeForce RTX 4070", Driver = "fixture-driver" }]
        },
        Displays = [new() { Primary = true, Width = 1920, Height = 1080, RefreshHz = 240 }]
    };
    private static AppOptions Options() => new()
    {
        OptionsVersion = 9, Intent = new() { Power = PowerPreference.Automatic, Fps = new() { FrameCap = FortniteFrameCap.KeepCurrent } },
        HighPriority = false, HighPerformancePower = false, GameTuning = false, SilenceNotifications = false, ForceClose = false
    };

    private sealed class WorkflowFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "TourneyTweakFixtures-" + Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(Directory, "session.json");
        public PcAnalysis Report { get; } = Desktop();
        public FixtureSystem System { get; }
        public TourneyEngine Engine { get; }
        public TweakWorkflow Workflow { get; }
        public Action<int>? OnAnalysis;
        private int analysisCalls;
        public WorkflowFixture(SessionState? recovery = null)
        {
            System = new() { Directory = Directory };
            global::System.IO.Directory.CreateDirectory(Directory);
            if (recovery is not null) File.WriteAllText(Journal, Serialize(recovery));
            Engine = new TourneyEngine(Directory, System, () => [], _ =>
            {
                OnAnalysis?.Invoke(++analysisCalls);
                return Task.FromResult(Clone(Report));
            });
            Workflow = new(Engine);
        }
        public void Dispose() => global::System.IO.Directory.Delete(Directory, true);
    }

    private sealed class FixtureSystem : ISystemFacade
    {
        public void CreateRestorePoint(SystemRestoreCheckpoint checkpoint)
        {
            Check(File.Exists(Path.Combine(Directory, "session.json")), "Restore point requested before the safety journal.");
            checkpoint.SequenceNumber = 1; checkpoint.Verified = true; checkpoint.CreatedUtc = DateTime.UtcNow;
        }
        public bool VerifyRestorePoint(SystemRestoreCheckpoint checkpoint) => checkpoint.Verified;
        public const string OriginalPower = "11111111-1111-1111-1111-111111111111";
        public string Directory = "";
        public GamePresence Presence;
        public int Writes, ForbiddenWrites, TuningCalls, TuningRestores;
        public bool FailTuning, FailRestore, TuningActive;
        public bool? PreserveCapture;
        public Action? BeforeCaptureTuning;
        public Action? AfterCreatePlan;
        public List<string> AppliedNames = [], RestoredNames = [];
        public string Power = OriginalPower;
        public bool AcPowerAvailable() => true;
        public GamePresence GetGamePresence() => Presence;
        public List<ProcessIdentity> Snapshot() => [];
        public List<ProcessIdentity> Games() => [];
        public List<ProcessIdentity> WatchedProcesses(List<AppCandidate> apps) => [];
        public bool Alive(ProcessIdentity p) => false;
        public bool CanTouch(ProcessIdentity p) => false;
        public void CloseWindow(ProcessIdentity p) => Forbidden();
        public void Terminate(ProcessIdentity p) => Forbidden();
        public int GetPriority(ProcessIdentity p) => (int)ProcessPriorityClass.Normal;
        public void SetPriority(ProcessIdentity p, int priority) => Forbidden();
        public bool Launch(AppCandidate app) { Forbidden(); return false; }
        public List<NotificationBackup> CaptureNotifications() => [];
        public void SuppressNotifications() => Write();
        public void RestoreNotification(NotificationBackup item) => Write();
        public string GetPowerScheme() => Power;
        public void SetPowerScheme(string guid) { Write(); Power = guid; }
        public bool? GetDnd() => false;
        public void SetDnd(bool enabled) => Write();
        public List<NotificationBackup> CaptureTuning()
        {
            BeforeCaptureTuning?.Invoke();
            return
            [
                new() { Key = "fixture-game", Name = "AutoGameModeEnabled", Existed = true, Value = 0 },
                new() { Key = "fixture-capture", Name = "AppCaptureEnabled", Existed = true, Value = 1 },
                new() { Key = "fixture-capture", Name = "GameDVR_Enabled", Existed = true, Value = 1 }
            ];
        }
        public void ApplyTuning() { Write(); TuningCalls++; TuningActive = true; if (FailTuning) throw new IOException("Fixture partial write failure."); }
        public void ApplyGameTuning(bool preserveCapture) { PreserveCapture = preserveCapture; ApplyTuning(); }
        public void ApplyGameTuningSubset(List<NotificationBackup> values)
        {
            Check(values.All(value => value.Name is "AutoGameModeEnabled" or "AllowAutoGameMode" or "AppCaptureEnabled" or "GameDVR_Enabled"),
                "An unallowlisted setting reached the subset writer.");
            AppliedNames.AddRange(values.Select(value => value.Name));
            PreserveCapture = values.All(value => value.Name is not "AppCaptureEnabled" and not "GameDVR_Enabled");
            ApplyTuning();
        }
        public void RestoreTuning(NotificationBackup item) { Write(); TuningRestores++; if (FailRestore) throw new IOException("Fixture recovery failure."); RestoredNames.Add(item.Name); TuningActive = false; }
        public List<string> CreatePlan(string original, string destination, bool fortniteOnly) { Write(); AfterCreatePlan?.Invoke(); return []; }
        public void DeletePlan(string guid) => Write();
        public Task StopNative() { Forbidden(); return Task.CompletedTask; }
        public Task StartNative() { Forbidden(); return Task.CompletedTask; }
        public bool NativeReady() => true;
        public bool MobileReady() => true;
        public bool AppReady(AppCandidate app) => false;
        public void SetRecoveryStartup(bool enabled) { if (enabled) Write(); }
        public Task Delay(int ms) => Task.CompletedTask;
        public void StopOptionalService(ServiceBackup item) => Forbidden();
        public void DisableOptionalTask(ScheduledTaskBackup item) => Forbidden();
        public void ApplyEthernetPriority(NetworkPriorityBackup item) => Forbidden();
        public void ApplyEthernetAdapter(EthernetAdapterBackup item) => Forbidden();
        public void ApplyBackgroundPriorities(List<BackgroundPriorityBackup> backups, Action persist, Action<string> log) => Forbidden();
        public Task StopCodex(CodexBackup backup, bool force, Action save, Action<string> log) { Forbidden(); return Task.CompletedTask; }
        private void Forbidden() { ForbiddenWrites++; throw new InvalidOperationException("Tweak touched an operation outside its scope."); }
        private void Write()
        {
            Check(File.Exists(Path.Combine(Directory, "session.json")), "Mutation preceded the durable recovery journal.");
            Writes++;
        }
    }
}
