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

/// <summary>
/// Which ShellWindows entry speaks for a window. Explorer's census goes stale:
/// entries duplicate or point at old tab panes until the window is reopened.
/// Opening and browsing use this one rule, so a census fix heals both.
/// </summary>
internal static class ExplorerCensus
{
    internal enum Outcome
    {
        ActiveTab,
        ActiveTabEmpty,
        NoTabIdentity,
        StaleTabRescue,
        DeadCensus,
        NoEntry
    }

    /// <param name="tabs">Each entry's tab pane, Zero when it has none.</param>
    /// <param name="activeTab">The window's topmost tab pane, Zero on a shell without tabs.</param>
    /// <param name="read">Reads entry i; null when it has nothing usable.</param>
    internal static (T? Value, Outcome Outcome) Choose<T>(
        IReadOnlyList<IntPtr> tabs,
        IntPtr activeTab,
        Func<int, T?> read)
        where T : class
    {
        if (tabs.Count == 0)
        {
            return (null, Outcome.NoEntry);
        }

        var sawActiveTab = false;
        T? withoutIdentity = null;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (activeTab != IntPtr.Zero && tabs[i] != IntPtr.Zero)
            {
                if (tabs[i] != activeTab)
                {
                    continue;
                }

                sawActiveTab = true;
                if (read(i) is { } active)
                {
                    return (active, Outcome.ActiveTab);
                }

                // A dead duplicate registered for the same tab must not
                // silence a live entry behind it.
                continue;
            }

            withoutIdentity ??= read(i);
        }

        if (sawActiveTab)
        {
            return (null, Outcome.ActiveTabEmpty);
        }

        if (withoutIdentity is not null)
        {
            return (withoutIdentity, Outcome.NoTabIdentity);
        }

        // The active tab has no entry at all. One entry cannot be ambiguous,
        // so it is the selection's only possible home.
        if (tabs.Count == 1 && read(0) is { } rescued)
        {
            return (rescued, Outcome.StaleTabRescue);
        }

        return (null, Outcome.DeadCensus);
    }

    internal static string Name(Outcome outcome) => outcome switch
    {
        Outcome.ActiveTab => "active-tab",
        Outcome.ActiveTabEmpty => "active-tab-empty",
        Outcome.NoTabIdentity => "no-tab-identity",
        Outcome.StaleTabRescue => "stale-tab-rescue",
        Outcome.DeadCensus => "dead-census",
        _ => "no-entry"
    };
}

/// <summary>
/// Reads what Explorer and the desktop have selected, through the folder
/// view itself (IFolderView). STA only: everything here runs on
/// <see cref="StaTask"/>.
/// </summary>
internal static class ExplorerSelectionService
{
    private static DesktopShellSessionCache? _desktopSessionCache;

    private readonly record struct CensusEntry(object Browser, IntPtr TabHwnd);

    public static ExplorerSelection? TryGetForegroundSelection(
        ExplorerSelectionLookupTrace? diagnostics = null,
        bool includeViewPosition = true,
        bool includeMultiSelectionPaths = true)
    {
        var context = TryGetForegroundContext();
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

    public static ForegroundSelectionContext? TryGetForegroundContext()
    {
        // Glimpse's own windows offer nothing to glance at. Sign mode's stamp
        // import is a real file dialog, but a glance from it would replace
        // the preview that opened it.
        var root = NativeMethods.GetForegroundRoot();
        if (root == IntPtr.Zero || NativeMethods.GetWindowProcessId(root) == (uint)Environment.ProcessId)
        {
            return null;
        }

        var className = NativeMethods.GetClassNameString(root);

        var fileDialogListHasFocus = className.Equals(
                CommonFileDialogSelectionService.DialogWindowClass,
                StringComparison.OrdinalIgnoreCase)
            && CommonFileDialogSelectionService.HasFileListFocus(root);

        return PreviewInputPolicy.ResolveForegroundContext(
            root,
            className,
            commonFileDialogListHasFocus: fileDialogListHasFocus);
    }

    /// <param name="includeViewPosition">
    /// Folder position is optional chrome. Explorer can block that query for
    /// hundreds of milliseconds, so the Space and arrow paths leave it off.
    /// </param>
    /// <param name="includeMultiSelectionPaths">
    /// Opens and Shift moves carry the set in view order. Plain arrows stay
    /// path-only: grouped views briefly report the old and new rows together
    /// at a date header, and ordering that transient set stalls Explorer.
    /// </param>
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

        IShellWindows? windows = null;
        var entries = new List<CensusEntry>();
        try
        {
            IntPtr activeTab;
            using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.ActiveTab))
            {
                activeTab = TryGetActiveTabHwnd(explorerHwnd);
            }

