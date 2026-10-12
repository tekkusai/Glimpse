using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    private const string Pangram = "The quick brown fox jumps over the lazy dog";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz\n0123456789 &@?!%*(){}";
    private static readonly double[] SpecimenSizes = [12, 16, 24, 32, 48, 72];
    private const double SpecimenInset = 40;

    // Ink past the letters' own widths, as a share of them: under 0.1 for
    // text, italic and handwriting faces (Times Italic 0.08), about 0.6 for
    // a swash script.
    private const double CrowdedOverhang = 0.25;

    /// <param name="Family">Resolves to this file alone, never its folder.</param>
    internal sealed record FontSpecimen(
        FontFamily Family,
        FontStyle Style,
        FontWeight Weight,
        FontStretch Stretch,
        string FamilyName,
        string Details,
        string Hero,
        string Alphabet,
        string Sample);

    private async Task<FrameworkElement> BuildFontPreviewAsync(string path, CancellationToken token)
    {
        var specimen = await Task.Run(() => TryLoadFontSpecimen(path), token);
        return specimen is null
            ? await BuildFileCardAsync(path, token)
            : BuildFontPreview(specimen);
    }

    /// <summary>
    /// Reads the face this file holds. The family points at the file itself
    /// and the weight, style and stretch come from its own tables, so a Bold
    /// file shows bold even when its Regular sibling sits in the same folder.
    /// Runs off the UI thread: parsing the font is the slow part.
    /// </summary>
    internal static FontSpecimen? TryLoadFontSpecimen(string path)
    {
        try
        {
            var uri = new Uri(path, UriKind.Absolute);
            var glyphs = new GlyphTypeface(uri);
            var familyName = Localized(glyphs.FamilyNames) ?? Path.GetFileNameWithoutExtension(path);
            if (ResolveOwnFamily(uri, path, glyphs, familyName) is not { } family)
            {
                return null;
            }

            // A symbol or non-Latin font gets samples from its own characters
            // rather than a row of empty boxes.
            bool Covers(string text) => text.All(c => char.IsWhiteSpace(c) || glyphs.CharacterToGlyphMap.ContainsKey(c));
            var own = new string(glyphs.CharacterToGlyphMap.Keys
                .Order()
                .Where(code => code > ' ' && code < 0xFFFF && !char.IsControl((char)code) && !char.IsSurrogate((char)code))
                .Take(78)
                .Select(code => (char)code)
                .ToArray());
            if (own.Length == 0)
            {
                return null;
            }

            var face = Localized(glyphs.FaceNames) ?? "Regular";
            var version = Localized(glyphs.VersionStrings)?.Split(';')[0].Trim();
            var details = string.Join(
                " · ",
                new[] { face, version, $"{glyphs.GlyphCount:N0} glyphs" }.Where(part => !string.IsNullOrWhiteSpace(part)));
            return new FontSpecimen(
                family,
                glyphs.Style,
                glyphs.Weight,
                glyphs.Stretch,
                familyName,
                details,
                Covers("Aa") ? "Aa" : own[..Math.Min(2, own.Length)],
                SpacedWhenCrowded(
                    new Typeface(family, glyphs.Style, glyphs.Weight, glyphs.Stretch),
                    Covers(Alphabet) ? Alphabet : string.Join('\n', own.Chunk(26).Select(line => new string(line)))),
                Covers(Pangram) ? Pangram : own[..Math.Min(40, own.Length)]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A family that resolves to this very file. WPF reads the file name in
    /// the location as written, so an escaped space or "(1)" misses it, and
    /// some name tables answer only to their Win32 name. Each spelling is
    /// tried; one that lands on a sibling or a fallback face never counts.
    /// </summary>
    private static FontFamily? ResolveOwnFamily(Uri uri, string path, GlyphTypeface glyphs, string familyName)
    {
        var file = Path.GetFileName(path);
        var names = glyphs.FamilyNames.Values.Concat(glyphs.Win32FamilyNames.Values).Prepend(familyName).Distinct(StringComparer.Ordinal).ToList();
        foreach (var location in new[] { "./" + file, "./" + Uri.EscapeDataString(file) }.Distinct(StringComparer.Ordinal))
        {
            foreach (var name in names)
            {
                var family = new FontFamily(uri, location + "#" + name);
                if (new Typeface(family, glyphs.Style, glyphs.Weight, glyphs.Stretch).TryGetGlyphTypeface(out var resolved)
                    && string.Equals(resolved.FontUri.LocalPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return family;
                }
            }
        }

        // An odd name table answers only to a name WPF composes itself
        // ("Anago ☞"); ask WPF for the faces it sees and keep this file's.
        foreach (var typeface in Fonts.GetTypefaces(uri))
        {
            if (typeface.TryGetGlyphTypeface(out var resolved)
                && string.Equals(resolved.FontUri.LocalPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return typeface.FontFamily;
            }
        }

        return null;
    }

    /// <summary>
    /// A script face's letters reach well past their own width and run into
    /// each other set solid; spaced out, each one can be seen. Measured from
    /// the ink, since a face's side bearings are not always told.
    /// </summary>
    internal static string SpacedWhenCrowded(Typeface typeface, string alphabet)
    {
        double advance = 0, overhang = 0;
        foreach (var c in alphabet.Where(c => !char.IsWhiteSpace(c)))
        {
            var glyph = Format(c.ToString(), typeface, 100);
            var ink = glyph.BuildGeometry(new Point()).Bounds;
            if (ink.IsEmpty)
            {
                continue;
            }

            advance += glyph.WidthIncludingTrailingWhitespace;
            overhang += Math.Max(0, -ink.Left) + Math.Max(0, ink.Right - glyph.WidthIncludingTrailingWhitespace);
        }

        return advance > 0 && overhang / advance > CrowdedOverhang
            // Spaced, a line is twice as wide, so it breaks in half to keep
            // every letter clear of the edge fade.
            ? string.Join('\n', alphabet.Split('\n')
                .Select(line => line.Where(c => c != ' ').ToArray())
                .SelectMany(line => line.Chunk((line.Length + 1) / 2))
                .Select(half => string.Join(' ', half)))
            : alphabet;
    }

    private static FormattedText Format(string text, Typeface typeface, double size, double lineHeight = 0)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            Brushes.Black,
            1.0);
        if (lineHeight > 0)
        {
            formatted.LineHeight = lineHeight;
        }

        return formatted;
    }

    /// <summary>
    /// Fits a specimen line's box to its ink. A script's swash reaches left
    /// of its first letter and its descenders fall below the line, outside
    /// the box WPF lays the text out in, where they were cut or struck
    /// through. The lines of a block are spaced by their ink, so they never
    /// run into each other.
    /// </summary>
    internal static void FitInk(TextBlock text)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var lines = text.Text.Split('\n');
        if (lines.Length > 1)
        {
            double above = 0, below = 0;
            foreach (var line in lines)
            {
                var formatted = Format(line, typeface, text.FontSize);
                var ink = formatted.BuildGeometry(new Point()).Bounds;
                if (!ink.IsEmpty)
                {
                    above = Math.Max(above, formatted.Baseline - ink.Top);
                    below = Math.Max(below, ink.Bottom - formatted.Baseline);
                }
            }

            text.LineHeight = Math.Max(text.FontSize * 1.3, Math.Ceiling((above + below) * 1.08));
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }

        var whole = Format(text.Text, typeface, text.FontSize, double.IsNaN(text.LineHeight) ? 0 : text.LineHeight);
        var bounds = whole.BuildGeometry(new Point()).Bounds;
        text.Padding = bounds.IsEmpty
            ? new Thickness(0)
            : new Thickness(
                Math.Ceiling(Math.Max(0, -bounds.Left)),
                Math.Ceiling(Math.Max(0, -bounds.Top)),
                Math.Ceiling(Math.Max(0, bounds.Right - whole.WidthIncludingTrailingWhitespace)),
                Math.Ceiling(Math.Max(0, bounds.Bottom - whole.Height)));
    }

    private static string? Localized(IDictionary<System.Globalization.CultureInfo, string> names) =>
        names.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var english)
            ? english
            : names.Values.FirstOrDefault();

    /// <summary>
    /// A type specimen: the family's name and facts, a large "Aa" beside the
    /// alphabet, then a ladder of sizes. The large sizes follow the stage's
    /// width, so the page fills a wide stage and still fits a narrow one.
    /// </summary>
    private static FrameworkElement BuildFontPreview(FontSpecimen specimen)
    {
        // Lines too long for the stage run on into the right inset and fade
        // out at the window's edge instead of being cut through a letter.
        // Anything that fits ends before the fade starts.
        var edgeFade = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        TextBlock Specimen(string text, double size, string colorToken) => new TextBlock
        {
            Text = text,
            FontFamily = specimen.Family,
            FontStyle = specimen.Style,
            FontWeight = specimen.Weight,
            FontStretch = specimen.Stretch,
            FontSize = size,
            TextWrapping = TextWrapping.NoWrap
        }.Themed(TextBlock.ForegroundProperty, colorToken);

        var title = new TextBlock
        {
            Text = specimen.FamilyName,
            Margin = new Thickness(0, 0, SpecimenInset, 0),
            FontSize = Metrics.FontTitle,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        }.Themed(TextBlock.ForegroundProperty, "Theme.TextPrimary");
        var details = new TextBlock
        {
            Text = specimen.Details,
            Margin = new Thickness(0, 4, SpecimenInset, 0),
            FontSize = Metrics.FontBody,
            TextTrimming = TextTrimming.CharacterEllipsis
        }.Themed(TextBlock.ForegroundProperty, "Theme.TextSecondary");

        var hero = Specimen(specimen.Hero, 140, "Theme.TextPrimary");
        hero.Margin = new Thickness(0, 0, 40, 0);
        hero.VerticalAlignment = VerticalAlignment.Center;
        var alphabet = Specimen(specimen.Alphabet, 28, "Theme.TextSecondary");
        alphabet.VerticalAlignment = VerticalAlignment.Center;
        var showcase = new Grid
        {
            Margin = new Thickness(0, 28, 0, 0),
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            OpacityMask = edgeFade
        };
        Grid.SetColumn(alphabet, 1);
        showcase.Children.Add(hero);
        showcase.Children.Add(alphabet);

        var ladder = new StackPanel { Margin = new Thickness(0, 28, 0, 0), OpacityMask = edgeFade };
        foreach (var size in SpecimenSizes)
        {
            var label = new TextBlock
            {
                Text = $"{size:0}",
                Width = 40,
                FontSize = Metrics.FontCaption,
                VerticalAlignment = VerticalAlignment.Center
            }.Themed(TextBlock.ForegroundProperty, "Theme.TextTertiary");
            var line = Specimen(specimen.Sample, size, "Theme.TextPrimary");
            FitInk(line);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, size / 3), LastChildFill = true };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(line);
            ladder.Children.Add(row);
        }

        var page = new StackPanel
        {
            // No clip: a script face's swash hangs left of its first letter
            // into the inset, and the fade already ends every long line.
            Margin = new Thickness(SpecimenInset, 32, 0, 40),
            Children =
            {
                title,
                details,
                showcase,
                new Border { Height = 1, Margin = new Thickness(0, 28, SpecimenInset, 0) }.Themed(Border.BackgroundProperty, "Theme.Border"),
                ladder
            }
        };

        // The showcase scales with the stage: a wide stage gets a bigger
        // "Aa" and alphabet, a narrow one keeps both on screen.
        page.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged)
            {
                return;
            }

            var width = e.NewSize.Width;
            edgeFade.StartPoint = new Point(Math.Max(0, width - SpecimenInset), 0);
            edgeFade.EndPoint = new Point(width, 0);
            hero.FontSize = Math.Clamp(width * 0.17, 88, 200);
            alphabet.FontSize = Math.Clamp(width * 0.032, 18, 36);
            FitInk(hero);
            FitInk(alphabet);
        };

        // The surface takes a fixed color; the resource lets it follow the theme.
        return BuildScrollSurface(page, new Thickness(0), horizontal: false, Theme.Current.CanvasBg)
            .Themed(Control.BackgroundProperty, "Theme.CanvasBg");
    }
}
