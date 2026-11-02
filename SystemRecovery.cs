using System.Text.Json;
using System.Text.RegularExpressions;

namespace TourneyMode;

public sealed class SystemRestoreCheckpoint
{
    public string Description { get; set; } = "T Mode safety " + Guid.NewGuid().ToString("N");
    public bool Attempted { get; set; }
    public bool Verified { get; set; }
    public long SequenceNumber { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Creates and reads back an actual Windows restore point, never a dummy receipt.</summary>
public static class SystemRecovery
{
    public static void Create(SystemRestoreCheckpoint checkpoint)
    {
        if (!checkpoint.Attempted || !Regex.IsMatch(checkpoint.Description, "\\AT Mode safety [a-f0-9]{32}\\z"))
            throw new InvalidOperationException("Journal the safety checkpoint request before creating it.");
        string script = """
            $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            if (!$admin) { throw 'Run T Mode as administrator to create the required Windows restore point.' }
            Get-Command Checkpoint-Computer,Get-ComputerRestorePoint -ErrorAction Stop | Out-Null
            """ + "\n$description = '" + checkpoint.Description + "'\n" + """
            $before = @(Get-ComputerRestorePoint -ErrorAction Stop)
            $last = ($before | Measure-Object SequenceNumber -Maximum).Maximum
            if ($null -eq $last) { $last = 0 }
            Checkpoint-Computer -Description $description -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningAction Stop
            $point = @(Get-ComputerRestorePoint -ErrorAction Stop | Where-Object { $_.Description -eq $description -and $_.SequenceNumber -gt $last } | Sort-Object SequenceNumber -Descending | Select-Object -First 1)
            if ($point.Count -ne 1) { throw 'Windows did not verify a new restore point. Enable System Protection for the Windows drive, then retry; no performance settings were applied.' }
            [pscustomobject]@{SequenceNumber=[long]$point[0].SequenceNumber;Description=$point[0].Description} | ConvertTo-Json -Compress
            """;
        var result = JsonSerializer.Deserialize<SystemRestoreCheckpoint>(TournamentResources.RunPowerShell(script, 120000), SystemAnalysis.Json)
            ?? throw new InvalidDataException("Windows returned no checkpoint identity.");
        if (result.SequenceNumber <= 0 || result.Description != checkpoint.Description) throw new InvalidDataException("The new Windows restore point could not be identified.");
        checkpoint.SequenceNumber = result.SequenceNumber; checkpoint.CreatedUtc = DateTime.UtcNow; checkpoint.Verified = true;
    }

    public static bool Verify(SystemRestoreCheckpoint checkpoint)
    {
        if (!checkpoint.Verified || checkpoint.SequenceNumber <= 0 || !Regex.IsMatch(checkpoint.Description, "\\AT Mode safety [a-f0-9]{32}\\z")) return false;
        string script = "$p = @(Get-ComputerRestorePoint -ErrorAction Stop | Where-Object { $_.SequenceNumber -eq " + checkpoint.SequenceNumber
            + " -and $_.Description -eq '" + checkpoint.Description + "' }); if ($p.Count -eq 1) { 'VERIFIED' } else { 'MISSING' }";
        return TournamentResources.RunPowerShell(script, 15000) == "VERIFIED";
    }
}
