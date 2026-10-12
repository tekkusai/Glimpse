using System.Reflection;
using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class HookPathArchitectureTests
{
    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    // Windows calls KeyboardHook.HookCallback on the keyboard hook thread;
    // the rest are the delegates it and the controller invoke there.
    private static readonly MethodBase[] KeyboardRoots =
    [
        Method(typeof(KeyboardHook), "HookCallback"),
        Method(typeof(SpacePreviewController), "OnKeyDown"),
        Method(typeof(SpacePreviewController), "OnKeyUp"),
        Method(typeof(SpacePreviewController), "ReadKey")
    ];

    private static readonly MethodBase[] MouseRoots =
    [
        Method(typeof(MouseHook), "HookCallback"),
        Method(typeof(SpacePreviewController), "QueueMouseDown"),
        Method(typeof(SpacePreviewController), "IsMouseActivationWindow")
    ];

    // Any member of these types can hold a thread.
    private static readonly string[] BlockingTypes =
    [
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileStream",
        "System.Diagnostics.Process",
        "System.Threading.Monitor",
        "System.Threading.Lock",
        "System.Threading.Mutex",
        "System.Threading.WaitHandle",
        "System.Threading.ManualResetEventSlim",
        "System.Threading.SemaphoreSlim",
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.Task`1",
        "System.Runtime.CompilerServices.TaskAwaiter",
        "System.Runtime.CompilerServices.TaskAwaiter`1",
        "System.Runtime.CompilerServices.AsyncVoidMethodBuilder",
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1",
        "System.Windows.Threading.DispatcherOperation",
        "SpacePreview.Services.ProcessIntegrityService",
        "SpacePreview.Services.StaTask"
    ];

    private static readonly string[] BlockingMethods =
    [
        "System.GC.Collect",
        "System.GC.WaitForPendingFinalizers",
        "System.Threading.Thread.Sleep",
        "System.Threading.Thread.Join",
        // Waiting for the UI thread is the one thing the hook has a thread to avoid.
        "System.Windows.Threading.Dispatcher.Invoke",
        // The picker is read only by its bounded monitor, never on the hook.
        "SpacePreview.Services.CommonFileDialogSelectionService.TryGetSelection"
    ];

    /// <summary>
    /// Everything of Glimpse a key waits for, by name. The list is checked
    /// against the compiled product, so a method added to the path fails
    /// here until someone has looked at it and written it down.
    /// </summary>
    [Fact]
    public void TheKeyboardHookThreadRunsExactlyThese()
    {
        Assert.Equal(
            [
                "CommonFileDialogSelectionMonitor.TryGetCachedSelection",
                "CommonFileDialogSelectionService.HasFileListFocus",
                "DeferredFileDialogPreviewIntentSlot.Cancel",
                "DiagnosticLog.Write",
                "ExplorerSelectionService.HasTextFocus",
                "ExplorerSelectionService.IsTyping",
                "ExplorerSelectionService.TryGetForegroundContext",
                "KeyboardHook.HookCallback",
                "NativeMethods.CallNextHookEx",
                "NativeMethods.GetAncestor",
                "NativeMethods.GetAsyncKeyState",
                "NativeMethods.GetClassName",
                "NativeMethods.GetClassNameString",
                "NativeMethods.GetForegroundRoot",
                "NativeMethods.GetForegroundWindow",
                "NativeMethods.GetGUIThreadInfo",
                "NativeMethods.GetParent",
                "NativeMethods.GetRoot",
                "NativeMethods.GetWindowProcessId",
                "NativeMethods.GetWindowThreadProcessId",
                "NativeMethods.IsKeyDown",
                "NativeMethods.IsWindow",
                "PreviewInputPolicy.IsDesktopWindowClass",
                "PreviewInputPolicy.IsHeaderIslandClass",
                "PreviewInputPolicy.IsTextEditingClass",
                "PreviewInputPolicy.ResolveForegroundContext",
                "PreviewKeyMap.Claim",
                "PreviewKeyMap.Classify",
                "PreviewKeyMap.Facts",
                "PreviewKeyMap.IsArrow",
                "PreviewKeyMap.IsMapped",
                "PreviewKeyMap.IsNavigation",
                "PreviewKeyMap.IsZoomKey",
                "PreviewKeyMap.Let",
                "PreviewKeyMap.NoModifiers",
                "PreviewKeyMap.OpenFromForeground",
                "PreviewKeyMap.Route",
                "PreviewKeyMap.RouteEnter",
                "PreviewKeyMap.RouteIdle",
                "PreviewKeyMap.RouteNavigation",
                "PreviewKeyMap.RoutePinned",
                "PreviewKeyMap.RouteSpace",
                "PreviewKeyMap.RouteVisible",
                "SpaceKeyLatch.IsRepeat",
                "SpaceKeyLatch.Press",
                "SpaceKeyLatch.Release",
                "SpaceKeyLatch.Reset",
                "SpacePreviewController.ClearPendingFileDialogPreview",
                "SpacePreviewController.HasChildFocus",
                "SpacePreviewController.OnKeyDown",
                "SpacePreviewController.OnKeyUp",
                "SpacePreviewController.ReadKey"
            ],
            HookThreadReach.Names(HookThreadReach.From(KeyboardRoots)));
    }

    [Fact]
    public void TheMouseHookThreadRunsExactlyThese()
    {
        Assert.Equal(
            [
                "MouseHook.HookCallback",
                "NativeMethods.CallNextHookEx",
                "NativeMethods.GetAncestor",
                "NativeMethods.GetForegroundWindow",
                "NativeMethods.GetRootWindowAt",
                "NativeMethods.SetForegroundWindow",
                "NativeMethods.WindowFromPoint",
                "SpacePreviewController.IsMouseActivationWindow",
                "SpacePreviewController.QueueMouseDown"
            ],
            HookThreadReach.Names(HookThreadReach.From(MouseRoots)));
    }

    /// <summary>
    /// The walk stops at a delegate, so each one a hook thread invokes is
    /// listed, and what it points to is a root above.
    /// </summary>
    [Fact]
    public void TheDelegatesTheHookThreadsInvokeAreTheRootsAbove()
    {
        Assert.Equal(
            [
                "KeyboardHook.HookCallback -> Action<Int32>",
                "KeyboardHook.HookCallback -> Func<Int32, UInt32, Int64, Boolean>",
                "SpacePreviewController.OnKeyDown -> Func<Int32, Boolean, KeyPress>"
            ],
            HookThreadReach.DelegateCalls(HookThreadReach.From(KeyboardRoots)));
        Assert.Equal(
            [
                "MouseHook.HookCallback -> Action<Int32, Int32, Int32>",
                "MouseHook.HookCallback -> Func<IntPtr, Boolean>"
            ],
            HookThreadReach.DelegateCalls(HookThreadReach.From(MouseRoots)));

        Sta.Run(() =>
        {
            var controller = new SpacePreviewController(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            try
            {
                var keyboard = Fields.Get<KeyboardHook>(controller, "_keyboardHook");
                var mouse = Fields.Get<MouseHook>(controller, "_mouseHook");

                Assert.Equal("OnKeyDown", Fields.Get<Delegate>(keyboard, "_onKeyDown").Method.Name);
                Assert.Equal("OnKeyUp", Fields.Get<Delegate>(keyboard, "_onKeyUp").Method.Name);
                Assert.Equal("ReadKey", Fields.Get<Delegate>(controller, "_readKey").Method.Name);
                Assert.Equal("QueueMouseDown", Fields.Get<Delegate>(mouse, "_onMouseDown").Method.Name);
                Assert.Equal("IsMouseActivationWindow", Fields.Get<Delegate>(mouse, "_shouldActivateWindow").Method.Name);
            }
            finally
            {
                controller.Dispose();
            }
        });
    }

    /// <summary>
    /// Windows silently removes a low-level hook whose callback overruns its
    /// timeout, and until then every key waits. Nothing a hook thread runs
    /// may block, wait for the UI thread or touch a window.
    /// </summary>
    [Fact]
    public void NothingAHookThreadRunsCanBlockOrTouchAWindow()
    {
        Assert.Empty(Offenses([.. KeyboardRoots, .. MouseRoots]));
    }

    /// <summary>
    /// The list this replaces named methods by hand and scanned their text.
    /// It passed while a bare Space ran three unlisted methods on the hook
    /// path that start a task to open a process token. The walk has to be
    /// able to see that work: it finds it where it lives now, behind the
    /// picker coming to the front.
    /// </summary>
    [Fact]
    public void TheWalkSeesTheElevationCheckTheHandKeptListMissed()
    {
        var observed = Method(typeof(SpacePreviewController), "OnCommonFileDialogObserved");

        Assert.Contains(
            "SpacePreviewController.StartElevationMismatchCheck",
            HookThreadReach.Names(HookThreadReach.From(observed)));
        Assert.Contains(
            "SpacePreviewController.StartElevationMismatchCheck calls System.Threading.Tasks.Task.Run",
            Offenses([observed]));
        Assert.DoesNotContain(observed, HookThreadReach.From([.. KeyboardRoots, .. MouseRoots]));
    }

    /// <summary>
    /// In 3.0.0 a bare Space where nothing offered a selection, and every
    /// Space in a picker, started that task. Windows never calls this hook
    /// while an elevated window has the keyboard, so no key could find what
    /// the task looked for. Nothing a key runs reaches it now, on the hook
    /// thread or on the dispatcher.
    /// </summary>
    [Fact]
    public void NoKeyStartsTheElevationCheck()
    {
        MethodBase[] keyPath =
        [
            .. KeyboardRoots,
            Method(typeof(SpacePreviewController), "OnKeyRouted"),
            Method(typeof(SpacePreviewController), "OnNavigationKeyUp")
        ];

        Assert.DoesNotContain(
            "SpacePreviewController.StartElevationMismatchCheck",
            HookThreadReach.Names(HookThreadReach.From(keyPath)));
    }

    private static List<string> Offenses(MethodBase[] roots)
    {
        var offenses = new List<string>();
        foreach (var method in HookThreadReach.From(roots))
        {
            foreach (var callee in HookThreadReach.Calls(method))
            {
                var type = callee.DeclaringType!;
                var typeName = type.IsGenericType ? type.GetGenericTypeDefinition().FullName! : type.FullName!;
                var blocking = BlockingTypes.Contains(typeName)
                    || BlockingMethods.Contains($"{typeName}.{callee.Name}");
                var window = typeName.StartsWith("SpacePreview.Preview.", StringComparison.Ordinal)
                    || (typeName.StartsWith("System.Windows.", StringComparison.Ordinal)
                        && callee.Name != "BeginInvoke");
                if (blocking || window)
                {
                    offenses.Add($"{HookThreadReach.Name(method)} calls {typeName}.{callee.Name}");
                }
            }
        }

        return offenses;
    }

    private static MethodBase Method(Type type, string name) =>
        type.GetMethod(name, Any) ?? throw new MissingMethodException(type.Name, name);

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
    // dispatcher, never waits on finalizers and never compacts the large
    // object heap.
    [Fact]
    public void IdleTrimCollectsOffTheDispatcherWithoutFinalizersOrLargeObjectCompaction()
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
    public void HoveredFileIconsMatchTheDirectCardPresentation()
    {
        var body = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "BuildHoverArtwork");

        // A hovered file is drawn by the same builder as its own card.
        Assert.Contains("BuildCardVisual(shellImage", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder's first frame is local UI: the shell icon and the size walk
    /// start from their own async methods. The walk starts when the card
    /// loads and runs off the dispatcher, so no fixed delay stands in front
    /// of it.
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
        Assert.Contains("card.Loaded +=", cardBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", scanRunner, StringComparison.Ordinal);
        Assert.Contains("Task.Run(", scanRunner, StringComparison.Ordinal);
        Assert.Contains("_folderSizeScanGate.WaitAsync(token)", scanRunner, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Render", revealScheduler, StringComparison.Ordinal);
    }

    [Fact]
    public void GiantImagesUseCacheOnlyArtworkThenAStampAwareFullDecode()
    {
        var firstFrame = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "DecodeImageFirstFrame");
        var shell = TestSource.CodeMethod(@"SpacePreview\Preview\ShellThumbnailProvider.cs", "TryGetCachedThumbnail");
        var rotate = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "RotateCurrentImage");

        Assert.Contains("_imageDecodeCache.TryGet", firstFrame, StringComparison.Ordinal);
        Assert.Contains("TryGetCachedThumbnail", firstFrame, StringComparison.Ordinal);
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
