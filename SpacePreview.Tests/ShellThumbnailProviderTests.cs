using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class ShellThumbnailProviderTests
{
    [Fact]
    public void IconPaddingIsTrimmedBeforePreviewDisplay()
    {
        const int width = 64;
        const int height = 64;
        var pixels = new byte[width * height * 4];
        for (var y = 24; y < 40; y++)
        {
            for (var x = 20; x < 44; x++)
            {
                var offset = ((y * width) + x) * 4;
                pixels[offset + 0] = 255;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);

        var trimmed = ShellThumbnailProvider.TrimTransparentPadding(source);

        Assert.Equal(24, trimmed.PixelWidth);
        Assert.Equal(16, trimmed.PixelHeight);
    }

    [Fact]
    public void FolderHoverPreservesRealThumbnailArtwork()
    {
        using var temp = new TempDirectory();
        var padded = CreateTransparentBitmap(256, 256, 52, 64, 156, 128);

        var normalized = ShellThumbnailProvider.NormalizeFolderImage(
            temp.Path,
            new ShellImage(padded, IsThumbnail: true),
            fallbackIconPixels: 280);

        Assert.NotNull(normalized);
        Assert.False(normalized.IsThumbnail);
        Assert.Equal(156, normalized.Bitmap.PixelWidth);
        Assert.Equal(128, normalized.Bitmap.PixelHeight);
    }

    [Fact]
    public void AlbumArtWriterOnlyAcceptsRealThumbnails()
    {
        using var temp = new TempDirectory();
        var source = CreateTransparentBitmap(32, 32, 4, 4, 24, 24);

        var iconResult = ShellThumbnailProvider.TryWriteThumbnailPng(new ShellImage(source, IsThumbnail: false), temp.Path, "icon");
        var artResult = ShellThumbnailProvider.TryWriteThumbnailPng(new ShellImage(source, IsThumbnail: true), temp.Path, "art");

        Assert.Null(iconResult);
        Assert.NotNull(artResult);
        Assert.Equal(".png", Path.GetExtension(artResult.LocalPath));
        Assert.True(File.Exists(artResult.LocalPath));
    }

    private static BitmapSource CreateTransparentBitmap(int width, int height, int left, int top, int visibleWidth, int visibleHeight)
    {
        var pixels = new byte[width * height * 4];
        for (var y = top; y < top + visibleHeight; y++)
        {
            for (var x = left; x < left + visibleWidth; x++)
            {
                var offset = ((y * width) + x) * 4;
                pixels[offset + 0] = 255;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }
}
