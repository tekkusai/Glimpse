using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Native;

namespace SpacePreview.Preview;

/// <summary>
/// EXIF orientation math. An orientation value (1-8) names the transform a
/// viewer must apply to the stored pixels to display them correctly; rotating
/// the view is a walk within the same eight states, so both display and
/// persistence go through these tables.
/// </summary>
internal static class ImageOrientation
{
    public const int Normal = 1;

    // Next orientation after rotating the DISPLAYED image 90 degrees
    // clockwise. Upright states cycle 1-6-3-8; mirrored states 2-7-4-5.
    private static readonly int[] RotateCwMap = [0, 6, 7, 8, 5, 2, 3, 4, 1];

    public static int RotateCw(int orientation, int quarterTurns)
    {
        if (orientation is < 1 or > 8)
        {
            orientation = Normal;
        }

        var turns = ((quarterTurns % 4) + 4) % 4;
        for (var i = 0; i < turns; i++)
        {
            orientation = RotateCwMap[orientation];
        }

        return orientation;
    }

    public static int FromQuarterTurns(int quarterTurns) => RotateCw(Normal, quarterTurns);

    public static bool SwapsAxes(int orientation) => orientation is 5 or 6 or 7 or 8;

    /// <summary>
    /// Reads the EXIF orientation tag without decoding pixels. Only JPEG and
    /// TIFF carry the tag in practice; everything else reads as normal.
    /// </summary>
    public static int Read(string path)
    {
        if (MetadataQueryFor(path) is null)
        {
            return Normal;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            return ReadFrom(decoder.Frames[0], path);
        }
        catch
        {
            // Unreadable metadata displays as stored.
            return Normal;
        }
    }

    /// <summary>The orientation tag of a frame already open, read with the query for its file type.</summary>
    public static int ReadFrom(BitmapFrame frame, string path)
    {
        try
        {
            // The RAW decoder leaves a portrait shot on its side and says
            // so here, whatever the camera's own format calls the tag.
            var query = frame.Decoder?.CodecInfo?.ContainerFormat == MediaCodecs.RawContainer
                ? "System.Photo.Orientation"
                : MetadataQueryFor(path);
            if (query is not null
                && frame.Metadata is BitmapMetadata metadata
                && metadata.GetQuery(query) is ushort value
                && value is >= 1 and <= 8)
            {
                return value;
            }
        }
        catch
        {
            // Unreadable metadata displays as stored.
        }

        return Normal;
    }

    /// <summary>
    /// Metadata query path for the orientation tag (0x0112 = 274), or null
    /// when the format does not carry EXIF this codebase persists.
    /// </summary>
    public static string? MetadataQueryFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "/app1/ifd/{ushort=274}",
            ".tif" or ".tiff" => "/ifd/{ushort=274}",
            _ => null
        };

    /// <summary>
    /// Applies an orientation to decoded pixels. Rotations and axis-aligned
    /// flips only, so TransformedBitmap stays on its supported path.
    /// </summary>
    public static BitmapSource Apply(BitmapSource source, int orientation)
    {
        if (orientation is <= Normal or > 8)
        {
            return source;
        }

        Transform transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => Transform.Identity
        };

        if (transform.CanFreeze)
        {
            transform.Freeze();
        }

        var oriented = new TransformedBitmap(source, transform);
        oriented.Freeze();
        return oriented;
    }
}
