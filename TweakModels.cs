namespace TourneyMode;

public sealed class TweakProgress
{
    public string Stage { get; set; } = "";
    public string Detail { get; set; } = "";
    public int Percent { get; set; }
}

public sealed class TweakFinding
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string ProposedAction { get; set; } = "";
    public bool Automated { get; set; }
    public bool Attention { get; set; }
}

public sealed class TweakAssessment
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public PcAnalysis Analysis { get; set; } = new();
    public AppOptions Options { get; set; } = new();
    public OptimizationPlan Plan { get; set; } = new();
    public List<TweakFinding> Findings { get; set; } = [];
    public bool GameRunning { get; set; }
    public bool RecoveryPending { get; set; }
    public bool CanApply { get; set; }
    public string Summary { get; set; } = "";
    public string ApprovalFingerprint { get; set; } = "";
}

public sealed class TweakRunResult
{
    public bool Success { get; set; }
    public bool NeedsRecovery { get; set; }
    public string Summary { get; set; } = "";
    public List<FeatureResult> Results { get; set; } = [];
}
