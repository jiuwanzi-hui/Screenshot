using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfButton = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfSlider = System.Windows.Controls.Slider;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace Screenshot.App.Pin;

public partial class PinnedImageWindow : Window
{
    private const double ShadowInset = 24;
    private const double HeaderAndShadowHeight = 54;
    private CapturedImage _capturedImage;
    private readonly Func<CapturedImage, Task<OcrRecognitionResult>>?
        _recognizeTextAsync;
    private readonly Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
        _translateTextAsync;
    private readonly Func<AppSettings>? _settingsProvider;
    private readonly Action<string>? _customStrokeColorChanged;
    private readonly Action<int[]>? _customColorPaletteChanged;
    private readonly Action<ArrowStyle>? _arrowStyleChanged;
    private readonly Action<ArrowToolMode>? _arrowToolModeChanged;
    private readonly Action<ShapeToolMode>? _shapeToolModeChanged;
    private readonly Action<AnnotationToolMode>? _lastAnnotationToolChanged;
    private OcrRecognitionResult? _recognition;
    private IReadOnlyList<OcrTextRegion> _displayedRegions = [];
    private IReadOnlyList<OcrTextRegion> _translatedRegions = [];
    private Task _textRecognitionTask = Task.CompletedTask;
    private bool _isShowingTranslation;
    private bool _isClosed;
    private CapturedImage? _inlineEditorImage;
    private PinnedImageEditorToolbarWindow? _editorToolbar;
    private Int32Rect _cropRect;
    private Rect _renderedImageBounds;
    private WpfPoint? _cropDragStart;
    private bool _isEditorMode;
    private bool _isCropMode;
    private BitmapSource? _groupingPreview;
    private bool _isPanningInlineEditor;
    private WpfPoint _inlineEditorPanStartPoint;
    private double _inlineEditorPanStartHorizontalOffset;
    private double _inlineEditorPanStartVerticalOffset;
    private PinnedWindowMinimization.Bounds? _restoreBounds;
    private bool _isMinimized;
    private double _restoreMinWidth;
    private double _restoreMinHeight;
    private bool _isDraggingThumbnail;
    private bool _thumbnailDragMoved;
    private WpfPoint _thumbnailDragStart;
    private WpfPoint _thumbnailDragStartScreen;
    private double _thumbnailStartTop;
    private Thickness _restoreShellMargin;
    private Thickness _restoreShellBorderThickness;
    private CornerRadius _restoreShellCornerRadius;
    private System.Windows.Media.Effects.Effect? _restoreShellEffect;
    private ContextMenu? _restoreContextMenu;

