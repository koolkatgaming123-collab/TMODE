using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace TourneyMode;

public interface ISystemFacade
{
    void CreateRestorePoint(SystemRestoreCheckpoint checkpoint) => throw new NotSupportedException("System restore checkpoint creation is unavailable.");
    bool VerifyRestorePoint(SystemRestoreCheckpoint checkpoint) => false;
    INetworkTestSystem CreateInternetTestSystem() => throw new NotSupportedException("Adaptive network testing is unavailable.");
    void ApplyWindowsProfile(WindowsProfileBackup backup, Action save, Func<bool> absent, CancellationToken token) => throw new NotSupportedException("Windows preferences/policies are unavailable.");
    void RestoreWindowsProfile(WindowsProfileBackup backup, Action save, Func<bool> absent) => throw new NotSupportedException("Windows preferences/policies recovery is unavailable.");
    StripBackup CaptureStrip(IEnumerable<string> groupIds) => throw new NotSupportedException("Windows stripping is unavailable.");
    void ApplyStrip(StripBackup backup, Action save, Action<string> log, CancellationToken token) => throw new NotSupportedException("Windows stripping is unavailable.");
    void RepairStrip(StripBackup backup, string? groupId, Action save, Action<string> log, CancellationToken token) => throw new NotSupportedException("Windows repair is unavailable.");
    bool AllowLegacyFixtureActivation => false;
    FortniteSettingsBackup? CaptureFortniteSettings(FpsLatencyOptions options, bool nvidiaGpu, bool acceptTearing) => null;
    void ApplyFortniteSettings(FortniteSettingsBackup backup, Action persist) { }
    void RestoreFortniteSettings(FortniteSettingsBackup backup, Action persist) { }
    List<BackgroundPriorityBackup> CaptureBackgroundPriorities(List<AppCandidate> eligible, IEnumerable<string> approved, IEnumerable<string> closing) => [];
    void ApplyBackgroundPriorities(List<BackgroundPriorityBackup> backups, Action persist, Action<string> log) { }
    void RestoreBackgroundPriorities(List<BackgroundPriorityBackup> backups, Action persist, Action<string> log) { }
    CodexBackup CaptureCodex(IEnumerable<string> keep) => new();
    Task StopCodex(CodexBackup backup, bool force, Action save, Action<string> log) => Task.CompletedTask;
    Task RestoreCodex(CodexBackup backup, Action save, Action<string> log) => Task.CompletedTask;
    void ConfigureProtection(IEnumerable<string> paths) { }
    bool AcPowerAvailable() => true;
    DateTime UtcNow => DateTime.UtcNow;
    List<ProcessIdentity> Snapshot();
    List<ProcessIdentity> Games();
    GamePresence GetGamePresence() => Games().Any(ProcessPolicy.IsFortnite) ? GamePresence.Present : GamePresence.Absent;
    List<ProcessIdentity> WatchedProcesses(List<AppCandidate> apps);
    bool Alive(ProcessIdentity p);
    bool CanTouch(ProcessIdentity p);
    void CloseWindow(ProcessIdentity p);
    void Terminate(ProcessIdentity p);
    int GetPriority(ProcessIdentity p);
    void SetPriority(ProcessIdentity p, int priority);
    bool Launch(AppCandidate app);
    List<NotificationBackup> CaptureNotifications();
    void SuppressNotifications();
    void RestoreNotification(NotificationBackup item);
    string GetPowerScheme();
    void SetPowerScheme(string guid);
    bool? GetDnd();
    void SetDnd(bool enabled);
    List<NotificationBackup> CaptureTuning();
    void ApplyTuning();
    void RestoreTuning(NotificationBackup item);
    List<string> CreatePlan(string original, string destination, bool fortniteOnly);
    List<string> CreateAdaptivePlan(string original, string destination, PowerPreference preference) => CreatePlan(original, destination, false);
    void ApplyGameTuning(bool preserveCapture) => ApplyTuning();
    void ApplyGameTuningSubset(List<NotificationBackup> values) => throw new NotSupportedException("Selected Windows fixes are unsupported by this system provider.");
    void DeletePlan(string guid);
    Task StopNative();
    Task StartNative();
    bool NativeReady();
    bool MobileReady();
    bool AppReady(AppCandidate app);
    List<ServiceBackup> CaptureOptionalServices(bool maximumHeadroom = false) => [];
    void StopOptionalService(ServiceBackup item) { }
    void RestoreOptionalService(ServiceBackup item) { }
    List<ScheduledTaskBackup> CaptureOptionalTasks() => [];
    void DisableOptionalTask(ScheduledTaskBackup item) { }
    void RestoreOptionalTask(ScheduledTaskBackup item) { }
    NetworkPriorityBackup? PrepareEthernetPriority() => null;
    void ApplyEthernetPriority(NetworkPriorityBackup item) { }
    void RestoreEthernetPriority(NetworkPriorityBackup item) { }
    EthernetAdapterBackup? CaptureEthernetAdapter() => null;
    void ApplyEthernetAdapter(EthernetAdapterBackup item) { }
    void RestoreEthernetAdapter(EthernetAdapterBackup item) { }
    void SetRecoveryStartup(bool enabled);
    void SetRecoveryStartup(bool enabled, string workflow) => SetRecoveryStartup(enabled);
    Task Delay(int ms);
}

