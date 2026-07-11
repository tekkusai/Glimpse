# Glimpse

Glimpse is a Quick Look-style file previewer for Windows. Select something in File Explorer or on the desktop, press Space, and Glimpse opens a preview. The same shortcut also works in standard Windows Open and upload dialogs.

Press Space or Esc when you're done.

![Glimpse](docs/hero.png)

Glimpse is a native WPF app with a small Win32 input hook. It reads the current Explorer selection through Windows shell APIs; it doesn't take over file associations or replace your default apps.

## Getting Glimpse

Download `Glimpse-Setup-<version>.exe` from the [latest release](https://github.com/tekkusai/Glimpse/releases/latest). That's the right download for most people. GitHub's automatic “Source code” archives are there for anyone who wants to build Glimpse themselves.

The installer runs for the current user and can start Glimpse when you sign in. It installs WebView2 only when Windows doesn't already have it. For full Word, Excel, and PowerPoint previews, it can also offer LibreOffice if no compatible Office engine is installed; the installer shows the size of that download before anything happens.

## What it can do

- Preview files from Explorer, the desktop, and standard Open/upload dialogs.
- Move through the current folder with the header arrows or the arrow keys.
- Open the selected file with Enter or the Open button.
- Pin a preview while you keep browsing.
- Copy selected text where the preview supports it, or copy the file itself.
- Follow the Windows theme, with manual Light and Dark options in the tray menu.

Glimpse has built-in views for common images, text and source files, folders, archives, audio, video, fonts, executables, shortcuts, Photoshop and Illustrator files, and several 3D formats including STL, OBJ, PLY, 3MF, and STEP.

PDFs use the WebView2 PDF viewer. Office files get their best previews through LibreOffice or OpenOffice, with a simpler fallback when neither is installed. Local HTML is opened with scripts disabled and remote requests blocked.

## Requirements

- Windows 10 1809 or newer on x64. ARM64 works through Windows' x64 emulation.
- WebView2 Runtime for web-backed previews.
- .NET 10 SDK if you're building from source.
- Optional: LibreOffice or OpenOffice for full Office previews.
- Optional: Inno Setup 6 if you want to build the installer.

## Building from source

```powershell
git clone https://github.com/tekkusai/Glimpse.git
cd Glimpse
dotnet build .\SpacePreview.sln -c Release
```

The development build lands at:

```text
.\SpacePreview\bin\Release\net10.0-windows10.0.17763.0\Glimpse.exe
```

You can run that executable directly while working on the app.

## Tests

```powershell
dotnet test .\SpacePreview.sln -c Release
```

The `SpacePreview\scripts` folder also contains smoke and regression checks:

```powershell
.\SpacePreview\scripts\smoke.ps1
.\SpacePreview\scripts\regression.ps1
```

The smoke test expects Glimpse to be running already.

## Publishing

```powershell
# Framework-dependent build
.\SpacePreview\scripts\publish.ps1

# Self-contained installer payload
.\SpacePreview\scripts\publish.ps1 -SelfContained

# Installer
.\SpacePreview\scripts\build-installer.ps1
```

The installer build fetches Microsoft's WebView2 bootstrapper when necessary and verifies its Microsoft signature before packaging it. Downloaded dependencies, publish output, and generated installers stay out of Git.

## Startup commands

```powershell
# Start on Windows sign-in for the current user
.\SpacePreview\bin\Release\net10.0-windows10.0.17763.0\Glimpse.exe --install-startup

# Remove startup registration
.\SpacePreview\bin\Release\net10.0-windows10.0.17763.0\Glimpse.exe --uninstall-startup

# Stop the resident process
.\SpacePreview\bin\Release\net10.0-windows10.0.17763.0\Glimpse.exe --quit
```

## Security and privacy

Glimpse reads a file only when you ask to preview it. Files aren't uploaded, and Glimpse doesn't change their default applications.

The global Space shortcut is implemented with a low-level keyboard hook. Glimpse handles it only on supported selection surfaces such as Explorer, the desktop, standard file dialogs, and its own windows. Diagnostic logs remain under `%LOCALAPPDATA%\Glimpse`, rotate by size, and redact local paths by default.

Please use the process in [SECURITY.md](SECURITY.md) to report a vulnerability.

## License

Glimpse is available under the [MIT License](LICENSE).
