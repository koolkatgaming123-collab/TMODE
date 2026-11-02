namespace TourneyMode;

public enum FortniteRenderer { KeepCurrent, PerformanceDx11, PerformanceDx12, DirectX11, DirectX12 }
public enum FortniteReflex { KeepCurrent, On, OnPlusBoost }
public enum FortniteFrameCap { KeepCurrent, Uncapped, Target }
public enum FortnitePriority { Normal, AboveNormal, High }
public enum GamePresence { Absent, Present, Unknown }

public sealed class FpsLatencyOptions
{
    public bool ApplyGameProfile { get; set; }
    public bool CompetitiveVisuals { get; set; } = true;
    public FortniteRenderer Renderer { get; set; }
    public FortniteReflex Reflex { get; set; } = FortniteReflex.On;
    public FortniteFrameCap FrameCap { get; set; }
    public int TargetFps { get; set; } = 240;
    public int ResolutionPercent { get; set; } // 0 preserves resolution quality.
    public bool DisableVSync { get; set; }
    public FortnitePriority GamePriority { get; set; } = FortnitePriority.High;
    public bool QuietDuringGame { get; set; } = true;
    public List<string> BackgroundAppPaths { get; set; } = [];
}

public static class FpsLatencyPolicy
{
    public static FpsLatencyOptions Normalize(FpsLatencyOptions? options, bool acceptTearing)
    {
        options ??= new();
        if (!Enum.IsDefined(options.Renderer) || !Enum.IsDefined(options.Reflex) || !Enum.IsDefined(options.FrameCap) || !Enum.IsDefined(options.GamePriority))
            throw new ArgumentException("Unknown FPS / latency choice.");
        if (options.FrameCap == FortniteFrameCap.Target && options.TargetFps is < 30 or > 1000)
            throw new ArgumentException("The game FPS cap must be between 30 and 1000.");
        if (options.ResolutionPercent != 0 && options.ResolutionPercent is < 50 or > 100)
            throw new ArgumentException("Resolution scale must be preserved (0), or between 50 and 100 percent.");
        if (options.ApplyGameProfile && options.DisableVSync && !acceptTearing)
            throw new ArgumentException("Accept tearing before requesting VSync OFF.");
        options.BackgroundAppPaths = ConnectionProtection.NormalizePaths(options.BackgroundAppPaths ?? []).Select(path => path.ToLowerInvariant()).Take(16)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        return options;
    }
}
