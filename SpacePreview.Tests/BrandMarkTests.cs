using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// One mark everywhere: the app icon is the tile BrandMark draws, the hand
/// hinting never strays from the shape, and the welcome shows the tile.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class BrandMarkTests
{
    [Fact]
    public Task TheAppIconIsTheTileBrandMarkDraws() => WpfApp.RunAsync(() =>
    {
        var icon = File.ReadAllBytes(TestSource.FilePath(@"SpacePreview\Assets\Glimpse.ico"));
        var sizes = new List<int>();
        for (var i = 0; i < BitConverter.ToUInt16(icon, 4); i++)
        {
            var entry = 6 + (16 * i);
            var size = icon[entry] == 0 ? 256 : icon[entry];
            var frame = BitmapFrame.Create(
                new MemoryStream(icon, BitConverter.ToInt32(icon, entry + 12), BitConverter.ToInt32(icon, entry + 8)),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            Assert.True(Bgra(BrandMark.RenderTile(size)).SequenceEqual(Bgra(frame)), $"frame {size} is not the mark; run scripts/render-icon.ps1");
            sizes.Add(size);
        }

        // Every size Windows asks for from 100 to 300 percent, then 64 and 256.
        Assert.Equal([16, 20, 24, 30, 32, 36, 40, 48, 64, 256], sizes.Order());
        return Task.CompletedTask;
    });

    [Fact]
    public void HandHintingStaysWithinAPixelOfTheShape()
    {
        foreach (var (pixels, (inset, corner, sign)) in BrandMark.HintedTiles)
        {
            var exactInset = pixels * BrandMark.TileInset;
            var side = pixels - (2 * exactInset);
            Assert.InRange(inset - exactInset, -1, 1);
            Assert.InRange(corner - (side * BrandMark.TileCorner), -1, 1);
            AssertNear(BrandMark.OnTile.Scaled(side, exactInset), sign, pixels);
        }

        foreach (var (pixels, sign) in BrandMark.HintedGlyphs)
        {
            AssertNear(BrandMark.Alone.Scaled(pixels, 0), sign, pixels);
        }
    }

    [Fact]
    public Task SmallTilesDrawTheSignOnWholePixels() => WpfApp.RunAsync(() =>
    {
        foreach (var (pixels, (_, _, sign)) in BrandMark.HintedTiles.Where(row => row.Key < 48))
        {
            // Inside the sign's box each pixel is the sign or the plate:
            // white, or a red no higher than the plate's.
            var bgra = Bgra(BrandMark.RenderTile(pixels));
            for (var y = (int)sign.Top; y < sign.Bottom; y++)
            {
                for (var x = (int)sign.Left; x < sign.Right; x++)
                {
                    var red = bgra[(((y * pixels) + x) * 4) + 2];
                    Assert.True(red is 255 or <= 0x4E, $"red {red} at {x},{y} in {pixels}");
                }
            }
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task TheWelcomeShowsTheAppTile() => WpfApp.RunAsync(() =>
    {
        var mark = BrandMark.Create(64);
        mark.Measure(new Size(64, 64));
        mark.Arrange(new Rect(0, 0, 64, 64));
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(mark);

        Assert.True(Bgra(BrandMark.RenderTile(64)).SequenceEqual(Bgra(bitmap)));
        return Task.CompletedTask;
    });

    private static void AssertNear(BrandMark.Sign exact, BrandMark.Sign hinted, int pixels)
    {
        var pairs = new[]
        {
            (exact.Left, hinted.Left), (exact.Top, hinted.Top), (exact.Right, hinted.Right),
            (exact.Bottom, hinted.Bottom), (exact.Stroke, hinted.Stroke)
        };
        foreach (var (shape, pixel) in pairs)
        {
            Assert.True(pixel == Math.Floor(pixel), $"{pixel} is not a whole pixel at {pixels}");
            Assert.True(Math.Abs(pixel - shape) <= 1, $"{pixel} strays from {shape:0.00} at {pixels}");
        }
    }

    private static byte[] Bgra(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
