namespace SpacePreview.Tests;

public sealed class ReleaseHygieneTests
{
    [Fact]
    public void PublicDocsPointUsersAtGlimpseArtifacts()
    {
        var text = string.Join("\n", new[]
        {
            TestSource.Read("README.md"),
            TestSource.Read(@"docs\RELEASE.md"),
            TestSource.Read("SECURITY.md")
        });

        Assert.Contains("# Glimpse", text, StringComparison.Ordinal);
        Assert.Contains("Glimpse-Setup-<version>.exe", text, StringComparison.Ordinal);
        Assert.Contains(@"%LOCALAPPDATA%\Glimpse", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SpacePreview-Setup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SpacePreview.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"%LOCALAPPDATA%\SpacePreview", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseRegressionScriptTargetsTheShippedExecutable()
    {
        var script = TestSource.Read(@"SpacePreview\scripts\regression.ps1");

        Assert.Contains(@"Glimpse.exe", script, StringComparison.Ordinal);
        Assert.Contains("GLIMPSE_DATA_ROOT", script, StringComparison.Ordinal);
        Assert.Contains("'Glimpse.log'", script, StringComparison.Ordinal);
        Assert.Contains("'settings.json'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"$env:LOCALAPPDATA 'Glimpse\Glimpse.log'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"$env:LOCALAPPDATA 'Glimpse\settings.json'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SpacePreview.exe", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"SpacePreview\SpacePreview.log", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"SpacePreview\settings.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLicenseAndNoticesUsePublicBranding()
    {
        var license = TestSource.Read("LICENSE");
        var notices = TestSource.Read(@"docs\THIRD-PARTY-NOTICES.txt");

        Assert.Contains("Glimpse contributors", license, StringComparison.Ordinal);
        Assert.Contains("source license is in LICENSE", notices, StringComparison.Ordinal);
        Assert.DoesNotContain("SpacePreview contributors", license, StringComparison.Ordinal);
    }

}
