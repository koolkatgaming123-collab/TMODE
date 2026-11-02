using System.Text;
using System.Text.Json;

namespace TourneyMode;

public enum StartupUpdateOutcome { Continue, Deferred, RestartReady }

public sealed class StartupUpdateSafety
{
    public bool GameDefinitelyAbsent { get; init; }
    public bool SessionActive { get; init; }
    public bool RecoveryJournalPresent { get; init; }
    public bool Busy { get; init; }
    public bool IsSafe => GameDefinitelyAbsent && !SessionActive && !RecoveryJournalPresent && !Busy;
}

public sealed class StartupUpdateResult
{
    public StartupUpdateOutcome Outcome { get; init; }
    public bool ShouldExit => Outcome == StartupUpdateOutcome.RestartReady && LaunchResult?.Started == true;
    public string Message { get; init; } = "";
    public UpdateCheckResult? CheckResult { get; init; }
    public StagedUpdate? StagedUpdate { get; init; }
    public UpdateLaunchResult? LaunchResult { get; init; }
}

public sealed class StartupUpdateAttempt
{
    public int Schema { get; init; } = 1;
    public string Version { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public DateTime AttemptedUtc { get; init; } = DateTime.UtcNow;
}

public interface IStartupUpdateAttemptStore
{
    StartupUpdateAttempt? Read();
    void Record(StartupUpdateAttempt attempt);
}

public interface IStartupUpdateBackend
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
    Task<StagedUpdate> StageAsync(UpdateCandidate candidate, Func<bool> isBusy, CancellationToken cancellationToken = default);
    Task<UpdateLaunchResult> LaunchApplyHelperAsync(StagedUpdate update, Func<bool> isBusy, CancellationToken cancellationToken = default);
}

/// <summary>One startup attempt. Existing AppUpdater owns trust, bounded download and parent-bound helper verification.</summary>
public sealed class StartupUpdatePolicy
{
    private readonly IStartupUpdateBackend backend;
    private readonly Func<StartupUpdateSafety> safety;
    private readonly IStartupUpdateAttemptStore attempts;
    private int started;

    public StartupUpdatePolicy(AppUpdater updater, Func<StartupUpdateSafety> safety, IStartupUpdateAttemptStore? attempts = null)
        : this(new ExistingUpdater(updater), safety, attempts ?? new JsonStartupUpdateAttemptStore()) { }
    public StartupUpdatePolicy(IStartupUpdateBackend backend, Func<StartupUpdateSafety> safety, IStartupUpdateAttemptStore attempts)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.safety = safety ?? throw new ArgumentNullException(nameof(safety));
        this.attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
    }

    public async Task<StartupUpdateResult> RunAsync(CancellationToken token = default)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
            return new() { Outcome = StartupUpdateOutcome.Deferred, Message = "This startup update attempt already ran. The current app stays open; no helper was replayed." };
        UpdateCheckResult? check = null; StagedUpdate? staged = null; UpdateLaunchResult? launch = null;
        StartupUpdateResult Result(StartupUpdateOutcome outcome, string message) => new()
        { Outcome = outcome, Message = message, CheckResult = check, StagedUpdate = staged, LaunchResult = launch };
        bool Blocked() => !(safety() ?? throw new InvalidOperationException("Startup update safety state is unavailable.")).IsSafe;
        try
        {
            token.ThrowIfCancellationRequested();
            if (Blocked()) return Result(StartupUpdateOutcome.Deferred, "Automatic updating waits for Fortnite to close and all activity/recovery to finish.");
            var previous = attempts.Read();
            if (previous != null) ValidateAttempt(previous);
            check = await backend.CheckAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (check.Status == UpdateCheckStatus.Blocked)
                return Result(StartupUpdateOutcome.Deferred, check.Message);
            if (!check.IsAvailable)
                return Result(StartupUpdateOutcome.Continue, string.IsNullOrWhiteSpace(check.Message) ? "No verified newer release is available. The current app continues." : check.Message);
            var candidate = check.Candidate!;
            var target = new StartupUpdateAttempt { Version = AppUpdater.ParseVersion(candidate.Version).ToString(), Sha256 = candidate.Manifest.Sha256.ToUpperInvariant() };
            ValidateAttempt(target);
            if (previous != null && SameTarget(previous, target))
                return Result(StartupUpdateOutcome.Deferred, "Automatic installation of this exact signed version was already attempted. The current app continues; use Updates to retry manually.");
            if (Blocked()) return Result(StartupUpdateOutcome.Deferred, "Activity changed after the update check. Automatic download is deferred.");
            staged = await backend.StageAsync(candidate, Blocked, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!string.Equals(staged.Version, candidate.Version, StringComparison.Ordinal)) throw new InvalidDataException("The staged update version differs from the verified release.");
            if (Blocked()) return Result(StartupUpdateOutcome.Deferred, "The verified download is ready, but activity/recovery now blocks installation.");
            // A launch/rollback/reopen cycle must not automatically attempt the same target again.
            attempts.Record(target);
            var durable = attempts.Read();
            if (durable == null) throw new InvalidDataException("The automatic update attempt was not durably recorded.");
            ValidateAttempt(durable);
            if (!SameTarget(durable, target)) throw new InvalidDataException("Automatic update attempt readback did not match the verified target.");
            token.ThrowIfCancellationRequested();
            if (Blocked()) return Result(StartupUpdateOutcome.Deferred, "Activity changed before helper preparation. Installation is deferred; no automatic retry will be replayed.");
            launch = await backend.LaunchApplyHelperAsync(staged, Blocked, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!launch.Started) return Result(StartupUpdateOutcome.Continue, string.IsNullOrWhiteSpace(launch.Message) ? "Automatic update preparation failed. The current app continues; retry through Updates." : launch.Message);
            if (Blocked()) return Result(StartupUpdateOutcome.Deferred, "Activity changed after helper preparation. Keep the current app open; automatic closing was canceled.");
            return Result(StartupUpdateOutcome.RestartReady, "The signed update helper is ready. This inactive app can close so the verified release can reopen at the same path.");
        }
        catch (OperationCanceledException)
        { return Result(StartupUpdateOutcome.Continue, "Automatic updating was canceled. The current app continues."); }
        catch (Exception error)
        { return Result(StartupUpdateOutcome.Continue, "Automatic updating could not finish (" + error.GetType().Name + "). The current app continues; use Updates for a manual retry."); }
    }

    internal static bool SameTarget(StartupUpdateAttempt first, StartupUpdateAttempt second) =>
        AppUpdater.ParseVersion(first.Version) == AppUpdater.ParseVersion(second.Version) && first.Sha256.Equals(second.Sha256, StringComparison.OrdinalIgnoreCase);
    internal static void ValidateAttempt(StartupUpdateAttempt attempt)
    {
        if (attempt.Schema != 1 || attempt.Sha256 == null || attempt.Sha256.Length != 64 || !attempt.Sha256.All(Uri.IsHexDigit)
            || attempt.AttemptedUtc.Kind != DateTimeKind.Utc || attempt.AttemptedUtc.Year < 2020 || attempt.AttemptedUtc > DateTime.UtcNow.AddDays(1))
            throw new InvalidDataException("The automatic update attempt record is invalid. Preserve it and retry manually.");
        _ = AppUpdater.ParseVersion(attempt.Version);
    }
    private sealed class ExistingUpdater : IStartupUpdateBackend
    {
        private readonly AppUpdater updater;
        public ExistingUpdater(AppUpdater updater) => this.updater = updater ?? throw new ArgumentNullException(nameof(updater));
        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) => updater.CheckAsync(cancellationToken);
        public Task<StagedUpdate> StageAsync(UpdateCandidate candidate, Func<bool> isBusy, CancellationToken cancellationToken = default) => updater.StageAsync(candidate, isBusy, cancellationToken);
        public Task<UpdateLaunchResult> LaunchApplyHelperAsync(StagedUpdate update, Func<bool> isBusy, CancellationToken cancellationToken = default) => updater.LaunchApplyHelperAsync(update, isBusy, cancellationToken);
    }
}

