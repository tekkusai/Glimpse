using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// Saves and the removal of a stranded working file are queued by file.
/// The table of those queues is the only thing their lock guards: no file
/// is touched under it, so a disk that stalls on one picture holds up
/// nothing that asks about another.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ImageRotationQueueTests
{
    // Every image preview asks, on the UI thread, whether a save of its
    // file is pending. 3.0.1 first removed a stranded working file with
    // that same lock held, so one folder on a share that had stopped
    // answering froze every preview.
    [Fact]
    public async Task RemovingAWorkingFileOnAStalledDiskHoldsUpNoOtherPicture()
    {
        using var temp = new TempDirectory();
        var path = ImageRotationRecoveryTests.WriteImage(temp, "scan.png", 16, 8);
        var other = ImageRotationRecoveryTests.WriteImage(temp, "other.png", 16, 8);
        var working = ImageRotationService.TempPathFor(path);
        File.WriteAllBytes(working, [1, 2, 3]);

        Task removal;
        using (var stall = StalledFile.Take(working))
        {
            removal = Task.Run(() => ImageRotationSession.RemoveStrandedTemp(path));
            Assert.True(stall.Reached.WaitOne(TimeSpan.FromSeconds(10)), "the removal reached the file and is waiting on it");

            // What another picture's preview asks before it decodes.
            var asked = Task.Run(() => Assert.True(ImageRotationSession.WhenSaved(other).IsCompleted));
            await asked.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await removal.WaitAsync(TimeSpan.FromSeconds(10));
        await ImageRotationSession.WhenSaved(path).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(working));
    }

    /// <summary>
    /// A file whose next open for anything but its attributes waits until
    /// this is disposed, as an open on a stalled share would: an exclusive
    /// oplock, taken and never acknowledged. <see cref="Reached"/> is set
    /// when such an open has arrived.
    /// </summary>
    private sealed class StalledFile : IDisposable
    {
        private const uint GenericRead = 0x80000000;
        private const uint ShareAll = 7;
        private const uint OpenExisting = 3;
        private const uint Overlapped = 0x40000000;
        private const uint RequestOplockLevel1 = 0x00090000;
        private const int IoPending = 997;

        private readonly SafeFileHandle _file;
        private readonly IntPtr _overlapped;

        private StalledFile(SafeFileHandle file, IntPtr overlapped, ManualResetEvent reached)
        {
            _file = file;
            _overlapped = overlapped;
            Reached = reached;
        }

        public ManualResetEvent Reached { get; }

        public static StalledFile Take(string path)
        {
            // Whatever scans a new file has to let go before the lock is granted.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                var file = CreateFileW(path, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, Overlapped, IntPtr.Zero);
                Assert.False(file.IsInvalid, "opened for the oplock");
                var reached = new ManualResetEvent(false);
                var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = reached.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
                if (!DeviceIoControl(file, RequestOplockLevel1, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, overlapped)
                    && Marshal.GetLastWin32Error() == IoPending)
                {
                    return new StalledFile(file, overlapped, reached);
                }

                file.Dispose();
                Marshal.FreeHGlobal(overlapped);
                reached.Dispose();
                Assert.True(DateTime.UtcNow < deadline, "Windows never granted the oplock.");
                Thread.Sleep(100);
            }
        }

        public void Dispose()
        {
            // Closing the handle lets the waiting open through.
            _file.Dispose();
            Reached.WaitOne(TimeSpan.FromSeconds(5));
            Marshal.FreeHGlobal(_overlapped);
            Reached.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    }
}
