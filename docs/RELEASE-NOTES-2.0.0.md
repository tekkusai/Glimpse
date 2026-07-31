# Glimpse 2.0

**Settings has a home.** The tray menu used to be the settings: a list of toggles living inside a menu. There is now a real settings window, and the tray menu is simply the door to it. Settings, the update window, and the access notice also share one look now, so no part of Glimpse falls back to a plain Windows dialog.

**Space works on the desktop.** Previews are no longer limited to Explorer windows. Select something on the desktop and press Space.

**Show in Explorer.** A preview can now reveal the file it is showing.

**Illustrator artwork is a real document.** A PDF-compatible `.ai` file used to open as a flat picture, page one of one, with no way to zoom in or move around. It now opens in the same viewer as a PDF, with zoom, scrolling, search, and the page rail. Very large artwork, such as packaging dielines several feet across, previews properly and zooms back out again instead of getting stuck at whatever the first zoom left behind. Files whose contents are a PDF but whose name is not were taking the same lesser path, and they are fixed too.

**Also in this release**

- The maximize button no longer stops responding after you close a fullscreen preview and open the file again
- A folder or file that Windows will not let Glimpse read now explains itself once rather than repeatedly
- Pin behaviour moved into settings, alongside the other preferences

**Installing**

Download **Glimpse-Setup-2.0.0.exe** below and run it. Existing preferences and your startup choice are preserved. If you are updating from 1.0.0, use the installer rather than the in-app updater.

**Requirements**

- Windows 10 1809 or newer
- WebView2 Runtime for web-backed previews; Setup installs it only when it is missing
- LibreOffice or OpenOffice for full Word, Excel and PowerPoint previews; Setup can add LibreOffice when needed

**SHA-256**

`9AEFA3292524857F76CF654ED9050F074847152DC7469C6EE13A9197D56D9700`

The installer is not code-signed yet, so Windows may show a SmartScreen warning.
