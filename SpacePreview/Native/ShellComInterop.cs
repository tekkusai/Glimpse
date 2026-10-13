using System.Reflection;
using System.Runtime.InteropServices;

namespace SpacePreview.Native;

/// <summary>
/// The shell's window list. Declared only up to the slots Glimpse calls:
/// later slots register and revoke windows, so they stay out of reach.
/// </summary>
[ComImport]
[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    [PreserveSig]
    int get_Count(out int count);

    [PreserveSig]
    int Item(
        [MarshalAs(UnmanagedType.Struct)] object index,
        [MarshalAs(UnmanagedType.IDispatch)] out object? folder);
}

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
}

/// <summary>
/// The view behind a shell window: items addressed by their position in the
/// view's display order, which is what "previous" and "next" mean.
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
    int GetItemAt(uint dwIndex, out IntPtr ppsi);
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
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey(Guid formatId, uint propertyId)
{
    public Guid FormatId = formatId;
    public uint PropertyId = propertyId;
}

/// <summary>
/// IShellItem with its properties. Every slot is declared, in the order
/// Windows lays them out, so the three this app calls land where they are.
/// </summary>
[ComImport]
[Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem2
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

    [PreserveSig]
    int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int Update(IntPtr pbc);

    [PreserveSig]
    int GetProperty(ref PropertyKey key, IntPtr ppropvar);

    [PreserveSig]
    int GetCLSID(ref PropertyKey key, out Guid pclsid);

    [PreserveSig]
    int GetFileTime(ref PropertyKey key, out long pft);

    [PreserveSig]
    int GetInt32(ref PropertyKey key, out int pi);

    [PreserveSig]
    int GetString(ref PropertyKey key, out IntPtr ppsz);

    [PreserveSig]
    int GetUInt32(ref PropertyKey key, out uint pui);

    [PreserveSig]
    int GetUInt64(ref PropertyKey key, out ulong pull);

    [PreserveSig]
    int GetBool(ref PropertyKey key, out int pf);
}

/// <summary>What a folder view says is selected.</summary>
/// <param name="Count">Every selected item, files or not.</param>
/// <param name="AnchorPath">The anchor's filesystem path, or the path of a copy borrowed for it; null when the anchor is neither a file or folder on disk nor one that could be borrowed.</param>
/// <param name="OrderedPaths">The selected filesystem paths in view order, when asked for.</param>
internal readonly record struct FolderViewSelection(
    int Count,
    string? AnchorPath,
    IReadOnlyList<string>? OrderedPaths);

