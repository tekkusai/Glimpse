using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// Sign mode holds work the user cannot get back. The window's own key
/// handler must leave Space, Enter and the arrows to the page and its date
/// editor, and no exit may drop placed ink without asking.
/// </summary>
public sealed class InputSignModeTests
{
    [Theory]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    public void SignModeKeysReachThePageInsteadOfDismissingOrBrowsing(Key key)
    {
        Sta.Run(() =>
        {
            var window = new PreviewWindow();
            try
            {
                EnterSignMode(window, withWork: true);
                var dismissed = 0;
                var browsed = 0;
                window.DismissRequested += (_, _) => dismissed++;
                window.BrowseRequested += (_, _) => browsed++;

                var args = Keys.PreviewKeyDown(window, key);

                Assert.False(args.Handled);
                Assert.Equal(0, dismissed);
                Assert.Equal(0, browsed);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscInSignModeOffersTheSaveChoiceInsteadOfDismissing()
    {
        Sta.Run(() =>
        {
            var window = new PreviewWindow();
            try
            {
                var surface = EnterSignMode(window, withWork: true);
                var dismissed = 0;
                window.DismissRequested += (_, _) => dismissed++;

                var args = Keys.PreviewKeyDown(window, Key.Escape);

                Assert.True(args.Handled);
                Assert.Equal(0, dismissed);
                Assert.Equal(Visibility.Visible, Fields.Get<UIElement>(surface, "_saveBar").Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LeavingSignModeWithPlacedWorkAsksFirst()
    {
        Sta.Run(() =>
        {
            var window = new PreviewWindow();
            try
            {
                var surface = EnterSignMode(window, withWork: true);

                Assert.False(window.TryLeaveFillSign());
                Assert.Equal(Visibility.Visible, Fields.Get<UIElement>(surface, "_saveBar").Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LeavingSignModeWithNothingPlacedGoesAhead()
    {
        Sta.Run(() =>
        {
            var window = new PreviewWindow();
            try
            {
                EnterSignMode(window, withWork: false);

                Assert.True(window.TryLeaveFillSign());
                Assert.True(new PreviewWindow().TryLeaveFillSign());
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static FillSignSurface EnterSignMode(PreviewWindow window, bool withWork) =>
        SignMode.Enter(window, withWork);
}

internal static class SignMode
{
    /// <summary>
    /// Puts the window in sign mode on a one-page document, the state
    /// EnterFillSignModeAsync leaves behind, without rendering a real PDF.
    /// The window is in picker mode so an Enter that wrongly reached
    /// OpenCurrent could never launch anything.
    /// </summary>
    public static FillSignSurface Enter(PreviewWindow window, bool withWork)
    {
        var pixels = new byte[4 * 4 * 4];
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var path = Path.Combine(TestDataRoot.Path, "sign-mode.pdf");
        var surface = new FillSignSurface(0, 1, new PdfPreviewPage(bitmap, 4, 4));
        if (withWork)
        {
            surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        }

        Fields.Set(window, "CurrentPath", path);
        Fields.Set(window, "_isFileDialogMode", true);
        Fields.Set(window, "_fillSignSurface", surface);
        return surface;
    }

    public static bool SaveChoiceShown(FillSignSurface surface) =>
        Fields.Get<UIElement>(surface, "_saveBar").Visibility == Visibility.Visible;
}
