using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// Sign mode's whole world: the current page rendered on our own WPF
/// surface, so placement happens in coordinates we own instead of inside
/// the closed embedded viewer. Everything here is display-space; the PDF
/// writer receives normalized top-left rectangles and owns the rest.
/// </summary>
internal sealed class FillSignSurface : Grid
{
    private readonly string _documentPath;
    private readonly SignSession _session = new();
    private readonly Grid _pageHost;
    private readonly Image _pageImage;
    private readonly Canvas _placementLayer;
    private readonly Grid _toolBar;
    private readonly Grid _saveBar;
    private readonly Grid _inkOverlay;
    private readonly Canvas _inkCanvas;
    private readonly List<List<Point>> _inkStrokes = [];
    private readonly List<Polyline> _inkVisuals = [];
    private readonly int _totalPages;
    private double _pageWidth;
    private double _pageHeight;
    private readonly List<Action> _pillRefreshers = [];
    private SignPlacementKind? _armedTool;
    private Grid? _selectedFrame;
    private readonly Dictionary<Grid, Action<bool>> _frameChrome = [];
    private Polyline? _activeInkVisual;
    private TextBlock? _pageLabel;
    private Border? _previousPageChip;
    private Border? _nextPageChip;
    private Border? _signaturePill;
    private Border? _newStampPill;
    private TextBlock? _inkTitle;
    private StackPanel? _saveChoiceRow;
    private TextBlock? _saveStatusLabel;
    private Grid? _flyoutHost;
    private Border? _signatureFlyout;
    private string? _selectedSignaturePath;
    private BitmapSource? _inkBaseImage;
    private Rect _inkBaseRect = Rect.Empty;
    private Image? _inkBaseVisual;
    private Grid? _inkEditingFrame;

    public event Action? ExitRequested;

    /// <summary>True asks the writer to replace the original file.</summary>
    public event Action<SignSession, bool>? SaveRequested;

    /// <summary>Asks the host to render another page into this surface.</summary>
    public event Action<int>? PageChangeRequested;

