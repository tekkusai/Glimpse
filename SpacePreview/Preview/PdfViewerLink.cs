using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// The Chromium viewer chooses its handler by file extension, so a
/// PDF-compatible Illustrator file offers itself as a download instead of
/// opening. The same bytes under a .pdf name in the document cache reach the
/// real viewer, and artwork gets the zoom, scrolling, and page rail every
/// other PDF already has.
/// </summary>
internal static class PdfViewerLink
{
    // The viewer's zoom ladder stops at 25%, and fit is not a rung on it. A
    // page wide enough to open below 25% can never be zoomed back out to how
    // it arrived, so artboards past this are rewritten to it. The narrowest
    // stage leaves about 506 device-independent pixels for the viewer once the
    // page rail takes its column, and 25% of 1400 points is 467 of them, so a
    // page rewritten to this still opens above the floor with the rail out.
    private const double MaxViewerEdgePoints = 1400;

    // The proof renders every page of two documents. On complex artwork that
    // is a few hundred milliseconds a page, so a long set has to be allowed to
    // lose rather than to hold the first preview open indefinitely.
    private static readonly TimeSpan ProofTimeout = TimeSpan.FromSeconds(10);

    // Cache entries are published by rename, but two previews of one file can
    // still both decide to build one. Striping keeps that to a wait instead of
    // duplicated work, without a dictionary of locks to clean up afterwards.
    private static readonly object[] BuildGates =
        Enumerable.Range(0, 16).Select(_ => new object()).ToArray();

