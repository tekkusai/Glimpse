using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Windows.Controls;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// 2.0 showed what a model or a drawing said about itself when it could
/// not draw it, and 3.0.0 showed a bare card. The card says it again: at
/// most two lines, nothing the header already shows, and no more of an
/// untrusted file read than 2.0 read.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CardFactsTests
{
    private const string StepHeader = """
        ISO-10303-21;
        HEADER;
        FILE_DESCRIPTION(('a bracket'),'2;1');
        FILE_NAME('bracket.stp','2026-10-12T09:00:00',('Mark'),('Glsswrks'),'','','');
        FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));
        ENDSEC;
        DATA;

        """;

    // Surfaces only: no edge curves, so there is no wireframe to draw. The
    // first product carries the file's own name, as most exports do.
    private const string StepBody = """
        #1 = PRODUCT('Bracket','Bracket','',(#3));
        #2=PRODUCT('M8 bolt','M8 bolt','',(#3));
        #3=PRODUCT_CONTEXT('',#4,'mechanical');
        #4=APPLICATION_CONTEXT('automotive design');
          #5=PRODUCT('m8 BOLT','again under another case','',(#3));
        #6=PRODUCT('Mark''s washer','','',(#3));
        ENDSEC;
        END-ISO-10303-21;
        """;

    [Fact]
    public void AStepFileWithNoWireframeNamesItsOtherProductsThenItsSchemaAndEntities()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("bracket.step", StepHeader + StepBody);

        Assert.Null(MeshPreviewLoader.TryLoadStepOutline(path, CancellationToken.None));
        Assert.Equal(
            new[] { "M8 bolt, Mark's washer", "AUTOMOTIVE_DESIGN · 6 entities" },
            CardFacts.Step(path, CancellationToken.None));
    }

    // 2.0 counted the first 8 MB and gave that as the file's count.
    [Fact]
    public void AStepFileLargerThanTheSampleHoldsAtLeastTheNumberCounted()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "plant.stp");
        var line = "#7=CARTESIAN_POINT('',(0.,0.,0.));\n";
        var lines = CardFacts.ModelSampleBytes / line.Length + 1000;
        File.WriteAllText(path, StepHeader + string.Concat(Enumerable.Repeat(line, lines)));

        var facts = CardFacts.Step(path, CancellationToken.None);

        var about = Assert.Single(facts);
        Assert.StartsWith("AUTOMOTIVE_DESIGN · At least ", about, StringComparison.Ordinal);
        Assert.EndsWith(" entities", about, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepFileThatSaysNothingGetsNoLines()
    {
        using var temp = new TempDirectory();

        Assert.Empty(CardFacts.Step(temp.CreateFile("empty.step", "ISO-10303-21;\nEND-ISO-10303-21;\n"), CancellationToken.None));
        Assert.Empty(CardFacts.Step(Path.Combine(temp.Path, "gone.step"), CancellationToken.None));
    }

    [Fact]
    public void AnStlThatWillNotLoadKeepsItsTriangleCount()
    {
        using var temp = new TempDirectory();

        // Binary, and every corner is not a number: nothing to draw.
        var binary = Path.Combine(temp.Path, "broken.stl");
        var bytes = new byte[84 + (1200 * 50)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 1200);
        bytes.AsSpan(84).Fill(0xFF);
        File.WriteAllBytes(binary, bytes);

        var text = temp.CreateFile(
            "broken-text.stl",
            "solid part\n" + string.Concat(Enumerable.Repeat("facet normal 0 0 1\n outer loop\n  vertex nan nan nan\n endloop\nendfacet\n", 3)) + "endsolid part\n");

        Assert.Null(MeshPreviewLoader.TryLoad(binary, CancellationToken.None));
        Assert.Null(MeshPreviewLoader.TryLoad(text, CancellationToken.None));
        // Counts are grouped the region's way, so the expectation is too.
        Assert.Equal(new[] { $"{1200:N0} triangles" }, CardFacts.Stl(binary, CancellationToken.None));
        Assert.Equal(new[] { "3 triangles" }, CardFacts.Stl(text, CancellationToken.None));
        Assert.Empty(CardFacts.Stl(temp.CreateFile("words.stl", "not a mesh at all"), CancellationToken.None));
    }

    [Fact]
    public void APostScriptIllustratorFileKeepsItsTitleThenItsAppAndDate()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("poster.ai", Illustrator("(Summer poster)"));

        Assert.Equal(
            new[] { "Summer poster", "Adobe Illustrator(R) 8.0 · 5/21/98 3:14 PM" },
            CardFacts.Illustrator(path));
    }

    [Theory]
    [InlineData(@"(Left \) right)", "Left ) right")]
    [InlineData(@"(Left \\) (right)", "Left \\ right")]
    [InlineData(@"(Left \051 right)", "Left ) right")]
    [InlineData(@"(Left \( right)", "Left ( right")]
    [InlineData(@"(Left\nright)", "Left right")]
    [InlineData(@"(Left\012right)", "Left right")]
    [InlineData(@"(Pai\156t \162\164\142\146)", "Paint rtbf")]
    [InlineData(@"(Caf\351)", "Caf\u00E9")]
    [InlineData("(Left (middle) right)", "Left (middle) right")]
    [InlineData("(Left) (right)", "Left right")]
    [InlineData("Left \\", "Left \\")]
    public void AnIllustratorTitleReadsEscapesBeforeParentheses(string title, string expected)
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("poster.ai", Illustrator(title));

        Assert.Equal(expected, CardFacts.Illustrator(path)[0]);
    }

    [Theory]
    [InlineData("(logo.ai)")]
    [InlineData("LOGO")]
    public void AnIllustratorTitleThatIsTheFileNameIsLeftToTheHeader(string title)
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("logo.ai", Illustrator(title));

        Assert.Equal(new[] { "Adobe Illustrator(R) 8.0 · 5/21/98 3:14 PM" }, CardFacts.Illustrator(path));
    }

    [Fact]
    public void AnIllustratorFileWithNoCommentsGetsNoLines()
    {
        using var temp = new TempDirectory();

        Assert.Empty(CardFacts.Illustrator(temp.CreateFile("blank.ai", "%!PS-Adobe-3.0\n%%EndComments\n")));
    }

    // A name is whatever sits between two quotes, so a crafted file can
    // make one megabytes long. It never reaches a text block at that length.
    [Fact]
    public void AFactLineIsNeverLongerThanTheCardCouldShow()
    {
        using var temp = new TempDirectory();
        var step = temp.CreateFile("long.step", StepHeader + $"#1=PRODUCT('{new string('a', 1_000_000)}','','',());\n");
        var artwork = temp.CreateFile("long.ai", Illustrator(new string('b', 100_000)));

        Assert.All(
            CardFacts.Step(step, CancellationToken.None).Concat(CardFacts.Illustrator(artwork)),
            line => Assert.InRange(line.Length, 1, CardFacts.MaxFactChars + 1));
    }

    // 2.0's entity pattern let the blanks before a number run across
    // lines, so a file of empty lines cost the square of its length: this
    // one took minutes.
    [Fact]
    public void AStepFileOfEmptyLinesIsCountedInOnePass()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("blank.step", new string('\n', 600_000));
        var clock = Stopwatch.StartNew();

        Assert.Empty(CardFacts.Step(path, CancellationToken.None));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Theory]
    [InlineData("bracket.step", "M8 bolt, Mark's washer", "AUTOMOTIVE_DESIGN · 6 entities")]
    [InlineData("poster.ai", "Summer poster", "Adobe Illustrator(R) 8.0 · 5/21/98 3:14 PM")]
    [InlineData("broken.stl", "3 triangles", null)]
    public Task TheCardShowsTheFactsUnderTheName(string name, string first, string? second) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile(name, Path.GetExtension(name) switch
        {
            ".step" => StepHeader + StepBody,
            ".ai" => Illustrator("(Summer poster)"),
            _ => "solid part\n" + string.Concat(Enumerable.Repeat("facet normal 0 0 1\n vertex nan nan nan\nendfacet\n", 3))
        });
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (window.PreviewContent.Content is not FileCardLayout && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            var card = Assert.IsType<FileCardLayout>(window.PreviewContent.Content);
            var lines = Assert.IsType<StackPanel>(card.Children[1]).Children.OfType<TextBlock>().Select(line => line.Text).ToArray();
            string[] expected = second is null ? [name, first] : [name, first, second];
            Assert.Equal(expected, lines);
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    private static string Illustrator(string title) =>
        "%!PS-Adobe-3.0 \r\n"
        + "%%Creator: Adobe Illustrator(R) 8.0\r\n"
        + "%%AI8_CreatorVersion: 8\r\n"
        + "%%For: (Mark) (Glsswrks)\r\n"
        + $"%%Title: {title}\r\n"
        + "%%CreationDate: (5/21/98) (3:14 PM)\r\n"
        + "%%BoundingBox: 0 0 612 792\r\n"
        + "%%DocumentProcessColors: Cyan Magenta Yellow Black\r\n"
        + "%%EndComments\r\n"
        + "%%BeginProlog\r\n";
}
