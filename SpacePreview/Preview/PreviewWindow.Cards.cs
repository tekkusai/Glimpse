using System.Diagnostics;
using System.IO;
using System.Text;
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
    // ---------------------------------------------------------------------
    // Header actions that only appear where they can work.
    // ---------------------------------------------------------------------

    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Offered only when the shell has a "runas" verb for the file's type:
    /// an .exe has one, a .dll does not, so the menu never holds a dead item.
    /// </summary>
    internal static bool CanRunAsAdministrator(string path)
    {
        if (!FileKinds.IsExecutableLike(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            return new ProcessStartInfo(path).Verbs.Contains("runas", StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"runas verb lookup failed {path}: {ex.GetType().Name}");
            return false;
        }
    }

    private void RunAsAdministrator(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // The user said no to UAC; the preview stays.
            return;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"run as admin failed {path}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenInTerminal(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d {QuoteArgument(folder)}") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    WorkingDirectory = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"open terminal failed {folder}: {ex.GetType().Name}: {ex.Message}");
                return;
            }
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Quotes one command-line argument. Backslashes before the closing quote
    /// double, or "C:\" would reach the program as C:" (an escaped quote).
    /// </summary>
    internal static string QuoteArgument(string value)
    {
        var trailing = value.Length - value.TrimEnd('\\').Length;
        return $"\"{value}{new string('\\', trailing)}\"";
    }

    // ---------------------------------------------------------------------
    // The file card: one layout for every file without a richer view.
    // Thumbnails may scale up; icons never do (an upscaled icon is blur).
    // ---------------------------------------------------------------------

    private const double CardIconSide = 144;
    private const double CardTileSide = 120;
    private static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private sealed record FileCardData(ShellImage? Image, string Title, IReadOnlyList<string> Facts);

    private async Task<FrameworkElement> BuildFileCardAsync(string path, CancellationToken token = default)
    {
        var dpi = PixelsPerDip();
        var iconPixels = DevicePixels(CardIconSide, dpi);
        var data = await Task.Run(() => CollectFileCardData(path, iconPixels, token), token);
        return BuildFileCard(BuildCardVisual(data.Image, dpi), data.Title, data.Facts);
    }

    private double PixelsPerDip() => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private static int DevicePixels(double dips, double pixelsPerDip) => (int)Math.Ceiling(dips * pixelsPerDip);

    private static FileCardData CollectFileCardData(string path, int iconPixels, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var shellImage = ShellThumbnailProvider.TryGetThumbnail(path, iconSize: iconPixels);
        token.ThrowIfCancellationRequested();

        var title = Path.GetFileName(path);
        IReadOnlyList<string> facts = [];
        if (FileKinds.IsExecutableLike(path))
        {
            facts = ReadExecutableFacts(path, title);
        }
        else if (FileKinds.IsInternetShortcut(path) && TryReadUrlTarget(path) is { } url)
        {
            facts = [url];
        }
        else if (FileKinds.IsShortcut(path))
        {
            // A shortcut that resolves previews its target instead; reaching
            // the card means the target is missing or out of reach.
            facts = ShortcutFacts(TryReadShortcutTarget(path));
        }

        return new FileCardData(shellImage, title, facts);
    }

    private static IReadOnlyList<string> ReadExecutableFacts(string path, string title)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return ExecutableFacts(info.FileDescription, info.ProductVersion ?? info.FileVersion, info.CompanyName, title);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"version info failed {path}: {ex.GetType().Name}");
            return [];
        }
    }

    /// <summary>
    /// What the header cannot say about a program: what it is, then its
    /// version and maker on one line.
    /// </summary>
    internal static IReadOnlyList<string> ExecutableFacts(string? description, string? version, string? company, string title)
    {
        var facts = new List<string>(2);
        description = description?.Trim();
        if (!string.IsNullOrEmpty(description)
            && !description.Equals(title, StringComparison.OrdinalIgnoreCase)
            && !description.Equals(Path.GetFileNameWithoutExtension(title), StringComparison.OrdinalIgnoreCase))
        {
            facts.Add(description);
        }

        var second = new List<string>(2);
        version = version?.Trim();
        if (!string.IsNullOrEmpty(version))
        {
            second.Add($"Version {(version.Length > 40 ? version[..40] + "…" : version)}");
        }

        company = company?.Trim();
        if (!string.IsNullOrEmpty(company))
        {
            second.Add(company);
        }

        if (second.Count > 0)
        {
            facts.Add(string.Join(" · ", second));
        }

        return facts;
    }

    internal static IReadOnlyList<string> ShortcutFacts(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return ["This shortcut doesn't point to a file"];
        }

        return File.Exists(target) || Directory.Exists(target)
            ? [target]
            : [target, "The target can't be found"];
    }

    private static string? TryReadShortcutTarget(string path)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            shell = shellType is null ? null : Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            shortcut = ((dynamic)shell).CreateShortcut(path);
            var target = ((dynamic)shortcut!).TargetPath as string;
            return string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"shortcut target read failed {path}: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static string? TryReadUrlTarget(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Take(50))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var url = line[4..].Trim();
                    return string.IsNullOrWhiteSpace(url) ? null : url;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"url target read failed {path}: {ex.GetType().Name}");
        }

        return null;
    }

    /// <summary>The shell's picture of a file: its thumbnail, else its icon, else a quiet tile.</summary>
    private static FrameworkElement BuildCardVisual(ShellImage? image, double pixelsPerDip)
    {
        if (image is null)
        {
            return GlyphTile("\uE7C3", Theme.Current.TextTertiary);
        }

        return image.IsThumbnail
            ? ThumbnailVisual(image.Bitmap, pixelsPerDip)
            : IconVisual(image.Bitmap, CardIconSide, pixelsPerDip);
    }

    /// <summary>
    /// An icon at most <paramref name="side"/> DIPs and never larger than the
    /// pixels the shell drew. The card's slot may shrink it further.
    /// </summary>
    private static FrameworkElement IconVisual(BitmapSource bitmap, double side, double pixelsPerDip)
    {
        var natural = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) / pixelsPerDip;
        var icon = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = Math.Min(side, natural),
            MaxHeight = Math.Min(side, natural),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        return icon;
    }

    /// <summary>A content thumbnail: rounded like every thumbnail, lifted by a soft shadow.</summary>
    private static FrameworkElement ThumbnailVisual(BitmapSource bitmap, double pixelsPerDip)
    {
        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = 2 * bitmap.PixelWidth / pixelsPerDip,
            MaxHeight = 2 * bitmap.PixelHeight / pixelsPerDip
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.SizeChanged += (_, e) => image.Clip = new RectangleGeometry(new Rect(e.NewSize), Metrics.RadiusSurface, Metrics.RadiusSurface);
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = CardShadow(),
            Child = image
        };
    }

    private static DropShadowEffect CardShadow() => new()
    {
        BlurRadius = 20,
        ShadowDepth = 4,
        Direction = 270,
        Color = Colors.Black,
        Opacity = Theme.Current.IsDark ? 0.32 : 0.14
    };

    /// <summary>The quiet tile that stands in for a picture: a glyph on a raised square.</summary>
    private static Border GlyphTile(string glyph, Color color)
    {
        var palette = Theme.Current;
        return new Border
        {
            Width = CardTileSide,
            Height = CardTileSide,
            CornerRadius = Metrics.CardCorner,
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            Background = Theme.Brush(palette.RaisedBg),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = GlyphFont,
                FontSize = 48,
                Foreground = Theme.Brush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static FrameworkElement BuildFileCard(FrameworkElement visual, string title, IReadOnlyList<string> facts, FrameworkElement? footer = null) =>
        BuildFileCard(visual, title, facts.Take(2).Select(FactLine).ToList(), footer);

    /// <summary>
    /// The card: <paramref name="visual"/> in the anchored slot, a 20 px
    /// title, at most two fact lines, and an optional footer.
    /// </summary>
    private static FrameworkElement BuildFileCard(FrameworkElement visual, string title, IReadOnlyList<TextBlock> facts, FrameworkElement? footer)
    {
        var palette = Theme.Current;
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = Metrics.FontTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        foreach (var fact in facts)
        {
            body.Children.Add(fact);
        }

        return new FileCardLayout(visual, body, footer)
        {
            Background = Theme.Brush(palette.CanvasBg)
        };
    }

    private static TextBlock FactLine(string text, int index) => new()
    {
        Text = text,
        Margin = new Thickness(0, 4, 0, 0),
        FontSize = Metrics.FontBody,
        Foreground = Theme.Brush(index == 0 ? Theme.Current.TextSecondary : Theme.Current.TextTertiary),
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    // ---------------------------------------------------------------------
    // Errors: the same card with a warning glyph, one sentence and at most
    // one action.
    // ---------------------------------------------------------------------

    internal sealed record PreviewAction(string Label, string Uri, string Tooltip);

    private static FrameworkElement BuildErrorPreview(string title, string detail, PreviewAction? action = null) =>
        BuildFileCard(
            GlyphTile("\uE7BA", Theme.Current.Warning),
            title,
            [detail],
            action is null ? null : BuildActionPill(action));

    /// <summary>The one action pill: the house pill, centered, opening a URI.</summary>
    internal static FrameworkElement BuildActionPill(PreviewAction action)
    {
        var pill = HousePill.Build(action.Label, () => OpenExternalUri(action.Uri));
        pill.Margin = new Thickness(0);
        pill.HorizontalAlignment = HorizontalAlignment.Center;
        pill.VerticalAlignment = VerticalAlignment.Top;
        pill.ToolTip = action.Tooltip;
        return pill;
    }

    // ---------------------------------------------------------------------
    // App packages: identity and reach. The trust line arrives once the
    // package has been hashed, after the card is on screen.
    // ---------------------------------------------------------------------

    private static readonly Dictionary<string, string> AppCapabilityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["internetClient"] = "Internet access",
        ["internetClientServer"] = "Accepts inbound internet connections",
        ["privateNetworkClientServer"] = "Local network access",
        ["picturesLibrary"] = "Pictures library",
        ["videosLibrary"] = "Videos library",
        ["musicLibrary"] = "Music library",
        ["documentsLibrary"] = "Documents library",
        ["removableStorage"] = "Removable drives",
        ["webcam"] = "Camera",
        ["microphone"] = "Microphone",
        ["location"] = "Location",
        ["broadFileSystemAccess"] = "Full file system access",
        ["runFullTrust"] = "Runs with full trust",
        ["allowElevation"] = "Can request administrator rights",
        ["packageManagement"] = "Can manage other apps",
        ["inputInjectionBrokered"] = "Can inject input"
    };

    private static readonly HashSet<string> AppCapabilityWarnings = new(StringComparer.OrdinalIgnoreCase)
    {
        "broadFileSystemAccess", "runFullTrust", "allowElevation", "packageManagement", "inputInjectionBrokered"
    };

    internal static (string Label, bool Known, bool Warning) DescribeAppCapability(string capability)
    {
        var warning = AppCapabilityWarnings.Contains(capability);
        return AppCapabilityLabels.TryGetValue(capability, out var label)
            ? (label, true, warning)
            : (HumanizeCapability(capability), false, warning);
    }

    /// <summary>
    /// An unmapped capability still reads as words, not manifest camelCase:
    /// "userNotificationListener" becomes "User notification listener".
    /// </summary>
    internal static string HumanizeCapability(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return capability;
        }

        var builder = new StringBuilder(capability.Length + 8);
        builder.Append(char.ToUpperInvariant(capability[0]));
        for (var i = 1; i < capability.Length; i++)
        {
            var c = capability[i];
            if (char.IsUpper(c) && !char.IsUpper(capability[i - 1]))
            {
                builder.Append(' ');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    internal static IReadOnlyList<string> AppPackageFacts(AppPackageInfo info)
    {
        var facts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(info.Publisher))
        {
            facts.Add(info.Publisher);
        }

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Version))
        {
            meta.Add($"Version {info.Version}");
        }

        if (!string.IsNullOrWhiteSpace(info.Architecture) && !info.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase))
        {
            meta.Add(info.Architecture);
        }

        if (info.IsBundle)
        {
            meta.Add("App bundle");
        }

        if (info.ApplicationCount > 1)
        {
            meta.Add($"{info.ApplicationCount:N0} apps");
        }

        if (meta.Count > 0)
        {
            facts.Add(string.Join(" · ", meta));
        }

        return facts;
    }

    private static FrameworkElement BuildAppPackageCard(AppPackageInfo info)
    {
        var palette = Theme.Current;
        var footer = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        // Reserved at its final height, so the line arriving moves nothing.
        var trust = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0
        };
        footer.Children.Add(trust);

        if (info.Capabilities.Count > 0)
        {
            footer.Children.Add(new TextBlock
            {
                Text = "Capabilities",
                Margin = new Thickness(0, 16, 0, 0),
                FontSize = Metrics.FontCaption,
                Foreground = Theme.Brush(palette.TextTertiary),
                TextAlignment = TextAlignment.Center
            });

            var chips = new CenteredWrapPanel
            {
                MaxWidth = 480,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            foreach (var capability in info.Capabilities)
            {
                chips.Children.Add(BuildAppCapabilityChip(capability));
            }

            if (info.CapabilityOverflow > 0)
            {
                chips.Children.Add(BuildAppChip($"{info.CapabilityOverflow:N0} more", palette.TextTertiary, palette.RaisedBorder));
            }

            footer.Children.Add(chips);
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = footer
        };
        StyleScrollViewer(scroll);

        var visual = BuildAppPackageLogo(info);
        var card = BuildFileCard(visual, info.Title, AppPackageFacts(info), scroll);
        _ = FillAppPackageTrustAsync(info.SourcePath, trust);
        return card;
    }

    private static async Task FillAppPackageTrustAsync(string path, StackPanel row)
    {
        AppPackageSignature signature;
        string? signer;
        try
        {
            (signature, signer) = await Task.Run(() => AppPackagePreviewLoader.SignatureFor(path));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"package signature failed {path}: {ex.GetType().Name}");
            return;
        }

        var palette = Theme.Current;
        var (text, warning) = AppPackagePreviewLoader.DescribeSignature(signature, signer);
        var brush = Theme.Brush(warning ? palette.Warning : palette.TextSecondary);
        row.Children.Add(new TextBlock
        {
            Text = warning ? "\uE7BA" : "\uE73E",
            FontFamily = GlyphFont,
            FontSize = 13,
            Foreground = brush,
            Margin = new Thickness(0, 1, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = Metrics.FontBody,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 440
        });

        if (!Motion.Enabled)
        {
            row.Opacity = 1;
            return;
        }

        row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static FrameworkElement BuildAppPackageLogo(AppPackageInfo info)
    {
        if (info.LogoBytes is { Length: > 0 })
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(info.LogoBytes);
                bitmap.DecodePixelWidth = 256;
                bitmap.EndInit();
                bitmap.Freeze();
                return new Border
                {
                    Width = CardTileSide,
                    Height = CardTileSide,
                    CornerRadius = Metrics.CardCorner,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill }
                };
            }
            catch (Exception ex)
            {
                // Undecodable logo asset: the glyph tile stands in.
                DiagnosticLog.Write($"package logo decode failed: {ex.GetType().Name}");
            }
        }

        return GlyphTile("\uE7B8", Theme.Current.TextTertiary);
    }

    private static FrameworkElement BuildAppCapabilityChip(string capability)
    {
        var palette = Theme.Current;
        var (label, known, warning) = DescribeAppCapability(capability);
        return BuildAppChip(
            label,
            warning ? palette.Warning : known ? palette.TextSecondary : palette.TextTertiary,
            warning ? palette.Warning : palette.RaisedBorder);
    }

    private static Border BuildAppChip(string label, Color foreground, Color border) => new()
    {
        CornerRadius = Metrics.RowCorner,
        Background = Theme.Brush(Theme.Current.RaisedBg),
        BorderBrush = Theme.Brush(border),
        BorderThickness = new Thickness(1),
        Padding = Metrics.ChipPadding,
        Margin = new Thickness(3),
        Child = new TextBlock
        {
            Text = label,
            FontSize = Metrics.FontCaption,
            Foreground = Theme.Brush(foreground)
        }
    };

    /// <summary>A wrap panel whose rows are centered, so a short last row sits under the middle.</summary>
    private sealed class CenteredWrapPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double width = 0, height = 0, rowWidth = 0, rowHeight = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(availableSize);
                var size = child.DesiredSize;
                if (rowWidth > 0 && rowWidth + size.Width > availableSize.Width)
                {
                    width = Math.Max(width, rowWidth);
                    height += rowHeight;
                    rowWidth = rowHeight = 0;
                }

                rowWidth += size.Width;
                rowHeight = Math.Max(rowHeight, size.Height);
            }

            return new Size(Math.Max(width, rowWidth), height + rowHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var row = new List<UIElement>();
            double top = 0, rowWidth = 0, rowHeight = 0;

            void Place()
            {
                var x = (finalSize.Width - rowWidth) / 2;
                foreach (var child in row)
                {
                    child.Arrange(new Rect(x, top, child.DesiredSize.Width, rowHeight));
                    x += child.DesiredSize.Width;
                }

                top += rowHeight;
                row.Clear();
                rowWidth = rowHeight = 0;
            }

            foreach (UIElement child in InternalChildren)
            {
                if (row.Count > 0 && rowWidth + child.DesiredSize.Width > finalSize.Width)
                {
                    Place();
                }

                row.Add(child);
                rowWidth += child.DesiredSize.Width;
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            Place();
            return finalSize;
        }
    }
}
