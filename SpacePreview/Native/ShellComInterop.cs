using System.Runtime.InteropServices;

namespace SpacePreview.Native;

/// <summary>
/// Minimal shell COM plumbing to resolve which Explorer tab a ShellWindows
/// entry maps to. Each tab hosts its own IShellBrowser whose window is the
/// per-tab ShellTabWindowClass pane. Handles are identity where tab names can
/// collide; the active pane is found by z-order, not visibility.
/// </summary>
[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

[ComImport]
[Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    // IOleWindow slots.
    [PreserveSig]
    int GetWindow(out IntPtr phwnd);

    [PreserveSig]
    int ContextSensitiveHelp(int fEnterMode);

    // IShellBrowser slots. Only QueryActiveShellView is ever called; the
    // earlier slots are declared solely to keep the vtable offsets true.
    [PreserveSig]
    int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);

    [PreserveSig]
    int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);

    [PreserveSig]
    int RemoveMenusSB(IntPtr hmenuShared);

    [PreserveSig]
    int SetStatusTextSB(IntPtr pszStatusText);

    [PreserveSig]
    int EnableModelessSB(int fEnable);

    [PreserveSig]
    int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);

    [PreserveSig]
    int BrowseObject(IntPtr pidl, uint wFlags);

    [PreserveSig]
    int GetViewStateStream(uint grfMode, out IntPtr ppStrm);

    [PreserveSig]
    int GetControlWindow(uint id, out IntPtr phwnd);

    [PreserveSig]
    int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);

    [PreserveSig]
    int QueryActiveShellView(out IntPtr ppshv);

    [PreserveSig]
    int OnViewWindowActive(IntPtr pshv);

    [PreserveSig]
    int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
}

