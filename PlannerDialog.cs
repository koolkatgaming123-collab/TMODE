using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace TourneyMode;

/// <summary>Review and save a goal-driven plan. This dialog never activates a mode.</summary>
public sealed class PlannerDialog : Form
{
    private readonly PcAnalysis _report;
    private readonly AppOptions _baseline;
    private readonly bool _gameRunning;
    private readonly bool _recoveryPending;
    private readonly ComboBox _goal = ChoiceBox();
    private readonly ComboBox _activity = ChoiceBox();
    private readonly ComboBox _power = ChoiceBox();
    private readonly NumericUpDown _target = new() { Minimum = 0, Maximum = 1000, Increment = 10, Width = 100 };
    private readonly CheckBox _matchSafe = ChoiceCheck("Match-safe: defer disruptive preparation while the game is running");
    private readonly CheckBox _network = ChoiceCheck("Allow a network experiment before play");
    private readonly CheckBox _heat = ChoiceCheck("Allow higher power use and heat");
    private readonly CheckBox _tearing = ChoiceCheck("Accept tearing when considering latency advice");
    private readonly CheckBox _applyFps = ChoiceCheck("Apply Fortnite FPS settings before play");
    private readonly CheckBox _competitive = ChoiceCheck("Competitive visuals + motion blur off");
    private readonly CheckBox _vsyncOff = ChoiceCheck("VSync OFF (accept tearing first)");
    private readonly CheckBox _quiet = ChoiceCheck("Quiet match: pause closures / retuning");
    private readonly CheckBox _keepScale = new() { Text = "Keep current", AutoSize = true, Checked = true };
    private readonly ComboBox _renderer = ChoiceBox();
    private readonly ComboBox _reflex = ChoiceBox();
    private readonly ComboBox _frameCap = ChoiceBox();
    private readonly ComboBox _gamePriority = ChoiceBox();
    private readonly NumericUpDown _gameFps = new() { Minimum = 30, Maximum = 1000, Value = 240, Width = 100 };
    private readonly NumericUpDown _scale = new() { Minimum = 50, Maximum = 100, Value = 100, Increment = 5, Width = 90 };
    private readonly ListBox _background = new() { Dock = DockStyle.Fill, Height = 95, HorizontalScrollbar = true, ForeColor = SystemColors.WindowText, BackColor = SystemColors.Window };
    private readonly RichTextBox _review = ReportBox();
    private readonly Label _preflight = new();
    private readonly Button _use = new();
    private bool _loading = true;
    private bool _reviewed;
    private bool _validPlan;
    public OptimizationPlan Plan { get; private set; } = new();
    public AppOptions? SelectedOptions { get; private set; }

