using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private bool _guideHasScan;
    internal bool GuideHasScan => _guideHasScan;
    private readonly TableLayoutPanel _guideContent = UiTheme.Stack();
    private readonly Label _guideNotice = new();

    private void BuildGuidePage(Panel page)
    {
        var stack = UiTheme.Stack(); page.Controls.Add(stack);
        var intro = Card("Your hardware and BIOS guide", "Recommendations use the facts from your latest Tweak PC scan. Firmware and BIOS changes are manual: this app does not flash firmware or change BIOS settings.", out var introBody);
        _guideNotice.Text = "Scan first. Choose Tweak PC to identify this machine and prepare its guidance."; _guideNotice.AutoSize = true; _guideNotice.ForeColor = UiTheme.Muted; UiTheme.Add(introBody, _guideNotice);
        var scan = new ThemeButton { Text = "Tweak PC", Primary = true, Width = 155 }; scan.Click += async (_, _) => { SelectNavigation("Tweak"); if (!_preview) await StartTweakScanAsync(); }; UiTheme.Add(introBody, Actions(scan)); UiTheme.Add(stack, intro); UiTheme.Add(stack, _guideContent);
    }
    private void PopulateOptimizationGuide(PcAnalysis analysis)
    {
        _guideHasScan = true;
        foreach (Control control in _guideContent.Controls.Cast<Control>().ToArray()) control.Dispose();
        _guideContent.Controls.Clear(); _guideContent.RowStyles.Clear(); _guideContent.RowCount = 0;
        var facts = analysis.Hardware;
        _guideNotice.Text = facts.MotherboardManufacturer + " " + facts.MotherboardModel + "\r\nBIOS: " + facts.Bios + " · Scan " + analysis.CapturedUtc.ToLocalTime().ToString("g");
        foreach (var item in OptimizationGuide.Build(analysis))
        {
            var card = Card(item.Title, item.Recommendation, out var body);
            UiTheme.Add(body, UiTheme.Label(item.Area.ToUpperInvariant(), 8, true, UiTheme.Cyan));
            UiTheme.Add(body, Paragraph("Detected: " + item.Detected));
            UiTheme.Add(body, Paragraph("Why: " + item.Why));
            var tradeoff = Paragraph("Tradeoff: " + item.Tradeoff); tradeoff.ForeColor = UiTheme.Amber; UiTheme.Add(body, tradeoff);
            UiTheme.Add(body, UiTheme.Label((item.RequiresManualAction ? "Manual action" : "Review guidance") + (item.NeedsRestart ? " · Restart required" : "") + " · " + item.Confidence, 9, false, UiTheme.Muted));
            foreach (string source in item.SourceUrls)
            {
                string url = source; if (!IsOfficialGuideUrl(url)) continue;
                var open = new ThemeButton { Text = "Open official source", Width = 205, AccessibleName = "Open official source for " + item.Title };
                _toolTip.SetToolTip(open, url); open.Click += (_, _) => { if (!_preview) OpenOfficialGuideUrl(url); }; UiTheme.Add(body, Actions(open));
            }
            UiTheme.Add(_guideContent, card);
        }
    }
    private static bool IsOfficialGuideUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort || address.UserInfo.Length != 0) return false;
        string host = address.IdnHost;
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return address.AbsolutePath.Equals("/GameTechDev/PresentMon", StringComparison.OrdinalIgnoreCase) || address.AbsolutePath.StartsWith("/GameTechDev/PresentMon/", StringComparison.OrdinalIgnoreCase);
        return new[] { "intel.com", "amd.com", "nvidia.com", "microsoft.com", "asus.com", "msi.com", "gigabyte.com", "asrock.com", "dell.com", "hp.com", "lenovo.com", "acer.com", "epicgames.com", "fortnite.com", "unrealengine.com", "oo-software.com" }
            .Any(official => host.Equals(official, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + official, StringComparison.OrdinalIgnoreCase));
    }
    private void OpenOfficialGuideUrl(string url)
    {
        if (_preview || !IsOfficialGuideUrl(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) { AppendLog("Official source could not be opened: " + error.Message); }
    }
}
