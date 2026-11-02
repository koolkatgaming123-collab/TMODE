using System.Diagnostics;
using System.Text.Json;

namespace TourneyMode;

internal static class SelfTests
{
    public static async Task<int> Run(string output)
    {
        var lines = new List<string>();
        int failed = 0;
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); lines.Add("PASS " + name); }
            catch (Exception e) { failed++; lines.Add("FAIL " + name + ": " + e); }
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        async Task ExpectFailure(Func<Task> action) { bool thrown = false; try { await action(); } catch { thrown = true; } Check(thrown, "Expected failure was silently ignored."); }
        (TourneyEngine, FakeSystem) Engine(string name)
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, name) };
            var engine = new TourneyEngine(fake.Directory, fake);
            return (engine, fake);
        }
        AppOptions OffOptions() => new() { SilenceNotifications = false, HighPerformancePower = false, GameTuning = false, HighPriority = false };
        (TourneyEngine Engine, FakeSystem System, AppCandidate App) WatchEngine(string name, bool present = true)
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, name) };
            var chrome = fake.Processes[0];
            chrome.Path = @"C:\FixtureApps\Chrome\chrome.exe";
            fake.Processes[1].Path = @"C:\FixtureApps\Discord\Discord.exe";
            var app = new AppCandidate { Id = "chrome|" + chrome.Path.ToLowerInvariant(), Name = "Google Chrome",
                LaunchPath = chrome.Path, Processes = [chrome] };
            if (!present) fake.Processes.Remove(chrome);
            return (new TourneyEngine(fake.Directory, fake, () => [app]), fake, app);
        }
        AppOptions WatchOptions(AppCandidate app) { var options = OffOptions(); options.FortniteOnly = true; options.KeepAppsClosed = true; options.SelectedApps = [app.Id]; return options; }

        await Test("protected Windows, security, Epic, ExitLag, anti-cheat, wrong-session and unknown-path processes", () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            foreach (string name in new[] { "svchost.exe", "MsMpEng.exe", "EasyAntiCheat_EOS.exe", "BEService.exe", "ExitLag.exe", "NVDisplay.Container.exe", "EpicGamesLauncher.exe", "FortniteClient-Win64-Shipping.exe", "lghub.exe", "Tourney Mode.exe" })
            {
                var p = new ProcessIdentity { Id = 123, Name = name, Path = name == "svchost.exe" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", name) : @"C:\Programs\" + name, StartTicks = 1, SessionId = sid };
                Check(ProcessPolicy.Protected(p, sid), name + " was not protected.");
            }
            Check(ProcessPolicy.Protected(new ProcessIdentity { Id = 124, Path = @"C:\a.exe", Name = "chrome.exe", StartTicks = 1, SessionId = sid + 1 }, sid), "Other session targeted.");
            Check(ProcessPolicy.Protected(new ProcessIdentity { Id = 124, Name = "chrome.exe", StartTicks = 1, SessionId = sid }, sid), "Unknown path targeted.");
            return Task.CompletedTask;
        });
        await Test("current process identity read; PID reuse identity rejected", () =>
        {
            var own = ProcessCatalog.Snapshot().Single(p => p.Id == Environment.ProcessId);
            Check(ProcessCatalog.Matches(own), "Actual own identity did not match.");
            using var pinned = Process.GetCurrentProcess();
            Check(ProcessCatalog.MatchesPinned(pinned, own), "Pinned actual identity did not match.");
            own.StartTicks++;
            Check(!ProcessCatalog.Matches(own), "Changed start time matched.");
            Check(!ProcessCatalog.MatchesPinned(pinned, own), "Pinned handle accepted altered identity.");
            return Task.CompletedTask;
        });
        await Test("selective close journals before mutation; crash recovery leaves apps closed", async () =>
        {
            var (engine, fake) = Engine("selective");
            var options = OffOptions();
            var available = engine.Scan();
            if (!available.Any(a => a.Name == "Google Chrome")) throw new Exception("Fixture candidates: " + string.Join(",", available.Select(a => a.Name)) + "; " + string.Join("|", fake.Processes.Select(p => p.Name + " exists=" + File.Exists(p.Path) + " protected=" + ProcessPolicy.Protected(p, Process.GetCurrentProcess().SessionId))));
            options.SelectedApps = [available.Single(a => a.Name == "Google Chrome").Id];
            await engine.EnableAsync(options);
            Check(fake.Killed.SequenceEqual(new[] { 101 }), "Wrong processes terminated.");
            Check(fake.Processes.Any(p => p.Id == 102), "Unselected Discord closed.");
            Check(engine.IsActive, "Session missing.");
            var reopened = new TourneyEngine(fake.Directory, fake);
            await reopened.RestoreAsync();
            Check(!reopened.IsActive && fake.Launches == 0 && fake.NativeStarts == 0 && !fake.Processes.Any(p => p.Id == 101) && !File.Exists(Path.Combine(fake.Directory, "session.json")), "Settings-only recovery reopened an app or retained completed state.");
            await reopened.RestoreAsync(); Check(fake.Launches == 0 && fake.NativeStarts == 0, "Repeated turn-off restarted an app.");
        });
        await Test("force-close disabled leaves refusing apps; off avoids duplicate main instance", async () =>
        {
            var (engine, fake) = Engine("graceful"); var o = OffOptions(); o.ForceClose = false; o.SelectedApps = [engine.Scan().First().Id];
            await engine.EnableAsync(o); await engine.RestoreAsync();
            Check(fake.Killed.Count == 0 && fake.Launches == 0, "Graceful-only mode killed/reopened existing app.");
        });
        await Test("temporary aggressive plan and all original settings recover exactly", async () =>
        {
            var (engine, fake) = Engine("settings");
            await engine.EnableAsync(new AppOptions { Intent = new() { MatchSafe = false } });
            Check(fake.Plan != FakeSystem.OriginalPlan && fake.NotificationsSuppressed && fake.TuningApplied && !fake.Dnd
                && engine.State!.OriginalDoNotDisturb is null, "Performance settings not applied or shell DND opened during play.");
            Check(fake.Priority == (int)ProcessPriorityClass.High, "Fortnite not prioritized.");
            await engine.RestoreAsync();
            Check(fake.Plan == FakeSystem.OriginalPlan && !fake.NotificationsSuppressed && !fake.TuningApplied && !fake.Dnd && fake.Priority == (int)ProcessPriorityClass.Normal && fake.DeletedPlans == 1, "Original values not restored.");
        });
        await Test("unsupported DND stays explicitly unknown; no blind toggle", async () =>
        {
            var (engine, fake) = Engine("dnd"); fake.DndReadable = false; var o = OffOptions(); o.SilenceNotifications = true;
            await engine.EnableAsync(o); Check(engine.State!.OriginalDoNotDisturb == null && !fake.Dnd, "Unsupported DND blindly changed.");
            await engine.RestoreAsync();
        });
        await Test("partial settings apply failure retains recoverable journal", async () =>
        {
            var (engine, fake) = Engine("failure"); fake.FailTuningApply = true; var o = OffOptions(); o.GameTuning = true;
            await ExpectFailure(() => engine.EnableAsync(o)); Check(engine.IsActive && engine.State!.Phase == "Needs recovery", "Failure lost state.");
            await engine.RestoreAsync(); Check(!engine.IsActive && !fake.TuningApplied, "Partial settings not recovered.");
        });
        await Test("denied Fortnite priority is not repeatedly retried or falsely pending on restore", async () =>
        {
            var (engine, fake) = Engine("denied-priority"); fake.DenyPriority = true; var o = OffOptions(); o.HighPriority = true;
            await engine.EnableAsync(o); await engine.MaintainAsync();
            Check(fake.PriorityAttempts == 1 && fake.Priority == (int)ProcessPriorityClass.Normal, "Denied priority was retried or changed.");
            await engine.RestoreAsync();
            Check(!engine.IsActive && fake.PriorityAttempts == 1, "Restore rewrote already-original priority or retained a false pending item.");
        });
        await Test("closed-app launcher failures do not affect settings-only recovery", async () =>
        {
            var (engine, fake) = Engine("no-relaunch"); var o = OffOptions();
            o.GameTuning = true; o.SelectedApps = [engine.Scan().First(a => a.Name == "Google Chrome").Id];
            await engine.EnableAsync(o); fake.FailLaunch = true; fake.UnobservedLaunch = true;
            fake.BeforeLaunch = () => throw new Exception("A closed app must not be launched.");
            await engine.RestoreAsync();
            Check(!engine.IsActive && !fake.TuningApplied && fake.Launches == 0 && fake.NativeStarts == 0
                && fake.AppReadinessChecks == 0 && fake.MobileReadinessChecks == 0
                && !fake.Processes.Any(p => p.Id == 101), "Turn-off queried or reopened closed apps.");
        });
        await Test("failed settings restore retains journal and retries without reopening apps", async () =>
        {
            var (engine, fake) = Engine("settings-restore-retry"); var o = OffOptions();
            o.GameTuning = true; o.SelectedApps = [engine.Scan().First(a => a.Name == "Google Chrome").Id];
            await engine.EnableAsync(o); fake.FailTuningRestore = true;
            await ExpectFailure(engine.RestoreAsync);
            Check(engine.IsActive && engine.State!.Phase == "Needs recovery" && fake.TuningApplied
                && File.Exists(Path.Combine(fake.Directory, "session.json")), "Failed settings recovery lost its journal.");
            Check(fake.Launches == 0 && fake.NativeStarts == 0 && engine.State!.Apps.All(a => a.Restored),
                "Settings failure reopened apps or retained an app relaunch obligation.");
            fake.FailTuningRestore = false;
            await new TourneyEngine(fake.Directory, fake).RestoreAsync();
            Check(!File.Exists(Path.Combine(fake.Directory, "session.json")) && !fake.TuningApplied
                && fake.Launches == 0 && fake.NativeStarts == 0, "Settings retry reopened apps or failed to finish.");
        });
        await Test("surviving helper does not count as restored main process", () =>
        {
            var parent = new ProcessIdentity { Id = 1, ParentId = 0, Path = @"C:\app.exe", StartTicks = 1, SessionId = 1 };
            var child = new ProcessIdentity { Id = 2, ParentId = 1, Path = @"C:\app.exe", StartTicks = 2, SessionId = 1 };
            Check(!TourneyEngine.AppReturned(new AppCandidate { Processes = [parent, child] }, [child]), "Orphan renderer counted as app root.");
            return Task.CompletedTask;
        });
        await Test("corrupt recovery journal blocks fresh activation", async () =>
        {
            string dir = Path.Combine(root, "corrupt"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "session.json"), "corrupt");
            await ExpectFailure(() => { _ = new TourneyEngine(dir, new FakeSystem()); return Task.CompletedTask; });
        });
        (TourneyEngine, FakeSystem) NativeEngine(string name)
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, name) };
            fake.Processes[1].Name = "FixtureBridge.exe";
            var native = new AppCandidate { Id = "spencer-native", Name = "Spencer Native", Processes = [fake.Processes[0]], LaunchPath = Environment.ProcessPath! };
            var bridge = new AppCandidate { Id = "codex-bridge", Name = "Codex Bridge", Processes = [fake.Processes[1]], LaunchPath = Environment.ProcessPath! };
            return (new TourneyEngine(fake.Directory, fake, () => [bridge, native]), fake);
        }
        await Test("legacy Native and bridge selections never stop connection backends", async () =>
        {
            var (engine, fake) = NativeEngine("native-busy"); fake.FailNativeStop = true;
            var o = OffOptions(); o.SelectedApps = ["codex-bridge", "spencer-native"];
            await engine.EnableAsync(o);
            Check(fake.Killed.Count == 0 && fake.CloseCalls.Count == 0 && engine.IsActive && engine.State!.Apps.Count == 0, "Connection work was closed or selected.");
            await engine.RestoreAsync(); Check(!engine.IsActive, "Busy refusal was not recoverable.");
        });
        await Test("turn-off leaves Native stopped without requiring provider readiness", async () =>
        {
            var (engine, fake) = NativeEngine("worker-only"); fake.ProviderReady = false;
            var o = OffOptions(); o.SelectedApps = ["spencer-native"];
            await engine.EnableAsync(o); await engine.RestoreAsync();
            Check(!engine.IsActive && fake.NativeStarts == 0 && fake.Launches == 0
                && fake.AppReadinessChecks == 0, "Turn-off restarted Native or waited on app readiness.");
        });
        await Test("repeated on-off cycles keep Native and its bridge available", async () =>
        {
            var (engine, fake) = NativeEngine("native-and-bridge-stay-closed");
            var options = OffOptions(); options.SelectedApps = ["codex-bridge", "spencer-native"];
            await engine.EnableAsync(options);
            Check(fake.Killed.Count == 0 && fake.Processes.Any(p => p.Id == 101) && fake.Processes.Any(p => p.Id == 102), "Native/bridge connection was closed.");
            fake.BeforeLaunch = () => throw new Exception("No connection process should need relaunch.");
            await engine.RestoreAsync();
            await engine.EnableAsync(options); await engine.MaintainAsync(); await engine.RestoreAsync();
            Check(!engine.IsActive && fake.NativeStarts == 0 && fake.Launches == 0
                && fake.AppReadinessChecks == 0 && fake.MobileReadinessChecks == 0,
                "Native or its bridge was restarted during turn-off.");
        });
        await Test("recorded Native mobile launchers are ignored during settings-only recovery", async () =>
        {
            var (engine, fake) = NativeEngine("native-mobile"); var o = OffOptions(); o.SelectedApps = ["spencer-native"];
            await engine.EnableAsync(o);
            engine.State!.Apps.Add(new RestoreApp { App = new AppCandidate { Id = "spencer-native", AdditionalLaunchPaths = ["mobile.vbs"] }, CloseAttempted = true });
            fake.MobileIsReady = false;
            await engine.RestoreAsync();
            Check(!engine.IsActive && fake.NativeStarts == 0 && fake.Launches == 0 && fake.MobileReadinessChecks == 0,
                "Recovery launched or waited for a previously closed mobile backend.");
        });
        await Test("app guard journals fresh respawns before close; crash recovery leaves them closed", async () =>
        {
            var (engine, fake, app) = WatchEngine("guard-respawn");
            await engine.EnableAsync(WatchOptions(app));
            var returned = new ProcessIdentity { Id = 201, Name = "chrome.exe", Path = app.LaunchPath,
                StartTicks = 201, SessionId = Process.GetCurrentProcess().SessionId };
            fake.Processes.Add(returned);
            bool journaled = false;
            fake.BeforeClose = p =>
            {
                if (p.Id != returned.Id) return;
                var saved = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(Path.Combine(fake.Directory, "session.json")))!;
                var entry = saved.Apps.Single();
                journaled = entry.CloseAttempted && !entry.Restored && entry.App.Processes.Any(q =>
                    q.Id == returned.Id && q.StartTicks == returned.StartTicks && q.Path == returned.Path);
            };
            await engine.MaintainAsync();
            Check(journaled && fake.Killed.SequenceEqual(new[] { 101, 201 }), "Fresh process was not journaled before shutdown.");
            Check(engine.State!.Apps.Count == 1, "A respawn created duplicate recovery entries.");
            var reopened = new TourneyEngine(fake.Directory, fake);
            await reopened.RestoreAsync();
            await reopened.MaintainAsync();
            await reopened.RestoreAsync();
            Check(!reopened.IsActive && fake.Launches == 0 && fake.NativeStarts == 0 && !fake.Processes.Any(p => p.Name == "chrome.exe"), "Crash recovery reopened a closed app.");
        });
        await Test("app guard preserves unchecked and newly opened unselected apps", async () =>
        {
            var (engine, fake, app) = WatchEngine("guard-unselected");
            var options = WatchOptions(app); options.ExcludedApps = ["discord|" + fake.Processes[1].Path.ToLowerInvariant()];
            await engine.EnableAsync(options);
            var obs = new ProcessIdentity { Id = 202, Name = "obs64.exe", Path = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
                SessionId = Process.GetCurrentProcess().SessionId, StartTicks = 202 };
            fake.Processes.Add(obs);
            await engine.MaintainAsync();
            Check(fake.Processes.Any(p => p.Id == 102) && fake.Processes.Any(p => p.Id == obs.Id), "An unselected app was closed.");
            Check(fake.Killed.SequenceEqual(new[] { 101 }), "Guard expanded beyond the selected app.");
            await engine.RestoreAsync();
        });
        await Test("shared console and WebView helpers require the selected app's current ancestry", () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            var originalRoot = new ProcessIdentity { Id = 101, Name = "chrome.exe", Path = @"C:\Apps\chrome.exe", SessionId = sid, StartTicks = 1 };
            var originalConsole = new ProcessIdentity { Id = 102, ParentId = 101, Name = "conhost.exe", Path = @"C:\Windows\System32\conhost.exe", SessionId = sid, StartTicks = 2 };
            var originalWebView = new ProcessIdentity { Id = 103, ParentId = 101, Name = "msedgewebview2.exe", Path = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\1\msedgewebview2.exe", SessionId = sid, StartTicks = 3 };
            var app = new AppCandidate { Id = "chrome|" + originalRoot.Path.ToLowerInvariant(), LaunchPath = originalRoot.Path, Processes = [originalRoot, originalConsole, originalWebView] };
            var rootNow = new ProcessIdentity { Id = 201, Name = originalRoot.Name, Path = originalRoot.Path, SessionId = sid, StartTicks = 201 };
            var ownConsole = new ProcessIdentity { Id = 202, ParentId = 201, Name = originalConsole.Name, Path = originalConsole.Path, SessionId = sid, StartTicks = 202 };
            var ownWebView = new ProcessIdentity { Id = 203, ParentId = 201, Name = originalWebView.Name, Path = originalWebView.Path, SessionId = sid, StartTicks = 203 };
            var otherConsole = new ProcessIdentity { Id = 204, ParentId = 999, Name = originalConsole.Name, Path = originalConsole.Path, SessionId = sid, StartTicks = 204 };
            var reusedWebView = new ProcessIdentity { Id = 103, ParentId = 999, Name = originalWebView.Name, Path = originalWebView.Path, SessionId = sid, StartTicks = 205 };
            var targets = TourneyEngine.WatchTargets(app, [rootNow, ownConsole, ownWebView, otherConsole, reusedWebView]);
            Check(targets.Select(p => p.Id).Order().SequenceEqual(new[] { 201, 202, 203 }), "Shared helper from another app or reused PID was targeted.");
            return Task.CompletedTask;
        });
        await Test("identity, session and protection checks reject unsafe app guard targets", () =>
        {
            var fake = new FakeSystem(); var original = fake.Processes[0];
            var reused = new ProcessIdentity { Id = original.Id, Name = original.Name, Path = original.Path,
                SessionId = original.SessionId, StartTicks = original.StartTicks + 1 };
            fake.Processes.Remove(original); fake.Processes.Add(reused);
            Check(!fake.CanTouch(original), "Reused PID accepted an old identity.");
            var otherSession = new ProcessIdentity { Id = 205, Name = "chrome.exe", Path = @"C:\Apps\chrome.exe",
                SessionId = original.SessionId + 1, StartTicks = 205 };
            var protectedImage = new ProcessIdentity { Id = 206, Name = "chrome.exe", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "chrome.exe"),
                SessionId = original.SessionId, StartTicks = 206 };
            fake.Processes.AddRange([otherSession, protectedImage]);
            Check(!fake.CanTouch(otherSession) && !fake.CanTouch(protectedImage), "Protected or other-session app became touchable.");
            return Task.CompletedTask;
        });
        await Test("app guard excludes Native and bridge staged lifecycles", async () =>
        {
            var (engine, fake) = NativeEngine("guard-native");
            var options = OffOptions(); options.KeepAppsClosed = true; options.SelectedApps = ["spencer-native", "codex-bridge"];
            await engine.EnableAsync(options);
            int closed = fake.CloseCalls.Count;
            await engine.MaintainAsync();
            Check(fake.WatchedScans == 0 && fake.CloseCalls.Count == closed, "Generic guard scanned Native/bridge entries.");
            Check(!ProcessPolicy.IsMonitoredApp(new AppCandidate { Id = "spencer-native", LaunchPath = "node.exe" })
                && !ProcessPolicy.IsMonitoredApp(new AppCandidate { Id = "codex-bridge", LaunchPath = "bridge.lnk" }), "Special lifecycle was monitorable.");
            await engine.RestoreAsync();
        });
        await Test("app guard attempts each refusing identity once and accepts a later new identity", async () =>
        {
            var (engine, fake, app) = WatchEngine("guard-refusal");
            var options = WatchOptions(app); options.ForceClose = false;
            await engine.EnableAsync(options);
            await engine.MaintainAsync();
            int firstTickCalls = fake.CloseCalls.Count;
            await engine.MaintainAsync(); await engine.MaintainAsync();
            Check(fake.CloseCalls.Count == firstTickCalls && fake.Killed.Count == 0, "Refusing process was retried continuously or force-closed.");
            fake.Processes.RemoveAll(p => p.Id == 101);
            fake.Processes.Add(new ProcessIdentity { Id = 207, Name = "chrome.exe", Path = app.LaunchPath, StartTicks = 207, SessionId = Process.GetCurrentProcess().SessionId });
            await engine.MaintainAsync();
            Check(fake.CloseCalls.Count == firstTickCalls + 1, "A later respawn was incorrectly skipped.");
            await engine.RestoreAsync();
            Check(fake.Launches == 0, "A surviving main process was duplicated during recovery.");
        });
        await Test("app guard records late shutdown; turn-off leaves the late app closed", async () =>
        {
            var (engine, fake, app) = WatchEngine("guard-late", present: false);
            await engine.EnableAsync(WatchOptions(app));
            Check(engine.State!.Apps.Single().Restored && !engine.State.Apps.Single().CloseAttempted, "Absent app was incorrectly marked closed.");
            fake.Processes.Add(new ProcessIdentity { Id = 208, Name = "chrome.exe", Path = app.LaunchPath, StartTicks = 208, SessionId = Process.GetCurrentProcess().SessionId });
            await engine.MaintainAsync();
            Check(!engine.State.Apps.Single().Restored && engine.State.Apps.Single().CloseAttempted && fake.Killed.Contains(208), "Late shutdown discarded the restore requirement.");
            await new TourneyEngine(fake.Directory, fake).RestoreAsync();
            Check(fake.Launches == 0 && fake.NativeStarts == 0 && !fake.Processes.Any(p => p.Id == 208), "Late app was relaunched during turn-off.");
        });
        await Test("restore marks its phase before settings recovery and prevents concurrent guard work", async () =>
        {
            var (engine, fake, app) = WatchEngine("guard-restore");
            var options = WatchOptions(app); options.GameTuning = true;
            await engine.EnableAsync(options);
            bool restoring = false;
            fake.BeforeRestoreTuning = () =>
            {
                var saved = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(Path.Combine(fake.Directory, "session.json")))!;
                restoring = saved.Phase == "Restoring";
                engine.MaintainAsync().GetAwaiter().GetResult();
            };
            await engine.RestoreAsync();
            await engine.MaintainAsync();
            Check(restoring && fake.WatchedScans == 0 && fake.Launches == 0 && !fake.Processes.Any(p => p.Name == "chrome.exe"),
                "Guard ran during settings recovery or turn-off reopened an app.");
        });
        await Test("legacy journals keep new aggressive behavior disabled and remain recoverable", async () =>
        {
            var options = JsonSerializer.Deserialize<AppOptions>("{\"ForceClose\":true,\"SelectedApps\":[]}")!;
            Check(!options.FortniteOnly && !options.KeepAppsClosed, "Missing new options silently enabled aggressive behavior.");
            var (engine, fake) = Engine("legacy-guard");
            var oldOptions = OffOptions(); oldOptions.SelectedApps = [engine.Scan().First(a => a.Name == "Google Chrome").Id];
            await engine.EnableAsync(oldOptions);
            string statePath = Path.Combine(fake.Directory, "session.json");
            string legacy = File.ReadAllText(statePath).Replace("\"FortniteOnly\": false,", "").Replace("\"KeepAppsClosed\": false,", "");
            File.WriteAllText(statePath, legacy);
            var reopened = new TourneyEngine(fake.Directory, fake);
            await reopened.MaintainAsync();
            Check(fake.WatchedScans == 0, "Legacy session started the new app guard.");
            await reopened.RestoreAsync(); Check(!reopened.IsActive && fake.Launches == 0 && fake.NativeStarts == 0 && !fake.Processes.Any(p => p.Id == 101), "Legacy recovery reopened an app or failed.");
        });
        await Test("broader policy adds installed editors while leaving Steam optional for controller users", () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programs86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var steam = new ProcessIdentity { Id = 1000101, Name = "steam.exe", Path = Path.Combine(programs86, "Steam", "steam.exe"), SessionId = sid, StartTicks = 1 };
            var helper = new ProcessIdentity { Id = 1000102, ParentId = steam.Id, Name = "steamwebhelper.exe", Path = Path.Combine(programs86, "Steam", "bin", "cef", "cef.win64", "steamwebhelper.exe"), SessionId = sid, StartTicks = 2 };
            var obs = new ProcessIdentity { Id = 1000103, Name = "obs64.exe", Path = Path.Combine(programs, "obs-studio", "bin", "64bit", "obs64.exe"), SessionId = sid, StartTicks = 3 };
            var word = new ProcessIdentity { Id = 1000104, Name = "WINWORD.EXE", Path = Path.Combine(programs, "Microsoft Office", "root", "Office16", "WINWORD.EXE"), SessionId = sid, StartTicks = 4 };
            var standard = ProcessPolicy.Candidates([steam, helper, obs, word], exists: _ => true);
            var dedicated = ProcessPolicy.Candidates([steam, helper, obs, word], exists: _ => true, fortniteOnly: true);
            Check(standard.Count == 1 && standard[0].Name.StartsWith("Steam") && !standard[0].DefaultSelected, "Standard policy/default changed.");
            Check(dedicated.Count == 3 && dedicated.Where(a => !a.Name.StartsWith("Steam")).All(a => a.DefaultSelected)
                && !dedicated.Single(a => a.Name.StartsWith("Steam")).DefaultSelected, "Installed apps were missing or Steam Input was selected by default.");
            Check(dedicated.Single(a => a.Name.StartsWith("Steam")).Processes.Any(p => p.Id == helper.Id), "Steam's known helper was missed.");
            Check(dedicated.All(ProcessPolicy.IsMonitoredApp), "Direct installed app groups were not eligible for exact-path watching.");
            return Task.CompletedTask;
        });
        await Test("broader policy rejects wrong installation paths, renamed images and unknown runtimes", () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var wrongDirectory = new ProcessIdentity { Id = 1000201, Name = "obs64.exe", Path = Path.Combine(programs, "Unrelated Tools", "obs64.exe"), SessionId = sid, StartTicks = 1 };
            var downloaded = new ProcessIdentity { Id = 1000202, Name = "obs64.exe", Path = Path.Combine(user, "Downloads", "obs-studio", "obs64.exe"), SessionId = sid, StartTicks = 2 };
            var wrongName = new ProcessIdentity { Id = 1000203, Name = "Unrelated.exe", Path = Path.Combine(programs, "obs-studio", "bin", "64bit", "obs64.exe"), SessionId = sid, StartTicks = 3 };
            var runtime = new ProcessIdentity { Id = 1000204, Name = "node.exe", Path = Path.Combine(programs, "nodejs", "node.exe"), SessionId = sid, StartTicks = 4 };
            var hardware = new ProcessIdentity { Id = 1000205, Name = "iCUE.exe", Path = Path.Combine(programs, "Corsair", "iCUE5 Software", "iCUE.exe"), SessionId = sid, StartTicks = 5 };
            var game = new ProcessIdentity { Id = 1000206, Name = "FortniteClient-Win64-Shipping.exe", Path = Path.Combine(programs, "Epic Games", "Fortnite", "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe"), SessionId = sid, StartTicks = 6 };
            var candidates = ProcessPolicy.Candidates([wrongDirectory, downloaded, wrongName, runtime, hardware, game], exists: _ => true, fortniteOnly: true);
            Check(candidates.Count == 0, "An unrecognized image or protected component became a close candidate.");
            Check(ProcessPolicy.Protected(hardware, sid) && ProcessPolicy.Protected(game, sid), "Hardware/game protection was lost.");
            return Task.CompletedTask;
        });
        await Test("broader policy expands shared helpers only beneath a selected known app", () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programs86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var obs = new ProcessIdentity { Id = 1000301, Name = "obs64.exe", Path = Path.Combine(programs, "obs-studio", "bin", "64bit", "obs64.exe"), SessionId = sid, StartTicks = 1 };
            string console = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "conhost.exe");
            string webview = Path.Combine(programs86, "Microsoft", "EdgeWebView", "Application", "fixture", "msedgewebview2.exe");
            var ownConsole = new ProcessIdentity { Id = 1000302, ParentId = obs.Id, Name = "conhost.exe", Path = console, SessionId = sid, StartTicks = 2 };
            var ownWebView = new ProcessIdentity { Id = 1000303, ParentId = obs.Id, Name = "msedgewebview2.exe", Path = webview, SessionId = sid, StartTicks = 3 };
            var unrelatedConsole = new ProcessIdentity { Id = 1000304, ParentId = 1000399, Name = "conhost.exe", Path = console, SessionId = sid, StartTicks = 4 };
            var unrelatedWebView = new ProcessIdentity { Id = 1000305, ParentId = 1000399, Name = "msedgewebview2.exe", Path = webview, SessionId = sid, StartTicks = 5 };
            var script = new ProcessIdentity { Id = 1000306, ParentId = obs.Id, Name = "node.exe", Path = Path.Combine(programs, "nodejs", "node.exe"), SessionId = sid, StartTicks = 6 };
            var candidate = ProcessPolicy.Candidates([obs, ownConsole, ownWebView, unrelatedConsole, unrelatedWebView, script], exists: _ => true, fortniteOnly: true).Single();
            Check(candidate.Processes.Select(p => p.Id).Order().SequenceEqual(new[] { obs.Id, ownConsole.Id, ownWebView.Id }), "Shared runtime ancestry or unknown-child boundary was violated.");
            return Task.CompletedTask;
        });
        await Test("Discord variants and installed updater helpers are permanently protected", () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var discord = new List<ProcessIdentity>();
            int nextId = 1000500;
            foreach (string variant in new[] { "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment" })
            {
                foreach (string image in new[] { variant + ".exe", "Update.exe", "crashpad_handler.exe" })
                {
                    var process = new ProcessIdentity { Id = ++nextId, Name = image,
                        Path = Path.Combine(local, variant, "app-fixture", image), SessionId = sid, StartTicks = nextId };
                    discord.Add(process);
                    Check(ProcessPolicy.Protected(process, sid), process.Path + " was not protected.");
                }
                var stale = new AppCandidate { Id = "discord|" + Path.Combine(local, variant, "app-fixture", variant + ".exe").ToLowerInvariant(),
                    LaunchPath = Path.Combine(local, variant, "app-fixture", variant + ".exe") };
                Check(!ProcessPolicy.IsMonitoredApp(stale), "A stale Discord selection became monitorable.");
            }
            Check(ProcessPolicy.Candidates(discord, exists: _ => true).Count == 0
                && ProcessPolicy.Candidates(discord, exists: _ => true, fortniteOnly: true).Count == 0,
                "Discord appeared in an app-close candidate list.");
            return Task.CompletedTask;
        });
        await Test("stale explicit Discord selection and returning Discord are never closed", async () =>
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, "discord-stale-selection") };
            var discord = fake.Processes.Single(p => p.Name == "Discord.exe");
            discord.Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "app-fixture", "Discord.exe");
            var app = new AppCandidate { Id = "discord|" + discord.Path.ToLowerInvariant(), Name = "Discord", LaunchPath = discord.Path, Processes = [discord] };
            var engine = new TourneyEngine(fake.Directory, fake, () => [app]);
            var options = WatchOptions(app);
            await engine.EnableAsync(options);
            var returning = new ProcessIdentity { Id = 1000601, Name = "Discord.exe", Path = discord.Path, SessionId = discord.SessionId, StartTicks = 1000601 };
            fake.Processes.Add(returning);
            await engine.MaintainAsync();
            Check(!fake.CloseCalls.Contains(discord.Id) && !fake.CloseCalls.Contains(returning.Id)
                && !fake.Killed.Contains(discord.Id) && !fake.Killed.Contains(returning.Id)
                && fake.Processes.Any(p => p.Id == discord.Id) && fake.Processes.Any(p => p.Id == returning.Id),
                "Discord was touched by initial shutdown or the app guard.");
            await engine.RestoreAsync();
            Check(fake.Launches == 0 && fake.NativeStarts == 0, "Turn-off launched an app for a protected Discord selection.");
        });

        await Test("comparison plan validation is read-only and preserves balanced profile semantics", () =>
        {
            string planFile = Path.Combine(root, "comparison-plan.json");
            string outputFolder = Path.Combine(root, "comparison-output-not-created");
            File.WriteAllText(planFile, JsonSerializer.Serialize(new BenchmarkRunner.BenchmarkPlan
            {
                Comparison = true, CaptureSeconds = 60, SkipCollectionCallback = true,
                OutputDirectory = outputFolder, PresentMonPath = Environment.ProcessPath!, ExcludedAppPrefixes = []
            }));
            var plan = BenchmarkRunner.PreviewPlan(planFile);
            Check(!Directory.Exists(outputFolder), "Plan preview created benchmark state.");
            Check(plan.ComparisonBlockOrder.SequenceEqual(new[] { "V1A", "V2A", "V2B", "V1B" }) && plan.ComparisonTrialsPerBlock == 2,
                "Comparison is not balanced ABBA with two trials per block.");
            Check(plan.ExcludedAppPrefixes.Contains("codex-desktop|") && plan.CaptureSeconds == 60, "Collector protection or capture duration was lost.");
            return Task.CompletedTask;
        });
        await Test("invalid comparison duration and an existing status reject uncertain replay", async () =>
        {
            string planFile = Path.Combine(root, "invalid-comparison-plan.json");
            string outputFolder = Path.Combine(root, "existing-comparison");
            var plan = new BenchmarkRunner.BenchmarkPlan { CaptureSeconds = 0, SkipCollectionCallback = true,
                OutputDirectory = outputFolder, PresentMonPath = Environment.ProcessPath! };
            File.WriteAllText(planFile, JsonSerializer.Serialize(plan));
            await ExpectFailure(() => { BenchmarkRunner.PreviewPlan(planFile); return Task.CompletedTask; });
            plan.CaptureSeconds = 60; Directory.CreateDirectory(outputFolder);
            File.WriteAllText(Path.Combine(outputFolder, "benchmark-status.json"), "{}");
            File.WriteAllText(planFile, JsonSerializer.Serialize(plan));
            await ExpectFailure(() => { BenchmarkRunner.PreviewPlan(planFile); return Task.CompletedTask; });
        });
        await Test("benchmark focus recovers once and requires two stable seconds", async () =>
        {
            long now = 0; int pid = 99; int attempts = 0;
            await BenchmarkForegroundGate.WaitAsync(42, () => pid, () => { }, _ => { attempts++; pid = 42; },
                () => now, _ => { now += 250; return Task.CompletedTask; }, CancellationToken.None);
            Check(attempts == 1 && now == 2250, "Foreground recovery returned early or repeated a successful request.");
        });
        await Test("benchmark focus loss resets the stability interval", async () =>
        {
            long now = 0; int attempts = 0;
            await BenchmarkForegroundGate.WaitAsync(42, () => now >= 1000 && now < 1750 ? 99 : 42,
                () => { }, _ => attempts++, () => now, _ => { now += 250; return Task.CompletedTask; }, CancellationToken.None);
            Check(now == 3750 && attempts == 1, "Foreground stability ignored an interruption.");
        });
        await Test("benchmark focus retries are bounded and Windows refusal times out", async () =>
        {
            long now = 0; var attempts = new List<long>(); bool timedOut = false;
            try
            {
                await BenchmarkForegroundGate.WaitAsync(42, () => 99, () => { }, _ => attempts.Add(now),
                    () => now, _ => { now += 250; return Task.CompletedTask; }, CancellationToken.None);
            }
            catch (TimeoutException) { timedOut = true; }
            Check(timedOut && now == 120000 && attempts.SequenceEqual(new long[] { 0, 5000, 10000 }),
                "Focus refusal was ignored or requests were unbounded.");
        });
        await Test("benchmark identity failure prevents a focus request", async () =>
        {
            int attempts = 0;
            await ExpectFailure(() => BenchmarkForegroundGate.WaitAsync(42, () => 99,
                () => throw new InvalidOperationException("changed game"), _ => attempts++, () => 0,
                _ => Task.CompletedTask, CancellationToken.None));
            Check(attempts == 0, "A changed process was focused.");
        });
        await Test("benchmark cancellation prevents a focus request", async () =>
        {
            int attempts = 0; using var stop = new CancellationTokenSource(); stop.Cancel();
            bool cancelled = false;
            try
            {
                await BenchmarkForegroundGate.WaitAsync(42, () => 99, () => { }, _ => attempts++, () => 0,
                    _ => Task.CompletedTask, stop.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && attempts == 0, "Cancelled preparation attempted to focus a window.");
        });
        await Test("optional service allowlist excludes security, networking, audio, input and Discord", () =>
        {
            foreach (string name in new[] { "WSearch", "Spooler", "Fax", "MapsBroker", "RetailDemo", "XblAuthManager",
                "XblGameSave", "XboxNetApiSvc", "edgeupdate", "edgeupdatem", "gupdate", "gupdatem", "AdobeARMservice" })
                Check(TournamentResources.IsAllowedServiceName(name), "Expected optional service not allowed: " + name);
            foreach (string name in new[] { "WinDefend", "WdNisSvc", "MpsSvc", "BFE", "Dnscache", "Dhcp", "NlaSvc", "EventLog",
                "Audiosrv", "AudioEndpointBuilder", "HidServ", "Discord", "Discord.exe", "RpcSs", "WSearch-extra", "edgeupdatebad" })
                Check(!TournamentResources.IsAllowedServiceName(name), "Essential or unrecognized service allowed: " + name);
            return Task.CompletedTask;
        });
        await Test("network policy ownership and NIC tuning keyword allowlists reject unrelated settings", () =>
        {
            Check(TournamentResources.IsOwnedPolicyName("TourneyMode-" + Guid.NewGuid().ToString("N")),
                "A unique Tourney Mode policy name was rejected.");
            foreach (string name in new[] { "ExistingPolicy", "TourneyMode-other", "TourneyMode-" + new string('z', 32),
                "TourneyMode-" + new string('1', 31), "TourneyMode-" + new string('1', 33) })
                Check(!TournamentResources.IsOwnedPolicyName(name), "An unrelated QoS policy could be removed: " + name);
            foreach (string keyword in new[] { "*EEE", "*InterruptModeration", "*Rss" })
                Check(EthernetTuning.IsAllowedKeyword(keyword), "Expected optional NIC keyword not allowed: " + keyword);
            foreach (string keyword in new[] { "*SpeedDuplex", "*IPChecksumOffloadIPv4", "*TCPChecksumOffloadIPv4",
                "*LsoV2IPv4", "PnPCapabilities", "*FlowControl", "*PacketCoalescing", "*SelectiveSuspend" })
                Check(!EthernetTuning.IsAllowedKeyword(keyword), "Unrequested NIC setting allowed: " + keyword);
            return Task.CompletedTask;
        });
        await Test("optional resources are journaled before mutation and recover without reopening apps", async () =>
        {
            var (engine, fake) = Engine("resources-roundtrip");
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite); // Explicit pre-match preparation.
            var options = OffOptions();
            options.PauseOptionalServices = options.PrioritizeNetwork = options.TuneEthernet = true;
            options.SelectedApps = [engine.Scan().First(a => a.Name == "Google Chrome").Id];
            await engine.EnableAsync(options);
            Check(fake.ServiceStops.SequenceEqual(new[] { "WSearch" }) && !fake.ServiceRunning["WSearch"]
                && !fake.ServiceRunning["Spooler"], "An originally stopped service was touched or optional service was not paused.");
            Check(fake.ActivePolicyName.Length > 0 && fake.NetworkApplies == 1 && fake.AdapterApplies == 1
                && fake.AdapterValues["*EEE"].SequenceEqual(new[] { "0" }) && fake.AdapterValues["*Rss"].SequenceEqual(new[] { "1" }),
                "Fixture network/adapter tuning was not applied.");
            await new TourneyEngine(fake.Directory, fake).RestoreAsync();
            Check(fake.ServiceRestores.SequenceEqual(new[] { "WSearch" }) && fake.ServiceRunning["WSearch"]
                && !fake.ServiceRunning["Spooler"], "Recovery restarted a service that was originally stopped.");
            Check(fake.ActivePolicyName.Length == 0 && fake.NetworkRestores == 1 && fake.AdapterRestores == 1
                && fake.AdapterValues["*EEE"].SequenceEqual(new[] { "1" })
                && fake.AdapterValues["*InterruptModeration"].SequenceEqual(new[] { "1" })
                && fake.AdapterValues["*Rss"].SequenceEqual(new[] { "0" }), "Original network/adapter fixture state was not restored.");
            Check(fake.Launches == 0 && fake.NativeStarts == 0 && !fake.Processes.Any(p => p.Id == 101)
                && !File.Exists(Path.Combine(fake.Directory, "session.json")), "Resource recovery reopened apps or left completed state.");
        });
        await Test("partial optional resource apply failures retain recoverable original values", async () =>
        {
            var (engine, fake) = Engine("resources-partial");
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite);
            var options = OffOptions();
            options.PauseOptionalServices = options.PrioritizeNetwork = options.TuneEthernet = true;
            fake.FailServiceStop = fake.FailNetworkApply = fake.FailAdapterApply = true;
            await engine.EnableAsync(options);
            Check(engine.IsActive && File.Exists(Path.Combine(fake.Directory, "session.json"))
                && engine.State!.Services.Any(s => s.Name == "WSearch" && s.StopAttempted)
                && engine.State.NetworkPriority?.ApplyAttempted == true && engine.State.EthernetAdapter?.ApplyAttempted == true,
                "Partial resource mutations lost their recovery records.");
            Check(!fake.ServiceRunning["WSearch"] && fake.ActivePolicyName.Length == 0
                && fake.AdapterValues["*EEE"].SequenceEqual(new[] { "1" })
                && engine.State!.EthernetAdapter!.Restored && engine.State.NetworkPriority!.Restored,
                "Partial network failure did not roll back immediately and durably.");
            await new TourneyEngine(fake.Directory, fake).RestoreAsync();
            Check(fake.ServiceRunning["WSearch"] && fake.ActivePolicyName.Length == 0
                && fake.AdapterValues["*EEE"].SequenceEqual(new[] { "1" })
                && fake.Launches == 0 && fake.NativeStarts == 0 && !File.Exists(Path.Combine(fake.Directory, "session.json")),
                "Partial resource recovery failed or reopened an app.");
        });
        await Test("resource recovery failure keeps journal and retry skips already restored resources", async () =>
        {
            var (engine, fake) = Engine("resources-retry");
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite);
            var options = OffOptions();
            options.PauseOptionalServices = options.PrioritizeNetwork = options.TuneEthernet = true;
            await engine.EnableAsync(options);
            fake.FailNetworkRestore = true;
            await ExpectFailure(engine.RestoreAsync);
            Check(engine.IsActive && engine.State!.Phase == "Needs recovery"
                && engine.State.NetworkPriority?.Restored == false
                && File.Exists(Path.Combine(fake.Directory, "session.json")), "Failed network cleanup lost its recovery journal.");
            Check(fake.AdapterRestores == 1 && fake.ServiceRestores.SequenceEqual(new[] { "WSearch" })
                && fake.ActivePolicyName.Length > 0, "Independent resources were not restored after one cleanup failed.");
            fake.FailNetworkRestore = false;
            await new TourneyEngine(fake.Directory, fake).RestoreAsync();
            Check(fake.NetworkRestores == 2 && fake.AdapterRestores == 1
                && fake.ServiceRestores.SequenceEqual(new[] { "WSearch" }) && fake.ActivePolicyName.Length == 0
                && fake.Launches == 0 && fake.NativeStarts == 0 && !File.Exists(Path.Combine(fake.Directory, "session.json")),
                "Resource retry duplicated completed work, reopened apps or failed to finish.");
        });
        await Test("legacy options leave optional services and Ethernet untouched", async () =>
        {
            var options = JsonSerializer.Deserialize<AppOptions>("{\"ForceClose\":true,\"SelectedApps\":[]}")!;
            Check(!options.PauseOptionalServices && !options.PrioritizeNetwork && !options.TuneEthernet,
                "Missing optional-resource preferences enabled system changes.");
            var (engine, fake) = Engine("resources-legacy");
            await engine.EnableAsync(OffOptions());
            await new TourneyEngine(fake.Directory, fake).RestoreAsync();
            Check(fake.ResourceCaptures == 0 && fake.ServiceStops.Count == 0 && fake.ServiceRestores.Count == 0
                && fake.NetworkApplies == 0 && fake.NetworkRestores == 0 && fake.AdapterApplies == 0 && fake.AdapterRestores == 0,
                "Legacy settings triggered an optional-resource operation.");
        });

        await Test("real custom power scheme can be read without mutation", () =>
        {
            Check(Guid.TryParse(WindowsSettings.GetPowerScheme(), out _), "No valid power scheme."); return Task.CompletedTask;
        });
        await Test("maximum headroom includes unlisted runtimes but preserves Windows, hardware, security and game helper trees", () =>
        {
            int session = Process.GetCurrentProcess().SessionId;
            ProcessIdentity Fixture(int id, string name, string path, int parent = 0, long ticks = 100) => new()
                { Id = id, Name = name, Path = path, ParentId = parent, StartTicks = ticks, SessionId = session };
            var snapshot = new List<ProcessIdentity>
            {
                Fixture(81001, "node.exe", @"C:\Tools\node.exe"), Fixture(81002, "worker.exe", @"C:\Tools\worker.exe"),
                Fixture(81003, "ExitLag.exe", @"C:\Program Files\ExitLag\ExitLag.exe"),
                Fixture(81004, "handler.exe", @"C:\Shared\handler.exe", 81003, 101),
                Fixture(81005, "Discord.exe", @"C:\Discord\Discord.exe"),
                Fixture(81006, "helper.exe", @"C:\Shared\helper.exe", 81005, 101),
                Fixture(81007, "agent.exe", @"C:\Program Files\Bitdefender\agent.exe"),
                Fixture(81008, "controller.exe", @"C:\Program Files\Razer\controller.exe"),
                Fixture(81009, "svchost.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "svchost.exe")),
                Fixture(81010, "package.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", "UnknownPackage", "package.exe")),
                Fixture(81011, "msedgewebview2.exe", @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe")
            };
            Check(ProcessPolicy.Candidates(snapshot, exists: _ => true, fortniteOnly: true).Count == 0, "Legacy profile changed.");
            var apps = ProcessPolicy.Candidates(snapshot, exists: _ => true, maximumHeadroom: true);
            Check(apps.SelectMany(app => app.Processes).Select(process => process.Id).Order().SequenceEqual(new[] { 81001, 81002 }), "Maximum headroom missed a runtime or targeted protected dependencies.");
            Check(!ProcessPolicy.IsMonitoredApp(apps.Single(app => app.Processes.Any(process => process.Id == 81001))), "Shared node path was globally guarded.");
            Check(ProcessPolicy.IsMonitoredApp(apps.Single(app => app.Processes.Any(process => process.Id == 81002))), "Dedicated background executable was not guarded.");
            snapshot[3].StartTicks = 99;
            Check(!HeadroomPolicy.DependencyIds(snapshot).Contains(81004), "Stale reused parent identity granted a false dependency.");
            return Task.CompletedTask;
        });
        await Test("maximum headroom task scope never includes security, updates, ExitLag or recovery", () =>
        {
            Check(HeadroomTasks.IsAllowed(@"\", "Spencer-CS2-GitHub-Runner"), "Known background runner missing.");
            foreach (var task in new[] { (@"\Microsoft\Windows\Windows Defender\", "Windows Defender Scheduled Scan"),
                (@"\Microsoft\Windows\UpdateOrchestrator\", "Schedule Scan"), (@"\", "ExitLag"), (@"\", "TourneyModeRecovery") })
                Check(!HeadroomTasks.IsAllowed(task.Item1, task.Item2), "Protected task allowed.");
            bool rejected = false;
            try { HeadroomTasks.SetEnabled(new ScheduledTaskBackup { TaskPath = @"\", TaskName = "Unowned", ActionHash = new string('a', 64), DisableAttempted = true }, false); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unowned task reached the Windows helper.");
            return Task.CompletedTask;
        });
        await Test("headroom holds services and tasks only after durable backup; crash restores exact startup without starting previously stopped apps", async () =>
        {
            var (engine, fake) = Engine("maximum-headroom");
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite);
            var options = OffOptions(); options.MaximumHeadroom = true; options.PauseOptionalServices = true;
            await engine.EnableAsync(options);
            Check(fake.ServiceStart.Values.All(value => value == 4) && !fake.TasksEnabled, "Optional startup resources were not held.");
            var recovered = new TourneyEngine(fake.Directory, fake);
            await recovered.RestoreAsync();
            Check(fake.ServiceStart["WSearch"] == 2 && fake.ServiceStart["Spooler"] == 3 && fake.ServiceRunning["WSearch"]
                && !fake.ServiceRunning["Spooler"] && fake.TasksEnabled && fake.Launches == 0 && fake.NativeStarts == 0, "Recovery changed original startup/running state or relaunched apps.");
        });
        await Test("headroom partial task failure retains recovery and restore retry does not replay app shutdown", async () =>
        {
            var (engine, fake) = Engine("task-retry"); var options = OffOptions(); options.MaximumHeadroom = true;
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite);
            fake.FailTaskDisable = true; await engine.EnableAsync(options);
            Check(!fake.TasksEnabled, "Partial task failure fixture did not mutate.");
            fake.FailTaskRestore = true; await ExpectFailure(engine.RestoreAsync);
            Check(File.Exists(Path.Combine(fake.Directory, "session.json")), "Failed task recovery discarded its journal.");
            fake.FailTaskRestore = false;
            await new TourneyEngine(fake.Directory, fake).RestoreAsync();
            Check(fake.TasksEnabled && fake.Launches == 0 && fake.Killed.Count == 0, "Recovery replayed shutdown or failed to restore tasks.");
        });
        await Test("Codex and Native trees stay protected across profiles and stale background selections", async () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            var gui = new ProcessIdentity { Id = 82001, Name = "ChatGPT.exe", Path = @"F:\Apps\OpenAI.Codex_fixture\app\ChatGPT.exe", StartTicks = 100, SessionId = sid };
            var worker = new ProcessIdentity { Id = 82002, ParentId = 82001, Name = "node.exe", Path = @"F:\Shared\node.exe", StartTicks = 101, SessionId = sid };
            var native = new ProcessIdentity { Id = 82003, Name = "node.exe", Path = @"F:\Profiles\AnotherUser\.spencer-native\app\runtime\node.exe", StartTicks = 100, SessionId = sid };
            var bridge = new ProcessIdentity { Id = 82004, Name = "BridgeSilentHost.exe", Path = @"F:\Tools\bridge\BridgeSilentHost.exe", StartTicks = 100, SessionId = sid };
            Check(ProcessPolicy.Candidates([gui, worker, native, bridge], exists: _ => true, maximumHeadroom: true).Count == 0,
                "Different drive/profile lost a protected connection or child.");
            foreach (string path in new[] { @"F:\Profiles\AnotherUser\.cache\codex-runtimes\runtime\node.exe",
                @"F:\Profiles\AnotherUser\AppData\Local\OpenAI\Codex\runtimes\node.exe" })
                Check(ConnectionProtection.IsRoot(new ProcessIdentity { Name = "node.exe", Path = path }),
                    "Orphaned Codex transport runtime became a generic background target.");
            var fake = new FakeSystem { Directory = Path.Combine(root, "codex-connections"), Processes = [gui, worker, native, bridge] };
            var app = new AppCandidate { Id = "background|" + gui.Path.ToLowerInvariant(), Name = "old selected app", LaunchPath = gui.Path, Processes = [gui, worker] };
            var engine = new TourneyEngine(fake.Directory, fake, () => [app]);
            var options = OffOptions(); options.MaximumHeadroom = true; options.KeepAppsClosed = true; options.SelectedApps = [app.Id];
            await engine.EnableAsync(options); await engine.MaintainAsync(); await engine.RestoreAsync();
            Check(fake.Killed.Count == 0 && fake.CloseCalls.Count == 0 && fake.Processes.Count == 4,
                "Old generic selection severed the Codex connection.");
        });
        await Test("per-PC keep list excludes exact roots and descendants without protecting reused parents", async () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            var parent = new ProcessIdentity { Id = 83001, Name = "chrome.exe", Path = @"F:\Apps\chrome.exe", StartTicks = 100, SessionId = sid };
            var child = new ProcessIdentity { Id = 83002, ParentId = parent.Id, Name = "worker.exe", Path = @"F:\Shared\worker.exe", StartTicks = 101, SessionId = sid };
            var paths = new[] { parent.Path };
            Check(ProcessPolicy.Candidates([parent, child], exists: _ => true, maximumHeadroom: true, protectedPaths: paths).Count == 0, "Keep-list descendants were exposed.");
            child.StartTicks = 99;
            Check(!ConnectionProtection.ExtraIds([parent, child], paths).Contains(child.Id), "Reused parent PID created a false keep dependency.");
            var fake = new FakeSystem { Directory = Path.Combine(root, "keep-activation"), Processes = [parent] };
            var app = new AppCandidate { Id = "chrome|" + parent.Path.ToLowerInvariant(), Name = "Chrome", LaunchPath = parent.Path, Processes = [parent] };
            var engine = new TourneyEngine(fake.Directory, fake, () => [app]);
            var options = OffOptions(); options.ProtectedAppPaths = paths.ToList(); options.SelectedApps = [app.Id];
            await engine.EnableAsync(options); await engine.RestoreAsync();
            Check(fake.Killed.Count == 0 && fake.CloseCalls.Count == 0, "Current keep-list preference lost to a stale selection.");
            Check(ConnectionProtection.NormalizePaths([parent.Path, parent.Path.ToUpperInvariant(), "relative.exe", "https://example.com/x.exe", "bad\0.exe"]).Count == 1,
                "Invalid or duplicate keep paths were accepted.");
        });
        await Test("adaptive defaults upgrade old preferences without losing exclusions or explicit choices", () =>
        {
            var fresh = ProfileDefaults.Create();
            Check(fresh.OptionsVersion == 9 && fresh.CloseCodex && fresh.MaximumHeadroom && fresh.KeepAppsClosed && !fresh.PauseOptionalServices
                && !fresh.HighPerformancePower && fresh.HighPriority && !fresh.PrioritizeNetwork && !fresh.TuneEthernet
                && fresh.ForceClose && fresh.FortniteOnly && fresh.GameTuning && fresh.SilenceNotifications && fresh.AutoRestoreOnGameExit,
                "Adaptive preset did not preserve power/network defaults.");
            var legacy = new AppOptions { OptionsVersion = 4, ExcludedApps = ["chrome|fixture"], SelectedApps = ["fixture"] };
            var updated = ProfileDefaults.Upgrade(legacy);
            Check(updated.ExcludedApps.Single() == "chrome|fixture" && updated.SelectedApps.Single() == "fixture" && !updated.HighPerformancePower && !updated.CloseCodex,
                "Upgrade lost choices or promoted legacy settings.");
            var explicitCurrent = ProfileDefaults.Create(); explicitCurrent.HighPerformancePower = false; explicitCurrent.CloseCodex = false;
            Check(!ProfileDefaults.Upgrade(explicitCurrent).HighPerformancePower && !explicitCurrent.CloseCodex, "Repeated migration overwrote a current explicit OFF choice.");
            return Task.CompletedTask;
        });
        await Test("experimental power plan is skipped on battery without affecting cleanup or recovery", async () =>
        {
            var (engine, fake) = Engine("battery"); fake.OnAcPower = false;
            var options = OffOptions(); options.HighPerformancePower = true;
            await engine.EnableAsync(options);
            Check(fake.Plan == FakeSystem.OriginalPlan && engine.State!.PerformancePlanGuid.Length == 0,
                "Battery operation received a power override.");
            await engine.RestoreAsync(); Check(!engine.IsActive && fake.DeletedPlans == 0, "Skipped battery plan left false recovery work.");
        });
        await Test("automatic restore waits for a game and ignores a quick game-client restart", async () =>
        {
            var (engine, fake) = Engine("auto-restore");
            var game = fake.Processes.Single(ProcessPolicy.IsFortnite); fake.Processes.Remove(game);
            var options = OffOptions(); options.AutoRestoreOnGameExit = true; options.GameTuning = true;
            await engine.EnableAsync(options); fake.Clock = fake.Clock.AddMinutes(10); await engine.MaintainAsync();
            Check(engine.IsActive, "Mode restored before the game ever ran.");
            fake.Processes.Add(game); await engine.MaintainAsync();
            fake.Processes.Remove(game); await engine.MaintainAsync(); fake.Clock = fake.Clock.AddSeconds(10);
            fake.Processes.Add(game); await engine.MaintainAsync(); fake.Processes.Remove(game); await engine.MaintainAsync();
            fake.Clock = fake.Clock.AddSeconds(14); await engine.MaintainAsync();
            Check(engine.IsActive, "Quick game restart did not cancel the old countdown.");
            fake.Clock = fake.Clock.AddSeconds(1); await engine.MaintainAsync();
            Check(!engine.IsActive && !fake.TuningApplied && fake.Launches == 0 && fake.Killed.Count == 0,
                "Automatic restore failed or launched/closed an application.");
        });
        await Test("crash recovery preserves the armed countdown without resuming automatic app closure", async () =>
        {
            Check(!new AppOptions().AutoRestoreOnGameExit && ProfileDefaults.Create().AutoRestoreOnGameExit,
                "Legacy defaults or fresh Fortnite defaults changed.");
            var (engine, fake) = Engine("auto-recovery"); var options = OffOptions(); options.AutoRestoreOnGameExit = true;
            await engine.EnableAsync(options); fake.Processes.RemoveAll(ProcessPolicy.IsFortnite); await engine.MaintainAsync();
            fake.Clock = fake.Clock.AddSeconds(16);
            var recovered = new TourneyEngine(fake.Directory, fake); await recovered.MaintainAsync();
            Check(recovered.State?.Phase == "Needs recovery" && recovered.State.SawFortnite
                && recovered.State.GameAbsentSinceUtc.HasValue && fake.WatchedScans == 0,
                "Reopened controller resumed active behavior or lost recorded countdown.");
            await recovered.RestoreAsync();
            Check(!recovered.IsActive && !File.Exists(Path.Combine(fake.Directory, "session.json")), "Explicit crash recovery did not finish.");
        });
        await Test("resource analysis normalizes machine CPU, deduplicates PIDs and rejects reused identities", () =>
        {
            var p = new ProcessIdentity { Id = 84001, StartTicks = 100 };
            var app = new AppCandidate { Name = "fixture", Processes = [p, p] };
            var first = new Dictionary<int, SystemAnalysis.ResourcePoint> { [p.Id] = new(100, 10, 1048576) };
            var second = new Dictionary<int, SystemAnalysis.ResourcePoint> { [p.Id] = new(100, 12, 8388608) };
            var sample = SystemAnalysis.Measure(app, first, second, 2, 4);
            Check(sample.CpuPercent == 25 && sample.PrivateMiB == 8 && sample.MeasuredProcesses == 1, "Resource sample double counted or used one-core CPU percentage.");
            second[p.Id] = new(101, 20, 8388608);
            Check(SystemAnalysis.Measure(app, first, second, 2, 4).MeasuredProcesses == 0, "Reused PID became a measurement.");
            Check(SystemAnalysis.Measure(app, first, second, 0, 0).MeasuredProcesses == 0, "Invalid interval created a measurement.");
            return Task.CompletedTask;
        });
        await Test("system advice responds to this PC's pressure without inventing a hardware upgrade or FPS gain", () =>
        {
            var report = new PcAnalysis { AcPower = false, MemoryLoadPercent = 90, BackgroundCapture = 1,
                Hardware = new PcFacts { Cpu = "AMD fixture", Gpus = [new GpuFact { Name = "Radeon fixture" }] },
                Volumes = [new VolumeFact { Drive = "F:\\", FreePercent = 5, FreeGiB = 10 }] };
            var advice = SystemAnalysis.Recommend(report);
            Check(advice.Any(a => a.Area == "Power") && advice.Any(a => a.Area == "Memory") && advice.Any(a => a.Area == "Storage")
                && advice.Any(a => a.Area == "Capture"), "Observed pressure was ignored.");
            Check(!HeadroomTasks.IsCaptured(@"\", "Spencer-CS2-GitHub-Runner") && HeadroomTasks.IsAllowed(@"\", "Spencer-CS2-GitHub-Runner"),
                "Personal task became universal or legacy task recovery was lost.");
            return Task.CompletedTask;
        });
        await Test("unknown apps remain available for explicit aggressive selection, not selected on another PC automatically", () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            var p = new ProcessIdentity { Id = 85001, Name = "customtool.exe", Path = @"F:\OtherUser\Tools\customtool.exe", StartTicks = 100, SessionId = sid };
            var app = ProcessPolicy.Candidates([p], exists: _ => true, maximumHeadroom: true).Single();
            Check(!app.DefaultSelected && app.Processes.Single().Id == p.Id, "Unrecognized app was silently selected or impossible to review.");
            return Task.CompletedTask;
        });
        await Test("audited headroom never targets core services or storage and input drivers", () =>
        {
            foreach (string name in new[] { "RpcSs", "DcomLaunch", "PlugPlay", "Power", "Audiosrv", "AudioEndpointBuilder",
                "Dhcp", "Dnscache", "nsi", "BFE", "WinDefend", "MDCoreSvc", "WdNisSvc", "Schedule", "wuauserv",
                "ExitLagPmService", "nvlddmkm", "iaStorVD", "stornvme", "HidUsb", "logi_lamparray", "WUDFRd" })
                Check(!TournamentResources.IsAllowedServiceName(name), "Protected resource became eligible: " + name);
            foreach (string name in new[] { "InventorySvc", "midisrv", "RstMwService", "smphost", "HvHost" })
                Check(TournamentResources.IsAllowedServiceName(name), "Audited feature service missing: " + name);
            foreach (string name in new[] { "SysMain", "PcaSvc", "Spooler", "StiSvc", "midisrv", "RstMwService" })
                Check(TournamentResources.IsAllowedServiceName(name) && !TournamentResources.CaptureNames(true).Contains(name),
                    "Legacy service recovery was lost or caching/compatibility became a new cleanup target: " + name);
            return Task.CompletedTask;
        });
        await Test("Windows helper ignores only serialized progress, never errors or unexpected stderr", () =>
        {
            string progress = "#< CLIXML\n<Objs xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\"><MS /></Obj></Objs>";
            Check(TournamentResources.IsOnlyProgress(progress), "First-use module progress was treated as a Windows failure.");
            Check(!TournamentResources.IsOnlyProgress(progress.Replace("progress", "Error")), "Serialized error was ignored.");
            Check(!TournamentResources.IsOnlyProgress(progress + "Unexpected failure"), "Unparseable stderr was ignored.");
            Check(!TournamentResources.IsOnlyProgress("Access denied"), "Plain stderr was ignored.");
            return Task.CompletedTask;
        });
        await Test("maximum headroom journals the whole close batch and shares one grace period", async () =>
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, "batch-close") };
            var first = fake.Processes[0];
            var second = new ProcessIdentity { Id = 104, Name = "worker.exe", Path = @"C:\FixtureApps\worker.exe", StartTicks = 4, SessionId = first.SessionId };
            fake.Processes.Add(second);
            var apps = new List<AppCandidate> { new() { Id = "first", Name = "First", LaunchPath = first.Path, Processes = [first] },
                new() { Id = "second", Name = "Second", LaunchPath = second.Path, Processes = [second] } };
            fake.BeforeClose = _ =>
            {
                var saved = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(Path.Combine(fake.Directory, "session.json")))!;
                Check(saved.Apps.All(item => item.CloseAttempted), "One app was closed before the complete batch was journaled.");
            };
            var engine = new TourneyEngine(fake.Directory, fake, () => apps);
            var options = OffOptions(); options.MaximumHeadroom = true; options.SelectedApps = apps.Select(app => app.Id).ToList();
            await engine.EnableAsync(options);
            Check(fake.Killed.Order().SequenceEqual(new[] { 101, 104 }) && fake.GraceDelays == 16, "Batch missed a target or waited separately for each app.");
            await engine.RestoreAsync();
            Check(fake.Launches == 0 && fake.Processes.Any(process => process.Id == 102), "Batch recovery relaunched apps or closed Discord.");
        });
        await Test("Codex ownership excludes kept descendants, reused parents and other sessions", () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            var rootProcess = new ProcessIdentity { Id = 90101, StartTicks = 100, SessionId = sid };
            var child = new ProcessIdentity { Id = 90102, ParentId = 90101, StartTicks = 101, SessionId = sid };
            var reused = new ProcessIdentity { Id = 90103, ParentId = 90101, StartTicks = 99, SessionId = sid };
            var foreign = new ProcessIdentity { Id = 90104, ParentId = 90101, StartTicks = 101, SessionId = sid + 1 };
            var kept = new ProcessIdentity { Id = 90105, ParentId = 90101, StartTicks = 101, SessionId = sid };
            var ids = CodexLifecycle.OwnedIds([rootProcess, child, reused, foreign, kept], [90101], new HashSet<int> { 90105 });
            Check(ids.SetEquals([90101, 90102]), "Shutdown ownership crossed a keep/session/identity boundary.");
            Check(!CodexLifecycle.ValidBridge("node.exe", []), "Relative/unknown bridge launch was accepted.");
            return Task.CompletedTask;
        });
        await Test("Codex restart starts Native, bridge and phone before desktop; retries never duplicate restored hosts", async () =>
        {
            var backup = new CodexBackup { NativeStopAttempted = true, Launchers = [
                new() { Kind = "Desktop", StopAttempted = true }, new() { Kind = "Mobile", StopAttempted = true },
                new() { Kind = "Bridge", StopAttempted = true }] };
            var running = new HashSet<string>();
            var order = new List<string>();
            int saves = 0;
            Task nativeStart() { order.Add("Native"); return Task.CompletedTask; }
            void start(CodexLaunch item) { order.Add(item.Kind); running.Add(item.Kind); }
            await CodexLifecycle.RestoreWithAsync(backup, () => saves++, _ => { }, nativeStart,
                item => running.Contains(item.Kind), start, () => Task.CompletedTask);
            Check(order.SequenceEqual(new[] { "Native", "Bridge", "Mobile", "Desktop" }) && saves == 4,
                "Desktop reopened before its providers or restart progress was not saved.");
            await CodexLifecycle.RestoreWithAsync(backup, () => saves++, _ => { }, nativeStart,
                item => running.Contains(item.Kind), start, () => Task.CompletedTask);
            Check(order.Count == 4, "Recovery retry duplicated already restored hosts.");
        });
        await Test("unobserved bridge restart blocks desktop and can recover on retry", async () =>
        {
            var backup = new CodexBackup { Launchers = [
                new() { Kind = "Desktop", StopAttempted = true }, new() { Kind = "Bridge", StopAttempted = true }] };
            var starts = new List<string>(); bool bridgeReady = false;
            await ExpectFailure(() => CodexLifecycle.RestoreWithAsync(backup, () => { }, _ => { }, () => Task.CompletedTask,
                item => item.Kind == "Bridge" && bridgeReady, item => starts.Add(item.Kind), () => Task.CompletedTask));
            Check(starts.SequenceEqual(new[] { "Bridge" }) && backup.Launchers.All(l => !l.Restored),
                "Failed bridge restart was accepted or desktop inherited unavailable transports.");
            bridgeReady = true;
            await CodexLifecycle.RestoreWithAsync(backup, () => { }, _ => { }, () => Task.CompletedTask,
                item => item.Kind == "Bridge" ? bridgeReady : starts.Contains(item.Kind),
                item => starts.Add(item.Kind), () => Task.CompletedTask);
            Check(starts.SequenceEqual(new[] { "Bridge", "Desktop" }) && backup.Launchers.All(l => l.Restored),
                "Retry restarted a ready provider or failed to reopen desktop.");
        });
        await Test("Native start refusal blocks all dependent launches", async () =>
        {
            var backup = new CodexBackup { NativeStopAttempted = true, Launchers = [new() { Kind = "Desktop", StopAttempted = true }] };
            int starts = 0;
            await ExpectFailure(() => CodexLifecycle.RestoreWithAsync(backup, () => { }, _ => { },
                () => Task.FromException(new Exception("Native unavailable")), _ => false, _ => starts++, () => Task.CompletedTask));
            Check(starts == 0 && !backup.NativeRestored, "Dependent host launched after Native failure.");
        });
        await Test("explicit Codex shutdown is journaled before mutation and survives crash recovery", async () =>
        {
            var (engine, fake) = Engine("codex-explicit-recovery");
            fake.CodexFixture = new CodexBackup { Launchers = [new() { Kind = "Desktop", Path = "fixture" }] };
            var options = OffOptions(); options.CloseCodex = true;
            await engine.EnableAsync(options);
            Check(fake.CodexStops == 1 && engine.State!.Codex!.Launchers[0].StopAttempted, "Explicit shutdown did not record restart state.");
            fake.FailCodexRestore = true;
            await ExpectFailure(() => new TourneyEngine(fake.Directory, fake).RestoreAsync());
            Check(File.Exists(Path.Combine(fake.Directory, "session.json")), "Failed restart lost the recovery journal.");
            fake.FailCodexRestore = false;
            await new TourneyEngine(fake.Directory, fake).RestoreAsync();
            Check(!File.Exists(Path.Combine(fake.Directory, "session.json")) && fake.CodexRestores == 2
                && fake.Launches == 0, "Recovery failed or relaunched unrelated closed apps.");
        });
        await Test("Codex OFF option performs no lifecycle shutdown and old journals do not acquire it", async () =>
        {
            var (engine, fake) = Engine("codex-disabled");
            await engine.EnableAsync(OffOptions()); await engine.RestoreAsync();
            Check(fake.CodexStops == 0 && fake.CodexRestores == 0 && !new AppOptions().CloseCodex,
                "Unrequested connection shutdown or legacy recovery semantics changed.");
        });
        await Test("orphaned renderers never count as a restored Codex desktop", () =>
        {
            int sid = Process.GetCurrentProcess().SessionId;
            var rootProcess = new ProcessIdentity { Id = 90201, StartTicks = 100, SessionId = sid, Path = @"F:\Apps\Codex Web GPT.exe" };
            var helper = new ProcessIdentity { Id = 90202, ParentId = rootProcess.Id, StartTicks = 101, SessionId = sid, Path = rootProcess.Path };
            var launch = new CodexLaunch { Kind = "Desktop", Path = rootProcess.Path, Processes = [rootProcess, helper] };
            Check(!CodexLifecycle.DesktopReturned(launch, [helper], _ => []), "Surviving original helper was accepted as desktop.");
            helper.Id++; helper.StartTicks++;
            Check(!CodexLifecycle.DesktopReturned(launch, [helper], _ => ["--type=renderer"]), "New orphan renderer was accepted as desktop.");
            Check(CodexLifecycle.DesktopReturned(launch, [rootProcess], _ => []), "Returning root was rejected.");
            return Task.CompletedTask;
        });
        await Test("actual isolated desktop fixture closes and relaunches through the production lifecycle", async () =>
        {
            string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
            Check(File.Exists(compiler), "Windows C# fixture compiler missing; integration test was not run.");
            string folder = Path.Combine(root, "isolated-codex-restart"); Directory.CreateDirectory(folder);
            string exe = Path.Combine(folder, "Codex Web GPT.exe");
            string source = Path.Combine(folder, "Fixture.cs");
            string marker = Path.Combine(folder, "starts.txt");
            File.Delete(marker);
            File.WriteAllText(source, """using System; using System.IO; using System.Threading; class Fixture { static void Main() { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "starts.txt"), "start\n"); Thread.Sleep(60000); } }""");
            var compile = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "/nologo", "/target:winexe", "/out:" + exe, source }) compile.ArgumentList.Add(arg);
            using (var build = Process.Start(compile)!)
            {
                string output = await build.StandardOutput.ReadToEndAsync(); string error = await build.StandardError.ReadToEndAsync();
                await build.WaitForExitAsync(); Check(build.ExitCode == 0, "Fixture compile failed: " + output + error);
            }
            var backup = new CodexBackup();
            try
            {
                using var child = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true })!;
                for (int i = 0; i < 40 && !File.Exists(marker); i++) await Task.Delay(50);
                var identity = ProcessCatalog.Snapshot().Single(p => p.Id == child.Id);
                backup.Targets = [identity];
                backup.Launchers = [new() { Kind = "Desktop", Path = exe, Processes = [identity] }];
                int saves = 0;
                await CodexLifecycle.StopAsync(backup, true, () => saves++, _ => { });
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await CodexLifecycle.RestoreAsync(backup, () => saves++, _ => { });
                for (int i = 0; i < 40 && File.ReadAllLines(marker).Length < 2; i++) await Task.Delay(50);
                Check(backup.Launchers.Single().Restored && File.ReadAllLines(marker).Length == 2 && saves >= 2,
                    "Production close/relaunch did not restore the isolated desktop exactly once.");
            }
            finally
            {
                foreach (var identity in ProcessCatalog.Snapshot().Where(p => p.Path.Equals(exe, StringComparison.OrdinalIgnoreCase)))
                {
                    using var child = Process.GetProcessById(identity.Id);
                    if (ProcessCatalog.MatchesPinned(child, identity)) { child.Kill(false); await child.WaitForExitAsync(); }
                }
            }
        });
        await Test("reviewed plans reject stale scope before journal or mutations", async () =>
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, "plan-stale") };
            var report = new PcAnalysis { AcPower = true, ActivePowerPlan = FakeSystem.OriginalPlan };
            report.Hardware.Cpu = "Fixture desktop"; report.Hardware.Cores = 8;
            var engine = new TourneyEngine(fake.Directory, fake, analysis: _ => Task.FromResult(report));
            var options = ProfileDefaults.Create(); options.CloseCodex = false;
            var plan = await engine.AnalyzePlanAsync(options);
            options = PerformancePlanner.Apply(plan, options); options.ForceClose = !options.ForceClose;
            await ExpectFailure(() => engine.EnableAsync(options));
            Check(!engine.IsActive && !File.Exists(Path.Combine(fake.Directory, "session.json"))
                && fake.Killed.Count == 0 && !fake.TuningApplied, "Stale plan reached a mutation.");
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite);
            options = ProfileDefaults.Create(); options.Intent.Power = PowerPreference.Sustained;
            options = PerformancePlanner.Apply(await engine.AnalyzePlanAsync(options), options);
            Check(options.HighPerformancePower, "Fixture did not prepare an eligible power request.");
            options.HighPerformancePower = false;
            await ExpectFailure(() => engine.EnableAsync(options));
            Check(!engine.IsActive && fake.Plan == FakeSystem.OriginalPlan, "Changed plan-controlled switch was silently re-enabled.");
        });
        await Test("match-safe reviewed activation defers disruptive preparation", async () =>
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, "plan-match-safe") };
            var report = new PcAnalysis { AcPower = true, ActivePowerPlan = FakeSystem.OriginalPlan };
            report.Hardware.Cpu = "Fixture desktop"; report.Hardware.Cores = 8;
            var engine = new TourneyEngine(fake.Directory, fake, analysis: _ => Task.FromResult(report));
            var options = ProfileDefaults.Create(); options.CloseCodex = false;
            options.PauseOptionalServices = true; options.Intent.Power = PowerPreference.Maximum; options.Intent.AllowHigherHeat = true;
            options = PerformancePlanner.Apply(await engine.AnalyzePlanAsync(options), options);
            await engine.EnableAsync(options);
            Check(fake.Plan == FakeSystem.OriginalPlan && fake.ResourceCaptures == 0 && !fake.Dnd
                && fake.Priority == (int)ProcessPriorityClass.High, "Match-safe mode performed disruptive preparation.");
            await engine.RestoreAsync(); Check(engine.RecoveryDeferredUntilGameExit, "Quiet match recovery did not defer.");
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite);
            await engine.RestoreAsync(); Check(!engine.IsActive, "Match-safe originals failed recovery.");
        });
        await Test("NIC recovery waits for game exit and retains durable pending originals", async () =>
        {
            var (engine, fake) = Engine("restore-deferred");
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite);
            var options = OffOptions(); options.TuneEthernet = true;
            await engine.EnableAsync(options);
            fake.Processes.Add(new() { Id = 999, Name = "FortniteClient-Win64-Shipping.exe", Path = @"C:\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", StartTicks = 9, SessionId = Process.GetCurrentProcess().SessionId });
            await engine.RestoreAsync();
            Check(engine.RecoveryDeferredUntilGameExit && fake.AdapterRestores == 0
                && File.Exists(Path.Combine(fake.Directory, "session.json")) && !engine.State!.EthernetAdapter!.Restored,
                "In-game restoration reconnected NIC or lost recovery.");
            var recovered = new TourneyEngine(fake.Directory, fake);
            Check(recovered.RecoveryDeferredUntilGameExit, "Deferred state lost on reopen.");
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite); await recovered.RestoreAsync();
            Check(!recovered.IsActive && fake.AdapterRestores == 1 && File.Exists(Path.Combine(fake.Directory, "last-session.json")), "Deferred restoration failed to finish once game exited.");
        });
        await Test("recording preserves capture values through activation and recovery", async () =>
        {
            var (engine, fake) = Engine("recording-capture"); fake.IncludeCaptureSettings = true;
            var options = OffOptions(); options.GameTuning = true; options.Intent.Activity = SessionActivity.Recording;
            await engine.EnableAsync(options);
            Check(fake.PreserveCaptureRequested == true && engine.State!.Tuning.All(value => value.Name is not ("AppCaptureEnabled" or "GameDVR_Enabled")),
                "Capture settings were requested or journaled for later overwrite during recording.");
            await engine.RestoreAsync(); Check(!engine.IsActive, "Recording recovery did not finish.");
        });
        await Test("guard limits each sweep to eight returning identities and prunes dead history", async () =>
        {
            var (engine, fake, app) = WatchEngine("guard-bounded");
            await engine.EnableAsync(WatchOptions(app));
            var original = app.Processes[0];
            for (int i = 0; i < 12; i++) fake.Processes.Add(new() { Id = 600 + i, Name = original.Name, Path = original.Path, SessionId = original.SessionId, StartTicks = 60 + i });
            int before = fake.Killed.Count;
            fake.BeforeClose = identity =>
            {
                var journal = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(Path.Combine(fake.Directory, "session.json")))!;
                Check(journal.Apps.Single().App.Processes.Any(p => p.Id == identity.Id && p.StartTicks == identity.StartTicks), "Guard closed before batch journal.");
            };
            await engine.MaintainAsync(); Check(fake.Killed.Count - before == 8, "Guard sweep did not respect eight-process budget.");
            await engine.MaintainAsync(); Check(fake.Killed.Count - before == 12 && engine.State!.Apps.Single().App.Processes.Count <= 5, "Dead guard identities accumulated or remaining apps did not close.");
            await engine.RestoreAsync();
        });
        await Test("new legacy activations are rejected outside the explicit fixture seam", async () =>
        {
            var (engine, fake) = Engine("legacy-reject"); fake.LegacyAllowed = false;
            await ExpectFailure(() => engine.EnableAsync(OffOptions()));
            Check(!engine.IsActive && fake.Killed.Count == 0 && !fake.TuningApplied, "Legacy activation bypassed current review.");
        });
        await Test("quiet game mode pauses guard and priority rewrites then defers heavy recovery", async () =>
        {
            var (_, fake, app) = WatchEngine("fps-quiet");
            var report = new PcAnalysis { AcPower = true, ActivePowerPlan = FakeSystem.OriginalPlan };
            report.Hardware.Cpu = "Fixture"; report.Hardware.Cores = 8;
            var engine = new TourneyEngine(fake.Directory, fake, () => [app], _ => Task.FromResult(report));
            var options = ProfileDefaults.Create(); options.CloseCodex = false; options.SelectedApps = [app.Id];
            options.Intent.Fps.GamePriority = FortnitePriority.AboveNormal;
            options = PerformancePlanner.Apply(await engine.AnalyzePlanAsync(options), options);
            await engine.EnableAsync(options);
            Check(fake.Priority == (int)ProcessPriorityClass.AboveNormal, "Reviewed Above normal was not applied.");
            var original = app.Processes[0];
            fake.Processes.Add(new() { Id = 990, Name = original.Name, Path = original.Path, StartTicks = 990, SessionId = original.SessionId });
            int writes = fake.PriorityAttempts; fake.Priority = (int)ProcessPriorityClass.Normal;
            await engine.MaintainAsync();
            Check(fake.WatchedScans == 0 && fake.Processes.Any(p => p.Id == 990) && fake.PriorityAttempts == writes,
                "Quiet mode scanned/closed returning apps or fought a later priority change.");
            await engine.RestoreAsync(); Check(engine.RecoveryDeferredUntilGameExit, "Recovery restarted workloads during the game.");
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite); await engine.RestoreAsync(); Check(!engine.IsActive, "Quiet recovery did not finish.");
        });
        await Test("unknown game presence blocks preparation without falsely arming game-exit restore", async () =>
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, "fps-unknown"), PresenceOverride = GamePresence.Unknown };
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite);
            var report = new PcAnalysis { AcPower = true, ActivePowerPlan = FakeSystem.OriginalPlan };
            var engine = new TourneyEngine(fake.Directory, fake, analysis: _ => Task.FromResult(report));
            var options = ProfileDefaults.Create(); options.CloseCodex = false; options.HighPriority = false;
            options = PerformancePlanner.Apply(await engine.AnalyzePlanAsync(options), options);
            await engine.EnableAsync(options); await engine.MaintainAsync();
            Check(!engine.State!.SawFortnite && fake.ResourceCaptures == 0, "Unknown presence became a verified game sighting or allowed preparation.");
            fake.PresenceOverride = GamePresence.Absent; fake.Clock = fake.Clock.AddSeconds(30); await engine.MaintainAsync();
            Check(engine.IsActive, "An unknown-only observation triggered game-exit recovery.");
            await engine.RestoreAsync();
        });
        await Test("game profile is journaled before file mutation and merges unrelated game-exit edits on recovery", async () =>
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, "fps-file") }; fake.Processes.RemoveAll(ProcessPolicy.IsFortnite);
            Directory.CreateDirectory(fake.Directory); fake.GameConfigPath = Path.Combine(fake.Directory, "GameUserSettings.ini");
            File.WriteAllText(fake.GameConfigPath, "[/Script/FortniteGame.FortGameUserSettings]\r\nFrameRateLimit=0.000000\r\nbMotionBlur=True\r\n");
            var report = new PcAnalysis { AcPower = true, ActivePowerPlan = FakeSystem.OriginalPlan,
                GameSettingsFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fake.GameConfigPath))) };
            var engine = new TourneyEngine(fake.Directory, fake, analysis: _ => Task.FromResult(report));
            var options = OffOptions(); options.OptionsVersion = 9;
            options.Intent.Fps = new() { ApplyGameProfile = true, FrameCap = FortniteFrameCap.Target, TargetFps = 240 };
            options = PerformancePlanner.Apply(await engine.AnalyzePlanAsync(options), options);
            await engine.EnableAsync(options);
            Check(File.ReadAllText(fake.GameConfigPath).Contains("FrameRateLimit=240.000000") && engine.State!.FortniteSettings!.ApplyAttempted,
                "Actual fixture game settings were not applied with durable originals.");
            fake.PresenceOverride = GamePresence.Present; await engine.RestoreAsync();
            Check(engine.RecoveryDeferredUntilGameExit && File.ReadAllText(fake.GameConfigPath).Contains("240.000000"), "Live game settings recovery was not deferred.");
            File.AppendAllText(fake.GameConfigPath, "[Other]\r\nKeep=yes\r\n"); fake.PresenceOverride = GamePresence.Absent;
            await engine.RestoreAsync();
            var restored = File.ReadAllText(fake.GameConfigPath);
            Check(!engine.IsActive && restored.Contains("FrameRateLimit=0.000000") && restored.Contains("Keep=yes"), "Profile recovery lost originals or unrelated edits.");
        });
        await Test("Fortnite appearing after clone preparation stops power activation", async () =>
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, "fps-power-race") }; fake.Processes.RemoveAll(ProcessPolicy.IsFortnite);
            fake.AfterCreatePlan = () => fake.PresenceOverride = GamePresence.Present;
            var report = new PcAnalysis { AcPower = true, ActivePowerPlan = FakeSystem.OriginalPlan };
            report.Hardware.Cpu = "Fixture"; report.Hardware.Cores = 8;
            var engine = new TourneyEngine(fake.Directory, fake, analysis: _ => Task.FromResult(report));
            var options = OffOptions(); options.OptionsVersion = 9; options.Intent.Power = PowerPreference.Sustained;
            options = PerformancePlanner.Apply(await engine.AnalyzePlanAsync(options), options);
            await ExpectFailure(() => engine.EnableAsync(options));
            Check(engine.IsActive && fake.Plan == FakeSystem.OriginalPlan, "A late game launch did not stop power activation.");
            fake.PresenceOverride = GamePresence.Absent; await engine.RestoreAsync();
        });
        await Test("reviewed Fortnite priority recovery preserves external changes", async () =>
        {
            var fake = new FakeSystem { Directory = Path.Combine(root, "fps-priority-conflict") };
            var report = new PcAnalysis { AcPower = true, ActivePowerPlan = FakeSystem.OriginalPlan };
            var engine = new TourneyEngine(fake.Directory, fake, analysis: _ => Task.FromResult(report));
            var options = OffOptions(); options.OptionsVersion = 9; options.HighPriority = true;
            options.Intent.Fps.QuietDuringGame = false;
            options = PerformancePlanner.Apply(await engine.AnalyzePlanAsync(options), options);
            await engine.EnableAsync(options); fake.Priority = (int)ProcessPriorityClass.AboveNormal;
            await ExpectFailure(engine.RestoreAsync);
            Check(engine.IsActive && fake.Priority == (int)ProcessPriorityClass.AboveNormal, "Priority recovery overwrote an external choice.");
            fake.Processes.RemoveAll(ProcessPolicy.IsFortnite); await engine.RestoreAsync();
            Check(!engine.IsActive, "An exited game identity stranded priority recovery.");
        });
        await Test("FPS settings and background choices invalidate review and reject unsafe values", () =>
        {
            var report = new PcAnalysis(); var options = OffOptions(); options.OptionsVersion = 9;
            string original = PerformancePlanner.Build(report, options, false, false).InventoryFingerprint;
            options.Intent.Fps.FrameCap = FortniteFrameCap.Target; options.Intent.Fps.TargetFps = 200;
            Check(PerformancePlanner.Build(report, options, false, false).InventoryFingerprint != original, "Actual cap selection escaped review binding.");
            options.Intent.Fps.TargetFps = 0;
            bool rejected = false; try { PerformancePlanner.Build(report, options, false, false); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "An invalid game cap was accepted.");
            return Task.CompletedTask;
        });
        await NetworkSafetyTests.RunAsync(Test);
        await PlannerTests.RunAsync(Test);
        await FrameDiagnosticsTests.RunAsync(Test);
        await FortniteSettingsTests.RunAsync(Test);
        await BackgroundGovernorTests.RunAsync(Test);
        await TweakWorkflowTests.RunAsync(Test);
        await AppUpdaterTests.RunAsync(Test);
        await WindowsStripTests.RunAsync(Test);
        await WindowsProfileTests.RunAsync(Test);
        await OOSPrivacyTests.RunAsync(Test);
        await OptimizationSessionTests.RunAsync(Test);
        await OptimizationGuideTests.RunAsync(Test);
        await InternetOptimizerTests.RunAsync(Test);
        await InternetNativeHelperTests.RunAsync(Test);
        await Test("standalone embeds exact redistribution license texts", () =>
        {
            string notices = ThirdPartyNotices.ReadAll();
            Check(notices.Contains("dotnet-runtime-LICENSE.txt") && notices.Contains("dotnet-runtime-THIRD-PARTY-NOTICES.txt")
                && notices.Contains("windowsdesktop-LICENSE.txt") && notices.Contains("PresentMon-LICENSE.txt")
                && notices.Contains("Permission is hereby granted, free of charge") && notices.Contains("Intel Corporation"),
                "Required redistribution text is missing from the standalone executable.");
            return Task.CompletedTask;
        });
        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed. Live apps/settings were not changed (isolated test helpers only).");
        File.WriteAllLines(output, lines);
        return failed == 0 ? 0 : 1;
    }

    private sealed class FakeSystem : ISystemFacade
    {
        public bool LegacyAllowed = true;
        public bool AllowLegacyFixtureActivation => LegacyAllowed;
        public GamePresence? PresenceOverride;
        public GamePresence GetGamePresence() => PresenceOverride ?? (Games().Any() ? GamePresence.Present : GamePresence.Absent);
        public string? GameConfigPath;
        public Action? AfterCreatePlan;
        public FortniteSettingsBackup? CaptureFortniteSettings(FpsLatencyOptions options, bool nvidia, bool tearing)
            => GameConfigPath is null ? null : FortniteSettings.Preview(GameConfigPath, options, nvidia, tearing);
        public void ApplyFortniteSettings(FortniteSettingsBackup backup, Action persist)
            => FortniteSettings.Apply(backup, () => { Journal(); persist(); }, () => GetGamePresence() == GamePresence.Absent);
        public void RestoreFortniteSettings(FortniteSettingsBackup backup, Action persist)
            => FortniteSettings.Restore(backup, persist, () => GetGamePresence() == GamePresence.Absent);
        private static readonly string FixtureExe = Path.Combine(Path.GetTempPath(), "TourneyMode-Fixtures", Guid.NewGuid().ToString("N"), "chrome.exe");
        public const string OriginalPlan = "95210f1b-2f91-4d29-8471-0830383b0958";
        public string Directory = "";
        public List<ProcessIdentity> Processes = [];
        public List<int> Killed = [];
        public int Launches, DeletedPlans;
        public int NativeStarts;
        public CodexBackup CodexFixture = new();
        public int CodexStops, CodexRestores;
        public bool FailCodexRestore;
        public CodexBackup CaptureCodex(IEnumerable<string> keep) => CodexFixture;
        public Task StopCodex(CodexBackup backup, bool force, Action save, Action<string> log)
        {
            Journal();
            var persisted = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(Path.Combine(Directory, "session.json")))!;
            if (persisted.Codex is null || !persisted.Options.CloseCodex) throw new Exception("Codex shutdown before backup.");
            foreach (var item in backup.Launchers) item.StopAttempted = true;
            save(); CodexStops++; return Task.CompletedTask;
        }
        public Task RestoreCodex(CodexBackup backup, Action save, Action<string> log)
        {
            Journal(); CodexRestores++;
            if (FailCodexRestore) throw new Exception("Simulated unobserved Codex restart");
            foreach (var item in backup.Launchers) item.Restored = true;
            save(); return Task.CompletedTask;
        }
        public int AppReadinessChecks, MobileReadinessChecks;
        public int WatchedScans;
        public List<int> CloseCalls = [];
        public Action<ProcessIdentity>? BeforeClose;
        public Action? BeforeLaunch;
        public Action? BeforeRestoreTuning;
        public bool FailNativeStop, ProviderReady = true, MobileIsReady = true;
        public bool OnAcPower = true;
        public DateTime Clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => Clock;
        public bool AcPowerAvailable() => OnAcPower;
        public bool NotificationsSuppressed, TuningApplied, Dnd, DndReadable = true, FailTuningApply, FailTuningRestore, FailLaunch, UnobservedLaunch;
        public bool IncludeCaptureSettings;
        public bool? PreserveCaptureRequested;
        public string Plan = OriginalPlan;
        public int Priority = (int)ProcessPriorityClass.Normal;
        public bool DenyPriority;
        public int PriorityAttempts;
        public int ResourceCaptures, NetworkApplies, NetworkRestores, AdapterApplies, AdapterRestores;
        public readonly Dictionary<string, bool> ServiceRunning = new(StringComparer.OrdinalIgnoreCase)
            { ["WSearch"] = true, ["Spooler"] = false };
        public readonly List<string> ServiceStops = [], ServiceRestores = [];
        public readonly Dictionary<string, int> ServiceStart = new() { ["WSearch"] = 2, ["Spooler"] = 3 };
        public bool TasksEnabled = true, FailTaskDisable, FailTaskRestore;
        public int GraceDelays;
        public string ActivePolicyName = "";
        public readonly Dictionary<string, string[]> AdapterValues = new(StringComparer.OrdinalIgnoreCase)
            { ["*EEE"] = ["1"], ["*InterruptModeration"] = ["1"], ["*Rss"] = ["0"] };
        public bool FailServiceStop, FailNetworkApply, FailAdapterApply, FailNetworkRestore;

        public FakeSystem()
        {
            int sid = Process.GetCurrentProcess().SessionId;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FixtureExe)!);
            if (!File.Exists(FixtureExe)) File.WriteAllText(FixtureExe, "Test fixture only; never execute.");
            Processes = [new() { Id = 101, Name = "chrome.exe", Path = FixtureExe, SessionId = sid, StartTicks = 1 },
                new() { Id = 102, Name = "Discord.exe", Path = Path.Combine(Path.GetTempPath(), "TourneyMode-FixtureApps", "FixtureDiscord.exe"), SessionId = sid, StartTicks = 2 },
                new() { Id = 103, Name = "FortniteClient-Win64-Shipping.exe", Path = @"C:\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", SessionId = sid, StartTicks = 3 }];
        }
        private void Journal() { if (!File.Exists(Path.Combine(Directory, "session.json"))) throw new Exception("Mutation before journal persisted."); }
        public List<ProcessIdentity> Snapshot() => Processes.ToList();
        public List<ProcessIdentity> Games() => Processes.Where(ProcessPolicy.IsFortnite).ToList();
        public List<ProcessIdentity> WatchedProcesses(List<AppCandidate> apps) { WatchedScans++; return Processes.ToList(); }
        public bool Alive(ProcessIdentity p) => Processes.Any(q => q.Id == p.Id && q.StartTicks == p.StartTicks && q.SessionId == p.SessionId && q.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase));
        public bool CanTouch(ProcessIdentity p) => Alive(p) && !ProcessPolicy.Protected(p, Process.GetCurrentProcess().SessionId);
        public void CloseWindow(ProcessIdentity p) { Journal(); CloseCalls.Add(p.Id); BeforeClose?.Invoke(p); }
        public void Terminate(ProcessIdentity p) { Journal(); if (!Alive(p)) throw new Exception("Stale identity"); Killed.Add(p.Id); Processes.RemoveAll(q => q.Id == p.Id); }
        public int GetPriority(ProcessIdentity p) => Priority;
        public void SetPriority(ProcessIdentity p, int priority) { Journal(); PriorityAttempts++; if (DenyPriority) throw new System.ComponentModel.Win32Exception(5); Priority = priority; }
        public bool Launch(AppCandidate app)
        {
            Journal(); if (FailLaunch) throw new Exception("Simulated launcher missing"); BeforeLaunch?.Invoke(); Launches++;
            if (app.Id == "native-mobile") return true;
            if (!UnobservedLaunch)
            { var p = app.Processes.First(); Processes.Add(new ProcessIdentity { Id = 300 + Launches, Name = p.Name, Path = p.Path, SessionId = p.SessionId, StartTicks = 1000 + Launches }); }
            return true;
        }
        public List<NotificationBackup> CaptureNotifications() => [new() { Key = "fake-notification", Name = "Value", Existed = false }];
        public void SuppressNotifications() { Journal(); NotificationsSuppressed = true; }
        public void RestoreNotification(NotificationBackup item) { Journal(); NotificationsSuppressed = false; }
        public string GetPowerScheme() => Plan;
        public void SetPowerScheme(string guid) { Journal(); Plan = guid; }
        public bool? GetDnd() => DndReadable ? Dnd : null;
        public void SetDnd(bool enabled) { Journal(); Dnd = enabled; }
        public void SetRecoveryStartup(bool enabled) { if (enabled) Journal(); }
        public List<NotificationBackup> CaptureTuning() => IncludeCaptureSettings
            ? [new() { Key = "fake-game", Name = "AutoGameModeEnabled", Existed = true, Value = 1 },
               new() { Key = "fake-capture", Name = "AppCaptureEnabled", Existed = true, Value = 1 },
               new() { Key = "fake-capture", Name = "GameDVR_Enabled", Existed = true, Value = 1 }]
            : [new() { Key = "fake-game", Name = "Value", Existed = true, Value = 7 }];
        public void ApplyGameTuning(bool preserveCapture) { PreserveCaptureRequested = preserveCapture; ApplyTuning(); }
        public void ApplyTuning() { Journal(); TuningApplied = true; if (FailTuningApply) throw new Exception("Simulated partial settings failure"); }
        public void RestoreTuning(NotificationBackup item) { Journal(); BeforeRestoreTuning?.Invoke(); if (FailTuningRestore) throw new Exception("Simulated settings restore failure"); TuningApplied = false; }
        public List<string> CreatePlan(string original, string destination, bool fortniteOnly) { Journal(); AfterCreatePlan?.Invoke(); return []; }
        public void DeletePlan(string guid) { Journal(); DeletedPlans++; }
        public Task StopNative() { Journal(); if (FailNativeStop) throw new Exception("Simulated busy Native refusal"); return Task.CompletedTask; }
        public Task StartNative() { Journal(); NativeStarts++; return Task.CompletedTask; }
        public bool NativeReady() => ProviderReady;
        public bool MobileReady() { MobileReadinessChecks++; return MobileIsReady; }
        public bool AppReady(AppCandidate app) { AppReadinessChecks++; return TourneyEngine.AppReturned(app, Snapshot()); }
        private SessionState ResourceJournal()
        {
            Journal();
            return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(Path.Combine(Directory, "session.json")))!;
        }
        public List<ServiceBackup> CaptureOptionalServices(bool maximumHeadroom = false)
        {
            ResourceCaptures++;
            return ServiceRunning.Select(pair => new ServiceBackup { Name = pair.Key, WasRunning = pair.Value,
                HoldDisabled = maximumHeadroom, OriginalStartType = maximumHeadroom ? ServiceStart[pair.Key] : null }).ToList();
        }
        public void StopOptionalService(ServiceBackup service)
        {
            var saved = ResourceJournal().Services.Single(s => s.Name == service.Name);
            if (service.HoldDisabled)
            {
                if (!saved.ConfigurationAttempted || saved.OriginalStartType != ServiceStart[service.Name]) throw new Exception("Startup change was not backed up.");
                ServiceStart[service.Name] = 4;
            }
            if (!service.WasRunning) return;
            if (!saved.StopAttempted || !saved.WasRunning)
                throw new Exception("Service mutation was not journaled as an originally running service.");
            ServiceStops.Add(service.Name); ServiceRunning[service.Name] = false;
            if (FailServiceStop) throw new Exception("Simulated partial service stop.");
        }
        public void RestoreOptionalService(ServiceBackup service)
        {
            ResourceJournal();
            if (service.ConfigurationAttempted) ServiceStart[service.Name] = service.OriginalStartType!.Value;
            if (!service.WasRunning && service.HoldDisabled) return;
            if (!service.WasRunning || !service.StopAttempted)
                throw new Exception("A service that was not running was restarted.");
            ServiceRestores.Add(service.Name); ServiceRunning[service.Name] = true;
        }
        public List<ScheduledTaskBackup> CaptureOptionalTasks() => [new() { TaskPath = @"\", TaskName = "Spencer-CS2-GitHub-Runner", ActionHash = new string('a', 64) }];
        public void DisableOptionalTask(ScheduledTaskBackup task)
        {
            if (!ResourceJournal().ScheduledTasks.Single().DisableAttempted) throw new Exception("Task disabled before journal.");
            TasksEnabled = false;
            if (FailTaskDisable) throw new Exception("Simulated partial task disable.");
        }
        public void RestoreOptionalTask(ScheduledTaskBackup task)
        {
            ResourceJournal();
            if (FailTaskRestore) throw new Exception("Simulated task recovery failure.");
            TasksEnabled = true;
        }
        public NetworkPriorityBackup? PrepareEthernetPriority()
        {
            ResourceCaptures++;
            return new NetworkPriorityBackup { PolicyName = "TourneyMode-" + new string('1', 32), InterfaceName = "Fixture Ethernet" };
        }
        public void ApplyEthernetPriority(NetworkPriorityBackup network)
        {
            var saved = ResourceJournal().NetworkPriority;
            if (saved?.ApplyAttempted != true || saved.PolicyName != network.PolicyName)
                throw new Exception("QoS policy mutation was not journaled first.");
            NetworkApplies++; ActivePolicyName = network.PolicyName;
            if (FailNetworkApply) throw new Exception("Simulated partial network policy apply.");
        }
        public void RestoreEthernetPriority(NetworkPriorityBackup network)
        {
            ResourceJournal(); NetworkRestores++;
            if (FailNetworkRestore) throw new Exception("Simulated network policy cleanup failure.");
            if (ActivePolicyName.Length > 0 && ActivePolicyName != network.PolicyName)
                throw new Exception("A different QoS policy was selected for cleanup.");
            ActivePolicyName = "";
        }
        public EthernetAdapterBackup? CaptureEthernetAdapter()
        {
            ResourceCaptures++;
            return new EthernetAdapterBackup
            {
                InterfaceGuid = "11111111-2222-3333-4444-555555555555", InterfaceName = "Fixture Ethernet",
                Properties = [
                    new() { Keyword = "*EEE", OriginalValues = AdapterValues["*EEE"].ToArray(), AppliedValues = ["0"] },
                    new() { Keyword = "*InterruptModeration", OriginalValues = AdapterValues["*InterruptModeration"].ToArray(), AppliedValues = ["0"] },
                    new() { Keyword = "*Rss", OriginalValues = AdapterValues["*Rss"].ToArray(), AppliedValues = ["1"] }
                ]
            };
        }
        public void ApplyEthernetAdapter(EthernetAdapterBackup adapter)
        {
            var saved = ResourceJournal().EthernetAdapter;
            if (saved?.ApplyAttempted != true || saved.InterfaceGuid != adapter.InterfaceGuid
                || saved.Properties.Count != adapter.Properties.Count)
                throw new Exception("Adapter mutation was not journaled first.");
            AdapterApplies++;
            foreach (var property in adapter.Properties)
            {
                AdapterValues[property.Keyword] = property.AppliedValues.ToArray();
                if (FailAdapterApply) throw new Exception("Simulated partial adapter apply.");
            }
        }
        public void RestoreEthernetAdapter(EthernetAdapterBackup adapter)
        {
            ResourceJournal(); AdapterRestores++;
            foreach (var property in adapter.Properties) AdapterValues[property.Keyword] = property.OriginalValues.ToArray();
        }

        public Task Delay(int ms) { if (ms == 500) GraceDelays++; return Task.CompletedTask; }
    }
}