/// <summary>
/// Native reads of Explorer's views. Every call here crosses into Explorer
/// and must run on the shell STA: the per-tab browser answers QueryService
/// only there. HRESULTs come back as values so a dead Explorer reads as
/// <see cref="IsDisconnected"/> instead of as an empty selection.
/// </summary>
internal static class ShellViews
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserIid = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid FolderViewIid = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    private static readonly Guid ShellItemArrayIid = new("B63EA76D-1F85-456F-A19C-48159EFA858B");

    private const uint SvgioSelection = 0x1;
    private const uint SvgioAllView = 0x2;
    private const uint SvgioFlagViewOrder = 0x80000000;
    private const uint SigdnFilesysPath = 0x80058000;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;

    // SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED
    private const uint SelectExclusive = 0x1 | 0x4 | 0x8 | 0x10;

    // SVSI_SELECT | SVSI_ENSUREVISIBLE: grows the selection.
    private const uint SelectAdditional = 0x1 | 0x8;

    private const int RpcEDisconnected = unchecked((int)0x80010108);
    private const int RpcEServerDied = unchecked((int)0x80010007);
    private const int RpcEServerDiedDne = unchecked((int)0x80010012);
    private const int RpcSServerUnavailable = unchecked((int)0x800706BA);

    /// <summary>Explorer went away under the proxy: rebuild, do not report "nothing selected".</summary>
    public static bool IsDisconnected(int hr) =>
        hr is RpcEDisconnected or RpcEServerDied or RpcEServerDiedDne or RpcSServerUnavailable;

    public static IShellWindows CreateShellWindows()
    {
        var type = Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: true)!;
        return (IShellWindows)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// The desktop's browser. Found by name through IDispatch so no vtable
    /// slot past <see cref="IShellWindows.Item"/> is ever called.
    /// </summary>
    public static object? FindDesktop(IShellWindows windows, out IntPtr hwnd)
    {
        var args = new object?[] { null, null, SwcDesktop, 0, SwfoNeedDispatch };
        var byRef = new ParameterModifier(args.Length);
        byRef[0] = true;
        byRef[1] = true;
        byRef[3] = true;
        var desktop = windows.GetType().InvokeMember(
            "FindWindowSW",
            BindingFlags.InvokeMethod,
            binder: null,
            windows,
            args,
            [byRef],
            culture: null,
            namedParameters: null);
        hwnd = args[3] is { } raw ? new IntPtr(Convert.ToInt64(raw)) : IntPtr.Zero;
        return desktop;
    }

    /// <summary>
    /// The top-level window behind a ShellWindows entry, through IDispatch.
    /// Used only when the entry's tab pane is gone, the case the stale-census
    /// rescue exists for.
    /// </summary>
    public static IntPtr GetTopLevelHwnd(object browser)
    {
        try
        {
            var value = browser.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, browser, null);
            return value is null ? IntPtr.Zero : new IntPtr(Convert.ToInt64(value));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>The tab pane hwnd for a ShellWindows entry, or Zero.</summary>
    public static IntPtr GetTabHwnd(object browser)
    {
        var shellBrowser = GetShellBrowser(browser, out _);
        if (shellBrowser is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            return shellBrowser.GetWindow(out var hwnd) == 0 ? hwnd : IntPtr.Zero;
        }
        finally
        {
            Release(shellBrowser);
        }
    }

    /// <summary>The entry's active folder view, or null with the failing HRESULT.</summary>
    public static IFolderView? GetFolderView(object browser, out int hr)
    {
        var shellBrowser = GetShellBrowser(browser, out hr);
        if (shellBrowser is null)
        {
            return null;
        }

        try
        {
            hr = shellBrowser.QueryActiveShellView(out var viewPtr);
            if (hr != 0 || viewPtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                hr = Marshal.QueryInterface(viewPtr, in FolderViewIid, out var folderViewPtr);
                if (hr != 0 || folderViewPtr == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return (IFolderView)Marshal.GetUniqueObjectForIUnknown(folderViewPtr);
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
            Release(shellBrowser);
        }
    }

    /// <summary>
    /// The selection's size and anchor, and its members in view order when
    /// <paramref name="ordered"/> asks and there is more than one. Without
    /// the view-order flag the shell lists the anchor first. An anchor with
    /// no path on disk (a file on a phone, in a zip folder) is handed to
    /// <paramref name="borrow"/>, which may answer with the path of a copy.
    /// </summary>
    public static FolderViewSelection ReadSelection(IFolderView view, bool ordered, out int hr, Func<IShellItem, string?>? borrow = null)
    {
        hr = view.ItemCount(SvgioSelection, out var count);
        if (hr != 0 || count <= 0)
        {
            return new FolderViewSelection(0, null, null);
        }

        var anchor = ReadPaths(view, SvgioSelection, maxItems: 1, borrow, out hr);
        if (hr != 0)
        {
            return new FolderViewSelection(count, null, null);
        }

        var members = ordered && count > 1
            ? ReadPaths(view, SvgioSelection | SvgioFlagViewOrder, maxItems: 256, borrow: null, out _)
            : null;
        return new FolderViewSelection(count, anchor is { Count: > 0 } ? anchor[0] : null, members);
    }

    /// <summary>Focused-item index and item count in view order, or null.</summary>
    public static (int Index, int Count)? GetPosition(IFolderView view)
    {
        if (view.ItemCount(SvgioAllView, out var count) != 0 || count <= 0)
        {
            return null;
        }

        return (AnchorIndex(view), count);
    }

    /// <summary>
    /// Moves the selection by <paramref name="delta"/> view positions,
    /// clamped at the folder's ends. Null when there is no anchor to move from.
    /// </summary>
    public static (int Index, int Count)? SelectRelative(IFolderView view, int delta)
    {
        if (view.ItemCount(SvgioAllView, out var count) != 0 || count <= 0)
        {
            return null;
        }

        var current = AnchorIndex(view);
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

    /// <summary>
    /// Paths of the items nearest <paramref name="currentPath"/> in view
    /// order, after then before it at each step out, at most
    /// <paramref name="max"/>. The current item is found by path, so the
    /// view's own index never has to agree with the list's. Null when the
    /// view does not hold it.
    /// </summary>
    public static List<string>? ReadNeighbors(IFolderView view, string currentPath, int max)
    {
        const int MaxScan = 5000;
        var iid = ShellItemArrayIid;
        if (max <= 0 || view.Items(SvgioAllView | SvgioFlagViewOrder, ref iid, out var arrayPtr) != 0 || arrayPtr == IntPtr.Zero)
        {
            return null;
        }

        IShellItemArray? items = null;
        try
        {
            items = (IShellItemArray)Marshal.GetUniqueObjectForIUnknown(arrayPtr);
            if (items.GetCount(out var count) != 0 || count <= 1 || count > MaxScan)
            {
                return null;
            }

            bool Is(int index) =>
                string.Equals(TryReadPath(items, (uint)index), currentPath, StringComparison.OrdinalIgnoreCase);

            var focused = AnchorIndex(view);
            var current = focused >= 0 && focused < count && Is(focused) ? focused : -1;
            for (var i = 0; current < 0 && i < count; i++)
            {
                current = Is(i) ? i : -1;
            }

            if (current < 0)
            {
                return null;
            }

            var paths = new List<string>(max);
            for (var step = 1; paths.Count < max && (current + step < count || current - step >= 0); step++)
            {
                foreach (var index in new[] { current + step, current - step })
                {
                    if (paths.Count < max && index >= 0 && index < count && TryReadPath(items, (uint)index) is { } path)
                    {
                        paths.Add(path);
                    }
                }
            }

            return paths;
        }
        finally
        {
            Release(items);
            Marshal.Release(arrayPtr);
        }
    }

    public static bool SelectIndex(IFolderView view, int index) =>
        view.ItemCount(SvgioAllView, out var count) == 0
        && index >= 0
        && index < count
        && view.SelectItem(index, SelectExclusive) == 0;

    /// <summary>Dev test only: grows the selection by one view index.</summary>
    public static bool AddToSelection(IFolderView view, int index) =>
        view.SelectItem(index, SelectAdditional) == 0;

    /// <summary>
    /// Releases an object this class handed out. Each one is a private
    /// wrapper (or a single counted reference), so releasing it can never
    /// pull the rug from a wrapper someone else still holds.
    /// </summary>
    public static void Release(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance))
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(instance);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static IShellBrowser? GetShellBrowser(object browser, out int hr)
    {
        hr = 0;
        if (browser is not IShellServiceProvider provider)
        {
            return null;
        }

        var service = TopLevelBrowserService;
        var iid = ShellBrowserIid;
        hr = provider.QueryService(ref service, ref iid, out var browserPtr);
        if (hr != 0 || browserPtr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return (IShellBrowser)Marshal.GetUniqueObjectForIUnknown(browserPtr);
        }
        finally
        {
            Marshal.Release(browserPtr);
        }
    }

    private static int AnchorIndex(IFolderView view)
    {
        if (view.GetFocusedItem(out var focused) == 0 && focused >= 0)
        {
            return focused;
        }

        return view.GetSelectionMarkedItem(out var marked) == 0 && marked >= 0 ? marked : -1;
    }

    /// <summary>
    /// Filesystem paths of a view's items. A member with no path on disk (a
    /// zip entry, a phone, a library node) takes no slot unless
    /// <paramref name="borrow"/> answers for it; for the anchor an empty
    /// list means "not a file".
    /// </summary>
    private static List<string>? ReadPaths(IFolderView view, uint flags, int maxItems, Func<IShellItem, string?>? borrow, out int hr)
    {
        var iid = ShellItemArrayIid;
        hr = view.Items(flags, ref iid, out var arrayPtr);
        if (hr != 0 || arrayPtr == IntPtr.Zero)
        {
            return null;
        }

        IShellItemArray? items = null;
        try
        {
            items = (IShellItemArray)Marshal.GetUniqueObjectForIUnknown(arrayPtr);
            hr = items.GetCount(out var count);
            if (hr != 0)
            {
                return null;
            }

            var paths = new List<string>((int)Math.Min(count, (uint)maxItems));
            var take = maxItems == 1 ? Math.Min(count, 1u) : count;
            for (uint i = 0; i < take && paths.Count < maxItems; i++)
            {
                if (TryReadPath(items, i, borrow) is { } path)
                {
                    paths.Add(path);
                }
            }

            return paths;
        }
        finally
        {
            Release(items);
            Marshal.Release(arrayPtr);
        }
    }

    private static string? TryReadPath(IShellItemArray items, uint index, Func<IShellItem, string?>? borrow = null)
    {
        if (items.GetItemAt(index, out var itemPtr) != 0 || itemPtr == IntPtr.Zero)
        {
            return null;
        }

        IShellItem? item = null;
        try
        {
            item = (IShellItem)Marshal.GetUniqueObjectForIUnknown(itemPtr);
            if (item.GetDisplayName(SigdnFilesysPath, out var namePtr) != 0 || namePtr == IntPtr.Zero)
            {
                return borrow?.Invoke(item);
            }

            try
            {
                var path = Marshal.PtrToStringUni(namePtr);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePtr);
            }
        }
        finally
        {
            Release(item);
            Marshal.Release(itemPtr);
        }
    }
}
