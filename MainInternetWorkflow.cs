using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private InternetTestOptions InternetChoices() => new()
    {
        Mode = _internetMode.Value, MaximumCandidates = _internetMode.Value == InternetTestMode.Quick ? 4 : 12,
        OverallTimeoutSeconds = _internetMode.Value == InternetTestMode.Quick ? 300 : 1200,
        EnableHttpsLoad = _internetLoaded.Checked, HttpsLoadBytesPerMeasurement = 1024 * 1024, MaximumHttpsLoadBytesTotal = 32L * 1024 * 1024
    };
    private IProgress<InternetTestProgress> InternetProgressReporter() => new Progress<InternetTestProgress>(progress =>
    {
        if (IsDisposed || !_busy) return;
        _internetMessage.Text = progress.Phase + " · " + progress.Message;
        _internetProgress.Value = progress.Total > 0 ? (int)Math.Clamp(100d * progress.Completed / progress.Total, 0, 100) : 0;
        _internetProgressText.Text = progress.Total > 0 ? progress.Completed + " / " + progress.Total + " bounded steps" : "Preparing and preserving the original connection settings";
    });
    private async Task StartInternetTestAsync()
    {
        if (_preview || _busy || _updateInProgress || _reviewDialogOpen || _engine.InternetBusy || _engine.IsActive) return;
        if (_engine.GameClientRunning) { _internetMessage.Text = "Close Fortnite before starting a network test."; return; }
        var options = InternetChoices();
        using (var review = new InternetTestConsentDialog(options))
        {
            _reviewDialogOpen = true;
            try { if (review.ShowDialog(this) != DialogResult.OK) return; }
            finally { _reviewDialogOpen = false; }
        }
        using var cancellation = new CancellationTokenSource(); _internetCancellation = cancellation;
        _internetReport = null; _internetAllowProvisional.Checked = false; _internetResults.Items.Clear(); _internetEvidence.Clear();
        try
        {
            await RunOperationAsync(async () =>
            {
                try
                {
                    _internetMessage.Text = "Preparing a verified restore point and network recovery record…"; var progress = InternetProgressReporter();
                    var report = await Task.Run(() => _engine.CalibrateInternetAsync(options, cancellation.Token, progress));
                    PopulateInternetReport(report); AppendLog("Internet comparison: " + report.Recommendation + " " + report.Confidence);
                }
                catch (OperationCanceledException)
                {
                    _internetMessage.Text = _engine.State?.InternetCalibration?.Restored == true ? "Test canceled. Originals restored; choose Restore originals / finish to close the session." : "Test canceled. Review recovery and restore the recorded originals.";
                }
                catch (Exception error)
                {
                    _internetMessage.Text = "Internet test needs attention. " + error.Message;
                    _internetDetail.Text = "Do not start another test while recovery is pending. Use Restore originals / finish. A verified Windows restore point is required; enable System Protection or run as administrator if that check failed."; throw;
                }
            });
        }
        finally { _internetCancellation = null; if (!IsDisposed) UpdateState(); }
    }
    private async Task ApplyInternetResultAsync()
    {
        var report = _internetReport;
        if (_preview || _busy || _updateInProgress || _reviewDialogOpen || _engine.InternetBusy || _engine.State?.Workflow != "Internet" || _engine.State?.InternetCalibration?.Restored != true || report == null || !InternetReportMatchesChoices) return;
        bool provisional = !HasQualifiedInternetChoice && HasAllowedProvisionalInternetChoice;
        var candidate = provisional ? report.ProvisionalWinner : HasQualifiedInternetChoice ? report.Winner : null; if (candidate == null) return;
        using (var review = new InternetApplyReviewDialog(candidate, InternetCandidateSettings(candidate), report, provisional))
        {
            _reviewDialogOpen = true;
            try { if (review.ShowDialog(this) != DialogResult.OK) return; }
            finally { _reviewDialogOpen = false; }
        }
        using var cancellation = new CancellationTokenSource(); _internetCancellation = cancellation;
        try
        {
            await RunOperationAsync(async () =>
            {
                try
                {
                    var progress = InternetProgressReporter();
                    await Task.Run(() => _engine.ApplyInternetBestAsync(report, cancellation.Token, progress, allowProvisional: provisional));
                    _internetMessage.Text = "Apply operation finished. Recorded state: " + (_engine.State?.InternetCalibration?.Phase ?? "session finished");
                    _internetProgressText.Text = "Use Restore originals / finish to return the recorded baseline. This does not establish a lower Fortnite ping or input latency.";
                }
                catch (OperationCanceledException) { _internetMessage.Text = "Apply canceled. Review recovery and restore originals before another test."; }
                catch (Exception error) { _internetMessage.Text = "Apply needs attention. " + error.Message; throw; }
            });
        }
        finally { _internetCancellation = null; if (!IsDisposed) UpdateState(); }
    }
    private async Task RestoreInternetOriginalsAsync()
    {
        if (_preview || _busy || _updateInProgress || _reviewDialogOpen || _engine.InternetBusy || _engine.State?.Workflow != "Internet") return;
        await RunOperationAsync(async () =>
        {
            try
            {
                _internetMessage.Text = "Restoring recorded originals and finishing the session…";
                await Task.Run(() => _engine.RestoreInternetAsync());
                _internetMessage.Text = _engine.State?.Workflow == "Internet" ? "Restore attempt finished. Pending items remain recorded for recovery." : "Original settings restored and the network session finished.";
            }
            catch (Exception error) { _internetMessage.Text = "Restore needs attention. " + error.Message; throw; }
        });
    }
}
