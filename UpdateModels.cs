using System.Runtime.InteropServices;

namespace TourneyMode;

public enum UpdateCheckStatus { UpToDate, Available, Blocked, Unsupported, Failed }

public sealed class UpdateSettings
{
    public Uri ManifestUri { get; set; } = new("https://github.com/koolkatgaming123-collab/TMODE/releases/latest/download/tmode-update.json");
    public string PublicKeyPem { get; set; } = "";
    public string Product { get; set; } = "TourneyMode";
    public string Channel { get; set; } = "stable";
    public Version CurrentVersion { get; set; } = typeof(UpdateSettings).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    public string InstalledExecutablePath { get; set; } = Environment.ProcessPath ?? "";
    public string CacheDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TourneyMode", "Updates");
    public string JournalPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TourneyMode", "session.json");
    public Func<bool>? IsEngineActive { get; set; }
    public Func<bool>? IsGameRunning { get; set; }
    public Func<Version>? OsVersionProvider { get; set; }
    public Func<bool>? PlatformSupported { get; set; }
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan ParentExitTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public long MaximumDownloadBytes { get; set; } = 300L * 1024 * 1024;
    // Both the signed executable URL and every redirect hop must use one of these
    // HTTPS hosts. Signing does not silently enable arbitrary download origins.
    public string[] AllowedHosts { get; set; } = ["github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"];
}

public sealed class UpdateManifest
{
    public int Schema { get; set; }
    public string Product { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Version { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string ExeUrl { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string MinOsVersion { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class UpdateCandidate
{
    public UpdateManifest Manifest { get; }
    public string SignedEnvelopeJson { get; }
    public string Version => Manifest.Version;
    public string Notes => Manifest.Notes;
    internal UpdateCandidate(UpdateManifest manifest, string envelope) { Manifest = manifest; SignedEnvelopeJson = envelope; }
}

public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public string Message { get; init; } = "";
    public UpdateCandidate? Candidate { get; init; }
    public bool IsAvailable => Status == UpdateCheckStatus.Available && Candidate != null;
}

public sealed class StagedUpdate
{
    public string Version { get; init; } = "";
    public string DirectoryPath { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string EnvelopePath { get; init; } = "";
}

public sealed class UpdateProgress
{
    public string Stage { get; init; } = "";
    public string Message { get; init; } = "";
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public double? Percent => TotalBytes is > 0 ? Math.Clamp(100d * BytesReceived / TotalBytes.Value, 0, 100) : null;
}

public sealed class UpdateLaunchResult : IDisposable
{
    // Started means the helper is prepared. It cannot apply until its creating
    // AppUpdater synchronously authorizes this opaque result immediately at exit.
    public bool Started { get; init; }
    public string Message { get; init; } = "";
    public string TicketPath { get; init; } = "";
    public int? HelperProcessId { get; init; }
    internal AppUpdater.ParentExitAuthorization? Authorization { get; init; }
    public void Dispose() => Authorization?.Dispose();
}
