using System.Text;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// What the file, error and package cards say, and where the card puts
/// things. Each case is a decision a user reads on screen.
/// </summary>
public sealed class CardsBehaviorTests
{
    private const int ComponentNotFound = unchecked((int)0x88982F50);
    private const int InvalidFileFormat = unchecked((int)0xC00D36BE);

    [Fact]
    public void ARandomFileNamedHeicIsDamagedNotMissingACodec()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "broken.heic");
        var bytes = new byte[4096];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        // The old card sent every .heic that failed to the Store for HEVC.
        Assert.Equal(HeifFailure.Damaged, FileSignatures.ClassifyHeifFailure(path));
    }

    [Fact]
    public void OnlyARealHeifHeaderWithoutBadDataBlamesTheCodec()
    {
        var heic = HeifHeader("heic");
        var mif1 = HeifHeader("mif1");

        Assert.Equal(HeifFailure.NeedsHevc, FileSignatures.ClassifyHeifFailure(heic, ComponentNotFound));
        Assert.Equal(HeifFailure.NeedsHeif, FileSignatures.ClassifyHeifFailure(mif1, ComponentNotFound));
        Assert.Equal(HeifFailure.Damaged, FileSignatures.ClassifyHeifFailure(heic, InvalidFileFormat));
        Assert.Equal(HeifFailure.Damaged, FileSignatures.ClassifyHeifFailure(heic, null));
        Assert.Equal(HeifFailure.Damaged, FileSignatures.ClassifyHeifFailure(new byte[32], ComponentNotFound));
    }

    [Theory]
    [InlineData(0, nameof(AppPackageSignature.Trusted))]
    [InlineData(unchecked((int)0x800B0100), nameof(AppPackageSignature.NotSigned))]
    [InlineData(unchecked((int)0x800B0109), nameof(AppPackageSignature.Untrusted))]
    [InlineData(unchecked((int)0x800B010A), nameof(AppPackageSignature.Untrusted))]
    [InlineData(unchecked((int)0x800B0101), nameof(AppPackageSignature.Expired))]
    [InlineData(unchecked((int)0x80096010), nameof(AppPackageSignature.Tampered))]
    [InlineData(unchecked((int)0x80004005), nameof(AppPackageSignature.Invalid))]
    public void ASideloadedPackageIsUntrustedNotTampered(int result, string expected)
    {
        Assert.Equal(expected, AppPackagePreviewLoader.ClassifyTrustResult(result).ToString());
    }

    [Fact]
    public void TrustLinesNameTheSignerAndWarnOnlyWhenTrustFails()
    {
        Assert.Equal(("Signed by Contoso", false), AppPackagePreviewLoader.DescribeSignature(AppPackageSignature.Trusted, "Contoso"));
        Assert.Equal(("Signed by Contoso, not trusted on this PC", true), AppPackagePreviewLoader.DescribeSignature(AppPackageSignature.Untrusted, "Contoso"));
        Assert.Equal(("Signed by Contoso, certificate expired", true), AppPackagePreviewLoader.DescribeSignature(AppPackageSignature.Expired, "Contoso"));
        Assert.Equal(("Contents don't match the signature", true), AppPackagePreviewLoader.DescribeSignature(AppPackageSignature.Tampered, null));
        Assert.Equal(("Not signed", true), AppPackagePreviewLoader.DescribeSignature(AppPackageSignature.NotSigned, null));
    }

    [Fact]
    public void ProgramFactsSkipWhatTheHeaderAlreadySays()
    {
        Assert.Equal(
            ["Version 10.0.1 · Microsoft Corporation"],
            PreviewWindow.ExecutableFacts("Notepad", "10.0.1", "Microsoft Corporation", "notepad.exe"));
        Assert.Equal(
            ["Windows NT BASE API Client DLL", "Version 10.0.1"],
            PreviewWindow.ExecutableFacts("Windows NT BASE API Client DLL", " 10.0.1 ", null, "kernel32.dll"));
        Assert.Empty(PreviewWindow.ExecutableFacts(null, null, null, "tool.exe"));
    }

    [Fact]
    public void ABrokenShortcutNamesItsTarget()
    {
        Assert.Equal(
            [@"C:\nowhere\gone.exe", "The target can't be found"],
            PreviewWindow.ShortcutFacts(@"C:\nowhere\gone.exe"));
        Assert.Equal(["This shortcut doesn't point to a file"], PreviewWindow.ShortcutFacts(null));
    }

    [Fact]
    public void RunAsAdministratorIsOfferedOnlyWhereTheShellCanDoIt()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);

        Assert.True(PreviewWindow.CanRunAsAdministrator(Path.Combine(system, "notepad.exe")));
        Assert.False(PreviewWindow.CanRunAsAdministrator(Path.Combine(system, "kernel32.dll")));
    }

    [Theory]
    [InlineData(@"C:\", "\"C:\\\\\"")]
    [InlineData(@"C:\Users\me", "\"C:\\Users\\me\"")]
    [InlineData(@"\\server\share\", "\"\\\\server\\share\\\\\"")]
    public void ADriveRootReachesTheTerminalWhole(string folder, string expected)
    {
        Assert.Equal(expected, PreviewWindow.QuoteArgument(folder));
    }

    [Fact]
    public void NothingOnTheCardMovesBetweenKinds()
    {
        foreach (var canvas in new[] { 428.0, 623.0, 848.0 })
        {
            // A plain file (title only), a program (two facts) and an error
            // with its action pill share one visual slot and one title line.
            var plain = FileCardLayout.SlotTop(canvas, bodyHeight: 27, footerNeed: 0);
            Assert.Equal(plain, FileCardLayout.SlotTop(canvas, bodyHeight: 70, footerNeed: 0));
            Assert.Equal(plain, FileCardLayout.SlotTop(canvas, bodyHeight: 50, footerNeed: 30));
            Assert.Equal(Math.Round((canvas * FileCardLayout.Anchor) - (FileCardLayout.SlotHeight(canvas) / 2)), plain);
        }
    }

    [Fact]
    public void AFolderListLiftsTheCardOnlyWhenItWouldNotFit()
    {
        Assert.Equal(FileCardLayout.SlotTop(848, 50, 0), FileCardLayout.SlotTop(848, 50, 96));

        var lifted = FileCardLayout.SlotTop(428, 50, 96);
        Assert.True(lifted < FileCardLayout.SlotTop(428, 50, 0));
        Assert.True(lifted >= 24);
    }

    [Fact]
    public void PackageFactsAreThePublisherThenOneMetaLine()
    {
        var info = new AppPackageInfo("Sketchpad", "Contoso Labs", null, "2.4.1.0", "x64", 1, false, [], 0, null, "app.msix");

        Assert.Equal(["Contoso Labs", "Version 2.4.1.0 · x64"], PreviewWindow.AppPackageFacts(info));
    }

    private static byte[] HeifHeader(string brand)
    {
        var header = new byte[32];
        header[3] = 24;
        Encoding.ASCII.GetBytes("ftyp").CopyTo(header, 4);
        Encoding.ASCII.GetBytes(brand).CopyTo(header, 8);
        Encoding.ASCII.GetBytes(brand).CopyTo(header, 16);
        return header;
    }
}
