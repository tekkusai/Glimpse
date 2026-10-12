using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// What a model or a drawing says about itself when it reaches the file
/// card: a STEP file with no wireframe to draw, an STL that would not
/// parse, an Illustrator file that is PostScript inside. 2.0 gave each a
/// page of details; these are the two lines of them a person wants, and
/// none repeats the header (docs/DESIGN.md, Cards). Each reads a bounded
/// sample from the front of the file, no more than 2.0 read, and runs off
/// the UI thread.
/// </summary>
internal static partial class CardFacts
{
    internal const int ModelSampleBytes = 8 * 1024 * 1024;
    internal const int MaxStepProducts = 12;
    private const int IllustratorSampleBytes = 256 * 1024;
    private const int IllustratorSampleLines = 2000;

    // The card trims a line to its column. This keeps a name a hostile
    // file made megabytes long from reaching layout at all.
    internal const int MaxFactChars = 160;

    /// <summary>
    /// The products a STEP file names, then its schema and how many
    /// entities it holds. A part exported under its own file name is in
    /// the header already.
    /// </summary>
    public static IReadOnlyList<string> Step(string path, CancellationToken token)
    {
        try
        {
            string sample;
            bool whole;
            using (var stream = Open(path))
            {
                sample = ReadSample(stream, ModelSampleBytes, out whole);
            }

            token.ThrowIfCancellationRequested();
            var facts = new List<string>(2);
            var products = StepProduct().Matches(sample)
                .Select(match => match.Groups[1].Value.Replace("''", "'").Trim())
                .Where(name => name.Length > 0 && !RepeatsFileName(name, path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxStepProducts);
            AddFact(facts, string.Join(", ", products));

            token.ThrowIfCancellationRequested();
            var about = new List<string>(2);
            if (StepSchema().Match(sample) is { Success: true } schema)
            {
                // "AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }": the name,
                // without the object identifier some writers add.
                about.Add(schema.Groups[1].Value.Split([' ', '\t', '{'], 2)[0]);
            }

            var entities = StepEntity().Count(sample);
            if (entities > 0)
            {
                about.Add(Counted(entities, whole, "entity", "entities"));
            }

            AddFact(facts, string.Join(" · ", about.Where(part => part.Length > 0)));
            return facts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"step card facts failed {path}: {ex.GetType().Name}");
            return [];
        }
    }

