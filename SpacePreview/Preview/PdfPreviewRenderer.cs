using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;

namespace SpacePreview.Preview;

internal sealed record PdfPreviewPage(BitmapSource Bitmap, int Width, int Height);

internal sealed record PdfPreviewDocument(IReadOnlyList<PdfPreviewPage> Pages, int TotalPages);

internal sealed record PdfThumbnailManifest(int TotalPages, List<PdfThumbnailManifestPage> Pages);

internal sealed record PdfThumbnailManifestPage(int Number, string File, int Width, int Height);

internal static class PdfPreviewRenderer
{
    private const int MaxPages = 5;
    private const double PixelsPerPoint = 2.0; // 144 DPI. Sharp enough for preview, cheap enough for Spacebar.
    private const double ThumbnailPixelsPerPoint = 0.32;
    private const int MaxThumbnailPages = 120;
    private const int MaxPageRasterEdge = 8192;
    private const long MaxPageRasterPixels = 20_000_000;
    private const int MaxThumbnailRasterEdge = 4096;
    private const long MaxThumbnailRasterPixels = 4_000_000;

    // pdfium is not thread safe: every DocLib touch in the process must be
    // serialized, or overlapping renders (a rail rebuild racing the next
    // preview's render) die as a native access violation with no managed
    // trace in the log.
    private static readonly SemaphoreSlim PdfiumGate = new(1, 1);

    public static PdfPreviewDocument? TryRender(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfiumGate.Wait(cancellationToken);
        try
        {
            return TryRenderCore(path, cancellationToken);
        }
        finally
        {
            PdfiumGate.Release();
        }
    }

    private static PdfPreviewDocument? TryRenderCore(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var document = DocLib.Instance.GetDocReader(path, new PageDimensions(PixelsPerPoint));
            var totalPages = document.GetPageCount();
            var pageCount = Math.Min(totalPages, MaxPages);
            var pages = new List<PdfPreviewPage>(pageCount);

            for (var i = 0; i < pageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var pageReader = document.GetPageReader(i);
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();
                if (!IsRasterSizeSafe(width, height, thumbnail: false))
                {
                    return null;
                }

                var bytes = pageReader.GetImage(RenderFlags.RenderAnnotations | RenderFlags.OptimizeTextForLcd);
                CompositeBgraOverWhite(bytes);

                var bitmap = BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    bytes,
                    width * 4);
                bitmap.Freeze();
                pages.Add(new PdfPreviewPage(bitmap, width, height));
            }

            return pages.Count == 0 ? null : new PdfPreviewDocument(pages, totalPages);
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

    /// <summary>
    /// One page at reading resolution, for the sign surface: the user
    /// places ink against exactly what the document shows, so annotations
    /// render too. Page index is zero-based, matching the rail.
    /// </summary>
    public static PdfPreviewPage? TryRenderPage(string path, int pageIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfiumGate.Wait(cancellationToken);
        try
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var document = DocLib.Instance.GetDocReader(path, new PageDimensions(PixelsPerPoint));
                if (pageIndex < 0 || pageIndex >= document.GetPageCount())
                {
                    return null;
                }

                using var pageReader = document.GetPageReader(pageIndex);
                return RenderPage(pageReader, thumbnail: false);
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
        finally
        {
            PdfiumGate.Release();
        }
    }

    public static PdfPreviewDocument? TryRenderThumbnails(
        string path,
        CancellationToken cancellationToken,
        int pageLimit = MaxThumbnailPages,
        int startPage = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfiumGate.Wait(cancellationToken);
        try
        {
            // Rail thumbnails race Chromium's own load of the same document
            // and, on a fresh open, the entrance animation. They have until
            // the reveal gate; the frames the user is watching come first.
            var thread = System.Threading.Thread.CurrentThread;
            var previous = thread.Priority;
            try
            {
                thread.Priority = System.Threading.ThreadPriority.BelowNormal;
                return TryRenderThumbnailsCore(path, cancellationToken, pageLimit, startPage);
            }
            finally
            {
                thread.Priority = previous;
            }
        }
        finally
        {
            PdfiumGate.Release();
        }
    }

