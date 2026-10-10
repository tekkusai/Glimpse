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
    // Corner radius ladder (docs/DESIGN.md, Shape). Pills are capsules: half
    // their 30 px height.
    public const double RadiusRow = 6;      // icon buttons, menu items, list rows
    public const double RadiusIcon = 6;     // square icon buttons in the header
    public const double RadiusMenu = 8;     // menus and flyouts
    public const double RadiusSurface = 8;  // thumbnails, HUDs, framed content
    public const double RadiusPill = 15;    // pills: 30 px capsules
    public const double RadiusCard = 12;    // card tiles, specimens, raised panels

    public static readonly CornerRadius RowCorner = new(RadiusRow);
    public static readonly CornerRadius IconCorner = new(RadiusIcon);
    public static readonly CornerRadius MenuCorner = new(RadiusMenu);
    public static readonly CornerRadius SurfaceCorner = new(RadiusSurface);
    public static readonly CornerRadius PillCorner = new(RadiusPill);
    public static readonly CornerRadius CardCorner = new(RadiusCard);

    // A pill's hover tint sits inside its 1 px border; a segment of a split
    // pill tints an inset capsule, so one template serves every position.
    public static readonly CornerRadius PillInnerCorner = new(RadiusPill - 1);
    public static readonly CornerRadius SegmentCorner = new(RadiusPill - 3);

    // Padding.
    public static readonly Thickness ChipPadding = new(10, 4, 10, 5); // small labelled chips: tabs, badges, capability chips
    public static readonly Thickness MenuPadding = new(5);
    public static readonly Thickness MenuItemPadding = new(8, 7, 12, 8);

    // The gap between an anchor and its flyout.
    public const double FlyoutGap = 6;

    // Type ramp (docs/DESIGN.md, Type). Glyph fonts size to their icon, and
    // content rendering (documents, font specimens) keeps its own scale;
    // everything that reads as the app's own words draws from here.
    public const double FontCaption = 11.5; // header details, captions, tooltips
    public const double FontLabel = 12.5;   // pill and control labels
    public const double FontBody = 13;      // card body text, info lines, menus
    public const double FontName = 14;      // the header file name
    public const double FontTitle = 20;     // card titles
    public const double FontDisplay = 34;   // the welcome title
}