/// <summary>
/// The view behind a shell window: items addressed by their position in the
/// view's display order, which is
/// what "previous" and "next" mean while browsing.
/// </summary>
[ComImport]
[Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView
{
    [PreserveSig]
    int GetCurrentViewMode(out uint pViewMode);

    [PreserveSig]
    int SetCurrentViewMode(uint viewMode);

    [PreserveSig]
    int GetFolder(ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int Item(int iItemIndex, out IntPtr ppidl);

    [PreserveSig]
    int ItemCount(uint uFlags, out int pcItems);

    [PreserveSig]
    int Items(uint uFlags, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetSelectionMarkedItem(out int piItem);

    [PreserveSig]
    int GetFocusedItem(out int piItem);

    [PreserveSig]
    int GetItemPosition(IntPtr pidl, out long ppt);

    [PreserveSig]
    int GetSpacing(IntPtr ppt);

    [PreserveSig]
    int GetDefaultSpacing(out long ppt);

    [PreserveSig]
    int GetAutoArrange();

    [PreserveSig]
    int SelectItem(int iItem, uint dwFlags);

    [PreserveSig]
    int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
}

[ComImport]
[Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFolder2
{
    [PreserveSig]
    int GetClassID(out Guid pClassID);

    [PreserveSig]
    int Initialize(IntPtr pidl);

    [PreserveSig]
    int GetCurFolder(out IntPtr ppidl);
}

[ComImport]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);

    [PreserveSig]
    int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int GetCount(out uint pdwNumItems);

    [PreserveSig]
    int GetItemAt(uint dwIndex, out IShellItem ppsi);

    [PreserveSig]
    int EnumItems(out IntPtr ppenumShellItems);
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetParent(out IntPtr ppsi);

    [PreserveSig]
    int GetDisplayName(uint sigdnName, out IntPtr ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(IntPtr psi, uint hint, out int piOrder);
}

internal static class ShellBrowserIdentity
{
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserIid = new("000214E2-0000-0000-C000-000000000046");

    /// <summary>The tab pane hwnd for a ShellWindows entry, or Zero.</summary>
    public static IntPtr TryGetTabHwnd(object window)
    {
        var browserPtr = IntPtr.Zero;
        try
        {
            if (window is not IShellServiceProvider provider)
            {
                return IntPtr.Zero;
            }

            var service = TopLevelBrowserService;
            var iid = ShellBrowserIid;
            if (provider.QueryService(ref service, ref iid, out browserPtr) != 0 || browserPtr == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            if (Marshal.GetObjectForIUnknown(browserPtr) is not IShellBrowser browser)
            {
                return IntPtr.Zero;
            }

            try
            {
                return browser.GetWindow(out var hwnd) == 0 ? hwnd : IntPtr.Zero;
            }
            finally
            {
                Marshal.FinalReleaseComObject(browser);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (browserPtr != IntPtr.Zero)
            {
                Marshal.Release(browserPtr);
            }
        }
    }
}

/// <summary>
/// Stepping through a folder the way the user reads it. The shell's own view
/// is the reference for order and position; selecting through it is a
/// direct call into Explorer, so it works regardless of which window holds
/// the keyboard - unlike synthetic key presses, which Explorer's list
/// ignores. STA only, like everything touching the shell browser.
/// </summary>
internal static class FolderViewNavigator
{
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserIid = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid FolderViewIid = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");

    private const uint SvgioAllView = 0x2;
    private const uint SvgioSelection = 0x1;
    private const uint SigdnFilesysPath = 0x80058000;
    private static readonly Guid ShellItemArrayIid = new("B63EA76D-1F85-456F-A19C-48159EFA858B");

    // SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED
    private const uint SelectExclusive = 0x1 | 0x4 | 0x8 | 0x10;

    // SVSI_SELECT | SVSI_ENSUREVISIBLE: grows the selection.
    private const uint SelectAdditional = 0x1 | 0x8;

    /// <summary>Focused-item index and item count in view order, or null.</summary>
    public static (int Index, int Count)? TryGetPosition(object window)
    {
        IFolderView? view = null;
        try
        {
            view = GetFolderView(window);
            if (view is null || view.ItemCount(SvgioAllView, out var count) != 0 || count <= 0)
            {
                return null;
            }

            return (GetAnchorIndex(view), count);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseView(view);
        }
    }

    /// <summary>
    /// Moves the selection by <paramref name="delta"/> view positions,
    /// clamped at the folder's ends. Returns the resulting position, or null
    /// when the view offers no anchor to move from.
    /// </summary>
    public static (int Index, int Count)? TrySelectRelative(object window, int delta)
    {
        IFolderView? view = null;
        try
        {
            view = GetFolderView(window);
            if (view is null || view.ItemCount(SvgioAllView, out var count) != 0 || count <= 0)
            {
                return null;
            }

            var current = GetAnchorIndex(view);
            if (current < 0)
            {
                return null;
            }

            var next = Math.Clamp(current + delta, 0, count - 1);
            if (next != current && view.SelectItem(next, SelectExclusive) != 0)
            {
                return null;
            }

            return (next, count);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseView(view);
        }
    }

    public static bool TrySelectIndex(object window, int index)
    {
        IFolderView? view = null;
        try
        {
            view = GetFolderView(window);
            if (view is null || view.ItemCount(SvgioAllView, out var count) != 0 || count <= 0)
            {
                return false;
            }

            if (index < 0 || index >= count)
            {
                return false;
            }

            return view.SelectItem(index, SelectExclusive) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseView(view);
        }
    }

    public static IReadOnlyList<string>? TryGetNeighborPaths(object window, int currentIndex, int maxNeighbors)
    {
        IFolderView? view = null;
        IntPtr folderPidl = IntPtr.Zero;
        try
        {
            view = GetFolderView(window);
            if (view is null
                || view.ItemCount(SvgioAllView, out var count) != 0
                || count <= 0
                || currentIndex < 0
                || currentIndex >= count
                || maxNeighbors <= 0)
            {
                return null;
            }

            var paths = new List<string>(Math.Min(maxNeighbors, count - 1));
            var buffer = new System.Text.StringBuilder(1024);
            folderPidl = TryGetFolderPidl(view);
            if (folderPidl == IntPtr.Zero)
            {
                return null;
            }

            for (var radius = 1; paths.Count < maxNeighbors && (currentIndex + radius < count || currentIndex - radius >= 0); radius++)
            {
                TryAddPath(currentIndex + radius);
                TryAddPath(currentIndex - radius);
            }

            return paths;

            void TryAddPath(int index)
            {
                if (paths.Count >= maxNeighbors || index < 0 || index >= count)
                {
                    return;
                }

                var path = TryReadPathAt(view, index, folderPidl, buffer);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (folderPidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(folderPidl);
            }

            ReleaseView(view);
        }
    }

    /// <summary>
    /// First filesystem path from the current selection. This deliberately
    /// does not walk the full view: it is the last-resort path discovery used
    /// by latency-critical navigation when ShellFolderView.SelectedItems is
    /// temporarily unavailable.
    /// </summary>
    public static string? TryGetFirstSelectedPath(object window)
    {
        IFolderView? view = null;
        try
        {
            view = GetFolderView(window);
            if (view is null)
            {
                return null;
            }

            var selected = ReadItemPaths(view, SvgioSelection, maxItems: 1);
            return selected is { Count: > 0 } ? selected[0] : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseView(view);
        }
    }

    /// <summary>
    /// Filesystem paths of the current selection, in the view's own display
    /// order: what "previous" and "next" mean when browsing WITHIN a
    /// explicit multi-selection. The selection array itself arrives in the
    /// shell's internal order (anchor first, then selection history), so the
    /// members are re-sorted by walking the full view order.
    /// </summary>
    public static IReadOnlyList<string>? TryGetSelectedPaths(object window, int maxItems = 256)
    {
        IFolderView? view = null;
        try
        {
            view = GetFolderView(window);
            if (view is null)
            {
                return null;
            }

            var selected = ReadItemPaths(view, SvgioSelection, maxItems);
            if (selected is null || selected.Count == 0)
            {
                return null;
            }

            if (selected.Count == 1)
            {
                return selected;
            }

            // Match by FILE NAME: Item() hands back child pidls (their
            // resolved paths re-root at the desktop), but the name component
            // is authoritative and unique within one folder.
            var remaining = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in selected)
            {
                remaining[System.IO.Path.GetFileName(path)] = path;
            }

            var viewOrder = ReadViewOrderPaths(view, 4096);
            if (viewOrder is null)
            {
                return selected;
            }

            var ordered = new List<string>(selected.Count);
            foreach (var viewPath in viewOrder)
            {
                if (remaining.Remove(System.IO.Path.GetFileName(viewPath), out var fullPath))
                {
                    ordered.Add(fullPath);
                    if (remaining.Count == 0)
                    {
                        break;
                    }
                }
            }

            // Members past the enumeration cap keep their shell order at the
            // end rather than silently vanishing.
            foreach (var path in selected)
            {
                if (remaining.ContainsKey(System.IO.Path.GetFileName(path)))
                {
                    ordered.Add(path);
                }
            }

            return ordered;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseView(view);
        }
    }

    /// <summary>
    /// Paths by explicit view index. The
    /// Items() enumerations are rotated to start at the focused item, so
    /// they cannot be trusted for ordering.
    /// </summary>
    private static List<string>? ReadViewOrderPaths(IFolderView view, int maxItems)
    {
        if (view.ItemCount(SvgioAllView, out var count) != 0 || count <= 0)
        {
            return null;
        }

        var limit = Math.Min(count, maxItems);
        var paths = new List<string>(limit);
        var buffer = new System.Text.StringBuilder(1024);
        var folderPidl = TryGetFolderPidl(view);
        if (folderPidl == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            for (var i = 0; i < limit; i++)
            {
                var path = TryReadPathAt(view, i, folderPidl, buffer);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }
        finally
        {
            Marshal.FreeCoTaskMem(folderPidl);
        }
    }

    private static IntPtr TryGetFolderPidl(IFolderView view)
    {
        IntPtr folderPtr = IntPtr.Zero;
        IPersistFolder2? folder = null;
        try
        {
            var iid = typeof(IPersistFolder2).GUID;
            if (view.GetFolder(ref iid, out folderPtr) != 0 || folderPtr == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            folder = (IPersistFolder2)Marshal.GetObjectForIUnknown(folderPtr);
            return folder.GetCurFolder(out var folderPidl) == 0 ? folderPidl : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (folder is not null)
            {
                Marshal.FinalReleaseComObject(folder);
            }

            if (folderPtr != IntPtr.Zero)
            {
                Marshal.Release(folderPtr);
            }
        }
    }

    private static string? TryReadPathAt(IFolderView view, int index, IntPtr folderPidl, System.Text.StringBuilder buffer)
    {
        if (view.Item(index, out var pidl) != 0 || pidl == IntPtr.Zero)
        {
            return null;
        }

        IntPtr absolute = IntPtr.Zero;
        try
        {
            absolute = ILCombine(folderPidl, pidl);
            buffer.Clear();
            if (absolute != IntPtr.Zero && SHGetPathFromIDListEx(absolute, buffer, (uint)buffer.Capacity, 0) && buffer.Length > 0)
            {
                return buffer.ToString();
            }

            buffer.Clear();
            return SHGetPathFromIDListEx(pidl, buffer, (uint)buffer.Capacity, 0) && buffer.Length > 0
                ? buffer.ToString()
                : null;
        }
        finally
        {
            if (absolute != IntPtr.Zero)
            {
                ILFree(absolute);
            }

            Marshal.FreeCoTaskMem(pidl);
        }
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(IntPtr pidl, System.Text.StringBuilder pszPath, uint cchPath, int uOpts);

    private static List<string>? ReadItemPaths(IFolderView view, uint svgioFlags, int maxItems)
    {
        var iid = ShellItemArrayIid;
        if (view.Items(svgioFlags, ref iid, out var arrayPtr) != 0 || arrayPtr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (Marshal.GetObjectForIUnknown(arrayPtr) is not IShellItemArray items)
            {
                return null;
            }

            try
            {
                if (items.GetCount(out var count) != 0 || count == 0)
                {
                    return null;
                }

                var paths = new List<string>((int)Math.Min(count, (uint)maxItems));
                for (uint i = 0; i < count && paths.Count < maxItems; i++)
                {
                    if (items.GetItemAt(i, out var item) != 0 || item is null)
                    {
                        continue;
                    }

                    try
                    {
                        // Non-filesystem entries (a library node, a device)
                        // have no path; skip them.
                        if (item.GetDisplayName(SigdnFilesysPath, out var namePtr) == 0 && namePtr != IntPtr.Zero)
                        {
                            try
                            {
                                var path = Marshal.PtrToStringUni(namePtr);
                                if (!string.IsNullOrEmpty(path))
                                {
                                    paths.Add(path);
                                }
                            }
                            finally
                            {
                                Marshal.FreeCoTaskMem(namePtr);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(item);
                    }
                }

                return paths;
            }
            finally
            {
                Marshal.FinalReleaseComObject(items);
            }
        }
        finally
        {
            Marshal.Release(arrayPtr);
        }
    }

    /// <summary>Dev test only: every view item's path, in display order.</summary>
    public static IReadOnlyList<string>? TryGetViewPaths(object window, int maxItems = 64)
    {
        IFolderView? view = null;
        try
        {
            view = GetFolderView(window);
            return view is null ? null : ReadViewOrderPaths(view, maxItems);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseView(view);
        }
    }

    /// <summary>Dev test only: grows the selection by one view index.</summary>
    public static bool TryAddToSelection(object window, int index)
    {
        IFolderView? view = null;
        try
        {
            view = GetFolderView(window);
            return view is not null && view.SelectItem(index, SelectAdditional) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseView(view);
        }
    }

    private static int GetAnchorIndex(IFolderView view)
    {
        if (view.GetFocusedItem(out var focused) == 0 && focused >= 0)
        {
            return focused;
        }

        return view.GetSelectionMarkedItem(out var marked) == 0 && marked >= 0 ? marked : -1;
    }

    private static IFolderView? GetFolderView(object window)
    {
        if (window is not IShellServiceProvider provider)
        {
            return null;
        }

        var service = TopLevelBrowserService;
        var iid = ShellBrowserIid;
        if (provider.QueryService(ref service, ref iid, out var browserPtr) != 0 || browserPtr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (Marshal.GetObjectForIUnknown(browserPtr) is not IShellBrowser browser)
            {
                return null;
            }

            try
            {
                if (browser.QueryActiveShellView(out var viewPtr) != 0 || viewPtr == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    if (Marshal.QueryInterface(viewPtr, in FolderViewIid, out var folderViewPtr) != 0
                        || folderViewPtr == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        // GetObjectForIUnknown takes its own reference.
                        return Marshal.GetObjectForIUnknown(folderViewPtr) as IFolderView;
                    }
                    finally
                    {
                        Marshal.Release(folderViewPtr);
                    }
                }
                finally
                {
                    Marshal.Release(viewPtr);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(browser);
            }
        }
        finally
        {
            Marshal.Release(browserPtr);
        }
    }

    private static void ReleaseView(IFolderView? view)
    {
        if (view is null)
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(view);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
