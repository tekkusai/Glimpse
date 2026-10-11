using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using SpacePreview.Services;

namespace SpacePreview.Preview;

/// <summary>
/// pdfium never runs in the resident app. It can die in native code on an
/// ordinary document, and its form-fill teardown faults once enough pages
/// have rendered in one process, so every render happens in a short helper
/// (this exe in a helper mode, see Program). A dead helper costs an exit
/// code; a dead resident would cost the user's preview and any signing work.
/// </summary>
internal static class PdfiumHelper
{
    /// <summary>
    /// Far longer than a page takes, short enough that a hung helper costs
    /// one failed page instead of a sign mode that never opens.
    /// </summary>
    private static readonly TimeSpan PageDeadline = TimeSpan.FromSeconds(20);

    /// <summary>Starts this exe in a helper mode, or null when it cannot start.</summary>
    public static Process? Start(params string[] arguments)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo);
    }

    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The helper already ended.
        }
    }

    /// <summary>
    /// Waits for a helper to finish. Past the deadline, or once the token
    /// is cancelled, it kills that helper, by the handle it was started
    /// with, and answers false.
    /// </summary>
    internal static async Task<bool> TryWaitAsync(Process process, TimeSpan deadline, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(deadline);
        try
        {
            await process.WaitForExitAsync(stop.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return false;
        }
    }

    /// <summary>
    /// One page at reading resolution for the sign surface, or null when the
    /// page cannot be rendered in time or the session ended first. Page
    /// index is zero-based, matching the rail.
    /// </summary>
    public static async Task<PdfPreviewPage?> TryRenderPageAsync(string path, int pageIndex, CancellationToken token)
    {
        var png = Path.Combine(Path.GetTempPath(), $"glimpse-page-{Guid.NewGuid():N}.png");
        try
        {
            using var process = Start("--render-pdf-page", path, pageIndex.ToString(CultureInfo.InvariantCulture), png);
            if (process is null)
            {
                DiagnosticLog.Write("pdf page helper did not start");
                return null;
            }

            if (!await TryWaitAsync(process, PageDeadline, token))
            {
                if (!token.IsCancellationRequested)
                {
                    DiagnosticLog.Write($"pdf page helper missed its deadline page={pageIndex + 1}");
                }

                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(png))
            {
                DiagnosticLog.Write($"pdf page helper exit {process.ExitCode} page={pageIndex + 1}");
                return null;
            }

            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(png);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return new PdfPreviewPage(bitmap, bitmap.PixelWidth, bitmap.PixelHeight);
            }, token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(png);
            }
            catch
            {
                // Temp files are the system's to sweep.
            }
        }
    }
}
