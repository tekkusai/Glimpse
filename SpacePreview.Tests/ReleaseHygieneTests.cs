using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SpacePreview.Tests;

public sealed class ReleaseHygieneTests
{
    [Fact]
    public void ProductAndInstallerVersionsAre305()
    {
        var project = TestSource.Read(@"SpacePreview\SpacePreview.csproj");
        var manifest = TestSource.Read(@"SpacePreview\app.manifest");
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");

        Assert.Contains("<Version>3.0.5</Version>", project, StringComparison.Ordinal);
        Assert.Contains("version=\"3.0.5.0\"", manifest, StringComparison.Ordinal);
        Assert.Contains("#define MyAppVersion \"3.0.5\"", installer, StringComparison.Ordinal);
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

        Assert.Contains("alt=\"Glimpse\"", text, StringComparison.Ordinal);
        Assert.Contains("Glimpse-Setup-<version>.exe", text, StringComparison.Ordinal);
        Assert.Contains(@"%LOCALAPPDATA%\Glimpse", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SpacePreview-Setup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SpacePreview.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"%LOCALAPPDATA%\SpacePreview", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The README's button downloads the installer itself, so it names a
    /// version. A version bump that forgot it would hand out the old one.
    /// </summary>
    [Fact]
    public void TheReadmesDownloadIsTheProjectsVersion()
    {
        var project = TestSource.Read(@"SpacePreview\SpacePreview.csproj");
        var version = Regex.Match(project, "<Version>([^<]+)</Version>").Groups[1].Value;
        var readme = TestSource.Read("README.md");
        var links = Regex.Matches(readme, @"releases/download/v([^/]+)/Glimpse-Setup-(.+?)\.exe");

        Assert.NotEmpty(links);
        Assert.All(links, link =>
        {
            Assert.Equal(version, link.Groups[1].Value);
            Assert.Equal(version, link.Groups[2].Value);
        });
        Assert.Contains($"What's new in {version}<", readme, StringComparison.Ordinal);
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
        var script = TestSource.Read(@"SpacePreview\scripts\sign-installer.ps1");

        Assert.Contains("$checksum = \"$Setup.sha256\"", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::WriteAllText", script, StringComparison.Ordinal);
        Assert.Contains("$(Split-Path -Leaf $Setup)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerBuildSignsTheChecksumAndChecksItWithTheAppsKey()
    {
        var build = TestSource.Read(@"SpacePreview\scripts\build-installer.ps1");
        var sign = TestSource.Read(@"SpacePreview\scripts\sign-installer.ps1");

        // A missing key stops the build before the long part, not after it.
        var keyCheck = build.IndexOf("Test-Path -LiteralPath $signingKey", StringComparison.Ordinal);
        Assert.InRange(keyCheck, 0, build.IndexOf("publish.ps1", StringComparison.Ordinal));
        Assert.Contains("$env:GLIMPSE_UPDATE_KEY", build, StringComparison.Ordinal);
        Assert.Contains(@"GlimpseRelease\update-signing-key.pem", build, StringComparison.Ordinal);
        Assert.Contains("& pwsh -NoProfile -File $PSCommandPath", build, StringComparison.Ordinal);
        Assert.Contains(@"sign-installer.ps1"" -Setup $setup -Key $signingKey", build, StringComparison.Ordinal);
        Assert.Contains("$signatureFile = \"$checksum.sig\"", sign, StringComparison.Ordinal);
        Assert.Contains("$signer.SignData($checksumBytes", sign, StringComparison.Ordinal);
        Assert.Contains("$verifier.VerifyData($checksumBytes", sign, StringComparison.Ordinal);
        Assert.Contains(@"Services\AppUpdateService.cs", sign, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SigningWithAnyKeyButTheReleaseKeyLeavesNothingToUpload()
    {
        using var temp = new TempDirectory();
        using var throwaway = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = temp.CreateFile("throwaway.pem", throwaway.ExportPkcs8PrivateKeyPem());
        var setup = Path.Combine(temp.Path, "Glimpse-Setup-3.0.5.exe");
        File.WriteAllBytes(setup, SetupImage.Create("3.0.5"));

        var (exitCode, output) = await SignAsync(TestSource.FilePath(@"SpacePreview\scripts\sign-installer.ps1"), setup, key);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("is not the release key", output, StringComparison.Ordinal);
        Assert.Equal([key], Directory.GetFiles(temp.Path));
    }

    /// <summary>
    /// Every copy of Glimpse skips for good a release whose installer says it
    /// is another version. The signer runs from a copy beside a stand-in
    /// project and app source that carry a throwaway key.
    /// </summary>
    [Theory]
    [InlineData("Glimpse-Setup-3.1.0.exe", "3.1.0", "3.1.0", true)]
    [InlineData("Glimpse-Setup-3.1.0.exe", "3.0.9", "3.1.0", false)]
    [InlineData("Glimpse-Setup-3.1.0.exe", "3.1.0", "3.2.0", false)]
    [InlineData("Glimpse-Setup.exe", "3.1.0", "3.1.0", false)]
    public async Task OnlyAnInstallerNamedAndVersionedAsTheProjectIsSigned(
        string name,
        string installerVersion,
        string projectVersion,
        bool signs)
    {
        using var temp = new TempDirectory();
        using var throwaway = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var script = Path.Combine(temp.CreateDirectory("scripts"), "sign-installer.ps1");
        File.Copy(TestSource.FilePath(@"SpacePreview\scripts\sign-installer.ps1"), script);
        File.WriteAllText(Path.Combine(temp.CreateDirectory("Services"), "AppUpdateService.cs"), throwaway.ExportSubjectPublicKeyInfoPem());
        temp.CreateFile("SpacePreview.csproj", $"<Project><PropertyGroup><Version>{projectVersion}</Version></PropertyGroup></Project>");
        var key = temp.CreateFile("throwaway.pem", throwaway.ExportPkcs8PrivateKeyPem());
        var artifacts = temp.CreateDirectory("artifacts");
        var setup = Path.Combine(artifacts, name);
        File.WriteAllBytes(setup, SetupImage.Create(installerVersion));

        var (exitCode, output) = await SignAsync(script, setup, key);

        if (signs)
        {
            Assert.True(exitCode == 0, output);
            Assert.Equal([setup, setup + ".sha256", setup + ".sha256.sig"], Directory.GetFiles(artifacts).Order(StringComparer.Ordinal));
        }
        else
        {
            Assert.NotEqual(0, exitCode);
            Assert.Empty(Directory.GetFiles(artifacts));
        }
    }

    private static async Task<(int ExitCode, string Output)> SignAsync(string script, string setup, string key)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script,
                "-Setup", setup,
                "-Key", key
            }
        };

        using var pwsh = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var output = pwsh.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = pwsh.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await pwsh.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            pwsh.Kill();
            Assert.Fail("sign-installer.ps1 did not finish in a minute.");
        }

        return (pwsh.ExitCode, await output + await error);
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

        // 2.0's updater offers every release at once: no release gets two days there.
        Assert.Contains("Copies still on 2.x get no such window, for every release, for as long as any 2.x copy exists.", release, StringComparison.Ordinal);
        Assert.Contains("Test every release fully before it goes public.", release, StringComparison.Ordinal);
        Assert.Contains("Before every install, automatic or through Install now, a copy asks GitHub once more", release, StringComparison.Ordinal);
        Assert.Contains("If a check found it in the last minute, the copy does not ask again.", release, StringComparison.Ordinal);
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
