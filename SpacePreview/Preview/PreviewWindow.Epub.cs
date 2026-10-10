using System.Buffers.Binary;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
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
        public IReadOnlyList<EpubPreviewLoader.EpubChapterEntry> Chapters { get; set; } =
            Array.Empty<EpubPreviewLoader.EpubChapterEntry>();
    }

    /// <summary>
    /// EPUB keeps the whole book as one continuous document; the rail is
    /// orientation, not pagination. Clicking a chapter scrolls the document
    /// to that chapter's anchor. Same idiom and handle as the PDF page rail.
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
        TrackEpubRailLifetime(grid, railState);
        _ = LoadEpubChapterRailAsync(railState, path);
        return grid;
    }

    /// <summary>Bind at reveal, not at build: the folder-hover lesson.</summary>
    private void TrackEpubRailLifetime(Grid grid, EpubRailState railState)
    {
        grid.Loaded += (_, _) => _activeEpubRail = railState;
        grid.Unloaded += (_, _) =>
        {
            if (ReferenceEquals(_activeEpubRail, railState))
            {
                _activeEpubRail = null;
            }
        };
    }

    private async Task LoadEpubChapterRailAsync(EpubRailState state, string path)
    {
        try
        {
            var chapters = await Task.Run(() => EpubPreviewLoader.TryGetChapters(path), state.Token);
            if (state.Version != _loadVersion || state.Token.IsCancellationRequested)
            {
                return;
            }

            if (chapters is null || chapters.Count <= 1)
            {
                HideEpubChapterRail(state);
                return;
            }

            state.Chapters = chapters;
            // Cloak on the first render too: if the document has already
            // revealed, the rail's arrival would otherwise reflow it naked.
            RenderEpubChapterRail(state, cloakViewer: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"epub rail failed {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RenderEpubChapterRail(EpubRailState state, bool cloakViewer = false)
    {
        if (state.Chapters.Count <= 1)
        {
            HideEpubChapterRail(state);
            return;
        }

        var targetWidth = state.IsVisible ? EpubChapterRailWidth : PdfPageRailCollapsedWidth;
        state.RailColumn.Width = new GridLength(targetWidth);
        state.ChapterTiles.Clear();
        state.SelectedChapterTile = null;

        var content = state.IsVisible
            ? BuildEpubChapterRail(
                state.Chapters,
                state.CurrentSpineIndex,
                state.RailVerticalOffset,
                offset => state.RailVerticalOffset = offset,
                index => JumpEpubToChapter(state, index),
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
            : BuildCollapsedRail(state.Chapters.Count, "Show contents", () =>
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

    private void HideEpubChapterRail(EpubRailState state)
    {
        state.ChapterTiles.Clear();
        state.SelectedChapterTile = null;
        state.RailColumn.Width = new GridLength(0);
        AnimateRail(state.Host, 0, null);
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

    private static void SetEpubCurrentChapter(EpubRailState state, int spineIndex)
    {
        if (spineIndex == state.CurrentSpineIndex
            && state.SelectedChapterTile is not null
            && state.ChapterTiles.TryGetValue(spineIndex, out var currentTile)
            && ReferenceEquals(state.SelectedChapterTile, currentTile))
        {
            return;
        }

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
        IReadOnlyList<EpubPreviewLoader.EpubChapterEntry> chapters,
        int selectedSpineIndex,
        double verticalOffset,
        Action<double> rememberScroll,
        Action<int> navigate,
        Action<int, Border, bool> registerTile,
        Action hide)
    {
        var palette = Theme.Current;
        var stack = new StackPanel { Margin = new Thickness(10, 0, 10, 12) };

        var header = new Grid { Margin = new Thickness(10, 10, 10, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Contents",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextTertiary),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        var hideButton = BuildRailChromeButton("\uE76B", "Hide contents", hide);
        Grid.SetColumn(hideButton, 1);
        header.Children.Add(hideButton);

        foreach (var chapter in chapters)
        {
            var tile = new Border
            {
                CornerRadius = Metrics.RowCorner,
                Padding = new Thickness(8, 6, 8, 7),
                Margin = new Thickness(0, 0, 0, 2),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = chapter.Title,
                Child = new TextBlock
                {
                    Text = chapter.Title,
                    FontSize = 11.5,
                    Foreground = Theme.Brush(palette.TextSecondary),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };

            ConfigureRailTile(
                tile,
                chapter.SpineIndex,
                chapter.SpineIndex == selectedSpineIndex,
                palette,
                navigate,
                registerTile);
            stack.Children.Add(tile);
        }

        return BuildRailSurface(header, stack, verticalOffset, rememberScroll);
    }
}
