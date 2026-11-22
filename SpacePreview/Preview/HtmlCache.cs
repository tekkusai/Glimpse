using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// Pages Glimpse renders, kept on disk so a revisit skips the work. The key
/// names the content; the build stamp names the templates and renderers, so
/// a page an older build wrote is never served by a newer one. A page lands
/// by temp file and move, and only a file ending in &lt;/html&gt; counts as
/// cached, so a torn write is rendered again instead of shown.
/// CacheHousekeeping prunes the folders.
/// </summary>
internal static class HtmlCache
{
    private const int VerifiedMemoLimit = 256;

    // Paths already written or verified complete in this process, keyed with
    // their on-disk length and write time so an unchanged hit skips reopening
    // and seeking to the trailer, while a truncated or overwritten file is
    // re-checked and repaired.
    private static readonly ConcurrentDictionary<string, (long Length, long LastWriteTicks)> VerifiedComplete =
        new(StringComparer.OrdinalIgnoreCase);

    // A deterministic build gives each distinct compile its own module id:
    // any change to a template retires every page cached before it, with
    // nothing to remember to bump.
    internal static readonly string BuildStamp =
        typeof(HtmlCache).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    /// <summary>
    /// Returns the path of a complete page for this key, rendering it first
    /// when there is none. A null render means "no page" and caches nothing.
    /// </summary>
    public static string? Get(string folder, string key, Func<string?> render)
    {
        var path = PathFor(folder, key);
        if (TryUseCached(path))
        {
            return path;
        }

        var html = render();
        if (html is null)
        {
            return null;
        }

        Write(path, html);
        RememberComplete(path);
        return path;
    }

    private static bool TryUseCached(string path)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists || file.Length < 7)
            {
                VerifiedComplete.TryRemove(path, out _);
                return false;
            }
        }
        catch
        {
            VerifiedComplete.TryRemove(path, out _);
            return false;
        }

        var length = file.Length;
        var lastWrite = file.LastWriteTimeUtc;
        if (!VerifiedComplete.TryGetValue(path, out var verified)
            || verified.Length != length
            || verified.LastWriteTicks != lastWrite.Ticks)
        {
            if (!HasHtmlTrailer(path))
            {
                VerifiedComplete.TryRemove(path, out _);
                return false;
            }
        }

        var freshWrite = KeepFresh(path, lastWrite);
        RememberComplete(path, length, freshWrite.Ticks);
        return true;
    }

    private static void RememberComplete(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length >= 7)
            {
                RememberComplete(path, file.Length, file.LastWriteTimeUtc.Ticks);
            }
        }
        catch
        {
        }
    }

    private static void RememberComplete(string path, long length, long lastWriteTicks)
    {
        if (VerifiedComplete.Count >= VerifiedMemoLimit)
        {
            VerifiedComplete.Clear();
        }

        VerifiedComplete[path] = (length, lastWriteTicks);
    }

    internal static string PathFor(string folder, string key, string? buildStamp = null)
    {
        var directory = AppPaths.InDataRoot(folder);
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{key.ToUpperInvariant()}|{buildStamp ?? BuildStamp}")))[..16];
        return Path.Combine(directory, hash + ".html");
    }

    internal static bool IsComplete(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length >= 7 && HasHtmlTrailer(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasHtmlTrailer(string path)
    {
        try
        {
            Span<byte> ending = stackalloc byte[7];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(-ending.Length, SeekOrigin.End);
            stream.ReadExactly(ending);
            return ending.SequenceEqual("</html>"u8);
        }
        catch
        {
            return false;
        }
    }

    // A hit restarts the page's clock, so housekeeping drops what goes
    // unused, never a page that is about to be shown.
    private static DateTime KeepFresh(string path, DateTime lastWrite)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - lastWrite > TimeSpan.FromDays(1))
            {
                File.SetLastWriteTimeUtc(path, now);
                return File.GetLastWriteTimeUtc(path);
            }
        }
        catch
        {
            // A page that cannot be touched is still a good page.
        }

        return lastWrite;
    }

    private static void Write(string path, string html)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, html, Encoding.UTF8);
            if (IsComplete(path))
            {
                return;
            }

            try
            {
                File.Move(tempPath, path, overwrite: false);
                tempPath = string.Empty;
            }
            catch (IOException)
            {
                if (IsComplete(path))
                {
                    // Another preview completed the same deterministic page.
                    return;
                }

                File.Move(tempPath, path, overwrite: true);
                tempPath = string.Empty;
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // A stranded temp ages out with the rest of the folder.
                }
            }
        }
    }
}