/// <summary>Atomic loop-prevention record. Construction does no I/O; failed/corrupt records are never silently reset.</summary>
public sealed class JsonStartupUpdateAttemptStore : IStartupUpdateAttemptStore
{
    private readonly string path;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 4 };
    public JsonStartupUpdateAttemptStore(string? path = null) => this.path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TourneyMode", "Updates", "startup-attempt.json"));
    public StartupUpdateAttempt? Read()
    {
        AppUpdater.ValidateLocalPath(path, false);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 4096) throw new InvalidDataException("Automatic update attempt size is invalid.");
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false);
            return ParseRecord(reader.ReadToEnd());
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    internal static StartupUpdateAttempt ParseRecord(string content)
    {
        if (Utf8.GetByteCount(content) is <= 0 or > 4096) throw new InvalidDataException("Automatic update attempt size is invalid.");
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 4 });
        string[] required = ["schema", "version", "sha256", "attemptedUtc"];
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any(property => !required.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
            || names.Count != required.Length) throw new InvalidDataException("Automatic update attempt fields are invalid or duplicated.");
        var result = JsonSerializer.Deserialize<StartupUpdateAttempt>(content, Json) ?? throw new InvalidDataException("Automatic update attempt is empty.");
        StartupUpdatePolicy.ValidateAttempt(result); return result;
    }
    public void Record(StartupUpdateAttempt attempt)
    {
        StartupUpdatePolicy.ValidateAttempt(attempt);
        var old = Read();
        if (old != null && StartupUpdatePolicy.SameTarget(old, attempt)) throw new InvalidOperationException("This exact automatic update was already attempted.");
        string directory = Path.GetDirectoryName(path)!;
        AppUpdater.ValidateLocalPath(directory, true); Directory.CreateDirectory(directory); AppUpdater.ValidateLocalPath(path, false);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            AppUpdater.ValidateLocalPath(temporary, false);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { byte[] contents = JsonSerializer.SerializeToUtf8Bytes(attempt, Json); stream.Write(contents); stream.Flush(true); }
            File.Move(temporary, path, true);
            var readback = Read();
            if (readback == null || !StartupUpdatePolicy.SameTarget(readback, attempt)) throw new InvalidDataException("Automatic update attempt readback failed.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
