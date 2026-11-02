using System.Drawing;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace TourneyMode;

/// <summary>
/// Hidden, synthetic UI QA. No engine, system inventory, Show/Shown, input,
/// capture, tuning, or live-app controls are used.
/// </summary>
public static class UiPreview
{
    public static void Write(string outputDirectory)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Hidden dialog previews must run on the application's STA entry thread.");
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var checks = new List<PreviewCheck>();
        var images = new List<string>();
        var renderEvidence = new List<RenderEvidence>();
        var report = SyntheticReport();
        var options = new AppOptions
        {
            OptionsVersion = 8,
            Intent = new UserIntent { Goal = OptimizationGoal.StableEndgames, MatchSafe = true, TargetFps = 144 },
            FortniteOnly = true, KeepAppsClosed = true, ForceClose = false,
            HighPerformancePower = false, PauseOptionalServices = false,
            TuneEthernet = false, PrioritizeNetwork = false, CloseCodex = false,
            ProtectedAppPaths = [@"C:\PreviewOnly\KeptVoiceApp.exe"]
        };
        string original = JsonSerializer.Serialize(options, SystemAnalysis.Json);

        var tweakReport = SyntheticReport(); tweakReport.GameMode = 0; tweakReport.BackgroundCapture = 1;
        var tweakAssessment = TweakPolicy.Create(tweakReport, options, gameRunning: false, recoveryPending: false);
        using (var main = MainForm.CreateSyntheticPreview(options))
        {
            bool shown = false; main.Shown += (_, _) => shown = true;
            main.PrepareSyntheticPreview("Tweak", null); LayoutHidden(main);
            Check(checks, "Tweak starts with scan only", Field<Button>(main, "_startTweak").Enabled && !Field<Button>(main, "_applyTweak").Enabled,
                "No assessment exists, so Apply is disabled while Start Tweak is available.");
            Check(checks, "Guide initially requires scan", !Field<bool>(main, "_guideHasScan"), "No motherboard or BIOS recommendation is invented before scan evidence exists.");
            Check(checks, "Experimental controls default off", !Field<ThemeSwitch>(main, "_experimentalWired").Checked && !Field<ThemeSwitch>(main, "_experimentalDscp").Checked && !Field<ThemeSwitch>(main, "_experimentalGovernor").Checked,
                "All three experiments require an explicit opt-in and affect the next reviewed Tweak only.");
            Check(checks, "Internet preview starts idle and load off", !Field<ThemeSwitch>(main, "_internetLoaded").Checked && Field<CancellationTokenSource?>(main, "_internetCancellation") == null && Field<InternetTestReport?>(main, "_internetReport") == null,
                "Construction performs no network test and does not opt into HTTPS traffic.");
            foreach (string field in new[] { "_tweakGoal", "_tweakActivity", "_tweakPower" })
            {
                var choice = Field<IThemeChoice>(main, field);
                Check(checks, "main " + field + " default is readable", !string.IsNullOrWhiteSpace(choice.DisplayValue),
                    "The custom choice paints its selected label directly, including hidden previews.");
            }
            foreach (string page in new[] { "Overview", "Tweak", "Strip Windows ⚠", "Internet Optimizer", "Experimental", "Guide / BIOS", "Apps", "Activity", "Updates" })
            {
                string imageName = page == "Guide / BIOS" ? "guide-bios" : page.ToLowerInvariant().Replace(" ⚠", "").Replace(" ", "-");
                main.ClientSize = new Size(1220, 900); main.PrepareSyntheticPreview(page, tweakAssessment); LayoutHidden(main);
                Save(main, "main-" + imageName + ".png", output, images, checks, renderEvidence);
                main.Size = main.MinimumSize; LayoutHidden(main);
                Save(main, "main-" + imageName + "-minimum.png", output, images, checks, renderEvidence);
            }
            main.PrepareSyntheticPreview("Updates", tweakAssessment); main.Size = main.MinimumSize;
            var updateActions = new[] { Field<Button>(main, "_updateAction"), Field<Button>(main, "_updateRecheck"), Field<Button>(main, "_updateCancel") };
            foreach (var action in updateActions) action.Visible = true;
            LayoutHidden(main); Save(main, "main-updates-actions-minimum.png", output, images, checks, renderEvidence);
            Check(checks, "Updates action states share one bounded row", updateActions.All(action => action.Parent is TableLayoutPanel
                && action.Top >= 0 && action.Bottom <= action.Parent.ClientSize.Height && action.Right <= action.Parent.ClientSize.Width),
                "Download, recheck and cancel controls fit simultaneously without relying on hidden-control exclusions.");
            Check(checks, "Updates offers bundled licenses", Field<Button>(main, "_thirdPartyLicenses").Text == "Third-party licenses",
                "The Updates page exposes the local read-only license viewer without constructing the updater.");
            main.PrepareSyntheticPreview("Updates", tweakAssessment);
            Check(checks, "Strip optional groups start unselected", Field<Dictionary<string, CheckBox>>(main, "_stripChoices").Values.All(check => !check.Checked) && !Field<Button>(main, "_stripApply").Enabled,
                "No functionality is selected for loss by default and Apply requires a read-only review.");
            main.PrepareSyntheticInternetPreview(SyntheticInternetReport(processingMeasured: false)); LayoutHidden(main);
            Save(main, "main-internet-provisional-minimum.png", output, images, checks, renderEvidence);
            Check(checks, "Internet unknown trace remains unknown", Field<ListView>(main, "_internetResults").Items.Cast<ListViewItem>().All(row => row.SubItems[3].Text == "Unknown") && !Field<Button>(main, "_internetApply").Enabled,
                "Ping/stability-only evidence cannot become a processing measurement or an all-goal Apply action.");
            var networkSession = new SessionState { Workflow = "Internet", Phase = "Active", RestorePoint = new() { Verified = true }, InternetCalibration = new() { Restored = true } };
            main.PrepareSyntheticInternetPreview(SyntheticInternetReport(processingMeasured: false), networkSession); LayoutHidden(main);
            Check(checks, "Internet provisional override starts off", !Field<ThemeSwitch>(main, "_internetAllowProvisional").Checked && !Field<Button>(main, "_internetApply").Enabled,
                "A protected completed session does not inherit consent to apply incomplete evidence.");
            Field<ThemeSwitch>(main, "_internetAllowProvisional").Checked = true;
            Check(checks, "Internet provisional opt-in opens separate review", Field<Button>(main, "_internetApply").Enabled && Field<Button>(main, "_internetApply").Text == "Review provisional",
                "Explicit opt-in exposes the strong review dialog, without invoking apply or claiming all three goals were measured.");
            main.PrepareSyntheticInternetPreview(SyntheticInternetReport(processingMeasured: true), networkSession); LayoutHidden(main);
            Check(checks, "Internet qualified report needs shared safety session", Field<Button>(main, "_internetApply").Enabled && Field<Button>(main, "_internetRestore").Enabled && !Field<Button>(main, "_internetStart").Enabled,
                "A qualified synthetic result can be reviewed while the shared point is retained; a second test cannot replace that session.");
            Save(main, "main-internet-qualified-minimum.png", output, images, checks, renderEvidence);
            var stripReview = new StripBackup { SelectedGroups = ["printing"], Services = [new() { GroupId = "printing", Name = "Spooler", Existed = true, OriginalStartType = 2, WasRunning = true }], Groups = [new() { Id = "printing", State = "Captured" }] };
            main.PrepareSyntheticStripPreview(stripReview); LayoutHidden(main);
            Save(main, "main-strip-reviewed-minimum.png", output, images, checks, renderEvidence);
            Check(checks, "Strip read-only review enables consent action", Field<Button>(main, "_stripApply").Enabled && !Field<Button>(main, "_stripRepairAll").Enabled,
                "A synthetic installed service is reviewed; the action opens separate consent, while repair requires actual recorded changes.");
            stripReview.ApplyAttempted = true; stripReview.Services[0].ApplyAttempted = true; stripReview.Groups[0].State = "Disabled";
            var stripSession = new SessionState { Workflow = "Strip", Phase = "Active", Options = PlannerDialog.CopyOptions(options), Strip = stripReview };
            main.PrepareSyntheticStripPreview(null, stripSession); LayoutHidden(main);
            Save(main, "main-strip-repair-minimum.png", output, images, checks, renderEvidence);
            Check(checks, "Strip session exposes targeted repair", Field<Button>(main, "_stripRepairAll").Enabled && Field<Dictionary<string, ThemeButton>>(main, "_stripRepairButtons")["printing"].Enabled && !Field<Button>(main, "_stripApply").Enabled,
                "Only recorded changed groups offer repair; a new stripping run cannot replace an active session.");
            var appendReview = new StripBackup { SelectedGroups = ["search"], Services = [new() { GroupId = "search", Name = "WSearch", Existed = true, OriginalStartType = 2, OriginalDelayedAutoStart = true, WasRunning = true }], Groups = [new() { Id = "search", State = "Captured" }] };
            main.PrepareSyntheticStripPreview(appendReview, new SessionState { Workflow = "Tweak", Phase = "Active", RestorePoint = new() { Verified = true } }); LayoutHidden(main);
            Check(checks, "Strip can append to protected Tweak session", Field<Button>(main, "_stripApply").Enabled && !Field<Button>(main, "_stripRepairAll").Enabled,
                "An active Tweak with verified restore point and no existing Strip permits the reviewed optional append.");
            main.PrepareSyntheticPreview("Overview", tweakAssessment); main.SetPreviewSessionOptions(true); LayoutHidden(main);
            Save(main, "main-session-options-minimum.png", output, images, checks, renderEvidence);
            Check(checks, "legacy Tourney controls remain reachable", Descendants(main).OfType<CheckBox>().Where(main.IsOnCurrentPage).Count() == 13,
                "The expanded Session options section contains the original session preferences.");
            main.SetPreviewSessionOptions(false);
            main.PrepareSyntheticPreview("Tweak", tweakAssessment); LayoutHidden(main);
            Check(checks, "Guide populated only from scan data", Field<bool>(main, "_guideHasScan"), "The guide builds from the supplied synthetic hardware report; no firmware mutation or website is opened.");
            Check(checks, "reviewed eligible scan offers separate Apply", Field<Button>(main, "_applyTweak").Enabled,
                "The synthetic scan contains observed allowlisted corrections; no apply button is invoked.");
            Field<ThemeSwitch>(main, "_experimentalDscp").Checked = true;
            Check(checks, "Experimental opt-in invalidates old review", !Field<Button>(main, "_applyTweak").Enabled,
                "A changed experiment cannot inherit consent from a previous Tweak assessment.");
            Field<ThemeSwitch>(main, "_experimentalDscp").Checked = false; main.PrepareSyntheticPreview("Tweak", tweakAssessment);
            Field<ThemeChoice<OptimizationGoal>>(main, "_tweakGoal").Value = OptimizationGoal.LowestLatency;
            Check(checks, "changed Tweak goal invalidates scan", !Field<Button>(main, "_applyTweak").Enabled && Field<TweakAssessment?>(main, "_tweakAssessment") == null,
                "Changed choices require a fresh scan and review before any apply.");
            var pending = new SessionState { Workflow = "Tweak", Phase = "Restoring", Options = PlannerDialog.CopyOptions(options),
                Features = [new() { Name = "Power settings (sample)", State = FeatureState.Deferred, Detail = "Synthetic pending recovery." }] };
            main.PrepareSyntheticPreview("Tweak", tweakAssessment, pending); LayoutHidden(main);
            Check(checks, "shared recovery blocks new Tweak", !Field<Button>(main, "_startTweak").Enabled && !Field<Button>(main, "_applyTweak").Enabled && Field<Button>(main, "_restoreTweak").Enabled,
                "A shared recovery session permits Restore and blocks starting another workflow.");
            Check(checks, "Experimental choices lock during active session", !Field<ThemeSwitch>(main, "_experimentalWired").Enabled && !Field<ThemeSwitch>(main, "_experimentalDscp").Enabled && !Field<ThemeSwitch>(main, "_experimentalGovernor").Enabled,
                "Changing a switch cannot imply undo while recorded settings remain active.");
            Save(main, "main-tweak-recovery.png", output, images, checks, renderEvidence);
            main.PrepareSyntheticPreview("Updates", tweakAssessment, pending); LayoutHidden(main);
            Check(checks, "shared recovery blocks update", !Field<Button>(main, "_updateAction").Enabled, "Update installation cannot replace a session controller during recovery.");
            Check(checks, "main preview never constructs updater", Field<AppUpdater?>(main, "_updater") == null && !Field<bool>(main, "_startupUpdateCheckStarted"),
                "The engine-free preview never invokes Shown, startup checks or update operations.");
            Check(checks, "main preview never shown", !shown && !main.Visible, "Hidden controls and synthetic data only.");
        }
        using (var licenses = MainForm.CreateThirdPartyLicensesDialog())
        {
            bool shown = false; licenses.Shown += (_, _) => shown = true;
            LayoutHidden(licenses);
            var licenseText = Descendants(licenses).OfType<RichTextBox>().Single(control => control.Name == "ThirdPartyLicenseText");
            Check(checks, "Bundled license viewer is read-only and scrollable", licenseText.ReadOnly && !licenseText.DetectUrls
                && licenseText.ScrollBars == RichTextBoxScrollBars.ForcedVertical && licenseText.BackColor.ToArgb() == UiTheme.Surface.ToArgb()
                && licenseText.ForeColor.ToArgb() == UiTheme.Text.ToArgb()
                && licenseText.Text.Replace("\r\n", "\n").Replace('\r', '\n') == ThirdPartyNotices.ReadAll().Replace("\r\n", "\n").Replace('\r', '\n'),
                "Embedded license text is shown in a dark scrollable reader; no browser or update request is involved.");
            Save(licenses, "third-party-licenses.png", output, images, checks, renderEvidence);
            licenses.Size = licenses.MinimumSize; LayoutHidden(licenses);
            Save(licenses, "third-party-licenses-minimum.png", output, images, checks, renderEvidence);
            Check(checks, "Bundled license preview never shown", !shown && !licenses.Visible, "Only hidden layout and bitmap rendering were requested.");
        }
        using (var confirm = new TweakApplyDialog(tweakAssessment))
        {
            LayoutHidden(confirm); Save(confirm, "tweak-review-confirmation.png", output, images, checks, renderEvidence);
            Check(checks, "Tweak confirmation requires named apply", confirm.AcceptButton == null && confirm.CancelButton != null,
                "Pressing Enter without selecting Apply does not implicitly consent; Escape cancels.");
        }
        using (var stripConsent = new StripConsentDialog("PRINTING (synthetic preview)\r\nPrinting stops working until repaired.\r\nSpooler: running → stopped and disabled; original saved for repair."))
        {
            LayoutHidden(stripConsent);
            Check(checks, "Strip consent starts disabled", !Field<Button>(stripConsent, "_apply").Enabled && !Field<CheckBox>(stripConsent, "_understand").Checked,
                "Functionality-loss acknowledgement is required before the named disable action can be used.");
            Save(stripConsent, "strip-feature-consent.png", output, images, checks, renderEvidence);
            Field<CheckBox>(stripConsent, "_understand").Checked = true;
            Check(checks, "Strip acknowledgement gates disable action", Field<Button>(stripConsent, "_apply").Enabled && stripConsent.AcceptButton == null,
                "Explicit acknowledgement enables only the named action; no click or mutation occurred.");
            stripConsent.Size = stripConsent.MinimumSize; LayoutHidden(stripConsent);
            Save(stripConsent, "strip-feature-consent-minimum.png", output, images, checks, renderEvidence);
        }
        using (var internetConsent = new InternetTestConsentDialog(new InternetTestOptions { MaximumCandidates = 4 }))
        {
            LayoutHidden(internetConsent); Save(internetConsent, "internet-test-consent.png", output, images, checks, renderEvidence);
            Check(checks, "Internet test requires reconnect consent", !Field<Button>(internetConsent, "_start").Enabled && !Field<CheckBox>(internetConsent, "_consent").Checked && internetConsent.AcceptButton == null,
                "The bounded test does not start without acknowledging reconnects and using the named Start button.");
        }
        var provisionalReport = SyntheticInternetReport(processingMeasured: false);
        using (var provisional = new InternetApplyReviewDialog(provisionalReport.ProvisionalWinner!, "Synthetic RSS candidate only", provisionalReport, provisional: true))
        {
            LayoutHidden(provisional); Save(provisional, "internet-provisional-consent.png", output, images, checks, renderEvidence);
            var apply = Descendants(provisional).OfType<Button>().Single(button => button.Name == "ApplyInternetSettings");
            Check(checks, "Internet provisional review requires new acknowledgement", !apply.Enabled && !Field<CheckBox>(provisional, "_provisionalConsent").Checked && provisional.AcceptButton == null,
                "The page opt-in does not bypass the separate acknowledgement of unmeasured processing risk.");
            Field<CheckBox>(provisional, "_provisionalConsent").Checked = true;
            Check(checks, "Internet strong acknowledgement gates apply", apply.Enabled, "Acknowledgement enables the named action only; no button click, network test or mutation occurred.");
        }

