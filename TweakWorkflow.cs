using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Evidence-to-action policy. No model, network, commands or mutations.</summary>
public static class TweakPolicy
{
    public static AppOptions SafeScope(AppOptions source)
    {
        var options = PerformancePlanner.Copy(source);
        options.OptionsVersion = Math.Max(9, options.OptionsVersion);
        options.Intent ??= new(); options.Intent.Fps ??= new();
        options.SelectedApps = []; options.ExcludedApps = [];
        options.ForceClose = options.CloseCodex = options.FortniteOnly = options.MaximumHeadroom = false;
        options.KeepAppsClosed = options.PauseOptionalServices = false;
        options.TuneEthernet = options.ExperimentalWiredNetwork; options.PrioritizeNetwork = options.ExperimentalDscp;
        options.HighPriority = options.SilenceNotifications = false;
        options.Intent.MatchSafe = true; options.Intent.AllowNetworkExperiment = options.ExperimentalWiredNetwork || options.ExperimentalDscp;
        options.Intent.Fps.QuietDuringGame = true; options.Intent.Fps.BackgroundAppPaths = [];
        options.ReviewedPlanFingerprint = "";
        return options;
    }

    public static TweakAssessment Create(PcAnalysis report, AppOptions preferences, bool gameRunning, bool recoveryPending)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = SafeScope(preferences);
        var findings = new List<TweakFinding>();
        void Add(string id, string title, string evidence, string action, bool automated = false, bool attention = false)
            => findings.Add(new() { Id = id, Title = title, Evidence = evidence, ProposedAction = action, Automated = automated, Attention = attention });
        options.TweakWindowsSettings = [];
        if (report.GameMode == 0) options.TweakWindowsSettings.Add("game-mode");
        if (options.Intent.Activity == SessionActivity.Gaming && report.BackgroundCapture == 1) options.TweakWindowsSettings.Add("capture");
        options.GameTuning = options.TweakWindowsSettings.Count > 0;
        Add("windows-game", "Windows gaming settings", $"Game Mode: {Known(report.GameMode)}. Background capture: {Known(report.BackgroundCapture)}.",
            options.GameTuning ? string.Join("; ", options.TweakWindowsSettings.Select(id => id == "game-mode" ? "Enable Game Mode" : "Disable background capture")) + "; record exact originals of selected values only."
                : "Preserve these settings; no observed off/on value justifies an automatic correction.", options.GameTuning);

        bool iniKnown = report.GameSettingsFingerprint.Length == 64 && report.GameSettingsFingerprint.All(Uri.IsHexDigit);
        bool iniFix = options.Intent.Fps.ApplyGameProfile && iniKnown && report.GameSettingsChanges.Count > 0;
        options.Intent.Fps.ApplyGameProfile = iniFix;
        Add("game-profile", "Fortnite saved settings", iniKnown ? "An existing settings file was read. " + report.GameSettingsChanges.Count + " supported differences found."
            : "No editable supported Fortnite settings preview is available.", iniFix
            ? string.Join("; ", report.GameSettingsChanges) + ". Takes effect on next normal game launch."
            : "Preserve the saved file. No unknown renderer, Reflex, console variable or missing key is invented.", iniFix);
        foreach (string warning in report.GameSettingsWarnings) Add("profile-note", "Saved-settings note", warning, "Review this finding before play.", attention: true);
        options.ApplyWindowsProfile = options.ApplyWindowsProfile && report.WindowsProfile?.HasChanges == true;
        Add("windows-overhead", "Windows and background overhead", report.WindowsProfile is null ? "No supported Windows profile scan was requested or available."
                : report.WindowsProfile.Values.Count + " supported differing preferences/policies were found.",
            options.ApplyWindowsProfile ? string.Join("; ", report.WindowsProfile!.Values.Select(v => v.Title + ". " + v.Note))
                : "Preserve these settings. Existing managed policies, unsupported editions and matching values remain unchanged.", options.ApplyWindowsProfile);

