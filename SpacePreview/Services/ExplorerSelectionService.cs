using System.IO;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SpacePreview.Native;

namespace SpacePreview.Services;

internal sealed record ExplorerSelection(
    string Path,
    IntPtr ExplorerHwnd,
    int SelectedCount = 1,
    int ViewIndex = -1,
    int ViewCount = 0,
    IReadOnlyList<string>? SelectedPaths = null);

internal enum SelectionSource
{
    Explorer,
    Desktop,
    CommonFileDialog
}

internal sealed record ForegroundSelectionContext(IntPtr Hwnd, SelectionSource Source, string ClassName)
{
    public bool IsFileDialog => Source == SelectionSource.CommonFileDialog;
}

internal sealed record DesktopSelectionReaderSample(
    int Iteration,
    string FirstReader,
    double AutomationMilliseconds,
    double NativeMilliseconds,
    string? AutomationPath,
    string? NativePath,
    int AutomationCount,
    int NativeCount,
    bool Match);

internal readonly record struct DesktopSelectionSnapshot(
    string? Path,
    int SelectedCount);

internal static class DesktopSelectionReaderPolicy
{
    internal static bool CanUseNativePath(
        int selectedCount,
        int fileSystemPathCount) =>
        selectedCount == 1 && fileSystemPathCount == 1;
}

internal sealed record DesktopSelectionAbResult(
    bool Success,
    string TargetPath,
    int Iterations,
    bool SelectionRestored,
    double CacheMissMilliseconds,
    double CacheHitMedianMilliseconds,
    double FirstAutomationMilliseconds,
    double FirstNativeMilliseconds,
    double AutomationMedianMilliseconds,
    double NativeMedianMilliseconds,
    int CacheAcquisitions,
    int CacheHits,
    int CacheInvalidations,
    bool ForcedRebuildSucceeded,
    IReadOnlyList<DesktopSelectionReaderSample> Samples,
    string? FailureReason = null);

internal sealed record DesktopCurrentSelectionAbResult(
    bool Success,
    string FirstReader,
    int Iterations,
    double CacheMissMilliseconds,
    double CacheHitMedianMilliseconds,
    double FirstAutomationMilliseconds,
    double FirstNativeMilliseconds,
    double AutomationMedianMilliseconds,
    double NativeMedianMilliseconds,
    IReadOnlyList<DesktopSelectionReaderSample> Samples,
    string? FailureReason = null);

internal static class ExplorerSelectionService
{
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private static DesktopShellSessionCache? _desktopSessionCache;

    public static ExplorerSelection? TryGetForegroundSelection(
        bool ignoreTextEditing = true,
        ExplorerSelectionLookupTrace? diagnostics = null,
        bool includeViewPosition = true,
        bool includeMultiSelectionPaths = true)
    {
        var context = TryGetForegroundContext(ignoreTextEditing);
        if (context is null)
        {
            return null;
        }

        return context.Source switch
        {
            SelectionSource.Desktop => TryGetDesktopSelection(
                diagnostics,
                includeViewPosition,
                includeMultiSelectionPaths),
            // Dialog reads are pre-cached by their dedicated bounded monitor;
            // never put a cross-process UI Automation provider on Explorer's
            // shell STA fallback lane.
            SelectionSource.CommonFileDialog => null,
            _ => TryGetSelectionFromExplorerWindow(
                    context.Hwnd,
                    diagnostics,
                    includeViewPosition,
                    includeMultiSelectionPaths)
        };
    }

