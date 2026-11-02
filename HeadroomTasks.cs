using System.Text.Json;
using System.Text.RegularExpressions;

namespace TourneyMode;

public sealed class ScheduledTaskBackup
{
    public string TaskPath { get; set; } = "";
    public string TaskName { get; set; } = "";
    public string ActionHash { get; set; } = "";
    public bool DisableAttempted { get; set; }
    public bool Restored { get; set; }
}

public static class HeadroomTasks
{
    private static readonly (string Path, string Name)[] Tasks =
    [
        ("\\GoogleUserPEH\\", "RunPlatformExperienceHelper_Daily"), ("\\GoogleUserPEH\\", "RunPlatformExperienceHelper_Metrics"),
        ("\\Microsoft\\XblGameSave\\", "XblGameSaveTask"),
        ("\\Microsoft\\Windows\\Application Experience\\", "Microsoft Compatibility Appraiser"),
        ("\\Microsoft\\Windows\\Application Experience\\", "ProgramDataUpdater"),
        ("\\Microsoft\\Windows\\Customer Experience Improvement Program\\", "Consolidator"),
        ("\\Microsoft\\Windows\\Customer Experience Improvement Program\\", "UsbCeip"),
        ("\\Microsoft\\Windows\\Feedback\\Siuf\\", "DmClient"), ("\\Microsoft\\Windows\\Feedback\\Siuf\\", "DmClientOnScenarioDownload"),
        ("\\Microsoft\\Windows\\Maps\\", "MapsUpdateTask"), ("\\Microsoft\\Windows\\Maps\\", "MapsToastTask"),
        ("\\Microsoft\\Windows\\Windows Error Reporting\\", "QueueReporting")
    ];
    // Restore older sessions, but never assume another user's custom task is ours.
    private static readonly (string Path, string Name)[] LegacyTasks =
        [("\\", "Spencer-CS2-GitHub-Runner"), ("\\", "Spencer-CS2-Host-Startup")];

    public static bool IsAllowed(string taskPath, string taskName) => Tasks.Concat(LegacyTasks).Any(task => task.Path.Equals(taskPath, StringComparison.OrdinalIgnoreCase)
        && task.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase));
    internal static bool IsCaptured(string taskPath, string taskName) => Tasks.Any(task => task.Path.Equals(taskPath, StringComparison.OrdinalIgnoreCase)
        && task.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase));

    private const string HashFunction = """
        function Get-ActionHash($task) {
            [xml]$xml = Export-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName -ErrorAction Stop
            $sha = [Security.Cryptography.SHA256]::Create()
            try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($xml.Task.Actions.OuterXml)))).Replace('-','').ToLowerInvariant() }
            finally { $sha.Dispose() }
        }
        """;

    public static List<ScheduledTaskBackup> Capture()
    {
        string identities = string.Join(",", Tasks.Select(task => "'" + task.Path + task.Name + "'"));
        string script = HashFunction + "\n$allowed = @(" + identities + ")\n" + """
            $items = @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.Settings.Enabled -and $allowed -contains ($_.TaskPath + $_.TaskName) } | ForEach-Object {
                [pscustomobject]@{TaskPath=$_.TaskPath;TaskName=$_.TaskName;ActionHash=(Get-ActionHash $_);DisableAttempted=$false;Restored=$false}
            })
            ConvertTo-Json -InputObject $items -Compress
            """;
        return JsonSerializer.Deserialize<List<ScheduledTaskBackup>>(TournamentResources.RunPowerShell(script),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    public static void SetEnabled(ScheduledTaskBackup item, bool enabled)
    {
        if (!item.DisableAttempted || !IsAllowed(item.TaskPath, item.TaskName)
            || !Regex.IsMatch(item.ActionHash, @"\A[a-f0-9]{64}\z")) throw new InvalidOperationException("Task is not an owned, journaled optional task.");
        string script = HashFunction + "\n$task = Get-ScheduledTask -TaskPath '" + item.TaskPath + "' -TaskName '" + item.TaskName + "' -ErrorAction Stop\n"
            + "if ((Get-ActionHash $task) -ne '" + item.ActionHash + "') { throw 'Task actions changed externally; no task was modified.' }\n"
            + "$enabled = $" + enabled.ToString().ToLowerInvariant() + "\n" + """
            if ($task.Settings.Enabled -ne $enabled) {
                if ($enabled) { $task | Enable-ScheduledTask -ErrorAction Stop | Out-Null }
                else { $task | Disable-ScheduledTask -ErrorAction Stop | Out-Null }
            }
            $task = Get-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName -ErrorAction Stop
            if ($task.Settings.Enabled -ne $enabled) { throw 'Scheduled task state was not verified.' }
            Write-Output 'VERIFIED'
            """;
        if (TournamentResources.RunPowerShell(script) != "VERIFIED") throw new InvalidOperationException("Optional task update was not confirmed.");
    }
}
