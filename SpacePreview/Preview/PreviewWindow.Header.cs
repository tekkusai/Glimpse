using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// What Open does for an item: the app Windows would use and the icon that
/// stands for it. Neither means Windows will ask the user to pick one.
/// </summary>
internal sealed record OpenTarget(string? AppName, string? IconSource)
{
    public bool AsksForApp => AppName is null && IconSource is null;
}

/// <summary>The one tool a kind of file brings to the header.</summary>
internal enum HeaderTool
{
    None,
    Zoom,
    Rotate,
    Sign
}

public partial class PreviewWindow
{
    // ---------------------------------------------------------------------
    // Header: browse, identity, the kind's tool, Open and the window's own
    // buttons. docs/DESIGN.md, Header.
    // ---------------------------------------------------------------------

    private const string OpenWithLabelText = "Open with…";

    // App icons outlive items: stepping through one app's files never
    // reloads its icon. UI thread only.
    private static readonly Dictionary<string, BitmapSource> AppIcons = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<(string Label, Action Action)> _openActions = [];
    private OpenTarget _openTarget = new(null, null);
    private HeaderTool _headerTool;
    private int _selectedCount = 1;
    // The item the details describe, read once per item, and the one fact
    // beside its size: an image's pixels, or what the content learned later,
    // such as a PDF's pages. A fact that lands re-renders without the disk.
    private FileSystemInfo? _headerItem;
    private string? _headerFact;

    /// <summary>
    /// What one item leaves behind goes before the next arrives, for a
    /// preview and for the welcome alike.
    /// </summary>
    private void ResetForNewItem()
    {
        // The outgoing image writes its pending rotation before anything
        // else touches state; the new item must never inherit it.
        _rotationSession?.Commit();
        _rotationSession = null;
        ReleaseZoomTarget();
        _escapeYieldsToContent = false;
        _lastContentClickInViewerChrome = false;
        _userEngagedContent = false;
        // A session dropped without the user choosing keeps its work as a
        // draft, like any other end nobody chose.
        _signDraft?.Close();
        _signDraft = null;
        _hasSignDraft = false;
        _fillSignSurface = null;
        _fillSignParkedContent = null;
        _deferredWebFailure = null;
        _fillSignPath = null;
    }

    /// <summary>
    /// The header for one item: names and registry reads only. The file and
    /// app icons land asynchronously; until they do the previous ones stay,
    /// so stepping through a folder never blinks.
    /// </summary>
    private void ShowHeader(string path, string contentPath, int selectedCount, bool fileDialogMode, long version, CancellationToken token)
    {
        var isDirectory = Directory.Exists(contentPath);
        FileNameText.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(FileNameText.Text))
        {
            FileNameText.Text = path;
        }

        _selectedCount = selectedCount;
        _headerItem = null;
        _headerFact = null;
        ApplyHeaderSubtitle(path, _shortcutTarget);

        _openActions = BuildOpenActions(path, contentPath, isDirectory);
        ShowOpenTarget(ResolveOpenTarget(contentPath, isDirectory));
        OpenControl.Visibility = fileDialogMode ? Visibility.Collapsed : Visibility.Visible;
        PinButton.Visibility = fileDialogMode ? Visibility.Collapsed : Visibility.Visible;
        SetBrowseControlsVisible(!fileDialogMode && !IsPinned);
        ShowTool(ToolFor(contentPath, isDirectory, fileDialogMode));
        SyncHeaderTools();

        // Only a window on screen keeps its old icon; on a fresh open it
        // would name the wrong item. The welcome hid it already.
        if (!IsVisible)
        {
            FileIconImage.Visibility = Visibility.Hidden;
        }

