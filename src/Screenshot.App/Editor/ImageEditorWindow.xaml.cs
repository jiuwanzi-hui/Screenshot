using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;
using Screenshot.App.Core;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfRadioButton = System.Windows.Controls.RadioButton;

namespace Screenshot.App.Editor;

public partial class ImageEditorWindow : Window
{
    private CapturedImage _capturedImage;
    private readonly string _saveDirectory;
    private readonly Action<ArrowStyle>? _arrowStyleChanged;
    private readonly Action<ArrowToolMode>? _arrowToolModeChanged;
    private readonly Action<ShapeToolMode>? _shapeToolModeChanged;
    private readonly Action<AnnotationToolMode>? _lastAnnotationToolChanged;
    private readonly Action<string>? _customStrokeColorChanged;
    private readonly Action<int[]>? _customColorPaletteChanged;
    private readonly Action<BitmapSource>? _appliedImage;
    private ArrowStyle _arrowStyle;
    private ArrowToolMode _arrowToolMode;
    private ShapeToolMode _shapeToolMode;
    private EditorTool _selectedTool = EditorTool.Rectangle;
    private WpfColor _selectedColor = WpfColor.FromRgb(46, 175, 165);
    private WpfColor? _customColor;
    private int[] _customColorPalette;
    private double _displayWidth;
    private double _displayHeight;
    private bool _isInitialized;
    private bool _isClosed;
    private bool _isInitializing = true;

