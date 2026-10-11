using System.IO;
using System.IO.Compression;
using System.Text;

namespace SpacePreview.Utilities;

internal enum FileSignatureKind
{
    Unknown,
    Pdf,
    Png,
    Jpeg,
    Gif,
    Webp,
    Bmp,
    Tiff,
    Heif,
    Zip,
    Rar,
    SevenZip,
    Gzip
}

/// <summary>Why a HEIF image did not decode, as far as the file and the decoder can tell.</summary>
internal enum HeifFailure
{
    Damaged,
    NeedsHevc,
    NeedsHeif
}

internal enum ZipPackageKind
{
    Unknown,
    OfficeWord,
    OfficePowerPoint,
    OfficeExcel,
    Epub,
    WindowsAppPackage
}

internal static class FileSignatures
{
    private const int HeaderBytes = 96;

    public static FileSignatureKind Detect(string path)
    {
        try
        {
            Span<byte> buffer = stackalloc byte[HeaderBytes];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(buffer);
            return Detect(buffer[..read]);
        }
        catch
        {
            return FileSignatureKind.Unknown;
        }
    }

    internal static FileSignatureKind Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("%PDF-"u8))
        {
            return FileSignatureKind.Pdf;
        }

        if (StartsWith(bytes, 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A))
        {
            return FileSignatureKind.Png;
        }

        if (StartsWith(bytes, 0xFF, 0xD8, 0xFF))
        {
            return FileSignatureKind.Jpeg;
        }

        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            return FileSignatureKind.Gif;
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return FileSignatureKind.Webp;
        }

        if (bytes.StartsWith("BM"u8))
        {
            return FileSignatureKind.Bmp;
        }

        if (StartsWith(bytes, 0x49, 0x49, 0x2A, 0x00) || StartsWith(bytes, 0x4D, 0x4D, 0x00, 0x2A))
        {
            return FileSignatureKind.Tiff;
        }

        if (bytes.Length >= 16 && bytes[4..8].SequenceEqual("ftyp"u8) && ContainsIsoBrand(bytes[8..], "heic", "heix", "hevc", "hevx", "mif1", "msf1"))
        {
            return FileSignatureKind.Heif;
        }

        if (StartsWith(bytes, 0x50, 0x4B, 0x03, 0x04)
            || StartsWith(bytes, 0x50, 0x4B, 0x05, 0x06)
            || StartsWith(bytes, 0x50, 0x4B, 0x07, 0x08))
        {
            return FileSignatureKind.Zip;
        }

        if (StartsWith(bytes, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07))
        {
            return FileSignatureKind.Rar;
        }

        if (StartsWith(bytes, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C))
        {
            return FileSignatureKind.SevenZip;
        }

        return StartsWith(bytes, 0x1F, 0x8B)
            ? FileSignatureKind.Gzip
            : FileSignatureKind.Unknown;
    }

    public static bool IsImage(FileSignatureKind kind)
    {
        return kind is FileSignatureKind.Png
            or FileSignatureKind.Jpeg
            or FileSignatureKind.Gif
            or FileSignatureKind.Webp
            or FileSignatureKind.Bmp
            or FileSignatureKind.Tiff
            or FileSignatureKind.Heif;
    }

    public static bool IsBrowserImage(FileSignatureKind kind)
    {
        return kind is FileSignatureKind.Gif or FileSignatureKind.Webp;
    }

    public static bool IsArchive(FileSignatureKind kind)
    {
        return kind is FileSignatureKind.Zip
            or FileSignatureKind.Rar
            or FileSignatureKind.SevenZip
            or FileSignatureKind.Gzip;
    }

    public static ZipPackageKind DetectZipPackageKind(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var sawContentTypes = false;
            var sawWord = false;
            var sawPowerPoint = false;
            var sawExcel = false;
            var inspected = 0;

            foreach (var entry in archive.Entries)
            {
                if (++inspected > 512)
                {
                    break;
                }

                var name = entry.FullName.Replace('\\', '/');
                if (name.Equals("mimetype", StringComparison.OrdinalIgnoreCase) && IsEpubMimeType(entry))
                {
                    return ZipPackageKind.Epub;
                }

                if (name.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("AppxMetadata/AppxBundleManifest.xml", StringComparison.OrdinalIgnoreCase))
                {
                    return ZipPackageKind.WindowsAppPackage;
                }

                sawContentTypes |= name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase);
                sawWord |= name.StartsWith("word/", StringComparison.OrdinalIgnoreCase);
                sawPowerPoint |= name.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase);
                sawExcel |= name.StartsWith("xl/", StringComparison.OrdinalIgnoreCase);
            }

            if (!sawContentTypes)
            {
                return ZipPackageKind.Unknown;
            }

            if (sawWord)
            {
                return ZipPackageKind.OfficeWord;
            }

            if (sawPowerPoint)
            {
                return ZipPackageKind.OfficePowerPoint;
            }

            return sawExcel ? ZipPackageKind.OfficeExcel : ZipPackageKind.Unknown;
        }
        catch
        {
            return ZipPackageKind.Unknown;
        }
    }

    public static bool HasHevcCodedHeifBrand(string path)
    {
        try
        {
            Span<byte> buffer = stackalloc byte[HeaderBytes];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(buffer);
            return HasHevcCodedHeifBrand(buffer[..read]);
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasHevcCodedHeifBrand(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 16
            && bytes[4..8].SequenceEqual("ftyp"u8)
            && ContainsIsoBrand(bytes[8..], "heic", "heix", "hevc", "hevx");
    }

    public static string HeifBrandText(string path)
    {
        try
        {
            Span<byte> buffer = stackalloc byte[HeaderBytes];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(buffer);
            return HeifBrandText(buffer[..read]);
        }
        catch
        {
            return "unknown";
        }
    }

    internal static string HeifBrandText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[4..8].SequenceEqual("ftyp"u8))
        {
            return "unknown";
        }

        var brands = new List<string>();
        for (var offset = 8; offset + 4 <= bytes.Length && brands.Count < 8; offset += 4)
        {
            if (offset == 12)
            {
                continue;
            }

            var brand = Encoding.ASCII.GetString(bytes.Slice(offset, 4));
            if (brand.All(ch => ch >= 32 && ch <= 126) && !brands.Contains(brand, StringComparer.OrdinalIgnoreCase))
            {
                brands.Add(brand);
            }
        }

        return brands.Count == 0 ? "unknown" : string.Join(",", brands);
    }

    // Decoder answers that mean the bytes are bad, not that a codec is
    // missing: WIC's bad image, bad header, missing frame and stream read,
    // Media Foundation's invalid file format, end of file and invalid data.
    private static readonly HashSet<int> DamagedImageErrors =
    [
        unchecked((int)0x88982F60),
        unchecked((int)0x88982F61),
        unchecked((int)0x88982F62),
        unchecked((int)0x88982F72),
        unchecked((int)0xC00D36BE),
        unchecked((int)0x80070026),
        unchecked((int)0x8007000D)
    ];

    /// <summary>
    /// Decodes the file again to hear the decoder's own reason. Only a real
    /// HEIF header whose decode did not fail on bad data is blamed on a
    /// missing codec; a truncated or random file is called damaged.
    /// </summary>
    public static HeifFailure ClassifyHeifFailure(string path)
    {
        try
        {
            var header = new byte[HeaderBytes];
            int read;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                read = stream.Read(header);
            }

            return ClassifyHeifFailure(header.AsSpan(0, read), Detect(header.AsSpan(0, read)) == FileSignatureKind.Heif ? DecodeError(path) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HeifFailure.Damaged;
        }
    }

    internal static HeifFailure ClassifyHeifFailure(ReadOnlySpan<byte> header, int? decodeError)
    {
        if (Detect(header) != FileSignatureKind.Heif || decodeError is not { } error || DamagedImageErrors.Contains(error))
        {
            return HeifFailure.Damaged;
        }

        return HasHevcCodedHeifBrand(header) ? HeifFailure.NeedsHevc : HeifFailure.NeedsHeif;
    }

    /// <summary>The innermost HRESULT of a full decode, or null when it decodes.</summary>
    private static int? DecodeError(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0 ? null : unchecked((int)0x88982F62);
        }
        catch (Exception ex)
        {
            while (ex.InnerException is not null)
            {
                ex = ex.InnerException;
            }

            return ex.HResult;
        }
    }

    private static bool StartsWith(ReadOnlySpan<byte> bytes, params byte[] prefix) => bytes.StartsWith(prefix);

    private static bool IsEpubMimeType(ZipArchiveEntry entry)
    {
        if (entry.Length > 128)
        {
            return false;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim().Equals("application/epub+zip", StringComparison.Ordinal);
    }

    private static bool ContainsIsoBrand(ReadOnlySpan<byte> bytes, params string[] brands)
    {
        foreach (var brand in brands)
        {
            if (bytes.IndexOf(System.Text.Encoding.ASCII.GetBytes(brand)) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}

