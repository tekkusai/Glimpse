param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."

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
Write-Host ("Installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
Write-Host ("SHA256: {0}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath $setup).Hash)
