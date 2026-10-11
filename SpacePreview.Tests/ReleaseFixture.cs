using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// A release served the way GitHub serves one, signed with a throwaway key.
/// The metadata is the real /releases/latest response for v2.0.0, recorded
/// on 2026-10-11, with the version moved on and the .sig asset beside the
/// sidecar in the sidecar's own shape. No test reaches GitHub.
/// </summary>
internal sealed class ReleaseFixture : IDisposable
{
    public const long InstallerSize = 1024 * 1024;

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly object _gate = new();
    private readonly List<Uri> _requests = [];

    public ReleaseFixture(string version = "3.1.0", bool signed = true, DateTimeOffset? publishedAt = null)
    {
        Version = version;
        Installer = new byte[InstallerSize];
        Installer[0] = 0x47;
        Installer[^1] = 0x45;
        Sha256 = Convert.ToHexString(SHA256.HashData(Installer));
        Sidecar = Encoding.ASCII.GetBytes($"{Sha256}  {InstallerName}\r\n");
        Signature = Sign(Sidecar);
        PublicKey = _key.ExportSubjectPublicKeyInfoPem();
        PublishedAt = publishedAt ?? DateTimeOffset.UtcNow.AddDays(-3);
        Json = BuildJson(version, InstallerSize, Sha256, signed, PublishedAt);
    }

    public string Version { get; }

    public string InstallerName => $"Glimpse-Setup-{Version}.exe";

    public string Sha256 { get; }

    public DateTimeOffset PublishedAt { get; }

    /// <summary>The public half of the throwaway key; the app's own key never signs a test.</summary>
    public string PublicKey { get; }

    public byte[] Installer { get; set; }

    public byte[] Sidecar { get; set; }

    public byte[] Signature { get; set; }

    public string Json { get; set; }

    /// <summary>The release API answers 404: GitHub has no release.</summary>
    public bool NoRelease { get; set; }

    /// <summary>Every request fails as if offline.</summary>
    public bool Offline { get; set; }

    /// <summary>Holds the release check until released, to see Settings while it runs.</summary>
    public TaskCompletionSource? HoldCheck { get; set; }

    /// <summary>Holds the installer download until released.</summary>
    public TaskCompletionSource? HoldInstaller { get; set; }

    public static string Recorded => TestSource.Read(@"SpacePreview.Tests\Fixtures\github-release-v2.0.0.json");

    /// <summary>A never-started updater with no network: Settings before any check.</summary>
    public static AppUpdater QuietUpdater() =>
        new(new ReleaseFixture { Offline = true }.Service(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GlimpseUpdateTest-quiet")));

    public HttpClient Client() => new(new Handler(this));

    public AppUpdateService Service(string downloadRoot, string currentVersion = "3.0.0") =>
        new(Client(), new Version(currentVersion), downloadRoot, ownsHttpClient: true, publicKey: PublicKey);

    public byte[] Sign(byte[] data) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256)) + "\r\n");

    public int RequestsFor(string suffix)
    {
        lock (_gate)
        {
            return _requests.Count(uri => uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal));
        }
    }

    /// <summary>The recorded release, moved to <paramref name="version"/>.</summary>
    public static string BuildJson(
        string version,
        long installerSize,
        string installerSha256,
        bool signed,
        DateTimeOffset publishedAt)
    {
        var release = JsonNode.Parse(Recorded.Replace("2.0.0", version, StringComparison.Ordinal))!;
        release["published_at"] = publishedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var assets = release["assets"]!.AsArray();
        var installer = assets.Single(asset => (string?)asset!["name"] == $"Glimpse-Setup-{version}.exe")!;
        installer["size"] = installerSize;
        installer["digest"] = $"sha256:{installerSha256.ToLowerInvariant()}";
        if (signed)
        {
            var sidecar = assets.Single(asset => (string?)asset!["name"] == $"Glimpse-Setup-{version}.exe.sha256")!;
            var signature = sidecar.DeepClone();
            signature["id"] = 497493444;
            signature["name"] = $"Glimpse-Setup-{version}.exe.sha256.sig";
            signature["size"] = 90;
            signature["browser_download_url"] = (string)sidecar["browser_download_url"]! + ".sig";
            assets.Add(signature);
        }

        return release.ToJsonString();
    }

    public void Dispose() => _key.Dispose();

    private async Task<HttpResponseMessage> ServeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        lock (_gate)
        {
            _requests.Add(uri);
        }

        if (Offline)
        {
            throw new HttpRequestException("No route to GitHub in this test.");
        }

        var path = uri.AbsolutePath;
        var hold = uri == AppUpdateService.LatestReleaseUri ? HoldCheck
            : path.EndsWith(".exe", StringComparison.Ordinal) ? HoldInstaller
            : null;
        if (hold is not null)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        var body = uri == AppUpdateService.LatestReleaseUri ? (NoRelease ? null : Encoding.UTF8.GetBytes(Json))
            : path.EndsWith(".sha256.sig", StringComparison.Ordinal) ? Signature
            : path.EndsWith(".sha256", StringComparison.Ordinal) ? Sidecar
            : path.EndsWith(".exe", StringComparison.Ordinal) ? Installer
            : null;
        return new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(body ?? [])
        };
    }

    private sealed class Handler(ReleaseFixture release) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            release.ServeAsync(request, cancellationToken);
    }
}

/// <summary>
/// Sets the update fields of the process-wide settings for one test and
/// puts the previous settings back. Use it on the WPF test thread, where
/// every other settings write in the run also happens.
/// </summary>
internal sealed class UpdateSettingsScope : IDisposable
{
    private readonly Settings _saved = Settings.Current;

    public UpdateSettingsScope(Func<Settings, Settings>? change = null)
    {
        Settings.Update(settings => (change ?? (s => s))(settings with
        {
            InstallUpdatesAutomatically = true,
            LastUpdateCheckUtc = null,
            SkippedUpdateVersion = null,
            UpdateAttemptVersion = null,
            UpdateAttemptUtc = null
        }));
    }

    public void Dispose() => Settings.Update(_ => _saved);
}
