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

    private static readonly string[] FileNameAutomationIds =
    [
        "1148",
        "FileNameControlHost"
    ];

    internal readonly record struct SelectionCandidate(string Name, IReadOnlyList<string> Values);

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
                folderPath = ResolveExistingDirectory(addressValues)
                    ?? ResolveParentDirectory(candidates);
            }

            var paths = ResolveExistingEntries(folderPath, candidates);
            if (paths.Count == 0)
            {
                var fileNameValues = ReadValuesInsideAutomationIds(
                    dialogAutomationRoot,
                    FileNameAutomationIds);
                folderPath ??= ResolveExistingDirectory(fileNameValues);
                paths = ResolveExistingEntries(folderPath, candidates, fileNameValues);
            }
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
        IEnumerable<SelectionCandidate> selections,
        IEnumerable<string>? fileNameValues = null)
    {
        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectionCount = 0;

        foreach (var selection in selections)
        {
            selectionCount++;
            var before = resolved.Count;
            foreach (var value in selection.Values)
            {
                TryAddExistingEntry(value, resolved, seen);
            }

            TryAddExistingEntry(selection.Name, resolved, seen);
            if (resolved.Count == before && TryCombineWithFolder(folderPath, selection.Name, out var combined))
            {
                TryAddExistingEntry(combined, resolved, seen);
            }
        }

        if (fileNameValues is not null && (selectionCount == 0 || resolved.Count < selectionCount))
        {
            foreach (var value in fileNameValues)
            {
                foreach (var name in SplitDialogFileNames(value))
                {
                    TryAddExistingEntry(name, resolved, seen);
                    if (TryCombineWithFolder(folderPath, name, out var combined))
                    {
                        TryAddExistingEntry(combined, resolved, seen);
                    }
                }
            }
        }

        return resolved;
    }

    internal static IReadOnlyList<string> SplitDialogFileNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (!trimmed.Contains('"'))
        {
            return [trimmed];
        }

        var names = new List<string>();
        var start = -1;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != '"')
            {
                continue;
            }

            if (start < 0)
            {
                start = i + 1;
                continue;
            }

            if (i > start)
            {
                names.Add(trimmed[start..i]);
            }

            start = -1;
        }

        return names;
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
        var values = new List<string>();
        string name;
        try
        {
            var current = element.Current;
            name = current.Name ?? string.Empty;
            AddValue(values, current.HelpText);
            AddValue(values, current.ItemStatus);
        }
        catch (ElementNotAvailableException)
        {
            return new SelectionCandidate(string.Empty, []);
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern)
                && valuePattern is ValuePattern value)
            {
                AddValue(values, value.Current.Value);
            }
        }
        catch (InvalidOperationException)
        {
        }

        return new SelectionCandidate(name, values);
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

    private static string? ResolveParentDirectory(IEnumerable<SelectionCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            foreach (var value in candidate.Values.Prepend(candidate.Name))
            {
                if (!TryNormalizeRootedPath(value, out var path) || !IsExistingLocalEntry(path))
                {
                    continue;
                }

                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    return parent;
                }
            }
        }

        return null;
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

    private static bool TryCombineWithFolder(string? folderPath, string? name, out string combined)
    {
        combined = string.Empty;
        if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim().Trim('"');
        if (Path.IsPathFullyQualified(trimmed)
            || trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        try
        {
            combined = Path.Combine(folderPath, trimmed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryAddExistingEntry(
        string? value,
        List<string> resolved,
        HashSet<string> seen)
    {
        if (!TryNormalizeRootedPath(value, out var path)
            || !IsExistingLocalEntry(path)
            || !seen.Add(path))
        {
            return;
        }

        resolved.Add(path);
    }

    private static bool IsExistingLocalEntry(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            return IsAvailableLocalEntryAttributes(File.GetAttributes(path));
        }
        catch
        {
            return false;
        }
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
}
