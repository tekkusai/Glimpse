using System.Windows;
using System.Windows.Controls;

namespace SpacePreview.Preview;

/// <summary>
/// The one card geometry (docs/DESIGN.md, Cards). The visual's slot is
/// centered at 38% of the canvas height and the title sits a fixed step
/// under that slot, so neither moves between kinds. Facts follow the title;
/// the footer takes what is left. Only a footer that would not fit (a
/// list states its floor as MinHeight) lifts the card, never past the top
/// margin.
/// </summary>
internal sealed class FileCardLayout : Panel
{
    internal const double Anchor = 0.38;
    internal const double MaxSlot = 200;
    internal const double MinSlot = 112;
    internal const double SlotShare = 0.30;
    internal const double ColumnWidth = 560;
    private const double SideMargin = 40;
    private const double TopMargin = 24;
    private const double BottomMargin = 24;
    private const double SlotToBody = 16;
    private const double BodyToFooter = 20;

    private readonly UIElement _visual;
    private readonly UIElement _body;
    private readonly FrameworkElement? _footer;
    private double _footerNeed;

    public FileCardLayout(UIElement visual, UIElement body, FrameworkElement? footer)
    {
        _visual = visual;
        _body = body;
        _footer = footer;
        Children.Add(visual);
        Children.Add(body);
        if (footer is not null)
        {
            Children.Add(footer);
        }
    }

    /// <summary>The visual slot's height for a canvas this tall.</summary>
    internal static double SlotHeight(double canvasHeight) =>
        Math.Clamp(Math.Round(canvasHeight * SlotShare), MinSlot, MaxSlot);

    /// <summary>
    /// The visual slot's top edge. A footer that needs more room than the
    /// anchor leaves lifts the card by the shortfall, down to the top margin.
    /// </summary>
    internal static double SlotTop(double canvasHeight, double bodyHeight, double footerNeed)
    {
        var slot = SlotHeight(canvasHeight);
        var top = (canvasHeight * Anchor) - (slot / 2);
        if (footerNeed > 0)
        {
            var footerTop = top + slot + SlotToBody + bodyHeight + BodyToFooter;
            var shortfall = footerTop + footerNeed - (canvasHeight - BottomMargin);
            if (shortfall > 0)
            {
                top -= Math.Min(shortfall, Math.Max(0, top - TopMargin));
            }
        }

        return Math.Round(top);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;
        var width = double.IsInfinity(availableSize.Width) ? ColumnWidth + (2 * SideMargin) : availableSize.Width;
        var column = Column(width);
        var slot = SlotHeight(height);

        _visual.Measure(new Size(column, slot));
        _body.Measure(new Size(column, double.PositiveInfinity));
        _footerNeed = FooterNeed(column);
        if (_footer is not null)
        {
            var top = SlotTop(height, _body.DesiredSize.Height, _footerNeed);
            var footerTop = top + slot + SlotToBody + _body.DesiredSize.Height + BodyToFooter;
            _footer.Measure(new Size(column, Math.Max(0, height - BottomMargin - footerTop)));
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var column = Column(finalSize.Width);
        var left = (finalSize.Width - column) / 2;
        var slot = SlotHeight(finalSize.Height);
        var bodyHeight = _body.DesiredSize.Height;
        var top = SlotTop(finalSize.Height, bodyHeight, _footerNeed);

        _visual.Arrange(new Rect(left, top, column, slot));
        var bodyTop = top + slot + SlotToBody;
        _body.Arrange(new Rect(left, bodyTop, column, bodyHeight));
        if (_footer is not null)
        {
            var footerTop = bodyTop + bodyHeight + BodyToFooter;
            var room = Math.Max(0, finalSize.Height - BottomMargin - footerTop);
            _footer.Arrange(new Rect(left, footerTop, column, Math.Min(room, Math.Max(_footer.DesiredSize.Height, _footer.MinHeight))));
        }

        return finalSize;
    }

    private double FooterNeed(double column)
    {
        if (_footer is null)
        {
            return 0;
        }

        // A filling footer (the folder list) states its floor as MinHeight;
        // a small fixed one (an action pill) is just its own height.
        if (_footer.MinHeight > 0)
        {
            return _footer.MinHeight;
        }

        _footer.Measure(new Size(column, double.PositiveInfinity));
        return _footer.DesiredSize.Height;
    }

    private static double Column(double width) =>
        Math.Max(0, Math.Min(ColumnWidth, width - (2 * SideMargin)));
}
