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
    private static readonly HashSet<string> ExplorerWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CabinetWClass",
        "ExploreWClass",
        "XamlExplorerHostIslandWindow"
    };

    private static readonly HashSet<string> DesktopWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW"
    };

    internal static bool IsDesktopWindowClass(string? className) =>
        !string.IsNullOrWhiteSpace(className) && DesktopWindowClasses.Contains(className);

    /// <summary>
    /// The roots the keyboard hook serves: Explorer, the desktop and dialogs
    /// that may be file pickers. Glimpse's own windows count by process.
    /// Everywhere else the hook is removed and Glimpse is out of the input path.
    /// </summary>
    internal static bool IsServedSurfaceClass(string? className) =>
        !string.IsNullOrWhiteSpace(className)
        && (DesktopWindowClasses.Contains(className)
            || ExplorerWindowClasses.Contains(className)
            || className.Equals(CommonFileDialogSelectionService.DialogWindowClass, StringComparison.OrdinalIgnoreCase));

    // "Edit" is exact and RichEdit is a prefix. XAML islands expose several
    // TextBox-flavored class names, so that one is intentionally broader.
    public static bool IsTextEditingClass(string? focusClass)
    {
        if (string.IsNullOrWhiteSpace(focusClass))
        {
            return false;
        }

        return focusClass.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || focusClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
            || focusClass.Contains("TextBox", StringComparison.OrdinalIgnoreCase);
    }

    public static ForegroundSelectionContext? ResolveForegroundContext(
        IntPtr root,
        string className,
        bool ignoreTextEditing,
        bool rootThreadHasTextEditingFocus,
        bool commonFileDialogListHasFocus = false)
    {
        if (root == IntPtr.Zero)
        {
            return null;
        }

        if (IsDesktopWindowClass(className))
        {
            return ignoreTextEditing && rootThreadHasTextEditingFocus
                ? null
                : new ForegroundSelectionContext(root, SelectionSource.Desktop, className);
        }

        if (className.Equals(CommonFileDialogSelectionService.DialogWindowClass, StringComparison.OrdinalIgnoreCase))
        {
            return commonFileDialogListHasFocus
                && (!ignoreTextEditing || !rootThreadHasTextEditingFocus)
                    ? new ForegroundSelectionContext(root, SelectionSource.CommonFileDialog, className)
                    : null;
        }

        if (!ExplorerWindowClasses.Contains(className))
        {
            return null;
        }

        if (ignoreTextEditing && rootThreadHasTextEditingFocus)
        {
            return null;
        }

        return new ForegroundSelectionContext(root, SelectionSource.Explorer, className);
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