    public PinnedImageWindow(
        CapturedImage capturedImage,
        Func<CapturedImage, Task<OcrRecognitionResult>>? recognizeTextAsync = null,
        Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
            translateTextAsync = null,
        Func<AppSettings>? settingsProvider = null,
        Action<string>? customStrokeColorChanged = null,
        Action<int[]>? customColorPaletteChanged = null,
        Action<ArrowStyle>? arrowStyleChanged = null,
        Action<ArrowToolMode>? arrowToolModeChanged = null,
        Action<ShapeToolMode>? shapeToolModeChanged = null,
        Action<AnnotationToolMode>? lastAnnotationToolChanged = null)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);

        _capturedImage = capturedImage;
        _recognizeTextAsync = recognizeTextAsync;
        _translateTextAsync = translateTextAsync;
        _settingsProvider = settingsProvider;
        _customStrokeColorChanged = customStrokeColorChanged;
        _customColorPaletteChanged = customColorPaletteChanged;
        _arrowStyleChanged = arrowStyleChanged;
        _arrowToolModeChanged = arrowToolModeChanged;
        _shapeToolModeChanged = shapeToolModeChanged;
        _lastAnnotationToolChanged = lastAnnotationToolChanged;
        InitializeComponent();
        DataContext = _capturedImage;
        TranslateButton.IsEnabled = false;
        ApplyInitialSize();
        if (_capturedImage.SourceRegion is { IsEmpty: false })
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        InlineEditorCanvas.AnnotationSelectionChanged +=
            OnInlineAnnotationSelectionChanged;
    }

    internal Task TextRecognitionTask => _textRecognitionTask;

    public event EventHandler? SettingsRequested;

    public event EventHandler? HideAllRequested;

    public event EventHandler? MinimizeRequested;

    public event EventHandler? MinimizedStateChanged;

    internal bool IsMinimized => _isMinimized;

    internal PinnedWindowMinimization.Bounds GetPersistenceBounds() =>
        _restoreBounds ?? new(Left, Top, Width, Height);

    internal void MinimizeTo(int stackIndex)
    {
        if (_isMinimized) return;
        _restoreBounds = new(Left, Top, Width, Height);
        _restoreMinWidth = MinWidth;
        _restoreMinHeight = MinHeight;
        _isMinimized = true;
        _restoreContextMenu = PinnedShell.ContextMenu;
        PinnedShell.ContextMenu = null;
        MinimizedStateChanged?.Invoke(this, EventArgs.Empty);
        MinWidth = 0;
        MinHeight = 0;
        _restoreShellMargin = PinnedShell.Margin;
        _restoreShellBorderThickness = PinnedShell.BorderThickness;
        _restoreShellCornerRadius = PinnedShell.CornerRadius;
        _restoreShellEffect = PinnedShell.Effect;
        PinnedShell.Margin = new Thickness(0);
        PinnedShell.BorderThickness = new Thickness(0);
        PinnedShell.CornerRadius = new CornerRadius(0);
        PinnedShell.Effect = null;
        PinnedShell.Background = WpfBrushes.Transparent;
        PinnedHeaderRow.Height = new GridLength(0);
        PinnedHeader.Visibility = Visibility.Collapsed;
        ImageSurface.Background = WpfBrushes.Transparent;
        ImageSurface.CornerRadius = new CornerRadius(0);
        PinnedImage.Stretch = Stretch.UniformToFill;
        ImageSurface.Cursor = WpfCursors.Arrow;
        TextOverlay.Visibility = Visibility.Collapsed;
        PinnedWindowMinimization.Animate(this, PinnedWindowMinimization.GetThumbnailBounds(stackIndex), () => { });
    }

    internal void RestoreFromMinimized()
    {
        if (!_isMinimized || _restoreBounds is not { } bounds) return;
        PinnedWindowMinimization.Animate(this, bounds, () =>
        {
            BeginAnimation(LeftProperty, null); BeginAnimation(TopProperty, null);
            BeginAnimation(WidthProperty, null); BeginAnimation(HeightProperty, null);
            Left = bounds.Left; Top = bounds.Top;
            Width = bounds.Width; Height = bounds.Height;
            MinWidth = _restoreMinWidth; MinHeight = _restoreMinHeight;
            PinnedShell.Margin = _restoreShellMargin;
            PinnedShell.BorderThickness = _restoreShellBorderThickness;
            PinnedShell.CornerRadius = _restoreShellCornerRadius;
            PinnedShell.Effect = _restoreShellEffect;
            PinnedShell.SetResourceReference(
                BackgroundProperty,
                "AppPanelBackgroundBrush");
            PinnedShell.ContextMenu = _restoreContextMenu;
            PinnedHeaderRow.Height = new GridLength(30);
            PinnedHeader.Visibility = Visibility.Visible;
            ImageSurface.Background =
                (System.Windows.Media.Brush)FindResource("PinnedCheckerboardBrush");
            ImageSurface.CornerRadius = new CornerRadius(0, 0, 7, 7);
            PinnedImage.Stretch = Stretch.Uniform;
            ImageSurface.Cursor = WpfCursors.Arrow;
            TextOverlay.Visibility = Visibility.Visible;
            _isMinimized = false; _restoreBounds = null;
            MinimizedStateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public event EventHandler? GroupMembershipChanged;

    public event EventHandler? PersistenceChanged;

    internal event EventHandler? ImageChanged;

    internal string? PersistenceId { get; set; }

    internal bool IsGrouped { get; private set; }

    internal bool IsPersistent { get; private set; }

    internal CapturedImage CloneImage() => _capturedImage.Clone();

    internal Task<OcrRecognitionResult> RecognizeImageAsync(CapturedImage image) =>
        _recognizeTextAsync?.Invoke(image) ??
        Task.FromResult(OcrRecognitionResult.Failure("当前未配置文字识别"));

    internal Task<TranslationSegmentsResult> TranslateRecognitionAsync(
        OcrRecognitionResult recognition) =>
        _translateTextAsync?.Invoke(recognition) ??
        Task.FromResult(TranslationSegmentsResult.Failure("当前未配置翻译"));

    internal PinnedImageEditorToolbarWindow CreateEditorToolbar(Window owner) =>
        new(
            owner,
            _settingsProvider?.Invoke(),
            _customStrokeColorChanged,
            _customColorPaletteChanged,
            _arrowStyleChanged,
            _arrowToolModeChanged,
            _shapeToolModeChanged,
            _lastAnnotationToolChanged);

    internal BitmapSource Preview => _capturedImage.Preview;

    // Preserve the scale visible to the user when this pin enters a group.
    internal BitmapSource GroupingPreview => _groupingPreview ?? Preview;

    internal bool IsInlineEditorVisible => _isEditorMode;

    internal bool IsInlineCropVisible => _isCropMode;

    internal PinnedImageEditorToolbarWindow? EditorToolbar => _editorToolbar;

    internal void SetPersistentState(bool isPersistent)
    {
        IsPersistent = isPersistent;
        RestoreMenuItem.Header = isPersistent
            ? "取消重启后恢复"
            : "重启后恢复此钉图";
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        EndInlineEditorPanning();
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnLoaded;
        InlineEditorCanvas.AnnotationSelectionChanged -=
            OnInlineAnnotationSelectionChanged;
        CloseEditorToolbar();
        InlineEditorCanvas.Reset();
        _inlineEditorImage?.Dispose();
        _inlineEditorImage = null;
        _capturedImage.Dispose();
        base.OnClosed(e);
    }

    private void OnInlineAnnotationSelectionChanged(
        object? sender,
        EventArgs e)
    {
        if (!_isEditorMode || _editorToolbar is null ||
            InlineEditorCanvas.SelectedAnnotationStrokeWidth is not { } width)
        {
            return;
        }

        _editorToolbar.SetStrokeWidthFromCanvas(width);
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is WpfButton or WpfSlider)
        {
            return;
        }

        BeginWindowDrag(e);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        CopyImage();
    }

    internal void CopyImage()
    {
        try
        {
            System.Windows.Clipboard.SetImage(
                _isEditorMode
                    ? InlineEditorCanvas.RenderEditedImage()
                    : _capturedImage.Preview);
        }
        catch (COMException)
        {
        }
    }

    private async void SaveCurrentImage()
    {
        var settings = _settingsProvider?.Invoke();
        var restoreAfterPicker = settings?.PngSaveLocationMode ==
            PngSaveLocationMode.AskEveryTime &&
            IsVisible;
        try
        {
            var image = _isEditorMode
                ? InlineEditorCanvas.RenderEditedImage()
                : _capturedImage.Preview;
            if (restoreAfterPicker)
            {
                Hide();
            }
            var saveDirectory = await PngSaveLocationService.ResolveAsync(
                settings?.PngSaveLocationMode ?? PngSaveLocationMode.DefaultDirectory,
                settings?.SaveDirectory ?? AppMetadata.DefaultCaptureDirectory);
            if (saveDirectory is null)
            {
                return;
            }

            var path = CaptureFileService.SaveAsPng(
                image,
                saveDirectory);
            HeaderStatusText.Text = $"钉图 · 已保存 {System.IO.Path.GetFileName(path)}";
        }
        catch
        {
            HeaderStatusText.Text = "钉图 · 保存失败";
        }
        finally
        {
            if (restoreAfterPicker && !_isClosed)
            {
                Show();
                Activate();
            }
        }
    }

    private async Task<OcrRecognitionResult> EnsureRecognitionAsync()
    {
        if (_recognition is { IsSuccess: true } recognized)
        {
            return recognized;
        }

        await _textRecognitionTask;
        if (_recognition is { IsSuccess: true } completed)
        {
            return completed;
        }

        if (_recognizeTextAsync is null)
        {
            return OcrRecognitionResult.Failure("当前未配置文字识别");
        }

        await RecognizeTextAsync();
        return _recognition ?? OcrRecognitionResult.Failure("文字识别失败");
    }

    private async Task ShowRecognizedTextFromToolbarAsync()
    {
        HeaderStatusText.Text = "正在识别图片文字…";
        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess || recognition.Regions.Count == 0)
        {
            HeaderStatusText.Text = recognition.ErrorMessage ?? "未识别到文字";
            return;
        }

        if (_settingsProvider?.Invoke().RecognitionResultPresentation ==
            RecognitionResultPresentationMode.Popup)
        {
            ShowRecognitionPopup(
                "钉图 · 已识别",
                recognition.Text,
                translatedText: null);
            return;
        }

        _displayedRegions = recognition.Regions;
        _isShowingTranslation = false;
        RenderSelectableTextOverlay();
        TextOverlay.Visibility = Visibility.Visible;
        HeaderStatusText.Text = "钉图 · 文字可选择复制";
    }

    private async Task CopyRecognizedTextFromToolbarAsync()
    {
        HeaderStatusText.Text = "正在识别并提取文字…";
        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess || string.IsNullOrWhiteSpace(recognition.Text))
        {
            HeaderStatusText.Text = recognition.ErrorMessage ?? "未识别到文字";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(recognition.Text);
            HeaderStatusText.Text = "钉图 · 文字已复制";
        }
        catch (COMException)
        {
            HeaderStatusText.Text = "钉图 · 剪贴板正忙";
        }
    }

    private async Task CopyTableFromToolbarAsync()
    {
        HeaderStatusText.Text = "正在识别表格…";
        var image = CapturedImage.FromBitmapSource(
            _isEditorMode ? InlineEditorCanvas.RenderEditedImage() : _capturedImage.Preview);
        using (image)
        {
            var recognition = await RecognizeImageAsync(image);
            var table = TableRecognitionService.BuildTsv(
                recognition,
                image.Bitmap,
                await TableSupplementaryOcrService.RecognizeAsync(
                    image,
                    recognition.Words));
            if (!table.IsSuccess)
            {
                HeaderStatusText.Text = table.ErrorMessage ?? "未识别到表格";
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(table.ClipboardHtml))
                {
                    await ClipboardTextService.SetHtmlAsync(table.ClipboardHtml, table.Content);
                }
                else
                {
                    await ClipboardTextService.SetTextAsync(table.Content);
                }
                HeaderStatusText.Text = "钉图 · 表格已复制";
            }
            catch (COMException)
            {
                HeaderStatusText.Text = "钉图 · 剪贴板正忙";
            }
        }
    }

    private async Task TranslateFromToolbarAsync()
    {
        if (InlineEditorCanvas.HasTranslationOverlay)
        {
            InlineEditorCanvas.SetTranslationOverlayVisible(
                !InlineEditorCanvas.IsTranslationOverlayVisible);
            HeaderStatusText.Text = InlineEditorCanvas.IsTranslationOverlayVisible
                ? "钉图 · 已显示翻译，可撤销"
                : "钉图 · 已隐藏翻译";
            return;
        }

        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess)
        {
            HeaderStatusText.Text = recognition.ErrorMessage ?? "文字识别失败";
            return;
        }

        await TranslateTextAsync();
        if (_translatedRegions.Count > 0)
        {
            TextOverlay.Visibility = Visibility.Collapsed;
            InlineEditorCanvas.AddTranslationOverlay(
                _translatedRegions.Select(region =>
                    new TranslatedTextAnnotationRegion(
                        new Rect(region.X, region.Y, region.Width, region.Height),
                        region.Text,
                        Math.Max(
                            10,
                            region.EstimatedFontSize > 0
                                ? region.EstimatedFontSize
                                : region.Height * 0.78)))
                .ToArray());
            HeaderStatusText.Text = "钉图 · 已添加翻译，可撤销";
        }
    }

    private async Task RedactPrivacyFromToolbarAsync()
    {
        HeaderStatusText.Text = "正在检测敏感信息…";
        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess)
        {
            HeaderStatusText.Text = recognition.ErrorMessage ?? "敏感信息识别失败";
            return;
        }

        var candidates = PrivacyDetectionService.Detect(recognition);
        if (candidates.Count == 0)
        {
            HeaderStatusText.Text = "钉图 · 未检测到敏感信息";
            return;
        }

        var confirmation = new PrivacyRedactionWindow(candidates)
        {
            Owner = this,
            Topmost = true,
        };
        if (confirmation.ShowDialog() != true ||
            confirmation.SelectedCandidates.Count == 0)
        {
            HeaderStatusText.Text = "钉图 · 已取消隐私打码";
            return;
        }

        TextOverlay.Visibility = Visibility.Collapsed;
        InlineEditorCanvas.AddMosaicRegions(
            confirmation.SelectedCandidates.Select(candidate => candidate.Bounds));
        HeaderStatusText.Text =
            $"钉图 · 已添加 {confirmation.SelectedCandidates.Count} 处隐私马赛克";
        InlineEditorCanvas.Focus();
    }

    private void OnOpacityValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Opacity = e.NewValue;
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseFromContextMenuClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnHideAllClick(object sender, RoutedEventArgs e)
    {
        HideAllRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        MinimizeRequested?.Invoke(this, EventArgs.Empty);

    private void OnGroupClick(object sender, RoutedEventArgs e)
    {
        SetGroupedState(!IsGrouped);
    }

    internal void SetGroupedState(bool isGrouped)
    {
        if (IsGrouped == isGrouped)
        {
            return;
        }

        if (isGrouped)
        {
            CommitInlineEditor();
            ExitCropMode();
        }

        IsGrouped = isGrouped;
        _groupingPreview = isGrouped ? CreateGroupingPreview() : null;
        GroupMenuItem.Header = IsGrouped ? "移出钉图编组" : "加入钉图编组";
        HeaderStatusText.Text = IsGrouped ? "钉图 · 已加入编组" : "钉图";
        GroupMembershipChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        SetPersistentState(!IsPersistent);
        PersistenceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isEditorMode)
        {
            if (InlineEditorCanvas.HasSelectedAnnotation)
            {
                var annotationFactor = e.Delta > 0 ? 1.1 : 1 / 1.1;
                if (InlineEditorCanvas.AdjustSelectedAnnotationScale(
                        annotationFactor))
                {
                    e.Handled = true;
                    return;
                }
            }

            ZoomInlineEditor(e);
            e.Handled = true;
            return;
        }

        if (_isCropMode)
        {
            e.Handled = true;
            return;
        }

        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var previousWidth = Width;
        var previousHeight = Height;
        var nextWidth = Math.Clamp(previousWidth * factor, MinWidth, MaxWidth);
        var contentRatio = Math.Max(0.05,
            (previousHeight - HeaderAndShadowHeight) /
            Math.Max(1, previousWidth - ShadowInset));
        var nextHeight = Math.Clamp(
            ((nextWidth - ShadowInset) * contentRatio) + HeaderAndShadowHeight,
            MinHeight,
            MaxHeight);
        Left -= (nextWidth - previousWidth) / 2;
        Top -= (nextHeight - previousHeight) / 2;
        Width = nextWidth;
        Height = nextHeight;
        e.Handled = true;
    }

    private void ZoomInlineEditor(MouseWheelEventArgs e)
    {
        var pointer = e.GetPosition(InlineEditorViewport);
        var previousZoom = InlineEditorCanvas.Zoom;
        var contentX =
            (InlineEditorViewport.HorizontalOffset + pointer.X) / previousZoom;
        var contentY =
            (InlineEditorViewport.VerticalOffset + pointer.Y) / previousZoom;
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        InlineEditorCanvas.SetZoom(InlineEditorCanvas.Zoom * factor);
        InlineEditorFrame.Width = InlineEditorCanvas.DisplayWidth;
        InlineEditorFrame.Height = InlineEditorCanvas.DisplayHeight;
        InlineEditorViewport.UpdateLayout();
        InlineEditorViewport.ScrollToHorizontalOffset(
            (contentX * InlineEditorCanvas.Zoom) - pointer.X);
        InlineEditorViewport.ScrollToVerticalOffset(
            (contentY * InlineEditorCanvas.Zoom) - pointer.Y);
        HeaderStatusText.Text = $"钉图 · 正在编辑 · {InlineEditorCanvas.Zoom * 100:0}%";
    }

    private void OnInlineEditorPreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isEditorMode || e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _isPanningInlineEditor = true;
        _inlineEditorPanStartPoint = e.GetPosition(InlineEditorViewport);
        _inlineEditorPanStartHorizontalOffset = InlineEditorViewport.HorizontalOffset;
        _inlineEditorPanStartVerticalOffset = InlineEditorViewport.VerticalOffset;
        InlineEditorViewport.Cursor = WpfCursors.SizeAll;
        _ = InlineEditorViewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnInlineEditorPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isPanningInlineEditor)
        {
            return;
        }
        if (e.MiddleButton != MouseButtonState.Pressed)
        {
            EndInlineEditorPanning();
            return;
        }

        var current = e.GetPosition(InlineEditorViewport);
        InlineEditorViewport.ScrollToHorizontalOffset(
            _inlineEditorPanStartHorizontalOffset + _inlineEditorPanStartPoint.X - current.X);
        InlineEditorViewport.ScrollToVerticalOffset(
            _inlineEditorPanStartVerticalOffset + _inlineEditorPanStartPoint.Y - current.Y);
        e.Handled = true;
    }

    private void OnInlineEditorPreviewMouseUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_isPanningInlineEditor && e.ChangedButton == MouseButton.Middle)
        {
            EndInlineEditorPanning();
            e.Handled = true;
        }
    }

    private void OnInlineEditorLostMouseCapture(object sender, WpfMouseEventArgs e) =>
        EndInlineEditorPanning();

    private void EndInlineEditorPanning()
    {
        _isPanningInlineEditor = false;
        if (InlineEditorViewport.IsMouseCaptured)
        {
            InlineEditorViewport.ReleaseMouseCapture();
        }
        InlineEditorViewport.Cursor = WpfCursors.Arrow;
    }

    private void OnCropClick(object sender, RoutedEventArgs e)
    {
        OpenCrop(this);
    }

    internal void OpenCrop(Window owner)
    {
        if (_isEditorMode)
        {
            CommitInlineEditor();
        }
        if (_isCropMode)
        {
            return;
        }

        var source = _capturedImage.Preview;
        _cropRect = new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight);
        _isCropMode = true;
        SetWindowResizeHandlesEnabled(false);
        TextOverlay.Visibility = Visibility.Collapsed;
        CropOverlay.Visibility = Visibility.Visible;
        UpdateRenderedImageBounds();
        UpdateCropSelection();
        ShowEditorToolbar(PinnedImageToolbarMode.Crop);
        HeaderStatusText.Text = "钉图 · 框选裁剪";
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        OpenEditor(this);
    }

    internal void OpenEditor(Window owner)
    {
        if (_isEditorMode)
        {
            return;
        }
        if (_isCropMode)
        {
            ExitCropMode();
        }

        var source = _capturedImage.Preview;
        _inlineEditorImage = _capturedImage.Clone();
        var availableWidth = Math.Max(1, ImageViewport.ActualWidth);
        var availableHeight = Math.Max(1, ImageViewport.ActualHeight);
        var scale = Math.Min(
            1,
            Math.Min(
                availableWidth / Math.Max(1, source.PixelWidth),
                availableHeight / Math.Max(1, source.PixelHeight)));
        var displayWidth = Math.Max(1, source.PixelWidth * scale);
        var displayHeight = Math.Max(1, source.PixelHeight * scale);
        InlineEditorCanvas.Initialize(
            _inlineEditorImage,
            displayWidth,
            displayHeight);
        InlineEditorFrame.Width = displayWidth;
        InlineEditorFrame.Height = displayHeight;
        PinnedImage.Visibility = Visibility.Collapsed;
        TextOverlay.Visibility = Visibility.Collapsed;
        InlineEditorViewport.Visibility = Visibility.Visible;
        _isEditorMode = true;
        ShowEditorToolbar(PinnedImageToolbarMode.Edit);
        HeaderStatusText.Text = "钉图 · 正在编辑";
    }

    private void CommitInlineEditor()
    {
        if (!_isEditorMode)
        {
            return;
        }

        var edited = InlineEditorCanvas.RenderEditedImage();
        ExitInlineEditor();
        ReplaceImage(CapturedImage.FromBitmapSource(edited));
    }

    private void ExitInlineEditor()
    {
        if (!_isEditorMode && _inlineEditorImage is null)
        {
            return;
        }

        EndInlineEditorPanning();
        InlineEditorCanvas.Reset();
        _inlineEditorImage?.Dispose();
        _inlineEditorImage = null;
        _isEditorMode = false;
        InlineEditorViewport.Visibility = Visibility.Collapsed;
        PinnedImage.Visibility = Visibility.Visible;
        if (!_isCropMode)
        {
            CloseEditorToolbar();
        }
        TextOverlay.Visibility = Visibility.Visible;
        HeaderStatusText.Text = IsGrouped ? "钉图 · 已加入编组" : "钉图";
    }

    private void ApplyInlineCrop()
    {
        if (!_isCropMode || _cropRect.IsEmpty)
        {
            return;
        }

        var cropped = new CroppedBitmap(_capturedImage.Preview, _cropRect);
        cropped.Freeze();
        ExitCropMode();
        ReplaceImage(CapturedImage.FromBitmapSource(cropped));
    }

    private void ExitCropMode()
    {
        if (!_isCropMode)
        {
            return;
        }

        _cropDragStart = null;
        _isCropMode = false;
        SetWindowResizeHandlesEnabled(true);
        CropOverlay.ReleaseMouseCapture();
        CropOverlay.Visibility = Visibility.Collapsed;
        TextOverlay.Visibility = Visibility.Visible;
        if (!_isEditorMode)
        {
            CloseEditorToolbar();
        }
        HeaderStatusText.Text = IsGrouped ? "钉图 · 已加入编组" : "钉图";
    }

    private void SetWindowResizeHandlesEnabled(bool enabled)
    {
        foreach (var thumb in new[]
                 {
                     WindowResizeLeftThumb,
                     WindowResizeRightThumb,
                     WindowResizeTopThumb,
                     WindowResizeBottomThumb,
                     WindowResizeTopLeftThumb,
                     WindowResizeTopRightThumb,
                     WindowResizeBottomLeftThumb,
                     WindowResizeBottomRightThumb,
                 })
        {
            thumb.IsHitTestVisible = enabled;
        }
    }

    internal void ReplaceImage(CapturedImage replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var previous = _capturedImage;
        _capturedImage = replacement;
        DataContext = replacement;
        PinnedImage.Source = replacement.Preview;
        previous.Dispose();
        _recognition = null;
        _displayedRegions = [];
        _translatedRegions = [];
        TextOverlay.Children.Clear();
        TranslateButton.IsEnabled = false;
        ApplyInitialSize();
        if (IsGrouped)
        {
            _groupingPreview = CreateGroupingPreview();
        }
        _textRecognitionTask = RecognizeTextAsync();
        if (IsPersistent)
        {
            PersistenceChanged?.Invoke(this, EventArgs.Empty);
        }
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isMinimized)
        {
            PinnedWindowMinimization.CommitCurrentAnimation(this);
            _isDraggingThumbnail = true;
            _thumbnailDragMoved = false;
            _thumbnailDragStart = e.GetPosition(this);
            _thumbnailDragStartScreen = GetCurrentScreenCursorPosition();
            _thumbnailStartTop = Top;
            ImageSurface.Cursor = WpfCursors.SizeAll;
            _ = ImageSurface.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (_isEditorMode || _isCropMode || IsSelectableTextSource(e.OriginalSource))
        {
            return;
        }

        BeginWindowDrag(e);
    }

    private void OnImageMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isDraggingThumbnail || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var currentScreen = GetCurrentScreenCursorPosition();
        var top = _thumbnailStartTop +
            currentScreen.Y - _thumbnailDragStartScreen.Y;
        _thumbnailDragMoved |= Math.Abs(top - _thumbnailStartTop) >= 3;
        Left = workArea.Right - Width - 12;
        Top = Math.Clamp(top, workArea.Top + 12, workArea.Bottom - Height - 12);
        e.Handled = true;
    }

    private void OnImageMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var restore = _isDraggingThumbnail && !_thumbnailDragMoved;
        EndThumbnailDrag();
        if (restore)
        {
            RestoreFromMinimized();
        }
    }

    private void OnImageLostMouseCapture(object sender, WpfMouseEventArgs e) =>
        EndThumbnailDrag();

    private WpfPoint GetCurrentScreenCursorPosition()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(
                new WpfPoint(cursor.X, cursor.Y))
            : new WpfPoint(cursor.X, cursor.Y);
    }

    private void EndThumbnailDrag()
    {
        _isDraggingThumbnail = false;
        _thumbnailDragMoved = false;
        if (ImageSurface.IsMouseCaptured)
        {
            ImageSurface.ReleaseMouseCapture();
        }
        if (_isMinimized)
        {
            ImageSurface.Cursor = WpfCursors.Arrow;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ApplyInitialPlacement();
        _textRecognitionTask = RecognizeTextAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        ApplyInitialPlacement();
    }

    private async Task RecognizeTextAsync()
    {
        if (_recognizeTextAsync is null || _isClosed)
        {
            HeaderStatusText.Text = "钉图";
            TranslateButton.ToolTip = "当前未配置文字识别";
            return;
        }

        HeaderStatusText.Text = "正在识别图片文字…";
        try
        {
            using var image = _capturedImage.Clone();
            var recognition = await _recognizeTextAsync(image);
            if (_isClosed)
            {
                return;
            }

            _recognition = recognition;
            if (!recognition.IsSuccess)
            {
                HeaderStatusText.Text = "文字识别失败";
                TranslateButton.ToolTip = recognition.ErrorMessage ?? "文字识别失败";
                return;
            }

            if (recognition.Regions.Count == 0)
            {
                HeaderStatusText.Text = "钉图 · 未识别到文字";
                TranslateButton.ToolTip = "图片中没有可翻译的文字";
                return;
            }

            _displayedRegions = recognition.Regions;
            _isShowingTranslation = false;
            RenderSelectableTextOverlay();
            HeaderStatusText.Text = "钉图 · 文字可选择复制";
            TranslateButton.Content = "翻译";
            TranslateButton.IsEnabled = _translateTextAsync is not null;
            TranslateButton.ToolTip = _translateTextAsync is null
                ? "请先在设置中启用翻译"
                : "翻译图片文字并覆盖显示";
        }
        catch
        {
            if (!_isClosed)
            {
                HeaderStatusText.Text = "文字识别失败";
                TranslateButton.ToolTip = "请检查 OCR 语言设置";
            }
        }
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        await TranslateTextAsync();
    }

    internal async Task TranslateTextAsync()
    {
        if (_isClosed ||
            _translateTextAsync is null ||
            _recognition is not { IsSuccess: true, Regions.Count: > 0 } recognition)
        {
            return;
        }

        if (_translatedRegions.Count > 0)
        {
            if (_isShowingTranslation)
            {
                ShowOriginalText();
            }
            else
            {
                ShowTranslatedText();
            }

            return;
        }

        TranslateButton.IsEnabled = false;
        HeaderStatusText.Text = "正在翻译图片文字…";
        try
        {
            var translation = await _translateTextAsync(recognition);
            if (_isClosed)
            {
                return;
            }

            if (!translation.IsSuccess)
            {
                HeaderStatusText.Text = "翻译失败";
                TranslateButton.ToolTip = translation.ErrorMessage ?? "翻译失败";
                TranslateButton.IsEnabled = true;
                return;
            }

            if (translation.Segments.Count != recognition.Regions.Count)
            {
                HeaderStatusText.Text = "翻译结果不完整";
                TranslateButton.ToolTip = "翻译服务返回的分段数量不一致";
                TranslateButton.IsEnabled = true;
                return;
            }

            _translatedRegions = recognition.Regions
                .Select((region, index) => new OcrTextRegion(
                    translation.Segments[index],
                    Math.Max(0, region.X - 4),
                    Math.Max(0, region.Y - 3),
                    Math.Max(20, region.Width + 8),
                    Math.Max(24, region.Height + 12))
                {
                    EstimatedFontSize = region.EstimatedFontSize,
                })
                .ToArray();

            if (_settingsProvider?.Invoke().RecognitionResultPresentation ==
                RecognitionResultPresentationMode.Popup)
            {
                ShowRecognitionPopup(
                    "钉图 · 已翻译",
                    recognition.Text,
                    string.Join(
                        Environment.NewLine,
                        translation.Segments));
                return;
            }

            ShowTranslatedText();
        }
        catch
        {
            if (!_isClosed)
            {
                HeaderStatusText.Text = "翻译失败";
                TranslateButton.ToolTip = "请检查翻译服务设置";
                TranslateButton.IsEnabled = true;
            }
        }
    }

    private void ShowOriginalText()
    {
        if (_recognition is not { IsSuccess: true } recognition)
        {
            return;
        }

        _displayedRegions = recognition.Regions;
        _isShowingTranslation = false;
        RenderSelectableTextOverlay();
        HeaderStatusText.Text = "钉图 · 原文可选择复制";
        TranslateButton.Content = "译文";
        TranslateButton.ToolTip = "显示已缓存的译文";
        TranslateButton.IsEnabled = true;
    }

    private void ShowTranslatedText()
    {
        _displayedRegions = _translatedRegions;
        _isShowingTranslation = true;
        RenderSelectableTextOverlay();
        HeaderStatusText.Text = "钉图 · 译文可选择复制";
        TranslateButton.Content = "原文";
        TranslateButton.ToolTip = "显示原始文字";
        TranslateButton.IsEnabled = true;
    }

    private void ShowRecognitionPopup(
        string title,
        string sourceText,
        string? translatedText)
    {
        var popup = new RecognitionResultPopupWindow(
            title,
            sourceText,
            translatedText,
            closeAfterCopy: false)
        {
            Owner = this,
            Topmost = true,
        };
        popup.Show();
        popup.Activate();
    }

    private void OnImageViewportSizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (_displayedRegions.Count > 0)
        {
            RenderSelectableTextOverlay();
        }
        if (_isCropMode)
        {
            UpdateRenderedImageBounds();
            UpdateCropSelection();
        }
    }

    private void UpdateRenderedImageBounds()
    {
        var source = _capturedImage.Preview;
        if (ImageViewport.ActualWidth <= 0 || ImageViewport.ActualHeight <= 0)
        {
            _renderedImageBounds = Rect.Empty;
            return;
        }

        var scale = Math.Min(
            ImageViewport.ActualWidth / source.PixelWidth,
            ImageViewport.ActualHeight / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        _renderedImageBounds = new Rect(
            (ImageViewport.ActualWidth - width) / 2,
            (ImageViewport.ActualHeight - height) / 2,
            width,
            height);
    }

    private void OnCropMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCropMode || _renderedImageBounds.IsEmpty ||
            FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _cropDragStart = ClampToImage(e.GetPosition(ImageViewport));
        CropOverlay.CaptureMouse();
        UpdateCropFromDisplay(new Rect(_cropDragStart.Value, _cropDragStart.Value));
        e.Handled = true;
    }

    private void OnCropMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_cropDragStart is not { } start ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = ClampToImage(e.GetPosition(ImageViewport));
        UpdateCropFromDisplay(new Rect(start, current));
        e.Handled = true;
    }

    private void OnCropMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropDragStart is null)
        {
            return;
        }

        var current = ClampToImage(e.GetPosition(ImageViewport));
        UpdateCropFromDisplay(new Rect(_cropDragStart.Value, current));
        _cropDragStart = null;
        CropOverlay.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnCropHandleDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction } ||
            _renderedImageBounds.IsEmpty)
        {
            return;
        }

        var selection = GetCropDisplayRect();
        var adjusted = ImageCropWindow.AdjustSelectionRect(
            selection,
            _renderedImageBounds,
            direction,
            e.HorizontalChange,
            e.VerticalChange,
            Math.Max(1, _renderedImageBounds.Width / _capturedImage.Preview.PixelWidth),
            Math.Max(1, _renderedImageBounds.Height / _capturedImage.Preview.PixelHeight));
        if (string.Equals(direction, "Move", StringComparison.Ordinal))
        {
            _cropRect = ImageCropWindow.MoveCropRectWithoutResizing(
                _cropRect,
                _renderedImageBounds,
                adjusted,
                _capturedImage.Preview.PixelWidth,
                _capturedImage.Preview.PixelHeight);
            UpdateCropSelection();
        }
        else
        {
            UpdateCropFromDisplay(adjusted);
        }
    }

    private WpfPoint ClampToImage(WpfPoint point) => new(
        Math.Clamp(point.X, _renderedImageBounds.Left, _renderedImageBounds.Right),
        Math.Clamp(point.Y, _renderedImageBounds.Top, _renderedImageBounds.Bottom));

    private void UpdateCropFromDisplay(Rect selection)
    {
        _cropRect = ImageCropWindow.CalculateCropRectFromSelection(
            _renderedImageBounds,
            selection,
            _capturedImage.Preview.PixelWidth,
            _capturedImage.Preview.PixelHeight);
        UpdateCropSelection();
    }

    private Rect GetCropDisplayRect()
    {
        var scaleX = _renderedImageBounds.Width / _capturedImage.Preview.PixelWidth;
        var scaleY = _renderedImageBounds.Height / _capturedImage.Preview.PixelHeight;
        return new Rect(
            _renderedImageBounds.Left + (_cropRect.X * scaleX),
            _renderedImageBounds.Top + (_cropRect.Y * scaleY),
            Math.Max(1, _cropRect.Width * scaleX),
            Math.Max(1, _cropRect.Height * scaleY));
    }

    private void UpdateCropSelection()
    {
        if (_renderedImageBounds.IsEmpty || _cropRect.IsEmpty)
        {
            return;
        }

        var selection = GetCropDisplayRect();
        Canvas.SetLeft(CropSelection, selection.Left);
        Canvas.SetTop(CropSelection, selection.Top);
        CropSelection.Width = selection.Width;
        CropSelection.Height = selection.Height;
        Canvas.SetLeft(CropMoveThumb, selection.Left);
        Canvas.SetTop(CropMoveThumb, selection.Top);
        CropMoveThumb.Width = selection.Width;
        CropMoveThumb.Height = selection.Height;
        PositionCropThumb(CropTopLeftThumb, selection.Left, selection.Top);
        PositionCropThumb(CropTopRightThumb, selection.Right, selection.Top);
        PositionCropThumb(CropBottomLeftThumb, selection.Left, selection.Bottom);
        PositionCropThumb(CropBottomRightThumb, selection.Right, selection.Bottom);
        CropMask.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(_renderedImageBounds),
            new RectangleGeometry(selection));
        _editorToolbar?.UpdateCropSize(_cropRect.Width, _cropRect.Height);
    }

    private void PositionCropThumb(FrameworkElement thumb, double x, double y)
    {
        var position = ImageCropWindow.CalculateVisibleHandlePosition(
            _renderedImageBounds,
            x,
            y,
            thumb.Width,
            thumb.Height);
        Canvas.SetLeft(thumb, position.X);
        Canvas.SetTop(thumb, position.Y);
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void ShowEditorToolbar(PinnedImageToolbarMode mode)
    {
        CloseEditorToolbar();
        var toolbar = CreateEditorToolbar(this);
        _editorToolbar = toolbar;
        toolbar.ToolSelected += tool =>
        {
            if (_isEditorMode)
            {
                InlineEditorCanvas.SelectTool(tool);
                InlineEditorCanvas.Focus();
            }
        };
        toolbar.EmojiSelected += emoji =>
        {
            if (_isEditorMode)
            {
                InlineEditorCanvas.SelectEmoji(emoji);
                InlineEditorCanvas.SelectTool(EditorTool.Emoji);
                InlineEditorCanvas.Focus();
            }
        };
        toolbar.ColorSelected += color =>
        {
            if (_isEditorMode)
            {
                InlineEditorCanvas.SelectColor(color);
                InlineEditorCanvas.Focus();
            }
        };
        toolbar.StrokeWidthChanged += width =>
            InlineEditorCanvas.SetStrokeWidth(width);
        toolbar.ArrowStyleSelected += style =>
            InlineEditorCanvas.SelectArrowStyle(style);
        toolbar.UndoRequested += (_, _) => InlineEditorCanvas.Undo();
        toolbar.CropRequested += (_, _) => OpenCrop(this);
        toolbar.SaveRequested += (_, _) => SaveCurrentImage();
        toolbar.OcrRequested += async (_, _) =>
            await ShowRecognizedTextFromToolbarAsync();
        toolbar.CopyTableRequested += async (_, _) =>
            await CopyTableFromToolbarAsync();
        toolbar.CopyTextRequested += async (_, _) =>
            await CopyRecognizedTextFromToolbarAsync();
        toolbar.TranslateRequested += async (_, _) =>
            await TranslateFromToolbarAsync();
        toolbar.PrivacyRequested += async (_, _) =>
            await RedactPrivacyFromToolbarAsync();
        toolbar.ApplyRequested += (_, _) =>
        {
            if (_isEditorMode)
            {
                CommitInlineEditor();
            }
            else if (_isCropMode)
            {
                ApplyInlineCrop();
            }
        };
        toolbar.CancelRequested += (_, _) =>
        {
            if (_isEditorMode)
            {
                ExitInlineEditor();
            }
            else if (_isCropMode)
            {
                ExitCropMode();
            }
        };

        if (mode == PinnedImageToolbarMode.Edit)
        {
            toolbar.ShowEdit();
        }
        else
        {
            toolbar.ShowCrop(_cropRect.Width, _cropRect.Height);
        }
    }

    private void CloseEditorToolbar()
    {
        var toolbar = _editorToolbar;
        _editorToolbar = null;
        toolbar?.Close();
    }

    private void RenderSelectableTextOverlay()
    {
        TextOverlay.Children.Clear();
        var viewportWidth = ImageViewport.ActualWidth;
        var viewportHeight = ImageViewport.ActualHeight;
        var pixelWidth = _capturedImage.Preview.PixelWidth;
        var pixelHeight = _capturedImage.Preview.PixelHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0 ||
            pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(
            viewportWidth / pixelWidth,
            viewportHeight / pixelHeight);
        var renderedWidth = pixelWidth * scale;
        var renderedHeight = pixelHeight * scale;
        var imageOffsetX = (viewportWidth - renderedWidth) / 2;
        var imageOffsetY = (viewportHeight - renderedHeight) / 2;

        foreach (var region in _displayedRegions)
        {
            var width = Math.Max(12, region.Width * scale + 4);
            var height = Math.Max(16, region.Height * scale + 2);
            var preferredFontSize = _isShowingTranslation &&
                region.EstimatedFontSize > 0
                ? Math.Max(8, region.EstimatedFontSize * scale)
                : Math.Max(10, region.Height * scale * 0.78);
            var fontSize = _isShowingTranslation
                ? TranslationTextLayout.FitSingleLineFontSize(
                    region.Text,
                    Math.Max(8, width - 2),
                    Math.Max(8, height - 2),
                    preferredFontSize)
                : preferredFontSize;
            var textBox = new WpfTextBox
            {
                Text = region.Text,
                Width = width,
                Height = height,
                Padding = new Thickness(0),
                Background = _isShowingTranslation
                    ? new SolidColorBrush(WpfColor.FromRgb(15, 23, 26))
                    : WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = WpfCursors.IBeam,
                FontFamily = new WpfFontFamily("Microsoft YaHei UI"),
                FontSize = fontSize,
                FontWeight = FontWeights.Normal,
                Foreground = _isShowingTranslation
                    ? WpfBrushes.White
                    : WpfBrushes.Transparent,
                IsReadOnly = true,
                IsTabStop = false,
                SelectionBrush = new SolidColorBrush(
                    WpfColor.FromArgb(150, 46, 175, 165)),
                SelectionTextBrush = _isShowingTranslation
                    ? WpfBrushes.White
                    : WpfBrushes.Transparent,
                TextWrapping = TextWrapping.NoWrap,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Canvas.SetLeft(textBox, imageOffsetX + (region.X * scale));
            Canvas.SetTop(textBox, imageOffsetY + (region.Y * scale));
            TextOverlay.Children.Add(textBox);
        }
    }

    internal static bool IsSelectableTextSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is WpfTextBox)
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void BeginWindowDrag(MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
        e.Handled = true;
    }

    private void OnResizeEdgeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isEditorMode || _isCropMode ||
            sender is not FrameworkElement { Tag: string direction })
        {
            return;
        }

        if (direction.Contains("Left", StringComparison.Ordinal))
        {
            var previousWidth = Width;
            Width = Math.Clamp(Width - e.HorizontalChange, MinWidth, MaxWidth);
            Left += previousWidth - Width;
        }
        else if (direction.Contains("Right", StringComparison.Ordinal))
        {
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth);
        }

        if (direction.Contains("Top", StringComparison.Ordinal))
        {
            var previousHeight = Height;
            Height = Math.Clamp(Height - e.VerticalChange, MinHeight, MaxHeight);
            Top += previousHeight - Height;
        }
        else if (direction.Contains("Bottom", StringComparison.Ordinal))
        {
            Height = Math.Clamp(Height + e.VerticalChange, MinHeight, MaxHeight);
        }
    }

    private void ApplyInitialSize()
    {
        var referenceBounds = _capturedImage.SourceRegion is { IsEmpty: false } region
            ? new DrawingRectangle(region.X, region.Y, region.Width, region.Height)
            : System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ??
              System.Windows.Forms.SystemInformation.VirtualScreen;
        var workArea = MonitorGeometryService.GetWorkArea(referenceBounds);
        var dpi = MonitorGeometryService.GetDpiScale(referenceBounds);
        var workAreaWidth = workArea.Width / dpi.X;
        var workAreaHeight = workArea.Height / dpi.Y;
        var contentWidth = _capturedImage.Bitmap.Width / dpi.X;
        var contentHeight = _capturedImage.Bitmap.Height / dpi.Y;
        var maximumWindowWidth = Math.Max(MinWidth, workAreaWidth * 0.92);
        var maximumWindowHeight = Math.Max(MinHeight, workAreaHeight * 0.90);
        var maximumContentWidth = Math.Max(1, maximumWindowWidth - ShadowInset);
        var maximumContentHeight = Math.Max(1, maximumWindowHeight - HeaderAndShadowHeight);
        var scale = Math.Min(
            1,
            Math.Min(
                maximumContentWidth / Math.Max(1, contentWidth),
                maximumContentHeight / Math.Max(1, contentHeight)));

        MaxWidth = Math.Max(MinWidth, workAreaWidth);
        MaxHeight = Math.Max(MinHeight, workAreaHeight);
        Width = Math.Max(MinWidth, (contentWidth * scale) + ShadowInset);
        Height = Math.Max(MinHeight, (contentHeight * scale) + HeaderAndShadowHeight);
    }

    private BitmapSource CreateGroupingPreview()
    {
        var source = Preview;
        var availableWidth = Math.Max(1, Width - ShadowInset);
        var availableHeight = Math.Max(1, Height - HeaderAndShadowHeight);
        var scale = Math.Min(
            availableWidth / Math.Max(1, source.PixelWidth),
            availableHeight / Math.Max(1, source.PixelHeight));
        var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        if (width == source.PixelWidth && height == source.PixelHeight)
        {
            return source;
        }

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(source, new Rect(0, 0, width, height));
        }

        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    private void ApplyInitialPlacement()
    {
        if (_capturedImage.SourceRegion is not { IsEmpty: false } sourceRegion)
        {
            return;
        }

        if (!MonitorGeometryService.TryGetWindowBounds(this, out var windowBounds))
        {
            return;
        }

        var sourceBounds = new DrawingRectangle(
            sourceRegion.X,
            sourceRegion.Y,
            sourceRegion.Width,
            sourceRegion.Height);
        var workArea = MonitorGeometryService.GetWorkArea(sourceBounds);
        var dpi = MonitorGeometryService.GetDpiScale(sourceBounds);
        var contentLeftInset = (int)Math.Round((ShadowInset / 2) * dpi.X);
        var contentTopInset = (int)Math.Round(
            (HeaderAndShadowHeight - (ShadowInset / 2)) * dpi.Y);
        var maximumLeft = Math.Max(
            workArea.Left,
            workArea.Right - windowBounds.Width);
        var maximumTop = Math.Max(
            workArea.Top,
            workArea.Bottom - windowBounds.Height);
        var desiredLeft = Math.Clamp(
            sourceRegion.X - contentLeftInset,
            workArea.Left,
            maximumLeft);
        var desiredTop = Math.Clamp(
            sourceRegion.Y - contentTopInset,
            workArea.Top,
            maximumTop);
        _ = MonitorGeometryService.TryMoveWindow(
            this,
            desiredLeft,
            desiredTop);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
