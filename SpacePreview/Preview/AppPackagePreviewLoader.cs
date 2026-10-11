using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

internal enum AppPackageSignature
{
    NotSigned,
    Trusted,
    Untrusted,
    Expired,
    Tampered,
    Invalid
}

internal sealed record AppPackageInfo(
    string Title,
    string? Publisher,
    string? Description,
    string? IdentityName,
    string? Version,
    string? Architecture,
    int ApplicationCount,
    bool IsBundle,
    IReadOnlyList<string> Capabilities,
    int CapabilityOverflow,
    byte[]? LogoBytes,
    string SourcePath);

internal static class AppPackagePreviewLoader
{
    private const long MaxLogoBytes = 2 * 1024 * 1024;
    private const int MaxCapabilities = 16;
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
    private const int CertEExpired = unchecked((int)0x800B0101);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);
    private const int CertEUntrustedTestRoot = unchecked((int)0x800B010D);

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static AppPackageInfo? TryLoad(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var (manifest, isBundle) = LoadManifest(archive);
            var root = manifest?.Root;
            if (root is null)
            {
                return null;
            }

            var identity = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Identity", StringComparison.OrdinalIgnoreCase));
            var properties = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Properties", StringComparison.OrdinalIgnoreCase));
            var visual = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("VisualElements", StringComparison.OrdinalIgnoreCase));
            var applicationCount = root.Descendants().Count(e => e.Name.LocalName.Equals("Application", StringComparison.OrdinalIgnoreCase));
            var allCapabilities = root.Descendants()
                .Where(e => e.Name.LocalName.Equals("Capability", StringComparison.OrdinalIgnoreCase)
                    || e.Name.LocalName.Equals("DeviceCapability", StringComparison.OrdinalIgnoreCase)
                    || e.Name.LocalName.Equals("RestrictedCapability", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Attribute("Name")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var title = CleanValue(visual?.Attribute("DisplayName")?.Value)
                ?? CleanValue(properties?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("DisplayName", StringComparison.OrdinalIgnoreCase))?.Value)
                ?? identity?.Attribute("Name")?.Value
                ?? Path.GetFileNameWithoutExtension(path);
            var publisher = CleanValue(properties?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("PublisherDisplayName", StringComparison.OrdinalIgnoreCase))?.Value)
                ?? CleanPublisherIdentity(identity?.Attribute("Publisher")?.Value);
            var description = CleanValue(visual?.Attribute("Description")?.Value)
                ?? CleanValue(properties?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Description", StringComparison.OrdinalIgnoreCase))?.Value);
            var logoPath = visual?.Attribute("Square150x150Logo")?.Value
                ?? visual?.Attribute("Square44x44Logo")?.Value
                ?? properties?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Logo", StringComparison.OrdinalIgnoreCase))?.Value;
            var logo = TryReadLogo(archive, logoPath);

            return new AppPackageInfo(
                title,
                publisher,
                description,
                identity?.Attribute("Name")?.Value,
                identity?.Attribute("Version")?.Value,
                identity?.Attribute("ProcessorArchitecture")?.Value,
                applicationCount,
                isBundle,
                allCapabilities.Take(MaxCapabilities).ToList(),
                Math.Max(0, allCapabilities.Count - MaxCapabilities),
                logo,
                path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"app package preview failed {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Hashes the whole package, so it runs after the card is on screen.
    /// </summary>
    internal static (AppPackageSignature Signature, string? Signer) SignatureFor(string path)
    {
        var signature = ClassifyTrustResult(VerifyTrust(path));
        var signer = signature is AppPackageSignature.Trusted or AppPackageSignature.Untrusted or AppPackageSignature.Expired
            ? SignerName(path)
            : null;
        return (signature, signer);
    }

    /// <summary>
    /// A package signed by a certificate this PC does not trust, the normal
    /// sideloaded case, is not a tampered one; only a digest mismatch is.
    /// </summary>
    internal static AppPackageSignature ClassifyTrustResult(int result) => result switch
    {
        0 => AppPackageSignature.Trusted,
        TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => AppPackageSignature.NotSigned,
        TrustEBadDigest => AppPackageSignature.Tampered,
        CertEExpired => AppPackageSignature.Expired,
        CertEUntrustedRoot or CertEChaining or CertEUntrustedTestRoot or TrustEExplicitDistrust => AppPackageSignature.Untrusted,
        _ => AppPackageSignature.Invalid
    };

    /// <summary>The trust line's words, and whether they warn.</summary>
    internal static (string Text, bool Warning) DescribeSignature(AppPackageSignature signature, string? signer)
    {
        var by = string.IsNullOrWhiteSpace(signer) ? "Signed" : $"Signed by {signer}";
        return signature switch
        {
            AppPackageSignature.Trusted => (by, false),
            AppPackageSignature.Untrusted => ($"{by}, not trusted on this PC", true),
            AppPackageSignature.Expired => ($"{by}, certificate expired", true),
            AppPackageSignature.Tampered => ("Contents don't match the signature", true),
            AppPackageSignature.NotSigned => ("Not signed", true),
            _ => ("Signature can't be verified", true)
        };
    }

    private static string? SignerName(string path)
    {
        try
        {
            #pragma warning disable SYSLIB0057
            using var signedFileCertificate = X509Certificate.CreateFromSignedFile(path);
            #pragma warning restore SYSLIB0057
            using var certificate = new X509Certificate2(signedFileCertificate);
            var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(name) ? certificate.Subject : name;
        }
        catch
        {
            return null;
        }
    }

    private static int VerifyTrust(string path)
    {
        var fileInfo = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPointer,
                dwStateAction = WtdStateActionVerify,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = null,
                dwProvFlags = 0,
                dwUIContext = 0
            };

            var result = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
            data.dwStateAction = WtdStateActionClose;
            _ = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
            return result;
        }
        catch
        {
            return TrustENoSignature;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static byte[]? TryReadLogo(ZipArchive archive, string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return null;
        }

        var normalized = NormalizeEntryPath(manifestPath);
        var entry = archive.GetEntry(normalized) ?? FindScaledAsset(archive, normalized);
        if (entry is null || entry.Length <= 0 || entry.Length > MaxLogoBytes || !IsImageEntry(entry.FullName))
        {
            return null;
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static ZipArchiveEntry? FindScaledAsset(ZipArchive archive, string normalized)
    {
        var directory = PathPrefix(normalized);
        var fileName = Path.GetFileNameWithoutExtension(normalized);
        var extension = Path.GetExtension(normalized);
        var prefix = string.IsNullOrWhiteSpace(directory)
            ? $"{fileName}."
            : $"{directory}/{fileName}.";

        return archive.Entries
            .Where(entry =>
                entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Length)
            .FirstOrDefault();
    }

    private static (XDocument? Manifest, bool IsBundle) LoadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry("AppxManifest.xml");
        var isBundle = false;
        if (entry is null)
        {
            entry = archive.GetEntry("AppxMetadata/AppxBundleManifest.xml");
            isBundle = entry is not null;
        }

        if (entry is null || entry.Length > 5 * 1024 * 1024)
        {
            return (null, false);
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        return (XmlNesting.Load(entry.Open, settings, LoadOptions.None), isBundle);
    }

    private static string? CleanValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.Trim();
    }

    /// <summary>
    /// The Identity Publisher attribute is an X.500 distinguished name. Pull
    /// the CN out so the card never shows "CN=..., O=..." noise; anything
    /// unparseable is dropped rather than displayed raw.
    /// </summary>
    private static string? CleanPublisherIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var part in value.Split(','))
        {
            var piece = part.Trim();
            if (piece.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                var name = piece[3..].Trim().Trim('"');
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }

        return null;
    }

    private static bool IsImageEntry(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant()
            is ".jpg" or ".jpeg" or ".png" or ".gif";
    }

    private static string NormalizeEntryPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string PathPrefix(string path)
    {
        path = NormalizeEntryPath(path);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
        ref WintrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;

        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pwszURLReference;

        public uint dwProvFlags;
        public uint dwUIContext;
    }
}
