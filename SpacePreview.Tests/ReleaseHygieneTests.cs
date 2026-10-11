namespace SpacePreview.Tests;

public sealed class ReleaseHygieneTests
{
    [Fact]
    public void ProductAndInstallerVersionsAre300()
    {
        var project = TestSource.Read(@"SpacePreview\SpacePreview.csproj");
        var manifest = TestSource.Read(@"SpacePreview\app.manifest");
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");

        Assert.Contains("<Version>3.0.0</Version>", project, StringComparison.Ordinal);
        Assert.Contains("version=\"3.0.0.0\"", manifest, StringComparison.Ordinal);
        Assert.Contains("#define MyAppVersion \"3.0.0\"", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Public112NotesArePlainAndCarryTheFinalInstallerHash()
    {
        var notes = TestSource.Read(@"docs\RELEASE-NOTES-1.1.2.md");

        Assert.Contains("# Glimpse 1.1.2", notes, StringComparison.Ordinal);
        Assert.Contains("C4E726BCE4C6B4FD7B793DC29937FD72241C16193535B646162CD292F303A292", notes, StringComparison.Ordinal);
        Assert.Contains("installer or uninstaller", notes, StringComparison.Ordinal);
        Assert.Contains("certain large PDFs", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("INSTALLER_SHA256", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("PENDING", notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not code-signed", notes, StringComparison.Ordinal);
        Assert.Contains("SmartScreen", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicDocsPointUsersAtGlimpseArtifacts()
    {
        var text = string.Join("\n", new[]
        {
            TestSource.Read("README.md"),
            TestSource.Read(@"docs\RELEASE.md"),
            TestSource.Read("SECURITY.md")
        });

        Assert.Contains("# Glimpse", text, StringComparison.Ordinal);
        Assert.Contains("Glimpse-Setup-<version>.exe", text, StringComparison.Ordinal);
        Assert.Contains(@"%LOCALAPPDATA%\Glimpse", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SpacePreview-Setup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SpacePreview.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"%LOCALAPPDATA%\SpacePreview", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseRegressionScriptTargetsTheShippedExecutable()
    {
        var script = TestSource.Read(@"SpacePreview\scripts\regression.ps1");

        Assert.Contains(@"Glimpse.exe", script, StringComparison.Ordinal);
        Assert.Contains("GLIMPSE_DATA_ROOT", script, StringComparison.Ordinal);
        Assert.Contains("'Glimpse.log'", script, StringComparison.Ordinal);
        Assert.Contains("'settings.json'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"$env:LOCALAPPDATA 'Glimpse\Glimpse.log'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"$env:LOCALAPPDATA 'Glimpse\settings.json'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SpacePreview.exe", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"SpacePreview\SpacePreview.log", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"SpacePreview\settings.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLicenseAndNoticesUsePublicBranding()
    {
        var license = TestSource.Read("LICENSE");
        var notices = TestSource.Read(@"docs\THIRD-PARTY-NOTICES.txt");

        Assert.Contains("Glimpse contributors", license, StringComparison.Ordinal);
        Assert.Contains("source license is in LICENSE", notices, StringComparison.Ordinal);
        Assert.DoesNotContain("SpacePreview contributors", license, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedPayloadCarriesDependencyLicenseFiles()
    {
        var project = TestSource.Read(@"SpacePreview\SpacePreview.csproj");
        var notices = TestSource.Read(@"docs\THIRD-PARTY-NOTICES.txt");

        Assert.Contains("Microsoft.Web.WebView2-LICENSE.txt", project, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2-NOTICE.txt", project, StringComparison.Ordinal);
        Assert.Contains("Onigwrap-THIRD-PARTY-NOTICES.txt", project, StringComparison.Ordinal);
        Assert.Contains("CopyToPublishDirectory=\"PreserveNewest\"", project, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2018 Modestas Petravicius", notices, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2014 Adam Hathcock", notices, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2018-2019, Alexandre Mutel", notices, StringComparison.Ordinal);
        Assert.Contains("Redistributions in binary form must reproduce", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerKeepsLibreOfficeSelectedWithoutAnElevationHandoff()
    {
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");

        Assert.Contains("Name: \"fullpreviews\"", installer, StringComparison.Ordinal);
        Assert.Contains("/a \"\"{tmp}\\LibreOffice.msi\"\" /qn", installer, StringComparison.Ordinal);
        Assert.Contains("TARGETDIR=\"\"{localappdata}\\Programs\\LibreOffice\"\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Verb: \"runas\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Name: \"fullpreviews\"; Flags: unchecked", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerFollowsTheThemeAndShowsNoBorrowedArt()
    {
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");

        // Inno fills every unset picture slot with its own software box, and
        // under dynamic the dark slots fall back on their own.
        Assert.Contains("WizardStyle=modern dynamic", installer, StringComparison.Ordinal);
        foreach (var slot in new[] { "WizardImageFile", "WizardSmallImageFile", "WizardImageFileDynamicDark", "WizardSmallImageFileDynamicDark" })
        {
            Assert.Matches($@"(?m)^{slot}=\r?$", installer);
        }
    }

    [Fact]
    public void UninstallRemovesOnlyTheLibreOfficeImageOwnedByGlimpse()
    {
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");

        Assert.Contains("AfterInstall: MarkBundledOfficeEngine", installer, StringComparison.Ordinal);
        Assert.Contains("LoadStringFromFile(BundledOfficeEngineMarker(), MarkerText)", installer, StringComparison.Ordinal);
        Assert.Contains("MarkerText = '{#LibreOfficeManagedMarkerText}'", installer, StringComparison.Ordinal);
        Assert.Contains("DelTree(BundledOfficeEngineRoot(), True, True, True)", installer, StringComparison.Ordinal);
        Assert.Contains("RemoveBundledOfficeEngine();", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void ReinstallUsesTheLiveStartupPreference()
    {
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");

        Assert.Contains("HasExplicitTaskSelection", installer, StringComparison.Ordinal);
        Assert.Contains("UsePreviousTasks=yes", installer, StringComparison.Ordinal);
        Assert.Contains("StartupRegisteredAtStart", installer, StringComparison.Ordinal);
        Assert.Contains("ApplyLiveStartupDefault", installer, StringComparison.Ordinal);
        Assert.Contains("if WizardSilent", installer, StringComparison.Ordinal);
        Assert.Contains("WizardSelectTasks('startup')", installer, StringComparison.Ordinal);
        Assert.Contains("WizardSelectTasks('!startup')", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("WizardSelectTasks('*startup')", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("WizardSelectTasks('*!startup')", installer, StringComparison.Ordinal);
        Assert.Contains("Parameters: \"--uninstall-startup\"; Check: StartupTaskNotSelected", installer, StringComparison.Ordinal);
        Assert.Contains("Parameters: \"--install-startup\"; Check: StartupTaskSelected", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerBuildWritesACompanionChecksumFile()
    {
        var script = TestSource.Read(@"SpacePreview\scripts\build-installer.ps1");

        Assert.Contains("$checksum = \"$setup.sha256\"", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::WriteAllText", script, StringComparison.Ordinal);
        Assert.Contains("$(Split-Path -Leaf $setup)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerBuildSignsTheChecksumAndChecksItWithTheAppsKey()
    {
        var script = TestSource.Read(@"SpacePreview\scripts\build-installer.ps1");

        // A missing key stops the build before the long part, not after it.
        var keyCheck = script.IndexOf("Test-Path -LiteralPath $signingKey", StringComparison.Ordinal);
        Assert.InRange(keyCheck, 0, script.IndexOf("publish.ps1", StringComparison.Ordinal));
        Assert.Contains("$env:GLIMPSE_UPDATE_KEY", script, StringComparison.Ordinal);
        Assert.Contains(@"GlimpseRelease\update-signing-key.pem", script, StringComparison.Ordinal);
        Assert.Contains("$signatureFile = \"$checksum.sig\"", script, StringComparison.Ordinal);
        Assert.Contains("$signer.SignData($checksumBytes", script, StringComparison.Ordinal);
        Assert.Contains("$verifier.VerifyData($checksumBytes", script, StringComparison.Ordinal);
        Assert.Contains(@"Services\AppUpdateService.cs", script, StringComparison.Ordinal);
        Assert.Contains("& pwsh -NoProfile -File $PSCommandPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseDocsNameAllThreeAssetsTheKeyBackupAndTheRepositoryRule()
    {
        var release = TestSource.Read(@"docs\RELEASE.md");

        Assert.Contains("attach all three assets", release, StringComparison.Ordinal);
        Assert.Contains("`Glimpse-Setup-<version>.exe.sha256.sig`", release, StringComparison.Ordinal);
        Assert.Contains("IEEE P1363", release, StringComparison.Ordinal);
        Assert.Contains("GLIMPSE_UPDATE_KEY", release, StringComparison.Ordinal);
        Assert.Contains("Back up the key", release, StringComparison.Ordinal);
        Assert.Contains("Renaming the repository or transferring it", release, StringComparison.Ordinal);
    }

    [Fact]
    public void ASilentUpdateThatDoesNotFinishStartsTheOldGlimpseAgain()
    {
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");
        var deinitialize = installer[installer.IndexOf("procedure DeinitializeSetup();", StringComparison.Ordinal)..];

        Assert.Contains("if CurStep = ssPostInstall then", installer, StringComparison.Ordinal);
        Assert.Contains("InstallFinished := True;", installer, StringComparison.Ordinal);
        Assert.Contains("AutoUpdateRequested()", deinitialize, StringComparison.Ordinal);
        Assert.Contains("and not InstallFinished", deinitialize, StringComparison.Ordinal);
        // Setup failed before quitting a running Glimpse: a second launch would only open Settings.
        Assert.Contains("and not CheckForMutexes('{#MyMutexName}')", deinitialize, StringComparison.Ordinal);
        Assert.Contains(@"{localappdata}\Programs\{#MyAppName}\{#MyAppExeName}", deinitialize, StringComparison.Ordinal);
        Assert.Contains("Exec(ExePath, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);", deinitialize, StringComparison.Ordinal);
    }

    [Fact]
    public void InAppUpdateUsesTheExistingInstallerWithoutASecondLibreOfficeDownload()
    {
        var updater = TestSource.Read(@"SpacePreview\Services\AppUpdateService.cs");
        var tray = TestSource.Read(@"SpacePreview\Services\TrayIcon.cs");
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");

        // Updates never add to the tray menu: it stays a door to Settings.
        Assert.DoesNotContain("Update to", tray, StringComparison.Ordinal);
        Assert.Contains("/GLIMPSEUPDATE", updater, StringComparison.Ordinal);
        Assert.Contains("function AutoUpdateRequested", installer, StringComparison.Ordinal);
        Assert.Contains("Parameters: \"--updated\"; Check: AutoUpdateRequested", installer, StringComparison.Ordinal);
        Assert.Contains("and not AutoUpdateRequested()", installer, StringComparison.Ordinal);
    }

}
