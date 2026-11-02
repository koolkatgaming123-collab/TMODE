using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TourneyMode;

/// <summary>
/// Signed, opt-in updater. Checks do not stage or restart. Staging and launch
/// refuse an active game/recovery session. A copied instance of this same EXE
/// performs a separately acknowledged, parent-bound replacement after exit.
/// </summary>
public sealed class AppUpdater : IDisposable
{
    private const int EnvelopeLimit = 64 * 1024;
    private const int PayloadLimit = 16 * 1024;
    private const long HardDownloadLimit = 300L * 1024 * 1024;
    private const string PayloadFile = "TourneyMode.download.exe";
    private const string EnvelopeFile = "manifest.json";
    private const string HelperFile = "TourneyMode.UpdateHelper.exe";
    private const string TicketFile = "apply.ticket";
    private static readonly byte[] TicketEntropy = Encoding.UTF8.GetBytes("TourneyMode.SignedUpdate.Ticket.v1");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 8 };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly UpdateSettings settings;
    private readonly HttpClient client;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly Func<string, ExecutableInfo> inspect;
    private readonly object authorizationOwner = new();
    private readonly object preparedSync = new();
    private readonly HashSet<ParentExitAuthorization> preparedAuthorizations = [];
    private int disposed;
    public event Action<UpdateProgress>? Progress;

    public AppUpdater(UpdateSettings settings, HttpMessageHandler? handler = null)
        : this(settings, handler, InspectExecutable) { }

    // The only test seam for PE metadata. The apply helper always uses the real
    // inspector again, regardless of a network fixture's injected stage reader.
    internal AppUpdater(UpdateSettings input, HttpMessageHandler? handler, Func<string, ExecutableInfo> inspector)
    {
        ArgumentNullException.ThrowIfNull(input);
        settings = new UpdateSettings
        {
            ManifestUri = input.ManifestUri, PublicKeyPem = input.PublicKeyPem, Product = input.Product, Channel = input.Channel,
            CurrentVersion = Normalize(input.CurrentVersion), InstalledExecutablePath = Path.GetFullPath(input.InstalledExecutablePath),
            CacheDirectory = Path.GetFullPath(input.CacheDirectory), JournalPath = Path.GetFullPath(input.JournalPath),
            IsEngineActive = input.IsEngineActive, IsGameRunning = input.IsGameRunning, OsVersionProvider = input.OsVersionProvider,
            PlatformSupported = input.PlatformSupported, CheckTimeout = input.CheckTimeout, DownloadTimeout = input.DownloadTimeout,
            ParentExitTimeout = input.ParentExitTimeout, MaximumDownloadBytes = Math.Min(input.MaximumDownloadBytes, HardDownloadLimit),
            AllowedHosts = input.AllowedHosts.ToArray()
        };
        inspect = inspector ?? throw new ArgumentNullException(nameof(inspector));
        if (settings.MaximumDownloadBytes <= 0 || settings.AllowedHosts.Length == 0) throw new ArgumentException("Invalid update download limits.");
        ValidateTimeout(settings.CheckTimeout, TimeSpan.FromMinutes(1));
        ValidateTimeout(settings.DownloadTimeout, TimeSpan.FromMinutes(10));
        ValidateTimeout(settings.ParentExitTimeout, TimeSpan.FromMinutes(3));
        ValidateHttps(settings.ManifestUri);
        ValidateLocalPath(settings.CacheDirectory, directory: true);
        ValidateLocalPath(settings.InstalledExecutablePath, directory: false);
        ValidateLocalPath(settings.JournalPath, directory: false);
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }, true)
        { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TourneyMode-Updater/" + settings.CurrentVersion.Major + "." + settings.CurrentVersion.Minor);
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? blocked = BlockedReason();
            if (blocked != null) return CheckResult(UpdateCheckStatus.Blocked, blocked);
            if (!PlatformSupported()) return CheckResult(UpdateCheckStatus.Unsupported, "Updates require Windows x64; this process is unsupported.");
            Report("Checking", "Checking the signed stable release manifest.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(settings.CheckTimeout);
            using var response = await GetHttpsAsync(settings.ManifestUri, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return CheckResult(UpdateCheckStatus.UpToDate, "No signed update release is currently published.");
            response.EnsureSuccessStatusCode();
            string envelope = Utf8.GetString(await ReadBoundedAsync(response.Content, EnvelopeLimit, deadline.Token).ConfigureAwait(false));
            var manifest = ValidateEnvelope(envelope);
            Version version = ParseVersion(manifest.Version);
            if (version <= CurrentFloor()) return CheckResult(UpdateCheckStatus.UpToDate, "This copy is current; no newer signed version is available.");
            if (ParseVersion(manifest.MinOsVersion) > CurrentOs())
                return CheckResult(UpdateCheckStatus.Unsupported, "The signed release requires a newer Windows version.");
            return new() { Status = UpdateCheckStatus.Available, Message = "Signed version " + manifest.Version + " is available. Download and restart are separate choices.", Candidate = new(manifest, envelope) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return CheckResult(UpdateCheckStatus.Failed, "The update check timed out; the current app is unchanged."); }
        catch (Exception error) when (error is not OperationCanceledException)
        { return CheckResult(UpdateCheckStatus.Failed, SafeFailure("Update check", error)); }
        finally { operations.Release(); }
    }

    public async Task<StagedUpdate> StageAsync(UpdateCandidate candidate, Func<bool> isBusy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(isBusy);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stageDirectory = null;
        try
        {
            RequireSafe(isBusy);
            var manifest = ValidateEnvelope(candidate.SignedEnvelopeJson);
            if (JsonSerializer.Serialize(manifest, Json) != JsonSerializer.Serialize(candidate.Manifest, Json))
                throw new InvalidDataException("The displayed update candidate differs from its signed payload.");
            RequireNewerCompatible(manifest, CurrentFloor());
            EnsureCache();
            stageDirectory = Path.Combine(settings.CacheDirectory, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageDirectory);
            ValidateOwnedDirectory(stageDirectory, "stage-");
            string temporary = Path.Combine(stageDirectory, "download.part");
            string completed = Path.Combine(stageDirectory, PayloadFile);
            WriteAtomic(Path.Combine(stageDirectory, EnvelopeFile), Utf8.GetBytes(candidate.SignedEnvelopeJson));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(settings.DownloadTimeout);
            using var response = await GetHttpsAsync(new Uri(manifest.ExeUrl), deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long advertised && advertised != manifest.Size)
                throw new InvalidDataException("The executable response size differs from its signed manifest.");
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException("Encoded executable downloads are not accepted.");
            await using (var source = await response.Content.ReadAsStreamAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[128 * 1024]; long received = 0; long nextProgress = 0;
                while (true)
                {
                    int count = await source.ReadAsync(buffer, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > manifest.Size || received > settings.MaximumDownloadBytes)
                        throw new InvalidDataException("The executable exceeded its signed size or download limit.");
                    digest.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    if (received >= nextProgress) { RequireSafe(isBusy); Report("Downloading", "Downloading verified release bytes.", received, manifest.Size); nextProgress = received + 1024 * 1024; }
                }
                await destination.FlushAsync(deadline.Token).ConfigureAwait(false); destination.Flush(true);
                if (received != manifest.Size || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(manifest.Sha256)))
                    throw new InvalidDataException("The executable size or SHA-256 did not match its signed manifest.");
            }
            RequireSafe(isBusy);
            VerifyExecutable(temporary, manifest, inspect);
            File.Move(temporary, completed, false);
            Report("Staged", "Version " + manifest.Version + " is staged. The running app has not been replaced.", manifest.Size, manifest.Size);
            return new() { Version = manifest.Version, DirectoryPath = stageDirectory, ExecutablePath = completed, EnvelopePath = Path.Combine(stageDirectory, EnvelopeFile) };
        }
        catch
        {
            if (stageDirectory != null) CleanupOwnedStage(stageDirectory);
            throw;
        }
        finally { operations.Release(); }
    }

    public async Task<UpdateLaunchResult> LaunchApplyHelperAsync(StagedUpdate update, Func<bool> isBusy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update); ArgumentNullException.ThrowIfNull(isBusy);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        EventWaitHandle? ready = null, go = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            RequireSafe(isBusy);
            var manifest = ValidateStage(update);
            RequireNewerCompatible(manifest, CurrentFloor());
            using var parent = Process.GetCurrentProcess();
            string actualPath = ProcessCatalog.GetPath(parent.Id);
            if (!SamePath(actualPath, settings.InstalledExecutablePath))
                throw new InvalidOperationException("Only the running Tourney Mode EXE can request its own replacement.");
            ValidateLocalPath(actualPath, false);
            string id = Guid.NewGuid().ToString("N");
            string helperDirectory = Path.Combine(settings.CacheDirectory, "apply-" + id);
            Directory.CreateDirectory(helperDirectory); ValidateOwnedDirectory(helperDirectory, "apply-");
            string helperPath = Path.Combine(helperDirectory, HelperFile);
            string originalHash = HashFile(actualPath);
            await CopyFileAsync(actualPath, helperPath, cancellationToken).ConfigureAwait(false);
            if (!FixedHashEquals(HashFile(helperPath), originalHash)) throw new InvalidDataException("The copied update helper differs from the running EXE.");
            var ticket = new ApplyTicket
            {
                Schema = 1, Id = id, CreatedUtc = DateTime.UtcNow, InstalledExecutablePath = actualPath,
                Parent = new() { Id = parent.Id, StartTicks = parent.StartTime.ToUniversalTime().Ticks, SessionId = parent.SessionId, Path = actualPath, Name = parent.ProcessName + ".exe" },
                OriginalSha256 = originalHash, OriginalVersion = Normalize(InspectExecutable(actualPath).Version).ToString(),
                HelperExecutablePath = helperPath, StagedDirectory = update.DirectoryPath,
                TargetVersion = manifest.Version
            };
            string ticketPath = Path.Combine(helperDirectory, TicketFile);
            WriteProtected(ticketPath, ticket);
            ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent(id));
            go = new EventWaitHandle(false, EventResetMode.ManualReset, GoEvent(id));
            using var process = Process.Start(new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = helperDirectory, ArgumentList = { "--apply-update", ticketPath }
            }) ?? throw new InvalidOperationException("The update helper could not start.");
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            Task acknowledged = WaitHandleAsync(ready, handshake.Token);
            Task exited = process.WaitForExitAsync(handshake.Token);
            Task first = await Task.WhenAny(acknowledged, exited).ConfigureAwait(false);
            if (first == exited) { await exited.ConfigureAwait(false); throw new InvalidOperationException("The update helper exited before accepting the bound parent identity."); }
            await acknowledged.ConfigureAwait(false);
            RequireSafe(isBusy);
            cancellationToken.ThrowIfCancellationRequested();
            var helperIdentity = new ProcessIdentity { Id = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks,
                SessionId = process.SessionId, Path = helperPath, Name = process.ProcessName + ".exe" };
            var readyHandle = ready!; var goHandle = go!;
            var authorization = new ParentExitAuthorization(authorizationOwner, DateTime.UtcNow, () => DateTime.UtcNow,
                () => readyHandle.WaitOne(0) && VerifyPreparedIdentities(ticket, helperIdentity),
                () => goHandle.Set(), () => { readyHandle.Dispose(); goHandle.Dispose(); });
            RegisterPrepared(authorization);
            ready = null; go = null; // The opaque result now owns both handles.
            Report("RestartReady", "Update helper is prepared. Final authorization is required immediately before this app exits.");
            return new() { Started = true, HelperProcessId = process.Id, TicketPath = ticketPath, Authorization = authorization,
                Message = "The helper is prepared but has no installation grant. Cancel or keep this app open to leave the current version unchanged." };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new() { Started = false, Message = SafeFailure("Update preparation", error) }; }
        finally { ready?.Dispose(); go?.Dispose(); operations.Release(); }
    }

    /// <summary>
    /// Commits a prepared helper once. The UI must call this synchronously just
    /// before its normal Close, with no await/message loop between true and Close.
    /// A public ticket path or a result from another updater cannot grant access.
    /// </summary>
    public bool AuthorizeParentExit(UpdateLaunchResult prepared, Func<bool>? isBusy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        bool authorized = prepared.Started && prepared.Authorization?.TryAuthorize(authorizationOwner,
            () => Volatile.Read(ref disposed) == 0, () => RequireSafe(isBusy), cancellationToken) == true;
        if (!authorized) Report("RestartDeferred", "Update exit authorization was canceled, expired or unsafe. The current app stays usable.");
        return authorized;
    }

    private bool VerifyPreparedIdentities(ApplyTicket ticket, ProcessIdentity helperIdentity)
    {
        using var parent = Process.GetCurrentProcess();
        if (!ProcessCatalog.MatchesPinned(parent, ticket.Parent)
            || !SamePath(ProcessCatalog.GetPath(parent.Id), ticket.InstalledExecutablePath)) return false;
        using var helper = Process.GetProcessById(helperIdentity.Id);
        if (helper.HasExited || !ProcessCatalog.MatchesPinned(helper, helperIdentity)) return false;
        ValidateLocalPath(ticket.InstalledExecutablePath, false); ValidateLocalPath(helperIdentity.Path, false);
        return FixedHashEquals(HashFile(ticket.InstalledExecutablePath), ticket.OriginalSha256)
            && FixedHashEquals(HashFile(helperIdentity.Path), ticket.OriginalSha256);
    }

    private void RegisterPrepared(ParentExitAuthorization authorization)
    {
        lock (preparedSync)
        {
            if (Volatile.Read(ref disposed) != 0) { authorization.Dispose(); throw new ObjectDisposedException(nameof(AppUpdater)); }
            preparedAuthorizations.Add(authorization);
            authorization.Released = () => { lock (preparedSync) preparedAuthorizations.Remove(authorization); };
        }
    }

    // The same one-shot coordinator is exercised with inert callbacks in fixtures.
    // Production callbacks own named events and revalidate pinned real processes.
    internal sealed class ParentExitAuthorization(object owner, DateTime preparedUtc, Func<DateTime> utcNow,
        Func<bool> readyAndIdentitiesValid, Action signalGo, Action release) : IDisposable
    {
        private readonly object sync = new();
        private bool consumed;
        internal Action? Released { get; set; }
        internal bool TryAuthorize(object requester, Func<bool> ownerAlive, Action requireSafe, CancellationToken token)
        {
            if (!ReferenceEquals(owner, requester)) return false; // A wrong owner cannot consume someone else's capability.
            lock (sync)
            {
                if (consumed) return false;
                try
                {
                    token.ThrowIfCancellationRequested(); RequireTimely();
                    if (consumed || !ownerAlive()) return false;
                    requireSafe();
                    if (!readyAndIdentitiesValid()) return false;
                    token.ThrowIfCancellationRequested(); RequireTimely();
                    if (consumed || !ownerAlive()) return false;
                    requireSafe();
                    token.ThrowIfCancellationRequested(); RequireTimely();
                    if (consumed || !ownerAlive()) return false;
                    signalGo(); // The only parent-side GO. Remaining cleanup is synchronous; no message loop/await.
                    return true;
                }
                catch { return false; }
                finally { Consume(); }
            }
        }
        private void RequireTimely()
        {
            DateTime now = utcNow();
            if (preparedUtc.Kind != DateTimeKind.Utc || now.Kind != DateTimeKind.Utc || now < preparedUtc || now - preparedUtc > TimeSpan.FromSeconds(15))
                throw new InvalidOperationException("The prepared update authorization expired.");
        }
        private void Consume()
        {
            if (consumed) return;
            consumed = true;
            try { release(); } catch { }
            try { Released?.Invoke(); } catch { }
        }
        public void Dispose() { lock (sync) Consume(); }
    }

    internal UpdateLaunchResult CreatePreparedApplyFixture(Func<bool> readyAndIdentitiesValid, Action signalGo,
        DateTime preparedUtc, Func<DateTime> utcNow, Action? release = null)
    {
        var authorization = new ParentExitAuthorization(authorizationOwner, preparedUtc, utcNow, readyAndIdentitiesValid, signalGo, release ?? (() => { }));
        RegisterPrepared(authorization);
        return new() { Started = true, Authorization = authorization, Message = "Inert prepared-helper authorization fixture." };
    }

    public async Task<int> RunApplyHelperAsync(string ticketPath, CancellationToken cancellationToken = default)
    {
        string? helperDirectory = null; ApplyTicket? ticket = null; string phase = "Validating"; bool replaced = false; bool ownedHelperDirectory = false;
        bool trustedTicket = false, parentExited = false, replacementAttempted = false;
        string? backup = null; string? pending = null;
        try
        {
            ticketPath = Path.GetFullPath(ticketPath);
            helperDirectory = Path.GetDirectoryName(ticketPath)!;
            ValidateOwnedDirectory(helperDirectory, "apply-");
            ownedHelperDirectory = true;
            if (!SamePath(ticketPath, Path.Combine(helperDirectory, TicketFile))) throw new InvalidDataException("The update ticket is not in its owned location.");
            ticket = ReadProtected<ApplyTicket>(ticketPath);
            if (ticket.Schema != 1 || !Regex.IsMatch(ticket.Id, "\\A[a-f0-9]{32}\\z") || Path.GetFileName(helperDirectory) != "apply-" + ticket.Id
                || ticket.CreatedUtc < DateTime.UtcNow.AddMinutes(-15) || ticket.CreatedUtc > DateTime.UtcNow.AddMinutes(2))
                throw new InvalidDataException("The update ticket is invalid or expired.");
            string ownPath = Environment.ProcessPath ?? "";
            if (!SamePath(ownPath, ticket.HelperExecutablePath) || !SamePath(ownPath, Path.Combine(helperDirectory, HelperFile)))
                throw new InvalidDataException("This EXE is not the owned, copied update helper.");
            ValidateLocalPath(ticket.InstalledExecutablePath, false);
            if (ticket.Parent.Id <= 0 || ticket.Parent.Id == Environment.ProcessId || !SamePath(ticket.Parent.Path, ticket.InstalledExecutablePath)
                || !FixedHashEquals(HashFile(ownPath), ticket.OriginalSha256) || !FixedHashEquals(HashFile(ticket.InstalledExecutablePath), ticket.OriginalSha256))
                throw new InvalidDataException("The parent/installed EXE no longer matches the copied update helper.");
            using var parent = Process.GetProcessById(ticket.Parent.Id);
            if (!ProcessCatalog.MatchesPinned(parent, ticket.Parent)) throw new InvalidDataException("The update parent PID, start identity or executable path changed.");
            trustedTicket = true;
            using var ready = EventWaitHandle.OpenExisting(ReadyEvent(ticket.Id));
            using var go = EventWaitHandle.OpenExisting(GoEvent(ticket.Id));
            ready.Set();
            using (var grant = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                grant.CancelAfter(TimeSpan.FromSeconds(20));
                await WaitHandleAsync(go, grant.Token).ConfigureAwait(false);
            }
            phase = "Waiting for parent exit"; WriteOutcome(helperDirectory, phase, "The original app is allowed to exit normally.");
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                wait.CancelAfter(settings.ParentExitTimeout);
                await parent.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            parentExited = true;
            // A copied helper contains the same pinned public key as its parent.
            // Revalidate all authenticated data and the payload after the wait.
            var staged = new StagedUpdate
            {
                DirectoryPath = ticket.StagedDirectory, ExecutablePath = Path.Combine(ticket.StagedDirectory, PayloadFile),
                EnvelopePath = Path.Combine(ticket.StagedDirectory, EnvelopeFile), Version = ticket.TargetVersion
            };
            var manifest = ValidateStage(staged, realInspector: true);
            Version floor = Max(Normalize(settings.CurrentVersion), Max(ParseVersion(ticket.OriginalVersion), Normalize(InspectExecutable(ticket.InstalledExecutablePath).Version)));
            RequireNewerCompatible(manifest, floor);
            using (var controller = new Mutex(false, @"Local\TourneyMode-" + Environment.UserName))
            {
                bool acquired; try { acquired = controller.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("Another Tourney Mode copy opened; replacement was not attempted.");
                try
                {
                    RequireSafe(null);
                    ValidateLocalPath(ticket.InstalledExecutablePath, false);
                    if (!FixedHashEquals(HashFile(ticket.InstalledExecutablePath), ticket.OriginalSha256)) throw new InvalidDataException("The installed EXE changed while the helper waited.");
                    string targetDirectory = Path.GetDirectoryName(ticket.InstalledExecutablePath)!;
                    pending = Path.Combine(targetDirectory, ".tmode-new-" + ticket.Id + ".tmp");
                    backup = Path.Combine(targetDirectory, ".tmode-backup-" + ticket.Id + ".bak");
                    ValidateLocalPath(pending, false); ValidateLocalPath(backup, false);
                    if (File.Exists(pending) || File.Exists(backup)) throw new InvalidOperationException("This update ticket has already been used or needs manual recovery.");
                    phase = "Preparing atomic replacement"; WriteOutcome(helperDirectory, phase, "The original EXE is still in place.");
                    // Mutex ownership is thread-affine. This helper is already
                    // off the UI process: do not await inside the held region.
                    CopyFileSynchronous(staged.ExecutablePath, pending, cancellationToken);
                    VerifyFile(pending, manifest, InspectExecutable);
                    RequireSafe(null); // Recheck the journal and game immediately before mutation.
                    ValidateLocalPath(ticket.InstalledExecutablePath, false);
                    if (!FixedHashEquals(HashFile(ticket.InstalledExecutablePath), ticket.OriginalSha256)) throw new InvalidDataException("The installed EXE changed before replacement.");
                    replacementAttempted = true; // A throwing swap has an uncertain outcome; never treat it as an untouched original.
                    File.Replace(pending, ticket.InstalledExecutablePath, backup, false); replaced = true; pending = null;
                    VerifyFile(ticket.InstalledExecutablePath, manifest, InspectExecutable);
                    phase = "Replacement verified"; WriteOutcome(helperDirectory, phase, "Version, size and SHA-256 match the signed release. Original backup retained.");
                }
                finally { controller.ReleaseMutex(); }
            }
            phase = "Starting updated app";
            using var started = StartApp(ticket.InstalledExecutablePath);
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            if (started.HasExited) throw new InvalidOperationException("The updated app exited before its startup could be observed.");
            WriteOutcome(helperDirectory, "Completed", "The signed update is installed at the same path and its process started. The original backup is retained.");
            return 0;
        }
        catch (Exception error)
        {
            bool rolledBack = false, restartedOriginal = false;
            if (replaced && ticket != null && backup != null)
            {
                try
                {
                    RequireSafe(null);
                    using var controller = new Mutex(false, @"Local\TourneyMode-" + Environment.UserName);
                    bool acquired; try { acquired = controller.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new InvalidOperationException("A Tourney Mode process is running; automatic rollback was deferred.");
                    try
                    {
                        ValidateLocalPath(backup, false);
                        if (!FixedHashEquals(HashFile(backup), ticket.OriginalSha256)) throw new InvalidDataException("The saved original EXE backup changed.");
                        string failed = Path.Combine(Path.GetDirectoryName(ticket.InstalledExecutablePath)!, ".tmode-failed-" + ticket.Id + ".bak");
                        ValidateLocalPath(failed, false);
                        if (File.Exists(failed)) throw new InvalidOperationException("A failed update backup already exists.");
                        File.Replace(backup, ticket.InstalledExecutablePath, failed, false);
                        if (!FixedHashEquals(HashFile(ticket.InstalledExecutablePath), ticket.OriginalSha256)) throw new InvalidDataException("Rollback hash verification failed.");
                        rolledBack = true;
                    }
                    finally { controller.ReleaseMutex(); }
                    using var original = StartApp(ticket.InstalledExecutablePath);
                }
                catch (Exception rollback) { error = new AggregateException(error, rollback); }
            }
            else if (ticket != null)
            {
                try
                {
                    restartedOriginal = RestartUnchangedOriginal(new(trustedTicket, parentExited, replacementAttempted,
                        ticket.InstalledExecutablePath, ticket.OriginalSha256, ticket.OriginalVersion), action =>
                    {
                        using var controller = new Mutex(false, @"Local\TourneyMode-" + Environment.UserName);
                        bool acquired; try { acquired = controller.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) throw new InvalidOperationException("A Tourney Mode process is running; original-app restart was deferred.");
                        try { action(); } finally { controller.ReleaseMutex(); }
                    }, () => RequireSafe(null), InspectExecutable, path => { using var original = StartApp(path); });
                }
                catch (Exception restart) { error = new AggregateException(error, restart); }
            }
            if (pending != null && !replacementAttempted) { try { ValidateLocalPath(pending, false); File.Delete(pending); } catch { } }
            if (ownedHelperDirectory && helperDirectory != null)
            {
                try { WriteOutcome(helperDirectory, rolledBack ? "Rolled back" : restartedOriginal ? "Original app restarted" : replacementAttempted ? "Needs recovery" : "Not applied", SafeFailure(phase, error)); } catch { }
            }
            return rolledBack ? 2 : 1;
        }
    }

    internal sealed record OriginalRestartState(bool TicketTrusted, bool ParentExited, bool ReplacementAttempted,
        string InstalledExecutablePath, string OriginalSha256, string OriginalVersion);

    // The callbacks permit inert restart-decision fixtures. Production supplies the
    // real controller mutex, safety gates, PE inspector and process launcher.
    internal static bool RestartUnchangedOriginal(OriginalRestartState state, Action<Action> withControllerOwnership,
        Action requireSafe, Func<string, ExecutableInfo> inspector, Action<string> startOriginal)
    {
        if (!state.TicketTrusted || !state.ParentExited || state.ReplacementAttempted) return false;
        FileStream? pinnedOriginal = null;
        try
        {
            withControllerOwnership(() =>
            {
                requireSafe();
                ValidateLocalPath(state.InstalledExecutablePath, false);
                // Deny writes/deletion until process creation returns, so the
                // verified original cannot be swapped between validation and launch.
                pinnedOriginal = new FileStream(state.InstalledExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!FixedHashEquals(Convert.ToHexString(SHA256.HashData(pinnedOriginal)), state.OriginalSha256))
                    throw new InvalidDataException("The original EXE changed; automatic restart was refused.");
                var original = inspector(state.InstalledExecutablePath);
                if (!original.WindowsX64 || original.Product is not ("Tourney Mode" or "TourneyMode")
                    || Normalize(original.Version) != ParseVersion(state.OriginalVersion))
                    throw new InvalidDataException("The original EXE metadata changed; automatic restart was refused.");
            });
            // The application must acquire its own controller mutex on startup.
            // Release our ownership before launching, then recheck game/recovery.
            requireSafe();
            startOriginal(state.InstalledExecutablePath);
            return true;
        }
        finally { pinnedOriginal?.Dispose(); }
    }

    internal UpdateManifest ValidateEnvelope(string envelope)
    {
        if (Utf8.GetByteCount(envelope) > EnvelopeLimit) throw new InvalidDataException("The signed update envelope is too large.");
        using var document = JsonDocument.Parse(envelope, new JsonDocumentOptions { MaxDepth = 4 });
        RequireObject(document.RootElement, ["payloadBase64", "signatureBase64"]);
        byte[] payload = Convert.FromBase64String(document.RootElement.GetProperty("payloadBase64").GetString() ?? "");
        byte[] signature = Convert.FromBase64String(document.RootElement.GetProperty("signatureBase64").GetString() ?? "");
        if (payload.Length is <= 0 or > PayloadLimit || signature.Length is < 256 or > 1024)
            throw new InvalidDataException("The signed update payload or signature length is invalid.");
        using var rsa = RSA.Create(); rsa.ImportFromPem(settings.PublicKeyPem);
        if (rsa.KeySize < 2048 || rsa.KeySize > 8192 || !rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("The update signature is not trusted.");
        string text = Utf8.GetString(payload);
        using var body = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
        RequireObject(body.RootElement, ["schema", "product", "channel", "version", "architecture", "exeUrl", "size", "sha256", "minOsVersion", "notes"]);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(text, Json) ?? throw new InvalidDataException("The update payload is empty.");
        if (manifest.Schema != 1 || manifest.Product != settings.Product || manifest.Channel != settings.Channel || manifest.Architecture != "win-x64")
            throw new InvalidDataException("The update schema, product, channel or architecture is unsupported.");
        _ = ParseVersion(manifest.Version); _ = ParseVersion(manifest.MinOsVersion);
        if (manifest.Size <= 0 || manifest.Size > settings.MaximumDownloadBytes || manifest.Size > HardDownloadLimit || manifest.Notes == null || manifest.Notes.Length > 4000
            || manifest.Sha256 == null || !Regex.IsMatch(manifest.Sha256, "\\A[0-9a-fA-F]{64}\\z"))
            throw new InvalidDataException("The signed executable size, hash or notes are invalid.");
        if (!Uri.TryCreate(manifest.ExeUrl, UriKind.Absolute, out var uri)) throw new InvalidDataException("The signed executable URL is invalid.");
        ValidateHttps(uri);
        return manifest;
    }

    internal UpdateManifest ValidateStage(StagedUpdate update, bool realInspector = false)
    {
        ValidateOwnedDirectory(update.DirectoryPath, "stage-");
        if (!SamePath(update.ExecutablePath, Path.Combine(update.DirectoryPath, PayloadFile)) || !SamePath(update.EnvelopePath, Path.Combine(update.DirectoryPath, EnvelopeFile)))
            throw new InvalidDataException("The staged update is not in its owned location.");
        ValidateLocalPath(update.ExecutablePath, false); ValidateLocalPath(update.EnvelopePath, false);
        var manifest = ValidateEnvelope(ReadSmallText(update.EnvelopePath, EnvelopeLimit));
        if (manifest.Version != update.Version) throw new InvalidDataException("The staged version differs from the signed update.");
        VerifyFile(update.ExecutablePath, manifest, realInspector ? InspectExecutable : inspect);
        return manifest;
    }

    private void RequireNewerCompatible(UpdateManifest manifest, Version floor)
    {
        if (!PlatformSupported()) throw new PlatformNotSupportedException("Updates require Windows x64.");
        if (ParseVersion(manifest.Version) <= Normalize(floor)) throw new InvalidDataException("The signed update is not newer than this installed copy; downgrades are refused.");
        if (ParseVersion(manifest.MinOsVersion) > CurrentOs()) throw new PlatformNotSupportedException("The signed update requires a newer Windows version.");
    }
    private Version CurrentFloor()
    {
        Version result = settings.CurrentVersion;
        if (File.Exists(settings.InstalledExecutablePath)) result = Max(result, Normalize(inspect(settings.InstalledExecutablePath).Version));
        return result;
    }
    private bool PlatformSupported() => settings.PlatformSupported?.Invoke()
        ?? (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64 && RuntimeInformation.ProcessArchitecture == Architecture.X64);
    private Version CurrentOs() => Normalize(settings.OsVersionProvider?.Invoke() ?? Environment.OSVersion.Version);
    private string? BlockedReason(Func<bool>? busy = null)
    {
        if (busy?.Invoke() == true || settings.IsEngineActive?.Invoke() == true || File.Exists(settings.JournalPath))
            return "Finish Tourney Mode and all pending recovery before downloading or applying an update.";
        bool game;
        if (settings.IsGameRunning != null) game = settings.IsGameRunning();
        else
        {
            var running = Process.GetProcessesByName("FortniteClient-Win64-Shipping");
            try { game = running.Length != 0; } finally { foreach (var process in running) process.Dispose(); }
        }
        return game ? "Updates are deferred while Fortnite is running. Finish the match and close the client first." : null;
    }
    private void RequireSafe(Func<bool>? busy) { string? reason = BlockedReason(busy); if (reason != null) throw new InvalidOperationException(reason); }

    private async Task<HttpResponseMessage> GetHttpsAsync(Uri uri, CancellationToken token)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int hop = 0; hop <= 5; hop++)
        {
            ValidateHttps(uri);
            if (!visited.Add(uri.AbsoluteUri)) throw new InvalidDataException("The update response contains a redirect loop.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).WaitAsync(token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is < 300 or >= 400) return response;
            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location == null || hop == 5) throw new InvalidDataException("The update response has an invalid or excessive redirect chain.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException("The update redirect limit was exceeded.");
    }
    private void ValidateHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || (!uri.IsDefaultPort && uri.Port != 443)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !settings.AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The update URL or redirect is not an allowed HTTPS origin.");
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("The update manifest exceeded its download limit.");
        await using var source = await content.ReadAsStreamAsync(token).WaitAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream(); byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await source.ReadAsync(buffer, token).AsTask().WaitAsync(token).ConfigureAwait(false);
            if (count == 0) break;
            if (result.Length + count > maximum) throw new InvalidDataException("The update manifest exceeded its download limit.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
    private static void RequireObject(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("An update JSON object was expected.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
                throw new InvalidDataException("Duplicate or unsupported update JSON fields were rejected.");
        if (allowed.Any(name => !names.Contains(name))) throw new InvalidDataException("A required update JSON field is missing.");
    }
    internal static Version ParseVersion(string? value)
    {
        if (value == null || !Regex.IsMatch(value, "\\A[0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5}(?:\\.[0-9]{1,5})?\\z") || !Version.TryParse(value, out var parsed)
            || new[] { parsed.Major, parsed.Minor, parsed.Build, Math.Max(0, parsed.Revision) }.Any(part => part > 65535))
            throw new InvalidDataException("A stable numeric update version was expected.");
        return Normalize(parsed);
    }
    private static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    private static Version Max(Version first, Version second) => first >= second ? first : second;
    private static bool FixedHashEquals(string first, string second) => first.Length == 64 && second.Length == 64
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second));
    private static string HashFile(string path)
    { using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(source)); }
    private static void VerifyFile(string path, UpdateManifest manifest, Func<string, ExecutableInfo> inspector)
    {
        if (new FileInfo(path).Length != manifest.Size || !FixedHashEquals(HashFile(path), manifest.Sha256)) throw new InvalidDataException("The staged executable changed after verification.");
        VerifyExecutable(path, manifest, inspector);
    }
    private static void VerifyExecutable(string path, UpdateManifest manifest, Func<string, ExecutableInfo> inspector)
    {
        var info = inspector(path);
        if (!info.WindowsX64 || Normalize(info.Version) != ParseVersion(manifest.Version) || info.Product is not ("Tourney Mode" or "TourneyMode"))
            throw new InvalidDataException("The executable architecture, product or version differs from the signed release.");
    }
    internal sealed record ExecutableInfo(Version Version, string Product, bool WindowsX64);
    private static ExecutableInfo InspectExecutable(string path)
    {
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(input, Utf8, false))
        {
            if (input.Length < 96 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("A Windows PE executable was expected.");
            input.Position = 0x3C; int offset = reader.ReadInt32();
            if (offset < 64 || offset > 1024 * 1024 || offset > input.Length - 26) throw new InvalidDataException("Invalid PE header.");
            input.Position = offset;
            if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664) throw new InvalidDataException("Only Windows x64 executables are accepted.");
            input.Position = offset + 24;
            if (reader.ReadUInt16() != 0x20B) throw new InvalidDataException("Only PE32+ executables are accepted.");
        }
        var version = FileVersionInfo.GetVersionInfo(path);
        string text = (version.ProductVersion ?? "").Split('+')[0];
        return new(ParseVersion(text), version.ProductName ?? "", true);
    }

    private void EnsureCache() { ValidateLocalPath(settings.CacheDirectory, true); Directory.CreateDirectory(settings.CacheDirectory); ValidateLocalPath(settings.CacheDirectory, true); }
    private void ValidateOwnedDirectory(string path, string prefix)
    {
        string full = Path.GetFullPath(path); ValidateLocalPath(full, true);
        if (!SamePath(Path.GetDirectoryName(full) ?? "", settings.CacheDirectory)
            || !Regex.IsMatch(Path.GetFileName(full), "\\A" + Regex.Escape(prefix) + "[a-f0-9]{32}\\z"))
            throw new InvalidDataException("The update source or ticket is outside its owned cache directory.");
    }
    internal static void ValidateLocalPath(string path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0) throw new InvalidDataException("Only local, fully qualified update paths are accepted.");
        if (path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.EndsWith(' ') || segment.EndsWith('.')))
            throw new InvalidDataException("Ambiguous trailing-dot/space update paths are rejected.");
        string full = Path.GetFullPath(path); string root = Path.GetPathRoot(full) ?? "";
        if (root.Length != 3 || root[1] != ':' || new DriveInfo(root).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new InvalidDataException("Network or unknown update drives are rejected.");
        string current = root;
        foreach (string segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.EndsWith(' ') || segment.EndsWith('.')) throw new InvalidDataException("Ambiguous trailing-dot/space update paths are rejected.");
            current = Path.Combine(current, segment);
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse-point update paths are rejected.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (directory && File.Exists(full)) throw new InvalidDataException("An update directory was expected.");
        if (!directory && Directory.Exists(full)) throw new InvalidDataException("An update file was expected.");
    }
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static async Task CopyFileAsync(string source, string destination, CancellationToken token)
    {
        ValidateLocalPath(source, false); ValidateLocalPath(destination, false);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
    }
    private static void CopyFileSynchronous(string source, string destination, CancellationToken token)
    {
        ValidateLocalPath(source, false); ValidateLocalPath(destination, false);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough);
        byte[] buffer = new byte[128 * 1024]; long copied = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested(); int count = input.Read(buffer);
            if (count == 0) break;
            copied += count;
            if (copied > HardDownloadLimit) throw new InvalidDataException("The local staged executable exceeded its size limit.");
            output.Write(buffer, 0, count);
        }
        output.Flush(true); token.ThrowIfCancellationRequested();
    }
    private static string ReadSmallText(string path, int limit)
    { if (new FileInfo(path).Length > limit) throw new InvalidDataException("The update metadata file is too large."); return Utf8.GetString(File.ReadAllBytes(path)); }
    private static void WriteAtomic(string path, byte[] contents)
    {
        ValidateLocalPath(path, false); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(contents); stream.Flush(true); }
        File.Move(temporary, path, false);
    }
    private void CleanupOwnedStage(string directory)
    {
        try
        {
            ValidateOwnedDirectory(directory, "stage-");
            foreach (string name in new[] { "download.part", PayloadFile, EnvelopeFile })
            { string path = Path.Combine(directory, name); ValidateLocalPath(path, false); if (File.Exists(path)) File.Delete(path); }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
        catch { /* Incomplete owned artifacts remain inert; never recursively delete an unverified path. */ }
    }
    private static void ValidateTimeout(TimeSpan value, TimeSpan maximum)
    { if (value <= TimeSpan.Zero || value > maximum) throw new ArgumentOutOfRangeException(nameof(value), "Invalid updater deadline."); }
    private void Report(string stage, string message, long bytes = 0, long? total = null)
    { try { Progress?.Invoke(new() { Stage = stage, Message = message, BytesReceived = bytes, TotalBytes = total }); } catch { } }
    private static UpdateCheckResult CheckResult(UpdateCheckStatus status, string message) => new() { Status = status, Message = message };
    private static string SafeFailure(string phase, Exception error) => phase + " failed: " + (error switch
    {
        HttpRequestException http => "HTTPS request was unavailable" + (http.StatusCode is { } status ? " (HTTP " + (int)status + ")" : "") + ".",
        OperationCanceledException => "the bounded operation was cancelled or timed out.",
        InvalidDataException or CryptographicException or InvalidOperationException or PlatformNotSupportedException => error.Message,
        _ => error.GetType().Name + ". The original app and recovery data were retained."
    });
    private static string ReadyEvent(string id) => @"Local\TourneyModeUpdateReady-" + id;
    private static string GoEvent(string id) => @"Local\TourneyModeUpdateGo-" + id;
    private static async Task WaitHandleAsync(WaitHandle handle, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => completion.TrySetResult(), null, Timeout.Infinite, true);
        using var cancelled = token.Register(() => completion.TrySetCanceled(token));
        try { await completion.Task.ConfigureAwait(false); } finally { registration.Unregister(null); }
    }
    internal static ProcessStartInfo CreateRestartStartInfo(string path) => new(path)
    {
        UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)!,
        ArgumentList = { "--skip-startup-update-once" }
    };
    private static Process StartApp(string path) => Process.Start(CreateRestartStartInfo(path))
        ?? throw new InvalidOperationException("The updated app could not start.");
    private static void WriteOutcome(string directory, string phase, string message)
    {
        string path = Path.Combine(directory, "result.json"); string temporary = path + ".tmp";
        ValidateLocalPath(path, false); ValidateLocalPath(temporary, false);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, Utf8)) { writer.Write(JsonSerializer.Serialize(new { phase, message, utc = DateTime.UtcNow }, Json)); writer.Flush(); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private sealed class ApplyTicket
    {
        public int Schema { get; set; }
        public string Id { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public ProcessIdentity Parent { get; set; } = new();
        public string InstalledExecutablePath { get; set; } = "";
        public string HelperExecutablePath { get; set; } = "";
        public string OriginalSha256 { get; set; } = "";
        public string OriginalVersion { get; set; } = "";
        public string StagedDirectory { get; set; } = "";
        public string TargetVersion { get; set; } = "";
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] Protect(byte[] data, bool decrypt)
    {
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var entropy = new Blob { Length = TicketEntropy.Length, Data = Marshal.AllocHGlobal(TicketEntropy.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length); Marshal.Copy(TicketEntropy, 0, entropy.Data, TicketEntropy.Length);
            bool ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, "Tourney Mode update ticket", ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("The update ticket is not owned by this Windows user or is damaged.");
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(entropy.Data); }
    }
    private static void WriteProtected<T>(string path, T value) => WriteAtomic(path, Protect(JsonSerializer.SerializeToUtf8Bytes(value, Json), false));
    private static T ReadProtected<T>(string path)
    {
        ValidateLocalPath(path, false);
        if (new FileInfo(path).Length is <= 0 or > EnvelopeLimit) throw new InvalidDataException("The update ticket size is invalid.");
        return JsonSerializer.Deserialize<T>(Protect(File.ReadAllBytes(path), true), Json) ?? throw new InvalidDataException("The update ticket is empty.");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ParentExitAuthorization[] outstanding;
        lock (preparedSync) { outstanding = preparedAuthorizations.ToArray(); preparedAuthorizations.Clear(); }
        foreach (var prepared in outstanding) prepared.Dispose();
        client.Dispose();
        // The semaphore owns no native handle here. Leave it alive for an
        // in-flight async operation's finally block when the UI is closing.
    }
}
