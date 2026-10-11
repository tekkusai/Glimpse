using System.Collections;
using System.Reflection;
using System.Text;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The conversion job against a fake engine: one run per file however many
/// previews ask, the last preview of a file to leave stops its run, a
/// preview of another file takes the engine back from work nobody waits
/// for, and the first pages of a big deck are never the cache entry but are
/// what a revisit shows at once.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OfficeConversionJobTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ASecondRequestForTheSameFileAttachesToTheRunningJob()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var path = temp.CreateFile("report.docx");

        var first = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None);
        var run = await engine.RunAsync(0);
        var second = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None);
        run.Finish();

        Assert.Same(first, second);
        var document = await first.Complete.WaitAsync(Patience);
        Assert.NotNull(document);
        Assert.Equal(1, engine.RunCount);
    }

    [Fact]
    public async Task LeavingAFileStopsItsConversion()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var path = temp.CreateFile("report.docx");
        using var preview = new CancellationTokenSource();

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, preview.Token);
        var run = await engine.RunAsync(0);
        Assert.False(run.Request.Control.Background);

        preview.Cancel();

        Assert.Null(await job.Complete.WaitAsync(Patience));
        Assert.True(run.Killed);
        Assert.Null(OfficeDocumentPdfConverter.TryGetCached(path));
        Assert.Equal(1, engine.RunCount);
    }

    [Fact]
    public async Task AnotherPreviewOfTheFileKeepsItsConversionGoing()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var path = temp.CreateFile("report.docx");
        using var glance = new CancellationTokenSource();
        using var pinned = new CancellationTokenSource();

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, glance.Token);
        var run = await engine.RunAsync(0);
        OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, pinned.Token);

        glance.Cancel();
        await Task.Delay(150);
        Assert.False(run.Killed);

        pinned.Cancel();
        Assert.Null(await job.Complete.WaitAsync(Patience));
        Assert.True(run.Killed);
    }

    [Fact]
    public async Task APreviewOfAnotherFileTakesTheEngineFromWorkNobodyWaitsFor()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var left = temp.CreateFile("left.docx");
        var opened = temp.CreateFile("opened.docx");
        using var leftPreview = new CancellationTokenSource();

        var leftJob = OfficeDocumentPdfConverter.Convert(left, OfficeKind.Document, leftPreview.Token);
        var leftRun = await engine.RunAsync(0);
        leftPreview.Cancel();
        var openedJob = OfficeDocumentPdfConverter.Convert(opened, OfficeKind.Document, CancellationToken.None);
        var openedRun = await engine.RunAsync(1);
        openedRun.Finish();

        Assert.True(leftRun.Killed);
        Assert.Null(await leftJob.Complete.WaitAsync(Patience));
        Assert.NotNull(await openedJob.Complete.WaitAsync(Patience));
        Assert.Equal(opened, openedRun.Request.SourcePath);
    }

    [Fact]
    public async Task APreviewStillWaitingForItsFirstPagesKeepsTheEngine()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var pinned = temp.CreateFile("pinned.docx");
        var next = temp.CreateFile("next.docx");

        var pinnedJob = OfficeDocumentPdfConverter.Convert(pinned, OfficeKind.Document, CancellationToken.None);
        var pinnedRun = await engine.RunAsync(0);
        var nextJob = OfficeDocumentPdfConverter.Convert(next, OfficeKind.Document, CancellationToken.None);
        await Task.Delay(150);

        Assert.False(pinnedRun.Killed);
        Assert.Equal(1, engine.RunCount);
        pinnedRun.Finish();
        (await engine.RunAsync(1)).Finish();
        Assert.NotNull(await pinnedJob.Complete.WaitAsync(Patience));
        Assert.NotNull(await nextJob.Complete.WaitAsync(Patience));
    }

    [Fact]
    public async Task QueuedWorkNobodyWaitsForNeverStarts()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var current = temp.CreateFile("current.docx");
        var skipped = temp.CreateFile("skipped.docx");
        using var skippedPreview = new CancellationTokenSource();

        var currentJob = OfficeDocumentPdfConverter.Convert(current, OfficeKind.Document, CancellationToken.None);
        var currentRun = await engine.RunAsync(0);
        var skippedJob = OfficeDocumentPdfConverter.Convert(skipped, OfficeKind.Document, skippedPreview.Token);
        skippedPreview.Cancel();

        Assert.Null(await skippedJob.Complete.WaitAsync(Patience));
        currentRun.Finish();
        Assert.NotNull(await currentJob.Complete.WaitAsync(Patience));
        Assert.Equal(1, engine.RunCount);
    }

    [Fact]
    public async Task AFileTheUserMovedPastBeforeTheEngineFreedUpNeverStarts()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var path = temp.CreateFile("skipped.docx");
        using var preview = new CancellationTokenSource();
        preview.Cancel();

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, preview.Token);

        Assert.Null(await job.Complete.WaitAsync(Patience));
        Assert.Equal(0, engine.RunCount);
    }

    [Fact]
    public async Task LeavingADeckStopsTheRestAndARevisitShowsTheFirstPagesAtOnce()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var path = Path.Combine(temp.Path, "deck.pptx");
        OfficeDocumentPdfConverterTests.WriteOfficeAppProperties(path, "Slides", 51);
        using var preview = new CancellationTokenSource();

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Presentation, preview.Token);
        (await engine.RunAsync(0)).Finish(pages: 6);
        await job.FirstContent.WaitAsync(Patience);
        var rest = await engine.RunAsync(1);
        preview.Cancel();

        Assert.Null(await job.Complete.WaitAsync(Patience));
        Assert.True(rest.Killed);
        Assert.Null(OfficeDocumentPdfConverter.TryGetCached(path));

        var revisit = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Presentation, CancellationToken.None);
        var partial = await revisit.FirstContent.WaitAsync(Patience);
        Assert.NotNull(partial);
        Assert.True(partial.IsPartial);
        Assert.Equal(6, partial.PartialPages);
        Assert.Equal(51, partial.ExpectedPages);

        // Only the rest runs again.
        var again = await engine.RunAsync(2);
        Assert.Equal("pdf", again.Request.ConvertTo);
        again.Finish(pages: 51);
        Assert.NotNull(await revisit.Complete.WaitAsync(Patience));
        Assert.NotNull(OfficeDocumentPdfConverter.TryGetCached(path));
        Assert.Equal(3, engine.RunCount);

        // Nobody shows the first pages, so the whole deck replaces them,
        // just after it is published.
        var deadline = DateTime.UtcNow + Patience;
        while (Directory.Exists(Path.GetDirectoryName(partial.DocumentPath)))
        {
            Assert.True(DateTime.UtcNow < deadline, "The first pages outlived the whole deck.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task TheRestOfALeftDeckNeverStartsBehindAnotherPreview()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var path = Path.Combine(temp.Path, "deck.pptx");
        OfficeDocumentPdfConverterTests.WriteOfficeAppProperties(path, "Slides", 51);
        var other = temp.CreateFile("other.docx");
        using var preview = new CancellationTokenSource();

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Presentation, preview.Token);
        var firstPages = await engine.RunAsync(0);
        // Another preview queues behind the deck's first pages, so the rest
        // of the deck waits its turn, and the user leaves meanwhile.
        var otherJob = OfficeDocumentPdfConverter.Convert(other, OfficeKind.Document, CancellationToken.None);
        await WaitingInLaneAsync(1);
        firstPages.Finish(pages: 6);
        await job.FirstContent.WaitAsync(Patience);
        var otherRun = await engine.RunAsync(1);
        preview.Cancel();
        otherRun.Finish();

        Assert.NotNull(await otherJob.Complete.WaitAsync(Patience));
        Assert.Null(await job.Complete.WaitAsync(Patience));
        Assert.Equal(2, engine.RunCount);
    }

    // A quick return can land between a left job's stop and its end. Holding
    // the converter's locks holds that moment open, so the return lands in
    // it every time; before, it attached to the stopping job and got nothing.
    [Fact]
    public async Task AReturnAsALeftFileStopsConvertsItAfresh()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var other = temp.CreateFile("other.docx");
        var path = temp.CreateFile("report.docx");
        using var preview = new CancellationTokenSource();

        OfficeDocumentPdfConverter.Convert(other, OfficeKind.Document, CancellationToken.None);
        var otherRun = await engine.RunAsync(0);
        var left = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, preview.Token);
        await WaitingInLaneAsync(1);
        OfficeConversionJob revisit;
        lock (PriorityGate)
        {
            lock (Lane)
            {
                preview.Cancel();
                revisit = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None);
            }
        }

        Assert.NotSame(left, revisit);
        otherRun.Finish();
        Assert.Null(await left.Complete.WaitAsync(Patience));
        (await engine.RunAsync(1)).Finish();
        Assert.NotNull(await revisit.Complete.WaitAsync(Patience));
    }

    [Fact]
    public async Task AReturnAsALeftDeckStopsShowsItsFirstPagesAndConvertsTheRest()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var path = Path.Combine(temp.Path, "deck.pptx");
        OfficeDocumentPdfConverterTests.WriteOfficeAppProperties(path, "Slides", 51);
        var other = temp.CreateFile("other.docx");
        using var preview = new CancellationTokenSource();

        // The rest of the deck waits behind another preview's conversion.
        var left = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Presentation, preview.Token);
        var firstPages = await engine.RunAsync(0);
        OfficeDocumentPdfConverter.Convert(other, OfficeKind.Document, CancellationToken.None);
        await WaitingInLaneAsync(1);
        firstPages.Finish(pages: 6);
        await left.FirstContent.WaitAsync(Patience);
        var otherRun = await engine.RunAsync(1);
        await WaitingInLaneAsync(1);
        OfficeConversionJob revisit;
        lock (PriorityGate)
        {
            lock (Lane)
            {
                preview.Cancel();
                revisit = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Presentation, CancellationToken.None);
            }
        }

        Assert.NotSame(left, revisit);
        var partial = await revisit.FirstContent.WaitAsync(Patience);
        Assert.NotNull(partial);
        Assert.True(partial.IsPartial);
        otherRun.Finish();
        var rest = await engine.RunAsync(2);
        Assert.Equal("pdf", rest.Request.ConvertTo);
        rest.Finish(pages: 51);
        Assert.NotNull(await revisit.Complete.WaitAsync(Patience));
    }

    [Fact]
    public async Task AFinishedConversionNeverWaitsBehindTheEngine()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var done = temp.CreateFile("done.docx");
        var busy = temp.CreateFile("busy.docx");
        var doneJob = OfficeDocumentPdfConverter.Convert(done, OfficeKind.Document, CancellationToken.None);
        (await engine.RunAsync(0)).Finish();
        await doneJob.Complete.WaitAsync(Patience);

        var busyJob = OfficeDocumentPdfConverter.Convert(busy, OfficeKind.Document, CancellationToken.None);
        var busyRun = await engine.RunAsync(1);
        var again = OfficeDocumentPdfConverter.Convert(done, OfficeKind.Document, CancellationToken.None);

        Assert.NotNull(await again.Complete.WaitAsync(Patience));
        Assert.Equal(2, engine.RunCount);
        busyRun.Finish();
        await busyJob.Complete.WaitAsync(Patience);
    }

    [Fact]
    public async Task TheFirstPagesOfABigDeckAreNeverTheCacheEntry()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var path = Path.Combine(temp.Path, "deck.pptx");
        OfficeDocumentPdfConverterTests.WriteOfficeAppProperties(path, "Slides", 51);

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Presentation, CancellationToken.None);
        var firstRun = await engine.RunAsync(0);
        Assert.StartsWith("pdf:impress_pdf_Export", firstRun.Request.ConvertTo, StringComparison.Ordinal);
        firstRun.Finish(pages: 6);

        var partial = await job.FirstContent.WaitAsync(Patience);
        Assert.NotNull(partial);
        Assert.True(partial.IsPartial);
        Assert.Equal(6, partial.PartialPages);
        Assert.Equal(51, partial.ExpectedPages);
        Assert.True(job.TryHoldPartial());
        Assert.Null(OfficeDocumentPdfConverter.TryGetCached(path));

        var restRun = await engine.RunAsync(1);
        Assert.True(restRun.Request.Control.Background);
        restRun.Finish(pages: 51);
        var complete = await job.Complete.WaitAsync(Patience);

        Assert.NotNull(complete);
        Assert.False(complete.IsPartial);
        Assert.Equal(complete.DocumentPath, OfficeDocumentPdfConverter.TryGetCached(path)?.DocumentPath);
        Assert.True(File.Exists(partial.DocumentPath));
        job.ReleasePartial();
        Assert.False(Directory.Exists(Path.GetDirectoryName(partial.DocumentPath)));
    }

    [Fact]
    public async Task FirstPagesThatAreTheWholeDocumentBecomeTheCacheEntry()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var path = Path.Combine(temp.Path, "short.pptx");
        OfficeDocumentPdfConverterTests.WriteOfficeAppProperties(path, "Slides", 40);

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Presentation, CancellationToken.None);
        (await engine.RunAsync(0)).Finish(pages: 3);
        var complete = await job.Complete.WaitAsync(Patience);

        Assert.NotNull(complete);
        Assert.False(complete.IsPartial);
        Assert.Same(complete, await job.FirstContent);
        Assert.NotNull(OfficeDocumentPdfConverter.TryGetCached(path));
        Assert.Equal(1, engine.RunCount);
    }

    [Fact]
    public async Task ASpreadsheetFallsBackToPdfWhenItsHtmlExportFails()
    {
        using var temp = new TempDirectory();
        using var engine = new FakeEngine();
        var path = temp.CreateFile("book.xlsx");

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Spreadsheet, CancellationToken.None);
        var htmlRun = await engine.RunAsync(0);
        Assert.Equal("html", htmlRun.Request.ConvertTo);
        htmlRun.Fail();
        var pdfRun = await engine.RunAsync(1);
        Assert.Equal("pdf", pdfRun.Request.ConvertTo);
        pdfRun.Finish();

        var complete = await job.Complete.WaitAsync(Patience);
        Assert.NotNull(complete);
        Assert.EndsWith(".pdf", complete.DocumentPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(complete.DocumentPath, OfficeDocumentPdfConverter.TryGetCached(path)?.DocumentPath);
    }

    private static object Lane => Converter<object>("Lane");

    private static object PriorityGate => Converter<object>("PriorityGate");

    private static T Converter<T>(string field) =>
        (T)typeof(OfficeDocumentPdfConverter).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static async Task WaitingInLaneAsync(int count)
    {
        var waiting = Converter<IList>("Waiting");
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            lock (Lane)
            {
                if (waiting.Count == count)
                {
                    return;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, $"Never {count} waiting for the engine.");
            await Task.Delay(10);
        }
    }

    /// <summary>An engine that runs only when the test says so.</summary>
    private sealed class FakeEngine : IDisposable
    {
        private readonly Func<OfficeRunRequest, CancellationToken, string?> _previous;
        private readonly List<Run> _runs = [];

        public FakeEngine()
        {
            _previous = OfficeDocumentPdfConverter.Runner;
            OfficeDocumentPdfConverter.Runner = Execute;
        }

        public int RunCount
        {
            get
            {
                lock (_runs)
                {
                    return _runs.Count;
                }
            }
        }

        public async Task<Run> RunAsync(int index)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < deadline)
            {
                lock (_runs)
                {
                    if (_runs.Count > index)
                    {
                        return _runs[index];
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Run {index} never started.");
        }

        public void Dispose()
        {
            OfficeDocumentPdfConverter.Runner = _previous;

            // A test that failed midway leaves its runs holding the lane;
            // ended, they free it for the next test.
            lock (_runs)
            {
                foreach (var run in _runs)
                {
                    run.Fail();
                }
            }
        }

        private string? Execute(OfficeRunRequest request, CancellationToken kill)
        {
            var run = new Run(request);
            lock (_runs)
            {
                _runs.Add(run);
            }

            int? pages;
            try
            {
                pages = run.Outcome.Task.WaitAsync(kill).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                run.Killed = true;
                return null;
            }

            if (pages is null)
            {
                return null;
            }

            var html = request.ConvertTo.StartsWith("html", StringComparison.Ordinal);
            var output = Path.Combine(request.OutputDirectory, Path.GetFileNameWithoutExtension(request.SourcePath) + (html ? ".html" : ".pdf"));
            var content = new StringBuilder("%PDF-1.4\n");
            for (var page = 0; page < pages; page++)
            {
                content.Append("<< /Type /Page >>\n");
            }

            File.WriteAllText(output, html ? "<html><head></head><body><table></table></body></html>" : content.ToString());
            return output;
        }
    }

    private sealed class Run(OfficeRunRequest request)
    {
        public OfficeRunRequest Request { get; } = request;
        public TaskCompletionSource<int?> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Killed;

        public void Finish(int pages = 1) => Outcome.TrySetResult(pages);

        public void Fail() => Outcome.TrySetResult(null);
    }
}
