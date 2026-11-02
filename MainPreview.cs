namespace TourneyMode;

public sealed partial class MainForm
{
    internal static MainForm CreateSyntheticPreview(AppOptions options) => new(null!, true, options);

    internal void PrepareSyntheticPreview(string page, TweakAssessment? assessment, SessionState? session = null)
    {
        if (!_preview) throw new InvalidOperationException("Synthetic data is accepted only by preview instances.");
        _previewSession = session;
        _loading = true;
        try
        {
            _apps.Items.Clear();
            foreach (var app in new AppCandidate[]
            {
                new() { Id = "preview-browser", Name = "Browser (sample)", Detail = "Selected for your Tourney session; stays open during Tweak.", Processes = [new() { Id = 1 }, new() { Id = 2 }] },
                new() { Id = "preview-launcher", Name = "Game launcher (sample)", Detail = "Optional application; required Fortnite components stay protected.", DefaultSelected = false, Processes = [new() { Id = 3 }] },
                new() { Id = "preview-tools", Name = "Connected tools (sample)", Detail = "Kept running by the no-app-closure Tweak scope.", DefaultSelected = false, Processes = [new() { Id = 4 }] }
            })
            {
                var item = new System.Windows.Forms.ListViewItem(app.Name) { Tag = app, Checked = app.DefaultSelected };
                item.SubItems.Add(app.Processes.Count.ToString()); item.SubItems.Add(app.Detail); _apps.Items.Add(item);
            }
        }
        finally { _loading = false; }
        _tweakAssessment = assessment;
        if (assessment != null) PopulateTweakAssessment();
        _lastTweakResults = session?.Features.ToList() ??
        [new() { Name = "Windows gaming settings (sample)", State = FeatureState.Skipped, Detail = "Preview evidence only. No change was made." },
         new() { Name = "Fortnite saved settings (sample)", State = FeatureState.Deferred, Detail = "Synthetic recommendation awaiting review." }];
        _log.Text = "[Preview] Synthetic data only. No system scan, live tuning or update request was performed.\r\n[Preview] Exact changes remain available for review before Apply.\r\n";
        _updateMessage.Text = "Preview · no update check performed";
        _releaseNotes.Text = "Signed release notes will appear after an actual update check.\r\n\r\nThis hidden preview never connects to the release feed.";
        SelectNavigation(page); UpdateState();
    }

    internal bool IsOnCurrentPage(System.Windows.Forms.Control control)
    {
        for (var current = control; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, _sessionOptionsPanel) && !_sessionOptionsExpanded) return false;
            if (_pages.Values.Any(page => ReferenceEquals(page, current))) return ReferenceEquals(current, _pages[_selectedPage]);
        }
        return true;
    }

    internal void SetPreviewSessionOptions(bool expanded)
    {
        if (!_preview) throw new InvalidOperationException("Synthetic UI setup is available only in preview instances.");
        _sessionOptionsExpanded = expanded; if (_sessionOptionsPanel != null) _sessionOptionsPanel.Visible = expanded;
    }

    internal void PrepareSyntheticStopPreview(bool starting, bool working, SessionState? state)
    {
        if (!_preview) throw new InvalidOperationException("Synthetic stop state is available only in preview instances.");
        _startingTourney = starting; _busy = working; _previewSession = state;
        SelectNavigation("Overview"); UpdateState();
    }

    internal void PrepareSyntheticStripPreview(StripBackup? review, SessionState? session = null)
    {
        if (!_preview) throw new InvalidOperationException("Synthetic UI setup is available only in preview instances.");
        _previewSession = session; _stripReviewedBackup = review; _stripLastBackup = session?.Strip;
        if (review != null) _stripDetails.Text = FormatStripReview(review);
        SelectNavigation("Strip Windows ⚠"); UpdateState();
    }

    internal void PrepareSyntheticInternetPreview(InternetTestReport report, SessionState? session = null)
    {
        if (!_preview) throw new InvalidOperationException("Synthetic UI setup is available only in preview instances.");
        _previewSession = session; PopulateInternetReport(report); SelectNavigation("Internet Optimizer"); UpdateState();
    }
}
