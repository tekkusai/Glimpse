using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// One row of a listing. <see cref="Share"/> (0 to 1) fills the row's size
/// meter; <see cref="Children"/> makes an archive folder expandable and is
/// only called when the user opens it.
/// </summary>
internal sealed record EntryNode(
    string Name,
    bool IsDirectory,
    string Detail,
    double Share = 0,
    bool IsLink = false,
    Func<IReadOnlyList<EntryNode>>? Children = null);

/// <summary>
/// The list a folder breakdown and an archive tree share: one row look,
/// one hover and press, row caps with an "…and N more" line, and lazy
/// expansion. Rows refresh in place, so a scan filling in sizes never
/// rebuilds the element under the pointer.
/// </summary>
internal sealed class EntryList
{
    private const double RowHeight = 28;
    private const double Indent = 20;
    private const double ChevronColumn = 16;
    private const double MeterWidth = 56;
    private const double MeterDetailWidth = 112;
    private static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly int _rootCap;
    private readonly int _childCap;
    private readonly bool _meters;
    private readonly Action<EntryNode, FrameworkElement>? _activate;
    private readonly Action<EntryNode, FrameworkElement>? _enter;
    private readonly Action<EntryNode, FrameworkElement>? _leave;
    private readonly List<Row> _rows = [];

    /// <param name="childCap">Rows per expanded folder; 0 makes a flat list.</param>
    /// <param name="meters">Draw a size meter beside each row's detail.</param>
    public EntryList(
        int rootCap,
        int childCap = 0,
        bool meters = false,
        Action<EntryNode, FrameworkElement>? activate = null,
        Action<EntryNode, FrameworkElement>? enter = null,
        Action<EntryNode, FrameworkElement>? leave = null)
    {
        _rootCap = rootCap;
        _childCap = childCap;
        _meters = meters;
        _activate = activate;
        _enter = enter;
        _leave = leave;
    }

    public StackPanel Element { get; } = new();

    private bool Expandable => _childCap > 0;

    /// <summary>Replaces every row. <paramref name="moreAtLeast"/> marks an overflow count that is only a floor.</summary>
    public void Show(IReadOnlyList<EntryNode> roots, bool moreAtLeast = false)
    {
        Element.Children.Clear();
        _rows.Clear();
        var shown = Math.Min(roots.Count, _rootCap);
        for (var i = 0; i < shown; i++)
        {
            _rows.Add(AddRow(Element, roots[i], depth: 0));
        }

        if (roots.Count > shown || moreAtLeast)
        {
            Element.Children.Add(MoreLine(roots.Count - shown, moreAtLeast, depth: 0));
        }

        // A tarball usually holds one folder; opening it saves the first click.
        if (_rows.Count == 1)
        {
            _rows[0].Toggle?.Invoke();
        }
    }

