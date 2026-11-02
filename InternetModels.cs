namespace TourneyMode;

public enum InternetTestMode { Quick, Thorough }

public sealed class InternetTestOptions
{
    public InternetTestMode Mode { get; set; } = InternetTestMode.Quick;
    public List<string> Targets { get; set; } = [];
    public int SamplesPerTarget { get; set; } = 12;
    public int ProbeIntervalMs { get; set; } = 200;
    public int ProbeTimeoutMs { get; set; } = 800;
    public int MaximumCandidates { get; set; } = 8;
    public int Repetitions { get; set; } = 2;
    public bool EnableHttpsLoad { get; set; }
    public int HttpsLoadBytesPerMeasurement { get; set; } = 1024 * 1024;
    public long MaximumHttpsLoadBytesTotal { get; set; } = 32L * 1024 * 1024;
    public int OverallTimeoutSeconds { get; set; } = 300;
    public double MaximumExtraCpuPercent { get; set; } = 3;
    public int ObjectivesSchema { get; set; } = 1; // Equal ping / traced local processing / stability weights.
}

public sealed class InternetTestProgress
{
    public string Phase { get; set; } = "";
    public string Message { get; set; } = "";
    public int Completed { get; set; }
    public int Total { get; set; }
}

public sealed class InternetRssSettings
{
    public bool? Enabled { get; set; }
    public string? Profile { get; set; }
    public int? NumaNode { get; set; }
    public int? BaseProcessorGroup { get; set; }
    public int? BaseProcessorNumber { get; set; }
    public int? MaxProcessorGroup { get; set; }
    public int? MaxProcessorNumber { get; set; }
    public int? MaxProcessors { get; set; }
    public int? NumberOfReceiveQueues { get; set; }
}

