using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SpacePreview.Tests;

/// <summary>
/// A real PE that names itself the way Inno Setup's Glimpse installer does:
/// product Glimpse, description Glimpse Setup, and a version. Its body is a
/// copy of whoami.exe with every resource replaced by that one version
/// block. Tests read it and hash it; none ever runs it.
/// </summary>
internal static class SetupImage
{
    private const ushort RtVersion = 16;
    private const ushort EnglishUs = 0x0409;

    /// <param name="size">Zeros after the image bring it to this length, as Inno's payload does.</param>
    public static byte[] Create(string productVersion, long size = 0)
    {
        var path = Path.Combine(TestDataRoot.RunRoot, $"setup-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), path);
        try
        {
            var block = VersionBlock(productVersion);
            var update = BeginUpdateResource(path, bDeleteExistingResources: true);
            if (update == IntPtr.Zero)
            {
                throw new Win32Exception();
            }

            var updated = UpdateResource(update, RtVersion, 1, EnglishUs, block, (uint)block.Length);
            if (!EndUpdateResource(update, fDiscard: !updated) || !updated)
            {
                throw new Win32Exception();
            }

            var image = File.ReadAllBytes(path);
            if (size > image.Length)
            {
                Array.Resize(ref image, checked((int)size));
            }

            return image;
        }
        finally
        {
            File.Delete(path);
        }
    }

    // VS_VERSIONINFO: the fixed block, then one US English string table.
    private static byte[] VersionBlock(string productVersion)
    {
        var numbers = productVersion.Split('.').Select(int.Parse).ToArray();
        var high = (uint)(numbers[0] << 16 | numbers[1]);
        var low = (uint)(numbers[2] << 16);
        var fixedInfo = new byte[52];
        Buffer.BlockCopy(
            new uint[] { 0xFEEF04BD, 0x00010000, high, low, high, low, 0x3F, 0, 0x40004, 1, 0, 0, 0 },
            0,
            fixedInfo,
            0,
            fixedInfo.Length);

        var strings = new[]
        {
            ("ProductName", "Glimpse"),
            ("FileDescription", "Glimpse Setup"),
            ("ProductVersion", productVersion),
            ("FileVersion", productVersion)
        }.Select(entry => Node(entry.Item1, Encoding.Unicode.GetBytes(entry.Item2 + "\0"), text: true, entry.Item2.Length + 1));

        return Node("VS_VERSION_INFO", fixedInfo, text: false, fixedInfo.Length,
            Node("StringFileInfo", [], text: true, 0,
                Node("040904B0", [], text: true, 0, strings.ToArray())),
            Node("VarFileInfo", [], text: true, 0,
                Node("Translation", [0x09, 0x04, 0xB0, 0x04], text: false, 4)));
    }

    // One node of the tree: length, value length, type, key, value and
    // children, each starting on a four-byte boundary.
    private static byte[] Node(string key, byte[] value, bool text, int valueLength, params byte[][] children)
    {
        using var node = new MemoryStream();
        node.Write(new byte[2]);
        node.Write(BitConverter.GetBytes((ushort)valueLength));
        node.Write(BitConverter.GetBytes((ushort)(text ? 1 : 0)));
        node.Write(Encoding.Unicode.GetBytes(key + "\0"));
        Align(node);
        node.Write(value);
        foreach (var child in children)
        {
            Align(node);
            node.Write(child);
        }

        var bytes = node.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 2), (ushort)bytes.Length);
        return bytes;
    }

    private static void Align(MemoryStream stream)
    {
        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);
}
