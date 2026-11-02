using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private readonly ThemeSwitch _experimentalWired = new() { Text = "Wired NIC latency profile", AccessibleName = "Wired NIC latency profile" };
    private readonly ThemeSwitch _experimentalDscp = new() { Text = "Outbound DSCP 46 marking", AccessibleName = "Outbound DSCP 46 marking" };
    private readonly ThemeSwitch _experimentalGovernor = new() { Text = "Measured background CPU governor", AccessibleName = "Measured background CPU governor" };

    private void BuildExperimentalPage(Panel page)
    {
        var stack = UiTheme.Stack(); page.Controls.Add(stack);
        var intro = Card("Choose your experiments", "These switches start off. Turning one on requests it for your next Tweak PC scan and review; it changes nothing immediately. Restore changes returns the settings recorded by an active run. Switches stay locked while a run is active.", out var introBody);
        UiTheme.Add(introBody, Paragraph("An enabled experiment is a request, not a measured improvement. Hardware support and safety checks decide what can be applied.")); UiTheme.Add(stack, intro);
        AddExperiment(stack, "Wired NIC latency profile", _experimentalWired,
            "Requests Energy Efficient Ethernet off, interrupt moderation off and Receive Side Scaling on where the wired adapter exposes supported settings.",
            "The adapter can reconnect. Less interrupt batching can increase CPU and DPC work; the effect on latency and frame times needs a matched test.");
        AddExperiment(stack, "Outbound DSCP 46 marking", _experimentalDscp,
            "Requests a temporary outbound priority mark for supported Fortnite traffic.",
            "Routers and the wider network may ignore or rewrite the mark. This does not reserve bandwidth, increase line speed or establish a lower ping.");
        AddExperiment(stack, "Measured background CPU governor", _experimentalGovernor,
            "Requests Below Normal priority only for recognized apps with measured background CPU activity.",
            "Foreground, kept, streaming and recording dependencies stay excluded. A background task can finish more slowly. No real-time game priority or arbitrary process termination is requested.");
        BuildPrivacyToolPanel(stack);
        _experimentalWired.Checked = _preferences.ExperimentalWiredNetwork; _experimentalDscp.Checked = _preferences.ExperimentalDscp; _experimentalGovernor.Checked = _preferences.ExperimentalBackgroundGovernor;
        foreach (var toggle in new[] { _experimentalWired, _experimentalDscp, _experimentalGovernor })
            toggle.CheckedChanged += (_, _) =>
            {
                if (_loading) return;
                _reviewedPlan = null; _preferences.ReviewedPlanFingerprint = ""; InvalidateTweakAssessment(); UpdateSelectionSummary();
            };
    }
    private static void AddExperiment(TableLayoutPanel stack, string title, ThemeSwitch toggle, string action, string tradeoff)
    {
        var card = Card(title, action, out var body);
        UiTheme.Add(body, Actions(toggle, UiTheme.Label("Requested for the next reviewed Tweak run", 9, false, UiTheme.Muted)));
        var detail = Paragraph("Tradeoff: " + tradeoff); detail.ForeColor = UiTheme.Amber; detail.Margin = new Padding(0, 12, 0, 0); UiTheme.Add(body, detail); UiTheme.Add(stack, card);
    }
    private void UpdateExperimentalControls()
    {
        bool editable = !UiBusy && !_updateInProgress && !VisibleSessionActive && !_reviewDialogOpen;
        _experimentalWired.Enabled = _experimentalDscp.Enabled = _experimentalGovernor.Enabled = editable;
    }
}
