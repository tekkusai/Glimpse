using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using SpacePreview.Native;

namespace SpacePreview.Services;

/// <summary>
/// Read-only adapter for the shell view hosted by Windows' Common Item
/// Dialog. It deliberately does not invoke, select, focus, or inject into the
/// dialog process. Unsupported or virtual selections return null so the
/// dialog keeps its native Space behavior.
/// </summary>
internal static class CommonFileDialogSelectionService
{
    internal const string DialogWindowClass = "#32770";
    internal const string ShellViewWindowClass = "SHELLDLL_DefView";
    internal const string ExplorerFrameWindowClass = "DUIViewWndClassName";
    internal const string BreadcrumbParentWindowClass = "Breadcrumb Parent";
    internal const string BreadcrumbToolbarWindowClass = "ToolbarWindow32";

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Lazy<IReadOnlyList<string>> KnownFilesystemFolders = new(
        CreateKnownFilesystemFolders);

    private static readonly string[] AddressAutomationIds =
    [
        "1001",
        "41477",
        "AddressBandRoot"
    ];

    // The Type column, by the property the shell fills it from. Details
    // and tiles views carry it; list and icon views do not.
    private const string TypeCellAutomationId = "System.ItemTypeText";

    // The most entries one search reads for one row.
    internal const int SearchLimit = 1024;

    /// <summary>
    /// What a selected row says of itself: the shell's display name for the
    /// item, and the text of its Type column where the view shows one. The
    /// type is a second read across processes, made only when the name alone
    /// does not settle the row.
    /// </summary>
    internal readonly record struct SelectionCandidate(string Name, Func<string?> ReadType);

