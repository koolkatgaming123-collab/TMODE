using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace TourneyMode;

/// <summary>Explicit diagnostic import/capture only; never starts from a preview or timer.</summary>
public sealed class DiagnosticsDialog : Form
{
    private readonly string _dataDirectory;
    private readonly NumericUpDown _target = new() { Minimum = 1, Maximum = 1000, Value = 120, Increment = 10, Width = 90 };
    private readonly NumericUpDown _seconds = new() { Minimum = 10, Maximum = 120, Value = 30, Increment = 10, Width = 75 };
    private readonly TextBox _tool = new() { Width = 500, ReadOnly = true };
    private readonly RichTextBox _text = new() { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, Font = new Font("Consolas", 9.5f) };
    private readonly Button _import = new() { Text = "Import CSV...", AutoSize = true };
    private readonly Button _capture = new() { Text = "Capture Fortnite", AutoSize = true };
    private readonly Button _choose = new() { Text = "Choose PresentMon...", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Stop capture", AutoSize = true, Enabled = false };
    private readonly Button _save = new() { Text = "Save report...", AutoSize = true, Enabled = false };
    private CancellationTokenSource? _cancellation;
    private FrameDiagnosticReport? _report;
    private bool _busy;
    private bool _closeWhenDone;

    public DiagnosticsDialog(string dataDirectory, int suggestedTargetFps = 0)
    {
        _dataDirectory = dataDirectory;
        _target.Value = suggestedTargetFps is >= 1 and <= 1000 ? suggestedTargetFps : 120;
        string stagedTool = Path.Combine(AppContext.BaseDirectory, "Tools", "PresentMon.exe");
        if (File.Exists(stagedTool)) _tool.Text = stagedTool;
        Text = "Frame diagnostics - explicit capture or CSV import";
        StartPosition = FormStartPosition.CenterParent; Width = 980; Height = 740; MinimumSize = new Size(720, 500); Font = new Font("Segoe UI", 10f);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(root);
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Import existing PresentMon data, or explicitly record a short Fortnite sample. The target defines a diagnostic frame budget; it does not change the game's FPS cap. Software frame metrics do not measure complete click-to-photon latency." }, 0, 0);
        var controls = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 3; row++) controls.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var parameters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        parameters.Controls.Add(new Label { Text = "Target FPS", AutoSize = true, Padding = new Padding(0, 5, 2, 0) }); parameters.Controls.Add(_target);
        parameters.Controls.Add(new Label { Text = "Capture seconds", AutoSize = true, Padding = new Padding(12, 5, 2, 0) }); parameters.Controls.Add(_seconds);
        var captureActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        captureActions.Controls.Add(_import); captureActions.Controls.Add(_capture); captureActions.Controls.Add(_cancel);
        var toolRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        toolRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); toolRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tool.Width = 120; _tool.MinimumSize = new Size(120, 0); _tool.Dock = DockStyle.Fill;
        toolRow.Controls.Add(_choose, 0, 0); toolRow.Controls.Add(_tool, 1, 0);
        controls.Controls.Add(parameters, 0, 0); controls.Controls.Add(captureActions, 0, 1); controls.Controls.Add(toolRow, 0, 2); root.Controls.Add(controls, 0, 1);
        _text.Text = "No capture is running. Import or capture only when you choose.\r\n\r\nCapture uses the selected official Intel PresentMon 2.6.0 x64 executable, verified by signature and known hash. It changes no tuning settings and does not focus or control Fortnite. A five-second countdown lets you switch to the game yourself. Missing telemetry and uncertain causes remain explicit in the report.";
        root.Controls.Add(_text, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close(); actions.Controls.Add(close); actions.Controls.Add(_save); root.Controls.Add(actions, 0, 3);
        _choose.Click += (_, _) => ChooseTool();
        _import.Click += async (_, _) => await ImportAsync();
        _capture.Click += async (_, _) => await CaptureAsync();
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _save.Click += (_, _) => SaveReport();
        FormClosing += (_, e) => { if (!_busy) return; _closeWhenDone = true; _cancellation?.Cancel(); e.Cancel = true; };
    }

    private void ChooseTool()
    {
        using var picker = new OpenFileDialog { Filter = "PresentMon executable|*.exe", CheckFileExists = true, Title = "Choose official Intel PresentMon 2.6.0 x64" };
        if (picker.ShowDialog(this) == DialogResult.OK) _tool.Text = picker.FileName;
    }
    private async Task ImportAsync()
    {
        if (_busy) return;
        using var picker = new OpenFileDialog { Filter = "PresentMon CSV|*.csv", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        string path = picker.FileName; int target = (int)_target.Value;
        await RunAsync(async () => _report = await Task.Run(() => FrameDiagnostics.AnalyzeCsv(path, target)));
    }
    private async Task CaptureAsync()
    {
        if (_busy) return;
        if (!File.Exists(_tool.Text)) { MessageBox.Show(this, "Choose an existing official Intel PresentMon executable first. No tool is downloaded automatically.", "Capture tool required", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        int seconds = (int)_seconds.Value, target = (int)_target.Value; string tool = _tool.Text;
        if (MessageBox.Show(this, $"Record the current Fortnite scene for {seconds} seconds?\r\n\r\nAfter clicking OK, switch to Fortnite during the five-second countdown. The tool will not change focus, send game input or alter tuning. Stop capture is available here; closing this dialog also requests cancellation.", "Start explicit frame capture", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _cancellation = new CancellationTokenSource();
        await RunAsync(async () =>
        {
            _text.Text = "Capture begins in five seconds. Switch to Fortnite yourself; no focus or game input will be automated.";
            await Task.Delay(TimeSpan.FromSeconds(5), _cancellation.Token);
            _text.Text = $"Capturing Fortnite for {seconds} seconds. No game focus/input or tuning changes are being made. Return here after capture to inspect the report.";
            string folder = Path.Combine(_dataDirectory, "diagnostics"); Directory.CreateDirectory(folder);
            string output = Path.Combine(folder, "Fortnite-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".csv");
            _report = await Task.Run(() => FrameDiagnostics.CaptureAsync(tool, output, seconds, target, _cancellation.Token));
        });
        _cancellation.Dispose(); _cancellation = null;
    }
    private async Task RunAsync(Func<Task> action)
    {
        _busy = true; _report = null; SetControls();
        try { await action(); if (_report != null) _text.Text = FrameDiagnostics.Format(_report); }
        catch (OperationCanceledException) { _text.Text = "Capture cancelled. Tuning settings were not changed. Any retained CSV can be imported for inspection."; }
        catch (Exception e) { _text.Text = "Diagnostic needs attention: " + e.Message; }
        finally { _busy = false; SetControls(); if (_closeWhenDone) Close(); }
    }
    private void SetControls()
    {
        _import.Enabled = _capture.Enabled = _choose.Enabled = _target.Enabled = _seconds.Enabled = !_busy;
        _cancel.Enabled = _busy && _cancellation != null;
        _save.Enabled = !_busy && _report != null;
    }
    private void SaveReport()
    {
        if (_report == null) return;
        using var picker = new SaveFileDialog { Filter = "JSON report|*.json|Text report|*.txt", FileName = "Tourney-Mode-Frame-Diagnostics.json", AddExtension = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(picker.FileName, picker.FilterIndex == 2 ? FrameDiagnostics.Format(_report) : JsonSerializer.Serialize(_report, SystemAnalysis.Json)); }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Could not save report", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
