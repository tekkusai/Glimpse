using System.Text;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

// Moves GLIMPSE_DATA_ROOT, which is process-wide: it must not run beside
// another test reading a cache path.
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PdfViewerLinkTests
{
    [Fact]
    public void ArtworkReachesTheViewerUnderAPdfName()
    {
        WithIsolatedDataRoot(temp =>
        {
            var artwork = Path.Combine(temp, "dieline.ai");
            File.WriteAllBytes(artwork, "%PDF-1.6 artwork"u8.ToArray());

            var linked = PdfViewerLink.TryResolve(artwork, CancellationToken.None);

            Assert.NotNull(linked);
            Assert.EndsWith(".pdf", linked, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(File.ReadAllBytes(artwork), File.ReadAllBytes(linked!));

            // The same artwork resolves to the same link instead of relinking.
            Assert.Equal(linked, PdfViewerLink.TryResolve(artwork, CancellationToken.None));
        });
    }

    [Fact]
    public void EditedArtworkRelinksInsteadOfServingTheOldBytes()
    {
        WithIsolatedDataRoot(temp =>
        {
            var artwork = Path.Combine(temp, "dieline.ai");
            File.WriteAllBytes(artwork, "%PDF-1.6 first"u8.ToArray());
            var first = PdfViewerLink.TryResolve(artwork, CancellationToken.None);

            File.WriteAllBytes(artwork, "%PDF-1.6 second draft"u8.ToArray());
            var second = PdfViewerLink.TryResolve(artwork, CancellationToken.None);

            Assert.NotNull(second);
            Assert.NotEqual(first, second);
            Assert.Equal(File.ReadAllBytes(artwork), File.ReadAllBytes(second!));
        });
    }

    [Fact]
    public void ATimePreservingMiddleReplacementGetsANewCacheEntry()
    {
        WithIsolatedDataRoot(temp =>
        {
            var artwork = Path.Combine(temp, "dieline.ai");
            var replacement = Path.Combine(temp, "replacement.ai");
            var timestamp = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            var firstBytes = new byte[700 * 1024];
            "%PDF-1.6"u8.CopyTo(firstBytes);
            firstBytes[600 * 1024] = 0x31;
            File.WriteAllBytes(artwork, firstBytes);
            File.SetLastWriteTimeUtc(artwork, timestamp);

            var first = PdfViewerLink.TryResolve(artwork, CancellationToken.None);

            var secondBytes = (byte[])firstBytes.Clone();
            secondBytes[600 * 1024] = 0x32;
            File.WriteAllBytes(replacement, secondBytes);
            File.SetLastWriteTimeUtc(replacement, timestamp);
            File.Move(replacement, artwork, overwrite: true);
            File.SetLastWriteTimeUtc(artwork, timestamp);
            var second = PdfViewerLink.TryResolve(artwork, CancellationToken.None);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first, second);
            Assert.Equal(firstBytes, File.ReadAllBytes(first!));
            Assert.Equal(secondBytes, File.ReadAllBytes(second!));
        });
    }

    /// <summary>
    /// The zoom fix only reaches the user if the resolve pipeline publishes a
    /// rescaled file, and a test host cannot spawn the proof helper, so the
    /// pipeline lands on Unproven here. What must hold is that it does not
    /// bake that in: no verdict means no cache entry to serve next time. A
    /// single bad minute used to freeze one file unscaled for a fortnight,
    /// which reads to the user as the zoom bug coming back.
    /// </summary>
    [Fact]
    public void AProofThatCouldNotRunIsNotCachedAsAnAnswer()
    {
        WithIsolatedDataRoot(temp =>
        {
            var artwork = Path.Combine(temp, "dieline.ai");
            WriteOversizedPdf(artwork);

            var first = PdfViewerLink.TryResolve(artwork, CancellationToken.None);
            Assert.NotNull(first);

            // Published so this preview has something to open, but marked so
            // the next one rebuilds instead of trusting it.
            Assert.True(File.Exists(first + ".unproven"), "an unproved entry must be marked");

            var second = PdfViewerLink.TryResolve(artwork, CancellationToken.None);
            Assert.NotNull(second);
            Assert.True(File.Exists(second + ".unproven"), "a rebuild that still cannot prove stays marked");
        });
    }

    private static void WriteOversizedPdf(string path)
    {
        const int Width = 12848;
        const int Height = 5503;
        var content = $"0 0 0 rg 100 100 {Width - 200} {Height - 200} re f\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Width} {Height}] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        };

        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            text.Append($"{offset:D10} 00000 n \n");
        }

        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, text.ToString(), System.Text.Encoding.ASCII);
    }

    // The cache lives under the data root, so a run must never write into the
    // installed app's own cache.
    private static void WithIsolatedDataRoot(Action<string> body)
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        try
        {
            body(temp.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }
}
