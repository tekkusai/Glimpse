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
    private static FrameworkElement BuildArchivePreview(ArchiveListing listing)
    {
        var palette = Theme.Current;
        var glyphFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 26) };
        var summary = $"{listing.Kind} · {listing.TotalEntries:N0} files · {FileKinds.FormatSize(listing.TotalUncompressed)} uncompressed";
        if (listing.Truncated)
        {
            summary += " · partial contents";
        }

        stack.Children.Add(new TextBlock
        {
            Text = summary,
            Margin = new Thickness(4, 0, 0, 12),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextSecondary)
        });

        // Top level only; folders expand on demand. Deep contents build
        // lazily, so a huge archive costs nothing until it is explored.
        var shown = 0;
        foreach (var node in listing.Roots.Take(ArchiveRootCap))
        {
            AddArchiveNode(stack, node, depth: 0, palette, glyphFont);
            shown++;
        }

        if (listing.Roots.Count > shown)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"…and {listing.Roots.Count - shown:N0} more items",
                FontSize = 12,
                Margin = new Thickness(31, 4, 0, 8),
                Foreground = Theme.Brush(palette.TextTertiary)
            });
        }

        return BuildScrollSurface(stack, new Thickness(0), horizontal: false, palette.CanvasBg);
    }

    private static void AddArchiveNode(StackPanel host, ArchiveNode node, int depth, ThemePalette palette, FontFamily glyphFont)
    {
        var indent = Math.Min(depth, 12) * 20;
        var row = new Grid { Height = 27, Margin = new Thickness(indent, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var expandable = node.IsDirectory && node.Children.Count > 0;
        TextBlock? expander = null;
        if (expandable)
        {
            expander = new TextBlock
            {
                Text = "\uE76C",
                FontFamily = glyphFont,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(palette.TextTertiary),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform()
            };
            Grid.SetColumn(expander, 0);
            row.Children.Add(expander);
        }

        var glyph = new TextBlock
        {
            Text = node.IsDirectory ? "\uE8B7" : "\uE7C3",
            FontFamily = glyphFont,
            FontSize = 13,
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(node.IsDirectory ? palette.Accent : palette.TextTertiary)
        };
        Grid.SetColumn(glyph, 1);
        row.Children.Add(glyph);

        var name = new TextBlock
        {
            Text = node.Name,
            FontSize = 13,
            FontWeight = node.IsDirectory ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextPrimary),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 2);
        row.Children.Add(name);

        var detail = new TextBlock
        {
            Text = node.IsDirectory
                ? $"{node.FileCount:N0} {(node.FileCount == 1 ? "item" : "items")} · {FileKinds.FormatSize(node.Size)}"
                : FileKinds.FormatSize(node.Size),
            FontSize = 12,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextTertiary)
        };
        Grid.SetColumn(detail, 3);
        row.Children.Add(detail);

        var entry = new Border
        {
            CornerRadius = Metrics.RowCorner,
            Padding = new Thickness(4, 0, 6, 0),
            Background = Brushes.Transparent,
            Child = row
        };
        host.Children.Add(entry);

        if (!expandable)
        {
            return;
        }

        var childHost = new StackPanel { Visibility = Visibility.Collapsed };
        host.Children.Add(childHost);
        var built = false;
        var expanded = false;

        entry.Cursor = Cursors.Hand;
        entry.ToolTip = null;
        entry.MouseEnter += (_, _) => entry.Background = Theme.Brush(palette.HoverBg);
        entry.MouseLeave += (_, _) => entry.Background = Brushes.Transparent;
        entry.MouseLeftButtonDown += (_, e) => e.Handled = true;
        entry.MouseLeftButtonUp += (_, e) =>
        {
            expanded = !expanded;
            if (expanded && !built)
            {
                built = true;
                var shown = 0;
                foreach (var child in node.Children)
                {
                    if (shown >= ArchiveChildrenCap)
                    {
                        childHost.Children.Add(new TextBlock
                        {
                            Text = $"…and {node.Children.Count - shown:N0} more items",
                            FontSize = 12,
                            Margin = new Thickness(Math.Min(depth + 1, 12) * 20 + 31, 3, 0, 6),
                            Foreground = Theme.Brush(palette.TextTertiary)
                        });
                        break;
                    }

                    AddArchiveNode(childHost, child, depth + 1, palette, glyphFont);
                    shown++;
                }
            }

            childHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (expanded)
            {
                childHost.Opacity = 0;
                childHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }

            ((RotateTransform)expander!.RenderTransform).BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(expanded ? 90 : 0, TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            e.Handled = true;
        };
    }
}
