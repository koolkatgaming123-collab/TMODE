using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private readonly Dictionary<string, CheckBox> _stripChoices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Label> _stripGroupStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ThemeButton> _stripRepairButtons = new(StringComparer.Ordinal);
    private readonly ThemeButton _stripReview = new() { Text = "Review selection", Width = 175 };
    private readonly ThemeButton _stripSelectAll = new() { Text = "Select all features", Width = 175 };
    private readonly ThemeButton _stripApply = new() { Text = "Strip selected features", Primary = true, Width = 220 };
    private readonly ThemeButton _stripRepairAll = new() { Text = "Repair all", Width = 130 };
    private readonly ThemeButton _stripCancel = new() { Text = "Cancel", Width = 100 };
    private readonly Label _stripMessage = new();
    private readonly RichTextBox _stripDetails = new();
    private readonly ProgressBar _stripProgress = new() { Dock = DockStyle.Top, Height = 5, Margin = new Padding(0, 10, 0, 12) };
    private StripBackup? _stripReviewedBackup;
    private StripBackup? _stripLastBackup;
    private CancellationTokenSource? _stripCancellation;
    private bool CanBeginStrip => _preview
        ? VisibleSession == null || VisibleSession is { Phase: "Active", Workflow: "Tweak", RestorePoint.Verified: true } session && (session.Strip == null || session.Strip.FullyRestored)
        : _engine.CanStartStrip;

    private void BuildStripPage(Panel page)
    {
        var stack = UiTheme.Stack(); page.Controls.Add(stack);
        var intro = Card("Strip optional Windows features", "These changes can break features you use. Choose only what you can do without, review the exact services, then confirm. Windows must create and verify a restore point first. Nothing is uninstalled.", out var introBody);
        intro.Fill = Color.FromArgb(43, 34, 24);
        _stripMessage.Text = "Choose features to review. Recorded changes and repair options appear below."; _stripMessage.AutoSize = true; _stripMessage.ForeColor = UiTheme.Amber; _stripMessage.Font = new Font("Segoe UI", 10, FontStyle.Bold); UiTheme.Add(introBody, _stripMessage);
        _stripReview.Click += async (_, _) => { if (!_preview) await ReviewStripAsync(); };
        _stripSelectAll.Click += (_, _) => { foreach (var choice in _stripChoices.Values) choice.Checked = true; };
        _stripApply.Click += async (_, _) => { if (!_preview) await ApplyStripAsync(); };
        _stripRepairAll.Click += async (_, _) => { if (!_preview) await RepairStripGroupAsync(null); };
        _stripCancel.Click += (_, _) => _stripCancellation?.Cancel();
        UiTheme.Add(introBody, Actions(_stripSelectAll, _stripReview, _stripApply, _stripRepairAll, _stripCancel)); UiTheme.Add(introBody, _stripProgress);
        UiTheme.Add(introBody, Paragraph("Gaming, security, drivers, network connectivity, audio and input remain protected. Disabling optional features does not establish an FPS gain.")); UiTheme.Add(stack, intro);

        var evidence = Card("Your reviewed changes", "Review selection reads the current service state. Applying rechecks safety and saves exact originals for repair.", out var evidenceBody);
        _stripDetails.Dock = DockStyle.Fill; _stripDetails.Height = 175; _stripDetails.ReadOnly = true; _stripDetails.BorderStyle = BorderStyle.None; _stripDetails.BackColor = UiTheme.Surface; _stripDetails.ForeColor = UiTheme.Muted; _stripDetails.Font = new Font("Segoe UI", 9.5f); _stripDetails.DetectUrls = false; _stripDetails.Text = "Select one or more optional feature groups below, then choose Review selection."; _stripDetails.AccessibleName = "Selected Windows feature changes"; UiTheme.Add(evidenceBody, _stripDetails); UiTheme.Add(stack, evidence);
        foreach (var feature in WindowsStrip.Features)
        {
            var card = Card(feature.Title, feature.Description, out var body);
            var select = new CheckBox { Text = "Select " + feature.Title, AutoSize = true, ForeColor = UiTheme.Text, Margin = new Padding(0, 0, 0, 10), AccessibleName = "Select optional " + feature.Title };
            select.CheckedChanged += (_, _) => InvalidateStripReview(); _stripChoices.Add(feature.Id, select); UiTheme.Add(body, select);
            var loss = Paragraph("What stops working: " + feature.FunctionalityLost); loss.ForeColor = UiTheme.Amber; UiTheme.Add(body, loss);
            var status = UiTheme.Label("Not changed by this app", 9, false, UiTheme.Muted); _stripGroupStates.Add(feature.Id, status); UiTheme.Add(body, status);
            var fix = new ThemeButton { Text = StripRepairLabel(feature.Id), Width = 190 }; string groupId = feature.Id;
            fix.Click += async (_, _) => { if (!_preview) await RepairStripGroupAsync(groupId); }; _stripRepairButtons.Add(feature.Id, fix); UiTheme.Add(body, Actions(fix)); UiTheme.Add(stack, card);
        }
    }
    private static string StripRepairLabel(string id) => id switch
    {
        "printing" => "Fix Printing", "search" => "Fix Search", "xbox-online" => "Fix Xbox services", "maps" => "Fix Maps",
        "media-sharing" => "Fix Media sharing", "discovery" => "Fix Discovery", "diagnostics" => "Fix Diagnostics", "scanning" => "Fix Scanning", _ => "Repair feature"
    };
    private void InvalidateStripReview()
    {
        if (_loading) return;
        _stripReviewedBackup = null;
        int count = _stripChoices.Values.Count(check => check.Checked);
        _stripMessage.Text = count == 0 ? "Nothing selected. No new changes requested." : count + " feature groups selected. Review the exact changes before applying.";
        _stripDetails.Text = "Your selection changed. Choose Review selection before applying."; UpdateStripControls();
    }
    private bool StripWriteBlocked()
    {
        if (!_engine.GameClientRunning) return false;
        _stripMessage.Text = "Close Fortnite before changing or repairing Windows features."; return true;
    }
    private async Task ReviewStripAsync()
    {
        if (_preview || _busy || _reviewDialogOpen || _updateInProgress || !CanBeginStrip || StripWriteBlocked()) return;
        var ids = _stripChoices.Where(pair => pair.Value.Checked).Select(pair => pair.Key).ToArray(); if (ids.Length == 0) return;
        _stripReviewedBackup = null;
        await RunOperationAsync(async () =>
        {
            try
            {
                _stripMessage.Text = "Reading selected Windows features…";
                _stripReviewedBackup = await _engine.CaptureStripAsync(ids);
                _stripDetails.Text = FormatStripReview(_stripReviewedBackup);
                _stripMessage.Text = "Selection reviewed. Read the feature losses and confirm before applying.";
            }
            catch (Exception error) { _stripReviewedBackup = null; _stripMessage.Text = "Review could not finish. " + error.Message; throw; }
        });
    }
    private static string FormatStripReview(StripBackup backup)
    {
        var text = new StringBuilder();
        foreach (string id in backup.SelectedGroups)
        {
            var feature = WindowsStrip.Features.FirstOrDefault(feature => feature.Id == id); if (feature == null) continue;
            text.AppendLine(feature.Title.ToUpperInvariant()).Append("What stops working: ").AppendLine(feature.FunctionalityLost);
            foreach (var service in backup.Services.Where(service => service.GroupId == id))
            {
                text.Append("• ").Append(service.Name).Append(": ");
                if (!service.Existed) text.Append("not installed; no change");
                else if (!string.IsNullOrWhiteSpace(service.Issue)) text.Append("needs attention — ").Append(service.Issue);
                else if (service.OriginalStartType == 4 && !service.WasRunning) text.Append("already stopped and disabled; unchanged");
                else text.Append(service.WasRunning ? "running" : "stopped").Append("; ").Append(StripStartupName(service)).Append(" → ").Append(service.WasRunning ? "stop and disable" : "disable startup").Append("; save original for repair");
                text.AppendLine();
            }
            foreach (var status in backup.Groups.Where(group => group.Id == id && !string.IsNullOrWhiteSpace(group.Issue))) text.Append("Review note: ").AppendLine(status.Issue);
            text.AppendLine();
        }
        return text.ToString();
    }
    private static string StripStartupName(StripServiceBackup service) => service.OriginalStartType switch
    {
        2 => service.OriginalDelayedAutoStart ? "automatic (delayed start)" : "automatic start", 3 => "manual start", 4 => "disabled startup",
        0 => "boot startup", 1 => "system startup", _ => "startup type " + service.OriginalStartType
    };
    private async Task ApplyStripAsync()
    {
        var reviewed = _stripReviewedBackup;
        if (_preview || _busy || _reviewDialogOpen || _updateInProgress || !CanBeginStrip || reviewed == null || StripWriteBlocked()) return;
        using (var review = new StripConsentDialog(FormatStripReview(reviewed)))
        {
            _reviewDialogOpen = true;
            try { if (review.ShowDialog(this) != DialogResult.OK) return; }
            finally { _reviewDialogOpen = false; }
        }
        using var cancellation = new CancellationTokenSource(); _stripCancellation = cancellation;
        try
        {
            await RunOperationAsync(async () =>
            {
                try
                {
                    _stripMessage.Text = "Preparing restore point and selected features…";
                    var progress = StripProgressReporter();
                    await Task.Run(() => _engine.EnableStripAsync(reviewed, cancellation.Token, progress));
                    _stripLastBackup = _engine.State?.Strip;
                    _stripMessage.Text = "Operation finished. Review each feature's recorded result below.";
                    AppendLog("Windows feature operation finished. Review recorded status; an FPS gain has not been measured.");
                }
                catch (OperationCanceledException) { _stripMessage.Text = _engine.IsActive ? "Canceled. Use Repair all for recorded changes." : "Canceled. No active Windows feature changes remain."; }
                catch (Exception error) { _stripMessage.Text = "Changes need attention. " + error.Message; _stripDetails.Text = "A verified restore point is required before changing Windows. If protection is unavailable, enable it in Windows System Protection or run as administrator, then retry. Recorded changes remain repairable."; throw; }
                finally { _stripReviewedBackup = null; _stripLastBackup = _engine.State?.Strip ?? _stripLastBackup; }
            });
        }
        finally { _stripCancellation = null; if (!IsDisposed) UpdateState(); }
    }
    private IProgress<TweakProgress> StripProgressReporter() => new Progress<TweakProgress>(progress =>
    {
        if (IsDisposed) return;
        _stripProgress.Value = Math.Clamp(progress.Percent, 0, 100); _stripMessage.Text = progress.Stage + " · " + progress.Detail;
    });
    private async Task RepairStripGroupAsync(string? id)
    {
        if (_preview || _busy || _reviewDialogOpen || _updateInProgress || _engine.State?.Strip == null || StripWriteBlocked()) return;
        using var cancellation = new CancellationTokenSource(); _stripCancellation = cancellation;
        try
        {
            await RunOperationAsync(async () =>
            {
                try
                {
                    _stripLastBackup = _engine.State!.Strip;
                    _stripMessage.Text = id == null ? "Repairing all recorded Windows features…" : "Repairing selected feature…";
                    await Task.Run(() => _engine.RepairStripAsync(id, cancellation.Token));
                    _stripMessage.Text = "Repair attempt finished. Review the recorded status for each feature.";
                }
                catch (OperationCanceledException) { _stripMessage.Text = "Repair canceled. Unfinished items remain recorded for another attempt."; }
                catch (Exception error) { _stripMessage.Text = "Repair needs attention. " + error.Message; throw; }
            });
        }
        finally { _stripCancellation = null; if (!IsDisposed) UpdateState(); }
    }
    private void UpdateStripControls()
    {
        bool idle = !UiBusy && !_updateInProgress && !_reviewDialogOpen;
        var backup = VisibleSession?.Strip ?? _stripLastBackup;
        bool canBegin = CanBeginStrip;
        _stripReview.Enabled = idle && canBegin && _stripChoices.Values.Any(check => check.Checked);
        _stripSelectAll.Enabled = idle && canBegin;
        _stripApply.Enabled = idle && canBegin && _stripReviewedBackup?.Services.Any(service => service.Existed && string.IsNullOrWhiteSpace(service.Issue)) == true;
        _stripRepairAll.Enabled = idle && VisibleSession?.Strip?.HasChanges == true;
        _stripCancel.Enabled = _busy && _stripCancellation != null;
        foreach (var pair in _stripChoices) pair.Value.Enabled = idle && canBegin;
        foreach (var pair in _stripGroupStates)
        {
            var state = backup?.Groups.FirstOrDefault(group => group.Id == pair.Key);
            pair.Value.Text = state == null ? "Not changed by this app" : state.State + (string.IsNullOrWhiteSpace(state.Issue) ? "" : " · " + state.Issue);
            _stripRepairButtons[pair.Key].Enabled = idle && VisibleSession?.Strip?.Services.Any(service => service.GroupId == pair.Key && service.ApplyAttempted && !service.Restored) == true;
        }
    }
}

