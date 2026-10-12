using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using SpacePreview.Services;
using SpacePreview.Utilities;
using Quaternion = System.Numerics.Quaternion;

namespace SpacePreview.Preview;

/// <param name="SourceTriangles">Triangles in the file; 0 for a wireframe.</param>
internal sealed record MeshPreview(
    MeshGeometry3D Mesh,
    string FormatName,
    string CountLabel,
    int SourceTriangles);

/// <summary>
/// Loads mesh formats (STL, OBJ, PLY, 3MF, GLB) into one normalized
/// <see cref="MeshGeometry3D"/> for the rotating viewport. A mesh over the
/// render cap is simplified by vertex clustering, which keeps a closed
/// coarse surface instead of a sample of loose triangles. The label keeps
/// the file's true count.
/// </summary>
internal static class MeshPreviewLoader
{
    /// <summary>The most triangles the viewport gets; Viewport3D still rotates smoothly here.</summary>
    internal const int MaxRenderedTriangles = 250_000;

    // Two ceilings, because a file can ask for geometry in two ways. GLB
    // nodes and 3MF build items place their mesh again for every instance,
    // so a small file can ask without end: what instancing places stops at
    // the placed totals. Geometry the file stores itself costs memory in
    // step with the file's own bytes, so it gets the higher stored totals,
    // which hold a scan of many millions of triangles. Past either, the
    // file gets its card.
    internal const int MaxPlacedVertices = 4_000_000;
    internal const int MaxPlacedTriangles = 4_000_000;
    internal const int MaxStoredVertices = 16_000_000;
    internal const int MaxStoredTriangles = 16_000_000;

    // A surface fills about one cell for every two triangles it keeps. Tiny
    // triangles far apart fill a cell each and keep none, so a simplifying
    // pass's table would grow with the file and not with the result. This
    // many cells for each triangle the viewport may draw is the ceiling; at
    // the viewport's own cap it is the placed vertex total.
    private const int MaxCellsPerRenderedTriangle = MaxPlacedVertices / MaxRenderedTriangles;

    private const long MaxTextMeshBytes = 256L * 1024 * 1024;
    private const long Max3MfModelBytes = 32L * 1024 * 1024;
    private const long MaxGlbJsonBytes = 64L * 1024 * 1024;
    private const int MaxPolygonCorners = 1024;
    private const int MaxPlyHeaderLineChars = 4096;

    // Faces that turn less than about 45 degrees from each other share a
    // smoothed normal; a sharper turn stays a crease.
    private const float CreaseCosine = 0.7f;

    // A sphere's pole sits in one face per slice. A vertex in more faces
    // than this is a fan or a repeat, and flat shading serves it.
    internal const int MaxSmoothedFaces = 128;

    public static MeshPreview? TryLoad(string path, CancellationToken token) =>
        TryLoad(path, MaxRenderedTriangles, token);

    internal static MeshPreview? TryLoad(string path, int triangleCap, CancellationToken token)
    {
        try
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var preview = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".stl" => TryLoadStl(path, triangleCap, token),
                ".obj" => TryLoadObj(path, triangleCap, token),
                ".ply" => TryLoadPly(path, triangleCap, token),
                ".3mf" => TryLoad3mf(path, triangleCap, token),
                ".glb" => TryLoadGlb(path, triangleCap, token),
                _ => null
            };
            if (preview is not null)
            {
                DiagnosticLog.Write(
                    $"mesh loaded {preview.SourceTriangles} -> {preview.Mesh.Positions.Count / 3} triangles in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms {path}");
            }

            return preview;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"mesh preview failed {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------- STL --

