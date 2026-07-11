# Regression suite for Glimpse's interaction and rendering contracts.
# Generates most fixtures, runs the app's dev probes offscreen, asserts
# against the diagnostic log and rendered pixels inside an isolated app-data
# root. Media and full document zoom checks need local sample files.
# Run after building Release:
#   powershell -File scripts\regression.ps1
# Exit code 0 = all required tests passed.
#
# Covered regressions:
#   R1  Folder browsing via IFolderView: order, clamping, position.
#   R2  Multi-selection carried in VIEW order (shell enumerations are rotated).
#   R3  Text-preview scrollbars themed (Collapsed staging skipped Loaded styling).
#   R4  Media template: no decoy focus-steal, no page-side Space/arrow handlers
#       (host handles those keys); host toggle script works.
#   R5  Swap choreography: stage/reveal beats logged, no black flash mid-swap.
#   R6  Hardened-HTML root unlock for clipped pages.
#   R7  Zoom ladder steps match the browser's presets (needs office engine).
#   R8  Static generated HTML is visibly painted after reveal.
# Printed at the end: the MANUAL checks no probe can drive (global hook paths).

param(
    [switch]$SkipBuild,
    [switch]$AllowSkips,
    [switch]$SkipDesktopBrowse,
    [int]$ProbeTimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'bin\Release\net10.0-windows10.0.17763.0\Glimpse.exe'
$runId = '{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$work = Join-Path (Join-Path $env:TEMP 'GlimpseRegression') $runId
$dataRoot = Join-Path $work 'DataRoot'
$log = Join-Path $dataRoot 'Glimpse.log'
$settingsPath = Join-Path $dataRoot 'settings.json'
$results = [System.Collections.Generic.List[object]]::new()
$skips = [System.Collections.Generic.List[object]]::new()

Add-Type -AssemblyName System.Drawing

function Report([string]$name, [bool]$pass, [string]$detail) {
    $results.Add([pscustomobject]@{ Name = $name; Pass = $pass; Detail = $detail })
    $tag = if ($pass) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}  {2}" -f $tag, $name, $detail)
}

function Skip([string]$name, [string]$why) {
    $skips.Add([pscustomobject]@{ Name = $name; Detail = $why })
    Write-Host ("[SKIP] {0}  {1}" -f $name, $why)
    if (-not $AllowSkips) {
        Report $name $false ("skipped: {0}" -f $why)
    }
}

function LogTail([int]$lines = 40) {
    if (Test-Path $log) { Get-Content $log -Tail $lines } else { @() }
}

function LogLineCount {
    if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }
}

function LogSince([int]$startLine) {
    if (Test-Path $log) { Get-Content $log | Select-Object -Skip $startLine } else { @() }
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

function RunProbe([string[]]$probeArgs) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $exe
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.Arguments = (($probeArgs | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' ')

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw 'Probe process did not start.' }
        $pidForError = $process.Id
        if (-not $process.WaitForExit($ProbeTimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch {}
            throw "Probe pid $pidForError timed out after $ProbeTimeoutSeconds seconds: $($probeArgs -join ' ')"
        }

        if ($process.ExitCode -ne 0) {
            throw "Probe pid $pidForError exited $($process.ExitCode): $($probeArgs -join ' ')"
        }
    }
    finally {
        $process.Dispose()
    }
}

