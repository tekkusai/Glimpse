namespace SpacePreview.Tests;

/// <summary>
/// Setup cannot run in the unit suite, so these read its script: what it
/// may end before it copies, and what it must leave alone.
/// </summary>
public sealed class InstallerRunningCopyTests
{
    // taskkill by image name also ended a developer build or a portable
    // copy running from another folder.
    [Fact]
    public void SetupEndsProcessesByTheInstalledPathNeverByImageName()
    {
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");

        Assert.DoesNotContain("taskkill", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/IM ", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM Win32_Process WHERE ExecutablePath = \"", installer, StringComparison.Ordinal);
    }

    // A PDF helper of 3.0.0 or 2.x holds no mutex and can outlive its
    // resident, so the look for running copies cannot hang on the mutex.
    [Fact]
    public void HelpersAreEndedEvenWhenNoResidentIsRunning()
    {
        var quit = Body("function QuitResident(const ExePath: String): Boolean;");

        Assert.DoesNotContain("Exit;", quit, StringComparison.Ordinal);
        Assert.Contains("Result := EndProcessesAt(ExePath);", quit, StringComparison.Ordinal);

        // The resident is asked first, so signing work reaches its draft.
        Assert.True(
            quit.IndexOf("Exec(ExePath, '--quit'", StringComparison.Ordinal) < quit.IndexOf("EndProcessesAt(ExePath)", StringComparison.Ordinal));
    }

    // When Windows cannot be asked, or the resident is elevated and Setup
    // is not, no process is found and none is ended. The mutex is still
    // held, and that alone must refuse the copy.
    [Fact]
    public void AResidentThatStillHoldsItsMutexRefusesTheCopy()
    {
        var quit = Body("function QuitResident(const ExePath: String): Boolean;");

        var ended = quit.IndexOf("Result := EndProcessesAt(ExePath);", StringComparison.Ordinal);
        var held = quit.IndexOf("if Result and FileExists(ExePath) and CheckForMutexes('{#MyMutexName}') then", StringComparison.Ordinal);
        Assert.True(ended >= 0 && held > ended, "The mutex is looked at after the processes are ended.");
        Assert.EndsWith("Result := False;\n  end;", quit.ReplaceLineEndings("\n").TrimEnd(), StringComparison.Ordinal);
    }

    // Copying over a running copy leaves old and new files mixed. Setup
    // stops before the copy; a silent update then ends with exit code 7.
    [Fact]
    public void SetupStopsBeforeCopyingWhenACopyIsStillRunning()
    {
        var prepare = Body("function PrepareToInstall(var NeedsRestart: Boolean): String;");

        Assert.Contains("if not QuitResident(ExpandConstant('{app}\\{#MyAppExeName}')) then", prepare, StringComparison.Ordinal);
        Assert.Contains("Result := '{#MyAppName} is still running", prepare, StringComparison.Ordinal);
    }

    /// <summary>A routine's statements: from its heading to the end that closes it.</summary>
    private static string Body(string heading)
    {
        var installer = TestSource.Read(@"SpacePreview\installer\Glimpse.iss");
        var start = installer.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{heading} is not in the script.");
        var end = installer.IndexOf("\nend;", start, StringComparison.Ordinal);
        return installer[(start + heading.Length)..end];
    }
}
