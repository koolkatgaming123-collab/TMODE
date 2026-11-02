using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private readonly Label _privacyStatus = new();
    private readonly ThemeButton _privacySelect = new() { Text = "Select & verify tool", Width = 190 };
    private readonly ThemeButton _privacyOpen = new() { Text = "Open verified tool", Width = 190, Enabled = false };
    private OOSToolInspection? _privacyInspection;
    private bool _privacyLaunchStarted;

    private void BuildPrivacyToolPanel(TableLayoutPanel stack)
    {
        var card = Card("O&O ShutUp10++ · optional external tool", "Privacy settings are reviewed and applied in O&O's own window. Tourney Mode does not silently apply privacy settings and cannot verify or undo them. Use only the vendor's Recommended choices and retain its undo history.", out var body);
        UiTheme.Add(body, Paragraph("Download the official tool separately, then select it here. Its signature, product and supported version are verified before launch. A verified Windows restore point is required before it opens."));
        var download = new ThemeButton { Text = "Official download", Width = 180 }; download.Click += (_, _) => { if (!_preview) OpenOfficialGuideUrl(OOSPrivacy.OfficialProductUrl); };
        var manual = new ThemeButton { Text = "Vendor instructions", Width = 190 }; manual.Click += (_, _) => { if (!_preview) OpenOfficialGuideUrl(OOSPrivacy.OfficialManualUrl); };
        UiTheme.Add(body, Actions(download, manual));
        _privacyStatus.Text = "No external tool selected. Supported version: " + OOSPrivacy.SupportedVersion; _privacyStatus.AutoSize = true; _privacyStatus.ForeColor = UiTheme.Muted; _privacyStatus.Margin = new Padding(0, 14, 0, 8); UiTheme.Add(body, _privacyStatus);
        _privacySelect.Click += async (_, _) => { if (!_preview) await SelectPrivacyToolAsync(); };
        _privacyOpen.Click += async (_, _) => { if (!_preview) await OpenPrivacyToolAsync(); };
        UiTheme.Add(body, Actions(_privacySelect, _privacyOpen)); UiTheme.Add(stack, card);
    }
    private async Task SelectPrivacyToolAsync()
    {
        if (_preview || _busy || _reviewDialogOpen || _updateInProgress || _engine.IsActive) return;
        using var picker = new OpenFileDialog { Title = "Select the official O&O ShutUp10++ executable", Filter = "O&O executable (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        string path = picker.FileName;
        await RunOperationAsync(async () =>
        {
            _privacyInspection = await Task.Run(() => new OOSPrivacy().Inspect(path));
            _privacyLaunchStarted = false;
            _privacyStatus.Text = _privacyInspection.Message + (_privacyInspection.Verified ? "\r\nVersion " + _privacyInspection.Version + " · " + _privacyInspection.Publisher : "");
        });
    }
    private async Task OpenPrivacyToolAsync()
    {
        var inspection = _privacyInspection;
        if (_preview || _busy || _reviewDialogOpen || _updateInProgress || _engine.IsActive || inspection?.Verified != true || _privacyLaunchStarted) return;
        if (MessageBox.Show(this, "Open the separately verified O&O tool?\r\n\r\nTourney Mode must create and verify a Windows restore point first. Review Recommended settings in O&O's own interface and retain its undo history. Tourney Mode cannot verify or undo changes made there.", "Open external privacy tool", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        await RunOperationAsync(async () =>
        {
            try
            {
                _privacyStatus.Text = "Creating a Windows restore point and rechecking the external tool…";
                var result = await _engine.OpenPrivacyToolAsync(inspection.ExecutablePath);
                _privacyLaunchStarted = result.Started; _privacyStatus.Text = result.Message; AppendLog("External privacy tool: " + result.Message);
            }
            catch (Exception error) { _privacyStatus.Text = "Tool was not opened. " + error.Message + " Enable Windows System Protection or run as administrator if the required restore point could not be verified."; throw; }
        });
    }
    private void UpdatePrivacyControls()
    {
        bool idle = !UiBusy && !_updateInProgress && !VisibleSessionActive && !_reviewDialogOpen;
        _privacySelect.Enabled = idle;
        _privacyOpen.Enabled = idle && _privacyInspection?.Verified == true && !_privacyLaunchStarted;
    }
}
