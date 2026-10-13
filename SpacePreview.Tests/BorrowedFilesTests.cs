using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// A file with no path on disk is shown from a copy. The files here sit
/// inside a zip folder, which Windows presents the way it presents a
/// phone: items that can be read as a stream and opened by nothing.
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
            return BorrowedFiles.TryBorrow(item, maxBytes);
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
