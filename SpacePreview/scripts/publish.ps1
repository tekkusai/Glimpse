param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$project = Resolve-Path "$PSScriptRoot\..\SpacePreview.csproj"
$output = Resolve-Path "$PSScriptRoot\.."

function Reset-PublishDirectory([string]$Path) {
    $root = [System.IO.Path]::GetFullPath($output.Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $full = [System.IO.Path]::GetFullPath($Path)
    $allowedPrefix = $root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean publish directory outside project root: $full"
    }

    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $full | Out-Null
}

if ($SelfContained) {
    # Installer payload: carries the .NET runtime so the target machine needs
    # nothing installed. A folder (not single-file) because the installer is
    # the packaging; single-file would only add extraction cost at every launch.
    $publishDir = Join-Path $output "artifacts\publish\$Runtime-standalone"
    Reset-PublishDirectory $publishDir

    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        /p:PublishReadyToRun=true `
        /p:DebugType=None `
        /p:DebugSymbols=false `
        -o $publishDir
}
else {
    $publishDir = Join-Path $output "artifacts\publish\$Runtime"
    Reset-PublishDirectory $publishDir

    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained false `
        /p:PublishSingleFile=true `
        /p:PublishReadyToRun=true `
        /p:DebugType=None `
        /p:DebugSymbols=false `
        -o $publishDir
}

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Published to $publishDir"
