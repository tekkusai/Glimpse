using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// What the engine could not finish it is not asked again the same day, by
/// a revisit or by the neighbor warm-up. A changed file is tried at once,
/// first pages that did convert stay the preview, and a run the lane
/// stopped is nobody's failure.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OfficeConversionMemoryTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AConversionThatFailedIsNotRunAgainTheSameDay()
    {
        using var temp = new TempDirectory();
        using var engine = new OfficeConversionJobTests.FakeEngine();
        var path = temp.CreateFile("broken.docx");

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None);
        (await engine.RunAsync(0)).Fail();
        Assert.Null(await job.Complete.WaitAsync(Patience));

        var revisit = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None);
        Assert.Null(await revisit.Complete.WaitAsync(Patience));
        var warmUp = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None, background: true);
        Assert.Null(await warmUp.Complete.WaitAsync(Patience));

        Assert.Equal(1, engine.RunCount);
    }

    [Fact]
    public async Task AChangedFileIsTriedAgain()
    {
        using var temp = new TempDirectory();
        using var engine = new OfficeConversionJobTests.FakeEngine();
        var path = temp.CreateFile("report.docx", "first draft");

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None);
        (await engine.RunAsync(0)).Fail();
        Assert.Null(await job.Complete.WaitAsync(Patience));

        File.WriteAllText(path, "a second, longer draft");
        var saved = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None);
        (await engine.RunAsync(1)).Finish();

        Assert.NotNull(await saved.Complete.WaitAsync(Patience));
    }

    [Fact]
    public async Task AFailureFromBeforeYesterdayIsTriedAgain()
    {
        using var temp = new TempDirectory();
        using var engine = new OfficeConversionJobTests.FakeEngine();
        var path = temp.CreateFile("report.docx");

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None);
        (await engine.RunAsync(0)).Fail();
        Assert.Null(await job.Complete.WaitAsync(Patience));

        File.SetLastWriteTimeUtc(Path.Combine(job.Key, "failed"), DateTime.UtcNow.AddHours(-25));
        var nextDay = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None);
        (await engine.RunAsync(1)).Finish();

        Assert.NotNull(await nextDay.Complete.WaitAsync(Patience));
    }

    [Fact]
    public async Task AFailedRestLeavesTheFirstPagesToShowAndIsNotRunAgain()
    {
        using var temp = new TempDirectory();
        using var engine = new OfficeConversionJobTests.FakeEngine();
        var path = Path.Combine(temp.Path, "deck.pptx");
        OfficeDocumentPdfConverterTests.WriteOfficeAppProperties(path, "Slides", 51);

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Presentation, CancellationToken.None);
        (await engine.RunAsync(0)).Finish(pages: 6);
        var partial = await job.FirstContent.WaitAsync(Patience);
        Assert.NotNull(partial);
        (await engine.RunAsync(1)).Fail();
        Assert.Null(await job.Complete.WaitAsync(Patience));

        // A preview that arrives after the rest failed still shows the first slides.
        Assert.True(job.TryHoldPartial());
        job.ReleasePartial();
        Assert.True(File.Exists(partial.DocumentPath));

        var revisit = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Presentation, CancellationToken.None);
        var first = await revisit.FirstContent.WaitAsync(Patience);
        Assert.NotNull(first);
        Assert.True(first.IsPartial);
        Assert.Equal(6, first.PartialPages);
        Assert.Null(await revisit.Complete.WaitAsync(Patience));
        Assert.True(revisit.TryHoldPartial());
        revisit.ReleasePartial();
        Assert.True(File.Exists(partial.DocumentPath));
        Assert.Equal(2, engine.RunCount);
    }

    [Fact]
    public async Task ARunTheLaneStoppedIsNotRememberedAsAFailure()
    {
        using var temp = new TempDirectory();
        using var engine = new OfficeConversionJobTests.FakeEngine();
        var path = temp.CreateFile("report.docx");
        using var preview = new CancellationTokenSource();

        var left = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, preview.Token);
        var stopped = await engine.RunAsync(0);
        preview.Cancel();
        Assert.Null(await left.Complete.WaitAsync(Patience));
        Assert.True(stopped.Killed);

        var revisit = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None);
        (await engine.RunAsync(1)).Finish();

        Assert.NotNull(await revisit.Complete.WaitAsync(Patience));
    }

    // The rest of a document ran at BelowNormal under the 75 seconds a
    // preview waits, so a long file under load never got past page 6.
    [Fact]
    public void WorkNobodyIsBlockedOnGetsTheLongLimit()
    {
        var foreground = OfficeRunControl.TimeLeft(background: false, TimeSpan.Zero);
        var background = OfficeRunControl.TimeLeft(background: true, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(75), foreground);
        Assert.Equal(TimeSpan.FromMinutes(10), background);
    }

    [Fact]
    public void APreviewThatJoinsAWarmUpWaitsTheForegroundLimitFromThen()
    {
        var joinedAt = TimeSpan.FromMinutes(5);

        Assert.Equal(TimeSpan.FromSeconds(75), OfficeRunControl.TimeLeft(background: false, joinedAt));
        Assert.Equal(TimeSpan.FromMinutes(5), OfficeRunControl.TimeLeft(background: true, joinedAt));
        // Near the end of the long limit there is less than that left.
        Assert.Equal(TimeSpan.FromSeconds(30), OfficeRunControl.TimeLeft(background: false, TimeSpan.FromSeconds(570)));
        Assert.True(OfficeRunControl.TimeLeft(background: true, TimeSpan.FromMinutes(11)) < TimeSpan.Zero);
    }
}
