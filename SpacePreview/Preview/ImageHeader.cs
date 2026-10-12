using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// What an image file says about itself before any pixel decodes: its
/// stored size, its EXIF orientation, whether it is a JPEG (the one
/// format that decodes faster at a smaller size) and whether it moves. The
/// subtitle, the stage fit, the decoder and the viewer all ask, so the last
/// answer is kept per file version and the header is read once.
/// </summary>
internal sealed record ImageHeader(int StoredWidth, int StoredHeight, int Orientation, bool IsJpeg)
{
    // A PNG lists a handful of chunks before its pixels. A file with more
    // than this is not one to keep walking on the way to showing the window.
    private const int MaxPngHeaderChunks = 64;

    private static readonly object Sync = new();
    private static (ImageFileStamp Stamp, ImageHeader? Header)? _last;

    /// <summary>
    /// An animated PNG or WebP. WPF draws only the first frame of one, so it
    /// plays on the browser image page, as a GIF does.
    /// </summary>
    public bool IsAnimated { get; init; }

    /// <summary>Transparency a PNG or an extended WebP declares up front. Other formats do not say here.</summary>
    public bool HasAlpha { get; init; }

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

        ImageHeader? header;
        try
        {
            header = Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be read just now: a rotation is being
            // written into it, or another program has it. That is no answer
            // to keep for this version of the file.
            return null;
        }

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

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // PNG and extended WebP put their size in their first bytes. A
            // WIC decoder built just to read those once cost 34 ms on a
            // giant sample, and the subtitle reads it on the way to showing
            // the window. The same bytes say whether the file is animated,
            // which the decoder would not.
            if (TryReadPng(stream) is { } png)
            {
                return png;
            }

            stream.Position = 0;
            if (TryReadExtendedWebp(stream) is { } webp)
            {
                return webp;
            }

            stream.Position = 0;
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
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException))
        {
            // Not an image any decoder here reads.
            return null;
        }
    }

    /// <summary>
    /// The size from the fixed IHDR, then the chunks in front of the first
    /// IDAT: an acTL there makes the file an animation, and a tRNS gives it
    /// transparency its color type does not show.
    /// </summary>
    private static ImageHeader? TryReadPng(Stream stream)
    {
        // Signature, the IHDR's length and name, then width, height, bit
        // depth and color type.
        Span<byte> start = stackalloc byte[26];
        var read = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (read < 24 || !start[..8].SequenceEqual(signature) || !start[12..16].SequenceEqual("IHDR"u8))
        {
            return null;
        }

        var storedWidth = BinaryPrimitives.ReadUInt32BigEndian(start[16..20]);
        var storedHeight = BinaryPrimitives.ReadUInt32BigEndian(start[20..24]);
        if (storedWidth is 0 or > int.MaxValue || storedHeight is 0 or > int.MaxValue)
        {
            return null;
        }

        var hasAlpha = read == start.Length && start[25] is 4 or 6;
        var isAnimated = false;

        // Each chunk is a length, a name, that many bytes and a checksum.
        // A file cut short still has the size it declared.
        Span<byte> chunk = stackalloc byte[8];
        stream.Position = 16L + BinaryPrimitives.ReadUInt32BigEndian(start[8..12]) + 4;
        for (var walked = 0; walked < MaxPngHeaderChunks; walked++)
        {
            if (stream.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false) < chunk.Length)
            {
                break;
            }

            var name = chunk[4..];
            if (name.SequenceEqual("IDAT"u8) || name.SequenceEqual("IEND"u8))
            {
                break;
            }

            isAnimated |= name.SequenceEqual("acTL"u8);
            hasAlpha |= name.SequenceEqual("tRNS"u8);
            stream.Seek(BinaryPrimitives.ReadUInt32BigEndian(chunk) + 4L, SeekOrigin.Current);
        }

        return new ImageHeader((int)storedWidth, (int)storedHeight, ImageOrientation.Normal, IsJpeg: false)
        {
            IsAnimated = isAnimated,
            HasAlpha = hasAlpha
        };
    }

    /// <summary>
    /// An extended WebP opens with a VP8X chunk: flags for animation and
    /// alpha, then the canvas. A simple WebP has none, cannot be animated,
    /// and goes to the decoder like any other format.
    /// </summary>
    private static ImageHeader? TryReadExtendedWebp(Stream stream)
    {
        Span<byte> start = stackalloc byte[30];
        if (stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) < start.Length
            || !start[..4].SequenceEqual("RIFF"u8)
            || !start[8..12].SequenceEqual("WEBP"u8)
            || !start[12..16].SequenceEqual("VP8X"u8))
        {
            return null;
        }

        // The canvas is stored as two 24-bit numbers, each one less than
        // the size.
        var flags = start[20];
        var width = 1 + (start[24] | start[25] << 8 | start[26] << 16);
        var height = 1 + (start[27] | start[28] << 8 | start[29] << 16);
        return new ImageHeader(width, height, ImageOrientation.Normal, IsJpeg: false)
        {
            IsAnimated = (flags & 0x02) != 0,
            HasAlpha = (flags & 0x10) != 0
        };
    }
}