        using (var planner = new PlannerDialog(report, options, gameRunning: false, recoveryPending: false))
        {
            bool shown = false;
            planner.Shown += (_, _) => shown = true;
            LayoutHidden(planner);
            var use = Field<Button>(planner, "_use");
            var tabs = Descendants(planner).OfType<TabControl>().Single();
            foreach (string field in new[] { "_goal", "_activity", "_power" })
            {
                var choice = Field<ComboBox>(planner, field);
                Check(checks, "planner " + field.TrimStart('_') + " default is readable",
                    choice.SelectedIndex >= 0 && !string.IsNullOrWhiteSpace(choice.Text) && choice.ForeColor.ToArgb() != choice.BackColor.ToArgb(),
                    "A selected nonempty value uses explicit contrasting native text/window colors.");
            }
            Check(checks, "planner initially requires review", !use.Enabled, "Use this plan is disabled before the Review plan tab is visited.");
            Check(checks, "planner is synthetic and inactive", planner.SelectedOptions == null && !planner.Plan.RecoveryPending, "Construction builds a pure proposal and selects no applied options.");
            Save(planner, "planner-goals.png", output, images, checks, renderEvidence);

            tabs.SelectedIndex = 1;
            LayoutHidden(planner);
            Check(checks, "review enables eligible plan saving", use.Enabled, "Visiting Review plan permits saving eligible choices without activating a mode.");
            Save(planner, "planner-review.png", output, images, checks, renderEvidence);

            Field<ComboBox>(planner, "_goal").SelectedItem = OptimizationGoal.LowestLatency;
            Check(checks, "changed goal requires fresh review", !use.Enabled && planner.Plan.Intent.Goal == OptimizationGoal.LowestLatency,
                "A goal change updates the proposal and invalidates review, even if the review tab is still selected.");
            tabs.SelectedIndex = 0; tabs.SelectedIndex = 1;
            LayoutHidden(planner);
            Check(checks, "fresh review re-enables saving", use.Enabled, "The changed plan can be reviewed explicitly.");
            Check(checks, "review never applies options itself", planner.SelectedOptions == null, "No Use button, activation or external save was invoked.");

            tabs.SelectedIndex = 0;
            planner.Size = planner.MinimumSize;
            LayoutHidden(planner);
            Save(planner, "planner-goals-minimum.png", output, images, checks, renderEvidence);
            tabs.SelectedIndex = 3;
            LayoutHidden(planner);
            Save(planner, "planner-fps-minimum.png", output, images, checks, renderEvidence);
            planner.Size = new Size(1016, 889); LayoutHidden(planner);
            Field<CheckBox>(planner, "_applyFps").Checked = true;
            Field<ComboBox>(planner, "_frameCap").SelectedItem = FortniteFrameCap.Target;
            Field<NumericUpDown>(planner, "_gameFps").Value = 200;
            Check(checks, "FPS choices enter the reviewed plan", planner.Plan.Intent.Fps.ApplyGameProfile
                && planner.Plan.Intent.Fps.FrameCap == FortniteFrameCap.Target && planner.Plan.Intent.Fps.TargetFps == 200 && !use.Enabled,
                "Actual game-cap controls update copied intent and invalidate previous review without enabling mode.");
            Save(planner, "planner-fps.png", output, images, checks, renderEvidence);
            Check(checks, "planner never shown", !shown && !planner.Visible, "Only hidden handles and DrawToBitmap were used.");
        }

