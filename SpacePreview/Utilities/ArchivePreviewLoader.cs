using System.Diagnostics;
using System.IO;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SpacePreview.Services;

namespace SpacePreview.Utilities;

internal sealed record ArchiveNode(
    string Name,
    bool IsDirectory,
    long Size,
    IReadOnlyList<ArchiveNode> Children);

/// <summary>
/// <see cref="Truncated"/>: the tree does not show every entry.
/// <see cref="Stopped"/>: the walk ended early, so the totals are a floor too.
/// </summary>
internal sealed record ArchiveListing(
    int TotalEntries,
    long TotalUncompressed,
    IReadOnlyList<ArchiveNode> Roots,
    bool Truncated,
    bool Stopped = false);

internal static class ArchivePreviewLoader
{
    // A large archive should not build an unbounded tree; totals stay accurate
    // past the cap, structure does not.
    internal const int MaxNodes = 20_000;
    internal const int MaxPathDepth = 512;

    // A compressed tarball can only be listed by decompressing it, so its
    // walk gets a clock. The listing is the first frame; it cannot wait long.
    internal static readonly TimeSpan CompressedTarBudget = TimeSpan.FromSeconds(1.5);

    public static ArchiveListing? TryLoad(string path, CancellationToken token = default) =>
        TryLoad(path, CompressedTarBudget, token);

    internal static ArchiveListing? TryLoad(string path, TimeSpan compressedTarBudget, CancellationToken token)
    {
        try
        {
            // ArchiveFactory reads a compressed tar as a seekable archive and
            // can stop after the first entry without an error, so a tar
            // inside gzip, bzip2 or xz is always walked forward instead.
            if (CompressionSuffix(path) is not null)
            {
                var walked = TryWalkCompressedTar(path, compressedTarBudget, token);
                if (walked is not null)
                {
                    return walked;
                }
            }

            using var archive = ArchiveFactory.OpenArchive(path, new ReaderOptions());
            var tree = new TreeBuilder();
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();

                // A gzip stream need not record its file's name; the file's
                // own name minus .gz is what it holds.
                var key = string.IsNullOrEmpty(entry.Key) && archive.Type == ArchiveType.GZip
                    ? Path.GetFileNameWithoutExtension(path)
                    : entry.Key;
                tree.Add(key, entry.IsDirectory, entry.Size, token);
            }

            return tree.Build(stopped: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"archive listing failed {path}: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// The card's fact line: what it holds and how big that is unpacked,
    /// with "At least" when the walk stopped early and a note when the tree
    /// is cut. The header already names the kind.
    /// </summary>
    public static string Describe(ArchiveListing listing)
    {
        if (listing.TotalEntries == 0 && !listing.Stopped)
        {
            return "Empty archive";
        }

        var summary = $"{FolderScanner.Count(listing.TotalEntries, "file", "files")} · {FileKinds.FormatSize(listing.TotalUncompressed)} uncompressed";
        if (listing.Stopped)
        {
            return $"At least {summary}";
        }

        return listing.Truncated ? $"{summary} · Partial list" : summary;
    }

    /// <summary>
    /// Out of time is a floor, never a reason to try the archive again
    /// another way: the fallback below has no clock.
    /// </summary>
    private static ArchiveListing? TryWalkCompressedTar(string path, TimeSpan budget, CancellationToken token)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var stream = new DeadlineStream(file, budget, token);
        var tree = new TreeBuilder();
        IReader reader;
        try
        {
            reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            DiagnosticLog.Write($"compressed tar probe failed {path}: {ex.GetType().Name}");
            return stream.Expired ? tree.Build(stopped: true) : null;
        }

        using (reader)
        {
            if (reader.Type != ArchiveType.Tar)
            {
                // A single compressed file (notes.txt.gz) lists as an archive below.
                return stream.Expired ? tree.Build(stopped: true) : null;
            }

            var stopped = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // Moving on decompresses the previous entry's data, which
                    // the stream stops when the clock or the user says so.
                    if (!reader.MoveToNextEntry())
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();

                    // A cut-off download still lists what it holds, as a floor.
                    DiagnosticLog.Write($"compressed tar walk stopped {path}: {ex.GetType().Name}");
                    if (tree.IsEmpty && !stream.Expired)
                    {
                        return null;
                    }

                    stopped = true;
                    break;
                }

                var entry = reader.Entry;
                tree.Add(entry.Key, entry.IsDirectory, entry.Size, token);
                if (stream.Expired)
                {
                    stopped = true;
                    break;
                }
            }

            token.ThrowIfCancellationRequested();
            return tree.Build(stopped || stream.Expired);
        }
    }

    /// <summary>The outer compression by magic number, or null for anything else.</summary>
    internal static string? CompressionSuffix(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[6];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(header);
            return CompressionSuffix(header[..read]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? CompressionSuffix(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(new byte[] { 0x1F, 0x8B }))
        {
            return "GZ";
        }

        if (header.StartsWith("BZh"u8))
        {
            return "BZ2";
        }

        return header.StartsWith(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }) ? "XZ" : null;
    }

    private sealed class TreeBuilder
    {
        private readonly Node _root = new(string.Empty, isDirectory: true);
        private int _entries;
        private long _bytes;
        private int _nodes;
        private bool _truncated;

        public bool IsEmpty => _nodes == 0;

        public void Add(string? rawKey, bool isDirectory, long size, CancellationToken token)
        {
            var key = rawKey?.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return;
            }

            if (!isDirectory)
            {
                _bytes += Math.Max(0, size);
                _entries++;
            }

            var node = _root;
            var partLimit = Math.Min(parts.Length, MaxPathDepth);
            if (parts.Length > MaxPathDepth)
            {
                _truncated = true;
            }

            for (var i = 0; i < partLimit; i++)
            {
                token.ThrowIfCancellationRequested();

                var isCompleteLeaf = i == parts.Length - 1;
                if (!node.TryGetOrAddChild(parts[i], !isCompleteLeaf || isDirectory, ref _nodes, out var child))
                {
                    _truncated = true;
                    break;
                }

                if (isCompleteLeaf && !isDirectory)
                {
                    child.Size = Math.Max(0, size);
                }

                node = child;
            }
        }

        public ArchiveListing Build(bool stopped) =>
            new(_entries, _bytes, _root.ToImmutableChildren(), _truncated || stopped, stopped);
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

        // Largest first, like a folder's rows once its walk is done.
        public IReadOnlyList<ArchiveNode> ToImmutableChildren()
        {
            return Children.Values
                .Select(c => new ArchiveNode(c.Name, c.IsDirectory, c.TotalSize(), c.ToImmutableChildren()))
                .OrderByDescending(c => c.Size)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private long TotalSize()
        {
            _totalSize ??= Size + Children.Values.Sum(c => c.TotalSize());
            return _totalSize.Value;
        }
    }

    /// <summary>
    /// The compressed bytes under a tar walk. Every step of decompression,
    /// format detection included, reads through here, so a read past the
    /// clock or after the user has left is refused mid-entry.
    /// </summary>
    private sealed class DeadlineStream(Stream inner, TimeSpan budget, CancellationToken token) : Stream
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public bool Expired => _clock.Elapsed >= budget;

        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Check();
            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Check();
            return inner.Read(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Check()
        {
            token.ThrowIfCancellationRequested();
            if (Expired)
            {
                throw new TimeoutException("The archive walk ran out of time.");
            }
        }
    }
}