    public ImageEditorWindow(
        CapturedImage capturedImage,
        string saveDirectory,
        ArrowStyle arrowStyle = ArrowStyle.Filled,
        Action<ArrowStyle>? arrowStyleChanged = null,
        string? customStrokeColor = null,
        Action<string>? customStrokeColorChanged = null,
        int[]? customColorPalette = null,
        Action<int[]>? customColorPaletteChanged = null,
        Action<BitmapSource>? appliedImage = null,
        ArrowToolMode arrowToolMode = ArrowToolMode.Straight,
        Action<ArrowToolMode>? arrowToolModeChanged = null,
        ShapeToolMode shapeToolMode = ShapeToolMode.Rectangle,
        Action<ShapeToolMode>? shapeToolModeChanged = null,
        AnnotationToolMode lastAnnotationTool = AnnotationToolMode.Rectangle,
        Action<AnnotationToolMode>? lastAnnotationToolChanged = null)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);

        _capturedImage = capturedImage;
        _saveDirectory = saveDirectory;
        _arrowStyle = Enum.IsDefined(arrowStyle)
            ? arrowStyle
            : ArrowStyle.Filled;
        _arrowToolMode = Enum.IsDefined(arrowToolMode)
            ? arrowToolMode
            : ArrowToolMode.Straight;
        _shapeToolMode = Enum.IsDefined(shapeToolMode)
            ? shapeToolMode
            : ShapeToolMode.Rectangle;
        _arrowStyleChanged = arrowStyleChanged;
        _arrowToolModeChanged = arrowToolModeChanged;
        _shapeToolModeChanged = shapeToolModeChanged;
        _lastAnnotationToolChanged = lastAnnotationToolChanged;
        _selectedTool = ToEditorTool(
            AnnotationToolMode.Rectangle,
            arrowToolMode,
            shapeToolMode);
        _customStrokeColorChanged = customStrokeColorChanged;
        _customColorPalette = NormalizeCustomColorPalette(customColorPalette);
        _customColorPaletteChanged = customColorPaletteChanged;
        _appliedImage = appliedImage;

        InitializeComponent();
        ApplyButton.Visibility = _appliedImage is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        ApplyThemedContextMenu(ShapeToolButton.ContextMenu);
        ApplyThemedContextMenu(ArrowToolButton.ContextMenu);
        if (_selectedTool == EditorTool.CurvedArrow)
        {
            ShapeToolButton.IsChecked = false;
            ArrowToolButton.Tag = EditorTool.CurvedArrow.ToString();
            ArrowToolButton.IsChecked = true;
        }
        else if (_selectedTool is EditorTool.Rectangle or EditorTool.Ellipse)
        {
            ShapeToolButton.IsChecked = true;
            ShapeToolIcon.Data = (Geometry)FindResource(
                _selectedTool == EditorTool.Ellipse
                    ? "EllipseIconGeometry"
                    : "RectangleIconGeometry");
            ShapeToolLabel.Text = _selectedTool == EditorTool.Ellipse ? "椭圆" : "矩形";
        }
        else
        {
            GetToolButton(_selectedTool).IsChecked = true;
        }
        UpdateShapeMenuState();
        UpdateArrowMenuState();
        UpdateShapeButtonPresentation();
        UpdateArrowButtonPresentation();
        EditorCanvas.SelectArrowStyle(
            _arrowStyle,
            updateSelectedAnnotation: false);
        ApplySavedCustomColor(customStrokeColor);
        UpdateToolOptionPanels();
        _isInitializing = false;
        WindowPlacementService.Track(this, WindowPlacementKeys.ImageEditor);
        PopulateEmojiPalette();
        EditorCanvas.HistoryChanged += OnEditorHistoryChanged;
        EditorCanvas.AnnotationSelectionChanged += OnAnnotationSelectionChanged;

        EditorCanvas.Visibility = Visibility.Hidden;
        StatusText.Text = "正在准备编辑画布...";
        Loaded += OnEditorLoaded;
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        if (!_isInitializing)
        {
            _lastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(_selectedTool));
        }
        Loaded -= OnEditorLoaded;
        EditorCanvas.HistoryChanged -= OnEditorHistoryChanged;
        EditorCanvas.AnnotationSelectionChanged -= OnAnnotationSelectionChanged;
        _capturedImage.Dispose();
        base.OnClosed(e);
        Core.MemoryFootprint.TrimAfterHeavyOperation();
    }

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnEditorLoaded;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            InitializeEditorCanvas);
    }

    private void InitializeEditorCanvas()
    {
        if (_isClosed || _isInitialized)
        {
            return;
        }

        (_displayWidth, _displayHeight) = GetWidthFilledDisplaySize(
            _capturedImage.Preview.PixelWidth,
            _capturedImage.Preview.PixelHeight);
        EditorCanvas.Initialize(
            _capturedImage,
            _displayWidth,
            _displayHeight);
        EditorCanvas.SelectTool(_selectedTool);
        EditorCanvas.Visibility = Visibility.Visible;
        _isInitialized = true;
        StatusText.Text = "可以开始编辑。";
        UpdateEditorViewportSize();
        UpdateUndoRedoAvailability();
        // Loading a tall capture materializes transient decode and conversion
        // copies; return them so an open editor only holds its live surfaces.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            Core.MemoryFootprint.TrimAfterHeavyOperation);
    }

    private void OnEditorHistoryChanged(object? sender, EventArgs e)
    {
        UpdateUndoRedoAvailability();
    }

    private void OnAnnotationSelectionChanged(object? sender, EventArgs e)
    {
        StatusText.Text = EditorCanvas.HasSelectedAnnotation
            ? "已选中标注：可拖动或缩放，按 Delete 删除。"
            : "可以开始编辑。";
    }

    private void OnShapeMenuArrowMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ShapeToolButton.ContextMenu is not { } menu)
        {
            return;
        }

        UpdateShapeMenuState();
        menu.PlacementTarget = ShapeToolButton;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnArrowMenuArrowMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ArrowToolButton.ContextMenu is not { } menu)
        {
            return;
        }

        UpdateArrowMenuState();
        menu.PlacementTarget = ArrowToolButton;
        menu.IsOpen = true;
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
            !Enum.TryParse<ArrowStyle>(parts[1], out var arrowStyle) ||
            tool is not (EditorTool.Arrow or EditorTool.CurvedArrow))
        {
            return;
        }

        _selectedTool = tool;
        if (tool is EditorTool.Arrow or EditorTool.CurvedArrow)
        {
            _arrowToolMode = tool == EditorTool.CurvedArrow
                ? ArrowToolMode.Curved
                : ArrowToolMode.Straight;
            _arrowToolModeChanged?.Invoke(
                tool == EditorTool.CurvedArrow
                    ? ArrowToolMode.Curved
                    : ArrowToolMode.Straight);
        }
        _arrowStyle = arrowStyle;
        _arrowToolMode = tool == EditorTool.CurvedArrow
            ? ArrowToolMode.Curved
            : ArrowToolMode.Straight;
        ArrowToolButton.Tag = tool.ToString();
        EditorCanvas.SelectArrowStyle(arrowStyle);
        EditorCanvas.SelectTool(tool);
        ArrowToolButton.IsChecked = true;
        UpdateArrowButtonPresentation();
        UpdateArrowMenuState();
        _arrowStyleChanged?.Invoke(arrowStyle);
        _lastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(tool));
        EditorCanvas.Focus();
    }

    private static void ApplyThemedContextMenu(ContextMenu menu)
    {
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

    private void OnShapeMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string toolName } ||
            !Enum.TryParse<EditorTool>(toolName, out var tool) ||
            tool is not (EditorTool.Rectangle or EditorTool.Ellipse))
        {
            return;
        }

        ShapeToolButton.Tag = toolName;
        ShapeToolIcon.Data = (Geometry)FindResource(
            tool == EditorTool.Ellipse
                ? "EllipseIconGeometry"
                : "RectangleIconGeometry");
        ShapeToolLabel.Text = tool == EditorTool.Ellipse ? "椭圆" : "矩形";
        ShapeToolButton.ToolTip = $"{ShapeToolLabel.Text}标注";
        ShapeToolButton.IsChecked = true;
        _selectedTool = tool;
        _shapeToolMode = tool == EditorTool.Ellipse
            ? ShapeToolMode.Ellipse
            : ShapeToolMode.Rectangle;
        _shapeToolModeChanged?.Invoke(
            tool == EditorTool.Ellipse
                ? ShapeToolMode.Ellipse
                : ShapeToolMode.Rectangle);
        _lastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(tool));
        UpdateShapeMenuState();
        UpdateStrokeWidthText();
        if (_isInitialized)
        {
            EditorCanvas.SelectTool(tool);
            EditorCanvas.Focus();
        }
    }

    private void UpdateShapeMenuState()
    {
        var selected = _shapeToolMode == ShapeToolMode.Ellipse
            ? EditorTool.Ellipse
            : EditorTool.Rectangle;
        RectangleShapeMenuItem.IsChecked = selected == EditorTool.Rectangle;
        EllipseShapeMenuItem.IsChecked = selected == EditorTool.Ellipse;
    }

    private void UpdateArrowMenuState()
    {
        var selected = _arrowToolMode == ArrowToolMode.Curved
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

    private void UpdateArrowButtonPresentation()
    {
        var isCurved = _arrowToolMode == ArrowToolMode.Curved;
        ArrowToolButton.Tag = isCurved
            ? EditorTool.CurvedArrow.ToString()
            : EditorTool.Arrow.ToString();
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
            "ImageEditorToolbarIconBrush");
        ArrowToolIcon.StrokeThickness = isHollow ? 1.8 : 0;
        ArrowToolLabel.Text = string.Concat(isCurved ? "弧形" : "直线", _arrowStyle == ArrowStyle.Hollow ? "空心" : "实心");
        ArrowToolButton.ToolTip = string.Concat(
            isCurved ? "弧形" : "直线",
            _arrowStyle == ArrowStyle.Hollow ? "空心箭头标注" : "实心箭头标注");
    }

    private void UpdateShapeButtonPresentation()
    {
        var isEllipse = _shapeToolMode == ShapeToolMode.Ellipse;
        ShapeToolButton.Tag = isEllipse
            ? EditorTool.Ellipse.ToString()
            : EditorTool.Rectangle.ToString();
        ShapeToolIcon.Data = (Geometry)FindResource(
            isEllipse ? "EllipseIconGeometry" : "RectangleIconGeometry");
        ShapeToolLabel.Text = isEllipse ? "椭圆" : "矩形";
    }

    private void OnToolSelected(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (sender is WpfRadioButton { Tag: string toolName } &&
            Enum.TryParse<EditorTool>(toolName, out var tool))
        {
            _selectedTool = tool;
            _lastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(tool));
            UpdateStrokeWidthText();
            UpdateToolOptionPanels();

            if (_isInitialized)
            {
                EditorCanvas.SelectTool(tool);
            }
        }
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

    private WpfRadioButton GetToolButton(EditorTool tool) => tool switch
    {
        EditorTool.Emoji => EmojiToolButton,
        EditorTool.Number => NumberToolButton,
        EditorTool.Brush => BrushToolButton,
        EditorTool.Mosaic => MosaicToolButton,
        EditorTool.Text => TextToolButton,
        _ => ShapeToolButton,
    };

    private void UpdateToolOptionPanels()
    {
        if (EmojiPaletteScroll is null || StrokeOptionsPanel is null)
        {
            return;
        }

        var isEmoji = _selectedTool == EditorTool.Emoji;
        EmojiPaletteScroll.Visibility = isEmoji ? Visibility.Visible : Visibility.Collapsed;
        StrokeOptionsPanel.Visibility = isEmoji ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PopulateEmojiPalette()
    {
        foreach (var emoji in EmojiStickerCatalog.All)
        {
            var button = new WpfButton
            {
                Tag = emoji,
                ToolTip = emoji,
                Style = (Style)FindResource("GlassButton"),
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
        if (sender is not WpfButton { Tag: string sticker } ||
            string.IsNullOrWhiteSpace(sticker))
        {
            return;
        }

        EditorCanvas.SelectEmoji(sticker);
        EmojiToolButton.IsChecked = true;
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string colorValue } &&
            WpfColorConverter.ConvertFromString(colorValue) is WpfColor color)
        {
            _selectedColor = color;
            UpdateSelectedColorButton((WpfButton)sender);
            EditorCanvas.SelectColor(color);
        }
    }

    private void OnCustomColorClick(object sender, RoutedEventArgs e)
    {
        var seedColor = _customColor ?? _selectedColor;
        var picker = new ThemeColorPickerWindow(seedColor, _customColorPalette)
        {
            Owner = this,
        };
        picker.ColorSelected += (_, color) => ApplyCustomColor(color);
        picker.Show();
    }

    private void ApplyCustomColor(WpfColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _selectedColor = color;
        _customColor = color;
        _customColorPalette = NormalizeCustomColorPalette(_customColorPalette.Append(
            color.R << 16 | color.G << 8 | color.B));
        CustomColorButton.Background = brush;
        UpdateSelectedColorButton(CustomColorButton);
        EditorCanvas.SelectColor(color);
        _customStrokeColorChanged?.Invoke(FormatColorText(color));
        _customColorPaletteChanged?.Invoke(_customColorPalette.ToArray());
    }

    private void ApplySavedCustomColor(string? customStrokeColor)
    {
        if (string.IsNullOrWhiteSpace(customStrokeColor))
        {
            return;
        }

        WpfColor color;
        try
        {
            if (WpfColorConverter.ConvertFromString(
                    customStrokeColor.Trim()) is not WpfColor parsed)
            {
                return;
            }

            color = parsed;
        }
        catch (FormatException)
        {
            return;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _customColor = color;
        CustomColorButton.Background = brush;
    }

    private static string FormatColorText(WpfColor color)
    {
        return color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static int[] NormalizeCustomColorPalette(IEnumerable<int>? colors)
    {
        return (colors ?? [])
            .Where(color => color is >= 0 and <= 0xFFFFFF)
            .Take(16)
            .ToArray();
    }

    private void OnStrokeWidthChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateStrokeWidthText(e.NewValue);

        if (_isInitialized)
        {
            EditorCanvas.SetStrokeWidth(e.NewValue);
        }
    }

    private void UpdateStrokeWidthText(double? value = null)
    {
        if (StrokeWidthText is null)
        {
            return;
        }

        var rawWidth = value ?? StrokeWidthSlider?.Value ?? 3;
        var displayedWidth = _selectedTool == EditorTool.Mosaic
            ? Math.Max(8, rawWidth * 4)
            : rawWidth;
        StrokeWidthText.Text = $"{displayedWidth:0} px";
    }

    private void OnEditorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isInitialized ||
            (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var zoomFactor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        EditorCanvas.SetZoom(EditorCanvas.Zoom * zoomFactor);
        UpdateEditorViewportSize();
        StatusText.Text = $"缩放 {EditorCanvas.Zoom * 100:0}%";
        e.Handled = true;
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }
        EditorCanvas.Undo();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }
        EditorCanvas.Redo();
    }

    private void OnCropClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
        {
            StatusText.Text = "编辑画布仍在准备中。";
            return;
        }

        try
        {
            var renderedImage = EditorCanvas.RenderEditedImage();
            var cropWindow = new ImageCropWindow(renderedImage)
            {
                Owner = this,
            };
            if (cropWindow.ShowDialog() != true || cropWindow.CroppedImage is null)
            {
                return;
            }

            if (cropWindow.CroppedImage.PixelWidth == renderedImage.PixelWidth &&
                cropWindow.CroppedImage.PixelHeight == renderedImage.PixelHeight)
            {
                StatusText.Text = "四边均未裁剪，图片保持不变。";
                return;
            }

            var replacement = CapturedImage.FromBitmapSource(
                cropWindow.CroppedImage);
            var previous = _capturedImage;
            _capturedImage = replacement;
            (_displayWidth, _displayHeight) = GetWidthFilledDisplaySize(
                replacement.Preview.PixelWidth,
                replacement.Preview.PixelHeight);
            EditorCanvas.Initialize(replacement, _displayWidth, _displayHeight);
            EditorCanvas.SelectTool(_selectedTool);
            UpdateEditorViewportSize();
            previous.Dispose();
            StatusText.Text =
                $"已裁剪为 {replacement.Preview.PixelWidth} x " +
                $"{replacement.Preview.PixelHeight}，可继续编辑。";
        }
        catch
        {
            StatusText.Text = "裁剪失败，请减小图片尺寸后重试。";
        }
    }

    private void OnTopmostClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        TopmostButton.ToolTip = Topmost ? "取消置顶" : "置顶";
        TopmostGlyph.Foreground = Topmost
            ? new SolidColorBrush(WpfColor.FromRgb(143, 231, 222))
            : System.Windows.Media.Brushes.White;
        StatusText.Text = Topmost ? "编辑窗口已置顶。" : "已取消置顶。";
    }

    private void UpdateSelectedColorButton(WpfButton selectedButton)
    {
        foreach (var button in new[]
                 {
                     CyanColorButton,
                     RedColorButton,
                     LightColorButton,
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

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
        {
            StatusText.Text = "编辑画布仍在准备中。";
            return;
        }
        try
        {
            var renderedImage = EditorCanvas.RenderEditedImage();
            // Use the same native CF_DIB path as the capture window. WPF's
            // delayed OLE bitmap provider is unreliable for tall scroll
            // captures and reports a generic copy failure when the editor
            // image is large or the clipboard is briefly occupied.
            await ClipboardImageService.SetImageAsync(renderedImage);
            StatusText.Text = "已复制编辑后的图片。";
        }
        catch
        {
            StatusText.Text = "无法复制图片，请重试。";
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
        {
            StatusText.Text = "编辑画布仍在准备中。";
            return;
        }
        try
        {
            var renderedImage = EditorCanvas.RenderEditedImage();
            var savedPath = CaptureFileService.SaveAsPng(
                renderedImage,
                _saveDirectory);
            StatusText.Text = $"已保存到 {savedPath}";
        }
        catch
        {
            StatusText.Text = "保存失败，请检查保存位置和权限。";
        }
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || _appliedImage is null)
        {
            return;
        }

        try
        {
            var renderedImage = EditorCanvas.RenderEditedImage();
            _appliedImage(renderedImage);
            Close();
        }
        catch
        {
            StatusText.Text = "无法应用编辑结果，请重试。";
        }
    }

    private void UpdateUndoRedoAvailability()
    {
        UndoButton.IsEnabled = EditorCanvas.CanUndo;
        RedoButton.IsEnabled = EditorCanvas.CanRedo;
    }

    private void UpdateEditorViewportSize()
    {
        EditorViewport.Width = Math.Max(1, EditorCanvas.DisplayWidth);
        EditorViewport.Height = Math.Max(1, EditorCanvas.DisplayHeight);
    }

    private (double Width, double Height) GetWidthFilledDisplaySize(
        int pixelWidth,
        int pixelHeight)
    {
        var viewportWidth = EditorScrollViewer.ViewportWidth;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 1)
        {
            viewportWidth = Math.Max(1, EditorScrollViewer.ActualWidth - 4);
        }

        var viewportHeight = Math.Max(1, EditorScrollViewer.ViewportHeight);
        var provisionalHeight = pixelHeight *
                                (viewportWidth / Math.Max(1, pixelWidth));
        if (provisionalHeight > viewportHeight)
        {
            viewportWidth = Math.Max(
                1,
                viewportWidth - SystemParameters.VerticalScrollBarWidth - 2);
        }

        var scale = viewportWidth / Math.Max(1, pixelWidth);

        return (
            Math.Max(1, viewportWidth),
            Math.Max(1, Math.Round(pixelHeight * scale)));
    }
}