public sealed class WindowsSystem : ISystemFacade
{
    public void CreateRestorePoint(SystemRestoreCheckpoint checkpoint) => SystemRecovery.Create(checkpoint);
    public bool VerifyRestorePoint(SystemRestoreCheckpoint checkpoint) => SystemRecovery.Verify(checkpoint);
    public INetworkTestSystem CreateInternetTestSystem() => new InternetNicSystem();
    public void ApplyWindowsProfile(WindowsProfileBackup backup, Action save, Func<bool> absent, CancellationToken token) => WindowsProfile.Apply(backup, save, absent, token);
    public void RestoreWindowsProfile(WindowsProfileBackup backup, Action save, Func<bool> absent) => WindowsProfile.Restore(backup, save, absent);
    public StripBackup CaptureStrip(IEnumerable<string> groupIds) => WindowsStrip.Capture(groupIds);
    public void ApplyStrip(StripBackup backup, Action save, Action<string> log, CancellationToken token) => WindowsStrip.Apply(backup, save, log, token);
    public void RepairStrip(StripBackup backup, string? groupId, Action save, Action<string> log, CancellationToken token) => WindowsStrip.Repair(backup, groupId, save, log, token);
    public FortniteSettingsBackup? CaptureFortniteSettings(FpsLatencyOptions options, bool nvidiaGpu, bool acceptTearing)
        => FortniteSettings.Preview(FortniteSettings.DefaultPath, options, nvidiaGpu, acceptTearing);
    public void ApplyFortniteSettings(FortniteSettingsBackup backup, Action persist)
        => FortniteSettings.Apply(backup, persist, () => GetGamePresence() == GamePresence.Absent);
    public void RestoreFortniteSettings(FortniteSettingsBackup backup, Action persist)
        => FortniteSettings.Restore(backup, persist, () => GetGamePresence() == GamePresence.Absent);
    public List<BackgroundPriorityBackup> CaptureBackgroundPriorities(List<AppCandidate> eligible, IEnumerable<string> approved, IEnumerable<string> closing)
        => BackgroundGovernor.Capture(eligible, approved, new WindowsBackgroundPrioritySystem(this, closing));
    public void ApplyBackgroundPriorities(List<BackgroundPriorityBackup> backups, Action persist, Action<string> log)
        => BackgroundGovernor.Apply(new WindowsBackgroundPrioritySystem(this), backups, persist, log);
    public void RestoreBackgroundPriorities(List<BackgroundPriorityBackup> backups, Action persist, Action<string> log)
        => BackgroundGovernor.Restore(new WindowsBackgroundPrioritySystem(this), backups, persist, log);
    public CodexBackup CaptureCodex(IEnumerable<string> keep) => CodexLifecycle.Capture(keep);
    public Task StopCodex(CodexBackup backup, bool force, Action save, Action<string> log) => CodexLifecycle.StopAsync(backup, force, save, log);
    public Task RestoreCodex(CodexBackup backup, Action save, Action<string> log) => CodexLifecycle.RestoreAsync(backup, save, log);
    private HashSet<string> extraProtected = new(StringComparer.OrdinalIgnoreCase);
    public void ConfigureProtection(IEnumerable<string> paths) => extraProtected = ConnectionProtection.NormalizePaths(paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public bool AcPowerAvailable() => SystemAnalysis.AcPowerOnline() == true;
    private readonly HashSet<int> ownAncestors = ProcessPolicy.Ancestors(ProcessCatalog.Snapshot(), Environment.ProcessId);
    public List<ProcessIdentity> Snapshot() => ProcessCatalog.Snapshot();
    public List<ProcessIdentity> Games()
    {
        var games = new List<ProcessIdentity>();
        foreach (var process in Process.GetProcessesByName("FortniteClient-Win64-Shipping"))
        {
            using (process)
            {
                try { games.Add(new ProcessIdentity { Id = process.Id, Name = process.ProcessName + ".exe", SessionId = process.SessionId, StartTicks = process.StartTime.ToUniversalTime().Ticks, Path = ProcessCatalog.GetPath(process.Id) }); }
                catch { /* Exited or inaccessible: retry on the next low-frequency tick. */ }
            }
        }
        return games;
    }
    public GamePresence GetGamePresence()
    {
        try
        {
            var processes = Process.GetProcessesByName("FortniteClient-Win64-Shipping");
            try { return processes.Length > 0 ? GamePresence.Present : GamePresence.Absent; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { return GamePresence.Unknown; }
    }
    public bool Alive(ProcessIdentity p) => ProcessCatalog.Matches(p);
    public List<ProcessIdentity> WatchedProcesses(List<AppCandidate> apps)
    {
        // One name-filtered snapshot, no Native/bridge HTTP probes during a match.
        // Helpers must still be bound to this app's fresh root by WatchTargets.
        var names = apps.SelectMany(a => a.Processes.Select(p => p.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ProcessCatalog.Snapshot(names);
    }
    public bool CanTouch(ProcessIdentity p)
    {
        if (!Alive(p)) return false;
        return !extraProtected.Contains(p.Path) && !ProcessPolicy.Protected(p, Process.GetCurrentProcess().SessionId, ownAncestors)
            && !ProcessCatalog.Critical(p.Id) && !IsProtectedDescendant(p);
    }
    private bool IsProtectedDescendant(ProcessIdentity process)
    {
        // Shared helpers can live outside Discord's installation. Follow the
        // current parent map before every close, without terminating any tree.
        var parents = ProcessCatalog.ParentIds();
        int id = process.Id;
        var seen = new HashSet<int>();
        while (parents.TryGetValue(id, out int parent) && parent > 0 && seen.Add(parent))
        {
            string path = ProcessCatalog.GetPath(parent);
            if (extraProtected.Contains(path) || ProcessPolicy.IsGamingDependency(new ProcessIdentity { Name = Path.GetFileName(path), Path = path })) return true;
            id = parent;
        }
        return false;
    }
    public void CloseWindow(ProcessIdentity p)
    {
        if (!CanTouch(p)) return;
        using var process = Process.GetProcessById(p.Id);
        if (!ProcessCatalog.MatchesPinned(process, p)) return;
        process.CloseMainWindow();
    }
    public void Terminate(ProcessIdentity p)
    {
        if (!CanTouch(p)) throw new InvalidOperationException("Process is protected, inaccessible or its identity changed.");
        using var process = Process.GetProcessById(p.Id);
        if (!ProcessCatalog.MatchesPinned(process, p)) return;
        process.Kill(entireProcessTree: false);
    }
    public int GetPriority(ProcessIdentity p) { using var process = Process.GetProcessById(p.Id); if (!ProcessCatalog.MatchesPinned(process, p)) throw new InvalidOperationException("Process identity changed."); return (int)process.PriorityClass; }
    public void SetPriority(ProcessIdentity p, int priority)
    {
        if (!Alive(p) || !ProcessPolicy.IsFortnite(p)) throw new InvalidOperationException("Fortnite identity changed.");
        using var process = Process.GetProcessById(p.Id);
        if (!ProcessCatalog.MatchesPinned(process, p)) throw new InvalidOperationException("Fortnite identity changed.");
        process.PriorityClass = (ProcessPriorityClass)priority;
        if ((int)process.PriorityClass != priority) throw new InvalidOperationException("Windows did not accept the priority change.");
    }
    public bool Launch(AppCandidate app)
    {
        if (!File.Exists(app.LaunchPath)) throw new FileNotFoundException("Recovery launcher is missing.", app.LaunchPath);
        StartKnown(app.LaunchPath, app.Arguments);
        foreach (string path in app.AdditionalLaunchPaths)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Additional recovery launcher is missing.", path);
            StartKnown(path, "");
        }
        // The engine separately observes returning processes; shell dispatch alone is not success.
        return true;
    }
    private static void StartKnown(string path, string args)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".exe" or ".vbs" or ".lnk")) throw new InvalidOperationException("Unsupported recovery launcher.");
        if (extension == ".vbs")
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            info.ArgumentList.Add(path);
            Process.Start(info)?.Dispose();
        }
        else Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)!, WindowStyle = ProcessWindowStyle.Normal })?.Dispose();
    }
    public List<NotificationBackup> CaptureNotifications() => WindowsSettings.CaptureNotifications();
    public void SuppressNotifications() => WindowsSettings.SuppressNotifications();
    public void RestoreNotification(NotificationBackup item) => WindowsSettings.RestoreNotification(item);
    public string GetPowerScheme() => WindowsSettings.GetPowerScheme();
    public void SetPowerScheme(string guid) => WindowsSettings.SetPowerScheme(guid);
    public bool? GetDnd() => WindowsSettings.GetDoNotDisturb();
    public void SetDnd(bool enabled) => WindowsSettings.SetDoNotDisturb(enabled);
    public List<NotificationBackup> CaptureTuning() => PerformanceTuning.CaptureGameSettings();
    public void ApplyTuning() => PerformanceTuning.ApplyGameSettings();
    public void RestoreTuning(NotificationBackup item) => PerformanceTuning.RestoreGameSetting(item);
    public List<string> CreatePlan(string original, string destination, bool fortniteOnly) => PerformanceTuning.CreatePlan(original, destination, fortniteOnly);
    public List<string> CreateAdaptivePlan(string original, string destination, PowerPreference preference) => PerformanceTuning.CreateAdaptivePlan(original, destination, preference);
    public void ApplyGameTuning(bool preserveCapture) => PerformanceTuning.ApplyGameSettings(preserveCapture);
    public void ApplyGameTuningSubset(List<NotificationBackup> values) => PerformanceTuning.ApplyGameSettingsSubset(values);
    public void DeletePlan(string guid) => PerformanceTuning.DeletePlan(guid);
    public Task StopNative() => NativeLifecycle.StopAsync();
    public Task StartNative() => NativeLifecycle.StartAsync();
    public bool NativeReady() => NativeLifecycle.IsServiceReady();
    public bool MobileReady() => Snapshot().Any(p => p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) && NativeLifecycle.IsKnownPhoneBridge(p.Id));
    public bool AppReady(AppCandidate app) => app.Id == "codex-bridge"
        ? ProcessCatalog.ProbeLocalBridge(app.BridgeProjectId, app.BridgeThreadId).Ready
        : TourneyEngine.AppReturned(app, Snapshot());
    public Task Delay(int ms) => Task.Delay(ms);
    public List<ServiceBackup> CaptureOptionalServices(bool maximumHeadroom = false) => TournamentResources.CaptureOptionalServices(maximumHeadroom);
    public void StopOptionalService(ServiceBackup item) => TournamentResources.StopOptionalService(item);
    public void RestoreOptionalService(ServiceBackup item) => TournamentResources.RestoreOptionalService(item);
    public List<ScheduledTaskBackup> CaptureOptionalTasks() => HeadroomTasks.Capture();
    public void DisableOptionalTask(ScheduledTaskBackup item) => HeadroomTasks.SetEnabled(item, false);
    public void RestoreOptionalTask(ScheduledTaskBackup item) => HeadroomTasks.SetEnabled(item, true);
    public NetworkPriorityBackup? PrepareEthernetPriority() => TournamentResources.PrepareEthernetPriority();
    public void ApplyEthernetPriority(NetworkPriorityBackup item) => TournamentResources.ApplyEthernetPriority(item);
    public void RestoreEthernetPriority(NetworkPriorityBackup item) => TournamentResources.RestoreEthernetPriority(item);
    public EthernetAdapterBackup? CaptureEthernetAdapter() => EthernetTuning.Capture();
    public void ApplyEthernetAdapter(EthernetAdapterBackup item) => EthernetTuning.Apply(item);
    public void RestoreEthernetAdapter(EthernetAdapterBackup item) => EthernetTuning.Restore(item);
    public void SetRecoveryStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("TourneyModeRecovery", "\"" + Environment.ProcessPath + "\" --restore", RegistryValueKind.String);
        else key.DeleteValue("TourneyModeRecovery", false);
    }
    public void SetRecoveryStartup(bool enabled, string workflow)
    {
        if (!enabled || workflow == "Tourney") { SetRecoveryStartup(enabled); return; }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key.SetValue("TourneyModeRecovery", "\"" + Environment.ProcessPath + "\" --show-recovery", RegistryValueKind.String);
    }
}

