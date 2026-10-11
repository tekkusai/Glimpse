using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// What an image file says about itself before any pixel decodes: its
/// stored size, its EXIF orientation, and whether it is a JPEG (the one
/// format that decodes faster at a smaller size). The subtitle, the stage
/// fit, the decoder and the viewer all ask, so the last answer is kept per
/// file version and the header is read once.
/// </summary>
internal sealed record ImageHeader(int StoredWidth, int StoredHeight, int Orientation, bool IsJpeg)
{
    private static readonly object Sync = new();
    private static (ImageFileStamp Stamp, ImageHeader? Header)? _last;

    /// <summary>Width as displayed, after the EXIF turn.</summary>
    public int Width => ImageOrientation.SwapsAxes(Orientation) ? StoredHeight : StoredWidth;

    /// <summary>Height as displayed, after the EXIF turn.</summary>
    public int Height => ImageOrientation.SwapsAxes(Orientation) ? StoredWidth : StoredHeight;

    public int LongSide => Math.Max(StoredWidth, StoredHeight);

    public static ImageHeader? TryRead(string path)
    {
        if (!ImageFileStamp.TryCreate(path, out var stamp))
        {
            return null;
        }

        lock (Sync)
        {
            if (_last is { } last && last.Stamp == stamp)
            {
                return last.Header;
            }
        }

        var header = Read(path);
        lock (Sync)
        {
            _last = (stamp, header);
        }

        return header;
    }

    /// <summary>An icon stores several sizes and the preview wants the biggest; other formats show their first frame.</summary>
    public static BitmapFrame LargestFrame(BitmapDecoder decoder) =>
        decoder is IconBitmapDecoder
            ? decoder.Frames.MaxBy(frame => ((long)frame.PixelWidth * frame.PixelHeight, frame.Format.BitsPerPixel))!
            : decoder.Frames[0];

    private static ImageHeader? Read(string path)
    {
        if (FileKinds.IsPhotoshop(path))
        {
            return PsdPreviewLoader.TryReadPixelSize(path, out var canvas)
                ? new ImageHeader((int)canvas.Width, (int)canvas.Height, ImageOrientation.Normal, IsJpeg: false)
                : null;
        }

        // PNG puts its size in the fixed IHDR header. A WIC decoder built
        // just to read those 24 bytes once cost 34 ms on a giant sample, and
        // the subtitle reads it on the way to showing the window.
        if (TryReadPngSize(path, out var width, out var height))
        {
            return new ImageHeader(width, height, ImageOrientation.Normal, IsJpeg: false);
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var frame = LargestFrame(decoder);
            return new ImageHeader(
                Math.Max(1, frame.PixelWidth),
                Math.Max(1, frame.PixelHeight),
                ImageOrientation.ReadFrom(frame, path),
                decoder is JpegBitmapDecoder);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadPngSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            Span<byte> header = stackalloc byte[24];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.ReadExactly(header);
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (!header[..8].SequenceEqual(signature) || !header[12..16].SequenceEqual("IHDR"u8))
            {
                return false;
            }

            var storedWidth = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
            var storedHeight = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
            if (storedWidth is 0 or > int.MaxValue || storedHeight is 0 or > int.MaxValue)
            {
                return false;
            }

            width = (int)storedWidth;
            height = (int)storedHeight;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
