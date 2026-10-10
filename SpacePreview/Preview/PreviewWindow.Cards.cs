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
    // ---------------------------------------------------------------------
    // Context actions: small pills that give the card real controls for the
    // thing being previewed, without changing the default look.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Persistent per-exe settings, drawn as what they are: switches. Launch
    /// actions live in the header's split Open control, not here.
    /// </summary>
    private static FrameworkElement? BuildExecutableToggles(string path)
    {
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new Border
        {
            Height = 1,
            Width = 200,
            Margin = new Thickness(0, 18, 0, 8),
            Background = Theme.Brush(Theme.Current.Border),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(CreateCompatToggleRow(path, "Always run as administrator", ExecutableTweaks.RunAsAdminFlag));
        stack.Children.Add(CreateCompatToggleRow(path, "Disable fullscreen optimizations", ExecutableTweaks.DisableFullscreenOptimizationsFlag));
        return stack;
    }

    private static FrameworkElement CreateCompatToggleRow(string path, string label, string flag)
    {
        // A machine-wide flag needs elevation to change; showing a switch that
        // snaps back is misleading. Show it on and locked instead.
        var locked = ExecutableTweaks.IsMachineEnforced(path, flag);
        return CreateToggleRow(
            label,
            () => ExecutableTweaks.HasFlag(path, flag),
            () => ExecutableTweaks.ToggleFlag(path, flag),
            lockedReason: locked ? "Set for all users in the file's Properties" : null);
    }

    private static FrameworkElement CreateToggleRow(string label, Func<bool> getState, Action toggle, string? lockedReason = null)
    {
        var palette = Theme.Current;

        var knobTransform = new TranslateTransform();
        var knob = new Border
        {
            Width = 11,
            Height = 11,
            CornerRadius = new CornerRadius(5.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 3, 0),
            RenderTransform = knobTransform
        };
        var track = new Border
        {
            Width = 32,
            Height = 17,
            CornerRadius = new CornerRadius(8.5),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = knob
        };

        var row = new Grid { Height = 30 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextSecondary),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(text, 0);
        row.Children.Add(text);
        Grid.SetColumn(track, 1);
        row.Children.Add(track);

        var container = new Border
        {
            Width = 320,
            CornerRadius = Metrics.RowCorner,
            Padding = new Thickness(10, 0, 8, 0),
            Background = Brushes.Transparent,
            Cursor = lockedReason is null ? Cursors.Hand : Cursors.Arrow,
            Child = row
        };

        if (lockedReason is not null)
        {
            container.Opacity = 0.62;
            container.ToolTip = lockedReason;
        }

        void Render(bool animate)
        {
            var on = getState();
            var accent = palette.Accent;
            track.Background = on ? Theme.Brush(accent) : Theme.Brush(palette.RaisedBg);
            track.BorderBrush = on
                ? Theme.Brush(accent)
                : Theme.Brush(palette.ScrollThumb);
            knob.Background = on ? Brushes.White : Theme.Brush(palette.TextTertiary);

            var targetX = on ? 13.0 : 0.0;
            if (animate)
            {
                knobTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            else
            {
                knobTransform.X = targetX;
            }
        }

        if (lockedReason is null)
        {
            container.MouseEnter += (_, _) => container.Background = Theme.Brush(palette.HoverBg);
            container.MouseLeave += (_, _) => container.Background = Brushes.Transparent;
            container.MouseLeftButtonDown += (_, e) => e.Handled = true;
            container.MouseLeftButtonUp += (_, e) =>
            {
                toggle();
                Render(animate: true);
                e.Handled = true;
            };
        }

        Render(animate: false);
        return container;
    }

    private void RunAsAdministrator(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined; nothing to do.
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
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
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

    // ---------------------------------------------------------------------
    // The fallback card for files without a specific preview.
    // Thumbnails scale up; icons never do (an upscaled icon is just blur).
    // ---------------------------------------------------------------------

    private sealed record FileCardData(BitmapSource? Image, bool IsThumbnail, string Title, IReadOnlyList<string> Lines, string BadgeText);

    private static async Task<FrameworkElement> BuildFileCardAsync(string path, CancellationToken token = default)
    {
        var data = await Task.Run(() => CollectFileCardData(path, token), token);
        return BuildFileCard(data);
    }
    private static FileCardData CollectFileCardData(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Directory.Exists(path))
        {
            return CollectDirectoryCardData(path);
        }

        var lines = new List<string>();
        string title;
        var shellImage = ShellThumbnailProvider.TryGetThumbnail(path);
        token.ThrowIfCancellationRequested();

        var file = new FileInfo(path);
        title = file.Name;
        lines.Add($"{FileKinds.FriendlyKind(path)} · {FileKinds.FormatSize(file.Length)}");
        lines.Add($"Modified {file.LastWriteTime:g}");

        if (FileKinds.IsExecutableLike(path))
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription)
                    && !versionInfo.FileDescription.Equals(title, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Insert(0, versionInfo.FileDescription.Trim());
                }

                var version = versionInfo.ProductVersion ?? versionInfo.FileVersion;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    version = version.Trim();
                    lines.Add($"Version {(version.Length > 40 ? version[..40] + "…" : version)}");
                }

                if (!string.IsNullOrWhiteSpace(versionInfo.CompanyName))
                {
                    lines.Add(versionInfo.CompanyName.Trim());
                }
            }
            catch
            {
                // Version info is a bonus, optional.
            }
        }

        if (FileKinds.IsInternetShortcut(path))
        {
            var url = TryReadUrlTarget(path);
            if (url is not null)
            {
                lines.Add(url);
            }
        }

        var badge = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (badge.Length is 0 or > 5)
        {
            badge = "FILE";
        }

        return new FileCardData(shellImage?.Bitmap, shellImage?.IsThumbnail ?? false, title, lines, badge);
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
        catch
        {
        }

        return null;
    }

    private static FrameworkElement BuildFileCardVisual(
        FileCardData data,
        double thumbnailMaxWidth = 560,
        double thumbnailMaxHeight = 400,
        double iconMinSide = 96,
        double iconMaxSide = 128)
    {
        var palette = Theme.Current;

        if (data.Image is null)
        {
            return new Border
            {
                Width = 96,
                Height = 120,
                CornerRadius = Metrics.CardCorner,
                BorderThickness = new Thickness(1),
                BorderBrush = Theme.Brush(palette.RaisedBorder),
                Background = Theme.Brush(palette.RaisedBg),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = data.BadgeText,
                    Foreground = Theme.Brush(palette.TextSecondary),
                    FontSize = data.BadgeText.Length > 4 ? 18 : 24,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
        }

        if (data.IsThumbnail)
        {
            var width = Math.Min(thumbnailMaxWidth, data.Image.Width);
            var height = Math.Min(thumbnailMaxHeight, data.Image.Height);
            var scale = Math.Min(width / data.Image.Width, height / data.Image.Height);
            var rectangle = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, data.Image.Width * scale),
                Height = Math.Max(1, data.Image.Height * scale),
                RadiusX = Metrics.RadiusSurface,
                RadiusY = Metrics.RadiusSurface,
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = new ImageBrush(data.Image) { Stretch = Stretch.Uniform }
            };
            return new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 22,
                    ShadowDepth = 5,
                    Direction = 270,
                    Color = Colors.Black,
                    Opacity = palette.IsDark ? 0.35 : 0.18
                },
                Child = rectangle
            };
        }

        var side = Math.Clamp(Math.Max(data.Image.Width, data.Image.Height), iconMinSide, iconMaxSide);
        var icon = new Image
        {
            Source = data.Image,
            Width = side,
            Height = side,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        return icon;
    }

    private static FrameworkElement BuildFileCard(
        FileCardData data,
        TextBlock? liveLine = null,
        FrameworkElement? footer = null,
        Action<ContentControl>? visualHostReady = null)
    {
        var palette = Theme.Current;
        var card = new Grid
        {
            Width = FileCardWidth,
            Margin = new Thickness(40, 24, 40, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FileCardVisualSlotHeight) });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FileCardTitleSlotHeight) });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FileCardInfoSlotHeight) });
        card.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(footer is null ? 0 : FileCardFooterSlotHeight)
        });

        var visualHost = new ContentControl
        {
            Width = FileCardVisualSlotWidth,
            Height = FileCardVisualSlotHeight,
            Content = BuildFileCardVisual(data, FileCardVisualSlotWidth, FileCardVisualSlotHeight, FileCardIconSide, FileCardIconSide),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        visualHostReady?.Invoke(visualHost);
        Grid.SetRow(visualHost, 0);
        card.Children.Add(visualHost);

        var title = new TextBlock
        {
            Text = data.Title,
            Margin = new Thickness(0, 24, 0, 0),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(title, 1);
        card.Children.Add(title);

        var infoStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top
        };

        for (var i = 0; i < data.Lines.Count; i++)
        {
            infoStack.Children.Add(new TextBlock
            {
                Text = data.Lines[i],
                Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0),
                FontSize = 13,
                Foreground = Theme.Brush(i == 0 ? palette.TextSecondary : palette.TextTertiary),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        if (liveLine is not null)
        {
            infoStack.Children.Add(liveLine);
        }
        Grid.SetRow(infoStack, 2);
        card.Children.Add(infoStack);

        if (footer is not null)
        {
            var footerHost = new ContentControl
            {
                Height = FileCardFooterSlotHeight,
                Content = footer,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(footerHost, 3);
            card.Children.Add(footerHost);
        }

        var surface = (ScrollViewer)BuildScrollSurface(card, new Thickness(0), horizontal: false, palette.CanvasBg);
        // A ScrollViewer aligns content to the top by default, which makes a
        // short generic card look visibly high even though the card itself
        // requests centered alignment. Center while there is spare room;
        // oversized rich cards continue to scroll normally.
        surface.VerticalContentAlignment = VerticalAlignment.Center;
        StyleScrollViewer(surface);
        return surface;
    }

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

    /// <summary>
    /// The package card is a trust moment: identity, who signed it, and what
    /// it can reach once installed. Native like the other file cards so it
    /// stays in that visual family.
    /// </summary>
    private static FrameworkElement BuildAppPackageCard(AppPackageInfo info)
    {
        var palette = Theme.Current;
        var warningBrush = Theme.Brush(palette.Warning);

        var stack = new StackPanel
        {
            Width = FileCardWidth,
            Margin = new Thickness(40, 44, 40, 44),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(BuildAppPackageIcon(info, palette));

        stack.Children.Add(new TextBlock
        {
            Text = info.Title,
            Margin = new Thickness(0, 22, 0, 0),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (!string.IsNullOrWhiteSpace(info.Publisher))
        {
            stack.Children.Add(new TextBlock
            {
                Text = info.Publisher,
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 13,
                Foreground = Theme.Brush(palette.TextSecondary),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Version))
        {
            meta.Add($"Version {info.Version}");
        }
        if (!string.IsNullOrWhiteSpace(info.Architecture))
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
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", meta),
                Margin = new Thickness(0, 4, 0, 0),
                FontSize = 13,
                Foreground = Theme.Brush(palette.TextTertiary),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        if (!string.IsNullOrWhiteSpace(info.Description))
        {
            stack.Children.Add(new TextBlock
            {
                Text = info.Description,
                Margin = new Thickness(60, 14, 60, 0),
                FontSize = 13,
                Foreground = Theme.Brush(palette.TextTertiary),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 60,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        stack.Children.Add(BuildAppPackageSignatureLine(info, palette, warningBrush));

        if (info.Capabilities.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Capabilities",
                Margin = new Thickness(0, 26, 0, 0),
                FontSize = 12,
                Foreground = Theme.Brush(palette.TextTertiary),
                TextAlignment = TextAlignment.Center
            });

            var chips = new WrapPanel
            {
                MaxWidth = 480,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            foreach (var capability in info.Capabilities)
            {
                chips.Children.Add(BuildAppCapabilityChip(capability, palette, warningBrush));
            }
            if (info.CapabilityOverflow > 0)
            {
                chips.Children.Add(BuildAppChip($"{info.CapabilityOverflow:N0} more", palette,
                    Theme.Brush(palette.TextTertiary), Theme.Brush(palette.RaisedBorder)));
            }
            stack.Children.Add(chips);
        }

        var surface = (ScrollViewer)BuildScrollSurface(stack, new Thickness(0), horizontal: false, palette.CanvasBg);
        StyleScrollViewer(surface);
        return surface;
    }

    private static FrameworkElement BuildAppPackageIcon(AppPackageInfo info, ThemePalette palette)
    {
        var tile = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = Metrics.CardCorner,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1)
        };

        if (info.LogoBytes is { Length: > 0 })
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(info.LogoBytes);
                bitmap.DecodePixelWidth = 192;
                bitmap.EndInit();
                bitmap.Freeze();
                tile.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                tile.BorderThickness = new Thickness(0);
                return tile;
            }
            catch
            {
                // Undecodable logo asset: the glyph tile below stands in.
            }
        }

        tile.Child = new TextBlock
        {
            Text = "\uE7B8",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 40,
            Foreground = Theme.Brush(palette.TextTertiary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        return tile;
    }

    private static FrameworkElement BuildAppPackageSignatureLine(AppPackageInfo info, ThemePalette palette, Brush warningBrush)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        string text;
        Brush brush;
        string? glyph;
        switch (info.Signature)
        {
            case AppPackageSignature.SignedTrusted:
                text = string.IsNullOrWhiteSpace(info.SignerName) ? "Signed" : $"Signed by {info.SignerName}";
                brush = Theme.Brush(palette.TextSecondary);
                glyph = "\uE73E";
                break;
            case AppPackageSignature.Invalid:
                text = "Signature not valid";
                brush = warningBrush;
                glyph = "\uE7BA";
                break;
            default:
                text = "Not signed";
                brush = warningBrush;
                glyph = null;
                break;
        }

        if (glyph is not null)
        {
            row.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = brush,
                Margin = new Thickness(0, 1, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420
        });
        return row;
    }

    private static FrameworkElement BuildAppCapabilityChip(string capability, ThemePalette palette, Brush warningBrush)
    {
        var (label, known, warning) = DescribeAppCapability(capability);
        var foreground = warning
            ? warningBrush
            : Theme.Brush(known ? palette.TextSecondary : palette.TextTertiary);
        var border = warning
            ? warningBrush
            : Theme.Brush(palette.RaisedBorder);
        return BuildAppChip(label, palette, foreground, border);
    }

    private static FrameworkElement BuildAppChip(string label, ThemePalette palette, Brush foreground, Brush border)
    {
        return new Border
        {
            CornerRadius = Metrics.RowCorner,
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = Metrics.ChipPadding,
            Margin = new Thickness(3),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = foreground
            }
        };
    }

    private static FrameworkElement BuildCadPreview(CadPreview preview, string accentHex)
    {
        var palette = Theme.Current;
        var accent = (Color)ColorConverter.ConvertFromString(accentHex);
        var header = new StackPanel
        {
            Margin = new Thickness(32, 26, 32, 0),
            Children =
            {
                new TextBlock
                {
                    Text = preview.Title,
                    FontSize = 22,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.Brush(palette.TextPrimary)
                },
                new TextBlock
                {
                    Text = preview.Subtitle,
                    Margin = new Thickness(0, 6, 0, 0),
                    FontSize = 13,
                    Foreground = Theme.Brush(palette.TextSecondary),
                    TextWrapping = TextWrapping.Wrap
                },
                new Border
                {
                    Width = 42,
                    Height = 3,
                    Margin = new Thickness(0, 14, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(accent)
                }
            }
        };

        var details = BuildSelectableTextBox(
            preview.Details,
            new FontFamily("Cascadia Mono, Consolas"),
            fontSize: 13,
            wrap: true,
            new Thickness(32, 22, 32, 32),
            palette.CanvasBg);

        var grid = new Grid
        {
            Background = Theme.Brush(palette.CanvasBg)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(details, 1);
        grid.Children.Add(header);
        grid.Children.Add(details);
        return grid;
    }

    private static FrameworkElement BuildLabeledTextPreview(
        string title,
        Color accent,
        string text,
        FontFamily fontFamily,
        double fontSize,
        bool wrap)
    {
        var palette = Theme.Current;
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

        var body = BuildSelectableTextBox(
            text,
            fontFamily,
            fontSize,
            wrap,
            new Thickness(24, 16, 24, 24),
            palette.CanvasBg);

        var grid = new Grid
        {
            Background = Theme.Brush(palette.CanvasBg)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(label, 0);
        Grid.SetRow(body, 1);
        grid.Children.Add(label);
        grid.Children.Add(body);
        return grid;
    }

    private static string BuildIllustratorSummary(string path)
    {
        var file = new FileInfo(path);
        var builder = new StringBuilder();
        builder.AppendLine("Adobe Illustrator artwork");
        builder.AppendLine($"Size: {FileKinds.FormatSize(file.Length)}");
        builder.AppendLine($"Modified: {file.LastWriteTime:g}");

        if (FileKinds.TryFindPdfHeaderOffset(path, out var pdfOffset))
        {
            builder.AppendLine(pdfOffset <= 1024
                ? "PDF-compatible stream: present"
                : $"PDF-compatible stream: found at byte {pdfOffset:N0}, outside the renderer's normal header range");
        }
        else
        {
            builder.AppendLine("PDF-compatible stream: not found");
        }

        var metadata = ReadIllustratorMetadata(path);
        if (metadata.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Metadata:");
            foreach (var (key, value) in metadata)
            {
                builder.AppendLine($"{key}: {value}");
            }
        }

        if (!FileKinds.TryFindPdfHeaderOffset(path, out _))
        {
            builder.AppendLine();
            builder.AppendLine($"This Illustrator file is legacy PostScript artwork, so {Branding.Name} shows its details instead.");
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<(string Key, string Value)> ReadIllustratorMetadata(string path)
    {
        try
        {
            var buffer = new byte[256 * 1024];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(buffer, 0, buffer.Length);
            var sample = Encoding.UTF8.GetString(buffer, 0, read).Replace("\0", string.Empty);
            var values = new List<(string Key, string Value)>();
            foreach (var rawLine in sample.Split(["\r\n", "\n"], StringSplitOptions.None).Take(2000))
            {
                var line = rawLine.Trim();
                AddPostScriptMetadata(values, line, "%%Title:", "Title");
                AddPostScriptMetadata(values, line, "%%Creator:", "Creator");
                AddPostScriptMetadata(values, line, "%%CreationDate:", "Created");
                AddPostScriptMetadata(values, line, "%%BoundingBox:", "Bounding box");
                AddPostScriptMetadata(values, line, "%%DocumentProcessColors:", "Process colors");
                AddPostScriptMetadata(values, line, "%%AI8_CreatorVersion:", "Illustrator version");
                if (values.Count >= 8)
                {
                    break;
                }
            }

            return values;
        }
        catch
        {
            return [];
        }
    }

    private static void AddPostScriptMetadata(List<(string Key, string Value)> values, string line, string prefix, string key)
    {
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var value = line[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(value) || values.Any(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        values.Add((key, value));
    }

    private sealed record PreviewAction(string Label, string Detail, string Uri, string Tooltip);

    private static FrameworkElement BuildCenteredBadge(
        string badgeText,
        string title,
        string subtitle,
        string path,
        Color accent,
        PreviewAction? action = null)
    {
        var palette = Theme.Current;

        var badge = new Border
        {
            Width = 116,
            Height = 144,
            CornerRadius = Metrics.CardCorner,
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            Background = Theme.Brush(palette.RaisedBg),
            Child = new TextBlock
            {
                Text = badgeText,
                Foreground = new SolidColorBrush(accent),
                FontSize = badgeText.Length > 4 ? 22 : 28,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };

        var stack = new StackPanel
        {
            Width = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(badge);
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(0, 22, 0, 0),
            FontSize = Metrics.FontTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 13,
            Foreground = Theme.Brush(palette.TextSecondary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (action is not null)
        {
            stack.Children.Add(BuildPreviewAction(action));
        }

        stack.Children.Add(new TextBlock
        {
            Text = path,
            Margin = new Thickness(0, action is null ? 20 : 16, 0, 0),
            FontSize = 12,
            Foreground = Theme.Brush(palette.TextTertiary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        return new Grid
        {
            Background = Theme.Brush(palette.CanvasBg),
            Children = { stack }
        };
    }

    private static Border BuildPreviewAction(PreviewAction action)
    {
        var palette = Theme.Current;
        var label = new TextBlock
        {
            Text = action.Label,
            FontSize = Metrics.FontBody,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary)
        };
        var detail = new TextBlock
        {
            Text = action.Detail,
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = Metrics.FontCaption,
            Foreground = Theme.Brush(palette.TextSecondary)
        };
        var arrow = new TextBlock
        {
            Text = "\uE76C",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextSecondary)
        };
        var pill = new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(16, 10, 15, 11),
            CornerRadius = Metrics.PillCorner,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = action.Tooltip,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new StackPanel { Children = { label, detail } },
                    arrow
                }
            }
        };
        pill.MouseEnter += (_, _) =>
        {
            pill.Background = Theme.Brush(palette.HoverBg);
            detail.Foreground = Theme.Brush(palette.TextPrimary);
            arrow.Foreground = Theme.Brush(palette.TextPrimary);
        };
        pill.MouseLeave += (_, _) =>
        {
            pill.Background = Theme.Brush(palette.RaisedBg);
            detail.Foreground = Theme.Brush(palette.TextSecondary);
            arrow.Foreground = Theme.Brush(palette.TextSecondary);
        };
        pill.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pill.MouseLeftButtonUp += (_, e) =>
        {
            OpenExternalUri(action.Uri);
            e.Handled = true;
        };
        return pill;
    }

    private static FrameworkElement BuildErrorPreview(string title, string detail, PreviewAction? action = null)
    {
        return BuildCenteredBadge("!", title, detail, string.Empty, Theme.Current.Warning, action);
    }
}
