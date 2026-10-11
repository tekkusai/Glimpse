using System.Globalization;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview;

/// <summary>
/// The entry point. pdfium can die in native code on an ordinary document,
/// so it runs in short helper processes that turn a crash into an exit code
/// instead of taking the resident app down. A helper only rasterizes and
/// exits, so it starts here, before any WPF application exists, and pays for
/// none of the resident app's startup or shutdown.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 4 && IsMode(args[0], "--render-pdf-thumbnails"))
        {
            return RenderPdfThumbnails(args);
        }

        if (args.Length >= 3 && IsMode(args[0], "--prove-pdf-scale"))
        {
            return ProvePdfScale(args);
        }

        return new App().Run();
    }

    private static bool IsMode(string arg, string mode) =>
        string.Equals(arg, mode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// --render-pdf-thumbnails pdf directory pageLimit [startPage]. Exits 0
    /// once the manifest is written, 2 when pdfium cannot read the document,
    /// 1 on bad arguments.
    /// </summary>
    private static int RenderPdfThumbnails(string[] args)
    {
        DiagnosticLog.TagProcess("render-pdf-thumbnails");
        PdfPreviewRenderer.SkipDocumentTeardown = true;
        try
        {
            var pageLimit = int.Parse(args[3], CultureInfo.InvariantCulture);
            var startPage = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 0;
            return PdfPreviewRenderer.TryRenderThumbnailsToDirectory(args[1], args[2], pageLimit, CancellationToken.None, startPage)
                ? 0
                : 2;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf thumbnail helper failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>--prove-pdf-scale original rewritten. Exits 0 proved, 2 rejected, 1 no verdict.</summary>
    private static int ProvePdfScale(string[] args)
    {
        DiagnosticLog.TagProcess("prove-pdf-scale");
        PdfPreviewRenderer.SkipDocumentTeardown = true;
        try
        {
            return PdfPreviewRenderer.RendersLike(args[1], args[2], CancellationToken.None) ? 0 : 2;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf proof helper failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