    private static MeshPreview? TryLoadStl(string path, int cap, CancellationToken token)
    {
        using var stream = OpenRead(path);
        if (TryReadBinaryStlCount(stream, out var count))
        {
            return Build(BinaryStlTriangles(stream, count), "STL mesh", cap, token);
        }

        if (stream.Length > MaxTextMeshBytes)
        {
            return null;
        }

        stream.Position = 0;
        var mesh = new IndexedMesh();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        int first = 0, second = 0, pending = 0, lines = 0;
        while (reader.ReadLine() is { } text)
        {
            if ((++lines & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var line = text.AsSpan();
            if (!NextToken(ref line, out var keyword)
                || !keyword.Equals("vertex", StringComparison.OrdinalIgnoreCase)
                || !TryReadVector(ref line, out var vertex))
            {
                continue;
            }

            var index = mesh.AddVertex(vertex);
            switch (pending++)
            {
                case 0:
                    first = index;
                    break;
                case 1:
                    second = index;
                    break;
                default:
                    mesh.AddTriangle(first, second, index);
                    pending = 0;
                    break;
            }
        }

        return Build(mesh.Triangles, "STL mesh", cap, token);
    }

    /// <summary>
    /// The triangle count a binary STL declares, when the file is long
    /// enough to hold that many. The file card asks too, for an STL that
    /// did not load.
    /// </summary>
    internal static bool TryReadBinaryStlCount(Stream stream, out uint count)
    {
        count = 0;
        if (stream.Length < 84)
        {
            return false;
        }

        stream.Position = 80;
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return count > 0 && 84L + count * 50L <= stream.Length;
    }

    // The body streams from disk on every pass, so a million-facet part
    // never has to sit in memory whole.
    private static TriangleStream BinaryStlTriangles(Stream stream, uint count) =>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)] (visit, token) =>
        {
            const int batchTriangles = 4096;
            var buffer = new byte[50 * batchTriangles];
            stream.Position = 84;
            for (var done = 0u; done < count;)
            {
                token.ThrowIfCancellationRequested();
                var batch = (int)Math.Min(batchTriangles, count - done);
                stream.ReadExactly(buffer, 0, batch * 50);
                for (var i = 0; i < batch; i++)
                {
                    var record = buffer.AsSpan(i * 50, 50);
                    VisitFinite(visit, ReadVector(record[12..]), ReadVector(record[24..]), ReadVector(record[36..]));
                }

                done += (uint)batch;
            }
        };

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector3 ReadVector(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));

    // -------------------------------------------------------------- OBJ --

    private static MeshPreview? TryLoadObj(string path, int cap, CancellationToken token)
    {
        using var stream = OpenRead(path);
        if (stream.Length > MaxTextMeshBytes)
        {
            return null;
        }

        var mesh = new IndexedMesh();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = 0;
        while (reader.ReadLine() is { } text)
        {
            if ((++lines & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var line = text.AsSpan();
            if (!NextToken(ref line, out var keyword))
            {
                continue;
            }

            if (keyword is "v")
            {
                if (TryReadVector(ref line, out var vertex))
                {
                    mesh.AddVertex(vertex);
                }
            }
            else if (keyword is "f")
            {
                // Fan-triangulate polygons; each corner is "v", "v/t", "v//n"
                // or "v/t/n" with 1-based (or negative relative) vertex indices.
                int first = -1, previous = -1, corners = 0;
                while (corners < MaxPolygonCorners && NextToken(ref line, out var corner))
                {
                    var index = ParseObjIndex(corner, mesh.VertexCount);
                    corners++;
                    if (corners == 1)
                    {
                        first = index;
                    }
                    else if (corners >= 3 && first >= 0 && previous >= 0 && index >= 0)
                    {
                        mesh.AddTriangle(first, previous, index);
                    }

                    previous = index;
                }
            }
        }

        return Build(mesh.Triangles, "OBJ mesh", cap, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ParseObjIndex(ReadOnlySpan<char> corner, int vertexCount)
    {
        var slash = corner.IndexOf('/');
        if (slash >= 0)
        {
            corner = corner[..slash];
        }

        if (!int.TryParse(corner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return -1;
        }

        var resolved = index < 0 ? vertexCount + index : index - 1;
        return resolved >= 0 && resolved < vertexCount ? resolved : -1;
    }

    // -------------------------------------------------------------- PLY --

    private sealed record PlyProperty(string Name, string Type, string? ListCountType);

    private sealed record PlyElement(string Name, int Count, List<PlyProperty> Properties);

    private static MeshPreview? TryLoadPly(string path, int cap, CancellationToken token)
    {
        using var stream = OpenRead(path);
        if (stream.Length > MaxTextMeshBytes)
        {
            return null;
        }

        // Nothing is held for a file that does not start as a PLY does.
        Span<byte> magic = stackalloc byte[3];
        if (stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) < magic.Length
            || !magic.SequenceEqual("ply"u8))
        {
            return null;
        }

        // The header is ASCII regardless of body format; read it byte-wise so
        // the stream lands at the start of a binary body.
        stream.Position = 0;
        var headerLines = new List<string>();
        var lineBuilder = new StringBuilder();
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                return null;
            }

            if (value == '\n')
            {
                var line = lineBuilder.ToString().TrimEnd('\r');
                lineBuilder.Clear();
                headerLines.Add(line);
                if (line == "end_header")
                {
                    break;
                }

                if (headerLines.Count > 500)
                {
                    return null;
                }

                continue;
            }

            // A header line is a few words. One with no end is not a header.
            if (lineBuilder.Length == MaxPlyHeaderLineChars)
            {
                return null;
            }

            lineBuilder.Append((char)value);
        }

        if (headerLines.Count == 0 || headerLines[0] != "ply")
        {
            return null;
        }

        string? format = null;
        var elements = new List<PlyElement>();
        foreach (var line in headerLines)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "format" when parts.Length >= 2:
                    format = parts[1];
                    break;
                case "element" when parts.Length >= 3 && int.TryParse(parts[2], out var count) && count >= 0:
                    elements.Add(new PlyElement(parts[1], count, []));
                    break;
                case "property" when elements.Count > 0 && parts.Length >= 3:
                    if (parts[1] == "list" && parts.Length >= 5)
                    {
                        elements[^1].Properties.Add(new PlyProperty(parts[4], parts[3], parts[2]));
                    }
                    else
                    {
                        elements[^1].Properties.Add(new PlyProperty(parts[2], parts[1], null));
                    }

                    break;
            }
        }

        if (format is not ("ascii" or "binary_little_endian"))
        {
            return null;
        }

        var mesh = new IndexedMesh();
        if (format == "ascii")
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            foreach (var element in elements)
            {
                for (var row = 0; row < element.Count; row++)
                {
                    if ((row & 4095) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    var line = reader.ReadLine();
                    if (line is null)
                    {
                        return null;
                    }

                    if (element.Name == "vertex")
                    {
                        ParsePlyAsciiVertex(line, element, mesh);
                    }
                    else if (element.Name == "face")
                    {
                        ParsePlyAsciiFace(line, mesh);
                    }
                }
            }
        }
        else
        {
            var body = new BufferedStream(stream, 1 << 16);
            using var reader = new BinaryReader(body, Encoding.ASCII, leaveOpen: true);
            var indices = new int[MaxPolygonCorners];
            foreach (var element in elements)
            {
                // The count is the file's claim. A row of no bytes holds
                // nothing, and looping over such rows would read nothing
                // however many are declared. Rows of some bytes must fit in
                // what is left of the file.
                var rowBytes = element.Count == 0 ? 0 : PlyRowBytes(element);
                if (rowBytes == 0)
                {
                    continue;
                }

                if (element.Count > (stream.Length - body.Position) / rowBytes)
                {
                    throw new InvalidDataException("PLY declares more rows than the file holds.");
                }

                for (var row = 0; row < element.Count; row++)
                {
                    if ((row & 4095) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    if (element.Name == "vertex")
                    {
                        double x = 0, y = 0, z = 0;
                        foreach (var property in element.Properties)
                        {
                            if (property.ListCountType is not null)
                            {
                                SkipPlyList(reader, property);
                                continue;
                            }

                            var value = ReadPlyScalar(reader, property.Type);
                            switch (property.Name)
                            {
                                case "x": x = value; break;
                                case "y": y = value; break;
                                case "z": z = value; break;
                            }
                        }

                        mesh.AddVertex(x, y, z);
                        continue;
                    }

                    foreach (var property in element.Properties)
                    {
                        if (property.ListCountType is null)
                        {
                            ReadPlyScalar(reader, property.Type);
                            continue;
                        }

                        var count = (int)ReadPlyScalar(reader, property.ListCountType);
                        if (count < 0 || count > MaxPolygonCorners)
                        {
                            throw new InvalidDataException("PLY face is too large for preview.");
                        }

                        for (var i = 0; i < count; i++)
                        {
                            indices[i] = (int)ReadPlyScalar(reader, property.Type);
                        }

                        if (element.Name == "face" && property.Name.Contains("vertex", StringComparison.OrdinalIgnoreCase))
                        {
                            for (var corner = 1; corner < count - 1; corner++)
                            {
                                mesh.AddTriangle(indices[0], indices[corner], indices[corner + 1]);
                            }
                        }
                    }
                }
            }
        }

        return Build(mesh.Triangles, "PLY mesh", cap, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ParsePlyAsciiVertex(string line, PlyElement element, IndexedMesh mesh)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        double x = 0, y = 0, z = 0;
        var column = 0;
        foreach (var property in element.Properties)
        {
            if (column >= parts.Length)
            {
                break;
            }

            if (property.ListCountType is not null)
            {
                if (int.TryParse(parts[column], out var listCount))
                {
                    column += 1 + listCount;
                }

                continue;
            }

            if (double.TryParse(parts[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                switch (property.Name)
                {
                    case "x": x = value; break;
                    case "y": y = value; break;
                    case "z": z = value; break;
                }
            }

            column++;
        }

        mesh.AddVertex(x, y, z);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ParsePlyAsciiFace(string line, IndexedMesh mesh)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !int.TryParse(parts[0], out var count) || count < 3 || count > MaxPolygonCorners || parts.Length < count + 1)
        {
            return;
        }

        Span<int> indices = stackalloc int[count];
        for (var i = 0; i < count; i++)
        {
            if (!int.TryParse(parts[i + 1], out indices[i]))
            {
                return;
            }
        }

        for (var corner = 1; corner < count - 1; corner++)
        {
            mesh.AddTriangle(indices[0], indices[corner], indices[corner + 1]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SkipPlyList(BinaryReader reader, PlyProperty property)
    {
        var count = (int)ReadPlyScalar(reader, property.ListCountType!);
        if (count < 0 || count > MaxPolygonCorners)
        {
            throw new InvalidDataException("PLY list is too large for preview.");
        }

        for (var i = 0; i < count; i++)
        {
            ReadPlyScalar(reader, property.Type);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double ReadPlyScalar(BinaryReader reader, string type) => type switch
    {
        "char" or "int8" => reader.ReadSByte(),
        "uchar" or "uint8" => reader.ReadByte(),
        "short" or "int16" => reader.ReadInt16(),
        "ushort" or "uint16" => reader.ReadUInt16(),
        "int" or "int32" => reader.ReadInt32(),
        "uint" or "uint32" => reader.ReadUInt32(),
        "float" or "float32" => reader.ReadSingle(),
        "double" or "float64" => reader.ReadDouble(),
        _ => throw new InvalidDataException($"Unknown PLY type {type}")
    };

    /// <summary>The fewest bytes one binary row takes: every scalar, and each list's count.</summary>
    private static int PlyRowBytes(PlyElement element) =>
        element.Properties.Sum(property => (property.ListCountType ?? property.Type) switch
        {
            "char" or "int8" or "uchar" or "uint8" => 1,
            "short" or "int16" or "ushort" or "uint16" => 2,
            "int" or "int32" or "uint" or "uint32" or "float" or "float32" => 4,
            "double" or "float64" => 8,
            var type => throw new InvalidDataException($"Unknown PLY type {type}")
        });

    // -------------------------------------------------------------- GLB --

    /// <summary>
    /// glTF binary: a JSON chunk naming scenes, nodes, meshes and accessors,
    /// then a BIN chunk with their bytes. Every triangle primitive the
    /// default scene places is read with its node transform; materials,
    /// textures and animation are not. A skinned mesh shows its bind pose.
    /// glTF is Y-up and the viewport Z-up, so Y and Z trade places.
    /// </summary>
    private static MeshPreview? TryLoadGlb(string path, int cap, CancellationToken token)
    {
        using var stream = OpenRead(path);
        Span<byte> header = stackalloc byte[20];
        if (stream.Length < header.Length)
        {
            return null;
        }

        stream.ReadExactly(header);
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (!header[..4].SequenceEqual("glTF"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 2
            || !header[16..].SequenceEqual("JSON"u8)
            || jsonLength > MaxGlbJsonBytes
            || header.Length + 8L + jsonLength > stream.Length)
        {
            return null;
        }

        var json = new byte[jsonLength];
        stream.ReadExactly(json);
        Span<byte> chunk = stackalloc byte[8];
        stream.ReadExactly(chunk);
        var binLength = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        var binStart = stream.Position;
        if (!chunk[4..].SequenceEqual("BIN\0"u8) || binStart + binLength > stream.Length)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var gltf = new Gltf(document.RootElement, stream, binStart, binLength, new IndexedMesh(), token);
        if (gltf.Root.TryGetProperty("meshes", out var meshes) && meshes.ValueKind == JsonValueKind.Array)
        {
            if (TryGetSceneRoots(gltf.Root, out var roots))
            {
                var visits = 0;
                foreach (var root in roots)
                {
                    EmitGltfNode(gltf, root, Matrix4x4.Identity, depth: 0, ref visits);
                }
            }

            if (gltf.Mesh.TriangleCount == 0)
            {
                // No scene to place them: show every mesh where it lies.
                for (var index = 0; index < meshes.GetArrayLength(); index++)
                {
                    EmitGltfMesh(gltf, index, Matrix4x4.Identity);
                }
            }
        }

        return Build(gltf.Mesh.Triangles, "glTF model", cap, token);
    }

    private sealed record Gltf(
        JsonElement Root,
        Stream Stream,
        long BinStart,
        long BinLength,
        IndexedMesh Mesh,
        CancellationToken Token);

    private static bool TryGetSceneRoots(JsonElement root, out List<int> roots)
    {
        roots = [];
        if (!root.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var sceneIndex = GetInt(root, "scene", 0);
        if (sceneIndex < 0 || sceneIndex >= scenes.GetArrayLength()
            || !scenes[sceneIndex].TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetInt32(out var index))
            {
                roots.Add(index);
            }
        }

        return true;
    }

    private static void EmitGltfNode(Gltf gltf, int index, Matrix4x4 parent, int depth, ref int visits)
    {
        // Nodes form a forest, but a malformed file can loop or share; both
        // limits keep a hostile graph from running away.
        if (depth > 64 || ++visits > 100_000
            || !gltf.Root.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array
            || index < 0 || index >= nodes.GetArrayLength())
        {
            return;
        }

        gltf.Token.ThrowIfCancellationRequested();
        var node = nodes[index];
        var world = GltfNodeMatrix(node) * parent;
        var meshIndex = GetInt(node, "mesh", -1);
        if (meshIndex >= 0)
        {
            // A skinned mesh ignores its node's transform; its vertices
            // already sit in the bind pose.
            EmitGltfMesh(gltf, meshIndex, node.TryGetProperty("skin", out _) ? Matrix4x4.Identity : world);
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                if (child.TryGetInt32(out var childIndex))
                {
                    EmitGltfNode(gltf, childIndex, world, depth + 1, ref visits);
                }
            }
        }
    }

    private static Matrix4x4 GltfNodeMatrix(JsonElement node)
    {
        // glTF matrices are column-major for column vectors; read in order
        // they are the row-major, row-vector matrix System.Numerics uses.
        if (ReadFloats(node, "matrix", 16) is { } m)
        {
            return new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        var scale = ReadFloats(node, "scale", 3) is { } s ? new Vector3(s[0], s[1], s[2]) : Vector3.One;
        var rotation = ReadFloats(node, "rotation", 4) is { } r ? new Quaternion(r[0], r[1], r[2], r[3]) : Quaternion.Identity;
        var translation = ReadFloats(node, "translation", 3) is { } t ? new Vector3(t[0], t[1], t[2]) : Vector3.Zero;
        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation))
            * Matrix4x4.CreateTranslation(translation);
    }

    private static void EmitGltfMesh(Gltf gltf, int meshIndex, Matrix4x4 transform)
    {
        if (!gltf.Root.TryGetProperty("meshes", out var meshes)
            || meshIndex >= meshes.GetArrayLength()
            || !meshes[meshIndex].TryGetProperty("primitives", out var primitives)
            || primitives.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // A mirroring transform turns the winding inside out; swap it back
        // so normals still face out.
        var mirrored = transform.GetDeterminant() < 0;
        foreach (var primitive in primitives.EnumerateArray())
        {
            var mode = GetInt(primitive, "mode", 4);
            if (mode is not (4 or 5 or 6)
                || !primitive.TryGetProperty("attributes", out var attributes)
                || ReadGltfPositions(gltf, GetInt(attributes, "POSITION", -1)) is not { } positions)
            {
                continue;
            }

            int[]? indices = null;
            if (primitive.TryGetProperty("indices", out _))
            {
                indices = ReadGltfIndices(gltf, GetInt(primitive, "indices", -1));
                if (indices is null)
                {
                    continue;
                }
            }

            var mesh = gltf.Mesh;
            var corners = indices?.Length ?? positions.Length;
            Place(mesh, positions.Length, mode == 4 ? corners / 3 : Math.Max(0, corners - 2));
            var first = mesh.VertexCount;
            foreach (var position in positions)
            {
                var placed = Vector3.Transform(position, transform);
                mesh.AddVertex(new Vector3(placed.X, -placed.Z, placed.Y));
            }

            int Corner(int i) => first + (indices is null ? i : indices[i]);
            void Add(int a, int b, int c)
            {
                if (mirrored)
                {
                    mesh.AddTriangle(Corner(a), Corner(c), Corner(b));
                }
                else
                {
                    mesh.AddTriangle(Corner(a), Corner(b), Corner(c));
                }
            }

            switch (mode)
            {
                case 4:
                    for (var i = 0; i + 2 < corners; i += 3)
                    {
                        Add(i, i + 1, i + 2);
                    }

                    break;
                case 5:
                    for (var i = 0; i + 2 < corners; i++)
                    {
                        if ((i & 1) == 0)
                        {
                            Add(i, i + 1, i + 2);
                        }
                        else
                        {
                            Add(i + 1, i, i + 2);
                        }
                    }

                    break;
                case 6:
                    for (var i = 1; i + 1 < corners; i++)
                    {
                        Add(0, i, i + 1);
                    }

                    break;
            }
        }
    }

    private static Vector3[]? ReadGltfPositions(Gltf gltf, int accessorIndex)
    {
        // Float VEC3 only: quantized or Draco-compressed positions have no
        // readable float data here, and that primitive is skipped.
        var room = MaxPlacedVertices - gltf.Mesh.VertexCount;
        if (ReadGltfAccessor(gltf, accessorIndex, "VEC3", room, out var componentType, out var count, out var stride, out var bytes)
            && componentType == 5126)
        {
            var positions = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                positions[i] = ReadVector(bytes.AsSpan(i * stride, 12));
            }

            return positions;
        }

        return null;
    }

    private static int[]? ReadGltfIndices(Gltf gltf, int accessorIndex)
    {
        // A strip or fan makes a triangle per index past its first two.
        var room = 3L * (MaxPlacedTriangles - gltf.Mesh.TriangleCount) + 2;
        if (!ReadGltfAccessor(gltf, accessorIndex, "SCALAR", room, out var componentType, out var count, out var stride, out var bytes))
        {
            return null;
        }

        var indices = new int[count];
        for (var i = 0; i < count; i++)
        {
            var at = bytes.AsSpan(i * stride);
            indices[i] = componentType switch
            {
                5121 => at[0],
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(at),
                5125 => (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(at), int.MaxValue),
                _ => -1
            };
        }

        return componentType is 5121 or 5123 or 5125 ? indices : null;
    }

    /// <summary>
    /// The bytes behind one accessor, sliced from the BIN chunk: element i
    /// starts at i * stride. Sparse accessors and external buffers are not
    /// read. More than <paramref name="room"/> elements refuses the file
    /// before anything is allocated.
    /// </summary>
    private static bool ReadGltfAccessor(
        Gltf gltf,
        int accessorIndex,
        string type,
        long room,
        out int componentType,
        out int count,
        out int stride,
        out byte[] bytes)
    {
        componentType = 0;
        count = 0;
        stride = 0;
        bytes = [];
        if (!gltf.Root.TryGetProperty("accessors", out var accessors)
            || accessorIndex < 0 || accessorIndex >= accessors.GetArrayLength())
        {
            return false;
        }

        var accessor = accessors[accessorIndex];
        componentType = GetInt(accessor, "componentType", 0);
        count = GetInt(accessor, "count", 0);
        var viewIndex = GetInt(accessor, "bufferView", -1);
        if (count <= 0
            || accessor.TryGetProperty("sparse", out _)
            || !accessor.TryGetProperty("type", out var typeName)
            || typeName.GetString() != type
            || !gltf.Root.TryGetProperty("bufferViews", out var views)
            || viewIndex < 0 || viewIndex >= views.GetArrayLength())
        {
            return false;
        }

        var view = views[viewIndex];
        var componentBytes = componentType switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => 0
        };
        var elementBytes = componentBytes * (type == "VEC3" ? 3 : 1);
        var declaredStride = GetInt(view, "byteStride", 0);
        stride = declaredStride > 0 ? declaredStride : elementBytes;
        long viewOffset = GetInt(view, "byteOffset", 0);
        long viewLength = GetInt(view, "byteLength", 0);
        long accessorOffset = GetInt(accessor, "byteOffset", 0);
        var span = (count - 1L) * stride + elementBytes;
        if (elementBytes == 0
            || GetInt(view, "buffer", 0) != 0
            || stride < elementBytes
            || viewOffset < 0 || accessorOffset < 0
            || viewOffset + viewLength > gltf.BinLength
            || accessorOffset + span > viewLength
            || span > int.MaxValue)
        {
            return false;
        }

        gltf.Token.ThrowIfCancellationRequested();
        if (count > room)
        {
            throw new InvalidDataException(TooMuchGeometry);
        }

        bytes = new byte[span];
        gltf.Stream.Position = gltf.BinStart + viewOffset + accessorOffset;
        gltf.Stream.ReadExactly(bytes);
        return true;
    }

    private static int GetInt(JsonElement element, string name, int fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static float[]? ReadFloats(JsonElement element, string name, int length)
    {
        if (!element.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() != length)
        {
            return null;
        }

        var values = new float[length];
        for (var i = 0; i < length; i++)
        {
            if (!array[i].TryGetSingle(out values[i]))
            {
                return null;
            }
        }

        return values;
    }

    // ------------------------------------------------------------- STEP --

    /// <summary>
    /// STEP is a B-rep format: shaded surfaces need a CAD kernel, its edge
    /// graph does not. EDGE_CURVE entities name their two vertices and curve,
    /// so lines, circles, and arcs resolve into a wireframe that sketches the
    /// part honestly. Assemblies place components through SHAPE_REPRESENTATION
    /// relationships (ITEM_DEFINED_TRANSFORMATION), so parts land where the
    /// assembly puts them, not at their local origins. Splines draw their
    /// control polygon; anything unknown draws the segment between endpoints.
    /// </summary>
    public static MeshPreview? TryLoadStepOutline(string path, CancellationToken token)
    {
        try
        {
            var entities = ParseStepEntities(path, token);
            if (entities.Count == 0)
            {
                return null;
            }

            // Local polyline per edge, built once; assembly instances reuse
            // it under their own placement transforms.
            var edgeCache = new Dictionary<int, List<Point3D>?>();

            List<Point3D>? GetEdgePolyline(int edgeId)
            {
                if (edgeCache.TryGetValue(edgeId, out var cached))
                {
                    return cached;
                }

                List<Point3D>? polyline = null;
                if (entities.TryGetValue(edgeId, out var edge))
                {
                    var refs = ExtractRefs(edge.Args);
                    if (refs.Count >= 3)
                    {
                        var start = ResolveStepPoint(entities, refs[0]);
                        var end = ResolveStepPoint(entities, refs[1]);
                        polyline = BuildStepEdge(entities, refs[2], start, end);
                    }
                }

                edgeCache[edgeId] = polyline;
                return polyline;
            }

            var polylines = new List<List<Point3D>>();
            var relationships = CollectStepRelationships(entities);
            if (relationships.Count > 0)
            {
                EmitAssembly(entities, relationships, GetEdgePolyline, polylines, token);
            }

            if (polylines.Count < 8)
            {
                // No usable assembly structure: draw every edge where it lies.
                polylines.Clear();
                foreach (var (id, entity) in entities)
                {
                    if (entity.Type.Equals("EDGE_CURVE", StringComparison.OrdinalIgnoreCase)
                        && GetEdgePolyline(id) is { Count: > 1 } polyline)
                    {
                        polylines.Add(polyline);
                    }
                }
            }

            if (polylines.Count < 8)
            {
                return null;
            }

            // Bounds drive both the viewport normalization and the strut
            // thickness that keeps the wireframe legible at any scale.
            var extent = Rect3D.Empty;
            var totalSegments = 0;
            foreach (var polyline in polylines)
            {
                totalSegments += polyline.Count - 1;
                foreach (var point in polyline)
                {
                    extent.Union(point);
                }
            }

            var maxDimension = Math.Max(extent.SizeX, Math.Max(extent.SizeY, extent.SizeZ));
            if (maxDimension <= 0 || !double.IsFinite(maxDimension))
            {
                return null;
            }

            // Each segment is a 3-sided strut (6 triangles); whole edges get
            // sampled out when the model would blow the budget. Struts are
            // cheap flat geometry, so the wireframe affords a deeper budget
            // than a shaded mesh before rotation stutters.
            const int trianglesPerSegment = 6;
            const int wireframeTriangleBudget = 120_000;
            var step = Math.Max(1, (int)Math.Ceiling(totalSegments * (double)trianglesPerSegment / wireframeTriangleBudget));
            var thickness = maxDimension / 700;
            var mesh = new IndexedMesh();
            var drawnEdges = 0;
            for (var index = 0; index < polylines.Count; index += step)
            {
                if ((drawnEdges & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                var polyline = polylines[index];
                for (var i = 0; i < polyline.Count - 1; i++)
                {
                    AddStrut(mesh, polyline[i], polyline[i + 1], thickness);
                }

                drawnEdges++;
            }

            return Build(
                mesh.Triangles,
                "STEP wireframe",
                int.MaxValue,
                token,
                drawnEdges < polylines.Count
                    ? $"{drawnEdges:N0} of {polylines.Count:N0} edges"
                    : $"{polylines.Count:N0} edges");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"step wireframe preview failed {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private sealed record StepRelationship(int Child, int Parent, Matrix3D Transform);

    /// <summary>
    /// Walks the assembly: every root representation (never a child) emits
    /// its own edges, then each component rep transformed into place,
    /// recursively for sub-assemblies.
    /// </summary>
    private static void EmitAssembly(
        Dictionary<int, (string Type, string Args)> entities,
        List<StepRelationship> relationships,
        Func<int, List<Point3D>?> getEdgePolyline,
        List<List<Point3D>> polylines,
        CancellationToken token)
    {
        var representations = new List<int>();
        foreach (var (id, entity) in entities)
        {
            if (entity.Type.Contains("SHAPE_REPRESENTATION", StringComparison.OrdinalIgnoreCase)
                && !entity.Type.Contains("RELATIONSHIP", StringComparison.OrdinalIgnoreCase))
            {
                representations.Add(id);
            }
        }

        var edgesByRep = new Dictionary<int, List<int>>();
        foreach (var rep in representations)
        {
            edgesByRep[rep] = CollectRepresentationEdges(entities, rep, token);
        }

        var childSet = new HashSet<int>();
        var childrenByParent = new Dictionary<int, List<StepRelationship>>();
        foreach (var relationship in relationships)
        {
            childSet.Add(relationship.Child);
            if (!childrenByParent.TryGetValue(relationship.Parent, out var list))
            {
                childrenByParent[relationship.Parent] = list = [];
            }

            list.Add(relationship);
        }

        var emitted = 0;
        var calls = 0;

        void Emit(int rep, Matrix3D transform, bool isIdentity, int depth)
        {
            // The call cap bounds pathological relationship graphs (high
            // fan-out, few edges) that the emitted-edge cap alone would not.
            if (depth > 24 || emitted > 80_000 || ++calls > 20_000)
            {
                return;
            }

            if (edgesByRep.TryGetValue(rep, out var edges))
            {
                foreach (var edgeId in edges)
                {
                    var local = getEdgePolyline(edgeId);
                    if (local is null)
                    {
                        continue;
                    }

                    if (isIdentity)
                    {
                        polylines.Add(local);
                    }
                    else
                    {
                        var placed = new List<Point3D>(local.Count);
                        foreach (var point in local)
                        {
                            placed.Add(transform.Transform(point));
                        }

                        polylines.Add(placed);
                    }

                    emitted++;
                }
            }

            if (childrenByParent.TryGetValue(rep, out var children))
            {
                foreach (var child in children)
                {
                    var combined = isIdentity ? child.Transform : Matrix3D.Multiply(child.Transform, transform);
                    Emit(child.Child, combined, isIdentity && child.Transform.IsIdentity, depth + 1);
                }
            }
        }

        // Roots are the assembly-side representations: parents that are not
        // themselves placed as children. Representations outside the
        // relationship graph are deliberately not drawn - they carry the
        // same solids the assembly already places (a CAD viewer shows the
        // product tree, not every stray representation).
        foreach (var rep in representations)
        {
            if (childrenByParent.ContainsKey(rep) && !childSet.Contains(rep))
            {
                Emit(rep, Matrix3D.Identity, isIdentity: true, depth: 0);
            }
        }
    }

    /// <summary>
    /// The EDGE_CURVEs a representation actually contains, via breadth-first
    /// reference walk (solid -> shell -> face -> loop -> edge). Other
    /// representations are not entered: components arrive through
    /// relationships with their placement, never by containment.
    /// </summary>
    private static List<int> CollectRepresentationEdges(
        Dictionary<int, (string Type, string Args)> entities,
        int repId,
        CancellationToken token)
    {
        var edges = new List<int>();
        var visited = new HashSet<int> { repId };
        var queue = new Queue<int>();
        foreach (var reference in ExtractRefs(entities[repId].Args))
        {
            if (visited.Add(reference))
            {
                queue.Enqueue(reference);
            }
        }

        // Every representation walks again whatever it shares with the
        // others, so the walks together can far outlast the parse.
        var walked = 0;
        while (queue.Count > 0)
        {
            if ((walked++ & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var id = queue.Dequeue();
            if (!entities.TryGetValue(id, out var entity))
            {
                continue;
            }

            if (entity.Type.Equals("EDGE_CURVE", StringComparison.OrdinalIgnoreCase))
            {
                edges.Add(id);
                continue;
            }

            if (entity.Type.Contains("SHAPE_REPRESENTATION", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var reference in ExtractRefs(entity.Args))
            {
                if (visited.Add(reference))
                {
                    queue.Enqueue(reference);
                }
            }
        }

        return edges;
    }

    /// <summary>
    /// Finds SHAPE_REPRESENTATION_RELATIONSHIPs, including the complex
    /// (multi-typed) form that carries REPRESENTATION_RELATIONSHIP_WITH_
    /// TRANSFORMATION. A relationship whose transformation cannot be
    /// resolved is dropped: placing a part wrongly is worse than the flat
    /// fallback.
    /// </summary>
    private static List<StepRelationship> CollectStepRelationships(Dictionary<int, (string Type, string Args)> entities)
    {
        var relationships = new List<StepRelationship>();
        foreach (var (type, args) in entities.Values)
        {
            string? relationArgs;
            string? transformArgs = null;
            if (type == "*")
            {
                if (!args.Contains("SHAPE_REPRESENTATION_RELATIONSHIP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                relationArgs = ExtractSegmentArgs(args, "REPRESENTATION_RELATIONSHIP(");
                transformArgs = ExtractSegmentArgs(args, "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(");
            }
            else if (type.Equals("SHAPE_REPRESENTATION_RELATIONSHIP", StringComparison.OrdinalIgnoreCase))
            {
                relationArgs = args;
            }
            else
            {
                continue;
            }

            if (relationArgs is null)
            {
                continue;
            }

            var refs = ExtractRefs(relationArgs);
            if (refs.Count < 2)
            {
                continue;
            }

            var transform = Matrix3D.Identity;
            if (transformArgs is not null)
            {
                var transformRefs = ExtractRefs(transformArgs);
                if (transformRefs.Count == 0 || BuildItemTransform(entities, transformRefs[0]) is not { } resolved)
                {
                    continue;
                }

                transform = resolved;
            }

            relationships.Add(new StepRelationship(refs[0], refs[1], transform));
        }

        return relationships;
    }

    /// <summary>Args of the named segment inside a complex instance body.</summary>
    private static string? ExtractSegmentArgs(string body, string marker)
    {
        var searchFrom = 0;
        while (true)
        {
            var index = body.IndexOf(marker, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            // Boundary: "REPRESENTATION_RELATIONSHIP(" must not match inside
            // "SHAPE_REPRESENTATION_RELATIONSHIP(".
            if (index > 0 && (char.IsLetterOrDigit(body[index - 1]) || body[index - 1] == '_'))
            {
                searchFrom = index + marker.Length;
                continue;
            }

            var start = index + marker.Length;
            var close = body.IndexOf(')', start);
            return close < 0 ? null : body[start..close];
        }
    }

    /// <summary>
    /// ITEM_DEFINED_TRANSFORMATION('', '', #from, #to): geometry expressed
    /// against the 'from' frame is placed at the 'to' frame.
    /// </summary>
    private static Matrix3D? BuildItemTransform(Dictionary<int, (string Type, string Args)> entities, int id)
    {
        if (!entities.TryGetValue(id, out var entity)
            || !entity.Type.Equals("ITEM_DEFINED_TRANSFORMATION", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var refs = ExtractRefs(entity.Args);
        if (refs.Count < 2
            || BuildPlacementBasis(entities, refs[0]) is not { } from
            || BuildPlacementBasis(entities, refs[1]) is not { } to)
        {
            return null;
        }

        if (!from.HasInverse)
        {
            return null;
        }

        from.Invert();
        return Matrix3D.Multiply(from, to);
    }

    /// <summary>Local-to-world matrix of an AXIS2_PLACEMENT_3D (origin, z axis, x reference).</summary>
    private static Matrix3D? BuildPlacementBasis(Dictionary<int, (string Type, string Args)> entities, int placementId)
    {
        if (!entities.TryGetValue(placementId, out var placement)
            || !placement.Type.Equals("AXIS2_PLACEMENT_3D", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var refs = ExtractRefs(placement.Args);
        if (refs.Count < 1 || ResolveStepPoint(entities, refs[0]) is not { } origin)
        {
            return null;
        }

        var z = (refs.Count > 1 ? ResolveStepDirection(entities, refs[1]) : null) ?? new Vector3D(0, 0, 1);
        if (z.LengthSquared < 1e-12)
        {
            z = new Vector3D(0, 0, 1);
        }

        z.Normalize();
        var x = (refs.Count > 2 ? ResolveStepDirection(entities, refs[2]) : null)
            ?? Vector3D.CrossProduct(z, Math.Abs(z.Z) < 0.9 ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0));
        x -= Vector3D.DotProduct(x, z) * z;
        if (x.LengthSquared < 1e-12)
        {
            x = Vector3D.CrossProduct(z, Math.Abs(z.Z) < 0.9 ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0));
        }

        x.Normalize();
        var y = Vector3D.CrossProduct(z, x);

        return new Matrix3D(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            origin.X, origin.Y, origin.Z, 1);
    }

    /// <summary>
    /// Reads the Part 21 DATA section into id -> (type, args). Statements
    /// can span lines. Complex (multi-type) instances are kept under the
    /// type "*" with their full body, for the relationship scan.
    /// </summary>
    private static Dictionary<int, (string Type, string Args)> ParseStepEntities(string path, CancellationToken token)
    {
        const long maxReadBytes = 64 * 1024 * 1024;
        var entities = new Dictionary<int, (string, string)>();
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maxReadBytes)
        {
            // Avoid showing a partial wireframe from a truncated read.
            return entities;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var statement = new StringBuilder();
        var lines = 0;
        while (reader.ReadLine() is { } line)
        {
            if ((++lines & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            statement.Append(line);
            if (!line.TrimEnd().EndsWith(';'))
            {
                continue;
            }

            var text = statement.ToString();
            statement.Clear();
            if (text.Length < 4 || text[0] != '#')
            {
                continue;
            }

            var equalsIndex = text.IndexOf('=');
            var open = text.IndexOf('(');
            if (equalsIndex < 0 || open < 0 || open < equalsIndex)
            {
                continue;
            }

            if (!int.TryParse(text.AsSpan(1, equalsIndex - 1).Trim(), out var id))
            {
                continue;
            }

            var close = text.LastIndexOf(')');
            if (close <= open)
            {
                continue;
            }

            var type = text[(equalsIndex + 1)..open].Trim();
            entities[id] = type.Length == 0
                ? ("*", text[(open + 1)..close])
                : (type, text[(open + 1)..close]);
        }

        return entities;
    }

    private static List<int> ExtractRefs(string args)
    {
        var refs = new List<int>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != '#')
            {
                continue;
            }

            var j = i + 1;
            var value = 0;
            while (j < args.Length && char.IsAsciiDigit(args[j]))
            {
                value = value * 10 + (args[j] - '0');
                j++;
            }

            if (j > i + 1)
            {
                refs.Add(value);
            }

            i = j - 1;
        }

        return refs;
    }

    /// <summary>VERTEX_POINT -> CARTESIAN_POINT -> Point3D (or direct point).</summary>
    private static Point3D? ResolveStepPoint(Dictionary<int, (string Type, string Args)> entities, int id)
    {
        for (var hop = 0; hop < 3; hop++)
        {
            if (!entities.TryGetValue(id, out var entity))
            {
                return null;
            }

            if (entity.Type.Equals("CARTESIAN_POINT", StringComparison.OrdinalIgnoreCase))
            {
                return ParseStepTriple(entity.Args) is { } triple ? new Point3D(triple.X, triple.Y, triple.Z) : null;
            }

            var refs = ExtractRefs(entity.Args);
            if (refs.Count == 0)
            {
                return null;
            }

            id = refs[0];
        }

        return null;
    }

    private static (double X, double Y, double Z)? ParseStepTriple(string args)
    {
        var open = args.IndexOf('(');
        var close = open < 0 ? -1 : args.IndexOf(')', open + 1);
        if (close < 0)
        {
            return null;
        }

        var list = args.AsSpan(open + 1, close - open - 1);
        Span<Range> parts = stackalloc Range[4];
        var count = list.Split(parts, ',', StringSplitOptions.TrimEntries);
        if (count != 3
            || !double.TryParse(list[parts[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(list[parts[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(list[parts[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return null;
        }

        return (x, y, z);
    }

    /// <summary>The polyline for one edge, by its underlying curve type.</summary>
    private static List<Point3D>? BuildStepEdge(
        Dictionary<int, (string Type, string Args)> entities,
        int curveId,
        Point3D? start,
        Point3D? end)
    {
        entities.TryGetValue(curveId, out var curve);

        if (curve.Type is not null && curve.Type.StartsWith("B_SPLINE_CURVE", StringComparison.OrdinalIgnoreCase))
        {
            // The control polygon hugs the spline closely enough to sketch it.
            var controlPoints = new List<Point3D>();
            foreach (var reference in ExtractRefs(curve.Args))
            {
                if (entities.TryGetValue(reference, out var candidate)
                    && candidate.Type.Equals("CARTESIAN_POINT", StringComparison.OrdinalIgnoreCase)
                    && ParseStepTriple(candidate.Args) is { } triple)
                {
                    controlPoints.Add(new Point3D(triple.X, triple.Y, triple.Z));
                }
            }

            if (controlPoints.Count > 1)
            {
                return controlPoints;
            }
        }

        if (curve.Type is not null && curve.Type.Equals("CIRCLE", StringComparison.OrdinalIgnoreCase))
        {
            var arc = BuildStepArc(entities, curve.Args, start, end);
            if (arc is not null)
            {
                return arc;
            }
        }

        // LINE, unknown, or complex-instance curves: the straight segment
        // between the edge's vertices.
        if (start is { } straightStart && end is { } straightEnd)
        {
            return [straightStart, straightEnd];
        }

        return null;
    }

    private static List<Point3D>? BuildStepArc(
        Dictionary<int, (string Type, string Args)> entities,
        string circleArgs,
        Point3D? start,
        Point3D? end)
    {
        // CIRCLE('', #axis2placement, radius)
        var refs = ExtractRefs(circleArgs);
        if (refs.Count == 0)
        {
            return null;
        }

        var lastComma = circleArgs.LastIndexOf(',');
        if (lastComma < 0 || !double.TryParse(
                circleArgs.AsSpan(lastComma + 1).Trim().TrimEnd(')'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var radius)
            || radius <= 0 || !double.IsFinite(radius))
        {
            return null;
        }

        if (!entities.TryGetValue(refs[0], out var placement)
            || !placement.Type.Equals("AXIS2_PLACEMENT_3D", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // AXIS2_PLACEMENT_3D('', #location, #axis(z), #refDirection(x))
        var placementRefs = ExtractRefs(placement.Args);
        if (placementRefs.Count < 1 || ResolveStepPoint(entities, placementRefs[0]) is not { } center)
        {
            return null;
        }

        var zAxis = placementRefs.Count > 1 ? ResolveStepDirection(entities, placementRefs[1]) : null;
        var xAxis = placementRefs.Count > 2 ? ResolveStepDirection(entities, placementRefs[2]) : null;
        var z = zAxis ?? new Vector3D(0, 0, 1);
        if (z.LengthSquared < 1e-12)
        {
            z = new Vector3D(0, 0, 1);
        }

        z.Normalize();
        var x = xAxis ?? Vector3D.CrossProduct(z, Math.Abs(z.Z) < 0.9 ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0));
        x -= Vector3D.DotProduct(x, z) * z;
        if (x.LengthSquared < 1e-12)
        {
            return null;
        }

        x.Normalize();
        var y = Vector3D.CrossProduct(z, x);

        // The curve parameter runs counterclockwise around +z from +x; an
        // edge covers the arc from its start angle to its end angle in that
        // direction. Coincident vertices mean the full circle.
        var startAngle = 0.0;
        var sweep = Math.Tau;
        if (start is { } arcStart && end is { } arcEnd && (arcStart - arcEnd).LengthSquared > 1e-12)
        {
            startAngle = Math.Atan2(Vector3D.DotProduct(arcStart - center, y), Vector3D.DotProduct(arcStart - center, x));
            var endAngle = Math.Atan2(Vector3D.DotProduct(arcEnd - center, y), Vector3D.DotProduct(arcEnd - center, x));
            sweep = endAngle - startAngle;
            if (sweep <= 1e-9)
            {
                sweep += Math.Tau;
            }
        }

        var segments = Math.Clamp((int)Math.Ceiling(sweep / (Math.Tau / 24)), 2, 24);
        var polyline = new List<Point3D>(segments + 1);
        for (var i = 0; i <= segments; i++)
        {
            var angle = startAngle + sweep * i / segments;
            polyline.Add(center + radius * Math.Cos(angle) * x + radius * Math.Sin(angle) * y);
        }

        return polyline;
    }

    private static Vector3D? ResolveStepDirection(Dictionary<int, (string Type, string Args)> entities, int id)
    {
        if (!entities.TryGetValue(id, out var entity)
            || !entity.Type.Equals("DIRECTION", StringComparison.OrdinalIgnoreCase)
            || ParseStepTriple(entity.Args) is not { } triple)
        {
            return null;
        }

        return new Vector3D(triple.X, triple.Y, triple.Z);
    }

    /// <summary>A thin 3-sided prism between two points; visible from every angle.</summary>
    private static void AddStrut(IndexedMesh mesh, Point3D from, Point3D to, double thickness)
    {
        var axis = to - from;
        if (axis.LengthSquared < 1e-18)
        {
            return;
        }

        var direction = axis;
        direction.Normalize();
        var reference = Math.Abs(direction.Z) < 0.9 ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0);
        var u = Vector3D.CrossProduct(direction, reference);
        u.Normalize();
        var v = Vector3D.CrossProduct(direction, u);

        Span<Vector3D> offsets =
        [
            u * thickness,
            (-0.5 * u + 0.866 * v) * thickness,
            (-0.5 * u - 0.866 * v) * thickness
        ];

        for (var i = 0; i < 3; i++)
        {
            var cornerA = from + offsets[i];
            var cornerB = from + offsets[(i + 1) % 3];
            var cornerC = to + offsets[i];
            var cornerD = to + offsets[(i + 1) % 3];
            mesh.AddTriangle(ToVector(cornerA), ToVector(cornerB), ToVector(cornerC));
            mesh.AddTriangle(ToVector(cornerB), ToVector(cornerD), ToVector(cornerC));
        }
    }

    // -------------------------------------------------------------- 3MF --

    private static MeshPreview? TryLoad3mf(string path, int cap, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(path);
        var modelEntry = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
        if (modelEntry is null || modelEntry.Length > Max3MfModelBytes)
        {
            return null;
        }

        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            MaxCharactersInDocument = Max3MfModelBytes
        };
        var document = XmlNesting.Load(modelEntry.Open, settings, LoadOptions.None);
        token.ThrowIfCancellationRequested();
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var objects = new Dictionary<string, (List<Vector3> Vertices, List<(int A, int B, int C)> Faces)>();
        foreach (var objectElement in document.Descendants(ns + "object"))
        {
            var id = objectElement.Attribute("id")?.Value;
            var meshElement = objectElement.Element(ns + "mesh");
            if (id is null || meshElement is null)
            {
                continue;
            }

            var vertices = new List<Vector3>();
            foreach (var vertex in meshElement.Element(ns + "vertices")?.Elements(ns + "vertex") ?? [])
            {
                vertices.Add(new Vector3(
                    (float)ParseXmlDouble(vertex.Attribute("x")),
                    (float)ParseXmlDouble(vertex.Attribute("y")),
                    (float)ParseXmlDouble(vertex.Attribute("z"))));
            }

            token.ThrowIfCancellationRequested();
            var faces = new List<(int A, int B, int C)>();
            foreach (var triangle in meshElement.Element(ns + "triangles")?.Elements(ns + "triangle") ?? [])
            {
                faces.Add((
                    ParseXmlInt(triangle.Attribute("v1")),
                    ParseXmlInt(triangle.Attribute("v2")),
                    ParseXmlInt(triangle.Attribute("v3"))));
            }

            objects[id] = (vertices, faces);
        }

        // Assemble what the build plate references (with placement
        // transforms); a model with no build items shows every mesh where
        // it lies.
        var mesh = new IndexedMesh();
        foreach (var item in document.Root?.Element(ns + "build")?.Elements(ns + "item") ?? [])
        {
            var objectId = item.Attribute("objectid")?.Value;
            if (objectId is not null && objects.TryGetValue(objectId, out var source))
            {
                Append3mfObject(mesh, source.Vertices, source.Faces, Parse3mfMatrix(item.Attribute("transform")?.Value));
            }
        }

        if (mesh.TriangleCount == 0)
        {
            foreach (var (vertices, faces) in objects.Values)
            {
                Append3mfObject(mesh, vertices, faces, null);
            }
        }

        return Build(mesh.Triangles, "3MF model", cap, token);
    }

    private static void Append3mfObject(
        IndexedMesh mesh,
        List<Vector3> vertices,
        List<(int A, int B, int C)> faces,
        Matrix4x4? transform)
    {
        Place(mesh, vertices.Count, faces.Count);
        var first = mesh.VertexCount;
        foreach (var vertex in vertices)
        {
            mesh.AddVertex(transform is { } matrix ? Vector3.Transform(vertex, matrix) : vertex);
        }

        // A mirroring placement turns the winding inside out.
        var mirrored = transform?.GetDeterminant() < 0;
        foreach (var (a, b, c) in faces)
        {
            if (a < 0 || b < 0 || c < 0 || a >= vertices.Count || b >= vertices.Count || c >= vertices.Count)
            {
                continue;
            }

            if (mirrored)
            {
                mesh.AddTriangle(first + a, first + c, first + b);
            }
            else
            {
                mesh.AddTriangle(first + a, first + b, first + c);
            }
        }
    }

    /// <summary>3MF transform: 12 numbers, a row-major 4x3 affine matrix for row vectors.</summary>
    private static Matrix4x4? Parse3mfMatrix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 12)
        {
            return null;
        }

        var m = new float[12];
        for (var i = 0; i < 12; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out m[i]))
            {
                return null;
            }
        }

        return new Matrix4x4(
            m[0], m[1], m[2], 0,
            m[3], m[4], m[5], 0,
            m[6], m[7], m[8], 0,
            m[9], m[10], m[11], 1);
    }

    private static double ParseXmlDouble(XAttribute? attribute) =>
        attribute is not null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int ParseXmlInt(XAttribute? attribute) =>
        attribute is not null && int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

    // ----------------------------------------------------------- shared --

    private const string TooMuchGeometry = "The model places more geometry than a preview holds.";

    /// <summary>
    /// Refuses an instance that would carry the mesh past the placed totals,
    /// before any of it is placed. The mesh's own ceiling is the higher
    /// stored one, so this is what bounds instancing.
    /// </summary>
    private static void Place(IndexedMesh mesh, long vertices, long triangles)
    {
        if (mesh.VertexCount + vertices > MaxPlacedVertices || mesh.TriangleCount + triangles > MaxPlacedTriangles)
        {
            throw new InvalidDataException(TooMuchGeometry);
        }
    }

    // The first preview after launch runs the per-vertex and per-triangle
    // paths millions of times before tiering would promote them, so they
    // start optimized.
    private delegate void TriangleVisitor(Vector3 a, Vector3 b, Vector3 c);

    /// <summary>Visits every source triangle in order; building may ask for more than one pass.</summary>
    private delegate void TriangleStream(TriangleVisitor visit, CancellationToken token);

    /// <summary>A shape ready for normals: shared positions and the corners that index them.</summary>
    private sealed record MeshShape(Vector3[] Positions, int[] Corners)
    {
        public int TriangleCount => Corners.Length / 3;
    }

    /// <summary>
    /// One path for every format: measure the source, keep it whole when it
    /// fits the cap or simplify it when it does not, then shade it.
    /// </summary>
    private static MeshPreview? Build(
        TriangleStream source,
        string formatName,
        int cap,
        CancellationToken token,
        string? countLabel = null)
    {
        var count = 0;
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var area = 0.0;
        source([MethodImpl(MethodImplOptions.AggressiveOptimization)] (a, b, c) =>
        {
            count++;
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
            area += Vector3.Cross(b - a, c - a).Length() * 0.5;
        }, token);

        if (count == 0)
        {
            return null;
        }

        var shape = count <= cap
            ? Weld(source, count, token)
            : Simplify(source, min, max, area, cap, token);
        if (shape.TriangleCount == 0)
        {
            return null;
        }

        countLabel ??= shape.TriangleCount < count
            ? $"{count:N0} triangles, simplified to {shape.TriangleCount:N0}"
            : $"{count:N0} triangles";
        return new MeshPreview(ToGeometry(shape, min, max, token), formatName, countLabel, count);
    }

    /// <summary>
    /// Joins corners that sit at exactly the same point, so neighboring
    /// faces can share a normal even when the file stores each facet alone.
    /// </summary>
    private static MeshShape Weld(TriangleStream source, int count, CancellationToken token)
    {
        var ids = new Dictionary<Vector3, int>(Math.Min(count, 1 << 20));
        var positions = new List<Vector3>();
        var corners = new int[count * 3];
        var next = 0;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        int Id(Vector3 point)
        {
            if (!ids.TryGetValue(point, out var id))
            {
                id = positions.Count;
                positions.Add(point);
                ids[point] = id;
            }

            return id;
        }

        source([MethodImpl(MethodImplOptions.AggressiveOptimization)] (a, b, c) =>
        {
            if (next < corners.Length)
            {
                corners[next++] = Id(a);
                corners[next++] = Id(b);
                corners[next++] = Id(c);
            }
        }, token);

        return new MeshShape([.. positions], next == corners.Length ? corners : corners[..next]);
    }

    /// <summary>
    /// Vertex clustering: snap every corner to a grid cell, merge each
    /// cell's corners into their mean, and keep the triangles whose three
    /// corners land in three different cells, once each. Linear in the
    /// source, and the result is still a closed surface. The cell size comes
    /// from the surface area; a result over the cap, or far under it, takes
    /// another pass with cells resized from the count it got.
    /// </summary>
    private static MeshShape Simplify(
        TriangleStream source,
        Vector3 min,
        Vector3 max,
        double area,
        int cap,
        CancellationToken token)
    {
        var extent = max - min;
        double longest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
        if (!(longest > 0) || !double.IsFinite(longest))
        {
            return new MeshShape([], []);
        }

        // A smooth closed surface keeps about two triangles per occupied
        // cell and crosses about one and a half cells per cell-sized patch
        // of area, so with these cells a dense sphere lands at 1.4 times the
        // cap. CAD meshes, whose flat faces carry few vertices at any cell
        // size, land under it: a mouse shell at 0.4. A third of the cap is
        // plenty to show.
        var floor = longest / 1_000_000;
        var cell = Math.Sqrt(1.6 * area / cap);
        if (!(cell > floor))
        {
            cell = longest / 512;
        }

        var shape = ClusterOnce(source, min, cell, cap, token);
        if (shape.TriangleCount < cap / 3)
        {
            cell = Math.Max(floor, cell * Math.Sqrt(shape.TriangleCount / (0.8 * cap)));
            shape = ClusterOnce(source, min, cell, cap, token);
        }

        // Each pass widens the cells by at least 17 percent, so this ends.
        while (shape.TriangleCount > cap * 1.1)
        {
            cell *= Math.Sqrt(shape.TriangleCount / (0.8 * cap));
            shape = ClusterOnce(source, min, cell, cap, token);
        }

        return shape;
    }

    private static MeshShape ClusterOnce(TriangleStream source, Vector3 origin, double cell, int cap, CancellationToken token)
    {
        const long axisCells = 1L << 21;
        var maxCells = (long)cap * MaxCellsPerRenderedTriangle;
        var inverse = 1 / cell;
        var cells = new Dictionary<long, int>();
        var sums = new List<(double X, double Y, double Z, int Count)>();
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        int Cell(Vector3 point)
        {
            var x = Math.Min(axisCells - 1, (long)((point.X - origin.X) * inverse));
            var y = Math.Min(axisCells - 1, (long)((point.Y - origin.Y) * inverse));
            var z = Math.Min(axisCells - 1, (long)((point.Z - origin.Z) * inverse));
            var key = (x * axisCells + y) * axisCells + z;
            if (!cells.TryGetValue(key, out var id))
            {
                if (sums.Count >= maxCells)
                {
                    throw new InvalidDataException(TooMuchGeometry);
                }

                id = sums.Count;
                cells[key] = id;
                sums.Add(default);
            }

            var sum = sums[id];
            sums[id] = (sum.X + point.X, sum.Y + point.Y, sum.Z + point.Z, sum.Count + 1);
            return id;
        }

        var seen = new HashSet<(int, int, int)>();
        var corners = new List<int>();
        source([MethodImpl(MethodImplOptions.AggressiveOptimization)] (a, b, c) =>
        {
            int ia = Cell(a), ib = Cell(b), ic = Cell(c);
            if (ia == ib || ib == ic || ia == ic)
            {
                return;
            }

            var low = Math.Min(ia, Math.Min(ib, ic));
            var high = Math.Max(ia, Math.Max(ib, ic));
            if (seen.Add((low, ia + ib + ic - low - high, high)))
            {
                corners.Add(ia);
                corners.Add(ib);
                corners.Add(ic);
            }
        }, token);

        // Only cells a surviving triangle uses become vertices.
        var remap = new int[sums.Count];
        Array.Fill(remap, -1);
        var positions = new List<Vector3>();
        var result = new int[corners.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var id = corners[i];
            if (remap[id] < 0)
            {
                var sum = sums[id];
                remap[id] = positions.Count;
                positions.Add(new Vector3((float)(sum.X / sum.Count), (float)(sum.Y / sum.Count), (float)(sum.Z / sum.Count)));
            }

            result[i] = remap[id];
        }

        return new MeshShape([.. positions], result);
    }

    /// <summary>
    /// Per-corner normals. A corner averages the faces around its vertex
    /// that turn less than the crease angle from its own face, weighted by
    /// area, so curved surfaces shade smooth and hard edges stay sharp.
    /// Every corner scans its vertex's faces, so a vertex in more than
    /// <see cref="MaxSmoothedFaces"/> shades flat instead of costing the
    /// square of its count.
    /// </summary>
    internal static Vector3[] CreaseNormals(Vector3[] positions, int[] corners, CancellationToken token)
    {
        var weighted = new Vector3[corners.Length / 3];
        var unit = new Vector3[corners.Length / 3];
        for (var t = 0; t < weighted.Length; t++)
        {
            var a = positions[corners[t * 3]];
            weighted[t] = Vector3.Cross(positions[corners[t * 3 + 1]] - a, positions[corners[t * 3 + 2]] - a);
            var length = weighted[t].Length();
            unit[t] = length > 0 ? weighted[t] / length : Vector3.Zero;
        }

        // The faces around each vertex, as one flat list with offsets.
        var start = new int[positions.Length + 1];
        foreach (var vertex in corners)
        {
            start[vertex + 1]++;
        }

        for (var v = 0; v < positions.Length; v++)
        {
            start[v + 1] += start[v];
        }

        var cursor = start[..^1];
        var around = new int[corners.Length];
        for (var i = 0; i < corners.Length; i++)
        {
            around[cursor[corners[i]]++] = i / 3;
        }

        var normals = new Vector3[corners.Length];
        for (var i = 0; i < corners.Length; i++)
        {
            if ((i & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var own = unit[i / 3];
            var vertex = corners[i];
            if (start[vertex + 1] - start[vertex] > MaxSmoothedFaces)
            {
                normals[i] = own == Vector3.Zero ? Vector3.UnitZ : own;
                continue;
            }

            var sum = Vector3.Zero;
            for (var k = start[vertex]; k < start[vertex + 1]; k++)
            {
                var face = around[k];
                if (Vector3.Dot(unit[face], own) >= CreaseCosine)
                {
                    sum += weighted[face];
                }
            }

            var length = sum.Length();
            normals[i] = length > 0 ? sum / length : Vector3.UnitZ;
        }

        return normals;
    }

    /// <summary>Centers the shape and scales its longest side to the viewport's frame.</summary>
    private static MeshGeometry3D ToGeometry(MeshShape shape, Vector3 min, Vector3 max, CancellationToken token)
    {
        var center = (min + max) / 2;
        var extent = max - min;
        double longest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
        var scale = longest > 0 && double.IsFinite(longest) ? 3.1 / longest : 1;
        var normals = CreaseNormals(shape.Positions, shape.Corners, token);
        var positions = new Point3D[shape.Corners.Length];
        var vectors = new Vector3D[shape.Corners.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var point = shape.Positions[shape.Corners[i]];
            positions[i] = new Point3D(
                (point.X - center.X) * scale,
                (point.Y - center.Y) * scale,
                (point.Z - center.Z) * scale);
            vectors[i] = new Vector3D(normals[i].X, normals[i].Y, normals[i].Z);
        }

        // No index list: each three positions are one triangle. The
        // collections take whole arrays in one copy instead of an Add each.
        var geometry = new MeshGeometry3D
        {
            Positions = new Point3DCollection(positions),
            Normals = new Vector3DCollection(vectors)
        };
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Triangles held in memory: float positions and the corners that index
    /// them. It stops at the stored totals itself, so every format that
    /// fills one is bounded, the next one added included.
    /// </summary>
    private sealed class IndexedMesh
    {
        private Vector3[] _positions = new Vector3[1024];
        private int[] _corners = new int[3 * 1024];

        public int VertexCount { get; private set; }

        public int TriangleCount { get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int AddVertex(Vector3 position)
        {
            if (VertexCount == MaxStoredVertices)
            {
                throw new InvalidDataException(TooMuchGeometry);
            }

            if (VertexCount == _positions.Length)
            {
                Array.Resize(ref _positions, _positions.Length * 2);
            }

            _positions[VertexCount] = position;
            return VertexCount++;
        }

        public int AddVertex(double x, double y, double z) => AddVertex(new Vector3((float)x, (float)y, (float)z));

        /// <summary>A corner outside the vertex list drops its triangle.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void AddTriangle(int a, int b, int c)
        {
            if ((uint)a >= (uint)VertexCount || (uint)b >= (uint)VertexCount || (uint)c >= (uint)VertexCount)
            {
                return;
            }

            if (TriangleCount == MaxStoredTriangles)
            {
                throw new InvalidDataException(TooMuchGeometry);
            }

            var at = TriangleCount * 3;
            if (at + 3 > _corners.Length)
            {
                Array.Resize(ref _corners, _corners.Length * 2);
            }

            _corners[at] = a;
            _corners[at + 1] = b;
            _corners[at + 2] = c;
            TriangleCount++;
        }

        public void AddTriangle(Vector3 a, Vector3 b, Vector3 c) => AddTriangle(AddVertex(a), AddVertex(b), AddVertex(c));

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Triangles(TriangleVisitor visit, CancellationToken token)
        {
            for (var t = 0; t < TriangleCount; t++)
            {
                if ((t & 4095) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                var at = t * 3;
                VisitFinite(visit, _positions[_corners[at]], _positions[_corners[at + 1]], _positions[_corners[at + 2]]);
            }
        }
    }

    // Text formats can carry literal NaN or Infinity coordinates, and binary
    // ones garbage floats; one such corner would poison the bounds and blank
    // the whole render, so its triangle is dropped.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void VisitFinite(TriangleVisitor visit, Vector3 a, Vector3 b, Vector3 c)
    {
        if (IsFinite(a) && IsFinite(b) && IsFinite(c))
        {
            visit(a, b, c);
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static Vector3 ToVector(Point3D point) => new((float)point.X, (float)point.Y, (float)point.Z);

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 1 << 16,
        FileOptions.SequentialScan);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool NextToken(ref ReadOnlySpan<char> line, out ReadOnlySpan<char> token)
    {
        line = line.TrimStart();
        if (line.IsEmpty)
        {
            token = default;
            return false;
        }

        var end = line.IndexOfAny(' ', '\t');
        token = end < 0 ? line : line[..end];
        line = end < 0 ? default : line[end..];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryReadVector(ref ReadOnlySpan<char> line, out Vector3 vector)
    {
        vector = default;
        if (!NextToken(ref line, out var xText) || !NextToken(ref line, out var yText) || !NextToken(ref line, out var zText)
            || !double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(zText, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        vector = new Vector3((float)x, (float)y, (float)z);
        return true;
    }
}
