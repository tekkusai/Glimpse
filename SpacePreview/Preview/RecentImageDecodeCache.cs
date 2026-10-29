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
/// Keep the two images a user just viewed so stepping back and forth does not
/// decode them again. Each entry is at most 64 MiB; pinning or idle retirement clears both.
/// </summary>
internal sealed class RecentImageDecodeCache
{
    internal const long MaxEntryBytes = 64L * 1024 * 1024;

    private readonly object _sync = new();
    private Entry? _recent;
    private Entry? _older;
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
            if (_recent is { } recent && SameStamp(recent.Stamp, stamp))
            {
                decoded = recent.Decoded;
                return true;
            }

            if (_older is { } older && SameStamp(older.Stamp, stamp))
            {
                _older = _recent;
                _recent = older;
                decoded = older.Decoded;
                return true;
            }
        }

        decoded = null;
        return false;
    }

    public bool Store(ImageFileStamp stamp, CachedImageDecode decoded, int generation)
    {
        if (EstimatedBytes(decoded.Bitmap) > MaxEntryBytes)
        {
            return false;
        }

        lock (_sync)
        {
            if (generation != _generation)
            {
                return false;
            }

            var entry = new Entry(stamp, decoded);
            if (_recent is { } recent && SamePath(recent.Stamp, stamp))
            {
                _recent = entry;
            }
            else
            {
                _older = _recent;
                _recent = entry;
            }
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _recent = null;
            _older = null;
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
        && SamePath(left, right);

    private static bool SamePath(ImageFileStamp left, ImageFileStamp right) =>
        string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

    private sealed record Entry(ImageFileStamp Stamp, CachedImageDecode Decoded);
}
