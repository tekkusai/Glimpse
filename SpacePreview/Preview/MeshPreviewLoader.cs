using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

internal sealed record MeshPreview(
    MeshGeometry3D Mesh,
    string FormatName,
    string CountLabel);

/// <summary>
/// Loads printable/scannable mesh formats (STL, OBJ, PLY, 3MF) into one
/// normalized <see cref="MeshGeometry3D"/> for the rotating viewport. Large
/// meshes are step-sampled to a render cap; totals stay accurate in the label.
/// </summary>
internal static class MeshPreviewLoader
{
    private const int MaxRenderedTriangles = 45_000;
    private const long MaxTextMeshBytes = 96L * 1024 * 1024;
    private const long Max3MfModelBytes = 32L * 1024 * 1024;
    private const int MaxIndexedVertices = 500_000;
    private const int MaxIndexedFaces = 700_000;
    private const int MaxPolygonCorners = 1024;

    public static MeshPreview? TryLoad(string path)
    {
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".stl" => TryLoadStl(path),
                ".obj" => TryLoadObj(path),
                ".ply" => TryLoadPly(path),
                ".3mf" => TryLoad3mf(path),
                _ => null
            };
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"mesh preview failed {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------- STL --

    private static MeshPreview? TryLoadStl(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (TryLoadBinaryStl(path, stream, out var binaryPreview))
        {
            return binaryPreview;
        }

        stream.Position = 0;
        return TryLoadAsciiStl(path, stream);
    }

    private static bool TryLoadBinaryStl(string path, Stream stream, out MeshPreview? preview)
    {
        preview = null;
        if (stream.Length < 84)
        {
            return false;
        }

        stream.Position = 80;
        Span<byte> countBytes = stackalloc byte[4];
        stream.ReadExactly(countBytes);
        var totalTriangles = BitConverter.ToUInt32(countBytes);
        var expectedLength = 84L + totalTriangles * 50L;
        if (totalTriangles == 0 || expectedLength > stream.Length || totalTriangles > int.MaxValue)
        {
            return false;
        }

        var geometry = new GeometryAccumulator();
        var step = Math.Max(1, (int)Math.Ceiling(totalTriangles / (double)MaxRenderedTriangles));
        Span<byte> triangleBytes = stackalloc byte[50];

        for (var triangleIndex = 0; triangleIndex < totalTriangles; triangleIndex++)
        {
            if (triangleIndex % step != 0)
            {
                stream.Seek(50, SeekOrigin.Current);
                continue;
            }

            stream.ReadExactly(triangleBytes);
            var normal = new Vector3D(
                ReadSingle(triangleBytes, 0),
                ReadSingle(triangleBytes, 4),
                ReadSingle(triangleBytes, 8));
            var p1 = ReadPoint(triangleBytes, 12);
            var p2 = ReadPoint(triangleBytes, 24);
            var p3 = ReadPoint(triangleBytes, 36);
            geometry.AddTriangle(p1, p2, p3, normal);
        }

        preview = BuildPreview(geometry, "STL mesh", (int)totalTriangles);
        return preview is not null;
    }

    private static MeshPreview? TryLoadAsciiStl(string path, Stream stream)
    {
        if (stream.Length > MaxTextMeshBytes)
        {
            return null;
        }

        var geometry = new GeometryAccumulator();
        var pending = new List<Point3D>(3);
        var totalTriangles = 0;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                continue;
            }

            pending.Add(new Point3D(x, y, z));
            if (pending.Count != 3)
            {
                continue;
            }

            totalTriangles++;
            if (geometry.TriangleCount < MaxRenderedTriangles)
            {
                geometry.AddTriangle(pending[0], pending[1], pending[2], default);
            }

