using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

internal static class DiagnosticLog
{
    private const int MaxQueuedLines = 8192;
    private const long MaxLogBytes = 2L * 1024 * 1024;
    private const long RotationCheckBytes = 64L * 1024;
    private static long _bytesSinceRotationCheck;

    private static readonly Regex WindowsPathPattern = new(
        @"(?<![\w:])([A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*)",
        RegexOptions.Compiled);

    private static readonly Regex UncPathPattern = new(
        @"\\\\[^\s\\/:*?""<>|]+\\(?:[^\r\n\\/:*?""<>|]+\\?)*[^\r\n\\/:*?""<>|]*",
        RegexOptions.Compiled);

    private static readonly BlockingCollection<string> Queue = new(
        new ConcurrentQueue<string>(),
        MaxQueuedLines);

    public static string Pathname { get; } = AppPaths.InDataRoot(Branding.LogFileName);

    // Helpers, the probe and the resident append to one file; the mutex
    // keeps two processes from writing at the same end offset.
    private static readonly Mutex FileLock = new(false, $@"Local\{Branding.Name}.DiagnosticLog");

    private static string _source = string.Empty;

    private static readonly Thread WriterThread = StartWriter();

    static DiagnosticLog()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopWriter();
    }

    /// <summary>
    /// Names a process that is not the resident app, so its lines in the
    /// shared log say whose they are: "[prove-pdf-scale 4120] ...".
    /// </summary>
    public static void TagProcess(string? name) =>
        _source = string.IsNullOrWhiteSpace(name) ? string.Empty : $"[{name} {Environment.ProcessId}] ";

    public static void Write(string message)
    {
        // This is intentionally safe for low-level hook callbacks: callers
        // only enqueue. The writer thread owns redaction, directory creation,
        // and file I/O.
        try
        {
            Queue.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {_source}{message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must not affect preview behavior.
        }
    }

    public static string DescribeUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return "<uri>";
        }

        return string.IsNullOrWhiteSpace(parsed.Host)
            ? $"{parsed.Scheme}:"
            : $"{parsed.Scheme}://{parsed.Host}/";
    }

    private static Thread StartWriter()
    {
        var thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = $"{Branding.Name} diagnostics"
        };
        thread.Start();
        return thread;
    }

    private static void StopWriter()
    {
        try
        {
            Queue.CompleteAdding();
            WriterThread.Join(TimeSpan.FromMilliseconds(750));
        }
        catch
        {
            // Diagnostics must not affect shutdown.
        }
    }

    private static void WriterLoop()
    {
        var pending = new List<string>(64);
        while (!Queue.IsCompleted)
        {
            try
            {
                pending.Clear();
                pending.Add(Queue.Take());
                while (pending.Count < 64 && Queue.TryTake(out var line))
                {
                    pending.Add(line);
                }

                var directory = Path.GetDirectoryName(Pathname);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                AppendShared(Encoding.UTF8.GetBytes(string.Concat(pending.Select(Redact))));
            }
            catch (InvalidOperationException) when (Queue.IsCompleted)
            {
                return;
            }
            catch
            {
                // Diagnostics must not affect preview behavior.
            }
        }
    }

    /// <summary>
    /// Appends under the cross-process lock with every share mode open, so
    /// a helper writing while the resident does no longer loses its batch
    /// to a sharing violation.
    /// </summary>
    private static void AppendShared(byte[] batch)
    {
        bool owned;
        try
        {
            owned = FileLock.WaitOne(TimeSpan.FromSeconds(1));
        }
        catch (AbandonedMutexException)
        {
            // A helper died holding it; the lock is ours now.
            owned = true;
        }

        try
        {
            RotateIfNeeded(batch.Length);
            using var stream = new FileStream(
                Pathname,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Write(batch);
        }
        finally
        {
            if (owned)
            {
                FileLock.ReleaseMutex();
            }
        }
    }

    internal static string Redact(string message)
    {
        message = WindowsPathPattern.Replace(message, match => DescribePath(match.Value));
        return UncPathPattern.Replace(message, match => DescribePath(match.Value));
    }

    private static string DescribePath(string path)
    {
        try
        {
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "path";
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..8];
            return $"<path:{name}:{hash}>";
        }
        catch
        {
            return "<path>";
        }
    }

    private static void RotateIfNeeded(int nextBytes)
    {
        _bytesSinceRotationCheck += nextBytes;
        if (_bytesSinceRotationCheck < RotationCheckBytes)
        {
            return;
        }

        _bytesSinceRotationCheck = 0;
        try
        {
            var current = new FileInfo(Pathname);
            if (!current.Exists || current.Length <= MaxLogBytes)
            {
                return;
            }

            var previous = Pathname + ".1";
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }

            File.Move(Pathname, previous);
        }
        catch
        {
            // Best-effort size cap only.
        }
    }
}
