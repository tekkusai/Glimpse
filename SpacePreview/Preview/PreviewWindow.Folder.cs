using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    private const double FolderArtSide = 184;
    private const double ListingWidth = 480;
    // Three rows: at the smallest stage the list fits without lifting the card.
    private const double ListingMinHeight = 84;
    private const int FolderBreakdownRowsCap = 300;
    private const int FolderSizeScanEntryBudget = 25000;
    private static readonly TimeSpan FolderSizeScanStartDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FolderSizeScanTimeBudget = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// The folder card is the first frame, not the census: its art and title
    /// are local, the shell icon and the size walk land later. The children
    /// list is the card's footer and fills the rest of the canvas.
    /// </summary>
    private FrameworkElement BuildDirectoryCard(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dpi = PixelsPerDip();
        var visualHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = BuildFolderArtwork(null, dpi)
        };

        var list = new EntryList(
            FolderBreakdownRowsCap,
            meters: true,
            activate: (node, _) => OpenFolderChild(Path.Combine(path, node.Name), node.IsDirectory),
            enter: (node, row) => _folderHover?.OnRowEnter(row, Path.Combine(path, node.Name), node.IsDirectory),
            leave: (_, row) => _folderHover?.OnRowLeave(row));
        var sizeLine = FactLine("Calculating size…", 0);
        var card = BuildListingCard(visualHost, FolderTitle(path), sizeLine, list);

        BitmapSource? restingFolder = null;
        ShellImage? hovered = null;
        var hoveredIsDirectory = false;
        void Render() => visualHost.Content = hovered is null
            ? BuildFolderArtwork(restingFolder, dpi)
            : BuildHoverArtwork(restingFolder, hoveredIsDirectory, hovered, dpi);

        var folderPixels = DevicePixels(FolderArtSide, dpi);
        _ = UpdateFolderArtworkAsync(path, folderPixels, icon =>
        {
            restingFolder = icon;
            Render();
        }, token);

        var hover = new FolderHoverThumbnail((isDirectory, shellImage) =>
        {
            hovered = shellImage;
            hoveredIsDirectory = isDirectory;
            Render();
        }, folderPixels, DevicePixels(CardIconSide, dpi));
        card.Loaded += (_, _) => _folderHover = hover;
        card.Unloaded += (_, _) =>
        {
            if (ReferenceEquals(_folderHover, hover))
            {
                hover.Reset();
                _folderHover = null;
            }
        };

        StartFolderSizeScan(path, sizeLine, list, token);
        return card;
    }

    /// <summary>
    /// A folder and an archive are both lists of files, so they share one
    /// card: the picture, the name, one fact line, and the rows with their
    /// size bars as the footer.
    /// </summary>
    private static FrameworkElement BuildListingCard(FrameworkElement visual, string title, TextBlock facts, EntryList list)
    {
        var listScroll = new ScrollViewer
        {
            Width = ListingWidth,
            MinHeight = ListingMinHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = list.Element
        };
        StyleScrollViewer(listScroll);
        return BuildFileCard(visual, title, [facts], listScroll);
    }

    private static string FolderTitle(string path)
    {
        var title = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(title) ? path : title;
    }

    private void StartFolderSizeScan(string path, TextBlock sizeLine, EntryList list, CancellationToken token)
    {
        // The pulse's clock is held so it can be stopped: a forever clock
        // that is only detached keeps WPF rendering frames for nobody.
        AnimationClock? pulse = null;
        if (Motion.Enabled)
        {
            pulse = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            }.CreateClock();
            sizeLine.ApplyAnimationClock(OpacityProperty, pulse);
        }

        _ = RunFolderSizeScanAsync(path, sizeLine, list, pulse, token);
    }

    /// <summary>
    /// One walker owns the disk at a time. Superseded folders cancel while
    /// they wait, so rapid browsing leaves only the newest one queued.
    /// </summary>
    private async Task RunFolderSizeScanAsync(string path, TextBlock sizeLine, EntryList list, AnimationClock? pulse, CancellationToken token)
    {
        var entered = false;
        try
        {
            // The first frame wins. Disk enumeration starts only after the
            // swap and reveal have had an uncontested dispatcher turn.
            await Task.Delay(FolderSizeScanStartDelay, token);
            await _folderSizeScanGate.WaitAsync(token);
            entered = true;

            var dispatcher = sizeLine.Dispatcher;
            var listed = false;
            var finished = false;
            var scan = await Task.Run(
                () => FolderScanner.Scan(
                    path,
                    FolderSizeScanEntryBudget,
                    FolderSizeScanTimeBudget,
                    progress => dispatcher.BeginInvoke(() =>
                    {
                        // The await below may resume ahead of queued
                        // progress; a late snapshot never undoes the result.
                        if (finished || token.IsCancellationRequested)
                        {
                            return;
                        }

                        sizeLine.Text = FolderScanner.DescribeProgress(progress);
                        ShowFolderRows(list, progress, rebuild: !listed);
                        listed = true;
                    }),
                    token),
                token);

            finished = true;
            sizeLine.Text = FolderScanner.Describe(scan);
            if (!scan.RootUnreadable)
            {
                // Sizes are a ranking only when every child has one. Sort
                // once, at the end, so rows never shuffle under the pointer
                // while the walk runs.
                var final = scan.Stopped ? scan : scan with { Children = SortBySize(scan.Children) };
                ShowFolderRows(list, final, rebuild: !listed || !scan.Stopped);
                _folderHover?.SetWarmPaths(final.Children
                    .Where(child => child.IsDirectory)
                    .Take(FolderBreakdownRowsCap)
                    .Select(child => Path.Combine(path, child.Name))
                    .ToList());
            }
        }
        catch (OperationCanceledException)
        {
            // A newer folder owns the only pending slot.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"folder size scan failed {path}: {ex.GetType().Name}: {ex.Message}");
            if (!token.IsCancellationRequested)
            {
                sizeLine.Text = "Size unavailable";
            }
        }
        finally
        {
            // Done, failed or cancelled: the pulse never outlives the walk.
            pulse?.Controller?.Stop();
            sizeLine.ApplyAnimationClock(OpacityProperty, null);
            sizeLine.Opacity = 1;
            if (entered)
            {
                _folderSizeScanGate.Release();
            }
        }
    }

    private static IReadOnlyList<FolderChild> SortBySize(IReadOnlyList<FolderChild> children) =>
        children
            .OrderByDescending(child => child.Bytes ?? -1)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void ShowFolderRows(EntryList list, FolderScan scan, bool rebuild)
    {
        var largest = scan.Children.Max(child => child.Bytes) ?? 0;
        double Share(FolderChild child) => largest > 0 && child.Bytes is { } bytes ? (double)bytes / largest : 0;

        if (rebuild)
        {
            list.Show(
                scan.Children
                    .Select(child => new EntryNode(child.Name, child.IsDirectory, FolderScanner.DescribeChild(child), Share(child), child.IsLink))
                    .ToList(),
                moreAtLeast: !scan.ListedEveryChild);
            return;
        }

        for (var i = 0; i < Math.Min(scan.Children.Count, FolderBreakdownRowsCap); i++)
        {
            list.Update(i, FolderScanner.DescribeChild(scan.Children[i]), Share(scan.Children[i]));
        }
    }

    /// <summary>
    /// A slim scrollbar that only materializes while the pointer is over the
    /// list (the wheel needs the pointer there anyway) and vanishes after.
    /// </summary>
    private static void StyleScrollViewer(ScrollViewer scroll)
    {
        ApplyScrollBarStyle(scroll);
        scroll.Loaded += (_, _) =>
        {
            foreach (var bar in FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(scroll))
            {
                bar.Width = 7;
                bar.Opacity = 0;
            }
        };
        scroll.MouseEnter += (_, _) =>
        {
            // Only materialize when there is genuinely something to scroll.
            if (scroll.ScrollableHeight > 0 || scroll.ScrollableWidth > 0)
            {
                FadeScrollBars(scroll, 1, 120);
            }
        };
        scroll.MouseLeave += (_, _) => FadeScrollBars(scroll, 0, 350);
    }

    private static void FadeScrollBars(ScrollViewer scroll, double to, int milliseconds)
    {
        foreach (var bar in FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(scroll))
        {
            bar.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private void OpenFolderChild(string path, bool isDirectory)
    {
        try
        {
            var arguments = isDirectory ? QuoteArgument(path) : $"/select,{QuoteArgument(path)}";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"open folder child failed {path}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task UpdateFolderArtworkAsync(
        string path,
        int pixels,
        Action<BitmapSource> apply,
        CancellationToken token)
    {
        var entered = false;
        try
        {
            await _shellIconGate.WaitAsync(token);
            entered = true;
            var icon = await Task.Run(() => ShellThumbnailProvider.TryGetIcon(path, pixels), token);
            if (token.IsCancellationRequested || icon is null)
            {
                return;
            }

            apply(icon);
        }
        catch (OperationCanceledException)
        {
            // A newer preview owns the shell lane now.
        }
        catch (Exception ex)
        {
            // The built-in folder mark is the permanent fallback.
            DiagnosticLog.Write($"folder icon failed {path}: {ex.GetType().Name}");
        }
        finally
        {
            if (entered)
            {
                _shellIconGate.Release();
            }
        }
    }

    private static FrameworkElement BuildFolderArtwork(BitmapSource? shellFolder, double pixelsPerDip)
    {
        if (shellFolder is not null)
        {
            return IconVisual(shellFolder, FolderArtSide, pixelsPerDip);
        }

        // A complete code-native folder, not a loading glyph. Shell artwork
        // may replace it, but a cold or unavailable shell can leave this on
        // screen forever without making the preview look unfinished.
        var folder = new Grid
        {
            Width = 184,
            Height = 150
        };

        folder.Children.Add(new Border
        {
            Width = 84,
            Height = 42,
            Margin = new Thickness(10, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(11, 11, 4, 4),
            Background = Theme.Brush(Color.FromRgb(245, 180, 27)),
            BorderBrush = Theme.Brush(Color.FromRgb(215, 145, 5)),
            BorderThickness = new Thickness(1)
        });
        var shadow = new DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 4,
            Direction = 270,
            Color = Colors.Black
        };
        folder.Children.Add(new Border
        {
            Width = 184,
            Height = 122,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(12),
            Background = new LinearGradientBrush(
                Color.FromRgb(255, 222, 126),
                Color.FromRgb(255, 197, 56),
                90),
            BorderBrush = Theme.Brush(Color.FromRgb(218, 151, 8)),
            BorderThickness = new Thickness(1),
            Effect = shadow
        });
        Theme.Follow(folder, () => shadow.Opacity = Theme.Current.IsDark ? 0.3 : 0.16);

        // The slot shrinks on short stages; the drawing shrinks with it.
        return new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Child = folder
        };
    }

    /// <summary>
    /// A hovered child takes the folder art's slot. A folder keeps the
    /// resting folder's exact outline; a file shows exactly as its own card
    /// would, thumbnail or icon.
    /// </summary>
    private static FrameworkElement BuildHoverArtwork(BitmapSource? restingFolder, bool isDirectory, ShellImage shellImage, double pixelsPerDip)
    {
        if (isDirectory)
        {
            var bitmap = restingFolder is null
                ? shellImage.Bitmap
                : MatchFolderArtworkBounds(shellImage.Bitmap, restingFolder);
            return shellImage.IsThumbnail
                ? ThumbnailVisual(bitmap, pixelsPerDip)
                : IconVisual(bitmap, FolderArtSide, pixelsPerDip);
        }

        return BuildCardVisual(shellImage, pixelsPerDip);
    }

    internal static BitmapSource MatchFolderArtworkBounds(BitmapSource artwork, BitmapSource restingFolder)
    {
        BitmapSource resized = artwork;
        if (artwork.PixelWidth != restingFolder.PixelWidth
            || artwork.PixelHeight != restingFolder.PixelHeight)
        {
            resized = new TransformedBitmap(
                artwork,
                new ScaleTransform(
                    (double)restingFolder.PixelWidth / artwork.PixelWidth,
                    (double)restingFolder.PixelHeight / artwork.PixelHeight));
            resized.Freeze();
        }

        var hoverBgra = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        var restingBgra = new FormatConvertedBitmap(restingFolder, PixelFormats.Bgra32, null, 0);
        var stride = restingFolder.PixelWidth * 4;
        var hoverPixels = new byte[stride * restingFolder.PixelHeight];
        var restingPixels = new byte[hoverPixels.Length];
        hoverBgra.CopyPixels(hoverPixels, stride, 0);
        restingBgra.CopyPixels(restingPixels, stride, 0);

        // Windows' preview-folder edge is slightly more opaque than its closed
        // folder edge. Cap only alpha so the child artwork stays intact while
        // every antialiased boundary follows the resting folder exactly.
        for (var alpha = 3; alpha < hoverPixels.Length; alpha += 4)
        {
            hoverPixels[alpha] = Math.Min(hoverPixels[alpha], restingPixels[alpha]);
        }

        var matched = BitmapSource.Create(
            restingFolder.PixelWidth,
            restingFolder.PixelHeight,
            restingFolder.DpiX,
            restingFolder.DpiY,
            PixelFormats.Bgra32,
            null,
            hoverPixels,
            stride);
        matched.Freeze();
        return matched;
    }
}
