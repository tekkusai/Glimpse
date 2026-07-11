using System.IO;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace SpacePreview.Utilities;

internal sealed record ArchiveNode(
    string Name,
    bool IsDirectory,
    long Size,
    int FileCount,
    IReadOnlyList<ArchiveNode> Children);

internal sealed record ArchiveListing(
    string Kind,
    int TotalEntries,
    long TotalUncompressed,
    IReadOnlyList<ArchiveNode> Roots,
    bool Truncated);

internal static class ArchivePreviewLoader
{
    // A large archive should not build an unbounded tree; totals stay accurate
    // past the cap, structure does not.
    internal const int MaxNodes = 20_000;
    internal const int MaxPathDepth = 512;

    public static ArchiveListing? TryLoad(string path, CancellationToken token = default)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path, new ReaderOptions());
            var root = new Node(string.Empty, isDirectory: true);
            var totalEntries = 0;
            long totalBytes = 0;
            var nodeCount = 0;
            var truncated = false;

            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var key = entry.Key?.Replace('\\', '/').Trim('/');
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                if (!entry.IsDirectory)
                {
                    totalBytes += entry.Size;
                    totalEntries++;
                }

                var node = root;
                var partLimit = Math.Min(parts.Length, MaxPathDepth);
                if (parts.Length > MaxPathDepth)
                {
                    truncated = true;
                }

                for (var i = 0; i < partLimit; i++)
                {
                    token.ThrowIfCancellationRequested();

                    var isCompleteLeaf = i == parts.Length - 1;
                    var isDirectory = !isCompleteLeaf || entry.IsDirectory;
                    if (!node.TryGetOrAddChild(parts[i], isDirectory, ref nodeCount, out var child))
                    {
                        truncated = true;
                        break;
                    }

                    if (isCompleteLeaf && !entry.IsDirectory)
                    {
                        child.Size = entry.Size;
                    }

                    node = child;
                }
            }

            var kind = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            return new ArchiveListing(
                string.IsNullOrEmpty(kind) ? "Archive" : $"{kind} archive",
                totalEntries,
                totalBytes,
                root.ToImmutableChildren(),
                truncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Node
    {
        public Node(string name, bool isDirectory)
        {
            Name = name;
            IsDirectory = isDirectory;
        }

        public string Name { get; }
        public bool IsDirectory { get; private set; }
        public long Size { get; set; }
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        private long? _totalSize;
        private int? _totalFiles;

        public bool TryGetOrAddChild(string name, bool isDirectory, ref int nodeCount, out Node child)
        {
            if (!Children.TryGetValue(name, out child!))
            {
                if (nodeCount >= MaxNodes)
                {
                    child = null!;
                    return false;
                }

                child = new Node(name, isDirectory);
                Children[name] = child;
                nodeCount++;
            }
            else if (isDirectory)
            {
                child.IsDirectory = true;
            }

            return true;
        }

        public IReadOnlyList<ArchiveNode> ToImmutableChildren()
        {
            return Children.Values
                .OrderByDescending(c => c.IsDirectory)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ArchiveNode(c.Name, c.IsDirectory, c.TotalSize(), c.TotalFiles(), c.ToImmutableChildren()))
                .ToList();
        }

        private long TotalSize()
        {
            _totalSize ??= Size + Children.Values.Sum(c => c.TotalSize());
            return _totalSize.Value;
        }

        private int TotalFiles()
        {
            _totalFiles ??= (IsDirectory ? 0 : 1) + Children.Values.Sum(c => c.TotalFiles());
            return _totalFiles.Value;
        }
    }
}
