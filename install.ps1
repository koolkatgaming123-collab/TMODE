param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Tourney Mode'))
$ErrorActionPreference = 'Stop'
$sourceExe = Join-Path $PSScriptRoot 'Tourney Mode.exe'
if (!(Test-Path -LiteralPath $sourceExe)) { $sourceExe = Join-Path $PSScriptRoot 'dist\Tourney Mode.exe' }
if (!(Test-Path -LiteralPath $sourceExe)) { $sourceExe = Join-Path $PSScriptRoot 'TMODE.exe' }
if (!(Test-Path -LiteralPath $sourceExe)) { throw 'Extract the portable package, or build the source with build.ps1 first.' }
if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'TourneyMode\session.json')) { throw 'Restore the active Tourney Mode session before updating.' }
if (Get-Process -Name 'Tourney Mode','TMODE' -ErrorAction SilentlyContinue) { throw 'Close Tourney Mode before updating.' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$exe = Join-Path $Destination 'Tourney Mode.exe'
if ([IO.Path]::GetFullPath($sourceExe) -ne [IO.Path]::GetFullPath($exe)) {
    $candidate = Join-Path $Destination 'Tourney Mode.new.exe'
    Copy-Item -LiteralPath $sourceExe -Destination $candidate -Force
    if ((Get-FileHash -LiteralPath $sourceExe).Hash -ne (Get-FileHash -LiteralPath $candidate).Hash) { throw 'Copied executable failed verification.' }
    Move-Item -LiteralPath $candidate -Destination $exe -Force
}
$readme = Join-Path $PSScriptRoot 'README.txt'
if ((Test-Path -LiteralPath $readme) -and [IO.Path]::GetFullPath($readme) -ne [IO.Path]::GetFullPath((Join-Path $Destination 'README.txt'))) {
    Copy-Item -LiteralPath $readme -Destination (Join-Path $Destination 'README.txt') -Force
}
$validation = Join-Path $PSScriptRoot 'VALIDATION.txt'
if ((Test-Path -LiteralPath $validation) -and [IO.Path]::GetFullPath($validation) -ne [IO.Path]::GetFullPath((Join-Path $Destination 'VALIDATION.txt'))) {
    Copy-Item -LiteralPath $validation -Destination (Join-Path $Destination 'VALIDATION.txt') -Force
}
$tools = Join-Path (Split-Path -Parent $sourceExe) 'Tools'
$installedTools = Join-Path $Destination 'Tools'
if ((Test-Path -LiteralPath $tools) -and [IO.Path]::GetFullPath($tools) -ne [IO.Path]::GetFullPath($installedTools)) {
    New-Item -ItemType Directory -Path $installedTools -Force | Out-Null
    Get-ChildItem -LiteralPath $tools -File | Copy-Item -Destination $installedTools -Force
}
$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Tourney Mode'
New-Item -ItemType Directory -Path $menu -Force | Out-Null
foreach ($linkPath in @((Join-Path $desktop 'Tourney Mode.lnk'),(Join-Path $menu 'Tourney Mode.lnk'))) {
    $shortcut = $shell.CreateShortcut($linkPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $Destination
    $shortcut.Description = 'Per-PC Fortnite cleanup with protected connections and settings recovery'
    $shortcut.Save()
}
$restore = $shell.CreateShortcut((Join-Path $menu 'Restore Tourney Mode.lnk'))
$restore.TargetPath = $exe
$restore.Arguments = '--restore'
$restore.WorkingDirectory = $Destination
$restore.Description = 'Restore Windows settings, services and tasks; closed apps stay closed'
$restore.Save()
Get-Item -LiteralPath $exe | Select-Object FullName,Length,LastWriteTime
