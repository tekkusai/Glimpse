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
        "Task.Delay("
    ];

    [Theory]
    [InlineData(@"SpacePreview\Services\KeyboardHook.cs", "HookCallback")]
    [InlineData(@"SpacePreview\Services\MouseHook.cs", "HookCallback")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "OnKeyDown")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "OnKeyUp")]
    [InlineData(@"SpacePreview\Services\SpacePreviewController.cs", "OnMouseDown")]
    [InlineData(@"SpacePreview\Services\FullscreenInputSuspension.cs", "ShouldPassThroughInput")]
    [InlineData(@"SpacePreview\Services\FullscreenInputSuspension.cs", "Recompute")]
    [InlineData(@"SpacePreview\Services\FullscreenInputSuspension.cs", "SetState")]
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

    [Fact]
    public void IdleTrimDoesNotBlockTheDispatcherWithCompactingGc()
    {
        var body = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "RunIdleTrim");

        Assert.Contains("Task.Run", body, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForPendingFinalizers", body, StringComparison.Ordinal);
        Assert.DoesNotContain("blocking: true", body, StringComparison.Ordinal);
        Assert.DoesNotContain("compacting: true", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LargeObjectHeapCompactionMode", body, StringComparison.Ordinal);
    }

    [Fact]
    public void SpaceAndEnterStayHostOwnedWhenContentHoldsTheKeyboard()
    {
        var body = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "OnKeyDown");

        // Enter opens the file even when the embedded browser has focus;
        // Space still dismisses. The armed find bar is the only yield for
        // both, so find-box typing and next-match keep working.
        Assert.Contains("IsEscapeYieldArmed", body, StringComparison.Ordinal);
        Assert.Contains("OpenCurrent", body, StringComparison.Ordinal);
        Assert.Contains("HidePreview", body, StringComparison.Ordinal);
        Assert.Contains("TryToggleMediaPlayback", body, StringComparison.Ordinal);

        // The PDF viewer's toolbar (page box) is the only other Enter yield,
        // and only when the user's last click actually landed in that band.
        // A click in the document body must never revive dead-Enter.
        var yieldBody = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "ConsumeEnterYieldToContent");
        Assert.Contains("_lastContentClickInViewerChrome", yieldBody, StringComparison.Ordinal);
        Assert.Contains("IsPdfViewerSource", yieldBody, StringComparison.Ordinal);
        Assert.Contains("_lastContentClickInViewerChrome = false", yieldBody, StringComparison.Ordinal);
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

        // Icon-only children use the file card's icon side; real thumbnails
        // keep the large artwork treatment.
        Assert.Contains("FileCardIconSide", body, StringComparison.Ordinal);
        Assert.Contains("BuildFolderArtwork", body, StringComparison.Ordinal);
        Assert.DoesNotContain("184", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderCardRevealDoesNotWaitForAThumbnailOrItemEnumeration()
    {
        var collector = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "CollectFileCardData");
        var directoryCollector = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "CollectDirectoryCardData");
        var loader = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "LoadPreviewAsync");
        var cardBuilder = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "BuildDirectoryCard");
        var folderArtwork = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "BuildFolderArtwork");
        var scanRunner = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "RunFolderSizeScanAsync");
        var revealScheduler = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "RunAfter");

        Assert.True(
            collector.IndexOf("Directory.Exists(path)", StringComparison.Ordinal)
            < collector.IndexOf("ShellThumbnailProvider.TryGetThumbnail(path)", StringComparison.Ordinal));
        Assert.True(
            loader.IndexOf("Directory.Exists(contentPath)", StringComparison.Ordinal)
            < loader.IndexOf("FileSignatures.Detect(contentPath)", StringComparison.Ordinal));
        Assert.DoesNotContain("TryGetThumbnail", directoryCollector, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetIcon", directoryCollector, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumerateFileSystemInfos", directoryCollector, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectoryInfo", directoryCollector, StringComparison.Ordinal);
        Assert.Contains("UpdateFolderArtworkAsync", cardBuilder, StringComparison.Ordinal);
        Assert.Contains("StartFolderSizeScan", cardBuilder, StringComparison.Ordinal);
        Assert.Contains("LinearGradientBrush", folderArtwork, StringComparison.Ordinal);
        Assert.Contains("DropShadowEffect", folderArtwork, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(FolderSizeScanStartDelay, token)", scanRunner, StringComparison.Ordinal);
        Assert.Contains("_folderSizeScanGate.WaitAsync(token)", scanRunner, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Render", revealScheduler, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationReleaseVerifiesOnlyWhenKeyDownDidNotAlreadyMoveSelection()
    {
        var keyDown = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "OnKeyDown");
        var keyUp = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "OnKeyUp");
        var verification = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "RequestKeyUpSelectionVerification");
        var coalescer = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "NewestRefreshRequest");
        var modifierIntent = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "IntentionalMultiSelectionNavigation");
        var hidePreview = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "HidePreview");
        var preconvert = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "PreconvertOfficeNeighborsAsync");

        Assert.Contains("IntentionalMultiSelectionNavigation(vk)", keyDown, StringComparison.Ordinal);
        Assert.Contains("_navigationKeyRefreshTokens[vk]", keyDown, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _navigationKeyRefreshTokens[vk], 0)", keyUp, StringComparison.Ordinal);
        Assert.Contains("RequestKeyUpSelectionVerification", keyUp, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshSelectionAsync", keyUp, StringComparison.Ordinal);
        Assert.Contains("PreviewInputPolicy.ShouldVerifyNavigationRelease", verification, StringComparison.Ordinal);
        Assert.Contains("_pendingKeyUpVerification", verification, StringComparison.Ordinal);
        Assert.Contains(
            "StartedGenerationAtRelease: Interlocked.Read(ref _lastStartedRefreshGeneration)",
            keyUp,
            StringComparison.Ordinal);
        Assert.Contains("incoming.Generation > current.Generation", coalescer, StringComparison.Ordinal);
        Assert.Contains("IncludeMultiSelectionPaths = true", coalescer, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.VK_SHIFT", modifierIntent, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeMethods.VK_CONTROL", modifierIntent, StringComparison.Ordinal);
        var nativeRefreshGeneration = keyDown.IndexOf(
            "var generation = Interlocked.Increment(ref _navigationRefreshGeneration);",
            StringComparison.Ordinal);
        var previewContentGuard = keyDown.LastIndexOf(
            "if (IsForegroundActivePreview())",
            nativeRefreshGeneration,
            StringComparison.Ordinal);
        Assert.True(previewContentGuard >= 0 && previewContentGuard < nativeRefreshGeneration);
        Assert.Contains("Interlocked.Read(ref _navigationRefreshGeneration)", verification, StringComparison.Ordinal);
        Assert.Contains("verification.StartedGenerationAtRelease", verification, StringComparison.Ordinal);
        Assert.Contains("ClearNavigationRefreshTokens();", hidePreview, StringComparison.Ordinal);
        Assert.Contains("OfficeNeighborPreconvertDelay", preconvert, StringComparison.Ordinal);
        Assert.True(
            preconvert.IndexOf("Task.Delay", StringComparison.Ordinal)
            < preconvert.IndexOf("StaTask.Run", StringComparison.Ordinal));
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
