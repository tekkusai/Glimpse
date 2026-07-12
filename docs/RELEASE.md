# Cutting a release

This is the maintainer checklist for a public Glimpse build. The important rule is simple: the tag, installer, checksum, and installed payload must all come from the same commit.

## Check the build

```powershell
dotnet test .\SpacePreview.sln -c Release
.\SpacePreview\scripts\regression.ps1
```

With the installed app running, finish with the smoke test:

```powershell
.\SpacePreview\scripts\smoke.ps1
```

## Build the installer

Install Inno Setup 6 if it isn't already available:

```powershell
winget install JRSoftware.InnoSetup
```

Then build the exact public artifact:

```powershell
.\SpacePreview\scripts\build-installer.ps1
```

The script reads the version from `SpacePreview\SpacePreview.csproj`, publishes a self-contained build, and writes the installer under `SpacePreview\artifacts\installer`. It downloads the WebView2 bootstrapper only when needed and checks Microsoft's signature before including it. The LibreOffice download used by Setup is pinned by SHA-256 as well.

Don't commit generated installers, publish directories, bootstrapper downloads, or Office installers.

## Publish on GitHub

Publish it as a stable release, not a draft or prerelease. Attach both `Glimpse-Setup-<version>.exe` and its generated `.sha256` sidecar. Keep the release tag and both asset names on the same three-part version; the in-app updater rejects missing, renamed, or mismatched assets. The installer should be the obvious choice for users; GitHub's source archives are for developers.

Write release notes for people using the app, not as a dump of commit messages. Start with what changed in day-to-day use, mention any compatibility or installation concerns, and include the installer's SHA-256. If the build isn't code-signed, say so plainly so the SmartScreen warning isn't a surprise.

Before publishing, confirm that the release tag points to the commit used for the installer. Install that artifact silently for the current user, preserve the user's startup choice, relaunch Glimpse if it was running, and compare the installed payload with the publish directory.

A portable ZIP may be added in the future, but the installer remains the primary download. Glimpse currently targets .NET 10 LTS.
