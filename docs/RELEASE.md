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

The script needs PowerShell 7 (`pwsh`). Started from Windows PowerShell 5.1, it hands itself to `pwsh`. It also needs the release key (see below) and stops before building when the key is missing.

The script reads the version from `SpacePreview\SpacePreview.csproj`, publishes a self-contained build, and writes the installer under `SpacePreview\artifacts\installer` with its `.sha256` sidecar and the sidecar's signature, `.sha256.sig`. It checks that signature with the public key in `AppUpdateService.cs` and fails if the two keys don't match. It downloads the WebView2 bootstrapper only when needed and checks Microsoft's signature before including it. The LibreOffice download used by Setup is pinned by SHA-256 as well.

Don't commit generated installers, publish directories, bootstrapper downloads, or Office installers.

## The release key

Glimpse installs an update only when the release's `.sha256.sig` verifies with the public key compiled into the app (`AppUpdateService.ReleasePublicKey`). Whoever holds the matching private key can update every installed copy, so the key never goes near GitHub.

- The script reads the key from `%LOCALAPPDATA%\GlimpseRelease\update-signing-key.pem`, or from the path in `GLIMPSE_UPDATE_KEY` when that is set.
- The `.sig` file is the base64 text of an ECDSA P-256 signature over the SHA-256 of the exact bytes of the `.sha256` file, followed by a line break. The signature is in IEEE P1363 form, r then s at 32 bytes each, which is what .NET's `ECDsa.SignData` returns by default.
- Back up the key. It is a short text file. Keep a copy in a password manager note or on a USB stick stored away from this PC. If the key is lost, no update can reach the installed copies until their users install a build with a new public key by hand.
- Never commit the key, paste it anywhere, or use it for a test build. Test builds sign with a throwaway key through `GLIMPSE_UPDATE_KEY`. The script then reports that the signature does not match the release key, which is expected for a test build.

## Publish on GitHub

Create the release as a draft, attach all three assets, then publish it as a stable release, not a prerelease:

- `Glimpse-Setup-<version>.exe`
- `Glimpse-Setup-<version>.exe.sha256`
- `Glimpse-Setup-<version>.exe.sha256.sig`

Keep the release tag (`v<version>`) and all three asset names on the same three-part version. The in-app updater rejects missing, renamed, or mismatched assets. A copy that checks while the release has no valid `.sig` skips that version for good, so attach everything before publishing. If a published release turns out wrong, ship the next patch version instead of replacing its assets. The installer should be the obvious choice for users; GitHub's source archives are for developers.

Installed copies install a release on their own 48 hours after it is published, at a moment when their user is away. Until then only people who press Install now in Settings get it. Those two days are the time to catch a bad release: deleting the release, or marking it a prerelease, stops it reaching anyone else.

Write release notes for people using the app, not as a dump of commit messages. Start with what changed in day-to-day use, mention any compatibility or installation concerns, and include the installer's SHA-256. If the build isn't code-signed, say so plainly so the SmartScreen warning isn't a surprise.

Before publishing, confirm that the release tag points to the commit used for the installer. Install that artifact silently for the current user, preserve the user's startup choice, relaunch Glimpse if it was running, and compare the installed payload with the publish directory.

A portable ZIP may be added in the future, but the installer remains the primary download. Glimpse currently targets .NET 10 LTS.

## Never move the repository

The updater trusts only `api.github.com/repos/tekkusai/Glimpse` and download URLs under `github.com/tekkusai/Glimpse/releases/download/`. Renaming the repository or transferring it to another owner ends updates for every installed copy. Before any rename or transfer, ship a release whose updater follows the new location, and wait until most copies have it.
