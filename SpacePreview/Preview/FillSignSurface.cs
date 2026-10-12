using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// Sign mode's whole world: the current page rendered on our own WPF
/// surface, so placement happens in coordinates we own instead of inside
/// the closed embedded viewer. Placements live in page pixels under a
/// Viewbox that fits the page's width, like the viewer the user just left.
/// The controls that act on a placement (outline, delete chip, resize
/// handle) live in a screen-space layer above it, so they keep their size
/// however small the page is drawn.
/// </summary>
internal sealed class FillSignSurface : Grid
{
    private const string Hint = "Pick Signature, Date or Stamp, then click the page.";
    private const string RestoredNotice = "Your earlier work is back.";
    private const double BarHeight = 48;
    private const double ChipSize = 22;
    private const double HandleSize = 12;
    private const double DateHeight = 0.028;

    private readonly List<SignPlacement> _placements = [];
    private readonly Grid _pageHost;
    private readonly Image _pageImage;
    private readonly Canvas _placementLayer;
    private readonly ScrollViewer _scroller;
    private readonly Grid _pageArea;
    private readonly Canvas _chromeLayer;
    private readonly Rectangle _hoverOutline;
    private readonly Rectangle _selectionOutline;
    private readonly Border _deleteChip;
    private readonly Border _resizeHandle;
    private readonly Grid _toolBar;
    private readonly Grid _saveBar;
    private readonly Grid _inkOverlay;
    private readonly Canvas _inkCanvas;
    private readonly List<List<Point>> _inkStrokes = [];
    private readonly List<Polyline> _inkVisuals = [];
    private readonly List<Action> _toolRefreshers = [];
    private StackPanel _tools = null!;
    private ButtonBase _signaturePill = null!;
    private ButtonBase _newStampPill = null!;
    private TextBlock _centerText = null!;
    private TextBlock _pageLabel = null!;
    private Button _previousPageChip = null!;
    private Button _nextPageChip = null!;
    private ButtonBase _cancelPill = null!;
    private ButtonBase _savePill = null!;
    private TextBlock _saveStatus = null!;
    private TextBlock _inkTitle = null!;
    private int _pageIndex;
    private int _totalPages;
    private double _pageWidth;
    private double _pageHeight;
    private string? _problem;
    private string? _notice;
    private bool _hintRetired;
    private SignPlacementKind? _armedTool;
    private Grid? _selectedFrame;
    private Grid? _hoveredFrame;
    private Action? _commitDateEdit;
    private Border? _signatureFlyout;
    private string? _selectedSignaturePath;
    private Polyline? _activeInkVisual;
    private BitmapSource? _inkBaseImage;
    private Rect _inkBaseRect = Rect.Empty;
    private Image? _inkBaseVisual;
    private Grid? _inkEditingFrame;

    public event Action? ExitRequested;

    /// <summary>True asks the host to replace the original file.</summary>
    public event Action<IReadOnlyList<SignPlacement>, bool>? SaveRequested;

    /// <summary>Asks the host to render another page into this surface.</summary>
    public event Action<int>? PageChangeRequested;

    /// <summary>The work or the page on screen changed: what a draft keeps.</summary>
    public event Action? WorkChanged;

    public FillSignSurface(int pageIndex, int totalPages, PdfPreviewPage page)
    {
        _pageIndex = pageIndex;
        _totalPages = Math.Max(totalPages, pageIndex + 1);
        _pageWidth = page.Width;
        _pageHeight = page.Height;

        var palette = Theme.Current;
        Background = Theme.Brush(palette.CanvasBg);
        Focusable = true;
        FocusVisualStyle = null;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(BarHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // The page, in its own pixel coordinates: the Viewbox scales the
        // image and the placement layer together, so normalized math never
        // needs the display size.
        _placementLayer = new Canvas { Width = page.Width, Height = page.Height, Background = Brushes.Transparent };
        _placementLayer.MouseLeftButtonDown += OnPageClick;
        _pageImage = new Image { Source = page.Bitmap, Width = page.Width, Height = page.Height, SnapsToDevicePixels = true };
        _pageHost = new Grid { Width = page.Width, Height = page.Height, Children = { _pageImage, _placementLayer } };

        // Fit to width and scroll, as the viewer does. The bitmap is two
        // pixels per point, so its own width is as large as it stays sharp.
        // The hairline sits outside the scaling, so it stays one pixel and
        // a white page keeps its edge on the light canvas.
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            Content = new Border
            {
                Margin = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                BorderBrush = Theme.Brush(palette.RaisedBorder),
                BorderThickness = new Thickness(1),
                Child = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    Child = _pageHost
                }
            }
        };
        _scroller.ScrollChanged += (_, _) => UpdateChrome();

        _hoverOutline = BuildOutline(palette);
        _selectionOutline = BuildOutline(palette);
        _deleteChip = BuildDeleteChip(palette);
        _resizeHandle = BuildResizeHandle(palette);
        _chromeLayer = new Canvas
        {
            ClipToBounds = true,
            Children = { _hoverOutline, _selectionOutline, _deleteChip, _resizeHandle }
        };

        // The pad's white comes from its rounded frame; the canvas only
        // needs a fill to be hit-testable.
        _inkCanvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        _inkOverlay = BuildInkOverlay(palette);

        _toolBar = BuildToolBar(palette);
        _saveBar = BuildSaveBar(palette);
        _saveBar.Visibility = Visibility.Collapsed;
        Children.Add(new Grid { Children = { _toolBar, _saveBar } });

        _pageArea = new Grid { ClipToBounds = true, Children = { _scroller, _chromeLayer, _inkOverlay } };
        SetRow(_pageArea, 1);
        Children.Add(_pageArea);

        KeyDown += (_, e) =>
        {
            // Delete edits the page, so it waits whenever the page does.
            if (e.Key is Key.Delete or Key.Back && _pageArea.IsEnabled && e.OriginalSource is not TextBox && _selectedFrame is { } selected)
            {
                RemovePlacementFrame(selected);
                e.Handled = true;
            }
        };

