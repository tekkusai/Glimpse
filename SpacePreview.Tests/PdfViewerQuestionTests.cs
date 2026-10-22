using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// Glimpse learns where the PDF viewer stands by asking it through UI
/// Automation. The first question a process asks comes back with no web
/// content at all, because Chromium only then starts building the tree it
/// was asked for. That is no answer, and 3.0.0 took it for "page 1". A
/// question can also block for good, so one viewer is asked one question
/// at a time, and never again after one it kept.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PdfViewerQuestionTests
{
    // This is the reader the page sync never asks about: no click and no
    // key, so nothing has asked the viewer anything before Sign does. The
    // viewer is silent only to a process's first question, so this fails on
    // the 3.0.0 read when no other test has asked first: run the class alone.
    [Fact]
    public Task AReaderWhoOnlyUsedTheWheelIsFoundOnTheirPage() => WpfApp.RunAsync(async () =>
    {
        using var viewer = await Viewer.ShowAsync(pages: 12);
        await viewer.WheelAsync(4000);
        Assert.False(viewer.Window.UserEngagedContent);
        Assert.False(viewer.Window.ContentOwnsKeyboard());

        // One question, the way the Sign pill asks it.
        var (page, total) = await viewer.ReadPositionAsync();

        var shown = await viewer.PageBoxAsync();
        Assert.True(shown > 1, "The wheel never moved the viewer.");
        Assert.Equal(shown, page);
        Assert.Equal(12, total);
    });

    [Fact]
    public Task ALookThatFindsNothingIsNotAnAnswer() => WpfApp.RunAsync(async () =>
    {
        using var viewer = await Viewer.ShowAsync(pages: 2);
        var looks = 0;

        // Nothing there yet, then a tree that changes under the look, then the page.
        var answer = await viewer.AskAsync(_ => Interlocked.Increment(ref looks) switch
        {
            1 => null,
            2 => throw new ElementNotAvailableException(),
            _ => 7
        });

        Assert.Equal(7, answer);
        Assert.Equal(3, looks);
    });

    [Fact]
    public Task AViewerWithNothingToSayIsLeftAfterTheBoundAndAskedAgainNextTime() => WpfApp.RunAsync(async () =>
    {
        using var viewer = await Viewer.ShowAsync(pages: 2);
        var looks = 0;

        Assert.Null(await viewer.AskAsync(_ =>
        {
            Interlocked.Increment(ref looks);
            return null;
        }));

        // It was looked at more than once, and it came back each time: it is not given up on.
        Assert.True(looks > 1);
        Assert.False(Fields.Get<bool>(viewer.Rail, "ViewerSilent"));
        Assert.Equal(4, await viewer.AskAsync(_ => 4));
    });

    [Fact]
    public Task OneViewerIsAskedOneQuestionAtATime() => WpfApp.RunAsync(async () =>
    {
        using var viewer = await Viewer.ShowAsync(pages: 2);
        using var held = new ManualResetEventSlim();
        var looking = 0;
        var most = 0;
        int? Look(int answer)
        {
            var now = Interlocked.Increment(ref looking);
            InterlockedMax(ref most, now);
            held.Wait(TimeSpan.FromSeconds(20));
            Interlocked.Decrement(ref looking);
            return answer;
        }

        // The page sync, Fill & Sign and the Office swap can all ask at once.
        var first = viewer.AskAsync(_ => Look(1));
        var second = viewer.AskAsync(_ => Look(2));
        var third = viewer.AskAsync(_ => Look(3));

        // Long enough for a second look to start, were one let through.
        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref looking));
        held.Set();

        Assert.Equal(new int?[] { 1, 2, 3 }, new[] { await first, await second, await third });
        Assert.Equal(1, most);
    });

    [Fact]
    public Task AViewerThatKeepsAQuestionIsNotAskedAgain() => WpfApp.RunAsync(async () =>
    {
        using var viewer = await Viewer.ShowAsync(pages: 2);
        using var kept = new ManualResetEventSlim();
        try
        {
            // A second question was already waiting when the first one blocked.
            var blocked = viewer.AskAsync(_ =>
            {
                kept.Wait(TimeSpan.FromSeconds(20));
                return 1;
            });
            var looks = 0;
            var waiting = viewer.AskAsync(_ => Interlocked.Increment(ref looks));

            Assert.Null(await blocked);
            Assert.Null(await waiting);
            Assert.Null(await viewer.AskAsync(_ => Interlocked.Increment(ref looks)));
            Assert.Equal(0, looks);
            Assert.True(Fields.Get<bool>(viewer.Rail, "ViewerSilent"));
        }
        finally
        {
            kept.Set();
        }
    });

    [Fact]
    public Task AnAnswerAboutAViewerSinceReplacedIsNoAnswer() => WpfApp.RunAsync(async () =>
    {
        using var viewer = await Viewer.ShowAsync(pages: 2);
        using var held = new ManualResetEventSlim();
        var shown = Fields.Get<WebView2>(viewer.Rail, "WebView");
        try
        {
            var asked = viewer.AskAsync(_ =>
            {
                held.Wait(TimeSpan.FromSeconds(20));
                return 1;
            });

            // A jump or the whole document swaps a new viewer in meanwhile.
            Fields.Set(viewer.Rail, "WebView", new WebView2());
            held.Set();

            Assert.Null(await asked);
        }
        finally
        {
            Fields.Set(viewer.Rail, "WebView", shown);
        }
    });

    /// <summary>
    /// The rule lives in the asker, so nothing else may walk the viewer's
    /// tree. In 3.0.0 the page sync kept to one question at a time, while
    /// Fill &amp; Sign's read and the check after a jump each started a
    /// call of their own. The Office swap then came with a loop of its own.
    /// </summary>
    [Fact]
    public void OnlyTheAskerLooksAtTheViewer()
    {
        var source = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var looks = new[] { "LookForPdfPage", "LookForPdfPagesAtOpening" };

        // The walk of the tree is in the two looks and nowhere else.
        var walks = Regex.Matches(source, @"TreeScope\.Descendants").Count;
        Assert.Equal(
            walks,
            looks.Sum(look => Regex.Matches(TestSource.MethodFrom(source, look), @"TreeScope\.Descendants").Count));
        Assert.Equal(3, walks);

        // A look is named where it is written and where the asker is handed it.
        foreach (var look in looks)
        {
            var named = source.Split('\n').Where(line => Regex.IsMatch(line, $@"\b{look}\b")).ToList();
            Assert.Equal(2, named.Count);
            Assert.Single(named, line => line.Contains("private static", StringComparison.Ordinal));
            Assert.Single(named, line => line.Contains("AskPdfViewerAsync", StringComparison.Ordinal));
        }

        // And only the asker runs one, after its turn has come.
        var turn = source.IndexOf("await state.ViewerTurn.WaitAsync();", StringComparison.Ordinal);
        Assert.True(turn >= 0 && turn < source.IndexOf("look(hwnd)", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(source, @"\blook\(hwnd\)"));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    /// <summary>A PDF in a real viewer, on a window that stays off screen.</summary>
    private sealed class Viewer : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public PreviewWindow Window { get; private set; } = null!;
        public object Rail { get; private set; } = null!;

        public static async Task<Viewer> ShowAsync(int pages)
        {
            var viewer = new Viewer();
            var path = Path.Combine(viewer._temp.Path, "pages.pdf");
            FillSignBehaviorTests.WritePdf(path, 612, 792, pages);
            viewer.Window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
            viewer.Window.MarkGeometryUserPinnedForProbe();
            viewer.Window.ShowPreview(path, IntPtr.Zero);
            viewer.Rail = await UntilAsync(
                () => Fields.Get<bool>(viewer.Window, "_loadFinished") ? Fields.Get<object?>(viewer.Window, "_activePdfRail") : null,
                "The PDF never showed.");

            // The rail's count comes from a helper this host may not start.
            if (Fields.Get<object?>(viewer.Rail, "Document") is null)
            {
                Fields.Set(viewer.Rail, "Document", new PdfPreviewDocument([], pages));
            }

            return viewer;
        }

        /// <summary>The wheel turned over the pages until they have moved and come to rest.</summary>
        public Task WheelAsync(int distance) =>
            PdfViewerWheel.ScrollAsync(Fields.Get<WebView2>(Rail, "WebView").CoreWebView2, distance);

        /// <summary>A question put through the product's one asker, with a look of the test's own.</summary>
        public Task<int?> AskAsync(Func<IntPtr, int?> look) =>
            (Task<int?>)typeof(PreviewWindow)
                .GetMethod("AskPdfViewerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(typeof(int))
                .Invoke(Window, [Rail, look])!;

        public async Task<(int Page, int TotalPages)> ReadPositionAsync()
        {
            var read = (Task)Fields.Call(Window, "ReadPdfPositionAsync")!;
            await read;
            return ((int Page, int TotalPages))read.GetType().GetProperty("Result")!.GetValue(read)!;
        }

        /// <summary>What the viewer's own page box says, asked until it answers.</summary>
        public async Task<int> PageBoxAsync()
        {
            var hwnd = new WindowInteropHelper(Window).Handle;
            var box = await Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (true)
                {
                    foreach (AutomationElement edit in AutomationElement.FromHandle(hwnd).FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
                    {
                        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                            && int.TryParse(((ValuePattern)pattern).Current.Value, out var page))
                        {
                            return page;
                        }
                    }

                    Assert.True(DateTime.UtcNow < deadline, "The viewer never showed a page box.");
                    await Task.Delay(50);
                }
            });
            return box;
        }

        public void Dispose()
        {
            Window.Close();
            _temp.Dispose();
        }
    }

    private static async Task<T> UntilAsync<T>(Func<T?> value, string failure)
        where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            if (value() is { } found)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(20);
        }
    }
}
