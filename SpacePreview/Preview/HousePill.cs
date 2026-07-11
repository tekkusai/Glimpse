using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// A pill's job on any surface: Accent is the surface's single commit
/// action, Ghost is a quiet exit, Surface is everything else.
/// </summary>
internal enum PillRole
{
    Surface,
    Accent,
    Ghost
}

/// <summary>
/// The app's pill, in one place: Metrics corners and padding, Surface fill
/// with HoverBg/PressedBg motion, action on release. Surfaces that show
/// selection state keep the returned refresher and call it when state moves.
/// </summary>
internal static class HousePill
{
    public static Border Build(string label, Action onClick, PillRole role = PillRole.Surface, Func<bool>? isSelected = null)
        => Build(label, onClick, out _, role, isSelected);

    public static Border Build(string label, Action onClick, out Action refresh, PillRole role = PillRole.Surface, Func<bool>? isSelected = null)
    {
        var palette = Theme.Current;
        var text = new TextBlock
        {
            Text = label,
            FontSize = Metrics.FontLabel,
            FontWeight = FontWeights.SemiBold,
            Foreground = role == PillRole.Accent
                ? Brushes.White
                : Theme.Brush(role == PillRole.Ghost ? palette.TextSecondary : palette.TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var pill = new Border
        {
            Margin = new Thickness(0, 0, 8, 0),
            Padding = Metrics.PillPadding,
            CornerRadius = Metrics.PillCorner,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = text
        };

        void Rest()
        {
            pill.Opacity = 1;
            if (role == PillRole.Accent)
            {
                pill.Background = Theme.Brush(palette.Accent);
                pill.BorderBrush = Theme.Brush(palette.Accent);
                return;
            }

            var selected = isSelected?.Invoke() == true;
            pill.Background = selected
                ? Theme.Brush(palette.PressedBg)
                : role == PillRole.Ghost ? Brushes.Transparent : Theme.Brush(palette.SurfaceBg);
            pill.BorderBrush = selected
                ? Theme.Brush(palette.Accent)
                : role == PillRole.Ghost ? Brushes.Transparent : Theme.Brush(palette.SurfaceBorder);
            text.Foreground = selected || role != PillRole.Ghost
                ? Theme.Brush(palette.TextPrimary)
                : Theme.Brush(palette.TextSecondary);
        }

        pill.MouseEnter += (_, _) =>
        {
            if (role == PillRole.Accent)
            {
                pill.Opacity = 0.9;
            }
            else if (isSelected?.Invoke() != true)
            {
                pill.Background = Theme.Brush(palette.HoverBg);
            }
        };
        pill.MouseLeave += (_, _) => Rest();
        pill.MouseLeftButtonDown += (_, e) =>
        {
            if (role == PillRole.Accent)
            {
                pill.Opacity = 0.82;
            }
            else
            {
                pill.Background = Theme.Brush(palette.PressedBg);
            }

            e.Handled = true;
        };
        pill.MouseLeftButtonUp += (_, e) =>
        {
            Rest();
            onClick();
            e.Handled = true;
        };

        refresh = Rest;
        Rest();
        return pill;
    }
}
