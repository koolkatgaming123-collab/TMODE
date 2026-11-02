using System.Drawing;
using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private readonly ThemeButton _internetStart = new() { Text = "Start quick test", Primary = true, Width = 190 };
    private readonly ThemeButton _internetCancel = new() { Text = "Cancel & restore", Width = 180 };
    private readonly ThemeButton _internetApply = new() { Text = "Apply best tested", Primary = true, Width = 190 };
    private readonly ThemeButton _internetRestore = new() { Text = "Restore originals / finish", Width = 230 };
    private readonly ThemeSwitch _internetLoaded = new() { Text = "Include a bounded network-load test", AccessibleName = "Include a bounded network-load test" };
    private readonly ThemeChoice<InternetTestMode> _internetMode = new() { Display = mode => mode == InternetTestMode.Quick ? "Quick · up to 4 candidates" : "Thorough · up to 12 candidates", AccessibleName = "Internet test mode", Width = 250 };
    private readonly ThemeSwitch _internetAllowProvisional = new() { Text = "Allow provisional ping and stability recommendation with local processing unmeasured", AccessibleName = "Allow provisional ping and stability recommendation with local processing unmeasured" };
    private readonly Label _internetMessage = new();
    private readonly Label _internetAdapter = new();
    private readonly Label _internetProgressText = new();
    private readonly ProgressBar _internetProgress = new() { Dock = DockStyle.Top, Height = 5, Margin = new Padding(0, 12, 0, 12) };
    private readonly ListView _internetResults = new();
    private readonly RichTextBox _internetDetail = new();
    private CancellationTokenSource? _internetCancellation;
    private InternetTestReport? _internetReport;
    private readonly Dictionary<string, string> _internetEvidence = new(StringComparer.Ordinal);

    private void BuildInternetPage(Panel page)
    {
        var stack = UiTheme.Stack(); page.Controls.Add(stack);
        var setup = Card("Measure this connection before changing it", "Compare a small set of configurations supported by the detected wired adapter and CPU. A quick adaptive test finds the best tested candidate, not an absolute optimum. No test starts until you choose Start.", out var setupBody);
        _internetAdapter.Text = "Adapter and capability details appear when you explicitly start a test."; _internetAdapter.AutoSize = true; _internetAdapter.ForeColor = UiTheme.Muted; UiTheme.Add(setupBody, _internetAdapter);
        UiTheme.Add(setupBody, Actions(_internetMode));
        UiTheme.Add(setupBody, Actions(_internetLoaded, UiTheme.Label("Include bounded HTTPS load · 1 MiB per period, at most 32 MiB per run", 9, false, UiTheme.Muted)));
        _internetMode.ValueChanged += (_, _) => InternetChoicesChanged(); _internetLoaded.CheckedChanged += (_, _) => InternetChoicesChanged();
        UiTheme.Add(setupBody, Paragraph("Adapter changes can briefly disconnect Ethernet. A verified Windows restore point and the shared recovery record are required before trial writes. Close Fortnite and finish any current optimization session first."));
        _internetStart.Enabled = false; _internetCancel.Enabled = false;
        _internetStart.Click += async (_, _) => { if (!_preview) await StartInternetTestAsync(); };
        _internetCancel.Click += (_, _) => { _internetCancellation?.Cancel(); _internetMessage.Text = "Cancel requested. Waiting for originals to restore; keep this app open."; };
        UiTheme.Add(setupBody, Actions(_internetStart, _internetCancel)); UiTheme.Add(stack, setup);

        var progress = Card("Test progress", "Original settings remain in the recovery record throughout testing and can be restored if a trial cannot finish.", out var progressBody);
        _internetMessage.Text = "Idle · no network tests started."; _internetMessage.AutoSize = true; _internetMessage.ForeColor = UiTheme.Text; _internetMessage.Font = new Font("Segoe UI", 10, FontStyle.Bold); UiTheme.Add(progressBody, _internetMessage);
        _internetProgressText.Text = "Quick test · bounded candidates · optional load stays off unless selected"; _internetProgressText.AutoSize = true; _internetProgressText.ForeColor = UiTheme.Muted; UiTheme.Add(progressBody, _internetProgressText); UiTheme.Add(progressBody, _internetProgress); UiTheme.Add(stack, progress);

        var results = Card("Measured comparison", "Ping, valid traced local processing, and stability from jitter/loss have equal weight. CPU use and DPC context do not establish packet-processing latency. An incomplete result stays provisional and cannot be applied as an all-goal winner.", out var resultsBody);
        UiTheme.Add(resultsBody, TableHeader([("Candidate", 24), ("Gateway RTT", 14), ("Remote RTT", 14), ("Processing", 16), ("Jitter / loss", 18), ("Result", 14)]));
        _internetResults.Dock = DockStyle.Fill; _internetResults.Height = 215; _internetResults.View = View.Details; _internetResults.HeaderStyle = ColumnHeaderStyle.None; _internetResults.FullRowSelect = true; _internetResults.MultiSelect = false; _internetResults.HideSelection = false; _internetResults.BorderStyle = BorderStyle.None; _internetResults.BackColor = UiTheme.Surface; _internetResults.ForeColor = UiTheme.Text; _internetResults.AccessibleName = "Internet candidate measurements";
        foreach (string title in new[] { "Candidate", "Gateway RTT", "Remote RTT", "Processing", "Jitter / loss", "Result" }) _internetResults.Columns.Add(title, 120);
        _internetResults.SizeChanged += (_, _) =>
        {
            int width = Math.Max(500, _internetResults.ClientSize.Width - 5); double[] weights = [.24, .14, .14, .16, .18, .14]; int used = 0;
            for (int i = 0; i < weights.Length; i++) { int columnWidth = i == weights.Length - 1 ? width - used : (int)(width * weights[i]); _internetResults.Columns[i].Width = columnWidth; used += columnWidth; }
        };
        _internetResults.SelectedIndexChanged += (_, _) =>
        {
            if (_internetResults.SelectedItems.Count > 0 && _internetResults.SelectedItems[0].Tag is string id && _internetEvidence.TryGetValue(id, out string? evidence)) _internetDetail.Text = evidence;
        };
        UiTheme.Add(resultsBody, _internetResults);
        _internetDetail.Dock = DockStyle.Fill; _internetDetail.Height = 145; _internetDetail.ReadOnly = true; _internetDetail.BorderStyle = BorderStyle.None; _internetDetail.BackColor = UiTheme.Raised; _internetDetail.ForeColor = UiTheme.Text; _internetDetail.Font = new Font("Segoe UI", 9.5f); _internetDetail.DetectUrls = false; _internetDetail.Margin = new Padding(0, 10, 0, 10); _internetDetail.Text = "Select a candidate after testing for full measurements, sample counts and limitations."; _internetDetail.AccessibleName = "Selected internet candidate evidence"; UiTheme.Add(resultsBody, _internetDetail);
        _internetApply.Enabled = false; _internetRestore.Enabled = false; UiTheme.Add(resultsBody, Actions(_internetApply, _internetRestore)); UiTheme.Add(stack, results);
        _internetApply.Click += async (_, _) => { if (!_preview) await ApplyInternetResultAsync(); };
        _internetRestore.Click += async (_, _) => { if (!_preview) await RestoreInternetOriginalsAsync(); };
        var provisional = Card("Experimental · incomplete processing evidence", "Off by default. You may explicitly review a provisional ping/stability recommendation when local processing is unmeasured. This does not satisfy all three goals and cannot establish a processing-latency improvement.", out var provisionalBody);
        UiTheme.Add(provisionalBody, Actions(_internetAllowProvisional, UiTheme.Label("Allow provisional recommendation · local processing unmeasured", 9, false, UiTheme.Amber)));
        _internetAllowProvisional.CheckedChanged += (_, _) => { if (!_loading) UpdateInternetControls(); }; UiTheme.Add(stack, provisional);
    }
    private void UpdateInternetControls()
    {
        bool idle = !_busy && !_updateInProgress && !_reviewDialogOpen && (_preview || !_engine.InternetBusy);
        bool internetSession = VisibleSession?.Workflow == "Internet";
        _internetLoaded.Enabled = _internetMode.Enabled = idle && !VisibleSessionActive;
        _internetStart.Text = _internetMode.Value == InternetTestMode.Quick ? "Start quick test" : "Start thorough test";
        _internetStart.Enabled = idle && !VisibleSessionActive;
        _internetApply.Enabled = idle && internetSession && VisibleSession?.RestorePoint?.Verified == true && VisibleSession?.InternetCalibration?.Restored == true && InternetReportMatchesChoices && (HasQualifiedInternetChoice || HasAllowedProvisionalInternetChoice);
        _internetApply.Text = HasQualifiedInternetChoice ? "Apply best tested" : HasAllowedProvisionalInternetChoice ? "Review provisional" : "Apply best tested";
        _internetRestore.Enabled = idle && internetSession;
        _internetAllowProvisional.Enabled = idle && internetSession && VisibleSession?.InternetCalibration?.Restored == true && _internetReport is { Completed: true, Canceled: false, RollbackVerified: true, ProcessingMeasured: false, PingMeasured: true, StabilityMeasured: true, ProvisionalWinner.IsBaseline: false };
        _internetCancel.Enabled = _busy && _internetCancellation != null;
        if (internetSession && _internetReport == null && !UiBusy)
        {
            _internetMessage.Text = "A saved network session remains recorded. Use Restore originals / finish before starting another test.";
            _internetProgressText.Text = VisibleSession?.InternetCalibration?.Phase ?? VisibleSession?.Phase ?? "Recovery pending";
            if (VisibleSession?.InternetCalibration is { } saved) _internetAdapter.Text = "Saved session · " + saved.Baseline.Name + " · driver " + saved.Baseline.DriverVersion;
        }
    }
    private void InternetChoicesChanged()
    {
        if (_loading) return;
        if (_internetReport != null) _internetMessage.Text = "Test options changed. Run a fresh comparison before applying a result.";
        UpdateInternetControls();
    }
    private bool InternetReportMatchesChoices => _internetReport != null && _internetReport.Options.Mode == _internetMode.Value && _internetReport.Options.EnableHttpsLoad == _internetLoaded.Checked;
    private bool HasQualifiedInternetChoice => _internetReport is { CanApplyBest: true, HasQualifiedWinner: true, AllObjectivesMeasured: true, Completed: true, Canceled: false, RollbackVerified: true, Winner.IsBaseline: false };
    private bool HasAllowedProvisionalInternetChoice => _internetAllowProvisional.Checked && _internetReport is { HasQualifiedWinner: false, Completed: true, Canceled: false, RollbackVerified: true, ProcessingMeasured: false, PingMeasured: true, StabilityMeasured: true, ProvisionalWinner.IsBaseline: false };
}
