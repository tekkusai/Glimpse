using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

[Collection(WpfCollection.Name)]
public sealed class RecentImageDecodeCacheTests
{
    [Fact]
    public Task PinningReleasesCachedDecodesWithoutDroppingVisiblePixels() => WpfApp.RunAsync(() =>
    {
        var window = new PreviewWindow { Left = -30000, Top = 0 };
        try
        {
            var cache = Fields.Get<RecentImageDecodeCache>(window, "_imageDecodeCache");
            var generation = cache.CaptureGeneration();
            var first = new ImageFileStamp("first.png", 1, 1);
            var second = new ImageFileStamp("second.png", 1, 1);
            var current = Bitmap(3, 3);
            var shown = new System.Windows.Controls.Image { Source = current };
            window.PreviewContent.Content = shown;
            Assert.True(cache.Store(first, new CachedImageDecode(Bitmap(2, 2), 1), generation));
            Assert.True(cache.Store(second, new CachedImageDecode(current, 1), generation));

            window.SetPinned(true);

            Assert.Same(shown, window.PreviewContent.Content);
            Assert.Same(current, shown.Source);
            Assert.False(cache.TryGet(first, out _));
            Assert.False(cache.TryGet(second, out _));
            Assert.False(cache.Store(first, new CachedImageDecode(Bitmap(2, 2), 1), generation));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public void StoredDecodeIsReturnedForTheSameFileStamp()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "image.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.True(ImageFileStamp.TryCreate(path, out var stamp));
        var bitmap = Bitmap(8, 6);
        var cache = new RecentImageDecodeCache();

        Assert.True(cache.Store(stamp, new CachedImageDecode(bitmap, 6), cache.CaptureGeneration()));
        Assert.True(cache.TryGet(stamp, out var hit));
        Assert.Same(bitmap, hit!.Bitmap);
        Assert.Equal(6, hit.Orientation);
    }

    [Fact]
    public void AlternatingBetweenTwoFilesReusesBothBitmaps()
    {
        var first = new ImageFileStamp("first.png", 1, 1);
        var second = new ImageFileStamp("second.png", 1, 1);
        var firstBitmap = Bitmap(2, 2);
        var secondBitmap = Bitmap(3, 3);
        var cache = new RecentImageDecodeCache();
        var generation = cache.CaptureGeneration();
        Assert.True(cache.Store(first, new CachedImageDecode(firstBitmap, 1), generation));
        Assert.True(cache.Store(second, new CachedImageDecode(secondBitmap, 1), generation));

        for (var i = 0; i < 3; i++)
        {
            Assert.True(cache.TryGet(first, out var firstHit));
            Assert.Same(firstBitmap, firstHit!.Bitmap);
            Assert.True(cache.TryGet(second, out var secondHit));
            Assert.Same(secondBitmap, secondHit!.Bitmap);
        }
    }

    [Fact]
    public void ThirdFileEvictsTheLeastRecentlyUsed()
    {
        var first = new ImageFileStamp("first.png", 1, 1);
        var second = new ImageFileStamp("second.png", 1, 1);
        var third = new ImageFileStamp("third.png", 1, 1);
        var cache = new RecentImageDecodeCache();
        var generation = cache.CaptureGeneration();
        Assert.True(cache.Store(first, new CachedImageDecode(Bitmap(2, 2), 1), generation));
        Assert.True(cache.Store(second, new CachedImageDecode(Bitmap(3, 3), 1), generation));
        Assert.True(cache.TryGet(first, out _));

        Assert.True(cache.Store(third, new CachedImageDecode(Bitmap(4, 4), 1), generation));

        Assert.True(cache.TryGet(first, out _));
        Assert.True(cache.TryGet(third, out _));
        Assert.False(cache.TryGet(second, out _));
    }

    [Fact]
    public void ChangedFileStampAndSecondPathDoNotReuseStalePixels()
    {
        using var temp = new TempDirectory();
        var first = Path.Combine(temp.Path, "first.bin");
        var second = Path.Combine(temp.Path, "second.bin");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);
        Assert.True(ImageFileStamp.TryCreate(first, out var firstStamp));
        Assert.True(ImageFileStamp.TryCreate(second, out var secondStamp));
        var cache = new RecentImageDecodeCache();
        var generation = cache.CaptureGeneration();
        cache.Store(firstStamp, new CachedImageDecode(Bitmap(2, 2), 1), generation);
        cache.Store(secondStamp, new CachedImageDecode(Bitmap(3, 3), 1), generation);

        Assert.True(cache.TryGet(secondStamp, out _));
        Assert.True(cache.TryGet(firstStamp, out _));

        File.WriteAllBytes(second, [2, 3]);
        Assert.True(ImageFileStamp.TryCreate(second, out var changedStamp));
        Assert.False(cache.TryGet(changedStamp, out _));
        var changedBitmap = Bitmap(4, 4);
        Assert.True(cache.Store(changedStamp, new CachedImageDecode(changedBitmap, 1), generation));
        Assert.False(cache.TryGet(secondStamp, out _));
        Assert.True(cache.TryGet(changedStamp, out var changedHit));
        Assert.Same(changedBitmap, changedHit!.Bitmap);
        Assert.True(cache.TryGet(firstStamp, out _));
    }

    [Fact]
    public void OversizedDecodeIsNotStored()
    {
        var cache = new RecentImageDecodeCache();
        var oversized = Bitmap(4097, 4096);
        Assert.True(RecentImageDecodeCache.EstimatedBytes(oversized) > RecentImageDecodeCache.MaxEntryBytes);
        Assert.False(cache.Store(new ImageFileStamp("large.png", 1, 1), new CachedImageDecode(oversized, 1), cache.CaptureGeneration()));
        Assert.False(cache.TryGet(new ImageFileStamp("large.png", 1, 1), out _));
    }

    [Fact]
    public void InvalidationClearsBothEntriesAndRejectsAnInFlightOldGeneration()
    {
        using var temp = new TempDirectory();
        var first = new ImageFileStamp(Path.Combine(temp.Path, "first.bin"), 1, 1);
        var second = new ImageFileStamp(Path.Combine(temp.Path, "second.bin"), 1, 1);
        var cache = new RecentImageDecodeCache();
        var oldGeneration = cache.CaptureGeneration();
        Assert.True(cache.Store(first, new CachedImageDecode(Bitmap(2, 2), 1), oldGeneration));
        Assert.True(cache.Store(second, new CachedImageDecode(Bitmap(3, 3), 1), oldGeneration));

        cache.Invalidate();

        Assert.False(cache.Store(first, new CachedImageDecode(Bitmap(2, 2), 1), oldGeneration));
        Assert.False(cache.TryGet(first, out _));
        Assert.False(cache.TryGet(second, out _));
    }

    [Fact]
    public void PngPixelProbeReadsTheFixedHeader()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "large.png");
        var header = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(header, 0);
        "IHDR"u8.CopyTo(header.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 9800);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 8399);
        File.WriteAllBytes(path, header);

        var image = ImageHeader.TryRead(path);
        Assert.NotNull(image);
        Assert.Equal((9800, 8399), (image.Width, image.Height));
    }

    [Fact]
    public void FullDecodeUpgradeChangesQualityWithoutACompositionReset()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "preview.webp");
        File.WriteAllBytes(path, [1]);
        var first = Bitmap(8, 6);
        var full = Bitmap(16, 12);
        var session = new ImageRotationSession(path, first, ImageOrientation.Normal, ImageRotationService.Classify(path));
        var qualityChanges = 0;
        var compositionChanges = 0;
        session.QualityUpgraded += () => qualityChanges++;
        session.ViewChanged += () => compositionChanges++;

        Assert.True(session.TryUpgradeDecoded(full, ImageOrientation.Normal));
        Assert.Same(full, session.CurrentBitmap);
        Assert.Equal(1, qualityChanges);
        Assert.Equal(0, compositionChanges);
    }

    [Fact]
    public void PendingTurnsRideOnTopOfALateFullDecode()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "preview.webp");
        File.WriteAllBytes(path, [1]);
        var session = new ImageRotationSession(path, Bitmap(8, 6), ImageOrientation.Normal, ImageRotationService.Classify(path));
        session.Rotate(1);

        // Rotating while the stand-in shows must not freeze the stand-in.
        Assert.True(session.TryUpgradeDecoded(Bitmap(16, 12), ImageOrientation.Normal));
        Assert.Equal(12, session.CurrentBitmap.PixelWidth);
        Assert.Equal(16, session.CurrentBitmap.PixelHeight);
    }

    [Fact]
    public void ADecodeThatPredatesACommittedRotationIsRefused()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "preview.jpg");
        File.WriteAllBytes(path, [1]);
        var session = new ImageRotationSession(path, Bitmap(8, 6), ImageOrientation.Normal, ImageRotationService.Classify(path));
        session.Rotate(1);
        session.Commit();

        Assert.False(session.TryUpgradeDecoded(Bitmap(16, 12), ImageOrientation.Normal));
        Assert.Equal(6, session.CurrentBitmap.PixelWidth);
        Assert.Equal(8, session.CurrentBitmap.PixelHeight);
    }

    private static BitmapSource Bitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
