# Off-screen UI stress and runtime audit for Glimpse.
#
# Unlike the fast unit suite, this launches the real Release WPF window and
# checks arranged geometry, button hit targets, rendered pixels, async overlay
# ownership, rapid A->B->A cancellation, resize behavior, and invalid-content
# recovery. It never installs hooks or drives the user's desktop.
#
#   & .\scripts\ui-stress.ps1
#   & .\scripts\ui-stress.ps1 -SkipBuild -StressRounds 3

param(
    [switch]$SkipBuild,
    [ValidateRange(1, 12)][int]$StressRounds = 2,
    [ValidateRange(10, 120)][int]$ProbeTimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'bin\Release\net10.0-windows10.0.17763.0\Glimpse.exe'
$runId = '{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$work = Join-Path (Join-Path $env:TEMP 'GlimpseUiStress') $runId
$dataRoot = Join-Path $work 'DataRoot'
$log = Join-Path $dataRoot 'Glimpse.log'
$settingsPath = Join-Path $dataRoot 'settings.json'
$summaryPath = Join-Path $work 'results.json'
$results = [System.Collections.Generic.List[object]]::new()
$runCompleted = $false

Add-Type -AssemblyName System.Drawing

function Save-Results {
    $failed = @($results | Where-Object { -not $_.Pass })
    $payload = [ordered]@{
        Complete = $runCompleted
        Passed = @($results | Where-Object Pass).Count
        Failed = $failed.Count
        Results = $results.ToArray()
    }
    $payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
}

function Report([string]$name, [bool]$pass, [string]$detail) {
    $results.Add([pscustomobject]@{ Name = $name; Pass = $pass; Detail = $detail })
    Save-Results
    $tag = if ($pass) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}  {2}" -f $tag, $name, $detail)
}

function Write-Settings([string]$theme) {
    (@{ Theme = $theme; WelcomeShown = $true; MediaVolume = 0.0; MediaMuted = $true } | ConvertTo-Json -Compress) |
        Set-Content -LiteralPath $settingsPath -Encoding utf8
}

