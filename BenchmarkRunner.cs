using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TourneyMode;

/// <summary>
/// Detached, explicitly requested live test. The caller supplies a fresh output folder
/// and stages the signed PresentMon binary. This runner injects no input of its own.
/// </summary>
public static partial class BenchmarkRunner
{
    private const string GameExecutable = "FortniteClient-Win64-Shipping.exe";
    private static string StopEventName => @"Local\TourneyModeBenchStop-" + Environment.UserName;
    private const string Disclaimer = "Fixed UEFN Creative edit scene; this is not representative of combat or a tournament. PresentMon frame metrics do not measure end-to-end input latency. Foreground changes and scene changes invalidate comparisons.";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Task<int> Run(string planPath) => Task.Factory.StartNew(
        () => RunWithMutex(planPath), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Reads and validates a plan without creating files or changing live state.</summary>
    public static BenchmarkPlan PreviewPlan(string planPath)
    {
        var plan = JsonSerializer.Deserialize<BenchmarkPlan>(File.ReadAllText(Path.GetFullPath(planPath)), Json)
            ?? throw new InvalidDataException("The benchmark plan is empty.");
        if (!Path.IsPathFullyQualified(plan.OutputDirectory) || !Path.IsPathFullyQualified(plan.PresentMonPath))
            throw new InvalidDataException("Benchmark output and PresentMon paths must be absolute.");
        plan.OutputDirectory = Path.GetFullPath(plan.OutputDirectory);
        plan.PresentMonPath = Path.GetFullPath(plan.PresentMonPath);
        if (!plan.SkipCollectionCallback && (!Guid.TryParse(plan.ProjectId, out _) || string.IsNullOrWhiteSpace(plan.ExpectedThreadId)))
            throw new InvalidDataException("The benchmark plan must identify the project and expected collection thread unless its callback is explicitly skipped.");
        if (plan.CaptureSeconds is < 1 or > 120)
            throw new InvalidDataException("CaptureSeconds must be between 1 and 120.");
        if (plan.AllOnOffTest && (plan.Comparison || plan.Pairs is < 2 or > 4 || plan.StabilizationSeconds is < 10 or > 60))
            throw new InvalidDataException("All-ON/OFF needs 2-4 pairs, 10-60 seconds stabilization, and cannot also compare old versions.");
        if (plan.ExcludedAppPrefixes is null || plan.ExcludedAppPrefixes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("ExcludedAppPrefixes must contain only nonempty app ID prefixes.");
        // Keep the desktop collector constant even if a caller supplies other exclusions.
        if (plan.Comparison && !plan.ExcludedAppPrefixes.Contains("codex-desktop|", StringComparer.OrdinalIgnoreCase))
            plan.ExcludedAppPrefixes.Add("codex-desktop|");
        if (File.Exists(Path.Combine(plan.OutputDirectory, "benchmark-status.json")))
            throw new InvalidOperationException("This output folder already contains a benchmark status. Use a fresh folder; an uncertain test must not be replayed.");
        if (!File.Exists(plan.PresentMonPath))
            throw new FileNotFoundException("The staged PresentMon binary is missing.", plan.PresentMonPath);
        return plan;
    }

    /// <summary>Requests graceful benchmark cancellation and uncancelled recovery.</summary>
    public static bool RequestStop()
    {
        try
        {
            using var stop = EventWaitHandle.OpenExisting(StopEventName);
            return stop.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    // Windows mutex ownership is thread-affine. Keep acquire/release on this dedicated
    // thread while the async test runs to completion on worker continuations.
    private static int RunWithMutex(string planPath)
    {
        BenchmarkPlan? plan = null;
        BenchmarkStatus? status = null;
        try
        {
            plan = PreviewPlan(planPath);
            Directory.CreateDirectory(plan.OutputDirectory);
            if (File.Exists(Path.Combine(plan.OutputDirectory, "benchmark-status.json")))
                throw new InvalidOperationException("This output folder already contains a benchmark status. Use a fresh folder; an uncertain test must not be replayed.");
            status = new BenchmarkStatus
            {
                RunId = Guid.NewGuid().ToString("N"),
                PlanPath = Path.GetFullPath(planPath),
                OutputDirectory = plan.OutputDirectory,
                PresentMonPath = plan.PresentMonPath,
                ProjectId = plan.ProjectId,
                ExpectedThreadId = plan.ExpectedThreadId,
                Comparison = plan.Comparison,
                AllOnOffTest = plan.AllOnOffTest,
                CaptureSeconds = plan.CaptureSeconds,
                SkipCollectionCallback = plan.SkipCollectionCallback,
                WaitForForeground = plan.WaitForForeground,
                ExcludedAppPrefixes = plan.ExcludedAppPrefixes.ToList(),
                SceneDescription = plan.SceneDescription,
                Disclaimer = plan.AllOnOffTest ? "Stationary solo private Creative map scene, not an actual fight or tournament endgame. OFF1 includes original background apps; later OFF runs retain closed apps, isolating the settings effect more closely. Frame metrics do not measure end-to-end input latency. Scene/focus changes invalidate comparisons."
                    : plan.Comparison ? "Fixed Creative scene; not representative of combat or tournament endgames. Profiles use the same corrected engine. Frame metrics do not measure end-to-end input latency. Foreground and scene changes invalidate comparisons." : Disclaimer,
                BlockOrder = plan.AllOnOffTest ? Enumerable.Range(1, plan.Pairs).SelectMany(i => new[] { "OFF" + i, "ON" + i }).ToList()
                    : plan.Comparison ? ["V1A", "V2A", "V2B", "V1B"] : [],
                RecoveryJournalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TourneyMode", "session.json")
            };
            Save(plan, status, "Preflight");
            if (!File.Exists(plan.PresentMonPath)) throw new FileNotFoundException("The staged PresentMon binary is missing.", plan.PresentMonPath);
            using var mutex = new Mutex(false, @"Local\TourneyMode-" + Environment.UserName);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("Another Tourney Mode instance is running. No test or live change was started.");
            try
            {
                using var cancellation = new CancellationTokenSource();
                using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
                var registration = ThreadPool.RegisterWaitForSingleObject(stopEvent, static (state, _) =>
                {
                    try { ((CancellationTokenSource)state!).Cancel(); }
                    catch (ObjectDisposedException) { }
                }, cancellation, Timeout.Infinite, executeOnlyOnce: true);
                try { return RunCoreAsync(plan, status, cancellation.Token).GetAwaiter().GetResult(); }
                finally { registration.Unregister(null); }
            }
            // Mutex ownership stays on this thread even though RunCoreAsync resumes elsewhere.
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception error)
        {
            if (plan != null && status != null)
            {
                status.Errors.Add(error.ToString());
                status.CompletedUtc = DateTime.UtcNow;
                status.Result = "Failed";
                try { Save(plan, status, "Failed before live test"); } catch { }
            }
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(BenchmarkPlan plan, BenchmarkStatus status, CancellationToken cancellation)
    {
        if (plan.AllOnOffTest) return await RunAllOnOffAsync(plan, status, cancellation).ConfigureAwait(false);
        if (plan.Comparison) return await RunComparisonAsync(plan, status, cancellation).ConfigureAwait(false);
        var system = new WindowsSystem();
        var engine = new TourneyEngine(system: system);
        engine.Log += line => AppendLog(plan, line);
        if (engine.IsActive)
            throw new InvalidOperationException("A saved Tourney Mode session already exists. The benchmark will not overwrite or restore a session it did not create.");
        bool testingSucceeded = false;
        bool enableAttempted = false;
        bool cancelled = false;
        status.OriginalPowerScheme = system.GetPowerScheme();
        status.InitialProcessCount = system.Snapshot().Count;
        status.InitialSnapshot = Metadata(system);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            RequireGame(system);
            Save(plan, status, "OFF warmup (20 seconds)");
            await Task.Delay(TimeSpan.FromSeconds(20), cancellation).ConfigureAwait(false);
            await CaptureAsync(plan, status, system, "OFF1", false, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            await CaptureAsync(plan, status, system, "OFF2", false, cancellation).ConfigureAwait(false);

            cancellation.ThrowIfCancellationRequested();
            var apps = engine.Scan().Where(app => app.DefaultSelected).ToList();
            var options = new AppOptions { SelectedApps = apps.Select(app => app.Id).Distinct().ToList() };
            status.SelectedApps = apps.Select(app => new SelectedApp
            {
                Id = app.Id, Name = app.Name, RecordedProcessCount = app.Processes.Count
            }).ToList();
            status.SelectedProcessCount = apps.SelectMany(app => app.Processes).Select(process => process.Id).Distinct().Count();
            status.Options = options;
            enableAttempted = true;
            status.EnableAttempted = true;
            Save(plan, status, "Enabling Tourney Mode");
            cancellation.ThrowIfCancellationRequested();
            await engine.EnableAsync(options).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (!engine.IsActive || engine.State?.Phase != "Active")
                throw new InvalidOperationException("Tourney Mode did not reach the Active phase.");
            status.EnabledUtc = DateTime.UtcNow;
            // Freeze the ON record: RestoreAsync mutates the engine's state object.
            status.ActiveState = JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(engine.State, Json), Json);
            Save(plan, status, "ON stabilization (30 seconds)");
            await Task.Delay(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            await engine.MaintainAsync().ConfigureAwait(false);
            await CaptureAsync(plan, status, system, "ON1", true, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            await engine.MaintainAsync().ConfigureAwait(false);
            await CaptureAsync(plan, status, system, "ON2", true, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            await engine.MaintainAsync().ConfigureAwait(false);
            await CaptureAsync(plan, status, system, "ON3", true, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            testingSucceeded = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancelled = true;
            AppendLog(plan, "Manual benchmark stop requested. Restoring without cancelling recovery.");
        }
        catch (Exception error)
        {
            status.Errors.Add(error.ToString());
            AppendLog(plan, "Benchmark test failed: " + error.Message);
        }
        finally
        {
            // Even a baseline-capture failure performs the no-op restore on this
            // initially inactive engine, allowing collection only after this boundary.
            status.RestorationAttempted = true;
            try { Save(plan, status, "Restoring recorded apps and settings"); }
            catch (Exception error) { status.Errors.Add("Pre-restore status write: " + error.Message); }
            try
            {
                await engine.RestoreAsync().ConfigureAwait(false);
                status.RestorationSucceeded = !engine.IsActive && !File.Exists(status.RecoveryJournalPath);
                if (!status.RestorationSucceeded) throw new InvalidOperationException("Recovery journal is still present after restore.");
            }
            catch (Exception error)
            {
                status.Errors.Add("RESTORE: " + error);
                AppendLog(plan, "RESTORE failed; journal preserved: " + error.Message);
            }
            status.RestoredUtc = DateTime.UtcNow;
            status.RecoveryJournalExists = File.Exists(status.RecoveryJournalPath);
            try
            {
                status.FinalPowerScheme = system.GetPowerScheme();
                if (!string.Equals(status.FinalPowerScheme, status.OriginalPowerScheme, StringComparison.OrdinalIgnoreCase))
                {
                    status.RestorationSucceeded = false;
                    status.Errors.Add("RESTORE: active power plan differs from the original power plan.");
                }
                status.FinalProcessCount = system.Snapshot().Count;
            }
            catch (Exception error) { status.Errors.Add("Post-restore metadata: " + error.Message); }
            try { Save(plan, status, status.RestorationSucceeded ? "Restore complete" : "Restore needs attention"); }
            catch (Exception error) { AppendLog(plan, "Status write after restore failed: " + error.Message); }
        }

        if (testingSucceeded && status.RestorationSucceeded && !cancellation.IsCancellationRequested)
        {
            try
            {
                Save(plan, status, "OFF recovery stabilization (30 seconds)");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
                await CaptureAsync(plan, status, system, "OFF3", false, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                cancelled = true;
                AppendLog(plan, "Manual stop requested after recovery; OFF3 skipped or interrupted.");
            }
            catch (Exception error)
            {
                testingSucceeded = false;
                status.Errors.Add("OFF3: " + error);
            }
        }
        status.EnableAttempted = enableAttempted;
        status.CompletedUtc = DateTime.UtcNow;
        status.RecoveryJournalExists = File.Exists(status.RecoveryJournalPath);
        cancelled |= cancellation.IsCancellationRequested;
        status.Result = cancelled && status.RestorationSucceeded && status.Errors.Count == 0 ? "Cancelled"
            : testingSucceeded && status.RestorationSucceeded && status.Errors.Count == 0 ? "Completed" : "Failed";
        Save(plan, status, status.Result);
        if (status.RestorationAttempted && !plan.SkipCollectionCallback)
        {
            status.CollectionQueued = await QueueCollectionAsync(plan, status).ConfigureAwait(false);
            Save(plan, status, status.Result);
        }
        return status.Result is "Completed" or "Cancelled" ? 0 : 1;
    }

    private static async Task<int> RunComparisonAsync(BenchmarkPlan plan, BenchmarkStatus status, CancellationToken cancellation)
    {
        var system = new WindowsSystem();
        var engine = new TourneyEngine(system: system);
        using var restoreRegistration = cancellation.Register(engine.RequestRestore);
        engine.Log += line => AppendLog(plan, line);
        if (engine.IsActive)
            throw new InvalidOperationException("A saved Tourney Mode session already exists. The comparison will not overwrite or restore a session it did not create.");
        status.OriginalPowerScheme = system.GetPowerScheme();
        status.InitialSnapshot = Metadata(system);
        status.InitialProcessCount = status.InitialSnapshot.ProcessCount;
        var initialGames = system.Games().Where(ProcessPolicy.IsFortnite).ToList();
        if (initialGames.Count != 1)
            throw new InvalidOperationException("The comparison requires exactly one running Fortnite game client; it does not launch or control the game.");
        var expectedGame = initialGames[0];
        status.ExpectedGame = new GameMetadata { Pid = expectedGame.Id, StartTicks = expectedGame.StartTicks, Path = expectedGame.Path };
        bool cancelled = false;

        foreach (string label in status.BlockOrder)
        {
            var block = new ComparisonBlock { Label = label, Mode = label.StartsWith("V2", StringComparison.Ordinal) ? "V2" : "V1" };
            status.Blocks.Add(block);
            using var maintenanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            Task? maintenance = null;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                RequireExpectedGame(system, expectedGame);
                bool fortniteOnly = block.Mode == "V2";
                var scanned = engine.Scan(fortniteOnly);
                var excluded = scanned.Where(app => plan.ExcludedAppPrefixes.Any(prefix => app.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    .Select(app => app.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var apps = scanned.Where(app => app.DefaultSelected && !excluded.Contains(app.Id, StringComparer.OrdinalIgnoreCase)).ToList();
                var options = new AppOptions
                {
                    OptionsVersion = 2,
                    FortniteOnly = fortniteOnly,
                    KeepAppsClosed = fortniteOnly,
                    SelectedApps = apps.Select(app => app.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    ExcludedApps = excluded
                };
                block.Options = options;
                block.SelectedApps = apps.Select(app => new SelectedApp { Id = app.Id, Name = app.Name, RecordedProcessCount = app.Processes.Count }).ToList();
                block.SelectedProcessCount = apps.SelectMany(app => app.Processes).Select(process => process.Id).Distinct().Count();
                block.EnableAttempted = true;
                status.EnableAttempted = true;
                Save(plan, status, "Enabling " + label + " (" + block.Mode + ")");
                await engine.EnableAsync(options).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (!engine.IsActive || engine.State?.Phase != "Active")
                    throw new InvalidOperationException(label + " did not reach the Active phase.");
                block.EnabledUtc = DateTime.UtcNow;
                status.EnabledUtc ??= block.EnabledUtc;
                block.ActiveState = JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(engine.State, Json), Json);
                // Both app versions use a five-second timer. V1 only maintains
                // Fortnite priority; V2 additionally runs the selected-app guard.
                maintenance = MaintainBlockAsync(engine, block, maintenanceCancellation.Token);
                RequireExpectedGame(system, expectedGame);
                if (plan.WaitForForeground)
                    await WaitForForegroundAsync(plan, status, system, expectedGame, label, cancellation).ConfigureAwait(false);
                Save(plan, status, label + " stabilization (30 seconds)");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
                for (int trial = 1; trial <= 2; trial++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    RequireExpectedGame(system, expectedGame);
                    if (maintenance?.IsFaulted == true) await maintenance.ConfigureAwait(false);
                    await CaptureAsync(plan, status, system, label + trial, true, cancellation, block.Mode, block).ConfigureAwait(false);
                    RequireExpectedGame(system, expectedGame);
                }
                block.TestingSucceeded = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                cancelled = true;
                AppendLog(plan, "Manual stop requested during " + label + ". Restoring without cancelling recovery.");
            }
            catch (Exception error)
            {
                block.Errors.Add(error.ToString());
                status.Errors.Add(label + ": " + error);
                AppendLog(plan, label + " failed: " + error.Message);
            }
            finally
            {
                // Join the guard before restore; it must never close a returning app.
                maintenanceCancellation.Cancel();
                if (maintenance != null)
                {
                    try { await maintenance.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (maintenanceCancellation.IsCancellationRequested) { }
                    catch (Exception error)
                    {
                        block.TestingSucceeded = false;
                        block.Errors.Add("Maintenance: " + error);
                        status.Errors.Add(label + " maintenance: " + error);
                    }
                }
                block.RestorationAttempted = true;
                status.RestorationAttempted = true;
                try { Save(plan, status, "Restoring " + label + " apps and settings"); }
                catch (Exception error) { status.Errors.Add(label + " pre-restore status: " + error.Message); }
                try
                {
                    await engine.RestoreAsync().ConfigureAwait(false);
                    block.RecoveryJournalExists = File.Exists(status.RecoveryJournalPath);
                    block.FinalPowerScheme = system.GetPowerScheme();
                    if (engine.IsActive || block.RecoveryJournalExists)
                        throw new InvalidOperationException("Recovery journal is still present after restore.");
                    if (!block.FinalPowerScheme.Equals(status.OriginalPowerScheme, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Active power plan differs from the original power plan.");
                    block.RestorationSucceeded = true;
                }
                catch (Exception error)
                {
                    block.RestorationSucceeded = false;
                    block.Errors.Add("RESTORE: " + error);
                    status.Errors.Add(label + " RESTORE: " + error);
                    AppendLog(plan, label + " RESTORE failed; the comparison will stop and the journal is preserved: " + error.Message);
                }
                block.RestoredUtc = DateTime.UtcNow;
                block.RecoveryJournalExists = File.Exists(status.RecoveryJournalPath);
                status.RestoredUtc = block.RestoredUtc;
                status.RecoveryJournalExists = block.RecoveryJournalExists;
                status.RestorationSucceeded = status.Blocks.All(item => item.RestorationSucceeded);
                status.FinalPowerScheme = block.FinalPowerScheme;
                try { status.FinalProcessCount = system.Snapshot().Count; }
                catch (Exception error) { status.Errors.Add(label + " final process metadata: " + error.Message); }
                try { Save(plan, status, block.RestorationSucceeded ? label + " restore complete" : label + " restore needs attention"); }
                catch (Exception error) { AppendLog(plan, "Status write after " + label + " restore failed: " + error.Message); }
            }
            if (cancelled || cancellation.IsCancellationRequested || !block.TestingSucceeded || !block.RestorationSucceeded) break;
        }

        cancelled |= cancellation.IsCancellationRequested;
        bool capturesComplete = status.Blocks.Count == 4 && status.Blocks.All(block => block.TestingSucceeded)
            && status.Captures.Count(capture => capture.Completed && capture.Mode == "V1") == 4
            && status.Captures.Count(capture => capture.Completed && capture.Mode == "V2") == 4;
        status.CompletedUtc = DateTime.UtcNow;
        status.RecoveryJournalExists = File.Exists(status.RecoveryJournalPath);
        status.Result = cancelled && status.RestorationSucceeded && status.Errors.Count == 0 ? "Cancelled"
            : capturesComplete && status.RestorationSucceeded && status.Errors.Count == 0 ? "Completed" : "Failed";
        Save(plan, status, status.Result);
        if (status.RestorationAttempted && !plan.SkipCollectionCallback)
        {
            status.CollectionQueued = await QueueCollectionAsync(plan, status).ConfigureAwait(false);
            Save(plan, status, status.Result);
        }
        return status.Result is "Completed" or "Cancelled" ? 0 : 1;
    }

    private static async Task MaintainBlockAsync(TourneyEngine engine, ComparisonBlock block, CancellationToken cancellation)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            await engine.MaintainAsync().ConfigureAwait(false);
            block.MaintenancePasses++;
        }
    }

    private static void RequireExpectedGame(WindowsSystem system, ProcessIdentity expected)
    {
        var games = system.Games().Where(ProcessPolicy.IsFortnite).ToList();
        if (games.Count != 1 || games[0].Id != expected.Id || games[0].StartTicks != expected.StartTicks
            || !games[0].Path.Equals(expected.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Fortnite process identity changed. The comparison stopped rather than mixing game sessions.");
    }

    private static async Task WaitForForegroundAsync(BenchmarkPlan plan, BenchmarkStatus status, WindowsSystem system,
        ProcessIdentity expected, string label, CancellationToken cancellation)
    {
        Save(plan, status, "Waiting for Fortnite foreground [" + label + "]");
        var deadline = Stopwatch.StartNew();
        try
        {
            await BenchmarkForegroundGate.WaitAsync(expected.Id, ForegroundPid,
                () => RequireExpectedGame(system, expected),
                attempt => RequestGameForeground(plan, expected, label, attempt),
                () => deadline.ElapsedMilliseconds,
                token => Task.Delay(250, token), cancellation).ConfigureAwait(false);
            AppendLog(plan, label + ": Fortnite foreground verified for two seconds before stabilization.");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Fortnite did not remain foreground for two seconds within the 120-second wait for "
                + label + ". Final foreground PID=" + ForegroundPid() + ". No gameplay input was injected.");
        }
    }

    // Keep potentially blocking Windows focus calls in a disposable child process.
    // The benchmark controller never attaches its own input queue to another app.
    private static void RequestGameForeground(BenchmarkPlan plan, ProcessIdentity expected, string label, int attempt)
    {
        if (!ProcessCatalog.Matches(expected))
            throw new InvalidOperationException("Fortnite identity changed before foreground recovery.");
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add("--benchmark-focus");
        info.ArgumentList.Add(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(expected)));
        using var helper = new Process { StartInfo = info };
        if (!helper.Start()) throw new InvalidOperationException("The foreground helper could not start.");
        if (!helper.WaitForExit(5000))
        {
            // Only this exact helper belongs to us. Killing it cannot kill the
            // game or the foreground app; Windows detaches its input queue.
            try { helper.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
            if (!helper.WaitForExit(1000))
                throw new TimeoutException("The owned foreground helper could not be stopped.");
            AppendLog(plan, label + ": focus attempt " + attempt + " exceeded five seconds and its helper was stopped.");
            return;
        }
        AppendLog(plan, label + ": focus attempt " + attempt + "; helper exit=" + helper.ExitCode
            + "; expected PID=" + expected.Id + "; observed PID=" + ForegroundPid() + ".");
    }

    internal static ProcessIdentity DecodeFocusIdentity(string encoded)
    {
        var expected = JsonSerializer.Deserialize<ProcessIdentity>(Convert.FromBase64String(encoded))
            ?? throw new InvalidDataException("Missing foreground identity.");
        if (!ProcessPolicy.IsFortnite(expected) || !expected.Name.Equals(GameExecutable, StringComparison.OrdinalIgnoreCase)
            || expected.Id <= 0 || expected.StartTicks <= 0 || !Path.IsPathFullyQualified(expected.Path)
            || expected.SessionId != Process.GetCurrentProcess().SessionId)
            throw new InvalidDataException("Foreground preparation requires a Fortnite identity in this session.");
        return expected;
    }

    // Hidden CLI child only: no mutex, application restoration or gameplay input.
    // Its parent enforces a five-second lifetime even if a foreign UI thread hangs.
    public static int RunForegroundHelper(string encoded)
    {
        try
        {
            var expected = DecodeFocusIdentity(encoded);
            using var game = Process.GetProcessById(expected.Id);
            if (!ProcessCatalog.MatchesPinned(game, expected)) return 2;
            game.Refresh();
            IntPtr window = game.MainWindowHandle;
            if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window)) return 3;
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != expected.Id || !ProcessCatalog.MatchesPinned(game, expected)) return 2;
            if (IsIconic(window)) ShowWindowAsync(window, 9);
            if (SetForegroundWindow(window) && ForegroundPid() == expected.Id) return 0;
            uint currentThread = GetCurrentThreadId();
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            bool attached = false;
            try
            {
                if (foregroundThread != 0 && foregroundThread != currentThread)
                    attached = AttachThreadInput(currentThread, foregroundThread, true);
                if (attached || foregroundThread == currentThread)
                {
                    GetWindowThreadProcessId(window, out uint currentOwner);
                    if (currentOwner != expected.Id || !ProcessCatalog.MatchesPinned(game, expected)) return 2;
                    BringWindowToTop(window);
                    SetForegroundWindow(window);
                }
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }
            return ForegroundPid() == expected.Id ? 0 : 4;
        }
        catch { return 5; }
    }

    private static async Task CaptureAsync(BenchmarkPlan plan, BenchmarkStatus status, WindowsSystem system, string label, bool on,
        CancellationToken cancellation, string? mode = null, ComparisonBlock? block = null)
    {
        cancellation.ThrowIfCancellationRequested();
        RequireGame(system);
        var capture = new CaptureRecord
        {
            Label = label, Mode = mode ?? (on ? "ON" : "OFF"),
            Block = block?.Label ?? "",
            DurationSeconds = plan.CaptureSeconds,
            GuardActive = block?.Options?.KeepAppsClosed == true,
            MaintenancePassesBefore = block?.MaintenancePasses ?? 0,
            CsvPath = Path.Combine(plan.OutputDirectory, label + ".csv"),
            StdoutPath = Path.Combine(plan.OutputDirectory, label + ".stdout.txt"),
            StderrPath = Path.Combine(plan.OutputDirectory, label + ".stderr.txt"),
            Before = Metadata(system),
            SessionName = "Tourney" + status.RunId + label
        };
        if (File.Exists(capture.CsvPath)) throw new InvalidOperationException("Capture file already exists: " + capture.CsvPath);
        status.Captures.Add(capture);
        Save(plan, status, "Capturing " + label + " (" + plan.CaptureSeconds + " seconds)");
        var info = new ProcessStartInfo(plan.PresentMonPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = plan.OutputDirectory, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "--process_name", GameExecutable, "--output_file", capture.CsvPath,
            "--timed", plan.CaptureSeconds.ToString(CultureInfo.InvariantCulture), "--terminate_after_timed", "--no_console_stats", "--v1_metrics",
            "--session_name", capture.SessionName
        }) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        using var foregroundCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<string>? stdout = null, stderr = null;
        Task? foregroundMonitor = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("PresentMon did not start.");
            capture.PresentMonPid = process.Id;
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            int expectedPid = capture.Before.Games.Count == 1 ? capture.Before.Games[0].Pid : -1;
            foregroundMonitor = SampleForegroundAsync(capture, expectedPid, foregroundCancellation.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(plan.CaptureSeconds + 30));
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // This exact PresentMon process is ours; never terminate another capture
                // process, worker, benchmark, or a dynamic process tree.
                try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { }
                cancellation.ThrowIfCancellationRequested();
                throw new TimeoutException("PresentMon exceeded the " + (plan.CaptureSeconds + 30) + "-second capture deadline for " + label + ".");
            }
            capture.ExitCode = process.ExitCode;
            if (process.ExitCode != 0) throw new InvalidOperationException($"PresentMon {label} exited with code {process.ExitCode}.");
            if (!File.Exists(capture.CsvPath) || File.ReadLines(capture.CsvPath).Take(2).Count() < 2)
                throw new InvalidDataException("PresentMon did not produce a CSV containing frame data for " + label + ".");
            capture.CsvBytes = new FileInfo(capture.CsvPath).Length;
            capture.Completed = true;
        }
        catch (Exception error)
        {
            capture.Error = error.Message;
            throw;
        }
        finally
        {
            foregroundCancellation.Cancel();
            if (foregroundMonitor != null)
            {
                try { await foregroundMonitor.ConfigureAwait(false); }
                catch (OperationCanceledException) when (foregroundCancellation.IsCancellationRequested) { }
                catch (Exception error) { capture.ValidityWarnings.Add("Foreground sampling failed: " + error.Message); }
            }
            if (!capture.Completed && capture.PresentMonPid > 0)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: false);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
                catch (Exception error) { capture.ValidityWarnings.Add("Owned PresentMon cleanup could not be verified: " + error.Message); }
            }
            capture.CompletedUtc = DateTime.UtcNow;
            capture.MaintenancePassesAfter = block?.MaintenancePasses ?? 0;
            if (capture.GuardActive && capture.MaintenancePassesAfter == capture.MaintenancePassesBefore)
                capture.ValidityWarnings.Add("No five-second app-guard maintenance pass completed during this capture.");
            int foregroundLosses = capture.ForegroundSamples.Count(sample => !sample.IsExpectedGame);
            if (foregroundLosses > 0)
                capture.ValidityWarnings.Add(foregroundLosses + " of " + capture.ForegroundSamples.Count + " one-second foreground samples did not identify the expected Fortnite process.");
            try { capture.After = Metadata(system); }
            catch (Exception error) { capture.ValidityWarnings.Add("Post-capture metadata unavailable: " + error.Message); }
            if (!IsGameForeground(capture.Before)) capture.ValidityWarnings.Add("Fortnite was not the foreground process at capture start.");
            if (capture.After != null && !IsGameForeground(capture.After)) capture.ValidityWarnings.Add("Fortnite was not the foreground process at capture end.");
            if (capture.Before.Games.Count != 1 || capture.After?.Games.Count != 1)
                capture.ValidityWarnings.Add("A single Fortnite game process was not observed at both capture boundaries.");
            else if (capture.After != null && (capture.Before.Games[0].Pid != capture.After.Games[0].Pid
                     || capture.Before.Games[0].StartTicks != capture.After.Games[0].StartTicks))
                capture.ValidityWarnings.Add("The Fortnite process identity changed during capture.");
            try { if (stdout != null) await File.WriteAllTextAsync(capture.StdoutPath, await stdout.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).ConfigureAwait(false); }
            catch (Exception error) { capture.ValidityWarnings.Add("PresentMon stdout unavailable: " + error.Message); }
            try { if (stderr != null) await File.WriteAllTextAsync(capture.StderrPath, await stderr.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).ConfigureAwait(false); }
            catch (Exception error) { capture.ValidityWarnings.Add("PresentMon stderr unavailable: " + error.Message); }
            Save(plan, status, label + (capture.Completed ? " complete" : " failed"));
        }
    }

    private static async Task SampleForegroundAsync(CaptureRecord capture, int expectedPid, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int pid = ForegroundPid();
            capture.ForegroundSamples.Add(new ForegroundSample { Utc = DateTime.UtcNow, Pid = pid, IsExpectedGame = pid == expectedPid });
            await Task.Delay(TimeSpan.FromSeconds(1), cancellation).ConfigureAwait(false);
        }
    }

    private static int ForegroundPid()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(foreground, out uint pid);
        return (int)pid;
    }

    private static void RequireGame(WindowsSystem system)
    {
        if (!system.Snapshot().Any(p => p.Name.Equals(GameExecutable, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The Fortnite game client is not running; this benchmark does not launch or control the game.");
    }

    private static SnapshotMetadata Metadata(WindowsSystem system)
    {
        var snapshot = system.Snapshot();
        var data = new SnapshotMetadata { Utc = DateTime.UtcNow, ProcessCount = snapshot.Count, ActivePowerScheme = system.GetPowerScheme() };
        string nativeRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spencer-native", "app", "runtime");
        string bridgeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexLanBridge", "App");
        data.NativeRuntimeProcessCount = snapshot.Count(p => ProcessPolicy.Under(p.Path, nativeRuntime));
        data.LocalBridgeProcessCount = snapshot.Count(p => ProcessPolicy.Under(p.Path, bridgeRoot));
        using (var current = Process.GetCurrentProcess()) data.TourneyModeOwnWorkingSetMB = Math.Round(current.WorkingSet64 / 1048576d, 2);
        foreach (var game in snapshot.Where(p => p.Name.Equals(GameExecutable, StringComparison.OrdinalIgnoreCase)))
        {
            var item = new GameMetadata { Pid = game.Id, StartTicks = game.StartTicks, Path = game.Path };
            try { item.Priority = ((ProcessPriorityClass)system.GetPriority(game)).ToString(); }
            catch (Exception error) { item.PriorityError = error.Message; }
            data.Games.Add(item);
        }
        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            GetWindowThreadProcessId(foreground, out uint pid);
            data.ForegroundPid = (int)pid;
            data.ForegroundProcess = snapshot.FirstOrDefault(p => p.Id == pid)?.Name ?? "Unavailable";
        }
        return data;
    }

    private static bool IsGameForeground(SnapshotMetadata metadata) => metadata.ForegroundProcess.Equals(GameExecutable, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> QueueCollectionAsync(BenchmarkPlan plan, BenchmarkStatus status)
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:45875"), Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("X-Codex-Bridge", "1");
        client.DefaultRequestHeaders.Add("X-Codex-Project", plan.ProjectId);
        bool promptSubmitted = false;
        bool workPlanSubmitted = false;
        string task = "PC 1 owns TM-BENCHMARK-COLLECT-01. Analyze the completed standalone Tourney Mode live benchmark at " + plan.OutputDirectory
            + ". Read benchmark-status.json and engine.log first. Compare OFF1/OFF2/ON1/ON2/ON3/OFF3 CSVs with the benchmark analyzer; filter or disclose invalid foreground/scene samples. Verify session.json is absent, active power matches recorded OriginalPowerScheme, and Native/bridge restoration readiness. If restoration failed, retain the recovery journal and use the authorized restore path before reporting. Report actual frame-time/FPS results and limitations: fixed UEFN Creative edit scene, unrepresentative combat, no direct end-to-end latency measurement. Do not rerun the live test or close apps merely to collect results. Acceptance: evidence-based comparison, restoration evidence, and truthful final report to the user.";
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await client.GetAsync("/status").ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                var local = document.RootElement.GetProperty("local");
                string? thread = local.GetProperty("threadId").GetString();
                if (thread != plan.ExpectedThreadId) throw new InvalidOperationException("Bridge thread identity changed; collection was not queued.");
                bool busy = local.TryGetProperty("busy", out var busyValue) && busyValue.ValueKind == JsonValueKind.True;
                string? turn = CurrentTurn(document.RootElement);
                string route;
                object payload;
                if (busy && !string.IsNullOrWhiteSpace(turn))
                {
                    route = "/work/plan";
                    payload = new
                    {
                        action = "queue", expectedThreadId = thread, expectedTurnId = turn,
                        tasks = new[] { new { id = "TM-BENCHMARK-COLLECT-01", title = "Analyze live Fortnite benchmark and verify restoration", task } }
                    };
                }
                else
                {
                    // An unconfirmed work-plan request may already have queued this
                    // stable task ID. Never replay it through the non-deduplicated
                    // prompt route merely because the bridge became idle afterward.
                    if (workPlanSubmitted)
                    {
                        status.CollectionErrors.Add("Bridge became idle after a work-plan submission. No fallback prompt was sent; inspect the prior receipt/outcome.");
                        return false;
                    }
                    route = "/prompt";
                    payload = new
                    {
                        text = "AUTOMATED RESULT FOR EXISTING AUTHORIZED TOURNEY MODE TASK (root 06e7f60e-6c71-43b1-b8b8-2472aac35584; do not start a new solution): " + task,
                        expectedThreadId = plan.ExpectedThreadId
                    };
                }
                status.CollectionRoute = route;
                status.CollectionSubmissionAttempted = true;
                status.CollectionOutcomeUncertain = true;
                status.CollectionSubmissionUtc = DateTime.UtcNow;
                // Persist the dispatch intent before the request can start a turn.
                Save(plan, status, status.Result);
                using var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
                if (route == "/prompt") promptSubmitted = true;
                else workPlanSubmitted = true;
                status.CollectionSubmissionCount++;
                using var queued = await client.PostAsync(route, content).ConfigureAwait(false);
                if (!queued.IsSuccessStatusCode) status.CollectionOutcomeUncertain = false;
                queued.EnsureSuccessStatusCode();
                string result = await queued.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var receipt = JsonDocument.Parse(result);
                if (receipt.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    status.CollectionOutcomeUncertain = false;
                    throw new InvalidOperationException("Bridge rejected the collection task: " + result);
                }
                status.CollectionReceipt = result;
                status.CollectionOutcomeUncertain = false;
                AppendLog(plan, "Collection continuation accepted through " + route + " after restoration attempt.");
                return true;
            }
            catch (Exception error)
            {
                status.CollectionErrors.Add($"Attempt {attempt}: {error.Message}");
                AppendLog(plan, "Collection callback: " + error.Message);
                // /prompt has no idempotency key. A timeout or unreadable response
                // can follow acceptance, so it is submitted at most once.
                if (promptSubmitted) return false;
                if (attempt < 3) await Task.Delay(1000).ConfigureAwait(false);
            }
        }
        return false;
    }

    private static string? CurrentTurn(JsonElement root)
    {
        if (root.TryGetProperty("demo", out var demo) && demo.ValueKind == JsonValueKind.Object
            && demo.TryGetProperty("localActivity", out var activity) && activity.ValueKind == JsonValueKind.Object
            && activity.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Object
            && progress.TryGetProperty("turnId", out var turn) && turn.ValueKind == JsonValueKind.String)
            return turn.GetString();
        return null;
    }

    private static void Save(BenchmarkPlan plan, BenchmarkStatus status, string phase)
    {
        status.Phase = phase;
        status.UpdatedUtc = DateTime.UtcNow;
        string path = Path.Combine(plan.OutputDirectory, "benchmark-status.json");
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(JsonSerializer.Serialize(status, Json));
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    private static void AppendLog(BenchmarkPlan plan, string message)
    {
        try { File.AppendAllText(Path.Combine(plan.OutputDirectory, "engine.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine); }
        catch { /* Logging must never prevent restoration. */ }
    }

    public sealed class BenchmarkPlan
    {
        public string OutputDirectory { get; set; } = "";
        public string PresentMonPath { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string ExpectedThreadId { get; set; } = "";
        public bool Comparison { get; set; }
        public bool AllOnOffTest { get; set; }
        public int Pairs { get; set; } = 3;
        public int StabilizationSeconds { get; set; } = 20;
        public int CaptureSeconds { get; set; } = 45;
        public bool SkipCollectionCallback { get; set; }
        public bool WaitForForeground { get; set; } = true;
        public List<string> ExcludedAppPrefixes { get; set; } = ["codex-desktop|"];
        public string SceneDescription { get; set; } = "Fixed Creative scene; no combat or endgame workload.";
        public string[] ComparisonBlockOrder => Comparison ? ["V1A", "V2A", "V2B", "V1B"] : [];
        public int ComparisonTrialsPerBlock => Comparison ? 2 : 0;
        public int ComparisonStabilizationSeconds => Comparison ? 30 : 0;
    }

    private sealed class BenchmarkStatus
    {
        public string RunId { get; set; } = "";
        public string Phase { get; set; } = "";
        public string Result { get; set; } = "Running";
        public string Disclaimer { get; set; } = BenchmarkRunner.Disclaimer;
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; }
        public DateTime? EnabledUtc { get; set; }
        public DateTime? RestoredUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public string PlanPath { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public string PresentMonPath { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string ExpectedThreadId { get; set; } = "";
        public bool Comparison { get; set; }
        public bool AllOnOffTest { get; set; }
        public int CaptureSeconds { get; set; } = 45;
        public bool SkipCollectionCallback { get; set; }
        public bool WaitForForeground { get; set; }
        public List<string> ExcludedAppPrefixes { get; set; } = [];
        public string SceneDescription { get; set; } = "";
        public List<string> BlockOrder { get; set; } = [];
        public List<ComparisonBlock> Blocks { get; set; } = [];
        public GameMetadata? ExpectedGame { get; set; }
        public string OriginalPowerScheme { get; set; } = "";
        public string FinalPowerScheme { get; set; } = "";
        public int InitialProcessCount { get; set; }
        public int FinalProcessCount { get; set; }
        public SnapshotMetadata? InitialSnapshot { get; set; }
        public bool EnableAttempted { get; set; }
        public bool RestorationAttempted { get; set; }
        public bool RestorationSucceeded { get; set; }
        public string RecoveryJournalPath { get; set; } = "";
        public bool RecoveryJournalExists { get; set; }
        public int SelectedProcessCount { get; set; }
        public AppOptions? Options { get; set; }
        public SessionState? ActiveState { get; set; }
        public List<SelectedApp> SelectedApps { get; set; } = [];
        public List<CaptureRecord> Captures { get; set; } = [];
        public List<string> Errors { get; set; } = [];
        public bool CollectionQueued { get; set; }
        public string CollectionRoute { get; set; } = "";
        public bool CollectionSubmissionAttempted { get; set; }
        public int CollectionSubmissionCount { get; set; }
        public DateTime? CollectionSubmissionUtc { get; set; }
        public bool CollectionOutcomeUncertain { get; set; }
        public string CollectionReceipt { get; set; } = "";
        public List<string> CollectionErrors { get; set; } = [];
    }

    private sealed class ComparisonBlock
    {
        public string Label { get; set; } = "";
        public string Mode { get; set; } = "";
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? EnabledUtc { get; set; }
        public DateTime? RestoredUtc { get; set; }
        public AppOptions? Options { get; set; }
        public List<SelectedApp> SelectedApps { get; set; } = [];
        public int SelectedProcessCount { get; set; }
        public SessionState? ActiveState { get; set; }
        public int MaintenancePasses { get; set; }
        public bool EnableAttempted { get; set; }
        public bool TestingSucceeded { get; set; }
        public bool RestorationAttempted { get; set; }
        public bool RestorationSucceeded { get; set; }
        public bool RecoveryJournalExists { get; set; }
        public string FinalPowerScheme { get; set; } = "";
        public List<string> Errors { get; set; } = [];
    }

    private sealed class SelectedApp
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int RecordedProcessCount { get; set; }
    }

    private sealed class CaptureRecord
    {
        public string Label { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Block { get; set; } = "";
        public int DurationSeconds { get; set; } = 45;
        public bool GuardActive { get; set; }
        public int MaintenancePassesBefore { get; set; }
        public int MaintenancePassesAfter { get; set; }
        public int ForegroundSampleIntervalSeconds { get; set; } = 1;
        public List<ForegroundSample> ForegroundSamples { get; set; } = [];
        public string CsvPath { get; set; } = "";
        public string StdoutPath { get; set; } = "";
        public string StderrPath { get; set; } = "";
        public string SessionName { get; set; } = "";
        public int PresentMonPid { get; set; }
        public int? ExitCode { get; set; }
        public bool Completed { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public long CsvBytes { get; set; }
        public string Error { get; set; } = "";
        public SnapshotMetadata Before { get; set; } = new();
        public SnapshotMetadata? After { get; set; }
        public List<string> ValidityWarnings { get; set; } = [];
    }

    private sealed class ForegroundSample
    {
        public DateTime Utc { get; set; }
        public int Pid { get; set; }
        public bool IsExpectedGame { get; set; }
    }

    private sealed class SnapshotMetadata
    {
        public DateTime Utc { get; set; }
        public int ProcessCount { get; set; }
        public int NativeRuntimeProcessCount { get; set; }
        public int LocalBridgeProcessCount { get; set; }
        public double TourneyModeOwnWorkingSetMB { get; set; }
        public string ActivePowerScheme { get; set; } = "";
        public int ForegroundPid { get; set; }
        public string ForegroundProcess { get; set; } = "";
        public List<GameMetadata> Games { get; set; } = [];
    }

    private sealed class GameMetadata
    {
        public int Pid { get; set; }
        public long StartTicks { get; set; }
        public string Path { get; set; } = "";
        public string Priority { get; set; } = "";
        public string PriorityError { get; set; } = "";
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint currentThread, uint foregroundThread, bool attach);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
