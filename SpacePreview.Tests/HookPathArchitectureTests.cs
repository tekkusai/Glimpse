namespace SpacePreview.Tests;

public sealed class HookPathArchitectureTests
{
    private static readonly string[] ForbiddenHookPathTokens =
    [
        "File.",
        "Directory.",
        "Process.GetProcessById",
        "Process.Start",
        "GC.Collect",
        "WaitForPendingFinalizers",
        "Thread.Sleep",
        ".Wait(",
        ".Result",
        "GetAwaiter().GetResult",
        "WaitOne(",
        "Task.Delay(",
        "Task.Run(",
        "ProcessIntegrityService",
        "Process.GetProcess",
        // The picker is read only by its bounded monitor, never on the hook.
        "CommonFileDialogSelectionService.TryGetSelection",
        "StaTask.Run"
    ];

    // Windows silently removes a low-level hook whose callback overruns its
    // timeout. The scan is shallow (it does not follow callees), so every
    // method the keyboard callback runs is listed.
    [Theory]
    [InlineData(@"SpacePreview\Services\KeyboardHook.cs", "HookCallback")]
    [InlineData(@"SpacePreview\Services\MouseHook.cs", "HookCallback")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "OnKeyDown")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "CaptureKeyFacts")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "Perform")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "FollowExplorer")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "QueueOpen")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "QueuePickerOpen")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "DeferPickerOpen")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "OnKeyUp")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "TargetOf")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "OnMouseDown")]
    [InlineData(@"SpacePreview\Services\PreviewKeyMap.cs", "Route")]
    [InlineData(@"SpacePreview\Services\SpaceKeyLatch.cs", "Press")]
    public void HookPathMethodsDoNotContainBlockingWork(string relativePath, string methodName)
    {
        var body = TestSource.CodeMethod(relativePath, methodName);

        foreach (var token in ForbiddenHookPathTokens)
        {
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DiagnosticLogWriteOnlyQueues()
    {
        var body = TestSource.CodeMethod(@"SpacePreview\Services\DiagnosticLog.cs", "Write");

        Assert.Contains("Queue.TryAdd", body, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AppendAllText", body, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAllText", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Redact", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Regex", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA", body, StringComparison.Ordinal);
        Assert.DoesNotContain("lock", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait(", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".Result", body, StringComparison.Ordinal);
    }

    // The trim's one collection is blocking (IdleTrimTests). It runs off the
    // dispatcher and never waits on finalizers or compacts the large object
    // heap, so its pause stays that of a small heap.
    [Fact]
    public void IdleTrimKeepsItsCollectionShort()
    {
        var body = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "RunIdleTrim");

        Assert.Contains("Task.Run", body, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForPendingFinalizers", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LargeObjectHeapCompactionMode", body, StringComparison.Ordinal);
    }

    // Typing a page number into the PDF viewer's own box and pressing Enter
    // must jump, not open. The yield holds only after a click in that band,
    // only for the PDF viewer, and only once.
    [Fact]
    public void EnterYieldsOnlyAfterAClickInThePdfToolbar()
    {
        var window = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var yieldStart = window.IndexOf("internal bool EnterYieldsToContent =>", StringComparison.Ordinal);
        Assert.True(yieldStart >= 0);
        var yieldBody = window[yieldStart..window.IndexOf(';', yieldStart)];
        var consumeBody = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "ConsumeEnterYieldToContent");

        Assert.Contains("_lastContentClickInViewerChrome", yieldBody, StringComparison.Ordinal);
        Assert.Contains("IsPdfViewerSource", yieldBody, StringComparison.Ordinal);
        Assert.Contains("_lastContentClickInViewerChrome = false", consumeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderInteractionReturnsVerticalArrowsToPreviewBrowsing()
    {
        var activation = TestSource.CodeMethod(
            @"SpacePreview\Preview\PreviewWindow.xaml.cs",
            "ActivateForInteraction");
        var headerBranch = activation.IndexOf("if (!clickedContent)", StringComparison.Ordinal);
        var engagementReset = activation.IndexOf("_userEngagedContent = false", headerBranch, StringComparison.Ordinal);
        var branchReturn = activation.IndexOf("return;", headerBranch, StringComparison.Ordinal);

        Assert.True(headerBranch >= 0);
        Assert.True(engagementReset > headerBranch && engagementReset < branchReturn);
    }

    [Fact]
    public void TrayIconDrawsTheBrandGlint()
    {
        var body = TestSource.CodeMethod(@"SpacePreview\Services\TrayIcon.cs", "BuildVisual");

        Assert.Contains("BrandMark.SparkPath", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawRoundedRectangle", body, StringComparison.Ordinal);
    }

    [Fact]
    public void HoveredFileIconsMatchTheDirectCardPresentation()
    {
        var body = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "BuildHoverArtwork");

        // A hovered file is drawn by the same builder as its own card.
        Assert.Contains("BuildCardVisual(shellImage", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder's first frame is local UI: the shell icon and the size walk
    /// start from their own async methods, after the reveal.
    /// </summary>
    [Fact]
    public void FolderCardRevealDoesNotWaitForAThumbnailOrItemEnumeration()
    {
        var loader = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "LoadPreviewAsync");
        var cardBuilder = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "BuildDirectoryCard");
        var scanRunner = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "RunFolderSizeScanAsync");
        var revealScheduler = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "RunAfter");

        var folderBranch = loader.IndexOf("Directory.Exists(contentPath)", StringComparison.Ordinal);
        var sniff = loader.IndexOf("FileSignatures.Detect(contentPath)", StringComparison.Ordinal);
        Assert.True(folderBranch >= 0 && sniff > folderBranch);
        Assert.DoesNotContain("ShellThumbnailProvider.", cardBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderScanner.Scan", cardBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumerateFileSystemInfos", cardBuilder, StringComparison.Ordinal);
        Assert.Contains("UpdateFolderArtworkAsync", cardBuilder, StringComparison.Ordinal);
        Assert.Contains("StartFolderSizeScan", cardBuilder, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(FolderSizeScanStartDelay, token)", scanRunner, StringComparison.Ordinal);
        Assert.Contains("_folderSizeScanGate.WaitAsync(token)", scanRunner, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Render", revealScheduler, StringComparison.Ordinal);
    }

    [Fact]
    public void GiantImagesUseCacheOnlyArtworkThenAStampAwareFullDecode()
    {
        var firstFrame = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "DecodeImageFirstFrame");
        var decoder = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "TryDecodeImage");
        var shell = TestSource.CodeMethod(@"SpacePreview\Preview\ShellThumbnailProvider.cs", "TryGetCachedThumbnail");
        var rotate = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "RotateCurrentImage");

        Assert.Contains("_imageDecodeCache.TryGet", firstFrame, StringComparison.Ordinal);
        Assert.Contains("TryGetCachedThumbnail", firstFrame, StringComparison.Ordinal);
        Assert.Contains("IgnoreImageCache", decoder, StringComparison.Ordinal);
        Assert.Contains("SIIGBF_INCACHEONLY", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetThumbnail", shell, StringComparison.Ordinal);
        Assert.Contains("_imageDecodeCache.Invalidate", rotate, StringComparison.Ordinal);
    }

    [Fact]
    public void WebPreviewsRejectStaleCallbacksAndReuseKnownVideoGeometry()
    {
        var createWebView = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "CreateWebView");
        var mediaBuilder = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "BuildMediaPreviewAsync");
        var mediaMessage = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "OnMediaMessage");
        var exitFillSign = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "ExitFillSignMode");

        Assert.Contains("requestVersion == _loadVersion", createWebView, StringComparison.Ordinal);
        Assert.Contains("requestToken.IsCancellationRequested", createWebView, StringComparison.Ordinal);
        Assert.Contains("args.IsSuccess", createWebView, StringComparison.Ordinal);
        Assert.Contains("ProcessFailed", createWebView, StringComparison.Ordinal);
        Assert.Contains("initializationStarted", createWebView, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange", createWebView, StringComparison.Ordinal);
        Assert.Contains("_deferredWebFailure", createWebView, StringComparison.Ordinal);
        Assert.Contains("deferredFailure?.Invoke", exitFillSign, StringComparison.Ordinal);
        Assert.Contains("_stageFitVersion == version", mediaBuilder, StringComparison.Ordinal);
        Assert.Contains("StageFitHint.FitKind.Pixels", mediaBuilder, StringComparison.Ordinal);
        Assert.Contains("version != _loadVersion", mediaMessage, StringComparison.Ordinal);
        Assert.Contains("token.IsCancellationRequested", mediaMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowseControlsStayVisibleUntilTheWindowFinishesLeaving()
    {
        var hideBody = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "HidePreview");
        var completeHideBody = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "CompleteHide");

        Assert.DoesNotContain("SetBrowseControlsVisible(false)", hideBody, StringComparison.Ordinal);
        Assert.Contains("SetBrowseControlsVisible(false)", completeHideBody, StringComparison.Ordinal);
    }

    // A hidden window gets no WM_SIZE, so no StateChanged to move the glyph.
    [Fact]
    public void LeavingTheStageResyncsTheMaximizeButtonItself()
    {
        var completeHideBody = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "CompleteHide");

        Assert.Contains("WindowState = WindowState.Normal;", completeHideBody, StringComparison.Ordinal);
        Assert.Contains("UpdateMaximizeChrome();", completeHideBody, StringComparison.Ordinal);

        // Order is the whole fix. Restoring after Hide moves the property
        // without moving the window, so the stage returns maximized while
        // everything else says normal and the next click is spent on the
        // disagreement instead of on the user's intent.
        var restore = completeHideBody.IndexOf("WindowState = WindowState.Normal;", StringComparison.Ordinal);
        var hide = completeHideBody.IndexOf("Hide();", StringComparison.Ordinal);
        Assert.True(hide > restore, "CompleteHide must restore the stage before it hides the window.");
    }
}