        _ = UpdateFileIconAsync(path, version, token);
    }

    /// <summary>The welcome's header is empty but for the window's buttons; its body says the rest.</summary>
    private void ShowWelcomeHeader()
    {
        FileNameText.Text = string.Empty;
        SubtitleText.Inlines.Clear();
        SubtitleText.ToolTip = null;
        _openActions = [];
        _openTarget = new OpenTarget(null, null);
        FileIconImage.Visibility = Visibility.Hidden;
        OpenControl.Visibility = Visibility.Collapsed;
        PinButton.Visibility = Visibility.Collapsed;
        SetBrowseControlsVisible(false);
        ShowTool(HeaderTool.None);
    }

    // ---------------------------------------------------------------------
    // Tools. The kind decides which one shows; the content binds it.
    // ---------------------------------------------------------------------

    /// <summary>The tool a kind of file brings, known from its name before anything loads.</summary>
    internal static HeaderTool ToolFor(string contentPath, bool isDirectory, bool fileDialogMode)
    {
        if (isDirectory)
        {
            return HeaderTool.None;
        }

        // Converted spreadsheets have no viewer chrome of their own.
        if (FileKinds.IsSpreadsheet(contentPath))
        {
            return OfficeDocumentPdfConverter.IsAvailable ? HeaderTool.Zoom : HeaderTool.None;
        }

        // A picker owns commit: nothing here may write to the file.
        if (fileDialogMode)
        {
            return HeaderTool.None;
        }

        if (FileKinds.IsImage(contentPath))
        {
            return HeaderTool.Rotate;
        }

        return Path.GetExtension(contentPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? HeaderTool.Sign
            : HeaderTool.None;
    }

    private void ShowTool(HeaderTool tool)
    {
        if (tool == _headerTool)
        {
            return;
        }

        _headerTool = tool;
        ZoomControls.Visibility = tool == HeaderTool.Zoom ? Visibility.Visible : Visibility.Collapsed;
        ContentActions.Visibility = tool == HeaderTool.Rotate ? Visibility.Visible : Visibility.Collapsed;
        SignButton.Visibility = tool == HeaderTool.Sign ? Visibility.Visible : Visibility.Collapsed;
        ZoomControls.IsEnabled = true;
        ContentActions.IsEnabled = true;
        SignButton.IsEnabled = true;
        ShowOptionalLanes();
    }

    private FrameworkElement? ToolFace(HeaderTool tool) => tool switch
    {
        HeaderTool.Zoom => ZoomControls,
        HeaderTool.Rotate => ContentActions,
        HeaderTool.Sign => SignButton,
        _ => null
    };

    /// <summary>
    /// Until the item reveals, its tool keeps its face and a click finds
    /// nothing to act on. Once revealed, a tool the content did not bind is
    /// disabled; content that binds a tool its kind did not predict shows it.
    /// </summary>
    private void SyncHeaderTools()
    {
        var bound = _zoomWebView is not null ? HeaderTool.Zoom
            : _isFileDialogMode ? HeaderTool.None
            : _rotationSession is not null ? HeaderTool.Rotate
            : _fillSignPath is not null ? HeaderTool.Sign
            : HeaderTool.None;
        if (bound != HeaderTool.None)
        {
            ShowTool(bound);
        }

        var settled = _loadFinished;
        ZoomControls.IsEnabled = !settled || _zoomWebView is not null;
        ContentActions.IsEnabled = !settled || _rotationSession is not null;
        SignButton.IsEnabled = _fillSignSurface is null && (!settled || _fillSignPath is not null);

        // Work kept from last time: the pill offers it back, at the same width.
        SignLabel.Visibility = _hasSignDraft ? Visibility.Hidden : Visibility.Visible;
        ResumeLabel.Visibility = _hasSignDraft ? Visibility.Visible : Visibility.Hidden;
        SignButton.ToolTip = _hasSignDraft ? "Resume signing" : "Fill and sign";
        AutomationProperties.SetName(SignButton, (string)SignButton.ToolTip);
    }

    private void SignButton_Click(object sender, RoutedEventArgs e) => _ = EnterFillSignModeAsync();

    // ---------------------------------------------------------------------
    // Fit. The name keeps its floor; optional lanes step aside in turn.
    // ---------------------------------------------------------------------

    // A lane that stepped aside returns only with this much to spare, so a
    // rounding pixel at the threshold cannot flip it every layout.
    private const double LaneReturnMargin = 2;

    /// <summary>
    /// Every optional lane the item wants shows; the next layout steps
    /// aside whatever leaves the name under its floor. These two methods
    /// are the lanes' only writers.
    /// </summary>
    private void ShowOptionalLanes()
    {
        var selection = _multiSelectionPaths is not null ? Visibility.Visible : Visibility.Collapsed;
        GridButton.Visibility = selection;
        PlayButton.Visibility = selection;
        ContentToolsSlot.Visibility = _headerTool == HeaderTool.None ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The name keeps at least its floor. On a stage too narrow for it, the
    /// selection's grid and slideshow buttons step aside first, then the
    /// tool; Ctrl+Enter, the tool's keys and the right-click menu still
    /// reach them. Each comes back once the name keeps its floor beside it.
    /// </summary>
    private void IdentityPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || !IdentityPanel.IsVisible)
        {
            return;
        }

        // Each lane measured the same way shown or aside, so the name's
        // room with every lane aside is the same in both states.
        var toolWidth = 0.0;
        if (ToolFace(_headerTool) is { } tool)
        {
            tool.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            toolWidth = ContentToolsSlot.Margin.Left + tool.DesiredSize.Width;
        }

        var selectionWidth = BrowseControls.IsVisible ? GridButton.Width + PlayButton.Width : 0;
        var room = IdentityPanel.ActualWidth
            + (ContentToolsSlot.IsVisible ? toolWidth : 0)
            + (GridButton.IsVisible ? selectionWidth : 0);

        // Back in priority order, each where the floor still holds: the
        // tool first, then the selection's buttons in whatever room is left.
        var showTool = toolWidth > 0
            && room - toolWidth >= Metrics.HeaderNameMinWidth + (ContentToolsSlot.IsVisible ? 0 : LaneReturnMargin);
        room -= showTool ? toolWidth : 0;
        var showSelection = _multiSelectionPaths is not null
            && room - selectionWidth >= Metrics.HeaderNameMinWidth + (GridButton.IsVisible ? 0 : LaneReturnMargin);

        GridButton.Visibility = showSelection ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.Visibility = GridButton.Visibility;
        ContentToolsSlot.Visibility = showTool ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The header zoom drives the same browser zoom as Ctrl+wheel, and the
    /// label follows both.
    /// </summary>
    private void ShowZoomControls(WebView2 webView)
    {
        _zoomWebView = webView;
        webView.ZoomFactorChanged += OnZoomFactorChanged;
        UpdateZoomLabel();
    }

    private void ReleaseZoomTarget()
    {
        if (_zoomWebView is not null)
        {
            _zoomWebView.ZoomFactorChanged -= OnZoomFactorChanged;
            _zoomWebView = null;
        }
    }

    private void OnZoomFactorChanged(object? sender, EventArgs e) => UpdateZoomLabel();

    private void UpdateZoomLabel()
    {
        // An unbound pill keeps its face until the next sheet binds it.
        if (_zoomWebView is null)
        {
            return;
        }

        var factor = _zoomWebView.ZoomFactor;
        ZoomResetButton.Content = $"{Math.Round(factor * 100)}%";
        ZoomOutButton.IsEnabled = CanAdjustZoom(factor, -1);
        ZoomInButton.IsEnabled = CanAdjustZoom(factor, 1);
        AutomationProperties.SetName(ZoomResetButton, $"Reset zoom, currently {Math.Round(factor * 100)} percent");
    }

    private void ResetZoom()
    {
        if (_zoomWebView is null)
        {
            return;
        }

        _zoomWebView.ZoomFactor = 1.0;
        UpdateZoomLabel();
        DiagnosticLog.Write("zoom reset -> 1");
    }

    // The browser's own zoom stops (what Ctrl+wheel walks). The buttons walk
    // the same ladder, so both controls always agree and the label reads in
    // familiar levels instead of drifting to 196%.
    private static readonly double[] ZoomLevels =
        [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    internal static bool IsZoomInKey(int vk) => vk is NativeMethods.VK_ADD or NativeMethods.VK_OEM_PLUS;

    internal static bool IsZoomOutKey(int vk) => vk is NativeMethods.VK_SUBTRACT or NativeMethods.VK_OEM_MINUS;

    internal static bool IsZoomResetKey(int vk) => vk is NativeMethods.VK_0 or NativeMethods.VK_NUMPAD0;

    internal static double NextZoomLevel(double current, int direction)
    {
        if (!double.IsFinite(current))
        {
            current = 1;
        }

        if (direction > 0)
        {
            foreach (var level in ZoomLevels)
            {
                if (level > current + 0.001)
                {
                    return level;
                }
            }
        }
        else if (direction < 0)
        {
            for (var i = ZoomLevels.Length - 1; i >= 0; i--)
            {
                if (ZoomLevels[i] < current - 0.001)
                {
                    return ZoomLevels[i];
                }
            }
        }

        return Math.Clamp(current, ZoomLevels[0], ZoomLevels[^1]);
    }

    internal static bool CanAdjustZoom(double current, int direction) =>
        Math.Abs(NextZoomLevel(current, direction) - current) > 0.001;

    private void AdjustZoom(int direction)
    {
        if (_zoomWebView is null)
        {
            DiagnosticLog.Write("zoom adjust ignored: no webview");
            return;
        }

        var target = NextZoomLevel(_zoomWebView.ZoomFactor, direction);

        _zoomWebView.ZoomFactor = target;

        // The control's ZoomFactorChanged covers Ctrl+wheel; programmatic
        // sets do not raise it, so the label follows directly here.
        UpdateZoomLabel();
        DiagnosticLog.Write($"zoom adjust -> {target:0.##}");
    }

    // ---------------------------------------------------------------------
    // Identity.
    // ---------------------------------------------------------------------

    private async Task UpdateFileIconAsync(string path, long version, CancellationToken token)
    {
        var entered = false;
        try
        {
            await _shellIconGate.WaitAsync(token);
            entered = true;
            var icon = await Task.Run(() => ShellThumbnailProvider.TryGetIcon(path, 48), token);
            if (version != _loadVersion)
            {
                return;
            }

            // Hidden, never Collapsed: the name must not shift sideways.
            FileIconImage.Source = icon;
            FileIconImage.Visibility = icon is null ? Visibility.Hidden : Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // A newer preview owns the shell lane now.
        }
        catch
        {
            // Icon is decoration; the preview must not care.
        }
        finally
        {
            if (entered)
            {
                _shellIconGate.Release();
            }
        }
    }

    /// <summary>
    /// A file picker can hold several items but never browses them, so the
    /// count lives in the details there; elsewhere the position beside the
    /// arrows says it.
    /// </summary>
    internal void SetSelectionCount(int selectedCount)
    {
        _selectedCount = Math.Max(1, selectedCount);
        if (CurrentPath is { } path)
        {
            ApplyHeaderSubtitle(path, _shortcutTarget);
        }
    }

    private void ApplyHeaderSubtitle(string path, string? shortcutTarget)
    {
        var pickerCount = _isFileDialogMode ? _selectedCount : 1;
        SubtitleText.Inlines.Clear();
        SubtitleText.ToolTip = null;
        if (shortcutTarget is null)
        {
            var (details, modified) = DescribeHeaderItem(path);
            SubtitleText.Text = pickerCount > 1 ? $"{details} · {pickerCount} selected" : details;
            if (modified is { } when)
            {
                SubtitleText.ToolTip = $"Modified {when.ToString("f", CultureInfo.CurrentCulture)}";
            }

            return;
        }

        // The target is a link: click reveals it in its containing folder.
        var link = new Hyperlink(new Run($"Shortcut → {shortcutTarget}"))
        {
            TextDecorations = null,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = "Show target in Explorer"
        };
        link.SetResourceReference(TextElement.ForegroundProperty, "Theme.TextSecondary");
        link.Click += (_, _) => RevealInExplorer(shortcutTarget);
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        SubtitleText.Inlines.Add(link);

        if (pickerCount > 1)
        {
            SubtitleText.Inlines.Add(new Run($" · {pickerCount} selected"));
        }
    }

    private void RevealInExplorer(string target)
    {
        if (ExplorerRevealService.TryShow(target))
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The details line and the time it names. The directory entry, and an
    /// image's pixel size from its header bytes, are read on the item's first
    /// line only. Nothing when the item is gone.
    /// </summary>
    private (string Details, DateTime? Modified) DescribeHeaderItem(string path)
    {
        try
        {
            if (_headerItem is null)
            {
                _headerItem = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                if ((FileKinds.IsImage(path) || FileKinds.IsPhotoshop(path))
                    && LooksLikeLocalDrivePath(path)
                    && ImageHeader.TryRead(path) is { } image)
                {
                    _headerFact = $"{image.Width}×{image.Height}";
                }
            }

            return (FileKinds.Describe(_headerItem, _headerFact, DateTime.Now, CultureInfo.CurrentCulture), _headerItem.LastWriteTime);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    /// <summary>A fact the content found after the header went up, for the item still showing.</summary>
    private void ShowHeaderFact(string fact, long version)
    {
        if (version != _loadVersion || CurrentPath is not { } path)
        {
            return;
        }

        _headerFact = fact;
        ApplyHeaderSubtitle(path, _shortcutTarget);
    }

    private static string? TryResolveShortcutTarget(string path)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            shortcut = ((dynamic)shell).CreateShortcut(path);
            var target = ((dynamic)shortcut!).TargetPath as string;
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            target = Environment.ExpandEnvironmentVariables(target);
            if (!IsSafeLocalPreviewTarget(target))
            {
                return null;
            }

            return File.Exists(target) || Directory.Exists(target) ? target : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        try
        {
            if (instance is not null && System.Runtime.InteropServices.Marshal.IsComObject(instance))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(instance);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    // ---------------------------------------------------------------------
    // Open. One pill: the app's icon, the word and a chevron.
    // ---------------------------------------------------------------------

    /// <summary>
    /// What Open does: a folder opens in File Explorer, a program or a
    /// shortcut runs itself, a document opens in its default app. With no
    /// default app Windows asks, and the pill says so.
    /// </summary>
    internal static OpenTarget ResolveOpenTarget(string contentPath, bool isDirectory)
    {
        if (isDirectory)
        {
            return new OpenTarget(
                "File Explorer",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        }

        var extension = Path.GetExtension(contentPath);
        var executable = NativeMethods.TryQueryAssociation(extension, NativeMethods.ASSOCSTR_EXECUTABLE);
        if (executable == "%1" || FileKinds.IsShortcut(contentPath) || FileKinds.IsInternetShortcut(contentPath))
        {
            return new OpenTarget(null, contentPath);
        }

        // Unassociated types resolve to the picker itself, and its name is
        // localized; the program behind it is not.
        var name = NativeMethods.TryQueryAssociation(extension, NativeMethods.ASSOCSTR_FRIENDLYAPPNAME);
        if (name is null || executable?.EndsWith(@"\OpenWith.exe", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new OpenTarget(null, null);
        }

        // A Store app (its id names the package) shows its tile icon; a
        // desktop app shows its program's icon.
        var appId = NativeMethods.TryQueryAssociation(extension, NativeMethods.ASSOCSTR_APPID);
        var icon = appId?.Contains('!') == true ? $@"shell:AppsFolder\{appId}" : executable;
        return new OpenTarget(name, icon);
    }

    private void ShowOpenTarget(OpenTarget target)
    {
        _openTarget = target;
        OpenInAppLabel.Visibility = target.AsksForApp ? Visibility.Hidden : Visibility.Visible;
        OpenWithLabel.Visibility = target.AsksForApp ? Visibility.Visible : Visibility.Hidden;
        OpenButton.ToolTip = target.AppName is { } app ? $"Open in {app} (Enter)"
            : target.AsksForApp ? "Choose an app to open this (Enter)"
            : "Open (Enter)";
        AutomationProperties.SetName(OpenButton, target.AppName is { } named ? $"Open in {named}"
            : target.AsksForApp ? "Open with"
            : "Open");

        if (target.IconSource is not { } source)
        {
            OpenAppIcon.Source = null;
            return;
        }

        if (AppIcons.TryGetValue(source, out var cached))
        {
            OpenAppIcon.Source = cached;
            return;
        }

        OpenAppIcon.Source = null;
        _ = LoadAppIconAsync(source);
    }

    private async Task LoadAppIconAsync(string source)
    {
        // Twice the slot's device pixels, scaled down: small icon frames are
        // often a different, blurrier drawing.
        var pixels = (int)Math.Ceiling(32 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        BitmapSource? icon = null;

        // One shell icon at a time: beside the file icon's request, one of
        // the two came back empty.
        await _shellIconGate.WaitAsync();
        try
        {
            icon = await Task.Run(() => ShellThumbnailProvider.TryGetIcon(source, pixels));
        }
        catch
        {
            // The pill reads without its icon.
        }
        finally
        {
            _shellIconGate.Release();
        }

        // A miss is not remembered; the next preview asks again.
        if (icon is null)
        {
            return;
        }

        AppIcons[source] = icon;
        if (string.Equals(_openTarget.IconSource, source, StringComparison.OrdinalIgnoreCase))
        {
            OpenAppIcon.Source = icon;
        }
    }

    /// <summary>
    /// The chevron's menu: another app first, then where the item lives,
    /// then what only this kind can do.
    /// </summary>
    private List<(string Label, Action Action)> BuildOpenActions(string path, string contentPath, bool isDirectory)
    {
        var actions = new List<(string Label, Action Action)>();
        var contentIsFile = !isDirectory && File.Exists(contentPath);
        if (contentIsFile && !FileKinds.IsExecutableLike(contentPath))
        {
            actions.Add((OpenWithLabelText, () => ShowOpenWithDialog(contentPath)));
        }

        // Not every preview was reached through an Explorer selection (file
        // dialogs, folder browsing), so every preview answers "where is it".
        if (File.Exists(path) || Directory.Exists(path))
        {
            actions.Add(("Show in Explorer", () => RevealInExplorer(path)));
        }

        if (contentIsFile && LooksLikeLocalDrivePath(contentPath))
        {
            actions.Add(("Share", () => SharePath(contentPath)));
        }

        if (_shortcutTarget is { } target)
        {
            actions.Add(("Show target in Explorer", () => RevealInExplorer(target)));
        }

        if (CanRunAsAdministrator(contentPath))
        {
            actions.Add(("Run as administrator", () => RunAsAdministrator(contentPath)));
        }

        if (isDirectory)
        {
            actions.Add(("Open in Terminal", () => OpenInTerminal(contentPath)));
        }

        return actions;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenCurrent();

    private void OpenMenuButton_Click(object sender, RoutedEventArgs e) => ShowOpenActionsMenu(OpenControl, _openActions);

    internal void OpenCurrent()
    {
        if (_isFileDialogMode)
        {
            DiagnosticLog.Write("open suppressed while file dialog owns commit");
            return;
        }

        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"open failed {path}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Windows' own Open With dialog, on a thread of its own: it is modal
    /// and may wait on another process, and this thread carries the
    /// keyboard hook, which Windows drops if it stalls. The preview steps
    /// aside the way Open does.
    /// </summary>
    private void ShowOpenWithDialog(string path)
    {
        if (_isFileDialogMode)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            var chosen = NativeMethods.ShowOpenWithDialog(path);
            DiagnosticLog.Write($"open with {(chosen ? "chosen" : "cancelled")} {path}");
        })
        {
            IsBackground = true,
            Name = "Open with"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SharePath(string path)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (ShareService.ShareFile(path, hwnd))
        {
            SuppressOutsideDismissFor(TimeSpan.FromSeconds(30));
            DiagnosticLog.Write($"share opened {path}");
            return;
        }

        DiagnosticLog.Write($"share declined {path}");
    }

    private void ShowOpenActionsMenu(FrameworkElement anchor, IReadOnlyList<(string Label, Action Action)> actions)
    {
        // The outside-click dismissal closes the menu on mouse DOWN; without
        // this guard the mouse UP on the chevron instantly reopens it, so
        // clicking the chevron a second time could fail to collapse the flyout.
        if (actions.Count == 0 || DateTime.UtcNow - _flyoutClosedAt < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("PreviewContextMenu"),
            PlacementTarget = anchor,
            Placement = PlacementMode.Custom
        };

        // Own the placement outright: right edge flush with the pill, one gap
        // below it. Post-open offset nudging conflicts with WPF's own edge
        // adjustment and loses.
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        [
            new CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width, targetSize.Height + Metrics.FlyoutGap),
                PopupPrimaryAxis.Horizontal)
        ];

        foreach (var (label, action) in actions)
        {
            menu.Items.Add(MenuItemFor(label, action));
        }

        TrackOwnedPopup(menu);
        menu.Closed += (_, _) => _flyoutClosedAt = DateTime.UtcNow;
        menu.IsOpen = true;
    }

    // An action runs after its menu has closed, so a dialog it opens never
    // fights the closing popup for the foreground.
    private MenuItem MenuItemFor(string label, Action action)
    {
        var item = new MenuItem { Header = label, Style = (Style)FindResource("PreviewMenuItem") };
        item.Click += (_, _) => Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
        return item;
    }

    // ---------------------------------------------------------------------
    // The right-click menu: one menu wherever the preview is ours. The PDF
    // viewer keeps its own; its selection lives inside the plugin.
    // ---------------------------------------------------------------------

    private void BuildContextMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("PreviewContextMenu") };
        TrackOwnedPopup(menu);
        RootChrome.ContextMenuOpening += (_, e) =>
        {
            // The welcome has no item to act on.
            if (CurrentPath is null)
            {
                e.Handled = true;
                return;
            }

            // A web right-click may have left the menu placed on the page.
            menu.ClearValue(ContextMenu.PlacementProperty);
            menu.ClearValue(ContextMenu.PlacementTargetProperty);
            menu.ClearValue(ContextMenu.HorizontalOffsetProperty);
            menu.ClearValue(ContextMenu.VerticalOffsetProperty);
            FillContextMenu(menu, selectedText: null);
        };
        _contextMenu = menu;
        RootChrome.ContextMenu = menu;
    }

    internal void FillContextMenu(ContextMenu menu, string? selectedText)
    {
        menu.Items.Clear();
        var separator = (Style)FindResource("PreviewMenuSeparator");
        if (!string.IsNullOrEmpty(selectedText))
        {
            menu.Items.Add(MenuItemFor("Copy", () => SetClipboard(() => Clipboard.SetText(selectedText), "copy selection")));
            menu.Items.Add(new Separator { Style = separator });
        }

        if (!_openTarget.AsksForApp)
        {
            menu.Items.Add(MenuItemFor(_openTarget.AppName is { } app ? $"Open in {app}" : "Open", OpenCurrent));
        }

        foreach (var (label, action) in _openActions)
        {
            menu.Items.Add(MenuItemFor(label, action));
        }

        // The selection's buttons may have stepped aside; their commands never do.
        if (_multiSelectionPaths is { Count: > 1 })
        {
            menu.Items.Add(new Separator { Style = separator });
            if (_gridView is null)
            {
                menu.Items.Add(MenuItemFor("All selected items", ToggleGridView));
            }

            menu.Items.Add(MenuItemFor(IsSlideshowActive ? "Stop slideshow" : "Slideshow", ToggleSlideshow));
        }

        menu.Items.Add(new Separator { Style = separator });
        menu.Items.Add(MenuItemFor(string.IsNullOrEmpty(selectedText) ? "Copy" : "Copy file", CopyCurrentFile));
        menu.Items.Add(MenuItemFor("Copy path", CopyCurrentPath));
    }

    private void OnWebContextMenuRequested(WebView2 webView, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        var core = webView.CoreWebView2;
        if (_contextMenu is not { } menu
            || RootChrome.ContextMenu is null
            || IsPdfViewerSource(core.Source ?? string.Empty))
        {
            ArmWebContextMenu(core);
            return;
        }

        args.Handled = true;
        var target = args.ContextMenuTarget;
        FillContextMenu(menu, target.HasSelection ? target.SelectionText : null);

        // Where the page asked, not where the mouse is: Shift+F10 and the
        // menu key ask at the caret. The browser reports device pixels.
        var dpi = VisualTreeHelper.GetDpi(webView);
        menu.PlacementTarget = webView;
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = args.Location.X / dpi.DpiScaleX;
        menu.VerticalOffset = args.Location.Y / dpi.DpiScaleY;
        menu.IsOpen = true;
        DiagnosticLog.Write($"web context menu -> preview menu at {args.Location.X},{args.Location.Y} selection={target.HasSelection}");
    }

    private void ArmWebContextMenu(CoreWebView2 core)
    {
        RememberWebViewProcesses(core);
        Interlocked.Exchange(ref _webContextMenuArmedUntilTicks, DateTime.UtcNow.AddSeconds(20).Ticks);
    }

    private void TrackOwnedPopup(ContextMenu menu)
    {
        menu.Opened += (_, _) => RegisterOwnedPopup(menu);
        menu.Closed += (_, _) => UnregisterOwnedPopup(menu);
    }

    private void RegisterOwnedPopup(ContextMenu menu)
    {
        Capture();
        Dispatcher.BeginInvoke(Capture, DispatcherPriority.Loaded);

        void Capture()
        {
            var hwnd = (PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            lock (_popupSync)
            {
                _ownedPopupHandles[menu] = hwnd;
            }
        }
    }

    private void UnregisterOwnedPopup(ContextMenu menu)
    {
        lock (_popupSync)
        {
            _ownedPopupHandles.Remove(menu);
        }
    }

    private void RememberWebViewProcesses(CoreWebView2 core)
    {
        var processIds = new List<uint>();
        if (core.BrowserProcessId != 0)
        {
            processIds.Add(core.BrowserProcessId);
        }

        try
        {
            foreach (var processInfo in core.Environment.GetProcessInfos())
            {
                if (processInfo.ProcessId > 0)
                {
                    processIds.Add((uint)processInfo.ProcessId);
                }
            }
        }
        catch
        {
            // The browser process id above is enough for normal menu routing.
        }

        if (processIds.Count == 0)
        {
            return;
        }

        lock (_popupSync)
        {
            foreach (var processId in processIds)
            {
                _webViewProcessIds.Add(processId);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Clipboard and focus.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Clicking into the preview hands it keyboard focus, so Ctrl+C and the
    /// viewers' own shortcuts land where the user is looking. Only a click in
    /// the CONTENT hands the keyboard to the content: a click on header chrome
    /// (chevron, pin, zoom) activates the window but keeps the keys, or
    /// stepping to a video would give every arrow key to the player.
    /// </summary>
    public void ActivateForInteraction(int screenX, int screenY)
    {
        if (!IsVisible)
        {
            return;
        }

        try
        {
            Activate();
        }
        catch
        {
            // Activation is best-effort; the click itself still lands.
        }

        // A click on web content lands in the browser process, so Windows may
        // deny the host the foreground and leave it with Explorer - where the
        // next Ctrl+C copies Explorer's selected file instead of the
        // highlighted text. The user clicked this window; make it true.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ForceForeground(hwnd);
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != hwnd)
            {
                DiagnosticLog.Write($"activate: foreground stayed 0x{foreground.ToInt64():X} (ours 0x{hwnd.ToInt64():X})");
            }
        }

        // Web-backed content (PDF viewer, docs) needs real keyboard focus in
        // the browser for its own Ctrl+C/Ctrl+F/arrow handling - the PDF
        // plugin's selection is invisible to the WPF side. Don't steal focus
        // from a text preview the user clicked into, and only do it when the
        // click actually landed on the content, not the header.
        bool clickedContent;
        double contentY;
        try
        {
            var y = PointFromScreen(new Point(screenX, screenY)).Y;
            clickedContent = y >= HeaderHeight;
            contentY = y - HeaderHeight;
        }
        catch
        {
            // A window mid-teardown cannot transform; treat as content.
            clickedContent = true;
            contentY = double.MaxValue;
        }

        if (!clickedContent)
        {
            // The browser child can retain Win32 focus after a content click.
            // A later header action means arrows belong to preview browsing
            // again, even though that stale child still reports focus.
            _userEngagedContent = false;
            _lastContentClickInViewerChrome = false;
            return;
        }

        // Only content clicks enable the content-specific key exceptions.
        // A click in the embedded viewer's own toolbar strip (page box,
        // find) is the one place typed keys are the viewer's words.
        _userEngagedContent = true;
        _lastContentClickInViewerChrome = contentY < ViewerChromeBandHeight;
        if (Keyboard.FocusedElement is not TextBoxBase
            && FindVisualChild<WebView2>(PreviewContent) is { } webView)
        {
            webView.Focus();
        }
    }

    private async Task CopyWebViewSelectionAsync(WebView2 webView)
    {
        try
        {
            if (webView.CoreWebView2 is { } core)
            {
                if (IsPdfViewerSource(core.Source ?? string.Empty))
                {
                    // The PDF plugin's selection is invisible to the DOM, so
                    // hand the copy to the viewer itself through the browser's
                    // input pipe. With nothing selected Chromium copies
                    // nothing: over a document, Ctrl+C means "copy the text".
                    DiagnosticLog.Write("ctrl+c forwarded into pdf viewer");
                    await ForwardCtrlCAsync(core);
                    return;
                }

                var json = await core.ExecuteScriptAsync("window.getSelection().toString()");
                var text = JsonSerializer.Deserialize<string>(json);
                if (!string.IsNullOrEmpty(text))
                {
                    SetClipboard(() => Clipboard.SetText(text), "copy web selection");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"webview selection copy failed: {ex.GetType().Name}: {ex.Message}");
        }

        CopyCurrentFile();
    }

    private static Task ForwardCtrlCAsync(CoreWebView2 core) =>
        DispatchModifiedKeyAsync(core, 67, "KeyC", "c", modifiers: 2);

    private static async Task DispatchPlainKeyAsync(CoreWebView2 core, int virtualKey, string code, string key)
    {
        var keyDown = $"{{\"type\":\"keyDown\",\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        var keyUp = $"{{\"type\":\"keyUp\",\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyDown);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUp);
    }

    private static async Task DispatchMouseClickAsync(CoreWebView2 core, double x, double y)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var sx = x.ToString("0.###", invariant);
        var sy = y.ToString("0.###", invariant);
        var down = $"{{\"type\":\"mousePressed\",\"x\":{sx},\"y\":{sy},\"button\":\"left\",\"clickCount\":1,\"buttons\":1,\"pointerType\":\"mouse\"}}";
        var up = $"{{\"type\":\"mouseReleased\",\"x\":{sx},\"y\":{sy},\"button\":\"left\",\"clickCount\":1,\"buttons\":0,\"pointerType\":\"mouse\"}}";
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", down);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", up);
    }

    private static async Task DispatchModifiedKeyAsync(CoreWebView2 core, int virtualKey, string code, string key, int modifiers)
    {
        var keyDown = $"{{\"type\":\"keyDown\",\"modifiers\":{modifiers},\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        var keyUp = $"{{\"type\":\"keyUp\",\"modifiers\":{modifiers},\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyDown);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUp);
    }

    /// <summary>
    /// The clipboard throws transiently when another process holds it open;
    /// for an app that supports Ctrl+C, one silent retry is the
    /// difference between trusted and flaky.
    /// </summary>
    private static void SetClipboard(Action write, string diagnosticLabel)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= 2)
                {
                    DiagnosticLog.Write($"{diagnosticLabel} failed: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                Thread.Sleep(35);
            }
        }
    }

    private void CopyCurrentFile()
    {
        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SetClipboard(() => Clipboard.SetFileDropList(new StringCollection { path }), $"copy file {path}");
    }

    private void CopyCurrentPath()
    {
        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SetClipboard(() => Clipboard.SetText(path), $"copy path {path}");
    }

    // ---------------------------------------------------------------------
    // Browse lane and window buttons.
    // ---------------------------------------------------------------------

    private void SetBrowseControlsVisible(bool visible)
    {
        BrowseControls.Visibility = visible && !_isFileDialogMode
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal void DetachBrowseContext(bool restoreSlideshowSize = true)
    {
        SetBrowseControlsVisible(false);
        _multiSelectionPaths = null;
        _multiSelectionIndex = 0;
        CloseGridView();
        StopSlideshow(restoreSlideshowSize);
        ShowOptionalLanes();
        UpdateSelectionPosition();
        SetBrowsePosition(-1, 0);
    }

    /// <summary>
    /// Chevrons rest at the folder's ends. An unknown position (probe, stale
    /// shell) keeps both live; the shell answers again on the next step.
    /// </summary>
    public void SetBrowsePosition(int index, int count)
    {
        var known = index >= 0 && count > 0;
        BrowsePreviousButton.IsEnabled = !known || index > 0;
        BrowseNextButton.IsEnabled = !known || index < count - 1;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPinned)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BrowsePreviousButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseRequested?.Invoke(this, PreviewBrowseDirection.Previous);
    }

    private void BrowseNextButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseRequested?.Invoke(this, PreviewBrowseDirection.Next);
    }

    /// <summary>
    /// At the pin cap the button stays until a slot frees up; the tooltip
    /// says how to free one. The unpin control remains available.
    /// </summary>
    public void SetPinLimitReached(bool reached)
    {
        if (IsPinned)
        {
            return;
        }

        PinButton.IsEnabled = !reached;
        PinButton.ToolTip = reached
            ? "Up to 6 previews can stay pinned. Unpin one to pin this."
            : "Pin (stays open)";
    }

    // ---------------------------------------------------------------------
    // Theme. Brushes follow the application's resources by themselves. The
    // frame's dark mode is re-applied, and every browser here is told the
    // new scheme: the pages Glimpse writes and the PDF viewer follow it.
    // ---------------------------------------------------------------------

    private void FollowTheme()
    {
        Action repaint = () =>
        {
            WindowFrame.ApplyThemeMode(Hwnd);
            foreach (var webView in FindVisualChildren<WebView2>(this))
            {
                try
                {
                    if (webView.CoreWebView2 is { } core)
                    {
                        WebViewTheme.Apply(core);
                    }
                }
                catch (InvalidOperationException)
                {
                    // A browser that closed or crashed has nothing to repaint.
                }
            }
        };
        Theme.Changed += repaint;
        Closed += (_, _) => Theme.Changed -= repaint;
    }
}
