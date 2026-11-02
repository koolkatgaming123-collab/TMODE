#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExecutablePath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Version,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$DownloadUrl,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PrivateKeyPath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputPath,
    [string]$Notes = '',
    [ValidateNotNullOrEmpty()][string]$Product = 'TourneyMode',
    [ValidateSet('stable')][string]$Channel = 'stable',
    [ValidateNotNullOrEmpty()][string]$MinOsVersion = '10.0.19041'
)
$ErrorActionPreference = 'Stop'

function ConvertTo-ReleaseVersion([string]$Text) {
    if ($Text -notmatch '^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}(?:\.[0-9]{1,5})?$') { throw 'A stable numeric version is required.' }
    $parsed = [version]$Text
    $parts = @($parsed.Major,$parsed.Minor,[Math]::Max(0,$parsed.Build),[Math]::Max(0,$parsed.Revision))
    if (@($parts | Where-Object { $_ -gt 65535 }).Count) { throw 'Version components must fit executable version metadata.' }
    return [version]::new($parts[0],$parts[1],$parts[2],$parts[3])
}

$exe = Get-Item -LiteralPath $ExecutablePath -ErrorAction Stop
if ($exe.PSIsContainer -or $exe.Length -le 0 -or $exe.Length -gt 300MB) { throw 'Executable size must be within 1..300 MiB.' }
if ($exe.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Do not publish an executable through a reparse point.' }
if ($Notes.Length -gt 4000) { throw 'Release notes must be at most 4000 characters.' }
$targetVersion = ConvertTo-ReleaseVersion $Version
$null = ConvertTo-ReleaseVersion $MinOsVersion
$uri = [uri]$DownloadUrl
if (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or (!$uri.IsDefaultPort -and $uri.Port -ne 443) -or $uri.UserInfo -or $uri.Fragment -or
    $uri.IdnHost -notin @('github.com','api.github.com','objects.githubusercontent.com','release-assets.githubusercontent.com','github-releases.githubusercontent.com')) {
    throw 'The release executable URL must use an allowed HTTPS GitHub origin.'
}
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName)
if ($metadata.ProductName -notin @('Tourney Mode','TourneyMode')) { throw 'The executable product is not Tourney Mode.' }
$actualVersion = ConvertTo-ReleaseVersion (($metadata.ProductVersion -split '\+')[0])
if ($actualVersion -ne $targetVersion) { throw 'The release version differs from executable product metadata.' }
$stream = [IO.File]::OpenRead($exe.FullName)
$reader = [IO.BinaryReader]::new($stream)
try {
    if ($stream.Length -lt 96 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'A Windows PE executable is required.' }
    $stream.Position = 0x3C
    $pe = $reader.ReadInt32()
    if ($pe -lt 64 -or $pe -gt 1MB -or $pe -gt ($stream.Length - 26)) { throw 'Invalid PE header.' }
    $stream.Position = $pe
    if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Only Windows x64 executables may be published.' }
    $stream.Position = $pe + 24
    if ($reader.ReadUInt16() -ne 0x20B) { throw 'Only PE32+ executables may be published.' }
} finally { $reader.Dispose(); $stream.Dispose() }

$payload = [ordered]@{
    schema = 1
    product = $Product
    channel = $Channel
    version = $Version
    architecture = 'win-x64'
    exeUrl = $uri.AbsoluteUri
    size = [long]$exe.Length
    sha256 = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash
    minOsVersion = $MinOsVersion
    notes = $Notes
}
$utf8 = [Text.UTF8Encoding]::new($false,$true)
$payloadBytes = $utf8.GetBytes(($payload | ConvertTo-Json -Depth 3 -Compress))
if ($payloadBytes.Length -gt 16KB) { throw 'The signed release payload exceeds 16 KiB.' }

# ImportFromPem takes ReadOnlySpan<char>. A small in-process C# wrapper keeps
# this portable across PowerShell 7 versions without exposing private material
# in command arguments, output or an intermediate source file.
if (-not ('TModeReleaseSigningV1' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;
public static class TModeReleaseSigningV1 {
    public static byte[] Sign(string pem, byte[] payload) {
        using (RSA rsa = RSA.Create()) {
            rsa.ImportFromPem(pem);
            if (rsa.KeySize < 2048 || rsa.KeySize > 8192) throw new CryptographicException("Unsupported signing key size.");
            return rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
    }
}
'@
}
$privatePem = [IO.File]::ReadAllText((Get-Item -LiteralPath $PrivateKeyPath -ErrorAction Stop).FullName)
try { $signature = [TModeReleaseSigningV1]::Sign($privatePem,$payloadBytes) }
catch { throw 'Release signing failed. Check the external RSA private key; no private material was written to the manifest.' }
finally { $privatePem = $null }
$envelope = [ordered]@{ payloadBase64 = [Convert]::ToBase64String($payloadBytes); signatureBase64 = [Convert]::ToBase64String($signature) }
$output = [IO.Path]::GetFullPath($OutputPath)
$directory = [IO.Path]::GetDirectoryName($output)
[IO.Directory]::CreateDirectory($directory) | Out-Null
[IO.File]::WriteAllText($output,($envelope | ConvertTo-Json -Depth 3),$utf8)
Write-Output ('Signed manifest created: ' + $output)
Write-Output ('Version: ' + $Version + '; executable SHA256: ' + $payload.sha256)
