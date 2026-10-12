namespace SpacePreview.Services;

internal enum RefreshSelectionAction
{
    KeepCurrent,
    HidePreview,
    ShowSelection
}

internal readonly record struct RefreshSelectionDecision(RefreshSelectionAction Action, ExplorerSelection? Selection);

internal static class PreviewInputPolicy
{
    // Explorer's own file windows. Task View and Alt+Tab are an Explorer
    // window too (XamlExplorerHostIslandWindow), with no files in it.
    private static readonly HashSet<string> ExplorerWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CabinetWClass",
        "ExploreWClass"
    };

    private static readonly HashSet<string> DesktopWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW"
    };

    // The window Explorer's header lives in on Windows 11: a XAML island
    // (Windows App SDK) that holds the tabs, the address bar, the search box
    // and the command bar, none of which has a window of its own.
    private const string HeaderIslandClass = "Microsoft.UI.Content.DesktopChildSiteBridge";

    internal static bool IsDesktopWindowClass(string? className) =>
        !string.IsNullOrWhiteSpace(className) && DesktopWindowClasses.Contains(className);

    /// <summary>
    /// The roots the keyboard hook serves by class alone: Explorer and the
    /// desktop. A dialog is served once its own windows show it to be a file
    /// picker, and Glimpse's own windows count by process. Everywhere else
    /// the hook is removed and Glimpse is out of the input path.
    /// </summary>
    internal static bool IsServedSurfaceClass(string? className) =>
        !string.IsNullOrWhiteSpace(className)
        && (DesktopWindowClasses.Contains(className) || ExplorerWindowClasses.Contains(className));

    /// <summary>A classic text box: an Edit, or any generation of RichEdit.</summary>
    public static bool IsTextEditingClass(string? focusClass) =>
        !string.IsNullOrWhiteSpace(focusClass)
        && (focusClass.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || focusClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase));

    internal static bool IsHeaderIslandClass(string? className) =>
        string.Equals(className, HeaderIslandClass, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the window in front offers to open. Whether the user is typing
    /// there is not asked here: the keymap asks that first, of every key.
    /// </summary>
    public static ForegroundSelectionContext? ResolveForegroundContext(
        IntPtr root,
        string className,
        bool commonFileDialogListHasFocus = false)
    {
        if (root == IntPtr.Zero)
        {
            return null;
        }

        if (IsDesktopWindowClass(className))
        {
            return new ForegroundSelectionContext(root, SelectionSource.Desktop, className);
        }

        if (className.Equals(CommonFileDialogSelectionService.DialogWindowClass, StringComparison.OrdinalIgnoreCase))
        {
            return commonFileDialogListHasFocus
                ? new ForegroundSelectionContext(root, SelectionSource.CommonFileDialog, className)
                : null;
        }

        return ExplorerWindowClasses.Contains(className)
            ? new ForegroundSelectionContext(root, SelectionSource.Explorer, className)
            : null;
    }

    public static RefreshSelectionDecision DecideRefresh(ExplorerSelection? selection, string? currentPath)
    {
        if (selection is null)
        {
            return new RefreshSelectionDecision(RefreshSelectionAction.HidePreview, null);
        }

        return string.Equals(selection.Path, currentPath, StringComparison.OrdinalIgnoreCase)
            ? new RefreshSelectionDecision(RefreshSelectionAction.KeepCurrent, selection)
            : new RefreshSelectionDecision(RefreshSelectionAction.ShowSelection, selection);
    }

    public static bool ShouldVerifyNavigationRelease(
        long verificationGeneration,
        long newestIssuedGeneration,
        long startedGenerationAtRelease,
        long lastChangedGeneration)
    {
        // The same key gets one trailing chance when its key-down read beat
        // Explorer's commit. An older release must never resurrect optional
        // work after a newer input was issued, and a release which arrived
        // before its own lookup started needs no duplicate trailing read.
        return verificationGeneration >= newestIssuedGeneration
            && verificationGeneration <= startedGenerationAtRelease
            && verificationGeneration > lastChangedGeneration;
    }
}
