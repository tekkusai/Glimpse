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
    private static readonly (string Title, string Subtitle) FirstOfficeRunMessage =
        ("Preparing document previews", "Just this once. After this, they open instantly.");

    private static readonly (string Title, string Subtitle) SlowConversionMessage =
        ("Preparing this document", "Next time it opens instantly.");

    private static bool TryMapOpenXmlPackage(ZipPackageKind packageKind, out OfficePreviewType previewType)
    {
        previewType = packageKind switch
        {
            ZipPackageKind.OfficeWord => OfficePreviewType.Word,
            ZipPackageKind.OfficePowerPoint => OfficePreviewType.PowerPoint,
            ZipPackageKind.OfficeExcel => OfficePreviewType.Excel,
            _ => default
        };

        return packageKind is ZipPackageKind.OfficeWord or ZipPackageKind.OfficePowerPoint or ZipPackageKind.OfficeExcel;
    }

    private async Task CompleteProgressiveOfficeConversionAsync(
        string sourcePath,
        int partialPageCount,
        PdfPageRailState railState,
        Action<ConvertedOfficeDocument> setFullDocument)
    {
        var started = DateTime.UtcNow;
        try
        {
            var full = await Task.Run(() => OfficeDocumentPdfConverter.TryConvert(sourcePath, railState.Token, background: true), railState.Token);
            if (full is null
                || railState.Version != _loadVersion
                || railState.Token.IsCancellationRequested)
            {
                return;
            }

            setFullDocument(full);
            DiagnosticLog.Write(
                $"office progressive full ready {sourcePath} +{(DateTime.UtcNow - started).TotalMilliseconds:0}ms partial={partialPageCount}");

            if (_userEngagedContent)
            {
                DiagnosticLog.Write($"office progressive upgrade -> none {sourcePath}");
                railState.Path = full.DocumentPath;
                railState.Document = null;
                _ = LoadPdfPageRailOutOfProcessAsync(railState);
                return;
            }

            DiagnosticLog.Write($"office progressive upgrade -> auto {sourcePath}");
            UpgradeConvertedOfficePreview(railState, full, Math.Max(1, railState.CurrentPage));
        }
        catch (OperationCanceledException)
        {
            // Preview moved on.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office progressive full failed {sourcePath}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UpgradeConvertedOfficePreview(
        PdfPageRailState state,
        ConvertedOfficeDocument fullDocument,
        int targetPage)
    {
        if (state.Version != _loadVersion
            || state.Token.IsCancellationRequested
            || string.Equals(state.Path, fullDocument.DocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var target = Math.Max(1, targetPage);
        StagePdfViewerReplacement(
            state,
            () => PdfViewerSource(fullDocument.DocumentPath, target),
            fullDocument.DocumentPath,
            $"office progressive upgrade {fullDocument.DocumentPath} -> {target}",
            "office upgrade",
            () =>
            {
                state.Path = fullDocument.DocumentPath;
                state.CurrentPage = target;
                state.Document = null;
                state.TryHandleJump = null;
                _ = LoadPdfPageRailOutOfProcessAsync(state);
            });
    }

    private FrameworkElement BuildConvertedOfficePreview(ConvertedOfficeDocument document, long version, CancellationToken token)
    {
        // Spreadsheets convert to HTML (screen-faithful, wide sheets scroll);
        // everything else converts to PDF for the Chromium viewer. Converted
        // HTML is treated like a user's HTML file: no scripts, no
        // network.
        // Both shapes sit on the same deep canvas: the PDF viewer draws its
        // own page there, and the exported spreadsheet HTML carries a
        // matching canvas in its stylesheet.
        var isHtml = document.DocumentPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        var palette = Theme.Current;
        var grid = new Grid { Background = Theme.Brush(palette.CanvasBg) };
        ColumnDefinition? railColumn = null;
        if (!isHtml)
        {
            railColumn = new ColumnDefinition { Width = new GridLength(0) };
            grid.ColumnDefinitions.Add(railColumn);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var background = System.Drawing.Color.FromArgb(palette.CanvasBg.R, palette.CanvasBg.G, palette.CanvasBg.B);
        var initialPath = isHtml && document.Sheets is { Count: > 1 } tabbedSheets
            ? tabbedSheets[0].Path
            : document.DocumentPath;
        if (!isHtml
            && PdfPageSizeProbe.TryReadFirstPageSize(initialPath, out var pageWidth, out var pageHeight))
        {
            SetStageFitHint(StageFitHint.PageAspect(pageWidth / pageHeight), version);
        }

        var webView = CreateWebView(
            background,
            () => isHtml ? new Uri(initialPath, UriKind.Absolute) : PdfViewerSource(initialPath),
            $"converted document {document.DocumentPath}",
            _ =>
            {
                if (version == _loadVersion)
                {
                    SetPreviewContent(BuildErrorPreview("Glimpse can't preview this document", "The converted document could not be displayed."), null, animate: false);
                }
            },
            configure: core =>
            {
                if (isHtml)
                {
                    HardenForUntrustedHtml(core);
                }
                else
                {
                    ConfigurePdfViewer(core, initialPath);
                }
            },
            readinessMode: isHtml
                ? WebPaintReadinessMode.StaticLayout
                : WebPaintReadinessMode.PdfPlugin);

        // Multi-sheet workbooks get a bottom tab strip - the native language
        // of spreadsheets - one page per sheet, instead of the export's
        // stacked tables and "Overview" link block.
        if (!isHtml)
        {
            Grid.SetColumn(webView, 1);
            var railHost = BuildPdfPageRailHost();
            Grid.SetColumn(railHost, 0);
            grid.Children.Add(railHost);
            grid.Children.Add(webView);
            // Converted Office PDFs use the same rail, but their thumbnails
            // render in a short helper process. If pdfium dislikes a
            // LibreOffice-generated PDF, the helper dies instead of the
            // resident preview process.
            var railState = new PdfPageRailState(railHost, railColumn!, webView, initialPath, version, token) { Surface = grid };
            grid.Tag = railState;
            TrackPdfRailLifetime(grid, railState);

            if (document.IsPartial
                && document.SourcePath is { } sourcePath
                && document.PartialPageCount is { } partialPageCount)
            {
                ConvertedOfficeDocument? fullDocument = null;
                railState.TryHandleJump = (state, page) =>
                {
                    if (fullDocument is null
                        || page <= partialPageCount
                        || string.Equals(state.Path, fullDocument.DocumentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    DiagnosticLog.Write($"office progressive upgrade -> jump page {page} {sourcePath}");
                    UpgradeConvertedOfficePreview(state, fullDocument, page);
                    return true;
                };

                grid.Unloaded += (_, _) =>
                {
                    var directory = Path.GetDirectoryName(document.DocumentPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        OfficeDocumentPdfConverter.TryDeleteDirectory(directory);
                    }
                };

                _ = CompleteProgressiveOfficeConversionAsync(
                    sourcePath,
                    partialPageCount,
                    railState,
                    full => fullDocument = full);
            }

            _ = LoadPdfPageRailOutOfProcessAsync(railState);
        }
        else if (document.Sheets is { Count: > 1 } sheets)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(webView);
            var tabBar = BuildSheetTabBar(sheets, webView);
            Grid.SetRow(tabBar, 1);
            grid.Children.Add(tabBar);
        }
        else
        {
            grid.Children.Add(webView);
        }

        if (isHtml)
        {
            // Converted spreadsheets have no viewer chrome of their own (the
            // PDF path does); the header uses them zoom controls.
            grid.Tag = ZoomableContentTag;
        }

        return grid;
    }

    /// <summary>
    /// Excel-style sheet tabs along the bottom: plain text buttons, the
    /// active one raised on a surface pill. Only built when a workbook has
    /// more than one sheet.
    /// </summary>
    private static FrameworkElement BuildSheetTabBar(IReadOnlyList<SpreadsheetSheet> sheets, WebView2 webView)
    {
        var palette = Theme.Current;
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 5, 10, 6) };
        var tabs = new List<(Border Chrome, TextBlock Label)>();
        var activeIndex = 0;

        void RefreshTabs()
        {
            for (var i = 0; i < tabs.Count; i++)
            {
                var active = i == activeIndex;
                tabs[i].Chrome.Background = active ? Theme.Brush(palette.RaisedBg) : Brushes.Transparent;
                tabs[i].Chrome.BorderBrush = active ? Theme.Brush(palette.RaisedBorder) : Brushes.Transparent;
                tabs[i].Label.Foreground = Theme.Brush(active ? palette.TextPrimary : palette.TextSecondary);
                tabs[i].Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        for (var i = 0; i < sheets.Count; i++)
        {
            var index = i;
            var label = new TextBlock
            {
                Text = sheets[i].Name,
                FontSize = 12,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var chrome = new Border
            {
                CornerRadius = Metrics.RowCorner,
                Padding = Metrics.ChipPadding,
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
                    chrome.Background = Theme.Brush(palette.HoverBg);
                }
            };
            chrome.MouseLeave += (_, _) => RefreshTabs();
            chrome.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chrome.MouseLeftButtonUp += (_, e) =>
            {
                if (index != activeIndex)
                {
                    activeIndex = index;
                    // WebView keeps the outgoing surface until navigation
                    // commits. A fire-and-forget script command can instead
                    // arrive in the incoming document and leave it blank.
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
            Background = Theme.Brush(palette.ChromeBg),
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = scroller
        };
    }

    private static FrameworkElement BuildOfficePreviewContent(OfficeOpenXmlPreview preview)
    {
        var palette = Theme.Current;
        var accent = preview.Type switch
        {
            OfficePreviewType.PowerPoint => Color.FromRgb(242, 112, 79),
            OfficePreviewType.Excel => Color.FromRgb(73, 179, 126),
            _ => Color.FromRgb(101, 151, 245)
        };

        var title = preview.Type switch
        {
            OfficePreviewType.PowerPoint => "PowerPoint slide text",
            OfficePreviewType.Excel => "Excel sheet preview",
            _ => "Word document text"
        };

        var label = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(24, 20, 24, 0),
            Padding = Metrics.ChipPadding,
            CornerRadius = Metrics.PillCorner,
            Background = new SolidColorBrush(Color.FromArgb(42, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Brush(palette.TextPrimary)
            }
        };

        var fontFamily = preview.Type == OfficePreviewType.Excel
            ? new FontFamily("Cascadia Mono, Consolas")
            : new FontFamily("Segoe UI Variable, Segoe UI");

        var grid = new Grid
        {
            Background = Theme.Brush(palette.CanvasBg)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(label, 0);

        FrameworkElement body = preview.Type == OfficePreviewType.Excel
            ? BuildSelectableTextBox(
                preview.Text,
                fontFamily,
                fontSize: 12.5,
                wrap: false,
                new Thickness(24, 16, 24, 24),
                palette.CanvasBg)
            : BuildOfficeDocumentPreview(preview, fontFamily, accent);

        Grid.SetRow(body, 1);
        grid.Children.Add(label);
        grid.Children.Add(body);
        return grid;
    }

    private static FrameworkElement BuildOfficeDocumentPreview(OfficeOpenXmlPreview preview, FontFamily fontFamily, Color accent)
    {
        var palette = Theme.Current;
        var document = new FlowDocument
        {
            Background = Theme.Brush(palette.CanvasBg),
            Foreground = Theme.Brush(palette.TextPrimary),
            FontFamily = fontFamily,
            FontSize = 14,
            PagePadding = new Thickness(24, 14, 24, 24),
            ColumnWidth = 100000
        };

        foreach (var block in preview.Text.Split(["\r\n", "\n"], StringSplitOptions.None).Take(500))
        {
            if (string.IsNullOrWhiteSpace(block))
            {
                document.Blocks.Add(new Paragraph
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    LineHeight = 8
                });
                continue;
            }

            var isHeading = preview.Type == OfficePreviewType.PowerPoint
                && block.StartsWith("Slide ", StringComparison.OrdinalIgnoreCase);
            document.Blocks.Add(new Paragraph(new Run(block.Trim()))
            {
                Margin = new Thickness(0, isHeading ? 16 : 2, 0, isHeading ? 6 : 2),
                Foreground = isHeading ? new SolidColorBrush(accent) : Theme.Brush(palette.TextPrimary),
                FontSize = isHeading ? 15 : 14,
                FontWeight = isHeading ? FontWeights.SemiBold : FontWeights.Normal,
                LineHeight = isHeading ? 22 : 21
            });
        }

        if (preview.ImagePaths.Count > 0)
        {
            document.Blocks.Add(new Paragraph(new Run("Images"))
            {
                Margin = new Thickness(0, 24, 0, 10),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Brush(palette.TextPrimary)
            });

            var wrap = new WrapPanel();
            foreach (var imagePath in preview.ImagePaths.Take(24))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.DecodePixelWidth = 320;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    wrap.Children.Add(new Border
                    {
                        Width = 220,
                        Height = 150,
                        Margin = new Thickness(0, 0, 12, 12),
                        CornerRadius = new CornerRadius(12),
                        ClipToBounds = true,
                        Background = Theme.Brush(palette.RaisedBg),
                        BorderBrush = Theme.Brush(palette.RaisedBorder),
                        BorderThickness = new Thickness(1),
                        Child = new Image
                        {
                            Source = bitmap,
                            Stretch = Stretch.Uniform
                        }
                    });
                }
                catch
                {
                    // Ignore broken embedded media.
                }
            }

            document.Blocks.Add(new BlockUIContainer(wrap)
            {
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        var richTextBox = new RichTextBox(document)
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Theme.Brush(palette.CanvasBg),
            Foreground = Theme.Brush(palette.TextPrimary),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = Theme.Brush(palette.Accent),
            SelectionOpacity = 0.55,
            CaretBrush = Theme.Brush(palette.TextPrimary)
        };
        AttachMiddleAutoScroll(richTextBox, () => FindVisualChild<ScrollViewer>(richTextBox), horizontal: false);
        ApplyScrollBarStyle(richTextBox);
        return richTextBox;
    }

    /// <summary>
    /// A card under a degraded document preview: first names the state
    /// (testers read the reduced view as the app rendering poorly), then the
    /// fix and its payoff. Shown in the preview only.
    /// </summary>
    private static FrameworkElement WithLibreOfficeSuggestion(FrameworkElement content)
    {
        var palette = Theme.Current;
        var title = new TextBlock
        {
            Text = "This is a basic preview",
            FontSize = Metrics.FontBody,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary)
        };
        var text = new TextBlock
        {
            Text = "Get LibreOffice (free) to see documents fully formatted",
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 12,
            Foreground = Theme.Brush(palette.TextSecondary)
        };
        var glyph = new TextBlock
        {
            Text = "\uE896",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.Accent)
        };
        var pill = new Border
        {
            CornerRadius = Metrics.SurfaceCorner,
            Padding = new Thickness(16, 10, 19, 11),
            Margin = new Thickness(0, 0, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Opens libreoffice.org in your browser",
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { glyph, new StackPanel { Children = { title, text } } }
            }
        };
        pill.MouseEnter += (_, _) =>
        {
            pill.Background = Theme.Brush(palette.HoverBg);
            text.Foreground = Theme.Brush(palette.TextPrimary);
        };
        pill.MouseLeave += (_, _) =>
        {
            pill.Background = Theme.Brush(palette.RaisedBg);
            text.Foreground = Theme.Brush(palette.TextSecondary);
        };
        pill.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pill.MouseLeftButtonUp += (_, e) =>
        {
            OpenExternalUri("https://www.libreoffice.org/download/");
            e.Handled = true;
        };

        var host = new Grid();
        host.Children.Add(content);
        host.Children.Add(pill);
        return host;
    }
}
