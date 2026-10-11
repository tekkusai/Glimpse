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
    public void InAppUpdateUsesTheExistingInstallerWithoutASecondLibreOfficeDownload()
    {
        var updater = TestSource.Read(@"SpacePreview\Services\AppUpdateService.cs");
        var tray = TestSource.Read(@"SpacePreview\Services\TrayIcon.cs");
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");

        Assert.Contains("Update to {update.VersionText}", tray, StringComparison.Ordinal);
        Assert.Contains("/GLIMPSEUPDATE", updater, StringComparison.Ordinal);
        Assert.Contains("function AutoUpdateRequested", installer, StringComparison.Ordinal);
        Assert.Contains("Parameters: \"--updated\"; Check: AutoUpdateRequested", installer, StringComparison.Ordinal);
        Assert.Contains("and not AutoUpdateRequested()", installer, StringComparison.Ordinal);
    }

}
