using System.IO;
using System.Collections;
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

internal static class ExplorerSelectionService
{
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;

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
        DiagnosticLog.Write($"foreground hwnd=0x{foreground.ToInt64():X} root=0x{root.ToInt64():X} class={className}");

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
        object? shell = null;
        object? windows = null;
        object? desktop = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                diagnostics?.SetOutcome("desktop-shell-unavailable");
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                diagnostics?.SetOutcome("desktop-shell-activation-empty");
                return null;
            }

            object location = 0;
            object locationRoot = 0;
            int hwnd = 0;
            using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.Census))
            {
                windows = ((dynamic)shell).Windows();
                desktop = ((dynamic)windows).FindWindowSW(ref location, ref locationRoot, SWC_DESKTOP, ref hwnd, SWFO_NEEDDISPATCH);
            }

            if (desktop is null)
            {
                diagnostics?.SetEntryCount(0);
                diagnostics?.SetOutcome("desktop-entry-empty");
                return null;
            }

            diagnostics?.SetEntryCount(1);

            dynamic document;
            dynamic selectedItems;
            int count;
            using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.Selection))
            {
                document = ((dynamic)desktop).Document;
                selectedItems = document.SelectedItems();
                count = Convert.ToInt32(selectedItems.Count);
            }

            if (count <= 0)
            {
                diagnostics?.SetOutcome("desktop-selection-empty");
                return null;
            }

            string? path;
            using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.Selection))
            {
                dynamic firstItem = selectedItems.Item(0);
                path = firstItem.Path as string;
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                diagnostics?.SetOutcome("desktop-path-empty");
                return null;
            }

            path = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                diagnostics?.SetOutcome("desktop-path-nonfilesystem");
                return null;
            }

            var selection = WithViewPosition(
                new ExplorerSelection(path, new IntPtr(hwnd), count),
                desktop,
                includeViewPosition,
                includeMultiSelectionPaths,
                diagnostics);
            diagnostics?.SetOutcome(selection is null ? "desktop-view-empty" : "desktop");
            return selection;
        }
        catch (Exception ex)
        {
            diagnostics?.SetOutcome($"desktop-exception-{ex.GetType().Name}");
            DiagnosticLog.Write($"desktop selection failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            ReleaseComObject(desktop);
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }
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
