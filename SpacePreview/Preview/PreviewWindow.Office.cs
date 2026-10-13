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

            // Starting the job asks the disk for the file's size and time.
            var job = await Task.Run(() => OfficeDocumentPdfConverter.Convert(path, kind, token), token);
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

        // Second best is never shown as if it were the file. With an engine,
        // the conversion failed, and the line says so. Without one, the fix
        // is offered in place: never a notification, one click to act.
        return OfficeDocumentPdfConverter.EngineName is { } failed
            ? WithOfficeLine(content, $"{failed} couldn't convert this file. Open it in its app instead.", null)
            : WithOfficeLine(content, "Preview without formatting. Install LibreOffice to see this file as designed.", LibreOfficeFix);
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
            var words = new TextBlock
            {
                FontSize = Metrics.FontCaption,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            }.Themed(TextBlock.ForegroundProperty, "Theme.TextTertiary");
            var note = new Border
            {
                BorderThickness = new Thickness(0, 1, 1, 0),
                Padding = new Thickness(12, 10, 12, 12),
                Child = words
            }.Themed(Border.BackgroundProperty, "Theme.ChromeVeil").Themed(Border.BorderBrushProperty, "Theme.Border");
            Grid.SetRow(note, 1);
            railStack.Children.Add(note);

            // Shown beside the open rail only, and only while it has words.
            void PlaceNote() =>
                note.Visibility = words.Text.Length > 0 && railStack.ActualWidth >= PdfPageRailWidth - 1
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            railStack.SizeChanged += (_, _) => PlaceNote();
            FollowProgressiveConversion(document, job, railState, said =>
            {
                words.Text = said ?? string.Empty;
                PlaceNote();
            });
            _ = LoadPartialOfficeRailAsync(railState, document, job);
        }
        else
        {
            _ = LoadPdfPageRailOutOfProcessAsync(railState);
        }

        return grid;
    }

    /// <summary>
    /// The first pages are on screen; the rest is still converting. The rail
    /// already has a slot for every page, and a click on a page that has not
    /// arrived is kept. When the whole document lands the rail shows it at
    /// once. The viewer swaps to it in place only while it still stands
    /// where it opened, because only then is the swap invisible. That is
    /// asked when the whole document lands and again when the swap is due:
    /// the replacement takes about half a second to stage, and a reader
    /// can start to scroll in it. A reader who has scrolled, zoomed or
    /// clicked into the pages is never moved: choosing a page past the
    /// first ones makes the swap, and so does the next look at the file.
    /// <paramref name="say"/> sets the note under the rail; null clears it.
    /// </summary>
    private void FollowProgressiveConversion(
        ConvertedOfficeDocument partial,
        OfficeConversionJob job,
        PdfPageRailState railState,
        Action<string?> say)
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

        var upgrading = false;
        int? askedPage = null;
        ConvertedOfficeDocument? full = null;

        // A click into the pages can leave a selection or an open find bar
        // that a swap would drop, so it settles the matter without asking
        // the viewer, before the question and after it. A viewer that gives
        // no answer counts as moved.
        async Task<bool> ReaderHasNotMovedAsync() =>
            !_userEngagedContent
            && !ContentOwnsKeyboard()
            && await AskPdfViewerAsync<bool>(railState, LookForPdfPagesAtOpening) == true
            && !_userEngagedContent
            && !ContentOwnsKeyboard();

        // The rest is ready, and the reader keeps their place.
        void Hold()
        {
            DiagnosticLog.Write($"office progressive upgrade held, the viewer is in use {job.SourcePath}");
            say(ProgressiveNote(ProgressiveRest.Ready, partial, job.Kind));
        }

        // A swap in place is one nobody asked for, so it goes ahead only if
        // the reader has still not moved when it is due. A page chosen
        // while it staged, or while the viewer was asked, is asking.
        void Upgrade(int page, bool inPlace = false)
        {
            upgrading = true;
            StagePdfViewerReplacement(
                railState,
                () => PdfViewerSource(full!.DocumentPath, page),
                full!.DocumentPath,
                $"office progressive upgrade page={page} {full.DocumentPath}",
                "office upgrade",
                onSwapped: () =>
                {
                    upgrading = false;
                    railState.CurrentPage = page;
                    railState.TryHandleJump = null;
                    say(null);
                    ReleasePartial();
                    if (askedPage is { } later && later != page)
                    {
                        JumpPdfToPage(railState, later);
                    }
                },
                stillWanted: inPlace
                    ? async () => askedPage.HasValue || await ReaderHasNotMovedAsync() || askedPage.HasValue
                    : null,
                onCalledOff: () =>
                {
                    upgrading = false;
                    Hold();
                });
        }

        railState.TryHandleJump = (_, page) =>
        {
            if (page <= partial.PartialPages)
            {
                // The last page chosen is the one that counts: one asked
                // for while it was on the way is no longer wanted. A swap
                // already staging lands where it was sent, then follows.
                askedPage = upgrading ? page : null;
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

        say(ProgressiveNote(ProgressiveRest.OnTheWay, partial, job.Kind));
        railState.Surface!.Unloaded += (_, _) => ReleasePartial();
        _ = WaitForRestAsync();

        async Task WaitForRestAsync()
        {
            var started = DateTime.UtcNow;
            bool Left() => railState.Version != _loadVersion || railState.Token.IsCancellationRequested;
            try
            {
                var complete = await job.Complete.WaitAsync(railState.Token);
                if (Left())
                {
                    ReleasePartial();
                    return;
                }

                if (complete is null)
                {
                    // The rest failed: the first pages are the preview. The
                    // rail stops promising more, and the note says what to do.
                    DiagnosticLog.Write($"office progressive rest failed {job.SourcePath}");
                    railState.TryHandleJump = null;
                    say(ProgressiveNote(ProgressiveRest.Failed, partial, job.Kind));
                    await railState.FirstRailReady.Task.WaitAsync(railState.Token);
                    if (!Left() && railState.Document is { } promised && promised.Pages.Count > partial.PartialPages)
                    {
                        railState.Document = new PdfPreviewDocument([.. promised.Pages.Take(partial.PartialPages)], partial.PartialPages);
                        RenderPdfPageRail(railState);
                    }

                    return;
                }

                full = complete;
                DiagnosticLog.Write(
                    $"office progressive full ready after={(DateTime.UtcNow - started).TotalMilliseconds:0}ms {job.SourcePath}");
                railState.Path = complete.DocumentPath;
                railState.Document = null;
                _ = LoadPdfPageRailOutOfProcessAsync(railState);

                var atRest = !askedPage.HasValue && await ReaderHasNotMovedAsync();
                if (Left())
                {
                    ReleasePartial();
                    return;
                }

                if (upgrading)
                {
                    return;
                }

                if (askedPage is { } asked)
                {
                    Upgrade(asked);
                }
                else if (atRest)
                {
                    DiagnosticLog.Write($"office progressive upgrade in place {job.SourcePath}");
                    Upgrade(1, inPlace: true);
                }
                else
                {
                    Hold();
                }
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
    /// still converting, so the rail shows the document's real length. A
    /// rest that has already failed gets no slots: nothing is on its way.
    /// </summary>
    private async Task LoadPartialOfficeRailAsync(PdfPageRailState state, ConvertedOfficeDocument partial, OfficeConversionJob job)
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
            var restFailed = job.Complete is { IsCompletedSuccessfully: true, Result: null };
            var slots = restFailed
                ? rendered.Pages.Count
                : Math.Min(Math.Max(partial.ExpectedPages, rendered.Pages.Count), PdfPageRailMaxPages);
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

    /// <summary>What has become of the pages after the first ones.</summary>
    internal enum ProgressiveRest
    {
        OnTheWay,
        Ready,
        Failed
    }

    /// <summary>
    /// The note under the rail of a document whose first pages show. Once
    /// the rest is here or has failed, it counts only the pages on screen:
    /// the count a file claims for itself is a promise, not a fact.
    /// </summary>
    internal static string ProgressiveNote(ProgressiveRest rest, ConvertedOfficeDocument partial, OfficeKind kind)
    {
        var unit = kind == OfficeKind.Presentation ? "slides" : "pages";
        switch (rest)
        {
            case ProgressiveRest.Ready:
                return $"The {unit} after {partial.PartialPages} are ready. Choose one to continue.";
            case ProgressiveRest.Failed:
                return $"Only the first {partial.PartialPages} {unit} can be shown. Open the file for the rest.";
            default:
                var arriving = partial.ExpectedPages > partial.PartialPages
                    ? $"{char.ToUpperInvariant(unit[0])}{unit[1..]} {partial.PartialPages + 1} to {partial.ExpectedPages}"
                    : $"More {unit}";
                return arriving + "\nare on the way";
        }
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
    /// A preview that is not the file as designed says so: one line, and
    /// the fix as one pill when there is one, in their own row under the
    /// content, never over it.
    /// </summary>
    private static FrameworkElement WithOfficeLine(FrameworkElement content, string words, PreviewAction? fix)
    {
        var row = new DockPanel { LastChildFill = true };
        if (fix is not null)
        {
            var pill = HousePill.Build(fix.Label, () => OpenExternalUri(fix.Uri));
            pill.Margin = new Thickness(16, 0, 0, 0);
            pill.ToolTip = fix.Tooltip;
            DockPanel.SetDock(pill, Dock.Right);
            row.Children.Add(pill);
        }

        row.Children.Add(new TextBlock
        {
            Text = words,
            FontSize = Metrics.FontBody,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        }.Themed(TextBlock.ForegroundProperty, "Theme.TextSecondary"));

        var bar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10, 12, 10),
            Child = row
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