    /// <summary>Refreshes a top-level row's detail and meter without replacing it.</summary>
    public void Update(int index, string detail, double share)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        var row = _rows[index];
        row.Detail.Text = detail;
        if (row.Meter is { } meter)
        {
            SetMeter(meter, share);
        }
    }

    private Row AddRow(Panel host, EntryNode node, int depth)
    {
        var palette = Theme.Current;
        var grid = new Grid { Height = RowHeight, Margin = new Thickness(8 + (Math.Min(depth, 12) * Indent), 0, 8, 0) };
        if (Expandable)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ChevronColumn) });
        }

        var column = grid.ColumnDefinitions.Count;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = _meters ? new GridLength(MeterDetailWidth) : GridLength.Auto });

        var children = node.IsDirectory ? node.Children : null;
        TextBlock? chevron = null;
        if (children is not null)
        {
            chevron = new TextBlock
            {
                Text = "\uE76C",
                FontFamily = GlyphFont,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(palette.TextTertiary),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform()
            };
            grid.Children.Add(chevron);
        }

        var glyph = new TextBlock
        {
            Text = node.IsLink ? "\uE71B" : node.IsDirectory ? "\uE8B7" : "\uE7C3",
            FontFamily = GlyphFont,
            FontSize = 14,
            Width = 16,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(node.IsDirectory && !node.IsLink ? palette.Accent : palette.TextTertiary)
        };
        Grid.SetColumn(glyph, column);
        grid.Children.Add(glyph);

        var name = new TextBlock
        {
            Text = node.Name,
            FontSize = Metrics.FontBody,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextPrimary),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, column + 1);
        grid.Children.Add(name);

        Border? meter = null;
        if (_meters)
        {
            meter = new Border
            {
                Width = MeterWidth,
                Height = 4,
                Margin = new Thickness(16, 0, 0, 0),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Theme.Brush(palette.RaisedBorder),
                Child = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    CornerRadius = new CornerRadius(2),
                    Background = Theme.Brush(palette.Accent)
                }
            };
            SetMeter(meter, node.Share);
            Grid.SetColumn(meter, column + 2);
            grid.Children.Add(meter);
        }

        var detail = new TextBlock
        {
            Text = node.Detail,
            FontSize = Metrics.FontCaption,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Foreground = Theme.Brush(palette.TextTertiary)
        };
        Grid.SetColumn(detail, column + 3);
        grid.Children.Add(detail);

        var rowHost = new Border
        {
            CornerRadius = Metrics.RowCorner,
            Background = Brushes.Transparent,
            Child = grid
        };
        host.Children.Add(rowHost);

        Action? toggle = null;
        if (children is not null)
        {
            toggle = WireExpansion(host, rowHost, chevron!, children, depth);
        }
        else if (_activate is not null)
        {
            rowHost.Cursor = Cursors.Hand;
            WireClick(rowHost, () => _activate(node, rowHost));
        }

        if (_enter is not null)
        {
            rowHost.MouseEnter += (_, _) => _enter(node, rowHost);
        }

        if (_leave is not null)
        {
            rowHost.MouseLeave += (_, _) => _leave(node, rowHost);
        }

        return new Row(detail, meter, toggle);
    }

    private Action WireExpansion(Panel host, Border rowHost, TextBlock chevron, Func<IReadOnlyList<EntryNode>> children, int depth)
    {
        var childHost = new StackPanel { Visibility = Visibility.Collapsed };
        host.Children.Add(childHost);
        var built = false;
        var expanded = false;

        void Toggle()
        {
            expanded = !expanded;
            if (expanded && !built)
            {
                built = true;
                var nodes = children();
                var shown = Math.Min(nodes.Count, _childCap);
                for (var i = 0; i < shown; i++)
                {
                    AddRow(childHost, nodes[i], depth + 1);
                }

                if (nodes.Count > shown)
                {
                    childHost.Children.Add(MoreLine(nodes.Count - shown, atLeast: false, depth + 1));
                }
            }

            childHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            var rotate = (RotateTransform)chevron.RenderTransform;
            if (!Motion.Enabled || !rowHost.IsLoaded)
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                rotate.Angle = expanded ? 90 : 0;
                return;
            }

            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(expanded ? 90 : 0, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        rowHost.Cursor = Cursors.Hand;
        WireClick(rowHost, Toggle);
        return Toggle;
    }

    private TextBlock MoreLine(int count, bool atLeast, int depth)
    {
        // Aligned with the row names: margin, indent, chevron, glyph.
        var lead = 8 + (Math.Min(depth, 12) * Indent) + (Expandable ? ChevronColumn : 0) + 24;
        return new TextBlock
        {
            Text = !atLeast ? $"…and {count:N0} more"
                : count > 0 ? $"…and at least {count:N0} more"
                : "…and more",
            FontSize = Metrics.FontCaption,
            Margin = new Thickness(lead, 6, 0, 8),
            Foreground = Theme.Brush(Theme.Current.TextTertiary)
        };
    }

    /// <summary>
    /// Hover, press, and an action on release. A press that started on
    /// another element never activates this one.
    /// </summary>
    internal static void WireClick(Border element, Action onClick)
    {
        var palette = Theme.Current;
        var pressed = false;
        element.MouseEnter += (_, _) => element.Background = Theme.Brush(palette.HoverBg);
        element.MouseLeave += (_, _) =>
        {
            pressed = false;
            element.Background = Brushes.Transparent;
        };
        element.MouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            element.Background = Theme.Brush(palette.PressedBg);
            e.Handled = true;
        };
        element.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed)
            {
                return;
            }

            pressed = false;
            element.Background = Theme.Brush(palette.HoverBg);
            e.Handled = true;
            onClick();
        };
    }

    /// <summary>An unsized row shows no meter at all, not an empty one.</summary>
    private static void SetMeter(Border meter, double share)
    {
        meter.Visibility = share > 0 ? Visibility.Visible : Visibility.Hidden;
        ((Border)meter.Child).Width = share <= 0 ? 0 : Math.Max(4, MeterWidth * Math.Min(1, share));
    }

    private sealed record Row(TextBlock Detail, Border? Meter, Action? Toggle);
}
