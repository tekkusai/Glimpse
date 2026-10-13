using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// A file with no path on disk is shown from a copy. The files here sit
/// inside a zip folder, which Windows presents the way it presents a
/// phone: items that can be read as a stream and opened by nothing. A
/// phone is also slow, and sometimes stops answering. The tests for that
/// stand in for the file's owner with a stream that answers when told to.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class BorrowedFilesTests
{
    [Fact]
    public void AFileInsideAZipFolderIsCopiedOutUnderItsOwnName() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var modified = new DateTime(2026, 3, 9, 14, 30, 0, DateTimeKind.Local);
        var zip = WriteZip(temp, ("IMG_0412.jpg", Bytes(5000, seed: 7), modified), ("notes.txt", Bytes(40, seed: 1), modified));

        var path = Borrow(zip, "IMG_0412.jpg");

        Assert.NotNull(path);
        Assert.Equal("IMG_0412.jpg", Path.GetFileName(path));
        Assert.Equal(Bytes(5000, seed: 7), File.ReadAllBytes(path));
        Assert.True(BorrowedFiles.IsBorrowed(path));
        Assert.False(BorrowedFiles.IsBorrowed(zip));

        // The copy carries the file's own time, and cannot be saved into.
        Assert.Equal(modified, File.GetLastWriteTime(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    });

    [Fact]
    public void TheSameFileAskedForAgainIsTheSameCopy() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var zip = WriteZip(temp, ("IMG_0412.jpg", Bytes(5000, seed: 7), DateTime.Now));

        var first = Borrow(zip, "IMG_0412.jpg");
        var created = File.GetCreationTimeUtc(first!);
        var second = Borrow(zip, "IMG_0412.jpg");

        Assert.Equal(first, second);
        Assert.Equal(created, File.GetCreationTimeUtc(second!));
    });

    [Fact]
    public void AFileOverTheLimitAndAFolderAreNotCopied() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var zip = WriteZip(temp, ("clip.mp4", Bytes(9000, seed: 3), DateTime.Now), ("album/IMG_0001.jpg", Bytes(100, seed: 4), DateTime.Now));

        Assert.Null(Borrow(zip, "clip.mp4", maxBytes: 8000));
        Assert.NotNull(Borrow(zip, "clip.mp4", maxBytes: 9000));
        Assert.Null(Borrow(zip, "album"));
    });

    [Fact]
    public void OnlyTheNewestCopiesAreKept() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var names = Enumerable.Range(1, 7).Select(i => $"IMG_000{i}.jpg").ToArray();
        var zip = WriteZip(temp, [.. names.Select((name, i) => (name, Bytes(200, seed: i), DateTime.Now))]);

        var copies = new List<string>();
        foreach (var name in names)
        {
            copies.Add(Borrow(zip, name)!);
            Thread.Sleep(20);
        }

        Assert.All(copies.Take(3), copy => Assert.False(File.Exists(copy), copy));
        Assert.All(copies.Skip(3), copy => Assert.True(File.Exists(copy), copy));

        BorrowedFiles.Clear(DateTime.UtcNow.AddMinutes(1));
        Assert.All(copies, copy => Assert.False(File.Exists(copy), copy));
    });

    [Fact]
    public void AtStartOnlyTheCopiesOfTheLastRunAreCleared() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var zip = WriteZip(temp, ("old.jpg", Bytes(200, seed: 1), DateTime.Now), ("kept.jpg", Bytes(200, seed: 2), DateTime.Now), ("half.jpg", Bytes(200, seed: 4), DateTime.Now), ("new.jpg", Bytes(200, seed: 3), DateTime.Now));
        var old = Borrow(zip, "old.jpg")!;
        var kept = Borrow(zip, "kept.jpg")!;

        // The last run ended with this one half made: its folder is there
        // and its file is not.
        var half = Borrow(zip, "half.jpg")!;
        File.SetAttributes(half, FileAttributes.Normal);
        File.Delete(half);
        Thread.Sleep(50);

        // The app starts here. Space is answered from now on, and the
        // clearing of what the last run left has not been reached yet.
        var started = DateTime.UtcNow;
        Thread.Sleep(50);
        Assert.Equal(kept, Borrow(zip, "kept.jpg"));
        Assert.Equal(half, Borrow(zip, "half.jpg"));
        var fresh = Borrow(zip, "new.jpg")!;

        BorrowedFiles.Clear(started);

        // What was asked for since the start may be on screen already:
        // the copy made now, the one found from the last run, and the
        // one made now in a folder the last run left.
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(half));
        Assert.True(File.Exists(fresh));
    });

    [Fact]
    public void ACopyAskedForAgainIsTheNewestAgain() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var names = Enumerable.Range(1, 6).Select(i => $"IMG_000{i}.jpg").ToArray();
        var zip = WriteZip(temp, [.. names.Select((name, i) => (name, Bytes(200, seed: i), DateTime.Now))]);

        var copies = new List<string>();
        foreach (var name in names[..4])
        {
            copies.Add(Borrow(zip, name)!);
            Thread.Sleep(20);
        }

        // Back to the first, as a reader steps back, and then on to two more.
        Assert.Equal(copies[0], Borrow(zip, names[0]));
        Thread.Sleep(20);
        copies.Add(Borrow(zip, names[4])!);
        Thread.Sleep(20);
        copies.Add(Borrow(zip, names[5])!);

        // The one just looked at is still there. The two before it went.
        Assert.True(File.Exists(copies[0]));
        Assert.False(File.Exists(copies[1]));
        Assert.False(File.Exists(copies[2]));
    });

    [Fact]
    public async Task AskingForACopyDoesNotWaitForIt()
    {
        // The owner says what the file is and then serves nothing until
        // told to. The asking is done on the thread that reads Explorer's
        // selection for every preview, so it has to come back while the
        // owner is still silent.
        using var owner = new Silence();
        var asking = Task.Run(() => Owners.Ask("held.bin", new OwnerStream(4000, owner.Wait)));

        Assert.Same(asking, await Task.WhenAny(asking, Task.Delay(5000)));
        var target = await asking;
        Assert.NotNull(target);
        Assert.Null(await Owners.Ready(target, within: 300));
        Assert.False(File.Exists(target));

        owner.End();
        Assert.True(await Owners.Ready(target));
        Assert.Equal(4000, new FileInfo(target).Length);
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        await Owners.Settled();
    }

    [Fact]
    public async Task AnOwnerSlowToSayWhatTheFileIsIsNotWaitedFor()
    {
        // Not even the file's name and size are asked for on the shell's
        // thread. It waits a moment to hear them, and an owner that has
        // said nothing by then has no file to show, as far as it goes.
        using var silent = new Silence();
        var stream = new OwnerStream(4000);
        var asking = Task.Run(() => Owners.Ask("unsaid.bin", stream, patience: TimeSpan.FromMilliseconds(200), describing: silent.Wait));

        Assert.Same(asking, await Task.WhenAny(asking, Task.Delay(5000)));
        Assert.Null(await asking);
        Assert.Equal(1, BorrowedFiles.Underway);

        // Told late what the file is, the thread makes no copy that
        // nobody is waiting for.
        silent.End();
        await Owners.Settled();
        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    public async Task WhatIsNotAFileOrIsTooLargeIsNotCopied()
    {
        var stream = new OwnerStream(4000);

        Assert.Null(Owners.Ask("album", stream, file: false));
        Assert.Null(Owners.Ask("film.mp4", stream, size: BorrowedFiles.MaxBytes + 1));
        Assert.Null(Owners.Ask("gone.bin", stream, describing: () => throw new InvalidOperationException("the phone was unplugged")));

        await Owners.Settled();
        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    public async Task ACopyThatTakesTooLongIsGivenUpAndLeavesNothing()
    {
        // A second of file at a kilobyte every tenth of a second, and a
        // third of a second to take it in.
        var slow = new OwnerStream(10_000, () => Thread.Sleep(100));
        var target = Owners.Ask("slow.bin", slow, deadline: TimeSpan.FromMilliseconds(300))!;

        Assert.False(await Owners.Ready(target));
        await Owners.Settled();
        Assert.InRange(slow.Reads, 1, 8);
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
    }

    [Fact]
    public async Task AnOwnerThatNeverAnswersHoldsUpNothingButItsOwnFile()
    {
        using var never = new Silence();
        var stuck = Owners.Ask("stuck.bin", new OwnerStream(4000, never.Wait), deadline: TimeSpan.FromMilliseconds(300), identity: "stuck")!;

        // Whoever waits for it is told there is no file when its time is
        // up, and at once when they ask again.
        Assert.False(await Owners.Ready(stuck));
        Assert.Equal(stuck, Owners.Ask("stuck.bin", new OwnerStream(4000), identity: "stuck"));
        Assert.False(await Owners.Ready(stuck, within: 100));

        // Another file is copied as if nothing were wrong.
        var other = Owners.Ask("other.bin", new OwnerStream(4000))!;
        Assert.True(await Owners.Ready(other));

        never.End();
        await Owners.Settled();
    }

    [Fact]
    public async Task AFileAskedForNextStopsTheCopyBeforeIt()
    {
        // The reader has moved on to another file while the first, a long
        // one, was still arriving.
        var first = new OwnerStream(200_000, () => Thread.Sleep(20));
        var left = Owners.Ask("left.bin", first)!;
        var wanted = Owners.Ask("wanted.bin", new OwnerStream(4000))!;

        Assert.True(await Owners.Ready(wanted));
        Assert.False(await Owners.Ready(left));
        await Owners.Settled();
        Assert.InRange(first.Reads, 0, 100);
        Assert.False(Directory.Exists(Path.GetDirectoryName(left)));
    }

    [Fact]
    public async Task NobodyWaitingStopsTheCopy()
    {
        var stream = new OwnerStream(200_000, () => Thread.Sleep(20));
        var target = Owners.Ask("cancelled.bin", stream)!;

        BorrowedFiles.StopWaiting();

        Assert.False(await Owners.Ready(target));
        await Owners.Settled();
        Assert.InRange(stream.Reads, 0, 100);
    }

    [Fact]
    public async Task TheSameFileAskedForAgainJoinsTheCopyOnItsWay()
    {
        using var owner = new Silence();
        var second = new OwnerStream(9000);
        var target = Owners.Ask("joined.bin", new OwnerStream(4000, owner.Wait), identity: "joined")!;
        Assert.Equal(target, Owners.Ask("joined.bin", second, identity: "joined"));

        owner.End();
        Assert.True(await Owners.Ready(target));
        Assert.Equal(0, second.Reads);
        Assert.Equal(4000, new FileInfo(target).Length);
        await Owners.Settled();
    }

    [Fact]
    public async Task ACopyThatFailsDoesNotTakeTheFolderOfOneStillComing()
    {
        // Two files of one owner share a folder here. The first is asked
        // for and its owner is slow to open it. The second is asked for,
        // fails, and clears up after itself with the folder holding
        // nothing yet. Then the first is asked for again and arrives.
        using var opening = new Silence();
        var coming = Owners.Ask("coming.bin", new OwnerStream(4000), identity: "shared", opening: opening.Wait)!;
        var failed = Owners.Ask("failed.bin", new OwnerStream(4000, () => throw new IOException("unplugged")), identity: "shared")!;
        Assert.Equal(Path.GetDirectoryName(coming), Path.GetDirectoryName(failed));
        Assert.False(await Owners.Ready(failed));
        SpinWait.SpinUntil(() => BorrowedFiles.Underway == 1, TimeSpan.FromSeconds(5));

        Assert.Equal(coming, Owners.Ask("coming.bin", new OwnerStream(9000), identity: "shared"));
        opening.End();

        Assert.True(await Owners.Ready(coming));
        Assert.Equal(4000, new FileInfo(coming).Length);
        await Owners.Settled();
    }

    [Fact]
    public async Task NewerCopiesDoNotTakeTheFolderOfOneStillComing()
    {
        // The reader steps on from a slow file past four quick ones, and
        // back. Only the newest four folders are kept, and the slow
        // file's is the oldest by then, with nothing in it yet.
        using var opening = new Silence();
        var coming = Owners.Ask("coming.bin", new OwnerStream(4000), identity: "slow", opening: opening.Wait)!;
        for (var i = 0; i < 4; i++)
        {
            Thread.Sleep(20);
            Assert.True(await Owners.Ready(Owners.Ask($"quick{i}.bin", new OwnerStream(4000))!));
        }

        SpinWait.SpinUntil(() => BorrowedFiles.Underway == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(coming, Owners.Ask("coming.bin", new OwnerStream(9000), identity: "slow"));
        opening.End();

        Assert.True(await Owners.Ready(coming));
        await Owners.Settled();
    }

    [Fact]
    public async Task ClearingLeavesTheFolderOfACopyStillComing()
    {
        using var opening = new Silence();
        var coming = Owners.Ask("coming.bin", new OwnerStream(4000), opening: opening.Wait)!;

        BorrowedFiles.Clear(DateTime.UtcNow.AddMinutes(1));
        opening.End();

        Assert.True(await Owners.Ready(coming));
        await Owners.Settled();
    }

    [Fact]
    public async Task NoOwnerIsAskedWhileFourHaveNotComeBack()
    {
        // Four owners, each asked for a file and each silent in its first
        // read. A copy stops for a newer one only between reads, so these
        // four cannot.
        using var never = new Silence();
        for (var i = 0; i < 4; i++)
        {
            var stream = new OwnerStream(4000, never.Wait);
            Assert.NotNull(Owners.Ask($"stuck{i}.bin", stream));
            SpinWait.SpinUntil(() => stream.Reads == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(1, stream.Reads);
        }

        var described = 0;
        Assert.Null(Owners.Ask("fifth.bin", new OwnerStream(4000), describing: () => described++));
        Assert.Equal(0, described);

        never.End();
        await Owners.Settled();
        var fifth = Owners.Ask("fifth.bin", new OwnerStream(4000))!;
        Assert.True(await Owners.Ready(fifth));
        await Owners.Settled();
    }

    [Fact]
    public async Task WhatAnOwnerThrowsEndsItsCopyAndNothingElse()
    {
        var target = Owners.Ask("thrown.bin", new OwnerStream(4000, () => throw new InvalidOperationException("the phone was unplugged")))!;

        Assert.False(await Owners.Ready(target));
        await Owners.Settled();

        var next = Owners.Ask("next.bin", new OwnerStream(4000))!;
        Assert.True(await Owners.Ready(next));
        await Owners.Settled();
    }

    /// <summary>
    /// The shell's thread asks an item for nothing but its ID list. The
    /// selection lookups answer with where the copy will be and wait for
    /// nothing: the wait is where the file is shown.
    /// </summary>
    [Fact]
    public void NothingWaitsForACopyOnTheShellsThread()
    {
        var borrow = TestSource.Method(@"SpacePreview\Services\BorrowedFiles.cs", "TryBorrow");
        Assert.Contains("return Ask(new ShellOwner(item), maxBytes, Deadline, Patience);", borrow, StringComparison.Ordinal);
        Assert.DoesNotContain("item.Get", borrow, StringComparison.Ordinal);
        Assert.DoesNotContain("BindToHandler", borrow, StringComparison.Ordinal);

        // A copy is waited for by what shows it: the open that found it,
        // and the glance that is already up.
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        Assert.Equal(2, controller.Split("BorrowedFiles.WhenReady(").Length - 1);
        Assert.Contains("BorrowedFiles.WhenReady(selection.Path)", TestSource.Method(@"SpacePreview\Services\SpacePreviewController.cs", "ShowPreviewFromContext"), StringComparison.Ordinal);
        Assert.Contains("BorrowedFiles.WhenReady(selection.Path)", TestSource.Method(@"SpacePreview\Services\SpacePreviewController.cs", "ShowPreview"), StringComparison.Ordinal);
        Assert.DoesNotContain("WhenReady", TestSource.Method(@"SpacePreview\Services\SpacePreviewController.cs", "RunSelectionLookupAsync"), StringComparison.Ordinal);
        Assert.DoesNotContain("WhenReady", TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("WhenReady", TestSource.Read(@"SpacePreview\Native\ShellComInterop.cs"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ACopyAWindowShowsIsKeptForAsLongAsItShowsIt(bool hidden) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var names = Enumerable.Range(1, 8).Select(i => $"notes{i}.txt").ToArray();
        var zip = WriteZip(temp, [.. names.Select((name, i) => (name, Encoding.ASCII.GetBytes($"file {i}\r\n"), DateTime.Now))]);
        var shown = Borrow(zip, names[0])!;
        var dpi = (uint)Math.Round(PreviewWindow.ReadMonitor(IntPtr.Zero).Scale * 96);
        var window = new PreviewWindow { ParkedMonitor = StageMonitor.FromPixels(new Rect(-30000, 0, 1920, 1040), dpi) };
        try
        {
            window.ShowPreview(shown, IntPtr.Zero);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!Fields.Get<bool>(window, "_loadFinished"))
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the window.");
                await Task.Delay(10);
            }

            // Six more are borrowed while it is on screen, as they are
            // behind a window pinned for the afternoon. Its text is read
            // from the copy as the reader scrolls, and a picture is
            // decoded from it again when zoomed.
            foreach (var name in names[1..7])
            {
                Borrow(zip, name);
                Thread.Sleep(20);
            }

            Assert.True(File.Exists(shown));

            // Once the window lets go of it, hidden or closed, it is a
            // copy like any other.
            if (hidden)
            {
                window.HidePreview();
            }
            else
            {
                window.Close();
            }

            Borrow(zip, names[7]);
            Assert.False(File.Exists(shown));
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(@"..\..\evil.exe", ".._.._evil.exe")]
    [InlineData("a:b*c?.jpg", "a_b_c_.jpg")]
    [InlineData("", "file")]
    [InlineData("trailing. ", "trailing")]
    public void ANameFromOutsideCannotLeaveItsFolder(string given, string expected) =>
        Assert.Equal(expected, BorrowedFiles.SafeName(given));

    private static string? Borrow(string zip, string entry, long maxBytes = BorrowedFiles.MaxBytes)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.Combine(zip, entry), IntPtr.Zero, ref iid, out var itemPtr));
        var item = (IShellItem)Marshal.GetUniqueObjectForIUnknown(itemPtr);
        Marshal.Release(itemPtr);
        try
        {
            // Asked for on this thread and made on another, as in the app.
            var path = BorrowedFiles.TryBorrow(item, maxBytes);
            return path is not null && BorrowedFiles.WhenReady(path).GetAwaiter().GetResult() ? path : null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private static string WriteZip(TempDirectory temp, params (string Name, byte[] Bytes, DateTime Modified)[] entries)
    {
        var path = Path.Combine(temp.Path, $"card-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes, modified) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            entry.LastWriteTime = modified;
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        return path;
    }

    private static byte[] Bytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IntPtr item);
}

/// <summary>Asks for the file of a stand-in owner, and waits as the app waits.</summary>
internal static class Owners
{
    public static string? Ask(
        string name,
        OwnerStream stream,
        TimeSpan? deadline = null,
        TimeSpan? patience = null,
        Action? describing = null,
        Action? opening = null,
        string? identity = null,
        long? size = null,
        bool file = true) =>
        BorrowedFiles.Ask(
            new Owner(name, identity ?? Guid.NewGuid().ToString("N"), stream, describing, opening, size, file),
            BorrowedFiles.MaxBytes,
            deadline ?? TimeSpan.FromSeconds(10),
            patience ?? TimeSpan.FromSeconds(5));

    /// <summary>Whether the copy arrived, or null while that is still not known.</summary>
    public static async Task<bool?> Ready(string path, int within = 5000)
    {
        var ready = BorrowedFiles.WhenReady(path);
        return await Task.WhenAny(ready, Task.Delay(within)) == ready ? await ready : null;
    }

    /// <summary>Every copy's thread has come back, what it left cleared up, so the next test starts with none.</summary>
    public static async Task Settled()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (BorrowedFiles.Underway > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "A copy never came back.");
            await Task.Delay(10);
        }
    }

    private sealed class Owner(string name, string identity, OwnerStream stream, Action? describing, Action? opening, long? size, bool file) : IFileOwner
    {
        public BorrowedFacts? Describe()
        {
            describing?.Invoke();
            return file ? new BorrowedFacts(name, identity, size, null) : null;
        }

        public IStream Open()
        {
            opening?.Invoke();
            return stream;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>An owner saying nothing. It ends when the test does, however that ends, so no copy is left waiting on it.</summary>
internal sealed class Silence : IDisposable
{
    private readonly ManualResetEventSlim _over = new();

    public void Wait() => _over.Wait();

    public void End() => _over.Set();

    public void Dispose() => _over.Set();
}

/// <summary>
/// A file's stream as its owner serves it: so many bytes, a kilobyte a
/// read, and before each read whatever the owner does first. That may
/// be to wait, to wait forever, or to fail.
/// </summary>
internal sealed class OwnerStream(int bytes, Action? first = null) : IStream
{
    private int _left = bytes;
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        Interlocked.Increment(ref _reads);
        first?.Invoke();
        var count = Math.Min(Math.Min(1000, cb), _left);
        Array.Fill(pv, (byte)'a', 0, count);
        _left -= count;
        Marshal.WriteInt32(pcbRead, count);
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition) => throw new NotSupportedException();

    public void SetSize(long libNewSize) => throw new NotSupportedException();

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();

    public void Commit(int grfCommitFlags) => throw new NotSupportedException();

    public void Revert() => throw new NotSupportedException();

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

    public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag) => throw new NotSupportedException();

    public void Clone(out IStream ppstm) => throw new NotSupportedException();
}
