using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The tray glyph at 100, 125 and 150 percent: the size the tray asks for,
/// and the whole spark in the middle of it, never a cropped corner.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class TrayIconRendererTests
{
    [Theory]
    [InlineData(96u, 16)]
    [InlineData(120u, 20)]
    [InlineData(144u, 24)]
    public Task TheWholeGlyphSitsCenteredInTheTraysSize(uint dpi, int pixels) => WpfApp.RunAsync(() =>
    {
        foreach (var light in new[] { false, true })
        {
            var bitmap = TrayIconRenderer.Render(dpi, light);

            Assert.Equal(pixels, bitmap.PixelWidth);
            Assert.Equal(pixels, bitmap.PixelHeight);

            var alpha = Alpha(bitmap);
            int left = pixels, top = pixels, right = -1, bottom = -1;
            double weight = 0, sumX = 0, sumY = 0;
            for (var y = 0; y < pixels; y++)
            {
                for (var x = 0; x < pixels; x++)
                {
                    var a = alpha[(y * pixels) + x];
                    if (a == 0)
                    {
                        continue;
                    }

                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    weight += a;
                    sumX += a * x;
                    sumY += a * y;
                }
            }

            // Inside: the edge pixels hold at most an antialiased tip, so
            // nothing of the spark falls off the square. Whole: it spans
            // most of the square.
            var edgeInk = Enumerable.Range(0, pixels)
                .SelectMany(i => new[] { alpha[i], alpha[i * pixels], alpha[(i * pixels) + pixels - 1], alpha[((pixels - 1) * pixels) + i] })
                .Max();
            Assert.True(edgeInk < 64, $"edge alpha {edgeInk} in {pixels}");
            Assert.True(right - left + 1 >= pixels * 0.75, $"width {right - left + 1} of {pixels}");
            Assert.True(bottom - top + 1 >= pixels * 0.75, $"height {bottom - top + 1} of {pixels}");

            // Centered: the ink balances on the middle of the square.
            var middle = (pixels - 1) / 2.0;
            Assert.Equal(middle, sumX / weight, 0.25);
            Assert.Equal(middle, sumY / weight, 0.25);
        }

        return Task.CompletedTask;
    });

    private static byte[] Alpha(BitmapSource bitmap)
    {
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bgra.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels.Where((_, index) => index % 4 == 3).ToArray();
    }
}
