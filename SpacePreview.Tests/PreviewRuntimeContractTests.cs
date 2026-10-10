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
            .Where(element => element.Name.LocalName is "Button" or "ToggleButton"
                && element.Attribute(x + "Name") is not null)
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
        Assert.Equal("False", (string?)Named("OutgoingContent").Attribute("IsHitTestVisible"));
        Assert.Equal("False", (string?)Named("StandInLayer").Attribute("IsHitTestVisible"));
        Assert.Equal("False", (string?)Named("IncomingContent").Attribute("IsHitTestVisible"));
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
        Assert.Contains("pinnedoff", app, StringComparison.Ordinal);
        Assert.Contains("settingsshot:", app, StringComparison.Ordinal);
        Assert.Contains("warningshot:", app, StringComparison.Ordinal);
        Assert.Contains("warningfinalshot:", app, StringComparison.Ordinal);
        Assert.Contains("overlayrace", app, StringComparison.Ordinal);
        Assert.Contains("string.Equals(extra, \"welcome\"", app, StringComparison.Ordinal);
        Assert.Contains("show:", app, StringComparison.Ordinal);
        Assert.Contains("ProbeAuditState", window, StringComparison.Ordinal);
        Assert.Contains("ProbeButtonAudit", window, StringComparison.Ordinal);
        Assert.Contains("ProbeLoadingOverlayRaceAsync", window, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewRequestsAreNotLoggedOrTrackedAsPresentedUntilNativeChecksPass()
    {
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var window = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var native = TestSource.Read(@"SpacePreview\Native\NativeMethods.cs");

        Assert.Contains("preview requested", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("show preview {selection.Path}", controller, StringComparison.Ordinal);
        Assert.Contains("var presented = _previewWindow.ShowPreview", controller, StringComparison.Ordinal);
        Assert.Contains("if (!presented)", controller, StringComparison.Ordinal);
        Assert.Contains("PresentationFailed += OnPreviewPresentationFailed", controller, StringComparison.Ordinal);
        Assert.Contains("var presented = _previewWindow.ShowWelcome()", controller, StringComparison.Ordinal);
        Assert.Contains("welcome presentation failed", controller, StringComparison.Ordinal);
        Assert.Contains("preview presentation confirmed", window, StringComparison.Ordinal);
        Assert.Contains("owner-remained-above-preview", window, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.IsWindowVisible(hwnd)", window, StringComparison.Ordinal);
        Assert.Contains("Hide();", window, StringComparison.Ordinal);
        Assert.Contains("if (!ShowPreview(finalPath, Hwnd))", window, StringComparison.Ordinal);
        Assert.Contains("GW_HWNDPREV", native, StringComparison.Ordinal);
    }

    [Fact]
    public void AlwaysOnTopIsPinnedOnlyWithASingleTopmostWritePath()
    {
        var document = XDocument.Load(TestSource.FilePath(@"SpacePreview\Preview\PreviewWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var toggle = document
            .Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "AlwaysOnTopButton");

        // Hidden until pinned, fresh default on, opacity-only reveal.
        Assert.Equal("ToggleButton", toggle.Name.LocalName);
        Assert.Equal("Collapsed", (string?)toggle.Attribute("Visibility"));
        Assert.Equal("True", (string?)toggle.Attribute("IsChecked"));
        Assert.Equal("0", (string?)toggle.Attribute("Opacity"));

        // The window declares no Topmost of its own: creation, the re-show
        // pulse, and fullscreen suppression all route through ApplyTopmost.
        Assert.Null(document.Root!.Attribute("Topmost"));

        var window = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        Assert.Contains(
            "(!IsPinned || _pinnedAlwaysOnTop) && !_topmostSuppressed",
            window,
            StringComparison.Ordinal);

        // Exactly two raw writes: ApplyTopmost itself, and the release half
        // of the re-show pulse. Anything else is a resurrection bug.
        Assert.Equal(2, CountOccurrences(window, "Topmost = "));
        Assert.Contains("Topmost = EffectiveTopmost", window, StringComparison.Ordinal);

        // A pinned window taken off the top layer must be recoverable when
        // buried: taskbar entry plus Alt+Tab. Presence is managed natively
        // (style bits + ITaskbarList); WPF's ShowInTaskbar stays the static
        // XAML value because flipping it at runtime hides and re-shows the
        // frame, a visible flash on a live window.
        Assert.Equal("False", (string?)document.Root!.Attribute("ShowInTaskbar"));
        Assert.Contains("IsPinned && !_pinnedAlwaysOnTop", window, StringComparison.Ordinal);
        Assert.Equal(0, CountOccurrences(window, "ShowInTaskbar = "));
        Assert.Contains("NativeMethods.SetTaskbarTab", window, StringComparison.Ordinal);
        Assert.Contains("WS_EX_APPWINDOW", window, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    [Fact]
    public void WelcomeMentionsSupportedFilePickers()
    {
        var window = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        Assert.Contains("Select a file in Explorer or a file picker, then press Space.", window, StringComparison.Ordinal);
    }

}
