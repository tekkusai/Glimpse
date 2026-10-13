using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The window asks the disk what an item is once, and everything it shows
/// before the content loads is answered from that. A disk of this PC is
/// asked on the window's thread, as it always was. A network is asked on
/// another thread, because a share that has stopped answering takes most
/// of a minute to say so, and the window would be stopped for as long.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ItemFactsTests
{
    // Loopback, and shares that are not there: nothing leaves this PC. The
    // tests stand in for the disk, so what these would answer is never asked.
    private const string SlowShare = @"\\localhost\glimpse-slow";
    private const string QuickShare = @"\\localhost\glimpse-quick";

    [Fact]
    public async Task AnItemOnThisPcIsAskedAboutAtOnceOnTheCallersThread()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("notes.txt", new string('a', 300));
        var folder = temp.CreateDirectory("album");
        var askedOn = 0;
        DiskEntry? Ask(string path)
        {
            askedOn = Environment.CurrentManagedThreadId;
            return ItemFacts.Entry(path);
        }

        var reading = ItemFacts.ReadAsync(file, CancellationToken.None, Ask);

        Assert.True(reading.IsCompletedSuccessfully, "A file on a disk of this PC was not known when the question returned.");
        Assert.Equal(Environment.CurrentManagedThreadId, askedOn);
        var facts = await reading;
        Assert.True(facts.IsFile);
        Assert.Equal(300, facts.Item!.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(file), facts.Item.ModifiedUtc);

        var album = await ItemFacts.ReadAsync(folder, CancellationToken.None);
        Assert.True(album.IsDirectory);
        Assert.False(album.IsFile);

        var gone = await ItemFacts.ReadAsync(Path.Combine(temp.Path, "gone.txt"), CancellationToken.None);
        Assert.Null(gone.Item);
        Assert.False(gone.IsFile);
        Assert.False(gone.IsDirectory);
    }

    [Theory]
    [InlineData(@"\\server\share\notes.txt", true)]
    [InlineData(@"\\?\UNC\server\share\notes.txt", true)]
    [InlineData(@"notes.txt", true)]
    [InlineData(@"", true)]
    public void WhatIsNotAPathOnADriveOfThisPcIsOnANetwork(string path, bool network) =>
        Assert.Equal(network, ItemFacts.OnANetwork(path));

    [Fact]
    public void AFileInTheTestFolderIsNotOnANetwork()
    {
        using var temp = new TempDirectory();
        Assert.False(ItemFacts.OnANetwork(temp.CreateFile("notes.txt")));
    }

    [Fact]
    public async Task AnItemOnANetworkIsAskedAboutOnAnotherThread()
    {
        using var share = new Silence();
        var asker = Environment.CurrentManagedThreadId;
        var askedOn = 0;
        var reading = ItemFacts.ReadAsync($@"{SlowShare}\notes.txt", CancellationToken.None, _ =>
        {
            askedOn = Environment.CurrentManagedThreadId;
            share.Wait();
            return new DiskEntry(false, 4096, DateTime.UtcNow);
        });

        Assert.False(reading.IsCompleted, "The question to a network was answered on the thread that asked it.");
        share.End();
        var facts = await reading;

        Assert.NotEqual(asker, askedOn);
        Assert.True(facts.IsFile);
        Assert.Equal(4096, facts.Item!.Length);
    }

    [Fact]
    public async Task AShareIsAskedOneQuestionAtATime()
    {
        // The reader steps through a folder on a share that has stopped
        // answering. Each step asks about another file, and gives up on
        // the one before.
        using var share = new Silence();
        var asked = new List<string>();
        DiskEntry? Ask(string path)
        {
            lock (asked)
            {
                asked.Add(Path.GetFileName(path));
            }

            if (path.StartsWith(@"\\stalled\", StringComparison.Ordinal))
            {
                share.Wait();
            }

            return new DiskEntry(false, 1, DateTime.UtcNow);
        }

        string[] Asked()
        {
            lock (asked)
            {
                return [.. asked];
            }
        }

        using var left = new CancellationTokenSource();
        var first = ItemFacts.ReadAsync(@"\\stalled\share\one.txt", CancellationToken.None, Ask);
        Assert.True(SpinWait.SpinUntil(() => Asked().Length == 1, TimeSpan.FromSeconds(5)));
        var second = ItemFacts.ReadAsync(@"\\stalled\share\two.txt", left.Token, Ask);
        var third = ItemFacts.ReadAsync(@"\\stalled\share\three.txt", CancellationToken.None, Ask);

        // Another share is not behind this one.
        var elsewhere = await ItemFacts.ReadAsync(@"\\answering\share\other.txt", CancellationToken.None, Ask);
        Assert.True(elsewhere.IsFile);
        await Task.Delay(200);
        Assert.Equal(["one.txt", "other.txt"], Asked());

        // The step to the second file was left before the share answered:
        // nobody waits for it, and its question is never put.
        left.Cancel();
        share.End();
        Assert.True((await first).IsFile);
        Assert.Null((await second).Item);
        Assert.True((await third).IsFile);
        Assert.Equal(["one.txt", "other.txt", "three.txt"], Asked());
    }

    [Fact]
    public async Task AShortcutSaysWhereItPointsWhenSomethingIsThere()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateFile("report.txt", "the report");
        var link = Path.Combine(temp.Path, "report.lnk");
        var dangling = Path.Combine(temp.Path, "gone.lnk");
        CreateShortcut(link, target);
        CreateShortcut(dangling, Path.Combine(temp.Path, "gone.txt"));

        var facts = await ItemFacts.ReadAsync(link, CancellationToken.None);
        Assert.Equal(target, facts.ShortcutTarget, ignoreCase: true);
        Assert.True(facts.IsFile);
        Assert.Equal(10, facts.Content!.Length);
        Assert.NotEqual(facts.Content, facts.Item);

        // One that points at nothing is previewed as itself.
        var broken = await ItemFacts.ReadAsync(dangling, CancellationToken.None);
        Assert.Null(broken.ShortcutTarget);
        Assert.Same(broken.Item, broken.Content);
        Assert.True(broken.IsFile);

        // A shortcut on a network is read on the thread that asked the
        // network about it, which is not the window's.
        Assert.Equal(target, await Task.Run(() => PreviewWindow.ReadShortcutTarget(link)), ignoreCase: true);
    }

    /// <summary>
    /// The thing itself: files of every kind on a share that will never
    /// answer, opened one after another in a real window, with nothing
    /// standing in for the disk. Each open returns, and the window's
    /// thread goes on running, which is what lets Esc close it. On the
    /// code before this, the first open stopped the thread for as long as
    /// Windows took to give up on the address.
    ///
    /// This is the one test that leaves the PC, so it runs only when
    /// GLIMPSE_DEAD_SHARE names an address nothing answers from, such as
    /// 192.0.2.1, which is reserved for examples.
    /// </summary>
    [Fact]
    public Task AShareThatNeverAnswersStopsNothing() => WpfApp.RunAsync(async () =>
    {
        if (Environment.GetEnvironmentVariable("GLIMPSE_DEAD_SHARE") is not { Length: > 0 } address)
        {
            return;
        }

        var window = Parked();
        try
        {
            foreach (var name in new[] { "notes.txt", "photo.png", "clip.mp4", "paper.pdf", "sheet.xlsx", "letter.docx", "setup.exe", "link.lnk", "drawing.svg", "album" })
            {
                var path = $@"\\{address}\glimpse\{name}";
                var opening = Stopwatch.StartNew();
                window.ShowPreview(path, IntPtr.Zero);
                Assert.True(opening.ElapsedMilliseconds < 1500, $"Opening {name} stopped the window's thread for {opening.ElapsedMilliseconds} ms.");

                Assert.Equal(name, window.FileNameText.Text);
                Assert.Equal(string.Empty, window.SubtitleText.Text);

                // The thread keeps its turns: nothing queued behind the
                // open is asking the share either.
                for (var beat = 0; beat < 6; beat++)
                {
                    var turn = Stopwatch.StartNew();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Task.Delay(40);
                    Assert.True(turn.ElapsedMilliseconds < 1000, $"With {name} open, the window's thread was stopped for {turn.ElapsedMilliseconds} ms.");
                }
            }

            var closing = Stopwatch.StartNew();
            window.HidePreview();
            Assert.True(closing.ElapsedMilliseconds < 1500, $"Closing stopped the window's thread for {closing.ElapsedMilliseconds} ms.");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AShareSlowToAnswerShowsTheNameAndThenTheRest() => WpfApp.RunAsync(async () =>
    {
        using var share = new Silence();
        var window = Parked();
        window.DiskForTests = _ =>
        {
            share.Wait();
            return new DiskEntry(false, 4096, DateTime.UtcNow);
        };
        try
        {
            var opening = Stopwatch.StartNew();
            window.ShowPreview($@"{SlowShare}\notes.txt", IntPtr.Zero);

            // The window waited a moment, no longer, and went on with the name.
            Assert.InRange(opening.ElapsedMilliseconds, (long)ItemFacts.Patience.TotalMilliseconds - 20, 1500);
            Assert.Equal("notes.txt", window.FileNameText.Text);
            Assert.Equal(string.Empty, window.SubtitleText.Text);
            Assert.Empty(OpenActions(window));

            share.End();
            var waited = Stopwatch.StartNew();
            while (window.SubtitleText.Text.Length == 0 && waited.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(10);
            }

            Assert.StartsWith("4 KB · ", window.SubtitleText.Text, StringComparison.Ordinal);
            Assert.Contains("Show in Explorer", OpenActions(window));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AShareThatAnswersOpensWithItsHeaderComplete() => WpfApp.RunAsync(() =>
    {
        var window = Parked();
        var windowThread = Environment.CurrentManagedThreadId;
        var askedOn = 0;
        window.DiskForTests = _ =>
        {
            askedOn = Environment.CurrentManagedThreadId;
            Thread.Sleep(5);
            return new DiskEntry(false, 4096, DateTime.UtcNow);
        };
        try
        {
            window.ShowPreview($@"{QuickShare}\quick.txt", IntPtr.Zero);

            Assert.StartsWith("4 KB · ", window.SubtitleText.Text, StringComparison.Ordinal);
            Assert.Contains("Show in Explorer", OpenActions(window));
            Assert.NotEqual(0, askedOn);
            Assert.NotEqual(windowThread, askedOn);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    /// <summary>
    /// What a header offers follows what the disk said the item is. A
    /// folder opens in File Explorer and can be opened in a terminal. A
    /// file can be opened with another app, and is shared from here.
    /// </summary>
    [Fact]
    public Task TheHeaderOffersWhatTheItemIs() => WpfApp.RunAsync(() =>
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("album");
        var file = temp.CreateFile("notes.txt");
        var window = Parked();
        try
        {
            window.ShowPreview(folder, IntPtr.Zero);
            Assert.Equal(["Show in Explorer", "Open in Terminal"], OpenActions(window));
            Assert.Equal("File Explorer", Fields.Get<OpenTarget>(window, "_openTarget").AppName);
            Assert.StartsWith("Folder · ", window.SubtitleText.Text, StringComparison.Ordinal);

            window.ShowPreview(file, IntPtr.Zero);
            Assert.Equal(["Open with…", "Show in Explorer", "Share"], OpenActions(window));
            Assert.NotEqual("File Explorer", Fields.Get<OpenTarget>(window, "_openTarget").AppName);
            Assert.StartsWith("9 B · ", window.SubtitleText.Text, StringComparison.Ordinal);

            window.ShowPreview(Path.Combine(temp.Path, "gone.txt"), IntPtr.Zero);
            Assert.Empty(OpenActions(window));
            Assert.Equal(string.Empty, window.SubtitleText.Text);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    /// <summary>
    /// While browsing, the stage is held for an item whose shape a probe
    /// has still to name. A folder has no such shape, whatever it is
    /// called, and neither has a file of a kind that opens at one size.
    /// </summary>
    [Fact]
    public async Task AFolderWaitsForNoProbeOfItsShape()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("album.png");
        var notes = temp.CreateFile("notes.txt");
        var photo = temp.CreateFile("photo.png");
        static bool TakesTheDefaultStage(string path, ItemFacts facts) =>
            (bool)typeof(PreviewWindow).GetMethod("UsesDefaultStageWithoutProbe", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path, facts])!;

        Assert.True(TakesTheDefaultStage(folder, await ItemFacts.ReadAsync(folder, CancellationToken.None)));
        Assert.True(TakesTheDefaultStage(notes, await ItemFacts.ReadAsync(notes, CancellationToken.None)));
        Assert.False(TakesTheDefaultStage(photo, await ItemFacts.ReadAsync(photo, CancellationToken.None)));

        // Not there, or not heard from yet: nothing is known of its shape.
        Assert.False(TakesTheDefaultStage(notes, ItemFacts.Unknown));
    }

    /// <summary>
    /// What the window does with an item before its content loads says
    /// nothing to the disk. It reads what was asked once.
    /// </summary>
    [Fact]
    public void BeforeTheContentLoadsTheWindowAsksTheDiskOnce()
    {
        var show = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "ShowPreview");
        Assert.Equal(1, show.Split("ItemFacts.ReadAsync(").Length - 1);

        foreach (var method in new[] { "ShowPreview", "ShowHeader", "ApplyHeaderSubtitle", "DescribeHeaderItem", "BuildOpenActions", "CanRunAsAdministrator", "ResolveOpenTarget", "ToolFor", "WillStageChange", "UsesDefaultStageWithoutProbe", "LoadPreviewAsync", "BuildExecutableSwitches" })
        {
            var body = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", method);
            foreach (var question in new[] { "File.Exists(", "Directory.Exists(", "new FileInfo(", "new DirectoryInfo(", "File.GetAttributes(" })
            {
                Assert.DoesNotContain(question, body, StringComparison.Ordinal);
            }
        }

        // The stage's shape may read a file's first bytes as the window
        // opens, and only where they are at hand.
        var stage = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "TrySetIntrinsicStageFit");
        Assert.DoesNotContain("Directory.Exists(", stage, StringComparison.Ordinal);
        var atHand = stage.IndexOf("HasBytesAtHand(contentPath)", StringComparison.Ordinal);
        Assert.InRange(atHand, 0, stage.IndexOf("TryRead", StringComparison.Ordinal));

        var bytesAtHand = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "HasBytesAtHand");
        var onANetwork = bytesAtHand.IndexOf("ItemFacts.OnANetwork(path)", StringComparison.Ordinal);
        Assert.InRange(onANetwork, 0, bytesAtHand.IndexOf("File.GetAttributes(path)", StringComparison.Ordinal));
    }

    /// <summary>
    /// Once the content is loading, the file is read by the load, on
    /// threads of its own. These are the reads that were made on the
    /// window's thread in between, each for one kind of file.
    /// </summary>
    [Fact]
    public void WhatALoadReadsOfTheFileItReadsOffTheWindowsThread()
    {
        var content = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "BuildPreviewContentAsync");
        Assert.Contains("Task.Run(() => (FileSignatures.HeifBrandText(path), FileSignatures.ClassifyHeifFailure(path)), token)", content, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => SvgSizeProbe.TryReadCanvasSize(path, out var size) ? size : (Size?)null, token)", content, StringComparison.Ordinal);

        var office = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "LoadOfficePreviewAsync");
        Assert.Contains("Task.Run(() => OfficeDocumentPdfConverter.Convert(path, kind, token), token)", office, StringComparison.Ordinal);

        var failure = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "ShowMediaFailureAsync");
        Assert.Contains("Task.Run(() => Mp4CodecSniffer.TryRead(path))", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("Mp4CodecSniffer", TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "OnMediaMessage"), StringComparison.Ordinal);
    }

    private static string[] OpenActions(PreviewWindow window) =>
        [.. Fields.Get<List<(string Label, Action Action)>>(window, "_openActions").Select(action => action.Label)];

    private static PreviewWindow Parked()
    {
        // Parked off screen and frozen, like the probe.
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 640, Height = 480 };
        window.MarkGeometryUserPinnedForProbe();
        return window;
    }

    private static void CreateShortcut(string link, string target)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = target;
        shortcut.Save();
    }
}
