using System.Text;
using System.Windows.Forms;

namespace TourneyMode;

public sealed partial class MainForm
{
    private void PopulateInternetReport(InternetTestReport report)
    {
        _internetReport = report; _internetResults.BeginUpdate(); _internetResults.Items.Clear(); _internetEvidence.Clear();
        _loading = true;
        try { _internetMode.Value = report.Options.Mode; _internetLoaded.Checked = report.Options.EnableHttpsLoad; _internetAllowProvisional.Checked = false; }
        finally { _loading = false; }
        var nic = report.Baseline;
        _internetAdapter.Text = nic.Name + " · driver " + (string.IsNullOrWhiteSpace(nic.DriverVersion) ? "unknown" : nic.DriverVersion)
            + "\r\n" + (nic.Capabilities.LogicalProcessors?.ToString() ?? "Unknown") + " logical CPUs · RSS queues " + (nic.Rss.NumberOfReceiveQueues?.ToString() ?? "unknown") + " · RSS processors " + (nic.Rss.MaxProcessors?.ToString() ?? "unknown");
        _internetMessage.Text = report.Recommendation;
        _internetProgressText.Text = "Equal objectives · Ping: " + EvidenceState(report.PingMeasured) + " · Local processing: " + EvidenceState(report.ProcessingMeasured) + " · Stability: " + EvidenceState(report.StabilityMeasured)
            + "\r\n" + (report.RollbackVerified ? "Original settings restored after testing." : "Original settings restoration is not verified. Use Restore originals.");
        var baselinePeriods = report.Trials.SelectMany(trial => new[] { trial.BaselineBefore, trial.BaselineAfter }).Where(period => period.Valid).ToArray();
        AddInternetResult("__original", "Original baseline", baselinePeriods, report, "Original", "BASELINE\r\n" + InternetPeriodsDetail(baselinePeriods));
        foreach (var score in report.Scores.OrderBy(score => score.ProvisionalRank ?? int.MaxValue))
        {
            var trials = report.Trials.Where(trial => trial.Candidate.Id == score.CandidateId).ToArray();
            var candidate = trials.FirstOrDefault()?.Candidate; if (candidate == null || candidate.IsBaseline) continue;
            string state = report.HasQualifiedWinner && report.Winner?.Id == candidate.Id ? "Best tested" : score.ProvisionalRank.HasValue ? "Provisional #" + score.ProvisionalRank : score.Eligible ? "Eligible" : "Not qualified";
            var detail = new StringBuilder().AppendLine(candidate.Title).AppendLine(candidate.Basis).AppendLine(InternetCandidateSettings(candidate)).AppendLine()
                .Append("Evidence: ").AppendLine(score.Confidence).Append("Valid repeats: ").AppendLine(score.ValidRepeats.ToString())
                .Append("Ping: ").Append(EvidenceState(score.PingMeasured)).Append(" · Processing: ").Append(EvidenceState(score.ProcessingMeasured)).Append(" · Stability: ").AppendLine(EvidenceState(score.StabilityMeasured));
            if (score.EqualWeightScore.HasValue) detail.Append("Equal-weight comparison score: ").AppendLine(score.EqualWeightScore.Value.ToString("0.###"));
            foreach (string reason in score.Reasons) detail.Append("• ").AppendLine(reason);
            foreach (var trial in trials)
            {
                detail.AppendLine().Append("TRIAL · ").AppendLine(trial.Treatment.Label).Append("Applied for trial: ").Append(trial.Applied ? "yes" : "no").Append(" · Restored and verified: ").AppendLine(trial.RestoreVerified ? "yes" : "no");
                if (!string.IsNullOrWhiteSpace(trial.Error)) detail.Append("Issue: ").AppendLine(trial.Error);
                detail.AppendLine("Before → candidate → after (matched periods)").Append(InternetPeriodsDetail([trial.BaselineBefore, trial.Treatment, trial.BaselineAfter]));
            }
            AddInternetResult(candidate.Id, candidate.Title, trials.Where(trial => trial.Treatment.Valid).Select(trial => trial.Treatment).ToArray(), report, state, detail.ToString());
        }
        _internetResults.EndUpdate();
        if (_internetResults.Items.Count > 0) _internetResults.Items[0].Selected = true;
        if (!report.AllObjectivesMeasured)
            _internetMessage.Text += "\r\nLocal processing or another required objective is unknown. Provisional rankings do not qualify for Apply best.";
        UpdateInternetControls();
    }
    private void AddInternetResult(string id, string title, InternetMeasurement[] periods, InternetTestReport report, string state, string detail)
    {
        var gateways = report.Baseline.GatewayTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gateway = periods.SelectMany(period => period.Endpoints.Where(pair => gateways.Contains(pair.Key)).Select(pair => pair.Value)).ToArray();
        var remote = periods.SelectMany(period => period.Endpoints.Where(pair => !gateways.Contains(pair.Key)).Select(pair => pair.Value)).ToArray();
        string processing = Ms(Median(periods.Where(period => period.Metrics.ProcessingTraceValid).Select(period => period.Metrics.LocalProcessingMedianMs)));
        var row = new ListViewItem(title) { Tag = id };
        row.SubItems.Add(Ms(Median(gateway.Select(metric => metric.PingMedianMs)))); row.SubItems.Add(Ms(Median(remote.Select(metric => metric.PingMedianMs)))); row.SubItems.Add(processing);
        row.SubItems.Add(Ms(Median(periods.Select(period => period.Metrics.JitterMs))) + " / " + Percent(Median(periods.Select(period => period.Metrics.PacketLossPercent)))); row.SubItems.Add(state);
        _internetResults.Items.Add(row); _internetEvidence[id] = detail + "\r\nTable values are medians of valid periods' RTT, processing, jitter and loss metrics; endpoint sample counts remain above. CPU context is not a processing-latency proxy."
            + (report.Limitations.Count == 0 ? "" : "\r\n\r\nREPORT LIMITATIONS\r\n" + string.Join("\r\n", report.Limitations.Select(text => "• " + text)));
    }
    private static string InternetCandidateSettings(InternetCandidate candidate) => "RSS " + Optional(candidate.RssEnabled)
        + " · queues " + (candidate.RssQueues?.ToString() ?? "preserve") + " · processors " + (candidate.RssProcessors?.ToString() ?? "preserve")
        + " · profile " + (candidate.RssProfile ?? "preserve") + "\r\nInterrupt moderation " + Optional(candidate.InterruptModeration) + " · Energy Efficient Ethernet " + Optional(candidate.EnergyEfficientEthernet);
    private static string InternetPeriodsDetail(IEnumerable<InternetMeasurement> periods)
    {
        var text = new StringBuilder();
        foreach (var period in periods)
        {
            text.AppendLine(period.Label).Append("Valid: ").Append(period.Valid ? "yes" : "no").Append(" · Replies ").Append(period.Metrics.Received).Append('/').Append(period.Metrics.Sent)
                .Append(" · Machine CPU context ").AppendLine(Percent(period.Metrics.MachineCpuPercent));
            if (period.LoadRequested) text.Append("HTTPS load: ").Append(period.LoadCompleted ? "completed" : "incomplete").Append(" · ").Append(period.LoadBytesReceived).AppendLine(" bytes received");
            foreach (var pair in period.Endpoints)
            {
                var value = pair.Value;
                text.Append(pair.Key).Append(": RTT median ").Append(Ms(value.PingMedianMs)).Append(" / p95 ").Append(Ms(value.PingP95Ms))
                    .Append("; jitter ").Append(Ms(value.JitterMs)).Append("; loss ").Append(Percent(value.PacketLossPercent))
                    .Append("; processing ").AppendLine(value.ProcessingTraceValid ? Ms(value.LocalProcessingMedianMs) : "Unknown (no valid trace)");
                if (!string.IsNullOrWhiteSpace(value.ProcessingEvidence)) text.Append("Processing evidence: ").AppendLine(value.ProcessingEvidence);
            }
            foreach (string warning in period.Warnings) text.Append("Note: ").AppendLine(warning);
        }
        return text.ToString();
    }
    private static double? Median(IEnumerable<double?> values)
    {
        var data = values.Where(value => value.HasValue && double.IsFinite(value.Value)).Select(value => value!.Value).Order().ToArray();
        if (data.Length == 0) return null;
        return data.Length % 2 == 0 ? (data[data.Length / 2 - 1] + data[data.Length / 2]) / 2 : data[data.Length / 2];
    }
    private static string Ms(double? value) => value.HasValue ? value.Value.ToString("0.###") + " ms" : "Unknown";
    private static string Percent(double? value) => value.HasValue ? value.Value.ToString("0.##") + "%" : "Unknown";
    private static string Optional(bool? value) => value.HasValue ? value.Value ? "on" : "off" : "preserve";
    private static string EvidenceState(bool measured) => measured ? "measured" : "unknown";
}
