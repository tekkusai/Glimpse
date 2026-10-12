using System.Diagnostics;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A pdfium helper lives no longer than the work that wanted it, and never
/// longer than the app: each one is in the app's job, has a deadline, and
/// ends when its preview moves on. Whatever it does, its caller gets an
/// answer and not a fault. The helper here is a script standing in for a
/// render that hangs, or that leaves a file nothing can read.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PdfHelperTests
{
    // Sign mode awaited this call bare: a page file that would not decode
    // threw past it, and the Sign press ended in nothing.
    [Fact]
    public async Task APageThatWillNotDecodeIsAPageThatDidNotRender()
    {
        using var temp = new TempDirectory();
        var exe = PdfiumHelper.Exe;

        // Exits clean, leaving a page file that is no image.
        PdfiumHelper.Exe = temp.CreateFile("broken-helper.cmd", "@echo off\r\necho not a page> %4\r\nexit /b 0\r\n");
        try
        {
            Assert.Null(await PdfiumHelper.TryRenderPageAsync(Path.Combine(temp.Path, "any.pdf"), 0, CancellationToken.None));
        }
        finally
        {
            PdfiumHelper.Exe = exe;
        }
    }

    [Fact]
    public Task AHelperIsInTheAppsJobFromItsStart() => WithStuckHelper(async _ =>
    {
        using var session = new CancellationTokenSource();

        var run = PdfiumHelper.RunAsync(TimeSpan.FromMinutes(5), session.Token, ProcessPriorityClass.Normal, "--render-pdf-page");

        // Windows ends what is in the job when the app goes, however it goes.
        Assert.True(await EventuallyAsync(() => PdfiumHelper.Running > 0), "the helper never joined the job");

        session.Cancel();
        var ended = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ended.Started);
        Assert.Null(ended.ExitCode);
        Assert.True(await EventuallyAsync(() => PdfiumHelper.Running == 0), "the helper outlived its session");
    });

    [Fact]
    public Task AHelperThatNeverFinishesIsKilledAtItsDeadline() => WithStuckHelper(async _ =>
    {
        var ended = await PdfiumHelper
            .RunAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None, ProcessPriorityClass.BelowNormal, "--render-pdf-thumbnails")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(ended.Started);
        Assert.Null(ended.ExitCode);
        Assert.True(await EventuallyAsync(() => PdfiumHelper.Running == 0), "the helper outlived its deadline");
    });

    [Fact]
    public void AThumbnailRangeGetsTimeForItsPagesAndNoMore()
    {
        Assert.Equal(TimeSpan.FromSeconds(40), PdfiumHelper.RangeDeadline(10));
        Assert.Equal(TimeSpan.FromSeconds(120), PdfiumHelper.RangeDeadline(50));
        Assert.Equal(TimeSpan.FromSeconds(22), PdfiumHelper.RangeDeadline(0));
    }

    // The proof of a rescaled copy held its thread for its whole ten
    // seconds after the preview that wanted it had gone.
    [Fact]
    public Task TheScaleProofEndsWhenItsPreviewMovesOn() => WithStuckHelper(async temp =>
    {
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp);
        try
        {
            // Artwork wider than the viewer can frame: it is rescaled, and the copy proved.
            var artwork = Path.Combine(temp, "dieline.ai");
            FillSignBehaviorTests.WritePdf(artwork, 12848, 5503);
            using var preview = new CancellationTokenSource();

            var resolving = Task.Run(() => PdfViewerLink.TryResolve(artwork, preview.Token));
            Assert.True(await EventuallyAsync(() => PdfiumHelper.Running > 0), "the proof never started");

            preview.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await EventuallyAsync(() => PdfiumHelper.Running == 0), "the proof outlived its preview");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    });

    /// <summary>Runs the body with a helper that ignores what it is asked and waits a minute.</summary>
    private static async Task WithStuckHelper(Func<string, Task> body)
    {
        using var temp = new TempDirectory();
        var script = temp.CreateFile("stuck-helper.cmd", "@echo off\r\nping -n 60 127.0.0.1 > nul\r\n");
        var exe = PdfiumHelper.Exe;
        PdfiumHelper.Exe = script;
        try
        {
            await body(temp.Path);
        }
        finally
        {
            PdfiumHelper.Exe = exe;
        }
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 8000 && !condition(); waited += 25)
        {
            await Task.Delay(25);
        }

        return condition();
    }
}
