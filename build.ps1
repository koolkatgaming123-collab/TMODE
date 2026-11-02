param([string]$Dotnet = "", [string]$OutputDirectory = "dist")
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (!$Dotnet) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.sdk10\dotnet.exe')) { $Dotnet = Join-Path $PSScriptRoot '.sdk10\dotnet.exe' }
    else { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
}
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $env:TEMP 'tm-nuget10'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $Dotnet publish '.\TourneyMode.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None --source 'https://api.nuget.org/v3/index.json' -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
foreach ($name in @('README.txt','install.ps1','VALIDATION.txt','THIRD-PARTY-NOTICES.txt')) {
    if (Test-Path -LiteralPath $name) { Copy-Item -LiteralPath $name -Destination (Join-Path $OutputDirectory $name) -Force }
}
Write-Output (Join-Path $OutputDirectory 'Tourney Mode.exe')
