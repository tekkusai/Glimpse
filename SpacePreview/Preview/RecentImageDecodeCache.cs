using System.IO;
using System.Windows.Media.Imaging;

namespace SpacePreview.Preview;

internal readonly record struct ImageFileStamp(string Path, long Length, long LastWriteUtcTicks)
{
    public static bool TryCreate(string path, out ImageFileStamp stamp)
    {
        stamp = default;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return false;
            }

            stamp = new ImageFileStamp(
                System.IO.Path.GetFullPath(path),
                file.Length,
                file.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed record CachedImageDecode(BitmapSource Bitmap, int Orientation);

/// <summary>
/// The preview only needs the image the user is likely to step back to. One
/// strong entry turns a repeated giant PNG from another full inflate into a
/// direct bitmap handoff. The window's existing idle retirement bounds the
/// lifetime; this class bounds the size.
/// </summary>
internal sealed class RecentImageDecodeCache
{
    internal const long MaxRetainedBytes = 64L * 1024 * 1024;

    private readonly object _sync = new();
    private Entry? _entry;
    private int _generation;

    public int CaptureGeneration()
    {
        lock (_sync)
        {
            return _generation;
        }
    }

    public bool TryGet(ImageFileStamp stamp, out CachedImageDecode? decoded)
    {
        lock (_sync)
        {
            if (_entry is { } entry
                && SameStamp(entry.Stamp, stamp))
            {
                decoded = entry.Decoded;
                return true;
            }
        }

        decoded = null;
        return false;
    }

    public bool Store(ImageFileStamp stamp, CachedImageDecode decoded, int generation)
    {
        if (EstimatedBytes(decoded.Bitmap) > MaxRetainedBytes)
        {
            return false;
        }

        lock (_sync)
        {
            if (generation != _generation)
            {
                return false;
            }

            _entry = new Entry(stamp, decoded);
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _entry = null;
            _generation++;
        }
    }

    internal static long EstimatedBytes(BitmapSource bitmap)
    {
        var bitsPerPixel = Math.Max(1, bitmap.Format.BitsPerPixel);
        var stride = ((long)bitmap.PixelWidth * bitsPerPixel + 7) / 8;
        return stride * bitmap.PixelHeight;
    }

    private static bool SameStamp(ImageFileStamp left, ImageFileStamp right) =>
        left.Length == right.Length
        && left.LastWriteUtcTicks == right.LastWriteUtcTicks
        && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

    private sealed record Entry(ImageFileStamp Stamp, CachedImageDecode Decoded);
}
