using System.Drawing;
using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private readonly Dictionary<string, Panel> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ThemeButton> _navigation = new(StringComparer.Ordinal);
    private readonly Panel _pageHost = new();
    private readonly Label _pageTitle = new();
    private readonly Label _pageSubtitle = new();
    private readonly Label _sessionBadge = new();
    private readonly Label _sessionMetric = new();
    private readonly Label _goalMetric = new();
    private readonly Label _protectionMetric = new();
    private readonly Label _overviewFacts = new();
    private readonly ThemeButton _undoTop = new() { Text = "Undo session", Width = 138, Height = 38 };
    private readonly ListView _featureList = new();
    private string _selectedPage = "Overview";
    private SessionState? _previewSession;
    private TableLayoutPanel? _sessionOptionsPanel;
    private bool _sessionOptionsExpanded;
    private SessionState? VisibleSession => _preview ? _previewSession : _engine.State;
    private bool VisibleSessionActive => VisibleSession != null;
    private bool VisibleTourneyStopped => string.Equals(VisibleSession?.Workflow, "Tourney", StringComparison.Ordinal)
        && (_preview ? VisibleSession?.Phase is "Restore deferred" or "Needs recovery" : _engine.StopRequested);
    private bool UiBusy => _busy || _updateInProgress || (!_preview && _engine.InternetBusy);

    private void BuildNavigationShell()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Background, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 214)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(shell);
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Sidebar, Margin = Padding.Empty, Padding = new Padding(16, 22, 16, 14) };
        shell.Controls.Add(sidebar, 0, 0);
        var brand = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = UiTheme.Sidebar };
        var title = UiTheme.Label("TOURNEY", 20, true); title.Location = new Point(10, 4);
        var sub = UiTheme.Label("MODE", 12, true, UiTheme.Cyan); sub.Location = new Point(12, 39);
        var caption = UiTheme.Label("PERFORMANCE CONTROL", 8, false, UiTheme.Muted); caption.Location = new Point(12, 68);
        brand.Controls.Add(title); brand.Controls.Add(sub); brand.Controls.Add(caption);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = false, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = UiTheme.Sidebar };
        var navigationHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Sidebar }; navigationHost.Controls.Add(nav);
        foreach (string name in new[] { "Overview", "Tweak", "Strip Windows ⚠", "Internet Optimizer", "Experimental", "Guide / BIOS", "Apps", "Activity", "Updates" })
        {
            var button = new ThemeButton { Text = name, Navigation = true, Width = 182, Height = 46, Margin = new Padding(0, 0, 0, 7) };
            button.Click += (_, _) => SelectNavigation(name); _navigation.Add(name, button); nav.Controls.Add(button);
        }
        nav.Height = nav.Controls.Count * 53;
        nav.SizeChanged += (_, _) => { foreach (Control button in nav.Controls) button.Width = Math.Max(120, nav.ClientSize.Width - 1); };
        var local = UiTheme.Label("ON-DEVICE ENGINE\r\n" + ReleaseIdentity.Version + "  |  Windows x64", 8.5f, false, UiTheme.Muted);
        local.Dock = DockStyle.Bottom; local.AutoSize = false; local.Height = 58; local.Padding = new Padding(10, 8, 0, 0);
        sidebar.Controls.Add(navigationHost); sidebar.Controls.Add(brand); sidebar.Controls.Add(local);

        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(28, 22, 28, 12), BackColor = UiTheme.Background, Margin = Padding.Empty };
        workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 82)); workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        shell.Controls.Add(workspace, 1, 0);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = UiTheme.Stack();
        _pageTitle.Text = "Overview"; _pageTitle.Font = new Font("Segoe UI", 23, FontStyle.Bold); _pageTitle.ForeColor = UiTheme.Text; _pageTitle.AutoSize = true; _pageTitle.Margin = new Padding(0, 0, 0, 3);
        _pageSubtitle.AutoSize = true; _pageSubtitle.Font = new Font("Segoe UI", 9.5f); _pageSubtitle.ForeColor = UiTheme.Muted; _pageSubtitle.Margin = Padding.Empty;
        UiTheme.Add(heading, _pageTitle); UiTheme.Add(heading, _pageSubtitle); header.Controls.Add(heading, 0, 0);
        // Fixed header cells must not inherit FlowLayoutPanel's 100px preferred height.
        var headerActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            AutoSize = false, Height = 56, MaximumSize = new Size(0, 74), Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 0) };
        headerActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); headerActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        headerActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _undoTop.Click += async (_, _) => { if (!_preview) await RequestRestoreAsync(false); };
        _sessionBadge.AutoSize = false; _sessionBadge.Size = new Size(118, 38); _sessionBadge.TextAlign = ContentAlignment.MiddleCenter; _sessionBadge.Font = new Font("Segoe UI", 9, FontStyle.Bold); _sessionBadge.ForeColor = UiTheme.Cyan; _sessionBadge.BackColor = UiTheme.Surface; _sessionBadge.Margin = new Padding(0, 0, 10, 0);
        _sessionBadge.Dock = DockStyle.Top; _undoTop.Dock = DockStyle.Top; _undoTop.Margin = Padding.Empty;
        headerActions.Controls.Add(_sessionBadge, 0, 0); headerActions.Controls.Add(_undoTop, 1, 0); header.Controls.Add(headerActions, 1, 0); workspace.Controls.Add(header, 0, 0);
        _pageHost.Dock = DockStyle.Fill; _pageHost.BackColor = UiTheme.Background; workspace.Controls.Add(_pageHost, 0, 1);
        foreach (string name in _navigation.Keys)
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Background, AutoScroll = true, Visible = false, Margin = Padding.Empty };
            _pages.Add(name, page); _pageHost.Controls.Add(page);
        }
        BuildOverviewPage(_pages["Overview"]); BuildTweakPage(_pages["Tweak"]); BuildAppsPage(_pages["Apps"]);
        BuildStripPage(_pages["Strip Windows ⚠"]); BuildExperimentalPage(_pages["Experimental"]); BuildGuidePage(_pages["Guide / BIOS"]);
        BuildInternetPage(_pages["Internet Optimizer"]); BuildActivityPage(_pages["Activity"]); BuildUpdatesPage(_pages["Updates"]);
        _status.Dock = DockStyle.Fill; _status.ForeColor = UiTheme.Muted; _status.Font = new Font("Segoe UI", 8.5f); _status.TextAlign = ContentAlignment.BottomLeft;
        _status.Text = "Changes stay visible. Undo stays available.  |  Ctrl+Alt+F10 Tourney / Ctrl+Alt+F11 restore";
        workspace.Controls.Add(_status, 0, 2);
        ConfigureLegacyControls(); SelectNavigation("Overview");
    }

    private static Label Paragraph(string text)
    {
        var label = UiTheme.Label(text, 10, false, UiTheme.Muted); label.Dock = DockStyle.Top; label.MaximumSize = new Size(760, 0); return label;
    }
    private static ThemeCard Card(string title, string? description, out TableLayoutPanel body)
    {
        var card = new ThemeCard { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        body = UiTheme.Stack(); UiTheme.Add(body, UiTheme.Label(title, 15, true));
        if (!string.IsNullOrWhiteSpace(description)) UiTheme.Add(body, Paragraph(description));
        card.Controls.Add(body);
        var content = body;
        card.SizeChanged += (_, _) =>
        {
            int width = Math.Max(200, card.ClientSize.Width - card.Padding.Horizontal);
            foreach (var label in content.Controls.OfType<Label>()) label.MaximumSize = new Size(width, 0);
        };
        return card;
    }
    private static FlowLayoutPanel Actions(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        row.Controls.AddRange(controls); return row;
    }
    private void BuildOverviewPage(Panel page)
    {
        var stack = UiTheme.Stack(); page.Controls.Add(stack);
        var hero = Card("Prepared for your next session", "Tweak PC prepares supported changes for FPS, responsiveness and stable frames. Review your optimizations, apply them with a verified Windows restore point, and keep Undo available.", out var heroBody);
        hero.Fill = Color.FromArgb(19, 38, 59);
        var open = new ThemeButton { Text = "Tweak PC", Primary = true, Width = 170 }; open.Click += async (_, _) => { SelectNavigation("Tweak"); if (!_preview) await StartTweakScanAsync(); };
        UiTheme.Add(heroBody, Actions(open)); UiTheme.Add(stack, hero);
        var metrics = new TableLayoutPanel { Dock = DockStyle.Top, Height = 108, ColumnCount = 3, Margin = new Padding(0, 0, 0, 16) };
        for (int i = 0; i < 3; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        metrics.Controls.Add(Metric("SESSION", _sessionMetric), 0, 0); metrics.Controls.Add(Metric("CURRENT GOAL", _goalMetric), 1, 0); metrics.Controls.Add(Metric("PROTECTION", _protectionMetric), 2, 0); UiTheme.Add(stack, metrics);
        var tourney = Card("Tourney session", "Use the full session controls when you want reviewed app cleanup and your advanced profile. Tweak and Tourney share one recovery record.", out var tourneyBody);
        _mode.AutoSize = true; _mode.ForeColor = UiTheme.Cyan; _mode.Font = new Font("Segoe UI", 12, FontStyle.Bold); _mode.Margin = new Padding(0, 3, 0, 8);
        _summary.AutoSize = true; _summary.ForeColor = UiTheme.Muted; _summary.Font = new Font("Segoe UI", 9.5f); _summary.Margin = new Padding(0, 0, 0, 8);
        UiTheme.Add(tourneyBody, _mode); UiTheme.Add(tourneyBody, _summary);
        _toggle.Width = 210; _toggle.Height = 44; _toggle.Click += async (_, _) => { if (!_preview) await ToggleAsync(); };
        var sessionOptions = new ThemeButton { Text = "Session options", Width = 170 };
        var advanced = UiTheme.Stack(); _sessionOptionsPanel = advanced; advanced.Visible = false; advanced.Margin = new Padding(0, 16, 0, 0);
        UiTheme.Add(advanced, Paragraph("These preferences apply to the full Tourney session. The reviewed plan checks what is supported before activation. Tweak keeps its own scope."));
        var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 0, Margin = Padding.Empty };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var checks = LegacyChecks();
        for (int i = 0; i < checks.Length; i++)
        {
            if (i % 2 == 0) { settings.RowCount++; settings.RowStyles.Add(new RowStyle(SizeType.AutoSize)); }
            checks[i].Margin = new Padding(0, 4, 10, 9); settings.Controls.Add(checks[i], i % 2, i / 2);
        }
        UiTheme.Add(advanced, settings);
        sessionOptions.Click += (_, _) => { _sessionOptionsExpanded = !_sessionOptionsExpanded; advanced.Visible = _sessionOptionsExpanded; sessionOptions.Text = _sessionOptionsExpanded ? "Hide options" : "Session options"; };
        UiTheme.Add(tourneyBody, Actions(_toggle, sessionOptions)); UiTheme.Add(tourneyBody, advanced); UiTheme.Add(stack, tourney);
        var facts = Card("This PC", "Hardware facts appear after an explicit Tweak scan. Inventory alone does not predict an FPS gain.", out var factsBody);
        _overviewFacts.Text = "No scan yet. Your current Windows settings have not been changed by this window."; _overviewFacts.AutoSize = true; _overviewFacts.ForeColor = UiTheme.Muted; _overviewFacts.Font = new Font("Segoe UI", 9.5f);
        UiTheme.Add(factsBody, _overviewFacts); UiTheme.Add(stack, facts);
    }
    private static ThemeCard Metric(string title, Label value)
    {
        var card = new ThemeCard { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), Padding = new Padding(16, 13, 14, 10) };
        var body = UiTheme.Stack(); UiTheme.Add(body, UiTheme.Label(title, 8, true, UiTheme.Muted));
        value.AutoSize = true; value.Font = new Font("Segoe UI", 12, FontStyle.Bold); value.ForeColor = UiTheme.Text; value.Margin = Padding.Empty;
        UiTheme.Add(body, value); card.Controls.Add(body); return card;
    }

    private void BuildAppsPage(Panel page)
    {
        var stack = UiTheme.Stack(); page.Controls.Add(stack);
        var card = Card("Apps you choose to close", "These selections apply to a Tourney session. Tweak PC uses its own scope and keeps apps running. Discord, kept apps and required components remain protected.", out var body);
        _scan.Text = "Refresh list"; _scan.Width = 130; _scan.Click += async (_, _) => { if (!_preview) await RefreshAppsAsync(); };
        _selectAll.Text = "Select all"; _selectAll.Width = 120; _selectAll.Click += (_, _) => { foreach (ListViewItem item in _apps.Items) item.Checked = true; UpdateSelectionSummary(); };
        _keepApps.Text = "Keep apps"; _keepApps.Width = 130; _keepApps.Click += async (_, _) => { if (!_preview) await EditKeepAppsAsync(); };
        UiTheme.Add(body, Actions(_scan, _selectAll, _keepApps));
        UiTheme.Add(body, TableHeader([("Application", 36), ("Processes", 13), ("Scope", 51)]));
        _apps.Dock = DockStyle.Fill; _apps.Height = 310; _apps.View = View.Details; _apps.HeaderStyle = ColumnHeaderStyle.None; _apps.CheckBoxes = true;
        _apps.FullRowSelect = true; _apps.HideSelection = false; _apps.MultiSelect = false; _apps.BorderStyle = BorderStyle.None; _apps.BackColor = UiTheme.Surface; _apps.ForeColor = UiTheme.Text; _apps.Font = new Font("Segoe UI", 9.5f);
        _apps.Columns.Add("Application", 260); _apps.Columns.Add("Processes", 90); _apps.Columns.Add("Scope", 400);
        _apps.SizeChanged += (_, _) => FitAppColumns();
        _apps.SelectedIndexChanged += (_, _) => _detail.Text = _apps.SelectedItems.Count > 0 && _apps.SelectedItems[0].Tag is AppCandidate app ? app.Detail : "Select an app for its closure and protection details.";
        _apps.ItemChecked += (_, _) => { if (!_loading) UpdateSelectionSummary(); };
        UiTheme.Add(body, _apps);
        _detail.Text = "Select an app for its closure and protection details."; _detail.ForeColor = UiTheme.Muted; _detail.AutoSize = true; _detail.Margin = new Padding(0, 12, 0, 0); UiTheme.Add(body, _detail);
        UiTheme.Add(stack, card);
    }
    private void FitAppColumns()
    {
        if (_apps.Columns.Count < 3) return; int width = Math.Max(360, _apps.ClientSize.Width - 5);
        _apps.Columns[0].Width = (int)(width * .36); _apps.Columns[1].Width = (int)(width * .13); _apps.Columns[2].Width = width - _apps.Columns[0].Width - _apps.Columns[1].Width;
    }
    private static TableLayoutPanel TableHeader((string Title, int Percent)[] columns)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, Height = 30, ColumnCount = columns.Length, Margin = new Padding(0, 12, 0, 4), BackColor = UiTheme.Raised };
        for (int i = 0; i < columns.Length; i++) { row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, columns[i].Percent)); var label = UiTheme.Label(columns[i].Title.ToUpperInvariant(), 8, true, UiTheme.Muted); label.Dock = DockStyle.Fill; label.Padding = new Padding(8, 6, 0, 0); row.Controls.Add(label, i, 0); }
        return row;
    }
    private void BuildActivityPage(Panel page)
    {
        var stack = UiTheme.Stack(); page.Controls.Add(stack);
        var results = Card("What actually changed", "Applied, deferred, denied and failed are separate outcomes. A successful setting change does not establish an FPS or latency improvement.", out var resultsBody);
        UiTheme.Add(resultsBody, TableHeader([("Feature", 27), ("State", 16), ("Evidence", 57)]));
        _featureList.Dock = DockStyle.Fill; _featureList.Height = 235; _featureList.View = View.Details; _featureList.HeaderStyle = ColumnHeaderStyle.None; _featureList.FullRowSelect = true; _featureList.BorderStyle = BorderStyle.None; _featureList.BackColor = UiTheme.Surface; _featureList.ForeColor = UiTheme.Text;
        _featureList.Columns.Add("Feature", 190); _featureList.Columns.Add("State", 110); _featureList.Columns.Add("Evidence", 450);
        _featureList.SizeChanged += (_, _) => { if (_featureList.Columns.Count == 3) { int width = Math.Max(360, _featureList.ClientSize.Width - 5); _featureList.Columns[0].Width = (int)(width * .27); _featureList.Columns[1].Width = (int)(width * .16); _featureList.Columns[2].Width = width - _featureList.Columns[0].Width - _featureList.Columns[1].Width; } };
        UiTheme.Add(resultsBody, _featureList); UiTheme.Add(stack, results);
        var log = Card("Activity log", "Local activity for this window. Recovery remains available through the Undo button or Ctrl+Alt+F11.", out var logBody);
        _log.Dock = DockStyle.Fill; _log.Height = 210; _log.ReadOnly = true; _log.DetectUrls = false; _log.BorderStyle = BorderStyle.None; _log.BackColor = UiTheme.Surface; _log.ForeColor = UiTheme.Muted; _log.Font = new Font("Consolas", 9);
        UiTheme.Add(logBody, _log); UiTheme.Add(stack, log);
    }
    private void SelectNavigation(string name)
    {
        if (!_pages.ContainsKey(name)) return;
        _selectedPage = name;
        foreach (var pair in _pages) pair.Value.Visible = pair.Key == name;
        foreach (var pair in _navigation) { pair.Value.Selected = pair.Key == name; pair.Value.Invalidate(); }
        _pages[name].BringToFront(); _pageTitle.Text = name;
        _pageSubtitle.Text = name switch
        {
            "Tweak" => "Understand this PC. Review the fixes. Apply with a way back.",
            "Strip Windows ⚠" => "Optional Windows features. Clear tradeoffs. Repair at any time.",
            "Experimental" => "Optional changes to test on your hardware. Off by default.",
            "Internet Optimizer" => "Measure supported candidates. Review the comparison. Keep a way back.",
            "Guide / BIOS" => "Hardware-specific guidance, manual steps and official sources.",
            "Apps" => "Your Tourney selections and always-keep protections.",
            "Activity" => "Recorded outcomes and the details behind every operation.",
            "Updates" => "Signed releases update automatically at safe startup.",
            _ => "A clear view of your next performance session."
        };
    }

    private void ConfigureLegacyControls()
    {
        SetCheck(_priority, "Reviewed Fortnite priority", _preferences.HighPriority); SetCheck(_silence, "Notification suppression", _preferences.SilenceNotifications);
        SetCheck(_force, "Force-close selected apps", _preferences.ForceClose); SetCheck(_power, "Reviewed AC power preference", _preferences.HighPerformancePower);
        SetCheck(_game, "Game Mode / capture preference", _preferences.GameTuning); SetCheck(_fortniteOnly, "Broader app selection", _preferences.FortniteOnly);
        SetCheck(_headroom, "Maximum headroom options", _preferences.MaximumHeadroom); SetCheck(_keepClosed, "Guard selected closed apps", _preferences.KeepAppsClosed);
        SetCheck(_services, "Optional service preparation", _preferences.PauseOptionalServices); SetCheck(_network, "Experimental traffic marking", _preferences.PrioritizeNetwork);
        SetCheck(_ethernet, "Experimental Ethernet settings", _preferences.TuneEthernet); SetCheck(_autoRestore, "Restore after Fortnite exits", _preferences.AutoRestoreOnGameExit); SetCheck(_closeCodex, "Close Codex / Native in Tourney", _preferences.CloseCodex);
        foreach (var check in LegacyChecks())
        {
            check.CheckedChanged += (_, _) => { if (!_loading) { _reviewedPlan = null; _preferences.ReviewedPlanFingerprint = ""; InvalidateTweakAssessment(); UpdateSelectionSummary(); } };
            _toolTip.SetToolTip(check, "Requested advanced Tourney option. The goal-driven plan revalidates eligibility, dependencies and disruptive preparation before activation.");
        }
        _fortniteOnly.CheckedChanged += async (_, _) => { if (!_loading && !_preview && !_engine.IsActive) await RefreshAppsAsync(); };
        _headroom.CheckedChanged += async (_, _) => { if (!_loading && !_preview && !_engine.IsActive) await RefreshAppsAsync(); };
        _analyze.Text = "Advanced controls"; _analyze.Width = 174; _analyze.Click += async (_, _) => { if (!_preview) await AnalyzeAsync(); };
        _diagnostics.Text = "Frame diagnostics"; _diagnostics.Width = 174; _diagnostics.Click += (_, _) => { if (!_preview) ShowDiagnostics(); };
        _features.Text = "View activity"; _features.Width = 140; _features.Click += (_, _) => SelectNavigation("Activity");
        StyleButton(_toggle, true);
    }
    private CheckBox[] LegacyChecks() => [_force, _priority, _silence, _power, _game, _fortniteOnly, _headroom, _keepClosed, _services, _network, _ethernet, _autoRestore, _closeCodex];
    private void UpdateNavigationStatus()
    {
        var state = VisibleSession; bool active = state != null; bool recovery = active && state!.Phase != "Active";
        bool tweak = string.Equals(state?.Workflow, "Tweak", StringComparison.Ordinal);
        bool strip = string.Equals(state?.Workflow, "Strip", StringComparison.Ordinal);
        bool internet = string.Equals(state?.Workflow, "Internet", StringComparison.Ordinal);
        bool stopped = VisibleTourneyStopped;
        _sessionBadge.Text = stopped ? "OFF / RECOVERY" : UiBusy ? "WORKING" : recovery ? "RECOVERY" : active ? internet ? state?.InternetCalibration?.Restored == true ? "NET READY" : "NET ACTIVE" : strip ? "STRIP ACTIVE" : tweak ? "TWEAKS ACTIVE" : "TOURNEY ON" : "READY";
        _sessionBadge.ForeColor = recovery ? UiTheme.Amber : UiTheme.Cyan;
        _sessionMetric.Text = stopped ? "Stopped / recovery pending" : recovery ? "Recovery pending" : active ? internet ? state?.InternetCalibration?.Restored == true ? "Original NIC restored" : "NIC changes active" : strip ? "Features stripped" : tweak ? "Tweaks active" : "Tourney active" : "No active session";
        _goalMetric.Text = internet ? "Internet tune" : strip ? "Optional features" : PlannerDialog.GoalName(state?.Options.Intent.Goal ?? _preferences.Intent.Goal);
        _protectionMetric.Text = "Discord kept";
        _undoTop.Text = _startingTourney ? "Cancel start" : stopped ? "Retry recovery" : active ? "Stop / restore" : "Undo session";
        _undoTop.Enabled = (active || _startingTourney || (!_preview && _engine.ActivationBusy)) && !_updateInProgress && !_reviewDialogOpen;
        _featureList.BeginUpdate(); _featureList.Items.Clear();
        foreach (var result in state?.Features ?? _lastTweakResults)
        {
            var item = new ListViewItem(result.Name); item.SubItems.Add(result.State.ToString()); item.SubItems.Add(result.Detail);
            item.ForeColor = result.State is FeatureState.Denied or FeatureState.Failed ? UiTheme.Amber : UiTheme.Text; _featureList.Items.Add(item);
        }
        if (_featureList.Items.Count == 0) _featureList.Items.Add(new ListViewItem("No recorded changes") { SubItems = { "Ready", "Choose Tweak PC to prepare optimizations, or review the Tourney profile." } });
        _featureList.EndUpdate(); UpdateTweakControls(); UpdateStripControls(); UpdateInternetControls(); UpdateExperimentalControls(); UpdatePrivacyControls(); UpdateUpdateControls();
    }
}