    public FillSignSurface(string documentPath, int pageIndex, int totalPages, PdfPreviewPage page)
    {
        _documentPath = documentPath;
        _session.PageIndex = pageIndex;
        _totalPages = Math.Max(totalPages, pageIndex + 1);
        _pageWidth = page.Width;
        _pageHeight = page.Height;

        var palette = Theme.Current;
        Background = Theme.Brush(palette.CanvasBg);
        Focusable = true;
        FocusVisualStyle = null;
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Delete or Key.Back && _selectedFrame is { } selected)
            {
                RemovePlacementFrame(selected);
                e.Handled = true;
            }
        };
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // The page, in its own pixel coordinates; the Viewbox scales the
        // image and the placement layer together so normalized math never
        // needs the display size.
        _placementLayer = new Canvas
        {
            Width = page.Width,
            Height = page.Height,
            Background = Brushes.Transparent
        };
        _placementLayer.MouseLeftButtonDown += OnPageClick;

        _inkCanvas = new Canvas { Background = Brushes.White, ClipToBounds = true };
        _inkOverlay = BuildInkOverlay(palette);

        _pageHost = new Grid { Width = page.Width, Height = page.Height };
        _pageImage = new Image
        {
            Source = page.Bitmap,
            Width = page.Width,
            Height = page.Height,
            SnapsToDevicePixels = true
        };
        _pageHost.Children.Add(_pageImage);
        _pageHost.Children.Add(_placementLayer);

        var viewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(18),
            Child = _pageHost
        };

        _toolBar = BuildToolBar(palette, pageIndex, totalPages);
        _saveBar = BuildSaveBar(palette);
        _saveBar.Visibility = Visibility.Collapsed;

        var barHost = new Grid();
        barHost.Children.Add(_toolBar);
        barHost.Children.Add(_saveBar);
        Children.Add(barHost);

        var pageArea = new Grid();
        pageArea.Children.Add(viewbox);
        pageArea.Children.Add(_inkOverlay);
        SetRow(pageArea, 1);
        Children.Add(pageArea);
        _flyoutHost = pageArea;

        // The picker dismisses like any flyout: a press anywhere outside it
        // (except its own pill, which toggles) puts it away.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_signatureFlyout is not { } flyout)
            {
                return;
            }

            if (e.OriginalSource is DependencyObject source
                && (IsWithin(source, flyout) || (_signaturePill is { } pill && IsWithin(source, pill))))
            {
                return;
            }

            CloseSignaturePicker();
        };
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

    public bool HasWork => _session.HasWork;

    /// <summary>
    /// Escape walks back one layer at a time: save choice to editing, ink
    /// capture to editing, then out - but never past unsaved work without
    /// offering the save choice first.
    /// </summary>
    public void HandleEscape()
    {
        if (_signatureFlyout is not null)
        {
            CloseSignaturePicker();
            return;
        }

        if (_saveBar.Visibility == Visibility.Visible)
        {
            HideSaveChoice();
            return;
        }

        if (_inkOverlay.Visibility == Visibility.Visible)
        {
            HideInkCapture();
            return;
        }

        if (_session.HasWork)
        {
            ShowSaveChoice();
            return;
        }

        ExitRequested?.Invoke();
    }

    /// <summary>
    /// While the writer runs, nothing here accepts input, and the save bar
    /// says so: replacing the original can retry for a few seconds, and
    /// silent numbness reads as a hang.
    /// </summary>
    public void SetSaving(bool saving)
    {
        _toolBar.IsEnabled = !saving;
        _saveBar.IsEnabled = !saving;
        _placementLayer.IsEnabled = !saving;
        if (_saveChoiceRow is not null && _saveStatusLabel is not null)
        {
            _saveChoiceRow.Visibility = saving ? Visibility.Collapsed : Visibility.Visible;
            _saveStatusLabel.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------
    // Chrome.
    // ------------------------------------------------------------------

    private Grid BuildToolBar(ThemePalette palette, int pageIndex, int totalPages)
    {
        var bar = new Grid { Background = Theme.Brush(palette.ChromeBg) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        _signaturePill = BuildPill("Signature", () => ArmTool(SignPlacementKind.Signature),
            isSelected: () => _armedTool == SignPlacementKind.Signature);
        tools.Children.Add(_signaturePill);
        tools.Children.Add(BuildPill("Date", () => ArmTool(SignPlacementKind.Date),
            isSelected: () => _armedTool == SignPlacementKind.Date));
        tools.Children.Add(BuildPill("Stamp", () => ArmTool(SignPlacementKind.Stamp),
            isSelected: () => _armedTool == SignPlacementKind.Stamp));

        // Replacing the stored stamp is offered exactly when the user is
        // holding that tool, and nowhere else. Signatures manage themselves
        // in the picker instead.
        _newStampPill = BuildPill("New stamp", ReplaceStamp, PillRole.Ghost);
        _newStampPill.Visibility = Visibility.Collapsed;
        tools.Children.Add(_newStampPill);
        bar.Children.Add(tools);

        _pageLabel = new TextBlock
        {
            Text = $"Page {pageIndex + 1} of {totalPages}",
            Foreground = Theme.Brush(palette.TextSecondary),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };

        // Moving between pages is part of signing, so the controls live
        // right on the readout; single-page documents show no arrows at all.
        var pageCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 14, 0)
        };
        _previousPageChip = BuildPageChip(palette, "", () => RequestPage(_session.PageIndex - 1));
        _nextPageChip = BuildPageChip(palette, "", () => RequestPage(_session.PageIndex + 1));
        pageCluster.Children.Add(_previousPageChip);
        pageCluster.Children.Add(_pageLabel);
        pageCluster.Children.Add(_nextPageChip);
        SetColumn(pageCluster, 1);
        bar.Children.Add(pageCluster);
        UpdatePageChrome();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        actions.Children.Add(BuildPill("Cancel", () => ExitRequested?.Invoke(), PillRole.Ghost));
        actions.Children.Add(BuildPill("Save", ShowSaveChoice, PillRole.Accent));
        SetColumn(actions, 2);
        bar.Children.Add(actions);

        var border = new Border
        {
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        bar.Children.Add(border);
        return bar;
    }

    private Grid BuildSaveBar(ThemePalette palette)
    {
        var bar = new Grid { Background = Theme.Brush(palette.ChromeBg) };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(BuildPill("Save a signed copy", () => RequestSave(overwrite: false), PillRole.Accent));
        row.Children.Add(BuildPill("Replace the original", () => RequestSave(overwrite: true)));
        row.Children.Add(BuildPill("Keep editing", HideSaveChoice, PillRole.Ghost));
        _saveChoiceRow = row;
        bar.Children.Add(row);

        _saveStatusLabel = new TextBlock
        {
            Text = "Saving…",
            FontSize = Metrics.FontLabel,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        bar.Children.Add(_saveStatusLabel);
        return bar;
    }

    /// <summary>
    /// The house pill (see <see cref="HousePill"/>), with its refresher
    /// enrolled so armed-tool state can repaint every pill at once.
    /// </summary>
    private Border BuildPill(string label, Action onClick, PillRole role = PillRole.Surface, Func<bool>? isSelected = null)
    {
        var pill = HousePill.Build(label, onClick, out var refresh, role, isSelected);
        _pillRefreshers.Add(refresh);
        return pill;
    }

    /// <summary>A quiet 22px icon chip for stepping between pages.</summary>
    private Border BuildPageChip(ThemePalette palette, string glyph, Action onClick)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = Theme.Brush(palette.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var chip = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = Metrics.IconCorner,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon
        };
        chip.MouseEnter += (_, _) => chip.Background = Theme.Brush(palette.HoverBg);
        chip.MouseLeave += (_, _) => chip.Background = Brushes.Transparent;
        chip.MouseLeftButtonDown += (_, e) =>
        {
            chip.Background = Theme.Brush(palette.PressedBg);
            e.Handled = true;
        };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            chip.Background = Brushes.Transparent;
            onClick();
            e.Handled = true;
        };
        return chip;
    }

    private void RequestPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _totalPages || pageIndex == _session.PageIndex)
        {
            return;
        }

        PageChangeRequested?.Invoke(pageIndex);
    }

    /// <summary>
    /// Swaps the rendered page under the same session: placements on other
    /// pages stay in the working set and come back when their page does, so
    /// one save carries everything.
    /// </summary>
    public void ShowPage(int pageIndex, PdfPreviewPage page)
    {
        SelectFrame(null);
        _session.PageIndex = pageIndex;
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
        _frameChrome.Clear();
        _selectedFrame = null;
        foreach (var placement in _session.Placements)
        {
            if (placement.PageIndex == pageIndex)
            {
                AddPlacementVisual(placement, select: false);
            }
        }

        UpdatePageChrome();
    }

    private void UpdatePageChrome()
    {
        if (_pageLabel is null || _previousPageChip is null || _nextPageChip is null)
        {
            return;
        }

        _pageLabel.Text = $"Page {_session.PageIndex + 1} of {_totalPages}";
        var navigable = _totalPages > 1;
        _previousPageChip.Visibility = navigable ? Visibility.Visible : Visibility.Collapsed;
        _nextPageChip.Visibility = navigable ? Visibility.Visible : Visibility.Collapsed;
        SetChipEnabled(_previousPageChip, _session.PageIndex > 0);
        SetChipEnabled(_nextPageChip, _session.PageIndex < _totalPages - 1);
    }

    private static void SetChipEnabled(Border chip, bool enabled)
    {
        chip.IsEnabled = enabled;
        chip.Opacity = enabled ? 1 : 0.35;
    }

    private void ShowSaveChoice()
    {
        if (!_session.HasWork)
        {
            ExitRequested?.Invoke();
            return;
        }

        _toolBar.Visibility = Visibility.Collapsed;
        _saveBar.Visibility = Visibility.Visible;
    }

    private void HideSaveChoice()
    {
        _saveBar.Visibility = Visibility.Collapsed;
        _toolBar.Visibility = Visibility.Visible;
    }

    private void RequestSave(bool overwrite) => SaveRequested?.Invoke(_session, overwrite);

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

        _armedTool = kind;
        _placementLayer.Cursor = Cursors.Cross;
        RefreshPills();
        UpdateContextualPills();
    }

    private void ArmSignature(string signaturePath)
    {
        _selectedSignaturePath = signaturePath;
        _armedTool = SignPlacementKind.Signature;
        _placementLayer.Cursor = Cursors.Cross;
        RefreshPills();
        UpdateContextualPills();
        CloseSignaturePicker();
    }

    private void DisarmTool()
    {
        _armedTool = null;
        _placementLayer.Cursor = Cursors.Arrow;
        RefreshPills();
        UpdateContextualPills();
    }

    private void UpdateContextualPills()
    {
        if (_newStampPill is not null)
        {
            _newStampPill.Visibility = _armedTool == SignPlacementKind.Stamp && SignatureStore.HasStamp
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ReplaceStamp()
    {
        if (!TryImportStampInteractively())
        {
            return;
        }

        _armedTool = null;
        ArmTool(SignPlacementKind.Stamp);
    }

    // ------------------------------------------------------------------
    // The signature picker: a small library under the Signature pill,
    // Apple-style. Pick one to place it, hover a row to delete it, or
    // draw a new one. An empty library skips straight to the pad. It is
    // an in-window flyout, not a Popup: one visual tree, one hwnd.
    // ------------------------------------------------------------------

    private void ToggleSignaturePicker()
    {
        if (_signatureFlyout is not null)
        {
            CloseSignaturePicker();
            return;
        }

        if (_flyoutHost is null)
        {
            return;
        }

        if (SignatureStore.ListSignatures().Count == 0)
        {
            ShowInkCapture();
            return;
        }

        var palette = Theme.Current;
        var card = new Border
        {
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = Metrics.SurfaceCorner,
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(SignaturePillLeft(), 6, 0, 0),
            Child = BuildSignaturePickerColumn()
        };

        _signatureFlyout = card;
        _flyoutHost.Children.Add(card);
    }

    private double SignaturePillLeft()
    {
        if (_signaturePill is { } pill && _flyoutHost is { } host && pill.IsLoaded)
        {
            try
            {
                return Math.Max(8, pill.TranslatePoint(new Point(0, 0), host).X);
            }
            catch
            {
                // Fall through to the default inset.
            }
        }

        return 12;
    }

    private void CloseSignaturePicker()
    {
        if (_signatureFlyout is { } flyout)
        {
            _flyoutHost?.Children.Remove(flyout);
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
        var column = new StackPanel { Width = 216 };
        foreach (var path in SignatureStore.ListSignatures())
        {
            column.Children.Add(BuildSignatureRow(palette, path));
        }

        var newPill = BuildPill("New signature", ShowInkCapture, PillRole.Ghost);
        newPill.Margin = new Thickness(0, 6, 0, 0);
        newPill.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(newPill);
        return column;
    }

    private Border BuildSignatureRow(ThemePalette palette, string path)
    {
        var image = SignatureStore.TryLoad(path);
        var pad = new Border
        {
            Background = Brushes.White,
            CornerRadius = Metrics.RowCorner,
            Height = 44,
            Child = new Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8, 4, 8, 4)
            }
        };

        var row = new Grid();
        row.Children.Add(pad);

        var selected = string.Equals(path, _selectedSignaturePath, StringComparison.OrdinalIgnoreCase)
            && _armedTool == SignPlacementKind.Signature;
        var frame = new Border
        {
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = Metrics.RowCorner,
            BorderThickness = new Thickness(1),
            BorderBrush = selected ? Theme.Brush(palette.Accent) : Theme.Brush(palette.RaisedBorder),
            Cursor = Cursors.Hand,
            Child = row
        };

        var removeGlyph = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 9,
            Foreground = Theme.Brush(palette.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var remove = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Child = removeGlyph
        };
        remove.MouseLeftButtonDown += (_, e) => e.Handled = true;
        remove.MouseLeftButtonUp += (_, e) =>
        {
            DeleteSignature(path);
            e.Handled = true;
        };
        row.Children.Add(remove);

        frame.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        frame.MouseLeave += (_, _) => remove.Visibility = Visibility.Collapsed;
        frame.MouseLeftButtonUp += (_, e) =>
        {
            ArmSignature(path);
            e.Handled = true;
        };
        return frame;
    }

    private void DeleteSignature(string path)
    {
        // Already-placed ink carries its own pixels; deleting a saved
        // signature only changes what can be placed next.
        SignatureStore.Remove(path);
        if (string.Equals(path, _selectedSignaturePath, StringComparison.OrdinalIgnoreCase))
        {
            _selectedSignaturePath = null;
            if (_armedTool == SignPlacementKind.Signature)
            {
                DisarmTool();
            }
        }

        RefreshSignaturePicker();
    }

    private void RefreshPills()
    {
        foreach (var refresh in _pillRefreshers)
        {
            refresh();
        }
    }

    private void SelectFrame(Grid? frame)
    {
        if (ReferenceEquals(_selectedFrame, frame))
        {
            return;
        }

        if (_selectedFrame is { } previous && _frameChrome.TryGetValue(previous, out var previousChrome))
        {
            previousChrome(false);
        }

        _selectedFrame = frame;
        if (frame is not null && _frameChrome.TryGetValue(frame, out var chrome))
        {
            chrome(true);
            Focus();
        }
    }

    private void RemovePlacementFrame(Grid frame)
    {
        _session.Placements.Remove((SignPlacement)frame.Tag);
        _placementLayer.Children.Remove(frame);
        _frameChrome.Remove(frame);
        if (ReferenceEquals(_selectedFrame, frame))
        {
            _selectedFrame = null;
        }
    }

    private static bool TryImportStampInteractively()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a stamp image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
        };
        return picker.ShowDialog() == true && SignatureStore.TryImportStamp(picker.FileName);
    }

    private void OnPageClick(object sender, MouseButtonEventArgs e)
    {
        SelectFrame(null);
        if (_armedTool is not { } tool)
        {
            return;
        }

        var at = e.GetPosition(_placementLayer);
        DisarmTool();

        var placement = BuildDefaultPlacement(tool, at);
        if (placement is null)
        {
            return;
        }

        _session.Placements.Add(placement);
        AddPlacementVisual(placement);
        e.Handled = true;
    }

    private SignPlacement? BuildDefaultPlacement(SignPlacementKind kind, Point at)
    {
        double width;
        double height;
        string? text = null;
        byte[]? imagePng = null;
        switch (kind)
        {
            case SignPlacementKind.Signature:
            case SignPlacementKind.Stamp:
                // Snapshot the pixels now: the placement must survive the
                // stored file being replaced or removed afterwards.
                var assetPath = kind == SignPlacementKind.Signature
                    ? _selectedSignaturePath ?? SignatureStore.ListSignatures().FirstOrDefault()
                    : SignatureStore.StampPath;
                imagePng = assetPath is null ? null : TryReadStoreBytes(assetPath);
                var source = imagePng is null ? null : SignatureStore.TryLoad(imagePng);
                if (source is null)
                {
                    return null;
                }

                width = kind == SignPlacementKind.Signature ? 0.24 : 0.18;
                height = width * (_pageWidth / _pageHeight) * (source.PixelHeight / (double)source.PixelWidth);
                break;

            case SignPlacementKind.Date:
                text = DateTime.Now.ToString("d", CultureInfo.CurrentCulture);
                width = 0.13;
                height = 0.028;
                break;

            default:
                return null;
        }

        return new SignPlacement(
            kind,
            _session.PageIndex,
            at.X / _pageWidth - width / 2,
            at.Y / _pageHeight - height / 2,
            width,
            height,
            text,
            imagePng).Clamped();
    }

    private static byte[]? TryReadStoreBytes(string path)
    {
        try
        {
            return System.IO.File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
    }

    private void AddPlacementVisual(SignPlacement placement, bool select = true)
    {
        var palette = Theme.Current;
        FrameworkElement inner;
        if (placement.Kind == SignPlacementKind.Date)
        {
            inner = new TextBlock
            {
                Text = placement.Text,
                Foreground = Brushes.Black,
                FontFamily = new FontFamily("Arial"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
        else
        {
            // Every image placement carries its own pixels by construction.
            var source = placement.ImagePng is { } png ? SignatureStore.TryLoad(png) : null;
            if (source is null)
            {
                _session.Placements.Remove(placement);
                return;
            }

            inner = new Image { Source = source, Stretch = Stretch.Fill };
        }

        var frame = new Grid { Tag = placement };
        frame.Children.Add(inner);

        var removeGlyph = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = Theme.Brush(palette.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var remove = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            // A chip floating just above the corner: a real target that
            // never covers the placed ink. Safe outside the frame because
            // selection holds the chrome; no hover gap can hide it.
            Margin = new Thickness(0, -28, -2, 0),
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Child = removeGlyph
        };
        remove.MouseEnter += (_, _) =>
        {
            remove.Background = Theme.Brush(palette.HoverBg);
            removeGlyph.Foreground = Theme.Brush(palette.TextPrimary);
        };
        remove.MouseLeave += (_, _) =>
        {
            remove.Background = Theme.Brush(palette.RaisedBg);
            removeGlyph.Foreground = Theme.Brush(palette.TextSecondary);
        };
        remove.MouseLeftButtonDown += (_, e) => e.Handled = true;
        remove.MouseLeftButtonUp += (_, e) =>
        {
            RemovePlacementFrame(frame);
            e.Handled = true;
        };
        frame.Children.Add(remove);

        var outline = new Rectangle
        {
            Stroke = Theme.Brush(palette.Accent),
            StrokeThickness = 1,
            StrokeDashArray = [3, 3],
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        frame.Children.Add(outline);

        void SetSelected(bool on)
        {
            remove.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            outline.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        _frameChrome[frame] = SetSelected;

        // Hover only hints with the outline; the delete chip belongs to
        // selection, which holds it until something else is clicked.
        frame.MouseEnter += (_, _) => outline.Visibility = Visibility.Visible;
        frame.MouseLeave += (_, _) =>
        {
            if (!ReferenceEquals(_selectedFrame, frame))
            {
                outline.Visibility = Visibility.Collapsed;
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
            SelectFrame(frame);
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

            SelectFrame(frame);
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
            var moved = (placement with
            {
                X = (at.X - grabOffset.X) / _pageWidth,
                Y = (at.Y - grabOffset.Y) / _pageHeight
            }).Clamped();
            ReplacePlacement(frame, moved);
        };
        frame.MouseLeftButtonUp += (_, _) =>
        {
            dragging = false;
            frame.ReleaseMouseCapture();
        };

        // The quiet resize: the wheel over an element scales it in place.
        frame.MouseWheel += (_, e) =>
        {
            if (frame.Tag is not SignPlacement placement)
            {
                return;
            }

            var factor = e.Delta > 0 ? 1.06 : 1 / 1.06;
            var center = new Point(placement.X + placement.Width / 2, placement.Y + placement.Height / 2);
            var width = Math.Clamp(placement.Width * factor, 0.03, 0.9);
            var height = placement.Height * (width / placement.Width);
            var scaled = (placement with
            {
                Width = width,
                Height = height,
                X = center.X - width / 2,
                Y = center.Y - height / 2
            }).Clamped();
            ReplacePlacement(frame, scaled);
            e.Handled = true;
        };
    }

    private void BeginDateEdit(Grid frame, FrameworkElement inner)
    {
        if (inner is not TextBlock label || frame.Tag is not SignPlacement placement)
        {
            return;
        }

        // Dressed to the page, not the system: white paper, black ink, and
        // an accent hairline standing in for the dashed selection outline.
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

        void Commit()
        {
            var text = string.IsNullOrWhiteSpace(editor.Text) ? placement.Text : editor.Text.Trim();
            label.Text = text;
            ReplacePlacement(frame, ((SignPlacement)frame.Tag) with { Text = text });
            frame.Children.Remove(editor);
            label.Visibility = Visibility.Visible;
        }

        editor.LostFocus += (_, _) => Commit();
        editor.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                Commit();
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
        var index = _session.Placements.IndexOf((SignPlacement)frame.Tag);
        if (index >= 0)
        {
            _session.Placements[index] = updated;
        }

        frame.Tag = updated;
        ApplyPlacementBounds(frame);
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
        if (placement.Kind == SignPlacementKind.Date
            && frame.Children.Count > 0
            && frame.Children[0] is TextBlock label)
        {
            label.FontSize = Math.Max(6, frame.Height * 0.72);
        }
    }

    // ------------------------------------------------------------------
    // Ink capture: drawing the signature happens right here on the stage,
    // never in a separate dialog.
    // ------------------------------------------------------------------

    private Grid BuildInkOverlay(ThemePalette palette)
    {
        var overlay = new Grid
        {
            Background = Theme.Brush(Theme.OverlayScrim),
            Visibility = Visibility.Collapsed
        };

        var card = new Border
        {
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = Metrics.SurfaceCorner,
            Padding = new Thickness(18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var column = new StackPanel { Width = 460 };
        _inkTitle = new TextBlock
        {
            Text = "Draw your signature",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary),
            Margin = new Thickness(0, 0, 0, 10)
        };
        column.Children.Add(_inkTitle);

        var inkFrame = new Border
        {
            Background = Brushes.White,
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = Metrics.RowCorner,
            Height = 170,
            Child = _inkCanvas
        };
        column.Children.Add(inkFrame);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(BuildPill("Use it", CommitInk, PillRole.Accent));
        buttons.Children.Add(BuildPill("Clear", ClearInk));
        buttons.Children.Add(BuildPill("Back", HideInkCapture, PillRole.Ghost));
        column.Children.Add(buttons);

        card.Child = column;
        overlay.Children.Add(card);

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

        return overlay;
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
        if (_inkTitle is not null)
        {
            _inkTitle.Text = editingFrame is not null ? "Edit this signature" : "Draw your signature";
        }

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
            if (edited is null)
            {
                return;
            }

            UpdatePlacementImage(frame, edited);
            HideInkCapture();
            return;
        }

        var saved = SignatureStore.SaveSignatureFromStrokes(strokes, Colors.Black);
        if (saved is null)
        {
            return;
        }

        HideInkCapture();
        ArmSignature(saved);
    }

    /// <summary>Commits edited ink back into one placed signature.</summary>
    private void UpdatePlacementImage(Grid frame, BitmapSource edited)
    {
        if (frame.Tag is not SignPlacement placement)
        {
            return;
        }

        var png = EncodePng(edited);
        if (png is null)
        {
            return;
        }

        // Keep the placement's width and center; the height follows the
        // edited ink's aspect.
        var height = placement.Width * (_pageWidth / _pageHeight) * (edited.PixelHeight / (double)edited.PixelWidth);
        var centerY = placement.Y + placement.Height / 2;
        var updated = (placement with
        {
            ImagePng = png,
            Height = height,
            Y = centerY - height / 2
        }).Clamped();

        if (frame.Children.Count > 0 && frame.Children[0] is Image image)
        {
            image.Source = SignatureStore.TryLoad(png);
        }

        ReplacePlacement(frame, updated);
    }

    private static byte[]? EncodePng(BitmapSource image)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new System.IO.MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
