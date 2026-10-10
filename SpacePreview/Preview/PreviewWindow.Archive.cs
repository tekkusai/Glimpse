using System.Windows;
using System.Windows.Controls;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    private const int ArchiveRootCap = 500;
    private const int ArchiveChildrenCap = 200;

    /// <summary>
    /// An archive is its listing: a summary line over the same rows a folder
    /// breakdown uses. Folders expand on demand, so a huge archive costs
    /// nothing until it is explored.
    /// </summary>
    private static FrameworkElement BuildArchivePreview(ArchiveListing listing)
    {
        var palette = Theme.Current;
        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        stack.Children.Add(new TextBlock
        {
            Text = ArchivePreviewLoader.Describe(listing),
            Margin = new Thickness(8, 0, 0, 12),
            FontSize = Metrics.FontBody,
            Foreground = Theme.Brush(palette.TextSecondary),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var list = new EntryList(ArchiveRootCap, ArchiveChildrenCap);
        list.Show(listing.Roots.Select(ArchiveEntry).ToList());
        stack.Children.Add(list.Element);
        return BuildScrollSurface(stack, new Thickness(0), horizontal: false, palette.CanvasBg);
    }

    private static EntryNode ArchiveEntry(ArchiveNode node) => new(
        node.Name,
        node.IsDirectory,
        node.IsDirectory
            ? $"{FolderScanner.Count(node.FileCount, "file", "files")} · {FileKinds.FormatSize(node.Size)}"
            : FileKinds.FormatSize(node.Size),
        Children: node.IsDirectory && node.Children.Count > 0
            ? () => node.Children.Select(ArchiveEntry).ToList()
            : null);
}
