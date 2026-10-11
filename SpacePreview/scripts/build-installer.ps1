param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# Signing imports a PEM key, which the .NET under Windows PowerShell 5.1 cannot.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    & pwsh -NoProfile -File $PSCommandPath -Configuration $Configuration
    exit $LASTEXITCODE
}

$root = Resolve-Path "$PSScriptRoot\.."

# Every copy of Glimpse skips a release its key did not sign, so a build
# without the key stops here, before the long part.
$signingKey = if ($env:GLIMPSE_UPDATE_KEY) { $env:GLIMPSE_UPDATE_KEY } else { Join-Path $env:LOCALAPPDATA "GlimpseRelease\update-signing-key.pem" }
if (-not (Test-Path -LiteralPath $signingKey -PathType Leaf)) {
    throw "No update signing key at $signingKey. Restore the release key there from its backup, or set GLIMPSE_UPDATE_KEY to its path. See docs/RELEASE.md."
}

# Version comes from the project file so the installer can never disagree with the exe.
[xml]$project = Get-Content (Join-Path $root "SpacePreview.csproj")
$version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in SpacePreview.csproj" }

& "$PSScriptRoot\publish.ps1" -Configuration $Configuration -SelfContained

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }

function Assert-SignedBy {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Publisher
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notlike "*$Publisher*") {
        throw "Signature check failed for $Path. Expected publisher: $Publisher. Status: $($signature.Status)"
    }
}

$bootstrapper = Join-Path $root "installer\assets\MicrosoftEdgeWebView2Setup.exe"
if (-not (Test-Path $bootstrapper)) {
    New-Item -ItemType Directory -Force (Split-Path $bootstrapper) | Out-Null
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $bootstrapper
}
Assert-SignedBy -Path $bootstrapper -Publisher "Microsoft Corporation"

& $iscc "/DMyAppVersion=$version" (Join-Path $root "installer\Glimpse.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Join-Path $root "artifacts\installer\Glimpse-Setup-$version.exe"
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setup).Hash
$checksum = "$setup.sha256"
$checksumLine = "$hash  $(Split-Path -Leaf $setup)"
[System.IO.File]::WriteAllText($checksum, $checksumLine + [Environment]::NewLine, [System.Text.Encoding]::ASCII)

# The .sig is base64 of the IEEE P1363 ECDSA P-256 signature over the
# sidecar's exact bytes: what ECDsa.SignData returns.
$checksumBytes = [System.IO.File]::ReadAllBytes($checksum)
$signer = [System.Security.Cryptography.ECDsa]::Create()
try {
    $signer.ImportFromPem([System.IO.File]::ReadAllText($signingKey))
    if ($signer.KeySize -ne 256) { throw "The update signing key at $signingKey is not a P-256 key." }
    $signatureBytes = $signer.SignData($checksumBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
}
finally {
    $signer.Dispose()
}
$signatureFile = "$checksum.sig"
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
    throw "The key at $signingKey is not the release key: its signature does not verify with the public key in AppUpdateService.cs, so every copy of Glimpse would skip this release."
}

Write-Host ("Installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
Write-Host ("Checksum: {0}" -f $checksum)
Write-Host ("Signature: {0}" -f $signatureFile)
Write-Host ("SHA256: {0}" -f $hash)
