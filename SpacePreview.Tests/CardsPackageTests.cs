using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The app package card: what its facts say, and that its logo is decoded
/// by the loader, within a size, before the window ever sees it.
/// </summary>
public sealed class CardsPackageTests
{
    // 20,000 by 20,000 one-bit pixels are 50 MB to decode from a file under
    // half a megabyte. The window used to decode them itself and stood still.
    [Fact]
    public void ALogoWithHugeDeclaredDimensionsIsRefusedByItsHeader()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "huge-logo.msix");
        WritePackage(path, Png(20_000, 20_000));

        var info = AppPackagePreviewLoader.TryLoad(path);

        Assert.NotNull(info);
        Assert.Null(info!.Logo);
    }

    [Fact]
    public void ALogoAtTheSizeBoundArrivesDecodedAndFrozen()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "logo.msix");
        WritePackage(path, Png(2048, 2048));

        var logo = AppPackagePreviewLoader.TryLoad(path)?.Logo;

        // Frozen, so the window's thread may draw what the loader's decoded.
        Assert.NotNull(logo);
        Assert.True(logo!.IsFrozen);
        Assert.Equal(256, logo.PixelWidth);
    }

    [Fact]
    public void ADescriptionIsTheFirstFactAndThePublisherJoinsTheBuildLine()
    {
        var info = Package("Sketchpad", "Sketch and annotate\n  on any image");

        Assert.Equal(
            ["Sketch and annotate on any image", "Contoso Labs · Version 2.4.1.0 · x64"],
            PreviewWindow.AppPackageFacts(info));
    }

    [Fact]
    public void ADescriptionThatOnlyRepeatsTheTitleIsNotShown()
    {
        Assert.Equal(["Contoso Labs", "Version 2.4.1.0 · x64"], PreviewWindow.AppPackageFacts(Package("Sketchpad", "sketchpad")));
        Assert.Equal(["Contoso Labs", "Version 2.4.1.0 · x64"], PreviewWindow.AppPackageFacts(Package("Sketchpad", null)));
    }

    private static AppPackageInfo Package(string title, string? description) =>
        new(title, "Contoso Labs", description, "2.4.1.0", "x64", 1, false, [], 0, null, "app.msix");

    private static void WritePackage(string path, byte[] logo)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("AppxManifest.xml").Open()))
        {
            writer.Write("""
                <?xml version="1.0" encoding="utf-8"?>
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Identity Name="Glimpse.Test" Publisher="CN=Example" Version="1.0.0.0"/>
                  <Properties>
                    <DisplayName>Logo test</DisplayName>
                    <Logo>Assets\Logo.png</Logo>
                  </Properties>
                </Package>
                """);
        }

        using var stream = archive.CreateEntry("Assets/Logo.png").Open();
        stream.Write(logo);
    }

    /// <summary>A valid one-bit PNG of zeros: small on disk whatever its dimensions.</summary>
    private static byte[] Png(int width, int height)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 1;
        Chunk(png, "IHDR", header);

        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + ((width + 7) / 8)];
            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        Chunk(png, "IDAT", data.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        png.Write(word);
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        png.Write(typed);

        var crc = 0xFFFFFFFFu;
        foreach (var value in typed)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        png.Write(word);
    }
}
