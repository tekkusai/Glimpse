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
    private FrameworkElement BuildDirectoryCard(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var data = CollectDirectoryCardData(path);
        var palette = Theme.Current;
        var sizeLine = new TextBlock
        {
            Text = "Calculating size…",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 13,
            Foreground = Theme.Brush(palette.TextTertiary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var breakdown = new StackPanel
        {
            Width = 340,
            Margin = new Thickness(0, 16, 0, 0)
        };

        ContentControl? visualHost = null;
        var card = BuildFileCard(data, sizeLine, breakdown, host =>
        {
            visualHost = host;
            visualHost.Width = FolderArtworkSlotWidth;
            visualHost.Height = FolderArtworkSlotHeight;
            visualHost.VerticalContentAlignment = VerticalAlignment.Center;
            visualHost.Content = BuildFolderArtwork(data);
        });
        if (visualHost is not null)
        {
            var folderArtworkData = data;
            ShellImage? hoveredArtwork = null;
            var hoveredArtworkIsDirectory = false;

            void RenderArtwork() => visualHost.Content = hoveredArtwork is null
                ? BuildFolderArtwork(folderArtworkData)
                : BuildHoverArtwork(folderArtworkData, hoveredArtworkIsDirectory, hoveredArtwork);

            _ = UpdateFolderArtworkAsync(path, icon =>
            {
                folderArtworkData = data with { Image = icon };
                RenderArtwork();
            }, token);
            var hover = new FolderHoverThumbnail((isDirectory, shellImage) =>
            {
                hoveredArtwork = shellImage;
                hoveredArtworkIsDirectory = isDirectory;
                RenderArtwork();
            }, (int)FolderArtworkSlotWidth);
            card.Loaded += (_, _) => _folderHover = hover;
            card.Unloaded += (_, _) =>
            {
                if (ReferenceEquals(_folderHover, hover))
                {
                    hover.Reset();
                    _folderHover = null;
                }
            };
        }

        StartFolderSizeScan(path, sizeLine, breakdown, token);
        return card;
    }

    private sealed record FolderChild(string Name, long Bytes, bool IsDirectory);
    private const int FolderBreakdownRowsCap = 300;
    private const int FolderSizeScanEntryBudget = 25000;
    private static readonly TimeSpan FolderSizeScanStartDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FolderSizeScanTimeBudget = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Walks the folder in the background, updating the card line live and
    /// finishing with a size breakdown of the largest children. One walker
    /// owns the disk at a time. Superseded requests cancel while waiting, so
    /// rapid browsing leaves only the newest folder behind the current walk.
    /// </summary>
    private void StartFolderSizeScan(string path, TextBlock target, StackPanel breakdown, CancellationToken token)
    {
        var pulse = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        target.BeginAnimation(OpacityProperty, pulse);

        _ = RunFolderSizeScanAsync(path, target, breakdown, token);
    }

    private async Task RunFolderSizeScanAsync(string path, TextBlock target, StackPanel breakdown, CancellationToken token)
    {
        var entered = false;
        try
        {
            // The first frame wins. Disk enumeration starts only after the
            // 110 ms swap/reveal beat has had an uncontested dispatcher turn.
            await Task.Delay(FolderSizeScanStartDelay, token);
            await _folderSizeScanGate.WaitAsync(token);
            entered = true;
            await Task.Run(() => ScanFolderSize(path, target, breakdown, token), token);
        }
        catch (OperationCanceledException)
        {
            // A newer folder owns the only pending slot.
        }
        finally
        {
            if (entered)
            {
                _folderSizeScanGate.Release();
            }
        }
    }

    private void ScanFolderSize(string path, TextBlock target, StackPanel breakdown, CancellationToken token)
    {
        long totalBytes = 0;
        long files = 0;
        long folders = 0;
        var entriesScanned = 0;
        var partial = false;
        var children = new List<FolderChild>();
        var lastReport = Stopwatch.StartNew();
        var scanClock = Stopwatch.StartNew();

        bool BudgetReached()
        {
            entriesScanned++;
            if (entriesScanned < FolderSizeScanEntryBudget && scanClock.Elapsed < FolderSizeScanTimeBudget)
            {
                return false;
            }

            partial = true;
            return true;
        }

        void ReportProgress()
        {
            if (lastReport.ElapsedMilliseconds <= 150)
            {
                return;
            }

            lastReport.Restart();
            var text = $"Calculating… {FileKinds.FormatSize(totalBytes)} so far";
            target.Dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    target.Text = text;
                }
            });
        }

        try
        {
            var recursive = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var topLevel = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", topLevel))
            {
                token.ThrowIfCancellationRequested();
                if (BudgetReached())
                {
                    break;
                }

                if (entry is FileInfo file)
                {
                    totalBytes += file.Length;
                    files++;
                    children.Add(new FolderChild(file.Name, file.Length, IsDirectory: false));
                }
                else if (entry is DirectoryInfo directory)
                {
                    folders++;

                    // AttributesToSkip only filters what enumeration
                    // yields; explicitly recursing into a junction still
                    // follows it. A child junction pointing up the tree
                    // would otherwise walk forever.
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        children.Add(new FolderChild(directory.Name, 0, IsDirectory: true));
                        continue;
                    }

                    long childBytes = 0;
                    foreach (var descendant in directory.EnumerateFileSystemInfos("*", recursive))
                    {
                        token.ThrowIfCancellationRequested();
                        if (BudgetReached())
                        {
                            break;
                        }

                        if (descendant is FileInfo descendantFile)
                        {
                            childBytes += descendantFile.Length;
                            files++;
                        }
                        else
                        {
                            folders++;
                        }

                        ReportProgress();
                    }

                    totalBytes += childBytes;
                    children.Add(new FolderChild(directory.Name, childBytes, IsDirectory: true));
                }

                ReportProgress();

                if (partial)
                {
                    break;
                }
            }

            var summary = files == 0 && folders == 0
                ? partial ? "Still scanning…" : "Empty"
                : $"{(partial ? "At least " : string.Empty)}{FileKinds.FormatSize(totalBytes)} · {folders:N0} {(folders == 1 ? "folder" : "folders")} · {files:N0} {(files == 1 ? "file" : "files")}";
            var sorted = children.Where(c => c.Bytes > 0).OrderByDescending(c => c.Bytes).ToList();

            target.Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                target.Text = summary;
                target.BeginAnimation(OpacityProperty, null);
                target.Opacity = 1;
                FillFolderBreakdown(breakdown, path, sorted);
            });
        }
        catch (OperationCanceledException)
        {
            // Preview moved on; stop the background scan.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"folder size scan failed {path}: {ex.GetType().Name}: {ex.Message}");
            target.Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                target.Text = "Size unavailable";
                target.BeginAnimation(OpacityProperty, null);
                target.Opacity = 1;
            });
        }
    }
    private void FillFolderBreakdown(StackPanel breakdown, string rootPath, IReadOnlyList<FolderChild> children)
    {
        if (children.Count == 0)
        {
            return;
        }

        var palette = Theme.Current;
        var glyphFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        var maxBytes = children[0].Bytes;

        var rows = new StackPanel();
        var shown = 0;
        foreach (var child in children.Take(FolderBreakdownRowsCap))
        {
            AddFolderBreakdownEntry(rows, rootPath, child, maxBytes, palette, glyphFont);
            shown++;
        }

        _folderHover?.SetWarmPaths(children
            .Where(child => child.IsDirectory)
            .Take(FolderBreakdownRowsCap)
            .Select(child => Path.Combine(rootPath, child.Name))
            .ToList());

        if (children.Count > shown)
        {
            rows.Children.Add(new TextBlock
            {
                Text = $"…and {children.Count - shown:N0} more items",
                FontSize = 12,
                Margin = new Thickness(4, 8, 0, 4),
                Foreground = Theme.Brush(palette.TextTertiary)
            });
        }

        // One list, always scrollable: about four rows tall, with a half-row
        // peek of the fifth so it is obvious there is more below. No mode,
        // no toggle; the wheel is the whole interaction, and the hover
        // scrollbar is the hint for anyone who needs one.
        var scroll = new ScrollViewer
        {
            MaxHeight = 136,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = rows
        };
        StyleScrollViewer(scroll);
        breakdown.Children.Add(scroll);

        breakdown.Opacity = 0;
        breakdown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
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


    private void AddFolderBreakdownEntry(StackPanel host, string rootPath, FolderChild child, long maxBytes, ThemePalette palette, FontFamily glyphFont)
    {
        var row = new Grid { Margin = new Thickness(6, 0, 6, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock
        {
            Text = child.IsDirectory ? "\uE8B7" : "\uE7C3",
            FontFamily = glyphFont,
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(child.IsDirectory ? palette.Accent : palette.TextTertiary)
        };
        Grid.SetColumn(glyph, 0);
        row.Children.Add(glyph);

        var name = new TextBlock
        {
            Text = child.Name,
            FontSize = 12.5,
            Foreground = Theme.Brush(palette.TextSecondary),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var size = new TextBlock
        {
            Text = FileKinds.FormatSize(child.Bytes),
            FontSize = 12,
            Margin = new Thickness(14, 0, 0, 0),
            Foreground = Theme.Brush(palette.TextTertiary)
        };
        Grid.SetColumn(size, 2);
        row.Children.Add(size);

        var bar = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(6, 328.0 * child.Bytes / Math.Max(1, maxBytes)),
            Margin = new Thickness(6, 3, 6, 5),
            Background = Theme.Brush(Color.FromArgb(150, palette.Accent.R, palette.Accent.G, palette.Accent.B))
        };

        var content = new StackPanel { Children = { row, bar } };

        // Each entry is a doorway: folders open, files get revealed.
        var childPath = Path.Combine(rootPath, child.Name);
        var isDirectory = child.IsDirectory;
        var entry = new Border
        {
            CornerRadius = Metrics.RowCorner,
            Padding = new Thickness(0, 4, 0, 0),
            Margin = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = isDirectory ? "Open folder" : "Show in Explorer",
            Child = content
        };
        entry.MouseEnter += (_, _) =>
        {
            entry.Background = Theme.Brush(palette.HoverBg);

            _folderHover?.OnRowEnter(entry, childPath, isDirectory);
        };
        entry.MouseLeave += (_, _) =>
        {
            entry.Background = Brushes.Transparent;
            _folderHover?.OnRowLeave(entry);
        };
        entry.MouseLeftButtonDown += (_, e) => e.Handled = true;
        entry.MouseLeftButtonUp += (_, e) =>
        {
            OpenFolderChild(childPath, isDirectory);
            e.Handled = true;
        };

        host.Children.Add(entry);
    }

    private void OpenFolderChild(string path, bool isDirectory)
    {
        try
        {
            var arguments = isDirectory ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"open folder child failed {path}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private const int ArchiveRootCap = 500;
    private const int ArchiveChildrenCap = 200;

    /// <summary>
    /// The folder card is the first frame, not the census. Its title comes
    /// from the path; all shell and filesystem enrichment lands later.
    /// </summary>
    private static FileCardData CollectDirectoryCardData(string path)
    {
        var title = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = path;
        }

        return new FileCardData(
            Image: null,
            IsThumbnail: false,
            Title: title,
            Lines: [],
            BadgeText: "Folder");
    }

    private async Task UpdateFolderArtworkAsync(
        string path,
        Action<BitmapSource> apply,
        CancellationToken token)
    {
        var entered = false;
        try
        {
            await _shellIconGate.WaitAsync(token);
            entered = true;
            var icon = await Task.Run(
                () => ShellThumbnailProvider.TryGetIcon(path, (int)FolderArtworkSlotWidth),
                token);
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
        catch
        {
            // The built-in folder mark is the permanent fallback.
        }
        finally
        {
            if (entered)
            {
                _shellIconGate.Release();
            }
        }
    }

    private static FrameworkElement BuildFolderArtwork(FileCardData data)
    {
        if (data.Image is not null)
        {
            return BuildFileCardVisual(data, FolderArtworkSlotWidth, FolderArtworkSlotHeight, 184, 184);
        }

        // A complete code-native folder, not a loading glyph. Shell artwork
        // may replace it, but a cold or unavailable shell can leave this on
        // screen forever without making the preview look unfinished.
        var folder = new Grid
        {
            Width = 184,
            Height = 150,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
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
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 4,
                Direction = 270,
                Color = Colors.Black,
                Opacity = Theme.Current.IsDark ? 0.3 : 0.16
            }
        });
        return folder;
    }

    /// <summary>
    /// A hovered FILE with only an icon renders exactly like its own file
    /// card would: same side, no blow-up to the artwork slot. Folders and
    /// real thumbnails keep the large artwork treatment, matching how their
    /// own cards present.
    /// </summary>
    private static FrameworkElement BuildHoverArtwork(FileCardData data, bool isDirectory, ShellImage shellImage)
    {
        if (isDirectory)
        {
            if (data.Image is { } restingFolder)
            {
                shellImage = shellImage with
                {
                    Bitmap = MatchFolderArtworkBounds(shellImage.Bitmap, restingFolder)
                };
            }

            return BuildFolderArtwork(data with { Image = shellImage.Bitmap, IsThumbnail = shellImage.IsThumbnail });
        }

        if (shellImage.IsThumbnail)
        {
            return BuildFolderArtwork(data with { Image = shellImage.Bitmap, IsThumbnail = shellImage.IsThumbnail });
        }

        return BuildFileCardVisual(
            data with { Image = shellImage.Bitmap, IsThumbnail = false },
            FolderArtworkSlotWidth,
            FolderArtworkSlotHeight,
            FileCardIconSide,
            FileCardIconSide);
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