        using (var recovery = new PlannerDialog(report, options, gameRunning: true, recoveryPending: true))
        {
            bool shown = false;
            recovery.Shown += (_, _) => shown = true;
            LayoutHidden(recovery);
            var use = Field<Button>(recovery, "_use");
            Check(checks, "recovery plan saving initially blocked", !use.Enabled && recovery.Plan.RecoveryPending,
                "A recovery-pending session cannot be replaced by a new plan.");
            Descendants(recovery).OfType<TabControl>().Single().SelectedIndex = 1;
            LayoutHidden(recovery);
            Check(checks, "recovery remains blocked after review", !use.Enabled, "Reviewing facts and decisions does not enable Use this plan during recovery.");
            Save(recovery, "planner-recovery-readonly.png", output, images, checks, renderEvidence);
            Check(checks, "recovery dialog never shown", !shown && !recovery.Visible, "No recovery operation or live session was created.");
        }

        using (var diagnostics = new DiagnosticsDialog(Path.Combine(output, "unused-diagnostic-output"), suggestedTargetFps: 144))
        {
            bool shown = false;
            diagnostics.Shown += (_, _) => shown = true;
            LayoutHidden(diagnostics);
            Check(checks, "diagnostics construction is idle", !Field<bool>(diagnostics, "_busy") && Field<object?>(diagnostics, "_cancellation") == null,
                "No import, capture, process start or countdown was requested.");
            Check(checks, "diagnostic stop/save initially disabled", !Field<Button>(diagnostics, "_cancel").Enabled && !Field<Button>(diagnostics, "_save").Enabled,
                "There is no active capture or report to stop/save.");
            Check(checks, "diagnostic duration is bounded", Field<NumericUpDown>(diagnostics, "_seconds").Minimum >= 10 && Field<NumericUpDown>(diagnostics, "_seconds").Maximum <= 120,
                "The UI offers only bounded explicit captures.");
            Save(diagnostics, "diagnostics.png", output, images, checks, renderEvidence);
            diagnostics.Size = diagnostics.MinimumSize;
            LayoutHidden(diagnostics);
            Save(diagnostics, "diagnostics-minimum.png", output, images, checks, renderEvidence);
            Check(checks, "diagnostics never shown", !shown && !diagnostics.Visible, "Only local hidden control handles were materialized.");
        }
        Check(checks, "source choices preserved", original == JsonSerializer.Serialize(options, SystemAnalysis.Json),
            "Creating and reviewing dialogs did not mutate the input options or keep list.");
        bool passed = checks.All(c => c.Passed);
        File.WriteAllText(Path.Combine(output, "ui-checks.json"), JsonSerializer.Serialize(new
        {
            Scope = "Synthetic hidden dialog previews only; no live UI, scans, capture, tuning, or engine.",
            Passed = passed, Images = images, Checks = checks, RenderingEvidence = renderEvidence
        }, SystemAnalysis.Json));
        var text = new StringBuilder("HIDDEN UI PREVIEW CHECKS\r\nSynthetic data only. No Show/Shown, live controls, inventory, captures or tuning.\r\n\r\n");
        foreach (var check in checks) text.Append(check.Passed ? "PASS " : "FAIL ").Append(check.Name).Append(" - ").AppendLine(check.Detail);
        foreach (var rendering in renderEvidence.Where(r => r.PreviewFallbackUsed))
            text.Append("INFO ").Append(rendering.Image).Append(' ').Append(rendering.Choice)
                .AppendLine(" - Native hidden WM_DRAWITEM callback unavailable. Shared selection renderer painted this preview only; no live drawing was observed.");
        text.AppendLine().Append("RESULT: ").Append(checks.Count(c => c.Passed)).Append(" passed, ").Append(checks.Count(c => !c.Passed)).AppendLine(" failed.");
        File.WriteAllText(Path.Combine(output, "ui-checks.txt"), text.ToString());
        if (!passed) throw new InvalidOperationException("Hidden UI checks found a control/gating problem. Inspect ui-checks.txt and the generated PNGs; no live action occurred.");
    }

    private static PcAnalysis SyntheticReport() => new()
    {
        CapturedUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), ReadOnly = true,
        Scope = "SYNTHETIC UI PREVIEW DATA. No hardware or app inventory was performed.",
        AcPower = true, AvailableRamGiB = 20, MemoryLoadPercent = 38,
        CommitUsedGiB = 12, CommitLimitGiB = 48, ActivePowerPlan = "00000000-0000-0000-0000-000000000001",
        GameMode = 1, BackgroundCapture = 0,
        Hardware = new PcFacts
        {
            Windows = "Synthetic Windows 11 x64", Cpu = "AMD Ryzen 7 7800X3D (synthetic preview)",
            Cores = 8, Threads = 16, OsVisibleLogicalProcessors = 16, ProcessorGroups = 1,
            RamGiB = 32, MemorySpeedMT = 6000, Bios = "Preview only", HasBattery = false,
            IsPortable = false, AutomaticPagefile = true,
            TopologySource = "Synthetic fixture", CpuEfficiencyClasses = [new() { EfficiencyClass = 0, LogicalProcessors = 16 }],
            Gpus = [new() { Name = "AMD Radeon RX 7800 XT (synthetic preview)", Driver = "Preview only", DedicatedMemoryMiB = 16384, MemorySource = "Synthetic fixture" }],
            Adapters = [new() { Name = "Synthetic wired adapter", Link = "1 Gbps" }]
        },
        Displays = [new() { Primary = true, Width = 1920, Height = 1080, RefreshHz = 144 }],
        Volumes = [new() { Drive = "Preview volume", FreeGiB = 200, FreePercent = 40 }],
        Recommendations = [new() { Area = "Evidence", Finding = "Preview facts are synthetic.", Action = "Review the generated decisions; no performance gain is inferred.", Evidence = "UI fixture only" }]
    };

    private static InternetTestReport SyntheticInternetReport(bool processingMeasured)
    {
        const string gateway = "192.0.2.1", remote = "198.51.100.1";
        InternetMetrics Metrics(double ping) => new() { Sent = 12, Received = 12, PingMedianMs = ping, PingP95Ms = ping + 1, JitterMs = .5, PacketLossPercent = 0,
            LocalProcessingMedianMs = processingMeasured ? .08 : null, ProcessingTraceValid = processingMeasured, ProcessingSamples = processingMeasured ? 12 : 0,
            ProcessingEvidence = processingMeasured ? "Synthetic trace fixture only" : "No valid local processing trace", MachineCpuPercent = 3 };
        InternetMeasurement Period(string name, double ping) => new() { Label = name + " (synthetic)", Metrics = Metrics(ping), Endpoints = new() { [gateway] = Metrics(.4), [remote] = Metrics(ping) } };
        var candidate = new InternetCandidate { Id = "synthetic-rss", Title = "RSS candidate (synthetic)", Basis = "Synthetic capability fixture; never applied.", RssEnabled = true, RssQueues = 2, RssProcessors = 2 };
        return new()
        {
            Baseline = new() { Name = "Synthetic wired adapter", DriverVersion = "Preview only", GatewayTargets = [gateway], Capabilities = new() { LogicalProcessors = 16, RssSupported = true }, Rss = new() { Enabled = true, NumberOfReceiveQueues = 1, MaxProcessors = 1 } },
            Trials = [new() { Candidate = candidate, BaselineBefore = Period("Before", 20), Treatment = Period("Candidate", 19), BaselineAfter = Period("After", 20), Applied = true, RestoreAttempted = true, RestoreVerified = true }],
            Scores = [new() { CandidateId = candidate.Id, Eligible = processingMeasured, PingMeasured = true, ProcessingMeasured = processingMeasured, StabilityMeasured = true, ProvisionalRank = 1, ValidRepeats = 2, Confidence = "Synthetic UI evidence only", Reasons = ["No real network measurement was performed."] }],
            Winner = processingMeasured ? candidate : null, ProvisionalWinner = candidate, HasQualifiedWinner = processingMeasured, CanApplyBest = processingMeasured,
            PingMeasured = true, ProcessingMeasured = processingMeasured, StabilityMeasured = true, Completed = true, RollbackVerified = true,
            Recommendation = "Synthetic preview · " + (processingMeasured ? "qualified-state display only" : "Processing is unknown; preserve originals."), Limitations = ["UI fixture only. No probes, adapter changes or benchmarks occurred."]
        };
    }

    private static void LayoutHidden(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        CreateHandles(form);
        for (int i = 0; i < 3; i++) LayoutTree(form);
    }
    private static void CreateHandles(Control control)
    {
        _ = control.Handle;
        foreach (Control child in control.Controls) CreateHandles(child);
    }
    private static void LayoutTree(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) LayoutTree(child);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static T Field<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Preview field unavailable: " + name);
        return (T)field.GetValue(instance)!;
    }
    private static void Save(Form form, string name, string output, List<string> images, List<PreviewCheck> checks, List<RenderEvidence> rendering)
    {
        CheckLayout(form, name, checks);
        bool choicesVisible = form is PlannerDialog && Descendants(form).OfType<ComboBox>().Any(InSelectedTab);
        if (choicesVisible) ((PlannerDialog)form).ResetChoicePreviewEvidence();
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        if (choicesVisible)
            using (var graphics = Graphics.FromImage(image)) ((PlannerDialog)form).DrawMissingChoicePreviewSelections(graphics);
        image.Save(Path.Combine(output, name), System.Drawing.Imaging.ImageFormat.Png);
        images.Add(name);
        if (choicesVisible)
            foreach (var evidence in ((PlannerDialog)form).ChoicePreviewEvidenceSnapshot())
            {
                bool native = evidence.SelectionDrawCalls > 0 && evidence.RenderedText == evidence.ExpectedText;
                bool fallback = evidence.PreviewFallbackDrawCalls > 0 && evidence.PreviewFallbackText == evidence.ExpectedText;
                Check(checks, name + " " + evidence.Name + " selection rendered",
                    !string.IsNullOrWhiteSpace(evidence.ExpectedText) && (native || fallback),
                    $"Native callback calls={evidence.SelectionDrawCalls}; shared-renderer preview fallback calls={evidence.PreviewFallbackDrawCalls}; expected='{evidence.ExpectedText}'; preview='{(native ? evidence.RenderedText : evidence.PreviewFallbackText)}'.");
                rendering.Add(new() { Image = name, Choice = evidence.Name, NativeCallbackObserved = evidence.SelectionDrawCalls > 0,
                    PreviewFallbackUsed = evidence.PreviewFallbackDrawCalls > 0, ExpectedText = evidence.ExpectedText,
                    RenderedText = native ? evidence.RenderedText : evidence.PreviewFallbackText });
            }
    }
    private static bool InSelectedTab(Control control)
    {
        for (Control? current = control; current != null; current = current.Parent)
            if (current is TabPage page && page.Parent is TabControl tabs && tabs.SelectedTab != page) return false;
        return true;
    }
    private static void CheckLayout(Form form, string label, List<PreviewCheck> checks)
    {
        var failures = new List<string>();
        foreach (var control in Descendants(form).Where(c => c is Button or ComboBox or NumericUpDown or CheckBox or TextBoxBase or IThemeChoice).Where(InSelectedTab).Where(c => form is not MainForm main || main.IsOnCurrentPage(c)))
        {
            if (control.Width < 3 || control.Height < 3) { failures.Add(Describe(control) + " has no usable size"); continue; }
            for (Control child = control; child.Parent != null; child = child.Parent)
            {
                var parent = child.Parent;
                var visible = parent.ClientRectangle; visible.Inflate(2, 2);
                bool fits = visible.Contains(child.Bounds);
                if (!fits && parent is ScrollableControl { AutoScroll: true } scroll)
                {
                    var scrollArea = scroll.DisplayRectangle; scrollArea.Inflate(2, 2);
                    fits = scrollArea.Contains(child.Bounds);
                }
                if (fits) continue;
                failures.Add(Describe(control) + " clipped at " + parent.GetType().Name + ": " + child.Bounds + " outside " + parent.ClientRectangle);
                break;
            }
        }
        Check(checks, label + " critical controls fit", failures.Count == 0,
            failures.Count == 0 ? "Inputs/buttons fit each parent client or explicit scroll area." : string.Join(" | ", failures.Distinct()));
    }
    private static string Describe(Control control)
    {
        string text = control.Text.Replace("\r", " ").Replace("\n", " ");
        if (text.Length > 55) text = text[..55];
        return control.GetType().Name + " '" + text + "'";
    }
    private static void Check(List<PreviewCheck> checks, string name, bool passed, string detail) => checks.Add(new() { Name = name, Passed = passed, Detail = detail });
    private sealed class PreviewCheck { public string Name { get; set; } = ""; public bool Passed { get; set; } public string Detail { get; set; } = ""; }
    private sealed class RenderEvidence
    {
        public string Image { get; set; } = "";
        public string Choice { get; set; } = "";
        public bool NativeCallbackObserved { get; set; }
        public bool PreviewFallbackUsed { get; set; }
        public string ExpectedText { get; set; } = "";
        public string RenderedText { get; set; } = "";
    }
}
