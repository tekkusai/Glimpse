namespace SpacePreview.Services;

internal static class PreviewMouseDismissalPolicy
{
    public static bool ShouldHideActivePreview(
        bool previewVisible,
        bool hitOwnedWindow,
        bool hitOwnedPopup,
        bool outsideDismissSuppressed = false)
    {
        return previewVisible && !hitOwnedWindow && !hitOwnedPopup && !outsideDismissSuppressed;
    }
}