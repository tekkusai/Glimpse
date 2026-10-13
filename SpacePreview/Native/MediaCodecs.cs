using System.Runtime.InteropServices;

namespace SpacePreview.Native;

/// <summary>
/// Whether Windows can decode a format right now, asked the way the
/// decoders are found when a file opens: Media Foundation's catalog for
/// video, WIC's for images. Each answer is true, false, or null when the
/// question itself failed; only a false may ever reach the user as
/// "missing". Each lookup takes milliseconds, so never on the UI thread.
/// </summary>
internal static class MediaCodecs
{
    // MFVideoFormat_HEVC, GUID_ContainerFormatHeif and GUID_ContainerFormatRaw.
    internal static readonly Guid HevcVideo = new("43564548-0000-0010-8000-00AA00389B71");
    internal static readonly Guid HeifContainer = new("E1E62521-6787-405B-A339-500715B5763F");
    internal static readonly Guid RawContainer = new("FE99CE60-F19C-433C-A3AE-00ACEFA9CA21");

    private static readonly Guid VideoDecoderCategory = new("D6C02D4B-6833-45B4-971A-05A4B04BAB91");
    private static readonly Guid VideoMajorType = new("73646976-0000-0010-8000-00AA00389B71");
    private const int WincodecErrComponentNotFound = unchecked((int)0x88982F50);

    // Every kind of decoder counts, hardware and Store ones included: a
    // decoder this misses would turn into a false "missing".
    private const uint EnumerateEverything = 0x3F | 0x400;

    public static bool? HasVideoDecoder(Guid subtype)
    {
        try
        {
            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = VideoMajorType, guidSubtype = subtype };
            if (MFTEnumEx(VideoDecoderCategory, EnumerateEverything, ref input, IntPtr.Zero, out var activates, out var count) < 0)
            {
                return null;
            }

            for (var i = 0; i < count; i++)
            {
                Marshal.Release(Marshal.ReadIntPtr(activates, i * IntPtr.Size));
            }

            Marshal.FreeCoTaskMem(activates);
            return count > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            // Windows N without the Media Feature Pack has no catalog to ask.
            return null;
        }
    }

    public static bool? HasImageDecoder(Guid containerFormat)
    {
        try
        {
            var factory = (IWICImagingFactory)new WICImagingFactory();
            try
            {
                var hr = factory.CreateDecoder(ref containerFormat, IntPtr.Zero, out var decoder);
                if (decoder != IntPtr.Zero)
                {
                    Marshal.Release(decoder);
                }

                return hr >= 0 ? true : hr == WincodecErrComponentNotFound ? false : null;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (COMException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_REGISTER_TYPE_INFO
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(
        Guid guidCategory,
        uint flags,
        ref MFT_REGISTER_TYPE_INFO inputType,
        IntPtr outputType,
        out IntPtr activates,
        out uint count);

    [ComImport]
    [Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        // The first four slots only hold the vtable order.
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();

        [PreserveSig]
        int CreateDecoder(ref Guid containerFormat, IntPtr vendor, out IntPtr decoder);
    }

    [ComImport]
    [Guid("CACAF262-9370-4615-A13B-9F5539DA4C0A")]
    private class WICImagingFactory
    {
    }
}
