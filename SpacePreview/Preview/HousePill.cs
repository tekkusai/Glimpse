using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

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
/// Code-built pills for surfaces that assemble their chrome at runtime. The
/// look lives in Themes/Chrome.xaml (Pill, AccentPill, QuietPill, ChoicePill);
/// a pill that shows a choice keeps the returned refresher and calls it when
/// the choice moves.
/// </summary>
internal static class HousePill
{
    public static ButtonBase Build(string label, Action onClick, PillRole role = PillRole.Surface, Func<bool>? isSelected = null)
        => Build(label, onClick, out _, role, isSelected);

    public static ButtonBase Build(string label, Action onClick, out Action refresh, PillRole role = PillRole.Surface, Func<bool>? isSelected = null)
    {
        ButtonBase pill = isSelected is null ? new Button() : new ToggleButton();
        pill.Content = label;
        pill.Margin = new Thickness(0, 0, 8, 0);
        pill.Style = (Style)Application.Current.FindResource(role switch
        {
            PillRole.Accent => "AccentPill",
            PillRole.Ghost => "QuietPill",
            _ => isSelected is null ? "Pill" : "ChoicePill"
        });
        pill.Click += (_, _) => onClick();

        refresh = () =>
        {
            if (pill is ToggleButton choice)
            {
                choice.IsChecked = isSelected?.Invoke() == true;
            }
        };
        refresh();
        return pill;
    }
}
