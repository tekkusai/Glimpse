using System.Runtime.InteropServices;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// A picker row carries the shell's display name, not the file name. These
/// rows are built from what the real shell shows for real files in a
/// temporary folder. Windows never shows the extension of a shortcut
/// (.lnk, .url), whatever the user chose for other types, so the rows read
/// the same on every PC and no test touches or depends on that setting.
/// </summary>
public sealed class CommonFileDialogDisplayNameTests
{
    [Fact]
    public void ARowNamedWithoutItsExtensionResolvesToItsFile()
    {
        using var folder = new TempDirectory();
        var shortcut = folder.CreateFile("Budget.lnk");
        folder.CreateFile("Budgets.lnk");
        folder.CreateFile("Budget 2.lnk");
        folder.CreateFile("Budget.final.lnk");

        Assert.Equal([shortcut], Resolve(folder, Untyped(shortcut)), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("archive.tar.lnk")]
    [InlineData("v1.2 notes.lnk")]
    [InlineData(".config.lnk")]
    [InlineData("Ünïcode café.lnk")]
    public void ANameWithSeveralDotsResolvesToItsFile(string fileName)
    {
        using var folder = new TempDirectory();
        var file = folder.CreateFile(fileName);
        folder.CreateFile("archive.lnk");
        folder.CreateFile("archive.tar.gz.lnk");
        folder.CreateFile("v1.lnk");
        folder.CreateFile("v1.2 notes.old.lnk");

        Assert.Equal([file], Resolve(folder, Untyped(file)), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoFilesWithOneDisplayNameResolveByTheTypeColumn()
    {
        using var folder = new TempDirectory();
        var shortcut = folder.CreateFile("Invoice.lnk");
        var webLink = folder.CreateFile("Invoice.url");

        Assert.Equal([shortcut], Resolve(folder, Row(shortcut)), StringComparer.OrdinalIgnoreCase);
        Assert.Equal([webLink], Resolve(folder, Row(webLink)), StringComparer.OrdinalIgnoreCase);
    }

    // The file really named "Invoice" is not the row for Invoice.lnk.
    [Fact]
    public void ARowIsNotTakenForAnotherFileThatBearsItsName()
    {
        using var folder = new TempDirectory();
        var plain = folder.CreateFile("Invoice");
        var shortcut = folder.CreateFile("Invoice.lnk");

        Assert.Equal([shortcut], Resolve(folder, Row(shortcut)), StringComparer.OrdinalIgnoreCase);
        Assert.Equal([plain], Resolve(folder, Row(plain)), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFolderAndAFileWithOneDisplayNameResolveByTheTypeColumn()
    {
        using var folder = new TempDirectory();
        var directory = folder.CreateDirectory("Plan");
        var shortcut = folder.CreateFile("Plan.lnk");

        Assert.Equal([directory], Resolve(folder, Row(directory)), StringComparer.OrdinalIgnoreCase);
        Assert.Equal([shortcut], Resolve(folder, Row(shortcut)), StringComparer.OrdinalIgnoreCase);
    }

    // List and icon views show no type. A wrong file is worse than no preview.
    [Fact]
    public void TwoEntriesWithOneDisplayNameResolveNothingWithoutAType()
    {
        using var folder = new TempDirectory();
        var plain = folder.CreateFile("Invoice");
        var shortcut = folder.CreateFile("Invoice.lnk");
        folder.CreateDirectory("Plan");
        var planShortcut = folder.CreateFile("Plan.lnk");

        Assert.Empty(Resolve(folder, Untyped(plain)));
        Assert.Empty(Resolve(folder, Untyped(shortcut)));
        Assert.Empty(Resolve(folder, Untyped(planShortcut)));
    }

    // A Type column that names neither bearer of the name settles nothing.
    [Fact]
    public void ATypeThatFitsNeitherEntryResolvesNothing()
    {
        using var folder = new TempDirectory();
        var plain = folder.CreateFile("Invoice");
        folder.CreateFile("Invoice.lnk");
        var row = Row(plain);

        Assert.Empty(Resolve(folder, Row(row.Name, "No such type")));
    }

    // The entries are counted before the cloud check, or the row for a
    // cloud-only file would open the local file that shares its name.
    [Fact]
    public void ACloudOnlyEntryStillCountsAsABearerOfTheName()
    {
        using var folder = new TempDirectory();
        var plain = folder.CreateFile("Invoice");
        var shortcut = folder.CreateFile("Invoice.lnk");
        var typed = Row(shortcut);
        var untyped = Untyped(plain);
        File.SetAttributes(shortcut, FileAttributes.Offline);

        Assert.Empty(Resolve(folder, untyped));
        Assert.Empty(Resolve(folder, typed));
    }

    // The Type column is a second read across processes. A name that one
    // entry bears is settled without it.
    [Fact]
    public void ANameOneEntryBearsIsSettledWithoutReadingTheType()
    {
        using var folder = new TempDirectory();
        var shortcut = folder.CreateFile("Budget.lnk");
        var reads = 0;
        var row = new CommonFileDialogSelectionService.SelectionCandidate(
            ShellNames.Shown(shortcut).Name,
            () =>
            {
                reads++;
                return null;
            });

        Assert.Equal([shortcut], Resolve(folder, row), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void SelectedRowsResolveInRowOrder()
    {
        using var folder = new TempDirectory();
        var second = folder.CreateFile("Second.lnk");
        var first = folder.CreateFile("First.url");
        var third = folder.CreateDirectory("Third");

        var resolved = CommonFileDialogSelectionService.ResolveExistingEntries(
            folder.Path,
            [Untyped(second), Untyped(first), Row("Not here", null), Untyped(third)]);

        Assert.Equal([second, first, third], resolved, StringComparer.OrdinalIgnoreCase);
    }

    // The breadcrumb is read up to 1,024 characters, so a folder past MAX_PATH is served.
    [Fact]
    public void ARowInAFolderPastMaxPathResolves()
    {
        using var folder = new TempDirectory();
        var deep = Path.Combine(folder.Path, new string('a', 100), new string('b', 100), new string('c', 100));
        Directory.CreateDirectory(deep);
        var shortcut = Path.Combine(deep, "Deep.lnk");
        File.WriteAllText(shortcut, "x");
        Assert.True(shortcut.Length > 300);

        var resolved = CommonFileDialogSelectionService.ResolveExistingEntries(
            deep,
            [Untyped(folder.CreateFile("Deep.lnk"))]);

        Assert.Equal([shortcut], resolved, StringComparer.OrdinalIgnoreCase);
    }

    // Every bearer of a name has to be seen to choose one, and a slow share
    // must not be read without end: past the limit the row is left alone.
    [Theory]
    [InlineData(CommonFileDialogSelectionService.SearchLimit - 1, true)]
    [InlineData(CommonFileDialogSelectionService.SearchLimit, false)]
    public void TheSearchForOneRowReadsAtMostTheLimit(int others, bool resolves)
    {
        using var folder = new TempDirectory();
        var shortcut = folder.CreateFile("Journal.lnk");
        for (var i = 0; i < others; i++)
        {
            File.WriteAllBytes(Path.Combine(folder.Path, $"Journal.{i:D4}.bak"), []);
        }

        string[] expected = resolves ? [shortcut] : [];

        Assert.Equal(expected, Resolve(folder, Untyped(shortcut)), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\Outside")]
    [InlineData("Out*")]
    [InlineData("Outsid?")]
    [InlineData("\"Inside\"")]
    public void ANameThatIsNotAFileNameResolvesNothing(string name)
    {
        using var folder = new TempDirectory();
        var inner = folder.CreateDirectory("Inner");
        folder.CreateFile("Outside");
        File.WriteAllText(Path.Combine(inner, "Inside"), "x");

        Assert.Empty(CommonFileDialogSelectionService.ResolveExistingEntries(inner, [Row(name, null)]));
    }

    // A row is a name in the folder the dialog shows. It is never a path of its own.
    [Fact]
    public void ARowThatReadsAsAPathResolvesNothing()
    {
        using var folder = new TempDirectory();
        var elsewhere = folder.CreateFile("elsewhere.unknownext9");
        var shown = folder.CreateDirectory("Shown");

        Assert.Empty(CommonFileDialogSelectionService.ResolveExistingEntries(shown, [Row(elsewhere, null)]));
    }

    private static IReadOnlyList<string> Resolve(
        TempDirectory folder,
        CommonFileDialogSelectionService.SelectionCandidate row) =>
        CommonFileDialogSelectionService.ResolveExistingEntries(folder.Path, [row]);

    /// <summary>The row a details view shows for this entry: its display name and its Type column.</summary>
    private static CommonFileDialogSelectionService.SelectionCandidate Row(string path)
    {
        var (name, type) = ShellNames.Shown(path);
        return Row(name, type);
    }

    /// <summary>The row a list or icon view shows for this entry: its display name alone.</summary>
    private static CommonFileDialogSelectionService.SelectionCandidate Untyped(string path) =>
        Row(ShellNames.Shown(path).Name, null);

    private static CommonFileDialogSelectionService.SelectionCandidate Row(string name, string? type) =>
        new(name, () => type);
}

/// <summary>What the shell itself shows for an entry on disk, read off the real item.</summary>
internal static class ShellNames
{
    private const uint DisplayName = 0x200;
    private const uint TypeName = 0x400;

    public static (string Name, string Type) Shown(string path)
    {
        var info = default(FileInfo);
        Assert.NotEqual(
            IntPtr.Zero,
            SHGetFileInfoW(path, 0, ref info, Marshal.SizeOf<FileInfo>(), DisplayName | TypeName));
        return (info.DisplayName, info.TypeName);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(
        string path,
        uint attributes,
        ref FileInfo info,
        int size,
        uint flags);
}
