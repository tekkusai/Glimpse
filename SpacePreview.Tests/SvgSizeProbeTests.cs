using System.Windows;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class SvgSizeProbeTests
{
    [Fact]
    public void PercentSizedSvgUsesViewBoxForCanvas()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "percent.svg");
        File.WriteAllText(path, "<svg width=\"100%\" viewBox=\"0 0 680 520\" xmlns=\"http://www.w3.org/2000/svg\" />");

        Assert.True(SvgSizeProbe.TryReadCanvasSize(path, out var size));
        Assert.Equal(new Size(680, 520), size);
    }

    [Fact]
    public void ExplicitSvgLengthsUseCssPixels()
    {
        Assert.True(SvgSizeProbe.TryResolveCanvasSize("10cm", "25.4mm", null, out var size));

        Assert.Equal(96 / 2.54 * 10, size.Width, precision: 3);
        Assert.Equal(96, size.Height, precision: 3);
    }

    [Fact]
    public void SingleExplicitSideKeepsViewBoxAspect()
    {
        Assert.True(SvgSizeProbe.TryResolveCanvasSize("340", null, "0 0 680 520", out var size));

        Assert.Equal(340, size.Width, precision: 3);
        Assert.Equal(260, size.Height, precision: 3);
    }
}
