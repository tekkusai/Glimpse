using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The folder card's facts come from FolderScanner. These run it on real
/// temp trees: what the rows say before sizes exist, what a budget stop
/// leaves, and what a folder the user cannot read says.
/// </summary>
public sealed class CardsFolderScanTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(30);

    [Fact]
    public void RowsArriveByNameBeforeAnySubfolderIsSized()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.CreateDirectory("beta"), "one.txt"), "12345");
        File.WriteAllText(Path.Combine(temp.CreateDirectory("Alpha"), "two.txt"), "12");
        temp.CreateFile("z.txt", "abc");
        temp.CreateFile("a.txt", "a");
        var snapshots = new List<FolderScan>();

        var scan = FolderScanner.Scan(temp.Path, 1000, Plenty, snapshots.Add, CancellationToken.None);

        var first = snapshots[0];
        Assert.Equal(["Alpha", "beta", "a.txt", "z.txt"], first.Children.Select(child => child.Name));
        Assert.Null(first.Children[0].Bytes);
        Assert.Equal(string.Empty, FolderScanner.DescribeChild(first.Children[0]));
        Assert.Equal(1, first.Children[2].Bytes);

        Assert.Equal(2, scan.Children[0].Bytes);
        Assert.Equal(5, scan.Children[1].Bytes);
        Assert.False(scan.IsLowerBound);
        Assert.Equal("11 B · 2 folders · 4 files", FolderScanner.Describe(scan));
    }

    [Fact]
    public void ABudgetStopLeavesUnscannedRowsBlankAndMarksTheCutChild()
    {
        using var temp = new TempDirectory();
        foreach (var name in new[] { "a", "b", "c" })
        {
            var directory = temp.CreateDirectory(name);
            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"{i}.txt"), "xx");
            }
        }

        // Three entries list the top level; three more reach into "a".
        var scan = FolderScanner.Scan(temp.Path, 6, Plenty, null, CancellationToken.None);

        Assert.True(scan.Stopped);
        Assert.True(scan.Children[0].Partial);
        Assert.Equal("At least 6 B", FolderScanner.DescribeChild(scan.Children[0]));
        Assert.Null(scan.Children[1].Bytes);
        Assert.Equal(string.Empty, FolderScanner.DescribeChild(scan.Children[2]));
        Assert.StartsWith("At least ", FolderScanner.Describe(scan), StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderTheUserCannotReadSaysSoInsteadOfEmpty()
    {
        using var temp = new TempDirectory();
        var locked = temp.CreateDirectory("locked");
        File.WriteAllText(Path.Combine(locked, "secret.txt"), "hidden");

        using (Deny(locked))
        {
            var scan = FolderScanner.Scan(locked, 1000, Plenty, null, CancellationToken.None);

            Assert.True(scan.RootUnreadable);
            Assert.Equal("Can't read this folder", FolderScanner.Describe(scan));
        }
    }

    [Fact]
    public void AnUnreadableSubfolderMakesTheTotalAFloor()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.CreateDirectory("open"), "a.txt"), "1234");
        var locked = temp.CreateDirectory("locked");
        File.WriteAllText(Path.Combine(locked, "b.txt"), "12345678");

        using (Deny(locked))
        {
            var scan = FolderScanner.Scan(temp.Path, 1000, Plenty, null, CancellationToken.None);

            Assert.Equal(1, scan.SkippedFolders);
            Assert.True(scan.Children.Single(child => child.Name == "locked").Partial);
            Assert.Equal("At least 4 B · 2 folders · 1 file", FolderScanner.Describe(scan));
        }
    }

    [Fact]
    public void AJunctionIsListedButNeverEntered()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.txt", "abc");
        var junction = Path.Combine(temp.Path, "loop");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{temp.Path}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!)
        {
            mklink.WaitForExit();
        }

        Assert.True(Directory.Exists(junction), "mklink /J did not create the junction");

        // A junction back to the root would walk forever if it were followed.
        var scan = FolderScanner.Scan(temp.Path, 1000, Plenty, null, CancellationToken.None);

        var loop = scan.Children.Single(child => child.Name == "loop");
        Assert.True(loop.IsLink);
        Assert.Equal("Link", FolderScanner.DescribeChild(loop));
        Assert.Equal(1, scan.Files);
        Assert.False(scan.Stopped);
        Directory.Delete(junction);
    }

    [Fact]
    public void ZeroCountsAndEmptyFoldersReadNaturally()
    {
        using var temp = new TempDirectory();
        var empty = temp.CreateDirectory("empty");
        var single = temp.CreateDirectory("single");
        File.WriteAllText(Path.Combine(single, "only.txt"), "12");

        Assert.Equal("Empty folder", FolderScanner.Describe(FolderScanner.Scan(empty, 1000, Plenty, null, CancellationToken.None)));
        Assert.Equal("2 B · 1 file", FolderScanner.Describe(FolderScanner.Scan(single, 1000, Plenty, null, CancellationToken.None)));
    }

    [Fact]
    public void ACancelledScanStops()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.txt");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => FolderScanner.Scan(temp.Path, 1000, Plenty, null, cancelled.Token));
    }

    /// <summary>Denies the current user listing and reading until disposed.</summary>
    private static IDisposable Deny(string directory)
    {
        var info = new DirectoryInfo(directory);
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new Restore(() =>
        {
            var current = info.GetAccessControl();
            current.RemoveAccessRule(rule);
            info.SetAccessControl(current);
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
