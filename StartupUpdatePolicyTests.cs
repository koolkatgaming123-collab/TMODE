namespace TourneyMode;

/// <summary>Pure fake update backend, attempt store and safety snapshots. No files, HTTPS, helpers or restarts.</summary>
internal static class StartupUpdatePolicyTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        await test("startup updater construction has no check, store or safety side effects", () =>
        {
            var fixture = new Fixture();
            Check(fixture.Backend.Checks == 0 && fixture.Store.Reads == 0 && fixture.SafetyReads == 0, "Constructing the coordinator performed work.");
            return Task.CompletedTask;
        });
        await test("startup updater current, offline and unsupported copies continue without download or suppression", async () =>
        {
            foreach (var status in new[] { UpdateCheckStatus.UpToDate, UpdateCheckStatus.Failed, UpdateCheckStatus.Unsupported })
            {
                var fixture = new Fixture(); fixture.Backend.CheckResult = new() { Status = status, Message = "Synthetic no-update result." };
                var result = await fixture.Policy.RunAsync();
                Check(result.Outcome == StartupUpdateOutcome.Continue && !result.ShouldExit && fixture.Backend.Checks == 1
                    && fixture.Backend.Stages == 0 && fixture.Backend.Launches == 0 && fixture.Store.Records == 0, "An unavailable update blocked the app or recorded an unattempted target.");
            }
        });
        await test("startup updater automatically stages and prepares a verified newer release after durable target intent", async () =>
        {
            var fixture = new Fixture(); var result = await fixture.Policy.RunAsync();
            Check(result.Outcome == StartupUpdateOutcome.RestartReady && result.ShouldExit && result.LaunchResult?.Started == true
                && result.CheckResult?.Candidate == fixture.Backend.CheckResult.Candidate && result.StagedUpdate?.Version == "1.7.2"
                && fixture.Events.SequenceEqual(["Check", "Stage", "Record", "Launch"]), "Automatic check/download/helper ordering was incorrect.");
            Check(fixture.Store.Last is { Schema: 1, Version: "1.7.2.0" } && fixture.Store.Last.Sha256 == new string('A', 64),
                "The exact verified target version/hash was not recorded before helper preparation.");
        });
        await test("startup updater game, unknown absence, active session, journal and busy states defer all work", async () =>
        {
            foreach (var safety in new StartupUpdateSafety[]
            {
                new StartupUpdateSafety(), new() { GameDefinitelyAbsent = false },
                new() { GameDefinitelyAbsent = true, SessionActive = true },
                new() { GameDefinitelyAbsent = true, RecoveryJournalPresent = true },
                new() { GameDefinitelyAbsent = true, Busy = true }
            })
            {
                var fixture = new Fixture { Safety = safety }; var result = await fixture.Policy.RunAsync();
                Check(result.Outcome == StartupUpdateOutcome.Deferred && !result.ShouldExit && fixture.Backend.Checks == 0 && fixture.Store.Reads == 0,
                    "An unsafe or unknown state allowed startup update work.");
            }
        });
        await test("startup updater rechecks safety after discovery and verified download", async () =>
        {
            var fixture = new Fixture(); fixture.Backend.AfterCheck = () => fixture.Safety = new() { GameDefinitelyAbsent = true, SessionActive = true };
            var result = await fixture.Policy.RunAsync();
            Check(result.Outcome == StartupUpdateOutcome.Deferred && fixture.Backend.Stages == 0 && fixture.Store.Records == 0, "A newly active session allowed download.");
            fixture = new Fixture(); fixture.Backend.AfterStage = () => fixture.Safety = new() { GameDefinitelyAbsent = false };
            result = await fixture.Policy.RunAsync();
            Check(result.Outcome == StartupUpdateOutcome.Deferred && result.StagedUpdate != null && fixture.Backend.Launches == 0 && fixture.Store.Records == 0,
                "A late game launch allowed helper preparation or unnecessary attempt suppression.");
        });
        await test("startup updater passes a live safety callback into existing stage and launch APIs", async () =>
        {
            var fixture = new Fixture(); fixture.Backend.DuringStage = busy =>
            {
                Check(!busy(), "The initial injected safety callback was incorrect."); fixture.Safety = new() { GameDefinitelyAbsent = true, Busy = true };
                Check(busy(), "The supplied backend guard was a stale snapshot."); throw new InvalidOperationException("Synthetic mid-download busy state.");
            };
            var result = await fixture.Policy.RunAsync();
            Check(!result.ShouldExit && fixture.Backend.Launches == 0 && fixture.Store.Records == 0, "A changing backend guard was ignored.");
            fixture = new Fixture(); fixture.Backend.DuringLaunch = busy => Check(!busy(), "The helper did not receive the live guard.");
            result = await fixture.Policy.RunAsync(); Check(result.ShouldExit, "A safe helper handoff could not complete.");
        });
        await test("startup updater suppresses the same target across future startups with normalized version and hash", async () =>
        {
            var fixture = new Fixture(); fixture.Store.Last = Attempt("1.7.2.0", new string('a', 64));
            var result = await fixture.Policy.RunAsync();
            Check(result.Outcome == StartupUpdateOutcome.Deferred && !result.ShouldExit && fixture.Backend.Checks == 1
                && fixture.Backend.Stages == 0 && fixture.Backend.Launches == 0 && fixture.Store.Records == 0,
                "A rollback/reopen could automatically retry the same signed target.");
        });
        await test("startup updater a different signed version or hash can receive a new attempt", async () =>
        {
            foreach (var previous in new[] { Attempt("1.7.1", new string('A', 64)), Attempt("1.7.2", new string('B', 64)) })
            {
                var fixture = new Fixture(); fixture.Store.Last = previous; var result = await fixture.Policy.RunAsync();
                Check(result.ShouldExit && fixture.Store.Records == 1 && fixture.Backend.Launches == 1, "A different trusted target was incorrectly suppressed.");
            }
        });
        await test("startup updater unreadable or malformed attempt records never reset or proceed", async () =>
        {
            foreach (int failure in new[] { 0, 1, 2, 3 })
            {
                var fixture = new Fixture();
                if (failure == 0) fixture.Store.FailRead = new IOException("Synthetic unreadable record.");
                if (failure == 1) fixture.Store.Last = new() { Schema = 2, Version = "1.7.2", Sha256 = new string('A', 64) };
                if (failure == 2) fixture.Store.Last = Attempt("invalid", new string('A', 64));
                if (failure == 3) fixture.Store.Last = Attempt("1.7.2", "invalid");
                var result = await fixture.Policy.RunAsync();
                Check(result.Outcome == StartupUpdateOutcome.Continue && !result.ShouldExit && fixture.Backend.Checks == 0
                    && fixture.Store.Records == 0 && fixture.Backend.Launches == 0, "A bad attempt record was guessed away or replayed.");
            }
        });
        await test("startup updater JSON attempt parser rejects corrupt, duplicate, unknown and oversized records", () =>
        {
            string content = System.Text.Json.JsonSerializer.Serialize(Attempt("1.7.2", new string('A', 64)),
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            var valid = JsonStartupUpdateAttemptStore.ParseRecord(content);
            Check(valid.Version == "1.7.2" && valid.AttemptedUtc.Kind == DateTimeKind.Utc, "Valid durable target evidence could not be read.");
            foreach (string corrupt in new[] { "{}", "null", "{broken", content.Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal),
                content.Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal), content[..^1] + ",\"unknown\":true}", new string('x', 4097) })
            {
                bool refused = false;
                try { _ = JsonStartupUpdateAttemptStore.ParseRecord(corrupt); }
                catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException) { refused = true; }
                Check(refused, "A corrupt attempt record could be treated as new/absent and replayed.");
            }
            return Task.CompletedTask;
        });
        await test("startup updater failed persistence and wrong readback block helper launch", async () =>
        {
            foreach (int failure in new[] { 0, 1, 2 })
            {
                var fixture = new Fixture(); fixture.Store.FailRecord = failure == 0; fixture.Store.IgnoreRecord = failure == 1;
                fixture.Store.WrongReadback = failure == 2;
                var result = await fixture.Policy.RunAsync();
                Check(result.Outcome == StartupUpdateOutcome.Continue && !result.ShouldExit && fixture.Backend.Stages == 1 && fixture.Backend.Launches == 0,
                    "A nondurable/mismatched attempt was followed by helper execution.");
            }
        });
        await test("startup updater canceled check or download continues and does not suppress an unattempted target", async () =>
        {
            using var pre = new CancellationTokenSource(); pre.Cancel(); var fixture = new Fixture();
            var result = await fixture.Policy.RunAsync(pre.Token);
            Check(!result.ShouldExit && fixture.Backend.Checks == 0 && fixture.Store.Records == 0, "Pre-cancellation performed startup update work.");
            using var during = new CancellationTokenSource(); fixture = new Fixture(); fixture.Backend.AfterStage = during.Cancel;
            result = await fixture.Policy.RunAsync(during.Token);
            Check(result.Outcome == StartupUpdateOutcome.Continue && fixture.Store.Records == 0 && fixture.Backend.Launches == 0,
                "Download cancellation recorded a helper attempt or prepared a restart.");
        });
        await test("startup updater cancellation or safety change after recording retains loop prevention", async () =>
        {
            foreach (bool cancel in new[] { false, true })
            {
                using var cancellation = new CancellationTokenSource(); var fixture = new Fixture();
                fixture.Store.AfterRecord = () => { if (cancel) cancellation.Cancel(); else fixture.Safety = new() { GameDefinitelyAbsent = true, RecoveryJournalPresent = true }; };
                var result = await fixture.Policy.RunAsync(cancellation.Token);
                Check(!result.ShouldExit && fixture.Backend.Launches == 0 && fixture.Store.Last != null, "A post-journal cancellation/safety change allowed helper launch.");
                fixture.Store.AfterRecord = null; fixture.Safety = Ready();
                result = await fixture.NewPolicy().RunAsync();
                Check(result.Outcome == StartupUpdateOutcome.Deferred && fixture.Backend.Launches == 0 && fixture.Store.Records == 1,
                    "An uncertain recorded attempt was automatically replayed on a future startup.");
            }
        });
        await test("startup updater late helper cancellation or safety does not close the app or replay stale readiness", async () =>
        {
            foreach (bool cancel in new[] { false, true })
            {
                using var cancellation = new CancellationTokenSource(); var fixture = new Fixture();
                fixture.Backend.AfterLaunch = () => { if (cancel) cancellation.Cancel(); else fixture.Safety = new() { GameDefinitelyAbsent = true, Busy = true }; };
                var result = await fixture.Policy.RunAsync(cancellation.Token);
                Check(!result.ShouldExit && result.LaunchResult?.Started == true, "A late cancellation/safety change still requested application exit.");
                fixture.Safety = Ready(); result = await fixture.Policy.RunAsync();
                Check(result.Outcome == StartupUpdateOutcome.Deferred && !result.ShouldExit && fixture.Backend.Launches == 1,
                    "A duplicate coordinator call replayed stale ShouldExit/helper readiness.");
            }
        });
        await test("startup updater failed helper preparation remains manual-only for the same target", async () =>
        {
            var fixture = new Fixture(); fixture.Backend.HelperStarts = false; var result = await fixture.Policy.RunAsync();
            Check(result.Outcome == StartupUpdateOutcome.Continue && !result.ShouldExit && fixture.Store.Last != null, "A failed helper preparation stopped the app or lost attempt intent.");
            fixture.Backend.HelperStarts = true; result = await fixture.NewPolicy().RunAsync();
            Check(result.Outcome == StartupUpdateOutcome.Deferred && fixture.Backend.Launches == 1, "A failed helper/reopen cycle automatically repeated.");
        });
        await test("startup updater check/download errors continue and allow a future startup retry", async () =>
        {
            foreach (bool atCheck in new[] { false, true })
            {
                var fixture = new Fixture(); fixture.Backend.FailCheck = atCheck; fixture.Backend.FailStage = !atCheck;
                var result = await fixture.Policy.RunAsync();
                Check(result.Outcome == StartupUpdateOutcome.Continue && fixture.Store.Records == 0 && fixture.Backend.Launches == 0,
                    "A mere check/download failure was persisted as an installation attempt.");
                fixture.Backend.FailCheck = fixture.Backend.FailStage = false; result = await fixture.NewPolicy().RunAsync();
                Check(result.ShouldExit && fixture.Backend.Launches == 1, "A transient check/download error suppressed future automatic recovery.");
            }
        });
        await test("startup updater mismatched staged version and malformed available result cannot prepare restart", async () =>
        {
            var fixture = new Fixture(); fixture.Backend.StagedVersion = "9.9.9"; var result = await fixture.Policy.RunAsync();
            Check(!result.ShouldExit && fixture.Store.Records == 0 && fixture.Backend.Launches == 0, "A different staged release was installed.");
            fixture = new Fixture(); fixture.Backend.CheckResult = new() { Status = UpdateCheckStatus.Available };
            result = await fixture.Policy.RunAsync(); Check(!result.ShouldExit && fixture.Backend.Stages == 0 && fixture.Store.Records == 0, "Missing signed candidate data enabled staging.");
        });
        await test("startup updater duplicate concurrent runs never start a second operation", async () =>
        {
            var fixture = new Fixture(); var check = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Backend.PendingCheck = check.Task;
            var first = fixture.Policy.RunAsync(); var duplicate = await fixture.Policy.RunAsync();
            Check(duplicate.Outcome == StartupUpdateOutcome.Deferred && !duplicate.ShouldExit && fixture.Backend.Checks == 1, "A concurrent duplicate ran another check.");
            check.SetResult(fixture.Backend.CheckResult); var result = await first;
            Check(result.ShouldExit && fixture.Backend.Launches == 1, "The original one-shot operation did not complete exactly once.");
            duplicate = await fixture.Policy.RunAsync(); Check(!duplicate.ShouldExit && duplicate.Outcome == StartupUpdateOutcome.Deferred, "A completed run replayed an old restart grant.");
        });
    }
    private static StartupUpdateSafety Ready() => new() { GameDefinitelyAbsent = true };
    private static StartupUpdateAttempt Attempt(string version, string hash) => new() { Version = version, Sha256 = hash };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Fixture
    {
        public readonly List<string> Events = [];
        public readonly FakeBackend Backend;
        public readonly FakeStore Store;
        public StartupUpdateSafety Safety = Ready();
        public int SafetyReads;
        public StartupUpdatePolicy Policy { get; }
        public Fixture() { Store = new(Events); Backend = new(Events, Store); Policy = NewPolicy(); }
        public StartupUpdatePolicy NewPolicy() => new(Backend, () => { SafetyReads++; return Safety; }, Store);
    }
    private sealed class FakeStore(List<string> events) : IStartupUpdateAttemptStore
    {
        public StartupUpdateAttempt? Last;
        public int Reads, Records;
        public Exception? FailRead;
        public bool FailRecord, IgnoreRecord, WrongReadback;
        public Action? AfterRecord;
        public StartupUpdateAttempt? Read()
        {
            Reads++; if (FailRead != null) throw FailRead;
            return Records > 0 && WrongReadback ? Attempt("1.7.2", new string('B', 64)) : Last;
        }
        public void Record(StartupUpdateAttempt attempt)
        {
            Records++; events.Add("Record"); if (FailRecord) throw new IOException("Synthetic durable-store failure.");
            if (!IgnoreRecord) Last = attempt; AfterRecord?.Invoke();
        }
    }
    private sealed class FakeBackend(List<string> events, FakeStore store) : IStartupUpdateBackend
    {
        public int Checks, Stages, Launches;
        public bool FailCheck, FailStage, HelperStarts = true;
        public string StagedVersion = "1.7.2";
        public Action? AfterCheck, AfterStage, AfterLaunch;
        public Action<Func<bool>>? DuringStage, DuringLaunch;
        public Task<UpdateCheckResult>? PendingCheck;
        public UpdateCheckResult CheckResult = new()
        {
            Status = UpdateCheckStatus.Available,
            Candidate = new(new UpdateManifest { Version = "1.7.2", Sha256 = new string('A', 64) }, "Synthetic verified candidate; no parser/network invoked.")
        };
        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Checks++; events.Add("Check");
            if (FailCheck) throw new IOException("Synthetic unavailable check.");
            var result = PendingCheck == null ? CheckResult : await PendingCheck; AfterCheck?.Invoke(); return result;
        }
        public Task<StagedUpdate> StageAsync(UpdateCandidate candidate, Func<bool> isBusy, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Check(!isBusy(), "Staging began in an unsafe state."); Stages++; events.Add("Stage");
            DuringStage?.Invoke(isBusy); if (FailStage) throw new IOException("Synthetic unavailable download.");
            AfterStage?.Invoke(); return Task.FromResult(new StagedUpdate { Version = StagedVersion, DirectoryPath = @"C:\Synthetic\Updates\stage", ExecutablePath = @"C:\Synthetic\Updates\stage\new.exe" });
        }
        public Task<UpdateLaunchResult> LaunchApplyHelperAsync(StagedUpdate update, Func<bool> isBusy, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Check(!isBusy(), "Helper preparation began in an unsafe state.");
            Check(store.Last != null && StartupUpdatePolicy.SameTarget(store.Last, Attempt("1.7.2", new string('A', 64))), "Helper began before durable exact target intent.");
            Launches++; events.Add("Launch"); DuringLaunch?.Invoke(isBusy); AfterLaunch?.Invoke();
            return Task.FromResult(new UpdateLaunchResult { Started = HelperStarts, HelperProcessId = HelperStarts ? 123 : null, TicketPath = @"C:\Synthetic\Updates\apply.ticket" });
        }
    }
}
