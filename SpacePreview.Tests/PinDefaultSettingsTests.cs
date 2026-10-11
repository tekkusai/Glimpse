using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// A new pin starts where the user last left Always on top on a pinned
/// preview.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PinDefaultSettingsTests
{
    [Fact]
    public void NewlyPinnedPreviewsStartOnTopByDefault()
    {
        Assert.True(new Settings().PinnedAlwaysOnTopDefault);
    }

    [Fact]
    public void AChoiceSavedBeforeThisVersionKeepsItsValue()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("settings.json", """{ "Theme": "Dark", "PinnedAlwaysOnTopDefault": false }""");

        Assert.False(Settings.Load(path).PinnedAlwaysOnTopDefault);
    }

    [Fact]
    public Task ThePinRemembersWhereTheUserLastLeftAlwaysOnTop() => WpfApp.RunAsync(() =>
    {
        var saved = Settings.Current.PinnedAlwaysOnTopDefault;
        // Never shown: no frame or taskbar button reaches the desktop.
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 800, Height = 600 };
        try
        {
            Settings.Update(settings => settings with { PinnedAlwaysOnTopDefault = true });
            window.SetPinned(true);
            Assert.True(window.AlwaysOnTopButton.IsChecked);

            window.AlwaysOnTopButton.IsChecked = false;
            Assert.False(Settings.Current.PinnedAlwaysOnTopDefault);

            window.SetPinned(false);
            window.SetPinned(true);
            Assert.False(window.AlwaysOnTopButton.IsChecked);
            Assert.False(window.Topmost);

            window.AlwaysOnTopButton.IsChecked = true;
            Assert.True(Settings.Current.PinnedAlwaysOnTopDefault);
        }
        finally
        {
            Settings.Update(settings => settings with { PinnedAlwaysOnTopDefault = saved });
            window.Close();
        }

        return Task.CompletedTask;
    });
}
