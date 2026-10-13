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

        BorrowedFiles.Clear();
        Assert.All(copies, copy => Assert.False(File.Exists(copy), copy));
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
        // The owner serves nothing until told to. The asking is done on
        // the thread that reads Explorer's selection for every preview,
        // so it has to come back while the owner is still silent.
        using var owner = new Silence();
        var target = Target("held.bin");
        var asking = Task.Run(() => Start(target, new OwnerStream(4000, owner.Wait)));

        Assert.Same(asking, await Task.WhenAny(asking, Task.Delay(5000)));
        Assert.Equal(target, await asking);
        Assert.Null(await Ready(target, within: 300));
        Assert.False(File.Exists(target));

        owner.End();
        Assert.True(await Ready(target));
        Assert.Equal(4000, new FileInfo(target).Length);
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        await Settled();
    }

    [Fact]
    public async Task ACopyThatTakesTooLongIsGivenUpAndLeavesNothing()
    {
        // A second of file at a kilobyte every tenth of a second, and a
        // third of a second to take it in.
        var slow = new OwnerStream(10_000, () => Thread.Sleep(100));
        var target = Target("slow.bin");
        Start(target, slow, deadline: TimeSpan.FromMilliseconds(300));

        Assert.False(await Ready(target));
        await Settled();
        Assert.InRange(slow.Reads, 1, 8);
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
    }

    [Fact]
    public async Task AnOwnerThatNeverAnswersHoldsUpNothingButItsOwnFile()
    {
        using var never = new Silence();
        var stuck = Target("stuck.bin");
        Start(stuck, new OwnerStream(4000, never.Wait), deadline: TimeSpan.FromMilliseconds(300));

        // Whoever waits for it is told there is no file when its time is
        // up, and at once when they ask again.
        Assert.False(await Ready(stuck));
        Assert.Equal(stuck, Start(stuck, new OwnerStream(4000)));
        Assert.False(await Ready(stuck, within: 100));

        // Another file is copied as if nothing were wrong.
        var other = Target("other.bin");
        Start(other, new OwnerStream(4000));
        Assert.True(await Ready(other));

        never.End();
        await Settled();
    }

    [Fact]
    public async Task AFileAskedForNextStopsTheCopyBeforeIt()
    {
        // The reader has moved on to another file while the first, a long
        // one, was still arriving.
        var first = new OwnerStream(200_000, () => Thread.Sleep(20));
        var left = Target("left.bin");
        Start(left, first);
        var wanted = Target("wanted.bin");
        Start(wanted, new OwnerStream(4000));

        Assert.True(await Ready(wanted));
        Assert.False(await Ready(left));
        await Settled();
        Assert.InRange(first.Reads, 0, 100);
        Assert.False(Directory.Exists(Path.GetDirectoryName(left)));
    }

    [Fact]
    public async Task NobodyWaitingStopsTheCopy()
    {
        var stream = new OwnerStream(200_000, () => Thread.Sleep(20));
        var target = Target("cancelled.bin");
        Start(target, stream);

        BorrowedFiles.StopWaiting();

        Assert.False(await Ready(target));
        await Settled();
        Assert.InRange(stream.Reads, 0, 100);
    }

    [Fact]
    public async Task TheSameFileAskedForAgainJoinsTheCopyOnItsWay()
    {
        using var owner = new Silence();
        var target = Target("joined.bin");
        var opened = 0;
        Start(target, new OwnerStream(4000, owner.Wait), opening: () => opened++);
        Assert.Equal(target, Start(target, new OwnerStream(9000), opening: () => opened++));

        owner.End();
        Assert.True(await Ready(target));
        Assert.Equal(1, opened);
        Assert.Equal(4000, new FileInfo(target).Length);
        await Settled();
    }

    [Fact]
    public async Task NoCopyStartsWhileFourHaveNotComeBack()
    {
        // Four owners, each asked for a file and each silent in its first
        // read. A copy stops for a newer one only between reads, so these
        // four cannot.
        using var never = new Silence();
        for (var i = 0; i < 4; i++)
        {
            var stream = new OwnerStream(4000, never.Wait);
            Assert.NotNull(Start(Target($"stuck{i}.bin"), stream));
            SpinWait.SpinUntil(() => stream.Reads == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(1, stream.Reads);
        }

        var fifth = Target("fifth.bin");
        Assert.Null(Start(fifth, new OwnerStream(4000)));

        never.End();
        await Settled();
        Assert.Equal(fifth, Start(fifth, new OwnerStream(4000)));
        Assert.True(await Ready(fifth));
        await Settled();
    }

    [Fact]
    public async Task WhatAnOwnerThrowsEndsItsCopyAndNothingElse()
    {
        var target = Target("thrown.bin");
        Start(target, new OwnerStream(4000, () => throw new InvalidOperationException("the phone was unplugged")));

        Assert.False(await Ready(target));
        await Settled();

        var next = Target("next.bin");
        Start(next, new OwnerStream(4000));
        Assert.True(await Ready(next));
        await Settled();
    }

    [Fact]
    public async Task ASelectionIsHandedOnWhenItsCopyHasArrivedAndNotWhenItNeverDoes()
    {
        using var owner = new Silence();
        var target = Target("selected.bin");
        Start(target, new OwnerStream(4000, owner.Wait));
        var selection = new ExplorerSelection(target, IntPtr.Zero);

        var arriving = SpacePreviewController.Arrived(selection);
        Assert.NotSame(arriving, await Task.WhenAny(arriving, Task.Delay(300)));
        owner.End();
        Assert.Same(selection, await arriving);
        await Settled();

        // A copy that does not come is no selection: Space does nothing,
        // as it did before files with no path could be shown at all.
        var lost = Target("lost.bin");
        Start(lost, new OwnerStream(4000, () => throw new IOException("gone")));
        Assert.Null(await SpacePreviewController.Arrived(new ExplorerSelection(lost, IntPtr.Zero)));
        await Settled();

        // A file of the user's own is not waited for, nor asked about.
        var own = new ExplorerSelection(@"Z:\not there\report.pdf", IntPtr.Zero);
        Assert.Same(own, await SpacePreviewController.Arrived(own));
        Assert.Null(await SpacePreviewController.Arrived(null));
    }

    /// <summary>
    /// Both ways a selection reaches a preview, the Space lookup and the
    /// arrow keys' step, wait for the copy after the shell's thread has
    /// answered. Waiting on that thread is what held every preview up.
    /// </summary>
    [Fact]
    public void EveryLookupWaitsForItsCopyOffTheShellsThread()
    {
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");

        Assert.Contains("return await Arrived(selection);", TestSource.Method(@"SpacePreview\Services\SpacePreviewController.cs", "RunSelectionLookupAsync"), StringComparison.Ordinal);
        Assert.Contains("await Arrived(await StaTask.Run(() => ExplorerSelectionService.TryBrowseRelative(hwnd, isDesktop, delta)))", controller, StringComparison.Ordinal);
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

    private static string Target(string name) =>
        Path.Combine(BorrowedFiles.Root, Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(), name);

    private static string? Start(string target, OwnerStream stream, TimeSpan? deadline = null, Action? opening = null) =>
        BorrowedFiles.Start(target, size: null, modified: null, BorrowedFiles.MaxBytes, deadline ?? TimeSpan.FromSeconds(10), () =>
        {
            opening?.Invoke();
            return () => stream;
        });

    /// <summary>Whether the copy arrived, or null while that is still not known.</summary>
    private static async Task<bool?> Ready(string path, int within = 5000)
    {
        var ready = BorrowedFiles.WhenReady(path);
        return await Task.WhenAny(ready, Task.Delay(within)) == ready ? await ready : null;
    }

    /// <summary>Every copy has come back, so the next test starts with none on its way.</summary>
    private static async Task Settled()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (BorrowedFiles.Underway > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "A copy never came back.");
            await Task.Delay(10);
        }
    }

    /// <summary>An owner saying nothing. It ends when the test does, however that ends, so no copy is left waiting on it.</summary>
    private sealed class Silence : IDisposable
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
    private sealed class OwnerStream(int bytes, Action? first = null) : IStream
    {
        private int _left = bytes;
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            Interlocked.Increment(ref _reads);
            first?.Invoke();
            var count = Math.Min(Math.Min(1000, cb), _left);
            Array.Fill(pv, (byte)7, 0, count);
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
