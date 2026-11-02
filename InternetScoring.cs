using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourneyMode;

/// <summary>Finite hardware-derived trials and conservative comparison. RTT and CPU/DPC context never become processing latency.</summary>
public static class InternetScoring
{
    public static string SnapshotFingerprint(InternetNicSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            snapshot.CapturedUtc, snapshot.AdapterGuid, snapshot.InterfaceIndex, snapshot.Name, snapshot.PnpDeviceId,
            snapshot.DriverVersion, snapshot.RouteSignature, snapshot.SettingsHash, snapshot.Rss, snapshot.InterruptModeration,
            snapshot.EnergyEfficientEthernet, snapshot.Capabilities,
            Gateways = snapshot.GatewayTargets.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Sources = snapshot.SourceAddresses.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Advanced = snapshot.AdvancedProperties.OrderBy(value => value.Keyword, StringComparer.Ordinal).ToArray()
        }))));
    }

    /// <summary>Returns experiments only, excluding the always-available preserve-original baseline.</summary>
    public static List<InternetCandidate> BuildCandidates(InternetNicSnapshot snapshot, InternetTestMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unknown Internet test mode.", nameof(mode));
        var result = new List<InternetCandidate>(); var caps = snapshot.Capabilities;
        if (!Guid.TryParse(snapshot.AdapterGuid, out _) || snapshot.InterfaceIndex <= 0 || string.IsNullOrWhiteSpace(snapshot.Name)
            || string.IsNullOrWhiteSpace(snapshot.PnpDeviceId) || string.IsNullOrWhiteSpace(snapshot.DriverVersion)
            || string.IsNullOrWhiteSpace(snapshot.RouteSignature) || !Sha(snapshot.SettingsHash)
            || snapshot.SourceAddresses.Count == 0 || snapshot.SourceAddresses.Any(value => !IPAddress.TryParse(value, out _))) return result;
        string basis = snapshot.Fingerprint;
        void Add(InternetCandidate candidate)
        { candidate.BaselineFingerprint = basis; candidate.Basis = "Exact captured adapter " + snapshot.AdapterGuid + ", driver, route, addresses and original settings; benefit must be measured."; result.Add(candidate); }
        bool topology = caps.TopologyKnown && caps.ProcessorGroups == 1 && caps.LogicalProcessors is >= 2 and <= 256;
        bool rss = topology && caps.RssSupported == true && snapshot.Rss.Enabled.HasValue
            && (snapshot.Rss.Enabled == true || caps.RssEnabledChangeSupported == true)
            && caps.MaxReceiveQueues is > 0 and <= 128 && caps.MaxRssProcessors is > 0 and <= 256
            && snapshot.Rss.NumaNode is >= 0 and <= 65535 && snapshot.Rss.BaseProcessorGroup == 0 && snapshot.Rss.MaxProcessorGroup == 0
            && snapshot.Rss.BaseProcessorNumber is >= 0 && snapshot.Rss.MaxProcessorNumber >= snapshot.Rss.BaseProcessorNumber
            && snapshot.Rss.MaxProcessorNumber < caps.LogicalProcessors;
        if (rss)
        {
            int range = snapshot.Rss.MaxProcessorNumber!.Value - snapshot.Rss.BaseProcessorNumber!.Value + 1;
            int bound = Math.Min(caps.LogicalProcessors!.Value, Math.Min(range, Math.Min(caps.MaxReceiveQueues!.Value, caps.MaxRssProcessors!.Value)));
            foreach (int processors in new[] { 2, 4, 8 })
            {
                if (processors > bound) continue;
                int? queues = null;
                if (snapshot.Rss.NumberOfReceiveQueues is not > 0) continue;
                if (processors > snapshot.Rss.NumberOfReceiveQueues.Value)
                {
                    if (!caps.SupportedRssQueues.Contains(processors)) continue;
                    queues = processors;
                }
                if (snapshot.Rss.Enabled == true && snapshot.Rss.MaxProcessors == processors && queues == null) continue;
                Add(new() { Id = "rss-" + processors, Title = "Test RSS with " + processors + " processors", RssProcessors = processors,
                    RssEnabled = snapshot.Rss.Enabled == false ? true : null, RssQueues = queues });
            }
        }
        if (caps.InterruptModerationSupported == true && snapshot.InterruptModeration.HasValue
            && BinaryValues(caps.InterruptModerationEnabledValue, caps.InterruptModerationDisabledValue))
            Add(new() { Id = "interrupt-moderation-" + (snapshot.InterruptModeration.Value ? "off" : "on"),
                Title = "Test interrupt moderation " + (snapshot.InterruptModeration.Value ? "off" : "on"), InterruptModeration = !snapshot.InterruptModeration.Value });
        if (caps.EnergyEfficientEthernetSupported == true && snapshot.EnergyEfficientEthernet.HasValue && BinaryValues(caps.EeeEnabledValue, caps.EeeDisabledValue))
            Add(new() { Id = "eee-" + (snapshot.EnergyEfficientEthernet.Value ? "off" : "on"),
                Title = "Test Energy Efficient Ethernet " + (snapshot.EnergyEfficientEthernet.Value ? "off" : "on"), EnergyEfficientEthernet = !snapshot.EnergyEfficientEthernet.Value });
        if (rss && mode == InternetTestMode.Thorough)
        {
            foreach (string profile in new[] { "Conservative", "ClosestStatic", "NUMAStatic" })
            {
                string? advertised = caps.SupportedRssProfiles.FirstOrDefault(value => value.Equals(profile, StringComparison.OrdinalIgnoreCase));
                if (advertised == null || advertised.Equals(snapshot.Rss.Profile, StringComparison.OrdinalIgnoreCase)) continue;
                Add(new() { Id = "rss-profile-" + advertised.ToLowerInvariant(), Title = "Test supported RSS profile " + advertised, RssProfile = advertised });
            }
        }
        return result.Take(mode == InternetTestMode.Quick ? 6 : 8).ToList();
    }

    public static InternetMetrics CalculateMetrics(IEnumerable<InternetProbeSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var rows = samples.Take(4097).ToList(); if (rows.Count > 4096) throw new InvalidDataException("The bounded Internet sample limit was exceeded.");
        var replies = rows.Where(value => value.Success && Finite(value.RoundTripMs, 60000)).ToList();
        var ping = replies.Select(value => value.RoundTripMs!.Value).Order().ToList();
        var differences = replies.GroupBy(value => value.Target, StringComparer.OrdinalIgnoreCase).SelectMany(group =>
        {
            var ordered = group.OrderBy(value => value.TimestampUtc).Select(value => value.RoundTripMs!.Value).ToArray();
            return ordered.Skip(1).Select((value, index) => Math.Abs(value - ordered[index]));
        }).ToList();
        var processing = replies.Where(value => value.ProcessingTraceValid && !string.IsNullOrWhiteSpace(value.ProcessingEvidence)
            && value.ProcessingEvidence.Length <= 512 && Finite(value.LocalProcessingMs, 1000)).Select(value => value.LocalProcessingMs!.Value).Order().ToList();
        bool trace = replies.Count >= 8 && processing.Count >= Math.Max(8, (int)Math.Ceiling(replies.Count * .8));
        return new()
        {
            Sent = rows.Count, Received = replies.Count, PingMedianMs = Median(ping), PingP95Ms = Percentile(ping, .95),
            JitterMs = differences.Count == 0 ? null : differences.Average(), PacketLossPercent = rows.Count == 0 ? null : 100d * (rows.Count - replies.Count) / rows.Count,
            ProcessingSamples = trace ? processing.Count : 0, ProcessingTraceValid = trace,
            LocalProcessingMedianMs = trace ? Median(processing) : null,
            ProcessingEvidence = trace ? "Valid supplied local processing trace; ICMP RTT and CPU/DPC counters are excluded." : "Unknown: no sufficiently populated valid local processing trace."
        };
    }

    public static InternetTestReport Score(InternetTestReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        report.Scores = []; report.Winner = Baseline(report.Baseline); report.ProvisionalWinner = null;
        report.HasQualifiedWinner = report.CanApplyBest = false; report.Confidence = "Insufficient evidence";
        report.Recommendation = "Keep the original settings; there is no qualified repeatable three-objective winner.";
        AddLimit(report, "Goals have equal weights: ping, valid traced local processing latency, and stability (jitter/loss). Missing processing is Unknown, not replaced by gateway RTT or CPU/DPC counters.");
        AddLimit(report, "Only tested NIC candidates are compared. ICMP responses and bounded HTTPS load are proxies for this test, not a Fortnite-server or end-to-end input-latency measurement.");
        AddLimit(report, "Calibration runs with Fortnite closed. NIC CPU/DPC behavior during a crowded endgame can differ; this is not an in-game load validation.");
        AddLimit(report, "Objective scores are normalized percentage changes. Stability combines jitter and loss equally; any measured packet-loss regression blocks selection. CPU/DPC percentages are resource context only.");
        var allowed = BuildCandidates(report.Baseline, report.Options.Mode).ToDictionary(value => value.Id, StringComparer.Ordinal);
        var comparisons = new List<(InternetCandidate Candidate, InternetCandidateScore Score, bool Comparable, bool Gain)>();
        foreach (var group in report.Trials.Where(value => !value.Candidate.IsBaseline).GroupBy(value => value.Candidate.Id, StringComparer.Ordinal))
        {
            var first = group.First().Candidate; var score = new InternetCandidateScore { CandidateId = group.Key }; report.Scores.Add(score);
            if (!allowed.TryGetValue(group.Key, out var expected) || CandidateKey(first) != CandidateKey(expected)
                || group.Any(trial => CandidateKey(trial.Candidate) != CandidateKey(first)))
            { score.Reasons.Add("Candidate settings, identity or captured baseline are unsupported or inconsistent."); continue; }
            var results = group.Select(trial => Compare(trial, report)).ToList();
            foreach (string reason in results.SelectMany(value => value.Reasons).Distinct()) score.Reasons.Add(reason);
            score.ValidRepeats = results.Count(value => value.Comparable);
            var usable = results.Where(value => value.Comparable).ToList();
            score.PingMeasured = usable.Count > 0; score.StabilityMeasured = usable.Count > 0;
            score.ProcessingMeasured = usable.Count > 0 && usable.All(value => value.Processing.HasValue);
            if (usable.Count > 0)
            {
                score.PingImprovementPercent = usable.Average(value => value.Ping);
                score.StabilityImprovementPercent = usable.Average(value => value.Stability);
                if (score.ProcessingMeasured) score.ProcessingImprovementPercent = usable.Average(value => value.Processing!.Value);
            }
            bool all = usable.Count == results.Count && usable.Count > 0;
            bool gain = all && (usable.All(value => value.PingGain) || usable.All(value => value.StabilityGain) || score.ProcessingMeasured && usable.All(value => value.ProcessingGain));
            bool resources = all && usable.All(value => value.ResourcesSafe);
            int repeats = Math.Max(2, report.Options.Repetitions);
            if (!all) score.Reasons.Add("Noisy, invalid or regressing repeats cannot be discarded to manufacture a winner.");
            if (!gain) score.Reasons.Add("No objective improved consistently beyond both surrounding baselines and the noise floor.");
            if (!score.ProcessingMeasured) score.Reasons.Add("Local processing latency is Unknown; this can only support a provisional ping/stability ranking.");
            if (usable.Count < repeats) score.Reasons.Add("At least " + repeats + " matching A/B/A repetitions are required for a qualified repeatable winner.");
            if (score.AllObjectivesMeasured) score.EqualWeightScore = (score.PingImprovementPercent + score.ProcessingImprovementPercent + score.StabilityImprovementPercent) / 3;
            score.Eligible = all && gain && resources && score.AllObjectivesMeasured && usable.Count >= repeats && score.EqualWeightScore > 3;
            score.Confidence = score.Eligible ? "Repeatable within this bounded test; not a global optimum" : all && gain && resources ? "Provisional observed comparison" : "Inconclusive";
            comparisons.Add((first, score, all && resources, gain));
        }
        report.PingMeasured = report.Scores.Any(value => value.PingMeasured);
        report.ProcessingMeasured = report.Scores.Any(value => value.ProcessingMeasured);
        report.StabilityMeasured = report.Scores.Any(value => value.StabilityMeasured);
        if (!report.ProcessingMeasured) AddLimit(report, "No valid local processing trace was measured. All-three-goal optimization is unproven; Apply best is disabled.");
        foreach (var current in comparisons.Where(value => value.Score.Eligible))
            current.Score.ParetoEfficient = !comparisons.Any(other => other.Score.Eligible && other.Candidate.Id != current.Candidate.Id && Dominates(other.Score, current.Score));
        if (!report.Completed || report.Canceled || !report.RollbackVerified)
        { AddLimit(report, "The run was incomplete, canceled or its restoration was unverified. The original settings remain the only recommendation."); return report; }
        var provisional = comparisons.Where(value => value.Comparable && value.Gain).OrderByDescending(value =>
            (value.Score.PingImprovementPercent!.Value + value.Score.StabilityImprovementPercent!.Value) / 2).ToList();
        for (int index = 0; index < provisional.Count; index++) provisional[index].Score.ProvisionalRank = index + 1;
        // A provisional action means repeatable ping/stability evidence with the
        // third objective explicitly Unknown. It cannot bypass ties or missing repeats.
        bool provisionalTie = provisional.Count > 1 && Math.Abs(
            (provisional[0].Score.PingImprovementPercent!.Value + provisional[0].Score.StabilityImprovementPercent!.Value) / 2
            - (provisional[1].Score.PingImprovementPercent!.Value + provisional[1].Score.StabilityImprovementPercent!.Value) / 2) <= 1;
        if (provisional.Count > 0 && !provisionalTie && !provisional[0].Score.ProcessingMeasured
            && provisional[0].Score.ValidRepeats >= Math.Max(2, report.Options.Repetitions))
            report.ProvisionalWinner = provisional[0].Candidate;
        if (provisionalTie) AddLimit(report, "Observed ping/stability leaders are tied; no provisional best can be selected from an uncertain tie.");
        var qualified = comparisons.Where(value => value.Score.Eligible && value.Score.ParetoEfficient).OrderByDescending(value => value.Score.EqualWeightScore).ToList();
        if (qualified.Count == 0) return report;
        if (qualified.Count > 1 && Math.Abs(qualified[0].Score.EqualWeightScore!.Value - qualified[1].Score.EqualWeightScore!.Value) <= 1)
        { report.Confidence = "Inconclusive tie"; AddLimit(report, "Leading candidates are within one normalized percentage point; the baseline wins this uncertain tie."); return report; }
        report.Winner = qualified[0].Candidate; report.HasQualifiedWinner = report.CanApplyBest = true;
        report.Confidence = qualified[0].Score.Confidence;
        report.Recommendation = "Best tested under these conditions: " + report.Winner.Title + ". Review the measured limits before explicitly applying it.";
        return report;
    }

    private sealed class Comparison
    {
        public bool Comparable = true, ResourcesSafe = true, PingGain = true, StabilityGain = true, ProcessingGain = true;
        public double Ping, Stability;
        public double? Processing;
        public List<string> Reasons = [];
        public void Reject(string reason) { Comparable = false; Reasons.Add(reason); }
    }
    private static Comparison Compare(InternetTrial trial, InternetTestReport report)
    {
        var result = new Comparison(); var a = trial.BaselineBefore; var b = trial.Treatment; var c = trial.BaselineAfter;
        foreach (var period in new[] { a, b, c }) Normalize(period);
        if (!trial.Applied || !trial.RestoreAttempted || !trial.RestoreVerified || trial.Canceled || !string.IsNullOrWhiteSpace(trial.Error))
            result.Reject("Candidate application/rollback was incomplete, canceled or unverified.");
        if (!a.Valid || !b.Valid || !c.Valid || a.StartedUtc >= a.EndedUtc || b.StartedUtc >= b.EndedUtc || c.StartedUtc >= c.EndedUtc
            || a.EndedUtc > b.StartedUtc || b.EndedUtc > c.StartedUtc) result.Reject("A/B/A measurement periods were invalid or out of order.");
        if (new[] { a, b, c }.Any(period => period.LoadRequested != report.Options.EnableHttpsLoad
            || period.LoadRequested && (!period.LoadCompleted || period.LoadBytesReceived != report.Options.HttpsLoadBytesPerMeasurement)))
            result.Reject("Baseline and candidate load conditions did not match the declared bounded load.");
        var targets = a.Endpoints.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0 || !targets.SequenceEqual(b.Endpoints.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            || !targets.SequenceEqual(c.Endpoints.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            result.Reject("The same responding endpoint set was not measured in every A/B/A period.");
        var pings = new List<double>(); var stability = new List<double>(); var processing = new List<double>();
        foreach (string target in targets)
        {
            if (!b.Endpoints.TryGetValue(target, out var bm) || !c.Endpoints.TryGetValue(target, out var cm)) continue;
            var am = a.Endpoints[target];
            if (!Usable(am) || !Usable(bm) || !Usable(cm)) { result.Reject(target + ": missing, invalid or insufficient ICMP evidence."); continue; }
            double ping = (am.PingMedianMs!.Value + cm.PingMedianMs!.Value) / 2;
            double jitter = (am.JitterMs!.Value + cm.JitterMs!.Value) / 2;
            double loss = (am.PacketLossPercent!.Value + cm.PacketLossPercent!.Value) / 2;
            if (Math.Abs(am.PingMedianMs.Value - cm.PingMedianMs.Value) > Math.Max(.2, ping * .1)
                || Math.Abs(am.JitterMs.Value - cm.JitterMs.Value) > Math.Max(.2, jitter * .5)
                || Math.Max(am.PacketLossPercent.Value, cm.PacketLossPercent.Value) > 2)
                result.Reject(target + ": surrounding baselines were noisy or lossy; endpoint/scheduling variation may dominate the result.");
            if (bm.PacketLossPercent > Math.Min(am.PacketLossPercent.Value, cm.PacketLossPercent.Value) + .01
                || bm.PingMedianMs > Math.Max(am.PingMedianMs.Value, cm.PingMedianMs.Value) + Math.Max(.1, ping * .02)
                || bm.PingP95Ms > Math.Max(am.PingP95Ms!.Value, cm.PingP95Ms!.Value) + Math.Max(.2, ping * .02)
                || bm.JitterMs > Math.Max(am.JitterMs.Value, cm.JitterMs.Value) + Math.Max(.1, jitter * .05))
                result.Reject(target + ": packet loss, median/tail ping or jitter regressed beyond the no-regression allowance.");
            pings.Add(Improvement(ping, bm.PingMedianMs!.Value, .1));
            stability.Add((Improvement(jitter, bm.JitterMs!.Value, .1) + Improvement(loss, bm.PacketLossPercent!.Value, 1)) / 2);
            result.PingGain &= Math.Min(am.PingMedianMs.Value, cm.PingMedianMs.Value) - bm.PingMedianMs.Value > Math.Max(.1, Math.Max(ping * .05, Math.Abs(am.PingMedianMs.Value - cm.PingMedianMs.Value) * 2));
            result.StabilityGain &= Math.Min(am.JitterMs.Value, cm.JitterMs.Value) - bm.JitterMs.Value > Math.Max(.1, Math.Max(jitter * .1, Math.Abs(am.JitterMs.Value - cm.JitterMs.Value) * 2))
                || bm.PacketLossPercent < Math.Min(am.PacketLossPercent.Value, cm.PacketLossPercent.Value) - .5;
            if (Trace(am) && Trace(bm) && Trace(cm))
            {
                double local = (am.LocalProcessingMedianMs!.Value + cm.LocalProcessingMedianMs!.Value) / 2;
                if (Math.Abs(am.LocalProcessingMedianMs.Value - cm.LocalProcessingMedianMs.Value) > Math.Max(.005, local * .1)) result.Reject(target + ": traced local processing baselines were unstable.");
                if (bm.LocalProcessingMedianMs > Math.Max(am.LocalProcessingMedianMs.Value, cm.LocalProcessingMedianMs.Value) + Math.Max(.005, local * .02)) result.Reject(target + ": traced processing latency regressed.");
                processing.Add(Improvement(local, bm.LocalProcessingMedianMs!.Value, .005));
                result.ProcessingGain &= Math.Min(am.LocalProcessingMedianMs.Value, cm.LocalProcessingMedianMs.Value) - bm.LocalProcessingMedianMs.Value > Math.Max(.005, Math.Max(local * .05, Math.Abs(am.LocalProcessingMedianMs.Value - cm.LocalProcessingMedianMs.Value) * 2));
            }
            else result.ProcessingGain = false;
        }
        if (pings.Count != targets.Length || targets.Length == 0) result.Comparable = false;
        result.Ping = pings.Count == 0 ? 0 : pings.Average(); result.Stability = stability.Count == 0 ? 0 : stability.Average();
        result.Processing = processing.Count == targets.Length && processing.Count > 0 ? processing.Average() : null;
        int? logical = report.Baseline.Capabilities.LogicalProcessors;
        double cap = logical is > 0 ? Math.Min(report.Options.MaximumExtraCpuPercent, logical <= 4 ? 1 : logical <= 8 ? 2 : 3) : 0;
        if (!report.Baseline.Capabilities.TopologyKnown || logical is not > 0 || !Finite(a.Metrics.MachineCpuPercent, 100) || !Finite(b.Metrics.MachineCpuPercent, 100)
            || !Finite(c.Metrics.MachineCpuPercent, 100) || cap <= 0
            || Math.Abs(a.Metrics.MachineCpuPercent!.Value - c.Metrics.MachineCpuPercent!.Value) > Math.Max(.5, cap)
            || b.Metrics.MachineCpuPercent > Math.Min(a.Metrics.MachineCpuPercent.Value, c.Metrics.MachineCpuPercent.Value) + cap)
        { result.ResourcesSafe = false; result.Reasons.Add("Measured CPU overhead exceeded the hardware-dependent cap, or topology/CPU resource evidence was unknown."); }
        return result;
    }
    private static void Normalize(InternetMeasurement period)
    {
        if (period.Samples.Count == 0) return;
        double? cpu = period.Metrics.MachineCpuPercent, dpc = period.Metrics.MachineDpcPercent;
        period.Endpoints = period.Samples.GroupBy(value => value.Target, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => CalculateMetrics(group), StringComparer.OrdinalIgnoreCase);
        period.Metrics = CalculateMetrics(period.Samples); period.Metrics.MachineCpuPercent = cpu; period.Metrics.MachineDpcPercent = dpc;
    }
    private static bool Usable(InternetMetrics value) => value.Sent is >= 8 and <= 4096 && value.Received >= 8 && value.Received <= value.Sent
        && Finite(value.PingMedianMs, 60000) && Finite(value.PingP95Ms, 60000) && value.PingP95Ms >= value.PingMedianMs
        && Finite(value.JitterMs, 60000) && Finite(value.PacketLossPercent, 100)
        && Math.Abs(value.PacketLossPercent!.Value - 100d * (value.Sent - value.Received) / value.Sent) <= .01;
    private static bool Trace(InternetMetrics value) => value.ProcessingTraceValid && value.ProcessingSamples >= 8
        && value.ProcessingSamples >= Math.Ceiling(value.Received * .8) && !string.IsNullOrWhiteSpace(value.ProcessingEvidence) && Finite(value.LocalProcessingMedianMs, 1000);
    private static bool Dominates(InternetCandidateScore a, InternetCandidateScore b)
        => a.PingImprovementPercent >= b.PingImprovementPercent && a.ProcessingImprovementPercent >= b.ProcessingImprovementPercent
            && a.StabilityImprovementPercent >= b.StabilityImprovementPercent && (a.PingImprovementPercent > b.PingImprovementPercent
                || a.ProcessingImprovementPercent > b.ProcessingImprovementPercent || a.StabilityImprovementPercent > b.StabilityImprovementPercent);
    private static InternetCandidate Baseline(InternetNicSnapshot snapshot) => new() { Id = "baseline", IsBaseline = true, Title = "Keep original NIC settings", BaselineFingerprint = snapshot.Fingerprint };
    private static string CandidateKey(InternetCandidate value) => JsonSerializer.Serialize(new { value.Id, value.BaselineFingerprint, value.IsBaseline, value.RssEnabled, value.RssQueues, value.RssProcessors, value.RssProfile, value.InterruptModeration, value.EnergyEfficientEthernet });
    private static bool BinaryValues(string? enabled, string? disabled) => !string.IsNullOrWhiteSpace(enabled) && !string.IsNullOrWhiteSpace(disabled) && enabled != disabled;
    private static bool Sha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool Finite(double? value, double maximum) => value.HasValue && double.IsFinite(value.Value) && value >= 0 && value <= maximum;
    private static double Improvement(double baseline, double candidate, double floor) => Math.Clamp(100 * (baseline - candidate) / Math.Max(baseline, floor), -100, 100);
    private static double? Median(List<double> sorted) => sorted.Count == 0 ? null : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    private static double? Percentile(List<double> sorted, double percentile) => sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1)];
    private static void AddLimit(InternetTestReport report, string message) { if (!report.Limitations.Contains(message, StringComparer.Ordinal)) report.Limitations.Add(message); }
}
