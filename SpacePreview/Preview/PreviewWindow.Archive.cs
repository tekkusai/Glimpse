using System.IO;
using System.Threading;
using System.Windows;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    private const int ArchiveRootCap = 500;
    private const int ArchiveChildrenCap = 200;

    /// <summary>
    /// An archive lists like a folder: the same card with the archive's own
    /// picture, its name, what it holds, and a size bar on every row.
    /// Folders inside expand on demand, so a huge archive costs nothing
    /// until it is explored. Null when it cannot be read.
    /// </summary>
    private async Task<FrameworkElement?> TryBuildArchiveCardAsync(string path, CancellationToken token)
    {
        var dpi = PixelsPerDip();
        var iconPixels = DevicePixels(CardIconSide, dpi);
        var (listing, image) = await Task.Run(
            () =>
            {
                var read = ArchivePreviewLoader.TryLoad(path, token);
                return (read, read is null ? null : ShellThumbnailProvider.TryGetThumbnail(path, iconSize: iconPixels));
            },
            token);
        if (listing is null)
        {
            return null;
        }

        var list = new EntryList(ArchiveRootCap, ArchiveChildrenCap, meters: true);
        list.Show(ArchiveRows(listing.Roots, listing.Roots.Count == 0 ? 0 : listing.Roots.Max(node => node.Size)));
        return BuildListingCard(BuildCardVisual(image, dpi), Path.GetFileName(path), FactLine(ArchivePreviewLoader.Describe(listing), 0), list);
    }

    /// <summary>
    /// One level of rows. Every bar in the tree is measured against the
    /// largest top-level row, so a folder's insides read as parts of it.
    /// </summary>
    private static IReadOnlyList<EntryNode> ArchiveRows(IReadOnlyList<ArchiveNode> nodes, long largest)
    {
        return nodes
            .Select(node => new EntryNode(
                node.Name,
                node.IsDirectory,
                FileKinds.FormatSize(node.Size),
                largest > 0 ? (double)node.Size / largest : 0,
                Children: node.IsDirectory && node.Children.Count > 0
                    ? () => ArchiveRows(node.Children, largest)
                    : null))
            .ToList();
    }
}