            using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.Census))
            {
                windows = ShellViews.CreateShellWindows();
                entries = TakeCensus(windows, explorerHwnd);
            }

            diagnostics?.SetEntryCount(entries.Count);
            var (selection, outcome) = ExplorerCensus.Choose(
                TabsOf(entries),
                activeTab,
                i => ReadView(
                    entries[i].Browser,
                    explorerHwnd,
                    diagnostics,
                    includeViewPosition,
                    includeMultiSelectionPaths,
                    out _));
            diagnostics?.SetOutcome(ExplorerCensus.Name(outcome));
            LogCensusOutcome(outcome, explorerHwnd, activeTab, entries, selection);
            return selection;
        }
        catch (Exception ex)
        {
            diagnostics?.SetOutcome($"exception-{ex.GetType().Name}");
            DiagnosticLog.Write($"explorer selection failed hwnd=0x{explorerHwnd.ToInt64():X}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            Release(entries);
            ShellViews.Release(windows);
        }
    }

    public static ExplorerSelection? TryGetDesktopSelection(
        ExplorerSelectionLookupTrace? diagnostics = null,
        bool includeViewPosition = true,
        bool includeMultiSelectionPaths = true)
    {
        if (!StaTask.IsWorkerThread)
        {
            diagnostics?.SetOutcome("desktop-off-sta");
            return null;
        }

        _desktopSessionCache ??= new DesktopShellSessionCache();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            DesktopShellSession? session;
            string sessionState;
            using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.Census))
            {
                session = _desktopSessionCache.GetOrCreate(out sessionState);
            }

            if (session is null)
            {
                diagnostics?.SetEntryCount(0);
                diagnostics?.SetOutcome($"desktop-{sessionState}");
                return null;
            }

            diagnostics?.SetEntryCount(1);
            try
            {
                var selection = ReadView(
                    session.Desktop,
                    session.Hwnd,
                    diagnostics,
                    includeViewPosition,
                    includeMultiSelectionPaths,
                    out var hr);

                // An Explorer restart can keep the desktop's hwnd and class
                // while the proxy behind the session dies. Rebuild once.
                if (ShellViews.IsDisconnected(hr) && attempt == 0)
                {
                    _desktopSessionCache.Invalidate($"read-0x{hr:X8}");
                    continue;
                }

                diagnostics?.SetOutcome(selection is null ? "desktop-selection-empty" : "desktop");
                return selection;
            }
            catch (Exception ex) when (IsRecoverableSessionFailure(ex) && attempt == 0)
            {
                _desktopSessionCache.Invalidate($"read-{ex.GetType().Name}");
            }
            catch (Exception ex)
            {
                _desktopSessionCache.Invalidate($"read-{ex.GetType().Name}");
                diagnostics?.SetOutcome($"desktop-exception-{ex.GetType().Name}");
                DiagnosticLog.Write($"desktop selection failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        diagnostics?.SetOutcome("desktop-retry-exhausted");
        return null;
    }

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
    /// Moves the folder view's selection by <paramref name="delta"/> in the
    /// view's own display order and reports what is now selected. This talks
    /// to Explorer through its view object, so it works whichever window
    /// holds the keyboard; Explorer's list ignores synthetic key presses.
    /// </summary>
    public static ExplorerSelection? TryBrowseRelative(IntPtr explorerHwnd, bool isDesktop, int delta)
    {
        try
        {
            return isDesktop ? BrowseDesktop(delta) : BrowseExplorer(explorerHwnd, delta);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"browse failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The files nearest the previewed one in its window's view order, for
    /// the Office warm-up. Shell STA only. Null when the view is gone or no
    /// longer holds the file.
    /// </summary>
    public static IReadOnlyList<string>? TryGetNeighborPaths(IntPtr explorerHwnd, bool isDesktop, string currentPath, int max)
    {
        try
        {
            if (!isDesktop)
            {
                return WithExplorerView(explorerHwnd, view => ShellViews.ReadNeighbors(view, currentPath, max));
            }

            if (!StaTask.IsWorkerThread)
            {
                return null;
            }

            _desktopSessionCache ??= new DesktopShellSessionCache();
            if (_desktopSessionCache.GetOrCreate(out _) is not { } session
                || ShellViews.GetFolderView(session.Desktop, out _) is not { } view)
            {
                return null;
            }

            try
            {
                return ShellViews.ReadNeighbors(view, currentPath, max);
            }
            finally
            {
                ShellViews.Release(view);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"neighbor lookup failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Dev test only: grows the selection, reads it back in view order.</summary>
    public static string? DescribeMultiSelectionForTest(IntPtr explorerHwnd, int addIndexA, int addIndexB)
    {
        try
        {
            return WithExplorerView(explorerHwnd, view =>
            {
                ShellViews.AddToSelection(view, addIndexA);
                ShellViews.AddToSelection(view, addIndexB);
                var selection = ShellViews.ReadSelection(view, ordered: true, out _);
                var names = selection.OrderedPaths is null
                    ? "NULL"
                    : string.Join(", ", selection.OrderedPaths.Select(System.IO.Path.GetFileName));
                return $"count={selection.Count} carried={selection.OrderedPaths?.Count ?? 0} order=[{names}]";
            }) ?? "no window entry";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// A text box in the window holds the keyboard, so every key is the text
    /// box's. Read from Windows alone and nothing here waits, so the hook
    /// thread asks it at each key. <paramref name="header"/> says the place
    /// is Explorer's header, which no key of the user's ends.
    /// </summary>
    public static bool IsTyping(IntPtr root, out bool header)
    {
        header = false;
        if (root == IntPtr.Zero)
        {
            return false;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(root, out _);
        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        return threadId != 0
            && NativeMethods.GetGUIThreadInfo(threadId, ref info)
            && HasTextFocus(root, info.hwndFocus, info.hwndCaret, out header);
    }

    /// <summary>The three things that are a text box with the keyboard.</summary>
    internal static bool HasTextFocus(IntPtr root, IntPtr focus, IntPtr caret, out bool header)
    {
        header = false;
        if (focus == IntPtr.Zero)
        {
            return false;
        }

        // A caret up in this window: a rename, a picker's name box, and the
        // text boxes that have no window of their own to name them (the
        // search box of a file dialog). A caret an app left in another of
        // its windows does not count.
        if (caret != IntPtr.Zero && NativeMethods.GetRoot(caret) == root)
        {
            return true;
        }

        if (PreviewInputPolicy.IsTextEditingClass(NativeMethods.GetClassNameString(focus)))
        {
            return true;
        }

        // Explorer's header on Windows 11 is one XAML island, a child of
        // the window itself: the address bar, the search box and the buttons
        // beside them share it, keep no caret, and Windows cannot say which
        // of them has the keyboard. The header has it, so its keys are its
        // own. An island further in is a folder's own view and is not this.
        var child = focus;
        for (var parent = NativeMethods.GetParent(child);
             parent != IntPtr.Zero && parent != root;
             parent = NativeMethods.GetParent(child))
        {
            child = parent;
        }

        header = PreviewInputPolicy.IsHeaderIslandClass(NativeMethods.GetClassNameString(child));
        return header;
    }

    private static ExplorerSelection? BrowseExplorer(IntPtr explorerHwnd, int delta) =>
        WithExplorerView(explorerHwnd, view => Step(view, explorerHwnd, delta));

    private static ExplorerSelection? BrowseDesktop(int delta)
    {
        if (!StaTask.IsWorkerThread)
        {
            return null;
        }

        _desktopSessionCache ??= new DesktopShellSessionCache();
        if (_desktopSessionCache.GetOrCreate(out var state) is not { } session)
        {
            DiagnosticLog.Write($"browse: desktop session {state}");
            return null;
        }

        var view = ShellViews.GetFolderView(session.Desktop, out var hr);
        if (view is null)
        {
            if (ShellViews.IsDisconnected(hr))
            {
                _desktopSessionCache.Invalidate($"browse-0x{hr:X8}");
            }

            DiagnosticLog.Write($"browse: desktop folder view unavailable hr=0x{hr:X8}");
            return null;
        }

        try
        {
            return Step(view, session.Hwnd, delta);
        }
        finally
        {
            ShellViews.Release(view);
        }
    }

    /// <summary>Runs <paramref name="use"/> on the folder view of the window's chosen census entry.</summary>
    private static T? WithExplorerView<T>(IntPtr explorerHwnd, Func<IFolderView, T?> use)
        where T : class
    {
        IShellWindows? windows = null;
        var entries = new List<CensusEntry>();
        var acquired = new List<IFolderView>();
        try
        {
            var activeTab = TryGetActiveTabHwnd(explorerHwnd);
            windows = ShellViews.CreateShellWindows();
            entries = TakeCensus(windows, explorerHwnd);
            var (view, _) = ExplorerCensus.Choose(TabsOf(entries), activeTab, i =>
            {
                var candidate = ShellViews.GetFolderView(entries[i].Browser, out _);
                if (candidate is not null)
                {
                    acquired.Add(candidate);
                }

                return candidate;
            });
            if (view is null)
            {
                DiagnosticLog.Write($"browse: no live folder view hwnd=0x{explorerHwnd.ToInt64():X} entries={entries.Count}");
                return null;
            }

            return use(view);
        }
        finally
        {
            foreach (var view in acquired)
            {
                ShellViews.Release(view);
            }

            Release(entries);
            ShellViews.Release(windows);
        }
    }

    /// <summary>
    /// One browse step. An item that is no file at all, neither on disk nor
    /// on a phone or in a zip folder, is skipped in the direction the user
    /// is going; at the folder's end the selection goes back where it
    /// started. A file whose copy cannot be made is not skipped: its card
    /// says why.
    /// </summary>
    // A selected file with no path on disk is shown from a copy.
    private static readonly Func<IShellItem, string?> Borrow = item => BorrowedFiles.TryBorrow(item);

    private static ExplorerSelection? Step(IFolderView view, IntPtr targetHwnd, int delta)
    {
        var start = ShellViews.GetPosition(view);
        if (ShellViews.SelectRelative(view, delta) is not { } moved)
        {
            DiagnosticLog.Write($"browse: folder view unavailable hwnd=0x{targetHwnd.ToInt64():X}");
            return null;
        }

        var (index, count) = moved;
        var direction = Math.Sign(delta);
        var skipped = false;
        while (true)
        {
            var selection = ShellViews.ReadSelection(view, ordered: false, out _, Borrow);
            if (selection.AnchorPath is { } path)
            {
                if (skipped)
                {
                    DiagnosticLog.Write($"browse: skipped non-previewable shell item -> {path}");
                }

                return new ExplorerSelection(path, targetHwnd, selection.Count, index, count);
            }

            var next = index + direction;
            if (direction == 0 || next < 0 || next >= count)
            {
                DiagnosticLog.Write($"browse: skipped non-previewable item at folder end index={index + 1} of {count}");
                RestoreStart();
                return null;
            }

            if (ShellViews.SelectRelative(view, direction) is not { } stepped || stepped.Index == index)
            {
                DiagnosticLog.Write($"browse: could not skip non-previewable item index={index + 1} of {count}");
                RestoreStart();
                return null;
            }

            (index, count) = stepped;
            skipped = true;
        }

        void RestoreStart()
        {
            if (start is { } origin)
            {
                ShellViews.SelectIndex(view, origin.Index);
            }
        }
    }

    /// <summary>Reads one browser's view. <paramref name="hr"/> carries the first failure, for disconnect detection.</summary>
    private static ExplorerSelection? ReadView(
        object browser,
        IntPtr hwnd,
        ExplorerSelectionLookupTrace? diagnostics,
        bool includeViewPosition,
        bool includeMultiSelectionPaths,
        out int hr)
    {
        IFolderView? view;
        FolderViewSelection selection;
        using (ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.Selection))
        {
            view = ShellViews.GetFolderView(browser, out hr);
            if (view is null)
            {
                return null;
            }

            selection = ShellViews.ReadSelection(view, includeMultiSelectionPaths, out hr, Borrow);
        }

        try
        {
            if (selection.AnchorPath is null)
            {
                return null;
            }

            var result = new ExplorerSelection(
                selection.AnchorPath,
                hwnd,
                selection.Count,
                SelectedPaths: selection.OrderedPaths);
            if (includeViewPosition)
            {
                using var viewTiming = ExplorerSelectionLookupTrace.Track(diagnostics, ExplorerSelectionLookupPhase.View);
                if (ShellViews.GetPosition(view) is { } position)
                {
                    result = result with { ViewIndex = position.Index, ViewCount = position.Count };
                }
            }

            return result;
        }
        finally
        {
            ShellViews.Release(view);
        }
    }

    /// <summary>
    /// Every ShellWindows entry registered for one top-level window. An
    /// entry's window comes from its tab pane; an entry whose pane is gone
    /// still counts through its own HWND, because the stale-census rescue
    /// exists for exactly that entry.
    /// </summary>
    private static List<CensusEntry> TakeCensus(IShellWindows windows, IntPtr explorerHwnd)
    {
        var entries = new List<CensusEntry>();
        if (windows.get_Count(out var count) != 0)
        {
            return entries;
        }

        for (var i = 0; i < count; i++)
        {
            if (windows.Item(i, out var browser) != 0 || browser is null)
            {
                continue;
            }

            var tab = ShellViews.GetTabHwnd(browser);
            var window = tab != IntPtr.Zero && NativeMethods.IsWindow(tab)
                ? NativeMethods.GetRoot(tab)
                : ShellViews.GetTopLevelHwnd(browser);
            if (window == explorerHwnd)
            {
                entries.Add(new CensusEntry(browser, tab));
            }
            else
            {
                ShellViews.Release(browser);
            }
        }

        return entries;
    }

    private static IntPtr[] TabsOf(List<CensusEntry> entries) =>
        entries.Select(entry => entry.TabHwnd).ToArray();

    private static void Release(List<CensusEntry> entries)
    {
        foreach (var entry in entries)
        {
            ShellViews.Release(entry.Browser);
        }
    }

    private static void LogCensusOutcome(
        ExplorerCensus.Outcome outcome,
        IntPtr explorerHwnd,
        IntPtr activeTab,
        List<CensusEntry> entries,
        ExplorerSelection? selection)
    {
        switch (outcome)
        {
            case ExplorerCensus.Outcome.NoEntry:
                DiagnosticLog.Write($"explorer selection: no shell entry hwnd=0x{explorerHwnd.ToInt64():X} activeTab=0x{activeTab.ToInt64():X}");
                break;
            case ExplorerCensus.Outcome.ActiveTabEmpty:
                DiagnosticLog.Write($"explorer active tab 0x{activeTab.ToInt64():X} has no usable selection");
                break;
            case ExplorerCensus.Outcome.NoTabIdentity:
                DiagnosticLog.Write($"explorer selected (no tab identity) path={selection?.Path}");
                break;
            case ExplorerCensus.Outcome.StaleTabRescue:
                DiagnosticLog.Write($"explorer selection rescued from stale tab registration entryTab=0x{entries[0].TabHwnd.ToInt64():X} activeTab=0x{activeTab.ToInt64():X} path={selection?.Path}");
                break;
            case ExplorerCensus.Outcome.DeadCensus:
                DiagnosticLog.Write($"explorer selection dead: activeTab=0x{activeTab.ToInt64():X} entries=[{DescribeEntries(entries)}]");
                break;
        }
    }

    private static string DescribeEntries(List<CensusEntry> entries) =>
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

    private static bool IsRecoverableSessionFailure(Exception exception) =>
        exception is COMException
            or InvalidComObjectException
            or ObjectDisposedException;
}
