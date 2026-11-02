using System.Drawing;
using System.Windows.Forms;

namespace TourneyMode;

internal sealed class InternetTestConsentDialog : Form
{
    private readonly CheckBox _consent = new() { Text = "I accept brief Ethernet reconnects during these temporary tests.", AutoSize = true, ForeColor = UiTheme.Amber };
    private readonly ThemeButton _start = new() { Text = "Start adaptive test", Primary = true, Width = 210, Enabled = false, DialogResult = DialogResult.OK };
    internal InternetTestConsentDialog(InternetTestOptions options)
    {
        Text = "Review the internet test"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(780, 570); MinimumSize = new Size(680, 500); BackColor = UiTheme.Background; ForeColor = UiTheme.Text; Font = new Font("Segoe UI", 10); Padding = new Padding(24); ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 }; layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); Controls.Add(layout);
        layout.Controls.Add(UiTheme.Label("Test supported settings on this PC", 20, true), 0, 0);
        string load = options.EnableHttpsLoad ? "HTTPS load is ON: up to " + (options.HttpsLoadBytesPerMeasurement / (1024d * 1024)).ToString("0.#") + " MiB per measurement and " + (options.MaximumHttpsLoadBytesTotal / (1024d * 1024)).ToString("0.#") + " MiB total. Other traffic may be affected during the bounded transfer."
            : "HTTPS load is OFF. This is a bounded latency/stability test, not a connection-speed test.";
        var exact = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BorderStyle = BorderStyle.None, BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Font = new Font("Segoe UI", 10),
            Text = options.Mode + " mode · up to " + options.MaximumCandidates + " supported candidates · " + options.Repetitions + " repetitions\r\n\r\n"
                + "The test reads the wired adapter's actual capabilities, creates and verifies a Windows restore point, and saves original settings before any trial write. Supported RSS queue/processor and adapter candidates can briefly reconnect Ethernet.\r\n\r\n"
                + "Each candidate is compared with surrounding original-setting periods. Ping, traced local processing, and jitter/loss stability have equal weight. Unknown processing evidence stays unknown and blocks an all-goal winner.\r\n\r\n"
                + load + "\r\n\r\nOriginals are restored after testing. Applying a qualified best-tested result is a separate choice. Keep this app open until test recovery finishes. Fortnite and other optimization sessions must stay closed." };
        layout.Controls.Add(exact, 0, 1); _consent.Dock = DockStyle.Fill; _consent.CheckedChanged += (_, _) => _start.Enabled = _consent.Checked; layout.Controls.Add(_consent, 0, 2);
        var cancel = new ThemeButton { Text = "Cancel", Width = 110, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) }; actions.Controls.Add(_start); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 3); CancelButton = cancel;
    }
}

internal sealed class InternetApplyReviewDialog : Form
{
    private readonly CheckBox _provisionalConsent = new() { Text = "I understand local processing is unmeasured and this choice is experimental.", AutoSize = true, ForeColor = UiTheme.Amber };
    internal InternetApplyReviewDialog(InternetCandidate candidate, string exactSettings, InternetTestReport report, bool provisional = false)
    {
        Text = provisional ? "Review experimental provisional settings" : "Review best-tested settings"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(760, 580); MinimumSize = new Size(680, 500); BackColor = UiTheme.Background; ForeColor = UiTheme.Text; Font = new Font("Segoe UI", 10); Padding = new Padding(24); ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 }; layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, provisional ? 58 : 0)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); Controls.Add(layout);
        layout.Controls.Add(UiTheme.Label(provisional ? "Experimental · local processing is unmeasured" : "Apply the qualified best-tested candidate", 18, true, provisional ? UiTheme.Amber : UiTheme.Text), 0, 0);
        var detail = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BorderStyle = BorderStyle.None, BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Font = new Font("Segoe UI", 10),
            Text = candidate.Title + "\r\n" + exactSettings + "\r\n\r\n" + report.Recommendation + "\r\n" + report.Confidence
                + (provisional ? "\r\n\r\nThis candidate is provisional from ping and jitter/loss stability only. Local packet-processing latency is UNKNOWN. It does not satisfy all three goals and could be worse for the unmeasured objective. You are explicitly choosing an experimental override."
                    : "\r\n\r\nAll three required objectives must be measured with repeatable comparisons and no qualifying regression. Best tested does not mean an absolute optimum or a guaranteed lower Fortnite ping.")
                + "\r\n\r\nThe current adapter and original settings will be checked again. A verified restore point and recovery record are required before applying. Ethernet may reconnect. Restore originals returns the recorded baseline." };
        layout.Controls.Add(detail, 0, 1);
        var apply = new ThemeButton { Name = "ApplyInternetSettings", Text = provisional ? "Apply experimental settings" : "Apply these settings", Primary = true, Width = 250, Enabled = !provisional, DialogResult = DialogResult.OK }; var cancel = new ThemeButton { Text = "Cancel", Width = 110, DialogResult = DialogResult.Cancel };
        if (provisional) { _provisionalConsent.Dock = DockStyle.Fill; _provisionalConsent.CheckedChanged += (_, _) => apply.Enabled = _provisionalConsent.Checked; layout.Controls.Add(_provisionalConsent, 0, 2); }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) }; actions.Controls.Add(apply); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 3); CancelButton = cancel;
    }
}
