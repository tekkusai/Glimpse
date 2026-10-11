using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The tray glyph at every scale Windows offers from 100 to 300 percent:
/// the size the tray asks for, the Space sign alone with every edge on a
/// whole pixel, centered across, a little above center as the system's
/// chevron is, and never a cropped corner.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class TrayIconRendererTests
{
    [Theory]
    [InlineData(96u, 16, 2)]
    [InlineData(120u, 20, 2)]
    [InlineData(144u, 24, 2)]
    [InlineData(168u, 28, 2)]
    [InlineData(192u, 32, 3)]
    [InlineData(216u, 36, 3)]
    [InlineData(240u, 40, 3)]
    [InlineData(288u, 48, 4)]
    public Task TheGlyphIsCrispAndCenteredInTheTraysSize(uint dpi, int pixels, int stroke) => WpfApp.RunAsync(() =>
    {
        foreach (var light in new[] { false, true })
        {
            var bitmap = TrayIconRenderer.Render(dpi, light);

            Assert.Equal(pixels, bitmap.PixelWidth);
            Assert.Equal(pixels, bitmap.PixelHeight);

            // Crisp: each pixel is ink or nothing, no antialiased edge.
            var alpha = Alpha(bitmap);
            Assert.All(alpha, a => Assert.True(a is 0 or 255, $"alpha {a} in {pixels}"));

            int left = pixels, top = pixels, right = -1, bottom = -1;
            for (var y = 0; y < pixels; y++)
            {
                for (var x = 0; x < pixels; x++)
                {
                    if (alpha[(y * pixels) + x] != 0)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            // Inside with room to spare, centered across, and half a pixel
            // to a pixel above center: the bar weighs the sign down, and
            // the system's chevron sits as high.
            Assert.True(left >= 1 && top >= 1 && right <= pixels - 2 && bottom <= pixels - 2, $"bounds {left},{top},{right},{bottom} in {pixels}");
            Assert.Equal(pixels - 1 - right, left);
            Assert.InRange(top + bottom - (pixels - 1), -2, -1);

            // The sign, not a plate: wide and low.
            Assert.True(right - left + 1 >= pixels * 0.7, $"width {right - left + 1} of {pixels}");
            Assert.True(bottom - top + 1 <= pixels * 0.6, $"height {bottom - top + 1} of {pixels}");

            // The bar under the middle is the stroke thick.
            var middle = pixels / 2;
            Assert.Equal(stroke, Enumerable.Range(0, pixels).Count(y => alpha[(y * pixels) + middle] == 255));
        }

        return Task.CompletedTask;
    });

    internal static byte[] Alpha(BitmapSource bitmap)
    {
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bgra.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels.Where((_, index) => index % 4 == 3).ToArray();
    }
}
