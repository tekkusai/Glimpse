using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Explorer's ShellWindows census goes stale: entries deregister or
/// duplicate while their window lives on, and the state persists until the
/// window is reopened. The Space key must survive that, so these tests pin
/// the lookup's resilience structure to the source.
/// </summary>
public sealed class ExplorerSelectionResilienceTests
{
    [Fact]
    public void SelectionLookupSurvivesStaleTabRegistration()
    {
        var source = TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs");

        // A husk entry matching the active tab must not silence a live
        // duplicate behind it: the matched-but-empty case keeps scanning.
        Assert.Contains("sawActiveTab = true;", source, StringComparison.Ordinal);
        Assert.Contains("// Explorer can leave a dead duplicate entry registered for", source, StringComparison.Ordinal);

        // When no entry matches the active tab, a single-entry window is
        // unambiguous and rescues the press instead of dying until reopen.
        Assert.Contains("if (entries.Count == 1)", source, StringComparison.Ordinal);
        Assert.Contains("explorer selection rescued from stale tab registration", source, StringComparison.Ordinal);
        Assert.Contains("browse entry rescued from stale tab registration", source, StringComparison.Ordinal);

        // The truly dead case names every registered entry so a field log
        // diagnoses the census, not just the symptom.
        Assert.Contains("explorer selection dead: activeTab=", source, StringComparison.Ordinal);
        Assert.Contains("DescribeEntries(entries)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionLookupAndBrowseShareOneEntryCensus()
    {
        var source = TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs");

        // One collector feeds both the Space-press lookup and arrow
        // browsing, so a census fix heals both paths together.
        Assert.Contains("CollectWindowEntries(windows, explorerHwnd)", source, StringComparison.Ordinal);
        var occurrences = CountOccurrences(source, "CollectWindowEntries(windows, explorerHwnd)");
        Assert.True(occurrences >= 2, $"Expected both lookup paths to use the shared census, found {occurrences} use(s).");
    }

    [Fact]
    public void SlowSelectionLookupLogSeparatesQueueStaAndContinuation()
    {
        var trace = TestSource.Read(@"SpacePreview\Services\ExplorerSelectionLookupTrace.cs");
        var service = TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs");
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");

        // The fast path emits no extra line.
        Assert.Contains("if (totalMilliseconds < SlowThresholdMilliseconds)", trace, StringComparison.Ordinal);
        Assert.Contains("selection lookup slow +{totalMilliseconds:0}ms", trace, StringComparison.Ordinal);

        // A slow line distinguishes queue and continuation latency from four
        // service buckets that cover the expensive Shell operations.
        foreach (var phase in new[]
                 {
                     "staDispatch=",
                     "service=",
                     "activeTab=",
                     "census=",
                     "selection=",
                     "view=",
                     "other=",
                     "continuation="
                 })
        {
            Assert.Contains(phase, trace, StringComparison.Ordinal);
        }

        Assert.Contains("diagnostics.MarkStaStarted();", controller, StringComparison.Ordinal);
        Assert.Contains("diagnostics.MarkStaCompleted();", controller, StringComparison.Ordinal);
        Assert.Contains("diagnostics.WriteIfSlow(origin, selection is not null);", controller, StringComparison.Ordinal);
        Assert.Contains("diagnostics?.SetOutcome", service, StringComparison.Ordinal);
        foreach (var phase in new[] { "ActiveTab", "Census", "Selection", "View" })
        {
            Assert.Contains($"ExplorerSelectionLookupPhase.{phase}", service, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SelectionLookupSkipsOptionalViewWorkByNavigationIntent()
    {
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var service = TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs");
        var shellInterop = TestSource.Read(@"SpacePreview\Native\ShellComInterop.cs");
        var previewWindow = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        // Showing the newly selected path is latency-critical. Folder-view
        // position is optional chrome and must not sit in front of it on the
        // shared Shell STA (field traces found this call blocking for ~700ms).
        Assert.Equal(3, CountOccurrences(controller, "includeViewPosition: false"));
        Assert.Contains("bool includeViewPosition = true", service, StringComparison.Ordinal);
        Assert.Contains("includeViewPosition && FolderViewNavigator.TryGetPosition", service, StringComparison.Ordinal);

        // A true multi-selection is preserved for explicit Space-open and
        // modifier-driven selection changes, but ordinary navigation does not
        // ask IFolderView to order Explorer's transient group-boundary set.
        Assert.Equal(4, CountOccurrences(service, "bool includeMultiSelectionPaths = true"));
        Assert.Contains("if (includeMultiSelectionPaths && selection.SelectedCount > 1)", service, StringComparison.Ordinal);
        Assert.Contains("SelectedPaths = FolderViewNavigator.TryGetSelectedPaths(window)", service, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(controller, "includeMultiSelectionPaths: true"));
        Assert.Contains(
            "includeMultiSelectionPaths: activeRequest.IncludeMultiSelectionPaths",
            controller,
            StringComparison.Ordinal);
        Assert.Contains("TryApplyIntentionalMultiSelection(selection, activeRequest)", controller, StringComparison.Ordinal);
        Assert.Contains("selection with { Path = currentPath }", controller, StringComparison.Ordinal);
        Assert.Contains("_previewWindow.SetSelectionCount(selection.SelectedCount)", controller, StringComparison.Ordinal);
        Assert.Contains("_previewWindow.SetSelectionCount(1)", controller, StringComparison.Ordinal);
        Assert.Contains("internal void SetSelectionCount(int selectedCount)", previewWindow, StringComparison.Ordinal);
        Assert.Contains("ApplyHeaderSubtitle(path, _shortcutTarget, Math.Max(1, selectedCount))", previewWindow, StringComparison.Ordinal);

        // Empty/virtual Shell selections still need their existing view-based
        // path fallback; ordinary navigation reads at most its first member
        // instead of sorting the full folder view.
        Assert.Contains("TryReadSelectionFromView(", service, StringComparison.Ordinal);
        Assert.Contains("FolderViewNavigator.TryGetFirstSelectedPath(window)", service, StringComparison.Ordinal);
        Assert.Contains("ReadItemPaths(view, SvgioSelection, maxItems: 1)", shellInterop, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(service, "FolderViewNavigator.TryGetSelectedPaths(window)") >= 2,
            "Expected both multi-selection carrying and fallback path discovery to remain enabled.");
    }

    [Fact]
    public void FullPositionLookupRemainsAvailableForProbesAndExplicitBrowsing()
    {
        var app = TestSource.Read(@"SpacePreview\App.xaml.cs");
        var service = TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs");

        // Omitted options preserve the diagnostic/probe contract: those
        // callers intentionally inspect the current view index and count.
        Assert.Contains("bool includeViewPosition = true", service, StringComparison.Ordinal);
        Assert.Contains("TryGetSelectionFromExplorerWindow(hwnd)", app, StringComparison.Ordinal);

        // Once the user explicitly browses, that operation already resolves
        // authoritative position and count and returns them with the landing.
        Assert.Contains("FolderViewNavigator.TrySelectRelative(entry, delta)", service, StringComparison.Ordinal);
        Assert.Contains("ViewIndex = moved.Value.Index, ViewCount = moved.Value.Count", service, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownRefreshPositionCannotOverwritePositionLearnedByExplicitBrowse()
    {
        const string path = @"C:\Users\PreviewUser\Downloads\current.pdf";
        var pathOnlyRefresh = new ExplorerSelection(
            path,
            new IntPtr(0x1234),
            SelectedCount: 3,
            ViewIndex: -1,
            ViewCount: 0);

        Assert.Equal(
            RefreshSelectionAction.KeepCurrent,
            PreviewInputPolicy.DecideRefresh(pathOnlyRefresh, path).Action);

        // The controller exits on KeepCurrent before it can apply either the
        // path-only selection's unknown -1/0 position or Explorer's temporary
        // group-boundary count over the current browse state.
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var keepCurrent = controller.IndexOf(
            "if (decision.Action == RefreshSelectionAction.KeepCurrent)",
            StringComparison.Ordinal);
        Assert.True(keepCurrent >= 0);
        var keepCurrentExit = controller.IndexOf("continue;", keepCurrent, StringComparison.Ordinal);
        var selectionAssignment = controller.IndexOf("selection = decision.Selection;", keepCurrent, StringComparison.Ordinal);
        var applyBrowseContext = controller.IndexOf("ApplySelectionBrowseContext(selection);", keepCurrent, StringComparison.Ordinal);
        Assert.True(keepCurrentExit > keepCurrent);
        Assert.True(selectionAssignment > keepCurrentExit);
        Assert.True(applyBrowseContext > keepCurrentExit);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

}