public sealed class TourneyEngine
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly ISystemFacade system;
    private readonly Func<List<AppCandidate>>? candidates;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> deniedPriorities = [];
    private readonly HashSet<string> watchedAttempts = [];
    private volatile bool restoreRequested;
    private readonly object restoreRequestSync = new();
    private int activationOperations;
    private int internetOperationClaimed;
    private long restoreGeneration;
    public bool InternetBusy => Volatile.Read(ref internetOperationClaimed) != 0;
    private CancellationTokenSource? internetCancellation;
    public event Action<string>? Log;
    public string DataDirectory { get; }
    private string StatePath => Path.Combine(DataDirectory, "session.json");
    public SessionState? State { get; private set; }
    public bool IsActive => State is not null;
    // IsActive preserves the recovery-journal contract. Stopping the guard and
    // completing potentially disruptive recovery are distinct states.
    public bool StopRequested => restoreRequested;
    public bool IsModeRunning => State?.Phase == "Active" && !restoreRequested;
    public bool ActivationBusy => Volatile.Read(ref activationOperations) != 0;
    public bool GameClientRunning => system.GetGamePresence() != GamePresence.Absent;
    private bool GameDefinitelyAbsent() => system.GetGamePresence() == GamePresence.Absent;
    public bool RecoveryDeferredUntilGameExit => State?.Phase == "Restore deferred";
    public bool CanStartStrip => State is null || State is { Workflow: "Tweak", Phase: "Active", RestorePoint.Verified: true }
        && !restoreRequested && (State.Strip is null || State.Strip.FullyRestored);
    private readonly Func<AppOptions, Task<PcAnalysis>> analysis;
    public Task<PcAnalysis> CaptureAnalysisAsync(AppOptions options) => analysis(PerformancePlanner.Copy(options));
    public async Task<OptimizationPlan> AnalyzePlanAsync(AppOptions options)
    {
        var report = await analysis(options).ConfigureAwait(false);
        return PerformancePlanner.Build(report, options, GameClientRunning, IsActive);
    }
    private void Feature(string id, string name, FeatureState state, string detail)
    {
        if (State is null) return;
        var entry = State.Features.FirstOrDefault(f => f.Id == id);
        if (entry?.State == state && entry.Detail == detail) return;
        if (entry is null) { entry = new FeatureResult { Id = id, Name = name }; State.Features.Add(entry); }
        entry.State = state; entry.Detail = detail; entry.UpdatedUtc = system.UtcNow;
        Save();
        Say(name + ": " + state + " — " + detail);
    }
    public TourneyEngine(string? dataDirectory = null, ISystemFacade? system = null, Func<List<AppCandidate>>? candidates = null,
        Func<AppOptions, Task<PcAnalysis>>? analysis = null)
    {
        this.system = system ?? new WindowsSystem();
        this.candidates = candidates;
        this.analysis = analysis ?? SystemAnalysis.CaptureAsync;
        DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TourneyMode");
        Directory.CreateDirectory(DataDirectory);
        if (File.Exists(StatePath))
        {
            // A corrupt journal must never silently become OFF and discard recovery.
            State = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(StatePath), Json) ?? throw new InvalidDataException("Recovery journal is empty.");
            if (State.Schema != 1) throw new InvalidDataException("Unrecognized recovery journal. Keep session.json for recovery.");
            // A newly opened controller must never resume app closure from a prior
            // controller's journal. Only recovery may proceed after an interruption.
            if (State.Phase is "Starting" or "Active" or "Restoring") State.Phase = "Needs recovery";
            restoreRequested = true;
            this.system.ConfigureProtection(State.Options.ProtectedAppPaths);
        }
    }

    private void Say(string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
        try { File.AppendAllText(Path.Combine(DataDirectory, "activity.log"), line + Environment.NewLine); } catch { }
        Log?.Invoke(line);
    }
    private static void AtomicWrite(string file, string contents)
    {
        string tmp = file + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream)) { writer.Write(contents); writer.Flush(); stream.Flush(true); }
        File.Move(tmp, file, true);
    }
    private void Save() { if (State is not null) AtomicWrite(StatePath, JsonSerializer.Serialize(State, Json)); }
    public AppOptions LoadOptions() => File.Exists(Path.Combine(DataDirectory, "options.json"))
        ? ProfileDefaults.Upgrade(JsonSerializer.Deserialize<AppOptions>(File.ReadAllText(Path.Combine(DataDirectory, "options.json")), Json) ?? ProfileDefaults.Create()) : ProfileDefaults.Create();
    public void SaveOptions(AppOptions options) => AtomicWrite(Path.Combine(DataDirectory, "options.json"), JsonSerializer.Serialize(options, Json));
    public List<AppCandidate> Scan(bool fortniteOnly = false, bool maximumHeadroom = false, IEnumerable<string>? protectedPaths = null)
    {
        var paths = ConnectionProtection.NormalizePaths(protectedPaths ?? LoadOptions().ProtectedAppPaths);
        system.ConfigureProtection(paths);
        var snapshot = system.Snapshot();
        var keepIds = ConnectionProtection.ExtraIds(snapshot, paths);
        return (candidates?.Invoke() ?? ProcessPolicy.Candidates(snapshot, fortniteOnly: fortniteOnly, maximumHeadroom: maximumHeadroom, protectedPaths: paths))
            .Where(a => !ProcessPolicy.IsDiscordApp(a) && !ConnectionProtection.IsApp(a)
                && !paths.Contains(a.LaunchPath, StringComparer.OrdinalIgnoreCase) && !a.Processes.Any(p => keepIds.Contains(p.Id))).ToList();
    }
    public void RequestRestore()
    {
        lock (restoreRequestSync)
        {
            restoreRequested = true;
            Interlocked.Increment(ref restoreGeneration);
        }
        try { internetCancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void ArmOperation(long expectedRestoreGeneration)
    {
        lock (restoreRequestSync)
        {
            if (Volatile.Read(ref restoreGeneration) != expectedRestoreGeneration)
                throw new OperationCanceledException("A stop request superseded this queued or pending activation.");
            restoreRequested = false;
        }
    }

    private void NetworkOutsideGameGate()
    {
        if (!GameDefinitelyAbsent()) throw new InvalidOperationException("Close Fortnite before any network trial or adapter restoration. Pending originals remain recorded.");
    }
    private void SaveInternetState(InternetCalibrationState calibration)
    {
        if (State is null) throw new InvalidOperationException("The shared network recovery session is missing.");
        State.InternetCalibration = calibration; Save();
    }
    private void PrepareInternetSession()
    {
        NetworkOutsideGameGate();
        if (State is not null && State.Workflow != "Internet") throw new InvalidOperationException("Restore the current optimization session before network calibration.");
        if (State is null)
        {
            var options = TweakPolicy.SafeScope(new AppOptions { OptionsVersion = 9, HighPerformancePower = false, GameTuning = false });
            State = new() { Workflow = "Internet", Options = options, CreatedByVersion = ReleaseIdentity.Version, RestorePoint = new() { Attempted = true } };
            Save(); system.SetRecoveryStartup(true, "Internet");
            Feature("restore-point", "Windows restore point", FeatureState.Pending, "A verified safety point is required before adaptive NIC experiments.");
            system.CreateRestorePoint(State.RestorePoint);
        }
        if (State.RestorePoint is not { Verified: true, SequenceNumber: > 0 } point || !system.VerifyRestorePoint(point))
            throw new InvalidOperationException("The network safety restore point is unavailable. No adapter change is allowed.");
        if (State.InternetCalibration is { MutationAttempted: true, Restored: false })
            throw new InvalidOperationException("Restore the pending NIC originals before another calibration.");
    }

    public async Task<InternetTestReport> CalibrateInternetAsync(InternetTestOptions options, CancellationToken token = default, IProgress<InternetTestProgress>? progress = null)
    {
        if (Interlocked.CompareExchange(ref internetOperationClaimed, 1, 0) != 0) throw new InvalidOperationException("Another Internet Optimizer operation is already queued or running.");
        long initialRestoreGeneration = Volatile.Read(ref restoreGeneration);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        internetCancellation = cancellation;
        bool entered = false;
        try
        {
            await gate.WaitAsync(cancellation.Token); entered = true;
            if (Volatile.Read(ref restoreGeneration) != initialRestoreGeneration) throw new OperationCanceledException("A restore request superseded this queued network test.");
            ArmOperation(initialRestoreGeneration);
            return await Task.Run(async () =>
            {
                progress?.Report(new() { Phase = "Safety checkpoint", Message = "Verifying a Windows restore point before any NIC trial.", Completed = 0, Total = 1 });
                PrepareInternetSession(); cancellation.Token.ThrowIfCancellationRequested();
                var optimizer = new InternetOptimizer(system.CreateInternetTestSystem());
                optimizer.Progress += update => progress?.Report(update);
                State!.Phase = "Network calibration"; Save();
                var report = await optimizer.CalibrateAsync(options, SaveInternetState, NetworkOutsideGameGate, cancellation.Token).ConfigureAwait(false);
                State.Phase = report.RollbackVerified ? "Active" : "Needs recovery"; Save();
                AtomicWrite(Path.Combine(DataDirectory, "last-internet-report.json"), JsonSerializer.Serialize(report, Json));
                Feature("internet-calibration", "Internet calibration", report.RollbackVerified ? FeatureState.Recovered : FeatureState.Failed,
                    report.RollbackVerified ? "Tested NIC trials completed or stopped; exact original settings are restored. No winning configuration is applied automatically."
                        : "Original NIC settings are not verified. Keep this recovery session and Restore originals.");
                return report;
            }, cancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            if (entered && State?.Workflow == "Internet") { State.Phase = "Needs recovery"; Save(); }
            throw;
        }
        finally { internetCancellation = null; Volatile.Write(ref internetOperationClaimed, 0); if (entered) gate.Release(); }
    }

    public async Task ApplyInternetBestAsync(InternetTestReport report, CancellationToken token = default,
        IProgress<InternetTestProgress>? progress = null, bool allowProvisional = false)
    {
        if (Interlocked.CompareExchange(ref internetOperationClaimed, 1, 0) != 0) throw new InvalidOperationException("Another Internet Optimizer operation is already queued or running.");
        long initialRestoreGeneration = Volatile.Read(ref restoreGeneration);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); internetCancellation = cancellation;
        bool entered = false;
        try
        {
            await gate.WaitAsync(cancellation.Token); entered = true;
            if (Volatile.Read(ref restoreGeneration) != initialRestoreGeneration) throw new OperationCanceledException("A restore request superseded this queued network apply.");
            ArmOperation(initialRestoreGeneration);
            await Task.Run(async () =>
            {
                PrepareInternetSession(); cancellation.Token.ThrowIfCancellationRequested();
                var optimizer = new InternetOptimizer(system.CreateInternetTestSystem());
                optimizer.Progress += update => progress?.Report(update);
                var applied = await optimizer.ApplyBestAsync(report, SaveInternetState, NetworkOutsideGameGate, cancellation.Token, allowProvisional).ConfigureAwait(false);
                State!.InternetCalibration = applied; State.Phase = "Active"; Save();
                Feature("internet-result", "Reviewed NIC result", FeatureState.Applied,
                    applied.Phase == "Applied provisional result" ? "Explicit provisional ping/stability choice applied. Local NIC processing latency remains unmeasured; no full three-goal optimum is claimed."
                        : "Qualified best-tested configuration applied; exact original NIC settings remain available for Restore.");
            }, cancellation.Token).ConfigureAwait(false);
        }
        catch { if (entered && State?.Workflow == "Internet") { State.Phase = "Needs recovery"; Save(); } throw; }
        finally { internetCancellation = null; Volatile.Write(ref internetOperationClaimed, 0); if (entered) gate.Release(); }
    }

    public Task RestoreInternetAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (State is not null && State.Workflow != "Internet") throw new InvalidOperationException("The active session is not an Internet Optimizer session.");
        return RestoreAsync();
    }

    public async Task<OOSLaunchResult> OpenPrivacyToolAsync(string executablePath, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (IsActive || !GameDefinitelyAbsent()) throw new InvalidOperationException("Finish the current session and close Fortnite before opening the external privacy tool.");
            var tool = new OOSPrivacy();
            var inspection = await Task.Run(() => tool.Inspect(executablePath), token).ConfigureAwait(false);
            if (!inspection.Verified) return new(false, inspection.Message);
            var point = new SystemRestoreCheckpoint { Attempted = true };
            string receipt = Path.Combine(DataDirectory, "external-privacy-safety.json");
            AtomicWrite(receipt, JsonSerializer.Serialize(point, Json));
            await Task.Run(() => system.CreateRestorePoint(point), token).ConfigureAwait(false);
            AtomicWrite(receipt, JsonSerializer.Serialize(point, Json));
            if (!point.Verified || point.SequenceNumber <= 0) throw new InvalidOperationException("A verified Windows restore point is required before proceeding.");
            token.ThrowIfCancellationRequested();
            return await Task.Run(() => tool.OpenInterface(executablePath, () => IsActive || !GameDefinitelyAbsent()
                ? "Close Fortnite and finish current recovery first." : !system.VerifyRestorePoint(point) ? "The Windows safety restore point is unavailable." : null), token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public Task<StripBackup> CaptureStripAsync(IEnumerable<string> groupIds)
    {
        var ids = groupIds.ToArray();
        if (!CanStartStrip || !GameDefinitelyAbsent()) throw new InvalidOperationException("Finish recovery and close Fortnite before reviewing Windows feature changes.");
        return Task.Run(() => system.CaptureStrip(ids));
    }

    public async Task EnableStripAsync(StripBackup reviewed, CancellationToken token = default, IProgress<TweakProgress>? progress = null)
    {
        long initialRestoreGeneration = Volatile.Read(ref restoreGeneration);
        await gate.WaitAsync(token);
        try
        {
            if (!CanStartStrip || !GameDefinitelyAbsent()) throw new InvalidOperationException("Close Fortnite and finish pending recovery before stripping Windows features.");
            if (DateTime.UtcNow - reviewed.CapturedUtc > TimeSpan.FromMinutes(10) || reviewed.CapturedUtc > DateTime.UtcNow.AddMinutes(1))
                throw new InvalidOperationException("The reviewed Windows feature snapshot expired. Review the selection again.");
            if (reviewed.ApplyAttempted) throw new InvalidOperationException("This feature snapshot was already used.");
            ArmOperation(initialRestoreGeneration);
            // Copy the exact reviewed originals. WindowsStrip.Apply verifies them again
            // rather than silently recapturing newly changed services after consent.
            var backup = JsonSerializer.Deserialize<StripBackup>(JsonSerializer.Serialize(reviewed, Json), Json) ?? throw new InvalidDataException("Empty strip review.");
            if (State is null)
            {
                var options = TweakPolicy.SafeScope(new AppOptions { OptionsVersion = 9, HighPerformancePower = false, GameTuning = false });
                State = new() { Workflow = "Strip", Options = options, CreatedByVersion = ReleaseIdentity.Version };
                Save(); system.SetRecoveryStartup(true, "Strip");
                State.RestorePoint = new() { Attempted = true }; Save();
                progress?.Report(new() { Stage = "Creating restore point", Detail = "Windows must verify a new safety point before any service is changed.", Percent = 15 });
                system.CreateRestorePoint(State.RestorePoint);
            }
            else progress?.Report(new() { Stage = "Checking restore point", Detail = "Using the verified safety checkpoint created before this session's changes.", Percent = 15 });
            if (State.RestorePoint is not { Verified: true, SequenceNumber: > 0 } point || !system.VerifyRestorePoint(point))
                throw new InvalidOperationException("The Windows restore point cannot be verified. No stripping is allowed; restore this session and enable System Protection before retrying.");
            token.ThrowIfCancellationRequested();
            if (restoreRequested || Volatile.Read(ref restoreGeneration) != initialRestoreGeneration) throw new OperationCanceledException("Stop requested before Windows feature changes.");
            if (!GameDefinitelyAbsent()) throw new InvalidOperationException("Fortnite started. No Windows features were stripped.");
            State.Strip = backup; State.Phase = "Applying Windows feature profile"; Save();
            progress?.Report(new() { Stage = "Applying selected groups", Detail = "Original service settings are saved before every change.", Percent = 50 });
            system.ApplyStrip(backup, Save, Say, token);
            token.ThrowIfCancellationRequested();
            if (restoreRequested || Volatile.Read(ref restoreGeneration) != initialRestoreGeneration) throw new OperationCanceledException("Stop requested; recorded Windows feature changes need recovery.");
            State.Phase = "Active";
            Feature("strip", "Optional Windows features", backup.HasChanges ? FeatureState.Applied : FeatureState.Skipped,
                "Only the reviewed groups were attempted. Use per-feature Repair to restore them. See group statuses for skips/rollback.");
            progress?.Report(new() { Stage = "Completed", Detail = "Review recorded group outcomes. Security, networking, audio/input and game dependencies remain protected.", Percent = 100 });
        }
        catch
        {
            if (State is not null) { State.Phase = "Needs recovery"; Save(); }
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task RepairStripAsync(string? groupId, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        bool finishSession = false;
        try
        {
            if (State?.Strip is not { } backup) return;
            if (!GameDefinitelyAbsent()) throw new InvalidOperationException("Close Fortnite before repairing Windows features.");
            system.RepairStrip(backup, groupId, Save, Say, token);
            Save();
            finishSession = backup.FullyRestored && State.Workflow == "Strip";
            if (backup.FullyRestored) Feature("strip", "Optional Windows features", FeatureState.Recovered, "All recorded Windows feature groups restored to their original settings.");
        }
        catch { if (State is not null) { State.Phase = "Needs recovery"; Save(); } throw; }
        finally { gate.Release(); }
        if (finishSession) await RestoreAsync();
    }

    public Task EnableAsync(AppOptions options, CancellationToken token = default) => EnableCoreAsync(options, "Tourney", token);
    public Task EnableTweakAsync(AppOptions options, CancellationToken token = default, IProgress<TweakProgress>? progress = null)
    {
        // A tweak session can never inherit app/Native/service/network shutdown.
        var safe = TweakPolicy.SafeScope(options);
        if (safe.ExperimentalBackgroundGovernor) safe.Intent.Fps.BackgroundAppPaths = options.Intent.Fps.BackgroundAppPaths.ToList();
        safe.ReviewedPlanFingerprint = options.ReviewedPlanFingerprint;
        return EnableCoreAsync(safe, "Tweak", token, progress);
    }
    private async Task EnableCoreAsync(AppOptions options, string workflow, CancellationToken token = default, IProgress<TweakProgress>? progress = null)
    {
        long initialRestoreGeneration = Volatile.Read(ref restoreGeneration);
        Interlocked.Increment(ref activationOperations);
        bool entered = false;
        SessionState? ownedState = null;
        try
        {
            await gate.WaitAsync(token); entered = true;
            void TweakBoundary()
            {
                token.ThrowIfCancellationRequested();
                if (restoreRequested || Volatile.Read(ref restoreGeneration) != initialRestoreGeneration)
                    throw new OperationCanceledException("Turn OFF requested; further activation and app shutdown stopped.");
                if (workflow == "Tweak" && !GameDefinitelyAbsent()) throw new InvalidOperationException("Fortnite started or its absence is unknown. Applying stopped; recorded changes can be restored.");
            }
            if (Volatile.Read(ref restoreGeneration) != initialRestoreGeneration)
                throw new OperationCanceledException("A stop request superseded this queued activation.");
            if (State is not null) throw new InvalidOperationException("Restore the existing session before enabling again.");
            if (workflow == "Tweak" && !GameDefinitelyAbsent()) throw new InvalidOperationException("Close Fortnite before applying Tweak changes.");
            options = PerformancePlanner.Copy(options);
            options.Intent ??= new UserIntent();
            options.Intent.Fps ??= new FpsLatencyOptions();
            if (options.OptionsVersion < 9 && !system.AllowLegacyFixtureActivation)
                throw new InvalidOperationException("Review a current FPS / latency plan before a new activation. Legacy sessions can still be restored.");
            OptimizationPlan? reviewedPlan = null;
            if (options.OptionsVersion >= 8)
            {
                if (string.IsNullOrWhiteSpace(options.ReviewedPlanFingerprint)) throw new InvalidOperationException("Analyze this PC, review the goal-based plan and choose Use this plan before turning ON.");
                reviewedPlan = await AnalyzePlanAsync(options);
                token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref restoreGeneration) != initialRestoreGeneration)
                    throw new OperationCanceledException("A stop request superseded the pending plan analysis.");
                bool Planned(string id) => reviewedPlan.Actions.Any(a => a.Id == id && a.Disposition == PlanDisposition.Recommended);
                if (options.HighPerformancePower != Planned("power") || options.TuneEthernet != Planned("ethernet")
                    || options.PrioritizeNetwork != Planned("qos"))
                    throw new InvalidOperationException("A plan-controlled power or network switch changed. Analyze this PC and review the plan again.");
                if (!string.Equals(reviewedPlan.InventoryFingerprint, options.ReviewedPlanFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("Hardware, power, game state or plan choices changed. Analyze this PC and review a fresh plan before turning ON.");
                options = PerformancePlanner.Apply(reviewedPlan, options);
            }
            ArmOperation(initialRestoreGeneration);
            TweakBoundary();
            watchedAttempts.Clear();
            deniedPriorities.Clear();
            int beforeCount = system.Snapshot().Count;
            options.ProtectedAppPaths = ConnectionProtection.NormalizePaths(options.ProtectedAppPaths);
            var eligibleApps = Scan(options.FortniteOnly, options.MaximumHeadroom, options.ProtectedAppPaths);
            var apps = eligibleApps.Where(a => options.SelectedApps.Contains(a.Id, StringComparer.OrdinalIgnoreCase) && !options.ExcludedApps.Contains(a.Id, StringComparer.OrdinalIgnoreCase)
                && !PerformancePlanner.ShouldPreserveApp(a, options)).ToList();
            TweakBoundary();
            using var owner = Process.GetCurrentProcess();
            State = new SessionState { Workflow = workflow, Options = options, Apps = apps.Select(a => new RestoreApp { App = a }).ToList(), Plan = reviewedPlan,
                CreatedByVersion = typeof(TourneyEngine).Assembly.GetName().Version?.ToString() ?? "1.5.0",
                Controller = new ProcessIdentity { Id = owner.Id, StartTicks = owner.StartTime.ToUniversalTime().Ticks, SessionId = owner.SessionId,
                    Path = Environment.ProcessPath ?? "", Name = owner.ProcessName + ".exe" } };
            ownedState = State;
            Save(); // From this point a crash is always recoverable by reopening the app.
            TweakBoundary();
            system.SetRecoveryStartup(true, workflow);
            if (workflow == "Tweak" || options.ApplyWindowsProfile)
            {
                progress?.Report(new() { Stage = "Creating restore point", Detail = "Windows must verify a real safety checkpoint before performance settings are changed.", Percent = 25 });
                State.RestorePoint = new() { Attempted = true }; Save();
                Feature("restore-point", "Windows restore point", FeatureState.Pending, "Creating the required safety checkpoint before any performance change.");
                TweakBoundary(); system.CreateRestorePoint(State.RestorePoint);
                if (!State.RestorePoint.Verified || State.RestorePoint.SequenceNumber <= 0)
                    throw new InvalidOperationException("Windows did not verify the required restore point. No performance changes were allowed.");
                Feature("restore-point", "Windows restore point", FeatureState.Applied, "Verified Windows restore point " + State.RestorePoint.SequenceNumber + ". Exact setting/file originals are also journaled.");
            }
            if (options.ApplyWindowsProfile)
            {
                progress?.Report(new() { Stage = "Windows profile", Detail = "Applying only supported differing values with exact-original recovery.", Percent = 45 });
                TweakBoundary();
                State.WindowsProfile = reviewedPlan?.WindowsProfile ?? throw new InvalidOperationException("Windows preference review is unavailable; scan again.");
                Save();
                system.ApplyWindowsProfile(State.WindowsProfile, Save, GameDefinitelyAbsent, token);
                Feature("windows-profile", "Windows and background overhead", FeatureState.Applied,
                    State.WindowsProfile.Values.Count + " differing preferences/policies written and read back. Browser restart/sign-out may be required; FPS benefit is unmeasured.");
            }
            if (options.Intent.Fps.ApplyGameProfile)
            {
                progress?.Report(new() { Stage = "Fortnite settings", Detail = "Revalidating supported saved settings before targeted writes.", Percent = 60 });
                TweakBoundary();
                if (!GameDefinitelyAbsent()) Feature("fortnite-settings", "Fortnite FPS / latency settings", FeatureState.Deferred, "Close Fortnite before applying file settings. Current game settings are unchanged.");
                else
                {
                    State.FortniteSettings = system.CaptureFortniteSettings(options.Intent.Fps, reviewedPlan?.NvidiaGpu == true, options.Intent.AcceptTearing);
                    Save();
                    if (State.FortniteSettings is { HasChanges: true } gameSettings)
                    {
                        if (reviewedPlan is not null && reviewedPlan.GameSettingsFingerprint != gameSettings.OriginalSHA256)
                            throw new InvalidOperationException("Fortnite settings changed after review; no game settings were written. Restore and review again.");
                        TweakBoundary();
                        system.ApplyFortniteSettings(gameSettings, Save);
                        Feature("fortnite-settings", "Fortnite FPS / latency settings", FeatureState.Applied, string.Join("; ", gameSettings.Changes) + ". File readback verified; loaded-game effect requires next launch.");
                    }
                    else Feature("fortnite-settings", "Fortnite FPS / latency settings", FeatureState.Skipped, "No supported existing game setting needs a change. Manual renderer/Reflex choices remain in the reviewed plan.");
                }
            }
            else Feature("fortnite-settings", "Fortnite FPS / latency settings", FeatureState.Skipped, "Current Fortnite settings preserved.");
            if (options.Intent.Fps.BackgroundAppPaths.Count > 0)
            {
                TweakBoundary();
                if (!GameDefinitelyAbsent()) Feature("background-priority", "Background CPU governor", FeatureState.Deferred, "Prepare approved background priorities before starting Fortnite.");
                else
                {
                    var closingPaths = apps.SelectMany(a => a.Processes.Select(p => p.Path).Append(a.LaunchPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    State.BackgroundPriorities = system.CaptureBackgroundPriorities(eligibleApps.Where(a => !apps.Contains(a)).ToList(), options.Intent.Fps.BackgroundAppPaths, closingPaths); Save();
                    TweakBoundary();
                    system.ApplyBackgroundPriorities(State.BackgroundPriorities, Save, Say);
                    bool adjusted = State.BackgroundPriorities.Any(item => item.Applied);
                    Feature("background-priority", "Background CPU governor", adjusted ? FeatureState.Applied : FeatureState.Skipped,
                        adjusted ? "Eligible approved background processes requested Below normal; original priorities journaled. No periodic retuning." : "No eligible approved background process required priority adjustment.");
                }
            }
            if (options.SilenceNotifications)
            {
                TweakBoundary();
                Feature("notifications", "Notifications", FeatureState.Pending, "Recording originals before suppression.");
                State.OriginalDoNotDisturb = GameClientRunning ? null : system.GetDnd();
                State.Notifications = system.CaptureNotifications();
                Save();
                TweakBoundary();
                if (State.OriginalDoNotDisturb is bool original)
                {
                    if (!original) system.SetDnd(true);
                    Say("Windows Do Not Disturb is ON (verified).");
                }
                else Say("Windows Do Not Disturb toggle could not be read; full DND is unverified.");
                TweakBoundary();
                system.SuppressNotifications();
                Feature("notifications", "Notifications", FeatureState.Applied, State.OriginalDoNotDisturb.HasValue
                    ? "Toast suppression and verified shell DND; exact originals saved."
                    : "Toast suppression applied; full shell DND unavailable or deferred while Fortnite runs.");
            }
            else Feature("notifications", "Notifications", FeatureState.Skipped, "Preserved by user choice.");
            bool deferPower = GameClientRunning && (options.Intent.MatchSafe || options.OptionsVersion >= 9);
            if (options.HighPerformancePower && (!system.AcPowerAvailable() || deferPower))
                Feature("power", "Power profile", deferPower ? FeatureState.Deferred : FeatureState.Skipped,
                    deferPower ? "Match-safe mode keeps the current power policy while Fortnite runs." : "Battery or unknown AC; current plan preserved.");
            if (options.HighPerformancePower && system.AcPowerAvailable() && !deferPower)
            {
                TweakBoundary();
                Feature("power", "Power profile", FeatureState.Pending, "Saving original plan and clone identity.");
                State.OriginalPowerScheme = system.GetPowerScheme();
                if (!Guid.TryParse(State.OriginalPowerScheme, out _)) throw new InvalidOperationException("Cannot back up the active power scheme.");
                State.PerformancePlanGuid = Guid.NewGuid().ToString("D");
                Save();
                var warnings = options.OptionsVersion >= 8
                    ? system.CreateAdaptivePlan(State.OriginalPowerScheme, State.PerformancePlanGuid, reviewedPlan!.EffectivePower)
                    : system.CreatePlan(State.OriginalPowerScheme, State.PerformancePlanGuid, options.FortniteOnly);
                foreach (string warning in warnings) Say(warning);
                if (options.OptionsVersion >= 9 && !GameDefinitelyAbsent()) throw new InvalidOperationException("Fortnite started while preparing power. Activation stopped; restore the saved session.");
                TweakBoundary();
                system.SetPowerScheme(State.PerformancePlanGuid);
                Feature("power", "Power profile", FeatureState.Applied,
                    (reviewedPlan?.EffectivePower.ToString() ?? "Legacy") + " AC copy active; original plan saved. " + warnings.Count + " unsupported/unverified requests. Benefit unmeasured.");
            }
            else if (!options.HighPerformancePower) Feature("power", "Power profile", FeatureState.Skipped, "Original/OEM plan preserved by the reviewed policy.");
            if (options.GameTuning)
            {
                TweakBoundary();
                State.Tuning = system.CaptureTuning();
                if (workflow == "Tweak")
                    State.Tuning.RemoveAll(value => value.Name is "AutoGameModeEnabled" or "AllowAutoGameMode"
                        ? !options.TweakWindowsSettings.Contains("game-mode")
                        : value.Name is "AppCaptureEnabled" or "GameDVR_Enabled" ? !options.TweakWindowsSettings.Contains("capture") : true);
                if (options.Intent.Activity != SessionActivity.Gaming)
                    State.Tuning.RemoveAll(value => value.Name is "AppCaptureEnabled" or "GameDVR_Enabled");
                Save();
                TweakBoundary();
                if (workflow == "Tweak") system.ApplyGameTuningSubset(State.Tuning);
                else system.ApplyGameTuning(options.Intent.Activity != SessionActivity.Gaming);
                Feature("game-mode", "Game Mode / capture", FeatureState.Applied, workflow == "Tweak"
                    ? "Selected fixes verified: " + string.Join(", ", options.TweakWindowsSettings) + ". Other values preserved."
                    : options.Intent.Activity == SessionActivity.Gaming
                    ? "Supported Game Mode requested and background capture disabled."
                    : "Supported Game Mode requested; recording preferences preserved.");
            }
            else Feature("game-mode", "Game Mode / capture", FeatureState.Skipped, "Preserved by user choice.");
            if (options.PauseOptionalServices && !GameClientRunning)
            {
                try
                {
                    State.Services = system.CaptureOptionalServices(options.MaximumHeadroom); Save();
                    if (!State.Services.Any(service => service.WasRunning || service.HoldDisabled)) Say("Optional services: none need pausing or holding.");
                    foreach (var service in State.Services.Where(service => service.WasRunning || service.HoldDisabled))
                    {
                        if (!GameDefinitelyAbsent()) { Feature("services", "Optional services", FeatureState.Deferred, "Fortnite started; remaining service preparation stopped."); break; }
                        if (restoreRequested) throw new OperationCanceledException("Turn OFF requested.");
                        service.StopAttempted = service.WasRunning;
                        service.ConfigurationAttempted = service.HoldDisabled;
                        Save();
                        try { system.StopOptionalService(service); Save(); Say(service.Name + ": optional service pause completed."); }
                        catch (Exception error) { Feature("services", "Optional services", FeatureState.Failed, service.Name + ": " + error.Message); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { Feature("services", "Optional services", FeatureState.Skipped, error.Message); }
                if (!State.Features.Any(f => f.Id == "services")) Feature("services", "Optional services", FeatureState.Applied, "Reviewed service preparation completed; no FPS gain inferred.");
            }
            else Feature("services", "Optional services", GameClientRunning && options.PauseOptionalServices ? FeatureState.Deferred : FeatureState.Skipped,
                GameClientRunning ? "Service preparation is deferred while the client runs." : "Not selected.");
            if (options.MaximumHeadroom && !GameClientRunning)
            {
                try
                {
                    State.ScheduledTasks = system.CaptureOptionalTasks(); Save();
                    foreach (var task in State.ScheduledTasks)
                    {
                        if (!GameDefinitelyAbsent()) { Feature("tasks", "Optional tasks", FeatureState.Deferred, "Fortnite started; remaining task preparation stopped."); break; }
                        if (restoreRequested) throw new OperationCanceledException("Turn OFF requested.");
                        task.DisableAttempted = true; Save();
                        try { system.DisableOptionalTask(task); Say(task.TaskName + ": optional task held until turn OFF."); }
                        catch (Exception error) { Feature("tasks", "Optional tasks", FeatureState.Failed, task.TaskName + ": " + error.Message); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { Feature("tasks", "Optional tasks", FeatureState.Skipped, error.Message); }
                if (!State.Features.Any(f => f.Id == "tasks")) Feature("tasks", "Optional tasks", FeatureState.Applied, "Reviewed maintenance holds completed.");
            }
            else Feature("tasks", "Optional tasks", GameClientRunning && options.MaximumHeadroom ? FeatureState.Deferred : FeatureState.Skipped,
                GameClientRunning ? "Task preparation is deferred while the client runs." : "Not selected.");
            if (options.TuneEthernet)
            {
                if (GameClientRunning) Feature("ethernet", "Ethernet experiment", FeatureState.Deferred, "Fortnite is running; adapter changes/reconnects are blocked.");
                else
                try
                {
                    State.EthernetAdapter = system.CaptureEthernetAdapter(); Save();
                    if (State.EthernetAdapter is { } adapter)
                    {
                        if (adapter.Properties.Count == 0) Feature("ethernet", "Ethernet experiment", FeatureState.Skipped, "Supported values already match; no changes or reconnect needed.");
                        else
                        {
                            if (restoreRequested) throw new OperationCanceledException("Turn OFF requested.");
                            adapter.ApplyAttempted = true; Save();
                            NetworkSafety.Apply(system, adapter, Save, Say);
                            Feature("ethernet", "Ethernet experiment", FeatureState.Applied, "Original values saved; setter/readback completed. Latency benefit is unmeasured.");
                        }
                    }
                    else Feature("ethernet", "Ethernet experiment", FeatureState.Skipped, "No supported unambiguous physical wired preferred route.");
                }
                catch (OperationCanceledException) { throw; }
                catch (NetworkTransitionException error)
                {
                    Feature("ethernet", "Ethernet experiment", error.RecoveryRequired ? FeatureState.Failed : FeatureState.Recovered, error.Message);
                    if (error.RecoveryRequired) throw;
                }
                catch (Exception error) { Feature("ethernet", "Ethernet experiment", FeatureState.Skipped, error.Message); }
            }
            else Feature("ethernet", "Ethernet experiment", FeatureState.Skipped, "Driver defaults preserved; no experimental reconnect selected.");
            if (options.PrioritizeNetwork)
            {
                if (GameClientRunning) Feature("qos", "Outbound DSCP request", FeatureState.Deferred, "Network preparation deferred while Fortnite runs.");
                else
                try
                {
                    State.NetworkPriority = system.PrepareEthernetPriority(); Save();
                    if (State.NetworkPriority is { } priority)
                    {
                        if (restoreRequested) throw new OperationCanceledException("Turn OFF requested.");
                        priority.ApplyAttempted = true; Save();
                        try { system.ApplyEthernetPriority(priority); Save(); }
                        catch (Exception applyError)
                        {
                            try { system.RestoreEthernetPriority(priority); priority.Restored = true; Save(); }
                            catch (Exception restoreError)
                            {
                                priority.Restored = false; Save();
                                throw new NetworkTransitionException("Outbound DSCP apply failed and rollback is unverified; recovery is required.", true, new AggregateException(applyError, restoreError));
                            }
                            throw new NetworkTransitionException("Outbound DSCP request failed; original policy state restored.", false, applyError);
                        }
                        Feature("qos", "Outbound DSCP request", FeatureState.Applied, "Per-app outbound marking requested; routers may ignore it. This is not bandwidth reservation or measured lower ping.");
                    }
                    else Feature("qos", "Outbound DSCP request", FeatureState.Skipped, "No supported unambiguous wired preferred route.");
                }
                catch (OperationCanceledException) { throw; }
                catch (NetworkTransitionException error)
                {
                    Feature("qos", "Outbound DSCP request", error.RecoveryRequired ? FeatureState.Failed : FeatureState.Recovered, error.Message);
                    if (error.RecoveryRequired) throw;
                }
                catch (Exception error) { Feature("qos", "Outbound DSCP request", FeatureState.Failed, error.Message); }
            }
            else Feature("qos", "Outbound DSCP request", FeatureState.Skipped, "Network routing/marking preserved by the reviewed policy.");
            foreach (var item in State.Apps)
            {
                if (restoreRequested) throw new OperationCanceledException("Restore requested; app shutdown stopped.");
                if (options.MaximumHeadroom) continue;
                await CloseApp(item, options.ForceClose);
            }
            if (options.MaximumHeadroom) await CloseBackgroundBatch(State.Apps, options.ForceClose);
            Feature("cleanup", "Reviewed app cleanup", State.Apps.Any(a => a.CloseAttempted) ? FeatureState.Applied : FeatureState.Skipped,
                State.Apps.Any(a => a.CloseAttempted) ? "Shutdown attempted only for reviewed eligible apps; see per-app log for skipped/denied items." : "No selected eligible live app required shutdown.");
            if (restoreRequested) throw new OperationCanceledException("Restore requested; app shutdown stopped.");
            await TuneFortnite();
            Feature("quiet-match", "Quiet match operation", options.Intent.Fps.QuietDuringGame ? FeatureState.Applied : FeatureState.Skipped,
                options.Intent.Fps.QuietDuringGame ? "Armed: returning-app closure and repeated priority rewrites pause while Fortnite is present. Recovery can finish after game exit."
                    : "The reviewed returning-app guard remains active during play.");
            if (options.CloseCodex)
            {
                TweakBoundary();
                State.Codex = system.CaptureCodex(options.ProtectedAppPaths); Save();
                TweakBoundary();
                await system.StopCodex(State.Codex, options.ForceClose, Save, Say);
                TweakBoundary();
                bool stopped = State.Codex.NativeStopAttempted || State.Codex.Launchers.Any(item => item.StopAttempted);
                Feature("codex", "Codex lifecycle", stopped ? FeatureState.Applied : FeatureState.Skipped,
                    stopped ? "Supported owners stopped through recorded lifecycle controls; skips remain in the activity log." : "No supported eligible owner required shutdown; kept/unsupported tools remain running.");
            }
            TweakBoundary();
            State.Phase = "Active";
            State.LastHeartbeatUtc = system.UtcNow;
            if (options.AutoRestoreOnGameExit)
            {
                State.SawFortnite = system.Games().Any(ProcessPolicy.IsFortnite);
                Say("Auto restore armed: waits for Fortnite to run, then restores settings 15 seconds after the client exits.");
            }
            Save();
            TweakBoundary();
            Say(workflow == "Tweak" ? "Tweak changes active. Selected supported settings were requested; applications and Native remain running. Restore changes returns recorded originals."
                : options.MaximumHeadroom ? "Maximum headroom ON. Reviewed background executables closed; optional tasks held. Windows, security, game dependencies and devices protected."
                : options.FortniteOnly ? "Fortnite-only profile ON. Selected supported apps closed; essential and unknown components protected." : "Tourney Mode ON. Protected services and unknown apps remain running.");
            Say($"Inspectable processes before/after: {beforeCount}/{system.Snapshot().Count}. Process count alone does not measure FPS or frame times.");
            Say(options.CloseCodex ? "Codex shutdown completed for supported launchers. OFF restarts recorded Codex components; see any skipped items above."
                : "Codex, Native and bridge connection processes stay running (Close Codex is OFF).");
            if (options.KeepAppsClosed) Say("App guard ON: selected app executables will be kept closed. Unchecked apps and protected connection tools remain available.");
        }
        catch (Exception e)
        {
            Say("Activation interrupted: " + e.Message + " Use Turn OFF / Restore.");
            if (ownedState is not null && ReferenceEquals(State, ownedState)) { State!.Phase = "Needs recovery"; Save(); }
            throw;
        }
        finally { if (entered) gate.Release(); Interlocked.Decrement(ref activationOperations); }
    }

    private async Task CloseApp(RestoreApp item, bool force)
    {
        if (ProcessPolicy.IsDiscordApp(item.App) || ConnectionProtection.IsApp(item.App)) { item.Restored = true; Save(); return; }
        var targets = item.App.Processes.Where(p => !ProcessPolicy.IsDiscord(p)).Where(system.Alive).Where(system.CanTouch).ToList();
        if (targets.Count == 0) { item.Restored = true; Save(); return; }
        item.CloseAttempted = true;
        item.Restored = false;
        Save(); // Before WM_CLOSE as well as before force termination.
        foreach (var p in targets)
        {
            if (restoreRequested) return;
            try { system.CloseWindow(p); }
            catch (Exception e) { Say(item.App.Name + ": graceful close: " + e.Message); }
        }
        for (int i = 0; i < 16 && !restoreRequested && targets.Any(system.Alive); i++) await system.Delay(500);
        if (force && !restoreRequested)
        {
            // Parent first: a Native supervisor must not recreate children after they are killed.
            foreach (var p in ParentFirst(targets))
            {
                if (restoreRequested) return;
                if (!system.Alive(p)) continue;
                try { if (system.CanTouch(p)) system.Terminate(p); }
                catch (Exception e) { Say(item.App.Name + ": kept PID " + p.Id + ": " + e.Message); }
            }
        }
        if (restoreRequested) return;
        await system.Delay(300);
        int remaining = targets.Count(system.Alive);
        Say(item.App.Name + $": closed {targets.Count - remaining}/{targets.Count} recorded processes" + (remaining > 0 ? "; some remained open." : "."));
    }
    private async Task CloseBackgroundBatch(List<RestoreApp> apps, bool force)
    {
        var pending = apps.Where(item => !ProcessPolicy.IsDiscordApp(item.App) && !ConnectionProtection.IsApp(item.App))
            .Select(item => (Item: item, Targets: item.App.Processes.Where(system.Alive).Where(system.CanTouch).ToList())).ToList();
        foreach (var (item, targets) in pending)
        {
            item.CloseAttempted = targets.Count > 0;
            item.Restored = targets.Count == 0;
        }
        Save();
        var allTargets = pending.SelectMany(entry => entry.Targets).DistinctBy(IdentityKey).ToList();
        foreach (var process in allTargets)
        {
            if (restoreRequested) return;
            try { system.CloseWindow(process); }
            catch (Exception error) { Say(process.Name + ": graceful close: " + error.Message); }
        }
        for (int attempt = 0; attempt < 16 && !restoreRequested && allTargets.Any(system.Alive); attempt++) await system.Delay(500);
        if (force && !restoreRequested)
            foreach (var process in ParentFirst(allTargets))
            {
                if (restoreRequested) return;
                try { if (system.Alive(process) && system.CanTouch(process)) system.Terminate(process); }
                catch (Exception error) { Say(process.Name + ": kept PID " + process.Id + ": " + error.Message); }
            }
        await system.Delay(300);
        foreach (var (item, targets) in pending)
            Say(item.App.Name + $": closed {targets.Count(process => !system.Alive(process))}/{targets.Count} recorded processes.");
    }

    private static IEnumerable<ProcessIdentity> ParentFirst(List<ProcessIdentity> targets)
    {
        var map = targets.ToDictionary(p => p.Id);
        int Depth(ProcessIdentity p) { int d = 0; var seen = new HashSet<int>(); while (map.TryGetValue(p.ParentId, out var parent) && seen.Add(parent.Id)) { d++; p = parent; } return d; }
        return targets.OrderBy(Depth);
    }

    private async Task TuneFortnite()
    {
        if (State is null) return;
        if (!State.Options.HighPriority) { Feature("priority", "Fortnite priority", FeatureState.Skipped, "Preserved by user choice."); return; }
        var games = system.Games().Where(ProcessPolicy.IsFortnite).ToList();
        int requested = State.Options.OptionsVersion >= 9 ? State.Options.Intent.Fps.GamePriority switch
        {
            FortnitePriority.Normal => (int)ProcessPriorityClass.Normal,
            FortnitePriority.AboveNormal => (int)ProcessPriorityClass.AboveNormal,
            _ => (int)ProcessPriorityClass.High
        } : (int)ProcessPriorityClass.High;
        if (games.Count == 0) Feature("priority", "Fortnite priority", FeatureState.Deferred, "Waiting for the Fortnite client; no other process is prioritized.");
        foreach (var p in games)
        {
            if (restoreRequested) return;
            string identity = p.Id + ":" + p.StartTicks;
            if (deniedPriorities.Contains(identity)) continue;
            var prior = State.Priorities.FirstOrDefault(b => b.Process.Id == p.Id && b.Process.StartTicks == p.StartTicks);
            try
            {
                if (prior is null)
                {
                    prior = new PriorityBackup { Process = p, Original = system.GetPriority(p), Requested = State.Options.OptionsVersion >= 9 ? requested : null };
                    State.Priorities.Add(prior);
                    Save();
                }
                if (system.GetPriority(p) != requested)
                { if (restoreRequested) return; system.SetPriority(p, requested); Say("Fortnite CPU priority set to " + (ProcessPriorityClass)requested + "."); }
                Feature("priority", "Fortnite priority", FeatureState.Applied, "Windows accepted " + (ProcessPriorityClass)requested + " for the current client; performance benefit is unmeasured.");
            }
            catch (Exception e)
            {
                // Anti-cheat can deny priority changes; never bypass it.
                if (e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }) deniedPriorities.Add(identity);
                Feature("priority", "Fortnite priority", deniedPriorities.Contains(identity) ? FeatureState.Denied : FeatureState.Failed, "Priority unchanged: " + e.Message);
            }
        }
        await Task.CompletedTask;
    }
    public async Task MaintainAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        bool autoRestore = false;
        try
        {
            if (State?.Phase != "Active" || restoreRequested) return;
            autoRestore = GameExitRestoreDue();
            if (!autoRestore)
            {
                bool quiet = State.Options.OptionsVersion >= 9 && State.Options.Intent.Fps.QuietDuringGame && !GameDefinitelyAbsent();
                if (!quiet || system.Games().Any(p => ProcessPolicy.IsFortnite(p) && !State.Priorities.Any(b => IdentityKey(b.Process) == IdentityKey(p))))
                    await TuneFortnite();
                if (!quiet && State.Options.KeepAppsClosed) await KeepSelectedAppsClosed();
            }
        }
        catch
        {
            if (State is not null) { State.Phase = "Needs recovery"; Save(); }
            throw;
        }
        finally { gate.Release(); }
        if (autoRestore)
        {
            Say("Fortnite exited; automatically restoring this session's settings.");
            await RestoreAsync();
        }
    }

    private bool GameExitRestoreDue()
    {
        if (State?.Options.AutoRestoreOnGameExit != true) return false;
        var presence = system.GetGamePresence();
        if (presence != GamePresence.Absent)
        {
            bool sawGame = State.SawFortnite || presence == GamePresence.Present;
            if (sawGame != State.SawFortnite || State.GameAbsentSinceUtc is not null)
            { State.SawFortnite = sawGame; State.GameAbsentSinceUtc = null; Save(); }
            return false;
        }
        if (!State.SawFortnite) return false;
        if (State.GameAbsentSinceUtc is null) { State.GameAbsentSinceUtc = system.UtcNow; Save(); return false; }
        return system.UtcNow - State.GameAbsentSinceUtc.Value >= TimeSpan.FromSeconds(15);
    }

    private static string IdentityKey(ProcessIdentity p) => p.Id + ":" + p.StartTicks + ":" + p.Path.ToLowerInvariant();

    internal static List<ProcessIdentity> WatchTargets(AppCandidate app, List<ProcessIdentity> live)
    {
        var recorded = app.Processes.Select(IdentityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = app.Processes.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A shared helper path is insufficient: it must descend from this app's
        // current root, or still be the exact original process from activation.
        var targets = live.Where(p => p.Path.Equals(app.LaunchPath, StringComparison.OrdinalIgnoreCase) || recorded.Contains(IdentityKey(p))).ToList();
        var ids = targets.Select(p => p.Id).ToHashSet();
        bool added;
        do
        {
            added = false;
            foreach (var p in live)
                if (!ids.Contains(p.Id) && ids.Contains(p.ParentId) && paths.Contains(p.Path)
                    && targets.Any(parent => parent.Id == p.ParentId && parent.StartTicks <= p.StartTicks))
                { targets.Add(p); ids.Add(p.Id); added = true; }
        } while (added);
        return targets;
    }

    private async Task KeepSelectedAppsClosed()
    {
        if (State is null || restoreRequested) return;
        var entries = State.Apps.Where(a => ProcessPolicy.IsMonitoredApp(a.App)).ToList();
        if (entries.Count == 0) return;
        var live = system.WatchedProcesses(entries.Select(a => a.App).ToList());
        var liveKeys = live.Select(IdentityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        watchedAttempts.RemoveWhere(key => !liveKeys.Contains(key));
        var pending = new List<(RestoreApp Item, List<ProcessIdentity> Targets)>();
        int remainingBudget = 8;
        foreach (var item in entries)
        {
            // Retain a recipe for each known helper path plus current identities;
            // dead respawn history is not useful for recovery or future matching.
            item.App.Processes = item.App.Processes.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group.Take(1).Concat(group.Where(p => liveKeys.Contains(IdentityKey(p)))))
                .DistinctBy(IdentityKey).ToList();
            if (remainingBudget == 0) break;
            var targets = WatchTargets(item.App, live).Where(p => !ProcessPolicy.IsDiscord(p))
                .Where(p => !watchedAttempts.Contains(IdentityKey(p))).Where(system.CanTouch).Take(remainingBudget).ToList();
            if (targets.Count == 0) continue;
            if (item.App.Processes.Count + targets.Count > 4096)
            { Say(item.App.Name + ": app guard paused after excessive restarts; use Restore."); continue; }
            foreach (var p in targets)
                if (!item.App.Processes.Any(old => IdentityKey(old) == IdentityKey(p))) item.App.Processes.Add(p);
            item.CloseAttempted = true;
            item.Restored = false;
            remainingBudget -= targets.Count;
            pending.Add((item, targets));
        }
        if (pending.Count == 0) return;
        Save(); // One durable batch of fresh identities before any WM_CLOSE.
        foreach (var (item, targets) in pending)
        {
            foreach (var p in targets)
            {
                watchedAttempts.Add(IdentityKey(p));
                if (restoreRequested) return;
                try { system.CloseWindow(p); } catch (Exception e) { Say(item.App.Name + ": app guard close: " + e.Message); }
            }
        }
        // A single shared short grace period avoids 8 seconds per app every tick.
        await system.Delay(500);
        foreach (var (item, targets) in pending)
        {
            if (restoreRequested) return;
            if (State.Options.ForceClose)
                foreach (var p in ParentFirst(targets))
                {
                    if (restoreRequested) return;
                    try { if (system.Alive(p) && system.CanTouch(p)) system.Terminate(p); }
                    catch (Exception e) { Say(item.App.Name + ": app guard kept PID " + p.Id + ": " + e.Message); }
                }
            Say(item.App.Name + $": app guard closed {targets.Count(p => !system.Alive(p))}/{targets.Count} returning processes.");
        }
    }

    public async Task RestoreAsync()
    {
        RequestRestore();
        await gate.WaitAsync();
        try
        {
            if (State is null) return;
            State.Phase = "Restoring";
            Save();
            Feature("quiet-match", "Quiet match operation", FeatureState.Recovered, "Mode and returning-app guard are stopped. Safe originals restore now; disruptive recovery can wait for game exit.");
            var errors = new List<string>();
            bool deferred = false;
            bool WaitForGameExit(string id, string name)
            {
                if (State.Options.OptionsVersion < 9 || GameDefinitelyAbsent()) return false;
                deferred = true;
                Feature(id, name, FeatureState.Deferred, "Fortnite appeared during recovery; remaining preparation/settings work waits for exit.");
                return true;
            }
            if (State.InternetCalibration is { MutationAttempted: true, Restored: false } internet && !WaitForGameExit("internet-result", "NIC originals"))
            {
                try
                {
                    var optimizer = new InternetOptimizer(system.CreateInternetTestSystem());
                    await optimizer.RestoreAsync(internet, SaveInternetState, NetworkOutsideGameGate, CancellationToken.None).ConfigureAwait(false);
                    Feature("internet-result", "NIC originals", FeatureState.Recovered, "Exact original adapter/RSS values verified.");
                }
                catch (Exception error) { errors.Add("Internet NIC recovery: " + error.Message); }
            }
            if (State.Strip is { HasChanges: true } strip && !WaitForGameExit("strip", "Optional Windows features"))
            {
                try { system.RepairStrip(strip, null, Save, Say, CancellationToken.None); Feature("strip", "Optional Windows features", FeatureState.Recovered, "Recorded feature groups restored."); }
                catch (Exception error) { errors.Add("Windows feature repair: " + error.Message); }
            }
            if (State.WindowsProfile is { } profile && !WaitForGameExit("windows-profile", "Windows preferences/policies"))
            {
                try { system.RestoreWindowsProfile(profile, Save, GameDefinitelyAbsent); Feature("windows-profile", "Windows preferences/policies", FeatureState.Recovered, "Recorded originals restored; external edits preserved."); }
                catch (Exception error) { errors.Add("Windows preference recovery: " + error.Message); }
            }
            if (State.FortniteSettings is { ApplyAttempted: true, Restored: false } settings)
            {
                if (!GameDefinitelyAbsent())
                { deferred = true; Feature("fortnite-settings", "Fortnite FPS / latency settings", FeatureState.Deferred, "Original game settings will restore after Fortnite fully exits."); }
                else try { system.RestoreFortniteSettings(settings, Save); Feature("fortnite-settings", "Fortnite FPS / latency settings", FeatureState.Recovered, "Original targeted game settings restored; unrelated changes preserved."); }
                catch (Exception error) { errors.Add("Fortnite settings: " + error.Message); }
            }
            if (State.BackgroundPriorities.Any(item => item.ApplyAttempted && !item.Restored))
            {
                if (!GameDefinitelyAbsent())
                { deferred = true; Feature("background-priority", "Background CPU governor", FeatureState.Deferred, "Original background priorities will restore after Fortnite exits."); }
                else try { system.RestoreBackgroundPriorities(State.BackgroundPriorities, Save, Say); Feature("background-priority", "Background CPU governor", FeatureState.Recovered, "Original background process priorities restored or recorded identities exited."); }
                catch (Exception error) { errors.Add("Background CPU priority: " + error.Message); }
            }
            if (State.NetworkPriority is { ApplyAttempted: true, Restored: false } network && !WaitForGameExit("qos", "Outbound DSCP request"))
            {
                try { system.RestoreEthernetPriority(network); network.Restored = true; Save(); }
                catch (Exception error) { errors.Add("Fortnite traffic priority: " + error.Message); }
            }
            if (State.EthernetAdapter is { ApplyAttempted: true, Restored: false } adapter)
            {
                if (GameClientRunning)
                {
                    deferred = true;
                    Feature("ethernet", "Ethernet experiment", FeatureState.Deferred, "Restoration can reconnect the adapter; waiting for Fortnite to exit. Original values remain journaled.");
                }
                else try { system.RestoreEthernetAdapter(adapter); adapter.Restored = true; Feature("ethernet", "Ethernet experiment", FeatureState.Recovered, "Recorded original adapter settings verified."); }
                catch (Exception error) { errors.Add("Ethernet driver settings: " + error.Message); }
            }
            foreach (var service in State.Services.Where(item => (item.StopAttempted || item.ConfigurationAttempted) && !item.Restored))
            {
                if (WaitForGameExit("services", "Optional services")) break;
                try { system.RestoreOptionalService(service); service.Restored = true; Save(); }
                catch (Exception error) { errors.Add(service.Name + " service: " + error.Message); }
            }
            foreach (var task in State.ScheduledTasks.Where(item => item.DisableAttempted && !item.Restored))
            {
                if (WaitForGameExit("tasks", "Optional tasks")) break;
                try { system.RestoreOptionalTask(task); task.Restored = true; Save(); }
                catch (Exception error) { errors.Add(task.TaskName + " task: " + error.Message); }
            }
            foreach (var p in State.Priorities.Where(p => !p.Restored))
            {
                try
                {
                    // A denied enable attempt may never change priority. Do not ask
                    // Windows to write a value that is already exactly restored.
                    if (system.Alive(p.Process))
                    {
                        int current = system.GetPriority(p.Process);
                        if (current != p.Original)
                        {
                            if (p.Requested is int requested && current != requested)
                                throw new InvalidOperationException("Fortnite priority changed outside this session; the external choice was preserved. Review it before retrying recovery.");
                            system.SetPriority(p.Process, p.Original);
                        }
                    }
                    p.Restored = true; Save();
                }
                catch (Exception e) { errors.Add("Fortnite priority: " + e.Message); }
            }
            if (!State.PowerRestored && State.OriginalPowerScheme.Length > 0 && !WaitForGameExit("power", "Power profile"))
            {
                try { system.SetPowerScheme(State.OriginalPowerScheme); State.PowerRestored = true; Save(); }
                catch (Exception e) { errors.Add("Power plan: " + e.Message); }
            }
            if (State.PowerRestored && State.PerformancePlanGuid.Length > 0)
            {
                try { system.DeletePlan(State.PerformancePlanGuid); State.PerformancePlanGuid = ""; Save(); }
                catch (Exception e) { errors.Add("Temporary power plan: " + e.Message); }
            }
            foreach (var n in State.Notifications.Where(n => !n.Restored))
            {
                try { system.RestoreNotification(n); n.Restored = true; Save(); }
                catch (Exception e) { errors.Add("Notification setting: " + e.Message); }
            }
            if (!State.DoNotDisturbRestored && State.OriginalDoNotDisturb is bool original)
            {
                if (GameClientRunning)
                {
                    deferred = true;
                    Feature("notifications", "Notifications", FeatureState.Deferred, "Toast originals restored; shell DND restoration waits for game exit to avoid opening a shell window.");
                }
                else try { system.SetDnd(original); State.DoNotDisturbRestored = true; Save(); }
                catch (Exception e) { errors.Add("Do Not Disturb: " + e.Message); }
            }
            foreach (var n in State.Tuning.Where(n => !n.Restored))
            {
                try { system.RestoreTuning(n); n.Restored = true; Save(); }
                catch (Exception e) { errors.Add("Game setting: " + e.Message); }
            }
            // Turning OFF restores Windows settings only. This also applies to
            // saved sessions from older versions: closed apps are never launched.
            foreach (var item in State.Apps)
            {
                item.Restored = true;
                item.Error = "";
            }
            Save();
            // Bring back providers before the desktop, after network/settings recovery.
            if (State.Codex is { } codex && errors.Count == 0 && !deferred && !WaitForGameExit("codex", "Codex lifecycle"))
            {
                try { await system.RestoreCodex(codex, Save, Say); }
                catch (Exception e) { errors.Add("Codex restart: " + e.Message); }
            }
            if (errors.Count > 0)
            {
                State.Phase = "Needs recovery"; Save();
                foreach (string e in errors) Say("Restore pending — " + e);
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            }
            if (deferred)
            {
                State.Phase = "Restore deferred"; Save();
                Say("Restore deferred until Fortnite exits. The recovery journal and startup recovery remain active.");
                return;
            }
            foreach (var feature in State.Features.Where(f => f.State is FeatureState.Applied or FeatureState.Pending or FeatureState.Failed or FeatureState.Deferred))
            { feature.State = FeatureState.Recovered; feature.Detail = feature.Id == "cleanup"
                ? "App shutdown and guard ended; other closed apps remain closed."
                : "Recorded original settings restored; recovery completed."; feature.UpdatedUtc = system.UtcNow; }
            State.Phase = "Recovered";
            AtomicWrite(Path.Combine(DataDirectory, "last-session.json"), JsonSerializer.Serialize(State, Json));
            system.SetRecoveryStartup(false);
            File.Delete(StatePath);
            State = null;
            Say("Tourney Mode OFF. Recorded settings and Codex components restored. Other closed apps stay closed; Discord stays running.");
        }
        finally { gate.Release(); }
    }

    internal static bool AppReturned(AppCandidate app, List<ProcessIdentity> snapshot)
    {
        // Require a root (not a surviving renderer/helper). Ignore PID reuse, only match expected app executable.
        var originalIds = app.Processes.Select(p => p.Id).ToHashSet();
        var roots = app.Processes.Where(p => !originalIds.Contains(p.ParentId)).ToList();
        if (roots.Count == 0) roots = app.Processes;
        if (app.Id == "spencer-native")
        {
            // Services: returning Native runtime root. MCP instances are host-managed, not replayable standalone.
            return snapshot.Any(p => p.Path.Equals(roots[0].Path, StringComparison.OrdinalIgnoreCase)
                && p.StartTicks > 0 && !app.Processes.Any(old => old.Id == p.Id && old.StartTicks == p.StartTicks));
        }
        return roots.Any(root => snapshot.Any(p => p.Name.Equals(root.Name, StringComparison.OrdinalIgnoreCase) && p.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase)
            && p.SessionId == root.SessionId
            && !app.Processes.Any(old => old.Id == p.Id && old.StartTicks == p.StartTicks && originalIds.Contains(old.ParentId))
            && !snapshot.Any(parent => parent.Id == p.ParentId && parent.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase))));
    }
}