    /// <summary>
    /// An STL's triangle count: the one a binary file declares, or the
    /// facets found in the sample of a text one.
    /// </summary>
    public static IReadOnlyList<string> Stl(string path, CancellationToken token)
    {
        try
        {
            using var stream = Open(path);

            // The loader's own test, so the card and the viewer never
            // disagree about which kind of STL this is.
            if (MeshPreviewLoader.TryReadBinaryStlCount(stream, out var declared))
            {
                return [Counted(declared, whole: true, "triangle", "triangles")];
            }

            var sample = ReadSample(stream, ModelSampleBytes, out var whole);
            token.ThrowIfCancellationRequested();
            var facets = StlFacet().Count(sample);
            return facets == 0 ? [] : [Counted(facets, whole, "triangle", "triangles")];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"stl card facts failed {path}: {ex.GetType().Name}");
            return [];
        }
    }

    /// <summary>
    /// A PostScript Illustrator file's title, then the app that wrote it
    /// and when, from the comments that open the file. A title that is only
    /// the file's own name is already in the header.
    /// </summary>
    public static IReadOnlyList<string> Illustrator(string path)
    {
        try
        {
            string sample;
            using (var stream = Open(path))
            {
                sample = ReadSample(stream, IllustratorSampleBytes, out _).Replace("\0", string.Empty);
            }

            string? title = null, creator = null, created = null;
            foreach (var raw in sample.Split('\n').Take(IllustratorSampleLines))
            {
                var line = raw.Trim();
                title ??= DscValue(line, "%%Title:");
                creator ??= DscValue(line, "%%Creator:");
                created ??= DscValue(line, "%%CreationDate:");
                if (title is not null && creator is not null && created is not null)
                {
                    break;
                }
            }

            var facts = new List<string>(2);
            if (title is not null && !RepeatsFileName(title, path))
            {
                AddFact(facts, title);
            }

            AddFact(facts, string.Join(" · ", new[] { creator, created }.Where(part => part is not null)));
            return facts;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"illustrator card facts failed {path}: {ex.GetType().Name}");
            return [];
        }
    }

    /// <summary>
    /// A count from a sample that was not the whole file is a floor, and
    /// says so the way a folder still being sized does.
    /// </summary>
    private static string Counted(long count, bool whole, string one, string many) =>
        whole ? FolderScanner.Count(count, one, many) : $"At least {FolderScanner.Count(count, one, many)}";

    /// <summary>The header shows the file's name, so a fact that is only that name says nothing new.</summary>
    private static bool RepeatsFileName(string text, string path) =>
        text.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
        || text.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase);

    private static void AddFact(List<string> facts, string text)
    {
        if (text.Length > 0)
        {
            facts.Add(text.Length > MaxFactChars ? text[..MaxFactChars] + "…" : text);
        }
    }

    private static FileStream Open(string path) =>
        File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>The first bytes of a file as text, and whether that was all of it.</summary>
    private static string ReadSample(FileStream stream, int maxBytes, out bool whole)
    {
        stream.Position = 0;
        var bytes = new byte[(int)Math.Min(stream.Length, maxBytes)];
        var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        whole = stream.Length <= read;
        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    /// <summary>The value of one document comment, or null when the line is not that comment or says nothing.</summary>
    private static string? DscValue(string line, string comment)
    {
        if (!line.StartsWith(comment, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = PostScriptText(line[comment.Length..].Trim());
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Illustrator writes a comment's words as PostScript strings,
    /// "(5/21/98) (3:14 PM)". The words inside them are what a person reads.
    /// </summary>
    private static string PostScriptText(string value)
    {
        if (!value.StartsWith('('))
        {
            return value;
        }

        var text = new StringBuilder(value.Length);
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            // An escape is read before a parenthesis is taken as the end:
            // \) is a parenthesis in the title, \351 a character by
            // its code, and the ones that lay text out are a space in a
            // one-line fact.
            if (character == '\\' && depth > 0 && i + 1 < value.Length)
            {
                character = value[++i];
                if (character is >= '0' and <= '7')
                {
                    var code = character - '0';
                    for (var digits = 1; digits < 3 && i + 1 < value.Length && value[i + 1] is >= '0' and <= '7'; digits++)
                    {
                        code = code * 8 + value[++i] - '0';
                    }

                    character = (char)(code & 255);
                    if (char.IsControl(character))
                    {
                        character = ' ';
                    }
                }
                else if (character is 'n' or 'r' or 't' or 'b' or 'f')
                {
                    character = ' ';
                }

                text.Append(character);
                continue;
            }

            if (character == '(')
            {
                if (depth++ == 0)
                {
                    // A new string: a space parts it from the one before.
                    if (text.Length > 0)
                    {
                        text.Append(' ');
                    }

                    continue;
                }
            }
            else if (character == ')' && depth > 0 && --depth == 0)
            {
                continue;
            }

            if (depth > 0)
            {
                text.Append(character);
            }
        }

        return text.ToString().Trim();
    }

    // A doubled quote is a quote inside a STEP string.
    [GeneratedRegex(@"PRODUCT\s*\(\s*'((?:[^']|'')*)'", RegexOptions.IgnoreCase)]
    private static partial Regex StepProduct();

    // Blanks before the number stay on its own line. Allowed to run across
    // lines, as in 2.0, a file of empty lines cost the square of its length.
    [GeneratedRegex(@"^[ \t]*#\d+[ \t]*=", RegexOptions.Multiline)]
    private static partial Regex StepEntity();

    [GeneratedRegex(@"FILE_SCHEMA\s*\(\s*\(\s*'([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex StepSchema();

    [GeneratedRegex(@"\bfacet\s+normal\b", RegexOptions.IgnoreCase)]
    private static partial Regex StlFacet();
}
