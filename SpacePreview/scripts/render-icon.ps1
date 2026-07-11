# Renders the app icon from the brand mark geometry: a glint of light on a
# dark glass tile. Produces a multi-size .ico with PNG-compressed frames.
# Usage:  pwsh -File render-icon.ps1  (writes ..\Assets\Glimpse.ico)

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$sparkPath = 'M 0,-50 C {0},-16 16,-{0} 50,0 C 16,{0} {0},16 0,50 C -{0},16 -16,{0} -50,0 C -16,-{0} -{0},-16 0,-50 Z'

function New-IconFramePng {
    param([int]$Pixels)

    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    $s = [double]$Pixels

    # Small frames need a fatter spark or it thins into nothing.
    $pull = if ($Pixels -lt 40) { '8' } elseif ($Pixels -lt 96) { '6' } else { '5' }
    $spark = [Windows.Media.Geometry]::Parse([string]::Format($inv, $sparkPath, $pull))

    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()

    # The tile: dark glass, lit faintly from the upper left.
    $bg = New-Object Windows.Media.LinearGradientBrush
    $bg.StartPoint = New-Object Windows.Point 0, 0
    $bg.EndPoint = New-Object Windows.Point 1, 1
    $null = $bg.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.Color]::FromRgb(0x26, 0x28, 0x33)), 0.0))
    $null = $bg.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.Color]::FromRgb(0x15, 0x16, 0x1C)), 1.0))
    $radius = $s * 0.225
    $dc.DrawRoundedRectangle($bg, $null, (New-Object Windows.Rect 0, 0, $s, $s), $radius, $radius)

    # A soft halo where the glint lands, so the spark reads as light, not paint.
    $halo = New-Object Windows.Media.RadialGradientBrush
    $halo.Center = New-Object Windows.Point 0.47, 0.5
    $halo.GradientOrigin = $halo.Center
    $halo.RadiusX = 0.42
    $halo.RadiusY = 0.42
    $null = $halo.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.Color]::FromArgb(0x30, 0x6E, 0x9A, 0xFF)), 0.0))
    $null = $halo.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.Color]::FromArgb(0x00, 0x6E, 0x9A, 0xFF)), 1.0))
    $dc.DrawRoundedRectangle($halo, $null, (New-Object Windows.Rect 0, 0, $s, $s), $radius, $radius)

    # The glint itself.
    $sparkFill = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(0xF4, 0xF7, 0xFF))
    $transform = New-Object Windows.Media.TransformGroup
    $transform.Children.Add((New-Object Windows.Media.ScaleTransform (($s * 0.0064), ($s * 0.0064))))
    $transform.Children.Add((New-Object Windows.Media.TranslateTransform (($s * 0.47), ($s * 0.50))))
    $dc.PushTransform($transform)
    $dc.DrawGeometry($sparkFill, $null, $spark)
    $dc.Pop()

    # The companion glint only exists where there is room for it.
    if ($Pixels -ge 48) {
        $companionFill = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(0x6E, 0x9A, 0xFF))
        $companionTransform = New-Object Windows.Media.TransformGroup
        $companionTransform.Children.Add((New-Object Windows.Media.ScaleTransform (($s * 0.0021), ($s * 0.0021))))
        $companionTransform.Children.Add((New-Object Windows.Media.TranslateTransform (($s * 0.78), ($s * 0.235))))
        $dc.PushTransform($companionTransform)
        $dc.DrawGeometry($companionFill, $null, $spark)
        $dc.Pop()
    }

    $dc.Close()

    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $Pixels, $Pixels, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return $stream.ToArray()
}

$sizes = 256, 128, 64, 48, 32, 24, 16
$frames = foreach ($size in $sizes) { , (New-IconFramePng -Pixels $size) }

$outPath = Join-Path $PSScriptRoot '..\Assets\Glimpse.ico'
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out

# ICONDIR
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)

# ICONDIRENTRY per frame; PNG frames are valid for every size on modern Windows.
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

foreach ($frame in $frames) {
    $writer.Write($frame, 0, $frame.Length)
}

$writer.Flush()
$resolved = [System.IO.Path]::GetFullPath($outPath)
[System.IO.File]::WriteAllBytes($resolved, $out.ToArray())
Write-Host "Wrote $($sizes.Count) frames, $($out.Length) bytes, to $resolved"
