using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using SpacePreview.Services;

namespace SpacePreview.Preview;

/// <summary>
/// Glimpse's own PDF viewer page, backed by Mozilla PDF.js inside WebView2.
/// Serving the viewer and the single target PDF from a private virtual host
/// keeps all network traffic blocked, works identically on local drives and
/// UNC shares, performs all file I/O off the UI thread against one stable
/// source, reports rendered-page readiness instead of waiting on a blind timer,
/// and talks to the WPF page rail and Fill &amp; Sign directly by postMessage
/// with no UI Automation or toolbar pixel guessing.
/// </summary>
internal static class PdfViewerPage
{
    public const string Host = "glimpse-pdf.local";
    private const string Origin = "https://" + Host;
    private const string ViewerPath = "/viewer.html";
    private const string DocumentPath = "/document.pdf";
    internal const long InMemoryPdfLimitBytes = 16 * 1024 * 1024;
    internal const int MaxRangeChunkBytes = 2 * 1024 * 1024;

    private static readonly Lazy<byte[]> CachedViewerHtml = new(() => Encoding.UTF8.GetBytes(BuildHtml()));

    public static string AssetsDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "PdfJs");

    public static Uri SourceUri(int page = 1) =>
        new($"{Origin}{ViewerPath}?page={Math.Max(1, page)}", UriKind.Absolute);

    public static bool IsViewerUri(string? uri) =>
        !string.IsNullOrWhiteSpace(uri)
        && Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && parsed.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);

    public static bool IsViewerDocumentUri(string? uri) =>
        !string.IsNullOrWhiteSpace(uri)
        && Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && parsed.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)
        && parsed.AbsolutePath.Equals(ViewerPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the viewer may fetch <paramref name="uri"/>: only its own
    /// bundled assets, its viewer shell, the single opened document, and
    /// inline data/blob URLs. Nothing else ever leaves the browser.
    /// </summary>
    internal static bool IsAllowedResource(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
            || parsed.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && parsed.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsExternalWebUri(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        && !parsed.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Attaches the virtual host, async range-capable stable document source,
    /// and strict navigation/resource isolation for <paramref name="sourcePath"/>.
    /// Returns a disposable handle that releases any open PDF stream when the
    /// owning WebView2 is disposed.
    /// </summary>
    public static IDisposable Attach(
        CoreWebView2 core,
        string sourcePath,
        Action<string> openExternalUri,
        Action<string> onMessage)
    {
        core.Settings.IsScriptEnabled = true;
        core.Settings.IsWebMessageEnabled = true;

        var documentSource = new PdfDocumentSource(sourcePath);

        core.WebMessageReceived += (_, args) =>
        {
            // Only accept bridge messages sent directly by Glimpse's own viewer shell.
            if (!IsViewerDocumentUri(args.Source))
            {
                return;
            }

            try
            {
                onMessage(args.WebMessageAsJson);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"pdf viewer message failed: {ex.GetType().Name}: {ex.Message}");
            }
        };

        core.NavigationStarting += (_, args) =>
        {
            if (IsViewerDocumentUri(args.Uri))
            {
                return;
            }

            args.Cancel = true;
            if (args.IsUserInitiated && IsExternalWebUri(args.Uri))
            {
                openExternalUri(args.Uri);
            }
        };

        core.FrameNavigationStarting += (_, args) =>
        {
            args.Cancel = true;
            DiagnosticLog.Write($"pdf viewer blocked frame {DiagnosticLog.DescribeUri(args.Uri)}");
        };

        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated && IsExternalWebUri(args.Uri))
            {
                openExternalUri(args.Uri);
            }
        };

        core.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += async (_, args) =>
        {
            var requestUri = args.Request.Uri;
            if (!IsAllowedResource(requestUri))
            {
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                DiagnosticLog.Write($"pdf viewer blocked resource {DiagnosticLog.DescribeUri(requestUri)}");
                return;
            }

            if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var parsed)
                || !parsed.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (parsed.AbsolutePath.Equals(ViewerPath, StringComparison.OrdinalIgnoreCase))
            {
                var stream = new MemoryStream(CachedViewerHtml.Value, writable: false);
                args.Response = core.Environment.CreateWebResourceResponse(
                    stream,
                    200,
                    "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                return;
            }

            if (parsed.AbsolutePath.Equals(DocumentPath, StringComparison.OrdinalIgnoreCase))
            {
                string? rangeHeader = null;
                try
                {
                    if (args.Request.Headers.Contains("Range"))
                    {
                        rangeHeader = args.Request.Headers.GetHeader("Range");
                    }
                }
                catch
                {
                    // Absent header.
                }

                var deferral = args.GetDeferral();
                try
                {
                    var payload = await documentSource.ReadAsync(rangeHeader);
                    try
                    {
                        args.Response = payload.Body is null
                            ? core.Environment.CreateWebResourceResponse(null, payload.StatusCode, payload.ReasonPhrase, payload.Headers)
                            : core.Environment.CreateWebResourceResponse(payload.Body, payload.StatusCode, payload.ReasonPhrase, payload.Headers);
                    }
                    catch
                    {
                        payload.Body?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"pdf document async read failed {sourcePath}: {ex.GetType().Name}: {ex.Message}");
                    try
                    {
                        args.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
                    }
                    catch
                    {
                        // View closed while reading.
                    }
                }
                finally
                {
                    try
                    {
                        deferral.Complete();
                    }
                    catch
                    {
                        // View closed.
                    }
                }

                return;
            }

            var assetDeferral = args.GetDeferral();
            var absolutePath = parsed.AbsolutePath;
            try
            {
                var asset = await Task.Run(() => ReadAsset(absolutePath));
                try
                {
                    args.Response = asset is null
                        ? core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store")
                        : core.Environment.CreateWebResourceResponse(
                            new MemoryStream(asset.Value.Bytes, writable: false),
                            200,
                            "OK",
                            $"Content-Type: {asset.Value.ContentType}\r\nCache-Control: public, max-age=3600");
                }
                catch
                {
                    // View closed while reading asset.
                }
            }
            finally
            {
                try
                {
                    assetDeferral.Complete();
                }
                catch
                {
                    // View closed.
                }
            }
        };

        return documentSource;
    }

    internal static (byte[] Bytes, string ContentType)? ReadAsset(string absolutePath)
    {
        try
        {
            var relative = Uri.UnescapeDataString(absolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(AssetsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                return null;
            }

            var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".mjs" or ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".html" => "text/html; charset=utf-8",
                ".wasm" => "application/wasm",
                ".json" => "application/json",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
            return (File.ReadAllBytes(fullPath), contentType);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf asset read failed {absolutePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    internal static bool TryParseByteRange(string? header, long length, out long start, out long end)
    {
        start = 0;
        end = Math.Max(0, length - 1);
        if (string.IsNullOrWhiteSpace(header) || length <= 0)
        {
            return false;
        }

        var trimmed = header.Trim();
        if (!trimmed.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = trimmed["bytes=".Length..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();
        if (left.Length == 0)
        {
            if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, length - suffix);
            end = length - 1;
            return true;
        }

        if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length)
        {
            return false;
        }

        if (right.Length == 0)
        {
            end = length - 1;
            return true;
        }

        if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
        {
            return false;
        }

        end = Math.Min(end, length - 1);
        return true;
    }

    internal readonly record struct DocumentResponsePayload(
        Stream? Body,
        int StatusCode,
        string ReasonPhrase,
        string Headers);

    /// <summary>
    /// Holds one stable PDF source for the lifetime of a viewer instance.
    /// Small PDFs (&lt;= 16 MiB) are snapshotted into memory once off the UI
    /// thread so the source file is never locked and cannot drift; larger PDFs
    /// keep one shared open handle (preferring read-only sharing so concurrent
    /// writers cannot mix revisions) and stream complete requested byte ranges
    /// through <see cref="SharedHandleReadStream"/> without large allocations.
    /// Disposal is non-blocking so browsing away from a stalled share never
    /// freezes the UI thread.
    /// </summary>
    internal sealed class PdfDocumentSource : IDisposable
    {
        private readonly string _sourcePath;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private byte[]? _snapshot;
        private FileStream? _stream;
        private long _totalLength;
        private bool _initialized;
        private volatile bool _disposed;

        public PdfDocumentSource(string sourcePath)
        {
            _sourcePath = sourcePath;
        }

        public Task<DocumentResponsePayload> ReadAsync(string? rangeHeader) =>
            Task.Run(() => ReadSynchronized(rangeHeader));

        private static FileStream OpenStableStream(string path)
        {
            try
            {
                // Prefer read-only sharing so concurrent writers cannot mutate bytes
                // between range reads, while still allowing rename/delete and other readers.
                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 65536,
                    useAsync: false);
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020))
            {
                // Only fall back on ERROR_SHARING_VIOLATION when another process already holds a write handle.
                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 65536,
                    useAsync: false);
            }
        }

        private DocumentResponsePayload ReadSynchronized(string? rangeHeader)
        {
            if (_disposed)
            {
                return NotFound("Closed");
            }

            _gate.Wait();
            try
            {
                if (_disposed)
                {
                    ReleaseResourcesLocked();
                    return NotFound("Closed");
                }

                if (!_initialized)
                {
                    _initialized = true;
                    var stream = OpenStableStream(_sourcePath);
                    var length = stream.Length;
                    if (length <= 0)
                    {
                        stream.Dispose();
                        return NotFound("Empty");
                    }

                    _totalLength = length;
                    if (length <= InMemoryPdfLimitBytes)
                    {
                        var bytes = new byte[length];
                        var read = ReadExact(stream, bytes);
                        stream.Dispose();
                        if (read != bytes.Length)
                        {
                            Array.Resize(ref bytes, read);
                        }

                        if (_disposed)
                        {
                            return NotFound("Closed");
                        }

                        _snapshot = bytes;
                        _totalLength = bytes.Length;
                    }
                    else
                    {
                        _stream = stream;
                    }
                }

                if (_totalLength <= 0)
                {
                    return NotFound("Empty");
                }

                if (TryParseByteRange(rangeHeader, _totalLength, out var start, out var end))
                {
                    // Serve the complete requested interval without allocating a large buffer:
                    // PDF.js expects the full requested range in its 206 response.
                    var count = end - start + 1;
                    var headers =
                        "Content-Type: application/pdf\r\n"
                        + "Accept-Ranges: bytes\r\n"
                        + $"Content-Range: bytes {start}-{end}/{_totalLength}\r\n"
                        + $"Content-Length: {count}\r\n"
                        + "Cache-Control: no-store";
                    if (_snapshot is not null)
                    {
                        var slice = new MemoryStream(_snapshot, (int)start, (int)count, writable: false);
                        return new DocumentResponsePayload(slice, 206, "Partial Content", headers);
                    }

                    if (_stream is null)
                    {
                        return NotFound("Not Found");
                    }

                    var rangeStream = new SharedHandleReadStream(this, start, count);
                    return new DocumentResponsePayload(rangeStream, 206, "Partial Content", headers);
                }

                if (_snapshot is not null)
                {
                    var fullCopy = new MemoryStream(_snapshot, writable: false);
                    var headers =
                        "Content-Type: application/pdf\r\n"
                        + "Accept-Ranges: bytes\r\n"
                        + $"Content-Length: {_totalLength}\r\n"
                        + "Cache-Control: no-store";
                    return new DocumentResponsePayload(fullCopy, 200, "OK", headers);
                }

                if (_stream is null)
                {
                    return NotFound("Not Found");
                }

                var sharedView = new SharedHandleReadStream(this, 0, _totalLength);
                var fullHeaders =
                    "Content-Type: application/pdf\r\n"
                    + "Accept-Ranges: bytes\r\n"
                    + $"Content-Length: {_totalLength}\r\n"
                    + "Cache-Control: no-store";
                return new DocumentResponsePayload(sharedView, 200, "OK", fullHeaders);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"pdf document read failed {_sourcePath}: {ex.GetType().Name}: {ex.Message}");
                return NotFound("Not Found");
            }
            finally
            {
                if (_disposed)
                {
                    ReleaseResourcesLocked();
                }

                _gate.Release();
            }
        }

        internal int ReadSlice(long position, byte[] buffer, int offset, int count)
        {
            if (_disposed || count <= 0)
            {
                return 0;
            }

            _gate.Wait();
            try
            {
                if (_disposed || _stream is null || position >= _totalLength)
                {
                    return 0;
                }

                _stream.Position = position;
                var toRead = (int)Math.Min(count, _totalLength - position);
                return _stream.Read(buffer, offset, toRead);
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (_disposed)
                {
                    ReleaseResourcesLocked();
                }

                _gate.Release();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            // Never block the UI thread if a background share read currently holds _gate.
            // If immediate acquisition fails, queue guaranteed off-thread cleanup so a
            // race between worker check and release can never leave the handle open.
            if (_gate.Wait(0))
            {
                try
                {
                    ReleaseResourcesLocked();
                }
                finally
                {
                    _gate.Release();
                }
            }
            else
            {
                _ = Task.Run(() =>
                {
                    _gate.Wait();
                    try
                    {
                        ReleaseResourcesLocked();
                    }
                    finally
                    {
                        _gate.Release();
                    }
                });
            }
        }

        private void ReleaseResourcesLocked()
        {
            _stream?.Dispose();
            _stream = null;
            _snapshot = null;
        }

        private static DocumentResponsePayload NotFound(string reason) =>
            new(null, 404, reason, "Cache-Control: no-store");
    }

    /// <summary>
    /// Streams a bounded interval [rangeStart, rangeStart + rangeLength) from
    /// <see cref="PdfDocumentSource"/>'s single open handle in small reads
    /// without allocating the full range in memory.
    /// </summary>
    private sealed class SharedHandleReadStream(PdfDocumentSource owner, long rangeStart, long rangeLength) : Stream
    {
        private long _offset;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => rangeLength;
        public override long Position
        {
            get => _offset;
            set => _offset = Math.Clamp(value, 0, rangeLength);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = rangeLength - _offset;
            if (remaining <= 0 || count <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(Math.Min(count, MaxRangeChunkBytes), remaining);
            var read = owner.ReadSlice(rangeStart + _offset, buffer, offset, toRead);
            _offset += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _offset + offset,
                SeekOrigin.End => rangeLength + offset,
                _ => _offset
            };
            _offset = Math.Clamp(next, 0, rangeLength);
            return _offset;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static int ReadExact(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var chunk = stream.Read(buffer, total, buffer.Length - total);
            if (chunk <= 0)
            {
                break;
            }

            total += chunk;
        }

        return total;
    }

    internal static string BuildHtml() => $$"""
<!doctype html>
<html class="pdf-viewer">
<head>
<meta charset="utf-8">
<style>
{{GeneratedHtmlTheme.PageCss}}
* { box-sizing: border-box; }
html, body {
  margin: 0;
  width: 100%;
  min-height: 100%;
  background: var(--canvas);
  color: var(--text);
  font-size: 13px;
}
html.pdf-viewer { animation: none; }
[hidden] { display: none !important; }

#toolbar {
  position: sticky;
  top: 0;
  left: 0;
  z-index: 20;
  width: 100%;
  height: 40px;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 4px;
  padding: 0 12px;
  background: var(--chrome);
  border-bottom: 1px solid var(--border);
  user-select: none;
}

.tb-sep {
  width: 1px;
  height: 16px;
  margin: 0 6px;
  background: var(--border);
}

.tb-btn {
  all: unset;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  color: var(--text-2);
  cursor: default;
}
.tb-btn:hover { background: var(--hover); color: var(--text); }
.tb-btn:active { background: var(--pressed); }
.tb-btn.active { background: var(--hover); color: var(--accent); }
.tb-btn svg { width: 15px; height: 15px; display: block; }

#pageBoxWrap {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 12px;
  color: var(--text-2);
  font-variant-numeric: tabular-nums;
}

#pageInput {
  width: 40px;
  height: 24px;
  padding: 0 6px;
  border: 1px solid var(--raised-border);
  border-radius: 6px;
  background: var(--raised);
  color: var(--text);
  font: 600 12px var(--font-ui);
  font-variant-numeric: tabular-nums;
  text-align: center;
  outline: none;
}
#pageInput:focus { border-color: var(--accent); }

#zoomLabel {
  all: unset;
  min-width: 44px;
  height: 26px;
  padding: 0 6px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 6px;
  font-size: 12px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  color: var(--text-2);
  cursor: default;
}
#zoomLabel:hover { background: var(--hover); color: var(--text); }

#findBar {
  position: fixed;
  top: 48px;
  right: 16px;
  z-index: 30;
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 6px 8px;
  background: var(--chrome);
  border: 1px solid var(--raised-border);
  border-radius: 8px;
  box-shadow: 0 6px 20px rgba(0,0,0,0.22);
}

#findInput {
  width: 190px;
  height: 26px;
  padding: 0 8px;
  border: 1px solid var(--raised-border);
  border-radius: 6px;
  background: var(--raised);
  color: var(--text);
  font: 12.5px var(--font-ui);
  outline: none;
}
#findInput:focus { border-color: var(--accent); }

#findCount {
  min-width: 46px;
  padding: 0 4px;
  font-size: 11.5px;
  color: var(--text-3);
  text-align: center;
  font-variant-numeric: tabular-nums;
}

#viewport {
  position: relative;
  background: var(--canvas);
  outline: none;
}

#pages {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  padding: 12px 16px 24px;
  min-width: min-content;
}

.page-slot {
  position: relative;
  background: #fff;
  box-shadow: 0 1px 4px rgba(0,0,0,0.22), 0 0 0 1px var(--border);
  overflow: hidden;
  flex: 0 0 auto;
}

.page-slot canvas {
  display: block;
  width: 100%;
  height: 100%;
}

.textLayer {
  position: absolute;
  inset: 0;
  overflow: hidden;
  line-height: 1;
  text-size-adjust: none;
  forced-color-adjust: none;
  transform-origin: 0 0;
  z-index: 2;
}
.textLayer :is(span, br) {
  color: transparent;
  position: absolute;
  white-space: pre;
  cursor: text;
  transform-origin: 0% 0%;
}
/* Upstream PDF.js rotation rules so selection aligns on rotated pages. */
.textLayer[data-main-rotation="90"] {
  transform: rotate(90deg) translateY(-100%);
}
.textLayer[data-main-rotation="180"] {
  transform: rotate(180deg) translate(-100%, -100%);
}
.textLayer[data-main-rotation="270"] {
  transform: rotate(270deg) translateX(-100%);
}
.textLayer .endOfContent {
  display: block;
  position: absolute;
  inset: 100% 0 0;
  z-index: -1;
  cursor: default;
  user-select: none;
}
.textLayer mark.find-hit {
  background: rgba(78, 128, 245, 0.32);
  color: transparent;
  border-radius: 2px;
  padding: 0;
}
.textLayer mark.find-hit.current {
  background: rgba(78, 128, 245, 0.68);
}

.annotLayer {
  position: absolute;
  inset: 0;
  z-index: 3;
  pointer-events: none;
}
.annotLayer a.link-annot {
  position: absolute;
  pointer-events: auto;
  display: block;
  border-radius: 2px;
}
.annotLayer a.link-annot:hover {
  background: rgba(78, 128, 245, 0.14);
}

#passwordOverlay {
  position: fixed;
  inset: 40px 0 0 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--canvas);
  z-index: 40;
}
.pwd-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 24px 28px;
  border-radius: 12px;
  background: var(--raised);
  border: 1px solid var(--raised-border);
  width: min(340px, 86vw);
}
.pwd-title { font-size: 15px; font-weight: 600; color: var(--text); }
.pwd-note { font-size: 12.5px; color: var(--text-2); text-align: center; }
.pwd-row { display: flex; gap: 8px; width: 100%; margin-top: 4px; }
#pwdInput {
  flex: 1;
  height: 30px;
  padding: 0 10px;
  border-radius: 6px;
  border: 1px solid var(--raised-border);
  background: var(--canvas);
  color: var(--text);
  font: 13px var(--font-ui);
  outline: none;
}
#pwdInput:focus { border-color: var(--accent); }
#pwdSubmit {
  all: unset;
  height: 30px;
  padding: 0 14px;
  border-radius: 15px;
  background: var(--accent);
  color: #fff;
  font: 600 12.5px var(--font-ui);
  cursor: default;
}

#printRoot {
  display: none;
}

@media print {
  @page { margin: 0; }
  #toolbar, #findBar, #viewport, #passwordOverlay { display: none !important; }
  html, body {
    height: auto !important;
    overflow: visible !important;
    background: #fff !important;
    margin: 0 !important;
    padding: 0 !important;
    font-size: 0 !important;
    line-height: 0 !important;
  }
  #printRoot { display: block !important; font-size: 0 !important; line-height: 0 !important; }
  #printRoot img.print-page {
    display: block;
    width: 100vw;
    height: 100vh;
    object-fit: contain;
    margin: 0;
    break-after: page;
    page-break-after: always;
  }
  #printRoot img.print-page:last-child {
    break-after: auto;
    page-break-after: auto;
  }
}
</style>
</head>
<body>
<div id="toolbar" role="toolbar" aria-label="Document controls">
  <div id="pageBoxWrap">
    <input id="pageInput" type="text" inputmode="numeric" value="1" aria-label="Page">
    <span id="pageTotal">/ 1</span>
  </div>
  <div class="tb-sep"></div>
  <button class="tb-btn" id="zoomOut" title="Zoom out (Ctrl+-)" aria-label="Zoom out">
    <svg viewBox="0 0 16 16"><path d="M3.5 8h9" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>
  </button>
  <button id="zoomLabel" title="Reset zoom (Ctrl+0)">100%</button>
  <button class="tb-btn" id="zoomIn" title="Zoom in (Ctrl++)" aria-label="Zoom in">
    <svg viewBox="0 0 16 16"><path d="M8 3.5v9M3.5 8h9" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>
  </button>
  <button class="tb-btn active" id="fitWidth" title="Fit width (Ctrl+0)" aria-label="Fit width">
    <svg viewBox="0 0 16 16"><path d="M2.5 3.5v9M13.5 3.5v9M5.5 8h5M5.5 8l1.8-1.8M5.5 8l1.8 1.8M10.5 8l-1.8-1.8M10.5 8l-1.8 1.8" fill="none" stroke="currentColor" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"/></svg>
  </button>
  <div class="tb-sep"></div>
  <button class="tb-btn" id="findBtn" title="Find (Ctrl+F)" aria-label="Find">
    <svg viewBox="0 0 16 16"><circle cx="7" cy="7" r="4.2" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M10.2 10.2L13.5 13.5" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>
  </button>
  <button class="tb-btn" id="printBtn" title="Print (Ctrl+P)" aria-label="Print">
    <svg viewBox="0 0 16 16"><path d="M4.5 5.5V2.8h7v2.7M4.5 11H3a1.2 1.2 0 0 1-1.2-1.2V7A1.2 1.2 0 0 1 3 5.8h10A1.2 1.2 0 0 1 14.2 7v2.8A1.2 1.2 0 0 1 13 11h-1.5M4.5 9.2h7v4h-7z" fill="none" stroke="currentColor" stroke-width="1.25" stroke-linejoin="round"/></svg>
  </button>
</div>

<div id="findBar" hidden>
  <input id="findInput" type="text" placeholder="Find in document" aria-label="Find in document">
  <span id="findCount"></span>
  <button class="tb-btn" id="findPrev" title="Previous match" aria-label="Previous match">
    <svg viewBox="0 0 16 16"><path d="M4.5 9.8L8 6.2l3.5 3.6" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>
  </button>
  <button class="tb-btn" id="findNext" title="Next match" aria-label="Next match">
    <svg viewBox="0 0 16 16"><path d="M4.5 6.2L8 9.8l3.5-3.6" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>
  </button>
  <button class="tb-btn" id="findClose" title="Close find (Esc)" aria-label="Close find">
    <svg viewBox="0 0 16 16"><path d="M4.5 4.5l7 7M11.5 4.5l-7 7" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>
  </button>
</div>

<div id="viewport" tabindex="-1">
  <div id="pages"></div>
</div>

<div id="printRoot" aria-hidden="true"></div>

<div id="passwordOverlay" hidden>
  <div class="pwd-card">
    <div class="pwd-title">This PDF is password protected</div>
    <div class="pwd-note" id="pwdNote">Enter the password to view this document.</div>
    <div class="pwd-row">
      <input id="pwdInput" type="password" aria-label="Password">
      <button id="pwdSubmit">Unlock</button>
    </div>
  </div>
</div>

<script>
(() => {
  // PDF.js steps page.render() via requestAnimationFrame, which Chromium pauses
  // while a staged replacement WebView2 is Visibility.Hidden. Keep rendering
  // off-screen so staged swaps finish promptly, and use real vsync once visible.
  const nativeRaf = window.requestAnimationFrame.bind(window);
  const nativeCancel = window.cancelAnimationFrame.bind(window);
  const pending = new Map();
  let nextId = 1;
  window.requestAnimationFrame = cb => {
    const id = nextId++;
    if (document.visibilityState === 'visible') {
      const handle = nativeRaf(ts => { pending.delete(id); cb(ts); });
      pending.set(id, { native: true, handle });
    } else {
      const handle = setTimeout(() => { pending.delete(id); cb(performance.now()); }, 0);
      pending.set(id, { native: false, handle });
    }
    return id;
  };
  window.cancelAnimationFrame = id => {
    const entry = pending.get(id);
    if (!entry) return;
    pending.delete(id);
    if (entry.native) nativeCancel(entry.handle);
    else clearTimeout(entry.handle);
  };
})();
</script>
<script type="module">
import * as pdfjsLib from './pdf.min.mjs';

pdfjsLib.GlobalWorkerOptions.workerSrc = './pdf.worker.min.mjs';

const MIN_SCALE = 0.25;
const MAX_SCALE = 5.0;
const ZOOM_STEP = 1.15;
const RENDER_BUFFER = 1;
const EVICT_DISTANCE = 10;
const MAX_CANVAS_PIXELS = 16 * 1024 * 1024; // 64 MiB RGBA cap per page
const MAX_CANVAS_DIM = 8192;
const PRINT_SCALE = 150 / 72;
const PRINT_MAX_DIM = 2048;

const query = new URLSearchParams(location.search);
const initialPage = Math.max(1, parseInt(query.get('page') || '1', 10) || 1);

const pagesEl = document.getElementById('pages');
const printRoot = document.getElementById('printRoot');
const pageInput = document.getElementById('pageInput');
const pageTotal = document.getElementById('pageTotal');
const zoomLabel = document.getElementById('zoomLabel');
const fitWidthBtn = document.getElementById('fitWidth');
const findBar = document.getElementById('findBar');
const findInput = document.getElementById('findInput');
const findCount = document.getElementById('findCount');
const pwdOverlay = document.getElementById('passwordOverlay');
const pwdInput = document.getElementById('pwdInput');
const pwdNote = document.getElementById('pwdNote');

let pdfDoc = null;
let numPages = 0;
let initialStartPage = initialPage;
let currentPage = initialPage;
let lockedJumpPage = null;
let programmaticScrollUntil = 0;
let fitMode = 'width';
let scale = 1.0;
let basePageWidth = 612;
let basePageHeight = 792;
let readySent = false;
let readyScheduled = false;
let findOpen = false;
let findQuery = '';
let findMatches = [];
let findActiveIndex = -1;
let findToken = 0;
let resizeTimer = 0;
let renderZoomTimer = 0;
let scrollTimer = 0;
let printing = false;
let printObjectUrls = [];
let inputFocusActive = false;
let pageTextCache = new Map();
let slots = [];

function post(message) {
  try { window.chrome.webview.postMessage(message); } catch { }
}

function setInputFocus(active) {
  const next = Boolean(active);
  if (next === inputFocusActive) return;
  inputFocusActive = next;
  post({ type: 'inputFocus', active: next });
}

function isEditableTarget(el) {
  return Boolean(el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable));
}

document.addEventListener('focusin', e => {
  if (isEditableTarget(e.target)) setInputFocus(true);
});
document.addEventListener('focusout', () => {
  setTimeout(() => {
    const active = !pwdOverlay.hidden || isEditableTarget(document.activeElement);
    setInputFocus(active);
  }, 0);
});

const TOOLBAR_HEIGHT = 40;

function scrollY() {
  return window.scrollY || document.documentElement.scrollTop || 0;
}

function scrollX() {
  return window.scrollX || document.documentElement.scrollLeft || 0;
}

function viewWidth() {
  return document.documentElement.clientWidth || window.innerWidth || 800;
}

function viewHeight() {
  return Math.max(1, (document.documentElement.clientHeight || window.innerHeight || 600) - TOOLBAR_HEIGHT);
}

function isAtOpening() {
  if (fitMode !== 'width' || findOpen) return false;
  if (scrollY() > 1 || scrollX() > 1) return false;
  const sel = window.getSelection();
  if (sel && !sel.isCollapsed && sel.toString().length > 0) return false;
  return true;
}

function emitState(extra) {
  post(Object.assign({
    type: 'state',
    page: currentPage,
    totalPages: numPages,
    atOpening: isAtOpening(),
    zoom: Math.round(scale * 100),
    findOpen
  }, extra || {}));
}

function computeFitWidthScale(pageWidthPts) {
  const avail = Math.max(200, viewWidth() - 32);
  return Math.min(MAX_SCALE, Math.max(MIN_SCALE, avail / Math.max(1, pageWidthPts)));
}

function syncZoomUi() {
  zoomLabel.textContent = Math.round(scale * 100) + '%';
  fitWidthBtn.classList.toggle('active', fitMode === 'width');
}

function sizeSlot(slot) {
  const w = Math.max(1, Math.round(slot.widthPts * scale));
  const h = Math.max(1, Math.round(slot.heightPts * scale));
  slot.el.style.width = w + 'px';
  slot.el.style.height = h + 'px';
}

function createSlots(count, defaultW, defaultH) {
  pagesEl.textContent = '';
  slots = [];
  const frag = document.createDocumentFragment();
  for (let i = 1; i <= count; i++) {
    const el = document.createElement('div');
    el.className = 'page-slot';
    el.dataset.page = String(i);
    const slot = {
      num: i,
      el,
      widthPts: defaultW,
      heightPts: defaultH,
      measured: false,
      renderedScale: 0,
      renderingScale: 0,
      rendering: false,
      needsRerender: false,
      renderTask: null,
      textLayerTask: null,
      canvas: null,
      textLayer: null,
      annotLayer: null
    };
    sizeSlot(slot);
    slots.push(slot);
    frag.appendChild(el);
  }
  pagesEl.appendChild(frag);
}

function visibleRange() {
  if (slots.length === 0) return { first: 1, last: 1, center: 1 };
  const top = scrollY() + TOOLBAR_HEIGHT;
  const bottom = top + viewHeight();
  const mid = top + viewHeight() * 0.35;

  let lo = 0, hi = slots.length - 1;
  while (lo < hi) {
    const m = (lo + hi) >> 1;
    const s = slots[m].el;
    if (s.offsetTop + s.offsetHeight < top) lo = m + 1;
    else hi = m;
  }
  const first = lo + 1;
  let last = first;
  let center = first;
  let bestDist = Infinity;
  for (let i = lo; i < slots.length; i++) {
    const s = slots[i].el;
    if (s.offsetTop > bottom && i > lo) break;
    last = i + 1;
    const slotTop = s.offsetTop;
    const slotBottom = slotTop + s.offsetHeight;
    const dist = mid < slotTop ? slotTop - mid : mid > slotBottom ? mid - slotBottom : 0;
    if (dist < bestDist) {
      bestDist = dist;
      center = i + 1;
    }
  }
  return { first, last, center };
}

function evictFarPages(first, last) {
  for (let i = 0; i < slots.length; i++) {
    const n = i + 1;
    if (n < first - EVICT_DISTANCE || n > last + EVICT_DISTANCE) {
      const slot = slots[i];
      if (slot.renderTask) {
        try { slot.renderTask.cancel(); } catch { }
        slot.renderTask = null;
      }
      if (slot.textLayerTask) {
        try { slot.textLayerTask.cancel(); } catch { }
        slot.textLayerTask = null;
      }
      if (slot.canvas || slot.textLayer || slot.annotLayer) {
        if (slot.canvas) {
          slot.canvas.width = 0;
          slot.canvas.height = 0;
        }
        slot.el.textContent = '';
        slot.canvas = null;
        slot.textLayer = null;
        slot.annotLayer = null;
        slot.renderedScale = 0;
        slot.renderingScale = 0;
        slot.rendering = false;
        slot.needsRerender = false;
      }
    }
  }
}

function signalFirstReady() {
  if (readySent || readyScheduled) return;
  readyScheduled = true;
  void pagesEl.offsetHeight;
  let readyTimer = 0;
  const finish = () => {
    if (readySent) return;
    readySent = true;
    clearTimeout(readyTimer);
    post({ type: 'ready', pages: numPages, page: currentPage, rendered: true, atOpening: isAtOpening() });
    scheduleVisibleRenders();
  };
  readyTimer = setTimeout(finish, 32);
  if (document.visibilityState === 'visible') {
    requestAnimationFrame(finish);
  } else {
    setTimeout(finish, 0);
  }
}

async function renderPageSlot(slot, targetScale) {
  if (!pdfDoc) return 'failed';
  if (slot.renderedScale === targetScale && slot.canvas) {
    if (!readySent && slot.num === initialStartPage) signalFirstReady();
    return 'ok';
  }
  if (slot.renderTask) {
    try { slot.renderTask.cancel(); } catch { }
    slot.renderTask = null;
  }
  slot.rendering = true;
  slot.renderingScale = targetScale;
  slot.needsRerender = false;
  const myScale = targetScale;
  let result = 'failed';
  try {
    const page = await pdfDoc.getPage(slot.num);
    if (!pdfDoc) return 'failed';
    if (scale !== myScale) {
      result = 'superseded';
      return 'superseded';
    }

    const unscaled = page.getViewport({ scale: 1 });
    if (!slot.measured || Math.abs(slot.widthPts - unscaled.width) > 0.5 || Math.abs(slot.heightPts - unscaled.height) > 0.5) {
      slot.widthPts = unscaled.width;
      slot.heightPts = unscaled.height;
      slot.measured = true;
      sizeSlot(slot);
    }

    const cssViewport = page.getViewport({ scale: myScale });
    let renderDpr = Math.min(window.devicePixelRatio || 1, 2.5);
    const rawW = cssViewport.width * renderDpr;
    const rawH = cssViewport.height * renderDpr;
    if (rawW > MAX_CANVAS_DIM || rawH > MAX_CANVAS_DIM || rawW * rawH > MAX_CANVAS_PIXELS) {
      const shrink = Math.min(
        MAX_CANVAS_DIM / Math.max(1, rawW),
        MAX_CANVAS_DIM / Math.max(1, rawH),
        Math.sqrt(MAX_CANVAS_PIXELS / Math.max(1, rawW * rawH)));
      renderDpr = renderDpr * shrink;
    }

    const canvas = document.createElement('canvas');
    canvas.width = Math.min(MAX_CANVAS_DIM, Math.max(1, Math.floor(cssViewport.width * renderDpr)));
    canvas.height = Math.min(MAX_CANVAS_DIM, Math.max(1, Math.floor(cssViewport.height * renderDpr)));
    const ctx = canvas.getContext('2d', { alpha: false });
    if (!ctx) return 'failed';
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    const effDprX = canvas.width / Math.max(1, cssViewport.width);
    const effDprY = canvas.height / Math.max(1, cssViewport.height);
    const transform = (effDprX !== 1 || effDprY !== 1) ? [effDprX, 0, 0, effDprY, 0, 0] : null;
    const renderTask = page.render({ canvasContext: ctx, viewport: cssViewport, transform });
    slot.renderTask = renderTask;
    await renderTask.promise;
    if (slot.renderTask !== renderTask || scale !== myScale) {
      result = 'superseded';
      return 'superseded';
    }
    slot.renderTask = null;

    if (slot.textLayerTask) {
      try { slot.textLayerTask.cancel(); } catch { }
      slot.textLayerTask = null;
    }

    const textLayer = document.createElement('div');
    textLayer.className = 'textLayer';
    textLayer.style.setProperty('--scale-factor', String(cssViewport.scale));
    const rot = ((cssViewport.rotation % 360) + 360) % 360;
    if (rot !== 0) {
      textLayer.dataset.mainRotation = String(rot);
    }

    const annotLayer = document.createElement('div');
    annotLayer.className = 'annotLayer';

    slot.el.textContent = '';
    slot.el.appendChild(canvas);
    slot.el.appendChild(textLayer);
    slot.el.appendChild(annotLayer);
    slot.canvas = canvas;
    slot.textLayer = textLayer;
    slot.annotLayer = annotLayer;
    slot.renderedScale = myScale;
    result = 'ok';

    if (!readySent && slot.num === initialStartPage) {
      signalFirstReady();
    }

    // Text layer and links populate right after canvas install so reveal never waits on them.
    buildTextAndLinks(page, slot, cssViewport, myScale);
    return 'ok';
  } catch (err) {
    if (err && err.name === 'RenderingCancelledException') {
      result = 'superseded';
      return 'superseded';
    }
    if (!readySent && slot.num === initialStartPage) {
      post({ type: 'error', message: 'initial-page-render-failed' });
    }
    return 'failed';
  } finally {
    slot.rendering = false;
    slot.renderingScale = 0;
    if (slot.needsRerender && pdfDoc && slot.renderedScale !== scale) {
      slot.needsRerender = false;
      renderPageSlot(slot, scale);
    } else if (result === 'superseded' && pdfDoc && scale !== myScale) {
      scheduleVisibleRenders();
    }
  }
}

async function buildTextAndLinks(page, slot, cssViewport, myScale) {
  try {
    const textContent = await page.getTextContent();
    if (!pdfDoc || scale !== myScale || !slot.textLayer) return;
    const tl = new pdfjsLib.TextLayer({
      textContentSource: textContent,
      container: slot.textLayer,
      viewport: cssViewport
    });
    slot.textLayerTask = tl;
    await tl.render();
    if (slot.textLayerTask === tl) {
      slot.textLayerTask = null;
    }
    if (findQuery && slot.textLayer) {
      const activeHit = findActiveIndex >= 0 ? findMatches[findActiveIndex] : null;
      applyFindHighlightsToSlot(slot, Boolean(activeHit && activeHit.page === slot.num));
    }
  } catch { }

  try {
    const annots = await page.getAnnotations({ intent: 'display' });
    if (!pdfDoc || scale !== myScale || !slot.annotLayer) return;
    for (const annot of annots) {
      if (annot.subtype !== 'Link' || !annot.rect) continue;
      const rect = cssViewport.convertToViewportRectangle(annot.rect);
      const x1 = Math.min(rect[0], rect[2]);
      const y1 = Math.min(rect[1], rect[3]);
      const w = Math.abs(rect[2] - rect[0]);
      const h = Math.abs(rect[3] - rect[1]);
      if (w < 1 || h < 1) continue;

      const a = document.createElement('a');
      a.className = 'link-annot';
      a.style.left = x1 + 'px';
      a.style.top = y1 + 'px';
      a.style.width = w + 'px';
      a.style.height = h + 'px';
      if (annot.url && /^https?:\/\//i.test(annot.url)) {
        a.href = '#';
        a.title = annot.url;
        a.addEventListener('click', e => {
          e.preventDefault();
          post({ type: 'openExternal', url: annot.url });
        });
      } else if (annot.dest) {
        a.href = '#';
        a.addEventListener('click', async e => {
          e.preventDefault();
          await jumpToDestination(annot.dest);
        });
      } else {
        continue;
      }
      slot.annotLayer.appendChild(a);
    }
  } catch { }
}

async function jumpToDestination(dest) {
  if (!pdfDoc) return;
  try {
    const explicit = typeof dest === 'string' ? await pdfDoc.getDestination(dest) : dest;
    if (Array.isArray(explicit) && explicit[0]) {
      const idx = await pdfDoc.getPageIndex(explicit[0]);
      if (Number.isInteger(idx)) jumpToPage(idx + 1, 0);
    }
  } catch { }
}

function scheduleVisibleRenders() {
  if (!pdfDoc || slots.length === 0) return;
  const { first, last, center } = visibleRange();
  const effectivePage = lockedJumpPage !== null ? lockedJumpPage : center;
  if (effectivePage !== currentPage) {
    currentPage = effectivePage;
    if (document.activeElement !== pageInput) {
      pageInput.value = String(currentPage);
    }
    emitState();
  }
  const from = Math.max(1, first - RENDER_BUFFER);
  const to = Math.min(numPages, last + RENDER_BUFFER);
  evictFarPages(from, to);
  for (let p = from; p <= to; p++) {
    const slot = slots[p - 1];
    if (slot.renderedScale !== scale) {
      if (slot.rendering) {
        if (slot.renderingScale !== scale) {
          slot.needsRerender = true;
          if (slot.renderTask) {
            try { slot.renderTask.cancel(); } catch { }
          }
        }
      } else {
        renderPageSlot(slot, scale);
      }
    }
  }
}

function jumpToPage(page, jumpId) {
  if (!pdfDoc || numPages <= 0) {
    if (jumpId) {
      post({ type: 'jumpAck', id: jumpId, page: currentPage, ok: false, atOpening: isAtOpening() });
    }
    return;
  }
  const target = Math.max(1, Math.min(numPages, Math.round(page)));
  const slot = slots[target - 1];
  if (!slot) {
    if (jumpId) {
      post({ type: 'jumpAck', id: jumpId, page: currentPage, ok: false, atOpening: isAtOpening() });
    }
    return;
  }
  lockedJumpPage = target;
  currentPage = target;
  pageInput.value = String(target);
  programmaticScrollUntil = performance.now() + 200;
  window.scrollTo(scrollX(), Math.max(0, slot.el.offsetTop - TOOLBAR_HEIGHT - 12));
  scheduleVisibleRenders();
  if (jumpId) {
    post({ type: 'jumpAck', id: jumpId, page: currentPage, ok: true, atOpening: isAtOpening() });
  } else {
    emitState();
  }
}

function setScale(newScale, custom, anchorX, anchorY) {
  if (!pdfDoc) return;
  const clamped = Math.min(MAX_SCALE, Math.max(MIN_SCALE, newScale));
  if (Math.abs(clamped - scale) < 0.002 && fitMode === (custom ? 'custom' : 'width')) return;
  const ratio = clamped / scale;
  const ax = typeof anchorX === 'number' ? anchorX : viewWidth() / 2;
  const ay = typeof anchorY === 'number' ? anchorY : viewHeight() / 2;
  const nextX = (scrollX() + ax) * ratio - ax;
  const nextY = (scrollY() + ay) * ratio - ay;

  scale = clamped;
  fitMode = custom ? 'custom' : 'width';
  syncZoomUi();

  for (const slot of slots) {
    sizeSlot(slot);
  }
  programmaticScrollUntil = performance.now() + 200;
  window.scrollTo(Math.max(0, nextX), Math.max(0, nextY));
  emitState();

  clearTimeout(renderZoomTimer);
  renderZoomTimer = setTimeout(() => {
    for (const slot of slots) slot.renderedScale = 0;
    scheduleVisibleRenders();
  }, 60);
}

function zoomBy(direction, anchorX, anchorY) {
  if (direction === 0) {
    setScale(computeFitWidthScale(basePageWidth), false);
    return;
  }
  const factor = direction > 0 ? ZOOM_STEP : 1 / ZOOM_STEP;
  setScale(scale * factor, true, anchorX, anchorY);
}

// --- Find (supports cross-span phrases, multiple occurrences per run, and clean cancel) ---
function openFind() {
  findOpen = true;
  findBar.hidden = false;
  findInput.focus();
  findInput.select();
  setInputFocus(true);
  post({ type: 'find', open: true });
  emitState();
}

function closeFind() {
  findToken++;
  if (!findOpen) return;
  findOpen = false;
  findBar.hidden = true;
  findQuery = '';
  findMatches = [];
  findActiveIndex = -1;
  findCount.textContent = '';
  clearFindHighlights();
  if (document.activeElement === findInput) {
    findInput.blur();
  }
  setInputFocus(!pwdOverlay.hidden || isEditableTarget(document.activeElement));
  post({ type: 'find', open: false });
  emitState();
}

function clearFindHighlights() {
  for (const slot of slots) {
    if (!slot.textLayer) continue;
    for (const span of slot.textLayer.querySelectorAll('span')) {
      if (span.dataset.rawText !== undefined && span.querySelector('mark.find-hit')) {
        span.textContent = span.dataset.rawText;
      }
    }
  }
}

function buildNormalizedPageText(strings) {
  let lower = '';
  const ranges = [];
  for (let i = 0; i < strings.length; i++) {
    const raw = strings[i] || '';
    if (lower.length > 0 && raw.length > 0 && !/\s$/.test(lower) && !/^\s/.test(raw)) {
      lower += ' ';
    }
    const start = lower.length;
    // Map each lowercased code unit back to its exact index in raw so length-changing
    // Unicode case folds (such as Turkish 'İ' -> 'i\u0307') never shift highlight offsets.
    const charMap = [];
    for (let c = 0; c < raw.length; c++) {
      const folded = raw[c].toLowerCase();
      for (let k = 0; k < folded.length; k++) {
        charMap.push(c);
      }
      lower += folded;
    }
    ranges.push({ start, end: lower.length, charMap });
  }
  return { lower, ranges };
}

function applyFindHighlightsToSlot(slot, scrollToCurrent = false) {
  if (!slot.textLayer) return;
  const spans = Array.from(slot.textLayer.querySelectorAll('span'));
  for (const span of spans) {
    if (span.dataset.rawText === undefined) {
      span.dataset.rawText = span.textContent || '';
    } else if (span.querySelector('mark.find-hit')) {
      span.textContent = span.dataset.rawText;
    }
  }
  if (!findQuery) return;
  const pageHits = findMatches
    .map((m, idx) => ({ m, idx }))
    .filter(x => x.m.page === slot.num);
  if (pageHits.length === 0) return;

  const { ranges } = buildNormalizedPageText(spans.map(s => s.dataset.rawText || ''));
  let activeMark = null;
  for (let i = 0; i < spans.length; i++) {
    const r = ranges[i];
    if (r.end <= r.start) continue;
    const raw = spans[i].dataset.rawText || '';
    const overlaps = [];
    for (const { m, idx } of pageHits) {
      const ls = Math.max(r.start, m.start) - r.start;
      const le = Math.min(r.end, m.end) - r.start;
      if (le > ls) {
        const s = r.charMap[ls];
        const e = r.charMap[le - 1] + 1;
        overlaps.push({ s, e, current: idx === findActiveIndex });
      }
    }
    if (overlaps.length === 0) continue;
    overlaps.sort((a, b) => a.s - b.s);
    const frag = document.createDocumentFragment();
    let cursor = 0;
    for (const ov of overlaps) {
      if (ov.s < cursor) continue;
      if (ov.s > cursor) {
        frag.appendChild(document.createTextNode(raw.slice(cursor, ov.s)));
      }
      const mark = document.createElement('mark');
      mark.className = ov.current ? 'find-hit current' : 'find-hit';
      mark.textContent = raw.slice(ov.s, ov.e);
      if (ov.current && !activeMark) activeMark = mark;
      frag.appendChild(mark);
      cursor = ov.e;
    }
    if (cursor < raw.length) {
      frag.appendChild(document.createTextNode(raw.slice(cursor)));
    }
    spans[i].textContent = '';
    spans[i].appendChild(frag);
  }
  if (scrollToCurrent && activeMark) {
    programmaticScrollUntil = performance.now() + 200;
    activeMark.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
  }
}

async function runFind(term) {
  const token = ++findToken;
  findQuery = term.trim();
  findMatches = [];
  findActiveIndex = -1;
  clearFindHighlights();
  if (!findOpen || !findQuery || !pdfDoc) {
    findCount.textContent = '';
    return;
  }
  const q = findQuery.toLowerCase();
  for (let p = 1; p <= numPages; p++) {
    if (!findOpen || token !== findToken) return;
    let indexed = pageTextCache.get(p);
    if (!indexed) {
      const page = await pdfDoc.getPage(p);
      if (!findOpen || token !== findToken) return;
      const tc = await page.getTextContent();
      if (!findOpen || token !== findToken) return;
      indexed = buildNormalizedPageText(tc.items.map(it => it.str || ''));
      pageTextCache.set(p, indexed);
    }
    const hay = indexed.lower;
    let pos = 0;
    while (pos <= hay.length - q.length) {
      const found = hay.indexOf(q, pos);
      if (found < 0) break;
      findMatches.push({ page: p, start: found, end: found + q.length });
      pos = found + Math.max(1, q.length);
    }
  }
  if (!findOpen || token !== findToken) return;
  if (findMatches.length > 0) {
    findActiveIndex = 0;
    goToFindMatch(0);
  } else {
    findCount.textContent = '0 / 0';
  }
}

function goToFindMatch(index) {
  if (!findOpen || findMatches.length === 0) return;
  findActiveIndex = (index + findMatches.length) % findMatches.length;
  findCount.textContent = (findActiveIndex + 1) + ' / ' + findMatches.length;
  const hit = findMatches[findActiveIndex];
  const slot = slots[hit.page - 1];
  if (slot && slot.textLayer) {
    currentPage = hit.page;
    lockedJumpPage = hit.page;
    pageInput.value = String(hit.page);
    for (const s of slots) {
      if (s.textLayer) applyFindHighlightsToSlot(s, s === slot);
    }
    emitState();
  } else {
    jumpToPage(hit.page, 0);
    for (const s of slots) {
      if (s.textLayer) applyFindHighlightsToSlot(s, false);
    }
  }
}

// --- Print: renders every page 1..N into compressed blob images, cleans up after print ---
function cleanupPrint() {
  for (const url of printObjectUrls) {
    try { URL.revokeObjectURL(url); } catch { }
  }
  printObjectUrls = [];
  printRoot.textContent = '';
  printing = false;
}

window.addEventListener('afterprint', cleanupPrint);

async function printDocument() {
  if (!pdfDoc || printing) return;
  printing = true;
  printRoot.textContent = '';
  const scratch = document.createElement('canvas');
  const ctx = scratch.getContext('2d', { alpha: false });
  if (!ctx) {
    printing = false;
    return;
  }

  try {
    const frag = document.createDocumentFragment();
    const decodePromises = [];
    for (let p = 1; p <= numPages; p++) {
      const page = await pdfDoc.getPage(p);
      const vp1 = page.getViewport({ scale: 1 });
      const fitScale = Math.min(
        PRINT_SCALE,
        PRINT_MAX_DIM / Math.max(1, vp1.width),
        PRINT_MAX_DIM / Math.max(1, vp1.height));
      const vp = page.getViewport({ scale: Math.max(1e-6, fitScale) });
      scratch.width = Math.min(PRINT_MAX_DIM, Math.max(1, Math.floor(vp.width)));
      scratch.height = Math.min(PRINT_MAX_DIM, Math.max(1, Math.floor(vp.height)));
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, scratch.width, scratch.height);
      const printScaleX = scratch.width / Math.max(1, vp.width);
      const printScaleY = scratch.height / Math.max(1, vp.height);
      const printTransform = (printScaleX !== 1 || printScaleY !== 1) ? [printScaleX, 0, 0, printScaleY, 0, 0] : null;
      await page.render({ canvasContext: ctx, viewport: vp, transform: printTransform }).promise;

      const blob = await new Promise(resolve => scratch.toBlob(resolve, 'image/jpeg', 0.92));
      let src;
      if (blob) {
        src = URL.createObjectURL(blob);
        printObjectUrls.push(src);
      } else {
        src = scratch.toDataURL('image/png');
      }
      const img = document.createElement('img');
      img.className = 'print-page';
      img.src = src;
      frag.appendChild(img);
      decodePromises.push(img.decode());
    }
    scratch.width = 0;
    scratch.height = 0;
    printRoot.appendChild(frag);
    await Promise.all(decodePromises);
    window.print();
  } catch (err) {
    // Print failure is logged without tearing down an already readable document.
    post({ type: 'printError', message: err && err.message ? String(err.message) : 'print-failed' });
  } finally {
    cleanupPrint();
  }
}

// --- Event wiring ---
window.addEventListener('scroll', () => {
  if (performance.now() > programmaticScrollUntil) {
    lockedJumpPage = null;
  }
  if (scrollTimer) return;
  scrollTimer = setTimeout(() => {
    scrollTimer = 0;
    scheduleVisibleRenders();
    emitState();
  }, 16);
}, { passive: true });

window.addEventListener('wheel', e => {
  if (e.ctrlKey) {
    e.preventDefault();
    zoomBy(e.deltaY < 0 ? 1 : -1, e.clientX, e.clientY - TOOLBAR_HEIGHT);
  } else if (Math.abs(e.deltaY) > 0 || Math.abs(e.deltaX) > 0) {
    lockedJumpPage = null;
  }
}, { passive: false });

pageInput.addEventListener('keydown', e => {
  if (e.key === 'Enter') {
    e.preventDefault();
    const n = parseInt(pageInput.value, 10);
    if (Number.isFinite(n)) jumpToPage(n, 0);
    else pageInput.value = String(currentPage);
    pageInput.blur();
  } else if (e.key === 'Escape') {
    pageInput.value = String(currentPage);
    pageInput.blur();
  }
});
pageInput.addEventListener('focus', () => pageInput.select());
pageInput.addEventListener('blur', () => { pageInput.value = String(currentPage); });

document.getElementById('zoomIn').addEventListener('click', () => zoomBy(1));
document.getElementById('zoomOut').addEventListener('click', () => zoomBy(-1));
zoomLabel.addEventListener('click', () => zoomBy(0));
fitWidthBtn.addEventListener('click', () => zoomBy(0));
document.getElementById('findBtn').addEventListener('click', () => findOpen ? closeFind() : openFind());
document.getElementById('printBtn').addEventListener('click', () => printDocument());
document.getElementById('findPrev').addEventListener('click', () => goToFindMatch(findActiveIndex - 1));
document.getElementById('findNext').addEventListener('click', () => goToFindMatch(findActiveIndex + 1));
document.getElementById('findClose').addEventListener('click', closeFind);
findInput.addEventListener('input', () => runFind(findInput.value));
findInput.addEventListener('keydown', e => {
  if (e.key === 'Enter') {
    e.preventDefault();
    goToFindMatch(e.shiftKey ? findActiveIndex - 1 : findActiveIndex + 1);
  } else if (e.key === 'Escape') {
    e.preventDefault();
    closeFind();
  }
});

window.addEventListener('keydown', e => {
  if (e.ctrlKey && !e.altKey) {
    if (e.key === 'f' || e.key === 'F') { e.preventDefault(); openFind(); }
    else if (e.key === 'p' || e.key === 'P') { e.preventDefault(); printDocument(); }
    else if (e.key === '=' || e.key === '+') { e.preventDefault(); zoomBy(1); }
    else if (e.key === '-') { e.preventDefault(); zoomBy(-1); }
    else if (e.key === '0') { e.preventDefault(); zoomBy(0); }
  } else if (e.key === 'Escape' && findOpen) {
    e.preventDefault();
    closeFind();
  } else if (!isEditableTarget(e.target) && (e.key === 'PageDown' || e.key === 'PageUp' || e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Home' || e.key === 'End')) {
    lockedJumpPage = null;
  }
});

window.addEventListener('resize', () => {
  if (!pdfDoc || fitMode !== 'width') return;
  const next = computeFitWidthScale(basePageWidth);
  if (Math.abs(next - scale) < 0.002) return;
  const pageBefore = currentPage;
  scale = next;
  syncZoomUi();
  for (const slot of slots) sizeSlot(slot);
  programmaticScrollUntil = performance.now() + 200;
  if (isAtOpening()) {
    window.scrollTo(0, 0);
  } else if (slots[pageBefore - 1]) {
    window.scrollTo(scrollX(), Math.max(0, slots[pageBefore - 1].el.offsetTop - TOOLBAR_HEIGHT - 12));
  }
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    for (const slot of slots) slot.renderedScale = 0;
    scheduleVisibleRenders();
  }, 80);
});

// Host <-> Viewer bridge
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', event => {
    const msg = event.data;
    if (!msg || typeof msg !== 'object') return;
    if (msg.type === 'jump' && Number.isFinite(msg.page)) {
      jumpToPage(msg.page, Number.isFinite(msg.id) ? msg.id : 0);
    } else if (msg.type === 'zoom') {
      zoomBy(msg.delta || 0);
    } else if (msg.type === 'find') {
      openFind();
    } else if (msg.type === 'escape') {
      closeFind();
    } else if (msg.type === 'print') {
      printDocument();
    } else if (msg.type === 'queryState') {
      post({
        type: 'stateReply',
        id: msg.id,
        page: currentPage,
        totalPages: numPages,
        atOpening: isAtOpening(),
        zoom: Math.round(scale * 100),
        findOpen
      });
    }
  });
}

// --- Load document ---
async function openDocument() {
  const loadingTask = pdfjsLib.getDocument({
    url: './document.pdf',
    cMapUrl: './cmaps/',
    cMapPacked: true,
    standardFontDataUrl: './standard_fonts/',
    disableRange: false,
    disableStream: true,
    enableXfa: false,
    isEvalSupported: false
  });

  loadingTask.onPassword = (updatePassword, reason) => {
    pwdOverlay.hidden = false;
    pwdNote.textContent = reason === 2
      ? 'That password did not work. Try again.'
      : 'Enter the password to view this document.';
    pwdInput.value = '';
    pwdInput.focus();
    setInputFocus(true);
    const submit = () => {
      const val = pwdInput.value;
      if (!val) return;
      pwdOverlay.hidden = true;
      setInputFocus(false);
      updatePassword(val);
    };
    document.getElementById('pwdSubmit').onclick = submit;
    pwdInput.onkeydown = e => { if (e.key === 'Enter') { e.preventDefault(); submit(); } };
    // Report prompt readiness separately from rendered-page readiness so a
    // staged replacement never promotes an unrendered password prompt.
    post({ type: 'password', reason });
  };

  try {
    pdfDoc = await loadingTask.promise;
    pwdOverlay.hidden = true;
    setInputFocus(false);
    numPages = pdfDoc.numPages;
    pageTotal.textContent = '/ ' + numPages;
    const startPage = Math.max(1, Math.min(numPages, initialPage));
    initialStartPage = startPage;
    currentPage = startPage;
    pageInput.value = String(startPage);

    const firstPage = await pdfDoc.getPage(startPage);
    const vp1 = firstPage.getViewport({ scale: 1 });
    basePageWidth = vp1.width;
    basePageHeight = vp1.height;
    scale = computeFitWidthScale(basePageWidth);
    syncZoomUi();

    createSlots(numPages, basePageWidth, basePageHeight);
    if (startPage > 1 && slots[startPage - 1]) {
      lockedJumpPage = startPage;
      programmaticScrollUntil = performance.now() + 200;
      window.scrollTo(0, Math.max(0, slots[startPage - 1].el.offsetTop - TOOLBAR_HEIGHT - 12));
    }

    // First-paint readiness follows the first successful canvas install on
    // initialStartPage (fired inside renderPageSlot). A resize that supersedes
    // this attempt leaves readiness pending for the retry at the new scale;
    // only an actual render failure reports an error.
    await renderPageSlot(slots[startPage - 1], scale);
  } catch (err) {
    post({ type: 'error', message: err && err.message ? String(err.message) : 'load-failed' });
  }
}

openDocument();
</script>
</body>
</html>
""";
}
