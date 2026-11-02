using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Mock HTTPS and inert local file fixtures. No apply helper or real app is launched.</summary>
public static class AppUpdaterTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        using var signing = RSA.Create(2048);
        await test("signed update check is metadata-only and exposes an available candidate", async () =>
        {
            using var fixture = new Fixture(signing); using var updater = fixture.Create();
            var result = await updater.CheckAsync();
            Check(result.IsAvailable && result.Candidate?.Version == "1.8.0" && fixture.Requests.Count == 1,
                "A trusted newer release was not offered.");
            Check(!Directory.Exists(fixture.Settings.CacheDirectory), "Checking created a staging cache or downloaded an executable.");
        });
        await test("signed update staging verifies size/hash and uses an atomic owned artifact", async () =>
        {
            using var fixture = new Fixture(signing); using var updater = fixture.Create(); var progress = new List<UpdateProgress>(); updater.Progress += progress.Add;
            var candidate = (await updater.CheckAsync()).Candidate!;
            var staged = await updater.StageAsync(candidate, () => false);
            Check(File.Exists(staged.ExecutablePath) && File.ReadAllBytes(staged.ExecutablePath).SequenceEqual(fixture.Bytes)
                && File.Exists(staged.EnvelopePath) && !File.Exists(Path.Combine(staged.DirectoryPath, "download.part")), "Staging did not finish atomically.");
            Check(staged.Version == "1.8.0" && progress.Any(p => p.Stage == "Staged" && p.Percent == 100), "Final user progress was missing.");
            _ = updater.ValidateStage(staged);
        });
        await test("unsigned/tampered/wrong-key manifests never become update candidates", async () =>
        {
            foreach (string kind in new[] { "unsigned", "payload", "signature", "key" })
            {
                using var fixture = new Fixture(signing);
                using var other = RSA.Create(2048);
                fixture.EnvelopeOverride = kind switch
                {
                    "unsigned" => JsonSerializer.Serialize(fixture.Manifest, Json),
                    "payload" => Tamper(fixture.Envelope(), payload: true),
                    "signature" => Tamper(fixture.Envelope(), payload: false),
                    _ => null
                };
                if (kind == "key") fixture.Settings.PublicKeyPem = other.ExportSubjectPublicKeyInfoPem();
                using var updater = fixture.Create();
                Check((await updater.CheckAsync()).Status == UpdateCheckStatus.Failed, "An untrusted " + kind + " manifest was accepted.");
                Check(fixture.Requests.Count == 1 && !Directory.Exists(fixture.Settings.CacheDirectory), "Untrusted metadata reached staging.");
            }
        });
        await test("signed duplicate JSON fields, wrong product/channel/schema and non-x64 releases are rejected", async () =>
        {
            foreach (string kind in new[] { "duplicate", "schema", "product", "channel", "architecture", "version" })
            {
                using var fixture = new Fixture(signing);
                switch (kind)
                {
                    case "duplicate": fixture.EnvelopeOverride = fixture.Sign(JsonSerializer.Serialize(fixture.Manifest, Json).Replace("{", "{\"schema\":1,", StringComparison.Ordinal)); break;
                    case "schema": fixture.Manifest.Schema = 2; break;
                    case "product": fixture.Manifest.Product = "DifferentProduct"; break;
                    case "channel": fixture.Manifest.Channel = "beta"; break;
                    case "architecture": fixture.Manifest.Architecture = "win-arm64"; break;
                    case "version": fixture.Manifest.Version = "1.8.0-preview"; break;
                }
                using var updater = fixture.Create();
                Check((await updater.CheckAsync()).Status == UpdateCheckStatus.Failed, "Unsupported signed " + kind + " metadata was accepted.");
            }
        });
        await test("same/older versions and installed metadata prevent signed downgrades", async () =>
        {
            foreach (string version in new[] { "1.7.0", "1.7.0.0", "1.6.9" })
            {
                using var fixture = new Fixture(signing); fixture.Manifest.Version = version;
                using var updater = fixture.Create();
                Check((await updater.CheckAsync()).Status == UpdateCheckStatus.UpToDate, "An equal or older numeric release was offered.");
                await ExpectAsync<InvalidDataException>(() => updater.StageAsync(new(fixture.Manifest, fixture.Envelope()), () => false));
            }
            using var newerDisk = new Fixture(signing); File.WriteAllBytes(newerDisk.Settings.InstalledExecutablePath, [1, 2, 3]);
            newerDisk.InstalledVersion = new Version(1, 9, 0); newerDisk.Settings.CurrentVersion = new Version(1, 6, 0);
            using var newerUpdater = newerDisk.Create();
            Check((await newerUpdater.CheckAsync()).Status == UpdateCheckStatus.UpToDate, "Configured version allowed a downgrade over newer installed metadata.");
        });
        await test("unsupported OS and platform fail closed without a binary download", async () =>
        {
            using var oldOs = new Fixture(signing); oldOs.Manifest.MinOsVersion = "10.0.60000";
            using var updater = oldOs.Create(); Check((await updater.CheckAsync()).Status == UpdateCheckStatus.Unsupported, "Unsupported Windows minimum was ignored.");
            using var wrongPlatform = new Fixture(signing); wrongPlatform.Settings.PlatformSupported = () => false;
            using var platformUpdater = wrongPlatform.Create();
            Check((await platformUpdater.CheckAsync()).Status == UpdateCheckStatus.Unsupported && wrongPlatform.Requests.Count == 0, "A non-x64 platform fetched an update.");
        });
        await test("HTTPS redirects remain allowed origins and HTTP downgrade/untrusted hosts are never followed", async () =>
        {
            using var secure = new Fixture(signing);
            secure.CustomResponse = (request, _) => Task.FromResult(request.RequestUri == secure.Settings.ManifestUri
                ? Redirect("https://release-assets.githubusercontent.com/fixture/manifest") : JsonResponse(secure.Envelope()));
            using var secureUpdater = secure.Create(); Check((await secureUpdater.CheckAsync()).IsAvailable && secure.Requests.Count == 2, "An allowed HTTPS redirect failed.");
            foreach (string target in new[] { "http://github.com/unsafe", "https://untrusted.example/unsafe", "https://github.com:444/unsafe", "https://user:secret@github.com/unsafe" })
            {
                using var fixture = new Fixture(signing); fixture.CustomResponse = (_, _) => Task.FromResult(Redirect(target));
                using var updater = fixture.Create();
                Check((await updater.CheckAsync()).Status == UpdateCheckStatus.Failed && fixture.Requests.Count == 1, "An unsafe redirect was followed.");
            }
        });
        await test("manifest limits and excessive redirect chains are bounded", async () =>
        {
            using var oversized = new Fixture(signing); oversized.EnvelopeOverride = new string('x', 65537);
            using var updater = oversized.Create(); Check((await updater.CheckAsync()).Status == UpdateCheckStatus.Failed, "An oversized envelope was accepted.");
            using var loop = new Fixture(signing); loop.CustomResponse = (_, _) => Task.FromResult(Redirect("https://github.com/fixture/hop/" + loop.Requests.Count));
            using var loopUpdater = loop.Create();
            Check((await loopUpdater.CheckAsync()).Status == UpdateCheckStatus.Failed && loop.Requests.Count == 6, "Redirects were not bounded to six responses.");
            using var declared = new Fixture(signing); declared.Manifest.Size = 300L * 1024 * 1024 + 1;
            using var declaredUpdater = declared.Create(); Check((await declaredUpdater.CheckAsync()).Status == UpdateCheckStatus.Failed && declared.Requests.Count == 1, "An over-limit executable was accepted for staging.");
        });
        await test("binary tampering, truncation and excess bytes never become staged executables", async () =>
        {
            foreach (string kind in new[] { "hash", "short", "long", "header" })
            {
                using var fixture = new Fixture(signing);
                fixture.PayloadResponse = () => kind switch
                {
                    "hash" => BinaryResponse(fixture.Bytes.Select((b, i) => i == 1 ? (byte)(b ^ 1) : b).ToArray()),
                    "short" => new(HttpStatusCode.OK) { Content = new UnknownLengthContent(fixture.Bytes[..^1]) },
                    "long" => new(HttpStatusCode.OK) { Content = new UnknownLengthContent(fixture.Bytes.Concat(new byte[] { 1 }).ToArray()) },
                    _ => WithLength(BinaryResponse(fixture.Bytes), fixture.Bytes.Length + 1)
                };
                using var updater = fixture.Create(); var candidate = (await updater.CheckAsync()).Candidate!;
                await ExpectAsync<InvalidDataException>(() => updater.StageAsync(candidate, () => false));
                Check(!Directory.EnumerateFiles(fixture.Settings.CacheDirectory, "*.exe", SearchOption.AllDirectories).Any(), "A corrupt payload became an executable artifact.");
            }
        });
        await test("staged executable metadata must match its signed architecture/product/version", async () =>
        {
            foreach (string kind in new[] { "version", "architecture", "product" })
            {
                using var fixture = new Fixture(signing);
                if (kind == "version") fixture.StageVersion = new Version(1, 8, 1);
                if (kind == "architecture") fixture.StageX64 = false;
                if (kind == "product") fixture.StageProduct = "Other program";
                using var updater = fixture.Create(); var candidate = (await updater.CheckAsync()).Candidate!;
                await ExpectAsync<InvalidDataException>(() => updater.StageAsync(candidate, () => false));
            }
        });
        await test("candidate and staged-file tampering are rechecked before any helper preparation", async () =>
        {
            using var fixture = new Fixture(signing); using var updater = fixture.Create();
            var changed = (await updater.CheckAsync()).Candidate!; changed.Manifest.Notes = "changed after review";
            await ExpectAsync<InvalidDataException>(() => updater.StageAsync(changed, () => false));
            var good = (await updater.CheckAsync()).Candidate!; var staged = await updater.StageAsync(good, () => false);
            File.WriteAllBytes(staged.ExecutablePath, [0, 1]);
            Expect<InvalidDataException>(() => updater.ValidateStage(staged));
            Check(!Directory.EnumerateDirectories(fixture.Settings.CacheDirectory, "apply-*").Any(), "A tampered stage launched/prepared a helper.");
        });
        await test("active engine/game or pending recovery blocks checks and staging before network work", async () =>
        {
            foreach (string kind in new[] { "engine", "game", "journal" })
            {
                using var fixture = new Fixture(signing);
                if (kind == "engine") fixture.EngineActive = true;
                if (kind == "game") fixture.GameRunning = true;
                if (kind == "journal") File.WriteAllText(fixture.Settings.JournalPath, "fixture pending recovery");
                using var updater = fixture.Create();
                Check((await updater.CheckAsync()).Status == UpdateCheckStatus.Blocked && fixture.Requests.Count == 0, "Busy state fetched metadata.");
                await ExpectAsync<InvalidOperationException>(() => updater.StageAsync(new(fixture.Manifest, fixture.Envelope()), () => false));
                Check(fixture.Requests.Count == 0 && !Directory.Exists(fixture.Settings.CacheDirectory), "Busy state downloaded a binary.");
            }
        });
        await test("download rechecks a newly active session and removes only its incomplete stage", async () =>
        {
            using var fixture = new Fixture(signing);
            fixture.PayloadResponse = () => { fixture.EngineActive = true; return BinaryResponse(fixture.Bytes); };
            using var updater = fixture.Create(); var candidate = (await updater.CheckAsync()).Candidate!;
            await ExpectAsync<InvalidOperationException>(() => updater.StageAsync(candidate, () => false));
            Check(!Directory.EnumerateFiles(fixture.Settings.CacheDirectory, "*.exe", SearchOption.AllDirectories).Any(), "A newly active session completed an executable download.");
        });
        await test("offline check errors remain contextual and do not modify or replace an app", async () =>
        {
            using var fixture = new Fixture(signing); fixture.CustomResponse = (_, _) => throw new HttpRequestException("fixture offline");
            using var updater = fixture.Create(); var result = await updater.CheckAsync();
            Check(result.Status == UpdateCheckStatus.Failed && result.Message.Contains("HTTPS", StringComparison.Ordinal) && !Directory.Exists(fixture.Settings.CacheDirectory), "An offline response was not handled safely.");
            using var absent = new Fixture(signing); absent.CustomResponse = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            using var absentUpdater = absent.Create(); Check((await absentUpdater.CheckAsync()).Status == UpdateCheckStatus.UpToDate, "A not-yet-published signed feed became a fatal app error.");
        });
        await test("update checks have bounded deadlines and propagate explicit cancellation", async () =>
        {
            using var timeout = new Fixture(signing); timeout.Settings.CheckTimeout = TimeSpan.FromMilliseconds(40);
            timeout.CustomResponse = (_, _) => new TaskCompletionSource<HttpResponseMessage>().Task;
            using var updater = timeout.Create(); Check((await updater.CheckAsync()).Status == UpdateCheckStatus.Failed, "A stalled transport ignored the check deadline.");
            using var cancelled = new Fixture(signing); cancelled.CustomResponse = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return JsonResponse(""); };
            using var cancelledUpdater = cancelled.Create(); using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
            await ExpectAsync<OperationCanceledException>(() => cancelledUpdater.CheckAsync(cancellation.Token));
        });
        await test("unowned stage paths, network paths and alternate streams are rejected without apply", () =>
        {
            using var fixture = new Fixture(signing); using var updater = fixture.Create();
            var outside = new StagedUpdate { DirectoryPath = fixture.Root, ExecutablePath = Path.Combine(fixture.Root, "TourneyMode.download.exe"), EnvelopePath = Path.Combine(fixture.Root, "manifest.json"), Version = "1.8.0" };
            Expect<InvalidDataException>(() => updater.ValidateStage(outside));
            foreach (string path in new[] { @"\\server\share\TMODE.exe", "relative.exe", Path.Combine(fixture.Root, "TMODE.exe:payload"), Path.Combine(fixture.Root, "ambiguous.") })
                Expect<InvalidDataException>(() => AppUpdater.ValidateLocalPath(path, false));
            return Task.CompletedTask;
        });
        await test("failed update restarts only the pinned unchanged original after controller ownership is released", () =>
        {
            using var fixture = new Fixture(signing);
            File.WriteAllBytes(fixture.Settings.InstalledExecutablePath, fixture.Bytes);
            bool controllerHeld = false, launched = false; int safetyChecks = 0;
            bool restarted = AppUpdater.RestartUnchangedOriginal(OriginalRestartState(fixture), action =>
            {
                controllerHeld = true;
                try { action(); } finally { controllerHeld = false; }
            }, () => safetyChecks++, _ => new(new Version(1, 7, 0), "Tourney Mode", true), path =>
            {
                Check(!controllerHeld && path == fixture.Settings.InstalledExecutablePath, "Restart retained the controller mutex or changed the original path.");
                // The real file remains pinned through the inert launch callback.
                Expect<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
                launched = true;
            });
            Check(restarted && launched && safetyChecks == 2, "An unchanged original was not restarted with both safety checks.");
            File.WriteAllBytes(fixture.Settings.InstalledExecutablePath, fixture.Bytes); // The pin must be released afterward.
            return Task.CompletedTask;
        });
        await test("untrusted ticket, live parent or attempted replacement never enters original restart callbacks", () =>
        {
            using var fixture = new Fixture(signing);
            var valid = OriginalRestartState(fixture);
            foreach (var state in new[] { valid with { TicketTrusted = false }, valid with { ParentExited = false }, valid with { ReplacementAttempted = true } })
            {
                bool callbackReached = false;
                bool restarted = AppUpdater.RestartUnchangedOriginal(state, _ => callbackReached = true, () => callbackReached = true,
                    _ => { callbackReached = true; return new(new Version(1, 7, 0), "Tourney Mode", true); }, _ => callbackReached = true);
                Check(!restarted && !callbackReached, "An unauthorized, live-parent or uncertain-swap failure reached restart work.");
            }
            return Task.CompletedTask;
        });
        await test("original restart refuses changed hash, version, architecture, product and invalid path", () =>
        {
            foreach (string kind in new[] { "hash", "version", "architecture", "product", "path" })
            {
                using var fixture = new Fixture(signing);
                File.WriteAllBytes(fixture.Settings.InstalledExecutablePath, kind == "hash" ? [1, 2, 3] : fixture.Bytes);
                var state = OriginalRestartState(fixture);
                if (kind == "path") state = state with { InstalledExecutablePath = @"\\server\share\current.exe" };
                bool launched = false;
                Expect<InvalidDataException>(() => AppUpdater.RestartUnchangedOriginal(state, action => action(), () => { }, _ => new(
                    kind == "version" ? new Version(1, 7, 1) : new Version(1, 7, 0), kind == "product" ? "Other product" : "Tourney Mode", kind != "architecture"), _ => launched = true));
                Check(!launched, "Changed original " + kind + " reached a process launcher.");
                File.WriteAllBytes(fixture.Settings.InstalledExecutablePath, fixture.Bytes); // Validation failures must release any file pin.
            }
            return Task.CompletedTask;
        });
        await test("controller and early or late game/recovery guards prevent original restart", () =>
        {
            foreach (string blocker in new[] { "controller", "early-safety", "late-safety" })
            {
                using var fixture = new Fixture(signing); File.WriteAllBytes(fixture.Settings.InstalledExecutablePath, fixture.Bytes);
                bool launched = false; int safetyChecks = 0;
                Expect<InvalidOperationException>(() => AppUpdater.RestartUnchangedOriginal(OriginalRestartState(fixture), action =>
                {
                    if (blocker == "controller") throw new InvalidOperationException("fixture existing controller");
                    action();
                }, () =>
                {
                    safetyChecks++;
                    if (blocker == "early-safety" || blocker == "late-safety" && safetyChecks == 2)
                        throw new InvalidOperationException("fixture game or recovery became active");
                }, _ => new(new Version(1, 7, 0), "Tourney Mode", true), _ => launched = true));
                Check(!launched, "An active controller or game/recovery guard reached restart.");
                File.WriteAllBytes(fixture.Settings.InstalledExecutablePath, fixture.Bytes);
            }
            return Task.CompletedTask;
        });
        await test("all helper restarts suppress exactly one startup auto-update without a shell command", () =>
        {
            using var fixture = new Fixture(signing);
            var info = AppUpdater.CreateRestartStartInfo(fixture.Settings.InstalledExecutablePath);
            Check(info.FileName == fixture.Settings.InstalledExecutablePath && info.WorkingDirectory == fixture.Root
                && info.ArgumentList.SequenceEqual(new[] { "--skip-startup-update-once" }) && info.Arguments.Length == 0,
                "A helper restart can chain an immediate auto-update or interpreted a shell command.");
            return Task.CompletedTask;
        });
        await test("prepared update has no grant until synchronous owner authorization and commits at most once", () =>
        {
            using var fixture = new Fixture(signing); using var updater = fixture.Create();
            DateTime now = DateTime.UtcNow; int grants = 0, released = 0;
            using var prepared = updater.CreatePreparedApplyFixture(() => true, () => grants++, now, () => now, () => released++);
            Check(prepared.Started && grants == 0 && released == 0, "Preparation granted an update or prematurely released readiness.");
            Check(updater.AuthorizeParentExit(prepared) && grants == 1 && released == 1, "A valid same-owner final commit did not signal exactly once.");
            Check(!updater.AuthorizeParentExit(prepared) && grants == 1, "The same prepared helper was granted twice.");
            return Task.CompletedTask;
        });
        await test("another updater or a public ticket path cannot authorize a prepared helper", () =>
        {
            using var fixture = new Fixture(signing); using var owner = fixture.Create(); using var other = fixture.Create();
            DateTime now = DateTime.UtcNow; int grants = 0;
            using var prepared = owner.CreatePreparedApplyFixture(() => true, () => grants++, now, () => now);
            Check(!other.AuthorizeParentExit(prepared) && grants == 0, "A different updater granted a capability it did not create.");
            using var forged = new UpdateLaunchResult { Started = true, TicketPath = prepared.TicketPath, HelperProcessId = prepared.HelperProcessId };
            Check(!owner.AuthorizeParentExit(forged) && grants == 0, "A public result/ticket path acted as update authorization.");
            Check(owner.AuthorizeParentExit(prepared) && grants == 1, "A wrong-owner attempt consumed the real owner's capability.");
            return Task.CompletedTask;
        });
        await test("late cancellation before, during identity validation or at the final safety check never grants GO", () =>
        {
            foreach (string point in new[] { "before", "identity", "final-safety" })
            {
                using var fixture = new Fixture(signing); using var updater = fixture.Create(); using var cancellation = new CancellationTokenSource();
                DateTime now = DateTime.UtcNow; int grants = 0, released = 0, safety = 0;
                using var prepared = updater.CreatePreparedApplyFixture(() => { if (point == "identity") cancellation.Cancel(); return true; },
                    () => grants++, now, () => now, () => released++);
                if (point == "before") cancellation.Cancel();
                bool granted = updater.AuthorizeParentExit(prepared, () =>
                { if (++safety == 2 && point == "final-safety") cancellation.Cancel(); return false; }, cancellation.Token);
                Check(!granted && grants == 0 && released == 1 && !updater.AuthorizeParentExit(prepared), "Canceled handoff still granted or retained a reusable helper.");
            }
            return Task.CompletedTask;
        });
        await test("expired, revoked or disposed-owner helper preparations cannot authorize parent exit", () =>
        {
            foreach (string kind in new[] { "expired", "revoked", "owner-disposed", "clock-reversed" })
            {
                using var fixture = new Fixture(signing); using var updater = fixture.Create(); DateTime ready = DateTime.UtcNow; int grants = 0, released = 0;
                DateTime clock = kind == "expired" ? ready.AddSeconds(16) : kind == "clock-reversed" ? ready.AddSeconds(-1) : ready;
                using var prepared = updater.CreatePreparedApplyFixture(() => true, () => grants++, ready, () => clock, () => released++);
                if (kind == "revoked") prepared.Dispose();
                if (kind == "owner-disposed") updater.Dispose();
                Check(!updater.AuthorizeParentExit(prepared) && grants == 0 && released == 1, "An expired/revoked/disposed preparation granted an update.");
            }
            return Task.CompletedTask;
        });
        await test("game, engine, recovery journal and caller busy state are checked again at final authorization", () =>
        {
            foreach (string blocker in new[] { "game", "engine", "journal", "busy", "late-game" })
            {
                using var fixture = new Fixture(signing); using var updater = fixture.Create(); DateTime now = DateTime.UtcNow; int grants = 0;
                using var prepared = updater.CreatePreparedApplyFixture(() => { if (blocker == "late-game") fixture.GameRunning = true; return true; },
                    () => grants++, now, () => now);
                if (blocker == "game") fixture.GameRunning = true;
                if (blocker == "engine") fixture.EngineActive = true;
                if (blocker == "journal") File.WriteAllText(fixture.Settings.JournalPath, "fixture pending recovery");
                Check(!updater.AuthorizeParentExit(prepared, () => blocker == "busy") && grants == 0, "A newly unsafe app/game/journal state granted GO.");
            }
            return Task.CompletedTask;
        });
        await test("missing readiness or changed parent/helper identity refuses GO and releases handles once", () =>
        {
            foreach (bool throws in new[] { false, true })
            {
                using var fixture = new Fixture(signing); using var updater = fixture.Create(); DateTime now = DateTime.UtcNow; int grants = 0, released = 0;
                using var prepared = updater.CreatePreparedApplyFixture(() => throws ? throw new InvalidOperationException("fixture identity query unavailable") : false,
                    () => grants++, now, () => now, () => released++);
                Check(!updater.AuthorizeParentExit(prepared) && grants == 0 && released == 1, "Dead/changed/unverifiable helper readiness granted GO.");
                prepared.Dispose(); Check(released == 1, "A failed prepared helper released its handles twice.");
            }
            return Task.CompletedTask;
        });
        await test("owner disposal during readiness validation revokes its prepared helper without a grant", () =>
        {
            using var fixture = new Fixture(signing); using var updater = fixture.Create(); DateTime now = DateTime.UtcNow; int grants = 0;
            using var prepared = updater.CreatePreparedApplyFixture(() => { updater.Dispose(); return true; }, () => grants++, now, () => now);
            Check(!updater.AuthorizeParentExit(prepared) && grants == 0, "A disposed owner still authorized a pending helper.");
            return Task.CompletedTask;
        });
    }

    private static AppUpdater.OriginalRestartState OriginalRestartState(Fixture fixture) => new(true, true, false,
        fixture.Settings.InstalledExecutablePath, Convert.ToHexString(SHA256.HashData(fixture.Bytes)), "1.7.0");

    private static string Tamper(string envelope, bool payload)
    {
        using var doc = JsonDocument.Parse(envelope);
        byte[] body = Convert.FromBase64String(doc.RootElement.GetProperty("payloadBase64").GetString()!);
        byte[] signature = Convert.FromBase64String(doc.RootElement.GetProperty("signatureBase64").GetString()!);
        if (payload) body[0] ^= 1; else signature[0] ^= 1;
        return JsonSerializer.Serialize(new { payloadBase64 = Convert.ToBase64String(body), signatureBase64 = Convert.ToBase64String(signature) });
    }
    private static HttpResponseMessage Redirect(string location) { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(location); return response; }
    private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage BinaryResponse(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static HttpResponseMessage WithLength(HttpResponseMessage response, long length) { response.Content.Headers.ContentLength = length; return response; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Expect<T>(Action action) where T : Exception { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }
    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }

    private sealed class Fixture : IDisposable
    {
        private readonly RSA signing;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TMODE-updater-test-" + Guid.NewGuid().ToString("N"));
        public byte[] Bytes { get; } = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        public UpdateSettings Settings { get; }
        public UpdateManifest Manifest { get; }
        public List<Uri> Requests { get; } = [];
        public bool EngineActive, GameRunning;
        public Version InstalledVersion = new(1, 7, 0);
        public Version StageVersion = new(1, 8, 0);
        public bool StageX64 = true;
        public string StageProduct = "Tourney Mode";
        public string? EnvelopeOverride;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? CustomResponse;
        public Func<HttpResponseMessage>? PayloadResponse;
        public Fixture(RSA signing)
        {
            this.signing = signing; Directory.CreateDirectory(Root);
            Settings = new()
            {
                ManifestUri = new("https://github.com/fixture/tmode-update.json"), PublicKeyPem = signing.ExportSubjectPublicKeyInfoPem(),
                CurrentVersion = new Version(1, 7, 0), InstalledExecutablePath = Path.Combine(Root, "current.exe"),
                CacheDirectory = Path.Combine(Root, "Updates"), JournalPath = Path.Combine(Root, "session.json"),
                IsEngineActive = () => EngineActive, IsGameRunning = () => GameRunning,
                OsVersionProvider = () => new Version(10, 0, 26200), PlatformSupported = () => true
            };
            Manifest = new() { Schema = 1, Product = "TourneyMode", Channel = "stable", Version = "1.8.0", Architecture = "win-x64", ExeUrl = "https://github.com/fixture/TMODE.exe",
                Size = Bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Bytes)), MinOsVersion = "10.0.19041", Notes = "Fixture release only; never executed." };
        }
        public string Sign(string payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            return JsonSerializer.Serialize(new { payloadBase64 = Convert.ToBase64String(bytes), signatureBase64 = Convert.ToBase64String(signing.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) });
        }
        public string Envelope() => EnvelopeOverride ?? Sign(JsonSerializer.Serialize(Manifest, Json));
        public AppUpdater Create() => new(Settings, new Handler(async (request, token) =>
        {
            Requests.Add(request.RequestUri!);
            if (CustomResponse != null) return await CustomResponse(request, token);
            return request.RequestUri!.AbsolutePath.EndsWith("tmode-update.json", StringComparison.Ordinal)
                ? JsonResponse(Envelope()) : PayloadResponse?.Invoke() ?? BinaryResponse(Bytes);
        }), path => new(path.Equals(Settings.InstalledExecutablePath, StringComparison.OrdinalIgnoreCase) ? InstalledVersion : StageVersion, StageProduct, StageX64));
        public void Dispose() { /* Inert tiny fixtures are retained under their unique temp root; no recursive cleanup. */ }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();
    }
}
