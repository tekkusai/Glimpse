using System.Numerics;
using System.Text;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// What a crafted mesh may cost. Geometry a file stores stops at the stored
/// ceiling, a count the file only declares is not taken on trust, a header
/// is not held before it is known to be one, and a simplifying pass cannot
/// fill a table with cells it will never draw.
/// </summary>
public sealed class MeshBoundsTests
{
    // Three vertices with no properties take no bytes. The old reader made
    // them up without reading, so the face below found its corners: with
    // two billion declared it filled memory the same way.
    [Fact]
    public void APlyElementWithNoPropertiesHoldsNoRows()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "empty-rows.ply");
        WriteBinaryPly(path, "element vertex 3\nelement face 1\nproperty list uchar int vertex_indices\n", [3, 0, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0]);

        Assert.Null(MeshPreviewLoader.TryLoad(path, CancellationToken.None));
    }

    [Fact]
    public void APlyThatDeclaresMoreRowsThanItsBytesHoldIsRefused()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "short.ply");
        WriteBinaryPly(path, "element vertex 2000000000\nproperty float x\nproperty float y\nproperty float z\n", new byte[36]);

        Assert.Null(MeshPreviewLoader.TryLoad(path, CancellationToken.None));
    }

    // A file's own vertices stop at the stored ceiling, four times what
    // instancing may place. PLY spells a vertex in two bytes. OBJ needs
    // eight and a text STL thirteen, so their files at the ceiling would be
    // 128 MB and 208 MB: too heavy for the suite, and they fill the same mesh.
    [Fact]
    public void StoredVerticesLoadAtTheCeilingAndAreRefusedPastIt()
    {
        using var temp = new TempDirectory();
        var at = Path.Combine(temp.Path, "at.ply");
        var past = Path.Combine(temp.Path, "past.ply");
        WritePlyVertices(at, MeshPreviewLoader.MaxStoredVertices);
        WritePlyVertices(past, MeshPreviewLoader.MaxStoredVertices + 1);

        Assert.Equal(1, MeshPreviewLoader.TryLoad(at, CancellationToken.None)?.SourceTriangles);
        Assert.Null(MeshPreviewLoader.TryLoad(past, CancellationToken.None));
    }

    // A polygon line makes a triangle for two bytes of text, so a text file
    // under the size limit can ask for far more triangles than it has lines.
    [Fact]
    public void StoredTrianglesLoadAtTheCeilingAndAreRefusedPastIt()
    {
        using var temp = new TempDirectory();
        var at = Path.Combine(temp.Path, "at.obj");
        var past = Path.Combine(temp.Path, "past.obj");
        WriteObjFans(at, MeshPreviewLoader.MaxStoredTriangles);
        WriteObjFans(past, MeshPreviewLoader.MaxStoredTriangles + 1);

        Assert.Equal(MeshPreviewLoader.MaxStoredTriangles, MeshPreviewLoader.TryLoad(at, CancellationToken.None)?.SourceTriangles);
        Assert.Null(MeshPreviewLoader.TryLoad(past, CancellationToken.None));
    }

    // The old reader kept every byte before the first newline, two bytes of
    // memory for each, and looked for "ply" only afterwards.
    [Theory]
    [InlineData("")]
    [InlineData("ply\n")]
    public void APlyHeaderWithNoNewlineIsRefusedWithoutBeingHeld(string start)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "endless.ply");
        using (var stream = File.Create(path))
        {
            stream.Write(Encoding.ASCII.GetBytes(start));
            var run = new byte[8 * 1024 * 1024];
            Array.Fill(run, (byte)'a');
            stream.Write(run);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var preview = MeshPreviewLoader.TryLoad(path, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(preview);
        Assert.True(allocated < 1024 * 1024, $"Reading the header allocated {allocated:N0} bytes.");
    }

    // A file under 4096 lines is parsed without a look at the token, and
    // one that draws under eight edges ends without another. So the only
    // look left for this file is the one in the walk over a representation.
    [Fact]
    public void TheStepWalkOverARepresentationStopsWhenCancelled()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "walk.step");
        File.WriteAllText(path, "ISO-10303-21;\nDATA;\n#1=SHAPE_REPRESENTATION('',(#4),#5);\n#2=SHAPE_REPRESENTATION('',(#4),#5);\n#3=SHAPE_REPRESENTATION_RELATIONSHIP('','',#1,#2);\nENDSEC;\n");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Null(MeshPreviewLoader.TryLoadStepOutline(path, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => MeshPreviewLoader.TryLoadStepOutline(path, cancelled.Token));
    }

    // Tiny triangles far apart each fill cells of their own and share none,
    // so the table grows with the file and nothing it holds is drawn.
    [Fact]
    public void ADustOfTinyFarApartTrianglesIsRefusedInsteadOfFillingTheCellTable()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "dust.stl");
        const int side = 18;
        const float size = 0.001f;
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(new byte[80]);
            writer.Write((uint)(side * side * side));
            for (var i = 0; i < side * side * side; i++)
            {
                var at = new Vector3(i % side, i / side % side, i / (side * side));
                foreach (var v in new[] { Vector3.Zero, at, at + new Vector3(size, 0, 0), at + new Vector3(0, size, 0) })
                {
                    writer.Write(v.X);
                    writer.Write(v.Y);
                    writer.Write(v.Z);
                }

                writer.Write((ushort)0);
            }
        }

        Assert.Null(MeshPreviewLoader.TryLoad(path, triangleCap: 100, CancellationToken.None));
    }

    private static void WriteBinaryPly(string path, string elements, byte[] body)
    {
        using var stream = File.Create(path);
        stream.Write(Encoding.ASCII.GetBytes("ply\nformat binary_little_endian 1.0\n" + elements + "end_header\n"));
        stream.Write(body);
    }

    /// <summary>
    /// <paramref name="count"/> vertices of two bytes each and one face on
    /// the first three.
    /// </summary>
    private static void WritePlyVertices(string path, int count)
    {
        using var stream = new BufferedStream(File.Create(path), 1 << 20);
        stream.Write(Encoding.ASCII.GetBytes(
            $"ply\nformat binary_little_endian 1.0\nelement vertex {count}\nproperty uchar x\nproperty uchar y\nelement face 1\nproperty list uchar int vertex_indices\nend_header\n"));
        stream.Write([0, 0, 1, 0, 0, 1]);
        stream.Write(new byte[2 * (count - 3)]);
        stream.Write([3, 0, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0]);
    }

    /// <summary>One triangle's three points, then polygons that fan into <paramref name="triangles"/> triangles.</summary>
    private static void WriteObjFans(string path, int triangles)
    {
        const int perLine = 1022;
        using var stream = new BufferedStream(File.Create(path), 1 << 20);
        stream.Write("v 0 0 0\nv 1 0 0\nv 0 1 0\n"u8);
        for (var left = triangles; left > 0; left -= perLine)
        {
            stream.Write("f 1"u8);
            for (var corner = 0; corner <= Math.Min(left, perLine); corner++)
            {
                stream.Write(corner % 2 == 0 ? " 2"u8 : " 3"u8);
            }

            stream.WriteByte((byte)'\n');
        }
    }
}
