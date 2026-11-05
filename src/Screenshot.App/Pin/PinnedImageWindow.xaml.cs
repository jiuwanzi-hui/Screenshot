using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "Pointer frame subscriptions are disposed by OnClosed.")]
public partial class PinnedImageWindow : Window
{
    internal const double ShadowMarginDip = 12;
    private const double ShadowExtentDip = ShadowMarginDip * 2;
    internal ImageEditorCanvas AnnotationCanvas => InlineEditorCanvas;
    private CapturedImage _capturedImage;
    private readonly Func<CapturedImage, Task<OcrRecognitionResult>>?
        _recognizeTextAsync;
    private readonly Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
        _translateTextAsync;
    private readonly Func<OcrRecognitionResult, CancellationToken, Task<TranslationSegmentsResult>>?
        _translateTextWithCancellationAsync;
    private bool CanTranslateText => _translateTextAsync is not null || _translateTextWithCancellationAsync is not null;
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
    private int _editorChangeVersion;
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
    private readonly PointerFramePump _imagePointerFrames;
    private bool _thumbnailDragMoved;
    private WpfPoint _thumbnailDragStart;
    private WpfPoint _thumbnailDragStartScreen;
    private double _thumbnailStartTop;
    private Thickness _restoreShellMargin;
    private Thickness _restoreShellBorderThickness;
    private CornerRadius _restoreShellCornerRadius;
    private System.Windows.Media.Effects.Effect? _restoreShellEffect;
    private ContextMenu? _restoreContextMenu;
    private readonly NativeWindowDragTracker _windowDragTracker;
    private readonly DispatcherTimer _inlineZoomQualityTimer;
    private readonly WheelZoomFramePump _zoomFrames;
    private System.Windows.Size _textOverlayViewportSize;

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
        Action<AnnotationToolMode>? lastAnnotationToolChanged = null,
        int outputScalePercent = 100,
        Func<CapturedImage, CancellationToken, Task<OcrRecognitionResult>>? recognizeTextWithCancellationAsync = null,
        OcrRecognitionResult? initialRecognition = null,
        Func<OcrRecognitionResult, CancellationToken, Task<TranslationSegmentsResult>>? translateTextWithCancellationAsync = null)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);

        _capturedImage = capturedImage;
        OutputScalePercent = Math.Clamp(outputScalePercent, 25, 200);
        _recognizeTextAsync = recognizeTextAsync;
        _recognizeTextWithCancellationAsync = recognizeTextWithCancellationAsync;
        _recognition = initialRecognition;
        _translateTextAsync = translateTextAsync;
        _translateTextWithCancellationAsync = translateTextWithCancellationAsync;
        _settingsProvider = settingsProvider;
        _customStrokeColorChanged = customStrokeColorChanged;
        _customColorPaletteChanged = customColorPaletteChanged;
        _arrowStyleChanged = arrowStyleChanged;
        _arrowToolModeChanged = arrowToolModeChanged;
        _shapeToolModeChanged = shapeToolModeChanged;
        _lastAnnotationToolChanged = lastAnnotationToolChanged;
        _imagePointerFrames = new PointerFramePump(PresentImagePointer);
        InitializeComponent();
        InlineEditorCanvas.HistoryChanged += (_, _) => _editorChangeVersion++;
        TranslationTargetPreferences.Changed += OnTranslationTargetChanged;
        Closed += (_, _) => TranslationTargetPreferences.Changed -= OnTranslationTargetChanged;
        Screenshot.App.Presentation.UiTransitions.PrepareWindow(this, PinnedShell);
        _inlineZoomQualityTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _inlineZoomQualityTimer.Tick += OnInlineZoomQualityTimerTick;
        _zoomFrames = new WheelZoomFramePump(ApplyWheelZoom);
        _windowDragTracker = new NativeWindowDragTracker(this, EndWindowDrag);
        DataContext = _capturedImage;
        IsTranslationEnabled = CanRecognizeText && CanTranslateText;
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

    internal int OutputScalePercent { get; }

    private bool _isTranslationEnabled;
    private string _translationActionText = "翻译图片文字";
    private string? _translationHint;

    internal bool IsTranslationEnabled
    {
        get => _isTranslationEnabled;
        private set { _isTranslationEnabled = value; UpdateTranslationAction(); }
    }

    internal string TranslationActionText
    {
        get => _translationActionText;
        private set { _translationActionText = value; UpdateTranslationAction(); }
    }

    private string? TranslationHint
    {
        get => _translationHint;
        set
        {
            _translationHint = value;
            StatusMenuItem.ToolTip = value;
            UpdateTranslationAction();
        }
    }

    private void UpdateTranslationAction() => _editorToolbar?.SetTranslationActionState(
        IsTranslationEnabled, TranslationActionText, TranslationHint);

    public event EventHandler? SettingsRequested;

    public event EventHandler? HideAllRequested;

    public event EventHandler? MinimizeRequested;

    public event EventHandler? MinimizedStateChanged;

    internal bool IsMinimized => _isMinimized;

    internal PinnedWindowMinimization.Bounds GetPersistenceBounds() =>
        _restoreBounds ?? new(Left, Top, Width, Height);

    internal void MinimizeTo(int stackIndex)
    {
        _automaticOcr.Cancel();
        CancelPendingInlineZoom();
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
        _restoreShellEffect = ImageOutline.Effect;
        PinnedShell.Margin = new Thickness(0);
        PinnedShell.BorderThickness = new Thickness(0);
        PinnedShell.CornerRadius = new CornerRadius(0);
        ImageOutline.Effect = null;
        PinnedShell.Background = WpfBrushes.Transparent;
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
            ImageOutline.Effect = _restoreShellEffect;
            PinnedShell.Background = WpfBrushes.Transparent;
            PinnedShell.ContextMenu = _restoreContextMenu;
            ImageSurface.Background = WpfBrushes.Transparent;
            ImageSurface.CornerRadius = new CornerRadius(0);
            PinnedImage.Stretch = Stretch.Uniform;
            TextOverlay.Visibility = Visibility.Visible;
            _isMinimized = false; _restoreBounds = null;
            ScheduleAutomaticOcr();
            UpdateImageCursor();
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

    internal async Task<OcrRecognitionResult> RecognizeImageAsync(CapturedImage image, CancellationToken cancellationToken = default)
    {
        if (_isClosed) return OcrRecognitionResult.Failure("识别已取消");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_recognitionLifetime.Token, cancellationToken);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            return await Task.Run(() => _recognizeTextWithCancellationAsync?.Invoke(image, lifetime.Token) ??
                _recognizeTextAsync?.Invoke(image) ??
                Task.FromResult(OcrRecognitionResult.Failure("当前未配置文字识别")), lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return OcrRecognitionResult.Failure("识别已取消");
        }
    }

    internal async Task<TranslationSegmentsResult> TranslateRecognitionAsync(
        OcrRecognitionResult recognition, CancellationToken cancellationToken = default)
    {
        if (_isClosed) return TranslationSegmentsResult.Failure("翻译已取消");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_recognitionLifetime.Token, cancellationToken);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            return await Task.Run(() => _translateTextWithCancellationAsync?.Invoke(recognition, lifetime.Token) ??
                _translateTextAsync?.Invoke(recognition) ??
                Task.FromResult(TranslationSegmentsResult.Failure("当前未配置翻译")), lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return TranslationSegmentsResult.Failure("翻译已取消");
        }
    }

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
    internal bool CanMoveImage => !_isEditorMode && !_isCropMode && !_isMinimized;

    private void UpdateImageCursor() =>
        ImageSurface.Cursor = CanMoveImage ? WpfCursors.SizeAll : WpfCursors.Arrow;

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
        _automaticOcr.Dispose();
        _recognitionLifetime.Cancel();
        _recognitionLifetime.Dispose();
        _zoomFrames.Dispose();
        _inlineZoomQualityTimer.Stop();
        _imagePointerFrames.Dispose();
        _windowDragTracker.Dispose();
        EndInlineEditorPanning();
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnLoaded;
        InlineEditorCanvas.AnnotationSelectionChanged -=
            OnInlineAnnotationSelectionChanged;
        CloseEditorToolbar();
        InlineEditorCanvas.Reset();
        _inlineEditorImage?.Dispose();
        _inlineEditorImage = null;
        PinnedImage.Source = null;
        TextOverlay.Children.Clear();
        TextOverlay.Visibility = Visibility.Collapsed;
        _recognition = null;
        _displayedRegions = [];
        _translatedRegions = [];
        _textRecognitionTask = Task.CompletedTask;
        _groupingPreview = null;
        DataContext = null;
        _capturedImage.Dispose();
        base.OnClosed(e);
        MemoryFootprint.TrimAfterHeavyOperation();
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


    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        CopyImage();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveCurrentImage();

    internal async void CopyImage()
    {
        try
        {
            if (_isClosed) return;
            await ClipboardImageService.SetImageAsync(CreateOutputPreview(), _recognitionLifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is COMException or System.ComponentModel.Win32Exception)
        {
            if (!_isClosed) StatusMenuItem.Header = "钉图 · 剪贴板正忙，请重试";
        }
    }

    internal BitmapSource CreateOutputPreview()
    {
        var source = _isEditorMode ? InlineEditorCanvas.RenderEditedImage() : _capturedImage.Preview;
        var percent = OutputScalePercent;
        if (percent == 100) return source;
        using var edited = _isEditorMode ? CapturedImage.FromBitmapSource(source) : null;
        using var scaled = (edited ?? _capturedImage).CreateScaled(percent);
        return scaled.Preview;
    }

    private async void SaveCurrentImage()
    {
        var settings = _settingsProvider?.Invoke();
        var restoreAfterPicker = settings?.PngSaveLocationMode ==
            PngSaveLocationMode.AskEveryTime &&
            IsVisible;
        try
        {
            var image = CreateOutputPreview();
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
            StatusMenuItem.Header = $"钉图 · 已保存 {System.IO.Path.GetFileName(path)}";
        }
        catch
        {
            StatusMenuItem.Header = "钉图 · 保存失败";
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

        if (!CanRecognizeText)
        {
            return OcrRecognitionResult.Failure("当前未配置文字识别");
        }

        _textRecognitionTask = RecognizeTextAsync();
        await _textRecognitionTask;
        return _recognition ?? OcrRecognitionResult.Failure("文字识别失败");
    }

    private async Task ShowRecognizedTextFromToolbarAsync()
    {
        var revision = _imageRevision;
        var editVersion = _editorChangeVersion;
        StatusMenuItem.Header = "正在识别图片文字…";
        var recognition = await EnsureRecognitionAsync();
        if (_isClosed || revision != _imageRevision) return;
        if (!recognition.IsSuccess || recognition.Regions.Count == 0)
        {
            StatusMenuItem.Header = recognition.ErrorMessage ?? "未识别到文字";
            return;
        }

        if (!_isEditorMode || _settingsProvider?.Invoke().RecognitionResultPresentation ==
            RecognitionResultPresentationMode.Popup)
        {
            ShowRecognitionPopup(
                "钉图 · 已识别",
                recognition.Text,
                translatedText: null, notice: recognition.ResultNotice);
            return;
        }

        if (editVersion != _editorChangeVersion || InlineEditorCanvas.IsTextInputActive)
        { _editorToolbar?.SetTranslationProgress("文字识别完成，点击文查看结果，可继续标注。"); return; }
        _displayedRegions = recognition.Regions;
        _automaticOcrOverlay = false;
        _isShowingTranslation = false;
        RenderSelectableTextOverlay();
        TextOverlay.Visibility = Visibility.Visible;
        StatusMenuItem.Header = "钉图 · 文字可选择复制";
    }

    private async Task CopyRecognizedTextFromToolbarAsync()
    {
        StatusMenuItem.Header = "正在识别并提取文字…";
        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess || string.IsNullOrWhiteSpace(recognition.Text))
        {
            StatusMenuItem.Header = recognition.ErrorMessage ?? "未识别到文字";
            return;
        }

        try
        {
            if (_isClosed) return;
            await ClipboardTextService.SetTextAsync(recognition.Text, _recognitionLifetime.Token);
            if (!_isClosed) StatusMenuItem.Header = "钉图 · 文字已复制";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is COMException or System.ComponentModel.Win32Exception)
        {
            if (!_isClosed) StatusMenuItem.Header = "钉图 · 剪贴板正忙，请重试";
        }
    }

    private async Task CopyTableFromToolbarAsync()
    {
        if (_isClosed) return;
        var cancellationToken = _recognitionLifetime.Token;
        StatusMenuItem.Header = "正在识别表格…";
        var image = CapturedImage.FromBitmapSource(
            _isEditorMode ? InlineEditorCanvas.RenderEditedImage() : _capturedImage.Preview);
        using (image)
        {
            var recognition = await RecognizeImageAsync(image);
            if (_isClosed) return;
            IReadOnlyList<OcrWordRegion> supplementaryWords;
            try
            {
                supplementaryWords = await TableSupplementaryOcrService.RecognizeAsync(
                    image, recognition.Words, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (_isClosed) return;
            ContentRecognitionResult table;
            try
            {
                table = await Task.Run(() => TableRecognitionService.BuildTsv(
                    recognition, image.Bitmap, supplementaryWords, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            if (_isClosed) return;
            if (!table.IsSuccess)
            {
                StatusMenuItem.Header = table.ErrorMessage ?? "未识别到表格";
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(table.ClipboardHtml))
                {
                    await ClipboardTextService.SetHtmlAsync(table.ClipboardHtml, table.Content, cancellationToken);
                }
                else
                {
                    await ClipboardTextService.SetTextAsync(table.Content, cancellationToken);
                }
                if (!_isClosed) StatusMenuItem.Header = "钉图 · 表格已复制";
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (exception is COMException or System.ComponentModel.Win32Exception)
            {
                if (!_isClosed) StatusMenuItem.Header = "钉图 · 剪贴板正忙，请重试";
            }
        }
    }

    private void OnTranslationTargetChanged(object? sender, EventArgs e)
    {
        _translatedRegions = [];
        _isShowingTranslation = false;
        _displayedRegions = _recognition?.Regions ?? [];
        TextOverlay.Children.Clear();
        TextOverlay.Visibility = Visibility.Collapsed;
        InlineEditorCanvas.ClearTranslationOverlay();
        TranslationActionText = "翻译图片文字";
        TranslationHint = null;
        ScheduleAutomaticOcr();
    }

    private bool _toolbarTranslationRunning;
    private async Task TranslateFromToolbarAsync()
    {
        if (_toolbarTranslationRunning) return;
        _toolbarTranslationRunning = true;
        try { await TranslateFromToolbarCoreAsync(); }
        finally { _toolbarTranslationRunning = false; }
    }
    private async Task TranslateFromToolbarCoreAsync()
    {
        var revision = _imageRevision;
        var editVersion = _editorChangeVersion;
        var languageVersion = TranslationTargetPreferences.Version;
        if (InlineEditorCanvas.HasTranslationOverlay)
        {
            InlineEditorCanvas.SetTranslationOverlayVisible(
                !InlineEditorCanvas.IsTranslationOverlayVisible);
            StatusMenuItem.Header = InlineEditorCanvas.IsTranslationOverlayVisible
                ? "钉图 · 已显示翻译，可撤销"
                : "钉图 · 已隐藏翻译";
            return;
        }

        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess)
        {
            StatusMenuItem.Header = recognition.ErrorMessage ?? "文字识别失败";
            return;
        }

        await TranslateTextAsync();
        if (_isClosed || !_isEditorMode || revision != _imageRevision || languageVersion != TranslationTargetPreferences.Version) return;
        if (editVersion != _editorChangeVersion || InlineEditorCanvas.IsTextInputActive)
        { _editorToolbar?.SetTranslationProgress("翻译完成，点击译查看结果，可继续标注。"); return; }
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
            StatusMenuItem.Header = "钉图 · 已添加翻译，可撤销";
        }
    }

    private async Task RedactPrivacyFromToolbarAsync()
    {
        StatusMenuItem.Header = "正在检测敏感信息…";
        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess)
        {
            StatusMenuItem.Header = recognition.ErrorMessage ?? "敏感信息识别失败";
            return;
        }

        var candidates = PrivacyDetectionService.Detect(recognition);
        if (candidates.Count == 0)
        {
            StatusMenuItem.Header = "钉图 · 未检测到敏感信息";
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
            StatusMenuItem.Header = "钉图 · 已取消隐私打码";
            return;
        }

        TextOverlay.Visibility = Visibility.Collapsed;
        InlineEditorCanvas.AddMosaicRegions(
            confirmation.SelectedCandidates.Select(candidate => candidate.Bounds));
        StatusMenuItem.Header =
            $"钉图 · 已添加 {confirmation.SelectedCandidates.Count} 处隐私马赛克";
        InlineEditorCanvas.Focus();
    }


    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseFromContextMenuClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnPinnedShellContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        if (_isEditorMode || _isCropMode)
        {
            e.Handled = true;
        }
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
        StatusMenuItem.Header = IsGrouped ? "钉图 · 已加入编组" : "钉图";
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

            RequestInlineZoom(e);
            e.Handled = true;
            return;
        }

        if (_isCropMode)
        {
            e.Handled = true;
            return;
        }

        if (_isMinimized || e.Delta == 0) { e.Handled = true; return; }
        BeginInlineZoomInteraction();
        _zoomFrames.Queue(e.Delta, e.GetPosition(ImageSurface));
        e.Handled = true;
    }

    private void ApplyWindowZoom(double steps)
    {
        var factor = Math.Pow(1.1, steps);
        var previousWidth = Width;
        var previousHeight = Height;
        var contentWidth = Math.Max(1, previousWidth - ShadowExtentDip);
        var contentHeight = Math.Max(1, previousHeight - ShadowExtentDip);
        var minimumScale = Math.Max(
            Math.Max(1, MinWidth - ShadowExtentDip) / contentWidth,
            Math.Max(1, MinHeight - ShadowExtentDip) / contentHeight);
        var maximumScale = Math.Min(
            Math.Max(1, MaxWidth - ShadowExtentDip) / contentWidth,
            Math.Max(1, MaxHeight - ShadowExtentDip) / contentHeight);
        factor = Math.Clamp(factor, minimumScale, Math.Max(minimumScale, maximumScale));
        var nextWidth = contentWidth * factor + ShadowExtentDip;
        var nextHeight = contentHeight * factor + ShadowExtentDip;
        var bounds = new Rect(Left - (nextWidth - previousWidth) / 2,
            Top - (nextHeight - previousHeight) / 2, nextWidth, nextHeight);
        if (!MonitorGeometryService.TryResizeWindow(this, bounds))
        {
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
        }
    }

    private void RequestInlineZoom(MouseWheelEventArgs e)
    {
        BeginInlineZoomInteraction();
        _zoomFrames.Queue(e.Delta, e.GetPosition(InlineEditorViewport));
    }

    private void ApplyWheelZoom(double steps, WpfPoint pointer)
    {
        if (_isClosed || _isCropMode || _isMinimized) return;
        if (!_isEditorMode) { ApplyWindowZoom(steps); return; }
        ApplyInlineZoom(steps, pointer);
    }

    private void ApplyInlineZoom(double steps, WpfPoint pointer)
    {
        var previousZoom = InlineEditorCanvas.Zoom;
        var contentX =
            (InlineEditorViewport.HorizontalOffset + pointer.X) / previousZoom;
        var contentY =
            (InlineEditorViewport.VerticalOffset + pointer.Y) / previousZoom;
        var factor = Math.Pow(1.1, steps);
        InlineEditorCanvas.SetZoom(previousZoom * factor);
        InlineEditorFrame.Width = InlineEditorCanvas.DisplayWidth;
        InlineEditorFrame.Height = InlineEditorCanvas.DisplayHeight;
        InlineEditorViewport.UpdateLayout();
        InlineEditorViewport.ScrollToHorizontalOffset(
            (contentX * InlineEditorCanvas.Zoom) - pointer.X);
        InlineEditorViewport.ScrollToVerticalOffset(
            (contentY * InlineEditorCanvas.Zoom) - pointer.Y);
        StatusMenuItem.Header = $"钉图 · 正在编辑 · {InlineEditorCanvas.Zoom * 100:0}%";
    }

    private void BeginInlineZoomInteraction()
    {
        RenderOptions.SetBitmapScalingMode(
            InlineEditorCanvas,
            BitmapScalingMode.LowQuality);
        RenderOptions.SetBitmapScalingMode(
            PinnedImage,
            BitmapScalingMode.LowQuality);
        ImageSurface.Background = WpfBrushes.Transparent;
        InlineEditorFrame.Background = WpfBrushes.Transparent;
        _inlineZoomQualityTimer.Stop();
        _inlineZoomQualityTimer.Start();
    }

    private void OnInlineZoomQualityTimerTick(object? sender, EventArgs e)
    {
        _zoomFrames.Flush();
        _inlineZoomQualityTimer.Stop();
        RenderOptions.SetBitmapScalingMode(
            InlineEditorCanvas,
            BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(
            PinnedImage,
            BitmapScalingMode.HighQuality);
        ImageSurface.Background = WpfBrushes.Transparent;
        InlineEditorFrame.Background = WpfBrushes.Transparent;
        if (_displayedRegions.Count > 0) RenderSelectableTextOverlay();
    }

    private void CancelPendingInlineZoom()
    {
        _zoomFrames.Cancel();
        _inlineZoomQualityTimer.Stop();
        RenderOptions.SetBitmapScalingMode(
            InlineEditorCanvas,
            BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(
            PinnedImage,
            BitmapScalingMode.HighQuality);
        ImageSurface.Background = WpfBrushes.Transparent;
        InlineEditorFrame.Background = WpfBrushes.Transparent;
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
        _imagePointerFrames.Start();
        e.Handled = true;
    }

    private void OnInlineEditorPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isPanningInlineEditor)
        {
            return;
        }
        e.Handled = true;
        if (e.MiddleButton != MouseButtonState.Pressed)
        {
            EndInlineEditorPanning();
            return;
        }

    }

    private void PresentInlinePan()
    {
        var current = GetPointerPosition(InlineEditorViewport);
        InlineEditorViewport.ScrollToHorizontalOffset(
            _inlineEditorPanStartHorizontalOffset + _inlineEditorPanStartPoint.X - current.X);
        InlineEditorViewport.ScrollToVerticalOffset(
            _inlineEditorPanStartVerticalOffset + _inlineEditorPanStartPoint.Y - current.Y);
    }

    private void OnInlineEditorPreviewMouseUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_isPanningInlineEditor && e.ChangedButton == MouseButton.Middle)
        {
            PresentInlinePan();
            EndInlineEditorPanning();
            e.Handled = true;
        }
    }

    private void OnInlineEditorLostMouseCapture(object sender, WpfMouseEventArgs e) =>
        EndInlineEditorPanning();

    private void EndInlineEditorPanning()
    {
        _isPanningInlineEditor = false;
        _imagePointerFrames.Stop();
        if (InlineEditorViewport.IsMouseCaptured)
        {
            InlineEditorViewport.ReleaseMouseCapture();
        }
        InlineEditorViewport.Cursor = WpfCursors.Arrow;
    }

    internal void OpenCrop(Window owner)
    {
        _automaticOcr.Cancel();
        EndWindowDrag();
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
        UpdateImageCursor();
        TextOverlay.Visibility = Visibility.Collapsed;
        CropOverlay.Visibility = Visibility.Visible;
        UpdateRenderedImageBounds();
        UpdateCropSelection();
        ShowEditorToolbar(PinnedImageToolbarMode.Crop);
        StatusMenuItem.Header = "钉图 · 框选裁剪";
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        OpenEditor(this);
    }

    internal void OpenEditor(Window owner)
    {
        _automaticOcr.Cancel();
        CancelPendingInlineZoom();
        EndWindowDrag();
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
        UpdateImageCursor();
        ShowEditorToolbar(PinnedImageToolbarMode.Edit);
        StatusMenuItem.Header = "钉图 · 正在编辑";
    }

    private void CommitInlineEditor()
    {
        if (!_isEditorMode)
        {
            return;
        }

        var edited = InlineEditorCanvas.RenderEditedImage();
        ExitInlineEditor();
        ReplaceImage(CapturedImage.FromBitmapSource(edited), preserveSize: true);
    }

    private void ExitInlineEditor()
    {
        if (!_isEditorMode && _inlineEditorImage is null)
        {
            return;
        }

        EndInlineEditorPanning();
        CancelPendingInlineZoom();
        InlineEditorCanvas.Reset();
        _inlineEditorImage?.Dispose();
        _inlineEditorImage = null;
        _isEditorMode = false;
        UpdateImageCursor();
        InlineEditorViewport.Visibility = Visibility.Collapsed;
        PinnedImage.Visibility = Visibility.Visible;
        ScheduleAutomaticOcr();
        if (!_isCropMode)
        {
            CloseEditorToolbar();
        }
        TextOverlay.Visibility = Visibility.Visible;
        StatusMenuItem.Header = IsGrouped ? "钉图 · 已加入编组" : "钉图";
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
        _imagePointerFrames.Stop();
        _isCropMode = false;
        ScheduleAutomaticOcr();
        UpdateImageCursor();
        CropOverlay.ReleaseMouseCapture();
        CropOverlay.Visibility = Visibility.Collapsed;
        TextOverlay.Visibility = Visibility.Visible;
        if (!_isEditorMode)
        {
            CloseEditorToolbar();
        }
        StatusMenuItem.Header = IsGrouped ? "钉图 · 已加入编组" : "钉图";
    }


    internal void ReplaceImage(CapturedImage replacement, bool preserveSize = false)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        _automaticOcr.Cancel();
        _imageRevision++;
        var previous = _capturedImage;
        _capturedImage = replacement;
        DataContext = replacement;
        PinnedImage.Source = replacement.Preview;
        previous.Dispose();
        _recognition = null;
        _displayedRegions = [];
        _translatedRegions = [];
        TextOverlay.Children.Clear();
        IsTranslationEnabled = CanRecognizeText && CanTranslateText;
        TranslationActionText = "翻译图片文字";
        _isShowingTranslation = false;
        if (!preserveSize) ApplyInitialSize();
        if (IsGrouped)
        {
            _groupingPreview = CreateGroupingPreview();
        }
        if (IsPersistent)
        {
            PersistenceChanged?.Invoke(this, EventArgs.Empty);
        }
        ImageChanged?.Invoke(this, EventArgs.Empty);
        ScheduleAutomaticOcr();
    }

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isMinimized && CanMoveImage && e.ChangedButton == MouseButton.Left && e.ClickCount == 2 &&
            !PinnedImageWindow.IsSelectableTextSource(e.OriginalSource))
        {
            e.Handled = true;
            EndWindowDrag();
            Close();
            return;
        }
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
            _imagePointerFrames.Start();
            e.Handled = true;
            return;
        }
        if (!CanMoveImage || IsSelectableTextSource(e.OriginalSource))
        {
            return;
        }

        BeginWindowDrag(e);
    }

    private void OnImageMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_isDraggingThumbnail) e.Handled = true;
    }

    private void PresentThumbnailDrag()
    {
        var workArea = SystemParameters.WorkArea;
        var currentScreen = GetCurrentScreenCursorPosition();
        var top = _thumbnailStartTop +
            currentScreen.Y - _thumbnailDragStartScreen.Y;
        _thumbnailDragMoved |= Math.Abs(top - _thumbnailStartTop) >= 3;
        Left = workArea.Right - Width - 12;
        Top = Math.Clamp(top, workArea.Top + 12, workArea.Bottom - Height - 12);
    }

    private void OnImageMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingThumbnail) PresentThumbnailDrag();
        var restore = _isDraggingThumbnail && !_thumbnailDragMoved;
        EndThumbnailDrag();
        if (restore)
        {
            RestoreFromMinimized();
        }
    }

    private void OnImageLostMouseCapture(object sender, WpfMouseEventArgs e) =>
        EndThumbnailDrag();

    private void OnWindowPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_windowDragTracker.IsActive)
        {
            e.Handled = true;
        }
    }

    private void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_windowDragTracker.IsActive)
        {
            return;
        }

        EndWindowDrag();
    }

    private void OnWindowLostMouseCapture(object sender, WpfMouseEventArgs e) =>
        EndWindowDrag();

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
        _imagePointerFrames.Stop();
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
        RefreshAutomaticOcrSetting();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        ApplyInitialPlacement();
    }

    private async Task RecognizeTextAsync()
    {
        if (!CanRecognizeText || _isClosed)
        {
            StatusMenuItem.Header = "钉图";
            TranslationHint = "当前未配置文字识别";
            return;
        }

        StatusMenuItem.Header = "正在识别图片文字…";
        var revision = _imageRevision;
        try
        {
            using var image = _capturedImage.Clone();
            var recognition = await RecognizeImageAsync(image);
            if (_isClosed || revision != _imageRevision)
            {
                return;
            }

            _recognition = recognition;
            if (!recognition.IsSuccess)
            {
                StatusMenuItem.Header = "文字识别失败";
                TranslationHint = recognition.ErrorMessage ?? "文字识别失败";
                return;
            }

            if (recognition.Regions.Count == 0)
            {
                StatusMenuItem.Header = "钉图 · 未识别到文字";
                TranslationHint = "图片中没有可翻译的文字";
                return;
            }

            _isShowingTranslation = false;
            StatusMenuItem.Header = "钉图 · 文字识别完成";
            TranslationActionText = "翻译";
            IsTranslationEnabled = CanTranslateText;
            TranslationHint = !CanTranslateText
                ? "请先在设置中启用翻译"
                : "翻译图片文字并覆盖显示";
        }
        catch
        {
            if (!_isClosed)
            {
                StatusMenuItem.Header = "文字识别失败";
                TranslationHint = "请检查 OCR 语言设置";
            }
        }
    }

    private Task _imageTranslationTask = Task.CompletedTask;
    internal Task TranslateTextAsync() => !_imageTranslationTask.IsCompleted
        ? _imageTranslationTask : _imageTranslationTask = TranslateImageTextCoreAsync();

    private async Task TranslateImageTextCoreAsync()
    {
        if (_isClosed || !CanTranslateText)
        {
            return;
        }
        var recognition = await EnsureRecognitionAsync();
        if (_isClosed || !recognition.IsSuccess || recognition.Regions.Count == 0) return;

        if (_translatedRegions.Count > 0)
        {
            if (_isEditorMode) return;
            if (_isShowingTranslation)
            {
                ShowOriginalText();
            }
            else
            {
                if (!_isEditorMode) ShowTranslatedText();
            }

            return;
        }

        var languageVersion = TranslationTargetPreferences.Version;
        var revision = _imageRevision;
        IsTranslationEnabled = false;
        StatusMenuItem.Header = "正在翻译图片文字…";
        try
        {
            using var popup = RecognitionPopupSession.Begin(
                _settingsProvider?.Invoke().RecognitionResultPresentation == RecognitionResultPresentationMode.Popup,
                "正在翻译…", "按已配置的模型优先顺序处理", closeAfterCopy: false);
            using var progress = TranslationProgress.Begin(message =>
            {
                if (_isClosed || languageVersion != TranslationTargetPreferences.Version) return;
                TranslationHint = message;
                _editorToolbar?.SetTranslationProgress(message);
                popup?.Update("翻译进度", message);
            });
            var translation = await TranslateRecognitionAsync(recognition);
            if (_isClosed || revision != _imageRevision) return;
            if (languageVersion != TranslationTargetPreferences.Version)
            {
                StatusMenuItem.Header = "目标语言已改变，请重新翻译。";
                return;
            }

            _editorToolbar?.SetTranslationProgress(translation.ErrorMessage ??
                (translation.IsSuccess ? "翻译完成" : "翻译失败"));

            if (!translation.IsSuccess)
            {
                StatusMenuItem.Header = "翻译失败";
                TranslationHint = translation.ErrorMessage ?? "翻译失败";
                IsTranslationEnabled = true;
                return;
            }

            if (translation.Segments.Count != recognition.Regions.Count)
            {
                StatusMenuItem.Header = "翻译结果不完整";
                TranslationHint = "翻译服务返回的分段数量不一致";
                IsTranslationEnabled = true;
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
                popup?.Complete(
                    "钉图 · 已翻译",
                    recognition.Text,
                    string.Join(
                        Environment.NewLine,
                        translation.Segments),
                    translation.ErrorMessage ?? translation.Notice);
                return;
            }

            if (!_isEditorMode) ShowTranslatedText();
            if (!string.IsNullOrWhiteSpace(translation.Notice))
            {
                StatusMenuItem.Header = "已翻译 · 模型质量提示";
                TranslationHint = translation.Notice;
            }
        }
        catch
        {
            if (!_isClosed)
            {
                StatusMenuItem.Header = "翻译失败";
                TranslationHint = "请检查翻译服务设置";
                _editorToolbar?.SetTranslationProgress("翻译失败，请检查翻译服务设置。");
                IsTranslationEnabled = true;
            }
        }
        finally
        {
            if (!_isClosed) IsTranslationEnabled = true;
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
        StatusMenuItem.Header = "钉图 · 已显示原文";
        TranslationActionText = "译文";
        TranslationHint = "显示已缓存的译文";
        IsTranslationEnabled = true;
    }

    private void ShowTranslatedText()
    {
        _displayedRegions = _translatedRegions;
        _isShowingTranslation = true;
        RenderSelectableTextOverlay();
        StatusMenuItem.Header = "钉图 · 已显示译文";
        TranslationActionText = "原文";
        TranslationHint = "显示原始文字";
        IsTranslationEnabled = true;
    }

    private void ShowRecognitionPopup(
        string title,
        string sourceText,
        string? translatedText,
        string? notice = null)
    {
        var popup = new RecognitionResultPopupWindow(
            title,
            sourceText,
            translatedText,
            closeAfterCopy: false,
            notice: notice)
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
            if (_inlineZoomQualityTimer.IsEnabled && _textOverlayViewportSize.Width > 0)
            {
                var source = _capturedImage.Preview;
                var oldScale = Math.Min(_textOverlayViewportSize.Width / source.PixelWidth,
                    _textOverlayViewportSize.Height / source.PixelHeight);
                var newScale = Math.Min(e.NewSize.Width / source.PixelWidth, e.NewSize.Height / source.PixelHeight);
                var ratio = newScale / oldScale;
                TextOverlay.RenderTransform = new MatrixTransform(ratio, 0, 0, ratio,
                    (e.NewSize.Width - _textOverlayViewportSize.Width * ratio) / 2,
                    (e.NewSize.Height - _textOverlayViewportSize.Height * ratio) / 2);
            }
            else RenderSelectableTextOverlay();
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
        _imagePointerFrames.Start();
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

        e.Handled = true;
    }

    private void OnCropMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropDragStart is null)
        {
            return;
        }

        var current = ClampToImage(GetPointerPosition(ImageViewport));
        UpdateCropFromDisplay(new Rect(_cropDragStart.Value, current));
        _cropDragStart = null;
        _imagePointerFrames.Stop();
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
        UpdateTranslationAction();
        toolbar.Closed += (_, _) =>
        {
            // Alt+F4 or a direct toolbar close also ends the edit session.
            // Keep completed annotations before restoring whole-image dragging.
            if (!ReferenceEquals(_editorToolbar, toolbar)) return;
            _editorToolbar = null;
            if (_isEditorMode) CommitInlineEditor();
            else if (_isCropMode) ExitCropMode();
        };
        toolbar.ToolSelected += tool =>
        {
            if (_isEditorMode)
            {
                var color = toolbar.SelectedColor;
                var width = toolbar.SelectedStrokeWidth;
                InlineEditorCanvas.SelectTool(tool);
                // The toolbar has resolved this tool's saved style. Apply it
                // before drawing instead of replacing it with a fresh canvas's
                // initial color when entering the pin editor.
                InlineEditorCanvas.SelectColor(color, updateSelectedAnnotation: false);
                InlineEditorCanvas.SetStrokeWidth(width, updateSelectedAnnotation: false);
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
        toolbar.ImageQuestionRequested += async (_, _) => await ShowImageQuestionAsync(InlineEditorCanvas.RenderEditedImage(), this);
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
            switch (ShowEditorCloseConfirmation("关闭钉图编辑"))
            {
                case PinnedEditorCloseChoice.Save when _isEditorMode:
                    CommitInlineEditor();
                    break;
                case PinnedEditorCloseChoice.Save when _isCropMode:
                    ApplyInlineCrop();
                    break;
                case PinnedEditorCloseChoice.Discard when _isEditorMode:
                    ExitInlineEditor();
                    break;
                case PinnedEditorCloseChoice.Discard when _isCropMode:
                    ExitCropMode();
                    break;
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

    private PinnedEditorCloseChoice ShowEditorCloseConfirmation(string title)
    {
        var dialog = new PinnedEditorCloseDialog(this, title);
        _ = dialog.ShowDialog();
        return dialog.Choice;
    }

    private void RenderSelectableTextOverlay()
    {
        // Preserve the existing glyphs during native sizing. Recreating every
        // TextBox and fitting translated fonts for each WM_SIZE blocks input.
        TextOverlay.Children.Clear();
        TextOverlay.RenderTransform = Transform.Identity;
        var viewportWidth = ImageViewport.ActualWidth;
        var viewportHeight = ImageViewport.ActualHeight;
        _textOverlayViewportSize = new System.Windows.Size(viewportWidth, viewportHeight);
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

        TextOverlay.IsHitTestVisible = true;
        if (!_isShowingTranslation)
        {
            RenderOriginalTextSelection(PixelAlignedImage.GetPixelAlignedBounds(pixelWidth, pixelHeight,
                new System.Windows.Size(viewportWidth, viewportHeight), VisualTreeHelper.GetDpi(PinnedImage)));
            return;
        }

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
                Tag = "UserContent",
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
            if (current is WpfTextBox or SelectableOcrTextOverlay)
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
        CancelPendingInlineZoom();
        if (!CanMoveImage || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // Start native capture while the original button-down is still
        // active. Activating first can consume the first press as a focus
        // change, which makes the window move only after a second click.
        if (_windowDragTracker.Start())
        {
            e.Handled = true;
        }
    }


    private void EndWindowDrag()
    {
        _windowDragTracker.Stop();
    }

    private static WpfPoint GetPointerPosition(Visual surface)
    {
        var pointer = System.Windows.Forms.Cursor.Position;
        return surface.PointFromScreen(new WpfPoint(pointer.X, pointer.Y));
    }

    private void PresentImagePointer()
    {
        if (_isClosed) { _imagePointerFrames.Stop(); return; }
        if (_isDraggingThumbnail) PresentThumbnailDrag();
        else if (_isPanningInlineEditor) PresentInlinePan();
        else if (_cropDragStart is { } start)
        {
            if (!CropOverlay.IsMouseCaptured)
            {
                _cropDragStart = null;
                _imagePointerFrames.Stop();
                return;
            }
            UpdateCropFromDisplay(new Rect(start, ClampToImage(GetPointerPosition(ImageViewport))));
        }
        else _imagePointerFrames.Stop();
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
        var maximumContentWidth = Math.Max(1, maximumWindowWidth - ShadowExtentDip);
        var maximumContentHeight = Math.Max(1, maximumWindowHeight - ShadowExtentDip);
        var scale = Math.Min(
            1,
            Math.Min(
                maximumContentWidth / Math.Max(1, contentWidth),
                maximumContentHeight / Math.Max(1, contentHeight)));

        MaxWidth = Math.Max(MinWidth, workAreaWidth);
        MaxHeight = Math.Max(MinHeight, workAreaHeight);
        Width = Math.Max(MinWidth, contentWidth * scale + ShadowExtentDip);
        Height = Math.Max(MinHeight, contentHeight * scale + ShadowExtentDip);
    }

    internal BitmapSource CreateGroupingPreview()
    {
        var source = Preview;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bounds = PixelAlignedImage.GetPixelAlignedBounds(source.PixelWidth, source.PixelHeight,
            new System.Windows.Size(Math.Max(1, Width - ShadowExtentDip), Math.Max(1, Height - ShadowExtentDip)), dpi);
        // Grouping stores physical pixels, not WPF DIPs. Repeated grouping on a
        // scaled display must not shrink and resample a native-size image.
        var width = Math.Max(1, (int)Math.Round(bounds.Width * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * dpi.DpiScaleY));
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
        var maximumLeft = Math.Max(
            workArea.Left,
            workArea.Right - windowBounds.Width);
        var maximumTop = Math.Max(
            workArea.Top,
            workArea.Bottom - windowBounds.Height);
        var desiredLeft = Math.Clamp(
            sourceRegion.X - (int)Math.Round(ShadowMarginDip * dpi.X),
            workArea.Left,
            maximumLeft);
        var desiredTop = Math.Clamp(
            sourceRegion.Y - (int)Math.Round(ShadowMarginDip * dpi.Y),
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