    public PlannerDialog(PcAnalysis report, AppOptions options, bool gameRunning, bool recoveryPending)
    {
        _report = report;
        _baseline = CopyOptions(options);
        _baseline.Intent ??= new();
        _baseline.Intent.Fps ??= new();
        _gameRunning = gameRunning;
        _recoveryPending = recoveryPending;
        Text = "Analyze PC - goals and reviewed plan";
        BackColor = Color.FromArgb(14, 18, 27);
        ForeColor = Color.FromArgb(235, 240, 248);
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterParent;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 960);
        ClientSize = new Size(Math.Min(1000, Math.Max(720, area.Width - 100)), Math.Min(850, Math.Max(610, area.Height - 100)));
        MinimumSize = new Size(720, 610);
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildLayout();
        SetChoices(_goal, Enum.GetValues<OptimizationGoal>(), _baseline.Intent.Goal);
        SetChoices(_activity, Enum.GetValues<SessionActivity>(), _baseline.Intent.Activity);
        SetChoices(_power, Enum.GetValues<PowerPreference>(), _baseline.Intent.Power);
        _target.Value = Math.Clamp(_baseline.Intent.TargetFps, 0, 1000);
        _matchSafe.Checked = _baseline.Intent.MatchSafe;
        _network.Checked = _baseline.Intent.AllowNetworkExperiment;
        _heat.Checked = _baseline.Intent.AllowHigherHeat;
        _tearing.Checked = _baseline.Intent.AcceptTearing;
        var fpsOptions = _baseline.Intent.Fps;
        SetChoices(_renderer, Enum.GetValues<FortniteRenderer>(), fpsOptions.Renderer);
        SetChoices(_reflex, Enum.GetValues<FortniteReflex>(), fpsOptions.Reflex);
        SetChoices(_frameCap, Enum.GetValues<FortniteFrameCap>(), fpsOptions.FrameCap);
        SetChoices(_gamePriority, Enum.GetValues<FortnitePriority>(), fpsOptions.GamePriority);
        _applyFps.Checked = fpsOptions.ApplyGameProfile; _competitive.Checked = fpsOptions.CompetitiveVisuals;
        _vsyncOff.Checked = fpsOptions.DisableVSync; _quiet.Checked = fpsOptions.QuietDuringGame;
        _gameFps.Value = Math.Clamp(fpsOptions.TargetFps, 30, 1000); _scale.Value = fpsOptions.ResolutionPercent == 0 ? 100 : Math.Clamp(fpsOptions.ResolutionPercent, 50, 100);
        _keepScale.Checked = fpsOptions.ResolutionPercent == 0;
        foreach (var path in fpsOptions.BackgroundAppPaths) _background.Items.Add(path);
        foreach (var pair in ChoiceFields()) pair.Box.Name = pair.Name;
        _matchSafe.Checked = true; _matchSafe.Enabled = false;
        _matchSafe.Text = "Safe preparation: game file, power and network changes wait until Fortnite closes";
        _loading = false;
        RebuildPlan();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(layout);
        _preflight.Dock = DockStyle.Fill;
        _preflight.ForeColor = Color.FromArgb(245, 191, 111);
        _preflight.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_preflight, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var choices = new TabPage("1  Goal and constraints") { BackColor = BackColor, ForeColor = ForeColor, AutoScroll = true };
        var plan = new TabPage("2  Review plan") { BackColor = BackColor };
        var facts = new TabPage("PC facts and evidence") { BackColor = BackColor };
        tabs.TabPages.AddRange([choices, plan, facts]);
        var performance = new TabPage("FPS / latency") { BackColor = BackColor, ForeColor = ForeColor, AutoScroll = true };
        tabs.TabPages.Add(performance);
        BuildPerformanceTab(performance);
        layout.Controls.Add(tabs, 0, 1);
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(16) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(grid, "What matters most", _goal);
        AddRow(grid, "Activity to preserve", _activity);
        AddRow(grid, "Power preference", _power);
        var fps = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        fps.Controls.Add(_target);
        fps.Controls.Add(new Label { AutoSize = true, Text = "FPS target; 0 keeps your current limit", Padding = new Padding(4, 4, 0, 0) });
        AddRow(grid, "Frame-budget target", fps);
        foreach (var check in new[] { _matchSafe, _network, _heat, _tearing })
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            int row = grid.RowCount++;
            grid.Controls.Add(check, 0, row); grid.SetColumnSpan(check, 2);
        }
        var note = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Padding = new Padding(0, 14, 0, 8),
            Text = "Open FPS / latency for actual game settings, priority and approved background controls. The target on this page is diagnostic advice; the separate game-cap control changes Fortnite's saved limiter. Keep apps and Discord stay protected. No predicted gain is assigned from inventory alone." };
        int noteRow = grid.RowCount++; grid.Controls.Add(note, 0, noteRow); grid.SetColumnSpan(note, 2);
        choices.Controls.Add(grid);
        plan.Controls.Add(_review);
        var factsText = ReportBox(); factsText.Text = SystemAnalysis.Format(_report); facts.Controls.Add(factsText);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var close = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _use.Text = "Use this plan"; _use.AutoSize = true;
        _use.Click += (_, _) =>
        {
            if (_recoveryPending || !_reviewed || !_validPlan) return;
            try
            {
                SelectedOptions = PerformancePlanner.Apply(Plan, Choices());
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Plan could not be saved", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var save = new Button { Text = "Save analysis...", AutoSize = true };
        save.Click += (_, _) => SaveAnalysis();
        var review = new Button { Text = "Review decisions", AutoSize = true };
        review.Click += (_, _) => { tabs.SelectedTab = plan; _reviewed = true; _use.Enabled = !_recoveryPending && _validPlan; };
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedTab == plan) { _reviewed = true; _use.Enabled = !_recoveryPending && _validPlan; } };
        buttons.Controls.Add(close); buttons.Controls.Add(_use); buttons.Controls.Add(review); buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 2);
        CancelButton = close;
        foreach (var box in new[] { _goal, _activity, _power, _renderer, _reflex, _frameCap, _gamePriority }) box.SelectedIndexChanged += (_, _) => RebuildPlan();
        _target.ValueChanged += (_, _) => RebuildPlan();
        _gameFps.ValueChanged += (_, _) => RebuildPlan(); _scale.ValueChanged += (_, _) => RebuildPlan();
        foreach (var check in new[] { _matchSafe, _network, _heat, _tearing, _applyFps, _competitive, _vsyncOff, _quiet, _keepScale }) check.CheckedChanged += (_, _) => RebuildPlan();
    }

    private void BuildPerformanceTab(TabPage tab)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(16) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(grid, "Game profile", _applyFps); AddRow(grid, "Graphics", _competitive);
        AddRow(grid, "Game FPS limiter", _frameCap); AddRow(grid, "Target game FPS", _gameFps);
        var scale = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        scale.Controls.Add(_keepScale); scale.Controls.Add(_scale); AddRow(grid, "3D resolution %", scale);
        AddRow(grid, "Sync", _vsyncOff); AddRow(grid, "Fortnite CPU priority", _gamePriority);
        AddRow(grid, "Match overhead", _quiet);
        AddRow(grid, "Renderer reminder", _renderer); AddRow(grid, "Reflex reminder", _reflex);
        var note = new Label { AutoSize = true, MaximumSize = new Size(720, 0), Text = "Resolution: Keep current, or 50–100% to trade image sharpness for GPU work. Renderer and Reflex are manual in-game reminders: unverified config flags are preserved. File settings apply only with Fortnite closed and take effect on the next launch. Recovery preserves unrelated game edits. Quiet mode lets returning apps stay open during play; manage them before starting." };
        int row = grid.RowCount++; grid.Controls.Add(note, 0, row); grid.SetColumnSpan(note, 2);
        AddRow(grid, "Background CPU governor", _background);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var add = new Button { Text = "Add executable…", AutoSize = true, ForeColor = ForeColor };
        var remove = new Button { Text = "Remove selected", AutoSize = true, ForeColor = ForeColor };
        add.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Application executable|*.exe", CheckFileExists = true, Multiselect = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            foreach (string path in picker.FileNames)
                if (_background.Items.Count < 16 && !_background.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) _background.Items.Add(path);
            RebuildPlan();
        };
        remove.Click += (_, _) => { if (_background.SelectedIndex >= 0) { _background.Items.RemoveAt(_background.SelectedIndex); RebuildPlan(); } };
        actions.Controls.Add(add); actions.Controls.Add(remove); AddRow(grid, "", actions);
        var governorNote = new Label { AutoSize = true, MaximumSize = new Size(720, 0), Text = "Only explicit, eligible apps that remain open can receive Below normal CPU priority. Current foreground, capture, kept, protected and selected-for-close apps are excluded. Up to 32 process identities are prepared once; no EcoQoS, periodic retuning or forced CPU affinity." };
        row = grid.RowCount++; grid.Controls.Add(governorNote, 0, row); grid.SetColumnSpan(governorNote, 2);
        tab.Controls.Add(grid);
    }

    private AppOptions Choices()
    {
        var options = CopyOptions(_baseline);
        options.Intent = new UserIntent
        {
            Goal = _goal.SelectedItem is OptimizationGoal goal ? goal : OptimizationGoal.StableEndgames,
            Activity = _activity.SelectedItem is SessionActivity activity ? activity : SessionActivity.Gaming,
            Power = _power.SelectedItem is PowerPreference power ? power : PowerPreference.Automatic,
            TargetFps = (int)_target.Value, MatchSafe = _matchSafe.Checked,
            AllowNetworkExperiment = _network.Checked, AllowHigherHeat = _heat.Checked, AcceptTearing = _tearing.Checked,
            Fps = new FpsLatencyOptions { ApplyGameProfile = _applyFps.Checked, CompetitiveVisuals = _competitive.Checked,
                Renderer = _renderer.SelectedItem is FortniteRenderer renderer ? renderer : FortniteRenderer.KeepCurrent,
                Reflex = _reflex.SelectedItem is FortniteReflex reflex ? reflex : FortniteReflex.KeepCurrent,
                FrameCap = _frameCap.SelectedItem is FortniteFrameCap cap ? cap : FortniteFrameCap.KeepCurrent,
                TargetFps = (int)_gameFps.Value, ResolutionPercent = _keepScale.Checked ? 0 : (int)_scale.Value, DisableVSync = _vsyncOff.Checked,
                GamePriority = _gamePriority.SelectedItem is FortnitePriority priority ? priority : FortnitePriority.High,
                QuietDuringGame = _quiet.Checked, BackgroundAppPaths = _background.Items.Cast<string>().ToList() }
        };
        return options;
    }

    private void RebuildPlan()
    {
        if (_loading) return;
        _reviewed = false; _validPlan = false; _use.Enabled = false;
        try
        {
            Plan = PerformancePlanner.Build(_report, Choices(), _gameRunning, _recoveryPending);
            _validPlan = true;
            _review.Text = PerformancePlanner.Format(Plan);
            _preflight.Text = _recoveryPending
                ? "READ-ONLY: a session is active or has pending recovery. Inspect the plan and facts here; restore that session before saving a new plan."
                : "Open Review plan before saving these decisions. Use this plan only updates your choices; it does not turn Tourney Mode on or change this PC.";
        }
        catch (Exception e) { _use.Enabled = false; _review.Text = "Plan unavailable: " + e.Message; }
    }

    private void SaveAnalysis()
    {
        using var picker = new SaveFileDialog { Filter = "JSON analysis and plan|*.json", FileName = "Tourney-Mode-Reviewed-Plan.json", AddExtension = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(picker.FileName, JsonSerializer.Serialize(new { Analysis = _report, ReviewedPlan = Plan }, SystemAnalysis.Json)); }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Could not save analysis", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    public static AppOptions CopyOptions(AppOptions options) => JsonSerializer.Deserialize<AppOptions>(JsonSerializer.Serialize(options, SystemAnalysis.Json), SystemAnalysis.Json) ?? new();
    public static UserIntent CopyIntent(UserIntent intent) => JsonSerializer.Deserialize<UserIntent>(JsonSerializer.Serialize(intent, SystemAnalysis.Json), SystemAnalysis.Json) ?? new();
    public static string GoalName(OptimizationGoal goal) => goal switch { OptimizationGoal.StableEndgames => "Stable endgames", OptimizationGoal.LowestLatency => "Lowest latency", OptimizationGoal.HighestFps => "Highest FPS", _ => "Balanced" };
    public static string PowerName(PowerPreference power) => power switch { PowerPreference.Original => "Original / OEM", PowerPreference.Sustained => "Sustained performance", PowerPreference.Maximum => "Experimental maximum", _ => "Automatic for this PC" };
    private static ComboBox ChoiceBox() => new DrawnChoiceBox();
    private static CheckBox ChoiceCheck(string text) => new() { Text = text, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
    private static RichTextBox ReportBox() => new() { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, Font = new Font("Consolas", 9.5f), BackColor = Color.FromArgb(23, 30, 43), ForeColor = Color.FromArgb(235, 240, 248), BorderStyle = BorderStyle.None };
    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 9, 8, 0) }, 0, row);
        grid.Controls.Add(control, 1, row);
    }
    private static void SetChoices<T>(ComboBox box, T[] choices, T selected) where T : struct, Enum
    {
        box.DataSource = choices;
        box.Format += (_, e) => e.Value = ChoiceText(e.ListItem);
        box.SelectedItem = selected;
    }

    private static string ChoiceText(object? value) => value switch
    {
        OptimizationGoal goal => GoalName(goal), PowerPreference power => PowerName(power),
        FortniteRenderer.KeepCurrent => "Keep current renderer", FortniteRenderer.PerformanceDx11 => "Performance DX11 (manual)",
        FortniteRenderer.PerformanceDx12 => "Performance DX12 (manual)", FortniteRenderer.DirectX11 => "DirectX 11 (manual)", FortniteRenderer.DirectX12 => "DirectX 12 (manual)",
        FortniteReflex.KeepCurrent => "Keep current Reflex", FortniteReflex.On => "Reflex On (manual)", FortniteReflex.OnPlusBoost => "Reflex On + Boost (manual)",
        FortniteFrameCap.KeepCurrent => "Keep current game cap", FortniteFrameCap.Uncapped => "Uncapped", FortniteFrameCap.Target => "Use target game FPS",
        FortnitePriority.Normal => "Normal", FortnitePriority.AboveNormal => "Above normal", FortnitePriority.High => "High (if Windows permits)",
        SessionActivity.Streaming => "Gaming and streaming", SessionActivity.Recording => "Gaming and recording",
        SessionActivity.Gaming => "Gaming", null => "Choose an option", _ => value.ToString() ?? "Choose an option"
    };
    private (string Name, ComboBox Box)[] ChoiceFields() =>
    [ ("goal", _goal), ("activity", _activity), ("power", _power), ("renderer", _renderer), ("reflex", _reflex), ("gameCap", _frameCap), ("gamePriority", _gamePriority) ];
    private static bool OnSelectedTab(Control control)
    {
        for (Control? item = control; item != null; item = item.Parent)
            if (item is TabPage page && page.Parent is TabControl tabs && tabs.SelectedTab != page) return false;
        return true;
    }

    internal static void DrawSelection(Graphics graphics, Rectangle bounds, Font font, string text,
        bool enabled, bool highlighted = false, bool focused = false)
    {
        Color background = !enabled ? SystemColors.Control : highlighted ? SystemColors.Highlight : SystemColors.Window;
        Color foreground = !enabled ? SystemColors.GrayText : highlighted ? SystemColors.HighlightText : SystemColors.WindowText;
        using (var brush = new SolidBrush(background)) graphics.FillRectangle(brush, bounds);
        var textBounds = new Rectangle(bounds.X + 4, bounds.Y, Math.Max(0, bounds.Width - 8), bounds.Height);
        TextRenderer.DrawText(graphics, text, font, textBounds, foreground, background,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (focused) ControlPaint.DrawFocusRectangle(graphics, bounds, foreground, background);
    }

    // Native ComboBox WM_PRINT rendering can omit the selected text in hidden
    // DrawToBitmap previews. Drawing every item and the closed selection uses the
    // same explicit text path; an index of -1 falls back to the current selection.
    private sealed class DrawnChoiceBox : ComboBox
    {
        public int SelectionDrawCalls { get; private set; }
        public string LastDrawnSelection { get; private set; } = "";
        public int PreviewFallbackDrawCalls { get; private set; }
        public string LastPreviewFallbackSelection { get; private set; } = "";
        public DrawnChoiceBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList; DrawMode = DrawMode.OwnerDrawFixed;
            Dock = DockStyle.Fill; Margin = new Padding(0, 5, 0, 8); FormattingEnabled = true;
            ForeColor = SystemColors.WindowText; BackColor = SystemColors.Window;
            ItemHeight = Math.Max(22, Font.Height + 6);
        }
        public void ResetPreviewEvidence()
        {
            SelectionDrawCalls = 0; LastDrawnSelection = "";
            PreviewFallbackDrawCalls = 0; LastPreviewFallbackSelection = "";
        }
        public void DrawHiddenPreviewSelection(Graphics graphics, Rectangle bounds)
        {
            string text = ChoiceText(SelectedItem);
            DrawSelection(graphics, bounds, Font, text, Enabled);
            PreviewFallbackDrawCalls++; LastPreviewFallbackSelection = text;
        }
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            ItemHeight = Math.Max(22, Font.Height + 6);
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            int index = e.Index >= 0 && e.Index < Items.Count ? e.Index : SelectedIndex;
            object? value = index >= 0 && index < Items.Count ? Items[index] : SelectedItem;
            string text = ChoiceText(value);
            bool closedSelection = (e.State & DrawItemState.ComboBoxEdit) != 0 || !DroppedDown;
            bool highlighted = !closedSelection && (e.State & DrawItemState.Selected) != 0;
            DrawSelection(e.Graphics, e.Bounds, Font, text, Enabled, highlighted, (e.State & DrawItemState.Focus) != 0);
            if (closedSelection) { SelectionDrawCalls++; LastDrawnSelection = text; }
            base.OnDrawItem(e);
        }
    }

    internal void ResetChoicePreviewEvidence()
    {
        foreach (var choice in ChoiceFields().Select(pair => pair.Box).OfType<DrawnChoiceBox>()) choice.ResetPreviewEvidence();
    }
    internal void DrawMissingChoicePreviewSelections(Graphics bitmapGraphics)
    {
        foreach (var choice in ChoiceFields().Select(pair => pair.Box).OfType<DrawnChoiceBox>().Where(OnSelectedTab))
        {
            if (choice.SelectionDrawCalls > 0) continue;
            // Hidden HWND screen coordinates need not match managed form bounds.
            // Sum managed ancestor offsets and account for DrawToBitmap's frame.
            var origin = Point.Empty;
            for (Control? child = choice; child != null && child != this; child = child.Parent)
                origin.Offset(child.Left, child.Top);
            int frame = (Width - ClientSize.Width) / 2;
            origin.Offset(frame, Height - ClientSize.Height - frame);
            int arrow = Math.Max(20, (int)Math.Ceiling(20d * choice.DeviceDpi / 96d));
            var field = new Rectangle(origin, choice.ClientSize);
            if (!new Rectangle(Point.Empty, Size).Contains(field))
                throw new InvalidOperationException("Hidden choice preview lies outside the bitmap.");
            bitmapGraphics.FillRectangle(SystemBrushes.Window, field);
            ControlPaint.DrawBorder(bitmapGraphics, field, SystemColors.WindowFrame, ButtonBorderStyle.Solid);
            ControlPaint.DrawComboButton(bitmapGraphics, new Rectangle(field.Right - arrow - 1, field.Top + 1, arrow, field.Height - 2), ButtonState.Normal);
            var bounds = new Rectangle(origin.X + 2, origin.Y + 2,
                Math.Max(0, choice.ClientSize.Width - arrow - 4), Math.Max(0, choice.ClientSize.Height - 4));
            if (bounds.Width > 0 && bounds.Height > 0) choice.DrawHiddenPreviewSelection(bitmapGraphics, bounds);
        }
    }
    internal IReadOnlyList<ChoicePreviewEvidence> ChoicePreviewEvidenceSnapshot() => ChoiceFields()
        .Where(pair => OnSelectedTab(pair.Box)).Select(pair => ChoiceEvidence(pair.Name, pair.Box)).ToArray();
    private static ChoicePreviewEvidence ChoiceEvidence(string name, ComboBox box) => new(name,
        ChoiceText(box.SelectedItem), (box as DrawnChoiceBox)?.LastDrawnSelection ?? "", (box as DrawnChoiceBox)?.SelectionDrawCalls ?? 0,
        (box as DrawnChoiceBox)?.LastPreviewFallbackSelection ?? "", (box as DrawnChoiceBox)?.PreviewFallbackDrawCalls ?? 0);
    internal sealed record ChoicePreviewEvidence(string Name, string ExpectedText, string RenderedText, int SelectionDrawCalls,
        string PreviewFallbackText, int PreviewFallbackDrawCalls);
}

internal static class PlanActivationDialog
{
    public static bool Confirm(IWin32Window owner, string message, string plan)
    {
        using var dialog = new Form { Text = "Review Tourney Mode activation", StartPosition = FormStartPosition.CenterParent, Width = 880, Height = 720, MinimumSize = new Size(660, 440), Font = new Font("Segoe UI", 10f) };
        var text = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, Font = new Font("Consolas", 9.5f), Text = message + Environment.NewLine + Environment.NewLine + plan };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var enable = new Button { Text = "Turn ON with this plan", AutoSize = true, DialogResult = DialogResult.OK };
        actions.Controls.Add(cancel); actions.Controls.Add(enable); dialog.Controls.Add(text); dialog.Controls.Add(actions); dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}
