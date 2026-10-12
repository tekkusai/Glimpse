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

    // Rows by name are for a walk still running. With no subfolder to walk
    // the listing is the result, and the card builds its rows once.
    [Fact]
    public void AFolderWithNothingToWalkSendsNoRowsAheadOfItsResult()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("b.txt", "12");
        temp.CreateFile("a.txt", "1");
        var snapshots = new List<FolderScan>();

        var scan = FolderScanner.Scan(temp.Path, 1000, Plenty, snapshots.Add, CancellationToken.None);

        Assert.Empty(snapshots);
        Assert.Equal("3 B · 2 files", FolderScanner.Describe(scan));
    }

    // The rows it remembers are bounded; what it counts is not. Entries
    // past the rows, a subfolder among them, still reach the totals.
    [Fact]
    public void EntriesPastTheRememberedRowsAreStillCountedAndSized()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.CreateDirectory("a"), "one.txt"), "12345");
        temp.CreateFile("b.txt", "12");
        File.WriteAllText(Path.Combine(temp.CreateDirectory("c"), "two.txt"), "1234567");
        temp.CreateFile("d.txt", "123");
        var snapshots = new List<FolderScan>();

        var scan = FolderScanner.Scan(temp.Path, 2, Plenty, snapshots.Add, CancellationToken.None);

        // The rows go out as soon as they are full, before the rest is read.
        Assert.Equal(["a", "b.txt"], snapshots[0].Children.Select(child => child.Name));
        Assert.Equal(["a", "b.txt"], scan.Children.Select(child => child.Name));
        Assert.False(scan.ListedEveryChild);
        Assert.Equal(5, scan.Children[0].Bytes);
        Assert.False(scan.IsLowerBound);
        Assert.Equal("17 B · 2 folders · 4 files", FolderScanner.Describe(scan));
    }

    // A wide tree and a deep one: every level's listing is read through
    // while the level below it is walked.
    [Fact]
    public void AWalkThatDescendsMidListingMissesNothing()
    {
        using var temp = new TempDirectory();
        var wide = temp.CreateDirectory("wide");
        for (var i = 0; i < 40; i++)
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(wide, $"d{i:00}")).FullName, "f.txt"), "12");
            File.WriteAllText(Path.Combine(wide, $"f{i:00}.txt"), "1");
        }

        var deep = temp.CreateDirectory("deep");
        for (var i = 0; i < 30; i++)
        {
            deep = Directory.CreateDirectory(Path.Combine(deep, "n")).FullName;
            File.WriteAllText(Path.Combine(deep, "f.txt"), "123");
        }

        var scan = FolderScanner.Scan(temp.Path, 1000, Plenty, null, CancellationToken.None);

        Assert.Equal(90, scan.Children.Single(child => child.Name == "deep").Bytes);
        Assert.Equal(120, scan.Children.Single(child => child.Name == "wide").Bytes);
        Assert.Equal("210 B · 72 folders · 110 files", FolderScanner.Describe(scan));
    }

    [Fact]
    public void AScanOutOfTimeBeforeItListsAnythingIsAFloor()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.txt", "abc");

        var scan = FolderScanner.Scan(temp.Path, 1000, TimeSpan.Zero, null, CancellationToken.None);

        Assert.True(scan.Stopped);
        Assert.False(scan.ListedEveryChild);
        Assert.StartsWith("At least ", FolderScanner.Describe(scan), StringComparison.Ordinal);
    }

    [Fact]
    public void AStopLeavesUnscannedRowsBlankAndMarksTheCutChild()
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
        var entries = 0;
        var scan = FolderScanner.Scan(temp.Path, 1000, () => ++entries > 6, null, CancellationToken.None);

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