    public static bool TryRenderThumbnailsToDirectory(
        string path,
        string directory,
        int pageLimit,
        CancellationToken cancellationToken,
        int startPage = 0)
    {
        var document = TryRenderThumbnails(path, cancellationToken, pageLimit, startPage);
        if (document is null)
        {
            return false;
        }

        Directory.CreateDirectory(directory);
        var manifestPages = new List<PdfThumbnailManifestPage>(document.Pages.Count);
        for (var i = 0; i < document.Pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = startPage + i + 1;
            var file = $"page-{pageNumber:0000}.png";
            var filePath = Path.Combine(directory, file);
            SavePng(document.Pages[i].Bitmap, filePath);
            manifestPages.Add(new PdfThumbnailManifestPage(
                pageNumber,
                file,
                document.Pages[i].Width,
                document.Pages[i].Height));
        }

        var manifest = new PdfThumbnailManifest(document.TotalPages, manifestPages);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest));
        return true;
    }

    private static PdfPreviewDocument? TryRenderThumbnailsCore(
        string path,
        CancellationToken cancellationToken,
        int pageLimit,
        int startPage)
    {
        try
        {
            using var document = DocLib.Instance.GetDocReader(path, new PageDimensions(ThumbnailPixelsPerPoint));
            var totalPages = document.GetPageCount();
            if (totalPages <= 0)
            {
                return null;
            }

            // A one-page document has no rail. Counting it is enough; do not
            // rasterize page one merely to discover that the rail stays hidden.
            if (totalPages == 1 && startPage == 0)
            {
                return new PdfPreviewDocument([], totalPages);
            }

            if (startPage >= totalPages)
            {
                return new PdfPreviewDocument([], totalPages);
            }

            startPage = Math.Max(0, startPage);
            var pageCount = Math.Min(totalPages - startPage, Math.Clamp(pageLimit, 1, MaxThumbnailPages));
            var pages = new List<PdfPreviewPage>(pageCount);

            for (var i = 0; i < pageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var pageReader = document.GetPageReader(startPage + i);
                var page = RenderPage(pageReader, thumbnail: true);
                if (page is null)
                {
                    return null;
                }

                pages.Add(page);
            }

            return new PdfPreviewDocument(pages, totalPages);
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

    private static PdfPreviewPage? RenderPage(Docnet.Core.Readers.IPageReader pageReader, bool thumbnail)
    {
        var width = pageReader.GetPageWidth();
        var height = pageReader.GetPageHeight();
        if (!IsRasterSizeSafe(width, height, thumbnail))
        {
            return null;
        }

        var bytes = pageReader.GetImage(RenderFlags.RenderAnnotations | RenderFlags.OptimizeTextForLcd);
        CompositeBgraOverWhite(bytes);

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bytes,
            width * 4);
        bitmap.Freeze();
        return new PdfPreviewPage(bitmap, width, height);
    }

    internal static bool IsRasterSizeSafe(int width, int height, bool thumbnail)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var maxEdge = thumbnail ? MaxThumbnailRasterEdge : MaxPageRasterEdge;
        var maxPixels = thumbnail ? MaxThumbnailRasterPixels : MaxPageRasterPixels;
        return width <= maxEdge
            && height <= maxEdge
            && (long)width * height <= maxPixels;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void CompositeBgraOverWhite(byte[] bytes)
    {
        for (var i = 0; i + 3 < bytes.Length; i += 4)
        {
            var alpha = bytes[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            if (alpha == 0)
            {
                bytes[i] = 255;
                bytes[i + 1] = 255;
                bytes[i + 2] = 255;
                bytes[i + 3] = 255;
                continue;
            }

            var inverse = 255 - alpha;
            bytes[i] = (byte)((bytes[i] * alpha + 255 * inverse) / 255);
            bytes[i + 1] = (byte)((bytes[i + 1] * alpha + 255 * inverse) / 255);
            bytes[i + 2] = (byte)((bytes[i + 2] * alpha + 255 * inverse) / 255);
            bytes[i + 3] = 255;
        }
    }
}
