using System.Xml.Linq;

namespace SpacePreview.Tests;

/// <summary>
/// Contracts for the visible surface and its process-level adversarial probe.
/// The PowerShell suite exercises these against a rendered window; these fast
/// tests keep the required instrumentation and accessibility hooks from being
/// removed accidentally.
/// </summary>
public sealed class PreviewRuntimeContractTests
{
    [Fact]
    public void EveryNamedHeaderButtonHasAnAutomationName()
    {
        var document = XDocument.Load(TestSource.FilePath(@"SpacePreview\Preview\PreviewWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var namedButtons = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Button" && element.Attribute(x + "Name") is not null)
            .ToArray();

        Assert.NotEmpty(namedButtons);
        foreach (var button in namedButtons)
        {
            Assert.True(
                button.Attributes().Any(attribute => attribute.Name.LocalName == "AutomationProperties.Name"
                    && !string.IsNullOrWhiteSpace(attribute.Value)),
                $"{button.Attribute(x + "Name")!.Value} has no AutomationProperties.Name");
        }
    }

    [Fact]
    public void HeaderAndSwapKeepStableSafeLanes()
    {
        var document = XDocument.Load(TestSource.FilePath(@"SpacePreview\Preview\PreviewWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);

        Assert.Equal("Hidden", (string?)Named("FileIconImage").Attribute("Visibility"));
        Assert.Equal("True", (string?)Named("ContentSwapScrim").Attribute("IsHitTestVisible"));
        Assert.NotNull(Named("IdentityPanel"));
    }

    [Fact]
    public void ProcessProbeCanAuditLayoutControlsRacesAndRapidSwitches()
    {
        var app = TestSource.Read(@"SpacePreview\App.xaml.cs");
        var window = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        Assert.Contains("RunProbeSafelyAsync", app, StringComparison.Ordinal);
        Assert.Contains("audit:", app, StringComparison.Ordinal);
        Assert.Contains("buttonaudit", app, StringComparison.Ordinal);
        Assert.Contains("overlayrace", app, StringComparison.Ordinal);
        Assert.Contains("string.Equals(extra, \"welcome\"", app, StringComparison.Ordinal);
        Assert.Contains("show:", app, StringComparison.Ordinal);
        Assert.Contains("ProbeAuditState", window, StringComparison.Ordinal);
        Assert.Contains("ProbeButtonAudit", window, StringComparison.Ordinal);
        Assert.Contains("ProbeLoadingOverlayRaceAsync", window, StringComparison.Ordinal);
    }

    [Fact]
    public void WelcomeMentionsSupportedFilePickers()
    {
        var window = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        Assert.Contains("Select a file in Explorer or a file picker, then press Space.", window, StringComparison.Ordinal);
    }

}
