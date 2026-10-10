using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpacePreview.Utilities;

/// <summary>How <see cref="Theme.Refresh"/> resolves the palette.</summary>
internal enum ThemePreference
{
    FollowSystem,
    Light,
    Dark
}

/// <summary>
/// Two fixed palettes that track the Windows app theme, resolved once per preview open.
/// XAML consumes them through "Theme.*" dynamic resources; code-built content reads
/// <see cref="Theme.Current"/> directly. docs/DESIGN.md names each token's job.
/// </summary>
internal sealed record ThemePalette(
    bool IsDark,
    Color ChromeBg,
    Color CanvasBg,
    Color RaisedBg,
    Color RaisedBorder,
    Color Border,
    Color TextPrimary,
    Color TextSecondary,
    Color TextTertiary,
    Color Accent,
    Color Warning,
    Color ScrollThumb,
    Color HoverBg,
    Color PressedBg);

internal static class Theme
{
    private static readonly ConcurrentDictionary<Color, SolidColorBrush> BrushCache = new();

    // Neutral grays, one hue. The canvas is the calmest surface in each
    // theme: deepest in dark, brightest in light.
    public static readonly ThemePalette Dark = new(
        IsDark: true,
        ChromeBg: Rgb(0x20, 0x20, 0x20),
        CanvasBg: Rgb(0x19, 0x19, 0x19),
        RaisedBg: Rgb(0x2C, 0x2C, 0x2C),
        RaisedBorder: Rgb(0x3A, 0x3A, 0x3A),
        Border: Rgb(0x2E, 0x2E, 0x2E),
        TextPrimary: Rgb(0xF2, 0xF2, 0xF2),
        TextSecondary: Rgb(0xB4, 0xB4, 0xB4),
        TextTertiary: Rgb(0x8C, 0x8C, 0x8C),
        Accent: Rgb(0x4E, 0x80, 0xF5),
        Warning: Rgb(0xD9, 0xA6, 0x6A),
        ScrollThumb: Rgb(0x5A, 0x5A, 0x5A),
        HoverBg: Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF),
        PressedBg: Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));

    public static readonly ThemePalette Light = new(
        IsDark: false,
        ChromeBg: Rgb(0xF3, 0xF3, 0xF3),
        CanvasBg: Rgb(0xFA, 0xFA, 0xFA),
        RaisedBg: Rgb(0xFF, 0xFF, 0xFF),
        RaisedBorder: Rgb(0xDC, 0xDC, 0xDC),
        Border: Rgb(0xE3, 0xE3, 0xE3),
        TextPrimary: Rgb(0x1A, 0x1A, 0x1A),
        TextSecondary: Rgb(0x5C, 0x5C, 0x5C),
        TextTertiary: Rgb(0x70, 0x70, 0x70),
        Accent: Rgb(0x3B, 0x72, 0xE8),
        Warning: Rgb(0x9A, 0x6B, 0x24),
        ScrollThumb: Rgb(0xB8, 0xB8, 0xB8),
        HoverBg: Color.FromArgb(0x0A, 0x00, 0x00, 0x00),
        PressedBg: Color.FromArgb(0x10, 0x00, 0x00, 0x00));

    // Canvas HUDs (zoom readout, mesh overlay, ink-capture backdrop) float
    // dark glass over content in both themes, so these live beside the
    // palette rather than on it: they deliberately never change with it.
    public static readonly Color OverlayScrim = Color.FromArgb(0xB8, 0x12, 0x12, 0x12);
    public static readonly Color OverlayBorder = Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF);
    public static readonly Color OverlayText = Color.FromRgb(0xF2, 0xF2, 0xF2);
    public static readonly Color OverlayTextSecondary = Color.FromRgb(0xB8, 0xB8, 0xB8);
    public static readonly Color OverlayTextTertiary = Color.FromRgb(0x8C, 0x8C, 0x8C);

    public static ThemePalette Current { get; private set; } = Dark;

    /// <summary>The user's tray preference; a forced value skips the registry read.</summary>
    public static ThemePreference Preference { get; set; } = ThemePreference.FollowSystem;

    /// <summary>Resolves the palette against <see cref="Preference"/>. Returns true when it changed.</summary>
    public static bool Refresh()
    {
        var prefersLight = Preference switch
        {
            ThemePreference.Light => true,
            ThemePreference.Dark => false,
            _ => ReadSystemPrefersLight()
        };

        var palette = prefersLight ? Light : Dark;
        if (ReferenceEquals(palette, Current))
        {
            return false;
        }

        Current = palette;
        return true;
    }

    public static void ApplyResources(FrameworkElement target) => ApplyResources(target.Resources);

    public static void ApplyResources(ResourceDictionary resources)
    {
        var p = Current;
        resources["Theme.ChromeBg"] = Brush(p.ChromeBg);
        resources["Theme.CanvasBg"] = Brush(p.CanvasBg);
        resources["Theme.RaisedBg"] = Brush(p.RaisedBg);
        resources["Theme.RaisedBorder"] = Brush(p.RaisedBorder);
        resources["Theme.Border"] = Brush(p.Border);
        resources["Theme.TextPrimary"] = Brush(p.TextPrimary);
        resources["Theme.TextSecondary"] = Brush(p.TextSecondary);
        resources["Theme.TextTertiary"] = Brush(p.TextTertiary);
        resources["Theme.Accent"] = Brush(p.Accent);
        resources["Theme.Warning"] = Brush(p.Warning);
        resources["Theme.ScrollThumb"] = Brush(p.ScrollThumb);
        resources["Theme.HoverBg"] = Brush(p.HoverBg);
        resources["Theme.PressedBg"] = Brush(p.PressedBg);
    }

    public static SolidColorBrush Brush(Color color)
    {
        return BrushCache.GetOrAdd(color, static c =>
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        });
    }

    private static bool ReadSystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