        bool knownCpu = report.Hardware.Cores > 0 && !string.IsNullOrWhiteSpace(report.Hardware.Cpu)
            && !report.Hardware.Cpu.Equals("Unavailable", StringComparison.OrdinalIgnoreCase);
        bool powerAllowed = knownCpu && report.AcPower == true && Guid.TryParse(report.ActivePowerPlan, out _);
        bool requestedPower = options.Intent.Power == PowerPreference.Sustained
            || options.Intent.Power == PowerPreference.Maximum && options.Intent.AllowHigherHeat && report.Hardware.IsPortable == false && !report.Hardware.HasBattery;
        options.HighPerformancePower = powerAllowed && requestedPower;
        Add("power", "Power and sustained load", "Power source: " + (report.AcPower == true ? "AC" : report.AcPower == false ? "battery" : "unknown")
            + "; requested policy: " + options.Intent.Power + ". Effective clocks and temperatures were not measured.",
            options.HighPerformancePower ? "Apply the explicitly chosen AC copy; preserve its original and Windows/vendor scheduling for Undo."
                : "Keep the current/OEM power plan. Static inventory does not prove that a higher-power plan is faster.", options.HighPerformancePower);

        bool adapter = report.Hardware.Adapters.Count > 0;
        if (options.ExperimentalWiredNetwork || options.ExperimentalDscp)
            Add("experimental-network", "Experimental network requests", "Only explicitly enabled experiments are selected. A scan does not measure packet latency or validate the preferred route.",
                adapter ? (options.ExperimentalWiredNetwork ? "Try the supported wired NIC profile; this can reconnect Ethernet and increase CPU/DPC work. " : "")
                    + (options.ExperimentalDscp ? "Request outbound DSCP46; routers may ignore it. " : "") + "Runtime route/identity/readback checks may skip this hardware. Exact originals are saved."
                    : "No active adapter was detected; no experiment is selected.", adapter, true);
        if (options.ExperimentalBackgroundGovernor)
        {
            options.Intent.Fps.BackgroundAppPaths = report.CandidateCosts.Where(a => a.EligibleForGovernor && a.CpuPercent >= 1 && Path.IsPathFullyQualified(a.ExecutablePath))
                .Select(a => a.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            Add("experimental-governor", "Experimental background CPU competition", options.Intent.Fps.BackgroundAppPaths.Count + " recognized background app paths used at least 1% machine CPU in the short sample.",
                options.Intent.Fps.BackgroundAppPaths.Count > 0 ? "Request Below normal for: " + string.Join(", ", options.Intent.Fps.BackgroundAppPaths) + ". No app is closed; fresh foreground/keep/dependency/capture checks can skip an app."
                    : "No eligible measured CPU workload justifies priority reduction.", options.Intent.Fps.BackgroundAppPaths.Count > 0, true);
        }

        if (report.MemoryLoadPercent >= 85 || report.CommitLimitGiB > 0 && report.CommitUsedGiB / report.CommitLimitGiB >= .9)
            Add("memory", "Memory pressure needs attention", $"Physical load {report.MemoryLoadPercent?.ToString() ?? "unknown"}%; commit {report.CommitUsedGiB:0.0}/{report.CommitLimitGiB:0.0} GiB.",
                "Review nonessential apps in Apps. No cache purge, page-file resize or unsaved-work termination is performed.", attention: true);
        foreach (var app in report.CandidateCosts.Where(a => a.CpuPercent >= 2).Take(5))
            Add("app-" + app.Application, app.Application, $"Used {app.CpuPercent:0.##}% machine CPU during the short background sample.",
                "Review it in Apps before using Tourney Mode. This Tweak run leaves applications and Native running.", attention: true);
        foreach (var drive in report.Volumes.Where(d => d.FreePercent < 10))
            Add("storage-" + drive.Drive, "Storage headroom", $"{drive.Drive}: {drive.FreePercent:0.#}% / {drive.FreeGiB:0.#} GiB free.", "Free space through normal file management; no files are deleted automatically.", attention: true);
        Add("frame-budget", "Frame pacing and latency", "An idle PC scan cannot measure endgame frame times or input latency.",
            "Your explicit frame-cap/resolution/tearing choices are respected. Use optional diagnostics for representative frame evidence; no benchmark starts here.");
        if (gameRunning) Add("game-running", "Close Fortnite before applying", "The client is present, or its absence cannot be established.", "Scanning remains read-only; apply is blocked until absence is verified.", attention: true);
        if (recoveryPending) Add("recovery", "Finish the current session", "Existing optimization or recovery is active.", "Use Restore changes before starting another Tweak run.", attention: true);
        foreach (string error in report.Errors) Add("inventory-note", "Inventory limitation", error, "Unknown capability remains unknown; unsupported changes are not selected.", attention: true);

        var plan = PerformancePlanner.Build(report, options, gameRunning, recoveryPending);
        if (!recoveryPending) options = PerformancePlanner.Apply(plan, options);
        var assessment = new TweakAssessment { Analysis = report, Options = options, Plan = plan, Findings = findings,
            GameRunning = gameRunning, RecoveryPending = recoveryPending,
            CanApply = !gameRunning && !recoveryPending && findings.Any(f => f.Automated) };
        int count = findings.Count(f => f.Automated);
        assessment.Summary = gameRunning || recoveryPending ? "Scan complete. Applying is blocked until game/session safety checks pass."
            : count == 0 ? "No supported automatic correction is justified by this scan. Review the findings below."
            : $"{count} supported fix groups ready. Review the exact changes, then apply. Other findings remain advisory.";
        assessment.ApprovalFingerprint = Fingerprint(assessment);
        return assessment;
    }

