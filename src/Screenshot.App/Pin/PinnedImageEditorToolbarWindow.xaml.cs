using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Pin;

internal enum PinnedImageToolbarMode
{
    Edit,
    Crop,
}

public partial class PinnedImageEditorToolbarWindow : Window
{
    private const double VisibleAttachmentGapDip = 2;
    private const double ToolbarOuterMarginDip = 6;
    private const double SinglePinShellInsetDip = 12;
    // The active tool belongs to this editor session. The persisted shape and
    // arrow variants are restored separately when a new toolbar is created.
    private EditorTool _lastTool = EditorTool.Rectangle;
    private WpfColor _selectedColor = WpfColor.FromRgb(214, 69, 69);
    private static double _lastStrokeWidth = 3;
    private WpfColor _defaultColor = WpfColor.FromRgb(214, 69, 69);
    private double _defaultStrokeWidth = 3;
    private readonly HashSet<EditorTool> _persistedTools = [];
    private bool _isApplyingToolPreferences;
    private int _programmaticWidthChangesPending;
    private ArrowToolMode _arrowToolMode = ArrowToolMode.Straight;
    private ShapeToolMode _shapeToolMode = ShapeToolMode.Rectangle;
    private IReadOnlyDictionary<CaptureToolbarFeature, string> _toolbarFeatureShortcuts =
        new Dictionary<CaptureToolbarFeature, string>();

    private readonly Window _attachedWindow;
    private readonly Action<string>? _customStrokeColorChanged;
    private readonly Action<int[]>? _customColorPaletteChanged;
    private readonly Action<ArrowStyle>? _arrowStyleChanged;
    private readonly Action<ArrowToolMode>? _arrowToolModeChanged;
    private readonly Action<ShapeToolMode>? _shapeToolModeChanged;
    private readonly Action<AnnotationToolMode>? _lastAnnotationToolChanged;
    private int[] _customColorPalette = [];
    private WpfColor? _customColor;
    private ArrowStyle _arrowStyle = ArrowStyle.Filled;
    private PinnedImageToolbarMode _mode;
    private bool _hasCustomPosition;
    private int _customOffsetX;
    private int _customOffsetY;
    private bool _isToolbarSurfaceDragging;
    private DrawingPoint _toolbarSurfacePointerStart;
    private DrawingRectangle _toolbarSurfaceWindowStart;
    private readonly ToolbarDragHintBehavior _toolbarDragHint;
    private bool _isClosing;
    private bool _isInitializing = true;
    private bool _colorOptionsAvailable = true;

    public PinnedImageEditorToolbarWindow(
        Window attachedWindow,
        AppSettings? settings = null,
        Action<string>? customStrokeColorChanged = null,
        Action<int[]>? customColorPaletteChanged = null,
        Action<ArrowStyle>? arrowStyleChanged = null,
        Action<ArrowToolMode>? arrowToolModeChanged = null,
        Action<ShapeToolMode>? shapeToolModeChanged = null,
        Action<AnnotationToolMode>? lastAnnotationToolChanged = null)
    {
        ArgumentNullException.ThrowIfNull(attachedWindow);
        _attachedWindow = attachedWindow;
        _customStrokeColorChanged = customStrokeColorChanged;
        _customColorPaletteChanged = customColorPaletteChanged;
        _arrowStyleChanged = arrowStyleChanged;
        _arrowToolModeChanged = arrowToolModeChanged;
        _shapeToolModeChanged = shapeToolModeChanged;
        _lastAnnotationToolChanged = lastAnnotationToolChanged;
        InitializeComponent();
        var toolbarScale = Math.Clamp(
            double.IsFinite(settings?.ToolbarScalePercent ?? 100)
                ? (settings?.ToolbarScalePercent ?? 100) / 100d
                : 1,
            0.5,
            1.5);
        ToolbarSurface.LayoutTransform = new ScaleTransform(
            toolbarScale,
            toolbarScale);
        _toolbarDragHint = new ToolbarDragHintBehavior(
            ToolbarSurface,
            ToolbarSurface);
        PopulateEmojiPalette();
        ApplyPreferences(settings);
        _toolbarFeatureShortcuts = settings?.CaptureToolbarFeatureShortcuts ??
            new Dictionary<CaptureToolbarFeature, string>();
        ApplyToolbarFeatureVisibility(settings?.VisibleCaptureToolbarFeatures);
        ApplyToolbarLayout(
            settings?.CaptureToolbarFeatureOrder,
            settings?.CaptureToolbarRows ?? CaptureToolbarRowCount.One);
        ApplyThemedContextMenu(ShapeToolButton.ContextMenu);
        ApplyThemedContextMenu(ArrowToolButton.ContextMenu);
        _isInitializing = false;
        Owner = attachedWindow;
        _attachedWindow.LocationChanged += OnAttachedWindowBoundsChanged;
        _attachedWindow.SizeChanged += OnAttachedWindowBoundsChanged;
        _attachedWindow.StateChanged += OnAttachedWindowBoundsChanged;
        _attachedWindow.Closed += OnAttachedWindowClosed;
        _attachedWindow.AddHandler(
            Keyboard.PreviewKeyDownEvent,
            new System.Windows.Input.KeyEventHandler(OnAttachedWindowPreviewKeyDown),
            handledEventsToo: true);
        Loaded += OnLoaded;
    }

    public event Action<EditorTool>? ToolSelected;

