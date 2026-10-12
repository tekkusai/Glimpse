using Microsoft.Web.WebView2.Core;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The browser replaces its helper processes by itself. Only a failure that
/// takes the page may replace a preview, a pinned one above all, with an
/// error.
/// </summary>
public sealed class BrowserProcessFailureTests
{
    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited)]
    public void LosingTheBrowserOrThePagesOwnProcessLosesThePage(CoreWebView2ProcessFailedKind failure)
    {
        foreach (var surface in Enum.GetValues<WebSurfaceKind>())
        {
            Assert.True(PreviewWindow.ProcessFailureLosesPage(failure, surface));
        }
    }

    // A graphics driver reset ends the GPU process; the browser starts
    // another and the page is still there.
    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiPluginProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.UnknownProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)]
    public void AHelperTheBrowserReplacesOrAPageThatMayAnswerAgainKeepsThePage(CoreWebView2ProcessFailedKind failure)
    {
        foreach (var surface in Enum.GetValues<WebSurfaceKind>())
        {
            Assert.False(PreviewWindow.ProcessFailureLosesPage(failure, surface));
        }
    }

    [Fact]
    public void ALostFrameCostsOnlyThePdfViewerItsPage()
    {
        const CoreWebView2ProcessFailedKind frame = CoreWebView2ProcessFailedKind.FrameRenderProcessExited;

        Assert.False(PreviewWindow.ProcessFailureLosesPage(frame, WebSurfaceKind.Page));
        Assert.False(PreviewWindow.ProcessFailureLosesPage(frame, WebSurfaceKind.SavedPage));
        Assert.True(PreviewWindow.ProcessFailureLosesPage(frame, WebSurfaceKind.PdfViewer));
    }
}
