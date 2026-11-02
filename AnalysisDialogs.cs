using System.Text.Json;
using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private async Task AnalyzeAsync()
    {
        if (_busy) return;
        var options = _engine.IsActive && _engine.State != null ? PlannerDialog.CopyOptions(_engine.State.Options) : ReadOptions();
        PcAnalysis? report = null;
        await RunOperationAsync(async () =>
        {
            AppendLog("Reading this PC for a goal-driven plan. This is a short background-app sample, not a game benchmark; settings remain unchanged.");
            report = await Task.Run(() => SystemAnalysis.CaptureAsync(options));
        });
        if (IsDisposed || report == null) return;
        // Read-only review must not pause the active app guard or automatic recovery.
        _reviewDialogOpen = true;
        try
        {
            using var dialog = new PlannerDialog(report, options, _engine.GameClientRunning, _engine.IsActive);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedOptions == null || _engine.IsActive) return;
            _reviewedPlan = dialog.Plan;
            ApplyPreferencesToControls(dialog.SelectedOptions);
            _engine.SaveOptions(_preferences);
            AppendLog("Reviewed plan saved: " + PlannerDialog.GoalName(_preferences.Intent.Goal) + ". Tourney Mode remains OFF; no live settings were changed.");
        }
        catch (Exception e) { AppendLog("Plan review needs attention: " + e.Message); }
        finally { _reviewDialogOpen = false; }
        if (!_engine.IsActive) await RefreshAppsAsync();
    }

    private void ShowDiagnostics()
    {
        if (_busy || _preview) return;
        _reviewDialogOpen = true;
        try { using var dialog = new DiagnosticsDialog(_engine.DataDirectory, _preferences.Intent.TargetFps); dialog.ShowDialog(this); }
        finally { _reviewDialogOpen = false; }
    }

    private void ShowFeatureStatus()
    {
        if (_busy) return;
        using var dialog = new Form { Text = "Actual feature results", Width = 920, Height = 560, MinimumSize = new Size(680, 380), StartPosition = FormStartPosition.CenterParent, Font = Font };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        list.Columns.Add("Feature", 200); list.Columns.Add("State", 110); list.Columns.Add("Evidence / detail", 560);
        var note = new Label { Dock = DockStyle.Top, Height = 56, Padding = new Padding(8), Text = "These are the engine's recorded outcomes. Applied means the requested operation was verified where supported; it does not establish an FPS or latency gain." };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        var refresh = new Button { Text = "Refresh results", AutoSize = true };
        void Populate()
        {
            list.Items.Clear();
            try
            {
                var results = _engine.State?.Features?.ToArray() ?? [];
                foreach (var result in results)
                {
                    var item = new ListViewItem(result.Name); item.SubItems.Add(result.State.ToString()); item.SubItems.Add(result.Detail); list.Items.Add(item);
                    if (result.State is FeatureState.Failed or FeatureState.Denied) item.ForeColor = Color.DarkRed;
                }
                if (results.Length == 0) list.Items.Add(new ListViewItem("No recorded results") { SubItems = { "", _engine.IsActive ? "This session has no feature results yet. Review recovery and the log." : "Tourney Mode is OFF. Review a plan before the next activation." } });
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { list.Items.Add("Results are being updated. Refresh after the current operation."); }
        }
        refresh.Click += (_, _) => Populate();
        controls.Controls.Add(close); controls.Controls.Add(refresh); dialog.Controls.Add(list); dialog.Controls.Add(note); dialog.Controls.Add(controls); dialog.CancelButton = close; Populate(); dialog.ShowDialog(this);
    }

    private async Task EditKeepAppsAsync()
    {
        if (_busy || _engine.IsActive) return;
        using var dialog = new Form { Text = "Always keep these apps running", Width = 800, Height = 400,
            MinimumSize = new Size(600, 320), StartPosition = FormStartPosition.CenterParent, Font = Font };
        var list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        list.Items.AddRange(_preferences.ProtectedAppPaths.Cast<object>().ToArray());
        var label = new Label { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8),
            Text = "Choose existing program executables to keep, including their child processes. This overrides Codex shutdown. Discord and required system components stay protected." };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(5) };
        var okay = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var add = new Button { Text = "Add app…", AutoSize = true };
        var remove = new Button { Text = "Remove selected", AutoSize = true };
        add.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Programs|*.exe", CheckFileExists = true, Multiselect = true, Title = "Choose programs to preserve" };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            var paths = ConnectionProtection.NormalizePaths(list.Items.Cast<string>().Concat(picker.FileNames));
            list.Items.Clear(); list.Items.AddRange(paths.Cast<object>().ToArray());
        };
        remove.Click += (_, _) => { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); };
        foreach (var button in new[] { okay, cancel, remove, add }) actions.Controls.Add(button);
        dialog.Controls.Add(list); dialog.Controls.Add(label); dialog.Controls.Add(actions);
        dialog.AcceptButton = okay; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _preferences.ProtectedAppPaths = ConnectionProtection.NormalizePaths(list.Items.Cast<string>());
        _preferences.ReviewedPlanFingerprint = ""; _reviewedPlan = null;
        InvalidateTweakAssessment();
        _engine.SaveOptions(ReadOptions());
        await RefreshAppsAsync();
    }
}
