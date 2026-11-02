using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Pure policy: inventory and intent become a reviewable plan, never a live mutation.</summary>
public static class PerformancePlanner
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static AppOptions Copy(AppOptions value) => JsonSerializer.Deserialize<AppOptions>(JsonSerializer.Serialize(value)) ?? new();
    public static UserIntent CopyIntent(UserIntent value) => JsonSerializer.Deserialize<UserIntent>(JsonSerializer.Serialize(value)) ?? new();

    public static OptimizationPlan Build(PcAnalysis report, AppOptions options, bool gameRunning, bool recoveryPending)
    {
        var intent = CopyIntent(options.Intent ?? new());
        intent.Fps = FpsLatencyPolicy.Normalize(intent.Fps, intent.AcceptTearing);
        intent.TargetFps = Math.Clamp(intent.TargetFps, 0, 1000);
        var plan = new OptimizationPlan { Intent = intent, GameRunning = gameRunning, RecoveryPending = recoveryPending,
            InventoryFingerprint = "pending" };
        plan.WindowsProfile = options.ApplyWindowsProfile ? report.WindowsProfile : null;
        void Add(string id, string title, PlanDisposition state, string reason, string tradeoff = "") => plan.Actions.Add(new()
        { Id = id, Title = title, Disposition = state, Reason = reason, Tradeoff = tradeoff });
        bool hardwareKnown = report.Hardware.Cores > 0 && !string.IsNullOrWhiteSpace(report.Hardware.Cpu)
            && !report.Hardware.Cpu.Equals("Unavailable", StringComparison.OrdinalIgnoreCase);
        bool mobile = report.Hardware.HasBattery;
        bool discreteNvidia = report.Hardware.Gpus.Any(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        plan.NvidiaGpu = discreteNvidia;
        plan.GameSettingsFingerprint = report.GameSettingsFingerprint;
        bool multiCcdConcern = report.Hardware.Cpu.Contains("X3D", StringComparison.OrdinalIgnoreCase)
            || report.Hardware.Cpu.Contains("Threadripper", StringComparison.OrdinalIgnoreCase);
        bool experimentalPower = intent.Power == PowerPreference.Maximum;
        plan.EffectivePower = intent.Power switch
        {
            PowerPreference.Sustained when hardwareKnown && report.AcPower == true => PowerPreference.Sustained,
            PowerPreference.Maximum when hardwareKnown && report.AcPower == true && intent.AllowHigherHeat && !mobile && report.Hardware.IsPortable == false => PowerPreference.Maximum,
            _ => PowerPreference.Original
        };
        if (recoveryPending)
            plan.Warnings.Add("A saved session needs recovery. Analyze is read-only; applying a new plan is blocked until recovery finishes.");
        if (gameRunning)
            plan.Warnings.Add("Fortnite is running: disruptive preparation, shell focus changes and power-policy changes are deferred. Only reviewed cleanup and supported game priority can run.");
        if (!hardwareKnown) plan.Warnings.Add("Hardware inventory is incomplete. Power overrides are unavailable; missing facts are not treated as zero-capacity hardware.");
        if (experimentalPower && plan.EffectivePower != PowerPreference.Maximum)
            plan.Warnings.Add("Maximum power is not eligible: it requires known desktop hardware, AC power and explicit acceptance of higher heat.");
        if (intent.Activity != SessionActivity.Gaming)
            plan.Warnings.Add("Recording/streaming protection is active: known capture applications and background capture preferences are preserved.");

        Add("cleanup", "Reviewed background-app cleanup", options.SelectedApps.Count > 0 ? PlanDisposition.Recommended : PlanDisposition.Preserved,
            options.SelectedApps.Count > 0 ? "Only selected identities can close; Discord, kept apps, routing/audio/input, security and game dependencies remain protected."
                : "No applications are selected for shutdown; existing apps and Native stay running.",
            options.SelectedApps.Count > 0 ? "Save work: force-close can discard unsaved work." : "");
        Add("guard", "Bounded app respawn guard", options.KeepAppsClosed ? PlanDisposition.Recommended : PlanDisposition.Preserved,
            "Selected, recorded apps only. A filtered sweep and one durable batch precede any close; no generic runtime blocking.");
        Add("power", "Power policy: " + plan.EffectivePower,
            plan.EffectivePower == PowerPreference.Original ? PlanDisposition.Preserved : gameRunning ? PlanDisposition.Deferred : PlanDisposition.Recommended,
            plan.EffectivePower == PowerPreference.Original
                ? "Preserve the current/OEM plan. Static inventory cannot establish that maximum power improves endgame frame times."
                : plan.EffectivePower == PowerPreference.Sustained
                    ? "Clone the current AC plan; retain its minimum CPU state, energy preference and parking policy. Request normal maximum CPU availability without forcing all cores awake."
                    : "Experimental desktop AC profile: higher performance requests with original values preserved. Core topology and parking remain under Windows/vendor control.",
            "Power policy does not prove effective clocks or a measured performance gain.");
        Add("scheduling", "CPU topology and vendor scheduling", PlanDisposition.Preserved,
            multiCcdConcern ? "Preserve chipset/cache-domain and parking behavior on this processor. No forced core mask or global unparking."
                : "Preserve OS/vendor core selection, SMT and CPU topology. No affinity or firmware modifications.");
        Add("priority", "Fortnite priority", options.HighPriority ? PlanDisposition.Recommended : PlanDisposition.Preserved,
            intent.Fps.GamePriority + " is requested only through normal Windows access. Denied requests stay visible and are not repeatedly retried or bypassed.");
        Add("fortnite-settings", "Fortnite FPS / latency settings", !intent.Fps.ApplyGameProfile ? PlanDisposition.Preserved
                : gameRunning ? PlanDisposition.Deferred : report.GameSettingsFingerprint == "Unavailable" ? PlanDisposition.Unavailable : PlanDisposition.Recommended,
            !intent.Fps.ApplyGameProfile ? "Current game settings are preserved."
                : gameRunning ? "File writes wait until the client is fully closed; the profile takes effect on a later game launch."
                : string.Join("; ", report.GameSettingsChanges.DefaultIfEmpty("No eligible value needs changing.")),
            "Competitive visuals trade image quality for less rendering work. A cap trades throughput for headroom; improvement is not measured. Originals are journaled, and unrelated game edits are preserved on recovery.");
        foreach (var warning in report.GameSettingsWarnings) plan.Warnings.Add(warning);
        if (intent.Fps.Reflex != FortniteReflex.KeepCurrent && intent.Fps.ApplyGameProfile)
            plan.Warnings.Add("Reflex is an in-game action: select the supported Reflex option in Fortnite. Undocumented latency flags are preserved.");
        if (intent.Fps.Renderer != FortniteRenderer.KeepCurrent && intent.Fps.ApplyGameProfile)
            plan.Warnings.Add("Renderer choice requires an in-game change and shader warmup; unverified renderer mappings are not written automatically.");
        Add("background-priority", "Approved background CPU governor", intent.Fps.BackgroundAppPaths.Count == 0 ? PlanDisposition.Preserved
                : gameRunning ? PlanDisposition.Deferred : PlanDisposition.Recommended,
            intent.Fps.BackgroundAppPaths.Count == 0 ? "No remaining background app was selected for priority reduction."
                : "Below normal requested only for these explicit executable paths: " + string.Join(", ", intent.Fps.BackgroundAppPaths)
                    + ". Foreground, kept, capture, routing, device and protected processes are excluded; original priorities are saved.");
        Add("quiet-match", "Quiet match operation", intent.Fps.QuietDuringGame ? PlanDisposition.Recommended : PlanDisposition.Preserved,
            intent.Fps.QuietDuringGame ? "While Fortnite runs, stop returning-app closures and recurring priority rewrites. Keep lightweight exit detection and recovery control."
                : "The reviewed returning-app guard remains enabled; it may do process and journal work during play.");
        Add("game-mode", "Game Mode and capture preferences", options.GameTuning ? PlanDisposition.Recommended : PlanDisposition.Preserved,
            options.TweakWindowsSettings.Count > 0 ? string.Join("; ", options.TweakWindowsSettings.Select(id => id == "game-mode" ? "Enable supported Game Mode" : "Disable background capture")) + "; preserve other Windows values and save exact selected originals."
                : intent.Activity == SessionActivity.Gaming ? "Enable supported Game Mode and disable background capture; save exact original values."
                : "Enable supported Game Mode while preserving recording/capture preferences.");
        Add("notifications", "Notification suppression", options.SilenceNotifications ? PlanDisposition.Recommended : PlanDisposition.Preserved,
            gameRunning ? "Toast suppression only while the client is running; no shell windows or foreground changes."
                : "Use verified DND where available, with toast suppression and explicit policy/permission limitations otherwise.");
        Add("services", "Optional services and tasks", options.PauseOptionalServices || options.MaximumHeadroom
                ? gameRunning ? PlanDisposition.Deferred : PlanDisposition.Recommended : PlanDisposition.Preserved,
            "Pre-match preparation only; preserve dependencies and required device features. Stopping an idle service does not establish an FPS gain.");
        bool networkEligible = intent.AllowNetworkExperiment && !gameRunning && report.Hardware.Adapters.Any()
            && (intent.Goal == OptimizationGoal.LowestLatency || options.ExperimentalWiredNetwork || options.ExperimentalDscp);
        Add("ethernet", "Ethernet driver experiment", !intent.AllowNetworkExperiment ? PlanDisposition.Preserved
                : gameRunning ? PlanDisposition.Deferred : networkEligible ? PlanDisposition.Recommended : PlanDisposition.Unavailable,
            !intent.AllowNetworkExperiment ? "Preserve driver defaults. Interrupt moderation trades packet batching delay for CPU/DPC work."
                : gameRunning ? "Adapter reconnects are blocked while Fortnite runs; apply/restore this experiment outside play."
                : networkEligible ? "Optional experiment on the actual wired preferred route; exact identity/readback and bounded rollback are mandatory."
                : "Requires Lowest latency goal and a detected adapter. Runtime routing and driver capabilities still need verification.",
            "Can reconnect Ethernet; DSCP/router behavior cannot guarantee lower Internet ping or FPS.");
        Add("qos", "Per-app outbound DSCP request", networkEligible && options.PrioritizeNetwork ? PlanDisposition.Recommended : PlanDisposition.Preserved,
            "Optional pre-match request, not bandwidth reservation or an Ethernet-bound policy. Preserve original routing; unsupported routes are skipped.");
        int budgetFps = intent.Fps.ApplyGameProfile && intent.Fps.FrameCap == FortniteFrameCap.Target ? intent.Fps.TargetFps : intent.TargetFps;
        string budget = budgetFps > 0 ? $"{budgetFps} FPS gives {1000d / budgetFps:0.00} ms per frame. " : "No diagnostic budget selected; the game-cap choice is reviewed separately. ";
        Add("frame-budget", "Frame-budget advisor", PlanDisposition.Advisory, budget + (intent.Goal switch
        {
            OptimizationGoal.StableEndgames => "Prioritize p99/p99.9 and missed deadlines in demanding scenes; select a sustainable session cap.",
            OptimizationGoal.LowestLatency => "Compare supported PC latency and queue behavior; a lower cap can improve headroom but increases the interval between frames.",
            OptimizationGoal.HighestFps => "Use the highest rendered rate that stays within stability and heat constraints; peak lobby FPS is insufficient evidence.",
            _ => "Meet the chosen target with reasonable power and thermal headroom."
        }));
        Add("gpu-latency", "Native GPU/game latency controls", PlanDisposition.Advisory,
            (discreteNvidia ? "Verify Fortnite's supported Reflex setting and actual queue behavior. Do not stack undocumented driver tweaks or assume VRR from refresh rate. "
                : "Use only documented game/vendor mechanisms present on this GPU. NVIDIA-only settings are not offered as AMD/Intel substitutes. ")
            + (intent.AcceptTearing ? "Tearing is accepted: compare supported capped and uncapped presentation for your throughput/latency goal; no display setting is changed."
                : "Tearing is not accepted: verify actual VRR support/range and supported limiter/sync settings before choosing a cap; no display capability is inferred from refresh alone."));
        if (report.AvailableRamGiB is double free && free > 8)
            Add("memory", "Memory and shader preparation", PlanDisposition.Advisory,
                $"{free:0.0} GiB currently available. No RAM cleaning is justified by this snapshot; inspect commit, faults, VRAM budget and asset I/O during a separately enabled diagnosis.");
        else Add("memory", "Memory and shader preparation", PlanDisposition.Advisory,
            "Check commit/page-file and streaming evidence. Preserve useful caches; no standby purge or routine shader-cache deletion.");
        if (report.Hardware.AutomaticPagefile == false) plan.Warnings.Add("Automatic page-file management is disabled. This is a review item, not proof of current memory stalls; no page-file setting is changed.");
        foreach (var volume in report.Volumes.Where(v => v.FreePercent < 10))
            plan.Warnings.Add($"Storage {volume.Drive}: {volume.FreeGiB:0.0} GiB / {volume.FreePercent:0.0}% free. Review capacity; this alone does not diagnose an I/O bottleneck.");
        Add("diagnostics", "Optional frame-incident diagnostics", PlanDisposition.Advisory,
            "Import a capture or explicitly record a bounded sample. Missing CPU/GPU/sensor data remains Unknown; this plan does not start a benchmark or trace.");
        plan.Summary = $"{intent.Goal}: {plan.Actions.Count(a => a.Disposition == PlanDisposition.Recommended)} eligible preparation actions; "
            + $"{plan.Actions.Count(a => a.Disposition == PlanDisposition.Deferred)} deferred. Endgame FPS and latency benefit are unmeasured.";
        // Bind the actual effective close scope and mutations. Canonicalizing derived
        // switches makes Build -> Apply -> Build stable without permitting stale edits.
        var effective = Copy(options);
        if (!recoveryPending) effective = Apply(plan, options);
        effective.Intent = CopyIntent(intent);
        effective.OptionsVersion = 9;
        effective.ReviewedPlanFingerprint = "";
        effective.ProtectedAppPaths = ConnectionProtection.NormalizePaths(effective.ProtectedAppPaths)
            .Select(p => p.ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal).ToList();
        effective.SelectedApps = effective.SelectedApps.Select(p => p.ToLowerInvariant()).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        effective.ExcludedApps = effective.ExcludedApps.Select(p => p.ToLowerInvariant()).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        plan.InventoryFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { Inventory = Fingerprint(report), report.ActivePowerPlan, report.GameMode, report.BackgroundCapture, WindowsProfile = options.ApplyWindowsProfile ? report.WindowsProfile?.Fingerprint : "Preserved", GameConfig = intent.Fps.ApplyGameProfile ? report.GameSettingsFingerprint : "Preserved", GameRunning = gameRunning, RecoveryPending = recoveryPending, Options = effective }))));
        return plan;
    }

    public static AppOptions Apply(OptimizationPlan plan, AppOptions options)
    {
        if (plan.RecoveryPending) throw new InvalidOperationException("Finish recovery before applying a new plan.");
        if (plan.Schema != 1 || string.IsNullOrWhiteSpace(plan.InventoryFingerprint)) throw new InvalidDataException("The reviewed plan is invalid.");
        var result = Copy(options); result.Intent = CopyIntent(plan.Intent); result.OptionsVersion = 9;
        bool Recommended(string id) => plan.Actions.Any(a => a.Id == id && a.Disposition == PlanDisposition.Recommended);
        result.HighPerformancePower = Recommended("power");
        result.TuneEthernet = Recommended("ethernet");
        result.PrioritizeNetwork = Recommended("qos");
        if (plan.GameRunning) { result.PauseOptionalServices = false; }
        result.SelectedApps.RemoveAll(id => IsCaptureIdentity(id) && result.Intent.Activity != SessionActivity.Gaming);
        result.ReviewedPlanFingerprint = plan.InventoryFingerprint;
        return result;
    }

    public static bool ShouldPreserveApp(AppCandidate app, AppOptions options) => options.Intent?.Activity != SessionActivity.Gaming
        && (IsCaptureIdentity(app.Id) || IsCaptureIdentity(app.LaunchPath) || IsCaptureIdentity(app.Name));
    private static bool IsCaptureIdentity(string text) => new[] { "obs64", "obs32", "obs-studio", "streamlabs", "xsplit", "bandicam", "camtasia", "action!", "nvidia broadcast" }
        .Any(part => text.Contains(part, StringComparison.OrdinalIgnoreCase));

    public static string Fingerprint(PcAnalysis report) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    { report.Hardware, report.AcPower, report.Displays, report.HagsPreference }))));
    public static string Format(OptimizationPlan plan)
    {
        var b = new StringBuilder(); b.AppendLine("REVIEWED PER-PC PLAN").AppendLine(plan.Summary)
            .AppendLine($"Goal: {plan.Intent.Goal} | Activity: {plan.Intent.Activity} | Match safe: {plan.Intent.MatchSafe}")
            .AppendLine("Detected / likely appropriate does not mean measured faster.").AppendLine();
        foreach (string warning in plan.Warnings) b.AppendLine("Attention: " + warning);
        foreach (var action in plan.Actions) b.AppendLine().AppendLine($"[{action.Disposition}] {action.Title}")
            .AppendLine(action.Reason).AppendLine(string.IsNullOrEmpty(action.Tradeoff) ? action.Confidence : action.Tradeoff);
        return b.ToString();
    }
}
