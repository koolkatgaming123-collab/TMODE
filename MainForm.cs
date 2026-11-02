using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace TourneyMode;

public sealed partial class MainForm : Form
{
    private readonly TourneyEngine _engine;
    private readonly Color _background = UiTheme.Background;
    private readonly Color _panel = UiTheme.Surface;
    private readonly Color _text = UiTheme.Text;
    private readonly Color _muted = UiTheme.Muted;
    private readonly Color _accent = UiTheme.Cyan;
    private readonly Label _mode = new();
    private readonly Label _summary = new();
    private readonly Label _detail = new();
    private readonly Label _status = new();
    private readonly Button _toggle = new ThemeButton();
    private readonly Button _scan = new ThemeButton();
    private readonly Button _selectAll = new ThemeButton();
    private readonly Button _analyze = new ThemeButton();
    private readonly Button _keepApps = new ThemeButton();
    private readonly Button _diagnostics = new ThemeButton();
    private readonly Button _features = new ThemeButton();
    private readonly CheckBox _force = new();
    private readonly CheckBox _priority = new();
    private readonly CheckBox _silence = new();
    private readonly CheckBox _power = new();
    private readonly CheckBox _game = new();
    private readonly CheckBox _fortniteOnly = new();
    private readonly CheckBox _headroom = new();
    private readonly CheckBox _keepClosed = new();
    private readonly CheckBox _services = new();
    private readonly CheckBox _network = new();
    private readonly CheckBox _ethernet = new();
    private readonly CheckBox _autoRestore = new();
    private readonly CheckBox _closeCodex = new();
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 16000, InitialDelay = 400, ReshowDelay = 100 };
    private readonly ListView _apps = new();
    private readonly RichTextBox _log = new();
    private readonly NotifyIcon _tray = new();
    private readonly Timer _timer = new() { Interval = 5000 };
    private bool _busy;
    private bool _maintaining;
    private bool _allowClose;
    private bool _pendingRestore;
    private bool _exitAfterRestore;
    private bool _loading;
    private bool _reviewDialogOpen;
    private bool _deferredRestoreAttempted;
    private bool _hotkeyToggle;
    private bool _hotkeyRestore;
    private bool _startingTourney;
    private bool _tourneyStartCanceled;
    private CancellationTokenSource? _tourneyStartCancellation;
    private AppOptions _preferences;
    private readonly bool _preview;
    private readonly bool _skipStartupUpdateOnce;
    private OptimizationPlan? _reviewedPlan;

    public MainForm(TourneyEngine engine, bool preview = false, bool skipStartupUpdateOnce = false) : this(engine, preview, preview ? new AppOptions() : engine.LoadOptions(), skipStartupUpdateOnce) { }

    private MainForm(TourneyEngine engine, bool preview, AppOptions options, bool skipStartupUpdateOnce = false)
    {
        _engine = engine;
        _preview = preview;
        _skipStartupUpdateOnce = skipStartupUpdateOnce;
        _preferences = PlannerDialog.CopyOptions(options);
        _preferences.Intent ??= new(); // ProfileDefaults owns safe migration; the UI never selects an all-ON preset.
        Text = ReleaseIdentity.Title + (preview ? " (preview)" : "");
        if (!preview) HandleCreated += (_, _) => ReleaseIdentity.MarkController(Handle);
        StartPosition = FormStartPosition.CenterScreen;
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 960);
        ClientSize = new Size(Math.Min(1220, Math.Max(980, workArea.Width - 80)), Math.Min(900, Math.Max(680, workArea.Height - 80)));
        MinimumSize = new Size(980, 680);
        AutoScroll = false;
        BackColor = _background;
        ForeColor = _text;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = SystemIcons.Shield;
        BuildLayout();
        if (!preview) _engine.Log += AppendLog;
        _tray.Icon = Icon;
        _tray.Text = "Tourney Mode — off";
        _tray.Visible = !preview;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Tourney Mode", null, (_, _) => ShowWindow());
        menu.Items.Add("Turn off / restore settings", null, async (_, _) => await RequestRestoreAsync(false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Turn off and exit", null, async (_, _) => await RequestRestoreAsync(true));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
        _timer.Tick += async (_, _) => await MaintainAsync();
        Shown += async (_, _) =>
        {
            if (_preview) return;
            _hotkeyToggle = RegisterHotKey(Handle, 1, 0x4003, 0x79);
            _hotkeyRestore = RegisterHotKey(Handle, 2, 0x4003, 0x7A);
            if (!_hotkeyToggle) AppendLog("Ctrl+Alt+F10 is in use by another app. Use the Tourney Mode button.");
            if (!_hotkeyRestore) AppendLog("Ctrl+Alt+F11 is in use by another app. Use Turn off / restore settings in the tray.");
            if (_engine.IsActive)
            {
                AppendLog("Saved Tourney Mode session found. Use TURN OFF to restore settings and recorded Codex components.");
                PopulateRecoveryList();
                UpdateState();
            }
            else await RefreshAppsAsync();
            _timer.Start();
            await StartupUpdateCheckAsync();
        };
        FormClosing += OnClosing;
        UpdateState();
    }

    private void BuildLayout()
    {
        BuildNavigationShell();
    }

    private void SetCheck(CheckBox control, string text, bool value)
    {
        control.Text = text;
        control.Checked = value;
        control.Dock = DockStyle.Fill;
        control.ForeColor = _text;
        control.AutoSize = true;
        control.Font = new Font("Segoe UI", 9f);
    }

    private void StyleButton(Button button, bool primary)
    {
        if (button is ThemeButton theme) { theme.Primary = primary; theme.Invalidate(); return; }
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(65, 81, 104);
        button.BackColor = primary ? _accent : _panel;
        button.ForeColor = primary ? Color.FromArgb(8, 31, 25) : _text;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    private AppOptions ReadOptions() => new()
    {
        OptionsVersion = Math.Max(9, _preferences.OptionsVersion),
        Intent = PlannerDialog.CopyIntent(_preferences.Intent),
        ReviewedPlanFingerprint = _preferences.ReviewedPlanFingerprint,
        CloseCodex = _closeCodex.Checked,
        ProtectedAppPaths = _preferences.ProtectedAppPaths.ToList(),
        FortniteOnly = _fortniteOnly.Checked,
        MaximumHeadroom = _headroom.Checked,
        KeepAppsClosed = _keepClosed.Checked,
        PauseOptionalServices = _services.Checked,
        PrioritizeNetwork = _network.Checked,
        TuneEthernet = _ethernet.Checked,
        ExperimentalWiredNetwork = _experimentalWired.Checked,
        ExperimentalDscp = _experimentalDscp.Checked,
        ExperimentalBackgroundGovernor = _experimentalGovernor.Checked,
        AutoRestoreOnGameExit = _autoRestore.Checked,
        ForceClose = _force.Checked,
        HighPriority = _priority.Checked,
        SilenceNotifications = _silence.Checked,
        HighPerformancePower = _power.Checked,
        GameTuning = _game.Checked,
        SelectedApps = _apps.CheckedItems.Cast<ListViewItem>().Select(i => ((AppCandidate)i.Tag!).Id).ToList(),
        ExcludedApps = _preferences.ExcludedApps.Where(id => !_apps.Items.Cast<ListViewItem>().Any(i => ((AppCandidate)i.Tag!).Id == id))
            .Concat(_apps.Items.Cast<ListViewItem>().Where(i => !i.Checked).Select(i => ((AppCandidate)i.Tag!).Id)).Distinct().ToList()
    };

    private void ApplyPreferencesToControls(AppOptions options)
    {
        _preferences = PlannerDialog.CopyOptions(options);
        _loading = true;
        try
        {
            _force.Checked = options.ForceClose; _priority.Checked = options.HighPriority; _silence.Checked = options.SilenceNotifications;
            _power.Checked = options.HighPerformancePower; _game.Checked = options.GameTuning; _fortniteOnly.Checked = options.FortniteOnly;
            _headroom.Checked = options.MaximumHeadroom; _keepClosed.Checked = options.KeepAppsClosed; _services.Checked = options.PauseOptionalServices;
            _network.Checked = options.PrioritizeNetwork; _ethernet.Checked = options.TuneEthernet; _autoRestore.Checked = options.AutoRestoreOnGameExit; _closeCodex.Checked = options.CloseCodex;
            _experimentalWired.Checked = options.ExperimentalWiredNetwork; _experimentalDscp.Checked = options.ExperimentalDscp; _experimentalGovernor.Checked = options.ExperimentalBackgroundGovernor;
        }
        finally { _loading = false; }
        InvalidateTweakAssessment();
        UpdateState();
    }

    private async Task RefreshAppsAsync()
    {
        if (_preview || _busy || _engine.IsActive) return;
        await RunOperationAsync(async () =>
        {
            var choices = _apps.Items.Cast<ListViewItem>().ToDictionary(i => ((AppCandidate)i.Tag!).Id, i => i.Checked);
            if (_apps.Items.Count > 0) _preferences = ReadOptions();
            bool fortniteOnly = _fortniteOnly.Checked;
            bool maximumHeadroom = _headroom.Checked;
            var candidates = await Task.Run(() => _engine.Scan(fortniteOnly, maximumHeadroom, _preferences.ProtectedAppPaths));
            _loading = true;
            _apps.BeginUpdate();
            _apps.Items.Clear();
            foreach (var app in candidates)
            {
                bool selected = choices.TryGetValue(app.Id, out bool prior) ? prior
                    : !_preferences.ExcludedApps.Contains(app.Id) && (_preferences.SelectedApps.Contains(app.Id) || app.DefaultSelected);
                var item = new ListViewItem(app.Name) { Tag = app, Checked = selected };
                item.SubItems.Add(app.Processes.Count.ToString());
                item.SubItems.Add(app.Detail);
                _apps.Items.Add(item);
            }
            _apps.EndUpdate();
            _loading = false;
            AppendLog(candidates.Count == 0 ? "No eligible apps currently available to close. Protected processes stay running."
                : $"Preview refreshed: {candidates.Count} app groups. Existing exclusions are preserved; Select all removes exclusions for listed groups.");
        });
    }

    private void PopulateRecoveryList()
    {
        _loading = true;
        _apps.Items.Clear();
        foreach (var entry in _engine.State?.Apps ?? [])
        {
            var item = new ListViewItem(entry.App.Name) { Tag = entry.App, Checked = true };
            item.SubItems.Add(entry.App.Processes.Count.ToString());
            item.SubItems.Add(entry.Restored ? "Completed" : string.IsNullOrWhiteSpace(entry.Error) ? "Closed for this session" : entry.Error);
            _apps.Items.Add(item);
        }
        _loading = false;
    }

    public void PreparePreview()
    {
        if (!_preview) throw new InvalidOperationException("Create MainForm with preview: true for a hidden preview.");
        PrepareSyntheticPreview("Overview", null);
        CreateControl();
        CreatePreviewHandles(this);
        PerformLayout();
    }

    private static void CreatePreviewHandles(Control parent)
    {
        _ = parent.Handle;
        foreach (Control child in parent.Controls) CreatePreviewHandles(child);
    }

    private async Task ToggleAsync()
    {
        if (_preview || _reviewDialogOpen || _updateInProgress) return;
        if (_engine.IsActive || _startingTourney || _engine.ActivationBusy) { await RequestRestoreAsync(false); return; }
        if (_busy) return;
        using var cancellation = new CancellationTokenSource();
        _tourneyStartCancellation = cancellation;
        _startingTourney = true; _tourneyStartCanceled = false;
        UpdateState();
        try { await StartTourneyAsync(cancellation.Token); }
        finally
        {
            _startingTourney = false; _tourneyStartCancellation = null;
            if (!IsDisposed) UpdateState();
        }
    }
    private async Task StartTourneyAsync(CancellationToken token)
    {
        var options = ReadOptions();
        if (string.IsNullOrWhiteSpace(options.ReviewedPlanFingerprint))
        {
            AppendLog("Analyze this PC and choose Use this plan before activation. Saving a reviewed plan does not turn the mode on.");
            await AnalyzeAsync();
            return;
        }
        OptimizationPlan? plan = null;
        await RunOperationAsync(async () =>
        {
            AppendLog("Revalidating this PC and the selected goal before activation. No settings changed yet.");
            plan = await Task.Run(() => _engine.AnalyzePlanAsync(options));
        });
        if (IsDisposed || plan == null || _engine.IsActive || _tourneyStartCanceled || token.IsCancellationRequested) return;
        if (!string.Equals(plan.InventoryFingerprint, options.ReviewedPlanFingerprint, StringComparison.Ordinal))
        {
            AppendLog("PC, power, game state or plan choices changed. Review a fresh plan; this activation has been stopped.");
            await AnalyzeAsync();
            return;
        }
        options = PerformancePlanner.Apply(plan, options);
        var names = _apps.CheckedItems.Cast<ListViewItem>().Select(i => i.Text).ToArray();
        var message = "Turn on Tourney Mode for this PC?\r\n\r\n"
            + "Goal: " + PlannerDialog.GoalName(options.Intent.Goal) + ". Match-safe: " + (options.Intent.MatchSafe ? "ON" : "OFF") + ". Power: " + PlannerDialog.PowerName(plan.EffectivePower) + ".\r\n\r\n"
            + (names.Length > 0 ? "Selected app preview (keep/activity exclusions are enforced at activation):\r\n• " + string.Join("\r\n• ", names) : "No app groups selected.")
            + "\r\n\r\nSave your work now. Other closed apps stay closed; supported Codex hosts can restart on OFF."
            + (options.ForceClose ? "\r\nForce close is ON: apps still open after the grace period can be terminated." : "\r\nForce close is OFF: apps that refuse to close will be left running.")
            + (options.FortniteOnly ? "\r\nFortnite-only profile: broader app shutdown." : "")
            + (options.CloseCodex ? "\r\nCodex and supported helpers will CLOSE. Finish active jobs first. Chat is unavailable while ON. OFF restarts providers, then Codex; unrelated closed apps stay closed." : "\r\nCodex connections stay running.")
            + (options.MaximumHeadroom ? "\r\nMAXIMUM HEADROOM: includes unlisted background executables, developer runtimes and local servers. Selected processes can lose unsaved work or interrupt jobs. Optional tasks are disabled until OFF. Hardware and essential services remain protected; fewer than 100 processes is not guaranteed." : "")
            + (options.KeepAppsClosed ? (options.Intent.Fps.QuietDuringGame ? "\r\nQuiet match: returning apps can remain open while Fortnite runs; the guard pauses to reduce process and journal work." : "\r\nApp guard: reviewed returning apps can close while ON. Kept apps remain protected.") : "")
            + (options.PauseOptionalServices ? (options.MaximumHeadroom ? "\r\nOptional services are temporarily held disabled to prevent restarts; OFF restores their original startup types and previously running services." : "\r\nOptional running services are paused and restarted on turn OFF. Security, audio, network, input and game services stay running.") : "")
            + (options.PrioritizeNetwork ? "\r\nFortnite traffic receives temporary Ethernet priority marking; network support determines its effect." : "")
            + (options.TuneEthernet ? "\r\nEthernet driver settings are saved and tuned where supported. Changes may briefly reconnect Ethernet; turn OFF restores them." : "")
            + (options.AutoRestoreOnGameExit ? "\r\nSettings will also restore automatically after the observed Fortnite client has exited for 15 seconds." : "")
            + "\r\n\r\nTurn off here, in the tray, or with Ctrl+Alt+F11 to restore settings and recorded Codex components. Discord stays running.";
        if (!PlanActivationDialog.Confirm(this, message, PerformancePlanner.Format(plan))) return;
        if (_tourneyStartCanceled || token.IsCancellationRequested) return;
        await RunOperationAsync(async () =>
        {
            if (_tourneyStartCanceled || token.IsCancellationRequested) return;
            _reviewedPlan = plan;
            ApplyPreferencesToControls(options);
            _engine.SaveOptions(options);
            try { await Task.Run(() => _engine.EnableAsync(options, token)); }
            catch (OperationCanceledException) { AppendLog("Tourney startup stopped. Recorded changes will be restored or kept available for recovery."); }
            if (_engine.IsActive) PopulateRecoveryList();
        });
    }

    private async Task RequestRestoreAsync(bool exit)
    {
        if (_preview) return;
        _tourneyStartCanceled = true;
        _tourneyStartCancellation?.Cancel();
        _tweakCancellation?.Cancel();
        _stripCancellation?.Cancel();
        _internetCancellation?.Cancel();
        _startupUpdateInterrupted = true;
        Interlocked.Increment(ref _updateInterruptionGeneration);
        _updateCancellation?.Cancel();
        _engine.RequestRestore();
        _deferredRestoreAttempted = _engine.RecoveryDeferredUntilGameExit && !_engine.GameClientRunning;
        _exitAfterRestore |= exit;
        if (_busy) { _pendingRestore = true; return; }
        _pendingRestore = false;
        await RunOperationAsync(async () =>
        {
            if (_engine.IsActive) await Task.Run(_engine.RestoreAsync);
            if (_engine.IsActive)
            {
                PopulateRecoveryList();
                ShowWindow();
                AppendLog("Some recovery items still need attention. Review the log, then press TURN OFF TOURNEY MODE to retry.");
                _exitAfterRestore = false;
            }
            else if (_exitAfterRestore)
            {
                _allowClose = true;
                Close();
            }
        });
        if (!IsDisposed && !_engine.IsActive && !_exitAfterRestore) await RefreshAppsAsync();
    }

    private async Task MaintainAsync()
    {
        if (_preview || _busy || _maintaining) return;
        if (!_engine.IsActive) { _deferredRestoreAttempted = false; return; }
        if (_engine.RecoveryDeferredUntilGameExit)
        {
            if (_deferredRestoreAttempted || _engine.GameClientRunning) return;
            _deferredRestoreAttempted = true;
            _maintaining = true;
            try
            {
                AppendLog("Fortnite exited. Resuming deferred settings recovery once; a failed attempt will stay recorded for a manual retry.");
                await RequestRestoreAsync(false);
            }
            finally { _maintaining = false; if (!IsDisposed) UpdateState(); }
            return;
        }
        _deferredRestoreAttempted = false;
        if (_engine.State?.Phase != "Active") return;
        _maintaining = true;
        try { await Task.Run(_engine.MaintainAsync); }
        catch (Exception ex) { AppendLog("APP GUARD NEEDS ATTENTION: " + ex.Message); }
        finally { _maintaining = false; if (!IsDisposed) UpdateState(); }
        if (!IsDisposed && !_engine.IsActive) await RefreshAppsAsync();
    }

    private async Task RunOperationAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateState();
        try { await action(); }
        catch (Exception ex)
        {
            AppendLog("ACTION NEEDS ATTENTION: " + ex.Message);
            ShowWindow();
            _exitAfterRestore = false;
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) UpdateState();
        }
        if (_pendingRestore && !IsDisposed) await RequestRestoreAsync(_exitAfterRestore);
    }

    private void UpdateState()
    {
        bool working = UiBusy;
        bool active = VisibleSessionActive;
        bool recovery = active && VisibleSession?.Phase != "Active";
        bool tweak = string.Equals(VisibleSession?.Workflow, "Tweak", StringComparison.Ordinal);
        bool strip = string.Equals(VisibleSession?.Workflow, "Strip", StringComparison.Ordinal);
        bool internet = string.Equals(VisibleSession?.Workflow, "Internet", StringComparison.Ordinal);
        bool stopped = VisibleTourneyStopped;
        _mode.Text = stopped ? "Tourney is stopped. Recorded settings still need recovery." : working ? "Working…" : recovery ? "Recovery available" : active ? internet ? VisibleSession?.InternetCalibration?.Restored == true ? "Network comparison ready for review" : "Network settings are active" : strip ? "Windows feature changes are active" : tweak ? "Tweak changes are active" : "Tourney Mode is on" : "Ready when you are";
        _mode.ForeColor = recovery ? Color.FromArgb(245, 191, 111) : _accent;
        _toggle.Text = stopped ? "Retry recovery" : active ? tweak || strip || internet ? "Restore changes" : "Turn off Tourney" : _startingTourney ? "Cancel start" : working ? "Please wait" : "Turn on Tourney";
        _toggle.Enabled = (active || _startingTourney || !working) && !_reviewDialogOpen && !_updateInProgress;
        _toggle.BackColor = active ? Color.FromArgb(245, 191, 111) : _accent;
        _scan.Enabled = !working && !active;
        _apps.Enabled = !working && !active;
        _selectAll.Enabled = !working && !active;
        _analyze.Enabled = !working;
        _diagnostics.Enabled = !working;
        _features.Enabled = !working;
        _keepApps.Enabled = !working && !active;
        foreach (var check in new[] { _force, _priority, _silence, _power, _game, _fortniteOnly, _headroom, _keepClosed, _services, _network, _ethernet, _autoRestore, _closeCodex }) check.Enabled = !working && !active;
        _tray.Text = stopped ? "Tourney Mode — off / recovery pending" : active ? "Tourney Mode — on / recovery available" : "Tourney Mode — off";
        UpdateSelectionSummary();
        UpdateNavigationStatus();
    }

    private void UpdateSelectionSummary()
    {
        if (VisibleSessionActive)
        {
            var state = VisibleSession!;
            if (string.Equals(state.Workflow, "Internet", StringComparison.Ordinal))
            {
                _summary.Text = state.InternetCalibration?.Restored == true ? "Original NIC settings are restored.\r\nReview the comparison or choose Restore originals / finish to close the safety session." : "NIC changes remain recorded.\r\nUse Restore originals / finish in Internet Optimizer to return the recorded baseline.";
                return;
            }
            if (string.Equals(state.Workflow, "Strip", StringComparison.Ordinal))
            {
                int pending = state.Strip?.Services.Count(service => service.ApplyAttempted && !service.Restored) ?? 0;
                _summary.Text = pending + " Windows service changes remain recorded.\r\nUse Repair all or a feature's Fix button in Strip Windows to return recorded original settings.";
                return;
            }
            var features = state.Features?.ToArray() ?? [];
            string actual = features.Length == 0 ? "Per-feature results not recorded yet; review the log."
                : $"Applied {features.Count(f => f.State == FeatureState.Applied)} | Deferred {features.Count(f => f.State == FeatureState.Deferred)} | Denied/failed {features.Count(f => f.State is FeatureState.Denied or FeatureState.Failed)}. See Feature status.";
            _summary.Text = state.Phase == "Active"
                ? (state.Plan != null ? PlannerDialog.GoalName(state.Plan.Intent.Goal) : "Existing session (goal not recorded)") + ". " + actual + "\r\nRestore returns recorded settings and supported Codex components. Other closed apps stay closed."
                : "A saved session has pending recovery items.\r\nRestore before starting another session.";
            return;
        }
        int groups = _apps.CheckedItems.Count;
        int processes = _apps.CheckedItems.Cast<ListViewItem>().Sum(i => ((AppCandidate)i.Tag!).Processes.Count);
        _summary.Text = PlannerDialog.GoalName(_preferences.Intent.Goal) + " | Match-safe " + (_preferences.Intent.MatchSafe ? "ON" : "OFF")
            + $"\r\n{groups} app groups / {processes} processes selected. "
            + (_reviewedPlan == null && string.IsNullOrWhiteSpace(_preferences.ReviewedPlanFingerprint) ? "Use Advanced controls to review the Tourney plan." : "Reviewed choices saved; activation revalidates them.");
    }

    private void AppendLog(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)(() => AppendLog(message))); } catch (InvalidOperationException) { } return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        if (_log.TextLength > 80000) _log.Text = _log.Text[^50000..];
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_preview) return;
        if (_allowClose) return;
        _updatesClosing = true;
        _startupUpdateInterrupted = true;
        Interlocked.Increment(ref _updateInterruptionGeneration);
        _updateCancellation?.Cancel();
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) return;
        if (_startupUpdateRunning)
        {
            _exitAfterRestore = true;
            _pendingRestore = true;
            e.Cancel = true;
            Hide();
            return;
        }
        if (_engine.IsActive || _busy)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        try { _engine.SaveOptions(ReadOptions()); } catch (Exception ex) { AppendLog("Could not save preferences: " + ex.Message); }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            if (m.WParam.ToInt32() == 1) _ = ToggleAsync();
            if (m.WParam.ToInt32() == 2) _ = RequestRestoreAsync(false);
        }
        else if (m.Msg == 0x8001) _ = RequestRestoreAsync(false);
        else if (m.Msg == 0x8002) ShowWindow();
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updatesClosing = true;
            _timer.Stop();
            _timer.Dispose();
            if (IsHandleCreated)
            {
                if (_hotkeyToggle) UnregisterHotKey(Handle, 1);
                if (_hotkeyRestore) UnregisterHotKey(Handle, 2);
            }
            _tray.Visible = false;
            _tray.Dispose();
            _toolTip.Dispose();
            _tourneyStartCancellation?.Cancel();
            _tweakCancellation?.Cancel();
            _stripCancellation?.Cancel();
            _internetCancellation?.Cancel();
            _updateCancellation?.Cancel();
            _updater?.Dispose();
            if (!_preview) _engine.Log -= AppendLog;
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
