# Writes Glimpse.ico from the app's own mark: every frame is
# BrandMark.RenderTile from the built Glimpse.dll, so the icon and the app
# cannot drift into two marks. The sizes are every target size Microsoft
# lists for an app icon, 16 to 96 and 256, which cover the title bar, the
# taskbar and Start at the scales in its table: 100, 125, 150, 200, 250,
# 300 and 400 percent (Microsoft Learn, "Construct your Windows app's
# icon"). Frames are PNG, which Windows reads at every
# size.
# Usage:  dotnet build -c Release, then pwsh -File render-icon.ps1
#         (writes ..\Assets\Glimpse.ico; build again to embed it)
param([string]$Assembly = (Join-Path $PSScriptRoot '..\bin\Release\net10.0-windows10.0.17763.0\Glimpse.dll'))

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$mark = [Reflection.Assembly]::LoadFrom((Resolve-Path $Assembly)).GetType('SpacePreview.Preview.BrandMark', $true)
$renderTile = $mark.GetMethod('RenderTile', [Reflection.BindingFlags]'Static, NonPublic')

$sizes = 256, 96, 80, 72, 64, 60, 48, 40, 36, 32, 30, 24, 20, 16
$frames = foreach ($size in $sizes) {
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($renderTile.Invoke($null, @($size))))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    # The comma keeps each frame one byte[] instead of a stream of bytes.
    , $stream.ToArray()
}

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out

# ICONDIR
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)

# ICONDIRENTRY per frame.
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $writer.Write([byte]($size -band 0xFF))   # 0 means 256
    $writer.Write([byte]($size -band 0xFF))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}

# PowerShell can bind Write($frame) to an overload that writes one byte;
# the explicit count cannot.
foreach ($frame in $frames) {
    $writer.Write($frame, 0, $frame.Length)
}

$writer.Flush()
if ($out.Length -ne $offset) {
    throw "Icon is $($out.Length) bytes, expected $offset"
}

$resolved = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Assets\Glimpse.ico'))
[System.IO.File]::WriteAllBytes($resolved, $out.ToArray())
Write-Host "Wrote $($sizes.Count) frames, $($out.Length) bytes, to $resolved"
