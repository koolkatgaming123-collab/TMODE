using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;

namespace TourneyMode;

public sealed class FrameDiagnosticReport
{
    public DateTime AnalyzedUtc { get; set; } = DateTime.UtcNow;
    public string SourceCsv { get; set; } = "";
    public int TargetFps { get; set; }
    public double FrameBudgetMs { get; set; }
    public int FrameCount { get; set; }
    public double DurationSeconds { get; set; }
    public double AverageFps { get; set; }
    public double OnePercentLowFps { get; set; }
    public double PointOnePercentLowFps { get; set; }
    public double P50FrameMs { get; set; }
    public double P95FrameMs { get; set; }
    public double P99FrameMs { get; set; }
    public double P999FrameMs { get; set; }
    public double BudgetMissPercent { get; set; }
    public double DoubleBudgetPercent { get; set; }
    public int HitchesOver50Ms { get; set; }
    public int? SelectedProcessId { get; set; }
    public string SelectedSwapChain { get; set; } = "Unknown";
    public int StreamsFound { get; set; }
    public string IntervalColumn { get; set; } = "";
    public string Timeline { get; set; } = "Cumulative valid present intervals; not synchronized to external sensors.";
    public int InvalidRows { get; set; }
    public int MalformedRows { get; set; }
    public double? MeanCpuBusyMs { get; set; }
    public double? MeanGpuBusyMs { get; set; }
    public bool? ForegroundGameAtAllSamples { get; set; }
    public int? RequestedCaptureSeconds { get; set; }
    public DateTime? CaptureStartedUtc { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<FrameCorrelation> Correlations { get; set; } = [];
    public List<FrameIncident> Incidents { get; set; } = [];
    public List<CaptureContextSample> CaptureContext { get; set; } = [];
}

public sealed class FrameCorrelation
{
    public string Area { get; set; } = "";
    public string Assessment { get; set; } = "Unknown";
    public string Evidence { get; set; } = "";
    public string Confidence { get; set; } = "Unknown";
}

public sealed class FrameContextSample
{
    public int FrameNumber { get; set; }
    public double TimeSeconds { get; set; }
    public double FrameTimeMs { get; set; }
    public double? CpuBusyMs { get; set; }
    public double? GpuBusyMs { get; set; }
}

public sealed class FrameIncident
{
    public FrameContextSample Frame { get; set; } = new();
    public string Reason { get; set; } = "";
    public List<FrameContextSample> Before { get; set; } = [];
    public List<FrameContextSample> After { get; set; } = [];
}

public sealed class CaptureContextSample
{
    public double Seconds { get; set; }
    public uint? MemoryLoadPercent { get; set; }
    public double? CommitUsedGiB { get; set; }
    public double? CommitLimitGiB { get; set; }
    public bool ForegroundGame { get; set; }
}

/// <summary>Explicit CSV analysis or opt-in capture. Never called by normal mode maintenance.</summary>
public static class FrameDiagnostics
{
    public const long MaxCsvBytes = 256L * 1024 * 1024;
    internal const int MaxFrames = 1_000_000;
    private const int MaxStreams = 256, MaxRows = 2_000_000, MaxLineCharacters = 65_536;
    private const string Game = "FortniteClient-Win64-Shipping.exe";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly record struct StreamKey(int? Pid, string Swap);
    private readonly record struct Frame(double Ms, double? Cpu, double? Gpu, double Time);

