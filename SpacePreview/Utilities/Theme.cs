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
/// <see cref="Theme.Current"/> directly.
/// </summary>
internal sealed record ThemePalette(
    bool IsDark,
    Color WindowBg,
    Color HeaderBg,
    Color ContentBg,
    Color CanvasBg,
    Color DeepBg,
    Color Border,
    Color SurfaceBg,
    Color SurfaceBorder,
    Color TextPrimary,
    Color TextSecondary,
    Color TextTertiary,
    Color TextFaint,
    Color Accent,
    Color Warning,
    Color ScrollThumb,
    Color HoverBg,
    Color PressedBg,
    Color BadgeBg,
    Color BadgeBorder);

internal static class Theme
{
    private static readonly ConcurrentDictionary<Color, SolidColorBrush> BrushCache = new();

    public static readonly ThemePalette Dark = new(
        IsDark: true,
        WindowBg: Rgb(0x20, 0x21, 0x26),
        HeaderBg: Rgb(0x25, 0x26, 0x2B),
        ContentBg: Rgb(0x18, 0x19, 0x1D),
        CanvasBg: Rgb(0x16, 0x17, 0x1B),
        DeepBg: Rgb(0x12, 0x13, 0x16),
        Border: Rgb(0x34, 0x37, 0x41),
        SurfaceBg: Rgb(0x23, 0x25, 0x2B),
        SurfaceBorder: Rgb(0x37, 0x3A, 0x44),
        TextPrimary: Rgb(0xF4, 0xF4, 0xF5),
        TextSecondary: Rgb(0xA9, 0xAD, 0xB8),
        TextTertiary: Rgb(0x8F, 0x95, 0xA0),
        TextFaint: Rgb(0x7E, 0x84, 0x90),
        Accent: Rgb(0x4E, 0x80, 0xF5),
        Warning: Rgb(0xD9, 0xA6, 0x6A),
        ScrollThumb: Rgb(0x5C, 0x61, 0x6E),
        HoverBg: Rgb(0x39, 0x3B, 0x44),
        PressedBg: Rgb(0x47, 0x4A, 0x55),
        BadgeBg: Rgb(0x26, 0x28, 0x2F),
        BadgeBorder: Rgb(0x4B, 0x4E, 0x58));

    public static readonly ThemePalette Light = new(
        IsDark: false,
        WindowBg: Rgb(0xF2, 0xF2, 0xF5),
        HeaderBg: Rgb(0xFA, 0xFA, 0xFC),
        ContentBg: Rgb(0xFF, 0xFF, 0xFF),
        CanvasBg: Rgb(0xEF, 0xEF, 0xF3),
        DeepBg: Rgb(0xE7, 0xE8, 0xEC),
        Border: Rgb(0xD8, 0xDA, 0xDF),
        SurfaceBg: Rgb(0xF3, 0xF3, 0xF6),
        SurfaceBorder: Rgb(0xE0, 0xE2, 0xE7),
        TextPrimary: Rgb(0x1D, 0x1E, 0x22),
        TextSecondary: Rgb(0x5C, 0x62, 0x70),
        TextTertiary: Rgb(0x87, 0x8D, 0x99),
        TextFaint: Rgb(0x9A, 0xA0, 0xAB),
        Accent: Rgb(0x3B, 0x72, 0xE8),
        Warning: Rgb(0x9A, 0x6B, 0x24),
        ScrollThumb: Rgb(0xB4, 0xB8, 0xC2),
        HoverBg: Rgb(0xE7, 0xE8, 0xED),
        PressedBg: Rgb(0xDC, 0xDE, 0xE5),
        BadgeBg: Rgb(0xF5, 0xF5, 0xF8),
        BadgeBorder: Rgb(0xD9, 0xDB, 0xE2));

    // Canvas HUDs (zoom readout, mesh overlay, ink-capture backdrop) float
    // dark glass over content in both themes, so these live beside the
    // palette rather than on it: they deliberately never change with it.
    public static readonly Color OverlayScrim = Color.FromArgb(0xB8, 0x0F, 0x10, 0x14);
    public static readonly Color OverlayBorder = Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF);
    public static readonly Color OverlayText = Color.FromRgb(0xF1, 0xF4, 0xF8);
    public static readonly Color OverlayTextSecondary = Color.FromRgb(0xB4, 0xBB, 0xC7);
    public static readonly Color OverlayTextTertiary = Color.FromRgb(0x87, 0x8F, 0x9C);

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
        resources["Theme.WindowBg"] = Brush(p.WindowBg);
        resources["Theme.HeaderBg"] = Brush(p.HeaderBg);
        resources["Theme.Border"] = Brush(p.Border);
        resources["Theme.TextPrimary"] = Brush(p.TextPrimary);
        resources["Theme.TextSecondary"] = Brush(p.TextSecondary);
        resources["Theme.TextTertiary"] = Brush(p.TextTertiary);
        resources["Theme.ScrollThumb"] = Brush(p.ScrollThumb);
        resources["Theme.HoverBg"] = Brush(p.HoverBg);
        resources["Theme.PressedBg"] = Brush(p.PressedBg);
        resources["Theme.SurfaceBg"] = Brush(p.SurfaceBg);
        resources["Theme.SurfaceBorder"] = Brush(p.SurfaceBorder);
        resources["Theme.Accent"] = Brush(p.Accent);
        resources["Theme.Warning"] = Brush(p.Warning);
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
