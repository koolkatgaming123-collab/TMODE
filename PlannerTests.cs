using System.Text.Json;

namespace TourneyMode;

/// <summary>Pure inventory/options fixtures. These tests query no devices and perform no live optimization.</summary>
public static class PlannerTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        await test("planner preserves OEM policy on battery or unknown AC", () =>
        {
            foreach (bool? ac in new bool?[] { false, null })
            foreach (var requested in new[] { PowerPreference.Sustained, PowerPreference.Maximum })
            {
                var report = Desktop(); report.AcPower = ac;
                var options = Options(); options.Intent.Power = requested; options.Intent.AllowHigherHeat = true;
                var plan = Build(report, options);
                Check(plan.EffectivePower == PowerPreference.Original && FindAction(plan, "power").Disposition == PlanDisposition.Preserved,
                    "Unavailable AC caused a power override.");
                Check(!PerformancePlanner.Apply(plan, options).HighPerformancePower, "Applying a battery/unknown-AC plan enabled power overrides.");
            }
            return Task.CompletedTask;
        });
        await test("planner treats missing CPU facts as unknown and preserves OEM power", () =>
        {
            foreach (var hardware in new[] { new PcFacts(), new PcFacts { Cpu = "Unavailable", Cores = 8 }, new PcFacts { Cpu = "Fixture CPU", Cores = 0 } })
            {
                var report = Desktop(); report.Hardware = hardware;
                var options = Options(); options.Intent.Power = PowerPreference.Maximum; options.Intent.AllowHigherHeat = true;
                var plan = Build(report, options);
                Check(plan.EffectivePower == PowerPreference.Original && !PerformancePlanner.Apply(plan, options).HighPerformancePower,
                    "Incomplete inventory was treated as eligible desktop hardware.");
                Check(plan.Warnings.Count > 0, "The incomplete inventory had no visible limitation.");
            }
            return Task.CompletedTask;
        });
        await test("maximum power is ineligible on a battery-equipped laptop even on AC", () =>
        {
            var report = Desktop(); report.Hardware.HasBattery = true;
            var options = Options(); options.Intent.Power = PowerPreference.Maximum; options.Intent.AllowHigherHeat = true;
            var plan = Build(report, options);
            Check(plan.EffectivePower == PowerPreference.Original && !PerformancePlanner.Apply(plan, options).HighPerformancePower,
                "A laptop received the desktop maximum profile.");
            return Task.CompletedTask;
        });
        await test("unknown portability never becomes an assumed desktop maximum profile", () =>
        {
            var report = Desktop(); report.Hardware.IsPortable = null; report.Hardware.HasBattery = false;
            var options = Options(); options.Intent.Power = PowerPreference.Maximum; options.Intent.AllowHigherHeat = true;
            var plan = Build(report, options);
            Check(plan.EffectivePower == PowerPreference.Original && !PerformancePlanner.Apply(plan, options).HighPerformancePower,
                "Unknown portability was promoted to a known desktop maximum profile.");
            return Task.CompletedTask;
        });
        await test("desktop maximum power requires explicit higher-heat consent", () =>
        {
            var options = Options(); options.Intent.Power = PowerPreference.Maximum;
            var withoutConsent = Build(Desktop(), options);
            Check(withoutConsent.EffectivePower == PowerPreference.Original, "Maximum power was selected without heat consent.");
            options.Intent.AllowHigherHeat = true;
            var withConsent = Build(Desktop(), options);
            Check(withConsent.EffectivePower == PowerPreference.Maximum && FindAction(withConsent, "power").Disposition == PlanDisposition.Recommended,
                "An explicitly accepted, known desktop AC experiment could not be represented.");
            return Task.CompletedTask;
        });
        await test("AMD X3D planning preserves vendor parking and topology", () =>
        {
            var report = Desktop(); report.Hardware.Cpu = "AMD Ryzen 9 7950X3D"; report.Hardware.Cores = 16; report.Hardware.Threads = 32;
            report.Hardware.Gpus = [new() { Name = "AMD Radeon RX 7900 XTX", Driver = "fixture-amd-1" }];
            var options = Options(); options.Intent.Power = PowerPreference.Maximum; options.Intent.AllowHigherHeat = true;
            var plan = Build(report, options);
            Check(FindAction(plan, "scheduling").Disposition == PlanDisposition.Preserved,
                "A cache-domain-sensitive processor was offered a forced scheduler/parking change.");
            Check(FindAction(plan, "scheduling").Reason.Contains("parking", StringComparison.OrdinalIgnoreCase),
                "The preservation reason omitted the X3D parking constraint.");
            Check(FindAction(plan, "gpu-latency").Disposition == PlanDisposition.Advisory && !FindAction(plan, "gpu-latency").Reason.Contains("Reflex", StringComparison.OrdinalIgnoreCase),
                "An AMD GPU received NVIDIA-specific Reflex advice.");
            return Task.CompletedTask;
        });
        await test("match-safe running game defers NIC and disruptive power preparation", () =>
        {
            var options = Options(); options.Intent.Goal = OptimizationGoal.LowestLatency; options.Intent.AllowNetworkExperiment = true;
            options.Intent.Power = PowerPreference.Sustained; options.TuneEthernet = true; options.PrioritizeNetwork = true;
            var plan = PerformancePlanner.Build(Desktop(), options, gameRunning: true, recoveryPending: false);
            Check(FindAction(plan, "ethernet").Disposition == PlanDisposition.Deferred && FindAction(plan, "power").Disposition == PlanDisposition.Deferred,
                "Running-game preparation was not deferred.");
            var applied = PerformancePlanner.Apply(plan, options);
            Check(!applied.TuneEthernet && !applied.PrioritizeNetwork && !applied.HighPerformancePower && !applied.PauseOptionalServices,
                "Applying a match-safe plan retained a disruptive operation.");
            return Task.CompletedTask;
        });
        await test("network experiments require explicit intent and Lowest latency goal", () =>
        {
            var options = Options(); options.TuneEthernet = true; options.PrioritizeNetwork = true;
            var preserved = Build(Desktop(), options);
            Check(FindAction(preserved, "ethernet").Disposition == PlanDisposition.Preserved && !PerformancePlanner.Apply(preserved, options).TuneEthernet,
                "A stale all-ON NIC switch bypassed explicit network intent.");
            options.Intent.AllowNetworkExperiment = true;
            var wrongGoal = Build(Desktop(), options);
            Check(FindAction(wrongGoal, "ethernet").Disposition != PlanDisposition.Recommended,
                "A network experiment was recommended for an unrelated goal.");
            options.Intent.Goal = OptimizationGoal.LowestLatency;
            var eligible = Build(Desktop(), options);
            Check(FindAction(eligible, "ethernet").Disposition == PlanDisposition.Recommended,
                "An explicit pre-match supported network experiment was unavailable.");
            return Task.CompletedTask;
        });
        await test("saved recovery blocks applying a fresh planner result", () =>
        {
            var options = Options(); string before = Serialize(options);
            var plan = PerformancePlanner.Build(Desktop(), options, gameRunning: false, recoveryPending: true);
            Expect<InvalidOperationException>(() => PerformancePlanner.Apply(plan, options));
            Check(plan.RecoveryPending && options.ReviewedPlanFingerprint == "" && Serialize(options) == before,
                "A pending recovery was overwritten or source choices changed.");
            return Task.CompletedTask;
        });
        await test("streaming and recording plans keep capture software running without changing source selection", () =>
        {
            foreach (var activity in new[] { SessionActivity.Streaming, SessionActivity.Recording })
            {
                var options = Options(); options.Intent.Activity = activity;
                options.SelectedApps = ["obs|C:\\Apps\\obs64.exe", "streamlabs|C:\\Apps\\Streamlabs.exe", "chrome|C:\\Apps\\chrome.exe"];
                string before = Serialize(options);
                var plan = Build(Desktop(), options); var applied = PerformancePlanner.Apply(plan, options);
                Check(applied.SelectedApps.SequenceEqual(new[] { "chrome|C:\\Apps\\chrome.exe" }), "The capture app remained a close target.");
                Check(PerformancePlanner.ShouldPreserveApp(new AppCandidate { Id = "obs", Name = "OBS Studio", LaunchPath = @"C:\Apps\obs64.exe" }, applied),
                    "A capture app was not protected by the activity constraint.");
                Check(Serialize(options) == before, "Planner application rewrote the user's original capture selections.");
            }
            return Task.CompletedTask;
        });
        await test("planner Build and Apply do not mutate or alias caller options", () =>
        {
            var options = Options(); options.Intent.TargetFps = 1200;
            string before = Serialize(options); var plan = Build(Desktop(), options);
            Check(options.Intent.TargetFps == 1200 && plan.Intent.TargetFps == 1000 && Serialize(options) == before,
                "Intent validation mutated caller-owned options.");
            var applied = PerformancePlanner.Apply(plan, options);
            applied.Intent.TargetFps = 144; applied.SelectedApps.Add("new-choice"); applied.ProtectedAppPaths.Add(@"C:\Apps\AnotherKeep.exe");
            Check(Serialize(options) == before && plan.Intent.TargetFps == 1000, "Applied options shared mutable lists or intent with their source.");
            return Task.CompletedTask;
        });
        await test("every optimization goal produces measured-benefit disclaimers and advisory controls", () =>
        {
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var goal in Enum.GetValues<OptimizationGoal>())
            {
                var options = Options(); options.Intent.Goal = goal; options.Intent.TargetFps = 240;
                var plan = Build(Desktop(), options);
                Check(plan.Intent.Goal == goal && FindAction(plan, "frame-budget").Disposition == PlanDisposition.Advisory
                    && FindAction(plan, "gpu-latency").Disposition == PlanDisposition.Advisory && FindAction(plan, "diagnostics").Disposition == PlanDisposition.Advisory,
                    "The goal lost its advisory boundary or silently offered a live trace.");
                Check(plan.Summary.Contains("unmeasured", StringComparison.OrdinalIgnoreCase)
                    && plan.Actions.All(a => a.Confidence.Contains("unmeasured", StringComparison.OrdinalIgnoreCase)),
                    "The plan lacked its explicit unmeasured-benefit qualification.");
                fingerprints.Add(plan.InventoryFingerprint);
            }
            Check(fingerprints.Count == Enum.GetValues<OptimizationGoal>().Length, "Different goals shared a reviewed fingerprint.");
            return Task.CompletedTask;
        });
        await test("plan fingerprint changes with hardware, GPU driver, goal, game and keep constraints", () =>
        {
            var report = Desktop(); var options = Options(); string baseline = Build(report, options).InventoryFingerprint;
            void Different(PcAnalysis changedReport, AppOptions changedOptions, bool gameRunning = false)
                => Check(PerformancePlanner.Build(changedReport, changedOptions, gameRunning, false).InventoryFingerprint != baseline,
                    "A changed inventory or constraint retained an old reviewed fingerprint.");
            var hardware = Clone(report); hardware.Hardware.Cpu = "Fixture replacement CPU"; Different(hardware, options);
            var driver = Clone(report); driver.Hardware.Gpus[0].Driver = "fixture-driver-2"; Different(driver, options);
            var goal = Clone(options); goal.Intent.Goal = OptimizationGoal.HighestFps; Different(report, goal);
            Different(report, options, gameRunning: true);
            var keep = Clone(options); keep.ProtectedAppPaths.Add(@"C:\Apps\KeepAnother.exe"); Different(report, keep);
            var activity = Clone(options); activity.Intent.Activity = SessionActivity.Streaming; Different(report, activity);
            var target = Clone(options); target.Intent.TargetFps = 180; Different(report, target);
            return Task.CompletedTask;
        });
        await test("plan fingerprint binds active power plan and effective mutation choices", () =>
        {
            var report = Desktop(); var options = Options(); string baseline = Build(report, options).InventoryFingerprint;
            var power = Clone(report); power.ActivePowerPlan = "22222222-2222-2222-2222-222222222222";
            Check(Build(power, options).InventoryFingerprint != baseline, "A changed source power plan did not invalidate review.");
            var constraints = new Action<AppOptions>[]
            {
                o => o.ForceClose = false, o => o.CloseCodex = false, o => o.MaximumHeadroom = false,
                o => o.KeepAppsClosed = false, o => o.PauseOptionalServices = false,
                o => o.HighPriority = false, o => o.GameTuning = false, o => o.SilenceNotifications = false,
                o => o.AutoRestoreOnGameExit = false, o => o.FortniteOnly = false,
                o => o.SelectedApps.Add("new-close-target"), o => o.ExcludedApps.Add(o.SelectedApps[0])
            };
            foreach (var change in constraints)
            {
                var changed = Clone(options); change(changed);
                Check(Build(report, changed).InventoryFingerprint != baseline, "A changed mutation or app-selection constraint reused review.");
            }
            return Task.CompletedTask;
        });
        await test("canonical plan fingerprint survives Apply and ignores the stored review token itself", () =>
        {
            var report = Desktop(); var options = Options();
            options.HighPerformancePower = true; options.TuneEthernet = true; options.PrioritizeNetwork = true;
            var plan = Build(report, options); var applied = PerformancePlanner.Apply(plan, options);
            Check(Build(report, applied).InventoryFingerprint == plan.InventoryFingerprint,
                "Apply-derived switches invalidated their own newly reviewed plan.");
            var token = Clone(options); token.ReviewedPlanFingerprint = "unrelated-old-review-token";
            Check(Build(report, token).InventoryFingerprint == plan.InventoryFingerprint, "The fingerprint included its own persisted token.");
            var reordered = Clone(options); reordered.SelectedApps.Reverse(); reordered.ProtectedAppPaths.Reverse();
            Check(Build(report, reordered).InventoryFingerprint == plan.InventoryFingerprint, "Reordering equivalent selections invalidated review.");
            return Task.CompletedTask;
        });
        await test("app keep paths remain protected after a plan is copied and applied", () =>
        {
            var options = Options(); var plan = Build(Desktop(), options); var applied = PerformancePlanner.Apply(plan, options);
            Check(applied.ProtectedAppPaths.SequenceEqual(options.ProtectedAppPaths), "Applying a plan dropped a user's kept executable.");
            var snapshot = new List<ProcessIdentity>
            {
                new() { Id = 100, ParentId = 1, Path = options.ProtectedAppPaths[0], StartTicks = 100 },
                new() { Id = 200, ParentId = 100, Path = @"C:\Apps\MixerChild.exe", StartTicks = 200 },
                new() { Id = 300, ParentId = 1, Path = @"C:\Apps\Other.exe", StartTicks = 300 }
            };
            var kept = ConnectionProtection.ExtraIds(snapshot, applied.ProtectedAppPaths);
            Check(kept.SetEquals(new[] { 100, 200 }), "The applied keep constraint failed to protect its child or included an unrelated process.");
            return Task.CompletedTask;
        });
        await test("old OFF choices survive migration instead of returning to an all-ON preset", () =>
        {
            var old = Options(); old.OptionsVersion = 2;
            old.CloseCodex = old.FortniteOnly = old.MaximumHeadroom = old.KeepAppsClosed = old.PauseOptionalServices = false;
            old.HighPriority = old.ForceClose = old.GameTuning = old.SilenceNotifications = old.AutoRestoreOnGameExit = false;
            old.HighPerformancePower = old.TuneEthernet = old.PrioritizeNetwork = false;
            var keep = old.ProtectedAppPaths.ToArray(); var selected = old.SelectedApps.ToArray(); var excluded = old.ExcludedApps.ToArray();
            var migrated = ProfileDefaults.Upgrade(old);
            Check(!migrated.CloseCodex && !migrated.FortniteOnly && !migrated.MaximumHeadroom && !migrated.KeepAppsClosed && !migrated.PauseOptionalServices
                && !migrated.HighPriority && !migrated.ForceClose && !migrated.GameTuning && !migrated.SilenceNotifications && !migrated.AutoRestoreOnGameExit
                && !migrated.HighPerformancePower && !migrated.TuneEthernet && !migrated.PrioritizeNetwork,
                "Migration silently enabled an old OFF choice.");
            Check(migrated.ProtectedAppPaths.SequenceEqual(keep) && migrated.SelectedApps.SequenceEqual(selected) && migrated.ExcludedApps.SequenceEqual(excluded),
                "Migration lost app or keep selections.");
            return Task.CompletedTask;
        });
        await test("legacy all-ON network/power choices require a fresh reviewed plan", () =>
        {
            var old = Options(); old.OptionsVersion = 7; old.HighPerformancePower = old.TuneEthernet = old.PrioritizeNetwork = true;
            old.ReviewedPlanFingerprint = "old-plan";
            var migrated = ProfileDefaults.Upgrade(old);
            Check(!migrated.HighPerformancePower && !migrated.TuneEthernet && !migrated.PrioritizeNetwork && migrated.ReviewedPlanFingerprint == "",
                "Migration promoted unreviewed legacy driver/power changes.");
            var fresh = ProfileDefaults.Create();
            Check(!fresh.HighPerformancePower && !fresh.TuneEthernet && !fresh.PrioritizeNetwork
                && fresh.Intent.MatchSafe && !fresh.Intent.AllowNetworkExperiment && !fresh.Intent.AllowHigherHeat,
                "Fresh defaults reintroduced unconditional experimental changes.");
            return Task.CompletedTask;
        });
    }

    private static OptimizationPlan Build(PcAnalysis report, AppOptions options) => PerformancePlanner.Build(report, options, false, false);
    private static PlanAction FindAction(OptimizationPlan plan, string id) => plan.Actions.Single(a => a.Id == id);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Serialize(value))!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown.");
    }

    private static PcAnalysis Desktop() => new()
    {
        AcPower = true, ActivePowerPlan = "11111111-1111-1111-1111-111111111111", AvailableRamGiB = 16, HagsPreference = 2,
        Hardware = new PcFacts
        {
            Cpu = "Intel Core i7-13700K", Cores = 16, Threads = 24, RamGiB = 32, HasBattery = false, IsPortable = false,
            Gpus = [new() { Name = "NVIDIA GeForce RTX 4070", Driver = "fixture-driver-1", DedicatedMemoryMiB = 12288 }],
            Adapters = [new() { Name = "Fixture Ethernet", Link = "1 Gbps" }]
        },
        Displays = [new() { Primary = true, Width = 1920, Height = 1080, RefreshHz = 240 }],
        Volumes = [new() { Drive = "C:\\", FreeGiB = 100, FreePercent = 25 }]
    };
    private static AppOptions Options() => new()
    {
        OptionsVersion = 8, Intent = new UserIntent(), CloseCodex = true, FortniteOnly = true,
        MaximumHeadroom = true, KeepAppsClosed = true, PauseOptionalServices = true, HighPriority = true,
        GameTuning = true, SilenceNotifications = true, AutoRestoreOnGameExit = true, ForceClose = true,
        HighPerformancePower = false, TuneEthernet = false, PrioritizeNetwork = false,
        SelectedApps = ["chrome|C:\\Apps\\chrome.exe", "steam|C:\\Apps\\steam.exe"],
        ExcludedApps = ["excluded-app"], ProtectedAppPaths = [@"C:\Apps\KeepMixer.exe", @"C:\Apps\KeepRouter.exe"]
    };
}