    public static FrameDiagnosticReport AnalyzeCsv(string path, int targetFps = 240)
    {
        ValidateTarget(targetFps);
        string fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        ValidateCsvLength(stream.Length);
        var counts = new Dictionary<StreamKey, int>();
        int invalid = 0, malformed = 0;
        string interval;
        using (var reader = Reader(stream))
        {
            var columns = Columns.Read(reader);
            interval = columns.IntervalName;
            int rows = 0;
            while (ReadLine(reader) is { } line)
            {
                if (++rows > MaxRows) throw new InvalidDataException("CSV exceeds the two-million-row diagnostic limit.");
                if (!TryRow(line, columns, out var key, out _, out bool game, out bool bad))
                { if (bad) malformed++; else if (game) invalid++; continue; }
                if (!counts.TryGetValue(key, out int count) && counts.Count == MaxStreams)
                    throw new InvalidDataException("Too many Fortnite process/swap-chain streams; split the capture.");
                if (count == MaxFrames) throw new InvalidDataException("A stream exceeds one million frames; export a shorter capture.");
                counts[key] = count + 1;
            }
        }
        if (counts.Count == 0) throw new InvalidDataException("No valid main Fortnite game frames were found. Launcher, UEFN and EAC bootstrap rows are excluded.");
        var selected = counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key.Pid ?? int.MaxValue).ThenBy(pair => pair.Key.Swap, StringComparer.Ordinal).First();
        if (selected.Value < 100) throw new InvalidDataException("Fewer than 100 valid frames in the dominant game stream; use a longer capture.");
        var frames = new List<Frame>(selected.Value);
        stream.Position = 0;
        using (var reader = Reader(stream))
        {
            var columns = Columns.Read(reader);
            double elapsed = 0;
            while (ReadLine(reader) is { } line)
            {
                if (!TryRow(line, columns, out var key, out var frame, out _, out _) || key != selected.Key) continue;
                if (!double.IsFinite(elapsed + frame.Ms)) throw new InvalidDataException("Frame interval sum is not finite; the CSV contains invalid timing data.");
                frames.Add(frame with { Time = elapsed / 1000 });
                elapsed += frame.Ms;
            }
        }
        var times = frames.Select(frame => frame.Ms).Order().ToArray();
        double sum = times.Sum(), budget = 1000d / targetFps;
        var report = new FrameDiagnosticReport
        {
            SourceCsv = fullPath, TargetFps = targetFps, FrameBudgetMs = budget,
            FrameCount = times.Length, DurationSeconds = sum / 1000, AverageFps = times.Length * 1000d / sum,
            OnePercentLowFps = Low(times, .01), PointOnePercentLowFps = Low(times, .001),
            P50FrameMs = Percentile(times, .50), P95FrameMs = Percentile(times, .95),
            P99FrameMs = Percentile(times, .99), P999FrameMs = Percentile(times, .999),
            BudgetMissPercent = times.Count(ms => ms > budget) * 100d / times.Length,
            DoubleBudgetPercent = times.Count(ms => ms > budget * 2) * 100d / times.Length,
            HitchesOver50Ms = times.Count(ms => ms > 50), SelectedProcessId = selected.Key.Pid,
            SelectedSwapChain = selected.Key.Swap, StreamsFound = counts.Count, IntervalColumn = interval,
            InvalidRows = invalid, MalformedRows = malformed,
            MeanCpuBusyMs = Mean(frames.Select(frame => frame.Cpu)), MeanGpuBusyMs = Mean(frames.Select(frame => frame.Gpu))
        };
        if (counts.Count > 1) report.Warnings.Add($"Selected the largest valid stream ({selected.Value} frames) from {counts.Count} streams. Other streams were not combined.");
        if (invalid + malformed > 0) report.Warnings.Add($"Skipped {invalid} invalid Fortnite intervals and {malformed} malformed CSV records across the input.");
        if (report.DurationSeconds < 10) report.Warnings.Add("Less than ten seconds of frame intervals: tail statistics are a short sample, not a stable endgame baseline.");
        if (times.Length < 10_000) report.Warnings.Add("The 0.1% low/p99.9 use few tail frames in this sample; interpret them cautiously.");
        report.Warnings.Add("Budget misses use a strict >1000/target-FPS comparison. Normal cap jitter can register a miss; read the tail percentiles and double-budget misses together. Present cadence can include undisplayed frames.");
        report.Warnings.Add("Imported CSV alone cannot verify foreground state, match conditions, shader warmup or thermal stability. FPS and frame intervals are not end-to-end input latency.");
        Correlate(report, frames, frame => frame.Cpu, "CPU busy");
        Correlate(report, frames, frame => frame.Gpu, "GPU busy");
        report.Correlations.Add(new() { Area = "Cause limits", Assessment = "Not measured", Evidence = "Per-core saturation, effective clocks, thermal throttling, hard faults, kernel DPC/ISR attribution and server/network delay require separate evidence.", Confidence = "Unknown" });
        var chosen = new List<int>();
        foreach (int index in Enumerable.Range(0, frames.Count).Where(i => frames[i].Ms > budget * 2 || frames[i].Ms > 50).OrderByDescending(i => frames[i].Ms))
        {
            if (chosen.Any(old => Math.Abs(old - index) <= 16)) continue;
            chosen.Add(index);
            if (chosen.Count == 12) break;
        }
        foreach (int index in chosen.Order())
        {
            var incident = new FrameIncident { Frame = Context(frames[index], index), Reason = frames[index].Ms > 50 ? "Frame interval above 50 ms" : "Frame interval above twice the target budget" };
            for (int i = Math.Max(0, index - 8); i < index; i++) incident.Before.Add(Context(frames[i], i));
            for (int i = index + 1; i < Math.Min(frames.Count, index + 9); i++) incident.After.Add(Context(frames[i], i));
            report.Incidents.Add(incident);
        }
        return report;
    }