            pending.Clear();
        }

        return BuildPreview(geometry, "STL mesh", totalTriangles);
    }

    // -------------------------------------------------------------- OBJ --

    private static MeshPreview? TryLoadObj(string path)
    {
        var vertices = new List<Point3D>();
        var faces = new List<(int A, int B, int C)>();

        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            if (stream.Length > MaxTextMeshBytes)
            {
                return null;
            }

            while (reader.ReadLine() is { } line)
            {
                if (line.Length < 3)
                {
                    continue;
                }

                if (line[0] == 'v' && char.IsWhiteSpace(line[1]))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                        && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                        && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    {
                        if (vertices.Count >= MaxIndexedVertices)
                        {
                            return null;
                        }

                        vertices.Add(new Point3D(x, y, z));
                    }
                }
                else if (line[0] == 'f' && char.IsWhiteSpace(line[1]))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4)
                    {
                        continue;
                    }

                    // Fan-triangulate polygons; each corner is "v", "v/t",
                    // "v//n" or "v/t/n" with 1-based (or negative relative)
                    // vertex indices.
                    var first = ParseObjIndex(parts[1], vertices.Count);
                    for (var corner = 2; corner < parts.Length - 1; corner++)
                    {
                        var second = ParseObjIndex(parts[corner], vertices.Count);
                        var third = ParseObjIndex(parts[corner + 1], vertices.Count);
                        if (first >= 0 && second >= 0 && third >= 0)
                        {
                            if (faces.Count >= MaxIndexedFaces)
                            {
                                return null;
                            }

                            faces.Add((first, second, third));
                        }
                    }
                }
            }
        }

        return BuildFromIndexed(vertices, faces, "OBJ mesh");
    }

    private static int ParseObjIndex(string token, int vertexCount)
    {
        var slash = token.IndexOf('/');
        var span = slash < 0 ? token : token[..slash];
        if (!int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return -1;
        }

        var resolved = index < 0 ? vertexCount + index : index - 1;
        return resolved >= 0 && resolved < vertexCount ? resolved : -1;
    }

    // -------------------------------------------------------------- PLY --

    private sealed record PlyProperty(string Name, string Type, string? ListCountType);

    private sealed record PlyElement(string Name, int Count, List<PlyProperty> Properties);

    private static MeshPreview? TryLoadPly(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxTextMeshBytes)
        {
            return null;
        }

        // The header is ASCII regardless of body format; read it byte-wise so
        // the stream lands at the start of a binary body.
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
                case "element" when parts.Length >= 3 && int.TryParse(parts[2], out var count):
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

        var vertexBudget = elements.FirstOrDefault(element => element.Name == "vertex")?.Count ?? 0;
        var faceBudget = elements.FirstOrDefault(element => element.Name == "face")?.Count ?? 0;
        if (vertexBudget > MaxIndexedVertices || faceBudget > MaxIndexedFaces)
        {
            return null;
        }

        var vertices = new List<Point3D>();
        var faces = new List<(int A, int B, int C)>();

        if (format == "ascii")
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            foreach (var element in elements)
            {
                for (var row = 0; row < element.Count; row++)
                {
                    var line = reader.ReadLine();
                    if (line is null)
                    {
                        return null;
                    }

                    if (element.Name == "vertex")
                    {
                        if (vertices.Count >= MaxIndexedVertices)
                        {
                            return null;
                        }

                        ParsePlyAsciiVertex(line, element, vertices);
                    }
                    else if (element.Name == "face")
                    {
                        if (faces.Count >= MaxIndexedFaces)
                        {
                            return null;
                        }

                        ParsePlyAsciiFace(line, faces);
                    }
                }
            }
        }
        else
        {
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            foreach (var element in elements)
            {
                for (var row = 0; row < element.Count; row++)
                {
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

                        vertices.Add(new Point3D(x, y, z));
                    }
                    else
                    {
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

                            if (element.Name == "face" && property.Name.Contains("vertex", StringComparison.OrdinalIgnoreCase) && count >= 3)
                            {
                                var indices = new int[count];
                                for (var i = 0; i < count; i++)
                                {
                                    indices[i] = (int)ReadPlyScalar(reader, property.Type);
                                }

                                for (var corner = 1; corner < count - 1; corner++)
                                {
                                    if (faces.Count >= MaxIndexedFaces)
                                    {
                                        return null;
                                    }

                                    faces.Add((indices[0], indices[corner], indices[corner + 1]));
                                }
                            }
                            else
                            {
                                for (var i = 0; i < count; i++)
                                {
                                    ReadPlyScalar(reader, property.Type);
                                }
                            }
                        }
                    }
                }
            }
        }

        return BuildFromIndexed(vertices, faces, "PLY mesh");
    }

    private static void ParsePlyAsciiVertex(string line, PlyElement element, List<Point3D> vertices)
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

        vertices.Add(new Point3D(x, y, z));
    }

    private static void ParsePlyAsciiFace(string line, List<(int A, int B, int C)> faces)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !int.TryParse(parts[0], out var count) || count < 3 || count > MaxPolygonCorners || parts.Length < count + 1)
        {
            return;
        }

        var indices = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (!int.TryParse(parts[i + 1], out indices[i]))
            {
                return;
            }
        }

        for (var corner = 1; corner < count - 1; corner++)
        {
            faces.Add((indices[0], indices[corner], indices[corner + 1]));
        }
    }

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

    // ------------------------------------------------------------- STEP --

    /// <summary>
    /// STEP is a B-rep exchange format; true shaded surfaces need a CAD
    /// kernel. Its edge graph does not: EDGE_CURVE entities name their two
    /// vertices and underlying curve, so lines, circles, and arcs resolve
    /// into a wireframe that sketches the part honestly in the rotating
    /// viewport. Assemblies place each component through its
    /// SHAPE_REPRESENTATION relationships (ITEM_DEFINED_TRANSFORMATION), so
    /// parts land where the assembly puts them instead of at their local
    /// origins. Spline edges draw their control polygon; anything unknown
    /// draws the straight segment between its endpoints.
    /// </summary>
    public static MeshPreview? TryLoadStepOutline(string path)
    {
        try
        {
            var entities = ParseStepEntities(path);
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
                EmitAssembly(entities, relationships, GetEdgePolyline, polylines);
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
            var bounds = BoundsAccumulator.Empty;
            var totalSegments = 0;
            foreach (var polyline in polylines)
            {
                totalSegments += polyline.Count - 1;
                foreach (var point in polyline)
                {
                    bounds.Include(point);
                }
            }

            if (!bounds.HasValue)
            {
                return null;
            }

            var extent = bounds.ToRect3D();
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
            var geometry = new GeometryAccumulator();
            var drawnEdges = 0;
            for (var index = 0; index < polylines.Count; index += step)
            {
                var polyline = polylines[index];
                for (var i = 0; i < polyline.Count - 1; i++)
                {
                    AddStrut(geometry, polyline[i], polyline[i + 1], thickness);
                }

                drawnEdges++;
            }

            if (geometry.TriangleCount == 0)
            {
                return null;
            }

            return BuildFromAccumulator(
                geometry,
                "STEP wireframe",
                drawnEdges < polylines.Count
                    ? $"{drawnEdges:N0} of {polylines.Count:N0} edges"
                    : $"{polylines.Count:N0} edges");
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
        List<List<Point3D>> polylines)
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
            edgesByRep[rep] = CollectRepresentationEdges(entities, rep);
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
    private static List<int> CollectRepresentationEdges(Dictionary<int, (string Type, string Args)> entities, int repId)
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

        while (queue.Count > 0)
        {
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
    private static Dictionary<int, (string Type, string Args)> ParseStepEntities(string path)
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
        while (reader.ReadLine() is { } line)
        {
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
    private static void AddStrut(GeometryAccumulator geometry, Point3D from, Point3D to, double thickness)
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
            geometry.AddTriangle(cornerA, cornerB, cornerC, default);
            geometry.AddTriangle(cornerB, cornerD, cornerC, default);
        }
    }

    // -------------------------------------------------------------- 3MF --

    private static MeshPreview? TryLoad3mf(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var modelEntry = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
        if (modelEntry is null)
        {
            return null;
        }

        if (modelEntry.Length > Max3MfModelBytes)
        {
            return null;
        }

        XDocument document;
        using (var entryStream = modelEntry.Open())
        {
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                MaxCharactersInDocument = Max3MfModelBytes
            };
            using var reader = System.Xml.XmlReader.Create(entryStream, settings);
            document = XDocument.Load(reader);
        }

        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var meshesById = new Dictionary<string, (List<Point3D> Vertices, List<(int A, int B, int C)> Faces)>();
        foreach (var objectElement in document.Descendants(ns + "object"))
        {
            var id = objectElement.Attribute("id")?.Value;
            var meshElement = objectElement.Element(ns + "mesh");
            if (id is null || meshElement is null)
            {
                continue;
            }

            var vertices = new List<Point3D>();
            foreach (var vertex in meshElement.Element(ns + "vertices")?.Elements(ns + "vertex") ?? [])
            {
                if (vertices.Count >= MaxIndexedVertices)
                {
                    return null;
                }

                vertices.Add(new Point3D(
                    ParseXmlDouble(vertex.Attribute("x")),
                    ParseXmlDouble(vertex.Attribute("y")),
                    ParseXmlDouble(vertex.Attribute("z"))));
            }

            var faces = new List<(int A, int B, int C)>();
            foreach (var triangle in meshElement.Element(ns + "triangles")?.Elements(ns + "triangle") ?? [])
            {
                var a = ParseXmlInt(triangle.Attribute("v1"));
                var b = ParseXmlInt(triangle.Attribute("v2"));
                var c = ParseXmlInt(triangle.Attribute("v3"));
                if (a >= 0 && b >= 0 && c >= 0)
                {
                    if (faces.Count >= MaxIndexedFaces)
                    {
                        return null;
                    }

                    faces.Add((a, b, c));
                }
            }

            meshesById[id] = (vertices, faces);
        }

        if (meshesById.Count == 0)
        {
            return null;
        }

        // Assemble what the build plate references (with placement transforms);
        // a model with no build items just shows every mesh where it lies.
        var vertices2 = new List<Point3D>();
        var faces2 = new List<(int A, int B, int C)>();
        var buildItems = document.Root?.Element(ns + "build")?.Elements(ns + "item").ToList();
        if (buildItems is { Count: > 0 })
        {
            foreach (var item in buildItems)
            {
                var objectId = item.Attribute("objectid")?.Value;
                if (objectId is null || !meshesById.TryGetValue(objectId, out var mesh))
                {
                    continue;
                }

                AppendTransformed(vertices2, faces2, mesh.Vertices, mesh.Faces, ParseMatrix(item.Attribute("transform")?.Value));
            }
        }

        if (faces2.Count == 0)
        {
            foreach (var mesh in meshesById.Values)
            {
                AppendTransformed(vertices2, faces2, mesh.Vertices, mesh.Faces, null);
            }
        }

        return BuildFromIndexed(vertices2, faces2, "3MF model");
    }

    private static void AppendTransformed(
        List<Point3D> vertices,
        List<(int A, int B, int C)> faces,
        List<Point3D> sourceVertices,
        List<(int A, int B, int C)> sourceFaces,
        Matrix3D? transform)
    {
        if (vertices.Count + sourceVertices.Count > MaxIndexedVertices || faces.Count + sourceFaces.Count > MaxIndexedFaces)
        {
            throw new InvalidDataException("3MF model is too large for preview.");
        }

        var offset = vertices.Count;
        foreach (var vertex in sourceVertices)
        {
            vertices.Add(transform?.Transform(vertex) ?? vertex);
        }

        foreach (var (a, b, c) in sourceFaces)
        {
            faces.Add((a + offset, b + offset, c + offset));
        }
    }

    /// <summary>3MF transform: 12 numbers, a row-major 4x3 affine matrix.</summary>
    private static Matrix3D? ParseMatrix(string? value)
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

        var m = new double[12];
        for (var i = 0; i < 12; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out m[i]))
            {
                return null;
            }
        }

        return new Matrix3D(
            m[0], m[1], m[2], 0,
            m[3], m[4], m[5], 0,
            m[6], m[7], m[8], 0,
            m[9], m[10], m[11], 1);
    }

    private static double ParseXmlDouble(XAttribute? attribute) =>
        attribute is not null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int ParseXmlInt(XAttribute? attribute) =>
        attribute is not null && int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

    // ---------------------------------------------------------- shared --

    /// <summary>Samples an indexed triangle list down to the render cap and builds the preview.</summary>
    private static MeshPreview? BuildFromIndexed(
        List<Point3D> vertices,
        List<(int A, int B, int C)> faces,
        string formatName)
    {
        if (vertices.Count == 0 || faces.Count == 0)
        {
            return null;
        }

        var geometry = new GeometryAccumulator();
        var step = Math.Max(1, (int)Math.Ceiling(faces.Count / (double)MaxRenderedTriangles));
        for (var faceIndex = 0; faceIndex < faces.Count; faceIndex += step)
        {
            var (a, b, c) = faces[faceIndex];
            if (a < vertices.Count && b < vertices.Count && c < vertices.Count && a >= 0 && b >= 0 && c >= 0)
            {
                geometry.AddTriangle(vertices[a], vertices[b], vertices[c], default);
            }
        }

        return BuildPreview(geometry, formatName, faces.Count);
    }

    private static MeshPreview? BuildPreview(
        GeometryAccumulator geometry,
        string formatName,
        int totalTriangles)
    {
        var renderedTriangles = geometry.TriangleCount;
        var countLabel = renderedTriangles < totalTriangles
            ? $"{renderedTriangles:N0} sampled of {totalTriangles:N0} triangles"
            : $"{renderedTriangles:N0} triangles";
        return BuildFromAccumulator(geometry, formatName, countLabel);
    }

    /// <summary>Centers and normalizes accumulated geometry into the viewport's frame.</summary>
    private static MeshPreview? BuildFromAccumulator(GeometryAccumulator geometry, string formatName, string countLabel)
    {
        if (geometry.TriangleCount == 0 || !geometry.Bounds.HasValue)
        {
            return null;
        }

        var originalBounds = geometry.Bounds.ToRect3D();
        var maxDimension = Math.Max(originalBounds.SizeX, Math.Max(originalBounds.SizeY, originalBounds.SizeZ));
        if (maxDimension <= 0 || double.IsNaN(maxDimension) || double.IsInfinity(maxDimension))
        {
            maxDimension = 1;
        }

        var center = new Point3D(
            originalBounds.X + originalBounds.SizeX / 2,
            originalBounds.Y + originalBounds.SizeY / 2,
            originalBounds.Z + originalBounds.SizeZ / 2);
        var scale = 3.1 / maxDimension;
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection(geometry.Points.Select(point => new Point3D(
                (point.X - center.X) * scale,
                (point.Y - center.Y) * scale,
                (point.Z - center.Z) * scale))),
            TriangleIndices = new Int32Collection(geometry.Indices),
            Normals = new Vector3DCollection(geometry.Normals)
        };
        mesh.Freeze();

        return new MeshPreview(mesh, formatName, countLabel);
    }

    private sealed class GeometryAccumulator
    {
        public List<Point3D> Points { get; } = [];

        public List<Vector3D> Normals { get; } = [];

        public List<int> Indices { get; } = [];

        public BoundsAccumulator Bounds;

        public GeometryAccumulator()
        {
            Bounds = BoundsAccumulator.Empty;
        }

        public int TriangleCount => Points.Count / 3;

        public void AddTriangle(Point3D p1, Point3D p2, Point3D p3, Vector3D normal)
        {
            // Text formats can carry literal NaN/Infinity coordinates; one
            // such vertex would poison the normalized bounds and blank the
            // whole render.
            if (!IsFinite(p1) || !IsFinite(p2) || !IsFinite(p3))
            {
                return;
            }

            if (normal.LengthSquared < 0.0000001 || double.IsNaN(normal.X) || double.IsNaN(normal.Y) || double.IsNaN(normal.Z))
            {
                normal = Vector3D.CrossProduct(p2 - p1, p3 - p1);
            }

            if (normal.LengthSquared < 0.0000001)
            {
                normal = new Vector3D(0, 0, 1);
            }
            else
            {
                normal.Normalize();
            }

            var start = Points.Count;
            Points.Add(p1);
            Points.Add(p2);
            Points.Add(p3);
            Normals.Add(normal);
            Normals.Add(normal);
            Normals.Add(normal);
            Indices.Add(start);
            Indices.Add(start + 1);
            Indices.Add(start + 2);
            Bounds.Include(p1);
            Bounds.Include(p2);
            Bounds.Include(p3);
        }

        private static bool IsFinite(Point3D point) =>
            double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
    }

    private static Point3D ReadPoint(ReadOnlySpan<byte> bytes, int offset) => new(
        ReadSingle(bytes, offset),
        ReadSingle(bytes, offset + 4),
        ReadSingle(bytes, offset + 8));

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset)
    {
        var value = BitConverter.ToSingle(bytes.Slice(offset, 4));
        return float.IsFinite(value) ? value : 0;
    }

    private struct BoundsAccumulator
    {
        public static BoundsAccumulator Empty => new()
        {
            MinX = double.PositiveInfinity,
            MinY = double.PositiveInfinity,
            MinZ = double.PositiveInfinity,
            MaxX = double.NegativeInfinity,
            MaxY = double.NegativeInfinity,
            MaxZ = double.NegativeInfinity
        };

        public double MinX;
        public double MinY;
        public double MinZ;
        public double MaxX;
        public double MaxY;
        public double MaxZ;

        public bool HasValue => !double.IsInfinity(MinX);

        public void Include(Point3D point)
        {
            MinX = Math.Min(MinX, point.X);
            MinY = Math.Min(MinY, point.Y);
            MinZ = Math.Min(MinZ, point.Z);
            MaxX = Math.Max(MaxX, point.X);
            MaxY = Math.Max(MaxY, point.Y);
            MaxZ = Math.Max(MaxZ, point.Z);
        }

        public Rect3D ToRect3D() => new(MinX, MinY, MinZ, MaxX - MinX, MaxY - MinY, MaxZ - MinZ);
    }
}
