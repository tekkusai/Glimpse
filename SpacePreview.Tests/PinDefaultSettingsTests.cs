using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class PinDefaultSettingsTests
{
    [Fact]
    public void NewlyPinnedPreviewsStartOnTopByDefault()
    {
        Assert.True(new Settings().PinnedAlwaysOnTopDefault);
    }

    [Fact]
    public void UserCanChooseThatNewPinsStartNormally()
    {
        var settings = Settings.Normalize(new Settings
        {
            PinnedAlwaysOnTopDefault = false
        });

        Assert.False(settings.PinnedAlwaysOnTopDefault);
    }

    [Fact]
    public void SettingsExplainsThePerPreviewOverrideInPlainLanguage()
    {
        var xaml = TestSource.Read(@"SpacePreview\Services\SettingsWindow.xaml");

        Assert.Contains("Pinned previews stay on top", xaml, StringComparison.Ordinal);
        Assert.Contains("You can change this on each pinned preview.", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Each pinned preview has its own switch too.", xaml, StringComparison.Ordinal);
    }
}