internal sealed class StripConsentDialog : Form
{
    private readonly CheckBox _understand = new() { Text = "I understand these features may stop working until I repair them.", AutoSize = true, ForeColor = UiTheme.Amber };
    private readonly ThemeButton _apply = new() { Text = "Disable these features", Primary = true, Enabled = false, Width = 225, DialogResult = DialogResult.OK };
    internal StripConsentDialog(string exactChanges)
    {
        Text = "Confirm Windows feature removal"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(800, 640); MinimumSize = new Size(680, 520); ShowInTaskbar = false;
        BackColor = UiTheme.Background; ForeColor = UiTheme.Text; Font = new Font("Segoe UI", 10); Padding = new Padding(24);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 }; layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); Controls.Add(layout);
        layout.Controls.Add(UiTheme.Label("These Windows features will stop working", 18, true, UiTheme.Amber), 0, 0);
        var note = UiTheme.Label("A Windows restore point must be created and verified before any change. Services are stopped and disabled, not uninstalled. Use Repair all or a feature's Fix button to restore its recorded original state.", 10, false, UiTheme.Muted); note.Dock = DockStyle.Fill; layout.Controls.Add(note, 0, 1);
        var exact = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BorderStyle = BorderStyle.None, BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Font = new Font("Segoe UI", 10), Text = exactChanges }; layout.Controls.Add(exact, 0, 2);
        _understand.Dock = DockStyle.Fill; _understand.CheckedChanged += (_, _) => _apply.Enabled = _understand.Checked; layout.Controls.Add(_understand, 0, 3);
        var cancel = new ThemeButton { Text = "Cancel", Width = 110, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) }; actions.Controls.Add(_apply); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 4); CancelButton = cancel;
    }
}
