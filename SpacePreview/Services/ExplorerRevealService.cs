using System.Diagnostics;
using System.IO;

namespace SpacePreview.Services;

internal static class ExplorerRevealService
{
    public static bool TryShow(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return false;
        }

        try
        {
            Process.Start(CreateStartInfo(path));
            DiagnosticLog.Write($"show in Explorer {path}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"show in Explorer failed {path}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string path)
    {
        var normalized = path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
        var isDirectoryRoot = Directory.Exists(path)
            && string.Equals(
                Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        var arguments = isDirectoryRoot
            ? $"\"{normalized}\""
            : $"/select,\"{normalized}\"";

        return new ProcessStartInfo("explorer.exe", arguments)
        {
            UseShellExecute = true
        };
    }
}
