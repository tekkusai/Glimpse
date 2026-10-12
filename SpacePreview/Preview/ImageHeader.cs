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
    // than this is not one to keep walking on the way to showing the window:
    // a glance stops here, and says it stopped.
    private const int PngChunksAtAGlance = 64;

    private static readonly object Sync = new();
    private static (ImageFileStamp Stamp, ImageHeader? Header)? _last;

    /// <summary>
    /// An animated PNG or WebP. WPF draws only the first frame of one, so it
    /// plays on the browser image page, as a GIF does.
    /// </summary>
    public bool IsAnimated { get; init; }

    /// <summary>Transparency a PNG or an extended WebP declares up front. Other formats do not say here.</summary>
    public bool HasAlpha { get; init; }

    /// <summary>
    /// A glance at a PNG ran out of chunks to walk before it reached the
    /// pixels. <see cref="IsAnimated"/> and <see cref="HasAlpha"/> are then
    /// only what it had found, not an answer: a read that is not a glance
    /// walks the rest.
    /// </summary>
    public bool IsUndecided { get; init; }

    /// <summary>Width as displayed, after the EXIF turn.</summary>
    public int Width => ImageOrientation.SwapsAxes(Orientation) ? StoredHeight : StoredWidth;

    /// <summary>Height as displayed, after the EXIF turn.</summary>
    public int Height => ImageOrientation.SwapsAxes(Orientation) ? StoredWidth : StoredHeight;

    public int LongSide => Math.Max(StoredWidth, StoredHeight);

    /// <summary>
    /// <paramref name="atAGlance"/> is for the UI thread on the way to
    /// showing the window: a PNG is walked no further than a fixed number
    /// of chunks, and the header may come back <see cref="IsUndecided"/>.
    /// Every other read walks to the pixels, however far that is, and can
    /// be called off through <paramref name="token"/>.
    /// </summary>
    public static ImageHeader? TryRead(string path, bool atAGlance = false, CancellationToken token = default) =>
        ImageFileStamp.TryCreate(path, out var stamp) ? TryRead(stamp, atAGlance, token) : null;

    /// <summary>
    /// The header of the file <paramref name="stamp"/> was taken from. A
    /// caller that holds the stamp it will decode under asks with it, and
    /// is answered from memory only for that same version of the file.
    /// </summary>
    public static ImageHeader? TryRead(ImageFileStamp stamp, bool atAGlance = false, CancellationToken token = default)
    {
        lock (Sync)
        {
            if (_last is { } last && last.Stamp == stamp && (atAGlance || last.Header is not { IsUndecided: true }))
            {
                return last.Header;
            }
        }

        ImageHeader? header;
        try
        {
            header = Read(stamp.Path, atAGlance, token);
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

    private static ImageHeader? Read(string path, bool atAGlance, CancellationToken token)
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
            // which the decoder would not. The walk is the rotation rule's
            // own, so the two give one answer to whether a PNG moves.
            if (PngRotation.ReadFront(stream, atAGlance ? PngChunksAtAGlance : int.MaxValue, token) is { } png)
            {
                return new ImageHeader(png.Width, png.Height, ImageOrientation.Normal, IsJpeg: false)
                {
                    IsAnimated = png.IsAnimated,
                    HasAlpha = png.ColorType is 4 or 6 || png.HasTransparency,
                    IsUndecided = !png.Complete
                };
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
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException))
        {
            // Not an image any decoder here reads.
            return null;
        }
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