    /// <summary>
    /// A .pdf path for this artwork, or null when one cannot be made.
    /// </summary>
    public static string? TryResolve(string path, CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(path);
            var key = CacheKey(file);
            var directory = Path.Combine(AppPaths.DataRoot, "DocumentCache", key);
            var viewable = Path.Combine(directory, Path.GetFileNameWithoutExtension(file.Name) + ".pdf");
            if (Usable(viewable))
            {
                return viewable;
            }

            lock (BuildGates[(uint)key.GetHashCode() % BuildGates.Length])
            {
                // The winner of the race published while this thread waited.
                if (Usable(viewable))
                {
                    return viewable;
                }

                // An entry left behind by a proof that could not run is torn
                // down and tried again rather than served.
                if (File.Exists(viewable))
                {
                    TryDelete(UnprovenMarker(viewable));
                    TryDelete(viewable);
                }

                Directory.CreateDirectory(directory);
                return Build(file, directory, viewable, cancellationToken);
            }
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
    /// Builds the entry beside its final name and renames it into place. The
    /// staging name is never the published one, because the published one can
    /// be a hard link to the user's artwork and creating a file over a hard
    /// link truncates what it points at. Nothing here may ever open the
    /// viewable path for writing.
    /// </summary>
    private static string? Build(FileInfo file, string directory, string viewable, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(directory, $"{Guid.NewGuid():N}.staging");
        try
        {
            var framed = TryStageFramedCopy(file.FullName, staging, cancellationToken);
            if (framed != FramingOutcome.Framed)
            {
                TryDelete(staging);

                // A hard link is instant and costs no disk, but both names must
                // live on one volume. Artwork on another drive falls back to a
                // copy. Either way the artwork is only read.
                if (!NativeMethods.CreateHardLink(staging, file.FullName, IntPtr.Zero))
                {
                    File.Copy(file.FullName, staging, overwrite: false);
                }
            }

            File.Move(staging, viewable, overwrite: false);

            // A link published because the proof could not RUN is an accident
            // of the moment, not a decision about this artwork. Caching it
            // would freeze one bad minute into two weeks of unscaled previews,
            // which is exactly the bug it is meant to prevent. Mark it so the
            // next open rebuilds; a proof that ran and said no is a real answer
            // and stays.
            if (framed == FramingOutcome.Unproven)
            {
                File.WriteAllText(UnprovenMarker(viewable), string.Empty);
            }

            return viewable;
        }
        catch (OperationCanceledException)
        {
            TryDelete(staging);
            throw;
        }
        catch
        {
            TryDelete(staging);
            return File.Exists(viewable) ? viewable : null;
        }
    }

    /// <summary>
    /// True once an oversized document has been rewritten to a size the viewer
    /// can frame. The rewrite has to earn it: the result must still open, and
    /// must still draw every page. A rewritten PDF can fail in ways a
    /// page count cannot see, so the proof is the picture rather than a list of
    /// the ways it could have gone wrong.
    /// </summary>
    private enum FramingOutcome
    {
        /// <summary>Rewritten and proved. Publish it.</summary>
        Framed,

        /// <summary>A real answer about this artwork. Link, and keep the answer.</summary>
        Declined,

        /// <summary>The proof could not run. Link for now, and try again next time.</summary>
        Unproven
    }

    private static FramingOutcome TryStageFramedCopy(string sourcePath, string staging, CancellationToken cancellationToken)
    {
        try
        {
            if (!PdfFillSignWriter.TryWriteScaledCopy(sourcePath, staging, MaxViewerEdgePoints))
            {
                DiagnosticLog.Write($"pdf rescale not applicable <path:{sourcePath}>");
                return FramingOutcome.Declined;
            }

            switch (ProvedInHelper(sourcePath, staging))
            {
                case ProofResult.Proved:
                    return FramingOutcome.Framed;

                case ProofResult.Rejected:
                    DiagnosticLog.Write($"scaled pdf copy did not match the original, linking instead: {sourcePath}");
                    return FramingOutcome.Declined;

                default:
                    return FramingOutcome.Unproven;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf rescale skipped {sourcePath}: {ex.GetType().Name}: {ex.Message}");
            return FramingOutcome.Declined;
        }
    }

    private enum ProofResult
    {
        Proved,
        Rejected,
        Unavailable
    }

    /// <summary>The marker that says an entry was published without a verdict.</summary>
    private static string UnprovenMarker(string viewable) => viewable + ".unproven";

    /// <summary>An entry is only servable once something actually vouched for it.</summary>
    private static bool Usable(string viewable) =>
        File.Exists(viewable) && !File.Exists(UnprovenMarker(viewable));

    /// <summary>
    /// Runs the proof in a short child process, the way rail thumbnails already
    /// do. pdfium can die in native code on a document it dislikes, and the
    /// document here is one this app wrote moments ago. A crash then costs an
    /// exit code and a fallback to linking instead of the user's preview.
    /// Anything other than a clean match is treated as a failed proof.
    /// </summary>
    private static ProofResult ProvedInHelper(string sourcePath, string staging)
    {
        var helper = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(helper) || !File.Exists(helper))
        {
            DiagnosticLog.Write("pdf proof helper unavailable");
            return ProofResult.Unavailable;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--prove-pdf-scale");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add(staging);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            DiagnosticLog.Write("pdf proof helper did not start");
            return ProofResult.Unavailable;
        }

        if (!process.WaitForExit(ProofTimeout))
        {
            // A proof that will not finish is a proof that failed. Artwork
            // this heavy is exactly what must not hold a preview open.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }

            DiagnosticLog.Write($"pdf proof timed out, linking instead: {sourcePath}");
            return ProofResult.Unavailable;
        }

        // 0 proved, 2 ran and rejected. Anything else (a native crash, a
        // startup failure) is the helper never reaching a verdict.
        return process.ExitCode switch
        {
            0 => ProofResult.Proved,
            2 => ProofResult.Rejected,
            _ => ProofResult.Unavailable
        };
    }

    /// <summary>
    /// Identity for the cache. Length, write time, and bounded samples all miss
    /// real edits: a timestamp-preserving replacement can change only a middle
    /// content stream. Hashing the file is cheaper than the PDF parse and two
    /// proof renders that an oversized document already pays, and makes the
    /// identity exact for every route.
    /// </summary>
    private static string CacheKey(FileInfo file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|pdfview|v4"));

        // Artwork open in Illustrator is still previewable, so the key must not
        // be the thing that needs an exclusive handle.
        using (var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..16];
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The cache entry is disposable; housekeeping sweeps it later.
        }
    }
}
