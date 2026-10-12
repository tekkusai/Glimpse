using System.IO.Compression;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class AppPackagePreviewLoaderTests
{
    [Theory]
    [InlineData("app.appx")]
    [InlineData("app.msix")]
    [InlineData("app.appxbundle")]
    [InlineData("app.msixbundle")]
    public void WindowsAppPackagesUseDedicatedPreviewKind(string fileName)
    {
        Assert.True(FileKinds.IsWindowsAppPackage(fileName));
    }

    [Fact]
    public void TryLoadReadsManifestMetadataAndLogo()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "app.msix");
        CreateMinimalPackage(path);

        var info = AppPackagePreviewLoader.TryLoad(path);

        Assert.NotNull(info);
        Assert.Equal("Glimpse Test App", info!.Title);
        Assert.Equal("Example Publisher", info.Publisher);
        Assert.Equal("Small package used by tests.", info.Description);
        Assert.Equal("1.2.3.4", info.Version);
        Assert.Equal("x64", info.Architecture);
        Assert.Equal(1, info.ApplicationCount);
        Assert.False(info.IsBundle);
        Assert.Contains("internetClient", info.Capabilities);
        Assert.Equal(path, info.SourcePath);
        Assert.NotNull(info.Logo);
    }

    [Fact]
    public void TryLoadKeepsUnknownCapabilityTokens()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "app.msix");
        CreateMinimalPackage(path, "rareDeviceAccess");

        var info = AppPackagePreviewLoader.TryLoad(path);

        Assert.NotNull(info);
        Assert.Contains("rareDeviceAccess", info!.Capabilities);
    }

    // Reading an element's text recurses once per level; a manifest nested
    // past the limit is refused before that, and ordinary nesting reads.
    [Fact]
    public void AManifestNestedPastTheLimitIsRefused()
    {
        using var temp = new TempDirectory();
        var deep = Path.Combine(temp.Path, "deep.msix");
        var ordinary = Path.Combine(temp.Path, "ordinary.msix");
        CreateMinimalPackage(deep, publisherDisplayName: Nested(XmlNesting.MaxDepth + 50, "Deep Publisher"));
        CreateMinimalPackage(ordinary, publisherDisplayName: Nested(20, "Deep Publisher"));

        Assert.Null(AppPackagePreviewLoader.TryLoad(deep));
        Assert.Equal("Deep Publisher", AppPackagePreviewLoader.TryLoad(ordinary)?.Publisher);
    }

    [Fact]
    public void PublisherIdentityFallsBackToCommonNameWithoutDnNoise()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "app.msix");
        CreateMinimalPackage(path, publisherDisplayName: "ms-resource:PublisherName");

        var info = AppPackagePreviewLoader.TryLoad(path);

        Assert.NotNull(info);
        Assert.Equal("Example", info!.Publisher);
    }

    [Fact]
    public void CapabilityDescriptionsSpeakHumanWithWarningsFlagged()
    {
        Assert.Equal(("Internet access", true, false), PreviewWindow.DescribeAppCapability("internetClient"));
        Assert.Equal(("Full file system access", true, true), PreviewWindow.DescribeAppCapability("broadFileSystemAccess"));
        Assert.Equal(("Runs with full trust", true, true), PreviewWindow.DescribeAppCapability("runFullTrust"));
        Assert.Equal(("Rare device access", false, false), PreviewWindow.DescribeAppCapability("rareDeviceAccess"));
    }

    [Fact]
    public void UnsignedZipReportsNotSignedRatherThanInvalid()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "app.msix");
        CreateMinimalPackage(path);

        var (signature, signer) = AppPackagePreviewLoader.SignatureFor(path);

        Assert.Equal(AppPackageSignature.NotSigned, signature);
        Assert.Null(signer);
    }

    private static void CreateMinimalPackage(string path, string? extraCapability = null, string publisherDisplayName = "Example Publisher")
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var extraCapabilityElement = extraCapability is null
            ? string.Empty
            : $"\n    <Capability Name=\"{extraCapability}\"/>";

        WriteEntry(archive, "AppxManifest.xml", $"""
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
  <Identity Name="Glimpse.Test" Publisher="CN=Example, O=Example Org" Version="1.2.3.4" ProcessorArchitecture="x64"/>
  <Properties>
    <DisplayName>Glimpse Test App</DisplayName>
    <PublisherDisplayName>{publisherDisplayName}</PublisherDisplayName>
    <Description>Small package used by tests.</Description>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Applications>
    <Application Id="App" Executable="Glimpse.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Glimpse Test App" Description="Small package used by tests." Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\SmallLogo.png"/>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient"/>
""" + extraCapabilityElement + """
  </Capabilities>
</Package>
""");
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
        var entry = archive.CreateEntry("Assets/Logo.scale-200.png");
        using var imageStream = entry.Open();
        imageStream.Write(image);
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private static string Nested(int depth, string text) =>
        string.Concat(Enumerable.Repeat("<Span>", depth)) + text + string.Concat(Enumerable.Repeat("</Span>", depth));
}