        // The picker dismisses like any flyout: a press anywhere outside it
        // (except its own pill, which toggles) puts it away.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_signatureFlyout is { } flyout
                && e.OriginalSource is DependencyObject source
                && !IsWithin(source, flyout)
                && !IsWithin(source, _signaturePill))
            {
                CloseSignaturePicker();
            }
        };

        // Settings, or this surface's own picker, may change the saved ink
        // while the mode is open.
        Loaded += (_, _) =>
        {
            SignatureStore.Changed -= OnStoreChanged;
            SignatureStore.Changed += OnStoreChanged;
        };
        Unloaded += (_, _) => SignatureStore.Changed -= OnStoreChanged;

        UpdatePageChrome();
        UpdateWorkState();
    }

    public IReadOnlyList<SignPlacement> Placements => _placements;

    public bool HasWork => _placements.Count > 0;

    public int PageIndex => _pageIndex;

    /// <summary>
    /// Brings back work from a draft, on its pages, and says so once. The
    /// line counts what did not come back: a placement whose signature or
    /// stamp was replaced or removed since is not restored. It is not a
    /// change: nothing is written until the user changes something.
    /// </summary>
    public void Restore(IReadOnlyList<SignPlacement> placements, int lost = 0)
    {
        foreach (var placement in placements)
        {
            _placements.Add(placement);
            if (placement.PageIndex == _pageIndex)
            {
                AddPlacementVisual(placement, select: false);
            }
        }

        _notice = !HasWork ? null : RestoredNoticeFor(lost);
        UpdateWorkState();
    }

    internal static string RestoredNoticeFor(int lost) => lost switch
    {
        <= 0 => RestoredNotice,
        1 => "Your earlier work is back, except 1 item.",
        _ => $"Your earlier work is back, except {lost} items."
    };

    /// <summary>A line in the bar's center, there until the next change to the work.</summary>
    public void ShowNotice(string notice)
    {
        _notice = notice;
        UpdateWorkState();
    }

    private void OnWorkChanged()
    {
        if (_notice is not null)
        {
            _notice = null;
            UpdateWorkState();
        }

        WorkChanged?.Invoke();
    }

    /// <summary>
    /// Escape walks back one layer at a time: an open editor, picker, pad
    /// or save choice closes, an armed tool is put down, and only then does
    /// the mode end, never past unsaved work without the save choice first.
    /// </summary>
    public void HandleEscape()
    {
        if (_commitDateEdit is { } commit)
        {
            commit();
        }
        else if (_signatureFlyout is not null)
        {
            CloseSignaturePicker();
        }
        else if (_inkOverlay.Visibility == Visibility.Visible)
        {
            HideInkCapture();
        }
        else if (_saveBar.Visibility == Visibility.Visible)
        {
            HideSaveChoice();
        }
        else if (_armedTool is not null)
        {
            DisarmTool();
        }
        else
        {
            LeaveOrAsk();
        }
    }

    /// <summary>
    /// True when leaving loses nothing: no work, or work this file could
    /// never save. Otherwise the save choice opens, Discard included, and
    /// the caller stays. Every way out of sign mode asks this first.
    /// </summary>
    public bool TryLeave()
    {
        if (!HasWork || _problem is not null)
        {
            return true;
        }

        ShowSaveChoice();
        return false;
    }

    private void LeaveOrAsk()
    {
        if (TryLeave())
        {
            ExitRequested?.Invoke();
        }
    }

    /// <summary>Offers the save choice: a copy, the original, or discarding the work.</summary>
    public void ShowSaveChoice()
    {
        if (!HasWork || _problem is not null)
        {
            return;
        }

        CloseSignaturePicker();
        SetSaveStatus(null, failed: false);
        _toolBar.Visibility = Visibility.Collapsed;
        _saveBar.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// While the writer runs, nothing here accepts input, and the save bar
    /// says so: replacing the original can retry for a few seconds, and
    /// silent numbness reads as a hang. The whole page area goes quiet, the
    /// chips and pad above the page included: an edit made now would miss
    /// the file and then go with the draft.
    /// </summary>
    public void SetSaving(bool saving)
    {
        _toolBar.IsEnabled = !saving;
        _saveBar.IsEnabled = !saving;
        _pageArea.IsEnabled = !saving;
        SetSaveStatus(saving ? "Saving\u2026" : null, failed: false);
    }

    /// <summary>A save that failed says why, and the choices come back.</summary>
    public void ShowSaveFailure(string reason)
    {
        SetSaving(false);
        _toolBar.Visibility = Visibility.Collapsed;
        _saveBar.Visibility = Visibility.Visible;
        SetSaveStatus(reason, failed: true);
    }

    /// <summary>
    /// The writer's verdict on this file. A readable file gets its true page
    /// count and live tools; one the writer cannot update says why where
    /// Save would be, and no work is ever placed on it.
    /// </summary>
    public void ApplyCheck(PdfSignCheck check)
    {
        if (check.Problem is null)
        {
            _totalPages = Math.Max(check.PageCount, _pageIndex + 1);
            UpdatePageChrome();
            _tools.IsEnabled = true;
            return;
        }

        ShowProblem(check.Problem + " Open it in another app to sign it.");
    }

    /// <summary>
    /// This file, or this page, cannot be signed here: the sentence says
    /// why and what to do, the tools go, and Cancel becomes Close.
    /// </summary>
    public void ShowProblem(string sentence)
    {
        _problem = sentence;
        DisarmTool();
        CloseSignaturePicker();
        HideInkCapture();
        HideSaveChoice();
        _tools.Visibility = Visibility.Collapsed;
        _savePill.Visibility = Visibility.Collapsed;
        _cancelPill.Content = "Close";
        UpdateWorkState();
    }

    // ------------------------------------------------------------------
    // Chrome.
    // ------------------------------------------------------------------

    private Grid BuildToolBar(ThemePalette palette)
    {
        var bar = new Grid { Background = Theme.Brush(palette.ChromeBg) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _signaturePill = BuildToolPill("Signature", SignPlacementKind.Signature);
        _newStampPill = HousePill.Build("New stamp", ReplaceStamp, PillRole.Ghost);
        _newStampPill.Visibility = Visibility.Collapsed;
        // Off until the writer's verdict: work goes on a file only once a
        // draft can keep it. A quick check never shows them off at all.
        _tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsEnabled = false,
            Children =
            {
                _signaturePill,
                BuildToolPill("Date", SignPlacementKind.Date),
                BuildToolPill("Stamp", SignPlacementKind.Stamp),
                _newStampPill
            }
        };
        bar.Children.Add(_tools);

        // The center says what to do next, or why this file cannot be
        // signed. A line that does not fit is hidden, never cut.
        _centerText = new TextBlock
        {
            FontSize = Metrics.FontCaption,
            Foreground = Theme.Brush(palette.TextTertiary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 12, 0)
        };
        var center = new Grid { ClipToBounds = true, Children = { _centerText } };
        center.SizeChanged += (_, _) => FitCenterText();
        SetColumn(center, 1);
        bar.Children.Add(center);

        // Moving between pages is part of signing, so the arrows live right
        // on the readout; single-page documents show no arrows at all.
        _pageLabel = new TextBlock
        {
            FontSize = Metrics.FontCaption,
            Foreground = Theme.Brush(palette.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        };
        _previousPageChip = BuildIconChip("\uE76B", "Previous page", () => RequestPage(_pageIndex - 1));
        _nextPageChip = BuildIconChip("\uE76C", "Next page", () => RequestPage(_pageIndex + 1));
        var pageCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Children = { _previousPageChip, _pageLabel, _nextPageChip }
        };
        SetColumn(pageCluster, 2);
        bar.Children.Add(pageCluster);

        _cancelPill = HousePill.Build("Cancel", LeaveOrAsk, PillRole.Ghost);
        _savePill = HousePill.Build("Save", ShowSaveChoice, PillRole.Accent);
        _savePill.Margin = new Thickness(0);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Children = { _cancelPill, _savePill }
        };
        SetColumn(actions, 3);
        bar.Children.Add(actions);

        bar.Children.Add(Hairline(palette, columns: 4));
        return bar;
    }

    private Grid BuildSaveBar(ThemePalette palette)
    {
        var bar = new Grid { Background = Theme.Brush(palette.ChromeBg) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var back = HousePill.Build("Keep editing", HideSaveChoice, PillRole.Ghost);
        back.Margin = new Thickness(12, 0, 0, 0);
        bar.Children.Add(back);

        _saveStatus = new TextBlock
        {
            FontSize = Metrics.FontCaption,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = BarHeight - 8,
            Margin = new Thickness(12, 0, 12, 0)
        };
        SetColumn(_saveStatus, 1);
        bar.Children.Add(_saveStatus);

        var copy = HousePill.Build("Save a signed copy", () => RequestSave(overwrite: false), PillRole.Accent);
        copy.Margin = new Thickness(0);
        var choices = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Children =
            {
                HousePill.Build("Discard", () => ExitRequested?.Invoke(), PillRole.Ghost),
                HousePill.Build("Replace the original", () => RequestSave(overwrite: true)),
                copy
            }
        };
        SetColumn(choices, 2);
        bar.Children.Add(choices);

        bar.Children.Add(Hairline(palette, columns: 3));
        return bar;
    }

    private static Border Hairline(ThemePalette palette, int columns)
    {
        var line = new Border
        {
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        SetColumnSpan(line, columns);
        return line;
    }

    /// <summary>A tool pill; only these three ever repaint for the armed tool.</summary>
    private ButtonBase BuildToolPill(string label, SignPlacementKind kind)
    {
        var pill = HousePill.Build(label, () => ArmTool(kind), out var refresh, PillRole.Surface, () => _armedTool == kind);
        AutomationProperties.SetName(pill, label);
        _toolRefreshers.Add(refresh);
        return pill;
    }

    /// <summary>A quiet icon button, the header's size and shape.</summary>
    /// <summary>A page arrow in the house icon button.</summary>
    private static Button BuildIconChip(string glyph, string name, Action onClick)
    {
        var chip = new Button
        {
            Content = glyph,
            ToolTip = name,
            Style = (Style)Application.Current.FindResource("IconButton")
        };
        AutomationProperties.SetName(chip, name);
        chip.Click += (_, _) => onClick();
        return chip;
    }

    private void SetSaveStatus(string? text, bool failed)
    {
        _saveStatus.Text = text ?? string.Empty;
        _saveStatus.Foreground = Theme.Brush(failed ? Theme.Current.Warning : Theme.Current.TextSecondary);
    }

    private void RequestPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _totalPages || pageIndex == _pageIndex)
        {
            return;
        }

        PageChangeRequested?.Invoke(pageIndex);
    }

    /// <summary>
    /// Swaps the rendered page under the same working set: placements on
    /// other pages stay and come back when their page does, so one save
    /// carries everything.
    /// </summary>
    public void ShowPage(int pageIndex, PdfPreviewPage page)
    {
        _commitDateEdit?.Invoke();
        _selectedFrame = null;
        _hoveredFrame = null;
        _pageIndex = pageIndex;
        _pageWidth = page.Width;
        _pageHeight = page.Height;
        _pageImage.Source = page.Bitmap;
        _pageImage.Width = page.Width;
        _pageImage.Height = page.Height;
        _pageHost.Width = page.Width;
        _pageHost.Height = page.Height;
        _placementLayer.Width = page.Width;
        _placementLayer.Height = page.Height;

        _placementLayer.Children.Clear();
        foreach (var placement in _placements.Where(item => item.PageIndex == pageIndex))
        {
            AddPlacementVisual(placement, select: false);
        }

        _scroller.ScrollToTop();
        UpdatePageChrome();
        UpdateChrome();
        OnWorkChanged();
    }

    private void UpdatePageChrome()
    {
        _pageLabel.Text = $"Page {_pageIndex + 1} of {_totalPages}";
        var navigable = _totalPages > 1;
        _previousPageChip.Visibility = navigable ? Visibility.Visible : Visibility.Collapsed;
        _nextPageChip.Visibility = navigable ? Visibility.Visible : Visibility.Collapsed;
        _previousPageChip.IsEnabled = _pageIndex > 0;
        _nextPageChip.IsEnabled = _pageIndex < _totalPages - 1;
    }

    /// <summary>Save, the hint and the problem line follow the work on the page.</summary>
    private void UpdateWorkState()
    {
        _savePill.IsEnabled = HasWork;
        _hintRetired |= HasWork;
        _centerText.Text = _problem ?? _notice ?? (_hintRetired ? string.Empty : Hint);
        _centerText.Foreground = Theme.Brush(_problem is null ? Theme.Current.TextTertiary : Theme.Current.Warning);
        FitCenterText();
    }

    private void FitCenterText()
    {
        if (_centerText.Parent is not FrameworkElement column || column.ActualWidth <= 0)
        {
            return;
        }

        var room = column.ActualWidth - _centerText.Margin.Left - _centerText.Margin.Right;
        _centerText.Measure(new Size(room, BarHeight));
        _centerText.Visibility = _centerText.DesiredSize.Height <= BarHeight - 8 && room > 0
            ? Visibility.Visible
            : Visibility.Hidden;
    }

    private void HideSaveChoice()
    {
        _saveBar.Visibility = Visibility.Collapsed;
        _toolBar.Visibility = Visibility.Visible;
    }

    /// <summary>A date still being typed is part of the work: it goes in as typed.</summary>
    private void RequestSave(bool overwrite)
    {
        _commitDateEdit?.Invoke();
        SaveRequested?.Invoke(_placements.ToArray(), overwrite);
    }

    // ------------------------------------------------------------------
    // Tools and placement.
    // ------------------------------------------------------------------

    private void ArmTool(SignPlacementKind kind)
    {
        // The Signature pill opens the picker: choosing, creating, and
        // deleting saved signatures all live there.
        if (kind == SignPlacementKind.Signature)
        {
            ToggleSignaturePicker();
            return;
        }

        // Clicking the armed tool again quietly puts it down.
        if (_armedTool == kind)
        {
            DisarmTool();
            return;
        }

        if (kind == SignPlacementKind.Stamp && !SignatureStore.HasStamp && !TryImportStampInteractively())
        {
            return;
        }

        SetArmedTool(kind);
    }

    private void ArmSignature(string signaturePath)
    {
        _selectedSignaturePath = signaturePath;
        CloseSignaturePicker();
        SetArmedTool(SignPlacementKind.Signature);
    }

    private void DisarmTool() => SetArmedTool(null);

    private void SetArmedTool(SignPlacementKind? kind)
    {
        _armedTool = kind;
        _placementLayer.Cursor = kind is null ? Cursors.Arrow : Cursors.Cross;
        foreach (var refresh in _toolRefreshers)
        {
            refresh();
        }

        // Replacing the stored stamp is offered exactly when the user is
        // holding that tool, and nowhere else.
        _newStampPill.Visibility = kind == SignPlacementKind.Stamp && SignatureStore.HasStamp
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ReplaceStamp()
    {
        if (TryImportStampInteractively())
        {
            SetArmedTool(SignPlacementKind.Stamp);
        }
    }

    private bool TryImportStampInteractively()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a stamp image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
        };
        return picker.ShowDialog(Window.GetWindow(this)) == true && SignatureStore.TryImportStamp(picker.FileName);
    }

    private void OnPageClick(object sender, MouseButtonEventArgs e)
    {
        Select(null);
        if (_armedTool is not { } tool)
        {
            return;
        }

        var at = e.GetPosition(_placementLayer);
        DisarmTool();
        Place(tool, at.X / _pageWidth, at.Y / _pageHeight);
        e.Handled = true;
    }

    /// <summary>
    /// Places the tool's item centered at a normalized point on the current
    /// page and selects it. False when there is nothing to place (no saved
    /// signature or stamp).
    /// </summary>
    internal bool Place(SignPlacementKind kind, double x, double y)
    {
        if (BuildDefaultPlacement(kind, x, y) is not { } placement)
        {
            return false;
        }

        _placements.Add(placement);
        AddPlacementVisual(placement);
        UpdateWorkState();
        OnWorkChanged();
        return true;
    }

    private SignPlacement? BuildDefaultPlacement(SignPlacementKind kind, double x, double y)
    {
        if (kind == SignPlacementKind.Date)
        {
            var date = new SignPlacement(kind, _pageIndex, x, y - DateHeight / 2, 0, DateHeight, TodayText());
            return date.FittedToText(_pageHeight / _pageWidth);
        }

        // Snapshot the pixels now: the placement must survive the stored
        // file being replaced or removed afterwards.
        var assetPath = kind == SignPlacementKind.Signature
            ? _selectedSignaturePath ?? SignatureStore.ListSignatures().FirstOrDefault()
            : SignatureStore.StampPath;
        SignInkSource? source = null;
        var png = assetPath is null ? null : SignatureStore.TryReadForPlacement(assetPath, out source);
        if (png is null || SignatureStore.TryLoad(png) is not { } image)
        {
            return null;
        }

        var width = kind == SignPlacementKind.Signature ? 0.24 : 0.18;
        var height = width * (_pageWidth / _pageHeight) * (image.PixelHeight / (double)image.PixelWidth);
        return new SignPlacement(kind, _pageIndex, x - width / 2, y - height / 2, width, height, null, png, source).Clamped();
    }

    /// <summary>Today in the user's short date form, kept to what the file can carry.</summary>
    private static string TodayText()
    {
        var local = SignText.Writable(DateTime.Now.ToString("d", CultureInfo.CurrentCulture)).Trim();
        return local.Length > 0 ? local : DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private void AddPlacementVisual(SignPlacement placement, bool select = true)
    {
        FrameworkElement inner;
        if (placement.Kind == SignPlacementKind.Date)
        {
            // Arial on screen, Helvetica in the file: the same widths, so the
            // frame sized from the text fits both.
            inner = new TextBlock
            {
                Text = placement.Text,
                Foreground = Brushes.Black,
                FontFamily = new FontFamily("Arial"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
        else if (placement.ImagePng is { } png && SignatureStore.TryLoad(png) is { } source)
        {
            inner = new Image { Source = source, Stretch = Stretch.Fill };
        }
        else
        {
            _placements.Remove(placement);
            return;
        }

        var frame = new Grid { Tag = placement, Background = Brushes.Transparent, Children = { inner } };
        frame.MouseEnter += (_, _) =>
        {
            _hoveredFrame = frame;
            UpdateChrome();
        };
        frame.MouseLeave += (_, _) =>
        {
            if (ReferenceEquals(_hoveredFrame, frame))
            {
                _hoveredFrame = null;
                UpdateChrome();
            }
        };

        WirePlacementInteraction(frame, inner);
        ApplyPlacementBounds(frame);
        _placementLayer.Children.Add(frame);

        // What you just placed is what Delete deletes: no second click
        // needed to make it real. Rebuilding a page on navigation is not
        // placing, so it selects nothing.
        if (select)
        {
            Select(frame);
        }
    }

    private void WirePlacementInteraction(Grid frame, FrameworkElement inner)
    {
        var dragging = false;
        var grabOffset = default(Point);

        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && frame.Tag is SignPlacement { Kind: SignPlacementKind.Date })
            {
                BeginDateEdit(frame, inner);
                e.Handled = true;
                return;
            }

            // The same direct manipulation dates get: double-click opens
            // this placement's ink on the pad, editable, not erased.
            if (e.ClickCount == 2
                && frame.Tag is SignPlacement { Kind: SignPlacementKind.Signature, ImagePng: { } png }
                && SignatureStore.TryLoad(png) is { } placedInk)
            {
                ShowInkCapture(placedInk, frame);
                e.Handled = true;
                return;
            }

            Select(frame);
            dragging = frame.CaptureMouse();
            grabOffset = e.GetPosition(frame);
            e.Handled = true;
        };
        frame.MouseMove += (_, e) =>
        {
            if (!dragging || frame.Tag is not SignPlacement placement)
            {
                return;
            }

            var at = e.GetPosition(_placementLayer);
            ReplacePlacement(frame, (placement with
            {
                X = (at.X - grabOffset.X) / _pageWidth,
                Y = (at.Y - grabOffset.Y) / _pageHeight
            }).Clamped());
        };
        frame.MouseLeftButtonUp += (_, _) =>
        {
            dragging = false;
            frame.ReleaseMouseCapture();
        };

        // The quick resize: the wheel over an element scales it in place.
        // The corner handle does the same for pens and touch.
        frame.MouseWheel += (_, e) =>
        {
            if (frame.Tag is SignPlacement placement)
            {
                var factor = e.Delta > 0 ? 1.06 : 1 / 1.06;
                ReplacePlacement(frame, Scaled(placement, placement.Width * factor, keepCenter: true));
                e.Handled = true;
            }
        };
    }

    /// <summary>The placement at a new width, aspect kept, so text still fits its frame.</summary>
    private static SignPlacement Scaled(SignPlacement placement, double width, bool keepCenter)
    {
        width = Math.Clamp(width, 0.03, 0.9);
        var height = placement.Height * (width / placement.Width);
        if (height > 1)
        {
            width *= 1 / height;
            height = 1;
        }

        return (keepCenter
            ? placement with
            {
                X = placement.X + (placement.Width - width) / 2,
                Y = placement.Y + (placement.Height - height) / 2,
                Width = width,
                Height = height
            }
            : placement with { Width = width, Height = height }).Clamped();
    }

    private void BeginDateEdit(Grid frame, FrameworkElement inner)
    {
        if (inner is not TextBlock label || frame.Tag is not SignPlacement || _commitDateEdit is not null)
        {
            return;
        }

        // Dressed to the page, not the system: white paper, black ink, and
        // an accent hairline standing in for the selection outline.
        var editor = new TextBox
        {
            Text = label.Text,
            FontSize = label.FontSize,
            FontFamily = label.FontFamily,
            Background = Brushes.White,
            Foreground = Brushes.Black,
            CaretBrush = Brushes.Black,
            BorderBrush = Theme.Brush(Theme.Current.Accent),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            FocusVisualStyle = null,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        // Only what the file can print may be typed, so screen and file
        // always show the same text.
        editor.TextChanged += (_, _) =>
        {
            var writable = SignText.Writable(editor.Text);
            if (writable != editor.Text)
            {
                var caret = Math.Max(0, editor.CaretIndex - (editor.Text.Length - writable.Length));
                editor.Text = writable;
                editor.CaretIndex = Math.Min(caret, writable.Length);
            }
        };

        void Commit()
        {
            _commitDateEdit = null;
            var current = (SignPlacement)frame.Tag;
            var text = editor.Text.Trim();
            var updated = (current with { Text = text.Length > 0 ? text : current.Text }).FittedToText(_pageHeight / _pageWidth);
            label.Text = updated.Text;
            frame.Children.Remove(editor);
            label.Visibility = Visibility.Visible;
            ReplacePlacement(frame, updated);
        }

        _commitDateEdit = Commit;
        editor.LostKeyboardFocus += (_, _) => _commitDateEdit?.Invoke();
        editor.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                _commitDateEdit?.Invoke();
                e.Handled = true;
            }
        };

        label.Visibility = Visibility.Hidden;
        frame.Children.Insert(1, editor);
        editor.Focus();
        editor.SelectAll();
    }

    private void ReplacePlacement(Grid frame, SignPlacement updated)
    {
        var index = _placements.IndexOf((SignPlacement)frame.Tag);
        if (index >= 0)
        {
            _placements[index] = updated;
        }

        frame.Tag = updated;
        ApplyPlacementBounds(frame);
        UpdateChrome();
        OnWorkChanged();
    }

    private void ApplyPlacementBounds(Grid frame)
    {
        if (frame.Tag is not SignPlacement placement)
        {
            return;
        }

        frame.Width = placement.Width * _pageWidth;
        frame.Height = placement.Height * _pageHeight;
        Canvas.SetLeft(frame, placement.X * _pageWidth);
        Canvas.SetTop(frame, placement.Y * _pageHeight);
        if (placement.Kind == SignPlacementKind.Date && frame.Children[0] is TextBlock label)
        {
            label.FontSize = frame.Height * SignText.FontScale;
        }
    }

    private void RemovePlacementFrame(Grid frame)
    {
        _placements.Remove((SignPlacement)frame.Tag);
        _placementLayer.Children.Remove(frame);
        if (ReferenceEquals(_selectedFrame, frame))
        {
            _selectedFrame = null;
        }

        if (ReferenceEquals(_hoveredFrame, frame))
        {
            _hoveredFrame = null;
        }

        UpdateChrome();
        UpdateWorkState();
        OnWorkChanged();
    }

    private void Select(Grid? frame)
    {
        _selectedFrame = frame;
        UpdateChrome();
        if (frame is not null)
        {
            Focus();
        }
    }

    // ------------------------------------------------------------------
    // Screen-space chrome: drawn over the scaled page at fixed size.
    // ------------------------------------------------------------------

    private static Rectangle BuildOutline(ThemePalette palette) => new()
    {
        Stroke = Theme.Brush(palette.Accent),
        StrokeThickness = 1,
        StrokeDashArray = [4, 3],
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };

    private Border BuildDeleteChip(ThemePalette palette) => BuildRemoveChip(palette, ChipSize, "Remove", () =>
    {
        if (_selectedFrame is { } selected)
        {
            RemovePlacementFrame(selected);
        }
    });

    /// <summary>The round x that removes a placement or a saved signature.</summary>
    private static Border BuildRemoveChip(ThemePalette palette, double size, string name, Action onClick)
    {
        var glyph = new TextBlock
        {
            Text = "\uE711",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 9,
            Foreground = Theme.Brush(palette.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var chip = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = name,
            Visibility = Visibility.Collapsed,
            Child = glyph
        };
        AutomationProperties.SetName(chip, name);
        chip.MouseEnter += (_, _) => glyph.Foreground = Theme.Brush(palette.TextPrimary);
        chip.MouseLeave += (_, _) => glyph.Foreground = Theme.Brush(palette.TextSecondary);
        chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };
        return chip;
    }

    private Border BuildResizeHandle(ThemePalette palette)
    {
        var handle = new Border
        {
            Width = HandleSize,
            Height = HandleSize,
            CornerRadius = new CornerRadius(HandleSize / 2),
            Background = Theme.Brush(palette.Accent),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Cursor = Cursors.SizeNWSE,
            ToolTip = "Drag to resize",
            Visibility = Visibility.Collapsed
        };
        var resizing = false;
        handle.MouseLeftButtonDown += (_, e) =>
        {
            resizing = handle.CaptureMouse();
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (resizing && _selectedFrame is { Tag: SignPlacement placement } frame)
            {
                var right = e.GetPosition(_placementLayer).X / _pageWidth;
                ReplacePlacement(frame, Scaled(placement, right - placement.X, keepCenter: false));
            }
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            resizing = false;
            handle.ReleaseMouseCapture();
            e.Handled = true;
        };
        return handle;
    }

    private void UpdateChrome()
    {
        var hovered = ReferenceEquals(_hoveredFrame, _selectedFrame) ? null : ScreenBounds(_hoveredFrame);
        PlaceOutline(_hoverOutline, hovered);

        var selected = ScreenBounds(_selectedFrame);
        PlaceOutline(_selectionOutline, selected);
        var visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        _deleteChip.Visibility = visibility;
        _resizeHandle.Visibility = visibility;
        if (selected is { } bounds)
        {
            // The chip floats just above the corner: a real target that never
            // covers the placed ink.
            Canvas.SetLeft(_deleteChip, bounds.Right - ChipSize + 2);
            Canvas.SetTop(_deleteChip, bounds.Top - ChipSize - 6);
            Canvas.SetLeft(_resizeHandle, bounds.Right - HandleSize / 2);
            Canvas.SetTop(_resizeHandle, bounds.Bottom - HandleSize / 2);
        }
    }

    private static void PlaceOutline(Rectangle outline, Rect? bounds)
    {
        if (bounds is not { } rect)
        {
            outline.Visibility = Visibility.Collapsed;
            return;
        }

        rect.Inflate(1, 1);
        outline.Width = rect.Width;
        outline.Height = rect.Height;
        Canvas.SetLeft(outline, rect.X);
        Canvas.SetTop(outline, rect.Y);
        outline.Visibility = Visibility.Visible;
    }

    private Rect? ScreenBounds(Grid? frame)
    {
        if (frame?.Tag is not SignPlacement placement || !_placementLayer.Children.Contains(frame))
        {
            return null;
        }

        try
        {
            var pageRect = new Rect(
                placement.X * _pageWidth,
                placement.Y * _pageHeight,
                placement.Width * _pageWidth,
                placement.Height * _pageHeight);
            return _pageHost.TransformToVisual(_chromeLayer).TransformBounds(pageRect);
        }
        catch (InvalidOperationException)
        {
            // Not laid out yet; the next scroll or layout pass places it.
            return null;
        }
    }

    // ------------------------------------------------------------------
    // The signature picker: a small library under the Signature pill. Pick
    // one to place it, hover a row to delete it, or draw a new one. An
    // empty library skips straight to the pad. It is an in-window flyout,
    // not a Popup: one visual tree, one hwnd.
    // ------------------------------------------------------------------

    private void ToggleSignaturePicker()
    {
        if (_signatureFlyout is not null)
        {
            CloseSignaturePicker();
            return;
        }

        if (SignatureStore.ListSignatures().Count == 0)
        {
            ShowInkCapture();
            return;
        }

        var palette = Theme.Current;
        _signatureFlyout = new Border
        {
            Background = Theme.Brush(palette.ChromeBg),
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = Metrics.MenuCorner,
            Padding = Metrics.MenuPadding,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(SignaturePillLeft(), Metrics.FlyoutGap, 0, 0),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black },
            Child = BuildSignaturePickerColumn()
        };
        _pageArea.Children.Add(_signatureFlyout);
    }

    private double SignaturePillLeft()
    {
        if (_signaturePill.IsLoaded)
        {
            try
            {
                return Math.Max(8, _signaturePill.TranslatePoint(new Point(0, 0), _pageArea).X);
            }
            catch (InvalidOperationException)
            {
                // Fall through to the toolbar's own inset.
            }
        }

        return 12;
    }

    private void CloseSignaturePicker()
    {
        if (_signatureFlyout is { } flyout)
        {
            _pageArea.Children.Remove(flyout);
            _signatureFlyout = null;
        }
    }

    private void RefreshSignaturePicker()
    {
        if (_signatureFlyout is not { } flyout)
        {
            return;
        }

        if (SignatureStore.ListSignatures().Count == 0)
        {
            CloseSignaturePicker();
            return;
        }

        flyout.Child = BuildSignaturePickerColumn();
    }

    private StackPanel BuildSignaturePickerColumn()
    {
        var palette = Theme.Current;
        var column = new StackPanel { Width = 220 };
        foreach (var path in SignatureStore.ListSignatures())
        {
            column.Children.Add(BuildSignatureRow(palette, path));
        }

        var newPill = HousePill.Build("New signature", ShowInkCapture, PillRole.Ghost);
        newPill.Margin = new Thickness(0, 2, 0, 2);
        newPill.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(newPill);
        return column;
    }

    /// <summary>
    /// One saved signature. Removing it takes two clicks on the row itself,
    /// the second on a named button, because a signature is work to redraw.
    /// </summary>
    private Border BuildSignatureRow(ThemePalette palette, string path)
    {
        var row = new Grid
        {
            Children =
            {
                new Border
                {
                    Background = Brushes.White,
                    CornerRadius = Metrics.RowCorner,
                    Height = 44,
                    Child = new Image
                    {
                        Source = SignatureStore.TryLoad(path),
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(8, 4, 8, 4)
                    }
                }
            }
        };

        var selected = string.Equals(path, _selectedSignaturePath, StringComparison.OrdinalIgnoreCase)
            && _armedTool == SignPlacementKind.Signature;
        var frame = new Border
        {
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = Metrics.RowCorner,
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Brush(selected ? palette.Accent : palette.RaisedBorder),
            Cursor = Cursors.Hand,
            Child = row
        };

        var confirm = new Border
        {
            Background = Theme.Brush(palette.ChromeBg),
            CornerRadius = Metrics.RowCorner,
            Visibility = Visibility.Collapsed
        };
        var delete = HousePill.Build("Delete", () => SignatureStore.Remove(path));
        delete.Margin = new Thickness(0);
        confirm.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { HousePill.Build("Keep", () => confirm.Visibility = Visibility.Collapsed, PillRole.Ghost), delete }
        };

        var remove = BuildRemoveChip(palette, 20, "Delete this signature", () => confirm.Visibility = Visibility.Visible);
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        remove.VerticalAlignment = VerticalAlignment.Top;
        remove.Margin = new Thickness(0, 2, 2, 0);
        row.Children.Add(remove);
        row.Children.Add(confirm);

        frame.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        frame.MouseLeave += (_, _) =>
        {
            remove.Visibility = Visibility.Collapsed;
            confirm.Visibility = Visibility.Collapsed;
        };
        frame.MouseLeftButtonUp += (_, e) =>
        {
            if (confirm.Visibility != Visibility.Visible)
            {
                ArmSignature(path);
            }

            e.Handled = true;
        };
        return frame;
    }

    /// <summary>
    /// The saved ink changed, in this picker or in Settings. The picker lists
    /// what is there now, and a tool holding ink that is gone is put down
    /// instead of placing nothing. Placed ink carries its own pixels, so
    /// deleting only changes what can be placed next.
    /// </summary>
    private void OnStoreChanged()
    {
        if (_selectedSignaturePath is { } selected && !System.IO.File.Exists(selected))
        {
            _selectedSignaturePath = null;
            if (_armedTool == SignPlacementKind.Signature)
            {
                DisarmTool();
            }
        }

        if (_armedTool == SignPlacementKind.Stamp && !SignatureStore.HasStamp)
        {
            DisarmTool();
        }

        RefreshSignaturePicker();
    }

    private static bool IsWithin(DependencyObject node, DependencyObject root)
    {
        for (var current = node; current is not null;)
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Ink capture: drawing the signature happens right here on the stage,
    // never in a separate dialog.
    // ------------------------------------------------------------------

    private Grid BuildInkOverlay(ThemePalette palette)
    {
        _inkTitle = new TextBlock
        {
            FontSize = Metrics.FontTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var use = HousePill.Build("Use it", CommitInk, PillRole.Accent);
        use.Margin = new Thickness(0);
        var card = new Border
        {
            Background = Theme.Brush(palette.ChromeBg),
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = Metrics.CardCorner,
            Padding = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Width = 460,
                Children =
                {
                    _inkTitle,
                    new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = Theme.Brush(palette.RaisedBorder),
                        BorderThickness = new Thickness(1),
                        CornerRadius = Metrics.SurfaceCorner,
                        Height = 172,
                        Child = _inkCanvas
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children =
                        {
                            HousePill.Build("Back", HideInkCapture, PillRole.Ghost),
                            HousePill.Build("Clear", ClearInk),
                            use
                        }
                    }
                }
            }
        };

        _inkCanvas.MouseLeftButtonDown += (_, e) =>
        {
            _inkCanvas.CaptureMouse();
            var stroke = new List<Point> { ClampToPad(e.GetPosition(_inkCanvas)) };
            _inkStrokes.Add(stroke);
            _activeInkVisual = new Polyline
            {
                Stroke = Brushes.Black,
                StrokeThickness = 2.2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _activeInkVisual.Points.Add(stroke[0]);
            _inkVisuals.Add(_activeInkVisual);
            _inkCanvas.Children.Add(_activeInkVisual);
        };
        _inkCanvas.MouseMove += (_, e) =>
        {
            if (_activeInkVisual is null || e.LeftButton != MouseButtonState.Pressed || _inkStrokes.Count == 0)
            {
                return;
            }

            // A drag past the card's edge slides along it, the way a real
            // signature pad behaves; ink cannot exist outside the pad.
            var point = ClampToPad(e.GetPosition(_inkCanvas));
            _inkStrokes[^1].Add(point);
            _activeInkVisual.Points.Add(point);
        };
        _inkCanvas.MouseLeftButtonUp += (_, _) =>
        {
            _activeInkVisual = null;
            _inkCanvas.ReleaseMouseCapture();
        };

        return new Grid
        {
            Background = Theme.Brush(Theme.OverlayScrim),
            Visibility = Visibility.Collapsed,
            Children = { card }
        };
    }

    private void ShowInkCapture() => ShowInkCapture(baseImage: null, editingFrame: null);

    /// <summary>
    /// One card, two intents: a blank pad draws a new saved signature; a
    /// pad opened from a placed signature shows that ink as the editable
    /// starting point and commits back to the placement alone.
    /// </summary>
    private void ShowInkCapture(BitmapSource? baseImage, Grid? editingFrame)
    {
        CloseSignaturePicker();
        ClearInk();
        _inkBaseImage = baseImage;
        _inkEditingFrame = editingFrame;
        _inkTitle.Text = editingFrame is not null ? "Edit this signature" : "Draw your signature";
        _inkOverlay.Visibility = Visibility.Visible;
        if (baseImage is not null)
        {
            // The pad has no size until the overlay lays out.
            Dispatcher.BeginInvoke(new Action(PlaceInkBase), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Fits the existing ink into the pad as the editable base.</summary>
    private void PlaceInkBase()
    {
        if (_inkBaseImage is not { } image || _inkOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        var padWidth = _inkCanvas.ActualWidth;
        var padHeight = _inkCanvas.ActualHeight;
        if (padWidth < 1 || padHeight < 1)
        {
            return;
        }

        const double margin = 12;
        var scale = Math.Min(
            (padWidth - margin * 2) / image.PixelWidth,
            (padHeight - margin * 2) / image.PixelHeight);
        var width = image.PixelWidth * scale;
        var height = image.PixelHeight * scale;
        _inkBaseRect = new Rect((padWidth - width) / 2, (padHeight - height) / 2, width, height);

        _inkBaseVisual = new Image
        {
            Source = image,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_inkBaseVisual, _inkBaseRect.X);
        Canvas.SetTop(_inkBaseVisual, _inkBaseRect.Y);
        _inkCanvas.Children.Insert(0, _inkBaseVisual);
    }

    private void HideInkCapture()
    {
        _inkOverlay.Visibility = Visibility.Collapsed;
        _inkEditingFrame = null;
    }

    private void ClearInk()
    {
        _inkStrokes.Clear();
        foreach (var visual in _inkVisuals)
        {
            _inkCanvas.Children.Remove(visual);
        }

        _inkVisuals.Clear();
        _activeInkVisual = null;

        if (_inkBaseVisual is { } baseVisual)
        {
            _inkCanvas.Children.Remove(baseVisual);
            _inkBaseVisual = null;
        }

        _inkBaseImage = null;
        _inkBaseRect = Rect.Empty;
    }

    private Point ClampToPad(Point point) => new(
        Math.Clamp(point.X, 0, Math.Max(0, _inkCanvas.ActualWidth)),
        Math.Clamp(point.Y, 0, Math.Max(0, _inkCanvas.ActualHeight)));

    private void CommitInk()
    {
        var strokes = _inkStrokes.ConvertAll(s => (IReadOnlyList<Point>)s);
        if (_inkEditingFrame is { } frame)
        {
            var edited = SignatureStore.RenderComposite(_inkBaseImage, _inkBaseRect, strokes, Colors.Black);
            if (edited is not null)
            {
                UpdatePlacementImage(frame, edited);
                HideInkCapture();
            }

            return;
        }

        if (SignatureStore.SaveSignatureFromStrokes(strokes, Colors.Black) is { } saved)
        {
            HideInkCapture();
            ArmSignature(saved);
        }
    }

    /// <summary>Commits edited ink back into one placed signature.</summary>
    private void UpdatePlacementImage(Grid frame, BitmapSource edited)
    {
        if (frame.Tag is not SignPlacement placement || SignatureStore.EncodePng(edited) is not { } png)
        {
            return;
        }

        // Keep the placement's width and center; the height follows the
        // edited ink's aspect.
        var height = placement.Width * (_pageWidth / _pageHeight) * (edited.PixelHeight / (double)edited.PixelWidth);
        var updated = (placement with
        {
            ImagePng = png,
            Source = placement.Source is { } source ? source with { Redrawn = true } : null,
            Height = height,
            Y = placement.Y + placement.Height / 2 - height / 2
        }).Clamped();

        if (frame.Children[0] is Image image)
        {
            image.Source = SignatureStore.TryLoad(png);
        }

        ReplacePlacement(frame, updated);
    }
}