    public static async Task<FrameDiagnosticReport> CaptureAsync(string toolPath, string outputCsv, int seconds = 60, int targetFps = 240, CancellationToken token = default)
    {
        ValidateCaptureRequest(seconds, targetFps);
        token.ThrowIfCancellationRequested();
        string tool = Path.GetFullPath(toolPath), output = Path.GetFullPath(outputCsv);
        if (!File.Exists(tool) || !Path.GetExtension(tool).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("Select an existing official Intel PresentMon executable.", tool);
        if (!Path.GetExtension(output).Equals(".csv", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Capture output must use the .csv extension.", nameof(outputCsv));
        if (File.Exists(output)) throw new IOException("Capture output already exists; choose a new file. Existing data is never replaced.");
        VerifyIntelTool(tool);
        var games = ProcessCatalog.Snapshot(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Game })
            .Where(p => p.SessionId == Process.GetCurrentProcess().SessionId && ProcessPolicy.IsFortnite(p)).ToList();
        if (games.Count != 1) throw new InvalidOperationException("Exactly one current-session main Fortnite client must be running. This diagnostic never launches the game.");
        var game = games[0];
        if (!GameForeground(game.Id)) throw new InvalidOperationException("Fortnite must be in the foreground after the countdown. Switch to the game and explicitly start a new capture.");
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string session = "TourneyDiagnostic-" + Guid.NewGuid().ToString("N");
        string staging = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileNameWithoutExtension(output) + "." + session + ".csv");
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(output)!
        };
        foreach (string argument in CaptureArguments(game.Id, staging, seconds, session)) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 15));
        using var sampleStop = new CancellationTokenSource();
        var samples = new List<CaptureContextSample>(seconds + 2);
        DateTime started = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException("PresentMon could not start.");
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput), stderr = ReadBoundedAsync(process.StandardError);
        Task sampling = SampleContextAsync(samples, clock, game.Id, sampleStop.Token);
        bool endedNormally = false;
        try
        {
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { }
                await StopOwnedSessionAsync(tool, session).ConfigureAwait(false);
                if (token.IsCancellationRequested) throw new OperationCanceledException("Diagnostic capture cancelled; any partial CSV is retained in the selected folder.", token);
                throw new TimeoutException("PresentMon exceeded its bounded diagnostic deadline; any partial CSV is retained in the selected folder.");
            }
            endedNormally = true;
            string error = await stderr.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await stdout.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                await StopOwnedSessionAsync(tool, session).ConfigureAwait(false);
                throw new InvalidOperationException($"PresentMon exited with code {process.ExitCode}. Capturing may require Windows tracing permissions. {SafeError(error)}");
            }
        }
        finally
        {
            sampleStop.Cancel();
            try { await sampling.ConfigureAwait(false); } catch (OperationCanceledException) { }
            samples.Add(Sample(clock.Elapsed.TotalSeconds, game.Id));
            if (!endedNormally) { try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { } }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
            if (File.Exists(staging))
            {
                try { File.Move(staging, output, overwrite: false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A concurrent destination is never replaced; the unique partial CSV remains beside it. */ }
            }
        }
        token.ThrowIfCancellationRequested();
        if (File.Exists(staging)) throw new IOException("The selected destination appeared during capture. It was not replaced; the diagnostic CSV is retained as " + staging);
        var report = await Task.Run(() => AnalyzeOnBackgroundWorker(output, targetFps), token).ConfigureAwait(false);
        report.CaptureStartedUtc = started;
        report.RequestedCaptureSeconds = seconds;
        report.CaptureContext = samples;
        report.ForegroundGameAtAllSamples = samples.Count > 0 && samples.All(sample => sample.ForegroundGame);
        report.Warnings.RemoveAll(warning => warning.StartsWith("Imported CSV alone", StringComparison.Ordinal));
        report.Warnings.Add("Foreground was checked at boundaries and about once per second; scene changes or brief focus changes between samples remain unverified. No end-to-end input latency is measured.");
        if (report.ForegroundGameAtAllSamples != true) report.Warnings.Add("Fortnite lost foreground in at least one sample. Do not use this capture as a controlled FPS comparison.");
        if (!ProcessCatalog.Matches(game)) report.Warnings.Add("The original Fortnite process exited or its identity changed; this is not a continuous same-client performance comparison.");
        if (report.SelectedProcessId != game.Id) throw new InvalidDataException("The resulting CSV did not identify the exact captured Fortnite process.");
        var loads = samples.Where(sample => sample.MemoryLoadPercent.HasValue).ToList();
        if (loads.Count > 0)
        {
            uint maximum = loads.Max(sample => sample.MemoryLoadPercent!.Value);
            report.Correlations.Add(new() { Area = "System memory", Assessment = maximum >= 90 ? "High pressure observed" : "No high load in sampled global values", Evidence = $"Peak physical memory load {maximum}%, sampled about once per second. This does not identify a hard-fault or frame-stall cause.", Confidence = "Observed context" });
        }
        return report;
    }

    internal static void ValidateCaptureRequest(int seconds, int targetFps)
    {
        ValidateTarget(targetFps);
        if (seconds is < 5 or > 180) throw new ArgumentOutOfRangeException(nameof(seconds), "Capture duration must be between 5 and 180 seconds.");
    }
    internal static void ValidateCsvLength(long bytes)
    {
        if (bytes > MaxCsvBytes) throw new InvalidDataException("CSV exceeds the 256 MiB diagnostic limit. Export a shorter capture.");
        if (bytes <= 0) throw new InvalidDataException("The CSV is empty.");
    }
    internal static string[] CaptureArguments(int pid, string output, int seconds, string session) =>
        ["--process_id", pid.ToString(Invariant), "--output_file", output, "--timed", seconds.ToString(Invariant), "--terminate_after_timed", "--terminate_on_proc_exit", "--no_console_stats", "--v2_metrics", "--session_name", session];
    private static void ValidateTarget(int targetFps) { if (targetFps is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(targetFps), "Target FPS must be between 1 and 2000."); }
    private static FrameDiagnosticReport AnalyzeOnBackgroundWorker(string path, int targetFps)
    {
        var thread = Thread.CurrentThread; ThreadPriority original = thread.Priority; bool changed = false;
        try
        {
            try { thread.Priority = ThreadPriority.BelowNormal; changed = true; } catch (System.ComponentModel.Win32Exception) { }
            return AnalyzeCsv(path, targetFps);
        }
        finally { if (changed) { try { thread.Priority = original; } catch (System.ComponentModel.Win32Exception) { } } }
    }
    private static StreamReader Reader(Stream stream) => new(stream, new UTF8Encoding(false, true), true, 64 * 1024, leaveOpen: true);
    private static string? ReadLine(StreamReader reader)
    {
        var line = new StringBuilder(256);
        while (true)
        {
            int value = reader.Read();
            if (value < 0) return line.Length == 0 ? null : line.ToString();
            if (value == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length == MaxLineCharacters) throw new InvalidDataException("A CSV record exceeds the 64 Ki-character limit.");
            line.Append((char)value);
        }
    }
    private static List<string> Split(string line)
    {
        var fields = new List<string>(); var value = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { fields.Add(value.ToString()); value.Clear(); if (fields.Count >= 128) throw new InvalidDataException("Too many CSV columns."); }
            else value.Append(c);
        }
        if (quoted) throw new InvalidDataException("Unclosed quoted CSV record; multiline fields are not supported.");
        fields.Add(value.ToString()); return fields;
    }
    private sealed class Columns
    {
        public int Application, Interval, Pid, Swap, Cpu, Gpu, Count;
        public string IntervalName = "";
        public static Columns Read(StreamReader reader)
        {
            string line = ReadLine(reader) ?? throw new InvalidDataException("CSV header is missing.");
            var header = Split(line).Select(name => name.Trim().TrimStart('\ufeff')).ToList();
            if (header.Distinct(StringComparer.OrdinalIgnoreCase).Count() != header.Count) throw new InvalidDataException("CSV contains duplicate column names.");
            int Find(params string[] names) { foreach (string name in names) { int index = header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase)); if (index >= 0) return index; } return -1; }
            var result = new Columns { Application = Find("Application", "ProcessName", "ApplicationName"), Interval = Find("msBetweenPresents", "FrameTime", "CPUFrameTime"), Pid = Find("ProcessID", "PID"), Swap = Find("SwapChainAddress", "SwapChain"), Cpu = Find("CPUBusy"), Gpu = Find("GPUBusy", "msGPUActive"), Count = header.Count };
            if (result.Application < 0 || result.Interval < 0) throw new InvalidDataException("CSV needs a process-name column and present/frame interval in milliseconds.");
            result.IntervalName = header[result.Interval]; return result;
        }
    }
    private static bool TryRow(string line, Columns columns, out StreamKey key, out Frame frame, out bool game, out bool malformed)
    {
        key = default; frame = default; game = malformed = false;
        List<string> values;
        try { values = Split(line); } catch (InvalidDataException) { malformed = true; return false; }
        if (values.Count != columns.Count) { malformed = true; return false; }
        string name = values[columns.Application].Trim().Replace('/', '\\');
        name = name[(name.LastIndexOf('\\') + 1)..];
        game = name.Equals(Game, StringComparison.OrdinalIgnoreCase) || name.Equals(Game[..^4], StringComparison.OrdinalIgnoreCase);
        if (!game) return false;
        double? ms = Number(values[columns.Interval]);
        if (ms is null or < .001) return false;
        int? pid = null;
        if (columns.Pid >= 0)
        {
            if (!int.TryParse(values[columns.Pid], NumberStyles.Integer, Invariant, out int parsed) || parsed <= 0) return false;
            pid = parsed;
        }
        key = new(pid, columns.Swap >= 0 ? values[columns.Swap].Trim() : "Unknown");
        frame = new(ms.Value, columns.Cpu >= 0 ? Number(values[columns.Cpu]) : null, columns.Gpu >= 0 ? Number(values[columns.Gpu]) : null, 0);
        return true;
    }
    private static double? Number(string value) => double.TryParse(value.Trim(), NumberStyles.Float, Invariant, out double number) && double.IsFinite(number) && number is >= 0 and <= 1_000_000_000 ? number : null;
    private static double Percentile(double[] sorted, double fraction) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];
    private static double Low(double[] sorted, double fraction) { int count = Math.Max(1, (int)Math.Ceiling(sorted.Length * fraction)); return 1000d / sorted.Skip(sorted.Length - count).Average(); }
    private static double? Mean(IEnumerable<double?> values) { var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToList(); return present.Count > 0 ? present.Average() : null; }
    private static FrameContextSample Context(Frame frame, int index) => new() { FrameNumber = index + 1, TimeSeconds = frame.Time, FrameTimeMs = frame.Ms, CpuBusyMs = frame.Cpu, GpuBusyMs = frame.Gpu };
    private static void Correlate(FrameDiagnosticReport report, List<Frame> frames, Func<Frame, double?> selector, string area)
    {
        var available = frames.Where(frame => selector(frame).HasValue).ToList();
        if (available.Count == 0) { report.Correlations.Add(new() { Area = area, Assessment = "Unavailable", Evidence = "This CSV has no supported recorded busy-time column. Frame intervals cannot substitute for CPU/GPU busy time." }); return; }
        var missed = available.Where(frame => frame.Ms > report.FrameBudgetMs).ToList();
        int near = missed.Count(frame => selector(frame) >= frame.Ms * .85);
        bool possible = available.Count >= frames.Count * .8 && missed.Count >= 30 && near >= missed.Count * .6;
        report.Correlations.Add(new() { Area = area, Assessment = possible ? "Possible execution pressure" : "No consistent high-busy association established", Evidence = $"Busy timing present in {available.Count}/{frames.Count} frames; {near}/{missed.Count} over-budget frames had recorded busy duration at least 85% of their interval. Overlap, queues and timing definitions prevent proof of the limiting subsystem.", Confidence = possible ? "Possible correlation" : "Limited correlation" });
    }

    public static string Format(FrameDiagnosticReport report)
    {
        var b = new StringBuilder();
        b.AppendLine("OPTIONAL FRAME DIAGNOSTIC").AppendLine($"{report.FrameCount:N0} frames · {report.DurationSeconds:0.00}s of present intervals · target {report.TargetFps} FPS / {report.FrameBudgetMs:0.###} ms")
            .AppendLine($"Average FPS {report.AverageFps:0.00} · 1% low {report.OnePercentLowFps:0.00} · 0.1% low {report.PointOnePercentLowFps:0.00}")
            .AppendLine($"Frame ms p50 {report.P50FrameMs:0.###} · p95 {report.P95FrameMs:0.###} · p99 {report.P99FrameMs:0.###} · p99.9 {report.P999FrameMs:0.###}")
            .AppendLine($"Above budget {report.BudgetMissPercent:0.##}% · above 2× budget {report.DoubleBudgetPercent:0.##}% · >50ms hitches {report.HitchesOver50Ms}")
            .AppendLine($"Stream PID {report.SelectedProcessId?.ToString() ?? "unknown"} / {report.SelectedSwapChain} · interval {report.IntervalColumn}")
            .AppendLine($"CPU busy mean {report.MeanCpuBusyMs?.ToString("0.###") ?? "unknown"} ms · GPU busy mean {report.MeanGpuBusyMs?.ToString("0.###") ?? "unknown"} ms")
            .AppendLine($"Foreground samples: {(report.ForegroundGameAtAllSamples == true ? "all observed Fortnite" : report.ForegroundGameAtAllSamples == false ? "interrupted / unsuitable for comparison" : "unknown for imported CSV")}").AppendLine();
        foreach (var correlation in report.Correlations) b.AppendLine($"{correlation.Area}: {correlation.Assessment} ({correlation.Confidence})").AppendLine(correlation.Evidence);
        b.AppendLine().AppendLine($"Incident windows: {report.Incidents.Count}; at most 12 non-overlapping worst windows with up to 8 frames before/after.");
        foreach (var incident in report.Incidents) b.AppendLine($"{incident.Frame.TimeSeconds:0.000}s · frame {incident.Frame.FrameNumber} · {incident.Frame.FrameTimeMs:0.###} ms · {incident.Reason}");
        b.AppendLine(); foreach (string warning in report.Warnings) b.AppendLine("Note: " + warning);
        return b.ToString();
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder(); char[] buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0) if (text.Length < 8192) text.Append(buffer, 0, Math.Min(count, 8192 - text.Length));
        return text.ToString();
    }
    private static string SafeError(string error) => error.Replace('\r', ' ').Replace('\n', ' ').Trim()[..Math.Min(error.Replace('\r', ' ').Replace('\n', ' ').Trim().Length, 400)];
    private static async Task StopOwnedSessionAsync(string tool, string session)
    {
        try
        {
            var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--session_name", session, "--terminate_existing_session" }) info.ArgumentList.Add(arg);
            using var cleanup = Process.Start(info);
            if (cleanup is null) return;
            Task output = ReadBoundedAsync(cleanup.StandardOutput), error = ReadBoundedAsync(cleanup.StandardError);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await cleanup.WaitForExitAsync(deadline.Token).ConfigureAwait(false); } catch (OperationCanceledException) { try { cleanup.Kill(entireProcessTree: false); } catch { } }
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        }
        catch { /* Only our unpredictable-GUID session is addressed; never stop another trace. */ }
    }
    private static async Task SampleContextAsync(List<CaptureContextSample> samples, Stopwatch clock, int gamePid, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            samples.Add(Sample(clock.Elapsed.TotalSeconds, gamePid));
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
    }
    private static CaptureContextSample Sample(double seconds, int gamePid)
    {
        var memory = FrameMemoryContext.Read();
        return new() { Seconds = seconds, MemoryLoadPercent = memory.MemoryLoadPercent, CommitUsedGiB = memory.CommitUsedGiB, CommitLimitGiB = memory.CommitLimitGiB, ForegroundGame = GameForeground(gamePid) };
    }
    private static bool GameForeground(int gamePid) { GetWindowThreadProcessId(GetForegroundWindow(), out uint id); return id == gamePid; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);

    private static void VerifyIntelTool(string path)
    {
        // The shipped integration targets this exact official portable release.
        // Publisher identity alone would also accept an unrelated Intel installer.
        using (var hashStream = File.OpenRead(path))
        {
            string hash = Convert.ToHexString(SHA256.HashData(hashStream));
            if (!hash.Equals("B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF", StringComparison.Ordinal))
                throw new InvalidOperationException("Capture currently supports the official PresentMon 2.6.0 x64 executable. Its SHA256 does not match; import an existing CSV or select that unmodified release.");
        }
        // Offline Authenticode validation: no network request, download or elevation.
        // X509CertificateLoader does not extract an Authenticode signer from a PE.
        // Keep this extraction API scoped; WinVerifyTrust below validates its trust.
#pragma warning disable SYSLIB0057
        using var signedCertificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        using var certificate = X509CertificateLoader.LoadCertificate(signedCertificate.GetRawCertData());
        if (!certificate.Subject.Split(',').Select(part => part.Trim()).Any(part => part.Equals("O=Intel Corporation", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The capture executable is not signed by Intel Corporation.");
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer, ProviderFlags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new InvalidOperationException("PresentMon's Intel Authenticode signature could not be validated offline. Select the official, unmodified executable.");
        }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    { public uint Size; public IntPtr PolicyCallback, SipClient; public uint UiChoice, RevocationChecks, UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData, UrlReference; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}

internal readonly record struct FrameMemorySnapshot(uint? MemoryLoadPercent, double? CommitUsedGiB, double? CommitLimitGiB);
internal static class FrameMemoryContext
{
    public static FrameMemorySnapshot Read()
    {
        uint? load = null; double? used = null, limit = null;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory)) load = memory.Load;
        var performance = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (GetPerformanceInfo(ref performance, performance.Size))
        { used = performance.CommitTotal.ToUInt64() * (double)performance.PageSize.ToUInt64() / 1073741824; limit = performance.CommitLimit.ToUInt64() * (double)performance.PageSize.ToUInt64() / 1073741824; }
        return new(load, used, limit);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
    [StructLayout(LayoutKind.Sequential)] private struct PerformanceInformation { public uint Size; public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonPaged, PageSize; public uint Handles, Processes, Threads; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);
}
