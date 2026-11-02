using System.Drawing;
using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private readonly ThemeChoice<OptimizationGoal> _tweakGoal = new() { Display = TweakGoalName, AccessibleName = "Performance goal" };
    private readonly ThemeChoice<SessionActivity> _tweakActivity = new() { AccessibleName = "Session activity" };
    private readonly ThemeChoice<PowerPreference> _tweakPower = new() { Display = PlannerDialog.PowerName, AccessibleName = "Power preference" };
    private readonly CheckBox _tweakHeat = new() { Text = "Allow higher power and heat", AutoSize = true, ForeColor = UiTheme.Muted };
    private readonly ThemeButton _startTweak = new() { Text = "Tweak PC", Primary = true, Width = 155 };
    private readonly ThemeButton _applyTweak = new() { Text = "Review & apply", Primary = true, Width = 170 };
    private readonly ThemeButton _cancelTweak = new() { Text = "Cancel", Width = 100 };
    private readonly ThemeButton _restoreTweak = new() { Text = "Restore changes", Width = 170 };
    private readonly Label _tweakSummary = new();
    private readonly Label _tweakProgressText = new();
    private readonly ProgressBar _tweakProgress = new() { Dock = DockStyle.Top, Height = 5, Margin = new Padding(0, 10, 0, 14) };
    private readonly ListView _tweakFindings = new();
    private readonly RichTextBox _findingDetail = new();
    private TweakAssessment? _tweakAssessment;
    private CancellationTokenSource? _tweakCancellation;
    private List<FeatureResult> _lastTweakResults = [];

    private void BuildTweakPage(Panel page)
    {
        var stack = UiTheme.Stack(); page.Controls.Add(stack);
        var setup = Card("Tune for FPS, response and steadier frames", "Tweak PC checks your hardware and settings, then prepares supported optimizations. Review the exact changes before applying. A verified Windows restore point is required before any change.", out var setupBody);
        var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 2) };
        foreach (var pair in new (string Name, Control Control)[] { ("YOUR GOAL", _tweakGoal), ("ACTIVITY", _tweakActivity), ("POWER PREFERENCE", _tweakPower) })
        {
            var group = UiTheme.Stack(); group.Dock = DockStyle.None; group.Width = 212; group.Margin = new Padding(0, 0, 14, 12);
            UiTheme.Add(group, UiTheme.Label(pair.Name, 8, true, UiTheme.Muted)); pair.Control.Width = 210; UiTheme.Add(group, pair.Control); choices.Controls.Add(group);
        }
        _tweakGoal.Value = OptimizationGoal.Balanced; _tweakActivity.Value = _preferences.Intent.Activity; _tweakPower.Value = PowerPreference.Maximum; _tweakHeat.Checked = true;
        _tweakGoal.ValueChanged += (_, _) => InvalidateTweakAssessment(); _tweakActivity.ValueChanged += (_, _) => InvalidateTweakAssessment();
        _tweakPower.ValueChanged += (_, _) => InvalidateTweakAssessment(); _tweakHeat.CheckedChanged += (_, _) => InvalidateTweakAssessment();
        UiTheme.Add(setupBody, choices); UiTheme.Add(setupBody, _tweakHeat);
        _startTweak.Click += async (_, _) => { if (!_preview) await StartTweakScanAsync(); };
        _cancelTweak.Click += (_, _) => { _tweakCancellation?.Cancel(); _tweakProgressText.Text = "Cancel requested. Any recorded changes remain available to restore."; };
        UiTheme.Add(setupBody, Actions(_startTweak, _cancelTweak)); UiTheme.Add(stack, setup);

        var review = Card("Your findings", "Select a finding to see what was observed and what will happen. Advice is kept separate from changes this app can apply.", out var reviewBody);
        _tweakSummary.Text = "Ready to scan. No changes have been made."; _tweakSummary.AutoSize = true; _tweakSummary.ForeColor = UiTheme.Text; _tweakSummary.Font = new Font("Segoe UI", 10, FontStyle.Bold); UiTheme.Add(reviewBody, _tweakSummary);
        _tweakProgressText.Text = "Choose Tweak PC to prepare your optimizations."; _tweakProgressText.AutoSize = true; _tweakProgressText.ForeColor = UiTheme.Muted; UiTheme.Add(reviewBody, _tweakProgressText); UiTheme.Add(reviewBody, _tweakProgress);
        UiTheme.Add(reviewBody, TableHeader([("Finding", 48), ("Action", 52)]));
        _tweakFindings.Dock = DockStyle.Fill; _tweakFindings.Height = 196; _tweakFindings.View = View.Details; _tweakFindings.HeaderStyle = ColumnHeaderStyle.None;
        _tweakFindings.FullRowSelect = true; _tweakFindings.HideSelection = false; _tweakFindings.MultiSelect = false; _tweakFindings.BorderStyle = BorderStyle.None; _tweakFindings.BackColor = UiTheme.Surface; _tweakFindings.ForeColor = UiTheme.Text; _tweakFindings.AccessibleName = "Scan findings";
        _tweakFindings.Columns.Add("Finding", 330); _tweakFindings.Columns.Add("Action", 330);
        _tweakFindings.SizeChanged += (_, _) => { int width = Math.Max(200, _tweakFindings.ClientSize.Width - 5); _tweakFindings.Columns[0].Width = (int)(width * .48); _tweakFindings.Columns[1].Width = width - _tweakFindings.Columns[0].Width; };
        _tweakFindings.SelectedIndexChanged += (_, _) => ShowSelectedFinding(); UiTheme.Add(reviewBody, _tweakFindings);
        _findingDetail.Dock = DockStyle.Fill; _findingDetail.Height = 115; _findingDetail.ReadOnly = true; _findingDetail.BorderStyle = BorderStyle.None; _findingDetail.BackColor = UiTheme.Raised; _findingDetail.ForeColor = UiTheme.Text; _findingDetail.Font = new Font("Segoe UI", 9.5f); _findingDetail.Margin = new Padding(0, 10, 0, 10); _findingDetail.Text = "Evidence and proposed action will appear here."; _findingDetail.AccessibleName = "Finding evidence and proposed action"; UiTheme.Add(reviewBody, _findingDetail);
        _applyTweak.Click += async (_, _) => { if (!_preview) await ApplyTweakAsync(); }; _restoreTweak.Click += async (_, _) => { if (!_preview) await RequestRestoreAsync(false); };
        UiTheme.Add(reviewBody, Actions(_applyTweak, _restoreTweak)); UiTheme.Add(stack, review);
        var tools = Card("More control", "Review the full Tourney profile or inspect an existing frame-time recording. These tools do not start automatically.", out var toolsBody);
        UiTheme.Add(toolsBody, Actions(_analyze, _diagnostics)); UiTheme.Add(stack, tools);
    }

    private AppOptions TweakChoices()
    {
        var options = PlannerDialog.CopyOptions(ReadOptions());
        options.Intent.Goal = _tweakGoal.Value; options.Intent.Activity = _tweakActivity.Value; options.Intent.Power = _tweakPower.Value;
        options.Intent.AllowHigherHeat = _tweakHeat.Checked;
        options.ApplyWindowsProfile = true;
        return options;
    }
    private void InvalidateTweakAssessment()
    {
        if (_loading) return;
        _tweakAssessment = null; _tweakFindings.Items.Clear();
        _tweakSummary.Text = "Choices changed. Choose Tweak PC for a fresh review."; _findingDetail.Text = "A fresh scan is required before applying changed choices.";
        UpdateTweakControls();
    }
    private async Task StartTweakScanAsync()
    {
        if (_preview || _busy || _reviewDialogOpen || _updateInProgress || _engine.IsActive) return;
        var options = TweakChoices(); _tweakAssessment = null;
        using var cancellation = new CancellationTokenSource(); _tweakCancellation = cancellation;
        try
        {
            await RunOperationAsync(async () =>
            {
                _tweakSummary.Text = "Scanning this PC…"; _tweakFindings.Items.Clear(); _tweakProgress.Value = 0;
                try
                {
                    _tweakAssessment = await new TweakWorkflow(_engine).AnalyzeAsync(options, cancellation.Token, TweakProgressReporter());
                    PopulateTweakAssessment(); AppendLog("Tweak scan complete. " + _tweakAssessment.Summary);
                }
                catch (OperationCanceledException) { _tweakSummary.Text = "Scan canceled. No changes were applied."; AppendLog(_tweakSummary.Text); }
                catch (Exception error) { _tweakAssessment = null; _tweakSummary.Text = "Scan could not finish. " + error.Message; _tweakProgressText.Text = "No changes were applied. Choose Tweak PC to try again."; throw; }
            });
        }
        finally { _tweakCancellation = null; if (!IsDisposed) UpdateState(); }
        if (!IsDisposed && !cancellation.IsCancellationRequested && _tweakAssessment?.CanApply == true) await ApplyTweakAsync();
    }
    private IProgress<TweakProgress> TweakProgressReporter() => new Progress<TweakProgress>(progress =>
    {
        if (IsDisposed) return;
        _tweakProgress.Value = Math.Clamp(progress.Percent, 0, 100); _tweakProgressText.Text = progress.Stage + " · " + progress.Detail;
    });
    private void PopulateTweakAssessment()
    {
        var assessment = _tweakAssessment; if (assessment == null) return;
        _tweakSummary.Text = assessment.Summary; _tweakFindings.BeginUpdate(); _tweakFindings.Items.Clear();
        foreach (var finding in assessment.Findings)
        {
            var item = new ListViewItem(finding.Title) { Tag = finding, ForeColor = finding.Attention ? UiTheme.Amber : UiTheme.Text };
            item.SubItems.Add(finding.Automated ? "Available to apply" : finding.Attention ? "Needs your attention" : "Advice / preserved"); _tweakFindings.Items.Add(item);
        }
        _tweakFindings.EndUpdate(); if (_tweakFindings.Items.Count > 0) _tweakFindings.Items[0].Selected = true;
        var facts = assessment.Analysis.Hardware;
        _overviewFacts.Text = facts.Cpu + "\r\n" + string.Join(" · ", facts.Gpus.Select(g => g.Name)) + $"\r\n{facts.RamGiB:0.#} GiB RAM · {facts.OsVisibleLogicalProcessors} logical processors visible to Windows";
        PopulateOptimizationGuide(assessment.Analysis);
        UpdateTweakControls();
    }
    private void ShowSelectedFinding()
    {
        _findingDetail.Text = _tweakFindings.SelectedItems.Count > 0 && _tweakFindings.SelectedItems[0].Tag is TweakFinding finding
            ? "OBSERVED\r\n" + finding.Evidence + "\r\n\r\n" + (finding.Automated ? "PROPOSED CHANGE" : "RECOMMENDATION") + "\r\n" + finding.ProposedAction
            : "Select a finding for its evidence and exact proposed action.";
    }
    private async Task ApplyTweakAsync()
    {
        var assessment = _tweakAssessment;
        if (_preview || _busy || _reviewDialogOpen || _updateInProgress || assessment?.CanApply != true || _engine.IsActive) return;
        using (var review = new TweakApplyDialog(assessment))
        {
            _reviewDialogOpen = true;
            try { if (review.ShowDialog(this) != DialogResult.OK) return; }
            finally { _reviewDialogOpen = false; }
        }
        using var cancellation = new CancellationTokenSource(); _tweakCancellation = cancellation;
        try
        {
            await RunOperationAsync(async () =>
            {
                try
                {
                    var result = await new TweakWorkflow(_engine).ApplyAsync(assessment, cancellation.Token, TweakProgressReporter());
                    _lastTweakResults = result.Results.ToList(); _tweakSummary.Text = result.Summary; AppendLog("Tweak: " + result.Summary);
                    _tweakAssessment = null;
                    if (_engine.IsActive) PopulateRecoveryList();
                }
                catch (OperationCanceledException) { _tweakAssessment = null; _tweakSummary.Text = _engine.IsActive ? "Canceled. Recorded changes remain available to restore." : "Canceled. No active changes remain."; AppendLog(_tweakSummary.Text); }
                catch (Exception error) { _tweakAssessment = null; _tweakSummary.Text = "Apply needs attention. " + error.Message; _tweakProgressText.Text = "A verified Windows restore point is required. If protection is unavailable, enable it in Windows System Protection or run as administrator, then retry."; throw; }
            });
        }
        finally { _tweakCancellation = null; if (!IsDisposed) UpdateState(); }
    }
    private void UpdateTweakControls()
    {
        bool active = VisibleSessionActive;
        _startTweak.Enabled = !UiBusy && !_updateInProgress && !active && !_reviewDialogOpen;
        _applyTweak.Enabled = !UiBusy && !_updateInProgress && !active && _tweakAssessment?.CanApply == true;
        _cancelTweak.Enabled = _busy && _tweakCancellation != null;
        _restoreTweak.Enabled = !UiBusy && active;
        _tweakGoal.Enabled = _tweakActivity.Enabled = _tweakPower.Enabled = _tweakHeat.Enabled = !UiBusy && !active;
    }
    private static string TweakGoalName(OptimizationGoal goal) => goal == OptimizationGoal.Balanced ? "FPS, latency & frame pacing" : PlannerDialog.GoalName(goal);
}

