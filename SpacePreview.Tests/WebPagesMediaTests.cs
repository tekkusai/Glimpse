using System.Web;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The player: one cached page per kind and theme, the track in the query,
/// audio in the theme, video on black.
/// </summary>
public sealed class WebPagesMediaTests
{
    [Fact]
    public void ThePlayerPageIsSharedAcrossTracksAndVolumes()
    {
        using var temp = new TempDirectory();
        var first = WriteSilentWav(Path.Combine(temp.Path, "first take.wav"));
        var second = WriteSilentWav(Path.Combine(temp.Path, "second.wav"));

        var a = MediaPlayerPage.Build(first, Theme.Dark, savedVolume: 0.5, muted: false);
        var b = MediaPlayerPage.Build(second, Theme.Dark, savedVolume: 0.9, muted: true);

        // A new track or a volume change never rewrites the page.
        Assert.Equal(a.LocalPath, b.LocalPath);
        Assert.True(HtmlCache.IsComplete(a.LocalPath));

        var query = HttpUtility.ParseQueryString(b.Query);
        Assert.Equal(new Uri(second).AbsoluteUri, query["src"]);
        Assert.Equal("0.9", query["volume"]);
        Assert.Equal("1", query["muted"]);
        Assert.Equal(new Uri(first).AbsoluteUri, HttpUtility.ParseQueryString(a.Query)["src"]);

        // A saved volume that is not a number opens at the default instead of breaking the page.
        var odd = MediaPlayerPage.Build(first, Theme.Dark, savedVolume: double.NaN, muted: false);
        Assert.Equal("0.75", HttpUtility.ParseQueryString(odd.Query)["volume"]);
    }

    [Fact]
    public void PlayerPagesShareTheTokenBlockAndRevealThemselves()
    {
        WebPagesStyleTests.AssertSharedPageChrome(MediaPlayerPage.Page(isAudio: true, Theme.Current));
        WebPagesStyleTests.AssertSharedPageChrome(MediaPlayerPage.Page(isAudio: false, Theme.Current));
    }

    [Fact]
    public void AudioFollowsTheThemeAndVideoStaysOnBlack()
    {
        var lightAudio = MediaPlayerPage.Page(isAudio: true, Theme.Light);
        var lightVideo = MediaPlayerPage.Page(isAudio: false, Theme.Light);

        Assert.Contains("<html class=\"audio\">", lightAudio, StringComparison.Ordinal);
        Assert.Contains($"--canvas: {WebPagesStyleTests.Hex(Theme.Light.CanvasBg)};", lightAudio, StringComparison.Ordinal);
        Assert.Contains("--ctl-text: var(--text);", lightAudio, StringComparison.Ordinal);

        Assert.Contains("<html class=\"video\">", lightVideo, StringComparison.Ordinal);
        Assert.Contains("html.video { background: #000; }", lightVideo, StringComparison.Ordinal);
        // No black frame with controls before the picture.
        Assert.Contains("html.video:not(.ready) { animation: none; opacity: 0; }", lightVideo, StringComparison.Ordinal);
        Assert.Contains("media.addEventListener('loadeddata', ready, { once: true });", lightVideo, StringComparison.Ordinal);
        Assert.Contains($"--ctl-text: {WebPagesStyleTests.Hex(Theme.OverlayText)};", lightVideo, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryValuesNeverBecomeMarkup()
    {
        foreach (var page in new[] { MediaPlayerPage.Page(true, Theme.Dark), MediaPlayerPage.Page(false, Theme.Dark) })
        {
            Assert.Contains("element.textContent = value", page, StringComparison.Ordinal);
            Assert.DoesNotContain("innerHTML", page, StringComparison.Ordinal);
            Assert.DoesNotContain("outerHTML", page, StringComparison.Ordinal);
            Assert.DoesNotContain("insertAdjacentHTML", page, StringComparison.Ordinal);
            Assert.DoesNotContain("document.write", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ButtonsAndArrowKeysSkipTheSameStep()
    {
        var page = MediaPlayerPage.Page(isAudio: false, Theme.Dark);
        var nudge = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "public bool TryNudgeMediaFromArrow");

        Assert.Contains($"const SKIP = {MediaPlayerPage.SkipSeconds};", page, StringComparison.Ordinal);
        Assert.Equal(2, page.Split($">{MediaPlayerPage.SkipSeconds}</text>").Length - 1);
        Assert.Contains("window.glimpseSkip(-1)", nudge, StringComparison.Ordinal);
        Assert.Contains("window.glimpseSkip(1)", nudge, StringComparison.Ordinal);
        Assert.DoesNotContain("currentTime", nudge, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedArtworkIsReusedWithoutAskingTheShell()
    {
        var key = $"art-{Guid.NewGuid():N}";
        using var temp = new TempDirectory();

        // The file does not exist, so the shell cannot supply art for it.
        var missing = Path.Combine(temp.Path, "missing.mp3");
        Assert.Null(ShellThumbnailProvider.TryWriteThumbnailPng(missing, key));

        var art = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
        art.Freeze();
        var written = ShellThumbnailProvider.TryWriteThumbnailPng(
            new ShellImage(art, IsThumbnail: true),
            AppPaths.InDataRoot("MediaArtwork"),
            key);

        Assert.NotNull(written);
        Assert.Equal(written, ShellThumbnailProvider.TryWriteThumbnailPng(missing, key));
        Assert.Empty(Directory.GetFiles(AppPaths.InDataRoot("MediaArtwork"), "*.tmp"));
    }

    private static string WriteSilentWav(string path)
    {
        const int sampleRate = 8000;
        var samples = new byte[sampleRate / 10];
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate);
        writer.Write((short)1);
        writer.Write((short)8);
        writer.Write("data"u8);
        writer.Write(samples.Length);
        writer.Write(samples);
        return path;
    }
}