    private void OnFeatureShortcutPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is System.Windows.Controls.TextBox)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shortcut = key >= Key.D0 && key <= Key.D9 ? key.ToString()[1..] :
            key >= Key.NumPad0 && key <= Key.NumPad9
                ? ((int)key - (int)Key.NumPad0).ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        var match = _toolbarFeatureShortcuts.FirstOrDefault(pair =>
            string.Equals(pair.Value, shortcut, StringComparison.OrdinalIgnoreCase));
        if (match.Equals(default(KeyValuePair<CaptureToolbarFeature, string>))) return;
        var tool = match.Key switch
        {
            CaptureToolbarFeature.Shape => _shapeToolMode == ShapeToolMode.Ellipse
                ? EditorTool.Ellipse
                : EditorTool.Rectangle,
            CaptureToolbarFeature.Arrow => _arrowToolMode == ArrowToolMode.Curved
                ? EditorTool.CurvedArrow
                : EditorTool.Arrow,
            CaptureToolbarFeature.Emoji => EditorTool.Emoji,
            CaptureToolbarFeature.Number => EditorTool.Number,
            CaptureToolbarFeature.Brush => EditorTool.Brush,
            CaptureToolbarFeature.Text => EditorTool.Text,
            CaptureToolbarFeature.Mosaic => EditorTool.Mosaic,
            _ => (EditorTool?)null,
        };
        if (tool is { } selected)
        {
            SelectToolButton(selected);
            _lastTool = selected;
            ApplySelectedToolPreferences();
            ToolSelected?.Invoke(selected);
            e.Handled = true;
            return;
        }
        var action = match.Key switch
        {
            CaptureToolbarFeature.Save => SaveButton,
            CaptureToolbarFeature.TextRecognition => OcrButton,
            CaptureToolbarFeature.CopyTable => CopyTableButton,
            CaptureToolbarFeature.CopyRecognizedText => CopyTextButton,
            CaptureToolbarFeature.Translation => TranslateActionButton,
            CaptureToolbarFeature.PrivacyRedaction => PrivacyButton,
            _ => null,
        };
        if (action is not null && action.IsEnabled)
        {
            action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            e.Handled = true;
        }
    }

    public event Action<string>? EmojiSelected;

    public event Action<WpfColor>? ColorSelected;

    public event Action<double>? StrokeWidthChanged;

    public event Action<ArrowStyle>? ArrowStyleSelected;

    public event EventHandler? UndoRequested;

    public event EventHandler? CropRequested;

    public event EventHandler? SaveRequested;

    public event EventHandler? OcrRequested;

    public event EventHandler? CopyTableRequested;

    public event EventHandler? CopyTextRequested;

    public event EventHandler? TranslateRequested;

    public event EventHandler? PrivacyRequested;

    public event EventHandler? ApplyRequested;

    public event EventHandler? CancelRequested;

    internal PinnedImageToolbarMode Mode => _mode;

    internal bool HasCustomPosition => _hasCustomPosition;

    internal WpfColor SelectedColor => _selectedColor;

    internal IReadOnlyList<int> CustomColorPalette => _customColorPalette;

    internal void ShowEdit()
    {
        _mode = PinnedImageToolbarMode.Edit;
        EditPanel.Visibility = Visibility.Visible;
        CropPanel.Visibility = Visibility.Collapsed;
        // Each editor session starts from the shape button. The selected shape
        // variant is a preference, while the last active tool is not.
        _lastTool = _shapeToolMode == ShapeToolMode.Ellipse
            ? EditorTool.Ellipse
            : EditorTool.Rectangle;
        SelectToolButton(_lastTool);
        ApplySelectedToolPreferences();
        UpdateEmojiPaletteVisibility();
        UpdateShapeButtonPresentation();
        UpdateArrowButtonPresentation();
        UpdateArrowMenuState();
        ShowAttached();
        ToolSelected?.Invoke(_lastTool);
        ColorSelected?.Invoke(_selectedColor);
        StrokeWidthChanged?.Invoke(_lastStrokeWidth);
        ArrowStyleSelected?.Invoke(_arrowStyle);
    }

    internal void SetStrokeWidthFromCanvas(double width)
    {
        if (StrokeWidthSlider is null || !double.IsFinite(width))
        {
            return;
        }

        var value = Math.Clamp(
            width,
            StrokeWidthSlider.Minimum,
            StrokeWidthSlider.Maximum);
        if (!double.Equals(StrokeWidthSlider.Value, value))
        {
            _programmaticWidthChangesPending++;
            StrokeWidthSlider.Value = value;
        }
        _lastStrokeWidth = value;
        if (StrokeWidthText is not null)
        {
            StrokeWidthText.Text = $"{value:0.#} px";
        }
    }

    internal void SetColorFromCanvas(WpfColor color)
    {
        _selectedColor = color;
        _customColor = color;
        if (CustomColorButton is not null &&
            ResolveSelectedColorButton(color) == CustomColorButton)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            CustomColorButton.Background = brush;
        }

        UpdateSelectedColorButton(ResolveSelectedColorButton(color));
    }

    internal void ShowCrop(int pixelWidth, int pixelHeight)
    {
        _mode = PinnedImageToolbarMode.Crop;
        EditPanel.Visibility = Visibility.Collapsed;
        CropPanel.Visibility = Visibility.Visible;
        UpdateCropSize(pixelWidth, pixelHeight);
        ShowAttached();
    }

    internal void UpdateCropSize(int pixelWidth, int pixelHeight)
    {
        CropSizeText.Text = $"{Math.Max(0, pixelWidth)} x {Math.Max(0, pixelHeight)} px";
    }

    internal void MoveToolbar(double horizontalChange, double verticalChange)
    {
        if (!MonitorGeometryService.TryGetWindowBounds(this, out var bounds))
        {
            Left += horizontalChange;
            Top += verticalChange;
            _hasCustomPosition = true;
            return;
        }

        var ownerBounds = GetOwnerBounds();
        var dpi = MonitorGeometryService.GetDpiScale(ownerBounds);
        var targetX = bounds.Left + (int)Math.Round(horizontalChange * dpi.X);
        var targetY = bounds.Top + (int)Math.Round(verticalChange * dpi.Y);
        _hasCustomPosition = true;
        _ = MonitorGeometryService.TryMoveWindow(this, targetX, targetY);
        RememberCustomOffset(targetX, targetY, bounds.Size);
    }

    internal void ResetPosition()
    {
        _hasCustomPosition = false;
        _customOffsetX = 0;
        _customOffsetY = 0;
        PositionAttached();
    }

    internal static DrawingPoint CalculateAttachedPosition(
        DrawingRectangle ownerBounds,
        DrawingSize toolbarSize,
        DrawingRectangle workArea,
        int gap)
    {
        var centeredX = ownerBounds.Left + ((ownerBounds.Width - toolbarSize.Width) / 2);
        var centeredY = ownerBounds.Top + ((ownerBounds.Height - toolbarSize.Height) / 2);
        int x;
        int y;

        if (ownerBounds.Bottom + gap + toolbarSize.Height <= workArea.Bottom)
        {
            x = centeredX;
            y = ownerBounds.Bottom + gap;
        }
        else if (ownerBounds.Top - gap - toolbarSize.Height >= workArea.Top)
        {
            x = centeredX;
            y = ownerBounds.Top - gap - toolbarSize.Height;
        }
        else if (ownerBounds.Right + gap + toolbarSize.Width <= workArea.Right)
        {
            x = ownerBounds.Right + gap;
            y = centeredY;
        }
        else if (ownerBounds.Left - gap - toolbarSize.Width >= workArea.Left)
        {
            x = ownerBounds.Left - gap - toolbarSize.Width;
            y = centeredY;
        }
        else
        {
            x = centeredX;
            y = ownerBounds.Bottom + gap;
        }

        return new DrawingPoint(
            Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - toolbarSize.Width)),
            Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - toolbarSize.Height)));
    }

    internal static DrawingPoint CalculateDraggedPosition(
        DrawingRectangle windowStart,
        DrawingPoint pointerStart,
        DrawingPoint pointerCurrent)
    {
        return new DrawingPoint(
            windowStart.Left + pointerCurrent.X - pointerStart.X,
            windowStart.Top + pointerCurrent.Y - pointerStart.Y);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_isInitializing)
        {
            _lastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(_lastTool));
        }
        _toolbarDragHint.Detach();
        FinishToolbarSurfaceDrag();
        _isClosing = true;
        Loaded -= OnLoaded;
        _attachedWindow.LocationChanged -= OnAttachedWindowBoundsChanged;
        _attachedWindow.SizeChanged -= OnAttachedWindowBoundsChanged;
        _attachedWindow.StateChanged -= OnAttachedWindowBoundsChanged;
        _attachedWindow.Closed -= OnAttachedWindowClosed;
        _attachedWindow.RemoveHandler(
            Keyboard.PreviewKeyDownEvent,
            new System.Windows.Input.KeyEventHandler(OnAttachedWindowPreviewKeyDown));
        base.OnClosed(e);
    }

    private void ShowAttached()
    {
        if (!IsVisible)
        {
            Show();
        }
        UpdateLayout();
        PositionAttached();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionAttached();
    }

    private void OnAttachedWindowBoundsChanged(object? sender, EventArgs e)
    {
        if (IsVisible && _attachedWindow.WindowState != WindowState.Minimized)
        {
            PositionAttached();
        }
    }

    private void OnAttachedWindowClosed(object? sender, EventArgs e)
    {
        if (!_isClosing)
        {
            Close();
        }
    }

    private void OnAttachedWindowPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        OnFeatureShortcutPreviewKeyDown(sender, e);
    }

    private DrawingRectangle GetOwnerBounds()
    {
        if (MonitorGeometryService.TryGetWindowBounds(_attachedWindow, out var bounds))
        {
            return bounds;
        }

        return new DrawingRectangle(
            (int)Math.Round(_attachedWindow.Left),
            (int)Math.Round(_attachedWindow.Top),
            Math.Max(1, (int)Math.Round(_attachedWindow.ActualWidth)),
            Math.Max(1, (int)Math.Round(_attachedWindow.ActualHeight)));
    }

    private void PositionAttached()
    {
        if (!IsVisible)
        {
            return;
        }

        var ownerBounds = GetOwnerBounds();
        var workArea = MonitorGeometryService.GetWorkArea(ownerBounds);
        var dpi = MonitorGeometryService.GetDpiScale(ownerBounds);
        var toolbarSize = MonitorGeometryService.TryGetWindowBounds(this, out var toolbarBounds)
            ? toolbarBounds.Size
            : new DrawingSize(
                Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.X)),
                Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.Y)));
        var anchor = CalculateAttachedPosition(
            ownerBounds,
            toolbarSize,
            workArea,
            GetAttachmentOffsetPixels(dpi));
        var x = anchor.X + (_hasCustomPosition ? _customOffsetX : 0);
        var y = anchor.Y + (_hasCustomPosition ? _customOffsetY : 0);
        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - toolbarSize.Width));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - toolbarSize.Height));
        _ = MonitorGeometryService.TryMoveWindow(this, x, y);
    }

    private void RememberCustomOffset(
        int currentX,
        int currentY,
        DrawingSize toolbarSize)
    {
        var ownerBounds = GetOwnerBounds();
        var workArea = MonitorGeometryService.GetWorkArea(ownerBounds);
        var dpi = MonitorGeometryService.GetDpiScale(ownerBounds);
        var anchor = CalculateAttachedPosition(
            ownerBounds,
            toolbarSize,
            workArea,
            GetAttachmentOffsetPixels(dpi));
        _customOffsetX = currentX - anchor.X;
        _customOffsetY = currentY - anchor.Y;
    }

    private int GetAttachmentOffsetPixels(MonitorDpiScale dpi)
    {
        var ownerInset = _attachedWindow is PinnedImageWindow
            ? SinglePinShellInsetDip
            : 0;
        return (int)Math.Round(
            (VisibleAttachmentGapDip - ToolbarOuterMarginDip - ownerInset) *
            Math.Max(dpi.X, dpi.Y));
    }

    private void SelectToolButton(EditorTool tool)
    {
        var button = tool switch
        {
            EditorTool.Rectangle or EditorTool.Ellipse => ShapeToolButton,
            EditorTool.Arrow or EditorTool.CurvedArrow => ArrowToolButton,
            EditorTool.Emoji => EmojiToolButton,
            EditorTool.Number => NumberToolButton,
            EditorTool.Brush => BrushToolButton,
            EditorTool.Text => TextToolButton,
            EditorTool.Mosaic => MosaicToolButton,
            _ => ShapeToolButton,
        };
        if (tool is EditorTool.Rectangle or EditorTool.Ellipse)
        {
            _shapeToolMode = tool == EditorTool.Ellipse
                ? ShapeToolMode.Ellipse
                : ShapeToolMode.Rectangle;
            ShapeToolButton.Tag = tool.ToString();
            ShapeToolButton.ToolTip = tool == EditorTool.Rectangle
                ? "矩形/椭圆（当前：矩形）"
                : "矩形/椭圆（当前：椭圆）";
            ShapeToolIcon.Data = (Geometry)FindResource(
                tool == EditorTool.Rectangle
                    ? "RectangleIconGeometry"
                    : "EllipseIconGeometry");
            RectangleShapeMenuItem.IsChecked = tool == EditorTool.Rectangle;
            EllipseShapeMenuItem.IsChecked = tool == EditorTool.Ellipse;
        }
        else if (tool is EditorTool.Arrow or EditorTool.CurvedArrow)
        {
            _arrowToolMode = tool == EditorTool.CurvedArrow
                ? ArrowToolMode.Curved
                : ArrowToolMode.Straight;
            ArrowToolButton.Tag = tool.ToString();
            UpdateArrowButtonPresentation(tool);
            UpdateArrowMenuState(tool);
        }
        button.IsChecked = true;
        UpdateEmojiPaletteVisibility();
    }

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (sender is WpfRadioButton { Tag: string toolName } &&
            Enum.TryParse<EditorTool>(toolName, out var tool))
        {
            var selected = sender == ShapeToolButton &&
                _lastTool is EditorTool.Rectangle or EditorTool.Ellipse
                ? _lastTool
                : sender == ArrowToolButton &&
                    _lastTool is EditorTool.Arrow or EditorTool.CurvedArrow
                    ? _lastTool
                    : tool;
            _lastTool = selected;
            if (selected is EditorTool.Rectangle or EditorTool.Ellipse)
            {
                _shapeToolMode = selected == EditorTool.Ellipse
                    ? ShapeToolMode.Ellipse
                    : ShapeToolMode.Rectangle;
            }
            else if (selected is EditorTool.Arrow or EditorTool.CurvedArrow)
            {
                _arrowToolMode = selected == EditorTool.CurvedArrow
                    ? ArrowToolMode.Curved
                    : ArrowToolMode.Straight;
            }
            ApplySelectedToolPreferences();
            _lastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(selected));
            ToolSelected?.Invoke(selected);
            UpdateEmojiPaletteVisibility();
        }
    }

    private void UpdateEmojiPaletteVisibility()
    {
        if (EmojiPalette is null)
        {
            return;
        }

        EmojiPalette.Visibility = _lastTool == EditorTool.Emoji
            ? Visibility.Visible
            : Visibility.Collapsed;
        ColorOptions.Visibility = !_colorOptionsAvailable
            ? Visibility.Collapsed
            : Visibility.Visible;
        var colorVisibility = _lastTool == EditorTool.Emoji
            ? Visibility.Collapsed
            : Visibility.Visible;
        RedColorButton.Visibility = colorVisibility;
        CyanColorButton.Visibility = colorVisibility;
        DarkColorButton.Visibility = colorVisibility;
        CustomColorButton.Visibility = colorVisibility;
    }

    private void PopulateEmojiPalette()
    {
        foreach (var emoji in EmojiStickerCatalog.All)
        {
            var button = new WpfButton
            {
                Tag = emoji,
                ToolTip = emoji,
                Style = (Style)FindResource("EmojiPaletteButton"),
                Content = new EmojiStickerImage
                {
                    Width = 23,
                    Height = 23,
                    Sticker = emoji,
                },
            };
            button.Click += OnEmojiClick;
            EmojiPalettePanel.Children.Add(button);
        }
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string emoji } ||
            string.IsNullOrWhiteSpace(emoji))
        {
            return;
        }

        _lastTool = EditorTool.Emoji;
        var wasEmojiSelected = EmojiToolButton.IsChecked == true;
        EmojiToolButton.IsChecked = true;
        // Emoji has its own persisted size just like every other annotation
        // tool. Refresh the shared per-tool preference before notifying the
        // canvas, including the case where Emoji was already selected.
        ApplySelectedToolPreferences();
        if (wasEmojiSelected)
        {
            UpdateEmojiPaletteVisibility();
            _lastAnnotationToolChanged?.Invoke(AnnotationToolMode.Emoji);
            ToolSelected?.Invoke(EditorTool.Emoji);
        }
        EmojiSelected?.Invoke(emoji);
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string colorText } &&
            WpfColorConverter.ConvertFromString(colorText) is WpfColor color)
        {
            _selectedColor = color;
            _customColor = color;
            _persistedTools.Add(_lastTool);
            AnnotationToolPreferences.SetColor(_lastTool, color);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            CustomColorButton.Background = brush;
            UpdateSelectedColorButton(CustomColorButton);
            ColorSelected?.Invoke(color);
            _customStrokeColorChanged?.Invoke(FormatColor(color));
        }
    }

    private void OnCustomColorClick(object sender, RoutedEventArgs e)
    {
        var picker = new ThemeColorPickerWindow(
            _customColor ?? _selectedColor,
            _customColorPalette)
        {
            Owner = this,
        };
        picker.ColorSelected += (_, color) => ApplyCustomColor(color);
        picker.PaletteChanged += (_, colors) =>
        {
            _customColorPalette = NormalizeCustomColorPalette(colors);
            _customColorPaletteChanged?.Invoke(_customColorPalette.ToArray());
        };
        picker.Show();
        picker.UpdateLayout();
        PositionColorPicker(picker);
    }

    private void PositionColorPicker(Window picker)
    {
        if (!MonitorGeometryService.TryGetWindowBounds(picker, out var pickerBounds))
        {
            return;
        }
        var buttonPoint = CustomColorButton.PointToScreen(
            new System.Windows.Point(0, 0));
        var ownerBounds = GetOwnerBounds();
        var workArea = MonitorGeometryService.GetWorkArea(ownerBounds);
        var dpi = MonitorGeometryService.GetDpiScale(ownerBounds);
        var gap = (int)Math.Round(8 * Math.Max(dpi.X, dpi.Y));
        var x = (int)Math.Round(buttonPoint.X);
        var y = (int)Math.Round(buttonPoint.Y) - pickerBounds.Height - gap;
        if (y < workArea.Top)
        {
            y = (int)Math.Round(buttonPoint.Y + CustomColorButton.ActualHeight * dpi.Y) + gap;
        }
        x = Math.Clamp(
            x,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - pickerBounds.Width));
        y = Math.Clamp(
            y,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - pickerBounds.Height));
        _ = MonitorGeometryService.TryMoveWindow(picker, x, y);
    }

    private void ApplyCustomColor(WpfColor color)
    {
        _selectedColor = color;
        _customColor = color;
        _persistedTools.Add(_lastTool);
        AnnotationToolPreferences.SetColor(_lastTool, color);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        CustomColorButton.Background = brush;
        UpdateSelectedColorButton(CustomColorButton);
        ColorSelected?.Invoke(color);
        _customStrokeColorChanged?.Invoke(FormatColor(color));
    }

    private void OnStrokeWidthChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        _lastStrokeWidth = e.NewValue;
        if (_programmaticWidthChangesPending > 0)
        {
            _programmaticWidthChangesPending--;
        }
        else if (!_isApplyingToolPreferences && !_isInitializing)
        {
            _persistedTools.Add(_lastTool);
            AnnotationToolPreferences.SetWidth(_lastTool, e.NewValue);
        }
        if (StrokeWidthText is not null)
        {
            StrokeWidthText.Text = $"{e.NewValue:0.#} px";
        }
        StrokeWidthChanged?.Invoke(e.NewValue);
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) =>
        UndoRequested?.Invoke(this, EventArgs.Empty);

    private void OnCropClick(object sender, RoutedEventArgs e) =>
        CropRequested?.Invoke(this, EventArgs.Empty);

    private void OnSaveClick(object sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);

    private void OnOcrClick(object sender, RoutedEventArgs e) =>
        OcrRequested?.Invoke(this, EventArgs.Empty);

    private void OnCopyTableClick(object sender, RoutedEventArgs e) =>
        CopyTableRequested?.Invoke(this, EventArgs.Empty);

    private void OnCopyTextClick(object sender, RoutedEventArgs e) =>
        CopyTextRequested?.Invoke(this, EventArgs.Empty);

    private void OnTranslateClick(object sender, RoutedEventArgs e) =>
        TranslateRequested?.Invoke(this, EventArgs.Empty);

    private void OnPrivacyClick(object sender, RoutedEventArgs e) =>
        PrivacyRequested?.Invoke(this, EventArgs.Empty);

    private void OnApplyClick(object sender, RoutedEventArgs e) =>
        ApplyRequested?.Invoke(this, EventArgs.Empty);

    private void OnCancelClick(object sender, RoutedEventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OnToolbarSurfaceMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            !ToolbarDragInteraction.IsBlankSurface(
                e.OriginalSource as DependencyObject,
                ToolbarSurface))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            ResetPosition();
            e.Handled = true;
            return;
        }

        if (!MonitorGeometryService.TryGetWindowBounds(this, out var bounds))
        {
            return;
        }

        _hasCustomPosition = true;
        _isToolbarSurfaceDragging = true;
        _toolbarSurfacePointerStart = WinForms.Cursor.Position;
        _toolbarSurfaceWindowStart = bounds;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            _isToolbarSurfaceDragging = false;
            return;
        }

        _ = NativeMethods.SetCapture(handle);
        if (NativeMethods.GetCapture() != handle)
        {
            _isToolbarSurfaceDragging = false;
            return;
        }

        e.Handled = true;
    }

    private void OnToolbarSurfaceMouseMove(
        object sender,
        WpfMouseEventArgs e)
    {
        if (!_isToolbarSurfaceDragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishToolbarSurfaceDrag();
            return;
        }

        var position = CalculateDraggedPosition(
            _toolbarSurfaceWindowStart,
            _toolbarSurfacePointerStart,
            WinForms.Cursor.Position);
        _ = MonitorGeometryService.TryMoveWindow(this, position.X, position.Y);
        e.Handled = true;
    }

    private void OnToolbarSurfaceMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isToolbarSurfaceDragging)
        {
            return;
        }

        FinishToolbarSurfaceDrag();
        e.Handled = true;
    }

    private void FinishToolbarSurfaceDrag()
    {
        _isToolbarSurfaceDragging = false;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && NativeMethods.GetCapture() == handle)
        {
            _ = NativeMethods.ReleaseCapture();
        }

        if (MonitorGeometryService.TryGetWindowBounds(this, out var bounds))
        {
            RememberCustomOffset(bounds.Left, bounds.Top, bounds.Size);
        }
    }

    private void OnShapeMenuArrowMouseDown(object sender, MouseButtonEventArgs e)
    {
        RectangleShapeMenuItem.IsChecked = _shapeToolMode == ShapeToolMode.Rectangle;
        EllipseShapeMenuItem.IsChecked = _shapeToolMode == ShapeToolMode.Ellipse;
        OpenContextMenu(ShapeToolButton);
        e.Handled = true;
    }

    private void OnShapeMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string toolName } ||
            !Enum.TryParse<EditorTool>(toolName, out var tool))
        {
            return;
        }

        _lastTool = tool;
        _shapeToolMode = tool == EditorTool.Ellipse
            ? ShapeToolMode.Ellipse
            : ShapeToolMode.Rectangle;
        _shapeToolModeChanged?.Invoke(
            tool == EditorTool.Ellipse
                ? ShapeToolMode.Ellipse
                : ShapeToolMode.Rectangle);
        _lastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(tool));
        SelectToolButton(tool);
        ApplySelectedToolPreferences();
        ToolSelected?.Invoke(tool);
    }

    private void OnArrowMenuArrowMouseDown(object sender, MouseButtonEventArgs e)
    {
        UpdateArrowMenuState();
        OpenContextMenu(ArrowToolButton);
        e.Handled = true;
    }

    private void OnArrowVariantMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
        {
            return;
        }
        var parts = tag.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !Enum.TryParse<EditorTool>(parts[0], out var tool) ||
            !Enum.TryParse<ArrowStyle>(parts[1], out var style) ||
            tool is not (EditorTool.Arrow or EditorTool.CurvedArrow))
        {
            return;
        }

        _lastTool = tool;
        _arrowStyle = style;
        _arrowToolMode = tool == EditorTool.CurvedArrow
            ? ArrowToolMode.Curved
            : ArrowToolMode.Straight;
        SelectToolButton(tool);
        ApplySelectedToolPreferences();
        ToolSelected?.Invoke(tool);
        ArrowStyleSelected?.Invoke(style);
        _arrowStyleChanged?.Invoke(style);
        _arrowToolModeChanged?.Invoke(tool == EditorTool.CurvedArrow
            ? ArrowToolMode.Curved
            : ArrowToolMode.Straight);
        _lastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(tool));
    }

    private void UpdateArrowMenuState(EditorTool? tool = null)
    {
        var selected = tool is EditorTool.Arrow or EditorTool.CurvedArrow
            ? tool.Value
            : _arrowToolMode == ArrowToolMode.Curved
                ? EditorTool.CurvedArrow
                : EditorTool.Arrow;
        StraightFilledArrowMenuItem.IsChecked =
            selected == EditorTool.Arrow && _arrowStyle == ArrowStyle.Filled;
        StraightHollowArrowMenuItem.IsChecked =
            selected == EditorTool.Arrow && _arrowStyle == ArrowStyle.Hollow;
        CurvedFilledArrowMenuItem.IsChecked =
            selected == EditorTool.CurvedArrow && _arrowStyle == ArrowStyle.Filled;
        CurvedHollowArrowMenuItem.IsChecked =
            selected == EditorTool.CurvedArrow && _arrowStyle == ArrowStyle.Hollow;
    }

    private void UpdateArrowButtonPresentation(EditorTool? tool = null)
    {
        var selected = tool is EditorTool.Arrow or EditorTool.CurvedArrow
            ? tool.Value
            : _arrowToolMode == ArrowToolMode.Curved
                ? EditorTool.CurvedArrow
                : EditorTool.Arrow;
        var isCurved = selected == EditorTool.CurvedArrow;
        // The Tag is consumed when the main button is clicked. Keep it aligned
        // with the restored icon and menu selection so a curved arrow does not
        // silently turn back into a straight arrow on its first use.
        ArrowToolButton.Tag = selected.ToString();
        var key = (isCurved, _arrowStyle) switch
        {
            (false, ArrowStyle.Hollow) => "StraightHollowArrowIconGeometry",
            (true, ArrowStyle.Filled) => "CurvedFilledArrowIconGeometry",
            (true, ArrowStyle.Hollow) => "CurvedHollowArrowIconGeometry",
            _ => "StraightFilledArrowIconGeometry",
        };
        ArrowToolIcon.Data = (Geometry)FindResource(key);
        var isHollow = _arrowStyle == ArrowStyle.Hollow;
        ArrowToolIcon.Fill = isHollow ? System.Windows.Media.Brushes.Transparent : null;
        ArrowToolIcon.Stroke = isHollow ? null : System.Windows.Media.Brushes.Transparent;
        ArrowToolIcon.SetResourceReference(
            isHollow ? System.Windows.Shapes.Path.StrokeProperty : System.Windows.Shapes.Path.FillProperty,
            "EditorToolbarIconBrush");
        ArrowToolIcon.StrokeThickness = isHollow ? 1.8 : 0;
        ArrowToolButton.ToolTip = string.Concat(
            isCurved ? "弧形" : "直线",
            _arrowStyle == ArrowStyle.Hollow ? "空心箭头" : "实心箭头");
    }

    private void UpdateShapeButtonPresentation()
    {
        var isEllipse = _shapeToolMode == ShapeToolMode.Ellipse;
        ShapeToolButton.Tag = isEllipse
            ? EditorTool.Ellipse.ToString()
            : EditorTool.Rectangle.ToString();
        ShapeToolIcon.Data = (Geometry)FindResource(
            isEllipse ? "EllipseIconGeometry" : "RectangleIconGeometry");
        ShapeToolButton.ToolTip = isEllipse
            ? "矩形/椭圆（当前：椭圆）"
            : "矩形/椭圆（当前：矩形）";
    }

    private static void OpenContextMenu(FrameworkElement target)
    {
        if (target.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetCapture(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr GetCapture();
    }

    private void ApplyPreferences(AppSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        _arrowStyle = settings.ArrowStyle;
        _arrowToolMode = settings.ArrowToolMode;
        _shapeToolMode = settings.ShapeToolMode;
        foreach (var setting in settings.AnnotationToolSettings)
        {
            if (Enum.TryParse<EditorTool>(setting.Tool, true, out var tool))
            {
                _persistedTools.Add(tool);
            }
        }
        _lastTool = ToEditorTool(
            AnnotationToolMode.Rectangle,
            settings.ArrowToolMode,
            settings.ShapeToolMode);
        _defaultStrokeWidth = Math.Clamp(settings.DefaultStrokeWidth, 1, 12);
        _lastStrokeWidth = _defaultStrokeWidth;
        _customColorPalette = NormalizeCustomColorPalette(
            settings.CustomColorPalette);
        var preferredColor = string.IsNullOrWhiteSpace(settings.CustomStrokeColor)
            ? settings.DefaultStrokeColor
            : settings.CustomStrokeColor;
        if (!TryParseColor(preferredColor, out var color))
        {
            return;
        }

        _selectedColor = color;
        _defaultColor = color;
        _customColor = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        CustomColorButton.Background = brush;
    }

    private void ApplySelectedToolPreferences()
    {
        _selectedColor = _persistedTools.Contains(_lastTool)
            ? AnnotationToolPreferences.GetColor(_lastTool, _defaultColor)
            : _defaultColor;
        _lastStrokeWidth = Math.Clamp(
            _persistedTools.Contains(_lastTool)
                ? AnnotationToolPreferences.GetWidth(
                    _lastTool,
                    _defaultStrokeWidth)
                : _defaultStrokeWidth,
            1,
            12);

        _isApplyingToolPreferences = true;
        try
        {
            if (StrokeWidthSlider is not null)
            {
                if (!double.Equals(StrokeWidthSlider.Value, _lastStrokeWidth))
                {
                    _programmaticWidthChangesPending++;
                }
                StrokeWidthSlider.Value = _lastStrokeWidth;
            }
        }
        finally
        {
            _isApplyingToolPreferences = false;
        }

        if (StrokeWidthText is not null)
        {
            StrokeWidthText.Text = $"{_lastStrokeWidth:0.#} px";
        }

        if (CustomColorButton is not null)
        {
            var brush = new SolidColorBrush(_selectedColor);
            brush.Freeze();
            CustomColorButton.Background = brush;
        }

        UpdateSelectedColorButton(ResolveSelectedColorButton(_selectedColor));
    }

    private static EditorTool ToEditorTool(
        AnnotationToolMode lastTool,
        ArrowToolMode arrowToolMode,
        ShapeToolMode shapeToolMode) =>
        lastTool switch
        {
            AnnotationToolMode.Rectangle or AnnotationToolMode.Ellipse =>
                shapeToolMode == ShapeToolMode.Ellipse
                    ? EditorTool.Ellipse
                    : EditorTool.Rectangle,
            AnnotationToolMode.StraightArrow or AnnotationToolMode.CurvedArrow =>
                arrowToolMode == ArrowToolMode.Curved
                    ? EditorTool.CurvedArrow
                    : EditorTool.Arrow,
            AnnotationToolMode.Emoji => EditorTool.Emoji,
            AnnotationToolMode.Number => EditorTool.Number,
            AnnotationToolMode.Brush => EditorTool.Brush,
            AnnotationToolMode.Mosaic => EditorTool.Mosaic,
            AnnotationToolMode.Text => EditorTool.Text,
            _ => EditorTool.Rectangle,
        };

    private static AnnotationToolMode ToAnnotationToolMode(EditorTool tool) =>
        tool switch
        {
            EditorTool.Ellipse => AnnotationToolMode.Ellipse,
            EditorTool.Arrow => AnnotationToolMode.StraightArrow,
            EditorTool.CurvedArrow => AnnotationToolMode.CurvedArrow,
            EditorTool.Emoji => AnnotationToolMode.Emoji,
            EditorTool.Number => AnnotationToolMode.Number,
            EditorTool.Brush => AnnotationToolMode.Brush,
            EditorTool.Mosaic => AnnotationToolMode.Mosaic,
            EditorTool.Text => AnnotationToolMode.Text,
            _ => AnnotationToolMode.Rectangle,
        };

    private void ApplyToolbarFeatureVisibility(
        IEnumerable<CaptureToolbarFeature>? configuredFeatures)
    {
        var features = (configuredFeatures ??
                Enum.GetValues<CaptureToolbarFeature>())
            .Where(Enum.IsDefined)
            .ToHashSet();
        SetVisibility(ShapeToolButton, CaptureToolbarFeature.Shape);
        SetVisibility(ArrowToolButton, CaptureToolbarFeature.Arrow);
        SetVisibility(EmojiToolButton, CaptureToolbarFeature.Emoji);
        SetVisibility(NumberToolButton, CaptureToolbarFeature.Number);
        SetVisibility(BrushToolButton, CaptureToolbarFeature.Brush);
        SetVisibility(TextToolButton, CaptureToolbarFeature.Text);
        SetVisibility(MosaicToolButton, CaptureToolbarFeature.Mosaic);
        SetVisibility(SaveButton, CaptureToolbarFeature.Save);
        SetVisibility(OcrButton, CaptureToolbarFeature.TextRecognition);
        SetVisibility(CopyTableButton, CaptureToolbarFeature.CopyTable);
        SetVisibility(CopyTextButton, CaptureToolbarFeature.CopyRecognizedText);
        SetVisibility(TranslateActionButton, CaptureToolbarFeature.Translation);
        SetVisibility(PrivacyButton, CaptureToolbarFeature.PrivacyRedaction);
        SetVisibility(UndoButton, CaptureToolbarFeature.UndoRedo);

        var editorButtons = new (WpfRadioButton Button, EditorTool Tool)[]
        {
            (ShapeToolButton, EditorTool.Rectangle),
            (ArrowToolButton, EditorTool.Arrow),
            (EmojiToolButton, EditorTool.Emoji),
            (NumberToolButton, EditorTool.Number),
            (BrushToolButton, EditorTool.Brush),
            (TextToolButton, EditorTool.Text),
            (MosaicToolButton, EditorTool.Mosaic),
        };
        var selected = editorButtons.FirstOrDefault(item => IsElementVisible(item.Button));
        if (selected.Button is not null &&
            !editorButtons.Any(item =>
                (item.Tool == _lastTool ||
                 (item.Tool == EditorTool.Arrow &&
                  _lastTool == EditorTool.CurvedArrow)) &&
                IsElementVisible(item.Button)))
        {
            _lastTool = selected.Tool;
        }
        _colorOptionsAvailable = editorButtons.Any(item => IsElementVisible(item.Button));
        UpdateEmojiPaletteVisibility();
        UpdateToolbarSeparators();
        return;

        void SetVisibility(FrameworkElement element, CaptureToolbarFeature feature)
        {
            element.Visibility = features.Contains(feature)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ApplyToolbarLayout(
        IEnumerable<CaptureToolbarFeature>? configuredOrder,
        CaptureToolbarRowCount rowCount)
    {
        var featureElements = new Dictionary<
            CaptureToolbarFeature,
            FrameworkElement[]>
        {
            [CaptureToolbarFeature.Shape] = [ShapeToolButton],
            [CaptureToolbarFeature.Arrow] = [ArrowToolButton],
            [CaptureToolbarFeature.Emoji] = [EmojiToolButton],
            [CaptureToolbarFeature.Number] = [NumberToolButton],
            [CaptureToolbarFeature.Brush] = [BrushToolButton],
            [CaptureToolbarFeature.Text] = [TextToolButton],
            [CaptureToolbarFeature.Mosaic] = [MosaicToolButton],
            [CaptureToolbarFeature.Save] = [SaveButton],
            [CaptureToolbarFeature.TextRecognition] = [OcrButton],
            [CaptureToolbarFeature.CopyTable] = [CopyTableButton],
            [CaptureToolbarFeature.CopyRecognizedText] = [CopyTextButton],
            [CaptureToolbarFeature.Translation] = [TranslateActionButton],
            [CaptureToolbarFeature.PrivacyRedaction] = [PrivacyButton],
            [CaptureToolbarFeature.UndoRedo] = [UndoButton],
        };
        var order = (configuredOrder ?? Enum.GetValues<CaptureToolbarFeature>())
            .Where(Enum.IsDefined)
            .Distinct()
            .ToList();
        foreach (var feature in Enum.GetValues<CaptureToolbarFeature>())
        {
            if (!order.Contains(feature))
            {
                order.Add(feature);
            }
        }

        var annotationFeatures = new HashSet<CaptureToolbarFeature>
        {
            CaptureToolbarFeature.Shape,
            CaptureToolbarFeature.Arrow,
            CaptureToolbarFeature.Emoji,
            CaptureToolbarFeature.Number,
            CaptureToolbarFeature.Brush,
            CaptureToolbarFeature.Text,
            CaptureToolbarFeature.Mosaic,
        };
        var actionFeatures = new HashSet<CaptureToolbarFeature>
        {
            CaptureToolbarFeature.Save,
            CaptureToolbarFeature.TextRecognition,
            CaptureToolbarFeature.CopyTable,
            CaptureToolbarFeature.CopyRecognizedText,
            CaptureToolbarFeature.Translation,
            CaptureToolbarFeature.PrivacyRedaction,
        };
        var tokens = new List<FrameworkElement>();
        AddFeatures(annotationFeatures);
        tokens.Add(ToolActionSeparator);
        AddFeatures(actionFeatures);
        tokens.Add(ActionHistorySeparator);
        tokens.Add(CropToolButton);
        AddFeatures(new HashSet<CaptureToolbarFeature>
        {
            CaptureToolbarFeature.UndoRedo,
        });
        tokens.Add(HistoryFinishSeparator);
        tokens.Add(CancelEditButton);
        tokens.Add(EditApplyButton);

        EditToolsRow1.Children.Clear();
        EditToolsRow2.Children.Clear();
        RestoreSeparatorLayout();
        if (rowCount != CaptureToolbarRowCount.Two)
        {
            AddToolbarRow(EditToolsRow1, tokens);
            EditToolsRow2.Visibility = Visibility.Collapsed;
            return;
        }

        var split = FindToolbarRowSplit(tokens);
        AddToolbarRow(EditToolsRow1, tokens.Take(split));
        AddToolbarRow(EditToolsRow2, tokens.Skip(split));
        EditToolsRow2.Visibility = Visibility.Visible;
        return;

        void AddFeatures(IReadOnlySet<CaptureToolbarFeature> group)
        {
            foreach (var feature in order.Where(group.Contains))
            {
                if (featureElements.TryGetValue(feature, out var elements))
                {
                    tokens.AddRange(elements);
                }
            }
        }
    }

    private int FindToolbarRowSplit(IReadOnlyList<FrameworkElement> elements)
    {
        var visibleCount = elements.Count(
            element => element.Visibility == Visibility.Visible &&
                !IsToolbarSeparator(element));
        var target = Math.Max(1, (visibleCount + 1) / 2);
        var seen = 0;
        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index].Visibility == Visibility.Visible &&
                !IsToolbarSeparator(elements[index]))
            {
                seen++;
            }

            if (seen >= target)
            {
                return index + 1;
            }
        }

        return elements.Count;
    }

    private void AddToolbarRow(
        System.Windows.Controls.Panel row,
        IEnumerable<FrameworkElement> rowElements)
    {
        var elements = rowElements.ToList();
        var firstVisible = elements.FindIndex(
            element => element.Visibility == Visibility.Visible);
        var lastVisible = elements.FindLastIndex(
            element => element.Visibility == Visibility.Visible);
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            row.Children.Add(element);
            if (IsToolbarSeparator(element) &&
                (index == firstVisible || index == lastVisible))
            {
                element.Width = 0;
                element.Margin = new Thickness(0);
                element.Opacity = 0;
            }
        }
    }

    private void RestoreSeparatorLayout()
    {
        foreach (var separator in new[]
                 {
                     ToolActionSeparator,
                     ActionHistorySeparator,
                     HistoryFinishSeparator,
                 })
        {
            separator.ClearValue(WidthProperty);
            separator.ClearValue(MarginProperty);
            separator.ClearValue(OpacityProperty);
        }
    }

    private bool IsToolbarSeparator(FrameworkElement element) =>
        ReferenceEquals(element, ToolActionSeparator) ||
        ReferenceEquals(element, ActionHistorySeparator) ||
        ReferenceEquals(element, HistoryFinishSeparator);

    private void UpdateToolbarSeparators()
    {
        var hasEditorTools = new FrameworkElement[]
        {
            ShapeToolButton,
            ArrowToolButton,
            EmojiToolButton,
            NumberToolButton,
            BrushToolButton,
            TextToolButton,
            MosaicToolButton,
        }.Any(IsElementVisible);
        var hasActions = new FrameworkElement[]
        {
            SaveButton,
            OcrButton,
            CopyTableButton,
            CopyTextButton,
            TranslateActionButton,
            PrivacyButton,
        }.Any(IsElementVisible);
        var hasHistory = IsElementVisible(UndoButton);
        var hasCropOrHistory = IsElementVisible(CropToolButton) || hasHistory;
        ToolActionSeparator.Visibility = hasEditorTools && (hasActions || hasHistory)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ActionHistorySeparator.Visibility = hasActions && hasHistory
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistoryFinishSeparator.Visibility = hasEditorTools || hasActions || hasCropOrHistory
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private WpfButton ResolveSelectedColorButton(WpfColor color)
    {
        if (TryParseColor(RedColorButton.Tag as string, out var red) && color == red)
        {
            return RedColorButton;
        }
        if (TryParseColor(CyanColorButton.Tag as string, out var cyan) && color == cyan)
        {
            return CyanColorButton;
        }
        if (TryParseColor(DarkColorButton.Tag as string, out var dark) && color == dark)
        {
            return DarkColorButton;
        }
        return CustomColorButton;
    }

    private void UpdateSelectedColorButton(WpfButton selectedButton)
    {
        foreach (var button in new[]
                 {
                     RedColorButton,
                     CyanColorButton,
                     DarkColorButton,
                     CustomColorButton,
                 })
        {
            button.BorderBrush = new SolidColorBrush(
                WpfColor.FromArgb(0x8A, 0xE1, 0xD8, 0xD0));
            button.BorderThickness = new Thickness(1);
        }
        selectedButton.BorderBrush = System.Windows.Media.Brushes.White;
        selectedButton.BorderThickness = new Thickness(2);
    }

    private static int[] NormalizeCustomColorPalette(IEnumerable<int>? colors) =>
        (colors ?? [])
            .Where(color => color is >= 0 and <= 0xFFFFFF)
            .Distinct()
            .Take(16)
            .ToArray();

    private static bool TryParseColor(string? text, out WpfColor color)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) &&
                WpfColorConverter.ConvertFromString(text.Trim()) is WpfColor parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException)
        {
        }
        color = default;
        return false;
    }

    private static int ToColorValue(WpfColor color) =>
        color.R << 16 | color.G << 8 | color.B;

    private static string FormatColor(WpfColor color) =>
        color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool IsElementVisible(FrameworkElement element) =>
        element.Visibility == Visibility.Visible;

    private static void ApplyThemedContextMenu(ContextMenu? menu)
    {
        if (menu is null)
        {
            return;
        }
        if (System.Windows.Application.Current?.TryFindResource(
                "ThemedContextMenuStyle") is Style menuStyle)
        {
            menu.Style = menuStyle;
        }
        if (System.Windows.Application.Current?.TryFindResource(
                "ThemedMenuItemStyle") is not Style itemStyle)
        {
            return;
        }
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Style = itemStyle;
        }
    }
}
