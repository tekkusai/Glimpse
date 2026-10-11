using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    /// <summary>
    /// Every Office file comes through here, found by name or by content: a
    /// finished conversion, else the engine's job, else the text-only page.
    /// Leaving stops the job unless another preview of the file still waits.
    /// </summary>
    private async Task<FrameworkElement> LoadOfficePreviewAsync(string path, OfficeKind kind, long version, CancellationToken token)
    {
        if (OfficeDocumentPdfConverter.IsAvailable)
        {
            var cached = await Task.Run(() => OfficeDocumentPdfConverter.TryGetCached(path, kind), token);
            if (cached is not null)
            {
                return BuildConvertedOfficePreview(cached, null, version, token);
            }

            var job = OfficeDocumentPdfConverter.Convert(path, kind, token);
            if (OfficeDocumentPdfConverter.EngineName is { } engine)
            {
                // The one wait worth a line: the stand-in names it after a beat.
                SetWaitReason(version, $"Converting with {engine}");
            }

            var first = await job.FirstContent.WaitAsync(token);
            if (first is { IsPartial: true } && !job.TryHoldPartial())
            {
                // The rest landed and the first pages were cleaned up meanwhile.
                first = await job.Complete.WaitAsync(token);
            }

            if (first is not null)
            {
                return BuildConvertedOfficePreview(first, job, version, token);
            }
        }

        var page = await Task.Run(() => OfficeOpenXmlPreview.TryBuild(path, kind, token), token);
        var content = page is null
            ? await BuildFileCardAsync(path, token)
            : BuildConvertedOfficePreview(page, null, version, token);

        // The machine lacks the engine (as opposed to a conversion failing):
        // offer the fix in place. Never a notification, one click to act.
        return OfficeDocumentPdfConverter.IsAvailable ? content : WithLibreOfficeSuggestion(content);
    }

    /// <summary>
    /// PDFs go to the Chromium viewer with the page rail. HTML (spreadsheets,
    /// and the text-only page) is treated like a user's HTML file: no
    /// scripts, no network; links open in the browser.
    /// </summary>
    private FrameworkElement BuildConvertedOfficePreview(ConvertedOfficeDocument document, OfficeConversionJob? job, long version, CancellationToken token)
    {
        var isHtml = document.DocumentPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        var grid = isHtml
            ? new Grid { Background = Brushes.Transparent }
            : new Grid().Themed(Panel.BackgroundProperty, "Theme.CanvasBg");
        var initialPath = document.Sheets is { Count: > 1 } sheets ? sheets[0].Path : document.DocumentPath;
        if (!isHtml && PdfPageSizeProbe.TryReadFirstPageSize(initialPath, out var pageWidth, out var pageHeight))
        {
            SetStageFitHint(StageFitHint.PageAspect(pageWidth / pageHeight), version);
        }

        var webView = CreateWebView(
            isHtml ? WebSurfaceKind.Page : WebSurfaceKind.PdfViewer,
            () => isHtml ? new Uri(initialPath, UriKind.Absolute) : PdfViewerSource(initialPath),
            $"converted document {document.DocumentPath}",
            _ =>
            {
                if (version == _loadVersion)
                {
                    SetPreviewContent(BuildErrorPreview("Can't show this document", "Open it in its app instead."), null);
                }
            },
            configure: core =>
            {
                if (isHtml)
                {
                    HardenForUntrustedHtml(core);
                    OpenWebLinksExternally(core);
                }
                else
                {
                    ConfigurePdfViewer(core, initialPath);
                }
            });

        if (isHtml)
        {
            grid.Children.Add(webView);
            // Multi-sheet workbooks get a bottom tab strip, one page per sheet.
            if (document.Sheets is { Count: > 1 } tabs)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var tabBar = BuildSheetTabBar(tabs, webView);
                Grid.SetRow(tabBar, 1);
                grid.Children.Add(tabBar);
            }

            // These pages have no viewer chrome of their own; the header's
            // zoom controls drive them.
            grid.Tag = ZoomableContentTag;
            return grid;
        }

        // The rail column holds the rail and, under it, the note a partial
        // document needs. The viewer spans the other column alone, which is
        // where a staged replacement lands too.
        var railColumn = new ColumnDefinition { Width = new GridLength(0) };
        grid.ColumnDefinitions.Add(railColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var railHost = BuildPdfPageRailHost();
        var railStack = new Grid();
        railStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        railStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        railStack.Children.Add(railHost);
        grid.Children.Add(railStack);
        Grid.SetColumn(webView, 1);
        grid.Children.Add(webView);

        // Thumbnails render in a short helper process: if pdfium dislikes a
        // LibreOffice PDF, the helper dies instead of the resident app.
        var railState = new PdfPageRailState(railHost, railColumn, webView, initialPath, version, token) { Surface = grid };
        grid.Tag = railState;
        TrackPdfRailLifetime(grid, railState);

        if (document.IsPartial && job is not null)
        {
            var note = BuildArrivingPagesNote(document, job.Kind);
            Grid.SetRow(note, 1);
            railStack.Children.Add(note);
            // Shown beside the open rail only, and only until the rest lands.
            railStack.SizeChanged += (_, _) =>
                note.Visibility = note.IsEnabled && railStack.ActualWidth >= PdfPageRailWidth - 1
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            FollowProgressiveConversion(document, job, railState, note);
            _ = LoadPartialOfficeRailAsync(railState, document);
        }
        else
        {
            _ = LoadPdfPageRailOutOfProcessAsync(railState);
        }

        return grid;
    }

    /// <summary>
    /// The first pages are on screen; the rest is still converting. The rail
    /// already has a slot for every page, a click on a page that has not
    /// arrived is kept, and when the whole document lands the viewer swaps
    /// to it in place, at the page the user is on or asked for.
    /// </summary>
    private void FollowProgressiveConversion(
        ConvertedOfficeDocument partial,
        OfficeConversionJob job,
        PdfPageRailState railState,
        FrameworkElement note)
    {
        var holdsPartial = true;
        void ReleasePartial()
        {
            if (holdsPartial)
            {
                holdsPartial = false;
                job.ReleasePartial();
            }
        }

        var showingPartial = true;
        var upgrading = false;
        int? askedPage = null;
        ConvertedOfficeDocument? full = null;

        void Upgrade(int page)
        {
            upgrading = true;
            StagePdfViewerReplacement(
                railState,
                () => PdfViewerSource(full!.DocumentPath, page),
                full!.DocumentPath,
                $"office progressive upgrade {full.DocumentPath} page={page}",
                "office upgrade",
                () =>
                {
                    showingPartial = false;
                    upgrading = false;
                    railState.Path = full.DocumentPath;
                    railState.CurrentPage = page;
                    railState.Document = null;
                    railState.TryHandleJump = null;
                    ReleasePartial();
                    _ = LoadPdfPageRailOutOfProcessAsync(railState);
                    if (askedPage is { } later && later != page)
                    {
                        JumpPdfToPage(railState, later);
                    }
                });
        }

        railState.TryHandleJump = (_, page) =>
        {
            if (!showingPartial || page <= partial.PartialPages)
            {
                return false;
            }

            askedPage = page;
            if (full is not null && !upgrading)
            {
                DiagnosticLog.Write($"office progressive jump page={page} {job.SourcePath}");
                Upgrade(page);
            }

            return true;
        };

        railState.Surface!.Unloaded += (_, _) => ReleasePartial();
        _ = WaitForRestAsync();

        async Task WaitForRestAsync()
        {
            var started = DateTime.UtcNow;
            try
            {
                var complete = await job.Complete.WaitAsync(railState.Token);
                if (railState.Version != _loadVersion || railState.Token.IsCancellationRequested)
                {
                    ReleasePartial();
                    return;
                }

                note.IsEnabled = false;
                note.Visibility = Visibility.Collapsed;
                if (complete is null)
                {
                    // The rest failed: the first pages are the preview, and
                    // the rail stops promising more.
                    DiagnosticLog.Write($"office progressive rest failed {job.SourcePath}");
                    railState.Document = null;
                    _ = LoadPdfPageRailOutOfProcessAsync(railState);
                    return;
                }

                // A wheel alone never moves the rail, so the viewer says
                // where the reader is. A page asked for meanwhile still wins.
                await ReadPdfPageFromViewerAsync(railState);
                if (railState.Version != _loadVersion || railState.Token.IsCancellationRequested)
                {
                    ReleasePartial();
                    return;
                }

                full = complete;
                var target = askedPage ?? Math.Max(1, railState.CurrentPage);
                DiagnosticLog.Write(
                    $"office progressive full ready {job.SourcePath} after={(DateTime.UtcNow - started).TotalMilliseconds:0}ms page={target}");
                Upgrade(target);
            }
            catch (OperationCanceledException)
            {
                // The preview moved on, maybe before it ever showed. The job
                // stops unless another preview still waits for it.
                ReleasePartial();
            }
        }
    }

    /// <summary>
    /// The partial's own thumbnails, then a page-shaped slot for every page
    /// still converting, so the rail shows the document's real length.
    /// </summary>
    private async Task LoadPartialOfficeRailAsync(PdfPageRailState state, ConvertedOfficeDocument partial)
    {
        try
        {
            var rendered = await TryRenderPdfThumbnailsOutOfProcessAsync(state.Path, partial.PartialPages, state.Token);
            if (state.Version != _loadVersion || state.Token.IsCancellationRequested)
            {
                return;
            }

            if (rendered is not { Pages.Count: > 0 }
                || !string.Equals(state.Path, partial.DocumentPath, StringComparison.OrdinalIgnoreCase))
            {
                // No thumbnails, or the viewer already moved to the whole document.
                return;
            }

            var first = rendered.Pages[0];
            var slots = Math.Min(Math.Max(partial.ExpectedPages, rendered.Pages.Count), PdfPageRailMaxPages);
            var ghost = new PdfPreviewPage(BuildGhostPage(first.Width, first.Height), first.Width, first.Height);
            var pages = rendered.Pages.Concat(Enumerable.Repeat(ghost, slots - rendered.Pages.Count)).ToList();
            state.Document = new PdfPreviewDocument(pages, pages.Count);
            HintStageFromRailTruth(state, state.Document);
            RenderPdfPageRail(state);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office partial rail failed {state.Path}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            state.FirstRailReady.TrySetResult(true);
        }
    }

    /// <summary>A blank sheet the size of a real thumbnail: a page that is on its way.</summary>
    private static BitmapSource BuildGhostPage(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var paper = Theme.Light.ChromeBg;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = paper.B;
            pixels[i + 1] = paper.G;
            pixels[i + 2] = paper.R;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static FrameworkElement BuildArrivingPagesNote(ConvertedOfficeDocument partial, OfficeKind kind)
    {
        var unit = kind == OfficeKind.Presentation ? "slides" : "pages";
        var text = partial.ExpectedPages > partial.PartialPages
            ? $"{char.ToUpperInvariant(unit[0])}{unit[1..]} {partial.PartialPages + 1} to {partial.ExpectedPages}"
            : $"More {unit}";
        return new Border
        {
            BorderThickness = new Thickness(0, 1, 1, 0),
            Padding = new Thickness(12, 10, 12, 12),
            Child = new TextBlock
            {
                Text = text + "\nare on the way",
                FontSize = Metrics.FontCaption,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            }.Themed(TextBlock.ForegroundProperty, "Theme.TextTertiary")
        }.Themed(Border.BackgroundProperty, "Theme.ChromeVeil").Themed(Border.BorderBrushProperty, "Theme.Border");
    }

    /// <summary>Links in a converted page open in the browser, as they do in the PDF viewer.</summary>
    private static void OpenWebLinksExternally(CoreWebView2 core)
    {
        core.NavigationStarting += (_, args) =>
        {
            if (args.IsUserInitiated
                && Uri.TryCreate(args.Uri, UriKind.Absolute, out var target)
                && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp))
            {
                args.Cancel = true;
                OpenExternalUri(target.AbsoluteUri);
            }
        };
    }

    /// <summary>
    /// Sheet tabs along the bottom: plain labels, the active one raised on a
    /// surface pill. Built only for a workbook with more than one sheet.
    /// </summary>
    private static FrameworkElement BuildSheetTabBar(IReadOnlyList<SpreadsheetSheet> sheets, WebView2 webView)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        var tabs = new List<(Border Chrome, TextBlock Label)>();
        var activeIndex = 0;

        void RefreshTabs()
        {
            for (var i = 0; i < tabs.Count; i++)
            {
                var (chrome, label) = tabs[i];
                if (i == activeIndex)
                {
                    chrome.Themed(Border.BackgroundProperty, "Theme.RaisedBg").Themed(Border.BorderBrushProperty, "Theme.RaisedBorder");
                    label.Themed(TextBlock.ForegroundProperty, "Theme.TextPrimary");
                }
                else
                {
                    chrome.Background = Brushes.Transparent;
                    chrome.BorderBrush = Brushes.Transparent;
                    label.Themed(TextBlock.ForegroundProperty, "Theme.TextSecondary");
                }
            }
        }

        for (var i = 0; i < sheets.Count; i++)
        {
            var index = i;
            var label = new TextBlock
            {
                Text = sheets[i].Name,
                FontSize = Metrics.FontLabel,
                FontWeight = FontWeights.SemiBold,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var chrome = new Border
            {
                Height = 30,
                CornerRadius = Metrics.PillCorner,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = label
            };
            chrome.MouseEnter += (_, _) =>
            {
                if (index != activeIndex)
                {
                    chrome.Themed(Border.BackgroundProperty, "Theme.HoverBg");
                }
            };
            chrome.MouseLeave += (_, _) => RefreshTabs();
            chrome.MouseLeftButtonDown += (_, e) =>
            {
                if (index != activeIndex)
                {
                    chrome.Themed(Border.BackgroundProperty, "Theme.PressedBg");
                }

                e.Handled = true;
            };
            chrome.MouseLeftButtonUp += (_, e) =>
            {
                if (index != activeIndex)
                {
                    activeIndex = index;
                    // A navigation keeps the outgoing surface until it
                    // commits; a scripted swap can land in the incoming
                    // document and leave it blank.
                    webView.Source = new Uri(sheets[index].Path, UriKind.Absolute);
                    RefreshTabs();
                }

                e.Handled = true;
            };
            strip.Children.Add(chrome);
            tabs.Add((chrome, label));
        }

        RefreshTabs();

        var scroller = new ScrollViewer
        {
            Content = strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        StyleScrollViewer(scroller);

        return new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = scroller
        }.Themed(Border.BackgroundProperty, "Theme.ChromeBg").Themed(Border.BorderBrushProperty, "Theme.Border");
    }

    /// <summary>Offered here and in Settings while this PC has no engine.</summary>
    internal static readonly PreviewAction LibreOfficeFix = new("Get LibreOffice", "https://www.libreoffice.org/download/", "Opens libreoffice.org in your browser");

    /// <summary>
    /// The text-only preview says what it is and offers the fix: one line
    /// and one pill in their own row under the content, never over it.
    /// </summary>
    private static FrameworkElement WithLibreOfficeSuggestion(FrameworkElement content)
    {
        var pill = HousePill.Build(LibreOfficeFix.Label, () => OpenExternalUri(LibreOfficeFix.Uri));
        pill.Margin = new Thickness(16, 0, 0, 0);
        pill.ToolTip = LibreOfficeFix.Tooltip;
        DockPanel.SetDock(pill, Dock.Right);

        var bar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10, 12, 10),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    pill,
                    new TextBlock
                    {
                        Text = "Preview without formatting. Install LibreOffice to see this file as designed.",
                        FontSize = Metrics.FontBody,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    }.Themed(TextBlock.ForegroundProperty, "Theme.TextSecondary")
                }
            }
        }.Themed(Border.BackgroundProperty, "Theme.ChromeBg").Themed(Border.BorderBrushProperty, "Theme.Border");

        var host = new Grid { Tag = content.Tag }.Themed(Panel.BackgroundProperty, "Theme.CanvasBg");
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.Children.Add(content);
        Grid.SetRow(bar, 1);
        host.Children.Add(bar);
        return host;
    }
}
