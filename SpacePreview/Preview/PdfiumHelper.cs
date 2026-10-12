using System.ComponentModel;
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

    // Every helper, for the life of the app. A hung render used to outlive
    // a quit and hold the exe and its DLLs open under an update; Windows
    // now ends whatever is in the job when the app's handle to it closes,
    // however the app ends.
    private static readonly Lazy<ChildJob?> Job = new(() =>
    {
        try
        {
            return new ChildJob();
        }
        catch (Win32Exception ex)
        {
            DiagnosticLog.Write($"pdf helper job unavailable: {ex.Message}");
            return null;
        }
    });

    /// <summary>What a helper run came to. No exit code means it was killed: past its deadline, or cancelled.</summary>
    internal readonly record struct Run(bool Started, int? ExitCode);

    /// <summary>The exe that runs as a helper: this one. A test names one that only waits.</summary>
    internal static string? Exe { get; set; } = Environment.ProcessPath;

    /// <summary>How many helpers are running now.</summary>
    internal static int Running => Job.Value?.ActiveProcesses ?? 0;

    /// <summary>
    /// A range of rail thumbnails: time to start and open the document, and
    /// two seconds a page, where a page takes a few hundredths.
    /// </summary>
    internal static TimeSpan RangeDeadline(int pages) => TimeSpan.FromSeconds(20 + 2 * Math.Max(1, pages));

    /// <summary>
    /// Runs this exe in a helper mode and waits for it to finish. Past the
    /// deadline, or once the token is cancelled, the helper is killed.
    /// </summary>
    public static async Task<Run> RunAsync(TimeSpan deadline, CancellationToken token, ProcessPriorityClass priority, params string[] arguments)
    {
        using var process = Start(arguments);
        if (process is null)
        {
            return default;
        }

        if (priority != ProcessPriorityClass.Normal)
        {
            try
            {
                process.PriorityClass = priority;
            }
            catch
            {
                // A helper that already exited keeps its default priority.
            }
        }

        return new Run(true, await TryWaitAsync(process, deadline, token) ? process.ExitCode : null);
    }

    /// <summary>Starts the helper in the app's job, or null when it cannot start.</summary>
    private static Process? Start(string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(Exe))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(startInfo);
            if (process is not null)
            {
                Job.Value?.Add(process);
            }

            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
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
    /// with, and answers false. The kill runs where the cancel happens: a
    /// continuation would wait on a dispatcher that a quit has stopped.
    /// </summary>
    internal static async Task<bool> TryWaitAsync(Process process, TimeSpan deadline, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(deadline);
        using var kill = stop.Token.Register(() => Kill(process));
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// One page at reading resolution for the sign surface, or null when the
    /// page cannot be rendered in time or the session ended first. Page
    /// index is zero-based, matching the rail. Nothing is thrown: a page
    /// that fails for any reason is a page that did not render.
    /// </summary>
    public static async Task<PdfPreviewPage?> TryRenderPageAsync(string path, int pageIndex, CancellationToken token)
    {
        var png = Path.Combine(Path.GetTempPath(), $"glimpse-page-{Guid.NewGuid():N}.png");
        try
        {
            var run = await RunAsync(PageDeadline, token, ProcessPriorityClass.Normal, "--render-pdf-page", path, pageIndex.ToString(CultureInfo.InvariantCulture), png);
            if (!run.Started)
            {
                DiagnosticLog.Write("pdf page helper did not start");
                return null;
            }

            if (run.ExitCode is not { } exitCode)
            {
                if (!token.IsCancellationRequested)
                {
                    DiagnosticLog.Write($"pdf page helper missed its deadline page={pageIndex + 1}");
                }

                return null;
            }

            if (exitCode != 0 || !File.Exists(png))
            {
                DiagnosticLog.Write($"pdf page helper exit {exitCode} page={pageIndex + 1}");
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
        catch (Exception ex)
        {
            // A helper that will not start, a PNG that will not decode: the
            // caller is told the page did not render, not handed a fault.
            DiagnosticLog.Write($"pdf page helper failed page={pageIndex + 1}: {ex.GetType().Name}");
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
