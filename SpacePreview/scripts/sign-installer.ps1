#Requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$Setup,
    [Parameter(Mandatory=$true)][string]$Key
)

# Checks that the installer's name and its own version are the project's
# version, writes the installer's .sha256 sidecar and the sidecar's .sig,
# then checks the .sig the way the app will, with the public key compiled
# into it. When any of that fails, the installer, sidecar and .sig are
# deleted before the error: no copy of Glimpse installs a release whose
# .sig does not verify, or whose installer names another version, so
# nothing half-signed may be left to upload.

$ErrorActionPreference = "Stop"

$root = Resolve-Path "$PSScriptRoot\.."
$checksum = "$Setup.sha256"
$signatureFile = "$checksum.sig"
try {
    [xml]$project = Get-Content -LiteralPath (Join-Path $root "SpacePreview.csproj")
    $version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw "No <Version> in SpacePreview.csproj" }
    $name = Split-Path -Leaf $Setup
    if ($name -cne "Glimpse-Setup-$version.exe") {
        throw "The installer is named $name. Project version $version needs Glimpse-Setup-$version.exe."
    }
    # The app reads the same field and rejects any other version.
    $productVersion = "$((Get-Item -LiteralPath $Setup).VersionInfo.ProductVersion)".Trim()
    if ($productVersion -cne $version) {
        throw "The installer says it is version '$productVersion', not $version. No copy of Glimpse would install this release."
    }

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Setup).Hash
    $checksumLine = "$hash  $(Split-Path -Leaf $Setup)"
    [System.IO.File]::WriteAllText($checksum, $checksumLine + [Environment]::NewLine, [System.Text.Encoding]::ASCII)

    # The .sig is base64 of the IEEE P1363 ECDSA P-256 signature over the
    # sidecar's exact bytes: what ECDsa.SignData returns.
    $checksumBytes = [System.IO.File]::ReadAllBytes($checksum)
    $signer = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $signer.ImportFromPem([System.IO.File]::ReadAllText($Key))
        if ($signer.KeySize -ne 256) { throw "The update signing key at $Key is not a P-256 key." }
        $signatureBytes = $signer.SignData($checksumBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally {
        $signer.Dispose()
    }
    [System.IO.File]::WriteAllText($signatureFile, [Convert]::ToBase64String($signatureBytes) + [Environment]::NewLine, [System.Text.Encoding]::ASCII)

    # Checked the way the app checks it, with the public key compiled into it.
    $updater = Get-Content -Raw -LiteralPath (Join-Path $root "Services\AppUpdateService.cs")
    if ($updater -notmatch '(?s)-----BEGIN PUBLIC KEY-----.+?-----END PUBLIC KEY-----') { throw "No release public key in AppUpdateService.cs" }
    $verifier = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $verifier.ImportFromPem(($Matches[0] -replace '(?m)^\s+', ''))
        $signed = $verifier.VerifyData($checksumBytes, [Convert]::FromBase64String([System.IO.File]::ReadAllText($signatureFile).Trim()), [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally {
        $verifier.Dispose()
    }
    if (-not $signed) {
        throw "The key at $Key is not the release key: its signature does not verify with the public key in AppUpdateService.cs, so no copy of Glimpse would install this release."
    }
}
catch {
    Remove-Item -LiteralPath $Setup, $checksum, $signatureFile -Force -ErrorAction SilentlyContinue
    Write-Warning "Deleted the installer, its checksum and signature, so nothing half-signed can be uploaded."
    throw
}

Write-Host ("Installer: {0} ({1:N1} MB)" -f $Setup, ((Get-Item -LiteralPath $Setup).Length / 1MB))
Write-Host ("Checksum: {0}" -f $checksum)
Write-Host ("Signature: {0}" -f $signatureFile)
Write-Host ("SHA256: {0}" -f $hash)
