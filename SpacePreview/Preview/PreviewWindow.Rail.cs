using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// The side rail a document carries (PDF pages, EPUB chapters): a column of
/// chrome beside the viewer, with the window header's surface and the
/// app's row states.
/// </summary>
public partial class PreviewWindow
{
    private static ContentControl BuildPdfPageRailHost()
    {
        return new ContentControl
        {
            Width = 0,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
    }

    private static void AnimateRail(ContentControl host, double targetWidth, FrameworkElement? content)
    {
        host.BeginAnimation(FrameworkElement.WidthProperty, null);
        if (content is not null)
        {
            content.Opacity = 0;
            host.Content = content;
            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else if (targetWidth < 1)
        {
            host.Content = null;
        }

        host.Width = targetWidth;
    }

    /// <summary>
    /// The viewer refits when the rail's column changes width; hide that
    /// refit behind a short fade of the document itself.
    /// </summary>
    private void CloakViewerForRailResize(WebView2 webView, long version)
    {
        if (webView.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            _ = core.ExecuteScriptAsync(
                "document.documentElement.style.transition='none';"
                + "document.documentElement.style.opacity='0';");
        }
        catch
        {
            return;
        }

        RunAfter(TimeSpan.FromMilliseconds(90), () =>
        {
            if (version != _loadVersion || webView.CoreWebView2 is not { } currentCore)
            {
                return;
            }

            try
            {
                _ = currentCore.ExecuteScriptAsync(
                    "document.documentElement.style.transition='opacity 120ms ease-out';"
                    + "document.documentElement.style.opacity='1';");
            }
            catch
            {
                // The resize itself is still valid; the script is just polish.
            }
        });
    }

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
        surface.Children.Add(header);
        Grid.SetRow(scroll, 1);
        surface.Children.Add(scroll);

        return new Border
        {
            Background = Theme.Brush(palette.ChromeBg),
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = surface
        };
    }

    private static FrameworkElement BuildCollapsedRail(int count, string tooltip, Action show)
    {
        var palette = Theme.Current;
        var button = BuildRailChromeButton("", tooltip, show);
        return new Border
        {
            Background = Theme.Brush(palette.ChromeBg),
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new StackPanel
            {
                Margin = new Thickness(4, 10, 4, 0),
                Children =
                {
                    button,
                    new TextBlock
                    {
                        Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Margin = new Thickness(0, 6, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = Theme.Brush(palette.TextTertiary),
                        FontSize = Metrics.FontCaption
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
            Width = 24,
            Height = 24,
            CornerRadius = Metrics.IconCorner,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(palette.TextSecondary)
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.MouseEnter += (_, _) => button.Background = Theme.Brush(palette.HoverBg);
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonDown += (_, e) =>
        {
            button.Background = Theme.Brush(palette.PressedBg);
            e.Handled = true;
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            button.Background = button.IsMouseOver ? Theme.Brush(palette.HoverBg) : Brushes.Transparent;
            action();
            e.Handled = true;
        };
        return button;
    }

    /// <summary>The selected row: the hover surface, ringed in the accent.</summary>
    private static void ApplyRailTileState(Border tile, ThemePalette palette, bool selected)
    {
        tile.Tag = selected;
        tile.Background = selected ? Theme.Brush(palette.HoverBg) : Brushes.Transparent;
        tile.BorderBrush = selected ? Theme.Brush(palette.Accent) : Brushes.Transparent;
    }

    private static bool IsRailTileSelected(Border tile) => tile.Tag is true;
}
