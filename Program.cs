using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace TourneyMode;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--apply-update")
                return AppUpdateConfiguration.CreateUpdater().RunApplyHelperAsync(args[1], CancellationToken.None).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : "self-test-results.txt").GetAwaiter().GetResult();
            if (args.Length == 2 && args[0] == "--restore-report")
            {
                using var recoveryMutex = new Mutex(true, @"Local\TourneyMode-" + Environment.UserName, out bool recoveryOwner);
                if (!recoveryOwner) throw new InvalidOperationException("Another Tourney Mode controller is running; use that controller to restore.");
                var recovery = new TourneyEngine();
                if (recovery.GameClientRunning) throw new InvalidOperationException("Report-only recovery requires Fortnite to be closed; no settings were changed.");
                Task.Run(recovery.RestoreAsync).GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { RecoveryComplete = !recovery.IsActive, Phase = recovery.State?.Phase ?? "OFF" }, new JsonSerializerOptions { WriteIndented = true }));
                return recovery.IsActive ? 2 : 0;
            }
            if (args.Length == 2 && args[0] == "--headroom-audit") { HeadroomAudit.Write(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--inspect-codex")
            {
                File.WriteAllText(args[1], JsonSerializer.Serialize(CodexLifecycle.Capture(new TourneyEngine().LoadOptions().ProtectedAppPaths),
                    new JsonSerializerOptions { WriteIndented = true })); return 0;
            }
            if (args.Length == 2 && args[0] == "--analyze-system")
            {
                var report = SystemAnalysis.CaptureAsync(new TourneyEngine().LoadOptions()).GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(report, SystemAnalysis.Json)); return 0;
            }
            if (args.Length == 2 && args[0] == "--tweak-preview")
            {
                var engine = new TourneyEngine();
                var options = engine.LoadOptions(); options.ApplyWindowsProfile = true;
                var result = new TweakWorkflow(engine).AnalyzeAsync(options).GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(result, SystemAnalysis.Json)); return 0;
            }
            if (args.Length == 2 && args[0] == "--check-update")
            {
                using var updater = AppUpdateConfiguration.CreateUpdater(new TourneyEngine());
                var result = updater.CheckAsync().GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(result, SystemAnalysis.Json));
                return result.Status == UpdateCheckStatus.Failed ? 1 : 0;
            }
            if (args.Length > 1 && args[0] == "--verify-inactive-plan") return VerifyInactivePlan(args[1], args.Contains("--fortnite-only"));
            if (args.Length == 2 && args[0] == "--benchmark-focus") return BenchmarkRunner.RunForegroundHelper(args[1]);
            if (args.Length > 1 && args[0] == "--benchmark") return BenchmarkRunner.Run(args[1]).GetAwaiter().GetResult();
            if (args.Length > 2 && args[0] == "--validate-benchmark-plan")
            {
                File.WriteAllText(args[2], JsonSerializer.Serialize(BenchmarkRunner.PreviewPlan(args[1]), new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length > 1 && args[0] == "--inspect")
            {
                var engine = new TourneyEngine();
                bool fortniteOnly = args.Contains("--fortnite-only");
                bool maximumHeadroom = args.Contains("--maximum-headroom");
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { Active = engine.IsActive, FortniteOnly = fortniteOnly, MaximumHeadroom = maximumHeadroom, Apps = engine.Scan(fortniteOnly, maximumHeadroom), Bridge = ProcessCatalog.ProbeLocalBridge(), NotificationCenterDisabledByPolicy = WindowsSettings.IsNotificationCenterDisabledByPolicy() }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            ApplicationConfiguration.Initialize();
            if (args.Length == 2 && args[0] == "--preview-dialogs") { UiPreview.Write(args[1]); return 0; }
            if (args.Length > 1 && args[0] == "--preview")
            {
                using var form = new MainForm(new TourneyEngine(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "preview-state")), preview: true);
                form.PreparePreview();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                return 0;
            }
            using var mutex = new Mutex(true, @"Local\TourneyMode-" + Environment.UserName, out bool first);
            if (!first)
            {
                if (!args.Contains("--restore"))
                {
                    Forms.MessageBox.Show("This " + ReleaseIdentity.Title + " copy did not start because another Tourney Mode copy is already running.\r\n\r\n"
                        + "That running copy may be an older version. When it is safe, finish its session, use its tray's Turn off and exit command, then reopen this EXE. Closing only its main window can leave it running in the tray.\r\n\r\n"
                        + "The existing copy and Fortnite were left unchanged.", ReleaseIdentity.Title + " — another copy is running",
                        Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                    return 2;
                }
                var window = ReleaseIdentity.FindController();
                if (window == IntPtr.Zero) return BenchmarkRunner.RequestStop() ? 0 : 1;
                if (!PostMessage(window, 0x8001, IntPtr.Zero, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not deliver the recovery request to the existing controller.");
                return 0;
            }
            var liveEngine = new TourneyEngine();
            if (args.Contains("--restore") && liveEngine.IsActive)
            {
                try { Task.Run(liveEngine.RestoreAsync).GetAwaiter().GetResult(); if (!liveEngine.IsActive) return 0; }
                catch { /* Show recovery UI and retain journal on partial failure. */ }
            }
            if (args.Contains("--restore") && !liveEngine.IsActive) return 0;
            Forms.Application.Run(new MainForm(liveEngine));
            GC.KeepAlive(mutex);
            return 0;
        }
        catch (Exception e)
        {
            if (args.Length > 0 && args[0].StartsWith("--"))
            {
                try { File.WriteAllText(args.Length > 1 ? args[1] + ".error.txt" : "tourney-error.txt", e.ToString()); } catch { }
            }
            else Forms.MessageBox.Show("Tourney Mode could not start. Existing recovery files were preserved.\r\n\r\n" + e.Message,
                "Tourney Mode", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int VerifyInactivePlan(string output, bool fortniteOnly)
    {
        string original = WindowsSettings.GetPowerScheme();
        string clone = Guid.NewGuid().ToString("D");
        // Diagnostic alters only an inactive temporary copy; persist its identity for cleanup.
        File.WriteAllText(output, $"Original: {original}\r\nTemporary inactive test plan: {clone}\r\nCleanup pending\r\n");
        var notes = new List<string>();
        try
        {
            notes = PerformanceTuning.CreatePlan(original, clone, fortniteOnly);
            if (!WindowsSettings.GetPowerScheme().Equals(original, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Active plan changed unexpectedly.");
        }
        finally { PerformanceTuning.DeletePlan(clone); }
        File.WriteAllLines(output, new[] { "PASS: created and configured an inactive Tourney Mode plan, verified values, deleted temporary plan.", "Active power plan remained: " + original, "Unsupported setting warnings: " + notes.Count }.Concat(notes));
        return notes.Count == 0 ? 0 : 2;
    }
}
