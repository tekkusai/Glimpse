using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using SpacePreview.Services;

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
    private const long PreferredInteractivePageRasterPixels = 12_000_000;
    private const double AdaptiveRasterHeadroom = 0.98;
    private const int MaxThumbnailRasterEdge = 4096;
    private const long MaxThumbnailRasterPixels = 4_000_000;

    // pdfium is not thread safe: every DocLib touch in the process must be
    // serialized, or overlapping renders (a rail rebuild racing the next
    // preview's render) die as a native access violation with no managed
    // trace in the log.
    private static readonly SemaphoreSlim PdfiumGate = new(1, 1);

    // A rewrite is accepted only when overlapping local color sums stay
    // within PDFium's edge-antialiasing drift. Small blocks catch local loss;
    // larger blocks tighten the permitted average drift across a region.
    private const int ProofEdge = 768;
    private const int ProofMaxPages = 120;
    private static readonly (int Size, int MaxChannelDifference)[] ProofBlocks =
    [
        (4, 72),
        (8, 36),
        (16, 20)
    ];

    /// <summary>
    /// True when two documents draw the same pages. Rewriting a PDF can
    /// go wrong in ways no page count reveals: a pattern that does not follow
    /// the page, content that resolves to nothing, a crop that stops being
    /// applied. Rather than enumerate those, the rewrite is only trusted when
    /// it still looks like the original. Both sides are forced to the same
    /// raster, so a page scaled correctly compares equal.
    /// </summary>
    public static bool RendersLike(string leftPath, string rightPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfiumGate.Wait(cancellationToken);
        try
        {
            using var leftDocument = DocLib.Instance.GetDocReader(leftPath, new PageDimensions(ProofEdge, ProofEdge));
            using var rightDocument = DocLib.Instance.GetDocReader(rightPath, new PageDimensions(ProofEdge, ProofEdge));
            var pageCount = leftDocument.GetPageCount();
            if (pageCount <= 0 || pageCount > ProofMaxPages || rightDocument.GetPageCount() != pageCount)
            {
                return false;
            }

            for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var leftPage = leftDocument.GetPageReader(pageIndex);
                using var rightPage = rightDocument.GetPageReader(pageIndex);
                if (leftPage.GetPageWidth() != rightPage.GetPageWidth()
                    || leftPage.GetPageHeight() != rightPage.GetPageHeight())
                {
                    DiagnosticLog.Write($"pdf proof page {pageIndex + 1} dimensions changed");
                    return false;
                }

                var left = leftPage.GetImage(RenderFlags.RenderAnnotations | RenderFlags.OptimizeTextForLcd);
                var right = rightPage.GetImage(RenderFlags.RenderAnnotations | RenderFlags.OptimizeTextForLcd);
                if (left.Length != right.Length)
                {
                    return false;
                }

                CompositeBgraOverWhite(left);
                CompositeBgraOverWhite(right);
                var width = leftPage.GetPageWidth();
                var height = leftPage.GetPageHeight();
                if (!BlockRastersMatch(left, right, width, height, out var changedPixel))
                {
                    DiagnosticLog.Write($"pdf proof page {pageIndex + 1} changed at pixel {changedPixel}");
                    return false;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            PdfiumGate.Release();
        }
    }

    /// <summary>
    /// PDFium rasterizes a page-level transform with different edge coverage
    /// even when the vectors land in the same place. Overlapping block sums
    /// preserve local color and ink while ignoring that subpixel distribution.
    /// Small blocks catch a lost mark; larger blocks catch a displaced region.
    /// </summary>
    private static bool BlockRastersMatch(
        byte[] left,
        byte[] right,
        int width,
        int height,
        out int changedPixel)
    {
        foreach (var (size, maxDifference) in ProofBlocks)
        {
            var step = size / 2;
            for (var y = 0; y < height; y += step)
            {
                var blockHeight = Math.Min(size, height - y);
                for (var x = 0; x < width; x += step)
                {
                    var blockWidth = Math.Min(size, width - x);
                    var pixelCount = blockWidth * blockHeight;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var difference = 0;
                        for (var dy = 0; dy < blockHeight; dy++)
                        {
                            var offset = ((y + dy) * width + x) * 4 + channel;
                            for (var dx = 0; dx < blockWidth; dx++)
                            {
                                difference += left[offset + dx * 4] - right[offset + dx * 4];
                            }
                        }

                        if (Math.Abs(difference) > maxDifference * pixelCount)
                        {
                            changedPixel = y * width + x;
                            return false;
                        }
                    }
                }
            }
        }

        changedPixel = -1;
        return true;
    }

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
                var page = RenderPage(pageReader, thumbnail: false)
                    ?? RenderFittedPage(path, i, pageReader, cancellationToken);
                if (page is null)
                {
                    return null;
                }

                pages.Add(page);
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
                return RenderPage(pageReader, thumbnail: false)
                    ?? RenderFittedPage(path, pageIndex, pageReader, cancellationToken);
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

    internal static double PageScaleForRasterBudget(
        int width,
        int height,
        double currentScale,
        bool thumbnail = false)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(currentScale) || currentScale <= 0)
        {
            return 0.01;
        }

        var maxEdge = thumbnail ? MaxThumbnailRasterEdge : MaxPageRasterEdge;
        var targetPixels = thumbnail ? MaxThumbnailRasterPixels : PreferredInteractivePageRasterPixels;
        var edgeFactor = Math.Min(
            (double)maxEdge / width,
            (double)maxEdge / height);
        var pixelFactor = Math.Sqrt(
            targetPixels / ((double)width * height));
        var factor = Math.Min(1, Math.Min(edgeFactor, pixelFactor));
        return Math.Max(0.01, currentScale * factor * AdaptiveRasterHeadroom);
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
        var document = DocLib.Instance.GetDocReader(path, new PageDimensions(ThumbnailPixelsPerPoint));
        try
        {
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

            // Artboards run to meters. One page over the thumbnail budget used
            // to drop the whole rail, so a set of large-format artwork simply
            // had none. Fit the set instead, at one scale so the rail stays
            // even. Measuring costs a page load each, so it only happens once
            // the first page has already proved the set is oversized.
            if (ThumbnailFitScale(document, startPage, pageCount, cancellationToken) is { } fitted)
            {
                document.Dispose();
                document = DocLib.Instance.GetDocReader(path, new PageDimensions(fitted));
            }

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
        finally
        {
            document.Dispose();
        }
    }

    /// <summary>
    /// The scale the most demanding page in range needs, or null when the set
    /// already fits. Every page is measured: a set can open with an ordinary
    /// cover and reach a dieline five pages in, and stopping at the first page
    /// that fits took the whole rail down with it.
    /// </summary>
    private static double? ThumbnailFitScale(
        Docnet.Core.Readers.IDocReader document,
        int startPage,
        int pageCount,
        CancellationToken cancellationToken)
    {
        double? scale = null;
        for (var i = 0; i < pageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pageReader = document.GetPageReader(startPage + i);
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();
            if (IsRasterSizeSafe(width, height, thumbnail: true))
            {
                continue;
            }

            var candidate = PageScaleForRasterBudget(width, height, ThumbnailPixelsPerPoint, thumbnail: true);
            scale = scale is null ? candidate : Math.Min(scale.Value, candidate);
        }

        return scale;
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

    /// <summary>
    /// A page too big to rasterize at reading resolution renders at the
    /// largest scale that fits the budget. Refusing it looks to the user
    /// like a damaged file.
    /// </summary>
    private static PdfPreviewPage? RenderFittedPage(
        string path,
        int pageIndex,
        Docnet.Core.Readers.IPageReader measured,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scale = PageScaleForRasterBudget(
            measured.GetPageWidth(),
            measured.GetPageHeight(),
            PixelsPerPoint);
        using var document = DocLib.Instance.GetDocReader(path, new PageDimensions(scale));
        if (pageIndex >= document.GetPageCount())
        {
            return null;
        }

        using var pageReader = document.GetPageReader(pageIndex);
        return RenderPage(pageReader, thumbnail: false);
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
