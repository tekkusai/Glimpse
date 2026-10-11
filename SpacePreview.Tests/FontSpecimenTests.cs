using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class FontSpecimenTests
{
    private static readonly string Fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    [Fact]
    public void BoldFileShowsItsOwnFaceBesideItsRegularSibling()
    {
        using var temp = new TempDirectory();
        // The Regular face shares the folder and the family name: a family
        // resolved from the folder would pick it.
        File.Copy(Path.Combine(Fonts, "segoeui.ttf"), Path.Combine(temp.Path, "segoeui.ttf"));
        var bold = Path.Combine(temp.Path, "segoeuib.ttf");
        File.Copy(Path.Combine(Fonts, "segoeuib.ttf"), bold);

        var specimen = PreviewWindow.TryLoadFontSpecimen(bold)!;

        Assert.Equal("Segoe UI", specimen.FamilyName);
        Assert.Equal(FontWeights.Bold, specimen.Weight);
        Assert.StartsWith("Bold · ", specimen.Details, StringComparison.Ordinal);
        Assert.True(new Typeface(specimen.Family, specimen.Style, specimen.Weight, specimen.Stretch)
            .TryGetGlyphTypeface(out var resolved));
        Assert.Equal(new Uri(bold), resolved.FontUri);
    }

    // Mark's "master_of_break (1).otf" showed a file card in 3.0.0: the
    // escaped name in the family's location never resolved.
    [Theory]
    [InlineData("with space.ttf")]
    [InlineData("segoeui (1).ttf")]
    [InlineData("a+b.ttf")]
    [InlineData("フォント.ttf")]
    public void AFontNamedTheWayDownloadsNameThemStillShowsItsSpecimen(string name)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, name);
        File.Copy(Path.Combine(Fonts, "segoeui.ttf"), path);

        var specimen = PreviewWindow.TryLoadFontSpecimen(path);

        Assert.NotNull(specimen);
        Assert.Equal("Segoe UI", specimen!.FamilyName);
        Assert.True(new Typeface(specimen.Family, specimen.Style, specimen.Weight, specimen.Stretch)
            .TryGetGlyphTypeface(out var resolved));
        Assert.Equal(path, resolved.FontUri.LocalPath, ignoreCase: true);
    }

    // A script face's swash ran left of its first letter and below the
    // line, where the specimen cut it and the divider struck through it.
    [Fact]
    public void ASpecimenLineIsPaddedToHoldAllItsInk()
    {
        Sta.Run(() =>
        {
            var line = new TextBlock
            {
                Text = "jump fj",
                FontFamily = new FontFamily("Times New Roman"),
                FontStyle = FontStyles.Italic,
                FontSize = 120
            };

            PreviewWindow.FitInk(line);

            var formatted = new FormattedText(line.Text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(line.FontFamily, line.FontStyle, line.FontWeight, line.FontStretch), line.FontSize, Brushes.Black, 1.0);
            var ink = formatted.BuildGeometry(new Point(line.Padding.Left, line.Padding.Top)).Bounds;
            Assert.True(line.Padding.Left > 0, "the italic j reaches left of its box");
            Assert.True(ink.Left >= -0.5 && ink.Top >= -0.5);
            Assert.True(ink.Right <= line.Padding.Left + formatted.WidthIncludingTrailingWhitespace + line.Padding.Right + 0.5);
            Assert.True(ink.Bottom <= line.Padding.Top + formatted.Height + line.Padding.Bottom + 0.5);
        });
    }

    // Mark: the pointer stayed an arrow over the switches.
    [Fact]
    public async Task TheHouseSwitchShowsTheHandPointer()
    {
        await WpfApp.RunAsync(() =>
        {
            var toggle = new System.Windows.Controls.Primitives.ToggleButton
            {
                Style = (Style)Application.Current.FindResource("HouseToggle")
            };

            Assert.Equal(System.Windows.Input.Cursors.Hand, toggle.Cursor);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("segoeui.ttf")]
    [InlineData("timesi.ttf")]
    public void TextAndItalicFacesKeepTheirAlphabetSetSolid(string file)
    {
        var specimen = PreviewWindow.TryLoadFontSpecimen(Path.Combine(Fonts, file))!;

        Assert.StartsWith("ABCDEFGHIJKLMNOPQRSTUVWXYZ\n", specimen.Alphabet, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolFontsSampleTheirOwnCharacters()
    {
        var specimen = PreviewWindow.TryLoadFontSpecimen(Path.Combine(Fonts, "segmdl2.ttf"))!;

        Assert.DoesNotContain("quick brown fox", specimen.Sample, StringComparison.Ordinal);
        Assert.NotEqual("Aa", specimen.Hero);
    }

    [Fact]
    public void AFileThatIsNotAFontHasNoSpecimen()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("broken.ttf", "not a font");

        Assert.Null(PreviewWindow.TryLoadFontSpecimen(path));
    }
}
