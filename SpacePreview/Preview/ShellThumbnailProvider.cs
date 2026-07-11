using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

internal sealed record ShellImage(BitmapSource Bitmap, bool IsThumbnail);

internal static class ShellThumbnailProvider
{
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const uint SIIGBF_THUMBNAILONLY = 0x00000008;
    private const uint SIIGBF_ICONONLY = 0x00000004;
    private const uint SIIGBF_INCACHEONLY = 0x00000010;

    /// <summary>
    /// Returns a real content thumbnail when the shell has one, otherwise the
    /// file's icon. Thumbnails can ask the shell for large artwork; icons use
    /// a normal icon frame so oversized .ico frames do not turn into tiny art
    /// inside a huge transparent canvas.
    /// </summary>
    public static ShellImage? TryGetThumbnail(string path, int size = 768, int iconSize = 128)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        if (TryGetImage(path, size, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out var thumbnail) && thumbnail is not null)
        {
            return new ShellImage(thumbnail, IsThumbnail: true);
        }

        if (TryGetImage(path, iconSize, SIIGBF_ICONONLY, out var icon) && icon is not null)
        {
            return new ShellImage(TrimTransparentPadding(icon), IsThumbnail: false);
        }

        return null;
    }

    public static BitmapSource? TryGetIcon(string path, int size = 40)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        return TryGetImage(path, size, SIIGBF_ICONONLY, out var icon) && icon is not null
            ? TrimTransparentPadding(icon)
            : null;
    }

    /// <summary>
    /// Returns existing Explorer artwork without asking a thumbnail provider
    /// to decode the file. This is the only safe first-frame path for giant
    /// PNGs: a cache miss must stay a millisecond miss, not start the same
    /// hundreds-of-milliseconds inflate as the full preview decoder.
    /// </summary>
    public static ShellImage? TryGetCachedThumbnail(string path, int size = 2048)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return TryGetImage(
            path,
            size,
            SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_INCACHEONLY,
            out var thumbnail)
            && thumbnail is not null
                ? new ShellImage(thumbnail, IsThumbnail: true)
                : null;
    }

    public static Uri? TryWriteThumbnailPng(string path, string cacheKey, int size = 512)
    {
        var shellImage = TryGetThumbnail(path, size);
        var directory = AppPaths.InDataRoot("MediaArtwork");
        return TryWriteThumbnailPng(shellImage, directory, cacheKey);
    }

    internal static Uri? TryWriteThumbnailPng(ShellImage? shellImage, string directory, string cacheKey)
    {
        if (shellImage is not { IsThumbnail: true })
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{HashCacheKey(cacheKey)}.png");
            if (!File.Exists(path))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(shellImage.Bitmap));
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                encoder.Save(stream);
            }

            return new Uri(path, UriKind.Absolute);
        }
        catch
        {
            return null;
        }
    }

    internal static ShellImage? NormalizeFolderImage(
        string path,
        ShellImage? shellImage,
        int fallbackIconPixels,
        int minThumbnailEdge = 128)
    {
        if (shellImage is null || !shellImage.IsThumbnail || !Directory.Exists(path))
        {
            return shellImage;
        }

        var trimmed = TrimTransparentPadding(shellImage.Bitmap);
        if (Math.Max(trimmed.Width, trimmed.Height) >= minThumbnailEdge)
        {
            return new ShellImage(trimmed, IsThumbnail: false);
        }

        var icon = TryGetIcon(path, fallbackIconPixels);
        return new ShellImage(icon ?? trimmed, IsThumbnail: false);
    }

    internal static BitmapSource TrimTransparentPadding(BitmapSource source)
    {
        var bitmap = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = Math.Max(1, bitmap.PixelWidth * 4);
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var left = bitmap.PixelWidth;
        var top = bitmap.PixelHeight;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            var row = y * stride;
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                if (pixels[row + (x * 4) + 3] <= 3)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
        {
            return source;
        }

        var width = right - left + 1;
        var height = bottom - top + 1;
        if (left == 0 && top == 0 && width == bitmap.PixelWidth && height == bitmap.PixelHeight)
        {
            return source;
        }

        var cropped = new CroppedBitmap(bitmap, new System.Windows.Int32Rect(left, top, width, height));
        cropped.Freeze();
        return cropped;
    }

    private static string HashCacheKey(string cacheKey)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cacheKey));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    private static bool TryGetImage(string path, int size, uint flags, out BitmapSource? bitmap)
    {
        bitmap = null;
        IShellItemImageFactory? factory = null;
        IntPtr hbitmap = IntPtr.Zero;

        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (factory is null)
            {
                return false;
            }

            factory.GetImage(new SIZE(size, size), flags, out hbitmap);
            if (hbitmap == IntPtr.Zero)
            {
                return false;
            }

            bitmap = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
            {
                DeleteObject(hbitmap);
            }

            if (factory is not null && Marshal.IsComObject(factory))
            {
                Marshal.FinalReleaseComObject(factory);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SIZE
    {
        public readonly int cx;
        public readonly int cy;

        public SIZE(int cx, int cy)
        {
            this.cx = cx;
            this.cy = cy;
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, uint flags, out IntPtr phbm);
    }
}
