using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Web.WebView2.Core;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// A saved page is somebody else's markup, shown the moment Space is pressed.
/// These load one in a real browser on a window that is never shown, against
/// servers on this machine, and count what arrives. Nothing may.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class SavedPageNetworkTests
{
    // The preview's own handlers, in a browser with no dead proxy under
    // them, so a request they miss reaches the server and is counted. A
    // frame they cancel has opened a socket to its host by then; that is
    // the proxy's to stop, and the next test's to count.
    [Fact]
    public Task NoFrameOfASavedPageSendsARequest() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        using var server = new LoopbackServer();
        var page = WriteSavedPage(temp, server.Port, withHints: null);

        var answers = new ConcurrentDictionary<string, int>();
        var requests = await HiddenBrowser.LoadAsync(
            await HiddenBrowser.DirectEnvironment,
            new Uri(page),
            core =>
            {
                PreviewWindow.HardenForUntrustedHtml(core, page);
                // Runs after the preview's own handler, so it sees its answer.
                core.WebResourceRequested += (_, args) =>
                    answers[args.Request.Uri] = args.Response?.StatusCode ?? 0;
            },
            _ => server.RequestsAsync());

        Assert.DoesNotContain(requests, path => path != LoopbackServer.Connection);

        // A share is no server of ours to count on: the handler's answer is the proof.
        Assert.Equal(403, answers.GetValueOrDefault(ShareImage));
        Assert.Equal(403, answers.GetValueOrDefault(ShareImageInFrame));
    });

    // The dead proxy alone, under a page nothing hardened: the browser
    // Glimpse starts has no route to a server. That covers what no handler
    // can: a preconnect, the socket a frame opens before it is cancelled,
    // and a request still waiting for its handler when the preview closes.
    [Fact]
    public Task TheBrowserItselfHasNoRouteToAServer() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        using var server = new LoopbackServer();
        using var hinted = new LoopbackServer();
        var page = WriteSavedPage(temp, server.Port, hinted.Port);

        await HiddenBrowser.LoadAsync(
            await WebViewEnvironment.GetAsync(),
            new Uri(page),
            core => core.Settings.IsScriptEnabled = false,
            _ => Task.FromResult(0));

        Assert.Empty(await server.RequestsAsync());
        Assert.Empty(await hinted.RequestsAsync());
    });

    // The cut cost every page 9 ms: the browser could no longer fetch its
    // own feature configuration, and without one it makes a view in 20 ms
    // where 3.0.0 took 11. That one host goes around the proxy. Nothing
    // else may, and loopback must stay named: Chromium lets it around any
    // proxy unless told otherwise.
    [Fact]
    public void OnlyTheBrowsersOwnConfigurationHostGoesAroundTheProxy()
    {
        var arguments = WebViewEnvironment.BrowserArguments.Split(' ');
        var bypass = Assert.Single(arguments, argument => argument.StartsWith("--proxy-bypass-list=", StringComparison.Ordinal));

        Assert.Equal(
            ["<-loopback>", "config.edge.skype.com"],
            bypass["--proxy-bypass-list=".Length..].Split(';'));
        Assert.Contains("--proxy-server=http://127.0.0.1:9", arguments);
    }

    // A page Glimpse writes has no frames. Should one ever get in, it
    // loads nothing and asks for nothing, a sandboxed one included.
    [Fact]
    public Task AFrameInAPageGlimpseWritesSendsNoRequest() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        using var server = new LoopbackServer();
        var page = WriteSavedPage(temp, server.Port, withHints: null);

        var requests = await HiddenBrowser.LoadAsync(
            await HiddenBrowser.DirectEnvironment,
            new Uri(page),
            core => PreviewWindow.HardenForGeneratedHtml(core, new GeneratedHtmlDocument(page, page)),
            _ => server.RequestsAsync());

        Assert.DoesNotContain(requests, path => path != LoopbackServer.Connection);
    });

    // The proxy must not cost Markdown its pictures: the folder mapped to
    // the page's own host is answered inside the browser.
    [Fact]
    public Task AMarkdownPageStillLoadsThePicturesBesideIt() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "dot.png"), Dot);
        var document = GeneratedHtmlPreview.TryBuildMarkdown(temp.CreateFile("notes.md", "![dot](dot.png)"));
        Assert.NotNull(document);

        var width = await HiddenBrowser.LoadAsync(
            await WebViewEnvironment.GetAsync(),
            document.Source,
            core => PreviewWindow.HardenForGeneratedHtml(core, document),
            core => core.ExecuteScriptAsync("document.images[0].naturalWidth"));

        Assert.Equal("1", width);
    });

    private const string ShareImage = "file://127.0.0.1/glimpse-fixture/share.png";
    private const string ShareImageInFrame = "file://127.0.0.1/glimpse-fixture/share-in-frame.png";

    private static readonly byte[] Dot = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    /// <summary>
    /// The ways a page reaches out: from its own markup, from a frame of
    /// each kind (the old filter was blind in the sandboxed ones), and from
    /// a file on a share. Each asks for a path of its own. With
    /// <paramref name="withHints"/>, the page also preconnects, preloads
    /// and prefetches at that port: requests no handler is sure to see.
    /// </summary>
    private static string WriteSavedPage(TempDirectory temp, int port, int? withHints)
    {
        var server = $"http://127.0.0.1:{port}";
        var hints = withHints is { } other
            ? $"<link rel='preconnect' href='http://127.0.0.1:{other}/'>"
              + $"<link rel='preload' as='image' href='http://127.0.0.1:{other}/preload.png'>"
              + $"<link rel='prefetch' href='http://127.0.0.1:{other}/prefetch.bin'>"
            : string.Empty;
        temp.CreateFile("inner.html", $"<img src='{server}/local-frame.png'><img src='{ShareImageInFrame}'>");
        return temp.CreateFile("saved.html", $$"""
            <!doctype html>
            <html>
            <head>
            {{hints}}
            <link rel="stylesheet" href="{{server}}/style.css">
            <style>body { background: url({{server}}/css.png); }</style>
            </head>
            <body>
            <img src="{{server}}/top.png">
            <img src="{{ShareImage}}">
            <iframe srcdoc="&lt;img src='{{server}}/srcdoc.png'&gt;"></iframe>
            <iframe sandbox srcdoc="&lt;img src='{{server}}/srcdoc-sandbox.png'&gt;"></iframe>
            <iframe src="data:text/html,&lt;img src='{{server}}/data.png'&gt;"></iframe>
            <iframe sandbox src="data:text/html,&lt;img src='{{server}}/data-sandbox.png'&gt;"></iframe>
            <iframe src="{{server}}/frame.html"></iframe>
            <iframe src="inner.html"></iframe>
            <iframe sandbox src="inner.html?sandbox"></iframe>
            </body>
            </html>
            """);
    }

    /// <summary>
    /// A server on this machine that records every connection it gets: the
    /// request's path, or <see cref="Connection"/> for one that sent nothing.
    /// </summary>
    private sealed class LoopbackServer : IDisposable
    {
        public const string Connection = "(connection)";
        private const string Sentinel = "/sentinel";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly List<Task> _connections = [];
        private readonly CancellationTokenSource _stop = new();

        public LoopbackServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        /// <summary>
        /// Everything that arrived. A connection of our own goes in last and
        /// is answered once every one accepted before it has been read, so
        /// nothing that reached the server is still unread when this returns.
        /// </summary>
        public async Task<string[]> RequestsAsync()
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {Sentinel} HTTP/1.1\r\n\r\n"));
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await stream.ReadExactlyAsync(new byte[1], patience.Token);
            return _requests.ToArray();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var earlier = _connections.ToArray();
                    _connections.Add(ServeAsync(client, earlier));
                }
            }
            catch
            {
                // Disposed: the test is over.
            }
        }

        private async Task ServeAsync(TcpClient client, Task[] earlier)
        {
            using (client)
            {
                var stream = client.GetStream();
                var path = await ReadPathAsync(stream);
                if (path == Sentinel)
                {
                    await Task.WhenAll(earlier);
                    await stream.WriteAsync("!"u8.ToArray());
                    return;
                }

                _requests.Enqueue(path);
                var body = path == "/frame.html" ? "<img src=\"/in-frame.png\">" : string.Empty;
                try
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
                }
                catch
                {
                    // The browser hung up first; the request is counted.
                }
            }
        }

        // A request arrives with its connection; one that stays silent is a preconnect.
        private static async Task<string> ReadPathAsync(NetworkStream stream)
        {
            var buffer = new byte[2048];
            using var patience = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                var read = await stream.ReadAsync(buffer, patience.Token);
                var parts = Encoding.ASCII.GetString(buffer, 0, read).Split(' ');
                return parts.Length > 1 ? parts[1] : Connection;
            }
            catch
            {
                return Connection;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
