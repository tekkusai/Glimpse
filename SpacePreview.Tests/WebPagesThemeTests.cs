using Microsoft.Web.WebView2.Core;
using SpacePreview.Preview;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// A page is the same text in either theme. It is written and cached once,
/// and an open one turns when the browser's color scheme does, without
/// loading again.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class WebPagesThemeTests
{
    private const string Logo = """<svg xmlns="http://www.w3.org/2000/svg" width="120" height="40"/>""";

    [Fact]
    public void APageIsWrittenOnceForBothThemes() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var notes = temp.CreateFile("notes.md", "# Notes");
        var code = temp.CreateFile("app.json", "{\"a\":1}");
        var logo = temp.CreateFile("logo.svg", Logo);
        var track = temp.CreateFile("take.wav", "RIFF");
        string[] Pages() =>
        [
            GeneratedHtmlPreview.TryBuildMarkdown(notes)!.HtmlPath,
            GeneratedHtmlPreview.TryBuildCodeForPreview(code).Document!.HtmlPath,
            ImagePage.Build(logo).LocalPath,
            MediaPlayerPage.Build(track, savedVolume: 0.5, muted: false).LocalPath
        ];

        try
        {
            Theme.Use(ThemePreference.Light);
            var light = Pages();
            var written = light.Select(File.GetLastWriteTimeUtc).ToArray();
            Theme.Use(ThemePreference.Dark);

            Assert.Equal(light, Pages());
            Assert.Equal(written, light.Select(File.GetLastWriteTimeUtc));
        }
        finally
        {
            Theme.Use(ThemePreference.Dark);
        }
    });

    // Read from a real browser on a window that is never shown: the canvas
    // each kind of page paints under the dark scheme, then under the light
    // one, with no navigation between the two.
    [Fact]
    public Task AnOpenPageOfEveryKindTurnsWithTheTheme() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var sheet = temp.CreateFile("sheet.html", SheetHtml.Page("Budget", "<table><colgroup width=\"80\"></colgroup><tr><td>1</td></tr></table>"));
        var pages = new[]
        {
            GeneratedHtmlPreview.TryBuildMarkdown(temp.CreateFile("notes.md", "# Notes"))!.Source,
            ImagePage.Build(temp.CreateFile("logo.svg", Logo)),
            MediaPlayerPage.Build(temp.CreateFile("take.wav", "RIFF"), savedVolume: 0.5, muted: true),
            new Uri(sheet)
        };

        await HiddenBrowser.LoadAsync(
            await WebViewEnvironment.GetAsync(),
            pages[0],
            core => core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark,
            async core =>
            {
                foreach (var page in pages)
                {
                    core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
                    await HiddenBrowser.NavigateAsync(core, page);

                    Assert.Equal(Rgb(Theme.Dark.CanvasBg), await CanvasAsync(core));
                    core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
                    Assert.Equal(Rgb(Theme.Light.CanvasBg), await CanvasAsync(core));
                }

                return 0;
            });
    });

    // Calc's export can paint its own page color on body, and a one-sheet
    // workbook is shown as exported. 3.0.0 put the canvas on body too; the
    // shared stylesheet left it on html alone.
    [Fact]
    public Task ASheetWhoseExportPaintsItsBodyStillShowsTheCanvas() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var sheet = temp.CreateFile(
            "sheet.html",
            "<html><head></head><body bgcolor=\"#ffffff\" text=\"#000000\"><table><tr><td>1</td></tr></table></body></html>");
        SheetHtml.Normalize(sheet);

        await HiddenBrowser.LoadAsync(
            await WebViewEnvironment.GetAsync(),
            new Uri(sheet),
            core => core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark,
            async core =>
            {
                Assert.Equal(
                    Rgb(Theme.Dark.CanvasBg),
                    await core.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor"));
                return 0;
            });
    });

    private static Task<string> CanvasAsync(CoreWebView2 core) =>
        core.ExecuteScriptAsync("getComputedStyle(document.documentElement).backgroundColor");

    private static string Rgb(System.Windows.Media.Color color) => $"\"rgb({color.R}, {color.G}, {color.B})\"";
}
