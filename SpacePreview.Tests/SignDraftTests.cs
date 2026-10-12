using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// Unsaved signing work survives Glimpse ending without the user choosing:
/// it comes back on its pages while the file is the one it was placed on,
/// never on a changed file, and only with the user's own ink as it was.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SignDraftTests
{
    private static readonly IReadOnlyList<IReadOnlyList<Point>> Strokes =
        [new List<Point> { new(0, 0), new(60, 20), new(120, 0) }];

    // ------------------------------------------------------------------
    // The draft file.
    // ------------------------------------------------------------------

    [Fact]
    public void WorkComesBackOnItsPagesWhileTheFileIsUnchanged() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        var png = SignatureStore.TryReadForPlacement(signature, out var source)!;
        SignPlacement[] placements =
        [
            new(SignPlacementKind.Date, 2, 0.4, 0.2, 0.14, 0.028, "10/10/2026"),
            new(SignPlacementKind.Signature, 0, 0.3, 0.7, 0.24, 0.05, null, png, source)
        ];

        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 2, placements);
        var (file, draft) = SignDrafts.Load(pdf);

        Assert.Equal(FileStamp.Of(pdf), file);
        Assert.NotNull(draft);
        Assert.Equal(2, draft!.PageIndex);
        Assert.Equal(placements.Select(Shape), draft.Placements.Select(Shape));
        Assert.Equal(png, draft.Placements[1].ImagePng);
    });

    [Fact]
    public void ADraftIsNeverAppliedToAChangedFile() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        WriteDateDraft(pdf);
        File.AppendAllText(pdf, " ");

        Assert.Null(SignDrafts.Load(pdf).Draft);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));

        // The same length, saved again: a new version all the same.
        WriteDateDraft(pdf);
        File.SetLastWriteTimeUtc(pdf, File.GetLastWriteTimeUtc(pdf).AddSeconds(2));

        Assert.Null(SignDrafts.Load(pdf).Draft);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void PlacementsWhoseInkIsGoneAreLetGo() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        File.WriteAllBytes(SignatureStore.StampPath, SolidPng(40, 20, 0));
        var signaturePng = SignatureStore.TryReadForPlacement(signature, out var signatureSource)!;
        var stampPng = SignatureStore.TryReadForPlacement(SignatureStore.StampPath, out var stampSource)!;
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0,
        [
            new SignPlacement(SignPlacementKind.Signature, 0, 0.1, 0.1, 0.24, 0.05, null, signaturePng, signatureSource),
            new SignPlacement(SignPlacementKind.Stamp, 0, 0.5, 0.5, 0.18, 0.09, null, stampPng, stampSource)
        ]);

        // The user removed the signature: its placement goes, the stamp stays.
        SignatureStore.Remove(signature);
        Assert.Equal([SignPlacementKind.Stamp], SignDrafts.Load(pdf).Draft!.Placements.Select(p => p.Kind));

        // A new stamp under the same name is not the one that was placed.
        File.WriteAllBytes(SignatureStore.StampPath, SolidPng(40, 20, 255));
        Assert.Null(SignDrafts.Load(pdf).Draft);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void InkRedrawnOnThePadComesBackAsDrawn() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        SignatureStore.TryReadForPlacement(signature, out var source);
        var redrawn = SolidPng(30, 10, 0);
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0,
        [
            new SignPlacement(SignPlacementKind.Signature, 0, 0.1, 0.1, 0.24, 0.08, null, redrawn, source! with { Redrawn = true })
        ]);

        var placed = SignDrafts.Load(pdf).Draft!.Placements.Single();
        Assert.Equal(redrawn, placed.ImagePng);
        Assert.True(placed.Source!.Redrawn);

        // It was drawn over that signature; removing the signature takes it too.
        SignatureStore.Remove(signature);
        Assert.Null(SignDrafts.Load(pdf).Draft);
    });

    [Fact]
    public void ADraftUntouchedFor30DaysIsAbandoned() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        WriteDateDraft(pdf);
        File.SetLastWriteTimeUtc(SignDrafts.PathFor(pdf), DateTime.UtcNow.AddDays(-29));
        Assert.NotNull(SignDrafts.Load(pdf).Draft);

        File.SetLastWriteTimeUtc(SignDrafts.PathFor(pdf), DateTime.UtcNow.AddDays(-31));
        Assert.Null(SignDrafts.Load(pdf).Draft);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void ADraftOnlyEverPointsIntoTheDataRoot() => InDataRoot(root =>
    {
        using var elsewhere = new TempDirectory();
        var pdf = WritePdf(root);
        var draftPath = SignDrafts.PathFor(pdf);
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        var outside = Path.Combine(elsewhere.Path, "someone-else.png");
        File.Copy(signature, outside);

        // Ink from outside the data root is never referenced.
        var png = SignatureStore.TryReadForPlacement(outside, out var foreign)!;
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0,
        [
            new SignPlacement(SignPlacementKind.Stamp, 0, 0.1, 0.1, 0.2, 0.1, null, png, foreign),
            Date(0)
        ]);

        Assert.DoesNotContain("someone-else", File.ReadAllText(draftPath), StringComparison.Ordinal);
        Assert.Equal([SignPlacementKind.Date], SignDrafts.Load(pdf).Draft!.Placements.Select(p => p.Kind));

        // A draft edited by hand to point there is not followed, though
        // the file there has the very bytes the draft expects.
        SignatureStore.TryReadForPlacement(signature, out var own);
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0,
        [
            new SignPlacement(SignPlacementKind.Signature, 0, 0.1, 0.1, 0.2, 0.1, null, png, own)
        ]);
        var json = File.ReadAllText(draftPath);
        var inside = JsonPath(Path.GetRelativePath(root, signature));
        Assert.Contains(inside, json, StringComparison.Ordinal);
        File.WriteAllText(draftPath, json.Replace(inside, JsonPath(Path.GetRelativePath(root, outside)), StringComparison.Ordinal));

        Assert.Null(SignDrafts.Load(pdf).Draft);
    });

    [Fact]
    public void ADamagedDraftIsLetGoWithoutTakingSigningDown() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var draftPath = SignDrafts.PathFor(pdf);
        WriteDateDraft(pdf);
        File.WriteAllText(draftPath, File.ReadAllText(draftPath).Replace("\"Placements\":[", "\"Placements\":[null,", StringComparison.Ordinal));
        Assert.Single(SignDrafts.Load(pdf).Draft!.Placements);

        File.WriteAllText(draftPath, "{\"Path\":");
        Assert.Null(SignDrafts.Load(pdf).Draft);
        Assert.False(File.Exists(draftPath));
    });

    // ------------------------------------------------------------------
    // Keeping the draft as the user works.
    // ------------------------------------------------------------------

    [Fact]
    public void ChangesReachTheDraftAfterAPauseAndAtOnceWhenTheAppEnds() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var keeper = new SignDraftKeeper(pdf, FileStamp.Of(pdf)!.Value, 0, []);

        keeper.Update(0, [Date(0)]);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
        Assert.True(SpinWait.SpinUntil(() => File.Exists(SignDrafts.PathFor(pdf)), TimeSpan.FromSeconds(5)));
        Assert.Single(SignDrafts.Load(pdf).Draft!.Placements);

        keeper.Update(4, [Date(0), Date(4)]);
        SignDraftKeeper.FlushAll();
        var draft = SignDrafts.Load(pdf).Draft!;
        Assert.Equal(4, draft.PageIndex);
        Assert.Equal(2, draft.Placements.Count);
        keeper.Discard();
    });

    [Fact]
    public void DiscardLeavesNoDraftEvenWithAWriteWaiting() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var keeper = new SignDraftKeeper(pdf, FileStamp.Of(pdf)!.Value, 0, []);
        keeper.Update(0, [Date(0)]);
        SignDraftKeeper.FlushAll();
        Assert.True(File.Exists(SignDrafts.PathFor(pdf)));

        keeper.Update(0, [Date(0), Date(0)]);
        keeper.Discard();
        Thread.Sleep(SignDraftKeeper.Pause * 3);
        SignDraftKeeper.FlushAll();

        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void RemovingTheLastPlacementRemovesTheDraft() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var keeper = new SignDraftKeeper(pdf, FileStamp.Of(pdf)!.Value, 0, []);
        keeper.Update(0, [Date(0)]);
        SignDraftKeeper.FlushAll();

        keeper.Update(1, []);
        SignDraftKeeper.FlushAll();

        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
        keeper.Discard();
    });

    [Fact]
    public void AWindowThatPlacedNothingNeverErasesAnothersDraft() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var stamp = FileStamp.Of(pdf)!.Value;
        var working = new SignDraftKeeper(pdf, stamp, 0, []);
        working.Update(0, [Date(0)]);
        SignDraftKeeper.FlushAll();

        // A second window on the same PDF turns a page and leaves.
        var looking = new SignDraftKeeper(pdf, stamp, 0, []);
        looking.Update(3, []);
        SignDraftKeeper.FlushAll();
        looking.Discard();

        Assert.NotNull(SignDrafts.Load(pdf).Draft);
        working.Discard();
    });

    [Fact]
    public void TheAppsEndPutsBackWorkAnotherWindowThrewAway() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var stamp = FileStamp.Of(pdf)!.Value;
        var working = new SignDraftKeeper(pdf, stamp, 0, []);
        working.Update(0, [Date(0)]);
        SignDraftKeeper.FlushAll();

        // A second window resumes that draft and discards it, while the
        // first still has the work on screen.
        var resumed = new SignDraftKeeper(pdf, stamp, 0, SignDrafts.Load(pdf).Draft!.Placements);
        resumed.Discard();
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));

        SignDraftKeeper.FlushAll();
        Assert.Single(SignDrafts.Load(pdf).Draft!.Placements);

        // A session that ended is never written again.
        working.Close();
        File.Delete(SignDrafts.PathFor(pdf));
        SignDraftKeeper.FlushAll();
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void ASessionClosedBeforeItsSaveFinishedStillTakesItsDraftOnce() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var stamp = FileStamp.Of(pdf)!.Value;
        var keeper = new SignDraftKeeper(pdf, stamp, 0, []);
        keeper.Update(0, [Date(0)]);

        // The next item took the session's work to its draft; then its save landed.
        keeper.Close();
        Assert.True(File.Exists(SignDrafts.PathFor(pdf)));
        keeper.Discard();
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));

        // Once: a draft another window keeps later is not this session's.
        WriteDateDraft(pdf);
        keeper.Discard();
        Assert.True(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void AWriteDueAsTheSessionEndsNeverBringsTheDraftBack() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var keeper = new SignDraftKeeper(pdf, FileStamp.Of(pdf)!.Value, 0, []);
        keeper.Update(0, [Date(0)]);
        SignDraftKeeper.FlushAll();

        // The next change's pause runs out while Discard is deleting the draft.
        var writing = Fields.Get<object>(keeper, "_writing");
        Monitor.Enter(writing);
        try
        {
            keeper.Update(0, [Date(0), Date(0)]);
            Thread.Sleep(SignDraftKeeper.Pause * 3);
            keeper.Discard();
        }
        finally
        {
            Monitor.Exit(writing);
        }

        Thread.Sleep(SignDraftKeeper.Pause);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void TheAppsEndWaitsForADraftWriteAlreadyUnderWay() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var keeper = new SignDraftKeeper(pdf, FileStamp.Of(pdf)!.Value, 0, []);
        var disk = typeof(SignDrafts).GetField("Io", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var writing = Fields.Get<object>(keeper, "_writing");
        Task flush;
        Monitor.Enter(disk);
        try
        {
            // The first change's write starts after its pause and stalls on the disk.
            keeper.Update(0, [Date(0)]);
            Assert.True(SpinWait.SpinUntil(() => HeldElsewhere(writing), TimeSpan.FromSeconds(5)));

            flush = Task.Run(SignDraftKeeper.FlushAll);
            Assert.False(flush.Wait(TimeSpan.FromMilliseconds(300)), "the app's end went ahead of a write still under way");
        }
        finally
        {
            Monitor.Exit(disk);
        }

        Assert.True(flush.Wait(TimeSpan.FromSeconds(5)));
        Assert.Single(SignDrafts.Load(pdf).Draft!.Placements);
        keeper.Discard();
    });

    // ------------------------------------------------------------------
    // The surface and the header.
    // ------------------------------------------------------------------

    [Fact]
    public void EveryChangeOnTheSurfaceReachesTheDraft() => InDataRoot(_ => Sta.Run(() =>
    {
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        var surface = new FillSignSurface(0, 2, BlankPage());
        var changes = 0;
        surface.WorkChanged += () => changes++;

        Assert.True(surface.Place(SignPlacementKind.Signature, 0.5, 0.5));
        Assert.Equal(1, changes);
        Assert.Equal(signature, surface.Placements.Single().Source!.File);

        var frame = Frames(surface).Single();
        frame.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.MouseWheelEvent });
        Assert.Equal(2, changes);

        Fields.Call(surface, "UpdatePlacementImage", frame, SignatureStore.TryLoad(SolidPng(30, 10, 0))!);
        Assert.Equal(3, changes);
        Assert.True(surface.Placements.Single().Source!.Redrawn);

        surface.ShowPage(1, BlankPage());
        Assert.Equal(4, changes);

        surface.ShowPage(0, BlankPage());
        Click(Descendants(surface).OfType<Border>().Single(border => border.ToolTip as string == "Remove"), Frames(surface).Single());
        Assert.Equal(6, changes);
        Assert.False(surface.HasWork);
    }));

    [Fact]
    public void RestoredWorkComesBackOnItsPagesAndSaysSoOnce() => InDataRoot(_ => Sta.Run(() =>
    {
        var surface = new FillSignSurface(0, 2, BlankPage());
        var changes = 0;
        surface.WorkChanged += () => changes++;

        surface.Restore([Date(0), Date(1)]);

        Assert.Equal(0, changes);
        Assert.Equal(2, surface.Placements.Count);
        Assert.Single(Frames(surface));
        Assert.True(Descendants(surface).OfType<ButtonBase>().Single(button => button.Content as string == "Save").IsEnabled);
        Assert.Contains(Descendants(surface).OfType<TextBlock>(), block => block.Text == "Your earlier work is back.");

        surface.ShowPage(1, BlankPage());
        Assert.Single(Frames(surface));
        Assert.DoesNotContain(Descendants(surface).OfType<TextBlock>(), block => block.Text == "Your earlier work is back.");
    }));

    [Fact]
    public Task TheSignPillOffersToResumeAtTheSameWidth() => InWindow(async (root, window) =>
    {
        var pdf = WritePdf(root);
        var version = Fields.Get<long>(window, "_loadVersion");
        Fields.Call(window, "ShowTool", HeaderTool.Sign);
        Fields.Set(window, "_fillSignPath", pdf);
        await (Task)Fields.Call(window, "FindSignDraftAsync", pdf, version)!;
        Assert.Equal("Fill and sign", AutomationProperties.GetName(window.SignButton));
        Assert.Equal(Visibility.Visible, window.SignLabel.Visibility);
        var signWidth = Width(window.SignButton);

        WriteDateDraft(pdf);
        await (Task)Fields.Call(window, "FindSignDraftAsync", pdf, version)!;

        // The pen says what is resumed; the name says it in full.
        Assert.Equal("Resume signing", AutomationProperties.GetName(window.SignButton));
        Assert.Equal("Resume signing", window.SignButton.ToolTip);
        Assert.Equal(Visibility.Visible, window.ResumeLabel.Visibility);
        Assert.Equal("Resume", window.ResumeLabel.Children.OfType<TextBlock>().Last().Text);
        Assert.Equal(Visibility.Hidden, window.SignLabel.Visibility);
        Assert.Equal(signWidth, Width(window.SignButton));
    });

    // ------------------------------------------------------------------
    // The window: sign mode entered the way the Sign pill enters it.
    // ------------------------------------------------------------------

    [Fact]
    public Task ResumingBringsTheWorkBackOnThePageItWasLeftOn() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root, pages: 3);
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 2, [Date(0), Date(2)]);
        var rendered = new List<int>();

        var surface = await EnterAsync(window, pdf, rendered);

        Assert.Equal(2, rendered.First());
        Assert.Equal(2, surface.PageIndex);
        Assert.Equal([0, 2], surface.Placements.Select(placement => placement.PageIndex).Order());
        Assert.Single(Frames(surface));
        Assert.Contains(Descendants(surface).OfType<TextBlock>(), block => block.Text == "Your earlier work is back.");
    });

    [Fact]
    public Task TheLastPageAskedForIsThePageShownAndKept() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root, pages: 3);
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 1, [Date(1)]);
        var slow = new Dictionary<int, TaskCompletionSource<PdfPreviewPage?>> { [0] = new(), [2] = new() };
        var surface = await EnterAsync(window, pdf, render: (index, _) =>
            slow.TryGetValue(index, out var page) ? page.Task : Task.FromResult<PdfPreviewPage?>(BlankPage()));
        Assert.Equal(1, surface.PageIndex);

        // Next, then Previous; the page asked for first arrives last.
        Fields.Get<Button>(surface, "_nextPageChip").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Fields.Get<Button>(surface, "_previousPageChip").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        slow[0].SetResult(BlankPage());
        await SettleAsync(window);
        slow[2].SetResult(BlankPage());
        await SettleAsync(window);

        Assert.Equal(0, surface.PageIndex);
        SignDraftKeeper.FlushAll();
        Assert.Equal(0, SignDrafts.Load(pdf).Draft!.PageIndex);
    });

    [Theory]
    [InlineData("Discard")]
    [InlineData("close")]
    [InlineData("next item")]
    public Task OneFileHasOneSigningSessionAcrossWindows(string end) => InDataRootAsync(async root =>
    {
        var first = new PreviewWindow();
        var second = new PreviewWindow();
        try
        {
            var pdf = SignablePdf(root);
            var working = await EnterAsync(first, pdf);
            working.Place(SignPlacementKind.Date, 0.5, 0.5);

            // A pinned window and a fresh glance on the same PDF: the second
            // asks to sign and the first comes forward instead.
            Assert.Null(await EnterAsync(second, pdf));
            Assert.Null(Fields.Get<SignDraftKeeper?>(second, "_signDraft"));
            Assert.True(await AppEndTests.LogEventuallyContainsAsync("fill-sign is open in another window"));
            SignDraftKeeper.FlushAll();
            Assert.Single(SignDrafts.Load(pdf).Draft!.Placements);

            // However the first session ends, the file is free to sign again.
            switch (end)
            {
                case "Discard":
                    working.ShowSaveChoice();
                    Press(working, "Discard");
                    break;
                case "close":
                    first.Close();
                    break;
                default:
                    Fields.Set(first, "_loadVersion", Fields.Get<long>(first, "_loadVersion") + 1);
                    Fields.Call(first, "ResetForNewItem");
                    break;
            }

            Assert.NotNull(await EnterAsync(second, pdf));
        }
        finally
        {
            second.Close();
            if (end != "close")
            {
                first.Close();
            }
        }
    });

    [Fact]
    public Task AClosedWindowHoldsNoSigningSession() => InDataRootAsync(async root =>
    {
        var window = new PreviewWindow();
        var sessions = (System.Collections.IDictionary)typeof(PreviewWindow)
            .GetField("SignSessions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var first = SignablePdf(root);
        var second = Path.Combine(root, "docs", "lease.pdf");
        FillSignBehaviorTests.WritePdf(second, 612, 792);

        // Signs one file, moves on to the next and signs that, then closes.
        await EnterAsync(window, first);
        Fields.Set(window, "_loadVersion", Fields.Get<long>(window, "_loadVersion") + 1);
        Fields.Call(window, "ResetForNewItem");
        window.PreviewContent.Content = null;
        Assert.NotNull(await EnterAsync(window, second));
        window.Close();

        Assert.False(sessions.Contains(SignDrafts.Canonical(first)));
        Assert.False(sessions.Contains(SignDrafts.Canonical(second)));
    });

    [Fact]
    public Task AnEntryThatFailedLeavesTheFileFreeToSign() => InDataRootAsync(async root =>
    {
        var first = new PreviewWindow();
        var second = new PreviewWindow();
        try
        {
            var pdf = SignablePdf(root);

            // Since 3.0.1 a page that will not open leaves a sheet that says
            // so, where it left nothing. It holds no work, and no session.
            var failed = await EnterAsync(first, pdf, render: (_, _) => Task.FromResult<PdfPreviewPage?>(null));
            Assert.Equal(Visibility.Collapsed, Fields.Get<StackPanel>(failed, "_tools").Visibility);
            Assert.Null(Fields.Get<SignDraftKeeper?>(first, "_signDraft"));

            var working = await EnterAsync(second, pdf);
            Assert.NotNull(working);
            Assert.True(Button(working, "Date").IsEnabled);
        }
        finally
        {
            second.Close();
            first.Close();
        }
    });

    [Theory]
    [InlineData("leave")]
    [InlineData("close")]
    [InlineData("next item")]
    public Task EveryEndOfSignModeStopsItsPageHelpers(string end) => InDataRootAsync(async root =>
    {
        var window = new PreviewWindow();
        try
        {
            // The load's own cancellation: a new item or a hide cancels it.
            var load = new CancellationTokenSource();
            Fields.Set(window, "_loadCancellation", load);
            var pdf = SignablePdf(root, pages: 3);
            var helpers = new List<CancellationToken>();
            var stuck = new TaskCompletionSource<PdfPreviewPage?>();
            var surface = await EnterAsync(window, pdf, render: (index, token) =>
            {
                helpers.Add(token);
                return index == 0 ? Task.FromResult<PdfPreviewPage?>(BlankPage()) : stuck.Task;
            });

            // The page on screen, and the next one rendering ahead.
            Assert.Equal(2, helpers.Count);
            Assert.DoesNotContain(helpers, token => token.IsCancellationRequested);

            switch (end)
            {
                case "leave":
                    surface.HandleEscape();
                    break;
                case "close":
                    window.Close();
                    break;
                default:
                    load.Cancel();
                    break;
            }

            Assert.All(helpers, token => Assert.True(token.IsCancellationRequested, "a page helper outlived its session"));
        }
        finally
        {
            if (end != "close")
            {
                window.Close();
            }
        }
    });

    [Theory]
    [InlineData("Discard")]
    [InlineData("Save a signed copy")]
    [InlineData("Replace the original")]
    public Task EveryWayOfFinishingTakesTheDraftWithIt(string choice) => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0, [Date(0)]);
        var surface = await EnterAsync(window, pdf);
        Assert.Single(surface.Placements);

        surface.ShowSaveChoice();
        Press(surface, choice);
        // A finished save shows its file next, in a viewer a test never starts.
        Fields.Set(window, "_loadVersion", Fields.Get<long>(window, "_loadVersion") + 1);

        Assert.True(await EventuallyAsync(() => !File.Exists(SignDrafts.PathFor(pdf))), "the draft outlived " + choice);
    });

    [Fact]
    public Task AFailedSaveKeepsTheWorkForItsDraft() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);

        // A folder holds the copy's name, so the copy cannot be written.
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(pdf)!, "contract (signed).pdf"));
        surface.ShowSaveChoice();
        Press(surface, "Save a signed copy");
        var status = Fields.Get<TextBlock>(surface, "_saveStatus");
        Assert.True(await EventuallyAsync(() => status.Text.Length > 0 && status.Text != "Saving\u2026"));

        SignDraftKeeper.FlushAll();
        Assert.Single(SignDrafts.Load(pdf).Draft!.Placements);
    });

    [Fact]
    public Task TwoCopiesSavedAtOnceGetTwoNames() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        var folder = Path.GetDirectoryName(pdf)!;
        var first = Path.Combine(folder, "contract (signed).pdf");

        // Another save takes the first free name between this one's look and its write.
        var written = new List<string>();
        window.WriteSignedPdf = (source, output, placements) =>
        {
            if (!File.Exists(first))
            {
                File.WriteAllText(first, "the other save's copy");
            }

            PdfFillSignWriter.WriteSignedCopy(source, output, placements);
            lock (written)
            {
                written.Add(output);
            }
        };
        surface.ShowSaveChoice();
        Press(surface, "Save a signed copy");
        // A finished save shows its file next, in a viewer a test never starts.
        Fields.Set(window, "_loadVersion", Fields.Get<long>(window, "_loadVersion") + 1);

        Assert.True(await EventuallyAsync(() =>
        {
            lock (written)
            {
                return written.Count > 0;
            }
        }));
        var second = Path.Combine(folder, "contract (signed 2).pdf");
        Assert.Equal([second], written);
        Assert.Equal("the other save's copy", File.ReadAllText(first));
        Assert.Contains("/Subtype /FreeText", Latin1(second), StringComparison.Ordinal);
    });

    [Fact]
    public Task TwoReplacesAtOnceEachUseTheirOwnSignedFile() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        var keeper = Fields.Get<SignDraftKeeper>(window, "_signDraft");
        using var firstStaged = new ManualResetEventSlim();
        using var firstGo = new ManualResetEventSlim();
        using var secondStaged = new ManualResetEventSlim();
        using var secondGo = new ManualResetEventSlim();
        window.WriteSignedPdf = (source, output, placements) =>
        {
            PdfFillSignWriter.WriteSignedCopy(source, output, placements);
            var (staged, go) = placements.Count == 1 ? (firstStaged, firstGo) : (secondStaged, secondGo);
            staged.Set();
            go.Wait(TimeSpan.FromSeconds(10));
        };

        // Two saves of different work, each written and waiting to swap.
        SignPlacement[] one = [Date(0) with { Text = "AAA" }];
        SignPlacement[] two = [Date(0) with { Text = "BBB" }, Date(0) with { Text = "BBB" }];
        var firstSave = (Task)Fields.Call(window, "SaveFillSignAsync", surface, keeper, one, true)!;
        Assert.True(await EventuallyAsync(() => firstStaged.IsSet));
        var secondSave = (Task)Fields.Call(window, "SaveFillSignAsync", surface, keeper, two, true)!;
        Assert.True(await EventuallyAsync(() => secondStaged.IsSet));
        Fields.Set(window, "_loadVersion", Fields.Get<long>(window, "_loadVersion") + 1);

        firstGo.Set();
        await firstSave;
        Assert.Contains("(AAA)", Latin1(pdf), StringComparison.Ordinal);
        Assert.DoesNotContain("(BBB)", Latin1(pdf), StringComparison.Ordinal);

        // The second was made from the file before the first landed, so it
        // is refused, and the first save's file stays.
        secondGo.Set();
        await secondSave;
        Assert.Contains("(AAA)", Latin1(pdf), StringComparison.Ordinal);
        Assert.DoesNotContain("(BBB)", Latin1(pdf), StringComparison.Ordinal);
        Assert.Equal([pdf], Directory.GetFiles(Path.GetDirectoryName(pdf)!));
    });

    [Theory]
    [InlineData("Save a signed copy")]
    [InlineData("Replace the original")]
    public Task ASaveRefusesAPdfThatChangedSinceSigningBegan(string choice) => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);

        // Another app saves a different one-page document under the same name.
        FillSignBehaviorTests.WritePdf(pdf, 595.5, 842);
        var newer = File.ReadAllBytes(pdf);

        surface.ShowSaveChoice();
        Press(surface, choice);

        await RefusedAsync(window, surface, pdf, () => newer);
    });

    [Fact]
    public Task AnEditSavedWhileTheSignedFileIsWrittenIsKept() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        byte[] newer = [];
        window.WriteSignedPdf = (source, output, placements) =>
        {
            PdfFillSignWriter.WriteSignedCopy(source, output, placements);
            // Another app saves its edit after the signed file is written, before the swap.
            FillSignBehaviorTests.WritePdf(source, 595.5, 842);
            newer = File.ReadAllBytes(source);
        };

        surface.ShowSaveChoice();
        Press(surface, "Replace the original");

        await RefusedAsync(window, surface, pdf, () => newer);
    });

    [Fact]
    public Task AFileThatCannotBeReadRightNowKeepsItsDraft() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0, [Date(0)]);
        FillSignSurface surface;
        using (new FileStream(pdf, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            surface = await EnterAsync(window, pdf);
        }

        // Nothing comes back into a surface that cannot save it, nothing is
        // kept from it, and the draft waits.
        Assert.Contains("can't be read right now", Fields.Get<TextBlock>(surface, "_centerText").Text, StringComparison.Ordinal);
        Assert.Empty(surface.Placements);
        Assert.Null(Fields.Get<SignDraftKeeper?>(window, "_signDraft"));
        Assert.True(File.Exists(SignDrafts.PathFor(pdf)));

        // Leaving offers it again, and once the file reads, the work is back.
        surface.HandleEscape();
        Assert.True(await EventuallyAsync(() => window.ResumeLabel.Visibility == Visibility.Visible));
        surface = await EnterAsync(window, pdf);
        Assert.Single(surface.Placements);
    });

    [Fact]
    public Task ClosingTheWindowWritesTheWorkAtOnce() => InDataRootAsync(async root =>
    {
        var window = new PreviewWindow();
        var pdf = SignablePdf(root);
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));

        window.Close();

        Assert.True(File.Exists(SignDrafts.PathFor(pdf)));
        // A normal shutdown closes windows before OnExit; this is its log line.
        Assert.True(await AppEndTests.LogEventuallyContainsAsync("window close kept unsaved signing work as a draft"));
    });

    [Fact]
    public Task AWindowClosedWhileSignModeOpensLeavesNoSession() => InDataRootAsync(async root =>
    {
        var window = new PreviewWindow();
        var pdf = SignablePdf(root);
        WriteDateDraft(pdf);
        var page = new TaskCompletionSource<PdfPreviewPage?>();
        Fields.Set(window, "CurrentPath", pdf);
        Fields.Set(window, "_fillSignPath", pdf);
        window.PreviewContent.Content ??= new Border();
        window.RenderSignPage = (_, _, _) => page.Task;

        var entering = (Task)Fields.Call(window, "EnterFillSignModeAsync")!;
        window.Close();
        page.SetResult(BlankPage());
        await entering;

        Assert.Null(Fields.Get<object?>(window, "_fillSignSurface"));
        Assert.Null(Fields.Get<object?>(window, "_signDraft"));

        // The draft is finished elsewhere; the app's end must not bring it back.
        SignDrafts.Delete(pdf);
        Assert.False(SignDraftKeeper.FlushAll());
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public Task TheToolsWaitForTheFileCheckSoEveryMarkHasADraft() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        using var checking = new ManualResetEventSlim();
        window.CheckSignable = path =>
        {
            checking.Wait(TimeSpan.FromSeconds(10));
            return PdfFillSignWriter.Preflight(path);
        };

        var entering = EnterAsync(window, pdf);
        Assert.True(await EventuallyAsync(() => Fields.Get<object?>(window, "_fillSignSurface") is not null));
        var surface = Fields.Get<FillSignSurface>(window, "_fillSignSurface");

        // The page is up, but nothing goes on it before a draft can keep it.
        Assert.False(Button(surface, "Signature").IsEnabled);
        Assert.False(Button(surface, "Date").IsEnabled);
        Assert.False(Button(surface, "Stamp").IsEnabled);
        Assert.False(Button(surface, "Save").IsEnabled);
        Assert.True(Button(surface, "Cancel").IsEnabled);

        checking.Set();
        await entering;
        Assert.True(Button(surface, "Date").IsEnabled);

        // The very first mark is kept, even if the app ends at once.
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        Assert.True(SignDraftKeeper.FlushAll());
        Assert.Single(SignDrafts.Load(pdf).Draft!.Placements);
    });

    [Fact]
    public Task AFileWithNoStampToPinADraftToOffersNoTools() => InWindow(async (root, window) =>
    {
        // Gone when the draft looked, back by the time the writer read it.
        var pdf = Path.Combine(root, "docs", "contract.pdf");
        window.CheckSignable = _ => new PdfSignCheck(1, null);

        var surface = await EnterAsync(window, pdf);

        Assert.Contains("can't be read right now", Fields.Get<TextBlock>(surface, "_centerText").Text, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, Fields.Get<StackPanel>(surface, "_tools").Visibility);
        Assert.Null(Fields.Get<SignDraftKeeper?>(window, "_signDraft"));
    });

    [Fact]
    public Task TheNextItemTakesUnsavedWorkToItsDraft() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);

        Fields.Call(window, "ResetForNewItem");
        Assert.True(File.Exists(SignDrafts.PathFor(pdf)));

        // That session is over: the app's end never writes it again.
        File.Delete(SignDrafts.PathFor(pdf));
        SignDraftKeeper.FlushAll();
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static void InDataRoot(Action<string> body)
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        try
        {
            body(temp.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }

    private static async Task InDataRootAsync(Func<string, Task> body)
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        try
        {
            await WpfApp.RunAsync(() => body(temp.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }

    /// <summary>A window that is never shown. It closes, and its session with it, before the data root goes.</summary>
    private static Task InWindow(Func<string, PreviewWindow, Task> body) => InDataRootAsync(async root =>
    {
        var window = new PreviewWindow();
        try
        {
            await body(root, window);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// Sign mode as the Sign pill enters it, on a PDF the writer reads. The
    /// page render is stood in for, since pdfium never runs here, and notes
    /// each page it is asked for.
    /// </summary>
    private static async Task<FillSignSurface> EnterAsync(
        PreviewWindow window,
        string pdf,
        List<int>? rendered = null,
        Func<int, CancellationToken, Task<PdfPreviewPage?>>? render = null)
    {
        Fields.Set(window, "CurrentPath", pdf);
        Fields.Set(window, "_fillSignPath", pdf);
        window.PreviewContent.Content ??= new Border();
        window.RenderSignPage = (_, index, token) =>
        {
            rendered?.Add(index);
            return render?.Invoke(index, token) ?? Task.FromResult<PdfPreviewPage?>(BlankPage());
        };

        await (Task)Fields.Call(window, "EnterFillSignModeAsync")!;
        return Fields.Get<FillSignSurface>(window, "_fillSignSurface");
    }

    private static string Latin1(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    /// <summary>The save said no, wrote nothing, left the newer file alone, and kept the work and its draft.</summary>
    private static async Task RefusedAsync(PreviewWindow window, FillSignSurface surface, string pdf, Func<byte[]> newer)
    {
        var status = Fields.Get<TextBlock>(surface, "_saveStatus");
        Assert.True(await EventuallyAsync(() => status.Text.Length > 0 && status.Text != "Saving…"), "the save never answered");
        Assert.Equal(PreviewWindow.ChangedSinceSigningBegan, status.Text);
        Assert.Equal(newer(), File.ReadAllBytes(pdf));
        Assert.Equal([pdf], Directory.GetFiles(Path.GetDirectoryName(pdf)!));
        Assert.Single(surface.Placements);
        Assert.Same(surface, Fields.Get<FillSignSurface?>(window, "_fillSignSurface"));
        Assert.True(SignDraftKeeper.FlushAll());
    }

    /// <summary>Lets every continuation already queued on the window's thread run.</summary>
    private static Task SettleAsync(PreviewWindow window) =>
        window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task;

    private static string SignablePdf(string root, int pages = 1)
    {
        var path = Path.Combine(root, "docs", "contract.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FillSignBehaviorTests.WritePdf(path, 612, 792, pages);
        return path;
    }

    private static void Press(DependencyObject root, string label) =>
        Button(root, label).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static ButtonBase Button(DependencyObject root, string label) =>
        Descendants(root).OfType<ButtonBase>().First(button => button.Content as string == label);

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 5000 && !condition(); waited += 20)
        {
            await Task.Delay(20);
        }

        return condition();
    }

    private static bool HeldElsewhere(object gate)
    {
        if (!Monitor.TryEnter(gate))
        {
            return true;
        }

        Monitor.Exit(gate);
        return false;
    }

    private static string WritePdf(string root)
    {
        var path = Path.Combine(root, "docs", "contract.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "%PDF-1.4 a contract");
        return path;
    }

    private static void WriteDateDraft(string pdf) =>
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0, [Date(0)]);

    private static SignPlacement Date(int page) =>
        new(SignPlacementKind.Date, page, 0.4, 0.2, 0.14, 0.028, "10/10/2026");

    private static (SignPlacementKind, int, double, double, double, double, string?) Shape(SignPlacement p) =>
        (p.Kind, p.PageIndex, p.X, p.Y, p.Width, p.Height, p.Text);

    /// <summary>A path as it reads inside a JSON string.</summary>
    private static string JsonPath(string path) => path.Replace(@"\", @"\\", StringComparison.Ordinal);

    private static double Width(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    private static PdfPreviewPage BlankPage()
    {
        var bitmap = BitmapSource.Create(400, 600, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 400 * 600 * 4).ToArray(), 400 * 4);
        bitmap.Freeze();
        return new PdfPreviewPage(bitmap, 400, 600);
    }

    private static Grid[] Frames(FillSignSurface surface) =>
        Fields.Get<Canvas>(surface, "_placementLayer").Children.OfType<Grid>().ToArray();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Selects the frame, then presses the chip, the way a click on each would.</summary>
    private static void Click(UIElement chip, UIElement frame)
    {
        frame.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        frame.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        chip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    }

    private static byte[] SolidPng(int width, int height, byte shade)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = shade;
            pixels[i + 3] = 255;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
