using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class AppUpdateServiceTests
{
    private const long InstallerSize = 1024 * 1024;

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

        Assert.Throws<InvalidDataException>(() => AppUpdateService.ValidateChecksumSidecar(
            $"{hash}  Other.exe\r\n",
            "Glimpse-Setup-1.1.0.exe",
            hash));
        Assert.Throws<InvalidDataException>(() => AppUpdateService.ValidateChecksumSidecar(
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
    public async Task DownloadIsKeptOnlyWhenGitHubDigestSidecarSizeAndBytesAgree()
    {
        var installer = new byte[InstallerSize];
        installer[0] = 0x47;
        installer[^1] = 0x45;
        var hash = Convert.ToHexString(SHA256.HashData(installer));
        var release = AppUpdateService.ParseRelease(BuildRelease("v1.1.0", hash), new Version(1, 0, 0))!;
        var root = Path.Combine(Path.GetTempPath(), $"GlimpseUpdateTest-{Guid.NewGuid():N}");
        var handler = new RecordingHandler(request => AssetResponse(request, release, installer, hash));
        using var client = new HttpClient(handler);
        using var service = new AppUpdateService(client, new Version(1, 0, 0), root);

        try
        {
            var path = await service.DownloadAndVerifyAsync(release, null, CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
            Assert.False(File.Exists(path + ".partial"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TamperedDownloadIsRejectedAndItsPartialFileIsRemoved()
    {
        var expected = new byte[InstallerSize];
        expected[0] = 0x47;
        var tampered = (byte[])expected.Clone();
        tampered[^1] = 0xFF;
        var hash = Convert.ToHexString(SHA256.HashData(expected));
        var release = AppUpdateService.ParseRelease(BuildRelease("v1.1.0", hash), new Version(1, 0, 0))!;
        var root = Path.Combine(Path.GetTempPath(), $"GlimpseUpdateTest-{Guid.NewGuid():N}");
        var handler = new RecordingHandler(request => AssetResponse(request, release, tampered, hash));
        using var client = new HttpClient(handler);
        using var service = new AppUpdateService(client, new Version(1, 0, 0), root);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadAndVerifyAsync(release, null, CancellationToken.None));

            Assert.Empty(Directory.EnumerateFiles(root, "*.partial", SearchOption.TopDirectoryOnly));
            Assert.False(File.Exists(Path.Combine(root, release.InstallerFileName)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void UpdateWindowKeepsConsentProgressCancellationAndRetryExplicit()
    {
        var xaml = TestSource.Read(@"SpacePreview\Services\AppUpdateWindow.xaml");
        var code = TestSource.Read(@"SpacePreview\Services\AppUpdateWindow.xaml.cs");

        Assert.Contains("Download and install", xaml, StringComparison.Ordinal);
        Assert.Contains("Update download progress", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Topmost=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Cancel update download", code, StringComparison.Ordinal);
        Assert.Contains("SecondaryButton.Click += (_, _) => OnSecondaryAction()", code, StringComparison.Ordinal);
        Assert.Contains("SnoozeRequested = false", TestSource.CodeMethod(
            @"SpacePreview\Services\AppUpdateWindow.xaml.cs",
            "CancelDownload"), StringComparison.Ordinal);
        Assert.Contains("Try update again", code, StringComparison.Ordinal);
        Assert.Contains("args.Key == Key.Escape", code, StringComparison.Ordinal);
        Assert.Contains("_downloadCancellation?.IsCancellationRequested == true", code, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException ex)", code, StringComparison.Ordinal);
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

    private static HttpResponseMessage AssetResponse(
        HttpRequestMessage request,
        AppUpdateRelease release,
        byte[] installer,
        string hash)
    {
        if (request.RequestUri == release.ChecksumUri)
        {
            return Response(request, $"{hash}  {release.InstallerFileName}\r\n");
        }

        Assert.Equal(release.InstallerUri, request.RequestUri);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(installer)
        };
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