function PixelStats([string]$png, [int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    $bmp = [System.Drawing.Bitmap]::new($png)
    try {
        $min = 765; $max = 0; $sum = 0L; $n = 0; $dark = 0; $bright = 0
        for ($y = [Math]::Max(0, $y0); $y -lt [Math]::Min($bmp.Height, $y1); $y += 4) {
            for ($x = [Math]::Max(0, $x0); $x -lt [Math]::Min($bmp.Width, $x1); $x += 2) {
                $p = $bmp.GetPixel($x, $y)
                $v = [int]$p.R + [int]$p.G + [int]$p.B
                if ($v -lt $min) { $min = $v }
                if ($v -gt $max) { $max = $v }
                if ($v -lt 180) { $dark++ }
                if ($v -gt 420) { $bright++ }
                $sum += $v; $n++
            }
        }
        [pscustomobject]@{
            Min = $min
            Max = $max
            Mean = if ($n) { $sum / $n } else { 0 }
            Dark = $dark
            Bright = $bright
            Samples = $n
            Width = $bmp.Width
            Height = $bmp.Height
        }
    }
    finally { $bmp.Dispose() }
}

if (-not $SkipBuild) {
    Push-Location $repo
    try { dotnet build -c Release -nologo | Out-Null } finally { Pop-Location }
}
if (-not (Test-Path $exe)) { throw "Build Release first: $exe not found." }

New-Item -ItemType Directory -Force $work | Out-Null
New-Item -ItemType Directory -Force $dataRoot | Out-Null

# ---- Settings: force a deterministic isolated environment -----------------
$previousDataRoot = [Environment]::GetEnvironmentVariable('GLIMPSE_DATA_ROOT', 'Process')
[Environment]::SetEnvironmentVariable('GLIMPSE_DATA_ROOT', $dataRoot, 'Process')
'{"Theme":"Dark","WelcomeShown":true,"MediaVolume":0.0,"MediaMuted":true}' | Set-Content $settingsPath

try {
    # ---- Fixtures ----------------------------------------------------------
    $folder = Join-Path $work 'browse'
    New-Item -ItemType Directory -Force $folder | Out-Null
    foreach ($n in 'aaa', 'bbb', 'ccc', 'ddd', 'eee') { "file $n" | Set-Content (Join-Path $folder "$n.txt") }

    $ini = Join-Path $work 'wide.ini'
    $line = '[section] key=' + ('value;' * 60)
    (1..400 | ForEach-Object { $line }) -join "`r`n" | Set-Content $ini

    $html = Join-Path $work 'clipped.html'
    '<!doctype html><html style="overflow:hidden"><body><div style="height:5000px;background:#123">tall</div></body></html>' | Set-Content $html

    # Dense, high-contrast syntax tokens make the WebView screenshot prove
    # actual generated content is visible, not merely its dark background.
    $json = Join-Path $work 'static-visible.json'
    @'
{
  "GLIMPSE_STATIC_HTML_VISIBILITY_SENTINEL": "BRIGHT GENERATED CONTENT 0123456789",
  "status": "visible",
  "values": [101, 202, 303, 404, 505, 606, 707, 808],
  "details": {
    "alpha": "THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG",
    "beta": true,
    "gamma": false,
    "delta": "STATIC WEBVIEW TEXT MUST BE PAINTED"
  }
}
'@ | Set-Content $json -Encoding utf8

    # ---- R1 + R2: folder browsing + multi-selection view order -------------
    # Opens one real Explorer window on the fixture folder for ~4 seconds.
    if ($SkipDesktopBrowse) {
        Skip 'R1 folder browse order + clamp' 'desktop Explorer browse disabled for this run'
        Skip 'R2 multi-selection view order' 'desktop Explorer browse disabled for this run'
    }
    else {
        $browseLogStart = LogLineCount
        RunProbe @('--browse-test', $folder)
        Start-Sleep 2
        $tail = (LogSince $browseLogStart | Where-Object { $_ -match 'browse-test' }) -join "`n"
        $r1 = ($tail -match 'browse-test delta \+1 -> ccc\.txt index=2') -and
              ($tail -match 'browse-test delta -9 -> aaa\.txt index=0') -and
              ($tail -match 'browse-test delta \+2 -> ccc\.txt index=2')
        Report 'R1 folder browse order + clamp' $r1 'IFolderView SelectItem walks view order, clamps at ends'
        $r2 = $tail -match 'browse-test multi -> count=3 carried=3 order=\[aaa\.txt, ccc\.txt, eee\.txt\]'
        Report 'R2 multi-selection view order' $r2 'carried set sorted by Item(i), not shell enumeration order'
    }

    # ---- R3: text scrollbars themed after Collapsed staging ----------------
    $iniShot = Join-Path $work 'ini.png'
    RunProbe @('--probe', $ini, $iniShot, 'delay:1200')
    # Sample only the scrollbar strips (right edge + bottom edge): in dark
    # theme a native white bar means untheme regression (mean ~700).
    $probeImg = [System.Drawing.Bitmap]::new($iniShot)
    $iw = $probeImg.Width; $ih = $probeImg.Height; $probeImg.Dispose()
    $v = PixelStats $iniShot ($iw - 16) 80 ($iw - 2) ($ih - 40)
    $h = PixelStats $iniShot 20 ($ih - 16) ($iw - 20) ($ih - 2)
    $r3 = ($v.Mean -lt 300) -and ($h.Mean -lt 300)
    Report 'R3 themed text scrollbars' $r3 ("bar strips mean v={0:0} h={1:0} (native white ~700)" -f $v.Mean, $h.Mean)

    # ---- R4: media template contract (needs any local .mp4) ----------------
    $sample = Get-ChildItem "$env:USERPROFILE\Downloads" -Filter *.mp4 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $sample) {
        Skip 'R4 media key ownership' 'no .mp4 in Downloads'
    }
    else {
        $mediaShot = Join-Path $work 'media.png'
        RunProbe @('--probe', $sample.FullName, $mediaShot, 'delay:2500',
            "evaljs:(document.getElementById('media')?1:0)+'|'+(document.body.innerHTML.includes('decoy')?1:0)",
            'delay:200',
            "evaljs:(m=>{if(m){if(m.paused)m.play().catch(()=>{});else m.pause();}})(document.getElementById('media'));'toggled'")
        Start-Sleep 1
        $mediaTail = (LogTail 12) -join "`n"
        $r4a = $mediaTail -match [regex]::Escape('probe evaljs -> "1|0"')
        $r4b = $mediaTail -match [regex]::Escape('probe evaljs -> "toggled"')
        # The freshest media page must keep keyboard ownership in the host.
        $mediaHtmlDir = Join-Path $dataRoot 'MediaHtml'
        $newest = Get-ChildItem $mediaHtmlDir -Filter *.html | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $body = Get-Content $newest.FullName -Raw
        $r4c = ($body -notmatch 'decoy') -and
              ($body -notmatch "addEventListener\(['""]key") -and
              ($body -notmatch '\bonkey(?:down|up|press)\b')
        # No native media chrome or fullscreen path: every visible control is ours.
        $r4d = ($body -notmatch '<(?:audio|video)\b[^>]*\scontrols(?:\s|=|>)') -and
              ($body -notmatch 'requestFullscreen|webkitRequestFullscreen|webkitRequestFullScreen|mozRequestFullScreen|msRequestFullscreen|media-controls-fullscreen-button') -and
              ($body -match 'id="media"') -and
              ($body -match 'id="play"') -and
              ($body -match 'id="back"') -and
              ($body -match 'id="fwd"')
        Report 'R4 media key ownership' ($r4a -and $r4b -and $r4c -and $r4d) 'no decoy, no page key handlers, no native fullscreen chrome, host toggle runs'
    }

    # ---- R5: swap beats + no black flash ------------------------------------
    $swapShot = Join-Path $work 'swap_mid.png'
    $swapFinal = Join-Path $work 'swap_final.png'
    RunProbe @('--probe', $ini, $swapFinal, 'delay:1200', $html, 'delay:150', "shot:$swapShot", 'delay:1500')
    Start-Sleep 1
    $swapTail = (LogTail 12) -join "`n"
    $r5a = ($swapTail -match 'stage \+\d+ms')
    $mid = PixelStats $swapShot 10 60 20000 20000
    $r5b = $mid.Min -gt 20   # pure-black hwnd flash would sample ~0
    Report 'R5 swap beats + no black flash' ($r5a -and $r5b) ("stage logged; mid-swap min rgb-sum {0}" -f $mid.Min)

    # ---- R6: hardened-HTML clipped-root unlock ------------------------------
    $htmlShot = Join-Path $work 'html.png'
    RunProbe @('--probe', $html, $htmlShot, 'delay:1800',
        "evaljs:getComputedStyle(document.documentElement).overflowY")
    Start-Sleep 1
    $r6 = ((LogTail 6) -join "`n") -match [regex]::Escape('probe evaljs -> "auto"')
    Report 'R6 clipped html root unlocked' $r6 'overflow:hidden root becomes auto (content reachable)'

    # ---- R7: zoom ladder (needs office engine + a sheet) --------------------
    $sheet = Get-ChildItem "$env:USERPROFILE\Downloads" -Include *.xls, *.xlsx -Recurse -Depth 0 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $sheet) {
        Skip 'R7 zoom ladder' 'no sheet in Downloads'
    }
    else {
        RunProbe @('--probe', $sheet.FullName, (Join-Path $work 'zoom.png'), 'delay:4500', 'zoomin', 'zoomin', 'zoomin')
        Start-Sleep 1
        $zoomTail = (LogTail 10) -join "`n"
        $r7 = ($zoomTail -match 'zoom adjust -> 1\.1\b') -and ($zoomTail -match 'zoom adjust -> 1\.25') -and ($zoomTail -match 'zoom adjust -> 1\.5')
        Report 'R7 zoom ladder presets' $r7 '100 -> 110 -> 125 -> 150, same stops as Ctrl+wheel'
    }

    # ---- R8: static generated HTML must be visibly painted -----------------
    # Capture through CDP: RenderTargetBitmap cannot see the browser HWND.
    # The CDP image is the WebView viewport only, so the app header is already
    # excluded. A blank/transparent root is nearly uniform; a real dark JSON
    # page has a broad luminance range even when its theme has no very bright
    # pixels.
    $jsonWindowShot = Join-Path $work 'json_window.png'
    $jsonWebShot = Join-Path $work 'json_web.png'
    Remove-Item -LiteralPath $jsonWindowShot, $jsonWebShot -Force -ErrorAction SilentlyContinue
    RunProbe @('--probe', $json, $jsonWindowShot, "webshot:$jsonWebShot")
    Start-Sleep 1
    if (-not (Test-Path $jsonWebShot)) {
        Report 'R8 static generated HTML visible' $false 'WebView screenshot was not produced'
    }
    else {
        $jsonPixels = PixelStats $jsonWebShot 8 8 20000 20000
        $range = $jsonPixels.Max - $jsonPixels.Min
        $r8 = ($range -gt 220) -and ($jsonPixels.Max -gt 350) -and ($jsonPixels.Dark -ge 100)
        Report 'R8 static generated HTML visible' $r8 (
            "content range={0} max={1} dark={2} of {3} sampled pixels" -f
            $range, $jsonPixels.Max, $jsonPixels.Dark, $jsonPixels.Samples)
    }
}
finally {
    [Environment]::SetEnvironmentVariable('GLIMPSE_DATA_ROOT', $previousDataRoot, 'Process')
}

$failed = @($results | Where-Object { -not $_.Pass })
Write-Host ''
Write-Host ("{0} passed, {1} failed, {2} skipped." -f @($results | Where-Object Pass).Count, $failed.Count, $skips.Count)
Write-Host ("Run artifacts: {0}" -f $work)

Write-Host ''
Write-Host 'MANUAL checks (global hook + foreground state; no probe can drive these):'
Write-Host ' - Left/right browse always, even on a clicked-in video (seek = scrubber only).'
Write-Host ' - After clicking INTO a video: up/down ride volume, Space toggles play/pause.'
Write-Host ' - A PINNED video: left/right seek, up/down volume, Space toggles.'
Write-Host ' - Video controls have no 3-dots menu and no native fullscreen button.'
Write-Host ' - Multi-select N files + Space: arrows/chevrons cycle only those, Explorer selection intact.'
Write-Host ' - Esc order in PDFs: find bar first, preview second.'

$exitCode = if ($failed.Count) { 1 } else { 0 }
exit $exitCode

