using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class SelectionGridViewGuardrailTests
{
    [Fact]
    public void GridKeepsHardMemoryAndWorkCaps()
    {
        Assert.Equal(512, SelectionGridView.MaxTiles);
        Assert.Equal(320, SelectionGridView.ThumbCacheCap);
        Assert.Equal(160, SelectionGridView.ThumbPixels);
        Assert.Equal(2, SelectionGridView.LookaheadRows);
        Assert.Equal(2, SelectionGridView.ThumbnailWorkerLimit);
    }

    [Fact]
    public void GridOnlyQueuesTheVisibleWindow()
    {
        var body = TestSource.Method(@"SpacePreview\Preview\SelectionGridView.cs", "RequestVisibleThumbnails");

        Assert.Contains("ViewportHeight", body, StringComparison.Ordinal);
        Assert.Contains("LookaheadRows", body, StringComparison.Ordinal);
        Assert.Contains("_pendingSet.Add", body, StringComparison.Ordinal);
        Assert.Contains("_workersRunning < ThumbnailWorkerLimit", body, StringComparison.Ordinal);
        Assert.DoesNotContain("for (var i = 0; i < _tiles.Length", body, StringComparison.Ordinal);
    }

    [Fact]
    public void GridThumbnailCacheEvictsDecodedImages()
    {
        var body = TestSource.Method(@"SpacePreview\Preview\SelectionGridView.cs", "StoreThumbnail");

        Assert.Contains("_thumbOrder.Count > ThumbCacheCap", body, StringComparison.Ordinal);
        Assert.Contains("_thumbCache.Remove(evicted)", body, StringComparison.Ordinal);
        Assert.Contains("_tiles[evicted].Image.Source = null", body, StringComparison.Ordinal);
    }

    [Fact]
    public void GridTeardownCancelsWorkAndDropsThumbnails()
    {
        var body = TestSource.Method(@"SpacePreview\Preview\SelectionGridView.cs", "Detach");

        Assert.Contains("_lifetime.Cancel()", body, StringComparison.Ordinal);
        Assert.Contains("_pendingQueue.Clear()", body, StringComparison.Ordinal);
        Assert.Contains("_pendingSet.Clear()", body, StringComparison.Ordinal);
        Assert.Contains("_thumbCache.Clear()", body, StringComparison.Ordinal);
        Assert.Contains("_thumbOrder.Clear()", body, StringComparison.Ordinal);
        Assert.Contains("tile.Image.Source = null", body, StringComparison.Ordinal);
    }

    [Fact]
    public void GridThumbnailPumpStaysCancellable()
    {
        var body = TestSource.Method(@"SpacePreview\Preview\SelectionGridView.cs", "PumpThumbnailsAsync");

        Assert.Contains("_lifetime.IsCancellationRequested", body, StringComparison.Ordinal);
        Assert.Contains("Task.Run", body, StringComparison.Ordinal);
        Assert.Contains("_lifetime.Token", body, StringComparison.Ordinal);
        Assert.Contains("OperationCanceledException", body, StringComparison.Ordinal);
    }
}
