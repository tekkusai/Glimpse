using System.Windows;

namespace SpacePreview.Utilities;

/// <summary>
/// The design language's shared numbers. Chrome built in code and in XAML must
/// agree on these; a magic number in a control is a future inconsistency.
/// Content-specific spacing inside individual previews may stay local, but
/// anything that reads as "the app's chrome" draws from here.
/// </summary>
internal static class Metrics
{
    // Corner radius ladder, one step per surface role.
    public const double RadiusRow = 6;      // hover rows: menu items, breakdown entries, toggle rows
    public const double RadiusIcon = 7;     // square icon buttons in the header
    public const double RadiusMenu = 8;     // flyout surfaces
    public const double RadiusSurface = 10; // embedded surfaces: thumbnails, framed content
    public const double RadiusPill = 14;    // pill controls and floating HUDs
    public const double RadiusCard = 16;    // large framed tiles: state badges, icon tiles, specimens

    public static readonly CornerRadius RowCorner = new(RadiusRow);
    public static readonly CornerRadius IconCorner = new(RadiusIcon);
    public static readonly CornerRadius MenuCorner = new(RadiusMenu);
    public static readonly CornerRadius SurfaceCorner = new(RadiusSurface);
    public static readonly CornerRadius PillCorner = new(RadiusPill);
    public static readonly CornerRadius CardCorner = new(RadiusCard);

    // Halves of a split pill hug the outer radius minus its 1px border.
    public static readonly CornerRadius PillLeftCorner = new(RadiusPill - 1, 0, 0, RadiusPill - 1);
    public static readonly CornerRadius PillRightCorner = new(0, RadiusPill - 1, RadiusPill - 1, 0);

    // Padding.
    public static readonly Thickness PillPadding = new(13, 5, 13, 6);
    public static readonly Thickness ChipPadding = new(10, 4, 10, 5); // small labelled chips: tabs, badges, capability chips
    public static readonly Thickness MenuPadding = new(5);
    public static readonly Thickness MenuItemPadding = new(8, 7, 12, 8);

    // Inner horizontal padding where a pill half meets its divider.
    public const double PillInnerPadding = 11;

    // The gap between an anchor and its flyout.
    public const double FlyoutGap = 6;

    // Type ramp for chrome text. Glyph fonts size to their icon, and content
    // rendering (documents, font specimens) keeps its own scale; everything
    // that reads as the app's own words draws from here.
    public const double FontCaption = 11.5; // detail lines, tile captions, tooltips
    public const double FontLabel = 12.5;   // pill and control labels
    public const double FontBody = 13;      // card body text and info lines
    public const double FontTitle = 20;     // card and sheet titles
    public const double FontDisplay = 34;   // the welcome title
}
