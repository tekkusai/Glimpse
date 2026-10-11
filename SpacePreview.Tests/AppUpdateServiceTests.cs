using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class AppUpdateServiceTests
{
    private const long InstallerSize = 1024 * 1024;
    private const string TwoPointZeroParserHash = "D7B7D212CFE096F7D57E2827FF8E4F2C52F81E7CE6AB9D8F96C2911D3A807C50";

    [Fact]
    public void NewerStableReleaseUsesOnlyTheExactVersionedInstallerAndChecksum()
    {
        var release = BuildRelease("v1.1.0", new string('A', 64));

        var update = AppUpdateService.ParseRelease(release, new Version(1, 0, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 1, 0), update.Version);
        Assert.Equal("Glimpse-Setup-1.1.0.exe", update.InstallerFileName);
        Assert.Equal(new string('A', 64), update.InstallerSha256);
        Assert.Equal(
            "https://github.com/tekkusai/Glimpse/releases/download/v1.1.0/Glimpse-Setup-1.1.0.exe",
            update.InstallerUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("v1.0.9")]
    public void CurrentAndOlderReleasesAreNeverOffered(string tag)
    {
        var release = BuildRelease(tag, new string('A', 64));

        Assert.Null(AppUpdateService.ParseRelease(release, new Version(1, 1, 0)));
    }

    [Fact]
    public void DraftsAndPrereleasesAreNeverOffered()
    {
        var draft = BuildRelease("v1.1.0", new string('A', 64));
        draft = new AppUpdateService.GitHubRelease
        {
            TagName = draft.TagName,
            Draft = true,
            Assets = draft.Assets
        };
        var prerelease = new AppUpdateService.GitHubRelease
        {
            TagName = "v1.1.0",
            Prerelease = true,
            Assets = BuildRelease("v1.1.0", new string('A', 64)).Assets
        };

        Assert.Null(AppUpdateService.ParseRelease(draft, new Version(1, 0, 0)));
        Assert.Null(AppUpdateService.ParseRelease(prerelease, new Version(1, 0, 0)));
    }

    [Fact]
    public void NewReleaseWithoutGitHubsDigestFailsClosed()
    {
        var release = BuildRelease("v1.1.0", new string('A', 64));
        release.Assets[0] = new AppUpdateService.GitHubAsset
        {
            Name = release.Assets[0].Name,
            State = release.Assets[0].State,
            Size = release.Assets[0].Size,
            BrowserDownloadUrl = release.Assets[0].BrowserDownloadUrl,
            Digest = null
        };

        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseRelease(release, new Version(1, 0, 0)));
    }

    [Theory]
    [InlineData("1.1.0", 1, 1, 0)]
    [InlineData("v12.34.56", 12, 34, 56)]
    [InlineData("V2.0.0", 2, 0, 0)]
    public void ThreePartReleaseTagsUseSemanticVersionPrecedence(
        string tag,
        int major,
        int minor,
        int patch)
    {
        Assert.True(AppUpdateService.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("1.1.0.1")]
    [InlineData("v1.1.0-beta")]
    [InlineData("01.1.0")]
    [InlineData("release-1.1.0")]
    public void AmbiguousReleaseTagsAreRejected(string tag)
    {
        Assert.False(AppUpdateService.TryParseVersion(tag, out _));
    }

    [Fact]
    public void InstallerIdentityMustMatchTheExpectedProductAndVersion()
    {
        AppUpdateService.ValidateInstallerIdentity(
            " Glimpse ",
            " Glimpse Setup ",
            "1.1.0",
            new Version(1, 1, 0));
    }

    [Theory]
    [InlineData("Other", "Glimpse Setup", "1.1.0")]
    [InlineData(null, "Glimpse Setup", "1.1.0")]
    [InlineData("Glimpse", "Other Setup", "1.1.0")]
    [InlineData("Glimpse", null, "1.1.0")]
    [InlineData("Glimpse", "Glimpse Setup", "1.0.0")]
    [InlineData("Glimpse", "Glimpse Setup", "1.1.0-beta")]
    [InlineData("Glimpse", "Glimpse Setup", null)]
    public void InstallerIdentityRejectsWrongOrAmbiguousMetadata(
        string? productName,
        string? fileDescription,
        string? productVersion)
    {
        Assert.Throws<UpdateRejectedException>(() => AppUpdateService.ValidateInstallerIdentity(
            productName,
            fileDescription,
            productVersion,
            new Version(1, 1, 0)));
    }

    [Theory]
    [InlineData("http://github.com/tekkusai/Glimpse/releases/download/v1.1.0/Glimpse-Setup-1.1.0.exe")]
    [InlineData("https://example.com/tekkusai/Glimpse/releases/download/v1.1.0/Glimpse-Setup-1.1.0.exe")]
    [InlineData("https://github.com/other/Glimpse/releases/download/v1.1.0/Glimpse-Setup-1.1.0.exe")]
    [InlineData("https://github.com/tekkusai/Glimpse/releases/download/v1.1.0/Glimpse-Setup-1.1.0.exe?other=1")]
    [InlineData("https://github.com/tekkusai/Glimpse/releases/download/v1.1.0/Other.exe")]
    public void ReleaseAssetUrlsCannotLeaveTheExpectedRepositoryOrName(string value)
    {
        Assert.False(AppUpdateService.IsExpectedReleaseAssetUri(
            new Uri(value),
            "v1.1.0",
            "Glimpse-Setup-1.1.0.exe"));
    }

    [Fact]
    public void ChecksumSidecarMustNameAndHashTheSameInstaller()
    {
        var hash = new string('B', 64);
        AppUpdateService.ValidateChecksumSidecar(
            $"{hash.ToLowerInvariant()}  Glimpse-Setup-1.1.0.exe\r\n",
            "Glimpse-Setup-1.1.0.exe",
            hash);

        Assert.Throws<UpdateRejectedException>(() => AppUpdateService.ValidateChecksumSidecar(
            $"{hash}  Other.exe\r\n",
            "Glimpse-Setup-1.1.0.exe",
            hash));
        Assert.Throws<UpdateRejectedException>(() => AppUpdateService.ValidateChecksumSidecar(
            $"{new string('C', 64)}  Glimpse-Setup-1.1.0.exe\r\n",
            "Glimpse-Setup-1.1.0.exe",
            hash));
    }

    [Fact]
    public async Task GitHubCheckUsesThePublicLatestReleaseEndpointAndRequiredHeaders()
    {
        var json = BuildReleaseJson("v1.1.0", new string('A', 64));
        var handler = new RecordingHandler(request => Response(request, json));
        using var client = new HttpClient(handler);
        using var service = new AppUpdateService(
            client,
            new Version(1, 0, 0),
            Path.Combine(Path.GetTempPath(), $"GlimpseUpdateTest-{Guid.NewGuid():N}"));

        var update = await service.CheckForUpdateAsync(CancellationToken.None);

        Assert.NotNull(update);
        Assert.Equal(AppUpdateService.LatestReleaseUri, handler.LastRequest?.RequestUri);
        Assert.Contains("application/vnd.github+json", handler.LastRequest!.Headers.Accept.ToString());
        Assert.Contains("Glimpse/1.0.0", handler.LastRequest.Headers.UserAgent.ToString());
        Assert.Equal("2026-03-10", handler.LastRequest.Headers.GetValues("X-GitHub-Api-Version").Single());
    }

    [Fact]
    public async Task DownloadIsKeptOnlyWhenSignatureDigestSidecarSizeAndBytesAgree()
    {
        using var fixture = new ReleaseFixture();
        using var root = new TempDirectory();
        using var service = fixture.Service(root.Path);
        var release = (await service.CheckForUpdateAsync(CancellationToken.None))!;

        var path = (await service.DownloadAndVerifyAsync(release, download: true, CancellationToken.None))!;

        Assert.True(File.Exists(path));
        Assert.Equal(fixture.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        Assert.False(File.Exists(path + ".partial"));
        Assert.Equal(1, fixture.RequestsFor(".sha256.sig"));
    }

    [Fact]
    public async Task TamperedDownloadIsRejectedAndItsPartialFileIsRemoved()
    {
        using var fixture = new ReleaseFixture();
        using var root = new TempDirectory();
        using var service = fixture.Service(root.Path);
        var release = (await service.CheckForUpdateAsync(CancellationToken.None))!;
        fixture.Installer = (byte[])fixture.Installer.Clone();
        fixture.Installer[^1] = 0xFF;

        await Assert.ThrowsAsync<UpdateRejectedException>(() =>
            service.DownloadAndVerifyAsync(release, download: true, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.partial", SearchOption.TopDirectoryOnly));
        Assert.False(File.Exists(Path.Combine(root.Path, release.InstallerFileName)));
    }

    [Fact]
    public async Task TheCheckCarriesTheSignatureAndThePublishDate()
    {
        var published = new DateTimeOffset(2026, 10, 9, 14, 30, 0, TimeSpan.Zero);
        using var fixture = new ReleaseFixture(publishedAt: published);
        using var root = new TempDirectory();
        using var service = fixture.Service(root.Path);

        var release = (await service.CheckForUpdateAsync(CancellationToken.None))!;

        Assert.Equal(
            "https://github.com/tekkusai/Glimpse/releases/download/v3.1.0/Glimpse-Setup-3.1.0.exe.sha256.sig",
            release.SignatureUri?.AbsoluteUri);
        Assert.Equal(published, release.PublishedAt);
    }

    [Fact]
    public async Task ASignedReleaseVerifiesWithItsKey()
    {
        using var fixture = new ReleaseFixture();
        using var root = new TempDirectory();
        using var service = fixture.Service(root.Path);
        var release = (await service.CheckForUpdateAsync(CancellationToken.None))!;

        await service.VerifyAsync(release, CancellationToken.None);

        Assert.Equal(1, fixture.RequestsFor(".sha256"));
        Assert.Equal(1, fixture.RequestsFor(".sha256.sig"));
    }

    [Fact]
    public async Task AReleaseWithoutItsSignatureIsRejectedBeforeAnythingDownloads()
    {
        using var fixture = new ReleaseFixture(signed: false);
        using var root = new TempDirectory();
        using var service = fixture.Service(root.Path);
        var release = (await service.CheckForUpdateAsync(CancellationToken.None))!;

        Assert.Null(release.SignatureUri);
        await Assert.ThrowsAsync<UpdateRejectedException>(() => service.VerifyAsync(release, CancellationToken.None));
        await Assert.ThrowsAsync<UpdateRejectedException>(() => service.DownloadAndVerifyAsync(release, download: true, CancellationToken.None));
        Assert.Equal(0, fixture.RequestsFor(".sha256"));
        Assert.Equal(0, fixture.RequestsFor(".exe"));
    }

    [Fact]
    public async Task ASignatureFromAnyOtherKeyIsRejected()
    {
        // Signed by a stranger, checked with the key compiled into Glimpse.
        using var fixture = new ReleaseFixture();
        using var root = new TempDirectory();
        using var service = new AppUpdateService(fixture.Client(), new Version(3, 0, 0), root.Path, ownsHttpClient: true);
        var release = (await service.CheckForUpdateAsync(CancellationToken.None))!;

        await Assert.ThrowsAsync<UpdateRejectedException>(() => service.VerifyAsync(release, CancellationToken.None));
        await Assert.ThrowsAsync<UpdateRejectedException>(() => service.DownloadAndVerifyAsync(release, download: true, CancellationToken.None));
        Assert.Equal(0, fixture.RequestsFor(".exe"));
    }

    [Fact]
    public async Task ASidecarChangedAfterSigningIsRejected()
    {
        using var fixture = new ReleaseFixture();
        using var root = new TempDirectory();
        using var service = fixture.Service(root.Path);
        var release = (await service.CheckForUpdateAsync(CancellationToken.None))!;
        fixture.Sidecar = Encoding.ASCII.GetBytes($"{fixture.Sha256}  {fixture.InstallerName}\n");

        await Assert.ThrowsAsync<UpdateRejectedException>(() => service.VerifyAsync(release, CancellationToken.None));
    }

    [Fact]
    public async Task ASignedSidecarForAnotherInstallerIsRejected()
    {
        // A good signature proves the maintainer wrote the sidecar; the
        // sidecar must still name the installer GitHub hashed.
        using var fixture = new ReleaseFixture();
        using var root = new TempDirectory();
        using var service = fixture.Service(root.Path);
        var release = (await service.CheckForUpdateAsync(CancellationToken.None))!;
        fixture.Sidecar = Encoding.ASCII.GetBytes($"{new string('C', 64)}  {fixture.InstallerName}\r\n");
        fixture.Signature = fixture.Sign(fixture.Sidecar);

        await Assert.ThrowsAsync<UpdateRejectedException>(() => service.VerifyAsync(release, CancellationToken.None));
    }

    [Fact]
    public void TheSignatureIsP1363OverTheExactSidecarBytes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var sidecar = Encoding.ASCII.GetBytes($"{new string('A', 64)}  Glimpse-Setup-3.1.0.exe\r\n");
        var p1363 = key.SignData(sidecar, HashAlgorithmName.SHA256);
        var der = key.SignData(sidecar, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        static byte[] SigFile(byte[] signature) => Encoding.ASCII.GetBytes(Convert.ToBase64String(signature) + "\n");

        Assert.True(AppUpdateService.SignatureMatches(sidecar, SigFile(p1363), publicKey));
        Assert.False(AppUpdateService.SignatureMatches(sidecar, SigFile(der), publicKey));
        Assert.False(AppUpdateService.SignatureMatches(sidecar, SigFile(p1363), AppUpdateService.ReleasePublicKey));
        Assert.False(AppUpdateService.SignatureMatches(sidecar[..^1], SigFile(p1363), publicKey));
        Assert.False(AppUpdateService.SignatureMatches(sidecar, Encoding.ASCII.GetBytes("not base64!"), publicKey));
    }

    [Fact]
    public void TheCompiledKeyIsTheMaintainersP256ReleaseKey()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(AppUpdateService.ReleasePublicKey);

        Assert.Equal(256, key.KeySize);
        Assert.Equal("nistP256", key.ExportParameters(false).Curve.Oid.FriendlyName);
        Assert.Equal(
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEQH1GH3d4xhGWICFol2t/gwGEx15LasX0TBrY0tVeuOTzGUDwaDOpGM0UbNzJYAeTLeIv20j+wPENSUw1CynCLg==",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void TwoPointZeroTakesAReleaseThatCarriesItsSignatureAsAThirdAsset()
    {
        // Every 2.0 copy meets 3.0 through its own updater. This parser is
        // the one v2.0.0 shipped, pinned below, so this is 2.0 reading
        // 3.0's release with the .sig beside the sidecar.
        var json = ReleaseFixture.BuildJson("3.0.0", 63_748_402, new string('a', 64), signed: true, DateTimeOffset.UtcNow);
        var release = JsonSerializer.Deserialize<AppUpdateService.GitHubRelease>(json)!;

        var update = AppUpdateService.ParseRelease(release, new Version(2, 0, 0));

        Assert.Equal(3, release.Assets.Count);
        Assert.NotNull(update);
        Assert.Equal(new Version(3, 0, 0), update.Version);
        Assert.Equal(
            "https://github.com/tekkusai/Glimpse/releases/download/v3.0.0/Glimpse-Setup-3.0.0.exe",
            update.InstallerUri.AbsoluteUri);
        Assert.Equal(
            "https://github.com/tekkusai/Glimpse/releases/download/v3.0.0/Glimpse-Setup-3.0.0.exe.sha256",
            update.ChecksumUri.AbsoluteUri);
    }

    [Fact]
    public void TheRecordedReleaseIsOneTheParserTakes()
    {
        var release = JsonSerializer.Deserialize<AppUpdateService.GitHubRelease>(ReleaseFixture.Recorded)!;

        var update = AppUpdateService.ParseRelease(release, new Version(1, 1, 2));

        Assert.NotNull(update);
        Assert.Equal("Glimpse-Setup-2.0.0.exe", update.InstallerFileName);
        Assert.Equal(63_748_402, update.InstallerSize);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 3, 28, 23, TimeSpan.Zero), release.PublishedAt);
    }

    [Fact]
    public void TheReleaseParserIsStillTheOneTwoPointZeroShipped()
    {
        // A hash of the parse path as v2.0.0 has it. When this fails, the
        // test above no longer runs 2.0's logic: copy the v2.0.0 parser into
        // it before changing this one.
        var source = string.Concat(new[]
        {
            "ParseRelease",
            "SingleUploadedAsset",
            "IsExpectedReleaseAssetUri",
            "TryParseSha256Digest",
            "TryParseVersion"
        }.Select(name => TestSource.Method(@"SpacePreview\Services\AppUpdateService.cs", name)));
        var normalized = Regex.Replace(source, @"\s+", " ");

        Assert.Equal(
            TwoPointZeroParserHash,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
    }

    [Fact]
    public void SilentSetupNeverShowsAMessageBox()
    {
        Assert.Equal(
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /GLIMPSEUPDATE /LOG=\"C:\\data\\update-install.log\"",
            AppUpdateService.InstallerArguments(@"C:\data\update-install.log"));
    }

    [Fact]
    public void StartupCleanupDropsPartialsAndOldInstallersButKeepsANewerVerifiedCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"GlimpseUpdateTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var partial = Path.Combine(root, "Glimpse-Setup-1.2.0.exe.partial");
        var current = Path.Combine(root, "Glimpse-Setup-1.1.0.exe");
        var future = Path.Combine(root, "Glimpse-Setup-1.2.0.exe");
        File.WriteAllBytes(partial, [1]);
        File.WriteAllBytes(current, [2]);
        File.WriteAllBytes(future, [3]);
        using var client = new HttpClient(new RecordingHandler(request => Response(request, "{}")));
        using var service = new AppUpdateService(
            client,
            new Version(1, 1, 0),
            root);

        try
        {
            service.CleanupStaleDownloads();

            Assert.False(File.Exists(partial));
            Assert.False(File.Exists(current));
            Assert.True(File.Exists(future));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AppUpdateService.GitHubRelease BuildRelease(string tag, string digest)
    {
        var version = tag.TrimStart('v');
        var installer = $"Glimpse-Setup-{version}.exe";
        var root = $"https://github.com/tekkusai/Glimpse/releases/download/{tag}";
        return new AppUpdateService.GitHubRelease
        {
            TagName = tag,
            Assets =
            [
                new AppUpdateService.GitHubAsset
                {
                    Name = installer,
                    State = "uploaded",
                    Size = InstallerSize,
                    Digest = $"sha256:{digest}",
                    BrowserDownloadUrl = $"{root}/{installer}"
                },
                new AppUpdateService.GitHubAsset
                {
                    Name = installer + ".sha256",
                    State = "uploaded",
                    Size = 91,
                    BrowserDownloadUrl = $"{root}/{installer}.sha256"
                }
            ]
        };
    }

    private static string BuildReleaseJson(string tag, string digest)
    {
        var version = tag.TrimStart('v');
        var installer = $"Glimpse-Setup-{version}.exe";
        var root = $"https://github.com/tekkusai/Glimpse/releases/download/{tag}";
        return $$"""
            {
              "tag_name": "{{tag}}",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "{{installer}}",
                  "state": "uploaded",
                  "size": {{InstallerSize}},
                  "digest": "sha256:{{digest}}",
                  "browser_download_url": "{{root}}/{{installer}}"
                },
                {
                  "name": "{{installer}}.sha256",
                  "state": "uploaded",
                  "size": 91,
                  "browser_download_url": "{{root}}/{{installer}}.sha256"
                }
              ]
            }
            """;
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, string content) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responseFactory(request));
        }
    }
}
