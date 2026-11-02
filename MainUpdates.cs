using System.Drawing;
using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private readonly ThemeButton _updateAction = new() { Text = "Check for updates", Primary = true, Width = 220 };
    private readonly ThemeButton _updateRecheck = new() { Text = "Check again", Width = 135 };
    private readonly ThemeButton _updateCancel = new() { Text = "Cancel download", Width = 170 };
    private readonly ThemeButton _thirdPartyLicenses = new() { Text = "Third-party licenses", Width = 220 };
    private readonly Label _updateMessage = new();
    private readonly Label _updateTransfer = new();
    private readonly RichTextBox _releaseNotes = new();
    private readonly ProgressBar _updateProgress = new() { Dock = DockStyle.Top, Height = 5, Margin = new Padding(0, 12, 0, 12) };
    private AppUpdater? _updater;
    private UpdateCandidate? _updateCandidate;
    private StagedUpdate? _stagedUpdate;
    private CancellationTokenSource? _updateCancellation;
    private bool _updateInProgress;
    private bool _startupUpdateCheckStarted;

    private void BuildUpdatesPage(Panel page)
    {
        var stack = UiTheme.Stack(); page.Controls.Add(stack);
        var current = Card("Tourney Mode " + ReleaseIdentity.Version, "Stable release · Windows x64", out var currentBody);
        _updateMessage.Text = "Ready to check for a newer release."; _updateMessage.AutoSize = true; _updateMessage.ForeColor = UiTheme.Text; _updateMessage.Font = new Font("Segoe UI", 11, FontStyle.Bold); UiTheme.Add(currentBody, _updateMessage);
        _updateTransfer.Text = "Checks are automatic while idle. Downloads and installs are always your choice."; _updateTransfer.AutoSize = true; _updateTransfer.ForeColor = UiTheme.Muted; UiTheme.Add(currentBody, _updateTransfer); UiTheme.Add(currentBody, _updateProgress);
        _updateAction.Click += async (_, _) => { if (!_preview) await UpdateActionAsync(); };
        _updateRecheck.Click += async (_, _) => { if (!_preview) await CheckUpdatesAsync(false); };
        _updateCancel.Click += (_, _) => _updateCancellation?.Cancel();
        // Keep all three states in explicit cells, including currently hidden buttons.
        // Hidden FlowLayout children can retain obsolete wrapped positions in previews/re-entry.
        var updateActions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 42,
            ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 8, 0, 0), BackColor = Color.Transparent };
        updateActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230)); updateActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        updateActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); updateActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        updateActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actionButtons = new[] { _updateAction, _updateRecheck, _updateCancel };
        for (int column = 0; column < actionButtons.Length; column++)
        { actionButtons[column].Dock = DockStyle.Fill; updateActions.Controls.Add(actionButtons[column], column, 0); }
        UiTheme.Add(currentBody, updateActions); UiTheme.Add(stack, current);
        var notes = Card("Release notes", "Only verified release details appear here.", out var notesBody);
        _releaseNotes.Dock = DockStyle.Fill; _releaseNotes.Height = 280; _releaseNotes.ReadOnly = true; _releaseNotes.BorderStyle = BorderStyle.None; _releaseNotes.DetectUrls = false; _releaseNotes.BackColor = UiTheme.Surface; _releaseNotes.ForeColor = UiTheme.Muted; _releaseNotes.Font = new Font("Segoe UI", 10); _releaseNotes.Text = "Check for updates to see release information."; _releaseNotes.AccessibleName = "Verified release notes"; UiTheme.Add(notesBody, _releaseNotes); UiTheme.Add(stack, notes);
        var trust = Card("An update you control", "Each release is verified against its signed release information and file hash. Installation waits until Fortnite is closed and no optimization or recovery session is active. You confirm before this app closes and restarts.", out var trustBody);
        _thirdPartyLicenses.Click += (_, _) => { if (!_preview) ShowThirdPartyLicenses(); };
        UiTheme.Add(trustBody, Actions(_thirdPartyLicenses)); UiTheme.Add(stack, trust);
    }
    private void ShowThirdPartyLicenses()
    {
        if (_preview || UiBusy || _reviewDialogOpen) return;
        _reviewDialogOpen = true;
        try
        {
            using var dialog = CreateThirdPartyLicensesDialog();
            dialog.ShowDialog(this);
        }
        catch (Exception error) { _updateMessage.Text = "Bundled licenses could not be read. " + error.Message; AppendLog(_updateMessage.Text); }
        finally { _reviewDialogOpen = false; }
    }
    internal static Form CreateThirdPartyLicensesDialog()
    {
        var dialog = new Form { Text = "Third-party licenses", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(860, 650), MinimumSize = new Size(620, 430), ShowInTaskbar = false,
            BackColor = UiTheme.Background, ForeColor = UiTheme.Text, Font = new Font("Segoe UI", 10), Padding = new Padding(22) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); dialog.Controls.Add(layout);
        layout.Controls.Add(UiTheme.Label("Third-party licenses", 19, true), 0, 0);
        var text = new RichTextBox { Name = "ThirdPartyLicenseText", Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.ForcedVertical, BorderStyle = BorderStyle.None,
            BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Font = new Font("Segoe UI", 9.5f), Text = ThirdPartyNotices.ReadAll(), AccessibleName = "Bundled third-party license texts" };
        layout.Controls.Add(text, 0, 1);
        var close = new ThemeButton { Text = "Close", Width = 120, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 0) };
        actions.Controls.Add(close); layout.Controls.Add(actions, 0, 2); dialog.CancelButton = close;
        return dialog;
    }
    private AppUpdater Updater()
    {
        if (_preview) throw new InvalidOperationException("Synthetic previews cannot use the updater.");
        if (_updater != null) return _updater;
        _updater = AppUpdateConfiguration.CreateUpdater(_engine);
        _updater.Progress += progress =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;
                    _updateTransfer.Text = progress.Message;
                    _updateProgress.Value = (int)Math.Clamp(progress.Percent ?? 0, 0, 100);
                }));
            }
            catch (InvalidOperationException) { }
        };
        return _updater;
    }
    private async Task StartupUpdateCheckAsync()
    {
        if (_preview || _startupUpdateCheckStarted) return;
        _startupUpdateCheckStarted = true;
        if (_engine.IsActive || _engine.GameClientRunning)
        {
            _updateTransfer.Text = "Automatic check deferred while Fortnite or a session is active. Check again when ready.";
            return;
        }
        await CheckUpdatesAsync(true);
    }
    private async Task CheckUpdatesAsync(bool automatic)
    {
        if (_preview || _updateInProgress || _busy || _reviewDialogOpen) return;
        if (_engine.IsActive || _engine.GameClientRunning)
        {
            _updateMessage.Text = "Updates are paused while Fortnite or a session is active."; return;
        }
        _updateInProgress = true; UpdateState();
        try
        {
            _updateMessage.Text = "Checking for updates…";
            var result = await Updater().CheckAsync();
            if (IsDisposed) return;
            _updateCandidate = result.Candidate;
            if (_stagedUpdate?.Version != _updateCandidate?.Version) _stagedUpdate = null;
            _updateMessage.Text = result.Message;
            _releaseNotes.Text = result.Candidate?.Notes ?? (result.Status == UpdateCheckStatus.UpToDate ? "You have the current verified release." : "Release notes are unavailable until a release is verified.");
            _navigation["Updates"].Text = result.IsAvailable ? "Updates  •" : "Updates";
            AppendLog((automatic ? "Automatic update check: " : "Update check: ") + result.Message);
        }
        catch (Exception error) { if (!IsDisposed) { _updateMessage.Text = "Update check could not finish. " + error.Message; AppendLog(_updateMessage.Text); } }
        finally { _updateInProgress = false; if (!IsDisposed) UpdateState(); }
    }
    private bool UpdateBlocked() => _engine.IsActive || _engine.GameClientRunning;
    private async Task UpdateActionAsync()
    {
        if (_preview || _busy || _updateInProgress || _reviewDialogOpen) return;
        if (_updateCandidate == null) { await CheckUpdatesAsync(false); return; }
        if (UpdateBlocked()) { _updateMessage.Text = "Close Fortnite and restore the current session before updating."; return; }
        if (_stagedUpdate == null)
        {
            using var cancellation = new CancellationTokenSource(); _updateCancellation = cancellation;
            _updateInProgress = true;
            try
            {
                await RunOperationAsync(async () =>
                {
                    try
                    {
                        _stagedUpdate = await Updater().StageAsync(_updateCandidate, UpdateBlocked, cancellation.Token);
                        _updateMessage.Text = "Tourney Mode " + _stagedUpdate.Version + " is downloaded and verified.";
                        _updateTransfer.Text = "Ready to install when you choose. This app will restart."; AppendLog(_updateMessage.Text);
                    }
                    catch (OperationCanceledException) { _updateMessage.Text = "Download canceled. Your current version is unchanged."; }
                    catch (Exception error) { _updateMessage.Text = "Download could not finish. " + error.Message; AppendLog(_updateMessage.Text); }
                });
            }
            finally { _updateCancellation = null; _updateInProgress = false; if (!IsDisposed) UpdateState(); }
            return;
        }
        if (MessageBox.Show(this, "Install Tourney Mode " + _stagedUpdate.Version + " and restart this app?\r\n\r\nFortnite must remain closed and all session changes must be restored. The installer preserves a backup of the current executable.", "Install and restart", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _updateInProgress = true;
        try
        {
            await RunOperationAsync(async () =>
            {
                if (UpdateBlocked()) { _updateMessage.Text = "Installation paused because Fortnite or a session became active."; return; }
                var result = await Updater().LaunchApplyHelperAsync(_stagedUpdate, UpdateBlocked);
                _updateMessage.Text = result.Message; AppendLog("Update install: " + result.Message);
                if (!result.Started || UpdateBlocked()) return;
                try { _engine.SaveOptions(ReadOptions()); } catch (Exception error) { AppendLog("Could not save preferences before restart: " + error.Message); }
                _allowClose = true; Close();
            });
        }
        finally { _updateInProgress = false; if (!IsDisposed) UpdateState(); }
    }
    private void UpdateUpdateControls()
    {
        bool idle = !UiBusy && !_updateInProgress && !VisibleSessionActive;
        _updateAction.Text = _stagedUpdate != null ? "Install & restart" : _updateCandidate != null ? "Download " + _updateCandidate.Version : "Check for updates";
        _updateAction.Enabled = idle; _updateRecheck.Enabled = idle && _updateCandidate != null;
        _updateRecheck.Visible = _updateCandidate != null;
        _updateCancel.Enabled = _updateCancellation != null; _updateCancel.Visible = _updateCancellation != null;
        _thirdPartyLicenses.Enabled = !UiBusy;
    }
}
