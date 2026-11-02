namespace TourneyMode;

public sealed class StripFeatureDefinition
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string FunctionalityLost { get; init; } = "";
    public IReadOnlyList<string> Services { get; init; } = [];
}

public sealed class StripServiceSnapshot
{
    public string Name { get; set; } = "";
    public bool Exists { get; set; }
    public int? StartType { get; set; }
    public bool? DelayedAutoStart { get; set; }
    public bool? Running { get; set; }
    public List<string> RunningDependents { get; set; } = [];
    public string Issue { get; set; } = "";
}

public sealed class StripServiceBackup
{
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public int OriginalStartType { get; set; }
    public bool OriginalDelayedAutoStart { get; set; }
    public bool WasRunning { get; set; }
    public List<string> OriginalRunningDependents { get; set; } = [];
    public int ApplyOrder { get; set; }
    public bool ApplyAttempted { get; set; }
    public bool StopAttempted { get; set; }
    public bool ConfigurationAttempted { get; set; }
    public bool Restored { get; set; }
    public string Issue { get; set; } = "";
}

public sealed class StripGroupStatus
{
    public string Id { get; set; } = "";
    public string State { get; set; } = "Captured";
    public string Issue { get; set; } = "";
}

public sealed class StripBackup
{
    public int Schema { get; set; } = 1;
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public List<string> SelectedGroups { get; set; } = [];
    public List<StripServiceBackup> Services { get; set; } = [];
    public List<StripGroupStatus> Groups { get; set; } = [];
    public bool ApplyAttempted { get; set; }
    public bool RepairAttempted { get; set; }
    public bool FullyRestored => Services.All(service => !service.ApplyAttempted || service.Restored);
    public bool HasChanges => Services.Any(service => service.ApplyAttempted && !service.Restored);
}

/// <summary>Read-only snapshots and exact SCM operations; fixtures implement this without OS access.</summary>
public interface IStripSystem
{
    GamePresence GetGamePresence();
    IReadOnlyList<StripServiceSnapshot> ReadServices(IReadOnlyList<string> serviceNames);
    int? ActivePrintJobs();
    void SetStartup(string serviceName, int startType, bool delayedAutoStart);
    void Stop(string serviceName, CancellationToken token);
    void Start(string serviceName, CancellationToken token);
}
