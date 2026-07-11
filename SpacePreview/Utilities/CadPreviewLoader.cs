using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SpacePreview.Utilities;

internal sealed record CadPreview(string Title, string Subtitle, string Details);

internal static partial class CadPreviewLoader
{
    public static CadPreview TryLoadStl(string path)
    {
        try
        {
            var file = new FileInfo(path);
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 84)
            {
                stream.Position = 80;
                Span<byte> countBytes = stackalloc byte[4];
                stream.ReadExactly(countBytes);
                var triangleCount = BitConverter.ToUInt32(countBytes);
                var expected = 84L + triangleCount * 50L;
                if (expected == stream.Length)
                {
                    return new CadPreview(
                        "STL mesh",
                        $"{triangleCount:N0} triangles - {FileKinds.FormatSize(file.Length)}",
                        $"Binary STL mesh\nTriangles: {triangleCount:N0}\nSize: {FileKinds.FormatSize(file.Length)}\nModified: {file.LastWriteTime:g}");
                }
            }

            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var sample = reader.ReadToEnd();
            var facets = Regex.Matches(sample, @"\bfacet\s+normal\b", RegexOptions.IgnoreCase).Count;
            return new CadPreview(
                "STL mesh",
                facets > 0 ? $"{facets:N0} facets - {FileKinds.FormatSize(file.Length)}" : $"ASCII STL - {FileKinds.FormatSize(file.Length)}",
                $"ASCII STL mesh\nFacets detected: {facets:N0}\nSize: {FileKinds.FormatSize(file.Length)}\nModified: {file.LastWriteTime:g}");
        }
        catch (Exception ex)
        {
            return new CadPreview("STL mesh", FileKinds.BuildSubtitle(path), ex.Message);
        }
    }

    public static CadPreview TryLoadStep(string path)
    {
        try
        {
            var file = new FileInfo(path);
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var sampleBytes = new byte[Math.Min(stream.Length, 8 * 1024 * 1024)];
            stream.ReadExactly(sampleBytes);
            var sample = Encoding.UTF8.GetString(sampleBytes);
            var products = ProductRegex().Matches(sample)
                .Select(match => match.Groups[1].Value.Replace("''", "'").Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();

            var entityCount = EntityRegex().Matches(sample).Count;
            var schema = SchemaRegex().Match(sample);
            var schemaName = schema.Success ? schema.Groups[1].Value : "STEP";
            var builder = new StringBuilder();
            builder.AppendLine($"{schemaName} CAD exchange file");
            builder.AppendLine($"Entities: {entityCount:N0}");
            builder.AppendLine($"Size: {FileKinds.FormatSize(file.Length)}");
            builder.AppendLine($"Modified: {file.LastWriteTime:g}");
            if (products.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Products:");
                foreach (var product in products)
                {
                    builder.AppendLine($"- {product}");
                }
            }

            return new CadPreview(
                "STEP assembly",
                $"{schemaName} - {entityCount:N0} entities - {FileKinds.FormatSize(file.Length)}",
                builder.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return new CadPreview("STEP assembly", FileKinds.BuildSubtitle(path), ex.Message);
        }
    }

    [GeneratedRegex(@"PRODUCT\s*\(\s*'([^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex ProductRegex();

    [GeneratedRegex(@"^\s*#\d+\s*=", RegexOptions.Multiline)]
    private static partial Regex EntityRegex();

    [GeneratedRegex(@"FILE_SCHEMA\s*\(\s*\(\s*'([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex SchemaRegex();
}
