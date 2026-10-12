using System.Collections.Concurrent;
using System.IO;

namespace SpacePreview.Preview;

/// <summary>The first page's shape in points and the document's page count (0 when unknown).</summary>
internal readonly record struct PdfOutline(double WidthPoints, double HeightPoints, int PageCount);

/// <summary>
/// The first page's shape and the page count, read before anything renders:
/// the shape sizes the stage and the count decides the page rail, both
/// before the viewer lays out. It follows the file's own index through
/// <see cref="PdfReader"/> (startxref, the cross-reference sections, the
/// catalog, the first leaf of the page tree) and reads only those objects.
/// A damaged index falls back to a scan of a small prefix. Answers are
/// remembered per file version, so the stage fit, the rail and a converted
/// document's second look all share one read.
/// </summary>
internal static class PdfPageSizeProbe
{
    private const int MemoLimit = 256;

    // Real files nest a handful of levels. A crafted one nests until the
    // stack runs out, and no catch survives that, so past these it is a
    // parse failure.
    internal const int MaxNesting = PdfReader.MaxNesting;
    internal const int MaxObjectDepth = PdfReader.MaxObjectDepth;

    private static readonly ConcurrentDictionary<string, Lazy<PdfOutline?>> Memo =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The remembered read, started on the pool when it is new.</summary>
    public static Task<PdfOutline?> ReadAsync(string path)
    {
        if (MemoFor(path) is not { } entry)
        {
            return Task.FromResult<PdfOutline?>(null);
        }

        return entry.IsValueCreated
            ? Task.FromResult(entry.Value)
            : Task.Run(() => entry.Value);
    }

    /// <summary>The remembered read, computed on this thread when it is new.</summary>
    public static PdfOutline? Read(string path) => MemoFor(path)?.Value;

    public static bool TryReadFirstPageSize(string path, out double widthPoints, out double heightPoints)
    {
        var outline = Read(path);
        widthPoints = outline?.WidthPoints ?? 0;
        heightPoints = outline?.HeightPoints ?? 0;
        return outline is not null;
    }

    private static Lazy<PdfOutline?>? MemoFor(string path)
    {
        string key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return null;
        }

        if (Memo.TryGetValue(key, out var known))
        {
            return known;
        }

        if (Memo.Count >= MemoLimit)
        {
            Memo.Clear();
        }

        // A read that ran out of time or met a locked file is this moment's
        // answer, not the file's: it is forgotten, and the next look reads again.
        Lazy<PdfOutline?> entry = null!;
        entry = new Lazy<PdfOutline?>(() =>
        {
            try
            {
                return ReadFile(path);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                Memo.TryRemove(KeyValuePair.Create(key, entry));
                return null;
            }
        });
        return Memo.GetOrAdd(key, entry);
    }

    internal static PdfOutline? ReadUncached(string path)
    {
        try
        {
            return ReadFile(path);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static PdfOutline? ReadFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < 8)
        {
            return null;
        }

        var reader = PdfReader.ForShape(stream);
        return reader.TryLoadIndex() && ReadOutline(reader) is { } outline
            ? outline
            : reader.TryScanPrefix() ? ReadOutline(reader) : null;
    }

    private static PdfOutline? ReadOutline(PdfReader reader)
    {
        if (reader.PageTree() is not { } tree)
        {
            return null;
        }

        PdfOutline? outline = null;
        try
        {
            // The first leaf is page one. A page whose frame cannot be told is no answer.
            reader.WalkPages(tree.Number, tree.Node, (_, page) =>
            {
                if (reader.FrameOf(page) is { } frame
                    && frame.Box.Width is >= 1 and <= 20000
                    && frame.Box.Height is >= 1 and <= 20000)
                {
                    var count = reader.DeclaredPageCount(tree.Node) ?? 0;
                    outline = frame.Quarters % 2 == 1
                        ? new PdfOutline(frame.Box.Height, frame.Box.Width, count)
                        : new PdfOutline(frame.Box.Width, frame.Box.Height, count);
                }

                return false;
            });
        }
        catch (PdfUnsureException)
        {
            return null;
        }

        return outline;
    }
}
