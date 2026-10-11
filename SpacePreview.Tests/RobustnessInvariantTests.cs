using System.Windows;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// Seeded property tests for values that real files and unusual desktops can
/// supply. These are deterministic on failure but cover far more of the state
/// space than one fixture per format.
/// </summary>
public sealed class RobustnessInvariantTests
{
    private static readonly Size NormalMinimum = new(PreviewWindow.StageMinWidth, PreviewWindow.StageMinHeight);

    [Fact]
    public void StageFitFuzzAlwaysReturnsFiniteReachableGeometry()
    {
        var random = new Random(20260709);
        for (var iteration = 0; iteration < 5_000; iteration++)
        {
            var workArea = new Rect(
                random.Next(1, 400),
                random.Next(1, 300),
                random.Next(240, 7_681),
                random.Next(180, 4_321));
            var dpi = (iteration % 11) switch
            {
                0 => double.NaN,
                1 => 0,
                2 => -1,
                _ => 0.75 + random.NextDouble() * 2.75
            };
            var hint = (iteration % 5) switch
            {
                0 => StageFitHint.Pixels(RandomDimension(random, iteration), RandomDimension(random, iteration + 1)),
                1 => StageFitHint.ImagePixels(RandomDimension(random, iteration), RandomDimension(random, iteration + 1)),
                2 => StageFitHint.PageAspect(RandomAspect(random, iteration)),
                3 => StageFitHint.Scroll(),
                _ => StageFitHint.Compact()
            };

            var result = StageFit.WindowFor(
                hint,
                workArea,
                dpi,
                documentRailWidth: random.Next(0, 220),
                documentToolbarHeight: random.Next(0, 90),
                NormalMinimum);

            Assert.True(double.IsFinite(result.Width) && double.IsFinite(result.Height), $"iteration {iteration}: {result}");
            Assert.InRange(result.Width, Math.Min(NormalMinimum.Width, workArea.Width), workArea.Width);
            Assert.InRange(result.Height, Math.Min(NormalMinimum.Height, workArea.Height), workArea.Height);
        }
    }

    [Theory]
    [InlineData(double.NaN, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, -42)]
    [InlineData(0, 0)]
    public void InvalidPixelDimensionsAreNormalized(double width, double height)
    {
        var hint = StageFitHint.ImagePixels(width, height);
        var result = StageFit.WindowFor(hint, new Rect(0, 0, 1920, 1080), 1, 132, 40, NormalMinimum);

        Assert.True(double.IsFinite(hint.Width));
        Assert.True(double.IsFinite(hint.Height));
        Assert.True(double.IsFinite(result.Width));
        Assert.True(double.IsFinite(result.Height));
    }

    [Fact]
    public void DefaultStageFitsAHighDpiOrSmallRemoteDesktop()
    {
        var workArea = new Rect(0, 0, 640, 360);

        var result = PreviewWindow.StageSize(workArea);

        Assert.Equal(640, result.Width);
        Assert.Equal(360, result.Height);
    }

    [Fact]
    public void SamePathDoesNotMakeAnOldAbaLoadCurrentAgain()
    {
        const long firstA = 41;
        const long finalA = 43;

        Assert.False(PreviewWindow.IsLoadIdentityCurrent(firstA, finalA, cancellationRequested: false));
        Assert.False(PreviewWindow.IsLoadIdentityCurrent(finalA, finalA, cancellationRequested: true));
        Assert.True(PreviewWindow.IsLoadIdentityCurrent(finalA, finalA, cancellationRequested: false));
    }

    [Theory]
    [InlineData(1224, 1584, false, true)]
    [InlineData(640, 900, true, true)]
    [InlineData(100_000, 100_000, false, false)]
    [InlineData(8192, 8192, false, false)]
    [InlineData(0, 1200, false, false)]
    [InlineData(4097, 500, true, false)]
    public void PdfRasterBudgetRejectsOversizedPages(int width, int height, bool thumbnail, bool expected)
    {
        Assert.Equal(expected, PdfPreviewRenderer.IsRasterSizeSafe(width, height, thumbnail));
    }

    [Fact]
    public void ZoomLadderStopsDisableTheSpentDirection()
    {
        Assert.False(PreviewWindow.CanAdjustZoom(0.25, -1));
        Assert.True(PreviewWindow.CanAdjustZoom(0.25, 1));
        Assert.True(PreviewWindow.CanAdjustZoom(5.0, -1));
        Assert.False(PreviewWindow.CanAdjustZoom(5.0, 1));
        Assert.Equal(1.1, PreviewWindow.NextZoomLevel(1.0, 1));
        Assert.Equal(0.9, PreviewWindow.NextZoomLevel(1.0, -1));
    }

    private static double RandomDimension(Random random, int iteration) => (iteration % 17) switch
    {
        0 => double.NaN,
        1 => double.PositiveInfinity,
        2 => double.NegativeInfinity,
        3 => 0,
        4 => -random.NextDouble() * 10_000,
        _ => Math.Pow(10, random.NextDouble() * 6)
    };

    private static double RandomAspect(Random random, int iteration) => (iteration % 13) switch
    {
        0 => double.NaN,
        1 => double.PositiveInfinity,
        2 => 0,
        3 => -1,
        _ => Math.Pow(10, random.NextDouble() * 6 - 3)
    };
}

