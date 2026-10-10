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
    private static FrameworkElement BuildFontPreview(string path)
    {
        var palette = Theme.Current;
        string familyName;
        FontFamily? fontFamily = null;
        try
        {
            var glyph = new GlyphTypeface(new Uri(path, UriKind.Absolute));
            familyName = glyph.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path);
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                fontFamily = new FontFamily(new Uri(folder + Path.DirectorySeparatorChar, UriKind.Absolute), "./#" + familyName);
            }
        }
        catch
        {
            familyName = Path.GetFileNameWithoutExtension(path);
        }

        var stack = new StackPanel { Margin = new Thickness(34, 28, 34, 34) };
        stack.Children.Add(new TextBlock
        {
            Text = familyName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary)
        });
        stack.Children.Add(new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(32, 30, 32, 34),
            CornerRadius = Metrics.CardCorner,
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "AaBbCc 0123456789",
                        FontFamily = fontFamily ?? new FontFamily("Segoe UI"),
                        FontSize = 46,
                        Foreground = Theme.Brush(palette.TextPrimary),
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "The quick brown fox jumps over the lazy dog.",
                        Margin = new Thickness(0, 18, 0, 0),
                        FontFamily = fontFamily ?? new FontFamily("Segoe UI"),
                        FontSize = 24,
                        Foreground = Theme.Brush(palette.TextSecondary),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        });

        return BuildScrollSurface(stack, new Thickness(0), horizontal: false, palette.CanvasBg);
    }
}