    public static ForegroundSelectionContext? TryGetForegroundContext(bool ignoreTextEditing = true)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return null;
        }

        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero)
        {
            root = foreground;
        }

        var className = NativeMethods.GetClassNameString(root);

        var fileDialogListHasFocus = className.Equals(
                CommonFileDialogSelectionService.DialogWindowClass,
                StringComparison.OrdinalIgnoreCase)
            && CommonFileDialogSelectionService.HasFileListFocus(root);

        return PreviewInputPolicy.ResolveForegroundContext(
            root,
            className,
            ignoreTextEditing,
            rootThreadHasTextEditingFocus: ignoreTextEditing && WindowThreadHasTextEditingFocus(root),
            commonFileDialogListHasFocus: fileDialogListHasFocus);
    }

    public static ExplorerSelection? TryGetSelectionFromExplorerWindow(
        IntPtr explorerHwnd,
        ExplorerSelectionLookupTrace? diagnostics = null,
        bool includeViewPosition = true,
        bool includeMultiSelectionPaths = true)
    {
        if (explorerHwnd == IntPtr.Zero)
        {
            diagnostics?.SetOutcome("invalid-hwnd");
            return null;
        }

        object? shell = null;
        object? windows = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                diagnostics?.SetOutcome("shell-unavailable");
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                diagnostics?.SetOutcome("shell-activation-empty");
                return null;
            }

            // Every tab is its own ShellWindows entry carrying its own tab
            // pane hwnd; the active tab is the topmost pane in the window's
            // z-order. Handles are identity where tab names can collide.
            IntPtr activeTabHwnd;
            using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.ActiveTab))
            {
                activeTabHwnd = TryGetActiveTabHwnd(explorerHwnd);
            }

            List<(object Window, IntPtr TabHwnd)> entries;
            using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.Census))
            {
                windows = ((dynamic)shell).Windows();
                entries = CollectWindowEntries(windows, explorerHwnd);
            }

            diagnostics?.SetEntryCount(entries.Count);
            if (entries.Count == 0)
            {
                diagnostics?.SetOutcome("no-entry");
                DiagnosticLog.Write($"explorer selection: no shell entry hwnd=0x{explorerHwnd.ToInt64():X} activeTab=0x{activeTabHwnd.ToInt64():X}");
                return null;
            }

            var sawActiveTab = false;
            ExplorerSelection? withoutIdentity = null;
            foreach (var (window, tabHwnd) in entries)
            {
                if (activeTabHwnd != IntPtr.Zero && tabHwnd != IntPtr.Zero)
                {
                    if (tabHwnd != activeTabHwnd)
                    {
                        continue;
                    }

                    sawActiveTab = true;
                    var active = WithViewPosition(
                        TryReadSelection(
                            (dynamic)window,
                            explorerHwnd,
                            diagnostics,
                            includeMultiSelectionPaths),
                        window,
                        includeViewPosition,
                        includeMultiSelectionPaths,
                        diagnostics);
                    if (active is not null)
                    {
                        diagnostics?.SetOutcome("active-tab");
                        DiagnosticLog.Write($"explorer selected active tab 0x{tabHwnd.ToInt64():X} path={active.Path}");
                        return active;
                    }

                    // Explorer can leave a dead duplicate entry registered for
                    // the same tab; a husk answering first must not silence a
                    // live entry behind it.
                    continue;
                }

                // Identity unavailable (older shell): first usable selection.
                withoutIdentity ??= WithViewPosition(
                    TryReadSelection(
                        (dynamic)window,
                        explorerHwnd,
                        diagnostics,
                        includeMultiSelectionPaths),
                    window,
                    includeViewPosition,
                    includeMultiSelectionPaths,
                    diagnostics);
            }

            if (sawActiveTab)
            {
                diagnostics?.SetOutcome("active-tab-empty");
                DiagnosticLog.Write($"explorer active tab 0x{activeTabHwnd.ToInt64():X} has no usable selection");
                return null;
            }

            if (withoutIdentity is not null)
            {
                diagnostics?.SetOutcome("no-tab-identity");
                DiagnosticLog.Write($"explorer selected (no tab identity) path={withoutIdentity.Path}");
                return withoutIdentity;
            }

            // The active tab has no registered entry: Explorer's ShellWindows
            // census went stale, and it stays stale until the window is
            // reopened. A single-entry window cannot be ambiguous, so its one
            // browser is the selection's only possible home; take it rather
            // than leaving the Space key dead.
            if (entries.Count == 1)
            {
                var rescued = WithViewPosition(
                    TryReadSelection(
                        (dynamic)entries[0].Window,
                        explorerHwnd,
                        diagnostics,
                        includeMultiSelectionPaths),
                    entries[0].Window,
                    includeViewPosition,
                    includeMultiSelectionPaths,
                    diagnostics);
                if (rescued is not null)
                {
                    diagnostics?.SetOutcome("stale-tab-rescue");
                    DiagnosticLog.Write($"explorer selection rescued from stale tab registration entryTab=0x{entries[0].TabHwnd.ToInt64():X} activeTab=0x{activeTabHwnd.ToInt64():X} path={rescued.Path}");
                    return rescued;
                }
            }

            diagnostics?.SetOutcome("dead-census");
            DiagnosticLog.Write($"explorer selection dead: activeTab=0x{activeTabHwnd.ToInt64():X} entries=[{DescribeEntries(entries)}]");
        }
        catch (Exception ex)
        {
            diagnostics?.SetOutcome($"exception-{ex.GetType().Name}");
            DiagnosticLog.Write($"explorer selection failed hwnd=0x{explorerHwnd.ToInt64():X}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }

        return null;
    }

    /// <summary>Every ShellWindows entry registered for one top-level window, with each entry's own tab pane hwnd.</summary>
    private static List<(object Window, IntPtr TabHwnd)> CollectWindowEntries(object windows, IntPtr explorerHwnd)
    {
        var entries = new List<(object, IntPtr)>();
        foreach (dynamic window in (IEnumerable)windows)
        {
            IntPtr hwnd;
            try
            {
                hwnd = new IntPtr(Convert.ToInt64(window.HWND));
            }
            catch
            {
                continue;
            }

            if (hwnd != explorerHwnd)
            {
                continue;
            }

            entries.Add((window, ShellBrowserIdentity.TryGetTabHwnd(window)));
        }

        return entries;
    }

    private static string DescribeEntries(List<(object Window, IntPtr TabHwnd)> entries) =>
        string.Join(", ", entries.Select(entry =>
            $"tab=0x{entry.TabHwnd.ToInt64():X}{(entry.TabHwnd != IntPtr.Zero && !NativeMethods.IsWindow(entry.TabHwnd) ? " dead" : "")}"));

    /// <summary>
    /// The topmost ShellTabWindowClass child: Explorer stacks all tab panes
    /// at the same rect and keeps the active one first in z-order (inactive
    /// panes stay WS_VISIBLE, so visibility cannot discriminate).
    /// </summary>
    private static IntPtr TryGetActiveTabHwnd(IntPtr explorerHwnd)
    {
        var child = NativeMethods.GetWindow(explorerHwnd, NativeMethods.GW_CHILD);
        while (child != IntPtr.Zero)
        {
            if (NativeMethods.GetClassNameString(child).Equals("ShellTabWindowClass", StringComparison.Ordinal))
            {
                return child;
            }

            child = NativeMethods.GetWindow(child, NativeMethods.GW_HWNDNEXT);
        }

        return IntPtr.Zero;
    }

    private static ExplorerSelection? TryReadSelection(
        dynamic window,
        IntPtr explorerHwnd,
        ExplorerSelectionLookupTrace? diagnostics = null,
        bool includeMultiSelectionPaths = true)
    {
        try
        {
            dynamic document;
            dynamic selectedItems;
            int count;
            using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.Selection))
            {
                document = window.Document;
                selectedItems = document.SelectedItems();
                count = Convert.ToInt32(selectedItems.Count);
            }

            if (count <= 0)
            {
                return TryReadSelectionFromView(
                    window,
                    explorerHwnd,
                    "empty shell selection",
                    includeMultiSelectionPaths,
                    diagnostics);
            }

            string? path = null;
            string? fallbackReason = null;
            using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.Selection))
            {
                dynamic firstItem = selectedItems.Item(0);
                path = firstItem.Path as string;
                if (string.IsNullOrWhiteSpace(path))
                {
                    fallbackReason = "empty shell path";
                }
                else
                {
                    path = Environment.ExpandEnvironmentVariables(path);
                    if (!File.Exists(path) && !Directory.Exists(path))
                    {
                        fallbackReason = "non-filesystem shell path";
                    }
                }
            }

            return fallbackReason is null
                ? new ExplorerSelection(path!, explorerHwnd, count)
                : TryReadSelectionFromView(
                    window,
                    explorerHwnd,
                    fallbackReason,
                    includeMultiSelectionPaths,
                    diagnostics);
        }
        catch (Exception ex)
        {
            return TryReadSelectionFromView(
                window,
                explorerHwnd,
                ex.GetType().Name,
                includeMultiSelectionPaths,
                diagnostics);
        }
    }

    private static ExplorerSelection? TryReadSelectionFromView(
        object window,
        IntPtr explorerHwnd,
        string reason,
        bool includeMultiSelectionPaths,
        ExplorerSelectionLookupTrace? diagnostics = null)
    {
        try
        {
            using var viewTiming = ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.View);
            var paths = includeMultiSelectionPaths
                ? FolderViewNavigator.TryGetSelectedPaths(window)
                : FolderViewNavigator.TryGetFirstSelectedPath(window) is { } firstPath
                    ? new[] { firstPath }
                    : null;
            if (paths is null || paths.Count == 0)
            {
                DiagnosticLog.Write($"explorer selection fallback empty reason={reason}");
                return null;
            }

            var path = Environment.ExpandEnvironmentVariables(paths[0]);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                DiagnosticLog.Write($"explorer selection fallback non-filesystem reason={reason}");
                return null;
            }

            DiagnosticLog.Write($"explorer selection fallback reason={reason} mode={(includeMultiSelectionPaths ? "ordered" : "path-only")} path={path}");
            return new ExplorerSelection(
                path,
                explorerHwnd,
                paths.Count,
                SelectedPaths: includeMultiSelectionPaths ? paths : null);
        }
        catch (Exception fallbackEx)
        {
            DiagnosticLog.Write($"explorer selection fallback failed reason={reason}: {fallbackEx.GetType().Name}: {fallbackEx.Message}");
            return null;
        }
    }

    public static ExplorerSelection? TryGetDesktopSelection(
        ExplorerSelectionLookupTrace? diagnostics = null,
        bool includeViewPosition = true,
        bool includeMultiSelectionPaths = true)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            DesktopShellSession? transientSession = null;
            try
            {
                DesktopShellSession? session;
                string sessionState;
                using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.Census))
                {
                    if (StaTask.IsWorkerThread)
                    {
                        _desktopSessionCache ??= new DesktopShellSessionCache();
                        session = _desktopSessionCache.GetOrCreate(out sessionState);
                    }
                    else if (DesktopShellSession.TryCreate(
                                 out transientSession,
                                 out var failureReason))
                    {
                        session = transientSession;
                        sessionState = "transient";
                    }
                    else
                    {
                        session = null;
                        sessionState = $"acquire-failed-{failureReason}";
                    }
                }

                if (session is null)
                {
                    diagnostics?.SetEntryCount(0);
                    diagnostics?.SetOutcome($"desktop-{sessionState}");
                    return null;
                }

                diagnostics?.SetEntryCount(1);
                var snapshot = ReadPreferredDesktopSelectionSnapshot(
                    session.Desktop,
                    diagnostics);
                if (snapshot.SelectedCount <= 0)
                {
                    diagnostics?.SetOutcome("desktop-selection-empty");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(snapshot.Path))
                {
                    diagnostics?.SetOutcome("desktop-path-empty");
                    return null;
                }

                var path = Environment.ExpandEnvironmentVariables(snapshot.Path);
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    diagnostics?.SetOutcome("desktop-path-nonfilesystem");
                    return null;
                }

                var selection = WithViewPosition(
                    new ExplorerSelection(path, session.Hwnd, snapshot.SelectedCount),
                    session.Desktop,
                    includeViewPosition,
                    includeMultiSelectionPaths,
                    diagnostics);
                diagnostics?.SetOutcome(selection is null ? "desktop-view-empty" : "desktop");
                return selection;
            }
            catch (Exception ex) when (IsRecoverableDesktopSessionFailure(ex) && attempt == 0)
            {
                if (StaTask.IsWorkerThread)
                {
                    _desktopSessionCache?.Invalidate($"read-{ex.GetType().Name}");
                }

                DiagnosticLog.Write(
                    $"desktop shell session read failed, rebuilding once: {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                if (StaTask.IsWorkerThread)
                {
                    _desktopSessionCache?.Invalidate($"read-{ex.GetType().Name}");
                }

                diagnostics?.SetOutcome($"desktop-exception-{ex.GetType().Name}");
                DiagnosticLog.Write($"desktop selection failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                transientSession?.Dispose();
            }
        }

        diagnostics?.SetOutcome("desktop-retry-exhausted");
        return null;
    }

    private static DesktopSelectionSnapshot ReadDesktopAutomationSnapshot(
        object desktop,
        ExplorerSelectionLookupTrace? diagnostics = null)
    {
        object? document = null;
        object? selectedItems = null;
        object? firstItem = null;
        try
        {
            using var selectionTiming = ExplorerSelectionLookupTrace.Track(
                diagnostics,
                ExplorerSelectionLookupPhase.Selection);
            document = ((dynamic)desktop).Document
                ?? throw new COMException("Desktop Document returned null.");
            selectedItems = ((dynamic)document).SelectedItems()
                ?? throw new COMException("Desktop SelectedItems returned null.");
            var count = Convert.ToInt32(((dynamic)selectedItems).Count);
            if (count <= 0)
            {
                return new DesktopSelectionSnapshot(null, 0);
            }

            firstItem = ((dynamic)selectedItems).Item(0);
            var path = firstItem is null ? null : ((dynamic)firstItem).Path as string;
            return new DesktopSelectionSnapshot(path, count);
        }
        finally
        {
            DesktopShellSession.ReleaseComObject(firstItem);
            DesktopShellSession.ReleaseComObject(selectedItems);
            DesktopShellSession.ReleaseComObject(document);
        }
    }

    private static DesktopSelectionSnapshot ReadPreferredDesktopSelectionSnapshot(
        object desktop,
        ExplorerSelectionLookupTrace? diagnostics = null)
    {
        FolderViewSelectionSnapshot? native;
        using (ExplorerSelectionLookupTrace.Track(
                   diagnostics,
                   ExplorerSelectionLookupPhase.Selection))
        {
            native = FolderViewNavigator.TryGetSelectionSnapshot(desktop);
        }

        if (native is { SelectedCount: 0 })
        {
            return new DesktopSelectionSnapshot(null, 0);
        }

        if (native is not null
            && DesktopSelectionReaderPolicy.CanUseNativePath(
                native.SelectedCount,
                native.FileSystemPaths.Count))
        {
            return new DesktopSelectionSnapshot(native.FileSystemPaths[0], 1);
        }

        // Preserve the current automation semantics for multi-selection
        // anchor order, virtual Shell items, and Windows builds where the
        // native view service is unavailable. The fallback is still fresh.
        return ReadDesktopAutomationSnapshot(desktop, diagnostics);
    }

    private static bool IsRecoverableDesktopSessionFailure(Exception exception) =>
        exception is COMException
        or InvalidComObjectException
        or ObjectDisposedException;

    internal static Task ReleaseDesktopSessionAsync()
    {
        // Always enqueue behind any outstanding selection work. A UI-thread
        // null check could race a queued lookup that creates the cache later.
        return StaTask.Run(() =>
        {
            _desktopSessionCache?.Dispose();
            _desktopSessionCache = null;
            return true;
        });
    }

    /// <summary>
    /// Dev-only live A/B. It selects one existing desktop file, alternates the
    /// automation and native readers, restores the prior selection, then
    /// forces one cache rebuild and verifies the restored state survived.
    /// </summary>
    internal static DesktopSelectionAbResult RunDesktopSelectionAbTest(
        string targetPath,
        int iterations)
    {
        if (!StaTask.IsWorkerThread)
        {
            throw new InvalidOperationException("Desktop selection A/B must run on the Shell STA.");
        }

        iterations = Math.Clamp(iterations, 2, 50);
        targetPath = Path.GetFullPath(targetPath);
        _desktopSessionCache?.Dispose();
        _desktopSessionCache = new DesktopShellSessionCache();
        var cache = _desktopSessionCache!;
        var samples = new List<DesktopSelectionReaderSample>(iterations);
        var cacheHitTimes = new List<double>(iterations);
        var automationTimes = new List<double>(iterations);
        var nativeTimes = new List<double>(iterations);
        var selectionChanged = false;
        var selectionRestored = false;
        var firstAutomationMilliseconds = 0d;
        var firstNativeMilliseconds = 0d;
        string? failureReason = null;
        var cacheMissTimer = Stopwatch.StartNew();
        var session = cache.GetOrCreate(out var cacheState);
        var cacheMissMilliseconds = cacheMissTimer.Elapsed.TotalMilliseconds;
        if (session is null)
        {
            return Failure($"desktop session {cacheState}");
        }

        var viewPaths = FolderViewNavigator.TryGetViewPaths(session.Desktop, 4096);
        if (viewPaths is null)
        {
            return Failure("desktop IFolderView did not expose view paths");
        }

        var targetIndex = IndexOfPath(viewPaths, targetPath);
        if (targetIndex < 0)
        {
            return Failure("target path is not present in the desktop view");
        }

        var previous = FolderViewNavigator.TryGetSelectionSnapshot(session.Desktop);
        if (previous is null)
        {
            return Failure("native reader could not capture the prior desktop selection");
        }

        var previousPaths = previous.FileSystemPaths.ToArray();
        if (previous.SelectedCount != previousPaths.Length)
        {
            return Failure("prior selection contains virtual items and cannot be restored exactly");
        }

        try
        {
            if (!FolderViewNavigator.TrySelectIndex(session.Desktop, targetIndex))
            {
                failureReason = "could not select the benchmark target";
            }
            else
            {
                selectionChanged = true;
                var committed = false;
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    var (automation, automationMilliseconds) = MeasureAutomation(session.Desktop);
                    var (native, nativeMilliseconds) = MeasureNative(session.Desktop);
                    if (attempt == 0)
                    {
                        firstAutomationMilliseconds = automationMilliseconds;
                        firstNativeMilliseconds = nativeMilliseconds;
                    }

                    if (automation.SelectedCount == 1
                        && PathEquals(automation.Path, targetPath)
                        && native is { SelectedCount: 1 }
                        && PathEquals(native.FileSystemPaths.FirstOrDefault(), targetPath))
                    {
                        committed = true;
                        break;
                    }

                    Thread.Sleep(25);
                }

                if (!committed)
                {
                    failureReason = "desktop selection did not commit consistently to both readers";
                }
                else
                {
                    // One unmeasured warm-up per reader keeps COM activation
                    // out of the comparison; cache-miss cost is separate.
                    _ = ReadDesktopAutomationSnapshot(session.Desktop);
                    _ = FolderViewNavigator.TryGetSelectionSnapshot(session.Desktop);

                    for (var iteration = 0; iteration < iterations; iteration++)
                    {
                        var hitTimer = Stopwatch.StartNew();
                        var cached = cache.GetOrCreate(out var hitState);
                        cacheHitTimes.Add(hitTimer.Elapsed.TotalMilliseconds);
                        if (!ReferenceEquals(cached, session) || hitState != "hit")
                        {
                            failureReason = $"cache identity changed during A/B ({hitState})";
                            break;
                        }

                        DesktopSelectionSnapshot automation;
                        FolderViewSelectionSnapshot? native;
                        double automationMilliseconds;
                        double nativeMilliseconds;
                        var automationFirst = iteration % 2 == 0;
                        if (automationFirst)
                        {
                            (automation, automationMilliseconds) = MeasureAutomation(session.Desktop);
                            (native, nativeMilliseconds) = MeasureNative(session.Desktop);
                        }
                        else
                        {
                            (native, nativeMilliseconds) = MeasureNative(session.Desktop);
                            (automation, automationMilliseconds) = MeasureAutomation(session.Desktop);
                        }

                        automationTimes.Add(automationMilliseconds);
                        nativeTimes.Add(nativeMilliseconds);
                        var nativePath = native?.FileSystemPaths.FirstOrDefault();
                        var match = native is not null
                            && automation.SelectedCount == native.SelectedCount
                            && PathEquals(automation.Path, nativePath)
                            && PathEquals(automation.Path, targetPath);
                        samples.Add(new DesktopSelectionReaderSample(
                            iteration,
                            automationFirst ? "automation" : "native",
                            automationMilliseconds,
                            nativeMilliseconds,
                            automation.Path,
                            nativePath,
                            automation.SelectedCount,
                            native?.SelectedCount ?? -1,
                            match));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failureReason = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (selectionChanged)
            {
                selectionRestored = RestoreDesktopSelection(
                    session.Desktop,
                    viewPaths,
                    previousPaths,
                    targetIndex);
            }
        }

        var forcedRebuildSucceeded = false;
        if (selectionRestored)
        {
            cache.Invalidate("benchmark-forced-rebuild");
            var rebuilt = cache.GetOrCreate(out var rebuildState);
            if (rebuilt is not null && rebuildState == "miss")
            {
                var rebuiltSnapshot = FolderViewNavigator.TryGetSelectionSnapshot(rebuilt.Desktop);
                forcedRebuildSucceeded = SelectionMatches(
                    rebuiltSnapshot,
                    previous.SelectedCount,
                    previousPaths);
            }
        }

        var allSamplesMatch = samples.Count == iterations && samples.All(sample => sample.Match);
        var success = failureReason is null
            && allSamplesMatch
            && selectionRestored
            && forcedRebuildSucceeded;
        return new DesktopSelectionAbResult(
            success,
            targetPath,
            iterations,
            selectionRestored,
            cacheMissMilliseconds,
            Median(cacheHitTimes),
            firstAutomationMilliseconds,
            firstNativeMilliseconds,
            Median(automationTimes),
            Median(nativeTimes),
            cache.AcquisitionCount,
            cache.HitCount,
            cache.InvalidationCount,
            forcedRebuildSucceeded,
            samples,
            failureReason ?? (!allSamplesMatch ? "one or more reader samples disagreed" : null));

        DesktopSelectionAbResult Failure(string reason) =>
            new(
                false,
                targetPath,
                iterations,
                SelectionRestored: !selectionChanged || selectionRestored,
                cacheMissMilliseconds,
                Median(cacheHitTimes),
                firstAutomationMilliseconds,
                firstNativeMilliseconds,
                Median(automationTimes),
                Median(nativeTimes),
                cache.AcquisitionCount,
                cache.HitCount,
                cache.InvalidationCount,
                ForcedRebuildSucceeded: false,
                samples,
                reason);
    }

    /// <summary>
    /// Dev-only reverse-order control. The caller prepares and restores the
    /// desktop selection; this process acquires a cold cache and immediately
    /// reads the already-selected item, with either reader deliberately first.
    /// </summary>
    internal static DesktopCurrentSelectionAbResult RunCurrentDesktopSelectionAbTest(
        int iterations,
        bool nativeFirst)
    {
        if (!StaTask.IsWorkerThread)
        {
            throw new InvalidOperationException("Desktop selection A/B must run on the Shell STA.");
        }

        iterations = Math.Clamp(iterations, 2, 50);
        _desktopSessionCache?.Dispose();
        _desktopSessionCache = new DesktopShellSessionCache();
        var cache = _desktopSessionCache;
        var missTimer = Stopwatch.StartNew();
        var session = cache.GetOrCreate(out var cacheState);
        var cacheMissMilliseconds = missTimer.Elapsed.TotalMilliseconds;
        if (session is null)
        {
            return new DesktopCurrentSelectionAbResult(
                false,
                nativeFirst ? "native" : "automation",
                iterations,
                cacheMissMilliseconds,
                0,
                0,
                0,
                0,
                0,
                [],
                $"desktop session {cacheState}");
        }

        var samples = new List<DesktopSelectionReaderSample>(iterations);
        var cacheHitTimes = new List<double>(iterations);
        var automationTimes = new List<double>(iterations);
        var nativeTimes = new List<double>(iterations);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var hitTimer = Stopwatch.StartNew();
            var cached = cache.GetOrCreate(out var hitState);
            cacheHitTimes.Add(hitTimer.Elapsed.TotalMilliseconds);
            if (!ReferenceEquals(cached, session) || hitState != "hit")
            {
                return BuildResult($"cache identity changed during A/B ({hitState})");
            }

            var readNativeFirst = iteration == 0 ? nativeFirst : iteration % 2 == 0;
            DesktopSelectionSnapshot automation;
            FolderViewSelectionSnapshot? native;
            double automationMilliseconds;
            double nativeMilliseconds;
            if (readNativeFirst)
            {
                (native, nativeMilliseconds) = MeasureNative(session.Desktop);
                (automation, automationMilliseconds) = MeasureAutomation(session.Desktop);
            }
            else
            {
                (automation, automationMilliseconds) = MeasureAutomation(session.Desktop);
                (native, nativeMilliseconds) = MeasureNative(session.Desktop);
            }

            automationTimes.Add(automationMilliseconds);
            nativeTimes.Add(nativeMilliseconds);
            var nativePath = native?.FileSystemPaths.FirstOrDefault();
            var match = native is not null
                && automation.SelectedCount > 0
                && automation.SelectedCount == native.SelectedCount
                && PathEquals(automation.Path, nativePath);
            samples.Add(new DesktopSelectionReaderSample(
                iteration,
                readNativeFirst ? "native" : "automation",
                automationMilliseconds,
                nativeMilliseconds,
                automation.Path,
                nativePath,
                automation.SelectedCount,
                native?.SelectedCount ?? -1,
                match));
        }

        return BuildResult(samples.All(sample => sample.Match) ? null : "reader mismatch");

        DesktopCurrentSelectionAbResult BuildResult(string? failureReason)
        {
            var first = samples.FirstOrDefault();
            return new DesktopCurrentSelectionAbResult(
                failureReason is null && samples.Count == iterations,
                nativeFirst ? "native" : "automation",
                iterations,
                cacheMissMilliseconds,
                Median(cacheHitTimes),
                first?.AutomationMilliseconds ?? 0,
                first?.NativeMilliseconds ?? 0,
                Median(automationTimes),
                Median(nativeTimes),
                samples,
                failureReason);
        }
    }

    private static (DesktopSelectionSnapshot Snapshot, double Milliseconds) MeasureAutomation(
        object desktop)
    {
        var timer = Stopwatch.StartNew();
        var snapshot = ReadDesktopAutomationSnapshot(desktop);
        return (snapshot, timer.Elapsed.TotalMilliseconds);
    }

    private static (FolderViewSelectionSnapshot? Snapshot, double Milliseconds) MeasureNative(
        object desktop)
    {
        var timer = Stopwatch.StartNew();
        var snapshot = FolderViewNavigator.TryGetSelectionSnapshot(desktop);
        return (snapshot, timer.Elapsed.TotalMilliseconds);
    }

    private static bool RestoreDesktopSelection(
        object desktop,
        IReadOnlyList<string> viewPaths,
        IReadOnlyList<string> previousPaths,
        int targetIndex)
    {
        if (previousPaths.Count == 0)
        {
            if (!FolderViewNavigator.TryDeselectIndex(desktop, targetIndex))
            {
                return false;
            }

            return FolderViewNavigator.TryGetSelectionSnapshot(desktop) is { SelectedCount: 0 };
        }

        var indexes = previousPaths.Select(path => IndexOfPath(viewPaths, path)).ToArray();
        if (indexes.Any(index => index < 0)
            || !FolderViewNavigator.TrySelectIndex(desktop, indexes[0]))
        {
            return false;
        }

        for (var i = 1; i < indexes.Length; i++)
        {
            if (!FolderViewNavigator.TryAddToSelection(desktop, indexes[i]))
            {
                return false;
            }
        }

        return SelectionMatches(
            FolderViewNavigator.TryGetSelectionSnapshot(desktop),
            previousPaths.Count,
            previousPaths);
    }

    private static bool SelectionMatches(
        FolderViewSelectionSnapshot? snapshot,
        int expectedCount,
        IReadOnlyList<string> expectedPaths)
    {
        if (snapshot is null || snapshot.SelectedCount != expectedCount)
        {
            return false;
        }

        var actual = snapshot.FileSystemPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return actual.SetEquals(expectedPaths);
    }

    private static int IndexOfPath(IReadOnlyList<string> paths, string targetPath)
    {
        for (var index = 0; index < paths.Count; index++)
        {
            if (PathEquals(paths[index], targetPath))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool PathEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    /// <summary>
    /// Moves the folder view's selection by <paramref name="delta"/> in the
    /// view's own display order and reports what is now selected. This talks
    /// to Explorer directly through its view object, so it works no matter
    /// which window holds the keyboard; Explorer's list ignores synthetic
    /// key presses entirely.
    /// </summary>
    public static ExplorerSelection? TryBrowseRelative(IntPtr explorerHwnd, bool isDesktop, int delta)
    {
        object? shell = null;
        object? windows = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            windows = ((dynamic)shell).Windows();
            var targetHwnd = explorerHwnd;
            var entry = isDesktop
                ? TryFindDesktopEntry(windows, out targetHwnd)
                : TryFindWindowEntry(windows, explorerHwnd);
            if (entry is null)
            {
                DiagnosticLog.Write($"browse: no shell window entry hwnd=0x{explorerHwnd.ToInt64():X} desktop={isDesktop}");
                return null;
            }

            var startPosition = FolderViewNavigator.TryGetPosition(entry);
            void RestoreStart()
            {
                if (startPosition is { } start)
                {
                    FolderViewNavigator.TrySelectIndex(entry, start.Index);
                }
            }

            var moved = FolderViewNavigator.TrySelectRelative(entry, delta);
            if (moved is null)
            {
                DiagnosticLog.Write($"browse: folder view unavailable hwnd=0x{explorerHwnd.ToInt64():X}");
                return null;
            }

            var selection = TryReadSelection(entry, targetHwnd);
            if (selection is not null)
            {
                return selection with { ViewIndex = moved.Value.Index, ViewCount = moved.Value.Count };
            }

            var direction = Math.Sign(delta);
            var index = moved.Value.Index;
            var count = moved.Value.Count;
            while (direction != 0)
            {
                var nextIndex = index + direction;
                if (nextIndex < 0 || nextIndex >= count)
                {
                    DiagnosticLog.Write($"browse: skipped non-previewable item at folder end index={index + 1} of {count}");
                    RestoreStart();
                    return null;
                }

                var skipped = FolderViewNavigator.TrySelectRelative(entry, direction);
                if (skipped is null || skipped.Value.Index == index)
                {
                    DiagnosticLog.Write($"browse: could not skip non-previewable item index={index + 1} of {count}");
                    RestoreStart();
                    return null;
                }

                index = skipped.Value.Index;
                count = skipped.Value.Count;
                selection = TryReadSelection(entry, targetHwnd);
                if (selection is not null)
                {
                    DiagnosticLog.Write($"browse: skipped non-previewable shell item -> {selection.Path}");
                    return selection with { ViewIndex = index, ViewCount = count };
                }
            }

            RestoreStart();
            return null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"browse failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }
    }

    public static IReadOnlyList<string>? TryGetNeighborPaths(IntPtr explorerHwnd, bool isDesktop, int currentIndex, int maxNeighbors)
    {
        object? shell = null;
        object? windows = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            windows = ((dynamic)shell).Windows();
            var entry = isDesktop
                ? TryFindDesktopEntry(windows, out _)
                : TryFindWindowEntry(windows, explorerHwnd);

            return entry is null
                ? null
                : FolderViewNavigator.TryGetNeighborPaths(entry, currentIndex, maxNeighbors);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"neighbor lookup failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }
    }

    /// <summary>Renaming handles the arrow keys; browsing must stand down.</summary>
    public static bool IsWindowTextEditing(IntPtr rootHwnd) =>
        rootHwnd != IntPtr.Zero && WindowThreadHasTextEditingFocus(rootHwnd);

    private static ExplorerSelection? WithViewPosition(
        ExplorerSelection? selection,
        object window,
        bool includeViewPosition,
        bool includeMultiSelectionPaths,
        ExplorerSelectionLookupTrace? diagnostics = null)
    {
        if (selection is null)
        {
            return null;
        }

        using var viewTiming = ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.View);
        if (includeViewPosition && FolderViewNavigator.TryGetPosition(window) is { } position)
        {
            selection = selection with { ViewIndex = position.Index, ViewCount = position.Count };
        }

        // Explicit opens and modifier-driven selection changes carry their
        // members in view order, so browsing can stay within the set.
        // Ordinary arrows intentionally remain path-only: grouped Explorer
        // views briefly report the old and new rows together while crossing
        // a date header, and asking IFolderView to order that transient set
        // blocks Explorer from completing the move.
        if (includeMultiSelectionPaths && selection.SelectedCount > 1)
        {
            selection = selection with { SelectedPaths = FolderViewNavigator.TryGetSelectedPaths(window) };
        }
        else if (!includeMultiSelectionPaths && selection.SelectedCount > 1)
        {
            DiagnosticLog.Write($"explorer multi-selection detail skipped count={selection.SelectedCount}");
        }

        return selection;
    }

    /// <summary>Dev test only: grows the selection, reads it back in view order.</summary>
    public static string? DescribeMultiSelectionForTest(IntPtr explorerHwnd, int addIndexA, int addIndexB)
    {
        object? shell = null;
        object? windows = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            windows = ((dynamic)shell).Windows();
            var entry = TryFindWindowEntry(windows, explorerHwnd);
            if (entry is null)
            {
                return "no window entry";
            }

            FolderViewNavigator.TryAddToSelection(entry, addIndexA);
            FolderViewNavigator.TryAddToSelection(entry, addIndexB);
            var paths = FolderViewNavigator.TryGetSelectedPaths(entry);
            var selection = WithViewPosition(
                TryReadSelection(entry, explorerHwnd),
                entry,
                includeViewPosition: true,
                includeMultiSelectionPaths: true);
            var names = paths is null
                ? "NULL"
                : string.Join(", ", paths.Select(System.IO.Path.GetFileName));
            var viewPaths = FolderViewNavigator.TryGetViewPaths(entry);
            var viewNames = viewPaths is null
                ? "NULL"
                : string.Join(", ", viewPaths.Select(System.IO.Path.GetFileName));
            return $"count={selection?.SelectedCount} carried={selection?.SelectedPaths?.Count ?? 0} order=[{names}] view=[{viewNames}]";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }
    }

    /// <summary>The ShellWindows entry for the window's active tab, or null.</summary>
    private static object? TryFindWindowEntry(object windows, IntPtr explorerHwnd)
    {
        var activeTabHwnd = TryGetActiveTabHwnd(explorerHwnd);
        var entries = CollectWindowEntries(windows, explorerHwnd);
        object? fallback = null;
        foreach (var (window, tabHwnd) in entries)
        {
            if (activeTabHwnd != IntPtr.Zero && tabHwnd != IntPtr.Zero)
            {
                if (tabHwnd == activeTabHwnd)
                {
                    return window;
                }

                continue;
            }

            fallback ??= window;
        }

        if (fallback is not null)
        {
            return fallback;
        }

        // Same stale-census rescue as the Space-press lookup: a window with
        // one registered entry has only one possible browser.
        if (entries.Count == 1)
        {
            DiagnosticLog.Write($"browse entry rescued from stale tab registration entryTab=0x{entries[0].TabHwnd.ToInt64():X} activeTab=0x{activeTabHwnd.ToInt64():X}");
            return entries[0].Window;
        }

        return null;
    }

    private static object? TryFindDesktopEntry(object windows, out IntPtr desktopHwnd)
    {
        object location = 0;
        object locationRoot = 0;
        int hwnd = 0;
        var desktop = ((dynamic)windows).FindWindowSW(ref location, ref locationRoot, SWC_DESKTOP, ref hwnd, SWFO_NEEDDISPATCH);
        desktopHwnd = new IntPtr(hwnd);
        return desktop;
    }

    private static bool WindowThreadHasTextEditingFocus(IntPtr hwnd)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (threadId == 0)
        {
            return false;
        }

        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (!NativeMethods.GetGUIThreadInfo(threadId, ref info))
        {
            return false;
        }

        var focusClass = NativeMethods.GetClassNameString(info.hwndFocus);
        if (string.IsNullOrEmpty(focusClass))
        {
            return false;
        }

        return PreviewInputPolicy.IsTextEditingClass(focusClass);
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
