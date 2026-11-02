#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ManifestPath,
    [string]$ConfigurationPath = (Join-Path $PSScriptRoot '..\AppUpdateConfiguration.cs')
)
$ErrorActionPreference = 'Stop'
$configuration = [IO.File]::ReadAllText((Get-Item -LiteralPath $ConfigurationPath).FullName)
$match = [regex]::Match($configuration, '(?s)-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----')
if (!$match.Success) { throw 'No pinned public verification key was found.' }
$envelope = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$payloadBytes = [Convert]::FromBase64String($envelope.payloadBase64)
$signature = [Convert]::FromBase64String($envelope.signatureBase64)
if (-not ('TModeReleaseVerificationV1' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;
public static class TModeReleaseVerificationV1 {
    public static bool Verify(string pem, byte[] payload, byte[] signature) {
        using (RSA rsa = RSA.Create()) {
            rsa.ImportFromPem(pem);
            return rsa.KeySize >= 2048 && rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
    }
}
'@
}
if (![TModeReleaseVerificationV1]::Verify($match.Value, $payloadBytes, $signature)) {
    throw 'The update signature does not match the verification key compiled into the app. Do not publish.'
}
$payload = [Text.UTF8Encoding]::new($false,$true).GetString($payloadBytes) | ConvertFrom-Json
$exe = Get-Item -LiteralPath $ExecutablePath
$actualVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName).ProductVersion -split '\+')[0]
if ($payload.schema -ne 1 -or $payload.product -ne 'TourneyMode' -or $payload.channel -ne 'stable' -or $payload.architecture -ne 'win-x64' -or
    [version]$payload.version -ne [version]$actualVersion -or $payload.size -ne $exe.Length -or
    $payload.sha256 -ne (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash) {
    throw 'The signed update payload does not describe the selected release executable.'
}
Write-Output ('Pinned signature and executable verified: ' + $payload.version + '; SHA256 ' + $payload.sha256)
