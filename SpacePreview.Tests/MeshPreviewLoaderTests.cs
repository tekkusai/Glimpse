using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Media3D;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Every parser reads a tiny generated file. The simplifier is checked on a
/// closed sphere: what it draws must still be a closed surface spanning the
/// whole part, which the old every-Nth-triangle sampling never was.
/// </summary>
public sealed class MeshPreviewLoaderTests
{
    [Fact]
    public void BinaryStlKeepsEveryTriangleUnderTheCap()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "cube.stl");
        WriteBinaryStl(path, CubeTriangles());

        var preview = Load(path);

        Assert.Equal(12, preview.SourceTriangles);
        Assert.Equal(12, RenderedTriangles(preview));
        Assert.Equal("STL mesh", preview.FormatName);
    }

    [Fact]
    public void LargeBinaryStlIsSimplifiedIntoAClosedSurfaceThatSpansThePart()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sphere.stl");
        var sphere = SphereTriangles(stacks: 90, slices: 120);
        WriteBinaryStl(path, sphere);

        var preview = MeshPreviewLoader.TryLoad(path, triangleCap: 2_000, CancellationToken.None)!;

        Assert.Equal(sphere.Count, preview.SourceTriangles);
        var rendered = RenderedTriangles(preview);
        Assert.InRange(rendered, 500, 2_200);
        Assert.True(BoundaryEdgeShare(preview) < 0.02, $"open edges: {BoundaryEdgeShare(preview):P1}");
        AssertExtents(preview, 3.1, 3.1, 3.1, tolerance: 0.15);
    }

    [Fact]
    public void AsciiStlReadsThePartPastItsFirstFacets()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sphere.stl");
        var sphere = SphereTriangles(stacks: 30, slices: 40);
        var text = new StringBuilder("solid sphere\n");
        foreach (var (a, b, c) in sphere)
        {
            text.Append("facet normal 0 0 0\nouter loop\n");
            foreach (var v in new[] { a, b, c })
            {
                text.Append(CultureInfo.InvariantCulture, $"vertex {v.X:R} {v.Y:R} {v.Z:R}\n");
            }

            text.Append("endloop\nendfacet\n");
        }

        File.WriteAllText(path, text.Append("endsolid sphere\n").ToString());

        var preview = MeshPreviewLoader.TryLoad(path, triangleCap: 600, CancellationToken.None)!;

        Assert.Equal(sphere.Count, preview.SourceTriangles);
        Assert.True(BoundaryEdgeShare(preview) < 0.02);
        AssertExtents(preview, 3.1, 3.1, 3.1, tolerance: 0.2);
    }

    [Fact]
    public void ObjFanTriangulatesPolygonsAndResolvesRelativeIndices()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "box.obj");
        File.WriteAllText(path, """
            # a 2 x 1 x 1 box, faces as quads
            v 0 0 0
            v 2 0 0
            v 2 1 0
            v 0 1 0
            v 0 0 1
            v 2 0 1
            v 2 1 1
            v 0 1 1
            f 1 4 3 2
            f 5/1 6/2 7/3 8/4
            f 1//1 2//1 6//1 5//1
            f 2/1/1 3/1/1 7/1/1 6/1/1
            f 3 4 8 7
            f -8 -4 -1 -5
            """);

        var preview = Load(path);

        Assert.Equal(12, preview.SourceTriangles);
        Assert.Equal(0, BoundaryEdgeShare(preview));
        AssertExtents(preview, 3.1, 1.55, 1.55);
    }

    [Fact]
    public void NonFiniteVerticesDropOnlyTheirTriangles()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "spike.obj");
        File.WriteAllText(path, """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            v NaN 0 0
            f 1 2 3
            f 1 2 4
            """);

        var preview = Load(path);

        Assert.Equal(1, preview.SourceTriangles);
        Assert.All(preview.Mesh.Positions, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z)));
    }

    // A model saved in map coordinates sits millions of units from zero,
    // where a float steps by half a unit. 2.0 kept doubles until it had
    // centered the model; 3.0 cast first, and a small part lost its shape:
    // this cube came out with four corners and no thickness.
    [Theory]
    [InlineData("obj")]
    [InlineData("stl")]
    [InlineData("ply")]
    public void ASmallPartFarFromZeroKeepsItsShape(string kind)
    {
        using var temp = new TempDirectory();
        var cube = CubeQuads();
        string At(Vector3 v) => FormattableString.Invariant($"{500000 + 0.1 * v.X:R} {4500000 + 0.1 * v.Y:R} {0.1 * v.Z:R}");
        var text = new StringBuilder();
        switch (kind)
        {
            case "obj":
                foreach (var v in cube.Vertices)
                {
                    text.Append("v ").Append(At(v)).Append('\n');
                }

                foreach (var face in cube.Faces)
                {
                    text.Append("f ").AppendJoin(' ', face.Select(index => index + 1)).Append('\n');
                }

                break;
            case "stl":
                text.Append("solid cube\n");
                foreach (var (a, b, c) in CubeTriangles())
                {
                    text.Append("facet normal 0 0 0\nouter loop\n");
                    foreach (var v in new[] { a, b, c })
                    {
                        text.Append("vertex ").Append(At(v)).Append('\n');
                    }

                    text.Append("endloop\nendfacet\n");
                }

                text.Append("endsolid cube\n");
                break;
            default:
                text.Append($"ply\nformat ascii 1.0\nelement vertex {cube.Vertices.Length}\nproperty double x\nproperty double y\nproperty double z\nelement face {cube.Faces.Length}\nproperty list uchar int vertex_indices\nend_header\n");
                foreach (var v in cube.Vertices)
                {
                    text.Append(At(v)).Append('\n');
                }

                foreach (var face in cube.Faces)
                {
                    text.Append("4 ").AppendJoin(' ', face).Append('\n');
                }

                break;
        }

        var path = Path.Combine(temp.Path, "far." + kind);
        File.WriteAllText(path, text.ToString());

        var preview = Load(path);

        Assert.Equal(8, preview.Mesh.Positions.Distinct().Count());
        var bounds = preview.Mesh.Bounds;
        Assert.True(bounds.SizeY > 0, "The cube has no thickness.");
        Assert.InRange(bounds.SizeY / bounds.SizeX, 0.99, 1.01);
        Assert.InRange(bounds.SizeZ / bounds.SizeX, 0.99, 1.01);
    }

    // Coordinates are measured from the first vertex, so the first vertex
    // has to be one: a stray "not a number" spoils only itself.
    [Fact]
    public void AVertexThatIsNotANumberIsNotWhereTheModelIsMeasuredFrom()
    {
        using var temp = new TempDirectory();
        var cube = CubeQuads();
        var text = new StringBuilder("v NaN 0 0\n");
        foreach (var v in cube.Vertices)
        {
            text.Append(FormattableString.Invariant($"v {v.X:R} {v.Y:R} {v.Z:R}\n"));
        }

        foreach (var face in cube.Faces)
        {
            text.Append("f ").AppendJoin(' ', face.Select(index => index + 2)).Append('\n');
        }

        var path = Path.Combine(temp.Path, "stray.obj");
        File.WriteAllText(path, text.ToString());

        var preview = Load(path);

        Assert.Equal(8, preview.Mesh.Positions.Distinct().Count());
        Assert.All(preview.Mesh.Positions, p => Assert.True(double.IsFinite(p.X + p.Y + p.Z)));
    }

    [Fact]
    public void BinaryAndAsciiPlyReadQuadFaces()
    {
        using var temp = new TempDirectory();
        var cube = CubeQuads();

        var binaryPath = Path.Combine(temp.Path, "binary.ply");
        using (var stream = File.Create(binaryPath))
        {
            var header = $"ply\nformat binary_little_endian 1.0\nelement vertex {cube.Vertices.Length}\nproperty float x\nproperty float y\nproperty float z\nelement face {cube.Faces.Length}\nproperty list uchar int vertex_indices\nend_header\n";
            stream.Write(Encoding.ASCII.GetBytes(header));
            using var writer = new BinaryWriter(stream);
            foreach (var v in cube.Vertices)
            {
                writer.Write(v.X);
                writer.Write(v.Y);
                writer.Write(v.Z);
            }

            foreach (var face in cube.Faces)
            {
                writer.Write((byte)face.Length);
                foreach (var index in face)
                {
                    writer.Write(index);
                }
            }
        }

        var asciiPath = Path.Combine(temp.Path, "ascii.ply");
        var text = new StringBuilder($"ply\nformat ascii 1.0\nelement vertex {cube.Vertices.Length}\nproperty float x\nproperty float y\nproperty float z\nelement face {cube.Faces.Length}\nproperty list uchar int vertex_indices\nend_header\n");
        foreach (var v in cube.Vertices)
        {
            text.Append(CultureInfo.InvariantCulture, $"{v.X} {v.Y} {v.Z}\n");
        }

        foreach (var face in cube.Faces)
        {
            text.Append(face.Length).Append(' ').AppendJoin(' ', face).Append('\n');
        }

        File.WriteAllText(asciiPath, text.ToString());

        foreach (var path in new[] { binaryPath, asciiPath })
        {
            var preview = Load(path);
            Assert.Equal(12, preview.SourceTriangles);
            Assert.Equal(0, BoundaryEdgeShare(preview));
        }
    }

    [Fact]
    public void GlbPlacesSceneNodesAndTurnsYUpIntoZUp()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "box.glb");
        // A unit cube stretched 2x along glTF X by its node, and 3x along
        // glTF Y (up) by its parent.
        WriteGlb(path, CubeQuads(), nodes: """
            [
              { "children": [1], "scale": [1, 3, 1] },
              { "mesh": 0, "scale": [2, 1, 1], "translation": [5, 5, 5] }
            ]
            """);

        var preview = Load(path);

        Assert.Equal("glTF model", preview.FormatName);
        Assert.Equal(12, preview.SourceTriangles);
        Assert.Equal(0, BoundaryEdgeShare(preview));
        // glTF up (3) becomes viewport Z; glTF depth (1) becomes Y.
        AssertExtents(preview, 3.1 * 2 / 3, 3.1 / 3, 3.1);
    }

    [Fact]
    public void GlbWithoutAReadableBinChunkIsNotAMesh()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "broken.glb");
        File.WriteAllBytes(path, [.. "glTF"u8, 2, 0, 0, 0, 40, 0, 0, 0, 4, 0, 0, 0, .. "JSON"u8, .. "{}  "u8]);

        Assert.Null(MeshPreviewLoader.TryLoad(path, CancellationToken.None));
    }

    [Fact]
    public void CancelledLoadsStopInsteadOfParsingToTheEnd()
    {
        using var temp = new TempDirectory();
        var stl = Path.Combine(temp.Path, "cube.stl");
        WriteBinaryStl(stl, CubeTriangles());
        var step = Path.Combine(temp.Path, "cube.step");
        File.WriteAllText(step, CubeStep());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => MeshPreviewLoader.TryLoad(stl, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => MeshPreviewLoader.TryLoadStepOutline(step, cancelled.Token));
        Assert.NotNull(MeshPreviewLoader.TryLoadStepOutline(step, CancellationToken.None));
    }

    // One mesh of many points placed again by every node: the placed total
    // has a budget, so the file is refused instead of filling memory.
    [Fact]
    public void GlbInstancesPastTheVertexBudgetAreRefused()
    {
        using var temp = new TempDirectory();
        var atBudget = Path.Combine(temp.Path, "at-budget.glb");
        var past = Path.Combine(temp.Path, "past.glb");
        const int points = 100_000;
        WriteInstancedGlb(atBudget, points, triangles: 1, instances: MeshPreviewLoader.MaxPlacedVertices / points);
        WriteInstancedGlb(past, points, triangles: 1, instances: MeshPreviewLoader.MaxPlacedVertices / points + 1);

        Assert.Equal(MeshPreviewLoader.MaxPlacedVertices / points, Load(atBudget).SourceTriangles);
        Assert.Null(MeshPreviewLoader.TryLoad(past, CancellationToken.None));
    }

    [Fact]
    public void GlbInstancesPastTheTriangleBudgetAreRefused()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "past.glb");
        const int triangles = 100_000;
        WriteInstancedGlb(path, points: 3, triangles, instances: MeshPreviewLoader.MaxPlacedTriangles / triangles + 1);

        Assert.Null(MeshPreviewLoader.TryLoad(path, CancellationToken.None));
    }

    [Fact]
    public void ThreeMfBuildItemsPastTheVertexBudgetAreRefused()
    {
        using var temp = new TempDirectory();
        var atBudget = Path.Combine(temp.Path, "at-budget.3mf");
        var past = Path.Combine(temp.Path, "past.3mf");
        const int points = 10_000;
        var model = Object3mf(Enumerable.Range(0, points).Select(i => new Vector3(i, i % 2, 0)), [(0, 1, 2)]);
        Write3mf(atBudget, model, items: MeshPreviewLoader.MaxPlacedVertices / points);
        Write3mf(past, model, items: MeshPreviewLoader.MaxPlacedVertices / points + 1);

        Assert.Equal(MeshPreviewLoader.MaxPlacedVertices / points, Load(atBudget).SourceTriangles);
        Assert.Null(MeshPreviewLoader.TryLoad(past, CancellationToken.None));
    }

    // XDocument slows with the square of nesting depth; a model nested past
    // the limit is refused before it is built.
    [Fact]
    public void A3mfNestedPastTheLimitIsRefused()
    {
        using var temp = new TempDirectory();
        var deep = Path.Combine(temp.Path, "deep.3mf");
        var ordinary = Path.Combine(temp.Path, "ordinary.3mf");
        var cube = CubeQuads();
        var model = Object3mf(cube.Vertices, cube.Faces.SelectMany(f => new[] { (f[0], f[1], f[2]), (f[0], f[2], f[3]) }));
        Write3mf(deep, model, items: 1, extra: Nested("m", XmlNesting.MaxDepth + 50));
        Write3mf(ordinary, model, items: 1, extra: Nested("m", 20));

        Assert.Null(MeshPreviewLoader.TryLoad(deep, CancellationToken.None));
        Assert.Equal(12, Load(ordinary).SourceTriangles);
    }

    // Three points and one face repeated: every corner sits on a vertex of a
    // quarter million faces, which smoothing used to scan for every corner.
    [Fact]
    public async Task ARepeatedFaceShadesWithoutScanningEveryNeighbor()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "repeat.obj");
        File.WriteAllText(path, "v 0 0 0\nv 1 0 0\nv 0 1 0\n" + string.Concat(Enumerable.Repeat("f 1 2 3\n", MeshPreviewLoader.MaxRenderedTriangles)));

        var preview = await Task.Run(() => MeshPreviewLoader.TryLoad(path, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MeshPreviewLoader.MaxRenderedTriangles, preview!.SourceTriangles);
        Assert.Equal(new Vector3D(0, 0, 1), preview.Mesh.Normals[0]);
    }

    // Every vertex sits in about as many faces as still smooth, the slowest
    // shading there is; a cancel landing in it stops it.
    [Fact]
    public void ShadingStopsWhenTheLoadIsCancelled()
    {
        const int triangles = 500_000;
        var vertices = 3 * triangles / MeshPreviewLoader.MaxSmoothedFaces;
        var positions = Enumerable.Range(0, vertices).Select(i => new Vector3(i, i * i % 97, i % 13)).ToArray();
        var corners = Enumerable.Range(0, 3 * triangles).Select(i => i % vertices).ToArray();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        Assert.ThrowsAny<OperationCanceledException>(() => MeshPreviewLoader.CreaseNormals(positions, corners, cancel.Token));
    }

    private static MeshPreview Load(string path) =>
        MeshPreviewLoader.TryLoad(path, CancellationToken.None) ?? throw new Xunit.Sdk.XunitException($"{path} did not load");

    private static string Nested(string name, int depth) =>
        string.Concat(Enumerable.Repeat($"<{name}>", depth)) + string.Concat(Enumerable.Repeat($"</{name}>", depth));

    private static string Object3mf(IEnumerable<Vector3> vertices, IEnumerable<(int A, int B, int C)> triangles) =>
        "<object id=\"1\" type=\"model\"><mesh><vertices>"
        + string.Concat(vertices.Select(v => FormattableString.Invariant($"<vertex x=\"{v.X}\" y=\"{v.Y}\" z=\"{v.Z}\"/>")))
        + "</vertices><triangles>"
        + string.Concat(triangles.Select(t => $"<triangle v1=\"{t.A}\" v2=\"{t.B}\" v3=\"{t.C}\"/>"))
        + "</triangles></mesh></object>";

    /// <summary>A 3MF whose build places object 1 <paramref name="items"/> times.</summary>
    private static void Write3mf(string path, string objects, int items, string extra = "")
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("3D/3dmodel.model").Open());
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<model unit=\"millimeter\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">"
            + extra
            + $"<resources>{objects}</resources>"
            + $"<build>{string.Concat(Enumerable.Repeat("<item objectid=\"1\"/>", items))}</build>"
            + "</model>");
    }

    /// <summary>
    /// A GLB whose one mesh has <paramref name="points"/> positions and the
    /// triangle 0 1 2 <paramref name="triangles"/> times, placed by
    /// <paramref name="instances"/> scene nodes.
    /// </summary>
    private static void WriteInstancedGlb(string path, int points, int triangles, int instances)
    {
        var indexOffset = points * 12;
        var bin = new byte[(indexOffset + triangles * 6 + 3) / 4 * 4];
        for (var i = 0; i < points; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(i * 12), i);
            BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(i * 12 + 4), i % 2);
        }

        for (var t = 0; t < triangles; t++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bin.AsSpan(indexOffset + t * 6 + 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bin.AsSpan(indexOffset + t * 6 + 4), 2);
        }

        var json = $$"""
            {
              "asset": { "version": "2.0" },
              "scene": 0,
              "scenes": [ { "nodes": [{{string.Join(",", Enumerable.Range(0, instances))}}] } ],
              "nodes": [{{string.Join(",", Enumerable.Repeat("""{ "mesh": 0 }""", instances))}}],
              "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0 }, "indices": 1 } ] } ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": {{points}}, "type": "VEC3" },
                { "bufferView": 1, "componentType": 5123, "count": {{triangles * 3}}, "type": "SCALAR" }
              ],
              "bufferViews": [
                { "buffer": 0, "byteOffset": 0, "byteLength": {{indexOffset}} },
                { "buffer": 0, "byteOffset": {{indexOffset}}, "byteLength": {{triangles * 6}} }
              ],
              "buffers": [ { "byteLength": {{bin.Length}} } ]
            }
            """;
        WriteGlbChunks(path, json, bin);
    }

    private static int RenderedTriangles(MeshPreview preview) => preview.Mesh.Positions.Count / 3;

    /// <summary>Share of edges used by only one triangle, after joining equal positions.</summary>
    private static double BoundaryEdgeShare(MeshPreview preview)
    {
        var ids = new Dictionary<Point3D, int>();
        int Id(Point3D p) => ids.TryGetValue(p, out var id) ? id : ids[p] = ids.Count;
        var edges = new Dictionary<(int, int), int>();
        var positions = preview.Mesh.Positions;
        for (var i = 0; i < positions.Count; i += 3)
        {
            var corners = new[] { Id(positions[i]), Id(positions[i + 1]), Id(positions[i + 2]) };
            for (var k = 0; k < 3; k++)
            {
                var a = corners[k];
                var b = corners[(k + 1) % 3];
                var key = (Math.Min(a, b), Math.Max(a, b));
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }

        return edges.Count == 0 ? 1 : edges.Values.Count(uses => uses == 1) / (double)edges.Count;
    }

    private static void AssertExtents(MeshPreview preview, double x, double y, double z, double tolerance = 0.01)
    {
        var bounds = preview.Mesh.Bounds;
        Assert.InRange(bounds.SizeX, x - tolerance, x + tolerance);
        Assert.InRange(bounds.SizeY, y - tolerance, y + tolerance);
        Assert.InRange(bounds.SizeZ, z - tolerance, z + tolerance);
    }

    private static List<(Vector3 A, Vector3 B, Vector3 C)> CubeTriangles()
    {
        var cube = CubeQuads();
        var triangles = new List<(Vector3, Vector3, Vector3)>();
        foreach (var face in cube.Faces)
        {
            triangles.Add((cube.Vertices[face[0]], cube.Vertices[face[1]], cube.Vertices[face[2]]));
            triangles.Add((cube.Vertices[face[0]], cube.Vertices[face[2]], cube.Vertices[face[3]]));
        }

        return triangles;
    }

    private static (Vector3[] Vertices, int[][] Faces) CubeQuads() => (
        [
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
            new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)
        ],
        [
            [0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4],
            [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]
        ]);

    private static List<(Vector3 A, Vector3 B, Vector3 C)> SphereTriangles(int stacks, int slices)
    {
        Vector3 Point(int stack, int slice)
        {
            var polar = Math.PI * stack / stacks;
            var azimuth = 2 * Math.PI * (slice % slices) / slices;
            return stack == 0 ? new Vector3(0, 0, 1)
                : stack == stacks ? new Vector3(0, 0, -1)
                : new Vector3(
                    (float)(Math.Sin(polar) * Math.Cos(azimuth)),
                    (float)(Math.Sin(polar) * Math.Sin(azimuth)),
                    (float)Math.Cos(polar));
        }

        var triangles = new List<(Vector3, Vector3, Vector3)>();
        for (var stack = 0; stack < stacks; stack++)
        {
            for (var slice = 0; slice < slices; slice++)
            {
                var a = Point(stack, slice);
                var b = Point(stack + 1, slice);
                var c = Point(stack + 1, slice + 1);
                var d = Point(stack, slice + 1);
                if (stack > 0)
                {
                    triangles.Add((a, b, d));
                }

                if (stack < stacks - 1)
                {
                    triangles.Add((d, b, c));
                }
            }
        }

        return triangles;
    }

    private static void WriteBinaryStl(string path, List<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(new byte[80]);
        writer.Write((uint)triangles.Count);
        foreach (var (a, b, c) in triangles)
        {
            foreach (var v in new[] { Vector3.Zero, a, b, c })
            {
                writer.Write(v.X);
                writer.Write(v.Y);
                writer.Write(v.Z);
            }

            writer.Write((ushort)0);
        }
    }

    private static void WriteGlb(string path, (Vector3[] Vertices, int[][] Faces) mesh, string nodes)
    {
        var bin = new List<byte>();
        foreach (var v in mesh.Vertices)
        {
            bin.AddRange(BitConverter.GetBytes(v.X));
            bin.AddRange(BitConverter.GetBytes(v.Y));
            bin.AddRange(BitConverter.GetBytes(v.Z));
        }

        var indexOffset = bin.Count;
        var indexCount = 0;
        foreach (var face in mesh.Faces)
        {
            foreach (var corner in new[] { 0, 1, 2, 0, 2, 3 })
            {
                bin.AddRange(BitConverter.GetBytes((ushort)face[corner]));
                indexCount++;
            }
        }

        while (bin.Count % 4 != 0)
        {
            bin.Add(0);
        }

        var json = $$"""
            {
              "asset": { "version": "2.0" },
              "scene": 0,
              "scenes": [ { "nodes": [0] } ],
              "nodes": {{nodes}},
              "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0 }, "indices": 1 } ] } ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": {{mesh.Vertices.Length}}, "type": "VEC3" },
                { "bufferView": 1, "componentType": 5123, "count": {{indexCount}}, "type": "SCALAR" }
              ],
              "bufferViews": [
                { "buffer": 0, "byteOffset": 0, "byteLength": {{indexOffset}} },
                { "buffer": 0, "byteOffset": {{indexOffset}}, "byteLength": {{indexCount * 2}} }
              ],
              "buffers": [ { "byteLength": {{bin.Count}} } ]
            }
            """;
        WriteGlbChunks(path, json, [.. bin]);
    }

    private static void WriteGlbChunks(string path, string json, byte[] bin)
    {
        using var _ = JsonDocument.Parse(json);
        var jsonBytes = Encoding.UTF8.GetBytes(json).ToList();
        while (jsonBytes.Count % 4 != 0)
        {
            jsonBytes.Add((byte)' ');
        }

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("glTF"u8);
        writer.Write(2u);
        writer.Write((uint)(12 + 8 + jsonBytes.Count + 8 + bin.Length));
        writer.Write((uint)jsonBytes.Count);
        writer.Write("JSON"u8);
        writer.Write(jsonBytes.ToArray());
        writer.Write((uint)bin.Length);
        writer.Write("BIN\0"u8);
        writer.Write(bin);
    }

    private static string CubeStep()
    {
        var cube = CubeQuads();
        var text = new StringBuilder("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n");
        for (var i = 0; i < cube.Vertices.Length; i++)
        {
            var v = cube.Vertices[i];
            text.Append(CultureInfo.InvariantCulture, $"#{i + 1}=CARTESIAN_POINT('',({v.X:0.0},{v.Y:0.0},{v.Z:0.0}));\n");
            text.Append(CultureInfo.InvariantCulture, $"#{i + 101}=VERTEX_POINT('',#{i + 1});\n");
        }

        (int, int)[] edges = [(0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7)];
        for (var i = 0; i < edges.Length; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"#{i + 201}=EDGE_CURVE('',#{edges[i].Item1 + 101},#{edges[i].Item2 + 101},#999,.T.);\n");
        }

        return text.Append("ENDSEC;\nEND-ISO-10303-21;\n").ToString();
    }
}