    private static string Known(int? value) => value.HasValue ? value == 1 ? "on" : value == 0 ? "off" : "other (" + value + ")" : "unknown";
    public static string Fingerprint(TweakAssessment assessment) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    { assessment.CreatedUtc, assessment.Analysis, assessment.Options, assessment.Plan, assessment.Findings, assessment.GameRunning, assessment.RecoveryPending, assessment.CanApply }))));

    public static void ValidateApproval(TweakAssessment assessment, DateTime utcNow)
    {
        if (!assessment.CanApply || assessment.GameRunning || assessment.RecoveryPending) throw new InvalidOperationException("This scan is read-only or has no supported automatic fixes.");
        if (assessment.CreatedUtc > utcNow.AddMinutes(1) || utcNow - assessment.CreatedUtc > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("This scan expired. Start Tweak again before applying.");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(assessment.ApprovalFingerprint), Encoding.ASCII.GetBytes(Fingerprint(assessment))))
            throw new InvalidOperationException("The reviewed scan or choices changed. Start Tweak again.");
    }
}

public sealed class TweakWorkflow
{
    private readonly TourneyEngine engine;
    public TweakWorkflow(TourneyEngine engine) => this.engine = engine;
    public async Task<TweakAssessment> AnalyzeAsync(AppOptions options, CancellationToken token = default, IProgress<TweakProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        progress?.Report(new() { Stage = "Analyzing", Detail = "Reading hardware, Windows settings and existing Fortnite configuration.", Percent = 15 });
        var scope = TweakPolicy.SafeScope(options);
        var report = await engine.CaptureAnalysisAsync(scope).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        progress?.Report(new() { Stage = "Building solutions", Detail = "Matching observed differences to supported reversible fixes.", Percent = 80 });
        var assessment = TweakPolicy.Create(report, scope, engine.GameClientRunning, engine.IsActive);
        progress?.Report(new() { Stage = "Review ready", Detail = assessment.Summary, Percent = 100 });
        return assessment;
    }

    public async Task<TweakRunResult> ApplyAsync(TweakAssessment assessment, CancellationToken token = default, IProgress<TweakProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        TweakPolicy.ValidateApproval(assessment, DateTime.UtcNow);
        if (engine.IsActive || engine.GameClientRunning) throw new InvalidOperationException("Close Fortnite and finish existing recovery before applying tweaks.");
        progress?.Report(new() { Stage = "Revalidating", Detail = "Checking the reviewed configuration before writing; originals will be journaled first.", Percent = 10 });
        try
        {
            await Task.Run(() => engine.EnableTweakAsync(assessment.Options, token, progress), token).ConfigureAwait(false);
            var results = engine.State?.Features.Select(f => new FeatureResult { Id = f.Id, Name = f.Name, State = f.State, Detail = f.Detail, UpdatedUtc = f.UpdatedUtc }).ToList() ?? [];
            int applied = results.Count(f => f.Id is "game-mode" or "fortnite-settings" or "power" or "windows-profile" or "ethernet" or "qos" or "background-priority" && f.State == FeatureState.Applied);
            var result = new TweakRunResult { Success = engine.State?.Phase == "Active" && applied > 0, Results = results,
                NeedsRecovery = engine.State is not null && engine.State.Phase != "Active",
                Summary = applied > 0 ? $"{applied} supported fix groups applied. Saved Fortnite settings take effect on next launch; use Restore changes to undo. FPS and latency gains are unmeasured."
                    : "No requested fix group was applied. Review skipped/unavailable results; Restore changes closes this recorded session." };
            progress?.Report(new() { Stage = "Completed", Detail = result.Summary, Percent = 100 });
            return result;
        }
        catch
        {
            if (engine.IsActive) progress?.Report(new() { Stage = "Recovery available", Detail = "Applying stopped. Originals remain recorded; use Restore changes.", Percent = 100 });
            throw;
        }
    }
}