function ConvertTo-ProcessArgument([string]$value) {
    if ($value.Length -gt 0 -and $value -notmatch '[\s"]') { return $value }
    $builder = [System.Text.StringBuilder]::new('"')
    $slashes = 0
    foreach ($character in $value.ToCharArray()) {
        if ($character -eq '\') {
            $slashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * ($slashes * 2 + 1)))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes) {
            [void]$builder.Append(('\' * $slashes))
            $slashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($slashes) { [void]$builder.Append(('\' * ($slashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Run-Probe([string[]]$arguments) {
    $started = [DateTime]::UtcNow
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $exe
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.Arguments = (($arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' ')

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw 'Probe process did not start.' }
        $probePid = $process.Id
        if (-not $process.WaitForExit($ProbeTimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch {}
            throw "Probe pid $probePid timed out after $ProbeTimeoutSeconds seconds."
        }

        if ($process.ExitCode -ne 0) {
            $tail = if (Test-Path $log) { (Get-Content -LiteralPath $log -Tail 15) -join "`n" } else { '<no log>' }
            throw "Probe pid $probePid exited $($process.ExitCode).`n$tail"
        }

        if ($arguments.Count -ge 3 -and $arguments[0] -eq '--probe') {
            $capture = $arguments[2]
            if (-not (Test-Path -LiteralPath $capture)) {
                throw "Probe pid $probePid exited successfully without producing $capture."
            }

            if ((Get-Item -LiteralPath $capture).LastWriteTimeUtc -lt $started.AddSeconds(-1)) {
                throw "Probe pid $probePid produced a stale capture: $capture."
            }
        }
    }
    finally {
        $process.Dispose()
    }
}

function Read-ProbeJson([string]$prefix, [string]$label = '') {
    $lines = @(Get-Content -LiteralPath $log | Where-Object { $_ -like "*$prefix*" })
    if ($label) {
        $lines = @($lines | Where-Object { $_ -like "*$label*" })
    }
    if ($lines.Count -eq 0) { throw "No '$prefix' record$(if ($label) { " for '$label'" }) in $log." }
    $line = $lines[-1]
    $index = $line.IndexOf($prefix, [StringComparison]::Ordinal)
    return ($line.Substring($index + $prefix.Length).Trim() | ConvertFrom-Json)
}

function Pixel-Stats([string]$png) {
    $bitmap = [System.Drawing.Bitmap]::new($png)
    try {
        $min = 765
        $max = 0
        $dark = 0
        $bright = 0
        $samples = 0
        for ($y = 0; $y -lt $bitmap.Height; $y += 5) {
            for ($x = 0; $x -lt $bitmap.Width; $x += 4) {
                $pixel = $bitmap.GetPixel($x, $y)
                $value = [int]$pixel.R + [int]$pixel.G + [int]$pixel.B
                $min = [Math]::Min($min, $value)
                $max = [Math]::Max($max, $value)
                if ($value -lt 180) { $dark++ }
                if ($value -gt 570) { $bright++ }
                $samples++
            }
        }
        return [pscustomobject]@{ Min = $min; Max = $max; Range = $max - $min; Dark = $dark; Bright = $bright; Samples = $samples }
    }
    finally {
        $bitmap.Dispose()
    }
}

if (-not $SkipBuild) {
    Push-Location $repo
    try { dotnet build -c Release -nologo | Out-Null } finally { Pop-Location }
}
if (-not (Test-Path -LiteralPath $exe)) { throw "Release executable not found: $exe" }

New-Item -ItemType Directory -Force -Path $work, $dataRoot | Out-Null
$previousDataRoot = [Environment]::GetEnvironmentVariable('GLIMPSE_DATA_ROOT', 'Process')
[Environment]::SetEnvironmentVariable('GLIMPSE_DATA_ROOT', $dataRoot, 'Process')

try {
    # Deterministic edge-case fixtures.
    $unicodePrefix = -join (@(
        0x975E, 0x5E38, 0x306B, 0x9577, 0x3044, 0x5F,
        0x0645, 0x0631, 0x062D, 0x0628, 0x0627, 0x5F,
        0x041F, 0x0440, 0x0438, 0x0432, 0x0435, 0x0442, 0x5F
    ) | ForEach-Object { [char]$_ })
    $longName = $unicodePrefix + ('segment-' * 12) + '.txt'
    if ($longName.Length -gt 170) { $longName = $longName.Substring(0, 166) + '.txt' }
    $longText = Join-Path $work $longName
    ((1..120 | ForEach-Object { "Line $_ : " + ('wide text ' * 24) }) -join "`r`n") | Set-Content -LiteralPath $longText -Encoding utf8

    $html = Join-Path $work 'clipped-scriptless.html'
    '<!doctype html><html style="overflow:hidden"><body><main style="height:5000px;background:#f3f4f6;color:#111"><h1>UI STRESS HTML</h1><a href="#bottom">reachable</a></main></body></html>' |
        Set-Content -LiteralPath $html -Encoding utf8

    $json = Join-Path $work 'final-visible.json'
    (@{ sentinel = 'GLIMPSE_UI_STRESS_FINAL_0123456789'; values = @(1..80); nested = @{ status = 'painted'; enabled = $true } } | ConvertTo-Json -Depth 4) |
        Set-Content -LiteralPath $json -Encoding utf8

    $wideSvg = Join-Path $work 'extreme-wide.svg'
    '<svg xmlns="http://www.w3.org/2000/svg" width="2048" height="64"><rect width="2048" height="64" fill="#154c79"/><text x="16" y="46" font-size="32" fill="white">WIDE</text></svg>' |
        Set-Content -LiteralPath $wideSvg -Encoding utf8
    $tallSvg = Join-Path $work 'extreme-tall.svg'
    '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="2048"><rect width="64" height="2048" fill="#7a1734"/><text x="8" y="46" font-size="18" fill="white">TALL</text></svg>' |
        Set-Content -LiteralPath $tallSvg -Encoding utf8

    $corruptImage = Join-Path $work 'truncated.png'
    'This fixture has an image extension but intentionally contains plain text.' | Set-Content -LiteralPath $corruptImage -Encoding ascii
    $unknown = Join-Path $work 'unknown-content.bin'
    'Unknown file type fixture.' | Set-Content -LiteralPath $unknown -Encoding ascii

    $largeFolder = Join-Path $work 'folder-64-items'
    New-Item -ItemType Directory -Force -Path $largeFolder | Out-Null
    1..64 | ForEach-Object { "fixture $_" | Set-Content -LiteralPath (Join-Path $largeFolder ("item-{0:D4}.txt" -f $_)) }

    Write-Settings 'Dark'

    # A1: the most crowded header at the functional floor.
    $minShot = Join-Path $work 'min-dark.png'
    Run-Probe @('--probe', $longText, $minShot, 'size:640,480', 'multiselect:4', 'audit:min-dark', 'buttonaudit')
    $minAudit = Read-ProbeJson 'probe audit ' 'min-dark'
    $buttons = Read-ProbeJson 'probe buttons '
    Report 'A1 minimum header lanes' (
        $minAudit.LanesInsideHeader -and $minAudit.LanesDoNotOverlap -and $minAudit.FileNameWidth -ge 40
    ) ("filename={0:0}px inside={1} overlapFree={2}" -f $minAudit.FileNameWidth, $minAudit.LanesInsideHeader, $minAudit.LanesDoNotOverlap)
    Report 'A2 button names + hit targets' (
        $minAudit.AllVisibleButtonsNamed -and $minAudit.AllVisibleButtonCentersHit
    ) ("named={0} centersHit={1}" -f $minAudit.AllVisibleButtonsNamed, $minAudit.AllVisibleButtonCentersHit)
    Report 'A3 safe button wiring' (
        $buttons.Previous -eq 1 -and $buttons.Next -eq 1 -and $buttons.Dismissed -eq 1 -and $buttons.Closed -eq 1 -and
        $buttons.GridOpened -and $buttons.GridClosed -and $buttons.Pinned -and $buttons.Unpinned
    ) ("prev={0} next={1} dismiss={2} close={3} grid={4}/{5} pin={6}/{7}" -f
        $buttons.Previous, $buttons.Next, $buttons.Dismissed, $buttons.Closed,
        $buttons.GridOpened, $buttons.GridClosed, $buttons.Pinned, $buttons.Unpinned)

    # A4: light theme and actual WPF paint, not only object state.
    Write-Settings 'Light'
    $lightShot = Join-Path $work 'min-light.png'
    Run-Probe @('--probe', $longText, $lightShot, 'size:640,480', 'audit:min-light')
    $lightAudit = Read-ProbeJson 'probe audit ' 'min-light'
    $lightPixels = Pixel-Stats $lightShot
    Report 'A4 light-theme layout + paint' (
        $lightAudit.LanesInsideHeader -and $lightAudit.AllVisibleButtonCentersHit -and $lightPixels.Range -gt 180 -and $lightPixels.Dark -gt 20 -and $lightPixels.Bright -gt 20
    ) ("range={0} dark={1} bright={2}" -f $lightPixels.Range, $lightPixels.Dark, $lightPixels.Bright)

    Write-Settings 'Dark'

    # A5/A6: outgoing content must be blocked immediately; an old loading
    # fade must not collapse a newer load's overlay.
    $raceShot = Join-Path $work 'overlay-race.png'
    Run-Probe @('--probe', $longText, $raceShot, "show:$html", 'audit:swap-immediate', 'overlayrace', 'delay:600', 'audit:swap-settled')
    $swapAudit = Read-ProbeJson 'probe audit ' 'swap-immediate'
    $overlayLine = @(Get-Content -LiteralPath $log | Where-Object { $_ -like '*probe overlay-race*' })[-1]
    Report 'A5 outgoing content cannot receive swap clicks' (
        $swapAudit.Scrim -eq 'Visible' -and $swapAudit.ScrimBlocksInput
    ) ("scrim={0} blocks={1}" -f $swapAudit.Scrim, $swapAudit.ScrimBlocksInput)
    Report 'A6 loading overlay generation ownership' ($overlayLine -like '*pass=True*') $overlayLine

    # A7: rapid, mixed-lane A->B->A switching. The final JSON must own the
    # header, content, and paint after every earlier async task has completed.
    $rapidShot = Join-Path $work 'rapid-final.png'
    $webShot = Join-Path $work 'rapid-final-web.png'
    $rapidArguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @('--probe', $longText, $rapidShot)) { $rapidArguments.Add($argument) }
    for ($round = 0; $round -lt $StressRounds; $round++) {
        foreach ($fixture in @($html, $wideSvg, $corruptImage, $tallSvg, $longText, $json)) {
            $rapidArguments.Add("show:$fixture")
        }
    }
    foreach ($argument in @('delay:2200', 'audit:rapid-final', "webshot:$webShot")) { $rapidArguments.Add($argument) }
    Run-Probe $rapidArguments.ToArray()
    $rapidAudit = Read-ProbeJson 'probe audit ' 'rapid-final'
    $rapidPixels = if (Test-Path -LiteralPath $webShot) { Pixel-Stats $webShot } else { $null }
    $rapidPainted = $null -ne $rapidPixels -and $rapidPixels.Range -gt 180 -and $rapidPixels.Dark -gt 20 -and $rapidPixels.Max -gt 350
    Report 'A7 rapid mixed-content final ownership' (
        $rapidAudit.CurrentName -eq (Split-Path -Leaf $json) -and $rapidAudit.LoadFinished -and
        $rapidAudit.Loading -eq 'Collapsed' -and $rapidAudit.Scrim -eq 'Collapsed' -and $rapidPainted
    ) ("name={0} finished={1} loading={2} scrim={3} painted={4}" -f
        $rapidAudit.CurrentName, $rapidAudit.LoadFinished, $rapidAudit.Loading, $rapidAudit.Scrim, $rapidPainted)

    # A8: corrupt/unsupported/folder lanes must recover to a valid preview.
    $recoveryPass = $true
    $recoveryDetails = [System.Collections.Generic.List[string]]::new()
    foreach ($fixture in @($corruptImage, $unknown, $largeFolder)) {
        $name = [System.IO.Path]::GetFileName($fixture)
        $shot = Join-Path $work ("recover-{0}.png" -f ($name -replace '[^A-Za-z0-9.-]', '_'))
        Run-Probe @('--probe', $fixture, $shot, "show:$longText", 'delay:900', "audit:recover-$name")
        $audit = Read-ProbeJson 'probe audit ' "recover-$name"
        $ok = $audit.CurrentName -eq (Split-Path -Leaf $longText) -and $audit.LoadFinished -and
            $audit.Loading -eq 'Collapsed' -and $audit.Scrim -eq 'Collapsed' -and $null -ne $audit.ContentType
        $recoveryPass = $recoveryPass -and $ok
        $recoveryDetails.Add("$name=$ok")
    }
    Report 'A8 invalid and folder content recovery' $recoveryPass ($recoveryDetails -join ', ')

    # A9: extreme aspect ratios and repeated resizing must keep the header
    # usable and the stage finite. The files themselves remain tiny SVG text.
    $aspectShot = Join-Path $work 'aspect-resize.png'
    Run-Probe @('--probe', $wideSvg, $aspectShot, 'audit:wide-aspect', "show:$tallSvg", 'delay:900', 'audit:tall-aspect', 'size:640,480', 'audit:tall-min')
    $wideAudit = Read-ProbeJson 'probe audit ' 'wide-aspect'
    $tallAudit = Read-ProbeJson 'probe audit ' 'tall-aspect'
    $tallMinAudit = Read-ProbeJson 'probe audit ' 'tall-min'
    $aspectPass = @($wideAudit, $tallAudit, $tallMinAudit) | Where-Object {
        -not ($_.Window.Width -gt 0 -and $_.Window.Width -le 3840 -and $_.Window.Height -gt 0 -and $_.Window.Height -le 2160 -and
            $_.LanesInsideHeader -and $_.LanesDoNotOverlap -and $_.AllVisibleButtonCentersHit)
    }
    Report 'A9 aspect-ratio and resize stability' ($aspectPass.Count -eq 0) ("wide={0:0}x{1:0} tall={2:0}x{3:0} min={4:0}x{5:0}" -f
        $wideAudit.Window.Width, $wideAudit.Window.Height, $tallAudit.Window.Width, $tallAudit.Window.Height,
        $tallMinAudit.Window.Width, $tallMinAudit.Window.Height)

    $fatalLines = @(Get-Content -LiteralPath $log | Where-Object { $_ -match 'FATAL|Unhandled exception|OutOfMemory' })
    $fatalDetail = if ($fatalLines.Count) { $fatalLines[-1] } else { 'no fatal lines' }
    Report 'A10 no fatal runtime failures' ($fatalLines.Count -eq 0) $fatalDetail
    $runCompleted = $true
}
finally {
    [Environment]::SetEnvironmentVariable('GLIMPSE_DATA_ROOT', $previousDataRoot, 'Process')
    try { Save-Results } catch { Write-Warning "Could not save UI audit results: $_" }
}

$failed = @($results | Where-Object { -not $_.Pass })
Write-Host ''
Write-Host ("{0} passed, {1} failed. Artifacts: {2}" -f @($results | Where-Object Pass).Count, $failed.Count, $work)
if ($failed.Count) { exit 1 }
exit 0

