using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The rail jumps by clicking the viewer's own page box. A viewer in a
/// right-to-left language mirrors its toolbar, and in 3.0.4 the click meant
/// for the page box zoomed the document in and left it on its page.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PdfPageBoxAimTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    // The first jump comes before the viewer has been asked anything, so it
    // aims where the box sits; the next ones aim at the box itself. Narrow
    // and wide stages, and a page count of three digits, which widens the box.
    [Theory]
    [InlineData("ar-SA", 480)]
    [InlineData("ar-SA", 1400)]
    [InlineData("he-IL", 480)]
    [InlineData("he-IL", 1400)]
    public Task ARightToLeftViewerTakesTheJumpAtItsPageBox(string language, double width) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "pages.pdf");
        FillSignBehaviorTests.WritePdf(path, 612, 792, 120);
        var view = new WebView2();
        var window = new Window
        {
            Left = -30000, Top = 0, Width = width, Height = 650,
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Content = view
        };
        var preview = new PreviewWindow();
        var culture = CultureInfo.CurrentUICulture;
        window.Show();
        try
        {
            // Its own browser, so the language does not leak into other tests.
            var options = new CoreWebView2EnvironmentOptions(WebViewEnvironment.BrowserArguments) { Language = language };
            var environment = await CoreWebView2Environment.CreateAsync(
                null, System.IO.Path.Combine(TestDataRoot.RunRoot, "webview-" + language), options);
            await view.EnsureCoreWebView2Async(environment);
            typeof(PreviewWindow).GetMethod("ConfigurePdfViewer", Private)!.Invoke(null, [view.CoreWebView2, path]);
            view.Source = new Uri(new Uri(path).AbsoluteUri + "#page=1&view=FitH");
            var hwnd = new WindowInteropHelper(window).Handle;
            Assert.Equal(1, (await PageBoxAsync(hwnd)).Page);

            // A viewer this young turns typing away; the rail types again.
            await Task.Delay(1000);

            var state = Activator.CreateInstance(
                typeof(PreviewWindow).GetNestedType("PdfPageRailState", BindingFlags.NonPublic)!,
                new ContentControl(), new ColumnDefinition(), view, path, 0L, CancellationToken.None)!;

            // Windows in the viewer's language, as a user's would be.
            CultureInfo.CurrentUICulture = new CultureInfo(language);
            var aimed = await JumpAsync(preview, state, view, 5);
            var (page, box) = await PageBoxAsync(hwnd);
            var origin = view.PointToScreen(new Point(0, 0));
            var bounds = box.Current.BoundingRectangle;
            Assert.True(page == 5, $"On page {page}: aimed at {aimed.X:0},{aimed.Y:0}, the box is {bounds.Left - origin.X:0} to {bounds.Right - origin.X:0}, {bounds.Top - origin.Y:0} to {bounds.Bottom - origin.Y:0} of {view.ActualWidth:0}.");

            // Once the viewer has shown its box, the box's own rectangle
            // decides, even where Windows' language would guess wrong.
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Fields.Set(state, "PageAutomationElement", box);
            await JumpAsync(preview, state, view, 97);
            Assert.Equal(97, (await PageBoxAsync(hwnd)).Page);
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
            preview.Close();
            view.Dispose();
            window.Close();
        }
    });

    /// <summary>Aims as the rail does, then clicks and types there.</summary>
    private static async Task<Point> JumpAsync(PreviewWindow preview, object state, WebView2 view, int page)
    {
        var aim = (Task<Point>)typeof(PreviewWindow).GetMethod("PdfPageBoxAsync", Private)!.Invoke(preview, [state])!;
        var box = await aim;
        await (Task)typeof(PreviewWindow).GetMethod("JumpPdfWithNativePageBoxAsync", Private)!.Invoke(null, [view.CoreWebView2, box, page])!;
        await Task.Delay(300);
        return box;
    }

    /// <summary>The viewer's page box and what it says, read without the product's asker.</summary>
    private static Task<(int Page, AutomationElement Box)> PageBoxAsync(IntPtr hwnd) => Task.Run(() =>
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            foreach (AutomationElement edit in AutomationElement.FromHandle(hwnd).FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
            {
                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                    && int.TryParse(((ValuePattern)pattern).Current.Value, out var page))
                {
                    return (page, edit);
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "The viewer never showed a page box.");
            Thread.Sleep(50);
        }
    });
}
