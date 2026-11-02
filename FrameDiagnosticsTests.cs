using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TourneyMode;

/// <summary>CSV/argument fixtures only. Never starts tracing, games or helper executables.</summary>
internal static class FrameDiagnosticsTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "TourneyFrameDiagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var files = new List<string>();
        string Csv(string name, string content)
        {
            string path = Path.Combine(directory, name + ".csv");
            File.WriteAllText(path, content, new UTF8Encoding(false)); files.Add(path); return path;
        }
        string Rows(string header, IEnumerable<string> rows) => header + "\r\n" + string.Join("\r\n", rows) + "\r\n";
        const string game = "FortniteClient-Win64-Shipping.exe";
        try
        {
            await test("frame diagnostics legacy arithmetic, tail percentiles and missing CPU busy", () =>
            {
                string path = Csv("legacy", Rows("Application,ProcessID,SwapChainAddress,msBetweenPresents,msInPresentAPI,msGPUActive",
                    Enumerable.Range(0, 200).Select(i => $"{game},7,0xa,{(i < 195 ? 10 : 100)},0.5,1")));
                var report = FrameDiagnostics.AnalyzeCsv(path, 100);
                Near(report.AverageFps, 200_000d / 2450); Near(report.OnePercentLowFps, 10); Near(report.PointOnePercentLowFps, 10);
                Near(report.P50FrameMs, 10); Near(report.P95FrameMs, 10); Near(report.P99FrameMs, 100); Near(report.P999FrameMs, 100);
                Near(report.BudgetMissPercent, 2.5); Near(report.DoubleBudgetPercent, 2.5);
                Check(report.FrameCount == 200 && report.HitchesOver50Ms == 5, "Incorrect legacy frame/hitch counts.");
                Check(report.MeanCpuBusyMs is null, "Present API duration was incorrectly treated as CPU busy time.");
                Near(report.MeanGpuBusyMs!.Value, 1);
                Check(report.ForegroundGameAtAllSamples is null, "Imported CSV invented a foreground observation.");
                _ = JsonSerializer.Serialize(report, SystemAnalysis.Json);
                return Task.CompletedTask;
            });
            await test("frame diagnostics modern busy columns remain separate from frame intervals", () =>
            {
                string path = Csv("modern", Rows("ProcessName,ProcessID,SwapChainAddress,FrameTime,CPUBusy,GPUBusy", Enumerable.Repeat($"{game},8,0xb,8,2,7.8", 200)));
                var report = FrameDiagnostics.AnalyzeCsv(path, 144);
                Near(report.AverageFps, 125); Near(report.MeanCpuBusyMs!.Value, 2); Near(report.MeanGpuBusyMs!.Value, 7.8);
                Near(report.BudgetMissPercent, 100); Near(report.DoubleBudgetPercent, 0);
                Check(report.Correlations.Any(c => c.Area == "GPU busy" && c.Confidence == "Possible correlation"), "Expected limited GPU association missing.");
                Check(report.Correlations.All(c => !c.Confidence.Contains("proof", StringComparison.OrdinalIgnoreCase)), "Correlation was labeled as proof.");
                return Task.CompletedTask;
            });
            await test("frame diagnostics selects dominant game stream and excludes UEFN and launchers", () =>
            {
                var rows = Enumerable.Repeat($"{game},10,A,10", 200)
                    .Concat(Enumerable.Repeat($"{game},10,B,20", 300)).Concat(Enumerable.Repeat($"{game},11,C,5", 150))
                    .Concat(Enumerable.Repeat("UnrealEditorFortnite-Win64-Shipping.exe,20,D,1", 1000))
                    .Concat(Enumerable.Repeat("FortniteClient-Win64-Shipping_EAC_EOS.exe,30,E,1", 1000))
                    .Concat(Enumerable.Repeat("FortniteLauncher.exe,40,F,1", 1000));
                var report = FrameDiagnostics.AnalyzeCsv(Csv("streams", Rows("Application,ProcessID,SwapChainAddress,CPUFrameTime", rows)));
                Check(report.FrameCount == 300 && report.SelectedProcessId == 10 && report.SelectedSwapChain == "B" && report.StreamsFound == 3, "Wrong dominant stream or non-game included.");
                Near(report.AverageFps, 50); Check(report.Warnings.Any(w => w.Contains("not combined")), "Stream selection was not disclosed.");
                return Task.CompletedTask;
            });
            await test("frame diagnostics handles BOM, quoted commas and invariant numbers", () =>
            {
                CultureInfo previous = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                    string row = "\"C:\\Games,Review\\FortniteClient-Win64-Shipping.exe\",7,\"0x,abc\",4.0,0,";
                    var report = FrameDiagnostics.AnalyzeCsv(Csv("quoted", Rows("\ufeffApplication,ProcessID,SwapChainAddress,FrameTime,CPUBusy,GPUBusy", Enumerable.Repeat(row, 200))));
                    Near(report.AverageFps, 250); Near(report.MeanCpuBusyMs!.Value, 0);
                    Check(report.MeanGpuBusyMs is null && report.SelectedSwapChain == "0x,abc", "Quoted comma or missing/zero timing was confused.");
                }
                finally { CultureInfo.CurrentCulture = previous; }
                return Task.CompletedTask;
            });
            await test("frame diagnostics rejects invalid intervals without fabricating zero busy time", () =>
            {
                var invalid = new[] { "NaN", "Infinity", "-1", "0", "1e309", "0.00000001" }.Select(value => $"{game},7,A,{value},bad,");
                var rows = Enumerable.Repeat($"{game},7,A,10,bad,", 100).Concat(invalid).Concat(new[] { "bad,columns", "\"unclosed" });
                var report = FrameDiagnostics.AnalyzeCsv(Csv("invalid", Rows("Application,ProcessID,SwapChainAddress,FrameTime,CPUBusy,GPUBusy", rows)));
                Check(report.FrameCount == 100 && report.InvalidRows == 6 && report.MalformedRows == 2, "Invalid row accounting failed.");
                Check(report.MeanCpuBusyMs is null && report.MeanGpuBusyMs is null, "Missing busy metrics were converted to zero.");
                Near(report.AverageFps, 100); return Task.CompletedTask;
            });
            await test("frame diagnostics bounds incident output and preserves pre/post context", () =>
            {
                var rows = Enumerable.Range(0, 2000).Select(i => $"{game},7,A,{(i % 100 == 0 || i == 1999 ? 100 : 4)}");
                var report = FrameDiagnostics.AnalyzeCsv(Csv("incidents", Rows("Application,ProcessID,SwapChainAddress,FrameTime", rows)));
                Check(report.Incidents.Count == 12, "Incident count is not bounded to the worst twelve separated windows.");
                Check(report.Incidents.All(i => i.Before.Count <= 8 && i.After.Count <= 8), "Unbounded incident context.");
                Check(report.Incidents.First().Frame.FrameNumber == 1 && report.Incidents.First().Before.Count == 0 && report.Incidents.First().After.Count == 8, "Boundary context failed.");
                Check(FrameDiagnostics.Format(report).Contains("not end-to-end input latency", StringComparison.OrdinalIgnoreCase), "Report lost the latency limitation.");
                return Task.CompletedTask;
            });
            await test("frame diagnostics refuses unsupported CSV shapes and excessive input", () =>
            {
                Expect<InvalidDataException>(() => FrameDiagnostics.AnalyzeCsv(Csv("short", Rows("Application,FrameTime", Enumerable.Repeat(game + ",10", 99)))));
                Expect<InvalidDataException>(() => FrameDiagnostics.AnalyzeCsv(Csv("duplicate", "Application,FrameTime,FrameTime\n")));
                Expect<InvalidDataException>(() => FrameDiagnostics.AnalyzeCsv(Csv("missing", "Application,GPUBusy\n")));
                Expect<InvalidDataException>(() => FrameDiagnostics.AnalyzeCsv(Csv("line-limit", "Application,FrameTime\n" + new string('x', 65_537))));
                Expect<InvalidDataException>(() => FrameDiagnostics.ValidateCsvLength(FrameDiagnostics.MaxCsvBytes + 1));
                Expect<InvalidDataException>(() => FrameDiagnostics.ValidateCsvLength(0));
                FrameDiagnostics.ValidateCsvLength(FrameDiagnostics.MaxCsvBytes);
                Expect<ArgumentOutOfRangeException>(() => FrameDiagnostics.AnalyzeCsv("not-read.csv", 0));
                return Task.CompletedTask;
            });
            await test("frame diagnostics capture requests are bounded and never address other sessions", () =>
            {
                FrameDiagnostics.ValidateCaptureRequest(5, 240); FrameDiagnostics.ValidateCaptureRequest(180, 360);
                Expect<ArgumentOutOfRangeException>(() => FrameDiagnostics.ValidateCaptureRequest(4, 240));
                Expect<ArgumentOutOfRangeException>(() => FrameDiagnostics.ValidateCaptureRequest(181, 240));
                Expect<ArgumentOutOfRangeException>(() => FrameDiagnostics.ValidateCaptureRequest(60, 2001));
                var args = FrameDiagnostics.CaptureArguments(1234, "chosen-output.csv", 60, "TourneyDiagnostic-owned");
                Check(args.Contains("--process_id") && args.Contains("1234") && args.Contains("--session_name") && args.Contains("TourneyDiagnostic-owned"), "Exact PID/session not supplied.");
                Check(!args.Contains("--stop_existing_session") && !args.Contains("--terminate_existing_session") && !args.Contains("--restart_as_admin"), "Capture can stop another session or request elevation.");
                return Task.CompletedTask;
            });
        }
        finally
        {
            foreach (string file in files) { try { File.Delete(file); } catch { } }
            try { Directory.Delete(directory, recursive: false); } catch { }
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected) { if (Math.Abs(actual - expected) > .000001 * Math.Max(1, Math.Abs(expected))) throw new InvalidOperationException($"Expected {expected:R}; observed {actual:R}."); }
    private static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }
}
