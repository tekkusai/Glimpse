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
    private static void ConfigureRailTile(
        Border tile,
        int index,
        bool selected,
        ThemePalette palette,
        Action<int> navigate,
        Action<int, Border, bool> register)
    {
        tile.MouseEnter += (_, _) =>
        {
            if (!IsRailTileSelected(tile))
            {
                tile.Background = Theme.Brush(palette.HoverBg);
            }
        };
        tile.MouseLeave += (_, _) =>
        {
            if (!IsRailTileSelected(tile))
            {
                tile.Background = Brushes.Transparent;
            }
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            navigate(index);
            e.Handled = true;
        };

        if (selected)
        {
            ApplyRailTileState(tile, palette, selected: true);
        }

        register(index, tile, selected);
    }

    private static FrameworkElement BuildRailSurface(
        Grid header,
        StackPanel content,
        double verticalOffset,
        Action<double> rememberScroll,
        Action<ScrollViewer>? registerScroll = null,
        Action<ScrollViewer>? onLoaded = null)
    {
        var palette = Theme.Current;
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        StyleScrollViewer(scroll);
        registerScroll?.Invoke(scroll);
        scroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0)
            {
                rememberScroll(e.VerticalOffset);
            }
        };
        scroll.Loaded += (_, _) =>
        {
            if (onLoaded is null)
            {
                scroll.ScrollToVerticalOffset(verticalOffset);
            }
            else
            {
                onLoaded(scroll);
            }
        };

        var surface = new Grid();
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.Children.Add(new Border
        {
            Background = Theme.Brush(Color.FromArgb(238, palette.ChromeBg.R, palette.ChromeBg.G, palette.ChromeBg.B)),
            Child = header
        });
        Grid.SetRow(scroll, 1);
        surface.Children.Add(scroll);

        return new Border
        {
            Background = Theme.Brush(Color.FromArgb(180, palette.ChromeBg.R, palette.ChromeBg.G, palette.ChromeBg.B)),
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = surface
        };
    }

    private static FrameworkElement BuildCollapsedRail(int count, string tooltip, Action show)
    {
        var palette = Theme.Current;
        var button = BuildRailChromeButton("\uE76C", tooltip, show);
        return new Border
        {
            Background = Theme.Brush(Color.FromArgb(180, palette.ChromeBg.R, palette.ChromeBg.G, palette.ChromeBg.B)),
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new StackPanel
            {
                Margin = new Thickness(4, 12, 4, 0),
                Children =
                {
                    button,
                    new TextBlock
                    {
                        Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Margin = new Thickness(0, 8, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = Theme.Brush(palette.TextTertiary),
                        FontSize = 10
                    }
                }
            }
        };
    }

    private static Border BuildRailChromeButton(string glyph, string tooltip, Action action)
    {
        var palette = Theme.Current;
        var button = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = Metrics.IconCorner,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(palette.TextSecondary)
            }
        };
        button.MouseEnter += (_, _) => button.Background = Theme.Brush(palette.HoverBg);
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return button;
    }

    private static void ApplyRailTileState(Border tile, ThemePalette palette, bool selected)
    {
        tile.Tag = selected;
        tile.Background = selected
            ? Theme.Brush(Color.FromArgb(60, palette.Accent.R, palette.Accent.G, palette.Accent.B))
            : Brushes.Transparent;
        tile.BorderBrush = selected
            ? Theme.Brush(Color.FromArgb(160, palette.Accent.R, palette.Accent.G, palette.Accent.B))
            : Brushes.Transparent;
    }

    private static bool IsRailTileSelected(Border tile) => tile.Tag is true;
}
