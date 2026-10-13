using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Space on a file with no path on disk previews a copy of it, and the
/// copy is treated as what it is: something to look at, with nothing saved
/// into it and nowhere to "show" it. The selection here is a real one of
/// shell items, served by a stand-in for Explorer's view.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class BorrowedSelectionTests
{
    private static readonly Rect ParkedPixels = new(-30000, 0, 1920, 1040);

    [Fact]
    public void ASelectedFileInAZipFolderIsReadAsItsCopy() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var zip = WriteZip(temp, ("IMG_0412.jpg", Jpeg()), ("IMG_0413.jpg", Jpeg()));
        var onDisk = temp.CreateFile("notes.txt", "on disk");
        var view = new SelectedItems(Path.Combine(zip, "IMG_0412.jpg"), onDisk, Path.Combine(zip, "IMG_0413.jpg"));

        var selection = ShellViews.ReadSelection(view, ordered: true, out var hr, item => BorrowedFiles.TryBorrow(item));

        Assert.Equal(0, hr);
        Assert.Equal(3, selection.Count);
        Assert.True(BorrowedFiles.IsBorrowed(selection.AnchorPath));
        Assert.Equal("IMG_0412.jpg", Path.GetFileName(selection.AnchorPath));

        // Only the anchor is borrowed. Copying every member of a selection
        // off a phone would be minutes of work for one press of Space.
        Assert.Equal([onDisk], selection.OrderedPaths);

        // Asked without a way to borrow, as a file dialog asks, the same
        // selection has no anchor that is a file.
        Assert.Null(ShellViews.ReadSelection(view, ordered: false, out _).AnchorPath);
    });

    [Fact]
    public void ABorrowedPdfIsNotSignedAndABorrowedPhotoStillTurns() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var pdf = Path.Combine(BorrowedFiles.Root, "0123456789ABCDEF", "form.pdf");
        var photo = Path.Combine(BorrowedFiles.Root, "0123456789ABCDEF", "IMG_0412.jpg");

        Assert.Equal(HeaderTool.None, PreviewWindow.ToolFor(pdf, isDirectory: false, fileDialogMode: false));
        Assert.Equal(HeaderTool.Sign, PreviewWindow.ToolFor(Path.Combine(temp.Path, "form.pdf"), isDirectory: false, fileDialogMode: false));
        Assert.Equal(HeaderTool.Rotate, PreviewWindow.ToolFor(photo, isDirectory: false, fileDialogMode: false));
    });

    [Fact]
    public Task ABorrowedPhotoSaysItsTurnIsNotSavedAndOffersNoWayToItsFolder() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var zip = WriteZip(temp, ("IMG_0412.jpg", Jpeg()));
        var view = new SelectedItems(Path.Combine(zip, "IMG_0412.jpg"));
        var path = ShellViews.ReadSelection(view, ordered: false, out _, item => BorrowedFiles.TryBorrow(item)).AnchorPath!;
        Assert.True(await BorrowedFiles.WhenReady(path));
        var window = Park();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Revealed(window) && Viewer(window) is not null && window.CanRotateFromKeyboard
                && ((string)window.RotateRightButton.ToolTip).Contains("Not saved", StringComparison.Ordinal));

            Assert.Equal("IMG_0412.jpg", window.FileNameText.Text);
            Assert.Equal("Rotate right (Ctrl+R). Not saved. This file is read-only.", window.RotateRightButton.ToolTip);

            var menu = new ContextMenu();
            window.FillContextMenu(menu, selectedText: null);
            var labels = menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
            Assert.Contains("Copy", labels);
            Assert.DoesNotContain("Show in Explorer", labels);
            Assert.DoesNotContain("Copy path", labels);

            // The same photo as a file of the user's own has both.
            var own = Path.Combine(temp.Path, "IMG_0412.jpg");
            File.WriteAllBytes(own, Jpeg());
            window.ShowPreview(own, IntPtr.Zero);
            await WaitUntilAsync(() => Revealed(window) && window.CurrentPath == own && Viewer(window) is not null);
            window.FillContextMenu(menu, selectedText: null);
            labels = [.. menu.Items.OfType<MenuItem>().Select(item => (string)item.Header)];
            Assert.Contains("Show in Explorer", labels);
            Assert.Contains("Copy path", labels);
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    private static byte[] Jpeg()
    {
        var pixels = new byte[64 * 48 * 3];
        Array.Fill(pixels, (byte)140);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 48, 96, 96, PixelFormats.Rgb24, null, pixels, 64 * 3)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string WriteZip(TempDirectory temp, params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(temp.Path, $"card-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
            stream.Write(bytes);
        }

        return path;
    }

    private static PreviewWindow Park()
    {
        var dpi = (uint)Math.Round(PreviewWindow.ReadMonitor(IntPtr.Zero).Scale * 96);
        return new PreviewWindow { ParkedMonitor = StageMonitor.FromPixels(ParkedPixels, dpi) };
    }

    private static bool Revealed(PreviewWindow window) => Fields.Get<bool>(window, "_loadFinished");

    private static Image? Viewer(PreviewWindow window) =>
        window.PreviewContent.Content is Grid { Children: [Canvas { Children: [Border { Child: Image picture }] }, ..] }
            ? picture
            : null;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the window.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A folder view whose selection is the given items, the first of them
    /// the anchor. It answers the two questions a selection read asks and
    /// declines the rest.
    /// </summary>
    private sealed class SelectedItems : IFolderView
    {
        private const int NotImplemented = unchecked((int)0x80004001);
        private readonly IntPtr[] _pidls;

        public SelectedItems(params string[] parsingNames)
        {
            _pidls = [.. parsingNames.Select(name =>
            {
                Marshal.ThrowExceptionForHR(SHParseDisplayName(name, IntPtr.Zero, out var pidl, 0, out _));
                return pidl;
            })];
        }

        public int ItemCount(uint uFlags, out int pcItems)
        {
            pcItems = _pidls.Length;
            return 0;
        }

        public int Items(uint uFlags, ref Guid riid, out IntPtr ppv) =>
            SHCreateShellItemArrayFromIDLists((uint)_pidls.Length, _pidls, out ppv);

        public int GetCurrentViewMode(out uint pViewMode)
        {
            pViewMode = 0;
            return NotImplemented;
        }

        public int SetCurrentViewMode(uint viewMode) => NotImplemented;

        public int GetFolder(ref Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            return NotImplemented;
        }

        public int Item(int iItemIndex, out IntPtr ppidl)
        {
            ppidl = IntPtr.Zero;
            return NotImplemented;
        }

        public int GetSelectionMarkedItem(out int piItem)
        {
            piItem = 0;
            return 0;
        }

        public int GetFocusedItem(out int piItem)
        {
            piItem = 0;
            return 0;
        }

        public int GetItemPosition(IntPtr pidl, out long ppt)
        {
            ppt = 0;
            return NotImplemented;
        }

        public int GetSpacing(IntPtr ppt) => NotImplemented;

        public int GetDefaultSpacing(out long ppt)
        {
            ppt = 0;
            return NotImplemented;
        }

        public int GetAutoArrange() => NotImplemented;

        public int SelectItem(int iItem, uint dwFlags) => NotImplemented;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out IntPtr array);
}
