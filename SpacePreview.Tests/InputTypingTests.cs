using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// "When you are typing, Glimpse is not listening." Whether the user is
/// typing is read from Windows at the key: where the keyboard focus sits in
/// the window in front, and whether a caret is up there. Each test builds
/// the windows of one place a key can go, in the classes Explorer and a file
/// dialog give them, puts the focus there and asks.
/// </summary>
public sealed class InputTypingTests
{
    // Explorer's header on Windows 11 is one XAML island: the tabs, the
    // address bar, the search box and the command bar share this window and
    // have none of their own. Read from a live Explorer window, build 26300.
    private const string HeaderIsland = "Microsoft.UI.Content.DesktopChildSiteBridge";
    private const string HeaderInput = "InputSiteWindowClass";

    /// <summary>
    /// The fault in 3.0.0: a Space typed into Explorer's search box was
    /// swallowed, because no window there is an Edit.
    /// </summary>
    [Fact]
    public void ExplorersSearchBoxAndAddressBarAreTyping() => TestWindow.OnOwnThread(() =>
    {
        using var explorer = TestWindow.Root("CabinetWClass");
        using var island = explorer.Child(HeaderIsland);
        using var input = island.Child(HeaderInput);

        // Whichever of the island's two windows Windows gives the keyboard to.
        input.Focus();
        Assert.True(Typing(explorer.Handle));

        island.Focus();
        Assert.True(Typing(explorer.Handle));
    });

    /// <summary>
    /// The header is the island directly under the window. One inside a tab
    /// is a folder's own view, where a selection may live, and is left as
    /// it was.
    /// </summary>
    [Fact]
    public void AnIslandInsideATabIsNotTheHeader() => TestWindow.OnOwnThread(() =>
    {
        using var explorer = TestWindow.Root("CabinetWClass");
        using var tab = explorer.Child("ShellTabWindowClass");
        using var view = tab.Child(HeaderIsland);
        using var input = view.Child(HeaderInput);

        input.Focus();

        Assert.False(Typing(explorer.Handle));
    });

    [Fact]
    public void ARenameBoxIsTyping() => TestWindow.OnOwnThread(() =>
    {
        using var explorer = TestWindow.Root("CabinetWClass");
        using var view = explorer.Child("SHELLDLL_DefView");
        using var list = view.Child("DirectUIHWND");
        using var rename = list.Child("Edit");

        rename.Focus();

        Assert.True(Typing(explorer.Handle));
    });

    /// <summary>
    /// A file dialog's search box draws its text in a window that is not an
    /// Edit. Windows still holds its caret, and only while the box is live.
    /// </summary>
    [Fact]
    public void ATextBoxWithNoEditWindowIsTypingWhileItsCaretIsUp() => TestWindow.OnOwnThread(() =>
    {
        using var dialog = TestWindow.Root("#32770");
        using var wrapper = dialog.Child("SearchEditBoxWrapperClass");
        using var search = wrapper.Child("DirectUIHWND");

        search.Focus();
        Assert.False(Typing(dialog.Handle));

        search.ShowCaret();
        Assert.True(Typing(dialog.Handle));
    });

    [Fact]
    public void TheFileListTheTreeAndAButtonAreNotTyping() => TestWindow.OnOwnThread(() =>
    {
        using var explorer = TestWindow.Root("CabinetWClass");
        using var island = explorer.Child(HeaderIsland);
        using var view = explorer.Child("SHELLDLL_DefView");
        using var list = view.Child("DirectUIHWND");
        using var tree = explorer.Child("SysTreeView32");
        using var button = explorer.Child("Button");

        Assert.False(Typing(explorer.Handle));
        foreach (var place in new[] { list, tree, button })
        {
            place.Focus();
            Assert.False(Typing(explorer.Handle));
        }
    });

    /// <summary>
    /// A caret counts only in the window the key is going to. An app can
    /// leave one up in its editor while its file dialog is in front.
    /// </summary>
    [Fact]
    public void ACaretInAnotherWindowOfTheAppDoesNotCount() => TestWindow.OnOwnThread(() =>
    {
        using var editor = TestWindow.Root("GlimpseTestEditor");
        using var dialog = TestWindow.Root("#32770");
        using var view = dialog.Child("SHELLDLL_DefView");
        using var list = view.Child("DirectUIHWND");

        list.Focus();
        editor.ShowCaret();

        Assert.False(Typing(dialog.Handle));
    });

    [Fact]
    public void NoWindowIsNotTyping()
    {
        Assert.False(Typing(IntPtr.Zero));
    }

    /// <summary>
    /// The header is the one text place that is asked about again: it
    /// holds buttons too, and Esc there leaves the keyboard where it is.
    /// A rename and a picker's boxes are text and nothing else.
    /// </summary>
    [Fact]
    public void OnlyExplorersHeaderIsNamedAsTheHeader() => TestWindow.OnOwnThread(() =>
    {
        using var explorer = TestWindow.Root("CabinetWClass");
        using var island = explorer.Child(HeaderIsland);
        using var input = island.Child(HeaderInput);
        using var view = explorer.Child("SHELLDLL_DefView");
        using var list = view.Child("DirectUIHWND");
        using var rename = list.Child("Edit");

        input.Focus();
        Assert.True(ExplorerSelectionService.IsTyping(explorer.Handle, out var header));
        Assert.True(header);

        rename.Focus();
        Assert.True(ExplorerSelectionService.IsTyping(explorer.Handle, out header));
        Assert.False(header);

        list.Focus();
        Assert.False(ExplorerSelectionService.IsTyping(explorer.Handle, out header));
        Assert.False(header);
    });

    private static bool Typing(IntPtr root) => ExplorerSelectionService.IsTyping(root, out _);
}
