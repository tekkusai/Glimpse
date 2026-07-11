using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

internal static class ShareService
{
    private const string DataTransferManagerClassName = "Windows.ApplicationModel.DataTransfer.DataTransferManager";
    private static readonly Guid DataTransferManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
    private static readonly Guid DataTransferManagerInteropIid = new("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");

    public static bool ShareFile(string path, IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero || !IsShareableLocalFilePath(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var interop = GetInterop();
            var manager = GetForWindow(interop, ownerHwnd);
            TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            DispatcherTimer? cleanup = null;

            void RemoveHandler()
            {
                if (handler is not null)
                {
                    manager.DataRequested -= handler;
                    handler = null;
                }

                cleanup?.Stop();
                cleanup = null;
            }

            handler = async (_, args) =>
            {
                RemoveHandler();

                var request = args.Request;
                var deferral = request.GetDeferral();
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    request.Data.Properties.Title = Path.GetFileName(path);
                    request.Data.Properties.Description = $"Shared from {Branding.Name}";
                    request.Data.SetStorageItems([file]);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"share failed {path}: {ex.GetType().Name}: {ex.Message}");
                    request.FailWithDisplayText($"{Branding.Name} could not share this file.");
                }
                finally
                {
                    deferral.Complete();
                }
            };

            manager.DataRequested += handler;
            cleanup = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => RemoveHandler(), Dispatcher.CurrentDispatcher);

            Marshal.ThrowExceptionForHR(interop.ShowShareUIForWindow(ownerHwnd));
            DiagnosticLog.Write($"share requested {path}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"share unavailable {path}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static bool IsShareableLocalFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;

        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(normalized);
            return root is { Length: >= 3 }
                && char.IsLetter(root[0])
                && root[1] == ':'
                && root[2] is '\\' or '/';
        }
        catch
        {
            return false;
        }
    }

    private static IDataTransferManagerInterop GetInterop()
    {
        var className = IntPtr.Zero;
        var factory = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(DataTransferManagerClassName, DataTransferManagerClassName.Length, out className));
            var iid = DataTransferManagerInteropIid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref iid, out factory));
            return (IDataTransferManagerInterop)Marshal.GetObjectForIUnknown(factory);
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
            }

            if (className != IntPtr.Zero)
            {
                _ = WindowsDeleteString(className);
            }
        }
    }

    private static DataTransferManager GetForWindow(IDataTransferManagerInterop interop, IntPtr hwnd)
    {
        var manager = IntPtr.Zero;
        try
        {
            var iid = DataTransferManagerIid;
            Marshal.ThrowExceptionForHR(interop.GetForWindow(hwnd, ref iid, out manager));
            return WinRT.MarshalInspectable<DataTransferManager>.FromAbi(manager);
        }
        finally
        {
            if (manager != IntPtr.Zero)
            {
                WinRT.MarshalInspectable<DataTransferManager>.DisposeAbi(manager);
            }
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        [PreserveSig]
        int GetForWindow(IntPtr appWindow, ref Guid riid, out IntPtr dataTransferManager);

        [PreserveSig]
        int ShowShareUIForWindow(IntPtr appWindow);
    }
}
