using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Explorer's ShellWindows census goes stale: entries duplicate or point at
/// old tab panes while their window lives on, until it is reopened. Opening
/// and browsing choose an entry by one rule, tested here with fake entries.
/// </summary>
public sealed class SelectionReaderTests
{
    private static readonly IntPtr ActiveTab = new(0x10);
    private static readonly IntPtr OtherTab = new(0x20);

    [Fact]
    public void TheActiveTabsEntrySpeaksForTheWindow()
    {
        var reads = new List<int>();
        var (value, outcome) = ExplorerCensus.Choose(
            [OtherTab, ActiveTab],
            ActiveTab,
            Reader(reads, "other", "active"));

        Assert.Equal("active", value);
        Assert.Equal(ExplorerCensus.Outcome.ActiveTab, outcome);
        Assert.Equal([1], reads);
    }

    [Fact]
    public void ADeadDuplicateOfTheActiveTabDoesNotSilenceTheLiveOne()
    {
        var reads = new List<int>();
        var (value, outcome) = ExplorerCensus.Choose(
            [ActiveTab, ActiveTab],
            ActiveTab,
            Reader(reads, null, "live"));

        Assert.Equal("live", value);
        Assert.Equal(ExplorerCensus.Outcome.ActiveTab, outcome);
        Assert.Equal([0, 1], reads);
    }

    [Fact]
    public void AnActiveTabWithNothingSelectedIsAnAnswerNotAFailure()
    {
        var (value, outcome) = ExplorerCensus.Choose(
            [ActiveTab, OtherTab],
            ActiveTab,
            Reader([], null, "other tab's selection"));

        Assert.Null(value);
        Assert.Equal(ExplorerCensus.Outcome.ActiveTabEmpty, outcome);
    }

    [Fact]
    public void AShellWithoutTabsTakesTheFirstUsableEntry()
    {
        var (value, outcome) = ExplorerCensus.Choose(
            [new IntPtr(0x30), new IntPtr(0x40)],
            IntPtr.Zero,
            Reader([], null, "second"));

        Assert.Equal("second", value);
        Assert.Equal(ExplorerCensus.Outcome.NoTabIdentity, outcome);
    }

    [Fact]
    public void AWindowWithOneStaleEntryIsRescued()
    {
        // The active tab never registered; the one entry left can only be
        // this window's browser.
        var (value, outcome) = ExplorerCensus.Choose(
            [OtherTab],
            ActiveTab,
            Reader([], "rescued"));

        Assert.Equal("rescued", value);
        Assert.Equal(ExplorerCensus.Outcome.StaleTabRescue, outcome);
    }

    [Fact]
    public void SeveralStaleEntriesAreAmbiguousAndDead()
    {
        var (value, outcome) = ExplorerCensus.Choose(
            [OtherTab, new IntPtr(0x30)],
            ActiveTab,
            Reader([], "a", "b"));

        Assert.Null(value);
        Assert.Equal(ExplorerCensus.Outcome.DeadCensus, outcome);
    }

    [Fact]
    public void NoEntryMeansNoSelection()
    {
        var (value, outcome) = ExplorerCensus.Choose<string>([], ActiveTab, _ => "never");

        Assert.Null(value);
        Assert.Equal(ExplorerCensus.Outcome.NoEntry, outcome);
    }

    [Fact]
    public void OpeningAndBrowsingChooseTheEntryByTheSameRule()
    {
        // Browse once took the first active-tab match even when it was a
        // dead duplicate; Space skipped it. One rule heals both.
        var service = TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs");
        var browseStart = service.IndexOf("T? WithExplorerView<T>(", StringComparison.Ordinal);
        Assert.True(browseStart >= 0);
        var browseLookup = service[browseStart..service.IndexOf("private static", browseStart, StringComparison.Ordinal)];

        Assert.Contains("ExplorerCensus.Choose", TestSource.CodeMethod(@"SpacePreview\Services\ExplorerSelectionService.cs", "TryGetSelectionFromExplorerWindow"), StringComparison.Ordinal);
        Assert.Contains("ExplorerCensus.Choose", browseLookup, StringComparison.Ordinal);
        Assert.Contains("WithExplorerView(explorerHwnd, view => Step(", service, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(unchecked((int)0x80010108), true)]
    [InlineData(unchecked((int)0x800706BA), true)]
    [InlineData(unchecked((int)0x80010007), true)]
    [InlineData(unchecked((int)0x80010012), true)]
    [InlineData(0, false)]
    [InlineData(unchecked((int)0x80004005), false)]
    [InlineData(unchecked((int)0x80004002), false)]
    public void ADeadExplorerReadsAsDisconnectedNotAsAnEmptySelection(int hr, bool disconnected)
    {
        // The desktop session rebuilds once on a disconnect; an empty
        // selection must not trigger that.
        Assert.Equal(disconnected, ShellViews.IsDisconnected(hr));
    }

    [Fact]
    public void ThePressPathNeverAsksForTheViewPosition()
    {
        // Explorer can block the IFolderView position query for hundreds of
        // milliseconds (458 ms in the field). The selected path drives the
        // preview; position stays off the Space and arrow lookups.
        var lookup = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "RunSelectionLookupAsync");

        Assert.Contains("includeViewPosition: false", lookup, StringComparison.Ordinal);
        Assert.DoesNotContain("includeViewPosition: true", lookup, StringComparison.Ordinal);
    }

    [Fact]
    public void APathOnlyRefreshOfTheCurrentFileKeepsTheBrowsePosition()
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
    }

    private static Func<int, string?> Reader(List<int> reads, params string?[] values) => index =>
    {
        reads.Add(index);
        return values[index];
    };
}
