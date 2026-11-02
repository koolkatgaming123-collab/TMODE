using System.Text.Json;

namespace TourneyMode;

public static partial class BenchmarkRunner
{
    private static async Task<int> RunAllOnOffAsync(BenchmarkPlan plan, BenchmarkStatus status, CancellationToken cancellation)
    {
        var system = new WindowsSystem();
        var engine = new TourneyEngine(system: system);
        engine.Log += line => AppendLog(plan, line);
        if (engine.IsActive) throw new InvalidOperationException("An existing session belongs to the user; the benchmark will not take it over.");
        status.OriginalPowerScheme = system.GetPowerScheme();
        status.InitialSnapshot = Metadata(system);
        status.InitialProcessCount = status.InitialSnapshot.ProcessCount;
        var expected = status.InitialSnapshot.Games.SingleOrDefault()
            ?? throw new InvalidOperationException("A single identifiable Fortnite process is required.");
        status.ExpectedGame = expected;
        bool completed = false, cancelled = false;
        try
        {
            for (int pair = 1; pair <= plan.Pairs; pair++)
            {
                cancellation.ThrowIfCancellationRequested();
                RequireSameGame();
                if (engine.IsActive) throw new InvalidOperationException("Previous ON block has not restored.");
                Save(plan, status, $"OFF{pair} stabilization ({plan.StabilizationSeconds}s)");
                await Task.Delay(TimeSpan.FromSeconds(plan.StabilizationSeconds), cancellation).ConfigureAwait(false);
                RequireSameGame();
                await CaptureAsync(plan, status, system, "OFF" + pair, false, cancellation).ConfigureAwait(false);

                var saved = engine.LoadOptions();
                var options = ProfileDefaults.Create();
                options.ProtectedAppPaths = saved.ProtectedAppPaths;
                options.ExcludedApps = saved.ExcludedApps;
                var apps = engine.Scan(true, true, options.ProtectedAppPaths)
                    .Where(a => !options.ExcludedApps.Contains(a.Id) && (a.DefaultSelected || saved.SelectedApps.Contains(a.Id))).ToList();
                options.SelectedApps = apps.Select(a => a.Id).Distinct().ToList();
                var block = new ComparisonBlock { Label = "ON" + pair, Mode = "ON", Options = options,
                    SelectedApps = apps.Select(a => new SelectedApp { Id = a.Id, Name = a.Name, RecordedProcessCount = a.Processes.Count }).ToList(),
                    SelectedProcessCount = apps.SelectMany(a => a.Processes).Select(p => p.Id).Distinct().Count() };
                status.Blocks.Add(block);
                status.Options = options; status.SelectedApps = block.SelectedApps; status.SelectedProcessCount = block.SelectedProcessCount;
                using var maintenanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                Task? maintenance = null;
                try
                {
                    status.EnableAttempted = block.EnableAttempted = true;
                    Save(plan, status, "Enabling all-ON preset [" + block.Label + "]");
                    await engine.EnableAsync(options).ConfigureAwait(false);
                    if (engine.State?.Phase != "Active") throw new InvalidOperationException("All-ON preset did not reach Active.");
                    block.EnabledUtc = status.EnabledUtc = DateTime.UtcNow;
                    block.ActiveState = JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(engine.State, Json), Json);
                    status.ActiveState = block.ActiveState;
                    maintenance = MaintainBlockAsync(engine, block, maintenanceCancellation.Token);
                    Save(plan, status, $"{block.Label} stabilization ({plan.StabilizationSeconds}s)");
                    await Task.Delay(TimeSpan.FromSeconds(plan.StabilizationSeconds), cancellation).ConfigureAwait(false);
                    RequireSameGame();
                    await CaptureAsync(plan, status, system, block.Label, true, cancellation, block: block).ConfigureAwait(false);
                    block.TestingSucceeded = true;
                }
                finally
                {
                    maintenanceCancellation.Cancel();
                    if (maintenance is not null)
                    {
                        try { await maintenance.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (maintenanceCancellation.IsCancellationRequested) { }
                        catch (Exception e) { block.Errors.Add("Maintenance: " + e.Message); status.Errors.Add("Maintenance: " + e.Message); }
                    }
                    status.RestorationAttempted = block.RestorationAttempted = true;
                    Save(plan, status, "Restoring [" + block.Label + "]");
                    await engine.RestoreAsync().ConfigureAwait(false);
                    block.RestorationSucceeded = !engine.IsActive && !File.Exists(status.RecoveryJournalPath);
                    block.RecoveryJournalExists = File.Exists(status.RecoveryJournalPath);
                    block.FinalPowerScheme = system.GetPowerScheme(); block.RestoredUtc = DateTime.UtcNow;
                    if (!block.RestorationSucceeded || !block.FinalPowerScheme.Equals(status.OriginalPowerScheme, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Block recovery could not be verified; no further trials will start.");
                    Save(plan, status, block.Label + " restored");
                }
                if (status.Errors.Count > 0) throw new InvalidOperationException("A block failed; later trials were not started.");
            }
            completed = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
        catch (Exception e) { status.Errors.Add(e.ToString()); AppendLog(plan, "All-ON/OFF test: " + e.Message); }
        finally
        {
            status.RestorationAttempted = true;
            try { await engine.RestoreAsync().ConfigureAwait(false); }
            catch (Exception e) { status.Errors.Add("RESTORE: " + e); }
            status.RecoveryJournalExists = File.Exists(status.RecoveryJournalPath);
            status.FinalPowerScheme = system.GetPowerScheme(); status.FinalProcessCount = system.Snapshot().Count;
            status.RestorationSucceeded = !engine.IsActive && !status.RecoveryJournalExists
                && status.FinalPowerScheme.Equals(status.OriginalPowerScheme, StringComparison.OrdinalIgnoreCase);
            status.RestoredUtc = status.CompletedUtc = DateTime.UtcNow;
            status.Result = status.RestorationSucceeded && status.Errors.Count == 0 ? (completed ? "Completed" : cancelled ? "Cancelled" : "Failed") : "Failed";
            Save(plan, status, status.Result);
        }
        return status.Result is "Completed" or "Cancelled" ? 0 : 1;

        void RequireSameGame()
        {
            var games = Metadata(system).Games;
            if (games.Count != 1 || games[0].Pid != expected.Pid || games[0].StartTicks != expected.StartTicks)
                throw new InvalidOperationException("Fortnite identity changed; no mixed-session capture will be taken.");
        }
    }
}
