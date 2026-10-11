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
    private const string Pangram = "The quick brown fox jumps over the lazy dog";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz\n0123456789 &@?!%*(){}";
    private static readonly double[] SpecimenSizes = [12, 16, 24, 32, 48, 72];
    private const double SpecimenInset = 40;

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
            var family = new FontFamily(uri, "./" + Uri.EscapeDataString(Path.GetFileName(path)) + "#" + familyName);
            var typeface = new Typeface(family, glyphs.Style, glyphs.Weight, glyphs.Stretch);
            if (!typeface.TryGetGlyphTypeface(out _))
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
                Covers(Alphabet) ? Alphabet : string.Join('\n', own.Chunk(26).Select(line => new string(line))),
                Covers(Pangram) ? Pangram : own[..Math.Min(40, own.Length)]);
        }
        catch
        {
            return null;
        }
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
        alphabet.LineHeight = 28 * 1.3;
        alphabet.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
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
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, size / 3), LastChildFill = true };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(line);
            ladder.Children.Add(row);
        }

        var page = new StackPanel
        {
            Margin = new Thickness(SpecimenInset, 32, 0, 40),
            ClipToBounds = true,
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
            var width = e.NewSize.Width;
            edgeFade.StartPoint = new Point(Math.Max(0, width - SpecimenInset), 0);
            edgeFade.EndPoint = new Point(width, 0);
            hero.FontSize = Math.Clamp(width * 0.17, 88, 200);
            alphabet.FontSize = Math.Clamp(width * 0.032, 18, 36);
            alphabet.LineHeight = alphabet.FontSize * 1.3;
        };

        // The surface takes a fixed color; the resource lets it follow the theme.
        return BuildScrollSurface(page, new Thickness(0), horizontal: false, Theme.Current.CanvasBg)
            .Themed(Control.BackgroundProperty, "Theme.CanvasBg");
    }
}
