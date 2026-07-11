# Drives Explorer for real: selects a file, presses Space through the low-level
# hook, screenshots the preview, then dismisses it with Esc. Run with the app
# already resident.
param(
    [string]$Folder = (Resolve-Path "$PSScriptRoot\..").Path,
    [string]$File = 'SpacePreview.csproj',
    [string]$Shot = "$env:TEMP\glimpse_smoke.png"
)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Native {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    public const uint KEYEVENTF_KEYUP = 0x0002;
}
"@

# Open an Explorer window on the folder and wait for it to register.
Start-Process explorer.exe $Folder
Start-Sleep -Seconds 2

$shell = New-Object -ComObject Shell.Application
$target = $null
foreach ($w in @($shell.Windows())) {
    try {
        if ($w.Document -and $w.Document.Folder.Self.Path -eq $Folder) { $target = $w }
    } catch { }
}
if (-not $target) { Write-Output 'FAIL: explorer window not found'; exit 1 }

# Select the file and focus the window.
$item = $target.Document.Folder.ParseName($File)
if (-not $item) { Write-Output 'FAIL: file not found in folder'; exit 1 }
$target.Document.SelectItem($item, 0x1D) # select + focus + deselect others + ensure visible
Start-Sleep -Milliseconds 600

# Pressing Alt first releases the foreground lock so SetForegroundWindow succeeds.
$hwnd = [IntPtr]$target.HWND
for ($i = 0; $i -lt 5; $i++) {
    [Native]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [Native]::SetForegroundWindow($hwnd) | Out-Null
    [Native]::keybd_event(0x12, 0, [Native]::KEYEVENTF_KEYUP, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
    if ([Native]::GetForegroundWindow() -eq $hwnd) { break }
}
if ([Native]::GetForegroundWindow() -ne $hwnd) { Write-Output 'FAIL: could not focus explorer'; exit 1 }
Start-Sleep -Milliseconds 400

# Space -> preview should open.
[Native]::keybd_event(0x20, 0, 0, [UIntPtr]::Zero)
[Native]::keybd_event(0x20, 0, [Native]::KEYEVENTF_KEYUP, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 1800

# Screenshot the whole virtual screen.
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$gfx.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bmp.Size)
$bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
$gfx.Dispose(); $bmp.Dispose()

# Esc -> preview should close.
[Native]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
[Native]::keybd_event(0x1B, 0, [Native]::KEYEVENTF_KEYUP, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 500

# Close the Explorer window we opened.
try { $target.Quit() } catch { }

Write-Output "OK: screenshot at $Shot"