internal sealed class TweakApplyDialog : Form
{
    internal TweakApplyDialog(TweakAssessment assessment)
    {
        Text = "Review your changes"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(760, 590); MinimumSize = new Size(640, 480);
        BackColor = UiTheme.Background; ForeColor = UiTheme.Text; Font = new Font("Segoe UI", 10); Padding = new Padding(24); ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 }; layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); Controls.Add(layout);
        layout.Controls.Add(UiTheme.Label("Review before applying", 21, true), 0, 0);
        var intro = UiTheme.Label("Windows must create and verify a restore point before any change. The listed changes also save their original values for Restore changes. Apps stay open. Apply is blocked while Fortnite or another session is active.", 10, false, UiTheme.Muted); intro.Dock = DockStyle.Fill; layout.Controls.Add(intro, 0, 1);
        var exact = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Font = new Font("Segoe UI", 10), DetectUrls = false,
            Text = string.Join("\r\n\r\n", assessment.Findings.Where(f => f.Automated).Select(f => f.Title + "\r\n" + f.ProposedAction + "\r\nObserved: " + f.Evidence)) };
        layout.Controls.Add(exact, 0, 2);
        var apply = new ThemeButton { Text = "Apply these changes", Primary = true, Width = 210, DialogResult = DialogResult.OK };
        var cancel = new ThemeButton { Text = "Cancel", Width = 110, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) }; actions.Controls.Add(apply); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 3); CancelButton = cancel;
        // Enter does not apply implicitly; consent requires the named button.
    }
}
