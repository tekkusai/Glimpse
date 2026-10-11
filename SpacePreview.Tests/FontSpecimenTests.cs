using System.Windows;
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
