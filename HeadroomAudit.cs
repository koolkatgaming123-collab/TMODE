using System.Diagnostics;
using System.Text.Json;

namespace TourneyMode;

public static class HeadroomAudit
{
    public static void Write(string output)
    {
        var snapshot = ProcessCatalog.Snapshot();
        var engine = new TourneyEngine();
        var options = engine.LoadOptions();
        var previous = ProcessPolicy.Candidates(snapshot, fortniteOnly: true, protectedPaths: options.ProtectedAppPaths);
        var expanded = ProcessPolicy.Candidates(snapshot, fortniteOnly: true, maximumHeadroom: true, protectedPaths: options.ProtectedAppPaths);
        var previousIds = previous.SelectMany(app => app.Processes).Select(process => process.Id).ToHashSet();
        var expandedIds = expanded.SelectMany(app => app.Processes).Select(process => process.Id).ToHashSet();
        var excluded = expanded.Where(app => options.ExcludedApps.Contains(app.Id)).ToList();
        var running = Process.GetProcesses();
        int total = running.Length;
        var groups = running.GroupBy(process => process.ProcessName).Select(group => new { Name = group.Key, Count = group.Count() }).OrderByDescending(group => group.Count).ToList();
        foreach (var process in running) process.Dispose();
        List<ServiceBackup> services = [];
        List<ScheduledTaskBackup> tasks = [];
        var errors = new List<string>();
        try { services = TournamentResources.CaptureOptionalServices(true); } catch (Exception error) { errors.Add("Service audit: " + error.Message); }
        try { tasks = HeadroomTasks.Capture(); } catch (Exception error) { errors.Add("Task audit: " + error.Message); }
        var report = new
        {
            CapturedUtc = DateTime.UtcNow, Active = engine.IsActive, ReadOnly = true,
            TotalProcesses = total, InspectableProcesses = snapshot.Count,
            PreviousProfileCandidateProcesses = previousIds.Count, MaximumHeadroomCandidateProcesses = expandedIds.Count,
            AdditionalReviewedProcesses = expandedIds.Except(previousIds).Count(),
            ExistingExcludedGroups = excluded.Select(app => new { app.Id, app.Name, Processes = app.Processes.Count }),
            Important = "Candidate counts are a preview, not a measured post-cleanup result. Existing exclusions remain in force until changed. No processes, services, tasks or devices were modified. Device/driver disabling and service-host regrouping are not performed.",
            Apps = expanded, Services = services, Tasks = tasks, ProcessGroups = groups,
            Remaining = snapshot.Where(process => !expandedIds.Contains(process.Id)).Select(process => new { process.Id, process.Name, process.Path, process.SessionId }),
            Errors = errors
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
