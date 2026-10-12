using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

internal sealed record AppUpdateRelease(
    Version Version,
    string Tag,
    Uri InstallerUri,
    long InstallerSize,
    string InstallerSha256,
    Uri ChecksumUri)
{
    public string VersionText => Version.ToString(3);

    public string InstallerFileName => $"Glimpse-Setup-{VersionText}.exe";

    /// <summary>The release key's signature over the sidecar. A release without one is not offered.</summary>
    public Uri? SignatureUri { get; init; }

    /// <summary>When GitHub published the release. Automatic installs wait on it.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// A release that cannot install as it stands: its signature, checksum or
/// identity failed. It is not offered, and the next check looks again.
/// </summary>
internal sealed class UpdateRejectedException(string message) : Exception(message);

/// <summary>
/// Checks Glimpse's public GitHub release and downloads only the installer
/// whose version, name, size, URL, GitHub digest and checksum sidecar agree,
/// and whose sidecar is signed with the maintainer's release key.
/// </summary>
internal sealed partial class AppUpdateService : IDisposable
{
    internal static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/tekkusai/Glimpse/releases/latest");

    /// <summary>
    /// The public half of the maintainer's release key (ECDSA P-256). The
    /// private half never touches GitHub, so a stolen account cannot ship code.
    /// </summary>
    internal const string ReleasePublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEQH1GH3d4xhGWICFol2t/gwGEx15L
        asX0TBrY0tVeuOTzGUDwaDOpGM0UbNzJYAeTLeIv20j+wPENSUw1CynCLg==
        -----END PUBLIC KEY-----
        """;

    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
    private const int MaxMetadataBytes = 1024 * 1024;
    private const int MaxChecksumBytes = 4096;
    private const int MaxSignatureBytes = 512;
    private const long MinInstallerBytes = 1024 * 1024;
    private const long MaxInstallerBytes = 1024L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _downloadRoot;
    private readonly string _publicKey;
    private readonly Action<ProcessStartInfo> _start;

    public Version CurrentVersion { get; }

    public string CurrentVersionText => CurrentVersion.ToString(3);

    public AppUpdateService()
        : this(CreateHttpClient(), DetectCurrentVersion(), AppPaths.InDataRoot("Updates"), ownsHttpClient: true)
    {
    }

    /// <param name="publicKey">Tests sign with a throwaway key; the app always uses <see cref="ReleasePublicKey"/>.</param>
    /// <param name="start">Starts setup; tests stand in for it and never run an installer.</param>
    internal AppUpdateService(
        HttpClient httpClient,
        Version currentVersion,
        string downloadRoot,
        bool ownsHttpClient = false,
        string publicKey = ReleasePublicKey,
        Action<ProcessStartInfo>? start = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        CurrentVersion = NormalizeVersion(currentVersion ?? throw new ArgumentNullException(nameof(currentVersion)));
        _downloadRoot = Path.GetFullPath(downloadRoot ?? throw new ArgumentNullException(nameof(downloadRoot)));
        _ownsHttpClient = ownsHttpClient;
        _publicKey = publicKey;
        _start = start ?? StartProcess;
    }

    public async Task<AppUpdateRelease?> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);

        using var request = NewRequest(HttpMethod.Get, LatestReleaseUri, json: true);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (response.RequestMessage?.RequestUri != LatestReleaseUri)
        {
            throw new InvalidDataException("The update check left GitHub's release API.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var json = await ReadLimitedAsync(response.Content, MaxMetadataBytes, timeout.Token);
        var release = JsonSerializer.Deserialize<GitHubRelease>(json)
            ?? throw new InvalidDataException("GitHub returned an empty release.");

        var update = ParseRelease(release, CurrentVersion);
        return update is null ? null : update with
        {
            SignatureUri = FindSignatureUri(release, update),
            PublishedAt = release.PublishedAt
        };
    }

    /// <summary>
    /// Proves the maintainer released this: the release key signed the exact
    /// sidecar bytes, and the sidecar names the installer GitHub hashed.
    /// Throws <see cref="UpdateRejectedException"/> for a release that does
    /// not pass as it stands, and anything else for a network that may recover.
    /// </summary>
    public async Task VerifyAsync(AppUpdateRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateRelease(release);
        if (release.SignatureUri is null)
        {
            throw new UpdateRejectedException("The release has no signature.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);
        var sidecar = await DownloadSmallAsync(release.ChecksumUri, MaxChecksumBytes, timeout.Token);
        var signature = await DownloadSmallAsync(release.SignatureUri, MaxSignatureBytes, timeout.Token);
        if (!SignatureMatches(sidecar, signature, _publicKey))
        {
            throw new UpdateRejectedException("The release signature did not verify.");
        }

        ValidateChecksumSidecar(Encoding.ASCII.GetString(sidecar), release.InstallerFileName, release.InstallerSha256);
    }

    /// <param name="download">
    /// False on a metered connection: only an installer already on disk is
    /// used, for the price of the sidecar and .sig, and null comes back
    /// when there is none.
    /// </param>
    /// <param name="fetching">Called just before the installer's own bytes are requested.</param>
    public async Task<string?> DownloadAndVerifyAsync(
        AppUpdateRelease release,
        bool download,
        Action? fetching,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateRelease(release);
        if (!download && !IsOnDisk(release))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        Directory.CreateDirectory(_downloadRoot);
        var destination = GetInstallerPath(release);
        var partial = destination + ".partial";

        await VerifyAsync(release, timeout.Token);

        if (IsOnDisk(release) && await FileMatchesSha256Async(destination, release.InstallerSha256, timeout.Token))
        {
            return destination;
        }

        if (!download)
        {
            // Not the release's installer: no use on any connection.
            TryDelete(destination);
            return null;
        }

        TryDelete(partial);
        try
        {
            fetching?.Invoke();
            using var request = NewRequest(HttpMethod.Get, release.InstallerUri, json: false);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            ValidateFinalDownloadUri(response.RequestMessage?.RequestUri);

            if (response.Content.Headers.ContentLength is { } contentLength
                && contentLength != release.InstallerSize)
            {
                throw new InvalidDataException("The installer size does not match its release metadata.");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, timeout.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                    if (received > release.InstallerSize)
                    {
                        throw new InvalidDataException("The installer exceeded its declared size.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }

                if (received != release.InstallerSize)
                {
                    throw new InvalidDataException("The installer download ended before its declared size.");
                }
            }

            if (!await FileMatchesSha256Async(partial, release.InstallerSha256, timeout.Token))
            {
                throw new UpdateRejectedException("The installer checksum did not match the release.");
            }

            File.Move(partial, destination, overwrite: true);
            return destination;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>An installer of the release's size is on disk, from this run or an earlier one.</summary>
    public bool IsOnDisk(AppUpdateRelease release)
    {
        var path = GetInstallerPath(release);
        return File.Exists(path) && new FileInfo(path).Length == release.InstallerSize;
    }

    /// <summary>
    /// Verifies the installer one last time and starts it, unless
    /// <paramref name="starting"/>, asked after the last await, says no.
    /// False: nothing started.
    /// </summary>
    public async Task<bool> LaunchInstallerAsync(
        AppUpdateRelease release,
        string installerPath,
        Func<bool> starting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateRelease(release);

        var expectedPath = GetInstallerPath(release);
        var actualPath = Path.GetFullPath(installerPath);
        if (!actualPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded installer could not be verified before launch.");
        }

        // Held from the hash until setup has started: the bytes that run
        // are the bytes that were hashed.
        await using var held = OpenToHash(actualPath);
        if (held.Length != release.InstallerSize
            || !await MatchesSha256Async(held, release.InstallerSha256, cancellationToken))
        {
            throw new InvalidDataException("The downloaded installer could not be verified before launch.");
        }

        var versionInfo = FileVersionInfo.GetVersionInfo(actualPath);
        ValidateInstallerIdentity(
            versionInfo.ProductName,
            versionInfo.FileDescription,
            versionInfo.ProductVersion,
            release.Version);

        if (!starting())
        {
            return false;
        }

        _start(new ProcessStartInfo(actualPath)
        {
            Arguments = InstallerArguments(AppPaths.InDataRoot("update-install.log")),
            UseShellExecute = true,
            WorkingDirectory = _downloadRoot
        });
        DiagnosticLog.Write($"update installer launched version={release.VersionText}");
        return true;
    }

    private static void StartProcess(ProcessStartInfo start)
    {
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows did not start the installer.");
    }

    /// <summary>
    /// Silent, no message boxes, no reboot. /GLIMPSEUPDATE tells setup to
    /// skip the LibreOffice download and to relaunch Glimpse either way.
    /// </summary>
    internal static string InstallerArguments(string logPath) =>
        $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /GLIMPSEUPDATE /LOG=\"{logPath}\"";

    /// <summary>The release's page on GitHub, where its notes live.</summary>
    internal static Uri ReleasePageUri(Version version) =>
        new($"https://github.com/tekkusai/Glimpse/releases/tag/v{NormalizeVersion(version).ToString(3)}");

    public void CleanupStaleDownloads()
    {
        try
        {
            if (!Directory.Exists(_downloadRoot))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(_downloadRoot, "Glimpse-Setup-*.exe*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                var match = DownloadedInstallerNameRegex().Match(name);
                if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(path);
                    continue;
                }

                if (!match.Success
                    || !TryParseVersion(match.Groups["version"].Value, out var version)
                    || version > CurrentVersion)
                {
                    continue;
                }

                TryDelete(path);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"update cleanup skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static AppUpdateRelease? ParseRelease(GitHubRelease release, Version currentVersion)
    {
        if (release.Draft || release.Prerelease)
        {
            return null;
        }

        if (!TryParseVersion(release.TagName, out var version))
        {
            throw new InvalidDataException("The latest release tag is not a Glimpse version.");
        }

        version = NormalizeVersion(version);
        currentVersion = NormalizeVersion(currentVersion);
        if (version <= currentVersion)
        {
            return null;
        }

        var installerName = $"Glimpse-Setup-{version.ToString(3)}.exe";
        var checksumName = installerName + ".sha256";
        var installer = SingleUploadedAsset(release.Assets, installerName);
        var checksum = SingleUploadedAsset(release.Assets, checksumName);

        if (installer.Size is < MinInstallerBytes or > MaxInstallerBytes)
        {
            throw new InvalidDataException("The release installer has an unreasonable size.");
        }

        if (checksum.Size is <= 0 or > MaxChecksumBytes)
        {
            throw new InvalidDataException("The release checksum has an unreasonable size.");
        }

        if (!TryParseSha256Digest(installer.Digest, out var sha256))
        {
            throw new InvalidDataException("The release installer has no valid GitHub SHA-256 digest.");
        }

        if (!Uri.TryCreate(installer.BrowserDownloadUrl, UriKind.Absolute, out var installerUri)
            || !IsExpectedReleaseAssetUri(installerUri, release.TagName, installerName)
            || !Uri.TryCreate(checksum.BrowserDownloadUrl, UriKind.Absolute, out var checksumUri)
            || !IsExpectedReleaseAssetUri(checksumUri, release.TagName, checksumName))
        {
            throw new InvalidDataException("The release assets did not use the expected GitHub URLs.");
        }

        return new AppUpdateRelease(
            version,
            release.TagName,
            installerUri,
            installer.Size,
            sha256,
            checksumUri);
    }

    /// <summary>
    /// The signature asset, when the release has exactly one at the pinned
    /// URL. One listed but still uploading fails the check, as a sidecar
    /// would, and it is asked again later. Missing beside an uploaded
    /// installer and sidecar is not a failed check: the release is not
    /// offered until a check finds one.
    /// </summary>
    internal static Uri? FindSignatureUri(GitHubRelease release, AppUpdateRelease update)
    {
        var name = update.InstallerFileName + ".sha256.sig";
        var matches = release.Assets
            .OfType<GitHubAsset>()
            .Where(asset => string.Equals(asset.Name, name, StringComparison.Ordinal))
            .ToArray();
        if (matches.Any(asset => !string.Equals(asset.State, "uploaded", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"The release's {name} is still uploading.");
        }

        return matches is [{ Size: > 0 and <= MaxSignatureBytes } signature]
            && Uri.TryCreate(signature.BrowserDownloadUrl, UriKind.Absolute, out var uri)
            && IsExpectedReleaseAssetUri(uri, update.Tag, name)
            ? uri
            : null;
    }

    /// <summary>
    /// The .sig file is base64 of an IEEE P1363 ECDSA P-256 signature over
    /// the SHA-256 of the sidecar's exact bytes: what ECDsa.SignData returns.
    /// </summary>
    internal static bool SignatureMatches(byte[] sidecar, byte[] signatureFile, string publicKey)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(Encoding.ASCII.GetString(signatureFile).Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(publicKey);
        return signature.Length == 64 && key.VerifyData(sidecar, signature, HashAlgorithmName.SHA256);
    }

    internal static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        var match = ReleaseVersionRegex().Match(value?.Trim() ?? string.Empty);
        return match.Success
            && int.TryParse(match.Groups["major"].Value, out var major)
            && int.TryParse(match.Groups["minor"].Value, out var minor)
            && int.TryParse(match.Groups["patch"].Value, out var patch)
            && SetVersion(major, minor, patch, out version);
    }

    internal static void ValidateInstallerIdentity(
        string? productName,
        string? fileDescription,
        string? productVersion,
        Version expectedVersion)
    {
        if (!string.Equals(productName?.Trim(), "Glimpse", StringComparison.Ordinal)
            || !string.Equals(fileDescription?.Trim(), "Glimpse Setup", StringComparison.Ordinal)
            || !TryParseVersion(productVersion, out var parsedVersion)
            || NormalizeVersion(parsedVersion) != NormalizeVersion(expectedVersion))
        {
            throw new UpdateRejectedException(
                "The downloaded installer did not identify as the expected Glimpse Setup version.");
        }
    }

    internal static bool IsExpectedReleaseAssetUri(Uri uri, string tag, string fileName)
    {
        return uri.IsAbsoluteUri
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.AbsolutePath.Equals(
                $"/tekkusai/Glimpse/releases/download/{tag}/{fileName}",
                StringComparison.Ordinal);
    }

    internal static void ValidateChecksumSidecar(
        string sidecar,
        string expectedFileName,
        string expectedSha256)
    {
        var lines = sidecar.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1)
        {
            throw new UpdateRejectedException("The release checksum file was malformed.");
        }

        var match = ChecksumLineRegex().Match(lines[0]);
        if (!match.Success
            || !match.Groups["file"].Value.Equals(expectedFileName, StringComparison.Ordinal)
            || !match.Groups["hash"].Value.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateRejectedException("The release checksum file did not match the installer.");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static Version DetectCurrentVersion()
    {
        try
        {
            var path = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            if (TryParseVersion(productVersion, out var parsed))
            {
                return parsed;
            }
        }
        catch
        {
            // Fall through to the assembly identity.
        }

        return NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.None
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private HttpRequestMessage NewRequest(HttpMethod method, Uri uri, bool json)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Glimpse", CurrentVersionText));
        if (json)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
        }

        return request;
    }

    private async Task<byte[]> DownloadSmallAsync(
        Uri uri,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, uri, json: false);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        ValidateFinalDownloadUri(response.RequestMessage?.RequestUri);
        return await ReadLimitedAsync(response.Content, maxBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new InvalidDataException("The response exceeded its size limit.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException("The response exceeded its size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static GitHubAsset SingleUploadedAsset(IReadOnlyList<GitHubAsset>? assets, string expectedName)
    {
        var matches = assets?
            .OfType<GitHubAsset>()
            .Where(asset => string.Equals(asset.Name, expectedName, StringComparison.Ordinal)
                && string.Equals(asset.State, "uploaded", StringComparison.Ordinal))
            .ToArray() ?? [];
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException($"The release did not contain one uploaded {expectedName} asset.");
    }

    private static bool TryParseSha256Digest(string? digest, out string sha256)
    {
        sha256 = string.Empty;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = digest["sha256:".Length..];
        if (candidate.Length != 64 || candidate.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        sha256 = candidate.ToUpperInvariant();
        return true;
    }

    private static bool SetVersion(int major, int minor, int patch, out Version version)
    {
        try
        {
            version = new Version(major, minor, patch);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            version = new Version(0, 0, 0);
            return false;
        }
    }

    private static Version NormalizeVersion(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build));

    private void ValidateRelease(AppUpdateRelease release)
    {
        if (!TryParseVersion(release.Tag, out var tagVersion)
            || NormalizeVersion(tagVersion) != NormalizeVersion(release.Version)
            || release.Version <= CurrentVersion
            || release.InstallerSize is < MinInstallerBytes or > MaxInstallerBytes
            || !IsExpectedReleaseAssetUri(release.InstallerUri, release.Tag, release.InstallerFileName)
            || !IsExpectedReleaseAssetUri(release.ChecksumUri, release.Tag, release.InstallerFileName + ".sha256")
            || release.SignatureUri is { } signature
                && !IsExpectedReleaseAssetUri(signature, release.Tag, release.InstallerFileName + ".sha256.sig")
            || release.InstallerSha256.Length != 64
            || release.InstallerSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The update release metadata was invalid.");
        }
    }

    private string GetInstallerPath(AppUpdateRelease release)
    {
        var path = Path.GetFullPath(Path.Combine(_downloadRoot, release.InstallerFileName));
        var rootWithSeparator = _downloadRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update path escaped its download directory.");
        }

        return path;
    }

    private static void ValidateFinalDownloadUri(Uri? uri)
    {
        if (uri is null
            || !uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The release download left GitHub's HTTPS hosts.");
        }
    }

    /// <summary>
    /// Read-only, sharing only reads: while it is open nothing can write,
    /// replace, rename or delete the file. Windows can still start it.
    /// </summary>
    internal static FileStream OpenToHash(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<bool> FileMatchesSha256Async(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = OpenToHash(path);
        return await MatchesSha256Async(stream, expectedSha256, cancellationToken);
    }

    private static async Task<bool> MatchesSha256Async(
        Stream stream,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        var expected = Convert.FromHexString(expectedSha256);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale download is harmless and can be retried on the next run.
        }
    }

    [GeneratedRegex(@"^[vV]?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionRegex();

    [GeneratedRegex(@"^(?<hash>[A-Fa-f0-9]{64})  (?<file>Glimpse-Setup-[0-9]+\.[0-9]+\.[0-9]+\.exe)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLineRegex();

    [GeneratedRegex(@"^Glimpse-Setup-(?<version>[0-9]+\.[0-9]+\.[0-9]+)\.exe(?:\.partial)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadedInstallerNameRegex();

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; init; } = [];
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
