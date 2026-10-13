using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The shell's quiet promises: settings survive a torn write and keep no
/// list of the user's apps, caches stay bounded, and the log keeps every
/// process's lines even while two of them write.
/// </summary>
public sealed class ShellPlumbingTests
{
    [Fact]
    public void ATornSettingsFileIsKeptAsideAndNamedInTheLog()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"Theme\": \"Li");

        var loaded = Settings.Load(path);

        Assert.Equal(new Settings(), loaded);
        Assert.False(File.Exists(path));
        Assert.Equal("{ \"Theme\": \"Li", File.ReadAllText(path + ".bad"));
        Assert.True(LogEventuallyContains("settings corrupt, kept as settings.json.bad"));
    }

    [Fact]
    public void SettingsSaveLeavesOnlyTheFinishedFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        new Settings { WelcomeShown = true }.Save(path);
        new Settings { WelcomeShown = true, MediaVolume = 0.5 }.Save(path);

        Assert.Equal(new[] { path }, Directory.GetFiles(temp.Path));
        Assert.Equal(0.5, Settings.Load(path).MediaVolume);
    }

    [Fact]
    public void LoadingOldSettingsForgetsTheAppNamesTheyListed()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, """
            {
              "Theme": "Light",
              "PreviewAccessWarningLifetimeShows": 2,
              "PreviewAccessWarnings": {
                "v2|accessunavailable|discord": { "SchemaVersion": 2, "InitialShows": 1, "LastShownUtc": "2026-07-19T16:58:35+00:00" }
              }
            }
            """);

        var loaded = Settings.Load(path);

        var written = File.ReadAllText(path);
        Assert.DoesNotContain("discord", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PreviewAccessWarnings\"", written, StringComparison.Ordinal);
        Assert.Equal(2, loaded.PreviewAccessWarningLifetimeShows);
        Assert.Equal(Utilities.ThemePreference.Light, loaded.Theme);
    }

    // A workbook's sheets are read from its cache folder one tab at a
    // time. 3.0.1 pruned the folder of a workbook that was still open,
    // once it was ten minutes old and the cache was over its cap.
    [Fact]
    public void HousekeepingLeavesWhatAnOpenPreviewHolds()
    {
        using var temp = new TempDirectory();
        var now = DateTime.UtcNow;
        var book = Directory.CreateDirectory(Path.Combine(temp.Path, "DocumentCache", "book 2")).FullName;
        var second = Path.Combine(book, "sheet 2.html");
        File.WriteAllText(second, "<table>");
        Directory.SetLastWriteTimeUtc(book, now.AddDays(-20));
        // A neighbor whose name is only the start of the held one is not held.
        var neighbor = Directory.CreateDirectory(Path.Combine(temp.Path, "DocumentCache", "book")).FullName;
        Directory.SetLastWriteTimeUtc(neighbor, now.AddDays(-20));

        // Two views on the one workbook: a pinned preview and a glance.
        var pinned = CacheHousekeeping.Hold(Path.Combine(book, "sheet 1.html"));
        var glance = CacheHousekeeping.Hold(Path.Combine(book, "sheet 1.html"));
        Assert.Equal(1, CacheHousekeeping.Run(temp.Path, now));
        Assert.True(File.Exists(second));
        Assert.False(Directory.Exists(neighbor));

        // The glance ends, and ending twice changes nothing: the pinned one still reads.
        glance.Dispose();
        glance.Dispose();
        Assert.Equal(0, CacheHousekeeping.Run(temp.Path, now));
        Assert.True(File.Exists(second));

        pinned.Dispose();
        Assert.Equal(1, CacheHousekeeping.Run(temp.Path, now));
        Assert.False(Directory.Exists(book));
    }

    // Over its cap, the cache gives up its oldest entries first, and an
    // open preview's entry is the oldest there is.
    [Fact]
    public void HousekeepingOverItsCapStillLeavesWhatIsHeld()
    {
        using var temp = new TempDirectory();
        var now = DateTime.UtcNow;
        var cache = Directory.CreateDirectory(Path.Combine(temp.Path, "DocumentCache")).FullName;
        string Entry(string name, int minutesOld)
        {
            var folder = Directory.CreateDirectory(Path.Combine(cache, name)).FullName;
            using (var file = File.Create(Path.Combine(folder, "document.pdf")))
            {
                file.SetLength(200L * 1024 * 1024);
            }

            Directory.SetLastWriteTimeUtc(folder, now.AddMinutes(-minutesOld));
            return folder;
        }

        var open = Entry("open", 120);
        var idle = Entry("idle", 60);
        var late = Entry("late", 30);

        using (CacheHousekeeping.Hold(Path.Combine(open, "document.pdf")))
        {
            Assert.Equal(1, CacheHousekeeping.Run(temp.Path, now));
        }

        Assert.True(Directory.Exists(open));
        Assert.False(Directory.Exists(idle));
        Assert.True(Directory.Exists(late));
    }

    [Fact]
    public void HousekeepingPrunesLooseFilesAndRetiredFolders()
    {
        using var temp = new TempDirectory();
        var now = DateTime.UtcNow;
        var art = Directory.CreateDirectory(Path.Combine(temp.Path, "MediaArtwork")).FullName;
        var stale = Path.Combine(art, "stale.png");
        var fresh = Path.Combine(art, "fresh.png");
        File.WriteAllText(stale, "png");
        File.WriteAllText(fresh, "png");
        File.SetLastWriteTimeUtc(stale, now.AddDays(-20));
        var retired = Directory.CreateDirectory(Path.Combine(temp.Path, "AppPackageHtml")).FullName;
        File.WriteAllText(Path.Combine(retired, "page.html"), "<p>");
        var signatures = Directory.CreateDirectory(Path.Combine(temp.Path, "signatures")).FullName;
        var signature = Path.Combine(signatures, "sig.png");
        File.WriteAllText(signature, "png");
        File.SetLastWriteTimeUtc(signature, now.AddDays(-400));

        Assert.Equal(2, CacheHousekeeping.Run(temp.Path, now));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.False(Directory.Exists(retired));
        Assert.True(File.Exists(signature), "user data is not a cache");
    }

    [Fact]
    public void TheLogKeepsLinesWhileAnotherProcessHasItOpen()
    {
        // A helper process appending at the same moment holds the file open
        // for writing. The old writer asked for FileShare.Read, failed, and
        // dropped its whole batch.
        Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticLog.Pathname)!);
        var marker = $"shared append {Guid.NewGuid():N}";
        using (new FileStream(DiagnosticLog.Pathname, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            DiagnosticLog.Write(marker);
            Assert.True(LogEventuallyContains(marker));
        }
    }

    [Fact]
    public void AHelpersLinesSayWhoseTheyAre()
    {
        var marker = $"tagged {Guid.NewGuid():N}";
        DiagnosticLog.TagProcess("prove-pdf-scale");
        try
        {
            DiagnosticLog.Write(marker);
        }
        finally
        {
            DiagnosticLog.TagProcess(null);
        }

        Assert.True(LogEventuallyContains($"[prove-pdf-scale {Environment.ProcessId}] {marker}"));
    }

    private static bool LogEventuallyContains(string text)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var stream = new FileStream(DiagnosticLog.Pathname, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if (reader.ReadToEnd().Contains(text, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Not written yet.
            }

            Thread.Sleep(50);
        }

        return false;
    }
}