public sealed class InternetNicProperty
{
    public string Keyword { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string> OriginalValues { get; set; } = [];
    public List<string> AdvertisedValues { get; set; } = [];
}

public sealed class InternetNicCapabilities
{
    public bool TopologyKnown { get; set; }
    public int? LogicalProcessors { get; set; }
    public int? PhysicalCores { get; set; }
    public int? ProcessorGroups { get; set; }
    public bool? RssSupported { get; set; }
    public bool? RssEnabledChangeSupported { get; set; }
    public int? MaxRssProcessors { get; set; }
    public int? MaxReceiveQueues { get; set; }
    public List<int> SupportedRssQueues { get; set; } = [];
    public List<string> SupportedRssProfiles { get; set; } = [];
    public bool? InterruptModerationSupported { get; set; }
    public string? InterruptModerationEnabledValue { get; set; }
    public string? InterruptModerationDisabledValue { get; set; }
    public bool? EnergyEfficientEthernetSupported { get; set; }
    public string? EeeEnabledValue { get; set; }
    public string? EeeDisabledValue { get; set; }
}

public sealed class InternetNicSnapshot
{
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string AdapterGuid { get; set; } = "";
    public int InterfaceIndex { get; set; }
    public string Name { get; set; } = "";
    public string PnpDeviceId { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string RouteSignature { get; set; } = "";
    public string SettingsHash { get; set; } = "";
    public List<string> GatewayTargets { get; set; } = [];
    public List<string> SourceAddresses { get; set; } = [];
    public InternetRssSettings Rss { get; set; } = new();
    public bool? InterruptModeration { get; set; }
    public bool? EnergyEfficientEthernet { get; set; }
    public List<InternetNicProperty> AdvancedProperties { get; set; } = [];
    public InternetNicCapabilities Capabilities { get; set; } = new();
    public List<string> Limitations { get; set; } = [];
    public string Fingerprint => InternetScoring.SnapshotFingerprint(this);
}

public sealed class InternetCandidate
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Basis { get; set; } = "";
    public string BaselineFingerprint { get; set; } = "";
    public bool IsBaseline { get; set; }
    public bool? RssEnabled { get; set; }
    public int? RssQueues { get; set; }
    public int? RssProcessors { get; set; }
    public string? RssProfile { get; set; }
    public bool? InterruptModeration { get; set; }
    public bool? EnergyEfficientEthernet { get; set; }
}

public sealed class InternetProbeSample
{
    public string Target { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public double? RoundTripMs { get; set; }
    public double? LocalProcessingMs { get; set; }
    public bool ProcessingTraceValid { get; set; }
    public string ProcessingEvidence { get; set; } = "";
    public string SourceMetricKind { get; set; } = "RemoteIcmpRoundTrip";
    public bool IsGatewayTarget { get; set; }
}

public sealed class InternetMetrics
{
    public int Sent { get; set; }
    public int Received { get; set; }
    public double? PingMedianMs { get; set; }
    public double? PingP95Ms { get; set; }
    public double? JitterMs { get; set; }
    public double? PacketLossPercent { get; set; }
    public double? LocalProcessingMedianMs { get; set; }
    public int ProcessingSamples { get; set; }
    public bool ProcessingTraceValid { get; set; }
    public string ProcessingEvidence { get; set; } = "";
    public double? MachineCpuPercent { get; set; }
    public double? MachineDpcPercent { get; set; }
}

public sealed class InternetMeasurement
{
    public string Label { get; set; } = "";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime EndedUtc { get; set; } = DateTime.UtcNow;
    public List<InternetProbeSample> Samples { get; set; } = [];
    public InternetMetrics Metrics { get; set; } = new();
    public Dictionary<string, InternetMetrics> Endpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Valid { get; set; } = true;
    public bool LoadRequested { get; set; }
    public bool LoadCompleted { get; set; }
    public long LoadBytesReceived { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class InternetTrial
{
    public InternetCandidate Candidate { get; set; } = new();
    public InternetMeasurement BaselineBefore { get; set; } = new();
    public InternetMeasurement Treatment { get; set; } = new();
    public InternetMeasurement BaselineAfter { get; set; } = new();
    public bool Applied { get; set; }
    public bool RestoreAttempted { get; set; }
    public bool RestoreVerified { get; set; }
    public bool Canceled { get; set; }
    public string Error { get; set; } = "";
}

public sealed class InternetCandidateScore
{
    public string CandidateId { get; set; } = "";
    public bool Eligible { get; set; }
    public bool ParetoEfficient { get; set; }
    public bool PingMeasured { get; set; }
    public bool ProcessingMeasured { get; set; }
    public bool StabilityMeasured { get; set; }
    public bool AllObjectivesMeasured => PingMeasured && ProcessingMeasured && StabilityMeasured;
    public int ValidRepeats { get; set; }
    public int? ProvisionalRank { get; set; }
    public double? PingImprovementPercent { get; set; }
    public double? ProcessingImprovementPercent { get; set; }
    public double? StabilityImprovementPercent { get; set; }
    public double? EqualWeightScore { get; set; }
    public string Confidence { get; set; } = "Insufficient evidence";
    public List<string> Reasons { get; set; } = [];
}

public sealed class InternetTestReport
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public InternetTestOptions Options { get; set; } = new();
    public InternetNicSnapshot Baseline { get; set; } = new();
    public List<InternetTrial> Trials { get; set; } = [];
    public List<InternetCandidateScore> Scores { get; set; } = [];
    public InternetCandidate? Winner { get; set; }
    public InternetCandidate? ProvisionalWinner { get; set; }
    public bool HasQualifiedWinner { get; set; }
    public bool PingMeasured { get; set; }
    public bool ProcessingMeasured { get; set; }
    public bool StabilityMeasured { get; set; }
    public bool AllObjectivesMeasured => PingMeasured && ProcessingMeasured && StabilityMeasured;
    public bool CanApplyBest { get; set; }
    public bool Completed { get; set; }
    public bool Canceled { get; set; }
    public bool RollbackVerified { get; set; }
    public string Confidence { get; set; } = "Insufficient evidence";
    public string Recommendation { get; set; } = "Keep the original settings.";
    public List<string> Limitations { get; set; } = [];
}

public sealed class InternetCalibrationState
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public InternetNicSnapshot Baseline { get; set; } = new();
    public InternetCandidate? PendingCandidate { get; set; }
    public bool MutationAttempted { get; set; }
    public bool Restored { get; set; } = true;
    public string Phase { get; set; } = "Captured";
    public string LastError { get; set; } = "";
}
