using System.Diagnostics;

namespace TourneyMode;

public sealed class AppOptions
{
    // False defaults preserve the meaning of pre-1.1 recovery journals.
    public int OptionsVersion { get; set; } = 1;
    public UserIntent Intent { get; set; } = new();
    public string ReviewedPlanFingerprint { get; set; } = "";
    public bool FortniteOnly { get; set; }
    public bool MaximumHeadroom { get; set; }
    public bool KeepAppsClosed { get; set; }
    public bool PauseOptionalServices { get; set; }
    public bool PrioritizeNetwork { get; set; }
    public bool TuneEthernet { get; set; }
    public bool AutoRestoreOnGameExit { get; set; }
    public bool CloseCodex { get; set; }
    public bool ForceClose { get; set; } = true;
    public bool HighPriority { get; set; } = true;
    public bool HighPerformancePower { get; set; } = true;
    public bool GameTuning { get; set; } = true;
    public List<string> TweakWindowsSettings { get; set; } = [];
    public bool ApplyWindowsProfile { get; set; }
    public bool ExperimentalWiredNetwork { get; set; }
    public bool ExperimentalDscp { get; set; }
    public bool ExperimentalBackgroundGovernor { get; set; }
    public bool SilenceNotifications { get; set; } = true;
    public List<string> SelectedApps { get; set; } = [];
    public List<string> ExcludedApps { get; set; } = [];
    public List<string> ProtectedAppPaths { get; set; } = [];
}

public sealed class ProcessIdentity
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public int SessionId { get; set; }
    public long StartTicks { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class AppCandidate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string LaunchPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public List<string> AdditionalLaunchPaths { get; set; } = [];
    public string BridgeProjectId { get; set; } = "";
    public string BridgeThreadId { get; set; } = "";
    public bool DefaultSelected { get; set; } = true;
    public List<ProcessIdentity> Processes { get; set; } = [];
    public override string ToString() => $"{Name}  ·  {Processes.Count} processes";
}

public sealed class RestoreApp
{
    public AppCandidate App { get; set; } = new();
    public bool CloseAttempted { get; set; }
    public bool Restored { get; set; }
    public string Error { get; set; } = "";
}

public sealed class PriorityBackup
{
    public ProcessIdentity Process { get; set; } = new();
    public int Original { get; set; }
    public int? Requested { get; set; }
    public bool Restored { get; set; }
}

public sealed class NotificationBackup
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public int Value { get; set; }
    public bool Restored { get; set; }
}

public sealed class SessionState
{
    public string Workflow { get; set; } = "Tourney";
    public int Schema { get; set; } = 1;
    public string Phase { get; set; } = "Starting";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public bool SawFortnite { get; set; }
    public DateTime? GameAbsentSinceUtc { get; set; }
    public AppOptions Options { get; set; } = new();
    public string OriginalPowerScheme { get; set; } = "";
    public string PerformancePlanGuid { get; set; } = "";
    public bool PowerRestored { get; set; }
    public List<NotificationBackup> Notifications { get; set; } = [];
    public List<NotificationBackup> Tuning { get; set; } = [];
    public bool? OriginalDoNotDisturb { get; set; }
    public bool DoNotDisturbRestored { get; set; }
    public List<RestoreApp> Apps { get; set; } = [];
    public List<ServiceBackup> Services { get; set; } = [];
    public List<ScheduledTaskBackup> ScheduledTasks { get; set; } = [];
    public NetworkPriorityBackup? NetworkPriority { get; set; }
    public EthernetAdapterBackup? EthernetAdapter { get; set; }
    public List<PriorityBackup> Priorities { get; set; } = [];
    public CodexBackup? Codex { get; set; }
    public FortniteSettingsBackup? FortniteSettings { get; set; }
    public List<BackgroundPriorityBackup> BackgroundPriorities { get; set; } = [];
    public List<FeatureResult> Features { get; set; } = [];
    public OptimizationPlan? Plan { get; set; }
    public DateTime? LastHeartbeatUtc { get; set; }
    public ProcessIdentity? Controller { get; set; }
    public string CreatedByVersion { get; set; } = "legacy";
    public SystemRestoreCheckpoint? RestorePoint { get; set; }
    public StripBackup? Strip { get; set; }
    public WindowsProfileBackup? WindowsProfile { get; set; }
    public InternetCalibrationState? InternetCalibration { get; set; }
}
