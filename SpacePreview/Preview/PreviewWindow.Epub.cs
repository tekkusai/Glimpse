using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    // The rail's last tile when the page stops before the book does.
    private const int EpubRestOfBookTile = int.MaxValue;

    // The section whose top has passed a line a little below the top of the
    // view. ExecuteScriptAsync runs even with page scripts off.
    private const string EpubCurrentSectionScript =
        "(()=>{let id=null;const y=window.scrollY+96;"
        + "for(const s of document.querySelectorAll('section[data-title]')){if(s.offsetTop<=y)id=s.id;else break;}"
        + "return id;})()";

    private sealed class EpubRailState(
        ContentControl host,
        ColumnDefinition railColumn,
        WebView2 webView,
        long version,
        CancellationToken token)
    {
        public ContentControl Host { get; } = host;
        public ColumnDefinition RailColumn { get; } = railColumn;
        public WebView2 WebView { get; } = webView;
        public long Version { get; } = version;
        public CancellationToken Token { get; } = token;
        public bool IsVisible { get; set; } = true;
        public int CurrentSpineIndex { get; set; } = -1;
        public double RailVerticalOffset { get; set; }
        public Dictionary<int, Border> ChapterTiles { get; } = [];
        public Border? SelectedChapterTile { get; set; }
        public EpubPreviewLoader.EpubContents Contents { get; set; } = new([], false);
        public DispatcherTimer? ScrollSync { get; set; }
        public bool ScrollSyncInFlight { get; set; }
    }

    /// <summary>
    /// EPUB keeps the whole book as one continuous page; the rail is
    /// orientation, not pagination. A tile scrolls the page to its section,
    /// and scrolling the page moves the highlight.
    /// </summary>
    private FrameworkElement BuildEpubWebPreview(GeneratedHtmlDocument document, string path, CancellationToken token)
    {
        var grid = new Grid { Background = Brushes.Transparent };
        var railColumn = new ColumnDefinition { Width = new GridLength(0) };
        grid.ColumnDefinitions.Add(railColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var webView = CreateWebView(
            WebSurfaceKind.Page,
            () => document.Source,
            $"epub preview {path}",
            ex => _ = ShowTextOrFileFallbackAsync(document.SourcePath),
            configure: core => HardenForGeneratedHtml(core, document));
        Grid.SetColumn(webView, 1);
        var railHost = BuildPdfPageRailHost();
        Grid.SetColumn(railHost, 0);
        grid.Children.Add(railHost);
        grid.Children.Add(webView);
        var railState = new EpubRailState(railHost, railColumn, webView, _loadVersion, token);
        grid.Loaded += (_, _) => StartEpubScrollSync(railState);
        grid.Unloaded += (_, _) => StopEpubScrollSync(railState);
        _ = LoadEpubChapterRailAsync(railState, document.HtmlPath);
        return grid;
    }

    private async Task LoadEpubChapterRailAsync(EpubRailState state, string htmlPath)
    {
        try
        {
            var contents = await Task.Run(() => EpubPreviewLoader.ReadContents(htmlPath), state.Token);
            if (state.Version != _loadVersion || state.Token.IsCancellationRequested)
            {
                return;
            }

            state.Contents = contents;
            // Cloak on the first render too: if the page has already
            // revealed, the rail's arrival would otherwise reflow it naked.
            RenderEpubChapterRail(state, cloakViewer: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"epub rail failed {htmlPath}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RenderEpubChapterRail(EpubRailState state, bool cloakViewer = false)
    {
        state.ChapterTiles.Clear();
        state.SelectedChapterTile = null;
        if (state.Contents.Sections.Count <= 1)
        {
            state.RailColumn.Width = new GridLength(0);
            AnimateRail(state.Host, 0, null);
            return;
        }

        var targetWidth = state.IsVisible ? EpubChapterRailWidth : PdfPageRailCollapsedWidth;
        state.RailColumn.Width = new GridLength(targetWidth);
        var content = state.IsVisible
            ? BuildEpubChapterRail(
                state.Contents,
                state.CurrentSpineIndex,
                state.RailVerticalOffset,
                offset => state.RailVerticalOffset = offset,
                index =>
                {
                    if (index == EpubRestOfBookTile)
                    {
                        OpenCurrent();
                    }
                    else
                    {
                        JumpEpubToChapter(state, index);
                    }
                },
                (index, tile, selected) =>
                {
                    state.ChapterTiles[index] = tile;
                    if (selected)
                    {
                        state.SelectedChapterTile = tile;
                    }
                },
                () =>
                {
                    state.IsVisible = false;
                    RenderEpubChapterRail(state, cloakViewer: true);
                })
            : BuildCollapsedRail(state.Contents.Sections.Count, "Show contents", () =>
            {
                state.IsVisible = true;
                RenderEpubChapterRail(state, cloakViewer: true);
            });

        if (cloakViewer)
        {
            CloakViewerForRailResize(state.WebView, state.Version);
        }

        AnimateRail(state.Host, targetWidth, content);
    }

    private void JumpEpubToChapter(EpubRailState state, int spineIndex)
    {
        SetEpubCurrentChapter(state, spineIndex);
        if (state.WebView.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            _ = core.ExecuteScriptAsync($"document.getElementById('ch-{spineIndex}')?.scrollIntoView()");
        }
        catch
        {
            // A disposed or navigating view drops the jump; the rail is intact.
        }
    }

    private void StartEpubScrollSync(EpubRailState state)
    {
        StopEpubScrollSync(state);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += async (_, _) =>
        {
            if (state.ScrollSyncInFlight
                || !state.IsVisible
                || state.ChapterTiles.Count == 0
                || state.WebView.CoreWebView2 is not { } core)
            {
                return;
            }

            state.ScrollSyncInFlight = true;
            try
            {
                var result = await core.ExecuteScriptAsync(EpubCurrentSectionScript);
                if (ParseSectionAnchor(result) is { } index && index != state.CurrentSpineIndex)
                {
                    SetEpubCurrentChapter(state, index);
                    state.SelectedChapterTile?.BringIntoView();
                }
            }
            catch
            {
                // A view mid-navigation answers next time.
            }
            finally
            {
                state.ScrollSyncInFlight = false;
            }
        };
        state.ScrollSync = timer;
        timer.Start();
    }

    private static void StopEpubScrollSync(EpubRailState state)
    {
        state.ScrollSync?.Stop();
        state.ScrollSync = null;
    }

    /// <summary>The spine index from a script result such as "ch-12"; null for anything else.</summary>
    internal static int? ParseSectionAnchor(string? scriptResult)
    {
        var value = scriptResult?.Trim().Trim('"');
        return value is not null
               && value.StartsWith("ch-", StringComparison.Ordinal)
               && int.TryParse(value.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? index
            : null;
    }

    private static void SetEpubCurrentChapter(EpubRailState state, int spineIndex)
    {
        state.CurrentSpineIndex = spineIndex;
        if (!state.IsVisible || state.ChapterTiles.Count == 0)
        {
            return;
        }

        var palette = Theme.Current;
        if (state.SelectedChapterTile is not null)
        {
            ApplyRailTileState(state.SelectedChapterTile, palette, selected: false);
        }

        state.SelectedChapterTile = null;
        if (state.ChapterTiles.TryGetValue(spineIndex, out var tile))
        {
            state.SelectedChapterTile = tile;
            ApplyRailTileState(tile, palette, selected: true);
        }
    }

    private static FrameworkElement BuildEpubChapterRail(
        EpubPreviewLoader.EpubContents contents,
        int selectedSpineIndex,
        double verticalOffset,
        Action<double> rememberScroll,
        Action<int> navigate,
        Action<int, Border, bool> registerTile,
        Action hide)
    {
        var palette = Theme.Current;
        var stack = new StackPanel { Margin = new Thickness(8, 0, 8, 12) };

        var header = new Grid { Margin = new Thickness(12, 8, 8, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Contents",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextTertiary),
            FontSize = Metrics.FontCaption,
            FontWeight = FontWeights.SemiBold
        });
        var hideButton = BuildRailChromeButton("", "Hide contents", hide);
        Grid.SetColumn(hideButton, 1);
        header.Children.Add(hideButton);

        Border Tile(string title, string? caption, string tooltip)
        {
            var lines = new StackPanel();
            lines.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = Metrics.FontCaption,
                Foreground = Theme.Brush(palette.TextSecondary),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (caption is not null)
            {
                lines.Children.Add(new TextBlock
                {
                    Text = caption,
                    FontSize = Metrics.FontCaption,
                    Foreground = Theme.Brush(palette.TextTertiary),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            return new Border
            {
                CornerRadius = Metrics.RowCorner,
                Padding = new Thickness(8, 6, 8, 7),
                Margin = new Thickness(0, 0, 0, 2),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Child = lines
            };
        }

        foreach (var section in contents.Sections)
        {
            var tile = Tile(section.Title, null, section.Title);
            ConfigureRailTile(tile, section.SpineIndex, section.SpineIndex == selectedSpineIndex, palette, navigate, registerTile);
            stack.Children.Add(tile);
        }

        if (contents.Truncated)
        {
            var rest = Tile("Rest of the book", "Open it in your reader", "Opens the book in its app");
            ConfigureRailTile(rest, EpubRestOfBookTile, selected: false, palette, navigate, (_, _, _) => { });
            stack.Children.Add(rest);
        }

        return BuildRailSurface(header, stack, verticalOffset, rememberScroll);
    }
}