    public static bool HasDialogWindowClass(IntPtr root) =>
        root != IntPtr.Zero
        && NativeMethods.GetClassNameString(root).Equals(
            DialogWindowClass,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A dialog with files in it: one that holds a shell view, or the
    /// Explorer frame an Open or Save dialog builds first and puts its view
    /// in once the folder has opened. A message box, an installer and a
    /// dialog-based app hold neither. Any process may ask this of any window.
    /// </summary>
    public static bool IsFilePicker(IntPtr root) =>
        HasDialogWindowClass(root)
        && (NativeMethods.FindDescendantWindowByClass(root, ShellViewWindowClass) != IntPtr.Zero
            || NativeMethods.FindDescendantWindowByClass(root, ExplorerFrameWindowClass) != IntPtr.Zero);

    /// <summary>
    /// Hook-path guard: only the shell view may claim Space. The filename
    /// edit, search box, buttons, places rail, and every other dialog control
    /// keep their native keyboard behavior.
    /// </summary>
    public static bool HasFileListFocus(IntPtr dialogRoot)
    {
        if (dialogRoot == IntPtr.Zero
            || !NativeMethods.GetClassNameString(dialogRoot).Equals(
                DialogWindowClass,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(dialogRoot, out _);
        if (threadId == 0)
        {
            return false;
        }

        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };
        if (!NativeMethods.GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == IntPtr.Zero)
        {
            return false;
        }

        var focusRoot = NativeMethods.GetAncestor(info.hwndFocus, NativeMethods.GA_ROOT);
        if (focusRoot != IntPtr.Zero && focusRoot != dialogRoot)
        {
            return false;
        }

        for (var current = info.hwndFocus;
             current != IntPtr.Zero && current != dialogRoot;
             current = NativeMethods.GetParent(current))
        {
            if (NativeMethods.GetClassNameString(current).Equals(
                    ShellViewWindowClass,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static ExplorerSelection? TryGetSelection(IntPtr dialogRoot)
    {
        if (!HasDialogWindowClass(dialogRoot))
        {
            return null;
        }

        try
        {
            var shellViewHwnd = NativeMethods.FindDescendantWindowByClass(
                dialogRoot,
                ShellViewWindowClass);
            if (shellViewHwnd == IntPtr.Zero)
            {
                return null;
            }

            var shellRoot = AutomationElement.FromHandle(shellViewHwnd);
            var dialogAutomationRoot = AutomationElement.FromHandle(dialogRoot);
            if (shellRoot is null || dialogAutomationRoot is null)
            {
                return null;
            }

            var selectedElements = GetSelectedElements(shellRoot);
            if (selectedElements.Count == 0)
            {
                return null;
            }

            var candidates = selectedElements.Select(ReadSelectionCandidate).ToArray();
            var folderPath = TryGetBreadcrumbFolderPath(dialogRoot, out var breadcrumbState);
            if (folderPath is null)
            {
                var addressValues = ReadValuesInsideAutomationIds(
                    dialogAutomationRoot,
                    AddressAutomationIds);
                folderPath = ResolveExistingDirectory(addressValues);
            }

            var paths = ResolveExistingEntries(folderPath, candidates);
            if (paths.Count == 0)
            {
                DiagnosticLog.Write(
                    $"file dialog selection unresolved hwnd=0x{dialogRoot.ToInt64():X} selected={selectedElements.Count} breadcrumb={breadcrumbState}");
                return null;
            }

            return new ExplorerSelection(
                paths[0],
                dialogRoot,
                SelectedCount: Math.Max(1, selectedElements.Count),
                SelectedPaths: paths.Count > 1 ? paths : null);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException ex)
        {
            DiagnosticLog.Write($"file dialog automation unavailable: 0x{ex.HResult:X8}");
            return null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"file dialog selection failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    internal static IReadOnlyList<string> ResolveExistingEntries(
        string? folderPath,
        IEnumerable<SelectionCandidate> selections)
    {
        var resolved = new List<string>();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return resolved;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            var path = ResolveEntry(folderPath, selection);
            if (path is not null && seen.Add(path))
            {
                resolved.Add(path);
            }
        }

        return resolved;
    }

    /// <summary>
    /// A row is named as the shell displays the item, not as the file is
    /// named: Windows drops the extension of every known type unless told
    /// to show it, and of a shortcut always. So the row means the entry of
    /// the dialog's folder to which the shell gives that name, and the
    /// shell is asked; its rules are not rebuilt here. Two entries can bear
    /// one name ("report" for report.docx and report.pdf, or a folder
    /// beside them). Then the Type column decides, and in a view without
    /// one nothing resolves: a wrong file is worse than no preview.
    /// </summary>
    private static string? ResolveEntry(string folderPath, SelectionCandidate selection)
    {
        var name = selection.Name;
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var bearers = new List<(string Name, FileAttributes Attributes, string Type)>();
        foreach (var entry in FindEntries(Path.Combine(folderPath, name + ".*")))
        {
            // The name itself, or the name and the one extension the shell
            // may have dropped. Nothing else in the folder can bear it.
            if (entry.Name.Length > name.Length && entry.Name.IndexOf('.', name.Length + 1) >= 0)
            {
                continue;
            }

            var info = default(ShellFileInfo);
            var kind = (entry.Attributes & FileAttributes.Directory) != 0 ? FileAttributes.Directory : FileAttributes.Normal;
            if (SHGetFileInfoW(entry.Name, kind, ref info, Marshal.SizeOf<ShellFileInfo>(), ShellDisplayAndTypeOfName) != IntPtr.Zero
                && string.Equals(info.DisplayName, name, StringComparison.Ordinal))
            {
                bearers.Add((entry.Name, entry.Attributes, info.TypeName));
            }
        }

        if (bearers.Count > 1)
        {
            var type = selection.ReadType();
            bearers.RemoveAll(bearer => !string.Equals(bearer.Type, type, StringComparison.Ordinal));
        }

        // Every bearer of the name is counted before the cloud check, or the
        // row of a cloud-only file would open the local file beside it.
        return bearers.Count == 1 && IsAvailableLocalEntryAttributes(bearers[0].Attributes)
            ? Path.Combine(folderPath, bearers[0].Name)
            : null;
    }

    /// <summary>
    /// The entries a pattern names, filtered by the file system or the file
    /// server and not here, so the cost is the matches and not the folder.
    /// Past <see cref="SearchLimit"/> matches the search stops and names
    /// nothing: every bearer of a name has to be seen to choose one, and a
    /// slow share must not be read without end.
    /// </summary>
    private static List<(string Name, FileAttributes Attributes)> FindEntries(string pattern)
    {
        var entries = new List<(string, FileAttributes)>();

        // Past MAX_PATH the Win32 search wants the extended form.
        var search = pattern.Length < 260 ? pattern
            : pattern.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + pattern[2..]
            : @"\\?\" + pattern;
        var find = FindFirstFileExW(search, FindExInfoBasic, out var data, 0, IntPtr.Zero, FindFirstExLargeFetch);
        if (find == new IntPtr(-1))
        {
            return entries;
        }

        try
        {
            do
            {
                if (data.Name is "." or "..")
                {
                    continue;
                }

                if (entries.Count == SearchLimit)
                {
                    return [];
                }

                entries.Add((data.Name, data.Attributes));
            }
            while (FindNextFileW(find, out data));
        }
        finally
        {
            FindClose(find);
        }

        return entries;
    }

    internal static string NormalizeBreadcrumbFolderText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (TryNormalizeRootedPath(trimmed, out var rooted))
        {
            return rooted;
        }

        var colonIndex = trimmed.IndexOf(':');
        var isDriveLetter = colonIndex == 1
            && char.IsLetter(trimmed[0])
            && trimmed.Length > 2
            && (trimmed[2] == Path.DirectorySeparatorChar
                || trimmed[2] == Path.AltDirectorySeparatorChar);
        return colonIndex >= 0 && !isDriveLetter
            ? trimmed[(colonIndex + 1)..].Trim()
            : trimmed;
    }

    internal static string? ResolveBreadcrumbFolderText(
        string? value,
        IEnumerable<string> knownFilesystemFolders)
    {
        var normalized = NormalizeBreadcrumbFolderText(value);
        if (TryNormalizeRootedPath(normalized, out var rooted) && Directory.Exists(rooted))
        {
            return rooted;
        }

        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return null;
        }

        foreach (var folder in knownFilesystemFolders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var name = Path.GetFileName(
                folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (name.Equals(normalized, StringComparison.CurrentCultureIgnoreCase))
            {
                return Path.GetFullPath(folder);
            }
        }

        return null;
    }

    private static string? TryGetBreadcrumbFolderPath(IntPtr dialogRoot, out string state)
    {
        var breadcrumb = NativeMethods.FindDescendantWindowByClass(
            dialogRoot,
            BreadcrumbParentWindowClass);
        if (breadcrumb == IntPtr.Zero)
        {
            state = "missing";
            return null;
        }

        var sawText = false;
        foreach (var toolbar in NativeMethods.FindDescendantWindowsByClass(
                     breadcrumb,
                     BreadcrumbToolbarWindowClass))
        {
            var text = NativeMethods.GetWindowMessageTextString(toolbar);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sawText = true;
            }

            var path = ResolveBreadcrumbFolderText(text, KnownFilesystemFolders.Value);
            if (path is not null)
            {
                state = Path.IsPathFullyQualified(NormalizeBreadcrumbFolderText(text))
                    ? "rooted"
                    : "known-folder";
                return path;
            }
        }

        state = sawText ? "display-only" : "empty";
        return null;
    }

    private static IReadOnlyList<string> CreateKnownFilesystemFolders()
    {
        var folders = new List<string>();
        AddKnownFolder(folders, NativeMethods.TryGetKnownFolderPath(DownloadsFolderId));
        AddKnownFolder(folders, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddKnownFolder(folders, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddKnownFolder(folders, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddKnownFolder(folders, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddKnownFolder(folders, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddKnownFolder(folders, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        AddKnownFolder(folders, Environment.GetEnvironmentVariable("OneDrive"));
        AddKnownFolder(folders, Environment.GetEnvironmentVariable("OneDriveConsumer"));
        AddKnownFolder(folders, Environment.GetEnvironmentVariable("OneDriveCommercial"));
        return folders;
    }

    private static void AddKnownFolder(List<string> folders, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)
            && Directory.Exists(path)
            && !folders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            folders.Add(path);
        }
    }

    private static IReadOnlyList<AutomationElement> GetSelectedElements(AutomationElement shellRoot)
    {
        var selectionContainers = shellRoot.FindAll(
            TreeScope.Element | TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsSelectionPatternAvailableProperty, true));
        foreach (AutomationElement container in selectionContainers)
        {
            try
            {
                if (container.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern)
                    && pattern is SelectionPattern selection)
                {
                    var selected = selection.Current.GetSelection();
                    if (selected.Length > 0)
                    {
                        return selected;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        // Older shell providers sometimes expose SelectionItem without a
        // Selection container. This slower walk is a fallback, off the input
        // hook and limited to the dialog's shell view.
        var selectionItems = shellRoot.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true));
        var fallback = new List<AutomationElement>();
        foreach (AutomationElement item in selectionItems)
        {
            try
            {
                if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                    && pattern is SelectionItemPattern selectionItem
                    && selectionItem.Current.IsSelected)
                {
                    fallback.Add(item);
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return fallback;
    }

    private static SelectionCandidate ReadSelectionCandidate(AutomationElement element)
    {
        try
        {
            return new SelectionCandidate(element.Current.Name ?? string.Empty, () => ReadTypeCell(element));
        }
        catch (ElementNotAvailableException)
        {
            return new SelectionCandidate(string.Empty, () => null);
        }
    }

    private static string? ReadTypeCell(AutomationElement row)
    {
        try
        {
            var cell = row.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.AutomationIdProperty, TypeCellAutomationId));
            return cell is not null
                && cell.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                && pattern is ValuePattern type
                    ? type.Current.Value
                    : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadValuesInsideAutomationIds(
        AutomationElement root,
        IReadOnlyList<string> automationIds)
    {
        var conditions = automationIds
            .Select(id => (Condition)new PropertyCondition(AutomationElement.AutomationIdProperty, id))
            .ToArray();
        var condition = conditions.Length == 1 ? conditions[0] : new OrCondition(conditions);
        var hosts = root.FindAll(TreeScope.Descendants, condition);
        var values = new List<string>();
        foreach (AutomationElement host in hosts)
        {
            TryReadValue(host, values);
            var descendants = host.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
            foreach (AutomationElement descendant in descendants)
            {
                TryReadValue(descendant, values);
            }
        }

        return values;
    }

    private static void TryReadValue(AutomationElement element, List<string> values)
    {
        try
        {
            var current = element.Current;
            AddValue(values, current.Name);
            AddValue(values, current.HelpText);
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                && pattern is ValuePattern value)
            {
                AddValue(values, value.Current.Value);
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void AddValue(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }

    private static string? ResolveExistingDirectory(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            var path = ResolveBreadcrumbFolderText(value, KnownFilesystemFolders.Value);
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }

    internal static bool IsAvailableLocalEntryAttributes(FileAttributes attributes)
    {
        const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
        var unavailable = FileAttributes.Offline
            | RecallOnOpen
            | RecallOnDataAccess;
        return (attributes & unavailable) == 0;
    }

    private static bool TryNormalizeRootedPath(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().Trim('"');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            candidate = uri.LocalPath;
        }
        else if (!Path.IsPathFullyQualified(candidate))
        {
            var rootedStart = FindRootedPathStart(candidate);
            if (rootedStart < 0)
            {
                return false;
            }

            candidate = candidate[rootedStart..].Trim().Trim('"');
        }

        try
        {
            if (!Path.IsPathFullyQualified(candidate))
            {
                return false;
            }

            path = Path.GetFullPath(candidate);
            return true;
        }
        catch
        {
            path = string.Empty;
            return false;
        }
    }

    private static int FindRootedPathStart(string value)
    {
        var unc = value.IndexOf(@"\\", StringComparison.Ordinal);
        if (unc >= 0)
        {
            return unc;
        }

        for (var i = 0; i + 2 < value.Length; i++)
        {
            if (char.IsLetter(value[i])
                && value[i + 1] == ':'
                && (value[i + 2] == Path.DirectorySeparatorChar
                    || value[i + 2] == Path.AltDirectorySeparatorChar))
            {
                return i;
            }
        }

        return -1;
    }

    // FindExInfoBasic: the search skips the 8.3 name it would otherwise fetch.
    // FIND_FIRST_EX_LARGE_FETCH: a large answer comes in few round trips.
    private const int FindExInfoBasic = 1;
    private const int FindFirstExLargeFetch = 2;

    // SHGFI_DISPLAYNAME | SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES: the shell
    // names the entry from its name and kind alone and reads nothing on disk.
    private const uint ShellDisplayAndTypeOfName = 0x200 | 0x400 | 0x10;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstFileExW(
        string lpFileName,
        int fInfoLevelId,
        out FindData lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out FindData lpFindFileData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(
        string pszPath,
        FileAttributes dwFileAttributes,
        ref ShellFileInfo psfi,
        int cbFileInfo,
        uint uFlags);

    // WIN32_FIND_DATAW. Its times are pairs of DWORDs, hence the packing.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct FindData
    {
        public FileAttributes Attributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long Size;
        public long Reserved;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Name;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string ShortName;
    }

    // SHFILEINFOW.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}
