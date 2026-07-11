using System.Runtime.InteropServices;

namespace SpacePreview.Native;

internal sealed record AudioMediaProperties(string? Title, string? DisplayArtist, string? AlbumTitle, TimeSpan? Duration);

/// <summary>
/// Reads audio properties from the shell property system - the same values
/// Explorer's details pane shows.
/// </summary>
internal static class ShellMediaProperties
{
    private static readonly Guid SummaryInformation = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid MediaFormat = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid MusicAlbumFormat = new("56A3372E-CE9C-11D2-9F0E-006097C686F6");
    private static readonly Guid MusicArtistFormat = new("FD122953-FA93-4EF7-92C3-04C946B2F7C8");
    internal const uint TitlePropertyId = 2;
    internal const uint MediaDurationPropertyId = 3;
    internal const uint AlbumTitlePropertyId = 4;
    internal const uint DisplayArtistPropertyId = 100;

    public static bool TryGetAudioMetadata(string path, out AudioMediaProperties metadata)
    {
        metadata = new AudioMediaProperties(null, null, null, null);
        try
        {
            var iid = typeof(IShellItem2).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) != 0 || item is null)
            {
                return false;
            }

            try
            {
                var title = ReadString(item, new PROPERTYKEY { FormatId = SummaryInformation, PropertyId = TitlePropertyId });
                var displayArtist = ReadString(item, new PROPERTYKEY { FormatId = MusicArtistFormat, PropertyId = DisplayArtistPropertyId });
                var albumTitle = ReadString(item, new PROPERTYKEY { FormatId = MusicAlbumFormat, PropertyId = AlbumTitlePropertyId });
                var duration = ReadDuration(item, new PROPERTYKEY { FormatId = MediaFormat, PropertyId = MediaDurationPropertyId });

                metadata = new AudioMediaProperties(title, displayArtist, albumTitle, duration);
                return title is not null || displayArtist is not null || albumTitle is not null || duration is not null;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadString(IShellItem2 item, PROPERTYKEY key)
    {
        var pointer = IntPtr.Zero;
        try
        {
            return item.GetString(ref key, out pointer) == 0 && pointer != IntPtr.Zero
                ? NormalizeString(Marshal.PtrToStringUni(pointer))
                : null;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    private static TimeSpan? ReadDuration(IShellItem2 item, PROPERTYKEY key)
    {
        return item.GetUInt64(ref key, out var ticks100ns) == 0 && ticks100ns is > 0 and <= long.MaxValue
            ? TimeSpan.FromTicks((long)ticks100ns)
            : null;
    }

    private static string? NormalizeString(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem2? ppv);

    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem (vtable order matters; only GetUInt32 is used).
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);

        // IShellItem2
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty(ref PROPERTYKEY key, IntPtr ppropvar);
        [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
        [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out System.Runtime.InteropServices.ComTypes.FILETIME pft);
        [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int pi);
        [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr ppsz);
        [PreserveSig] int GetUInt32(ref PROPERTYKEY key, out uint pui);
        [PreserveSig] int GetUInt64(ref PROPERTYKEY key, out ulong pull);
        [PreserveSig] int GetBool(ref PROPERTYKEY key, out int pf);
    }
}
