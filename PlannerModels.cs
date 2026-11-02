namespace TourneyMode;

public enum OptimizationGoal { StableEndgames, LowestLatency, HighestFps, Balanced }
public enum SessionActivity { Gaming, Streaming, Recording }
public enum PowerPreference { Automatic, Original, Sustained, Maximum }
public enum FeatureState { Pending, Applied, Skipped, Deferred, Denied, Failed, Recovered }
public enum PlanDisposition { Recommended, Preserved, Advisory, Unavailable, Deferred }

public sealed class UserIntent
{
    public FpsLatencyOptions Fps { get; set; } = new();
    public OptimizationGoal Goal { get; set; } = OptimizationGoal.StableEndgames;
    public SessionActivity Activity { get; set; }
    public PowerPreference Power { get; set; } = PowerPreference.Automatic;
    public int TargetFps { get; set; } // 0 keeps the user's current limiter.
    public bool MatchSafe { get; set; } = true;
    public bool AllowNetworkExperiment { get; set; }
    public bool AllowHigherHeat { get; set; }
    public bool AcceptTearing { get; set; }
}

public sealed class PlanAction
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public PlanDisposition Disposition { get; set; }
    public string Reason { get; set; } = "";
    public string Tradeoff { get; set; } = "";
    public string Confidence { get; set; } = "Detected / likely appropriate; benefit unmeasured";
}

public sealed class OptimizationPlan
{
    public int Schema { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string InventoryFingerprint { get; set; } = "";
    public UserIntent Intent { get; set; } = new();
    public List<PlanAction> Actions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool RecoveryPending { get; set; }
    public bool GameRunning { get; set; }
    public PowerPreference EffectivePower { get; set; }
    public bool NvidiaGpu { get; set; }
    public string GameSettingsFingerprint { get; set; } = "Unavailable";
    public WindowsProfileBackup? WindowsProfile { get; set; }
    public string Summary { get; set; } = "";
}

public sealed class FeatureResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public FeatureState State { get; set; }
    public string Detail { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
