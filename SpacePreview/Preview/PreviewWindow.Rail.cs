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
        Action<int> navigate,
        Action<int, Border, bool> register)
    {
        tile.MouseEnter += (_, _) =>
        {
            if (!IsRailTileSelected(tile))
            {
                tile.Themed(Border.BackgroundProperty, "Theme.HoverBg");
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
            ApplyRailTileState(tile, selected: true);
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
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = surface
        }.Themed(Border.BackgroundProperty, "Theme.ChromeBg").Themed(Border.BorderBrushProperty, "Theme.Border");
    }

    private static FrameworkElement BuildCollapsedRail(int count, string tooltip, Action show)
    {
        var button = BuildRailChromeButton("", tooltip, show);
        return new Border
        {
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
                        FontSize = Metrics.FontCaption
                    }.Themed(TextBlock.ForegroundProperty, "Theme.TextTertiary")
                }
            }
        }.Themed(Border.BackgroundProperty, "Theme.ChromeBg").Themed(Border.BorderBrushProperty, "Theme.Border");
    }

    private static Border BuildRailChromeButton(string glyph, string tooltip, Action action)
    {
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
                VerticalAlignment = VerticalAlignment.Center
            }.Themed(TextBlock.ForegroundProperty, "Theme.TextSecondary")
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.MouseEnter += (_, _) => button.Themed(Border.BackgroundProperty, "Theme.HoverBg");
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonDown += (_, e) =>
        {
            button.Themed(Border.BackgroundProperty, "Theme.PressedBg");
            e.Handled = true;
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            if (button.IsMouseOver)
            {
                button.Themed(Border.BackgroundProperty, "Theme.HoverBg");
            }
            else
            {
                button.Background = Brushes.Transparent;
            }

            action();
            e.Handled = true;
        };
        return button;
    }

    /// <summary>The selected row: the hover surface, ringed in the accent.</summary>
    private static void ApplyRailTileState(Border tile, bool selected)
    {
        tile.Tag = selected;
        if (selected)
        {
            tile.Themed(Border.BackgroundProperty, "Theme.HoverBg").Themed(Border.BorderBrushProperty, "Theme.Accent");
        }
        else
        {
            tile.Background = Brushes.Transparent;
            tile.BorderBrush = Brushes.Transparent;
        }
    }

    private static bool IsRailTileSelected(Border tile) => tile.Tag is true;
}
