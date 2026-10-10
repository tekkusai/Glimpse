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
        if (IsComplete(path))
        {
            KeepFresh(path);
            return path;
        }

        var html = render();
        if (html is null)
        {
            return null;
        }

        Write(path, html);
        CacheHousekeeping.RunIfDue();
        return path;
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
            if (!file.Exists || file.Length < 7)
            {
                return false;
            }

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
    private static void KeepFresh(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
            {
                File.SetLastWriteTimeUtc(path, now);
            }
        }
        catch
        {
            // A page that cannot be touched is still a good page.
        }
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
