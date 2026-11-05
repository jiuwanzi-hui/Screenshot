using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Drawing.Imaging;
using Screenshot.App.Editor;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using WinForms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Screenshot.App.Capture;

internal static class CaptureInputDiagnostics
{
    [System.Diagnostics.Conditional("SNAPCUT_INPUT_DIAGNOSTICS")]
    public static void Write(string _)
    {
        // Input diagnostics are intentionally removed from production paths.
    }
}

public enum CapturePointerButton
{
    Left,
    Right,
}

public sealed class CapturePointerContinuation
{
    private readonly TaskCompletionSource _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal CapturePointerContinuation(
        CapturePointerButton button,
        DrawingPoint? startScreenPoint = null,
        bool enterPickerWhenReleasedWithoutSelection = false)
    {
        Button = button;
        StartScreenPoint = startScreenPoint;
        EnterPickerWhenReleasedWithoutSelection =
            enterPickerWhenReleasedWithoutSelection;
    }

    public CapturePointerButton Button { get; }

    internal Guid DiagnosticId { get; } = Guid.NewGuid();

    internal DrawingPoint? StartScreenPoint { get; }

    internal bool EnterPickerWhenReleasedWithoutSelection { get; }

    internal Task WaitForReleaseAsync() => _released.Task;

    internal void NotifyReleased() => _released.TrySetResult();
}

public sealed class CaptureOverlayOptions
{
    public required string SaveDirectory { get; init; }

    public required bool KeepHistory { get; init; }

    public int ScreenshotScalePercent { get; init; } = 100;

    public CaptureAspectRatio CaptureAspectRatio { get; init; } = CaptureAspectRatio.Free;

    public Action<CaptureAspectRatio>? CaptureAspectRatioChanged { get; init; }

    public PngSaveLocationMode PngSaveLocationMode { get; init; } =
        PngSaveLocationMode.DefaultDirectory;

    public bool AutoSaveOnComplete { get; init; }

    public required int HistoryLimit { get; init; }

    public required CaptureHistoryService HistoryService { get; init; }

    public required PinnedImageManager PinnedImageManager { get; init; }

    public string TranslationTargetLanguage { get; init; } = "zh-Hans";

    public RecognitionResultPresentationMode RecognitionResultPresentation { get; init; } =
        RecognitionResultPresentationMode.Overlay;

    public required Func<CapturedImage, Task> StartOcrAsync { get; init; }

    public Func<CapturedImage, Task<OcrRecognitionResult>>? RecognizeTextAsync { get; init; }

    public Func<CapturedImage, CancellationToken, Task<OcrRecognitionResult>>? RecognizeTextWithCancellationAsync { get; init; }

    public bool AutoOcrAfterCapture { get; init; }
    public Func<AppSettings>? SettingsProvider { get; init; }
    public WatermarkSettings? Watermark { get; init; }

    public Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
        TranslateTextAsync { get; init; }

    internal Func<CancellationToken, TranslationPreparation?>? PrepareTranslation { get; init; }
    internal Func<OcrRecognitionResult, CancellationToken, Task<TranslationSegmentsResult>>?
        TranslateTextWithCancellationAsync { get; init; }

    public Func<string, string, string?, string?, Task>? ShowRecognitionPopupAsync { get; init; }

    public Func<CapturedImage, CancellationToken, Task<ContentRecognitionResult>>?
        RecognizeFormulaAsync { get; init; }

    public bool RecognizeTextAfterSelection { get; init; }

    public bool TranslateTextAfterSelection { get; init; }

    public CapturePointerContinuation? InitialPointerContinuation { get; init; }

    public Func<ScreenRegion, Task>? StartScrollCaptureAsync { get; init; }

    public Func<ScreenRegion, CapturedImage?, Task>?
        StartScrollCaptureWithSnapshotAsync { get; init; }

    public Func<ScreenRegion, Task>? StartVideoRecordingAsync { get; init; }

    public ScreenRegion? InitialSelection { get; init; }

    public Action<ScreenRegion>? CaptureCompleted { get; init; }

    public Action? CaptureClosed { get; init; }
    internal Action? CapturePresented { get; init; }
    internal IntPtr ResidentSelectionWindow { get; init; }

    public Action<string>? CaptureStartupFailed { get; init; }

    public string CompletionHotKey { get; init; } = string.Empty;

    public ArrowStyle ArrowStyle { get; init; } = ArrowStyle.Filled;

    public ArrowToolMode ArrowToolMode { get; init; } = ArrowToolMode.Straight;

    public Action<ArrowStyle>? ArrowStyleChanged { get; init; }

    public Action<ArrowToolMode>? ArrowToolModeChanged { get; init; }

    public ShapeToolMode ShapeToolMode { get; init; } = ShapeToolMode.Rectangle;

    public Action<ShapeToolMode>? ShapeToolModeChanged { get; init; }

    public AnnotationToolMode LastAnnotationTool { get; init; } =
        AnnotationToolMode.Rectangle;

    public Action<AnnotationToolMode>? LastAnnotationToolChanged { get; init; }

    public string CustomStrokeColor { get; init; } = string.Empty;

    public Action<string>? CustomStrokeColorChanged { get; init; }

    public int[] CustomColorPalette { get; init; } = [];

    public Action<int[]>? CustomColorPaletteChanged { get; init; }

    public double ToolbarPositionXRatio { get; init; } = -1;

    public double ToolbarPositionYRatio { get; init; } = -1;

    public Action<double, double>? ToolbarPositionChanged { get; init; }

    public CaptureToolbarFeature[] VisibleToolbarFeatures { get; init; } =
        Enum.GetValues<CaptureToolbarFeature>();

    public CaptureToolbarFeature[] ToolbarFeatureOrder { get; init; } =
        AppSettings.DefaultCaptureToolbarFeatureOrder();

    public IReadOnlyDictionary<CaptureToolbarFeature, string> ToolbarFeatureShortcuts { get; init; } =
        new Dictionary<CaptureToolbarFeature, string>();

    public CaptureToolbarRowCount ToolbarRows { get; init; } =
        CaptureToolbarRowCount.One;

    public double ToolbarScalePercent { get; init; } = 100;

    // Keyboard shortcuts can defer the initial activation by one dispatcher
    // turn so the shortcut's input frame is not forced through WPF activation.
    // Mouse-triggered sessions keep the original synchronous hand-off.
    public bool DeferInitialColorPickerActivation { get; init; }

    public bool DeferInitialScreenCapture { get; init; }
}

public partial class CaptureOverlayWindow : Window, IDisposable
{
    private static CaptureOverlayWindow? _activeInteractiveOverlay;
    private bool _isTransitioningCapture;
    private static readonly object NativeWindowPoolLock = new();
    private static NativeWindowPool? _nativeWindowPool;
    private const int TopmostWindow = -1;
    private const int ExtendedWindowStyleIndex = -20;
    private const int NonClientHitTestMessage = 0x0084;
    private const int TransparentHitTest = -1;
    private const uint DoNotResize = 0x0001;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const uint DoNotMove = 0x0002;
    private const uint DoNotChangeZOrder = 0x0004;
    private const uint FrameChanged = 0x0020;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const int RegionCombineDifference = 4;
    private const double MinimumSelectionEdge = 2;
    private const double ResizeThumbHalfSize = 6;
    private const int ColorPickerSampleRadius = 12;
    private const int ColorPickerSampleSize = ColorPickerSampleRadius * 2 + 1;
    // Keep the existing 104px source size; the XAML image scales it to the
    // same 112px visual box while the backing bitmap is reused between moves.
    private const int ColorPickerMagnifierSize = 104;
    // The move affordances sit immediately inside each resize corner. Their
    // small size leaves the visible corner thumb itself untouched.
    private const double MoveZoneInset = 14;

    private readonly ScreenRegion _virtualScreenBounds;
    private CaptureOverlayOptions? _options;
    private IReadOnlyDictionary<CaptureToolbarFeature, string> _toolbarFeatureShortcuts =
        new Dictionary<CaptureToolbarFeature, string>();
    private readonly bool _deferInitialColorPickerActivation;
    private bool _deferInitialScreenCapture;
    private readonly HotKeyGesture? _completionHotKeyGesture;
    private readonly bool _isScrollCaptureSelection;
    private readonly CapturePointerContinuation? _initialPointerContinuation;
    private readonly TaskCompletionSource<ScreenRegion?> _selectionCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ScrollCaptureSelection?>?
        _scrollCaptureSelectionCompletionSource;
    private readonly DispatcherTimer _windowSnapTimer;
    private readonly DispatcherTimer _selectionIdleTimer;
    private readonly NativeSelectionFrameWindow _nativeSelectionFrame;
    private readonly NativeSelectionMaskWindow _nativeSelectionMask;
    private readonly NativeSelectionSizeWindow _nativeSelectionSize;
    private readonly CancellationTokenSource _lifetimeCancellationSource = new();
    private WpfPoint _selectionStartPoint;
    private WpfPoint _dragStartPoint;
    private Rect _dragStartBounds;
    private Rect? _pendingSelectionBounds;
    private Rect? _lastRenderedSelectionBounds;
    private bool _selectionBoundsUpdateScheduled;
    private long _lastSelectionPreviewTimestamp;
    private double _selectionPreviewHz = 60;
    private long _lastSelectionBadgeTimestamp;
    private ScreenRegion? _selectionAdjustmentStartPhysicalBounds;
    private Rect? _selectionAdjustmentProtectedBounds;
    private CapturedImage? _inlineEditorImage;
    private CapturedImage? _screenSnapshot;
    private EditorTool _selectedInlineTool = EditorTool.Rectangle;
    private bool _hasSelectedInlineTool;
    private ArrowStyle _currentArrowStyle = ArrowStyle.Filled;
    private ArrowToolMode _currentArrowToolMode = ArrowToolMode.Straight;
    private ShapeToolMode _currentShapeToolMode = ShapeToolMode.Rectangle;
    private bool _isSelecting;
    private CapturePointerButton? _continuedSelectionButton;
    private Task _continuedSelectionReleaseTask = Task.CompletedTask;
    private bool _isMovingSelection;
    private bool _toolbarHiddenForMove;
    private bool _toolbarMaskPublishScheduled;
    private bool _isSelectionAdjustmentInProgress;
    private bool _isResizeDragging;
    private bool _resizeControlsSuppressed;
    private bool _resizeLeftEdge;
    private bool _resizeTopEdge;
    private bool _resizeRightEdge;
    private bool _resizeBottomEdge;
    private DrawingPoint _resizeDragStartCursor;
    private System.Drawing.Rectangle _resizeDragStartPhysicalBounds;
    private System.Drawing.Rectangle? _selectionAdjustmentProtectedPhysicalBounds;
    private bool _isActionInProgress;
    private bool _isEditorInitializing;
    private bool _isOcrInitializing;
    private bool _isQrInitializing;
    private bool _isTranslationInitializing;
    private bool _completeAfterRightButtonUp;
    private bool _cancelScrollCaptureAfterRightButtonUp;
    private bool _isWindowSnapClickPending;
    private bool _isColorPickerActive;
    private PointerFramePump? _colorPickerFrames;
    private DrawingPoint? _lastColorPickerCursor;
    private bool _colorPickerSamplePending;
    // Keyboard-triggered sessions defer cursor capture until after Show().
    private bool _initialPickerActivationQueued;
    // Keep the bootstrap overlay out of the mouse hit-test path until its
    // first desktop frame has been committed.
    private bool _initialInputPassthrough;
    private DrawingColor _selectedPixelColor = DrawingColor.Black;
    private Rect? _windowSnapBounds;
    private IntPtr _windowHandle;
    private bool _isCompleted;
    private bool _closedCleanupScheduled;
    private bool _isInitializing = true;
    private OcrRecognitionResult? _inlineOcrResult;
    private ContentRecognitionResult? _inlineQrResult;
    private QrCodeScanResult? _inlineQrScan;
    private ContentRecognitionResult? _inlineTableResult;
    private ContentRecognitionResult? _inlineFormulaResult;
    private IReadOnlyList<OcrTextRegion>? _inlineTranslatedTextRegions;
    private IReadOnlyList<TranslatedTextAnnotationRegion>?
        _inlineTranslatedAnnotationRegions;
    private bool _isShowingTranslatedText;
    private bool _isUnifiedRecognitionVisible;
    private int _automaticRecognitionGeneration;
    private int _selectionMessageGeneration;
    private bool _lastObservedTranslationOverlayExists;
    private bool _isScrollCaptureSelectionPublished;
    private bool _isScrollCaptureSelectionLocked;
    private bool _isScrollCaptureTemporarilyHidden;
    private bool _hasVisibleInlineEditorTools;
    private bool _isDisposed;
    private bool _closeQueued;
    private bool _hasCustomToolbarPosition;
    private bool _initialScreenInputReady;
    private bool _initialScreenPublicationPending;
    private bool _initialScreenCaptureStarted;
    private DrawingPoint? _lastWindowSnapProbeCursor;
    private long _lastColorPickerUpdateTimestamp;
    private long _colorPickerSampleRevision;
    private int _colorPickerSampleWorkerActive;
    private int _pendingColorPickerPixelX;
    private int _pendingColorPickerPixelY;
    private System.Drawing.Bitmap? _pendingColorPickerBitmap;
    private ColorPickerSample? _pendingColorPickerResult;
    private long _pendingColorPickerResultRevision;
    private int _colorPickerResultDispatchScheduled;
    private readonly object _colorPickerSampleLock = new();
    private readonly object _colorPickerBitmapLifetimeLock = new();
    private bool _isToolbarSurfaceDragging;
    private WpfPoint _toolbarSurfaceDragStart;
    private WpfPoint _toolbarSurfaceStartPosition;
    private double _toolbarPositionXRatio = -1;
    private double _toolbarPositionYRatio = -1;
    private readonly ToolbarDragHintBehavior _toolbarDragHint;
    private readonly double _toolbarScale;
    private readonly MonitorDpiScale _overlayDpi;
    private readonly double _interactionRefreshRate;
    private readonly System.Windows.Media.TranslateTransform _colorPickerPanelTransform = new();
    private WpfColor? _inlineCustomColor;
    private int[] _inlineCustomColorPalette = [];
    private WeakReference<ScrollCaptureSelection>?
        _publishedScrollCaptureSelection;
    private HwndSource? _windowSource;

    private readonly record struct ColorPickerSample(
        DrawingColor Color,
        BitmapSource Magnifier);

    internal CaptureOverlayWindow(
        CaptureOverlayOptions? options,
        bool isScrollCaptureSelection = false,
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? initialPointerContinuation = null,
        bool isStartupPrewarm = false,
        bool deferInitialColorPickerActivation = false,
        bool deferInitialScreenCapture = false,
        NativeWindowPool? preparedNativeWindowPool = null)
    {
        using var timing = CaptureTimingDiagnostics.Begin(
            "overlay-constructor",
            $"startup={isStartupPrewarm} hasSnapshot={initialScreenSnapshot is not null}");
        _options = options;
        _toolbarFeatureShortcuts = options?.ToolbarFeatureShortcuts ??
            new Dictionary<CaptureToolbarFeature, string>();
        CaptureTimingDiagnostics.Mark(
            "overlay-constructor-options-ready",
            $"startup={isStartupPrewarm} deferCapture={deferInitialScreenCapture}");
        _deferInitialColorPickerActivation =
            deferInitialColorPickerActivation ||
            options?.DeferInitialColorPickerActivation == true;
        _deferInitialScreenCapture =
            deferInitialScreenCapture ||
            options?.DeferInitialScreenCapture == true;
        _initialInputPassthrough = _deferInitialScreenCapture;
        _toolbarScale = Math.Clamp(
            double.IsFinite(options?.ToolbarScalePercent ?? 100)
                ? (options?.ToolbarScalePercent ?? 100) / 100d
                : 1,
            0.5,
            1.5);
        var completionHotKey = string.IsNullOrWhiteSpace(options?.CompletionHotKey)
            ? AppSettings.DefaultCompleteCaptureHotKey
            : options!.CompletionHotKey;
        if (HotKeyGesture.TryParseCompletionShortcut(
                completionHotKey,
                out var parsedCompletionHotKey,
                out _))
        {
            _completionHotKeyGesture = parsedCompletionHotKey;
        }
        _currentArrowStyle = options?.ArrowStyle ?? ArrowStyle.Filled;
        _currentArrowToolMode = options?.ArrowToolMode ?? ArrowToolMode.Straight;
        _currentShapeToolMode = options?.ShapeToolMode ?? ShapeToolMode.Rectangle;
        _selectedInlineTool = ToEditorTool(
            AnnotationToolMode.Rectangle,
            options?.ArrowToolMode ?? ArrowToolMode.Straight,
            options?.ShapeToolMode ?? ShapeToolMode.Rectangle);
        _isScrollCaptureSelection = isScrollCaptureSelection;
        _sessionAspectRatio = options?.CaptureAspectRatio ?? CaptureAspectRatio.Free;
        if (isStartupPrewarm)
        {
            // Keep the one-time visual-tree warm-up completely invisible and
            // non-activating. OnClosed still performs the normal native-handle
            // cleanup, so this cannot leak a hidden overlay instance.
            Opacity = 0;
            ShowActivated = false;
            ShowInTaskbar = false;
        }
        _initialPointerContinuation =
            initialPointerContinuation ?? options?.InitialPointerContinuation;
        _scrollCaptureSelectionCompletionSource = isScrollCaptureSelection
            ? new TaskCompletionSource<ScrollCaptureSelection?>(
                TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        // The capture toolbar starts from the automatic placement on every
        // capture. Its drag position is intentionally session-local.
        _hasCustomToolbarPosition = false;
        using (CaptureTimingDiagnostics.Begin("overlay-initialize-component"))
        {
            InitializeComponent();
            CaptureTimingDiagnostics.Mark("overlay-xaml-load-complete");
            TranslateButton.ContextMenu.Closed += OnInlineContextMenuClosed;
            TranslationTargetPreferences.Changed += OnTranslationTargetChanged;
            UpdateTranslationToolTip();
            Closed += (_, _) => TranslationTargetPreferences.Changed -= OnTranslationTargetChanged;
            InlineEditorCanvas.UseParentCursorWhenUnedited = true;
            AnnotationVariantButton.Configure(InlineDoubleArrowToolButton, EditorTool.DoubleArrow, SelectVariantTool, inlineOptions: true);
            AnnotationVariantButton.Configure(InlineLineToolButton, EditorTool.Line, SelectVariantTool, inlineOptions: true);
            AnnotationVariantButton.Configure(InlineMosaicToolButton, EditorTool.Mosaic, SelectVariantTool, inlineOptions: true);
            NumberAnnotationOptions.Configure(InlineNumberToolButton, () => InlineEditorCanvas, inlineOptions: true);
            TextAnnotationOptions.Configure(InlineTextToolButton, () => InlineEditorCanvas, closed: OnInlineContextMenuClosed, inlineOptions: true);
            InlineToolAdjustments.OptionsChanged += () =>
            {
                UpdateInlineToolOptionPanels();
                RefreshCaptureToolbarLayoutAfterOptionChange();
            };
            InlineActionAdjustments.OptionsChanged += RefreshCaptureToolbarLayoutAfterOptionChange;
            InlineDoubleArrowToolButton.ContextMenu.Closed += OnInlineContextMenuClosed;
            InlineLineToolButton.ContextMenu.Closed += OnInlineContextMenuClosed;
            InlineMosaicToolButton.ContextMenu.Closed += OnInlineContextMenuClosed;
        }
        // Move the picker in the compositor without invalidating the full
        // overlay layout on every mouse packet.
        ColorPickerPanel.RenderTransform = _colorPickerPanelTransform;
        CaptureTimingDiagnostics.Mark("overlay-xaml-ready");
        // Keep the declarative Topmost=True state stable for the deferred
        // bootstrap. The window is click-through through WM_NCHITTEST until
        // the first frame is ready, so changing z-order later is unnecessary
        // and would retarget the current cursor packet.
        using (CaptureTimingDiagnostics.Begin("overlay-native-windows"))
        {
            var nativeWindows = preparedNativeWindowPool ?? (isStartupPrewarm
                ? CreateNativeWindowPool()
                : TakeNativeWindowPool());
            _nativeSelectionFrame = nativeWindows.Frame;
            _nativeSelectionMask = nativeWindows.Mask;
            _nativeSelectionSize = nativeWindows.Size;
        }
        _nativeSelectionFrame.AttachSizeWindow(_nativeSelectionSize);
        foreach (var resizeThumb in new[]
        {
            TopLeftResizeThumb,
            TopRightResizeThumb,
            BottomLeftResizeThumb,
            BottomRightResizeThumb,
            TopResizeThumb,
            LeftResizeThumb,
            RightResizeThumb,
            BottomResizeThumb,
        })
        {
            resizeThumb.DragStarted += OnSelectionResizeThumbDragStarted;
        }
        SelectionFrameTheme.Attach(this, _nativeSelectionFrame);
        SyncNativeSizeBadgeTheme();
        using (CaptureTimingDiagnostics.Begin("overlay-toolbar-setup"))
        {
            CaptureToolbar.LayoutTransform = new System.Windows.Media.ScaleTransform(
                _toolbarScale,
                _toolbarScale);
        _toolbarDragHint = new ToolbarDragHintBehavior(
            CaptureToolbar,
            CaptureToolbar);
        ApplyThemedContextMenu(InlineShapeToolButton.ContextMenu);
        ApplyThemedContextMenu(InlineArrowToolButton.ContextMenu);
        InlineShapeToolButton.ContextMenu.Closed += OnInlineContextMenuClosed;
        InlineArrowToolButton.ContextMenu.Closed += OnInlineContextMenuClosed;
        if (_selectedInlineTool is EditorTool.Arrow or EditorTool.CurvedArrow)
        {
            InlineShapeToolButton.IsChecked = false;
            InlineArrowToolButton.Tag = _selectedInlineTool.ToString();
            InlineArrowToolButton.IsChecked = true;
        }
        else
        {
            InlineShapeToolButton.IsChecked = true;
            InlineShapeToolIcon.Data = (System.Windows.Media.Geometry)FindResource(
                _currentShapeToolMode == ShapeToolMode.Ellipse
                    ? "EllipseIconGeometry"
                    : "RectangleIconGeometry");
            InlineShapeToolButton.ToolTip = _currentShapeToolMode == ShapeToolMode.Ellipse
                ? "椭圆"
                : "矩形";
        }
        if (_selectedInlineTool is not (EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Arrow or EditorTool.CurvedArrow))
        {
            GetInlineToolButton(_selectedInlineTool).IsChecked = true;
        }
        UpdateInlineShapeButtonPresentation();
        UpdateInlineShapeMenuState();
        UpdateInlineArrowMenuState();
        UpdateInlineArrowButtonPresentation();
        InlineEditorCanvas.SelectArrowStyle(
            _currentArrowStyle,
            updateSelectedAnnotation: false);
        ApplySavedInlineCustomColor(options?.CustomStrokeColor);
        _inlineCustomColorPalette = NormalizeCustomColorPalette(
            options?.CustomColorPalette);
        ApplyToolbarFeatureVisibility(options?.VisibleToolbarFeatures);
        ApplyToolbarLayout(
            options?.ToolbarFeatureOrder,
            options?.ToolbarRows ?? CaptureToolbarRowCount.One);
        ClearInlineToolSelection();
        ToolbarVariantShortcut.Attach(this, () =>
            _hasSelectedInlineTool && !_isColorPickerActive && !_isActionInProgress &&
            !_isSelecting && !_isMovingSelection && !_isResizeDragging &&
            !InlineEditorCanvas.IsTextInputActive && !InlineEditorCanvas.IsMouseCaptured
                ? InlineToolAdjustments : null);
        // Choosing the initial visible tool is setup, not a user preference
        // change. In particular, preparing a cancelled selection must not save.
        _isInitializing = false;
        _windowSnapTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _windowSnapTimer.Tick += OnWindowSnapTimerTick;
        _selectionIdleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _selectionIdleTimer.Tick += OnSelectionIdleTimerTick;
        ScrollCaptureButton.Visibility =
            ScrollCaptureButton.Visibility == Visibility.Visible &&
            options?.StartScrollCaptureAsync is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        RecordButton.Visibility =
            RecordButton.Visibility == Visibility.Visible &&
            options?.StartVideoRecordingAsync is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateToolbarSeparators();
        UpdateConfirmButtonToolTip();
        InlineEditorCanvas.HistoryChanged += OnInlineEditorHistoryChanged;
        InlineEditorCanvas.AnnotationSelectionChanged +=
            OnInlineAnnotationSelectionChanged;
            InlineEditorCanvas.PreviewMouseLeftButtonDown +=
                OnInlineEditorCanvasPreviewMouseLeftButtonDown;
        }

        _virtualScreenBounds = VirtualScreen.GetBounds();
        _interactionRefreshRate = DisplayRefreshRateService.GetInteractionRefreshRate(
            new DrawingRectangle(
                _virtualScreenBounds.X,
                _virtualScreenBounds.Y,
                _virtualScreenBounds.Width,
                _virtualScreenBounds.Height));
        _selectionPreviewHz = _interactionRefreshRate;
        _overlayDpi = MonitorGeometryService.GetDpiScale(
            new DrawingRectangle(
                _virtualScreenBounds.X,
                _virtualScreenBounds.Y,
                _virtualScreenBounds.Width,
                _virtualScreenBounds.Height));
        _nativeSelectionFrame.AttachMaskWindow(
            _nativeSelectionMask,
            ToDrawingRectangle(_virtualScreenBounds));
        if ((!isStartupPrewarm || initialScreenSnapshot is not null) &&
            (!_deferInitialScreenCapture || initialScreenSnapshot is not null))
        {
            try
            {
                _screenSnapshot = initialScreenSnapshot ??
                    ScreenCaptureService.Capture(_virtualScreenBounds);
                if (!_deferInitialScreenCapture &&
                    !(_deferInitialColorPickerActivation &&
                      initialScreenSnapshot is not null))
                {
                    FrozenScreenImage.Source = _screenSnapshot.Preview;
                }
            }
            catch
            {
                lock (_colorPickerBitmapLifetimeLock)
                {
                    _screenSnapshot?.Dispose();
                    _screenSnapshot = null;
                }
                FrozenScreenImage.Source = null;
            }
        }
        if (_deferInitialScreenCapture)
        {
            // 直接设置全屏大小但不显示，避免后续从1x1调整到全屏的卡顿
            // 虽然创建大的透明窗口有开销，但比后续调整大小的卡顿要好
            Left = _virtualScreenBounds.X / _overlayDpi.X;
            Top = _virtualScreenBounds.Y / _overlayDpi.Y;
            Width = _virtualScreenBounds.Width / _overlayDpi.X;
            Height = _virtualScreenBounds.Height / _overlayDpi.Y;
        }
        else
        {
            Left = _virtualScreenBounds.X / _overlayDpi.X;
            Top = _virtualScreenBounds.Y / _overlayDpi.Y;
            Width = _virtualScreenBounds.Width / _overlayDpi.X;
            Height = _virtualScreenBounds.Height / _overlayDpi.Y;
        }
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnBackgroundStateChanged;
        ConfigureResidentPresentation();
        Core.StartupDiagnostics.LogWindowCreation(
            $"Overlay geometry: physical={_virtualScreenBounds.X},{_virtualScreenBounds.Y}," +
            $"{_virtualScreenBounds.Width}x{_virtualScreenBounds.Height}; " +
            $"dpi={_overlayDpi.X:0.###}x{_overlayDpi.Y:0.###}; " +
            $"logical={Left:0.##},{Top:0.##},{Width:0.##}x{Height:0.##}");
        CaptureTimingDiagnostics.Mark(
            "overlay-constructor-ready",
            $"physicalBounds={_virtualScreenBounds.X},{_virtualScreenBounds.Y}," +
            $"{_virtualScreenBounds.Width}x{_virtualScreenBounds.Height} " +
            $"dpi={_overlayDpi.X:0.###}x{_overlayDpi.Y:0.###} " +
            $"logicalBounds={Left:0.##},{Top:0.##},{Width:0.##}x{Height:0.##} " +
            $"deferCapture={_deferInitialScreenCapture}");
    }

    public static Task<ScreenRegion?> SelectAsync(
        CapturePointerContinuation? initialPointerContinuation = null,
        CapturedImage? initialScreenSnapshot = null,
        bool deferInitialColorPickerActivation = false,
        bool deferInitialScreenCapture = false)
    {
        var completion = new TaskCompletionSource<ScreenRegion?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        NativeWindowPool? nativePool = null;
        try
        {
            nativePool = PrepareNativeBootstrap();
            var dispatcher = System.Windows.Application.Current?.Dispatcher ??
                Dispatcher.CurrentDispatcher;
            _ = CaptureStartupDispatcher.Queue(dispatcher,
                new Action(() =>
                {
                    try
                    {
                        var overlay = new CaptureOverlayWindow(
                            options: null,
                            initialScreenSnapshot: initialScreenSnapshot,
                            initialPointerContinuation: initialPointerContinuation,
                            deferInitialColorPickerActivation:
                                deferInitialColorPickerActivation,
                            deferInitialScreenCapture: deferInitialScreenCapture,
                            preparedNativeWindowPool: nativePool);
                        nativePool = null;
                        if (deferInitialScreenCapture)
                        {
                            overlay.ShowActivated = false;
                            overlay.Opacity = 0;
                            Core.StartupDiagnostics.LogWindowCreation("CaptureOverlay: deferred capture mode, ShowActivated=false, Opacity=0");
                        }
                        else
                        {
                            Core.StartupDiagnostics.LogWindowCreation("CaptureOverlay: normal mode");
                        }
                        if (deferInitialScreenCapture &&
                            initialScreenSnapshot is null)
                        {
                            _ = overlay.CaptureBeforeShowingAsync();
                        }
                        else
                        {
                            overlay.Show();
                        }
                        if (deferInitialScreenCapture &&
                                 initialScreenSnapshot is not null)
                        {
                            _ = CaptureStartupDispatcher.Queue(overlay.Dispatcher,
                                new Action(() =>
                                {
                                    Core.StartupDiagnostics.LogWindowCreation("CaptureOverlay: publishing initial screen frame");
                                    overlay.PublishInitialScreenFrame();
                                }));
                        }

                        _ = overlay._selectionCompletionSource.Task.ContinueWith(
                            task => completion.TrySetResult(task.Result),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                    catch
                    {
                        nativePool?.Dispose();
                        nativePool = null;
                        initialScreenSnapshot?.Dispose();
                        completion.TrySetResult(null);
                    }
                }));
        }
        catch
        {
            nativePool?.Dispose();
            initialScreenSnapshot?.Dispose();
            completion.TrySetResult(null);
        }

        return completion.Task;
    }

    public static Task<ScrollCaptureSelection?> SelectForScrollCaptureAsync(
        CapturePointerContinuation? initialPointerContinuation = null)
    {
        var overlay = new CaptureOverlayWindow(
            options: null,
            isScrollCaptureSelection: true,
            initialPointerContinuation: initialPointerContinuation);
        overlay.Show();
        return overlay._scrollCaptureSelectionCompletionSource!.Task;
    }

    public static Task<ScrollCaptureSelection?> SelectForScrollCaptureAsync(
        ScreenRegion initialSelection)
    {
        return SelectForScrollCaptureAsync(initialSelection, initialScreenSnapshot: null);
    }

    public static Task<ScrollCaptureSelection?> SelectForScrollCaptureAsync(
        ScreenRegion initialSelection,
        CapturedImage? initialScreenSnapshot)
    {
        var overlay = new CaptureOverlayWindow(
            options: null,
            isScrollCaptureSelection: true,
            initialScreenSnapshot: initialScreenSnapshot);
        // Prepare the replacement behind the existing selection. Never expose
        // an unpositioned full-screen surface during a mode hand-off.
        overlay.Opacity = 0;
        overlay.Show();
        overlay.UpdateLayout();
        overlay.ApplyInitialScrollCaptureSelection(initialSelection);
        overlay.Opacity = 1;
        CloseActiveInteractiveSelectionForTransition();
        return overlay._scrollCaptureSelectionCompletionSource!.Task;
    }

    internal static void CloseActiveInteractiveSelectionForTransition()
    {
        var overlay = _activeInteractiveOverlay;
        if (overlay is null || overlay._isDisposed || overlay._isCompleted)
        {
            return;
        }

        if (overlay.Dispatcher.CheckAccess())
        {
            overlay.CompleteSelection(result: null);
            return;
        }

        _ = overlay.Dispatcher.BeginInvoke(
            new Action(() => overlay.CompleteSelection(result: null)));
    }

    public static CaptureOverlayWindow ShowInteractive(
        CaptureOverlayOptions options,
        CapturedImage? initialScreenSnapshot = null)
    {
        return ShowInteractiveCore(options, initialScreenSnapshot, null);
    }

    internal static async Task<CaptureOverlayWindow?> BeginInteractive(
        CaptureOverlayOptions options,
        CapturedImage? initialScreenSnapshot = null,
        CaptureOverlayWindow? preparedOverlay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var completion = new TaskCompletionSource<CaptureOverlayWindow?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        NativeWindowPool? nativePool = null;
        PendingCaptureFrame? pendingFrame = null;
        try
        {
            // Start the pixels before the cold WinForms/WPF constructors. The
            // source waits for resident chrome to hide on a background thread;
            // no overlay is shown or activated until this frame is available.
            if (options.DeferInitialScreenCapture && initialScreenSnapshot is null)
                pendingFrame = new(token => CaptureStartupFrameSource.Shared.CaptureAsync(
                    CaptureStartupFrameSource.CaptureDesktop, token));
            if (preparedOverlay is null) nativePool = PrepareNativeBootstrap();
            var dispatcher = System.Windows.Application.Current?.Dispatcher ??
                Dispatcher.CurrentDispatcher;

            _ = CaptureStartupDispatcher.Queue(dispatcher,
                new Action(() =>
                {
                    try
                    {
                        var overlay = ShowInteractiveCore(
                            options,
                            initialScreenSnapshot,
                            nativePool,
                            pendingFrame,
                            preparedOverlay);
                        nativePool = null;
                        pendingFrame = null;
                        completion.TrySetResult(overlay);
                    }
                    catch (Exception exception)
                    {
                        nativePool?.Dispose();
                        nativePool = null;
                        pendingFrame?.Dispose();
                        pendingFrame = null;
                        initialScreenSnapshot?.Dispose();
                        preparedOverlay?.Close();
                        completion.TrySetException(exception);
                    }
                }));
        }
        catch (Exception exception)
        {
            nativePool?.Dispose();
            pendingFrame?.Dispose();
            initialScreenSnapshot?.Dispose();
            preparedOverlay?.Close();
            completion.TrySetException(exception);
        }

        return await completion.Task;
    }

    private static CaptureOverlayWindow ShowInteractiveCore(
        CaptureOverlayOptions options,
        CapturedImage? initialScreenSnapshot,
        NativeWindowPool? preparedNativeWindowPool,
        PendingCaptureFrame? pendingFrame = null,
        CaptureOverlayWindow? preparedOverlay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        CaptureTimingDiagnostics.Mark(
            "show-interactive",
            $"hasSnapshot={initialScreenSnapshot is not null}");
        var overlay = preparedOverlay ?? new CaptureOverlayWindow(
            options, initialScreenSnapshot: initialScreenSnapshot,
            preparedNativeWindowPool: preparedNativeWindowPool);
        if (preparedOverlay is not null)
            overlay.CompletePreparedSession(options, initialScreenSnapshot!);
        var deferKeyboardPickerActivation =
            options.InitialPointerContinuation is null &&
            options.InitialSelection is null;
        // Keyboard shortcuts must not synchronously activate a full-screen
        // transparent WPF window while the key message is being dispatched.
        // Frame publication will activate it after the surface is ready.
        // Mouse-triggered sessions retain the
        // original activation timing.
        if (options.DeferInitialColorPickerActivation ||
            deferKeyboardPickerActivation)
        {
            overlay.ShowActivated = false;
            overlay._initialPickerActivationQueued = deferKeyboardPickerActivation;
            // Keep the compositor from presenting an empty full-screen frame
            // while the first desktop image is captured in the background.
            // When a pre-captured image was handed in, the image is already
            // available and no later SetInitialScreenSnapshotCore call will
            // restore the opacity. Leaving the window at zero opacity would
            // strand the coordinator in the in-progress state and disable all
            // subsequent tray/hotkey actions.
            overlay.Opacity = options.DeferInitialScreenCapture
                ? 0
                : 1;
        }
        _activeInteractiveOverlay = overlay;
        if (options.DeferInitialScreenCapture && initialScreenSnapshot is null)
        {
            _ = overlay.CaptureBeforeShowingAsync(pendingFrame: pendingFrame);
            return overlay;
        }
        CaptureTimingDiagnostics.Mark("before-overlay-show");
        overlay.Show();
        CaptureTimingDiagnostics.Mark("after-overlay-show");
        if (deferKeyboardPickerActivation && initialScreenSnapshot is not null)
        {
            _ = CaptureStartupDispatcher.Queue(overlay.Dispatcher,
                new Action(overlay.PublishInitialScreenFrame));
            CaptureTimingDiagnostics.Mark("initial-screen-frame-queued");
        }
        if (deferKeyboardPickerActivation && initialScreenSnapshot is not null)
        {
            // Loaded runs from inside Show(). Move cursor capture to an input
            // turn so the first full-screen frame can be presented first.
            _ = CaptureStartupDispatcher.Queue(overlay.Dispatcher,
                new Action(overlay.ActivateInitialColorPickerAfterShow));
            CaptureTimingDiagnostics.Mark("initial-color-picker-queued");
        }
        if (options.DeferInitialColorPickerActivation &&
            !options.DeferInitialScreenCapture &&
            initialScreenSnapshot is not null)
        {
            // The pre-captured frame skips SetInitialScreenSnapshotCore, so
            // explicitly complete the non-activating-to-active hand-off on
            // the next dispatcher turn after the overlay is visible.
            _ = overlay.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(overlay.ActivateDeferredColorPicker));
            CaptureTimingDiagnostics.Mark("deferred-color-picker-queued");
        }
        if (options.InitialSelection is { } initialSelection)
        {
            overlay.UpdateLayout();
            _ = overlay.ApplyInitialInteractiveSelectionAsync(initialSelection);
        }

        return overlay;
    }

    internal void SetInitialScreenSnapshot(CapturedImage snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // A completed background capture must not synchronously publish a
        // full-screen BitmapSource from the continuation that won the frame.
        // That update can invalidate the current mouse hit-test and briefly
        // make the cursor appear frozen on slower machines. Always publish
        // from a queued input turn, regardless of the capture worker thread;
        // an idle-only callback can starve under sustained pointer input.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => SetInitialScreenSnapshotCore(snapshot)));
    }

    private void SetInitialScreenSnapshotCore(CapturedImage snapshot)
    {
        if (_isDisposed || _isCompleted)
        {
            snapshot.Dispose();
            return;
        }

        lock (_colorPickerBitmapLifetimeLock)
        {
            _screenSnapshot?.Dispose();
            _screenSnapshot = snapshot;
        }
        FrozenScreenImage.Source = snapshot.Preview;
        if (_showWhenInitialFrameReady)
        {
            _showWhenInitialFrameReady = false;
            // The browser may now dismiss its hover card: its pixels are
            // already in the immutable frame used by selection/OCR/translation.
            try { Show(); }
            catch (Exception exception)
            {
                FailInitialScreenStartup(exception);
                return;
            }
        }
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(PublishInitialScreenVisuals));
        // Picker/snap work starts only after native input readiness, not from
        // a separate queue entry that can run ahead of bitmap presentation.
    }

    private bool _showWhenInitialFrameReady;

    internal Task CaptureBeforeShowingAsync(Func<CancellationToken, CapturedImage>? capture = null,
        PendingCaptureFrame? pendingFrame = null)
    {
        if (_isCompleted || _isDisposed || _initialScreenCaptureStarted)
        {
            pendingFrame?.Dispose();
            return Task.CompletedTask;
        }
        // Even ShowActivated=false/Opacity=0 can change native pointer routing
        // and dismiss browser DOM hover panels. Do not show any capture HWND
        // until the one-shot background copy is complete. The global Escape
        // path can cancel this hidden session; the frame source bounds hangs.
        _showWhenInitialFrameReady = true;
        _activeInteractiveOverlay = this;
        return CaptureInitialScreenAsync(capture, pendingFrame);
    }

    private async Task CaptureInitialScreenAsync(Func<CancellationToken, CapturedImage>? capture = null,
        PendingCaptureFrame? pendingFrame = null)
    {
        using var frameOwner = pendingFrame;
        if (_isCompleted || _isDisposed || _initialScreenCaptureStarted) return;
        _initialScreenCaptureStarted = true;
        var cancellationToken = _lifetimeCancellationSource.Token;
        Core.StartupDiagnostics.LogWindowCreation("CaptureInitialScreenAsync: starting background screen capture");
        try
        {
            var snapshot = pendingFrame is not null
                ? await pendingFrame.TakeAsync(cancellationToken)
                : await CaptureStartupFrameSource.Shared.CaptureAsync(
                    capture ?? CaptureStartupFrameSource.CaptureDesktop, cancellationToken);
            Core.StartupDiagnostics.LogWindowCreation("CaptureInitialScreenAsync: setting initial screen snapshot");
            SetInitialScreenSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing/Esc owns cleanup; a late native frame is disposed by the source.
        }
        catch (Exception ex)
        {
            Core.StartupDiagnostics.LogWindowCreation($"CaptureInitialScreenAsync: exception={ex.Message}");
            FailInitialScreenStartup(ex);
        }
    }

    internal static void PrewarmForStartup(
        CapturedImage? initialScreenSnapshot = null)
    {
        var overlay = new CaptureOverlayWindow(
            options: null,
            initialScreenSnapshot: initialScreenSnapshot,
            isStartupPrewarm: true);
        overlay.Show();
        CaptureTimingDiagnostics.Mark("show-interactive-return");
        // Force the Loaded/layout/source-initialized path while startup is
        // still in progress. Show() alone may defer layout until the next
        // dispatcher turn, allowing the first hotkey to pay that cost.
        overlay.UpdateLayout();
        overlay.Close();
    }

    internal static void PrewarmNativeWindows()
    {
        lock (NativeWindowPoolLock)
        {
            if (_nativeWindowPool is not null)
            {
                return;
            }

            try
            {
                _nativeWindowPool = CreateNativeWindowPool();
                CaptureTimingDiagnostics.Mark("native-window-pool-ready");
            }
            catch
            {
                _nativeWindowPool = null;
            }
        }
    }

    internal static BitmapSource? PrewarmColorPickerSample(
        System.Drawing.Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return BuildColorPickerSample(bitmap, 12, 12).Magnifier;
    }

    private static NativeWindowPool TakeNativeWindowPool()
    {
        lock (NativeWindowPoolLock)
        {
            var pool = _nativeWindowPool;
            _nativeWindowPool = null;
            if (pool is not null)
            {
                CaptureTimingDiagnostics.Mark("native-window-pool-used");
                return pool;
            }
        }

        return CreateNativeWindowPool();
    }

    private static NativeWindowPool CreateNativeWindowPool()
    {
        NativeSelectionFrameWindow? frame = null;
        NativeSelectionMaskWindow? mask = null;
        try
        {
            using (CaptureTimingDiagnostics.Begin("native-frame-create")) frame = new();
            using (CaptureTimingDiagnostics.Begin("native-mask-create")) mask = new();
            using var sizeTiming = CaptureTimingDiagnostics.Begin("native-size-create");
            return new(frame, mask, new NativeSelectionSizeWindow());
        }
        catch
        {
            mask?.Dispose();
            frame?.Dispose();
            throw;
        }
    }

    private static NativeWindowPool PrepareNativeBootstrap()
    {
        var pool = TakeNativeWindowPool();
        // A pooled HWND can still carry a visible state if a previous session
        // was closed during a dispatcher transition. Clear all native
        // surfaces before handing the pool to the next session; the hotkey
        // edge must not flash a stale mask or border.
        pool.Frame.Hide();
        pool.Mask.HideMask();
        pool.Size.HideSize();
        return pool;
    }

    internal sealed class NativeWindowPool
    {
        public NativeWindowPool(
            NativeSelectionFrameWindow frame,
            NativeSelectionMaskWindow mask,
            NativeSelectionSizeWindow size)
        {
            Frame = frame;
            Mask = mask;
            Size = size;
        }

        public NativeSelectionFrameWindow Frame { get; }

        public NativeSelectionMaskWindow Mask { get; }

        public NativeSelectionSizeWindow Size { get; }

        public void Dispose()
        {
            Frame.Dispose();
            Mask.Dispose();
            Size.Dispose();
        }
    }

    internal static bool TryCompleteActiveInteractiveSelection()
    {
        var overlay = _activeInteractiveOverlay;
        if (overlay is null || overlay._isDisposed || overlay._isCaptureBackgrounded)
        {
            return false;
        }

        if (!overlay.Dispatcher.CheckAccess())
        {
            return overlay.Dispatcher.Invoke(
                overlay.TryCompleteFromConfiguredHotKey);
        }

        return overlay.TryCompleteFromConfiguredHotKey();
    }

    internal static bool TryHandleGlobalCompletionKey(
        uint virtualKey,
        HotKeyModifiers modifiers)
    {
        var overlay = _activeInteractiveOverlay;
        if (overlay is null ||
            overlay._isDisposed || overlay._isCaptureBackgrounded ||
            !overlay.MatchesCompletionGesture(
                new HotKeyGesture(modifiers, virtualKey)))
        {
            return false;
        }

        if (!overlay.Dispatcher.CheckAccess())
        {
            return overlay.Dispatcher.Invoke(
                overlay.TryCompleteFromConfiguredHotKey);
        }

        return overlay.TryCompleteFromConfiguredHotKey();
    }

    internal static bool TryHandleGlobalPickerKey(
        uint virtualKey,
        HotKeyModifiers modifiers)
    {
        const uint virtualKeyEscape = 0x1B;
        const uint virtualKeyC = 0x43;
        var overlay = _activeInteractiveOverlay;
        if (overlay is null ||
            overlay._isDisposed || overlay._isCaptureBackgrounded ||
            overlay._isCompleted ||
            (!overlay._isColorPickerActive &&
             (virtualKey != virtualKeyEscape || overlay._isScrollCaptureSelection ||
              overlay._isActionInProgress || overlay._inlineEditorImage is not null)) ||
            (virtualKey != virtualKeyC && virtualKey != virtualKeyEscape))
        {
            return false;
        }

        // Allow cancellation while the desktop frame is still being prepared,
        // before the input surface has acquired foreground focus.
        // Always queue, including when the hook happens to run on the WPF
        // thread. Completing a capture or touching the clipboard must never
        // execute synchronously inside WH_KEYBOARD_LL.
        _ = overlay.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => overlay.HandleGlobalPickerKey(virtualKey)));
        return true;
    }

    private void HandleGlobalPickerKey(uint virtualKey)
    {
        if (_isDisposed || _isCompleted || _isCaptureBackgrounded)
        {
            return;
        }

        if (virtualKey == 0x1B)
        {
            ExitColorPicker();
            CompleteSelection(result: null);
        }
        else if (virtualKey == 0x43 && _isColorPickerActive)
        {
            _ = CopyPickedColorAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_isPreparedForResident) _options = null;
        ReleaseWatermark();
        CaptureTimingDiagnostics.Mark(
            "overlay-closed",
            $"completed={_isCompleted} selecting={_isSelecting} " +
            $"action={_isActionInProgress}");
        if (ReferenceEquals(_activeInteractiveOverlay, this))
        {
            _activeInteractiveOverlay = null;
        }

        if (!_isInitializing)
        {
            _options?.LastAnnotationToolChanged?.Invoke(
                ToAnnotationToolMode(_selectedInlineTool));
        }
        _toolbarDragHint.Detach();
        _lifetimeCancellationSource.Cancel();
        _automaticOcr.Dispose();
        Interlocked.Increment(ref _colorPickerSampleRevision);
        lock (_colorPickerSampleLock)
        {
            _pendingColorPickerBitmap = null;
            _pendingColorPickerResult = null;
        }
        _isSelecting = false;
        _continuedSelectionButton = null;
        StopSelectionPreviewRendering();
        ReleaseOverlayMouseCapture();
        _windowSnapTimer.Stop();
        _windowSnapTimer.Tick -= OnWindowSnapTimerTick;
        _colorPickerFrames?.Dispose();
        _colorPickerFrames = null;
        _selectionIdleTimer.Stop();
        _selectionIdleTimer.Tick -= OnSelectionIdleTimerTick;
        // Hide the native surfaces immediately, but defer their destruction.
        // Dispose stops a polling thread (including a bounded Join) and
        // releases layered bitmaps; doing that inside Close() steals the
        // foreground app's next mouse turn.
        _nativeSelectionFrame.Hide();
        _nativeSelectionMask.HideMask();
        _nativeSelectionSize.HideSize();
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(OnOverlayWindowMessage);
            _windowSource = null;
        }

        InlineEditorCanvas.HistoryChanged -= OnInlineEditorHistoryChanged;
        InlineEditorCanvas.AnnotationSelectionChanged -=
            OnInlineAnnotationSelectionChanged;
        InlineEditorCanvas.PreviewMouseLeftButtonDown -=
            OnInlineEditorCanvasPreviewMouseLeftButtonDown;
        ClearInlineOcrText();
        InlineEditorCanvas.Reset();
        var inlineEditorImage = _inlineEditorImage;
        _inlineEditorImage = null;
        FrozenScreenImage.Source = null;
        CapturedImage? screenSnapshot;
        lock (_colorPickerBitmapLifetimeLock)
        {
            screenSnapshot = _screenSnapshot;
            _screenSnapshot = null;
        }

        if (!_isCompleted)
        {
            _isCompleted = true;
            _selectionCompletionSource.TrySetResult(null);
        }

        _scrollCaptureSelectionCompletionSource?.TrySetResult(null);

        // CaptureClosed is the coordinator's final state-reset signal. Keep
        // native/WPF teardown deterministic even if a stale subscriber throws.
        try
        {
            if (!_isTransitioningCapture) _options?.CaptureClosed?.Invoke();
        }
        catch (Exception)
        {
            // A stale lifecycle subscriber must not abort native/WPF cleanup.
            CaptureTimingDiagnostics.Mark("capture-closed-callback-failed");
        }

        base.OnClosed(e);

        if (System.Windows.Application.Current is { ShutdownMode: ShutdownMode.OnExplicitShutdown } app &&
            ReferenceEquals(app.MainWindow, this)) app.MainWindow = null;

        if (!_closedCleanupScheduled &&
            !Dispatcher.HasShutdownStarted &&
            !Dispatcher.HasShutdownFinished)
        {
            _closedCleanupScheduled = true;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    inlineEditorImage?.Dispose();
                    screenSnapshot?.Dispose();
                    _nativeSelectionFrame.Dispose();
                    _nativeSelectionMask.Dispose();
                    _nativeSelectionSize.Dispose();
                    Dispose();
                }));
        }
        else
        {
            inlineEditorImage?.Dispose();
            screenSnapshot?.Dispose();
            _nativeSelectionFrame.Dispose();
            _nativeSelectionMask.Dispose();
            _nativeSelectionSize.Dispose();
            Dispose();
        }

        // Rebuild the reusable native HWND set only after the interaction has
        // fully closed. This keeps the next keyboard shortcut off the HWND
        // creation path without adding work to the current close operation.
        if (_options is not null && !_isTransitioningCapture &&
            !Dispatcher.HasShutdownStarted &&
            !Dispatcher.HasShutdownFinished)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(PrewarmNativeWindows));
        }
    }

    public void Dispose()
    {
        TranslationTargetPreferences.Changed -= OnTranslationTargetChanged;
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        ReleaseWatermark();
        _colorPickerFrames?.Dispose();
        _colorPickerFrames = null;
        _lifetimeCancellationSource.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        _windowHandle = windowHandle;
        OverlayShellBehavior.Apply(windowHandle);
        _nativeSelectionFrame.SetOwner(windowHandle);
        _nativeSelectionMask.SetOwner(windowHandle);
        _nativeSelectionSize.SetOwner(windowHandle);
        AttachResidentPresentationCover(windowHandle);
        // Normal sessions use the existing native z-order path. Deferred
        // keyboard sessions already keep the WPF Topmost state from Show();
        // repeating SetWindowPos when the bitmap arrives would recalculate
        // hit-testing in the middle of a mouse burst.
        if (!_deferInitialScreenCapture)
        {
            PositionOverlayWindowTopmost();
        }

        SetInitialInputPassthroughWindowStyle(_initialInputPassthrough);

        // The native mask is published from PublishInitialScreenVisuals after
        // the WPF desktop bitmap has reached a render pass. Creating it here
        // races the first compositor frame and can flash a black origin block.
        if (_screenSnapshot is not null && _options?.InitialSelection is null)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(PublishInitialScreenVisuals));
        }

        _windowSource = HwndSource.FromHwnd(windowHandle);
        _windowSource?.AddHook(OnOverlayWindowMessage);
    }

    private void SyncNativeSelectionFrameColor()
    {
        SelectionFrameTheme.Refresh(this);
        SyncNativeSizeBadgeTheme();
    }

    private void OnCaptureSurfaceLoaded(object sender, RoutedEventArgs e)
    {
        using var timing = CaptureTimingDiagnostics.Begin(
            "overlay-loaded",
            $"deferActivation={_deferInitialColorPickerActivation}");
        if (_initialPointerContinuation is not null)
        {
            CaptureInputDiagnostics.Write(
                $"overlay-loaded continuation={_initialPointerContinuation.DiagnosticId} " +
                $"button={_initialPointerContinuation.Button} active={IsActive}");
            ActivateContinuedSelectionWindow("loaded");
        }

        if (!_deferInitialScreenCapture)
        {
            CaptureSurface.Focus();
            Keyboard.Focus(CaptureSurface);
        }
        if (!_deferInitialScreenCapture) _windowSnapTimer.Start();
        BeginContinuedSelectionIfNeeded();
        if (_options is not null &&
            !_isSelecting &&
            !HasValidSelection())
        {
            if (!_initialPickerActivationQueued &&
                _initialPointerContinuation is not null)
            {
                EnterColorPickerCore(
                    activateWindow: !_deferInitialColorPickerActivation);
            }
        }
    }

    private void ActivateInitialColorPickerAfterShow()
    {
        if (_isDisposed || _isCompleted || _isColorPickerActive ||
            (_deferInitialScreenCapture && !_initialScreenInputReady) ||
            _isSelecting || HasValidSelection())
        {
            return;
        }

        CaptureTimingDiagnostics.Mark("initial-color-picker-start");
        // The full-screen surface already receives mouse messages at this
        // point. Capturing the mouse on the same dispatcher turn as window
        // activation causes USER32 to retarget the current cursor message,
        // which is perceived as a one-frame mouse hitch on high polling-rate
        // systems. Selection/drag paths still capture the mouse normally.
        // Keep the panel hidden while its size and cursor-relative position
        // are calculated. A newly shown Canvas child otherwise renders once
        // at (0, 0), which is the brief top-left flash seen on hotkey entry.
        ColorPickerPanel.Opacity = 0;
        EnterColorPickerCore(
            // The overlay is already topmost and receives mouse moves without
            // activation. Activating here changes the foreground window and
            // can retarget the cursor packet that opened the capture.
            activateWindow: false,
            captureMouse: false,
            updatePreview: false,
            updateLayout: false,
            updateWindowSnap: false);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(UpdateInitialColorPickerPreview));
        _initialPickerActivationQueued = false;
        CaptureTimingDiagnostics.Mark("initial-color-picker-end");
    }

    private void UpdateInitialColorPickerPreview()
    {
        if (_isDisposed || _isCompleted || !_isColorPickerActive)
        {
            return;
        }

        CaptureTimingDiagnostics.Mark("initial-color-picker-preview-start");
        var cursor = WinForms.Cursor.Position;
        UpdateColorPicker(CaptureSurface.PointFromScreen(
            new WpfPoint(cursor.X, cursor.Y)));
        UpdateWindowSnap(cursor.X, cursor.Y);
        CaptureTimingDiagnostics.Mark("initial-color-picker-preview-end");
    }

    private void PublishInitialScreenFrame()
    {
        if (_isDisposed || _isCompleted || _screenSnapshot is null)
        {
            return;
        }

        FrozenScreenImage.Source = _screenSnapshot.Preview;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(PublishInitialScreenVisuals));
    }

    private void PublishInitialScreenVisuals()
    {
        if (_isDisposed || _isCompleted || _screenSnapshot is null ||
            _options?.InitialSelection is not null ||
            _initialScreenPublicationPending || _initialScreenInputReady ||
            _isScrollCaptureSelectionLocked || HasValidSelection())
        {
            Core.StartupDiagnostics.LogWindowCreation($"PublishInitialScreenVisuals: skipped - disposed={_isDisposed}, completed={_isCompleted}, hasSnapshot={_screenSnapshot is not null}");
            return;
        }

        Core.StartupDiagnostics.LogWindowCreation($"PublishInitialScreenVisuals: starting - deferInitialCapture={_deferInitialScreenCapture}");
        CaptureTimingDiagnostics.Mark(
            "initial-screen-visuals-before-layout",
            $"opacity={Opacity:0.###} bounds={Width:0.##}x{Height:0.##}");

        _initialScreenPublicationPending = true;
        _ = CompleteInitialScreenHandoffAsync();
    }

    private async Task CompleteInitialScreenHandoffAsync()
    {
        var cancellationToken = _lifetimeCancellationSource.Token;
        try
        {
            if (!PrepareInitialInputSurface()) return;
            if (!_isScrollCaptureSelection && _initialPointerContinuation is null)
            {
                var activation = await CaptureInputActivation.AcquireAsync(_windowHandle,
                    IsInitialInputSurfacePresented, cancellationToken);
                if (activation == CaptureInputActivation.Result.Interrupted)
                {
                    CompleteSelection(result: null);
                    return;
                }
                // Mouse hit testing, rather than foreground ownership alone,
                // determines whether a click can safely start selection. The
                // existing global picker keys keep Esc/C available meanwhile.
                _ = EnsureInteractiveInputOwnership();

                var ready = await CaptureInputReadiness.WaitAsync(
                    IsInitialInputSurfacePresented,
                    () => CaptureInputReadiness.OwnsForeground(_windowHandle),
                    RecoverInitialScreenRendering,
                    cancellationToken, recoverInput: RecoverInitialScreenInput);
                if (!ready)
                {
                    FailInitialScreenStartup(CaptureInputReadiness.BlockedSurfaceFailure(_windowHandle));
                    return;
                }
            }
            if (_isDisposed || _isCompleted || _isScrollCaptureSelectionLocked) return;
            _initialScreenInputReady = true;
            if (!HasValidSelection())
            {
                _nativeSelectionFrame.ShowIdleMask(ToDrawingRectangle(_virtualScreenBounds));
                if (!_isScrollCaptureSelection && _initialPointerContinuation is null)
                    ActivateInitialColorPickerAfterShow();
            }
            _windowSnapTimer.Start();
            CaptureTimingDiagnostics.Mark("initial-screen-frame-published");
            FeatureStartupCompilation.Complete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { FailInitialScreenStartup(exception); }
        finally { _initialScreenPublicationPending = false; }
    }

    private bool IsInitialInputSurfacePresented()
    {
        return CaptureInputReadiness.SurfaceReceivesPointer(_windowHandle, _virtualScreenBounds);
    }

    private void RecoverInitialScreenInput()
    {
        if (_isDisposed || _isCompleted) return;
        CaptureInputActivation.RaiseSurface(_windowHandle);
        _ = EnsureInteractiveInputOwnership();
    }

    private void RecoverInitialScreenRendering()
    {
        // A resumed display driver may leave the layered render target stale.
        // Rebuild only this capture target once; no process-wide GPU switch,
        // bitmap recapture, forced layout loop or permanent software setting.
        if (_windowSource?.CompositionTarget is { } target)
            target.RenderMode = RenderMode.SoftwareOnly;
        FrozenScreenImage.InvalidateVisual();
        InvalidateVisual();
    }

    private void FailInitialScreenStartup(Exception exception)
    {
        if (_isDisposed || _isCompleted) return;
        CaptureStartupFailure.Report(exception);
        CompleteSelection(result: null);
        var reason = exception is TimeoutException
            ? "显示设备采集画面超时。"
            : exception.Message;
        _options?.CaptureStartupFailed?.Invoke("截图未能就绪，已退出，请重试。" + reason);
    }

    private bool PrepareInitialInputSurface()
    {
        if (_isDisposed || _isCompleted || _screenSnapshot is null) return false;
        FrozenScreenImage.Source = _screenSnapshot.Preview;
        _initialInputPassthrough = false;
        SetInitialInputPassthroughWindowStyle(enabled: false);
        Opacity = 1;
        return true;
    }

    private void PositionOverlayWindowTopmost()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        Topmost = true;
        _ = NativeMethods.SetWindowPos(
            _windowHandle,
            new IntPtr(TopmostWindow),
            _virtualScreenBounds.X,
            _virtualScreenBounds.Y,
            _virtualScreenBounds.Width,
            _virtualScreenBounds.Height,
            DoNotActivate | DoNotChangeOwnerZOrder);
    }

    private void ActivateDeferredColorPicker()
    {
        if (!_isColorPickerActive || _isDisposed || _isCompleted)
        {
            return;
        }

        CaptureTimingDiagnostics.Input(
            $"overlay-deferred-activation before active={IsActive} " +
            $"captured={CaptureSurface.IsMouseCaptured} cursor={CaptureSurface.Cursor}");
        // Do not activate or capture the mouse while merely presenting the
        // picker. Full-screen hit testing is sufficient for pointer tracking;
        // ownership is acquired by the first click via
        // EnsureInteractiveInputOwnership.
        UpdateColorPicker(CaptureSurface.PointFromScreen(
            new WpfPoint(
                WinForms.Cursor.Position.X,
                WinForms.Cursor.Position.Y)));
    }

    private void EnsureColorPickerMouseCapture()
    {
        if (_isDisposed || _isCompleted || !_isColorPickerActive ||
            CaptureSurface.IsMouseCaptured)
        {
            return;
        }

        CaptureSurface.CaptureMouse();
        CaptureTimingDiagnostics.Input(
            $"color-picker-capture captured={CaptureSurface.IsMouseCaptured}");
    }

    private void OnCaptureSurfaceKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_isColorPickerActive)
        {
            if (e.Key == Key.Escape)
            {
                ExitColorPicker();
                CompleteSelection(result: null);
                e.Handled = true;
            }
            else if (IsColorCopyKey(e))
            {
                _ = CopyPickedColorAsync();
                e.Handled = true;
            }

            return;
        }

        if (IsConfiguredCompletionShortcut(e) &&
            TryCompleteFromConfiguredHotKey())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C &&
            Keyboard.Modifiers == ModifierKeys.None &&
            !_isSelecting &&
            !HasValidSelection() &&
            !_isScrollCaptureSelection)
        {
            EnterColorPicker();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_isScrollCaptureSelection && _isScrollCaptureSelectionPublished)
            {
                RequestScrollCaptureCancellation();
            }
            else
            {
                CompleteSelection(result: null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.C &&
                 Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                 HasValidSelection() &&
                 !InlineEditorCanvas.IsTextInputActive &&
                 !HasSelectableTextSelection() &&
                 IsDefaultCompletionShortcut())
        {
            // A selectable OCR/translation text box handles Ctrl+C during
            // PreviewKeyDown. Reaching the capture surface means there is no
            // text selection to preserve, so Ctrl+C confirms the image.
            ConfirmCurrentSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && HasValidSelection())
        {
            ConfirmCurrentSelection();
            e.Handled = true;
        }
    }

    private bool TryCompleteFromConfiguredHotKey()
    {
        if (_isCompleted || _isCaptureBackgrounded ||
            _isScrollCaptureSelection ||
            _isColorPickerActive ||
            _isActionInProgress ||
            _isEditorInitializing ||
            InlineEditorCanvas.IsTextInputActive ||
            HasSelectableTextSelection() ||
            CaptureToolbar.Visibility != Visibility.Visible ||
            !HasValidSelection())
        {
            return false;
        }

        ConfirmCurrentSelection();
        return true;
    }

    private bool IsConfiguredCompletionShortcut(WpfKeyEventArgs e)
    {
        if (_completionHotKeyGesture is not { } configuredGesture)
        {
            return false;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var modifiers = HotKeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotKeyModifiers.Control;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotKeyModifiers.Alt;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotKeyModifiers.Shift;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotKeyModifiers.Windows;
        }

        return MatchesCompletionGesture(new HotKeyGesture(modifiers, virtualKey));
    }

    private bool MatchesCompletionGesture(HotKeyGesture gesture)
    {
        return _completionHotKeyGesture is { } configuredGesture &&
               configuredGesture == gesture;
    }

    private bool IsDefaultCompletionShortcut()
    {
        return _options is null ||
            string.IsNullOrWhiteSpace(_options.CompletionHotKey) ||
            string.Equals(
                _options.CompletionHotKey.Trim(),
                AppSettings.DefaultCompleteCaptureHotKey,
                StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateConfirmButtonToolTip()
    {
        var configuredHotKey = _options?.CompletionHotKey?.Trim();
        ConfirmButton.ToolTip = string.IsNullOrWhiteSpace(configuredHotKey) ||
            string.Equals(
                configuredHotKey,
                AppSettings.DefaultCompleteCaptureHotKey,
                StringComparison.OrdinalIgnoreCase)
            ? "完成并复制到剪贴板（Ctrl+C）"
            : $"完成并复制到剪贴板（{configuredHotKey}）";
    }

    private void ConfirmCurrentSelection()
    {
        if (!HasValidSelection())
        {
            return;
        }

        if (_isScrollCaptureSelection)
        {
            PublishScrollCaptureSelection();
        }
        else if (_options is null)
        {
            CompleteSelection(GetPhysicalSelectionBounds());
        }
        else
        {
            OnConfirmClick(ConfirmButton, new RoutedEventArgs());
        }
    }

    private void OnCaptureSurfacePreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_deferInitialScreenCapture && !_initialScreenInputReady)
        {
            e.Handled = true;
            return;
        }
        // The translation button owns its language menu. Leave this event
        // unhandled so WPF can open it on right-button release; the capture
        // gesture must not undo annotations or arm a deferred close first.
        if (e.OriginalSource is DependencyObject source &&
            InputElementTree.IsWithin(source, TranslateButton))
        {
            return;
        }
        EnsureInteractiveInputOwnership();
        if (InlineCustomColorPanel.Visibility == Visibility.Visible &&
            InlineSharedColorPicker.TryHandlePaletteRightClick(e.OriginalSource))
        {
            e.Handled = true;
            return;
        }


        if (_initialPointerContinuation?.Button == CapturePointerButton.Left &&
            !InlineEditorCanvas.HasImage)
        {
            e.Handled = true;
            CompleteOrDeferForRightButtonUp(deferFinalClose: true);
            return;
        }

        if (_isColorPickerActive)
        {
            ExitColorPicker();
        }

        if (_isSelecting &&
            _continuedSelectionButton == CapturePointerButton.Right)
        {
            e.Handled = true;
            return;
        }

        if (_isScrollCaptureSelection && _isScrollCaptureSelectionPublished)
        {
            _cancelScrollCaptureAfterRightButtonUp = true;
            CaptureSurface.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (OcrTextOverlay.Visibility == Visibility.Visible ||
            ContentRecognitionOverlay.Visibility == Visibility.Visible)
        {
            HideUnifiedRecognitionResults();
            CaptureStatusMessage = "已隐藏内容识别结果。";
            CaptureStatusText.Visibility = Visibility.Visible;
            e.Handled = true;
            return;
        }

        e.Handled = true;
        ReturnToPreviousCaptureStateCore(deferFinalClose: true);
    }

    private async void OnCaptureSurfacePreviewMouseRightButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_initialPointerContinuation is not null)
        {
            CaptureInputDiagnostics.Write(
                $"overlay-wpf-up id={_initialPointerContinuation.DiagnosticId} " +
                $"key=right selecting={_isSelecting} " +
                $"continuedButton={_continuedSelectionButton}");
        }

        if (_isSelecting &&
            _continuedSelectionButton == CapturePointerButton.Right)
        {
            e.Handled = true;
            await CompletePointerSelectionAsync(
                e.GetPosition(CaptureSurface));
            return;
        }

        if (_cancelScrollCaptureAfterRightButtonUp)
        {
            _cancelScrollCaptureAfterRightButtonUp = false;
            e.Handled = true;
            if (CaptureSurface.IsMouseCaptured)
            {
                CaptureSurface.ReleaseMouseCapture();
            }

            RequestScrollCaptureCancellation();
            return;
        }

        if (!_completeAfterRightButtonUp)
        {
            return;
        }

        e.Handled = true;
        _completeAfterRightButtonUp = false;
        if (CaptureSurface.IsMouseCaptured)
        {
            CaptureSurface.ReleaseMouseCapture();
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                if (!_isCompleted)
                {
                    CompleteSelection(result: null);
                }
            });
    }

    private void ReturnToPreviousCaptureState()
    {
        ReturnToPreviousCaptureStateCore(deferFinalClose: false);
    }

    private void ReturnToPreviousCaptureStateCore(bool deferFinalClose)
    {
        if (_isCompleted || _isActionInProgress || _isEditorInitializing)
        {
            return;
        }

        if (_isScrollCaptureSelection && _isScrollCaptureSelectionPublished)
        {
            RequestScrollCaptureCancellation();
            return;
        }

        if (InlineEditorCanvas.HasImage)
        {
            if (!InlineEditorCanvas.TryUndoPreviousOperation())
            {
                CompleteOrDeferForRightButtonUp(deferFinalClose);
            }
        }
        else if (_isSelecting || HasValidSelection())
        {
            ClearSelection();
        }
        else
        {
            CompleteOrDeferForRightButtonUp(deferFinalClose);
        }
    }

    private void CompleteOrDeferForRightButtonUp(bool deferFinalClose)
    {
        if (!deferFinalClose)
        {
            CompleteSelection(result: null);
            return;
        }

        _completeAfterRightButtonUp = true;
        CaptureSurface.CaptureMouse();
    }

    private void OnCaptureSurfacePreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        // A selectable OCR surface owns clicks on text, including text that
        // happens to touch the selection edge. Toolbar and resize controls
        // must likewise keep their own commands.
        if (e.OriginalSource is SelectableOcrTextOverlay ||
            e.OriginalSource is DependencyObject source &&
            (InputElementTree.IsWithin(source, CaptureToolbar) ||
             InputElementTree.IsWithin(source, ImageQuestionButton) ||
             InputElementTree.IsWithin(source, ContentRecognitionOverlay) ||
             InputElementTree.IsWithin(source, OcrTextOverlay)))
        {
            return;
        }

        EnsureInteractiveInputOwnership();
        if (e.Handled ||
            e.ClickCount == 2 ||
            _isSelecting ||
            _isMovingSelection ||
            _isResizeDragging ||
            _isColorPickerActive ||
            _isActionInProgress ||
            !HasValidSelection())
        {
            return;
        }

        var point = e.GetPosition(CaptureSurface);
        if (!GetSelectionBounds().Contains(point))
        {
            return;
        }

        var isMoveZone = e.OriginalSource is FrameworkElement moveElement &&
            moveElement.Name.StartsWith("SelectionMove", StringComparison.Ordinal);
        if (!isMoveZone &&
            TryGetResizeHandleAtPoint(point, GetSelectionBounds(), out var resizeHandle))
        {
            BeginNativeResize(resizeHandle);
            e.Handled = true;
            return;
        }

        if (!CanMoveSelectionFromPoint(point))
        {
            return;
        }

        // A completed, unedited capture can move from its entire interior.
        // Choosing an annotation tool hands these gestures to the editor.
        if (InlineEditorCanvas.HasImage)
        {
            OnInlineEditorOutlineMouseLeftButtonDown(InlineEditorOutline, e);
        }
        else
        {
            OnSelectionRectangleMouseLeftButtonDown(SelectionRectangle, e);
        }
    }

    private void OnSelectionMoveZoneMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.Handled || !HasValidSelection() ||
            _isSelecting || _isMovingSelection || _isResizeDragging ||
            _isColorPickerActive || _isActionInProgress ||
            !CanMoveSelection())
        {
            return;
        }

        OnSelectionRectangleMouseLeftButtonDown(SelectionRectangle, e);
    }

    private bool TryGetResizeHandleAtPoint(
        WpfPoint point,
        Rect bounds,
        out Thumb handle)
    {
        handle = null!;
        const double hit = 16;

        bool Near(double value, double target) =>
            Math.Abs(value - target) <= hit;

        if (Near(point.X, bounds.Left) && Near(point.Y, bounds.Top))
        {
            handle = TopLeftResizeThumb;
        }
        else if (Near(point.X, bounds.Right) && Near(point.Y, bounds.Top))
        {
            handle = TopRightResizeThumb;
        }
        else if (Near(point.X, bounds.Left) && Near(point.Y, bounds.Bottom))
        {
            handle = BottomLeftResizeThumb;
        }
        else if (Near(point.X, bounds.Right) && Near(point.Y, bounds.Bottom))
        {
            handle = BottomRightResizeThumb;
        }
        else if (Near(point.Y, bounds.Top) &&
                 point.X >= bounds.Left + hit &&
                 point.X <= bounds.Right - hit)
        {
            handle = TopResizeThumb;
        }
        else if (Near(point.X, bounds.Left) &&
                 point.Y >= bounds.Top + hit &&
                 point.Y <= bounds.Bottom - hit)
        {
            handle = LeftResizeThumb;
        }
        else if (Near(point.X, bounds.Right) &&
                 point.Y >= bounds.Top + hit &&
                 point.Y <= bounds.Bottom - hit)
        {
            handle = RightResizeThumb;
        }
        else if (Near(point.Y, bounds.Bottom) &&
                 point.X >= bounds.Left + hit &&
                 point.X <= bounds.Right - hit)
        {
            handle = BottomResizeThumb;
        }

        return handle is not null;
    }

    private void OnCaptureSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_deferInitialScreenCapture && !_initialScreenInputReady)
        {
            e.Handled = true;
            return;
        }
        EnsureInteractiveInputOwnership();
        if (InlineCustomColorPanel.Visibility == Visibility.Visible &&
            !IsSourceInsideInlineCustomColorPanel(e.OriginalSource))
        {
            HideInlineCustomColorPanel();
        }

        if (_isColorPickerActive)
        {
            var point = e.GetPosition(CaptureSurface);
            if (IsInsideColorPickerPanel(point))
            {
                e.Handled = true;
                return;
            }

            ExitColorPicker();
        }

        if (!CanStartNewSelectionFromBackground(e.OriginalSource))
        {
            return;
        }

        BeginPointerSelection(
            e.GetPosition(CaptureSurface),
            continuedButton: null,
            allowWindowSnap: true);
        e.Handled = true;
    }

    private void BeginContinuedSelectionIfNeeded()
    {
        if (_initialPointerContinuation is null)
        {
            return;
        }

        CaptureInputDiagnostics.Write(
            $"overlay-continued-begin id={_initialPointerContinuation.DiagnosticId} " +
            $"button={_initialPointerContinuation.Button}");

        var cursorPosition = _initialPointerContinuation.StartScreenPoint ??
            WinForms.Cursor.Position;
        var startPoint = CaptureSurface.PointFromScreen(
            new WpfPoint(cursorPosition.X, cursorPosition.Y));
        BeginPointerSelection(
            startPoint,
            _initialPointerContinuation.Button,
            allowWindowSnap: false);
        var currentCursorPosition = WinForms.Cursor.Position;
        var currentPoint = CaptureSurface.PointFromScreen(
            new WpfPoint(currentCursorPosition.X, currentCursorPosition.Y));
        UpdateSelectionBounds(new Rect(_selectionStartPoint, currentPoint));
        _continuedSelectionReleaseTask = CompleteSelectionWhenTriggerButtonIsReleasedAsync(
            _initialPointerContinuation);
        CaptureInputDiagnostics.Write(
            $"overlay-continued-waiting id={_initialPointerContinuation.DiagnosticId} " +
            $"captured={CaptureSurface.IsMouseCaptured} " +
            $"selecting={_isSelecting} start={_selectionStartPoint.X:0.##},{_selectionStartPoint.Y:0.##}");
    }

    internal Task ContinuedSelectionReleaseTask =>
        _continuedSelectionReleaseTask;

    private async Task CompleteSelectionWhenTriggerButtonIsReleasedAsync(
        CapturePointerContinuation continuation)
    {
        await continuation.WaitForReleaseAsync();
        CaptureInputDiagnostics.Write(
            $"overlay-continued-released id={continuation.DiagnosticId} " +
            $"selecting={_isSelecting} button={_continuedSelectionButton}");
        if (_isCompleted)
        {
            CaptureInputDiagnostics.Write(
                $"overlay-continued-stop id={continuation.DiagnosticId} reason=completed");
            return;
        }

        var completion = await Dispatcher.InvokeAsync(() =>
        {
            if (!_isSelecting ||
                _continuedSelectionButton != continuation.Button)
            {
                CaptureInputDiagnostics.Write(
                    $"overlay-continued-stop id={continuation.DiagnosticId} " +
                    $"reason=state-mismatch selecting={_isSelecting} button={_continuedSelectionButton}");
                return Task.CompletedTask;
            }

            // Some applications retain foreground activation while the physical
            // button is held. Re-activate after the real release so the first
            // editor click is not consumed merely to activate this overlay.
            ActivateContinuedSelectionWindow("physical-release");

            var cursorPosition = WinForms.Cursor.Position;
            var endPoint = CaptureSurface.PointFromScreen(
                new WpfPoint(cursorPosition.X, cursorPosition.Y));
            return CompletePointerSelectionCoreAsync(
                endPoint,
                enterPickerWhenEmpty:
                    continuation.EnterPickerWhenReleasedWithoutSelection);
        });
        await completion;
        CaptureInputDiagnostics.Write(
            $"overlay-continued-completed id={continuation.DiagnosticId} " +
            $"editorImage={InlineEditorCanvas.HasImage} completed={_isCompleted} active={IsActive}");
    }

    private void ActivateContinuedSelectionWindow(string reason)
    {
        var activeBefore = IsActive;
        var topmostApplied = ReassertOverlayTopmost();
        var activated = activeBefore || Activate();
        CaptureSurface.Focus();
        Keyboard.Focus(CaptureSurface);
        CaptureInputDiagnostics.Write(
            $"overlay-activate reason={reason} before={activeBefore} " +
            $"topmost={topmostApplied} activateResult={activated} after={IsActive} " +
            $"keyboardFocus={CaptureSurface.IsKeyboardFocusWithin}");
    }

    private bool ReassertOverlayTopmost()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            _windowHandle,
            new IntPtr(TopmostWindow),
            0,
            0,
            0,
            0,
            DoNotResize |
            DoNotMove |
            DoNotChangeOwnerZOrder);
    }

    private bool EnsureEditorInputOwnership()
    {
        if (_isCaptureBackgrounded) return false;
        // Raising the opaque owner above its native mask on every mouse-up
        // briefly exposes the undimmed desktop. A focused capture already owns
        // input; only repair z-order when ownership was actually lost.
        if (CaptureInputReadiness.OwnsForeground(_windowHandle)) return true;
        var applied = ReassertOverlayTopmost();
        _ = Activate();
        return applied;
    }

    private void BeginPointerSelection(
        WpfPoint startPoint,
        CapturePointerButton? continuedButton,
        bool allowWindowSnap)
    {
        _selectionStartPoint = new WpfPoint(
            Math.Clamp(startPoint.X, 0, CaptureSurface.ActualWidth),
            Math.Clamp(startPoint.Y, 0, CaptureSurface.ActualHeight));
        _continuedSelectionButton = continuedButton;
        _isSelecting = true;
        ResetSelectionPreviewRate();
        _windowSnapTimer.Stop();
        _isWindowSnapClickPending =
            allowWindowSnap &&
            WindowSnapRectangle.Visibility == Visibility.Visible &&
            _windowSnapBounds.HasValue;
        if (!_isWindowSnapClickPending)
        {
            HideWindowSnap();
            UpdateSelectionBounds(new Rect(
                _selectionStartPoint,
                _selectionStartPoint));
            // The WPF rectangle is input-only. Keep it transparent before it
            // becomes visible so the accent-muted fill cannot flash while the
            // native gray mask is taking ownership of the pixels.
            SelectionRectangle.Fill = WpfBrushes.Transparent;
            SyncNativeSelectionFrameColor();
            if (!_nativeSelectionFrame.IsMaskEnabled)
            {
                _nativeSelectionFrame.SetMaskEnabled(true);
            }
            _nativeSelectionFrame.SetSizeEnabled(ShouldShowSelectionSize);
            _nativeSelectionFrame.StartSelectionTracking(
                ToPhysicalScreenPoint(_selectionStartPoint),
                ToDrawingRectangle(_virtualScreenBounds),
                () => Dispatcher.BeginInvoke(new Action(() =>
                    _ = CompleteNativePointerSelectionAsync())),
                captureMouse: !continuedButton.HasValue,
                aspectRatio: !_isScrollCaptureSelection
                    ? CaptureAspectRatioHelper.GetValue(
                        _sessionAspectRatio)
                    : null);
            SetNativeSelectionVisualSuppressed(true);
            SelectionRectangle.Visibility = Visibility.Visible;
        }

        if (_isWindowSnapClickPending || continuedButton.HasValue)
        {
            // Window-snap needs WPF capture only until the drag crosses the
            // threshold. A mouse-shortcut continuation also keeps WPF capture
            // so the global hook can deliver the trigger-button release.
            CaptureSurface.CaptureMouse();
        }
        CaptureSurface.Focus();
        // Subscribe before the first MouseMove so a busy WPF input queue does
        // not delay the first native-position sample.
        QueueSelectionBoundsUpdate(new Rect(
            _selectionStartPoint,
            _selectionStartPoint));
        Mouse.UpdateCursor();
        if (continuedButton.HasValue)
        {
            CaptureInputDiagnostics.Write(
                $"overlay-pointer-selection id={_initialPointerContinuation?.DiagnosticId} " +
                $"button={continuedButton} captured={CaptureSurface.IsMouseCaptured} " +
                $"start={_selectionStartPoint.X:0.##},{_selectionStartPoint.Y:0.##}");
        }
    }

    private void OnCaptureSurfacePreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_isColorPickerActive || _isSelecting || _isMovingSelection ||
            _isResizeDragging || _isActionInProgress ||
            _isScrollCaptureSelectionLocked || !HasValidSelection())
        {
            return;
        }

        UpdateSelectionInteractionCursor(e.GetPosition(CaptureSurface));
    }

    private void OnCaptureSurfaceMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_nativeSelectionFrame.IsTracking && !_isWindowSnapClickPending && !_isColorPickerActive)
        {
            e.Handled = true;
            return;
        }
        var point = e.GetPosition(CaptureSurface);
#if DEBUG
        var screenPoint = CaptureSurface.PointToScreen(point);
        CaptureTimingDiagnostics.MouseMove(
            "WPF",
            (int)Math.Round(screenPoint.X),
            (int)Math.Round(screenPoint.Y),
            state: $"picker={_isColorPickerActive} selecting={_isSelecting} " +
                   $"active={IsActive} captured={CaptureSurface.IsMouseCaptured}");
#endif
        if (_isColorPickerActive)
        {
            StartColorPickerTracking();
            return;
        }

        if (_isSelecting)
        {
            var currentPoint = point;
            if (_isWindowSnapClickPending)
            {
                var delta = currentPoint - _selectionStartPoint;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }

                _isWindowSnapClickPending = false;
                HideWindowSnap();
                SyncNativeSelectionFrameColor();
                if (!_nativeSelectionFrame.IsMaskEnabled)
                {
                    _nativeSelectionFrame.SetMaskEnabled(true);
                }
                _nativeSelectionFrame.SetSizeEnabled(ShouldShowSelectionSize);
                _nativeSelectionFrame.StartSelectionTracking(
                    ToPhysicalScreenPoint(_selectionStartPoint),
                    ToDrawingRectangle(_virtualScreenBounds),
                    () => Dispatcher.BeginInvoke(new Action(() =>
                        _ = CompleteNativePointerSelectionAsync())),
                    captureMouse: true,
                    aspectRatio: !_isScrollCaptureSelection
                        ? CaptureAspectRatioHelper.GetValue(
                            _sessionAspectRatio)
                        : null);
                SetNativeSelectionVisualSuppressed(true);
                SelectionRectangle.Fill = WpfBrushes.Transparent;
                CaptureSurface.ReleaseMouseCapture();
                QueueSelectionBoundsUpdate(new Rect(
                    _selectionStartPoint,
                    _selectionStartPoint));
                SelectionRectangle.Visibility = Visibility.Visible;
            }

            if (!_isScrollCaptureSelection &&
                CaptureAspectRatioHelper.GetValue(
                    _sessionAspectRatio) is { } ratio)
            {
                var constrained = CaptureAspectRatioHelper.ConstrainFromAnchor(
                    ToPhysicalScreenPoint(_selectionStartPoint),
                    ToPhysicalScreenPoint(currentPoint),
                    ToDrawingRectangle(_virtualScreenBounds),
                    ratio);
                if (TryGetCaptureSurfacePoint(
                        new DrawingPoint(constrained.Left, constrained.Top),
                        out var constrainedTopLeft) &&
                    TryGetCaptureSurfacePoint(
                        new DrawingPoint(constrained.Right, constrained.Bottom),
                        out var constrainedBottomRight))
                {
                    QueueSelectionBoundsUpdate(
                        new Rect(constrainedTopLeft, constrainedBottomRight));
                }
            }
            else
            {
                QueueSelectionBoundsUpdate(new Rect(_selectionStartPoint, currentPoint));
            }
            return;
        }

        // Manual long-screenshot capture uses a native input hole and a
        // low-level wheel hook. Do not feed every high-report-rate mouse move
        // through WPF just to recompute a cursor that cannot affect capture.
        if (_isScrollCaptureSelectionLocked)
        {
            return;
        }

        // The visible frame is native and click-through. Keep the WPF surface
        // responsible for the interaction cursor even when the transparent
        // selection layer is hidden by the editor canvas.
        if (!_isMovingSelection && !_isResizeDragging &&
            !_isActionInProgress && HasValidSelection())
        {
            UpdateSelectionInteractionCursor(e.GetPosition(CaptureSurface));
        }

    }

    private void UpdateSelectionInteractionCursor(WpfPoint point)
    {
        var oldCursor = CaptureSurface.Cursor;
        if (TryGetResizeHandleAtPoint(point, GetSelectionBounds(), out var handle))
        {
            CaptureSurface.Cursor = handle.Cursor ??
                System.Windows.Input.Cursors.SizeAll;
            LogCursorChange(oldCursor, CaptureSurface.Cursor, point);
            return;
        }

        var bounds = GetSelectionBounds();
        if (!bounds.Contains(point))
        {
            CaptureSurface.Cursor = System.Windows.Input.Cursors.Cross;
            LogCursorChange(oldCursor, CaptureSurface.Cursor, point);
            return;
        }

        var canMove = CanMoveSelectionFromPoint(point);
        CaptureSurface.Cursor = canMove
            ? System.Windows.Input.Cursors.SizeAll
            : System.Windows.Input.Cursors.Arrow;
        LogCursorChange(oldCursor, CaptureSurface.Cursor, point);
    }

    private void LogCursorChange(
        System.Windows.Input.Cursor oldCursor,
        System.Windows.Input.Cursor newCursor,
        WpfPoint point)
    {
        if (!ReferenceEquals(oldCursor, newCursor))
        {
            CaptureTimingDiagnostics.Input(
                $"cursor-change old={oldCursor} new={newCursor} " +
                $"point={point.X:0.##},{point.Y:0.##} " +
                $"active={IsActive} captured={CaptureSurface.IsMouseCaptured}");
        }
    }

    private async void OnCaptureSurfaceMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_initialPointerContinuation is not null)
        {
            CaptureInputDiagnostics.Write(
                $"overlay-wpf-up id={_initialPointerContinuation.DiagnosticId} " +
                $"key=left selecting={_isSelecting} " +
                $"continuedButton={_continuedSelectionButton}");
        }

        if (!_isSelecting ||
            _continuedSelectionButton == CapturePointerButton.Right)
        {
            return;
        }

        // A mouse-triggered capture replays the native button-up after the
        // overlay takes ownership. The synthetic WPF event arrives before
        // the physical release continuation and would complete an empty
        // selection through the normal path, skipping the color picker.
        if (_initialPointerContinuation?.Button == CapturePointerButton.Left &&
            _continuedSelectionButton == CapturePointerButton.Left)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        await CompletePointerSelectionAsync(e.GetPosition(CaptureSurface));
    }

    private Task CompletePointerSelectionAsync(WpfPoint endPoint) =>
        CompletePointerSelectionCoreAsync(
            endPoint,
            enterPickerWhenEmpty: false);

    private async Task CompleteNativePointerSelectionAsync()
    {
        if (!_isSelecting)
        {
            return;
        }

        var cursorPosition = WinForms.Cursor.Position;
        var endPoint = CaptureSurface.PointFromScreen(
            new WpfPoint(cursorPosition.X, cursorPosition.Y));
        await CompletePointerSelectionCoreAsync(
            endPoint,
            enterPickerWhenEmpty: false);
    }

    private async Task CompletePointerSelectionCoreAsync(
        WpfPoint endPoint,
        bool enterPickerWhenEmpty)
    {
        CaptureTimingDiagnostics.Mark("editor-selection-mouse-up");
        var snappedBounds = _isWindowSnapClickPending
            ? _windowSnapBounds
            : null;
        FlushSelectionBoundsUpdate();
        _isWindowSnapClickPending = false;
        _isSelecting = false;
        _nativeSelectionFrame.StopTracking(hide: false);
        SetNativeSelectionVisualSuppressed(true);
        _continuedSelectionButton = null;
        CaptureSurface.ReleaseMouseCapture();
        if (snappedBounds.HasValue)
        {
            HideWindowSnap();
            UpdateSelectionBounds(snappedBounds.Value);
            SelectionRectangle.Visibility = Visibility.Visible;
        }
        else
        {
            var requested = new Rect(_selectionStartPoint, endPoint);
            if (!_isScrollCaptureSelection &&
                CaptureAspectRatioHelper.GetValue(
                    _sessionAspectRatio) is { } ratio)
            {
                var constrained = CaptureAspectRatioHelper.ConstrainFromAnchor(
                    ToPhysicalScreenPoint(_selectionStartPoint),
                    ToPhysicalScreenPoint(endPoint),
                    ToDrawingRectangle(_virtualScreenBounds),
                    ratio);
                if (TryGetCaptureSurfacePoint(
                        new DrawingPoint(constrained.Left, constrained.Top),
                        out var topLeft) &&
                    TryGetCaptureSurfacePoint(
                        new DrawingPoint(constrained.Right, constrained.Bottom),
                        out var bottomRight))
                {
                    requested = new Rect(topLeft, bottomRight);
                }
            }
            UpdateSelectionBounds(requested);
        }

        if (!HasValidSelection())
        {
            if (_initialPointerContinuation is not null)
            {
                CaptureInputDiagnostics.Write(
                    $"overlay-selection-invalid id={_initialPointerContinuation.DiagnosticId} " +
                    $"end={endPoint.X:0.##},{endPoint.Y:0.##}");
            }
            HideSelectionControls();
            _nativeSelectionFrame.SetMaskEnabled(false);
            if (enterPickerWhenEmpty && _options is not null)
            {
                EnterColorPicker();
            }

            return;
        }

        SyncNativeSelectionFrameColor();
        _nativeSelectionFrame.SetSizeEnabled(ShouldShowSelectionSize);
        _nativeSelectionFrame.Update(ToDrawingRectangle(
            GetPhysicalSelectionBounds()));
        _nativeSelectionFrame.RefreshMask();

        if (_isScrollCaptureSelection)
        {
            PrepareScrollCaptureSelection();
            return;
        }

        if (_options is null)
        {
            CompleteSelection(GetPhysicalSelectionBounds());
            return;
        }

        ShowSelectionControls();
        if (_initialPointerContinuation is not null)
        {
            CaptureInputDiagnostics.Write(
                $"overlay-selection-valid id={_initialPointerContinuation.DiagnosticId} " +
                $"bounds={GetSelectionBounds()}");
        }
        await EnterInlineEditorForCompletedSelectionAsync();
    }

    private async Task EnterInlineEditorForCompletedSelectionAsync(bool deferRecognition = false)
    {
        await EnterInlineEditorAsync();
        if (!deferRecognition) ScheduleSelectionRecognition();
    }

    private void ScheduleSelectionRecognition()
    {
        var generation = _automaticRecognitionGeneration;
        // Render/input work wins over recognition setup and pixel copies. In
        // resident handoffs this is queued only after the cover is released.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_isCompleted || _isDisposed || generation != _automaticRecognitionGeneration) return;
            _ = RecognizeCompletedSelectionAsync();
        }));
    }

    private async Task RecognizeCompletedSelectionAsync()
    {
        if (!InlineEditorCanvas.HasImage || _isCompleted)
        {
            return;
        }

        if (_options?.TranslateTextAfterSelection == true &&
            _options.RecognizeTextAsync is not null &&
            _options.TranslateTextAsync is not null &&
            InlineEditorCanvas.HasImage &&
            !_isCompleted)
        {
            await TranslateInlineTextAsync();
            return;
        }

        _ = RecognizeLocalInlineContentAsync(
            _automaticRecognitionGeneration,
            delayMilliseconds: 0);
        ScheduleAutomaticOcr();
    }

    private void OnWindowSnapTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _isSelecting ||
            (_deferInitialScreenCapture && !_initialScreenInputReady))
        {
            return;
        }

        var cursorPosition = WinForms.Cursor.Position;
        // The desktop is frozen for this session. Re-enumerating every HWND
        // at a stationary pointer adds driver/layout work with no new result.
        if (_lastWindowSnapProbeCursor == cursorPosition) return;
        UpdateWindowSnap(cursorPosition.X, cursorPosition.Y);
        _lastWindowSnapProbeCursor = cursorPosition;
    }

    private void UpdateWindowSnap(int screenX, int screenY)
    {
        _lastWindowSnapProbeCursor = new DrawingPoint(screenX, screenY);
        if (_windowHandle == IntPtr.Zero ||
            _isActionInProgress ||
            _isEditorInitializing ||
            InlineEditorCanvas.HasImage ||
            SelectionRectangle.Visibility == Visibility.Visible ||
            _isScrollCaptureSelectionPublished)
        {
            HideWindowSnap();
            return;
        }
        if (!WindowSnapService.TryGetWindowRegionAt(
                screenX,
                screenY,
                _windowHandle,
                _virtualScreenBounds,
                out var physicalBounds))
        {
            HideWindowSnap();
            return;
        }

        var topLeft = CaptureSurface.PointFromScreen(
            new WpfPoint(physicalBounds.X, physicalBounds.Y));
        var bottomRight = CaptureSurface.PointFromScreen(new WpfPoint(
            physicalBounds.X + physicalBounds.Width,
            physicalBounds.Y + physicalBounds.Height));
        var bounds = ClampBoundsToSurface(new Rect(topLeft, bottomRight));
        if (bounds.Width < 2 || bounds.Height < 2)
        {
            HideWindowSnap();
            return;
        }

        _windowSnapBounds = bounds;
        Canvas.SetLeft(WindowSnapRectangle, bounds.X);
        Canvas.SetTop(WindowSnapRectangle, bounds.Y);
        WindowSnapRectangle.Width = bounds.Width;
        WindowSnapRectangle.Height = bounds.Height;
        WindowSnapRectangle.Visibility = Visibility.Visible;
    }

    private void HideWindowSnap()
    {
        _lastWindowSnapProbeCursor = null;
        _windowSnapBounds = null;
        WindowSnapRectangle.Visibility = Visibility.Collapsed;
    }

    private void OnSelectionRectangleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasValidSelection())
        {
            return;
        }

        // Double-clicking inside the selection confirms the capture, matching the
        // WeChat and PixPin workflow where the user does not have to reach for the
        // toolbar checkmark.
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            ConfirmCurrentSelection();
            return;
        }

        if (!CanMoveSelection()) return;

        _isMovingSelection = true;
        HideToolbarForNativeMove();
        SetResizeControlsSuppressed(true);
        ResetSelectionPreviewRate();
        _dragStartPoint = TryGetCaptureSurfacePoint(
            WinForms.Cursor.Position,
            out var nativeStartPoint)
            ? nativeStartPoint
            : e.GetPosition(CaptureSurface);
        _dragStartBounds = GetSelectionBounds();
        SyncNativeSelectionFrameColor();
        if (!_nativeSelectionFrame.IsMaskEnabled)
        {
            _nativeSelectionFrame.SetMaskEnabled(true);
        }
        _nativeSelectionFrame.RefreshMask();
        _nativeSelectionFrame.StartMoveTracking(
            WinForms.Cursor.Position,
            ToDrawingRectangle(GetPhysicalSelectionBounds()),
            ToDrawingRectangle(_virtualScreenBounds),
            () => Dispatcher.BeginInvoke(new Action(() =>
                _ = CompleteNativeMoveAsync())));
        SetNativeSelectionVisualSuppressed(true);
        SelectionRectangle.Visibility = Visibility.Hidden;
        QueueSelectionBoundsUpdate(_dragStartBounds);
        e.Handled = true;
    }

    private void OnInlineEditorCanvasPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 ||
            _isColorPickerActive ||
            _isActionInProgress ||
            !HasValidSelection())
        {
            return;
        }

        e.Handled = true;
        ConfirmCurrentSelection();
    }

    private void EnterColorPicker() =>
        EnterColorPickerCore();

    private void EnterColorPickerCore(
        bool activateWindow = true,
        bool captureMouse = true,
        bool updatePreview = true,
        bool updateLayout = true,
        bool updateWindowSnap = true)
    {
        using var timing = CaptureTimingDiagnostics.Begin(
            "enter-color-picker",
            $"activate={activateWindow}");
        if (_screenSnapshot is null || _isCompleted || _isActionInProgress)
        {
            return;
        }

        _isColorPickerActive = true;
        _lastColorPickerUpdateTimestamp = 0;
        _lastColorPickerCursor = null;
        _colorPickerSamplePending = false;
        _windowSnapTimer.Start();
        CaptureTimingDiagnostics.Input(
            $"enter-picker before cursor={CaptureSurface.Cursor} active={IsActive} " +
            $"captured={CaptureSurface.IsMouseCaptured} activate={activateWindow}");
        CaptureSurface.Cursor = System.Windows.Input.Cursors.Cross;
        // Position the panel before making it visible. Canvas children render
        // at their default origin for one frame when Visibility changes first,
        // which causes the magnifier to flash in the top-left corner.
        ColorPickerPanel.Opacity = 0;
        ColorPickerPanel.Visibility = Visibility.Visible;
        if (updateLayout)
        {
            ColorPickerPanel.UpdateLayout();
        }
        if (activateWindow)
        {
            if (captureMouse)
            {
                CaptureSurface.CaptureMouse();
                CaptureTimingDiagnostics.Input(
                    $"enter-picker capture-mouse captured={CaptureSurface.IsMouseCaptured}");
            }
            Activate();
            CaptureTimingDiagnostics.Input(
                $"enter-picker activate active={IsActive}");
            CaptureSurface.Focus();
        }
        if (updatePreview)
        {
            UpdateColorPicker(CaptureSurface.PointFromScreen(
                new WpfPoint(
                    WinForms.Cursor.Position.X,
                    WinForms.Cursor.Position.Y)));
        }
        if (updateWindowSnap)
        {
            UpdateWindowSnap(
                WinForms.Cursor.Position.X,
                WinForms.Cursor.Position.Y);
        }
        // The panel remains hidden until the first magnifier bitmap is ready.
        StartColorPickerTracking();
    }

    private void OnCaptureWindowTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_isColorPickerActive &&
            e.Text.Any(character => character is 'c' or 'C'))
        {
            _ = CopyPickedColorAsync();
            e.Handled = true;
        }
    }

    private void OnCaptureWindowPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (!_isColorPickerActive && TrySelectToolbarFeatureShortcut(e))
        {
            e.Handled = true;
            return;
        }

        if (_isColorPickerActive && e.Key == Key.Escape)
        {
            ExitColorPicker();
            CompleteSelection(result: null);
            e.Handled = true;
            return;
        }

        if (_isColorPickerActive && IsColorCopyKey(e))
        {
            _ = CopyPickedColorAsync();
            e.Handled = true;
            return;
        }

        if (!_isColorPickerActive &&
            IsConfiguredCompletionShortcut(e) &&
            TryCompleteFromConfiguredHotKey())
        {
            e.Handled = true;
        }
    }

    private void ExitColorPicker()
    {
        if (!_isColorPickerActive)
        {
            return;
        }

        _isColorPickerActive = false;
        Interlocked.Increment(ref _colorPickerSampleRevision);
        lock (_colorPickerSampleLock)
        {
            _pendingColorPickerBitmap = null;
            _pendingColorPickerResult = null;
        }
        _colorPickerFrames?.Stop();
        _lastColorPickerCursor = null;
        _colorPickerSamplePending = false;
        ColorPickerPanel.Visibility = Visibility.Collapsed;
        ColorPickerPanel.Opacity = 1;
        ColorPickerMagnifier.Source = null;
        CaptureSurface.Cursor = System.Windows.Input.Cursors.Cross;
        if (CaptureSurface.IsMouseCaptured)
        {
            CaptureSurface.ReleaseMouseCapture();
        }

        if (!_isCompleted)
        {
            _windowSnapTimer.Start();
            CaptureSurface.Focus();
        }
    }

    private void StartColorPickerTracking()
    {
        if (!IsVisible || !_isColorPickerActive || _isDisposed || _isCompleted) return;
        _colorPickerFrames ??= new PointerFramePump(UpdateColorPickerFromPhysicalCursor, nativeFrames: true);
        _colorPickerFrames.Start();
    }

    private void UpdateColorPickerFromPhysicalCursor()
    {
        if (!IsVisible || !_isColorPickerActive || _isDisposed || _isCompleted) return;
        PresentColorPickerCursor(WinForms.Cursor.Position);
    }

    internal bool PresentColorPickerCursor(DrawingPoint cursor)
    {
        if (!_isColorPickerActive || _isDisposed || _isCompleted ||
            (_lastColorPickerCursor == cursor && !_colorPickerSamplePending)) return false;
        // Native hit testing/menu ownership can withhold WPF MouseMove after
        // Alt is released. Follow the actual cursor, independent of modifiers
        // or mouse capture, with at most one update per display frame.
        UpdateColorPicker(CaptureSurface.PointFromScreen(new WpfPoint(cursor.X, cursor.Y)));
        _lastColorPickerCursor = cursor;
        return true;
    }

    private async Task CopyPickedColorAsync()
    {
        var colorText = $"#{_selectedPixelColor.R:X2}{_selectedPixelColor.G:X2}{_selectedPixelColor.B:X2}";
        try
        {
            await ClipboardTextService.SetTextAsync(colorText);
            await ShowSelectionMessageForAsync($"已复制 {colorText}", 650);
            CompleteSelection(result: null);
        }
        catch
        {
            await ShowSelectionMessageForAsync("复制颜色失败，请重试", 1500);
        }
    }

    private void UpdateColorPicker(WpfPoint point)
    {
        if (!_isColorPickerActive || _screenSnapshot is null)
        {
            return;
        }

        _colorPickerSamplePending = false;
        try
        {
            var screenPoint = CaptureSurface.PointToScreen(point);
            var pixelX = Math.Clamp(
                (int)Math.Round(screenPoint.X) - _virtualScreenBounds.X,
                0,
                Math.Max(0, _screenSnapshot.PixelWidth - 1));
            var pixelY = Math.Clamp(
                (int)Math.Round(screenPoint.Y) - _virtualScreenBounds.Y,
                0,
                Math.Max(0, _screenSnapshot.PixelHeight - 1));
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var minInterval = System.Diagnostics.Stopwatch.Frequency /
                _interactionRefreshRate;
            if (now - _lastColorPickerUpdateTimestamp >= minInterval)
            {
                _lastColorPickerUpdateTimestamp = now;
                QueueColorPickerSample(_screenSnapshot.Bitmap, pixelX, pixelY);
            }
            else
            {
                // A final movement may fall between sampling frames. Retry
                // its bitmap once the interval allows, even if the cursor stops.
                _colorPickerSamplePending = true;
            }
        }
        catch (OutOfMemoryException)
        {
            // A transient preview failure must never escape through the WPF
            // input loop and strand the capture state. Keep the last frame so
            // the pointer path remains responsive and the next sample retries.
        }
        catch (ArgumentException)
        {
            // Screen captures are normally 32bpp; unsupported imported pixel
            // formats fall back to the direct color sample above.
        }

        var surfaceWidth = Math.Max(0, CaptureSurface.ActualWidth);
        var surfaceHeight = Math.Max(0, CaptureSurface.ActualHeight);
        var panelWidth = Math.Max(
            ColorPickerPanel.ActualWidth,
            ColorPickerPanel.DesiredSize.Width);
        var panelHeight = Math.Max(
            ColorPickerPanel.ActualHeight,
            ColorPickerPanel.DesiredSize.Height);
        const double cursorGap = 20;
        const double edgeMargin = 8;

        // Prefer the lower-right of the cursor, but move to the upper-left
        // when that side would overlap the cursor or leave the screen.
        var rightX = point.X + cursorGap;
        var leftX = point.X - panelWidth - cursorGap;
        var panelX = rightX + panelWidth <= surfaceWidth - edgeMargin
            ? rightX
            : leftX;
        var belowY = point.Y + cursorGap;
        var aboveY = point.Y - panelHeight - cursorGap;
        var panelY = belowY + panelHeight <= surfaceHeight - edgeMargin
            ? belowY
            : aboveY;

        panelX = Math.Clamp(
            panelX,
            edgeMargin,
            Math.Max(edgeMargin, surfaceWidth - panelWidth - edgeMargin));
        panelY = Math.Clamp(
            panelY,
            edgeMargin,
            Math.Max(edgeMargin, surfaceHeight - panelHeight - edgeMargin));
        _colorPickerPanelTransform.X = panelX;
        _colorPickerPanelTransform.Y = panelY;
    }

    private void QueueColorPickerSample(
        System.Drawing.Bitmap bitmap,
        int pixelX,
        int pixelY)
    {
        lock (_colorPickerSampleLock)
        {
            _pendingColorPickerBitmap = bitmap;
            _pendingColorPickerPixelX = pixelX;
            _pendingColorPickerPixelY = pixelY;
        }

        if (Interlocked.Exchange(ref _colorPickerSampleWorkerActive, 1) == 0)
        {
            _ = Task.Run(ProcessColorPickerSamples);
        }
    }

    private bool TrySelectToolbarFeatureShortcut(WpfKeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox ||
            InlineEditorCanvas.IsTextInputActive)
            return false;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (!CaptureToolbarShortcut.TryFind(_toolbarFeatureShortcuts, key, Keyboard.Modifiers, out var feature))
            return false;

        var annotationTool = feature switch
        {
            CaptureToolbarFeature.Shape => (EditorTool?)(_currentShapeToolMode == ShapeToolMode.Ellipse
                ? EditorTool.Ellipse
                : EditorTool.Rectangle),
            CaptureToolbarFeature.Arrow => _currentArrowToolMode == ArrowToolMode.Curved
                ? EditorTool.CurvedArrow
                : EditorTool.Arrow,
            CaptureToolbarFeature.Emoji => EditorTool.Emoji,
            CaptureToolbarFeature.Number => EditorTool.Number,
            CaptureToolbarFeature.Brush => EditorTool.Brush,
            CaptureToolbarFeature.Text => EditorTool.Text,
            CaptureToolbarFeature.Mosaic => AnnotationToolPreferences.GetVariant(EditorTool.Mosaic),
            CaptureToolbarFeature.DoubleArrow => AnnotationToolPreferences.GetVariant(EditorTool.DoubleArrow),
            CaptureToolbarFeature.Line => AnnotationToolPreferences.GetVariant(EditorTool.Line),
            _ => null,
        };
        if (annotationTool is { } tool &&
            GetInlineToolButton(tool).Visibility == Visibility.Visible)
        {
            SelectInlineTool(tool);
            return true;
        }

        var actionButton = feature switch
        {
            CaptureToolbarFeature.VideoRecording => RecordButton,
            CaptureToolbarFeature.Save => SaveButton,
            CaptureToolbarFeature.ScrollCapture => ScrollCaptureButton,
            CaptureToolbarFeature.TextRecognition => OcrButton,
            CaptureToolbarFeature.CopyTable => CopyTableButton,
            CaptureToolbarFeature.CopyRecognizedText => CopyRecognizedTextButton,
            CaptureToolbarFeature.Translation => TranslateButton,
            CaptureToolbarFeature.PrivacyRedaction => PrivacyRedactionButton,
            CaptureToolbarFeature.PinImage => PinButton,
            CaptureToolbarFeature.Watermark => WatermarkButton,
            CaptureToolbarFeature.AspectRatio => AspectRatioButton,
            _ => null,
        };
        if (actionButton?.Visibility != Visibility.Visible || !actionButton.IsEnabled)
        {
            return false;
        }
        if (feature == CaptureToolbarFeature.Translation)
        {
            ClearInlineToolSelection();
            _ = TranslateInlineTextAsync();
            return true;
        }
        actionButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        return true;
    }

    private void SelectInlineTool(EditorTool tool)
    {
        var button = GetInlineToolButton(tool);
        button.Tag = tool.ToString();
        if (tool is EditorTool.DoubleArrow or EditorTool.HollowDoubleArrow or EditorTool.Line or EditorTool.WavyLine or EditorTool.Mosaic or EditorTool.RectangleMosaic)
            AnnotationVariantButton.Update(button, tool);
        button.IsChecked = true;
        OnInlineEditorToolSelected(button, new RoutedEventArgs());
    }

    private void ProcessColorPickerSamples()
    {
        try
        {
            while (true)
            {
                System.Drawing.Bitmap? bitmap;
                int pixelX;
                int pixelY;
                long revision;
                lock (_colorPickerSampleLock)
                {
                    bitmap = _pendingColorPickerBitmap;
                    pixelX = _pendingColorPickerPixelX;
                    pixelY = _pendingColorPickerPixelY;
                    _pendingColorPickerBitmap = null;
                    revision = Volatile.Read(ref _colorPickerSampleRevision);
                }

                if (bitmap is null)
                {
                    return;
                }

                ColorPickerSample sample;
                try
                {
                    using var timing = CaptureTimingDiagnostics.Begin(
                        "color-picker-sample-worker");
                    lock (_colorPickerBitmapLifetimeLock)
                    {
                        sample = BuildColorPickerSample(bitmap, pixelX, pixelY);
                    }
                }
                catch (ArgumentException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    continue;
                }
                catch (ExternalException)
                {
                    continue;
                }
                catch (Exception exception)
                {
                    CaptureTimingDiagnostics.Exception(
                        "color-picker-sample-worker",
                        exception);
                    continue;
                }

                if (revision != Volatile.Read(ref _colorPickerSampleRevision))
                {
                    continue;
                }

                lock (_colorPickerSampleLock)
                {
                    _pendingColorPickerResult = sample;
                    _pendingColorPickerResultRevision = revision;
                }
                ScheduleColorPickerResultDispatch();
            }
        }
        finally
        {
            Volatile.Write(ref _colorPickerSampleWorkerActive, 0);
            lock (_colorPickerSampleLock)
            {
                if (_pendingColorPickerBitmap is not null &&
                    Interlocked.Exchange(ref _colorPickerSampleWorkerActive, 1) == 0)
                {
                    _ = Task.Run(ProcessColorPickerSamples);
                }
            }
        }
    }

    private void ApplyColorPickerSample(long revision, ColorPickerSample sample)
    {
        if (!_isColorPickerActive || _isDisposed || _isCompleted ||
            revision != Volatile.Read(ref _colorPickerSampleRevision))
        {
            return;
        }

        _selectedPixelColor = sample.Color;
        ColorPickerValueText.Text = $"#{sample.Color.R:X2}{sample.Color.G:X2}{sample.Color.B:X2}";
        ColorPickerRgbText.Text = $"RGB({sample.Color.R}, {sample.Color.G}, {sample.Color.B})";
        ColorPickerMagnifier.Source = sample.Magnifier;
        ColorPickerPanel.Opacity = 1;
    }

    private void ScheduleColorPickerResultDispatch()
    {
        if (Interlocked.Exchange(ref _colorPickerResultDispatchScheduled, 1) != 0)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(ApplyPendingColorPickerResult));
    }

    private void ApplyPendingColorPickerResult()
    {
        ColorPickerSample? sample;
        long revision;
        lock (_colorPickerSampleLock)
        {
            sample = _pendingColorPickerResult;
            revision = _pendingColorPickerResultRevision;
            _pendingColorPickerResult = null;
            // Keep the dispatch flag set while a producer has already placed a
            // newer result in the slot; otherwise a result can be stranded
            // between the check and the flag reset.
            if (_pendingColorPickerResult is null)
            {
                Volatile.Write(ref _colorPickerResultDispatchScheduled, 0);
            }
        }

        if (sample is { } ready)
        {
            ApplyColorPickerSample(revision, ready);
        }

        lock (_colorPickerSampleLock)
        {
            if (_pendingColorPickerResult is not null &&
                Interlocked.Exchange(ref _colorPickerResultDispatchScheduled, 1) == 0)
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(ApplyPendingColorPickerResult));
            }
        }
    }

    private static ColorPickerSample BuildColorPickerSample(
        System.Drawing.Bitmap bitmap,
        int pixelX,
        int pixelY)
    {
        const int bytesPerPixel = 4;
        var sourceLeft = Math.Clamp(
            pixelX - ColorPickerSampleRadius,
            0,
            Math.Max(0, bitmap.Width - ColorPickerSampleSize));
        var sourceTop = Math.Clamp(
            pixelY - ColorPickerSampleRadius,
            0,
            Math.Max(0, bitmap.Height - ColorPickerSampleSize));
        var sourceRectangle = new System.Drawing.Rectangle(
            sourceLeft,
            sourceTop,
            ColorPickerSampleSize,
            ColorPickerSampleSize);
        var pixelFormat = bitmap.PixelFormat == PixelFormat.Format32bppArgb
            ? PixelFormat.Format32bppArgb
            : PixelFormat.Format32bppPArgb;
        var data = bitmap.LockBits(sourceRectangle, ImageLockMode.ReadOnly, pixelFormat);
        try
        {
            var sourceStride = Math.Abs(data.Stride);
            var sampleStride = ColorPickerSampleSize * bytesPerPixel;
            var sampleBuffer = new byte[sampleStride * ColorPickerSampleSize];
            for (var row = 0; row < ColorPickerSampleSize; row++)
            {
                var sourceRow = data.Stride >= 0 ? row : ColorPickerSampleSize - row - 1;
                Marshal.Copy(
                    IntPtr.Add(data.Scan0, sourceRow * sourceStride),
                    sampleBuffer,
                    row * sampleStride,
                    sampleStride);
            }

            var centerOffset = ColorPickerSampleRadius * sampleStride +
                ColorPickerSampleRadius * bytesPerPixel;
            var selected = DrawingColor.FromArgb(
                sampleBuffer[centerOffset + 3],
                sampleBuffer[centerOffset + 2],
                sampleBuffer[centerOffset + 1],
                sampleBuffer[centerOffset]);

            var magnifierStride = ColorPickerMagnifierSize * bytesPerPixel;
            var magnifierBuffer = new byte[magnifierStride * ColorPickerMagnifierSize];
            for (var y = 0; y < ColorPickerMagnifierSize; y++)
            {
                var sampleY = y * ColorPickerSampleSize / ColorPickerMagnifierSize;
                for (var x = 0; x < ColorPickerMagnifierSize; x++)
                {
                    var sampleX = x * ColorPickerSampleSize / ColorPickerMagnifierSize;
                    var sourceOffset = sampleY * sampleStride + sampleX * bytesPerPixel;
                    var destinationOffset = y * magnifierStride + x * bytesPerPixel;
                    Buffer.BlockCopy(sampleBuffer, sourceOffset, magnifierBuffer, destinationOffset, bytesPerPixel);
                }
            }

            DrawColorPickerCrosshair(magnifierBuffer, magnifierStride);
            var magnifier = BitmapSource.Create(
                ColorPickerMagnifierSize,
                ColorPickerMagnifierSize,
                96,
                96,
                System.Windows.Media.PixelFormats.Pbgra32,
                null,
                magnifierBuffer,
                magnifierStride);
            magnifier.Freeze();
            return new ColorPickerSample(selected, magnifier);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void DrawColorPickerCrosshair(byte[] buffer, int stride)
    {
        static void SetPixel(byte[] buffer, int offset, byte b, byte g, byte r)
        {
            buffer[offset] = b;
            buffer[offset + 1] = g;
            buffer[offset + 2] = r;
            buffer[offset + 3] = 255;
        }

        for (var i = 46; i <= 58; i++)
        {
            SetPixel(buffer, i * stride + 52 * 4, 255, 255, 255);
            SetPixel(buffer, 52 * stride + i * 4, 255, 255, 255);
        }

        for (var x = 50; x <= 54; x++)
        {
            SetPixel(buffer, 50 * stride + x * 4, 0, 0, 0);
            SetPixel(buffer, 54 * stride + x * 4, 0, 0, 0);
        }
        for (var y = 50; y <= 54; y++)
        {
            SetPixel(buffer, y * stride + 50 * 4, 0, 0, 0);
            SetPixel(buffer, y * stride + 54 * 4, 0, 0, 0);
        }
    }

    private static bool IsColorCopyKey(WpfKeyEventArgs e)
    {
        return e.Key == Key.C ||
               (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.C) ||
               (e.Key == Key.System && e.SystemKey == Key.C);
    }

    private bool IsInsideColorPickerPanel(WpfPoint point)
    {
        if (ColorPickerPanel.Visibility != Visibility.Visible)
        {
            return false;
        }

        var left = _colorPickerPanelTransform.X;
        var top = _colorPickerPanelTransform.Y;

        return new Rect(left, top, ColorPickerPanel.ActualWidth, ColorPickerPanel.ActualHeight)
            .Contains(point);
    }

    private void OnSelectionRectangleMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isMovingSelection)
        {
            return;
        }

        var currentPoint = e.GetPosition(CaptureSurface);
        var delta = currentPoint - _dragStartPoint;
        var bounds = ClampMovedBoundsToSurface(new Rect(
            _dragStartBounds.X + delta.X,
            _dragStartBounds.Y + delta.Y,
            _dragStartBounds.Width,
            _dragStartBounds.Height));
        QueueSelectionBoundsUpdate(bounds);
    }

    private void SetNativeSelectionVisualSuppressed(bool suppressed)
    {
        // These WPF elements are input-only hosts. The visible selection,
        // mask, and size badge are always owned by the native windows; letting
        // the WPF rectangle recover opacity would create a second frame.
        SelectionRectangle.Opacity = 0;
        SelectionRectangle.Stroke = WpfBrushes.Transparent;
        SelectionRectangle.Fill = WpfBrushes.Transparent;
        // Keep the editor outline as an invisible hit-test surface. A null
        // fill only hits its stroke, so clicks inside the editor image would
        // bypass the native move path and leave the controls visible.
        InlineEditorOutline.Fill = WpfBrushes.Transparent;
        if (InlineEditorOutline.Visibility == Visibility.Visible)
        {
            InlineEditorOutline.Opacity = 0;
        }
    }

    private async void OnCaptureSurfacePreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        // The native tracker owns the physical release. WPF can receive a
        // synthetic preview-up when native capture is changed, which must not
        // restore stale controls or commit an intermediate rectangle.
        if (_isResizeDragging && !_nativeSelectionFrame.IsTracking)
        {
            e.Handled = true;
            await CompleteNativeResizeAsync();
            return;
        }

        if (_isResizeDragging)
        {
            e.Handled = true;
            return;
        }

        if (!_isMovingSelection)
        {
            return;
        }

        e.Handled = true;
        await CompleteNativeMoveAsync();
    }

    private async void OnSelectionRectangleMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMovingSelection)
        {
            return;
        }

        e.Handled = true;
        await CompleteNativeMoveAsync();
    }

    private void OnInlineEditorOutlineMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && HasValidSelection())
        {
            e.Handled = true;
            ConfirmCurrentSelection();
            return;
        }

        if (!CanMoveSelection())
        {
            e.Handled = true;
            return;
        }

        _isMovingSelection = true;
        HideToolbarForNativeMove();
        SetResizeControlsSuppressed(true);
        ResetSelectionPreviewRate();
        BeginSelectionAdjustment();
        _dragStartPoint = TryGetCaptureSurfacePoint(
            WinForms.Cursor.Position,
            out var nativeStartPoint)
            ? nativeStartPoint
            : e.GetPosition(CaptureSurface);
        _dragStartBounds = GetSelectionBounds();
        SyncNativeSelectionFrameColor();
        if (!_nativeSelectionFrame.IsMaskEnabled)
        {
            _nativeSelectionFrame.SetMaskEnabled(true);
        }
        _nativeSelectionFrame.RefreshMask();
        _nativeSelectionFrame.StartMoveTracking(
            WinForms.Cursor.Position,
            ToDrawingRectangle(GetPhysicalSelectionBounds()),
            ToDrawingRectangle(_virtualScreenBounds),
            () => Dispatcher.BeginInvoke(new Action(() =>
                _ = CompleteNativeMoveAsync())));
        SetNativeSelectionVisualSuppressed(true);
        InlineEditorOutline.Visibility = Visibility.Hidden;
        QueueSelectionBoundsUpdate(_dragStartBounds);
        e.Handled = true;
    }

    private void OnInlineEditorOutlineMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isMovingSelection || !InlineEditorOutline.IsMouseCaptured)
        {
            return;
        }

        var currentPoint = e.GetPosition(CaptureSurface);
        var delta = currentPoint - _dragStartPoint;
        var bounds = ClampBoundsToSurface(new Rect(
            _dragStartBounds.X + delta.X,
            _dragStartBounds.Y + delta.Y,
            _dragStartBounds.Width,
            _dragStartBounds.Height));
        QueueSelectionBoundsUpdate(bounds);
        e.Handled = true;
    }

    private async void OnInlineEditorOutlineMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isMovingSelection || !InlineEditorOutline.IsMouseCaptured)
        {
            return;
        }

        e.Handled = true;
        await CompleteNativeMoveAsync();
    }

    private async Task CompleteNativeMoveAsync()
    {
        if (!_isMovingSelection)
        {
            return;
        }

        var wasSelectionAdjustment = _isSelectionAdjustmentInProgress;
        _isMovingSelection = false;
        _nativeSelectionFrame.StopTracking(hide: false);
        SetNativeSelectionVisualSuppressed(true);
        // The native tracker owns the clamped rectangle for the entire drag.
        // Prefer it on release: the WPF cursor sample can be outside the
        // surface and ClampBoundsToSurface would clip the width/height.
        if (TryGetNativeFinalSelectionBounds(out var finalBounds, preserveMoveSize: true) ||
            TryGetMovePreviewBounds(out finalBounds))
        {
            // Read the release-time native cursor first. The polling loop may
            // be one sample behind when a high-report-rate mouse is released.
            // Its last bounds remain a fallback if the cursor cannot be
            // converted to the capture surface.
            UpdateSelectionBounds(finalBounds);
        }
        else
        {
            FlushSelectionBoundsUpdate();
        }
        if (SelectionRectangle.IsMouseCaptured)
        {
            SelectionRectangle.ReleaseMouseCapture();
        }
        if (InlineEditorOutline.IsMouseCaptured)
        {
            InlineEditorOutline.ReleaseMouseCapture();
        }
        if (wasSelectionAdjustment)
        {
            await CompleteSelectionAdjustmentAsync();
        }
        if (_lastRenderedSelectionBounds is { } committedBounds)
        {
            UpdateSelectionBounds(committedBounds);
            UpdateNativeSelectionVisuals(committedBounds);
        }
        _nativeSelectionFrame.RefreshMask();
        if (InlineEditorCanvas.HasImage)
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            InlineEditorOutline.Visibility = Visibility.Visible;
        }
        else
        {
            SelectionRectangle.Visibility = Visibility.Visible;
        }
        SetResizeControlsSuppressed(false);
        RestoreToolbarAfterNativeMove(_lastRenderedSelectionBounds);
    }

    private void OnTopLeftResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelectionFromDrag(e.HorizontalChange, e.VerticalChange, 0, 0);
    }

    private void OnTopRightResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelectionFromDrag(0, e.VerticalChange, e.HorizontalChange, 0);
    }

    private void OnBottomLeftResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelectionFromDrag(e.HorizontalChange, 0, 0, e.VerticalChange);
    }

    private void OnBottomRightResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelectionFromDrag(0, 0, e.HorizontalChange, e.VerticalChange);
    }

    private void OnTopResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelectionFromDrag(0, e.VerticalChange, 0, 0);
    }

    private void OnLeftResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelectionFromDrag(e.HorizontalChange, 0, 0, 0);
    }

    private void OnRightResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelectionFromDrag(0, 0, e.HorizontalChange, 0);
    }

    private void OnBottomResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelectionFromDrag(0, 0, 0, e.VerticalChange);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_options is null)
        {
            return;
        }

        if (!BeginAction())
        {
            return;
        }

        var completed = false;
        try
        {
            var completedRegion = GetPhysicalSelectionBounds();
            using var capturedImage = await CaptureCurrentResultAsync(restoreOverlay: false);
            using var image = capturedImage.CreateScaled(
                _options.ScreenshotScalePercent);
            // The folder picker is a separate native window.  Close the
            // full-screen capture overlay before opening it; otherwise the
            // overlay's owned/topmost windows can cover the picker.
            if (_options.PngSaveLocationMode == PngSaveLocationMode.AskEveryTime)
            {
                CompleteSelection(result: null);
            }
            var saveDirectory = await PngSaveLocationService.ResolveAsync(
                _options.PngSaveLocationMode,
                _options.SaveDirectory);
            if (saveDirectory is null)
            {
                return;
            }

            var savedPath = CaptureFileService.SaveAsPng(
                image,
                saveDirectory);
            CaptureHistoryItem? historyItem = null;
            if (_options.KeepHistory)
            {
                historyItem = _options.HistoryService.Add(
                    image,
                    _options.HistoryLimit);
                historyItem?.MarkSaved(savedPath);
            }

            try
            {
                await ClipboardImageService.SetImageAsync(image.Preview);
                historyItem?.MarkCopied();
            }
            catch
            {
                // Saving and history insertion already succeeded. A busy
                // clipboard must not discard those successful operations.
            }

            _options.CaptureCompleted?.Invoke(completedRegion);
            completed = true;
            CompleteSelection(result: null);
            _ = CaptureSuccessWindow.ShowAsync(completedRegion);
        }
        catch
        {
        }
        finally
        {
            if (completed)
            {
                CompleteSelection(result: null);
            }
            else
            {
                _isActionInProgress = false;
                CaptureToolbar.IsEnabled = true;
            }
        }
    }

    private async void OnScrollCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_options?.StartScrollCaptureAsync is null ||
            _isCompleted ||
            _isActionInProgress ||
            !HasValidSelection())
        {
            return;
        }

        var selection = GetPhysicalSelectionBounds();
        _isActionInProgress = true;
        CaptureToolbar.IsEnabled = false;
        var startScrollCaptureAsync = _options.StartScrollCaptureAsync;
        var startScrollCaptureWithSnapshotAsync =
            _options.StartScrollCaptureWithSnapshotAsync;
        CapturedImage? initialScreenSnapshot = null;
        if (startScrollCaptureWithSnapshotAsync is not null &&
            _screenSnapshot is not null)
        {
            initialScreenSnapshot = await Task.Run(() =>
            {
                lock (_colorPickerBitmapLifetimeLock)
                    return _screenSnapshot?.Clone();
            });
            if (_isCompleted)
            {
                initialScreenSnapshot?.Dispose();
                return;
            }
        }
        _isTransitioningCapture = true;
        try
        {
            if (startScrollCaptureWithSnapshotAsync is not null)
            {
                await startScrollCaptureWithSnapshotAsync(
                    selection,
                    initialScreenSnapshot);
                initialScreenSnapshot = null;
            }
            else
            {
                await startScrollCaptureAsync!(selection);
            }
        }
        catch
        {
            // The coordinator reports scroll-capture failures in the settings window.
        }
        finally
        {
            initialScreenSnapshot?.Dispose();
            CompleteSelection(result: null);
        }
    }

    private async void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_options?.StartVideoRecordingAsync is null ||
            _isCompleted ||
            _isActionInProgress ||
            !HasValidSelection())
        {
            return;
        }

        var selection = GetPhysicalSelectionBounds();
        _isActionInProgress = true;
        CaptureToolbar.IsEnabled = false;
        var startVideoRecordingAsync = _options.StartVideoRecordingAsync;
        _isTransitioningCapture = true;
        try
        {
            await startVideoRecordingAsync(selection);
        }
        catch
        {
            // The coordinator surfaces recording failures through app status.
        }
        finally
        {
            CompleteSelection(result: null);
        }
    }

    private async Task EnterInlineEditorAsync()
    {
        if (_options is null ||
            _isCompleted ||
            _isActionInProgress ||
            _isEditorInitializing ||
            InlineEditorCanvas.HasImage ||
            !HasValidSelection())
        {
            return;
        }

        _isEditorInitializing = true;
        using var editorTiming = CaptureTimingDiagnostics.Begin("editor-initialize");
        if (_initialPointerContinuation is not null)
        {
            CaptureInputDiagnostics.Write(
                $"overlay-editor-begin id={_initialPointerContinuation.DiagnosticId}");
        }
        ClearInlineOcrText();
        CaptureToolbar.IsEnabled = false;
        CapturedImage? image = null;

        try
        {
            image = await CaptureCurrentSelectionAsync(restoreOverlay: true);
            CaptureTimingDiagnostics.Mark("editor-selection-image-ready");

            if (_isCompleted)
            {
                return;
            }

            var selectionBounds = GetSelectionBounds();
            _inlineEditorImage = image;
            image = null;
            InlineEditorCanvas.Initialize(
                _inlineEditorImage,
                selectionBounds.Width,
                selectionBounds.Height);
            CaptureTimingDiagnostics.Mark("editor-canvas-ready");
            ClearInlineToolSelection();
            Canvas.SetLeft(InlineEditorCanvas, selectionBounds.X);
            Canvas.SetTop(InlineEditorCanvas, selectionBounds.Y);
            InlineEditorCanvas.Visibility = Visibility.Visible;
            LockSelectionForEditing();
            var topmostApplied = EnsureEditorInputOwnership();
            // ReassertOverlayTopmost can place the WPF owner above the native
            // selection windows. Submit the complete native visual state again
            // after activation so the border and size window remain visible
            // after the first selection enters the editor.
            if (HasValidSelection())
            {
                UpdateNativeSelectionVisuals(GetSelectionBounds());
            }
            InlineEditorCanvas.Focus();
            Keyboard.Focus(InlineEditorCanvas);
            InitializeWatermark();
            CaptureTimingDiagnostics.Mark("editor-toolbar-ready");
            if (_initialPointerContinuation is not null)
            {
                CaptureInputDiagnostics.Write(
                    $"overlay-editor-ready id={_initialPointerContinuation.DiagnosticId} " +
                    $"topmost={topmostApplied} active={IsActive} " +
                    $"focused={InlineEditorCanvas.IsKeyboardFocusWithin}");
            }
        }
        catch (Exception exception)
        {
            if (_initialPointerContinuation is not null)
            {
                CaptureInputDiagnostics.Write(
                    $"overlay-editor-failed id={_initialPointerContinuation.DiagnosticId} " +
                    $"error={exception.GetType().Name}:{exception.Message}");
            }
        }
        finally
        {
            image?.Dispose();
            _isEditorInitializing = false;

            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = true;
            }
        }
    }

    private async Task RefreshInlineEditorForSelectionAsync(
        ScreenRegion previousSelection)
    {
        if (_options is null ||
            _isCompleted ||
            _isEditorInitializing ||
            !InlineEditorCanvas.HasImage ||
            !HasValidSelection())
        {
            return;
        }

        _isEditorInitializing = true;
        CaptureToolbar.IsEnabled = false;
        CapturedImage? image = null;

        try
        {
            image = await CaptureCurrentSelectionAsync(restoreOverlay: true);
            if (_isCompleted)
            {
                return;
            }

            var previousImage = _inlineEditorImage;

            var selectionBounds = GetSelectionBounds();
            var currentSelection = GetPhysicalSelectionBounds();
            var replacementImage = image;
            InlineEditorCanvas.Reframe(
                replacementImage,
                selectionBounds.Width,
                selectionBounds.Height,
                new Vector(
                    previousSelection.X - currentSelection.X,
                    previousSelection.Y - currentSelection.Y));
            _inlineEditorImage = replacementImage;
            image = null;
            previousImage?.Dispose();
            InlineEditorCanvas.SelectTool(_selectedInlineTool);
            ApplyInlineToolStyleState();
            Canvas.SetLeft(InlineEditorCanvas, selectionBounds.X);
            Canvas.SetTop(InlineEditorCanvas, selectionBounds.Y);
            InlineEditorCanvas.Visibility = Visibility.Visible;
            InlineEditorCanvas.Focus();
            CaptureStatusText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            CaptureStatusMessage = "无法刷新当前选区，请重新框选。";
            CaptureStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            image?.Dispose();
            _isEditorInitializing = false;

            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = true;
            }
        }
    }

    private bool CanMoveSelection()
    {
        // An explicit tool choice must reserve the first drag for drawing,
        // even before the first annotation has been added.
        return HasValidSelection() &&
            (!InlineEditorCanvas.HasImage ||
                (!InlineEditorCanvas.IsAnnotationCreationEnabled && !InlineEditorCanvas.CanUndo));
    }

    internal bool CanMoveSelectionFromPoint(WpfPoint point) =>
        GetSelectionBounds().Contains(point) && CanMoveSelection();

    private void BeginSelectionAdjustment()
    {
        if (_isSelectionAdjustmentInProgress || !InlineEditorCanvas.HasImage)
        {
            return;
        }

        _isSelectionAdjustmentInProgress = true;
        InlineEditorCanvas.SetWatermarkSuppressed(true);
        _selectionAdjustmentStartPhysicalBounds = GetPhysicalSelectionBounds();
        _selectionAdjustmentProtectedBounds = GetProtectedAnnotationBounds();
        _selectionAdjustmentProtectedPhysicalBounds =
            _selectionAdjustmentProtectedBounds is { } protectedBounds
                ? ToDrawingRectangle(GetPhysicalSelectionBoundsForBounds(
                    protectedBounds))
                : null;
        ClearInlineOcrText();

        // Keep the edited image visible while the bounds are being dragged.
        // The protected annotation bounds below prevent the new selection from
        // covering or clipping existing edits.
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        ContentRecognitionOverlay.Visibility = Visibility.Collapsed;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        InlineEditorOutline.Visibility = Visibility.Collapsed;
    }

    private Rect? GetProtectedAnnotationBounds()
    {
        var annotationBounds = InlineEditorCanvas.GetAnnotationBounds();
        if (!annotationBounds.HasValue || annotationBounds.Value.IsEmpty)
        {
            return null;
        }

        var selection = GetSelectionBounds();
        // Annotation coordinates are stored in the editor's pixel space. Use
        // the configured pixel dimensions first, with ActualWidth/Height as a
        // fallback during the short interval before the first layout pass.
        var editorWidth = InlineEditorCanvas.Width;
        var editorHeight = InlineEditorCanvas.Height;
        if (!double.IsFinite(editorWidth) || editorWidth <= 0)
        {
            editorWidth = InlineEditorCanvas.ActualWidth;
        }

        if (!double.IsFinite(editorHeight) || editorHeight <= 0)
        {
            editorHeight = InlineEditorCanvas.ActualHeight;
        }

        if (editorWidth <= 0 || editorHeight <= 0)
        {
            return null;
        }

        var scaleX = selection.Width / editorWidth;
        var scaleY = selection.Height / editorHeight;
        var bounds = Rect.Intersect(
            annotationBounds.Value,
            new Rect(0, 0, editorWidth, editorHeight));
        if (bounds.IsEmpty)
        {
            return null;
        }

        return new Rect(
            selection.X + (bounds.X * scaleX),
            selection.Y + (bounds.Y * scaleY),
            bounds.Width * scaleX,
            bounds.Height * scaleY);
    }

    private async Task CompleteSelectionAdjustmentAsync()
    {
        if (!_isSelectionAdjustmentInProgress)
        {
            return;
        }

        var previousSelection = _selectionAdjustmentStartPhysicalBounds;
        _isSelectionAdjustmentInProgress = false;
        _selectionAdjustmentStartPhysicalBounds = null;
        _selectionAdjustmentProtectedBounds = null;
        _selectionAdjustmentProtectedPhysicalBounds = null;
        if (previousSelection.HasValue)
        {
            await RefreshInlineEditorForSelectionAsync(previousSelection.Value);
            if (InlineEditorCanvas.HasImage && !_isCompleted)
            {
                // Restore the hit-test outline only after the reframe finishes;
                // a stale WPF border during the drag causes visible flashing.
                InlineEditorOutline.Visibility = Visibility.Visible;
                _ = RecognizeLocalInlineContentAsync(
                    _automaticRecognitionGeneration,
                    delayMilliseconds: 350);
                ScheduleAutomaticOcr();
            }
        }
        InlineEditorCanvas.SetWatermarkSuppressed(_isMovingSelection || _isResizeDragging);
    }

    private async void OnSelectionResizeThumbDragCompleted(
        object sender,
        DragCompletedEventArgs e)
    {
        // Taking capture away from the WPF Thumb raises DragCompleted even
        // though the physical button is still held. The native tracker owns
        // the real gesture; its release callback is the only completion point
        // while tracking is active.
        if (_nativeSelectionFrame.IsTracking)
        {
            return;
        }

        // WPF can raise DragCompleted when capture changes even though the
        // physical button is still down. Completing here would restore the
        // old handles and toolbar in the middle of the native resize.
        if ((WinForms.Control.MouseButtons & WinForms.MouseButtons.Left) != 0)
        {
            return;
        }

        await CompleteNativeResizeAsync();
    }

    private async Task CompleteNativeResizeAsync()
    {
        if (!_isResizeDragging)
        {
            return;
        }

        _isResizeDragging = false;
        _nativeSelectionFrame.StopTracking(hide: false);
        SetNativeSelectionVisualSuppressed(true);
        if (TryGetResizePreviewBounds(out var finalBounds) ||
            TryGetNativeFinalSelectionBounds(out finalBounds))
        {
            UpdateSelectionBounds(finalBounds);
        }
        else
        {
            FlushSelectionBoundsUpdate();
        }
        await CompleteSelectionAdjustmentAsync();
        if (_lastRenderedSelectionBounds is { } committedBounds)
        {
            UpdateSelectionBounds(committedBounds);
            UpdateNativeSelectionVisuals(committedBounds);
        }
        _nativeSelectionFrame.RefreshMask();
        if (InlineEditorCanvas.HasImage)
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            InlineEditorOutline.Visibility = Visibility.Visible;
        }
        else
        {
            SelectionRectangle.Visibility = Visibility.Visible;
        }
        SetResizeControlsSuppressed(false);
        if (_lastRenderedSelectionBounds is { } finalSelection)
        {
            RestoreToolbarAfterNativeAdjustment(finalSelection);
        }
    }

    private void OnSelectionResizeThumbDragStarted(
        object sender,
        DragStartedEventArgs e)
    {
        BeginNativeResize(sender as Thumb);
    }

    private void BeginNativeResize(Thumb? resizeHandle)
    {
        if (!HasValidSelection() || resizeHandle is null)
        {
            return;
        }

        _isResizeDragging = true;
        _resizeLeftEdge = resizeHandle == TopLeftResizeThumb ||
            resizeHandle == BottomLeftResizeThumb ||
            resizeHandle == LeftResizeThumb;
        _resizeTopEdge = resizeHandle == TopLeftResizeThumb ||
            resizeHandle == TopRightResizeThumb ||
            resizeHandle == TopResizeThumb;
        _resizeRightEdge = resizeHandle == TopRightResizeThumb ||
            resizeHandle == BottomRightResizeThumb ||
            resizeHandle == RightResizeThumb;
        _resizeBottomEdge = resizeHandle == BottomLeftResizeThumb ||
            resizeHandle == BottomRightResizeThumb ||
            resizeHandle == BottomResizeThumb;
        _resizeDragStartCursor = WinForms.Cursor.Position;
        _resizeDragStartPhysicalBounds = ToDrawingRectangle(
            GetPhysicalSelectionBounds());

        BeginSelectionAdjustment();
        // The native mask is already the only visible dimming layer. Refresh
        // it before hiding the WPF thumbs so the resize gesture never exposes
        // the old WPF mask for a frame.
        if (!_nativeSelectionFrame.IsMaskEnabled)
        {
            _nativeSelectionFrame.SetMaskEnabled(true);
        }
        _nativeSelectionFrame.RefreshMask();
        QueueSelectionBoundsUpdate(GetSelectionBounds());
        SelectionRectangle.Visibility = Visibility.Collapsed;
        // During the native resize path WPF must not leave its handles,
        // toolbar, or editor outline on screen. They are still kept alive for
        // input capture, but are fully transparent so there is only one frame
        // to compose and paint.
        SetResizeControlsSuppressed(true);
        SyncNativeSelectionFrameColor();
        _nativeSelectionFrame.SetSizeEnabled(ShouldShowSelectionSize);
        _nativeSelectionFrame.StartCustomTracking(cursor =>
            ResizePhysicalBoundsFromCursor(cursor),
            () => Dispatcher.BeginInvoke(new Action(() =>
                _ = CompleteNativeResizeAsync())));
        SetNativeSelectionVisualSuppressed(true);
    }

    private void SetResizeControlsSuppressed(bool suppressed)
    {
        _resizeControlsSuppressed = suppressed;
        InlineEditorCanvas.SetWatermarkSuppressed(suppressed);
        var opacity = suppressed ? 0 : 1;
        // Keep the active Thumb in the tree while native resize is running.
        // Collapsing it releases WPF mouse capture and fires DragCompleted
        // before the native tracker has finished.
        var thumbVisibility = suppressed
            ? Visibility.Hidden
            : Visibility.Visible;
        TopLeftResizeThumb.Opacity = opacity;
        TopLeftResizeThumb.Visibility = thumbVisibility;
        TopRightResizeThumb.Opacity = opacity;
        TopRightResizeThumb.Visibility = thumbVisibility;
        BottomLeftResizeThumb.Opacity = opacity;
        BottomLeftResizeThumb.Visibility = thumbVisibility;
        BottomRightResizeThumb.Opacity = opacity;
        BottomRightResizeThumb.Visibility = thumbVisibility;
        TopResizeThumb.Opacity = opacity;
        TopResizeThumb.Visibility = thumbVisibility;
        LeftResizeThumb.Opacity = opacity;
        LeftResizeThumb.Visibility = thumbVisibility;
        RightResizeThumb.Opacity = opacity;
        RightResizeThumb.Visibility = thumbVisibility;
        BottomResizeThumb.Opacity = opacity;
        BottomResizeThumb.Visibility = thumbVisibility;
        // A move keeps the existing toolbar frame and mask exclusion intact;
        // only a resize hides it because the resize handles would otherwise
        // overlap the live native border. This avoids a one-frame blank hole
        // when a move starts or ends.
        var hideToolbar = suppressed;
        if (hideToolbar)
        {
            CaptureToolbar.Opacity = 0;
            ImageQuestionButton.Visibility = Visibility.Collapsed;
            CaptureToolbar.Visibility = Visibility.Collapsed;
        }
        if (hideToolbar && _nativeSelectionFrame.IsMaskEnabled)
        {
            _nativeSelectionFrame.SetMaskExclusionsSuspended(true);
            _nativeSelectionFrame.SetMaskExcludedRegions(null);
        }
        InlineEditorOptions.Opacity = opacity;
        InlineEditorOptions.Visibility = suppressed || !_hasVisibleInlineEditorTools || !_hasSelectedInlineTool
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (suppressed)
        {
            SelectionMoveTopLeftZone.Visibility = Visibility.Collapsed;
            SelectionMoveTopRightZone.Visibility = Visibility.Collapsed;
            SelectionMoveBottomLeftZone.Visibility = Visibility.Collapsed;
            SelectionMoveBottomRightZone.Visibility = Visibility.Collapsed;
        }
    }

    private void HideToolbarForNativeMove()
    {
        if (_toolbarHiddenForMove)
        {
            return;
        }

        _toolbarHiddenForMove = true;
        ImageQuestionButton.Visibility = Visibility.Collapsed;
        CaptureToolbar.Visibility = Visibility.Collapsed;
        CaptureToolbar.Opacity = 0;
        CaptureToolbar.UpdateLayout();
        if (_nativeSelectionFrame.IsMaskEnabled)
        {
            _nativeSelectionFrame.SetMaskExclusionsSuspended(true);
            // Commit the continuous mask after the toolbar has been removed
            // from the visual tree. This prevents its old exclusion rectangle
            // from becoming a white hole during the first drag sample.
            _nativeSelectionFrame.SetMaskExcludedRegions(null);
        }
    }

    private void RestoreToolbarAfterNativeMove(Rect? finalBounds = null)
    {
        if (!_toolbarHiddenForMove || _isCompleted)
        {
            return;
        }

        _toolbarHiddenForMove = false;
        RestoreToolbarAfterNativeAdjustment(finalBounds);
    }

    private void RestoreToolbarAfterNativeAdjustment(Rect? finalBounds)
    {
        if (_isCompleted)
        {
            return;
        }

        // Keep the full native mask while WPF lays out and paints the toolbar.
        // The exclusion is published only after that paint pass, so the
        // desktop can never show an empty toolbar hole.
        CaptureToolbar.Opacity = 0;
        CaptureToolbar.Visibility = Visibility.Visible;
        InlineEditorOptions.Visibility = _hasVisibleInlineEditorTools && _hasSelectedInlineTool
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Complete the WPF layout before publishing the native mask again.
        // The mask exclusion is applied after WPF has rendered the toolbar so
        // the compositor never presents an empty cutout in its place.
        CaptureToolbar.UpdateLayout();
        if (finalBounds is { } bounds)
        {
            UpdateResizeThumbPositions(bounds);
            UpdateSelectionControlPositions(bounds);
            UpdateSelectionMoveZonePositions(bounds);
        }
        if (_nativeSelectionFrame.IsMaskEnabled && HasValidSelection())
        {
            ScheduleToolbarMaskPublication(finalBounds ?? GetSelectionBounds());
        }
    }

    private System.Drawing.Rectangle ResizePhysicalBoundsFromCursor(
        DrawingPoint cursor)
    {
        var bounds = _resizeDragStartPhysicalBounds;
        var dx = cursor.X - _resizeDragStartCursor.X;
        var dy = cursor.Y - _resizeDragStartCursor.Y;
        var minimumEdge = (int)Math.Ceiling(MinimumSelectionEdge);
        var left = bounds.Left + (_resizeLeftEdge ? dx : 0);
        var top = bounds.Top + (_resizeTopEdge ? dy : 0);
        var right = bounds.Right + (_resizeRightEdge ? dx : 0);
        var bottom = bounds.Bottom + (_resizeBottomEdge ? dy : 0);
        var surface = ToDrawingRectangle(_virtualScreenBounds);
        left = Math.Clamp(left, surface.Left, surface.Right);
        top = Math.Clamp(top, surface.Top, surface.Bottom);
        right = Math.Clamp(right, surface.Left, surface.Right);
        bottom = Math.Clamp(bottom, surface.Top, surface.Bottom);

        if (right - left < minimumEdge)
        {
            if (_resizeLeftEdge)
            {
                left = Math.Max(surface.Left, right - minimumEdge);
            }
            else
            {
                right = Math.Min(surface.Right, left + minimumEdge);
            }
        }

        if (bottom - top < minimumEdge)
        {
            if (_resizeTopEdge)
            {
                top = Math.Max(surface.Top, bottom - minimumEdge);
            }
            else
            {
                bottom = Math.Min(surface.Bottom, top + minimumEdge);
            }
        }

        if (_selectionAdjustmentProtectedPhysicalBounds is { } protectedBounds)
        {
            if (_resizeLeftEdge)
            {
                left = Math.Min(left, protectedBounds.Left);
            }

            if (_resizeTopEdge)
            {
                top = Math.Min(top, protectedBounds.Top);
            }

            if (_resizeRightEdge)
            {
                right = Math.Max(right, protectedBounds.Right);
            }

            if (_resizeBottomEdge)
            {
                bottom = Math.Max(bottom, protectedBounds.Bottom);
            }
        }

        if (!_isScrollCaptureSelection &&
            CaptureAspectRatioHelper.GetValue(
                _sessionAspectRatio) is { } ratio)
        {
            var original = _resizeDragStartPhysicalBounds;
            var width = Math.Max(minimumEdge, right - left);
            var height = Math.Max(minimumEdge, bottom - top);
            var horizontal = _resizeLeftEdge || _resizeRightEdge;
            var vertical = _resizeTopEdge || _resizeBottomEdge;
            if (horizontal && !vertical)
            {
                // Side handles keep the opposite side fixed and adjust the
                // other dimension around the original center line. Without
                // this, the height changed but top/bottom stayed untouched,
                // so the ratio was immediately lost on release.
                height = Math.Max(minimumEdge, (int)Math.Round(width / ratio));
                var centerY = original.Top + original.Height / 2d;
                top = (int)Math.Round(centerY - height / 2d);
                bottom = top + height;
            }
            else if (vertical && !horizontal)
            {
                width = Math.Max(minimumEdge, (int)Math.Round(height * ratio));
                var centerX = original.Left + original.Width / 2d;
                left = (int)Math.Round(centerX - width / 2d);
                right = left + width;
            }
            else
            {
                var widthDelta = Math.Abs(width - original.Width);
                var heightDelta = Math.Abs(height - original.Height);
                if (widthDelta >= heightDelta * ratio)
                    height = Math.Max(minimumEdge, (int)Math.Round(width / ratio));
                else
                    width = Math.Max(minimumEdge, (int)Math.Round(height * ratio));
            }

            if (_resizeLeftEdge) left = right - width;
            else if (_resizeRightEdge) right = left + width;
            if (_resizeTopEdge) top = bottom - height;
            else if (_resizeBottomEdge) bottom = top + height;
            left = Math.Max(surface.Left, left);
            top = Math.Max(surface.Top, top);
            right = Math.Min(surface.Right, right);
            bottom = Math.Min(surface.Bottom, bottom);
            if (_resizeLeftEdge) left = right - Math.Min(width, surface.Width);
            if (_resizeTopEdge) top = bottom - Math.Min(height, surface.Height);
        }

        return new System.Drawing.Rectangle(
            Math.Min(left, right),
            Math.Min(top, bottom),
            Math.Abs(right - left),
            Math.Abs(bottom - top));
    }

    private bool TryGetResizePreviewBounds(out Rect bounds)
    {
        bounds = default;
        var physical = ResizePhysicalBoundsFromCursor(WinForms.Cursor.Position);
        if (!TryGetCaptureSurfacePoint(
                new DrawingPoint(physical.Left, physical.Top),
                out var topLeft) ||
            !TryGetCaptureSurfacePoint(
                new DrawingPoint(physical.Right, physical.Bottom),
                out var bottomRight))
        {
            return false;
        }

        bounds = ClampBoundsToSurface(new Rect(topLeft, bottomRight));
        return true;
    }

    private bool HasSelectableTextSelection()
    {
        if (OcrTextOverlay.Visibility != Visibility.Visible)
        {
            return false;
        }

        return OcrTextOverlay.Children
            .OfType<SelectableOcrTextOverlay>()
            .Any(overlay => overlay.HasSelectedText);
    }

    private bool TryGetNativeFinalSelectionBounds(
        out Rect bounds,
        bool preserveMoveSize = false)
    {
        bounds = default;
        if (!_nativeSelectionFrame.TryGetLastBounds(out var physical) ||
            !TryGetCaptureSurfacePoint(
                new DrawingPoint(physical.Left, physical.Top),
                out var topLeft) ||
            !TryGetCaptureSurfacePoint(
                new DrawingPoint(physical.Right, physical.Bottom),
                out var bottomRight))
        {
            return false;
        }

        var nativeBounds = new Rect(topLeft, bottomRight);
        bounds = preserveMoveSize
            ? ClampMovedBoundsToSurface(nativeBounds)
            : ClampBoundsToSurface(nativeBounds);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private void UpdateNativeSelectionVisuals(Rect bounds)
    {
        PositionImageQuestionButton(bounds);
        if (_isCaptureBackgrounded) return;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        SyncNativeSelectionFrameColor();
        _nativeSelectionFrame.SetMaskExcludedRegions(
            GetVisibleOverlayScreenBounds());
        _nativeSelectionFrame.SetSizeEnabled(ShouldShowSelectionSize);
        _nativeSelectionFrame.Update(ToDrawingRectangle(
            GetPhysicalSelectionBoundsForBounds(bounds)));
        _nativeSelectionFrame.RefreshMask();
        _nativeSelectionFrame.EnsureVisible();
        if (!_isScrollCaptureSelection && !_nativeSelectionFrame.IsPresentationDeferred)
        {
            _nativeSelectionMask.RefreshPresentation();
        }
        CaptureToolbar.Opacity = 1;
    }

    private void ScheduleToolbarMaskPublication(Rect bounds)
    {
        if (_isDisposed || _isCompleted || _isCaptureBackgrounded ||
            _isScrollCaptureSelection ||
            !_nativeSelectionFrame.IsMaskEnabled ||
            _nativeSelectionFrame.IsPresentationDeferred ||
            _toolbarMaskPublishScheduled)
        {
            return;
        }

        _toolbarMaskPublishScheduled = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                _toolbarMaskPublishScheduled = false;
                if (_isDisposed || _isCompleted || _isCaptureBackgrounded ||
                    _isScrollCaptureSelection ||
                    _isMovingSelection || _isResizeDragging ||
                    !_nativeSelectionFrame.IsMaskEnabled)
                {
                    return;
                }

                CaptureToolbar.Visibility = Visibility.Visible;
                CaptureToolbar.Opacity = 1;
                CaptureToolbar.UpdateLayout();
                // Publish after layout, but never wait for the pointer stream
                // to become idle before making the toolbar unobscured.
                _ = CaptureStartupDispatcher.Queue(
                    Dispatcher,
                    new Action(() =>
                    {
                        if (_isDisposed || _isCompleted || _isCaptureBackgrounded ||
                            _isScrollCaptureSelection ||
                            _isMovingSelection || _isResizeDragging ||
                            !_nativeSelectionFrame.IsMaskEnabled)
                        {
                            return;
                        }

                        _nativeSelectionFrame.SetMaskExclusionsSuspended(false);
                        UpdateNativeSelectionVisuals(bounds);
                    }));
            }));
    }

    private List<System.Drawing.Rectangle> GetVisibleOverlayScreenBounds()
    {
        var bounds = new List<System.Drawing.Rectangle>(capacity: 3);
        AddVisibleScreenBounds(CaptureToolbar, bounds);
        AddVisibleScreenBounds(ImageQuestionButton, bounds);
        AddVisibleScreenBounds(InlineCustomColorPanel, bounds);
        AddVisibleScreenBounds(ColorPickerPanel, bounds);
        return bounds;
    }

    private static void AddVisibleScreenBounds(
        FrameworkElement element,
        List<System.Drawing.Rectangle> bounds)
    {
        if (element.Visibility != Visibility.Visible ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0)
        {
            return;
        }

        var topLeft = element.PointToScreen(new WpfPoint(0, 0));
        var bottomRight = element.PointToScreen(
            new WpfPoint(element.ActualWidth, element.ActualHeight));
        bounds.Add(new System.Drawing.Rectangle(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X)),
            Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y))));
    }

    private bool TryGetMovePreviewBounds(out Rect bounds)
    {
        bounds = default;
        if (!TryGetCaptureSurfacePoint(WinForms.Cursor.Position, out var currentPoint))
        {
            return false;
        }

        var delta = currentPoint - _dragStartPoint;
        bounds = ClampMovedBoundsToSurface(new Rect(
            _dragStartBounds.X + delta.X,
            _dragStartBounds.Y + delta.Y,
            _dragStartBounds.Width,
            _dragStartBounds.Height));
        return true;
    }

    private bool ShouldShowSelectionSize =>
        _options is not null &&
        !_isScrollCaptureSelection;

    private async Task RecognizeLocalInlineContentAsync(
        int expectedGeneration,
        int delayMilliseconds)
    {
        if (delayMilliseconds > 0)
        {
            try
            {
                await Task.Delay(
                    delayMilliseconds,
                    _lifetimeCancellationSource.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (_options is null ||
            expectedGeneration != _automaticRecognitionGeneration ||
            _isQrInitializing ||
            _isActionInProgress ||
            !HasValidSelection())
        {
            return;
        }

        _isQrInitializing = true;

        try
        {
            // Automatic recognition must not render the live editor canvas.
            // RenderEditedImage temporarily rearranges that visual at native
            // pixel size and can expose a one-frame flash near the origin.
            using var image = _inlineEditorImage?.Clone() ??
                await CaptureCurrentSelectionAsync(restoreOverlay: true);
            var cancellationToken = _lifetimeCancellationSource.Token;
            var qrTask = Task.Run(
                () => QrCodeRecognitionService.Scan(image, cancellationToken),
                cancellationToken);
            var scan = await qrTask;
            if (expectedGeneration != _automaticRecognitionGeneration ||
                _isCompleted)
            {
                return;
            }

            _inlineQrScan = scan;
            _inlineQrResult = scan.Codes.Count > 0 ? scan.Codes[0] : null;
            _isUnifiedRecognitionVisible = scan.HasDetectedContent;
            if (scan.WeChatCodeRegion is not null)
            {
                CaptureStatusMessage = QrCodeScanResult.WeChatExplanation;
                CaptureStatusText.Visibility = Visibility.Visible;
            }
            ShowAutomaticRecognitionResults();
        }
        catch (OperationCanceledException) when (_isCompleted)
        {
            return;
        }
        catch
        {
            _inlineQrResult = null;
            _inlineQrScan = null;
            _isUnifiedRecognitionVisible = false;
            RebuildContentRecognitionOverlay();
        }
        finally
        {
            _isQrInitializing = false;
        }
    }

    private void ShowAutomaticRecognitionResults()
    {
        _isUnifiedRecognitionVisible = _inlineQrScan?.HasDetectedContent == true || _inlineQrResult?.IsSuccess == true;
        RebuildContentRecognitionOverlay();
    }

    private void HideUnifiedRecognitionResults()
    {
        _isUnifiedRecognitionVisible = false;
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        ContentRecognitionOverlay.Visibility = Visibility.Collapsed;
        ContentRecognitionOverlay.IsHitTestVisible = false;
    }

    private int ShowSelectionMessage(string message)
    {
        var generation = ++_selectionMessageGeneration;
        SelectionMessageText.Text = message;
        SelectionMessageToast.Visibility = Visibility.Visible;
        SelectionMessageToast.UpdateLayout();
        PositionSelectionMessageToast();
        return generation;
    }

    private async Task ShowSelectionMessageForAsync(
        string message,
        int durationMilliseconds)
    {
        var generation = ShowSelectionMessage(message);
        try
        {
            await Task.Delay(
                durationMilliseconds,
                _lifetimeCancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation == _selectionMessageGeneration)
        {
            SelectionMessageToast.Visibility = Visibility.Collapsed;
        }
    }

    private void PositionSelectionMessageToast()
    {
        if (SelectionMessageToast.Visibility != Visibility.Visible)
        {
            return;
        }

        Rect bounds;
        if (HasValidSelection())
        {
            bounds = GetSelectionBounds();
        }
        else if (_isColorPickerActive)
        {
            var cursor = CaptureSurface.PointFromScreen(
                new WpfPoint(WinForms.Cursor.Position.X, WinForms.Cursor.Position.Y));
            bounds = new Rect(cursor.X, cursor.Y, 1, 1);
        }
        else
        {
            return;
        }

        var width = Math.Max(
            SelectionMessageToast.MinWidth,
            SelectionMessageToast.ActualWidth);
        var height = Math.Max(40, SelectionMessageToast.ActualHeight);
        var maximumX = Math.Max(8, CaptureSurface.ActualWidth - width - 8);
        var maximumY = Math.Max(8, CaptureSurface.ActualHeight - height - 8);
        Canvas.SetLeft(
            SelectionMessageToast,
            Math.Clamp(bounds.X + ((bounds.Width - width) / 2), 8, maximumX));
        Canvas.SetTop(
            SelectionMessageToast,
            Math.Clamp(bounds.Y + ((bounds.Height - height) / 2), 8, maximumY));
    }

    private async Task RecognizeInlineTextAsync(
        Func<CapturedImage, Task<OcrRecognitionResult>> recognizeTextAsync)
    {
        if (_isOcrInitializing || _isTranslationInitializing || _isActionInProgress || !HasValidSelection())
        {
            return;
        }

        using var work = BeginImageWork();
        var generation = _automaticRecognitionGeneration;
        var editVersion = _imageWorkEditVersion;
        _isOcrInitializing = true;
        UpdateCaptureActivity();
        CaptureStatusMessage = "正在识别图片文字...";
        CaptureStatusText.Visibility = Visibility.Visible;

        try
        {
            using var image = await CaptureCurrentResultAsync(restoreOverlay: true);
            if (!IsImageWorkCurrent(work, generation)) return;
            var recognition = Task.Run(() => _options?.RecognizeTextWithCancellationAsync is { } recognize
                ? recognize(image, work.Token) : recognizeTextAsync(image), work.Token);
            using var popup = RecognitionPopupSession.Begin(
                _options?.RecognitionResultPresentation == RecognitionResultPresentationMode.Popup,
                "正在识别文字…");
            var result = await recognition;
            if (!IsImageWorkCurrent(work, generation)) return;
            _inlineOcrResult = result;

            if (!result.IsSuccess)
            {
                CaptureStatusMessage = result.ErrorMessage ?? "文字识别失败。";
                return;
            }

            if (result.Regions.Count == 0)
            {
                CaptureStatusMessage = "没有识别到可选择的文字。";
                return;
            }

            if (popup is not null)
            {
                if (IsImageWorkCurrent(work, generation) && editVersion == _imageWorkEditVersion) CompleteSelection(result: null);
                popup.Complete("文字识别", string.IsNullOrWhiteSpace(result.Text)
                    ? string.Join(Environment.NewLine, result.Regions.Select(region => region.Text))
                    : result.Text, notice: result.ResultNotice);
            }
            else if (editVersion == _imageWorkEditVersion && !InlineEditorCanvas.IsTextInputActive) await ShowRecognizedTextAsync(result);
            else CaptureStatusMessage = "文字识别完成，点击文字识别查看结果，可继续标注。";
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch
        {
            if (IsImageWorkCurrent(work, generation)) CaptureStatusMessage = "文字识别失败，请检查 OCR 语言设置。";
        }
        finally
        {
            EndImageWork(work);
            _isOcrInitializing = false;
            UpdateCaptureActivity();

            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = !_isActionInProgress;
            }
        }
    }

    private async void OnOcrClick(object sender, RoutedEventArgs e)
    {
        if (_inlineOcrResult is { IsSuccess: true, Regions.Count: > 0 } cached)
        {
            await ShowRecognizedTextAsync(cached);
            return;
        }

        if (_isOcrInitializing)
        {
            CaptureStatusMessage = "正在后台分析当前选区，完成后再点击“文”即可选择文字。";
            CaptureStatusText.Visibility = Visibility.Visible;
            return;
        }

        if (_options?.RecognizeTextAsync is { } recognizeTextAsync)
        {
            await RecognizeInlineTextAsync(recognizeTextAsync);
        }
    }

    private void OnOcrMenuArrowMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (OcrButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = OcrButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private string? GetSpecialContentReason()
    {
        if (_inlineQrResult?.IsSuccess == true)
        {
            return "选区中有二维码，请先处理二维码内容";
        }

        if (_inlineTableResult?.IsSuccess == true)
        {
            return "选区中有表格，请使用“表格复制”";
        }

        return null;
    }

    private async void OnCopyRecognizedTextClick(
        object sender,
        RoutedEventArgs e)
        => await CopyRecognizedTextAsync();

    private async Task CopyRecognizedTextAsync(Func<string, CancellationToken, Task>? copy = null)
    {
        if (_options?.RecognizeTextAsync is null ||
            _isOcrInitializing || _isTranslationInitializing ||
            _isActionInProgress ||
            !HasValidSelection())
        {
            return;
        }

        using var work = BeginImageWork();
        var generation = _automaticRecognitionGeneration;
        var editVersion = _imageWorkEditVersion;
        var completedRegion = GetPhysicalSelectionBounds();
        _isOcrInitializing = true;

        UpdateCaptureActivity();

        ShowSelectionMessage("正在识别文字...");
        try
        {
            using var image = _inlineEditorImage?.Clone() ??
                await CaptureCurrentSelectionAsync(restoreOverlay: true);
            var recognition = _inlineOcrResult;
            if (recognition is not { IsSuccess: true } ||
                (recognition.Regions.Count == 0 && string.IsNullOrWhiteSpace(recognition.Text)))
            {
                recognition = await Task.Run(() => _options.RecognizeTextWithCancellationAsync is { } recognize
                    ? recognize(image, work.Token) : _options.RecognizeTextAsync(image), work.Token);
                if (!IsImageWorkCurrent(work, generation)) return;
                _inlineOcrResult = recognition;
            }

            if (!recognition.IsSuccess ||
                (recognition.Regions.Count == 0 && string.IsNullOrWhiteSpace(recognition.Text)))
            {
                await ShowSelectionMessageForAsync(
                    recognition.ErrorMessage ?? "当前选区未识别到文字",
                    1500);
                return;
            }

            var text = string.IsNullOrWhiteSpace(recognition.Text)
                ? string.Join(
                    Environment.NewLine,
                    recognition.Regions
                        .OrderBy(region => region.Y)
                        .ThenBy(region => region.X)
                        .Select(region => region.Text))
                : recognition.Text;
            if (!IsImageWorkCurrent(work, generation) || editVersion != _imageWorkEditVersion) return;
            // “取” is always a direct text copy, independent of the “文” result-window setting.
            await (copy ?? ClipboardTextService.SetTextAsync)(text.Trim(), work.Token);
            if (IsImageWorkCurrent(work, generation) && editVersion == _imageWorkEditVersion)
            {
                CompleteSelection(result: null);
                _ = CaptureSuccessWindow.ShowAsync(completedRegion, "已识别复制完成");
            }
        }
        catch (OperationCanceledException) when (_isCompleted || _isDisposed)
        {
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch
        {
            await ShowSelectionMessageForAsync(
                "文字识别或复制失败，请重试",
                1500);
        }
        finally
        {
            EndImageWork(work);
            _isOcrInitializing = false;
            UpdateCaptureActivity();
            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = !_isActionInProgress;
            }
        }
    }

    private async void OnCopyTableMenuItemClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_options?.RecognizeTextAsync is null ||
            _isOcrInitializing || _isTranslationInitializing ||
            _isActionInProgress ||
            !HasValidSelection())
        {
            return;
        }

        using var work = BeginImageWork();
        var generation = _automaticRecognitionGeneration;
        var editVersion = _imageWorkEditVersion;
        _isOcrInitializing = true;

        UpdateCaptureActivity();

        ShowSelectionMessage("正在识别表格...");
        try
        {
            using var image = _inlineEditorImage?.Clone() ??
                await CaptureCurrentSelectionAsync(restoreOverlay: true);
            var recognition = _inlineOcrResult;
            if (recognition is not { IsSuccess: true, Words.Count: > 0 })
            {
                recognition = await Task.Run(() => _options.RecognizeTextWithCancellationAsync is { } recognize
                    ? recognize(image, work.Token) : _options.RecognizeTextAsync(image), work.Token);
                if (!IsImageWorkCurrent(work, generation)) return;
                _inlineOcrResult = recognition;
            }

            if (!recognition.IsSuccess)
            {
                await ShowSelectionMessageForAsync(
                    recognition.ErrorMessage ?? "表格识别失败。",
                    1500);
                return;
            }

            var supplementary = await TableSupplementaryOcrService.RecognizeAsync(
                image, recognition.Words, cancellationToken: work.Token);
            var table = await Task.Run(() => TableRecognitionService.BuildTsv(
                recognition, image.Bitmap, supplementary, work.Token),
                work.Token);
            if (!IsImageWorkCurrent(work, generation)) return;
            _inlineTableResult = table;
            if (!table.IsSuccess)
            {
                await ShowSelectionMessageForAsync(
                    "当前选区未识别到表格",
                    1500);
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(table.ClipboardHtml))
                {
                    await ClipboardTextService.SetHtmlAsync(
                        table.ClipboardHtml,
                        table.Content, work.Token);
                }
                else
                {
                    await ClipboardTextService.SetTextAsync(table.Content, work.Token);
                }
            }
            catch (OperationCanceledException) when (work.IsCancellationRequested) { return; }
            catch
            {
                await ShowSelectionMessageForAsync(
                    "表格复制失败，请稍后重试",
                    1500);
                return;
            }

            await ShowSelectionMessageForAsync(
                "表格已识别并复制",
                900);
            if (IsImageWorkCurrent(work, generation) && editVersion == _imageWorkEditVersion) CompleteSelection(result: null);
        }
        catch (OperationCanceledException) when (_isCompleted || _isDisposed)
        {
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch
        {
            await ShowSelectionMessageForAsync(
                "表格识别失败，请重新尝试",
                1500);
        }
        finally
        {
            EndImageWork(work);
            _isOcrInitializing = false;
            UpdateCaptureActivity();
            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = !_isActionInProgress;
                if (!_isCaptureBackgrounded) InlineEditorCanvas.Focus();
            }
        }
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        ClearInlineToolSelection();
        InlineActionAdjustments.Clear();
        RefreshCaptureToolbarLayoutAfterOptionChange();
        await TranslateInlineTextAsync();
    }

    private void UpdateTranslationToolTip(string action = "翻译")
    {
        var language = TranslationTargetPreferences.Language ?? _options?.TranslationTargetLanguage ?? "zh-Hans";
        var name = TranslationLanguageCatalog.Languages.FirstOrDefault(item => item.Tag == language)?.DisplayName ?? language;
        TranslateButton.ToolTip = $"{UiLocalization.Get(action)} · {UiLocalization.Get("目标语言")}：{name}\n{UiLocalization.Get("右键选择目标语言")}";
    }

    private async void OnPrivacyRedactionClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_options?.RecognizeTextAsync is null ||
            _isOcrInitializing ||
            _isActionInProgress ||
            !HasValidSelection())
        {
            return;
        }

        _isOcrInitializing = true;

        UpdateCaptureActivity();
        CaptureToolbar.IsEnabled = false;
        ShowSelectionMessage("正在检测敏感信息...");
        try
        {
            await EnterInlineEditorForCompletedSelectionAsync();
            if (!InlineEditorCanvas.HasImage || _isCompleted)
            {
                return;
            }

            var recognition = _inlineOcrResult;
            if (recognition is not { IsSuccess: true })
            {
                using var image = _inlineEditorImage?.Clone() ??
                    await CaptureCurrentSelectionAsync(restoreOverlay: true);
                recognition = await _options.RecognizeTextAsync(image);
                _inlineOcrResult = recognition;
            }

            if (!recognition.IsSuccess)
            {
                await ShowSelectionMessageForAsync(
                    recognition.ErrorMessage ?? "敏感信息识别失败",
                    1600);
                return;
            }

            var candidates = PrivacyDetectionService.Detect(recognition);
            if (candidates.Count == 0)
            {
                await ShowSelectionMessageForAsync(
                    "未检测到支持的敏感信息",
                    1700);
                return;
            }

            SelectionMessageToast.Visibility = Visibility.Collapsed;
            var confirmation = new PrivacyRedactionWindow(candidates)
            {
                Owner = this,
                Topmost = true,
            };
            if (confirmation.ShowDialog() != true)
            {
                CaptureStatusMessage = "已取消隐私打码。";
                CaptureStatusText.Visibility = Visibility.Visible;
                return;
            }

            var selected = confirmation.SelectedCandidates;
            if (selected.Count == 0)
            {
                CaptureStatusMessage = "未选择需要打码的项目。";
                CaptureStatusText.Visibility = Visibility.Visible;
                return;
            }

            InlineEditorCanvas.AddMosaicRegions(
                selected.Select(candidate => candidate.Bounds));
            CaptureStatusMessage =
                $"已添加 {selected.Count} 处隐私马赛克，可撤销或继续编辑。";
            CaptureStatusText.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (_isCompleted || _isDisposed)
        {
        }
        catch
        {
            CaptureStatusMessage = "隐私信息检测失败，请重试。";
            CaptureStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            _isOcrInitializing = false;
            UpdateCaptureActivity();
            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = !_isActionInProgress;
                InlineEditorCanvas.Focus();
            }
        }
    }

    private void OnTranslationTargetChanged(object? sender, EventArgs e)
    {
        if (_isTranslationInitializing) _imageWorkCancellation?.Cancel();
        _inlineTranslationInput = null;
        _inlineTranslationResult = null;
        _inlineTranslatedTextRegions = null;
        _inlineTranslatedAnnotationRegions = null;
        _isShowingTranslatedText = false;
        InlineEditorCanvas.ClearTranslationOverlay();
        OcrTextOverlay.Children.Clear();
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        RetryTranslationButton.Visibility = Visibility.Collapsed;
        TranslateButtonText.Text = "译";
        UpdateTranslationToolTip();
    }

    private async Task TranslateInlineTextAsync(bool retryIncomplete = false)
    {
        if (_options?.RecognizeTextAsync is null ||
            _options.TranslateTextAsync is null)
        {
            CaptureStatusMessage = "当前截图模式未配置翻译功能。";
            CaptureStatusText.Visibility = Visibility.Visible;
            return;
        }

        if (!retryIncomplete && _inlineTranslatedTextRegions is not null &&
            _inlineTranslatedAnnotationRegions is not null)
        {
            if (_inlineTranslatedTextRegions.Count == 0 ||
                _inlineTranslatedAnnotationRegions.Count == 0)
            {
                CaptureStatusMessage =
                    "识别文字已经是目标语言，没有需要覆盖的译文。";
                CaptureStatusText.Visibility = Visibility.Visible;
                return;
            }

            if (!InlineEditorCanvas.HasTranslationOverlay)
            {
                InlineEditorCanvas.AddTranslationOverlay(
                    _inlineTranslatedAnnotationRegions);
                SetInlineTranslationVisibility(isVisible: true);
                return;
            }

            SetInlineTranslationVisibility(
                !InlineEditorCanvas.IsTranslationOverlayVisible);
            return;
        }

        if (_isTranslationInitializing || _isOcrInitializing ||
            _isActionInProgress || !HasValidSelection())
        {
            return;
        }

        using var work = BeginImageWork();
        var generation = _automaticRecognitionGeneration;
        var editVersion = _imageWorkEditVersion;
        var targetLanguage = TranslationTargetPreferences.Language ?? _options.TranslationTargetLanguage;
        var languageVersion = TranslationTargetPreferences.Version;
        _isTranslationInitializing = true;
        var totalTranslationTimer = System.Diagnostics.Stopwatch.StartNew();

        UpdateCaptureActivity();
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        CaptureStatusMessage = "正在识别图片文字（本机处理）...";
        CaptureStatusText.Visibility = Visibility.Visible;

        try
        {
            using var modelPreparation = await Task.Run(() => _options.PrepareTranslation?.Invoke(work.Token), work.Token);
            using var translationImage = await CaptureCurrentResultAsync(restoreOverlay: true);
            if (!IsImageWorkCurrent(work, generation)) return;
            using var popup = RecognitionPopupSession.Begin(
                _options.RecognitionResultPresentation == RecognitionResultPresentationMode.Popup,
                "正在识别文字…");
            var recognition = _inlineOcrResult;
            if (recognition is not { IsSuccess: true, Regions.Count: > 0 })
            {
                recognition = await Task.Run(() => _options.RecognizeTextWithCancellationAsync is { } recognizeWithCancellation
                    ? recognizeWithCancellation(translationImage, work.Token)
                    : _options.RecognizeTextAsync(translationImage), work.Token);
                if (!IsImageWorkCurrent(work, generation)) return;
                _inlineOcrResult = recognition;
            }

            if (!recognition.IsSuccess)
            {
                CaptureStatusMessage =
                    recognition.ErrorMessage ?? "文字识别失败。";
                return;
            }

            if (recognition.Regions.Count == 0)
            {
                CaptureStatusMessage = "没有识别到可翻译的文字。";
                return;
            }

            if (languageVersion != TranslationTargetPreferences.Version) return;
            var measuredRegions = retryIncomplete && _inlineTranslationInput is { } savedInput
                ? savedInput.Regions
                : await Task.Run(() => recognition.Words.Count > 0
                    ? TranslationInkBounds.Refine(translationImage.Bitmap,
                        TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words))
                    : recognition.Regions,
                work.Token);
            if (!IsImageWorkCurrent(work, generation)) return;
            var tightenedRegions = measuredRegions
                .Where(region => !string.IsNullOrWhiteSpace(region.Text))
                .OrderBy(region => region.Y)
                .ThenBy(region => region.X)
                .ToArray();
            // OCR often returns each wrapped line as a separate region, even
            // for a small selection. Always group compatible lines first so
            // the translator and overlay calculate one font size and line
            // height for the complete paragraph.
            var translationRegions = retryIncomplete && _inlineTranslationInput is { } priorInput
                ? priorInput.Regions.ToArray()
                : TranslationPresentationLayout.GroupParagraphs(tightenedRegions).ToArray();
            var translationInput = recognition with
            {
                Text = string.Join(
                    Environment.NewLine,
                    translationRegions.Select(region => region.Text)),
                Regions = translationRegions,
            };

            if (translationRegions.All(region =>
                    TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(
                        region.Text,
                        targetLanguage)))
            {
                _inlineTranslatedTextRegions = translationRegions;
                _inlineTranslatedAnnotationRegions = [];
                CaptureStatusMessage =
                    "识别到的文字已是目标语言或无需翻译的内容，已保留原文。";
                return;
            }

            var retryIndices = retryIncomplete ? _inlineTranslationResult?.FailedSegmentIndices : null;
            var requestedInput = retryIndices is { Count: > 0 }
                ? translationInput with
                {
                    Regions = retryIndices.Select(index => translationRegions[index]).ToArray(),
                    Text = string.Join("\n", retryIndices.Select(index => translationRegions[index].Text)),
                    Words = [],
                }
                : translationInput;
            _inlineTranslationInput = translationInput;
            CaptureStatusMessage = $"正在{(retryIncomplete ? "补译" : "翻译")} {requestedInput.Regions.Count} 段文字...";
            var targetLanguageRegionCount = translationRegions.Count(region =>
                TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(
                    region.Text,
                    targetLanguage));
            var mostlyTargetLanguage = targetLanguageRegionCount >=
                Math.Max(1, (int)Math.Ceiling(translationRegions.Length * 0.75));
            popup?.Update("正在翻译…", "按已配置的模型优先顺序处理 · 完成后显示实际来源");
            var translationTimer = System.Diagnostics.Stopwatch.StartNew();
            var recognitionElapsed = totalTranslationTimer.Elapsed;
            var translationPhase = $"正在翻译 {requestedInput.Regions.Count} 段文字";
            using var translationProgress = TranslationProgress.Begin(message =>
            {
                if (!IsImageWorkCurrent(work, generation)) return;
                translationPhase = message;
                CaptureStatusMessage = message;
                popup?.Update("翻译进度", message);
            });
            // Large captures translate for a while; a static label reads as a
            // freeze, so keep the elapsed time visibly moving.
            var waitTicker = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            waitTicker.Tick += (_, _) =>
            {
                if (!IsImageWorkCurrent(work, generation)) return;
                CaptureStatusMessage =
                    $"{translationPhase} · 已等待 {translationTimer.Elapsed.TotalSeconds:F0} 秒（Esc 取消）";
            };
            waitTicker.Start();
            TranslationSegmentsResult translation;
            try
            {
                translation = await Task.Run(() => _options.TranslateTextWithCancellationAsync is { } translateWithCancellation
                        ? translateWithCancellation(requestedInput, work.Token)
                        : _options.TranslateTextAsync(requestedInput), work.Token);
                if (!IsImageWorkCurrent(work, generation)) return;
                if (retryIndices is { Count: > 0 } && _inlineTranslationResult is { } previousResult)
                    translation = LocalTranslationRepair.MergeRetry(translationRegions.Select(region => region.Text).ToArray(),
                        previousResult, retryIndices, translation, targetLanguage);
            }
            finally
            {
                waitTicker.Stop();
            }

            if (languageVersion != TranslationTargetPreferences.Version)
            {
                CaptureStatusMessage = "目标语言已改变，请重新翻译。";
                return;
            }
            translationTimer.Stop();
            translation = translation with { Notice = translation.Notice +
                $" 识别／版面准备 {recognitionElapsed.TotalSeconds:F1} 秒，翻译阶段 {translationTimer.Elapsed.TotalSeconds:F1} 秒。" +
                (recognition.Performance is { } ocrPerformance ? "\n" + ocrPerformance.Description : string.Empty) };
            _inlineTranslationResult = translation.IsSuccess && translation.Segments.Count == translationRegions.Length
                ? translation
                : new TranslationSegmentsResult(true, translationRegions.Select(region => region.Text).ToArray(), translation.ErrorMessage)
                    { FailedSegmentIndices = Enumerable.Range(0, translationRegions.Length).ToArray() };
            RetryTranslationButton.Visibility = _inlineTranslationResult.FailedSegmentIndices is { Count: > 0 }
                ? Visibility.Visible : Visibility.Collapsed;
            if (!translation.IsSuccess)
            {
                CaptureStatusMessage = translation.ErrorMessage ?? (mostlyTargetLanguage
                    ? "识别到的大部分文字已经是目标语言，未生成覆盖翻译。"
                    : "翻译失败。");
                return;
            }

            if (!string.IsNullOrWhiteSpace(translation.ErrorMessage))
            {
                CaptureStatusMessage = translation.ErrorMessage;
            }
            else if (!string.IsNullOrWhiteSpace(translation.Notice))
            {
                CaptureStatusMessage = translation.Notice;
            }

            if (translation.Segments.Count != translationRegions.Length)
            {
                CaptureStatusMessage = "翻译服务返回的分段结果不完整。";
                return;
            }

            var normalizedTranslations = translation.Segments
                .Select((text, index) => TranslationPresentationLayout
                    .NormalizeTranslatedText(
                        translationRegions[index].Text,
                        text))
                .ToArray();
            if (_options.RecognitionResultPresentation ==
                    RecognitionResultPresentationMode.Popup &&
                _options.ShowRecognitionPopupAsync is { } showPopup)
            {
                var translatedText = string.Join(
                    Environment.NewLine,
                    normalizedTranslations);
                var translationTitle = string.IsNullOrWhiteSpace(translation.ErrorMessage)
                    ? "已翻译"
                    : "翻译未完成";
                var popupNotice = string.Join("\n", new[] { translation.Notice, translation.ErrorMessage }
                    .Where(message => !string.IsNullOrWhiteSpace(message)));
                if (popup is not null)
                {
                    if (IsImageWorkCurrent(work, generation) && editVersion == _imageWorkEditVersion) CompleteSelection(result: null);
                    popup.Complete(translationTitle, translationInput.Text, translatedText, popupNotice);
                    return;
                }
                if (IsImageWorkCurrent(work, generation) && editVersion == _imageWorkEditVersion) CompleteSelection(result: null);
                await showPopup(
                    translationTitle,
                    translationInput.Text,
                    translatedText,
                    string.Join("\n", new[] { translation.Notice, translation.ErrorMessage }
                        .Where(message => !string.IsNullOrWhiteSpace(message))));
                return;
            }

            var translatedLines = translationRegions
                .Select((region, index) => new
                {
                    Region = region,
                    Text = normalizedTranslations[index],
                    HasTranslation = TranslationPresentationLayout
                        .HasMeaningfulTranslation(
                            region.Text,
                            normalizedTranslations[index]),
                })
                .ToArray();
            _inlineTranslatedAnnotationRegions = translatedLines
                .Where(item => item.HasTranslation)
                .Select(item => new TranslatedTextAnnotationRegion(
                     new Rect(
                         Math.Max(0, item.Region.X - 3),
                         Math.Max(0, item.Region.Y - 2),
                         Math.Max(20, item.Region.Width + 6),
                         Math.Max(12, item.Region.Height + 4)),
                     item.Text,
                     Math.Clamp(
                         item.Region.EstimatedFontSize > 0
                             ? item.Region.EstimatedFontSize
                             : item.Region.Height / 1.12,
                         TranslationTextLayout.MinimumFontSize,
                         32)))
                .ToArray();
            if (_inlineTranslatedAnnotationRegions.Count == 0)
            {
                _inlineTranslatedTextRegions = translationRegions;
                CaptureStatusMessage = mostlyTargetLanguage
                    ? "识别到的大部分文字已是目标语言或无需翻译的内容，未生成覆盖译文。"
                    : string.IsNullOrWhiteSpace(translation.ErrorMessage)
                        ? "没有生成可覆盖的译文。"
                        : translation.ErrorMessage;
                return;
            }

            var annotationIndex = 0;
            _inlineTranslatedTextRegions = translatedLines
                .SelectMany(line => line.HasTranslation
                    ? TranslationTextLayout.LayoutParagraph(
                            _inlineTranslatedAnnotationRegions[annotationIndex++])
                        .Lines
                    : [line.Region])
                .ToArray();
            var continuedEditing = editVersion != _imageWorkEditVersion || InlineEditorCanvas.IsTextInputActive;
            if (!continuedEditing)
            {
                InlineEditorCanvas.AddTranslationOverlay(
                    _inlineTranslatedAnnotationRegions, replaceExisting: retryIncomplete);
                SetInlineTranslationVisibility(isVisible: true);
            }
            totalTranslationTimer.Stop();
            var translationDetails = string.Join("\n", new[] { translation.Notice, translation.ErrorMessage }
                .Where(message => !string.IsNullOrWhiteSpace(message)));
            var summary = string.IsNullOrWhiteSpace(translation.ErrorMessage)
                ? $"翻译完成 · {totalTranslationTimer.Elapsed.TotalSeconds:F1} 秒 · " +
                    (continuedEditing ? "点击译查看结果，可继续标注" : "悬停查看模型详情")
                : $"部分段落未完成，已保留原文 · {totalTranslationTimer.Elapsed.TotalSeconds:F1} 秒 · 悬停查看补译说明";
            var usageHint = CaptureStatusMessage;
            CaptureStatusMessage = summary;
            CaptureStatusText.ToolTip = new TextBlock
            {
                Text = summary + "（含文字识别与译文排版）\n" + usageHint + "\n" + translationDetails,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            };
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested || _isCompleted)
        {
            return;
        }
        catch
        {
            if (IsImageWorkCurrent(work, generation)) CaptureStatusMessage = "识别或翻译失败，请检查 OCR 与翻译设置。";
        }
        finally
        {
            EndImageWork(work);
            _isTranslationInitializing = false;
            UpdateCaptureActivity();
            CaptureStatusText.Visibility = Visibility.Visible;
            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = !_isActionInProgress;
            }
        }
    }

    private OcrRecognitionResult? _inlineTranslationInput;
    private TranslationSegmentsResult? _inlineTranslationResult;

    private async void OnRetryTranslationClick(object sender, RoutedEventArgs e) =>
        await TranslateInlineTextAsync(retryIncomplete: true);

    private string _captureStatusMessage = string.Empty;
    private bool _captureToolbarLayoutPending;

    private string CaptureStatusMessage
    {
        get => _captureStatusMessage;
        set
        {
            _captureStatusMessage = value ?? string.Empty;
            CaptureStatusText.Text = _captureStatusMessage.ReplaceLineEndings(" ");
            CaptureStatusText.ToolTip = new TextBlock
            {
                Text = _captureStatusMessage,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            };
        }
    }

    private void UpdateCaptureActivity()
    {
        CaptureActivity.IsActive = _isOcrInitializing || _isTranslationInitializing;
        BackgroundCaptureButton.Visibility = CaptureActivity.IsActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCaptureToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Publish exclusions after WPF has measured status/options. Otherwise
        // the native dimming mask covers the newly exposed toolbar area.
        if (_captureToolbarLayoutPending || _isDisposed || !IsVisible ||
            PresentationSource.FromVisual(this) is null) return;
        _captureToolbarLayoutPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _captureToolbarLayoutPending = false;
            if (!_isDisposed && IsVisible && PresentationSource.FromVisual(this) is not null)
                RefreshCaptureToolbarLayoutAfterOptionChange();
        }));
    }

    private void SetInlineTranslationVisibility(bool isVisible)
    {
        if (_inlineTranslatedTextRegions is not { Count: > 0 } translatedRegions)
        {
            return;
        }

        InlineEditorCanvas.SetTranslationOverlayVisible(isVisible);
        if (isVisible)
        {
            ShowSelectableTextOverlay(translatedRegions, isTranslation: true);
            TranslateButtonText.Text = "原";
            UpdateTranslationToolTip("显示原文");
            return;
        }

        if (_inlineOcrResult is { IsSuccess: true, Regions.Count: > 0 } recognition)
        {
            ShowSelectableTextOverlay(recognition.Regions, isTranslation: false);
        }
        else
        {
            OcrTextOverlay.Children.Clear();
            OcrTextOverlay.Visibility = Visibility.Collapsed;
        }

        _isShowingTranslatedText = false;
        TranslateButtonText.Text = "译";
        UpdateTranslationToolTip("显示已缓存的译文");
        CaptureStatusMessage =
            "当前显示原图；可直接选择原文，再次点击译文按钮会立即显示缓存译文。";
        CaptureStatusText.Visibility = Visibility.Visible;
    }

    private void ShowInlineOcrText(OcrRecognitionResult result)
    {
        ShowSelectableTextOverlay(result.Regions, isTranslation: false);
    }

    private void ShowSelectableTextOverlay(
        IReadOnlyList<OcrTextRegion> regions,
        bool isTranslation)
    {
        _isAutomaticOcrOverlay = false;
        if (_inlineEditorImage is null)
        {
            return;
        }

        OcrTextOverlay.Children.Clear();
        var selectionBounds = GetSelectionBounds();
        var scaleX = selectionBounds.Width / _inlineEditorImage.Preview.PixelWidth;
        var scaleY = selectionBounds.Height / _inlineEditorImage.Preview.PixelHeight;
        Canvas.SetLeft(OcrTextOverlay, selectionBounds.X);
        Canvas.SetTop(OcrTextOverlay, selectionBounds.Y);
        OcrTextOverlay.Width = selectionBounds.Width;
        OcrTextOverlay.Height = selectionBounds.Height;

        var orderedRegions = regions
            .Where(region => !string.IsNullOrEmpty(region.Text))
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToArray();
        if (orderedRegions.Length == 0)
        {
            OcrTextOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var accentColor =
            (System.Windows.Application.Current?.TryFindResource("AppAccentBrush") as
                WpfSolidColorBrush)?.Color ?? WpfColor.FromRgb(46, 175, 165);
        var selectableWords = !isTranslation &&
            _inlineOcrResult is { Words.Count: > 0 } recognition
            ? recognition.Words
            : orderedRegions.Select(region => new OcrWordRegion(
                region.Text,
                region.X,
                region.Y,
                region.Width,
                region.Height)).ToArray();
        var textOverlay = new SelectableOcrTextOverlay(
            selectableWords,
            scaleX,
            scaleY,
            accentColor)
        {
            Width = selectionBounds.Width,
            Height = selectionBounds.Height,
        };
        textOverlay.PreviewKeyDown += OnSelectableTextPreviewKeyDown;
        Canvas.SetLeft(textOverlay, 0);
        Canvas.SetTop(textOverlay, 0);
        OcrTextOverlay.Children.Add(textOverlay);

        _isShowingTranslatedText = isTranslation;
        OcrTextOverlay.Visibility = Visibility.Visible;
        CaptureStatusMessage = isTranslation
            ? "译文已覆盖到截图；可直接拖选译文并按 Ctrl+C 复制，复制和保存会包含译文。"
            : "拖选文字后按 Ctrl+C 复制文字；未选中文字时 Ctrl+C 复制截图。";
        CaptureStatusText.Visibility = Visibility.Visible;
    }

    private async void OnSelectableTextPreviewKeyDown(
        object sender,
        WpfKeyEventArgs e)
    {
        if (e.Key != Key.C ||
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            sender is not SelectableOcrTextOverlay textOverlay ||
            string.IsNullOrEmpty(textOverlay.SelectedText))
        {
            return;
        }

        e.Handled = true;
        try
        {
            await ClipboardTextService.SetTextAsync(
                textOverlay.SelectedText);
            CaptureStatusMessage = _isShowingTranslatedText
                ? "已复制所选译文，可在 Win+V 中查看。"
                : "已复制所选识别文字，可在 Win+V 中查看。";
        }
        catch
        {
            CaptureStatusMessage = "复制文字失败，剪贴板可能正被其他程序使用，请重试。";
        }

        CaptureStatusText.Visibility = Visibility.Visible;
    }

    private void ClearInlineOcrText()
    {
        _imageWorkCancellation?.Cancel();
        CancelAutomaticOcr();
        _isAutomaticOcrOverlay = false;
        _automaticRecognitionGeneration++;
        _selectionMessageGeneration++;
        _inlineOcrResult = null;
        _inlineTranslationInput = null;
        _inlineTranslationResult = null;
        RetryTranslationButton.Visibility = Visibility.Collapsed;
        _inlineQrResult = null;
        _inlineQrScan = null;
        _inlineTableResult = null;
        _inlineFormulaResult = null;
        _inlineTranslatedTextRegions = null;
        _inlineTranslatedAnnotationRegions = null;
        _isShowingTranslatedText = false;
        _isUnifiedRecognitionVisible = false;
        TranslateButtonText.Text = "译";
        UpdateTranslationToolTip();
        OcrTextOverlay.Children.Clear();
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        ContentRecognitionOverlay.Children.Clear();
        ContentRecognitionOverlay.Visibility = Visibility.Collapsed;
        ContentRecognitionOverlay.IsHitTestVisible = false;
        SelectionMessageToast.Visibility = Visibility.Collapsed;
    }

    private void RebuildContentRecognitionOverlay()
    {
        ContentRecognitionOverlay.Children.Clear();
        if (!_isUnifiedRecognitionVisible || _inlineEditorImage is null)
        {
            ContentRecognitionOverlay.Visibility = Visibility.Collapsed;
            ContentRecognitionOverlay.IsHitTestVisible = false;
            return;
        }

        var selectionBounds = GetSelectionBounds();
        Canvas.SetLeft(ContentRecognitionOverlay, selectionBounds.X);
        Canvas.SetTop(ContentRecognitionOverlay, selectionBounds.Y);
        ContentRecognitionOverlay.Width = selectionBounds.Width;
        ContentRecognitionOverlay.Height = selectionBounds.Height;

        var resultButtons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };
        if (_inlineQrScan?.WeChatCodeRegion is not null)
        {
            var miniButton = new System.Windows.Controls.Button
            {
                Content = "疑似小程序码 · 复制图片", Height = 28, FontSize = 12,
                Padding = new Thickness(8, 3, 8, 3), ToolTip = QrCodeScanResult.WeChatExplanation,
            };
            miniButton.Click += OnCopyMiniProgramCodeClick;
            resultButtons.Children.Add(miniButton);
        }
        if (_inlineFormulaResult?.IsSuccess == true)
        {
            resultButtons.Children.Add(CreateRecognitionResultButton(
                "公式",
                _inlineFormulaResult,
                "查看并复制 LaTeX",
                copyDirectly: false));
        }
        if (resultButtons.Children.Count > 0)
        {
            Canvas.SetLeft(resultButtons, 8);
            Canvas.SetTop(resultButtons, 8);
            ContentRecognitionOverlay.Children.Add(resultButtons);
        }

        foreach (var qrResult in _inlineQrScan?.Codes ??
            (_inlineQrResult is { IsSuccess: true } singleCode ? [singleCode] : []))
        {
            if (qrResult.Region is not { } region) continue;
            const double markerSize = 22;
            var scaleX = selectionBounds.Width /
                         Math.Max(1, _inlineEditorImage.Preview.PixelWidth);
            var scaleY = selectionBounds.Height /
                         Math.Max(1, _inlineEditorImage.Preview.PixelHeight);
            var marker = new System.Windows.Controls.Button
            {
                Width = markerSize,
                Height = markerSize,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(2),
                Tag = qrResult,
                ToolTip = CreateQrCodeToolTip(qrResult.Content),
            };
            ToolTipService.SetInitialShowDelay(marker, 100);
            ToolTipService.SetShowDuration(marker, 60000);
            marker.SetResourceReference(
                System.Windows.Controls.Control.BackgroundProperty,
                "AppAccentBrush");
            marker.SetResourceReference(
                System.Windows.Controls.Control.BorderBrushProperty,
                "AppPanelBackgroundBrush");
            marker.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 8,
                Direction = 0,
                Opacity = 0.5,
                ShadowDepth = 0,
                Color = WpfColor.FromRgb(0, 20, 24),
            };
            marker.Click += OnQrMarkerClick;
            Canvas.SetLeft(
                marker,
                Math.Clamp(
                    (region.CenterX * scaleX) - (markerSize / 2),
                    0,
                    Math.Max(0, selectionBounds.Width - markerSize)));
            Canvas.SetTop(
                marker,
                Math.Clamp(
                    (region.CenterY * scaleY) - (markerSize / 2),
                    0,
                    Math.Max(0, selectionBounds.Height - markerSize)));
            ContentRecognitionOverlay.Children.Add(marker);
        }

        ContentRecognitionOverlay.Visibility =
            ContentRecognitionOverlay.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        ContentRecognitionOverlay.IsHitTestVisible =
            _inlineQrScan?.HasDetectedContent == true || _inlineQrResult?.IsSuccess == true ||
            _inlineFormulaResult?.IsSuccess == true;
    }

    private System.Windows.Controls.Button CreateRecognitionResultButton(
        string label,
        ContentRecognitionResult result,
        string toolTip,
        bool copyDirectly)
    {
        var button = new System.Windows.Controls.Button
        {
            MinWidth = 52,
            Height = 28,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 3, 10, 3),
            Content = label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Tag = result,
            ToolTip = toolTip,
        };
        button.SetResourceReference(
            System.Windows.Controls.Control.BackgroundProperty,
            "AppPanelBackgroundBrush");
        button.SetResourceReference(
            System.Windows.Controls.Control.BorderBrushProperty,
            "AppAccentBrush");
        button.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            "AppControlForegroundBrush");
        button.Click += copyDirectly
            ? OnTableResultButtonClick
            : OnRecognitionResultButtonClick;
        return button;
    }

    private async void OnTableResultButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button
            {
                Tag: ContentRecognitionResult { IsSuccess: true } result,
            })
        {
            return;
        }

        try
        {
            await ClipboardTextService.SetTextAsync(result.Content);
            CompleteSelection(result: null);
            return;
        }
        catch
        {
            CaptureStatusMessage = "表格复制失败，剪贴板可能正被其他程序使用。";
        }

        CaptureStatusText.Visibility = Visibility.Visible;
        InlineEditorCanvas.Focus();
    }

    private async void OnCopyMiniProgramCodeClick(object sender, RoutedEventArgs e)
    {
        if (_inlineEditorImage is null || _inlineQrScan?.WeChatCodeRegion is not { } region) return;
        try
        {
            var bitmap = _inlineEditorImage.Bitmap;
            var bounds = DrawingRectangle.FromLTRB(
                Math.Max(0, (int)Math.Floor(region.X) - 8), Math.Max(0, (int)Math.Floor(region.Y) - 8),
                Math.Min(bitmap.Width, (int)Math.Ceiling(region.X + region.Width) + 8),
                Math.Min(bitmap.Height, (int)Math.Ceiling(region.Y + region.Height) + 8));
            using var codeImage = new CapturedImage(bitmap.Clone(bounds, PixelFormat.Format32bppArgb));
            await ClipboardImageService.SetImageAsync(codeImage.Preview);
            await ShowSelectionMessageForAsync("已复制图片，可在微信中尝试识别", 1000);
            CompleteSelection(result: null);
        }
        catch
        {
            CaptureStatusMessage = "图片复制失败，请重试。";
            CaptureStatusText.Visibility = Visibility.Visible;
        }
    }

    private static StackPanel CreateQrCodeToolTip(string content)
    {
        var panel = new StackPanel
        {
            MaxWidth = 280,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "二维码",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5),
        });
        panel.Children.Add(new TextBlock
        {
            Text = content,
            MaxWidth = 260,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "单击圆点复制",
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 11,
            Opacity = 0.65,
        });
        return panel;
    }

    private async void OnQrMarkerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button
            {
                Tag: ContentRecognitionResult { IsSuccess: true } result,
            })
        {
            return;
        }

        try
        {
            await ClipboardTextService.SetTextAsync(result.Content);
            CaptureStatusMessage = "已复制二维码内容。";
        }
        catch
        {
            CaptureStatusMessage = "二维码内容复制失败，请重试。";
        }
        CaptureStatusText.Visibility = Visibility.Visible;
    }

    private void OnRecognitionResultButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button
            {
                Tag: ContentRecognitionResult result,
            })
        {
            return;
        }

        var window = new ContentRecognitionWindow(result)
        {
            Owner = this,
            Topmost = true,
        };
        _ = window.ShowDialog();
    }

    private async void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (_options is null)
        {
            return;
        }

        if (!BeginAction())
        {
            return;
        }

        CapturedImage? image = null;
        try
        {
            using var capturedImage = await CaptureCurrentResultAsync(
                restoreOverlay: false);
            // Output scaling belongs to save/copy. Keep the source pixels for
            // pin display, editing, OCR and subsequent zoom operations.
            image = capturedImage.Clone();
            var imageToPin = image;
            image = null;
            _options.PinnedImageManager.Pin(imageToPin, _options.ScreenshotScalePercent,
                AutomaticOcrEnabled && !InlineEditorCanvas.CanUndo ? _inlineOcrResult : null);
        }
        catch
        {
        }
        finally
        {
            image?.Dispose();
            CompleteSelection(result: null);
        }
    }

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        CaptureTimingDiagnostics.Mark("confirm-click");
        if (!BeginAction()) return;
        try { await CompleteCaptureAsync(); }
        catch { ShowCaptureCompletionFailure(); }
    }

    private async Task<BitmapSource?> CompleteCaptureAsync(bool showSuccessNotice = true)
    {
        if (_options is null) { CompleteSelection(result: null); return null; }
        var region = GetPhysicalSelectionBounds();
        using var captured = await CaptureCurrentResultAsync(restoreOverlay: false);
        var output = await CaptureCompletionService.CompleteAsync(captured, _options,
            () => CompleteSelection(result: null));
        if (output is null) return null;
        _options.CaptureCompleted?.Invoke(region);
        CompleteSelection(result: null);
        if (showSuccessNotice) _ = CaptureSuccessWindow.ShowAsync(region);
        return output;
    }

    private void ShowCaptureCompletionFailure()
    {
        CaptureTimingDiagnostics.Mark("confirm-failed");
        const string message = "截图未完成，请检查保存位置或剪贴板后重试。";
        if (_isCompleted || _isDisposed) { _ = StartupFeedbackWindow.ShowAsync(message); return; }
        _isActionInProgress = false;
        CaptureToolbar.IsEnabled = true;
        CaptureStatusMessage = message;
        CaptureStatusText.Visibility = Visibility.Visible;
        CaptureToolbar.UpdateLayout();
        UpdateSelectionControlPositions(GetSelectionBounds());
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CompleteSelection(result: null);
    }

    private void ApplyToolbarFeatureVisibility(
        IEnumerable<CaptureToolbarFeature>? configuredFeatures)
    {
        var features = (configuredFeatures ??
                Enum.GetValues<CaptureToolbarFeature>())
            .Where(Enum.IsDefined)
            .ToHashSet();

        SetVisibility(InlineShapeToolButton, CaptureToolbarFeature.Shape);
        SetVisibility(InlineArrowToolButton, CaptureToolbarFeature.Arrow);
        SetVisibility(InlineEmojiToolButton, CaptureToolbarFeature.Emoji);
        SetVisibility(InlineNumberToolButton, CaptureToolbarFeature.Number);
        SetVisibility(InlineDoubleArrowToolButton, CaptureToolbarFeature.DoubleArrow);
        SetVisibility(InlineLineToolButton, CaptureToolbarFeature.Line);
        SetVisibility(InlineBrushToolButton, CaptureToolbarFeature.Brush);
        SetVisibility(InlineTextToolButton, CaptureToolbarFeature.Text);
        SetVisibility(InlineMosaicToolButton, CaptureToolbarFeature.Mosaic);
        SetVisibility(AspectRatioButton, CaptureToolbarFeature.AspectRatio);
        if (_options is null || _isScrollCaptureSelection) AspectRatioButton.Visibility = Visibility.Collapsed;
        SetVisibility(RecordButton, CaptureToolbarFeature.VideoRecording);
        SetVisibility(SaveButton, CaptureToolbarFeature.Save);
        SetVisibility(ScrollCaptureButton, CaptureToolbarFeature.ScrollCapture);
        SetVisibility(OcrButton, CaptureToolbarFeature.TextRecognition);
        SetVisibility(CopyTableButton, CaptureToolbarFeature.CopyTable);
        SetVisibility(
            CopyRecognizedTextButton,
            CaptureToolbarFeature.CopyRecognizedText);
        SetVisibility(TranslateButton, CaptureToolbarFeature.Translation);
        SetVisibility(
            PrivacyRedactionButton,
            CaptureToolbarFeature.PrivacyRedaction);
        SetVisibility(PinButton, CaptureToolbarFeature.PinImage);
        SetVisibility(WatermarkButton, CaptureToolbarFeature.Watermark);
        if (_options?.Watermark is null) WatermarkButton.Visibility = Visibility.Collapsed;
        SetVisibility(InlineUndoButton, CaptureToolbarFeature.UndoRedo);
        SetVisibility(InlineRedoButton, CaptureToolbarFeature.UndoRedo);

        var editorButtons = new (
            System.Windows.Controls.RadioButton Button,
            EditorTool Tool)[]
        {
            (InlineShapeToolButton, EditorTool.Rectangle),
            (InlineArrowToolButton, EditorTool.Arrow),
            (InlineEmojiToolButton, EditorTool.Emoji),
            (InlineNumberToolButton, EditorTool.Number),
            (InlineDoubleArrowToolButton, EditorTool.DoubleArrow),
            (InlineLineToolButton, EditorTool.Line),
            (InlineBrushToolButton, EditorTool.Brush),
            (InlineTextToolButton, EditorTool.Text),
            (InlineMosaicToolButton, EditorTool.Mosaic),
        };
        var selected = editorButtons
            .FirstOrDefault(item => item.Tool == _selectedInlineTool ||
                item.Tool == EditorTool.Rectangle &&
                    _selectedInlineTool == EditorTool.Ellipse ||
                item.Tool == EditorTool.Arrow &&
                    _selectedInlineTool == EditorTool.CurvedArrow ||
                item.Tool == EditorTool.DoubleArrow && _selectedInlineTool == EditorTool.HollowDoubleArrow ||
                item.Tool == EditorTool.Line && _selectedInlineTool == EditorTool.WavyLine ||
                item.Tool == EditorTool.Mosaic && _selectedInlineTool == EditorTool.RectangleMosaic);
        var selectedButton = selected.Button;
        if (selectedButton is null || selectedButton.Visibility != Visibility.Visible)
        {
            selected = editorButtons.FirstOrDefault(
                item => item.Button.Visibility == Visibility.Visible);
            selectedButton = selected.Button;
        }
        _hasVisibleInlineEditorTools = selectedButton is not null;
        InlineEditorCanvas.SetAnnotationCreationEnabled(
            _hasVisibleInlineEditorTools);
        if (selectedButton is not null)
        {
            _selectedInlineTool = selected.Tool == EditorTool.Rectangle &&
                    _currentShapeToolMode == ShapeToolMode.Ellipse
                ? EditorTool.Ellipse
                : selected.Tool == EditorTool.Arrow &&
                    _currentArrowToolMode == ArrowToolMode.Curved
                    ? EditorTool.CurvedArrow
                    : selected.Tool is EditorTool.DoubleArrow or EditorTool.Line or EditorTool.Mosaic
                        ? AnnotationToolPreferences.GetVariant(selected.Tool) : selected.Tool;
            selectedButton.IsChecked = true;
            UpdateInlineShapeMenuState();
            UpdateInlineArrowButtonPresentation();
            UpdateInlineArrowMenuState();
        }
        else
        {
            InlineShapeToolButton.IsChecked = false;
        }

        UpdateToolbarSeparators();
        return;

        void SetVisibility(
            FrameworkElement element,
            CaptureToolbarFeature feature)
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
            [CaptureToolbarFeature.Shape] = [InlineShapeToolButton],
            [CaptureToolbarFeature.Arrow] = [InlineArrowToolButton],
            [CaptureToolbarFeature.Emoji] = [InlineEmojiToolButton],
            [CaptureToolbarFeature.Number] = [InlineNumberToolButton],
            [CaptureToolbarFeature.DoubleArrow] = [InlineDoubleArrowToolButton],
            [CaptureToolbarFeature.Line] = [InlineLineToolButton],
            [CaptureToolbarFeature.Brush] = [InlineBrushToolButton],
            [CaptureToolbarFeature.Text] = [InlineTextToolButton],
            [CaptureToolbarFeature.Mosaic] = [InlineMosaicToolButton],
            [CaptureToolbarFeature.AspectRatio] = [AspectRatioButton],
            [CaptureToolbarFeature.VideoRecording] = [RecordButton],
            [CaptureToolbarFeature.Save] = [SaveButton],
            [CaptureToolbarFeature.ScrollCapture] = [ScrollCaptureButton],
            [CaptureToolbarFeature.TextRecognition] = [OcrButton],
            [CaptureToolbarFeature.CopyTable] = [CopyTableButton],
            [CaptureToolbarFeature.CopyRecognizedText] =
                [CopyRecognizedTextButton],
            [CaptureToolbarFeature.Translation] = [TranslateButton],
            [CaptureToolbarFeature.PrivacyRedaction] =
                [PrivacyRedactionButton],
            [CaptureToolbarFeature.PinImage] = [PinButton],
            [CaptureToolbarFeature.Watermark] = [WatermarkButton],
            [CaptureToolbarFeature.UndoRedo] =
                [InlineUndoButton, InlineRedoButton],
        };
        var order = (configuredOrder ?? AppSettings.DefaultCaptureToolbarFeatureOrder())
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
            CaptureToolbarFeature.DoubleArrow,
            CaptureToolbarFeature.Line,
            CaptureToolbarFeature.Brush,
            CaptureToolbarFeature.Text,
            CaptureToolbarFeature.Mosaic,
        };
        var actionFeatures = new HashSet<CaptureToolbarFeature>
        {
            CaptureToolbarFeature.Watermark,
            CaptureToolbarFeature.AspectRatio,
            CaptureToolbarFeature.VideoRecording,
            CaptureToolbarFeature.Save,
            CaptureToolbarFeature.ScrollCapture,
            CaptureToolbarFeature.TextRecognition,
            CaptureToolbarFeature.CopyTable,
            CaptureToolbarFeature.CopyRecognizedText,
            CaptureToolbarFeature.Translation,
            CaptureToolbarFeature.PrivacyRedaction,
            CaptureToolbarFeature.PinImage,
        };
        var tokens = new List<FrameworkElement>();
        AddFeatures(annotationFeatures);
        tokens.Add(ToolActionSeparator);
        AddFeatures(actionFeatures);
        tokens.Add(ActionHistorySeparator);
        AddFeatures(new HashSet<CaptureToolbarFeature>
        {
            CaptureToolbarFeature.UndoRedo,
        });
        tokens.Add(HistoryFinishSeparator);
        tokens.Add(CancelButton);
        tokens.Add(ConfirmButton);

        InlineEditorToolsRow1.Children.Clear();
        InlineEditorToolsRow2.Children.Clear();
        RestoreSeparatorLayout();
        if (rowCount != CaptureToolbarRowCount.Two)
        {
            AddToolbarRow(InlineEditorToolsRow1, tokens);
            InlineEditorToolsRow2.Visibility = Visibility.Collapsed;
            return;
        }

        var split = FindToolbarRowSplit(tokens);
        AddToolbarRow(InlineEditorToolsRow1, tokens.Take(split));
        AddToolbarRow(InlineEditorToolsRow2, tokens.Skip(split));
        InlineEditorToolsRow2.Visibility = Visibility.Visible;
        return;

        void AddFeatures(IReadOnlySet<CaptureToolbarFeature> group)
        {
            foreach (var feature in order.Where(group.Contains))
            {
                tokens.AddRange(featureElements[feature]);
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
            InlineShapeToolButton,
            InlineArrowToolButton,
            InlineEmojiToolButton,
            InlineNumberToolButton,
            InlineDoubleArrowToolButton,
            InlineLineToolButton,
            InlineBrushToolButton,
            InlineTextToolButton,
            InlineMosaicToolButton,
        }.Any(IsVisible);
        var hasActions = new FrameworkElement[]
        {
            WatermarkButton,
            RecordButton,
            SaveButton,
            ScrollCaptureButton,
            OcrButton,
            CopyRecognizedTextButton,
            TranslateButton,
            PrivacyRedactionButton,
            PinButton,
        }.Any(IsVisible);
        var hasHistory = IsVisible(InlineUndoButton) || IsVisible(InlineRedoButton);

        ToolActionSeparator.Visibility = hasEditorTools && (hasActions || hasHistory)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ActionHistorySeparator.Visibility = hasActions && hasHistory
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistoryFinishSeparator.Visibility = hasEditorTools || hasActions || hasHistory
            ? Visibility.Visible
            : Visibility.Collapsed;

        static bool IsVisible(FrameworkElement element) =>
            element.Visibility == Visibility.Visible;
    }

    private void SelectVariantTool(EditorTool tool)
    {
        var button = tool is EditorTool.DoubleArrow or EditorTool.HollowDoubleArrow
            ? InlineDoubleArrowToolButton : tool is EditorTool.Line or EditorTool.WavyLine
            ? InlineLineToolButton : InlineMosaicToolButton;
        var alreadyChecked = button.IsChecked == true;
        button.IsChecked = true;
        if (alreadyChecked)
            OnInlineEditorToolSelected(button, new RoutedEventArgs());
    }

    private void OnInlineEditorToolSelected(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (sender is not System.Windows.Controls.RadioButton { Tag: string toolName } ||
            !Enum.TryParse<EditorTool>(toolName, out var tool))
        {
            return;
        }

        CancelAutomaticOcr();
        _hasSelectedInlineTool = true;
        InlineActionAdjustments.Clear();
        _selectedInlineTool = tool;
        if (tool is EditorTool.Rectangle or EditorTool.Ellipse)
        {
            _currentShapeToolMode = tool == EditorTool.Ellipse
                ? ShapeToolMode.Ellipse
                : ShapeToolMode.Rectangle;
            UpdateInlineShapeMenuState(tool);
        }
        else if (tool is EditorTool.Arrow or EditorTool.CurvedArrow)
        {
            _currentArrowToolMode = tool == EditorTool.CurvedArrow
                ? ArrowToolMode.Curved
                : ArrowToolMode.Straight;
            UpdateInlineArrowButtonPresentation(tool);
            UpdateInlineArrowMenuState(tool);
        }
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        UpdateInlineStrokeWidthText(InlineStrokeWidthSlider?.Value ?? 3);

        UpdateInlineToolOptionPanels();
        // Emoji and other option panels change the measured toolbar height.
        // Refresh its position and native mask exclusion so the newly shown
        // rows are not left covered by the selection mask or clipped at the
        // bottom edge of the screen.
        RefreshCaptureToolbarLayoutAfterOptionChange();

        if (InlineEditorCanvas.HasImage)
        {
            InlineEditorCanvas.SetAnnotationCreationEnabled(true);
            InlineEditorCanvas.SelectTool(tool);
            UpdateSelectionMoveZonePositions(GetSelectionBounds());
            ApplyInlineToolStyleState();
            InlineEditorCanvas.Focus();
        }
        else
        {
            ApplyInlineToolStyleState();
        }
        if (tool is EditorTool.Arrow or EditorTool.CurvedArrow)
        {
            _options?.ArrowToolModeChanged?.Invoke(tool == EditorTool.CurvedArrow
                ? ArrowToolMode.Curved
                : ArrowToolMode.Straight);
        }
        _options?.LastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(tool));
    }

    /// <summary>Re-measures the expanded toolbar and republishes its mask exclusion.</summary>
    private void RefreshCaptureToolbarLayoutAfterOptionChange()
    {
        if (_isDisposed ||
            CaptureToolbar.Visibility != Visibility.Visible ||
            !HasValidSelection())
        {
            return;
        }

        var left = Canvas.GetLeft(CaptureToolbar);
        var top = Canvas.GetTop(CaptureToolbar);
        CaptureToolbar.UpdateLayout();
        // Options change the toolbar's height, not the user's chosen anchor.
        // Re-running automatic placement here used to jump across the selection.
        if (double.IsFinite(left) && double.IsFinite(top)) SetCaptureToolbarPosition(left, top);
        else UpdateSelectionControlPositions(GetSelectionBounds());
        if (_nativeSelectionFrame.IsMaskEnabled)
        {
            _nativeSelectionFrame.SetMaskExcludedRegions(
                GetVisibleOverlayScreenBounds());
        }
    }

    private void OnInlineShapeMenuArrowClick(
        object sender,
        RoutedEventArgs e)
    {
        if (InlineShapeToolButton.ContextMenu is not { } menu)
        {
            return;
        }

        UpdateInlineShapeMenuState();
        menu.PlacementTarget = InlineShapeToolButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnInlineArrowMenuArrowClick(
        object sender,
        RoutedEventArgs e)
    {
        if (InlineArrowToolButton.ContextMenu is not { } menu)
        {
            return;
        }

        UpdateInlineArrowMenuState();
        menu.PlacementTarget = InlineArrowToolButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnInlineArrowVariantMenuItemClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string tag })
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

        InlineEditorCanvas.SelectArrowStyle(arrowStyle);
        _currentArrowStyle = arrowStyle;
        _currentArrowToolMode = tool == EditorTool.CurvedArrow
            ? ArrowToolMode.Curved
            : ArrowToolMode.Straight;
        _selectedInlineTool = tool;
        InlineArrowToolButton.Tag = tool.ToString();
        InlineArrowToolButton.IsChecked = true;
        UpdateInlineArrowButtonPresentation(tool);
        UpdateInlineArrowMenuState();
        _options?.ArrowStyleChanged?.Invoke(arrowStyle);
        _options?.ArrowToolModeChanged?.Invoke(tool == EditorTool.CurvedArrow
            ? ArrowToolMode.Curved
            : ArrowToolMode.Straight);
        _options?.LastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(tool));
        if (InlineEditorCanvas.HasImage)
        {
            InlineEditorCanvas.SelectTool(tool);
            ApplyInlineToolStyleState();
        }
        else
        {
            ApplyInlineToolStyleState();
        }
        InlineEditorCanvas.Focus();
    }

    private static void ApplyThemedContextMenu(
        System.Windows.Controls.ContextMenu menu)
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

        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            item.Style = itemStyle;
        }
    }

    private void OnInlineShapeMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string toolName } ||
            !Enum.TryParse<EditorTool>(toolName, out var tool) ||
            tool is not (EditorTool.Rectangle or EditorTool.Ellipse))
        {
            return;
        }

        InlineShapeToolButton.Tag = toolName;
        InlineShapeToolIcon.Data = (System.Windows.Media.Geometry)FindResource(
            tool == EditorTool.Ellipse
                ? "EllipseIconGeometry"
                : "RectangleIconGeometry");
        InlineShapeToolButton.ToolTip = tool == EditorTool.Ellipse
            ? "椭圆"
            : "矩形";
        InlineShapeToolButton.IsChecked = true;
        _selectedInlineTool = tool;
        _currentShapeToolMode = tool == EditorTool.Ellipse
            ? ShapeToolMode.Ellipse
            : ShapeToolMode.Rectangle;
        UpdateInlineShapeMenuState(tool);
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        UpdateInlineStrokeWidthText(InlineStrokeWidthSlider?.Value ?? 3);
        if (InlineEditorCanvas.HasImage)
        {
            InlineEditorCanvas.SelectTool(tool);
            ApplyInlineToolStyleState();
            InlineEditorCanvas.Focus();
        }
        else
        {
            ApplyInlineToolStyleState();
        }
        _options?.ShapeToolModeChanged?.Invoke(tool == EditorTool.Ellipse
            ? ShapeToolMode.Ellipse
            : ShapeToolMode.Rectangle);
        _options?.LastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(tool));
    }

    private void UpdateInlineShapeMenuState(EditorTool? tool = null)
    {
        var selected = tool ?? (_currentShapeToolMode == ShapeToolMode.Ellipse
            ? EditorTool.Ellipse
            : EditorTool.Rectangle);
        InlineRectangleShapeMenuItem.IsChecked = selected == EditorTool.Rectangle;
        InlineEllipseShapeMenuItem.IsChecked = selected == EditorTool.Ellipse;
    }

    private void UpdateInlineArrowMenuState(EditorTool? tool = null)
    {
        var selected = tool ?? (_currentArrowToolMode == ArrowToolMode.Curved
            ? EditorTool.CurvedArrow
            : EditorTool.Arrow);
        InlineStraightFilledArrowMenuItem.IsChecked =
            selected == EditorTool.Arrow && _currentArrowStyle == ArrowStyle.Filled;
        InlineStraightHollowArrowMenuItem.IsChecked =
            selected == EditorTool.Arrow && _currentArrowStyle == ArrowStyle.Hollow;
        InlineCurvedFilledArrowMenuItem.IsChecked =
            selected == EditorTool.CurvedArrow && _currentArrowStyle == ArrowStyle.Filled;
        InlineCurvedHollowArrowMenuItem.IsChecked =
            selected == EditorTool.CurvedArrow && _currentArrowStyle == ArrowStyle.Hollow;
    }

    private void UpdateInlineArrowButtonPresentation(EditorTool? tool = null)
    {
        var selected = tool ?? (_currentArrowToolMode == ArrowToolMode.Curved
            ? EditorTool.CurvedArrow
            : EditorTool.Arrow);
        var isCurved = selected == EditorTool.CurvedArrow;
        InlineArrowToolButton.Tag = selected.ToString();
        var key = (isCurved, _currentArrowStyle) switch
        {
            (false, ArrowStyle.Hollow) => "StraightHollowArrowIconGeometry",
            (true, ArrowStyle.Filled) => "CurvedFilledArrowIconGeometry",
            (true, ArrowStyle.Hollow) => "CurvedHollowArrowIconGeometry",
            _ => "StraightFilledArrowIconGeometry",
        };
        InlineArrowToolIcon.Data = (System.Windows.Media.Geometry)FindResource(
            key);
        var isHollow = _currentArrowStyle == ArrowStyle.Hollow;
        InlineArrowToolIcon.Fill = isHollow ? WpfBrushes.Transparent : null;
        InlineArrowToolIcon.Stroke = isHollow ? null : WpfBrushes.Transparent;
        InlineArrowToolIcon.SetResourceReference(
            isHollow ? System.Windows.Shapes.Path.StrokeProperty : System.Windows.Shapes.Path.FillProperty,
            "EditorToolbarIconBrush");
        InlineArrowToolIcon.StrokeThickness = isHollow ? 1.8 : 0;
        InlineArrowToolButton.ToolTip = string.Concat(
            isCurved ? "弧形" : "直线",
            _currentArrowStyle == ArrowStyle.Hollow ? "空心箭头" : "实心箭头");
    }

    private void UpdateInlineShapeButtonPresentation()
    {
        var isEllipse = _currentShapeToolMode == ShapeToolMode.Ellipse;
        InlineShapeToolButton.Tag = isEllipse
            ? EditorTool.Ellipse.ToString()
            : EditorTool.Rectangle.ToString();
        InlineShapeToolIcon.Data = (System.Windows.Media.Geometry)FindResource(
            isEllipse ? "EllipseIconGeometry" : "RectangleIconGeometry");
        InlineShapeToolButton.ToolTip = isEllipse ? "椭圆" : "矩形";
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
            AnnotationToolMode.Mosaic => AnnotationToolPreferences.GetVariant(EditorTool.Mosaic),
            AnnotationToolMode.RectangleMosaic => EditorTool.RectangleMosaic,
            AnnotationToolMode.DoubleArrow => AnnotationToolPreferences.GetVariant(EditorTool.DoubleArrow),
            AnnotationToolMode.Line => AnnotationToolPreferences.GetVariant(EditorTool.Line),
            AnnotationToolMode.HollowDoubleArrow => EditorTool.HollowDoubleArrow,
            AnnotationToolMode.WavyLine => EditorTool.WavyLine,
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
            EditorTool.RectangleMosaic => AnnotationToolMode.RectangleMosaic,
            EditorTool.DoubleArrow => AnnotationToolMode.DoubleArrow,
            EditorTool.Line => AnnotationToolMode.Line,
            EditorTool.HollowDoubleArrow => AnnotationToolMode.HollowDoubleArrow,
            EditorTool.WavyLine => AnnotationToolMode.WavyLine,
            EditorTool.Text => AnnotationToolMode.Text,
            _ => AnnotationToolMode.Rectangle,
        };

    private System.Windows.Controls.RadioButton GetInlineToolButton(EditorTool tool) => tool switch
    {
        EditorTool.Arrow or EditorTool.CurvedArrow => InlineArrowToolButton,
        EditorTool.Emoji => InlineEmojiToolButton,
        EditorTool.Number => InlineNumberToolButton,
        EditorTool.Brush => InlineBrushToolButton,
        EditorTool.Mosaic or EditorTool.RectangleMosaic => InlineMosaicToolButton,
        EditorTool.DoubleArrow or EditorTool.HollowDoubleArrow => InlineDoubleArrowToolButton,
        EditorTool.Line or EditorTool.WavyLine => InlineLineToolButton,
        EditorTool.Text => InlineTextToolButton,
        _ => InlineShapeToolButton,
    };

    private void UpdateInlineToolOptionPanels()
    {
        if (InlineEmojiPalette is null || InlineStrokeOptions is null)
        {
            return;
        }

        var selected = _hasSelectedInlineTool && _hasVisibleInlineEditorTools;
        InlineEditorOptions.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (!selected)
        {
            InlineToolAdjustments.Clear();
            InlineEmojiPalette.Visibility = Visibility.Collapsed;
            InlineStrokeOptions.Visibility = Visibility.Collapsed;
            if (InlineCustomColorPanel.Visibility == Visibility.Visible) HideInlineCustomColorPanel();
            return;
        }
        var isEmoji = _selectedInlineTool == EditorTool.Emoji;
        if (isEmoji) InlineToolAdjustments.Clear();
        else InlineToolAdjustments.Show(GetInlineToolButton(_selectedInlineTool), () => InlineEditorCanvas,
            number: _selectedInlineTool == EditorTool.Number);
        if (isEmoji && InlineEmojiPanel.Children.Count == 0) PopulateInlineEmojiPalette();
        InlineEmojiPalette.Visibility = isEmoji ? Visibility.Visible : Visibility.Collapsed;
        // Emoji uses the same per-tool size value as the other annotations;
        // keep the px slider visible instead of hiding it with the palette.
        InlineStrokeOptions.Visibility = Visibility.Visible;
        var colorVisibility = isEmoji || _selectedInlineTool is EditorTool.Mosaic or EditorTool.RectangleMosaic
            ? Visibility.Collapsed : Visibility.Visible;
        InlineRedColorButton.Visibility = colorVisibility;
        InlineCyanColorButton.Visibility = colorVisibility;
        InlineDarkColorButton.Visibility = colorVisibility;
        InlineCustomColorButton.Visibility = colorVisibility;
        if (colorVisibility == Visibility.Collapsed) HideInlineCustomColorPanel();
    }

    private void OnInlineAnnotationSelectionChanged(object? sender, EventArgs e)
    {
        UpdateSelectionMoveZonePositions(GetSelectionBounds());
        CaptureStatusMessage = InlineEditorCanvas.HasSelectedAnnotation
            ? "已选中标注：可拖动或缩放，按 Delete 删除。"
            : "可继续编辑当前截图。";
        CaptureStatusText.Visibility = Visibility.Visible;
        if (InlineEditorCanvas.HasSelectedAnnotation)
        {
            ApplyInlineToolStyleState();
        }
    }

    private void OnInlineEmojiClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string sticker } ||
            string.IsNullOrWhiteSpace(sticker))
        {
            return;
        }

        InlineEditorCanvas.SelectEmoji(sticker);
        if (InlineEmojiToolButton.Content is EmojiStickerImage icon) icon.Sticker = sticker;
        InlineEditorCanvas.SelectTool(EditorTool.Emoji);
        InlineEditorCanvas.Focus();
    }

    private void PopulateInlineEmojiPalette()
    {
        foreach (var emoji in Editor.EmojiStickerCatalog.All)
        {
            var button = new System.Windows.Controls.Button
            {
                Tag = emoji,
                ToolTip = emoji,
                Style = (Style)FindResource("InlineEmojiButton"),
                Content = new Editor.EmojiStickerImage
                {
                    Width = 23,
                    Height = 23,
                    Sticker = emoji,
                },
            };
            button.Click += OnInlineEmojiClick;
            InlineEmojiPanel.Children.Add(button);
        }
    }

    private void OnInlineUndoClick(object sender, RoutedEventArgs e)
    {
        InlineEditorCanvas.Undo();
    }

    private void OnInlineRedoClick(object sender, RoutedEventArgs e)
    {
        InlineEditorCanvas.Redo();
    }

    private void OnInlineColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string colorValue } &&
            WpfColorConverter.ConvertFromString(colorValue) is WpfColor color)
        {
            // Preset swatches belong to the active tool.  Do not route them
            // through the custom-color slot: doing so made every preset click
            // highlight the custom button and leaked one tool's color into
            // another tool's palette state.
            InlineEditorCanvas.SelectColor(color);
            UpdateInlineSelectedColorButton((System.Windows.Controls.Button)sender);
            InlineEditorCanvas.Focus();
        }
    }

    private async Task ShowRecognizedTextAsync(OcrRecognitionResult result)
    {
        if (_options?.RecognitionResultPresentation ==
                RecognitionResultPresentationMode.Popup &&
            _options.ShowRecognitionPopupAsync is { } showPopup)
        {
            var text = string.IsNullOrWhiteSpace(result.Text)
                ? string.Join(
                    Environment.NewLine,
                    result.Regions
                        .OrderBy(region => region.Y)
                        .ThenBy(region => region.X)
                        .Select(region => region.Text))
                : result.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                await ShowSelectionMessageForAsync("已识别", 650);
                CompleteSelection(result: null);
                await showPopup("已识别", text.Trim(), null, null);
            }

            return;
        }

        ShowInlineOcrText(result);
    }

    private void OnInlineCustomColorClick(object sender, RoutedEventArgs e)
    {
        ShowInlineCustomColorPanel();
        e.Handled = true;
    }

    private void ShowInlineCustomColorPanel()
    {
        InlineSharedColorPicker.SetState(
            _inlineCustomColor ?? WpfColor.FromRgb(0, 127, 115),
            _inlineCustomColorPalette);
        InlineCustomColorPanel.Visibility = Visibility.Visible;
        InlineCustomColorPanel.UpdateLayout();
        var toolbarY = Canvas.GetTop(CaptureToolbar);
        var panelPosition = ToolbarPopupPlacement.Below(
            new Rect(InlineCustomColorButton.TranslatePoint(new WpfPoint(0, 0), CaptureSurface).X,
                toolbarY, 0, GetCaptureToolbarHeight()), InlineCustomColorPanel.RenderSize,
            new Rect(8, 8, Math.Max(0, CaptureSurface.ActualWidth - 16), Math.Max(0, CaptureSurface.ActualHeight - 16)));
        Canvas.SetLeft(InlineCustomColorPanel, panelPosition.X);
        Canvas.SetTop(InlineCustomColorPanel, panelPosition.Y);
        if (_nativeSelectionFrame.IsMaskEnabled)
            _nativeSelectionFrame.SetMaskExcludedRegions(GetVisibleOverlayScreenBounds());
        UpdateSelectionSizeBadge(GetSelectionBounds());
    }

    private void OnSharedColorCommitted(WpfColor color) =>
        ApplyInlineCustomColor(color);

    private void OnSharedPaletteChanged(int[] colors)
    {
        _inlineCustomColorPalette = NormalizeCustomColorPalette(colors);
        _options?.CustomColorPaletteChanged?.Invoke(
            _inlineCustomColorPalette.ToArray());
    }

    private void OnSharedColorPickerCloseRequested(object? sender, EventArgs e) =>
        HideInlineCustomColorPanel();

    private void HideInlineCustomColorPanel()
    {
        var wasVisible = InlineCustomColorPanel.Visibility == Visibility.Visible;
        InlineCustomColorPanel.Visibility = Visibility.Collapsed;
        if (wasVisible && _nativeSelectionFrame.IsMaskEnabled)
            _nativeSelectionFrame.SetMaskExcludedRegions(GetVisibleOverlayScreenBounds());
        if (_nativeSelectionSize is not null) UpdateSelectionSizeBadge(GetSelectionBounds());
    }

    private bool IsSourceInsideInlineCustomColorPanel(object source)
    {
        return InputElementTree.IsWithin(source as DependencyObject, InlineCustomColorPanel);
    }

    private static WpfColor ColorFromHsv(double hue, double saturation, double value, byte alpha)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60d % 2) - 1));
        var match = value - chroma;
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        return WpfColor.FromArgb(alpha,
            (byte)Math.Round((red + match) * 255),
            (byte)Math.Round((green + match) * 255),
            (byte)Math.Round((blue + match) * 255));
    }

    private static (double Hue, double Saturation, double Value) ColorToHsv(WpfColor color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var hue = delta == 0 ? 0 : maximum == red
            ? 60 * ((green - blue) / delta % 6)
            : maximum == green ? 60 * ((blue - red) / delta + 2)
            : 60 * ((red - green) / delta + 4);
        return (hue < 0 ? hue + 360 : hue,
            maximum == 0 ? 0 : delta / maximum,
            maximum);
    }

    private void ApplyInlineCustomColor(WpfColor color)
    {
        _inlineCustomColor = color;
        InlineCustomColorButton.Background = new WpfSolidColorBrush(color);
        UpdateInlineSelectedColorButton(InlineCustomColorButton);
        InlineEditorCanvas.SelectColor(color);
        InlineEditorCanvas.Focus();
        _options?.CustomStrokeColorChanged?.Invoke(FormatInlineColorText(color));
    }

    private void ApplySavedInlineCustomColor(string? customStrokeColor)
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

        _inlineCustomColor = color;
        var brush = new WpfSolidColorBrush(color);
        brush.Freeze();
        InlineCustomColorButton.Background = brush;
    }

    private static string FormatInlineColorText(WpfColor color)
    {
        return color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static int[] NormalizeCustomColorPalette(IEnumerable<int>? colors)
    {
        return (colors ?? [])
            .Where(color => color is >= 0 and <= 0xFFFFFF)
            .Distinct()
            .Take(16)
            .ToArray();
    }

    private void OnInlineStrokeWidthChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateInlineStrokeWidthText(e.NewValue);

        if (InlineEditorCanvas?.HasImage == true)
        {
            InlineEditorCanvas.SetStrokeWidth(e.NewValue);
        }
    }

    private void UpdateInlineStrokeWidthText(double value)
    {
        if (InlineStrokeWidthText is null)
        {
            return;
        }

        var displayedWidth = _selectedInlineTool == EditorTool.Mosaic
            ? Math.Max(8, value * 4)
            : value;
        InlineStrokeWidthText.Text = _selectedInlineTool == EditorTool.RectangleMosaic
            ? $"{Math.Clamp((int)Math.Round(value * 1.5), 4, 18)} px"
            : $"{displayedWidth:0} px";
        InlineStrokeWidthSlider.ToolTip = _selectedInlineTool == EditorTool.RectangleMosaic
            ? "马赛克颗粒大小" : "线条/笔刷粗细";
    }

    private void UpdateInlineSelectedColorButton(
        System.Windows.Controls.Button selectedButton)
    {
        AnnotationColorSwatch.Update((selectedButton.Background as WpfSolidColorBrush)?.Color ?? InlineEditorCanvas.CurrentSelectedColor,
            InlineCustomColorButton, InlineRedColorButton, InlineCyanColorButton, InlineDarkColorButton);
    }

    private void OnInlineEditorHistoryChanged(object? sender, EventArgs e)
    {
        _imageWorkEditVersion++;
        if (_hasSelectedInlineTool && _selectedInlineTool == EditorTool.Number) UpdateInlineToolOptionPanels();
        InlineUndoButton.IsEnabled = InlineEditorCanvas.CanUndo;
        InlineRedoButton.IsEnabled = InlineEditorCanvas.CanRedo;
        var hasTranslationOverlay = InlineEditorCanvas.HasTranslationOverlay;
        if (hasTranslationOverlay == _lastObservedTranslationOverlayExists)
        {
            return;
        }

        _lastObservedTranslationOverlayExists = hasTranslationOverlay;
        SynchronizeInlineTranslationPresentation();
    }

    private void SynchronizeInlineTranslationPresentation()
    {
        if (_inlineTranslatedTextRegions is { Count: > 0 } translatedRegions &&
            InlineEditorCanvas.IsTranslationOverlayVisible)
        {
            ShowSelectableTextOverlay(translatedRegions, isTranslation: true);
            TranslateButtonText.Text = "原";
            UpdateTranslationToolTip("显示原文");
            return;
        }

        if (_inlineOcrResult is { IsSuccess: true, Regions.Count: > 0 } recognition)
        {
            ShowSelectableTextOverlay(recognition.Regions, isTranslation: false);
        }
        else
        {
            OcrTextOverlay.Children.Clear();
            OcrTextOverlay.Visibility = Visibility.Collapsed;
            _isShowingTranslatedText = false;
        }

        TranslateButtonText.Text = "译";
        UpdateTranslationToolTip(_inlineTranslatedTextRegions is { Count: > 0 }
            ? "显示已缓存的译文"
            : "翻译");
    }

    private bool BeginAction()
    {
        if (_isCompleted || _isActionInProgress || !HasValidSelection())
        {
            return false;
        }

        _isActionInProgress = true;
        CaptureToolbar.IsEnabled = false;
        return true;
    }

    private async Task<CapturedImage> CaptureCurrentSelectionAsync(
        bool restoreOverlay = true)
    {
        var selection = GetPhysicalSelectionBounds();

        if (_screenSnapshot is not null)
        {
            var sourceRectangle = new System.Drawing.Rectangle(
                selection.X - _virtualScreenBounds.X,
                selection.Y - _virtualScreenBounds.Y,
                selection.Width,
                selection.Height);
            var bitmap = _screenSnapshot.Bitmap.Clone(
                sourceRectangle,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            return new CapturedImage(bitmap, selection);
        }

        Hide();

        try
        {
            await Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.Render);
            // The overlay is already hidden and the render turn above lets
            // DWM retire it. A long fixed delay here exposes the live desktop
            // and is visible as a flash on the cold/fallback capture path.
            await Task.Delay(16);
            return ScreenCaptureService.Capture(selection);
        }
        finally
        {
            if (restoreOverlay && !_isCompleted)
            {
                Show();
                Activate();
                CaptureSurface.Focus();
                // Hiding the WPF owner also hides every owned native HWND.
                // Re-issue the full native update here so the frame, mask and
                // size badge are all shown again together on the next frame.
                if (HasValidSelection())
                {
                    UpdateNativeSelectionVisuals(GetSelectionBounds());
                }
            }
        }
    }

    private async Task<CapturedImage> CaptureCurrentResultAsync(
        bool restoreOverlay)
    {
        if (_options?.Watermark is not null && InlineEditorCanvas.HasImage)
        {
            InitializeWatermark();
            await _watermarkTask;
        }
        if (InlineEditorCanvas.HasImage)
        {
            var rendered = InlineEditorCanvas.RenderEditedImage();
            var source = GetPhysicalSelectionBounds();
            return await Task.Run(() => CapturedImage.FromBitmapSource(rendered, source));
        }

        var image = await CaptureCurrentSelectionAsync(restoreOverlay);
        if (_options?.Watermark is not { AutoApply: true } watermark || (_watermarkInitialized && !_watermarkEnabled)) return image;
        try
        {
            _watermarkCancellation ??= new CancellationTokenSource();
            var result = await new Screenshot.App.Watermarks.WatermarkLoader().LoadAsync(
                watermark, _watermarkTimestamp, _watermarkCancellation.Token);
            return CapturedImage.FromBitmapSource(result.Composition.RenderOn(image.Preview), image.SourceRegion);
        }
        finally { image.Dispose(); }
    }

    private void QueueSelectionBoundsUpdate(Rect requestedBounds)
    {
        if (_isDisposed ||
            _windowHandle == IntPtr.Zero ||
            !IsVisible)
        {
            _pendingSelectionBounds = requestedBounds;
            FlushSelectionBoundsUpdate();
            return;
        }

        if (Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // During an active native drag the border, outside mask, and optional
        // size badge are updated by NativeSelectionFrameWindow. Scheduling a
        // WPF render pass here would rebuild hidden layout and can stall the
        // video/translation selection path behind the compositor.
        if ((_isSelecting || _isMovingSelection || _isResizeDragging) &&
            _nativeSelectionFrame.IsHandleCreated)
        {
            _pendingSelectionBounds = null;
            return;
        }

        _pendingSelectionBounds = requestedBounds;

        // Do not queue this at Render priority. That priority runs before the
        // next input event, so a high-polling-rate mouse would still force a
        // complete layout pass for every raw sample. CompositionTarget renders
        // once per display frame and therefore presents only the latest point.
        if (_selectionBoundsUpdateScheduled)
        {
            _selectionIdleTimer.Stop();
            _selectionIdleTimer.Start();
            return;
        }

        _selectionBoundsUpdateScheduled = true;
        _selectionIdleTimer.Stop();
        _selectionIdleTimer.Start();
        System.Windows.Media.CompositionTarget.Rendering +=
            OnSelectionPreviewRendering;
    }

    private void OnSelectionPreviewRendering(object? sender, EventArgs e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _lastSelectionPreviewTimestamp <
            System.Diagnostics.Stopwatch.Frequency / _selectionPreviewHz)
        {
            return;
        }

        _lastSelectionPreviewTimestamp = now;
        Rect? bounds = null;

        // WPF mouse events can queue behind layout work on high-polling-rate
        // devices. Read the native cursor at render time so the preview uses
        // the latest physical position instead of the last queued event.
        if (_isSelecting && !_isWindowSnapClickPending)
        {
            var cursor = WinForms.Cursor.Position;
            if (TryGetCaptureSurfacePoint(cursor, out var currentPoint))
            {
                bounds = new Rect(_selectionStartPoint, currentPoint);
            }
        }
        else if (_isMovingSelection)
        {
            var cursor = WinForms.Cursor.Position;
            if (TryGetCaptureSurfacePoint(cursor, out var currentPoint))
            {
                var delta = currentPoint - _dragStartPoint;
                bounds = ClampMovedBoundsToSurface(new Rect(
                    _dragStartBounds.X + delta.X,
                    _dragStartBounds.Y + delta.Y,
                    _dragStartBounds.Width,
                    _dragStartBounds.Height));
            }
        }
        else if (_isResizeDragging && TryGetResizePreviewBounds(out var resizeBounds))
        {
            bounds = resizeBounds;
        }
        else if (_pendingSelectionBounds is { } pending)
        {
            bounds = pending;
        }

        _pendingSelectionBounds = null;
        if (bounds is { } nextBounds)
        {
            var applyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplySelectionPreviewBounds(nextBounds);
            AdjustSelectionPreviewRate(
                System.Diagnostics.Stopwatch.GetTimestamp() - applyStarted);
        }

        if (!_isSelecting && !_isMovingSelection && !_isResizeDragging &&
            _pendingSelectionBounds is null)
        {
            StopSelectionPreviewRendering();
        }
    }

    private void OnInlineContextMenuClosed(
        object? sender,
        RoutedEventArgs e)
    {
        if (!_nativeSelectionFrame.IsMaskEnabled || !HasValidSelection() ||
            Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // ContextMenu is a separate popup HWND. Reassert the native mask after
        // the popup leaves so a z-order change cannot make the dimming layer
        // disappear until the next drag update.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                if (!_isCompleted && _nativeSelectionFrame.IsMaskEnabled &&
                    HasValidSelection())
                {
                    UpdateNativeSelectionVisuals(GetSelectionBounds());
                }
            }));
    }

    private void ApplyInlineToolStyleState()
    {
        var color = InlineEditorCanvas.HasImage
            ? InlineEditorCanvas.CurrentSelectedColor
            : AnnotationToolPreferences.GetColor(
                _selectedInlineTool,
                WpfColor.FromRgb(214, 69, 69));
        var width = InlineEditorCanvas.HasImage
            ? InlineEditorCanvas.SelectedAnnotationStrokeWidth ??
              InlineEditorCanvas.CurrentStrokeWidth
            : AnnotationToolPreferences.GetWidth(_selectedInlineTool, 3);
        InlineStrokeWidthSlider.Value = width;
        UpdateInlineStrokeWidthText(width);
        var selected = color == WpfColor.FromRgb(214, 69, 69)
            ? InlineRedColorButton
            : color == WpfColor.FromRgb(0, 127, 115)
                ? InlineCyanColorButton
                : color == WpfColor.FromRgb(37, 49, 58)
                    ? InlineDarkColorButton
                    : InlineCustomColorButton;

        // The custom swatch is also the visual fallback for per-tool colors
        // that are not one of the three presets.  Keep its fill synchronized
        // with the active tool so switching tools always shows the color that
        // will be used next, rather than the last globally edited custom color.
        if (ReferenceEquals(selected, InlineCustomColorButton))
        {
            var brush = new WpfSolidColorBrush(color);
            brush.Freeze();
            InlineCustomColorButton.Background = brush;
            InlineCustomColorButton.ToolTip = $"自定义颜色 {FormatInlineColorText(color)}";
            _inlineCustomColor = color;
        }
        UpdateInlineSelectedColorButton(selected);
    }

    private bool TryGetCaptureSurfacePoint(System.Drawing.Point screenPoint,
        out WpfPoint point)
    {
        point = default;
        if (_isDisposed || !CaptureSurface.IsVisible ||
            PresentationSource.FromVisual(CaptureSurface) is null)
        {
            return false;
        }

        try
        {
            point = CaptureSurface.PointFromScreen(
                new WpfPoint(screenPoint.X, screenPoint.Y));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ResetSelectionPreviewRate()
    {
        _selectionPreviewHz = _interactionRefreshRate;
        _lastSelectionPreviewTimestamp = 0;
        _lastSelectionBadgeTimestamp = 0;
    }

    private void OnSelectionIdleTimerTick(object? sender, EventArgs e)
    {
        _selectionIdleTimer.Stop();
        if (_isDisposed || _isResizeDragging ||
            (!_isSelecting && !_isMovingSelection) ||
            _lastRenderedSelectionBounds is not { } bounds)
        {
            return;
        }

        // The drag path only updates the border, mask, and size badge. Apply
        // the remaining control/editor layout once the pointer has been idle.
        UpdateSelectionBounds(bounds);
    }

    private void AdjustSelectionPreviewRate(long applyTicks)
    {
        // Keep the producer cadence fixed at the requested 1ms. Expensive
        // visual work must be reduced or moved off-thread, not compensated by
        // silently dropping the pointer update rate back to a display-derived
        // value.
        _ = applyTicks;
        _selectionPreviewHz = _interactionRefreshRate;
    }

    private void ClearInlineToolSelection()
    {
        _hasSelectedInlineTool = false;
        InlineShapeToolButton.IsChecked = false;
        InlineArrowToolButton.IsChecked = false;
        InlineEmojiToolButton.IsChecked = false;
        InlineNumberToolButton.IsChecked = false;
        InlineBrushToolButton.IsChecked = false;
        InlineTextToolButton.IsChecked = false;
        InlineDoubleArrowToolButton.IsChecked = false;
        InlineLineToolButton.IsChecked = false;
        InlineMosaicToolButton.IsChecked = false;
        // Entering the editor starts with no active annotation tool. Existing
        // annotations remain selectable, but dragging empty canvas must not
        // silently create a rectangle until the user chooses a tool.
        InlineEditorCanvas.SetAnnotationCreationEnabled(false);
        UpdateInlineToolOptionPanels();
    }

    private void StopSelectionPreviewRendering()
    {
        if (!_selectionBoundsUpdateScheduled)
        {
            return;
        }

        _selectionBoundsUpdateScheduled = false;
        System.Windows.Media.CompositionTarget.Rendering -=
            OnSelectionPreviewRendering;
    }

    private void FlushSelectionBoundsUpdate()
    {
        StopSelectionPreviewRendering();
        if (_pendingSelectionBounds is not { } bounds)
        {
            return;
        }

        _pendingSelectionBounds = null;
        if (_isDisposed || Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        UpdateSelectionBounds(bounds);
    }

    private void ApplySelectionPreviewBounds(Rect requestedBounds)
    {
        var bounds = ClampBoundsToSurface(NormalizeBounds(requestedBounds));
        if (_lastRenderedSelectionBounds is { } previous &&
            previous == bounds)
        {
            return;
        }

        _lastRenderedSelectionBounds = bounds;
        // During an active drag the dedicated native tracker owns the border.
        // Do not write it again from the WPF render callback: that callback can
        // be one or more input samples behind and would overwrite a newer
        // native position, which is visible as a trailing rectangle.
        if (_isSelecting || _isMovingSelection || _isResizeDragging)
        {
            SetNativeSelectionVisualSuppressed(true);
        }
        Canvas.SetLeft(SelectionRectangle, bounds.X);
        Canvas.SetTop(SelectionRectangle, bounds.Y);
        SelectionRectangle.Width = bounds.Width;
        SelectionRectangle.Height = bounds.Height;

        // During the initial drag the handles and toolbar are hidden. Avoid
        // moving those invisible controls for every rendered frame; they are
        // positioned once when the completed selection exposes them.
        if (!_isSelecting && !_isMovingSelection && !_isResizeDragging &&
            (TopLeftResizeThumb.Visibility == Visibility.Visible ||
            CaptureToolbar.Visibility == Visibility.Visible)
           )
        {
            UpdateSelectionControlPositions(bounds);
        }

        // Keep the lightweight size badge in sync with the live border.
        // Text measurement/layout is substantially more expensive than the
        // border and mask updates. Refresh the badge on completed bounds;
        // keeping it out of the hot drag path avoids UI-thread stalls.
        var badgeInterval = System.Diagnostics.Stopwatch.Frequency / 30;
        if ((!_isSelecting && !_isMovingSelection) ||
            System.Diagnostics.Stopwatch.GetTimestamp() -
                _lastSelectionBadgeTimestamp >= badgeInterval)
        {
            UpdateSelectionSizeBadge(bounds);
            _lastSelectionBadgeTimestamp =
                System.Diagnostics.Stopwatch.GetTimestamp();
        }
        if (InlineEditorCanvas.HasImage)
        {
            Canvas.SetLeft(InlineEditorOutline, bounds.X);
            Canvas.SetTop(InlineEditorOutline, bounds.Y);
            InlineEditorOutline.Width = bounds.Width;
            InlineEditorOutline.Height = bounds.Height;
            if (!_isSelectionAdjustmentInProgress &&
                !_isMovingSelection &&
                !_isResizeDragging)
            {
                InlineEditorOutline.Visibility = Visibility.Visible;
            }
            Canvas.SetLeft(OcrTextOverlay, bounds.X);
            Canvas.SetTop(OcrTextOverlay, bounds.Y);
            Canvas.SetLeft(ContentRecognitionOverlay, bounds.X);
            Canvas.SetTop(ContentRecognitionOverlay, bounds.Y);

            if (!_isSelectionAdjustmentInProgress)
            {
                Canvas.SetLeft(InlineEditorCanvas, bounds.X);
                Canvas.SetTop(InlineEditorCanvas, bounds.Y);
            }
        }
    }

    private void UpdateSelectionBounds(Rect requestedBounds)
    {
        var bounds = ClampBoundsToSurface(NormalizeBounds(requestedBounds));
        _lastRenderedSelectionBounds = bounds;
        var hasArea = bounds.Width >= MinimumSelectionEdge &&
                      bounds.Height >= MinimumSelectionEdge;
        if (hasArea && SelectionRectangle.Visibility == Visibility.Visible)
        {
            // Convert the requested bounds directly. Reading the WPF
            // rectangle here would return the previous frame because the
            // Canvas coordinates are assigned below.
            var physicalBounds = GetPhysicalSelectionBoundsForBounds(bounds);
            _nativeSelectionFrame.Update(ToDrawingRectangle(physicalBounds));
            SetNativeSelectionVisualSuppressed(true);
        }
        else if (!hasArea)
        {
            // A fresh drag starts with a zero-area selection. Keep the native
            // full-screen mask visible while hiding only the border; hiding
            // both here causes a visible mask switch on the first sample.
            if (_isSelecting && _nativeSelectionFrame.IsMaskEnabled)
            {
                _nativeSelectionFrame.HideBorderOnly();
            }
            else
            {
                _nativeSelectionFrame.Hide();
            }
        }
        if (_isColorPickerActive &&
            bounds.Width >= MinimumSelectionEdge &&
            bounds.Height >= MinimumSelectionEdge)
        {
            ExitColorPicker();
        }

        Canvas.SetLeft(SelectionRectangle, bounds.X);
        Canvas.SetTop(SelectionRectangle, bounds.Y);
        SelectionRectangle.Width = bounds.Width;
        SelectionRectangle.Height = bounds.Height;

        UpdateSelectionControlPositions(bounds);
        UpdateSelectionSizeBadge(bounds);
        if (_isScrollCaptureSelectionPublished)
        {
            UpdateScrollCaptureOutline(bounds);
        }

        if (InlineEditorCanvas.HasImage)
        {
            Canvas.SetLeft(InlineEditorOutline, bounds.X);
            Canvas.SetTop(InlineEditorOutline, bounds.Y);
            InlineEditorOutline.Width = bounds.Width;
            InlineEditorOutline.Height = bounds.Height;
            if (!_isSelectionAdjustmentInProgress)
            {
                Canvas.SetLeft(InlineEditorCanvas, bounds.X);
                Canvas.SetTop(InlineEditorCanvas, bounds.Y);
                Canvas.SetLeft(OcrTextOverlay, bounds.X);
                Canvas.SetTop(OcrTextOverlay, bounds.Y);
                OcrTextOverlay.Width = bounds.Width;
                OcrTextOverlay.Height = bounds.Height;
                Canvas.SetLeft(ContentRecognitionOverlay, bounds.X);
                Canvas.SetTop(ContentRecognitionOverlay, bounds.Y);
                ContentRecognitionOverlay.Width = bounds.Width;
                ContentRecognitionOverlay.Height = bounds.Height;
            }
        }

        PositionSelectionMessageToast();

        if (_isScrollCaptureSelectionPublished &&
            !_isScrollCaptureSelectionLocked &&
            HasValidSelection())
        {
            if (_publishedScrollCaptureSelection?.TryGetTarget(
                    out var scrollSelection) == true)
            {
                scrollSelection.UpdateCaptureRegion(
                    GetPhysicalSelectionBounds());
            }
        }
    }

    private void UpdateScrollCaptureOutline(Rect bounds)
    {
        var border = ScrollCaptureBorderGeometry.GetBounds(
            ToDrawingRectangle(GetPhysicalSelectionBoundsForBounds(bounds)), ToDrawingRectangle(_virtualScreenBounds));
        var topLeft = CaptureSurface.PointFromScreen(new WpfPoint(border.Left, border.Top));
        var bottomRight = CaptureSurface.PointFromScreen(new WpfPoint(border.Right, border.Bottom));
        ScrollCaptureOutline.Width = Math.Max(0, bottomRight.X - topLeft.X);
        ScrollCaptureOutline.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
        Canvas.SetLeft(ScrollCaptureOutline, topLeft.X);
        Canvas.SetTop(ScrollCaptureOutline, topLeft.Y);
    }

    private void UpdateResizeThumbPositions(Rect bounds)
    {
        SetControlPosition(TopLeftResizeThumb, bounds.X, bounds.Y);
        SetControlPosition(TopRightResizeThumb, bounds.Right, bounds.Y);
        SetControlPosition(BottomLeftResizeThumb, bounds.X, bounds.Bottom);
        SetControlPosition(BottomRightResizeThumb, bounds.Right, bounds.Bottom);
        SetControlPosition(TopResizeThumb, bounds.X + (bounds.Width / 2), bounds.Y);
        SetControlPosition(LeftResizeThumb, bounds.X, bounds.Y + (bounds.Height / 2));
        SetControlPosition(RightResizeThumb, bounds.Right, bounds.Y + (bounds.Height / 2));
        SetControlPosition(BottomResizeThumb, bounds.X + (bounds.Width / 2), bounds.Bottom);
    }

    private void UpdateSelectionMoveZonePositions(Rect bounds)
    {
        var canExposeMoveZones = bounds.Width >= (MoveZoneInset * 2 + 18) &&
            bounds.Height >= (MoveZoneInset * 2 + 18);
        var zones = new[]
        {
            (SelectionMoveTopLeftZone, bounds.Left + MoveZoneInset, bounds.Top + MoveZoneInset),
            (SelectionMoveTopRightZone, bounds.Right - MoveZoneInset, bounds.Top + MoveZoneInset),
            (SelectionMoveBottomLeftZone, bounds.Left + MoveZoneInset, bounds.Bottom - MoveZoneInset),
            (SelectionMoveBottomRightZone, bounds.Right - MoveZoneInset, bounds.Bottom - MoveZoneInset),
        };

        foreach (var (zone, centerX, centerY) in zones)
        {
            SetControlPosition(zone, centerX, centerY);
            zone.Visibility = canExposeMoveZones &&
                CanMoveSelection() &&
                !_resizeControlsSuppressed
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void UpdateSelectionControlPositions(Rect bounds)
    {
        PositionImageQuestionButton(bounds);
        UpdateResizeThumbPositions(bounds);

        if (_hasCustomToolbarPosition)
        {
            SetCaptureToolbarPosition(
                _toolbarPositionXRatio * GetToolbarMaximumX(),
                _toolbarPositionYRatio * GetToolbarMaximumY());
            return;
        }

        var toolbarX = CalculateAutomaticToolbarX(
            bounds,
            GetCaptureToolbarWidth(),
            CaptureSurface.ActualWidth);
        var toolbarY = bounds.Bottom + 10;

        if (toolbarY + GetCaptureToolbarHeight() > CaptureSurface.ActualHeight)
        {
            toolbarY = Math.Max(0, bounds.Y - GetCaptureToolbarHeight() - 10);
        }

        SetCaptureToolbarPosition(toolbarX, toolbarY);
    }

    internal static double CalculateAutomaticToolbarX(
        Rect selectionBounds,
        double toolbarWidth,
        double surfaceWidth)
    {
        var maximumX = Math.Max(0, surfaceWidth - toolbarWidth);
        return Math.Clamp(
            selectionBounds.X + ((selectionBounds.Width - toolbarWidth) / 2),
            0,
            maximumX);
    }

    private void OnCaptureToolbarSurfaceMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            !ToolbarDragInteraction.IsBlankSurface(
                e.OriginalSource as DependencyObject,
                CaptureToolbar))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            ResetCaptureToolbarPosition();
            e.Handled = true;
            return;
        }

        HideInlineCustomColorPanel();
        _isToolbarSurfaceDragging = true;
        _hasCustomToolbarPosition = true;
        _toolbarSurfaceDragStart = e.GetPosition(CaptureSurface);
        _toolbarSurfaceStartPosition = new WpfPoint(
            double.IsFinite(Canvas.GetLeft(CaptureToolbar))
                ? Canvas.GetLeft(CaptureToolbar)
                : 0,
            double.IsFinite(Canvas.GetTop(CaptureToolbar))
                ? Canvas.GetTop(CaptureToolbar)
                : 0);
        _ = CaptureToolbar.CaptureMouse();
        e.Handled = true;
    }

    private void OnCaptureToolbarSurfaceMouseMove(
        object sender,
        WpfMouseEventArgs e)
    {
        if (!_isToolbarSurfaceDragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishCaptureToolbarSurfaceDrag();
            return;
        }

        var current = e.GetPosition(CaptureSurface);
        SetCaptureToolbarPosition(
            _toolbarSurfaceStartPosition.X +
            current.X - _toolbarSurfaceDragStart.X,
            _toolbarSurfaceStartPosition.Y +
            current.Y - _toolbarSurfaceDragStart.Y);
        e.Handled = true;
    }

    private void OnCaptureToolbarSurfaceMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isToolbarSurfaceDragging)
        {
            return;
        }

        FinishCaptureToolbarSurfaceDrag();
        e.Handled = true;
    }

    private void FinishCaptureToolbarSurfaceDrag()
    {
        _isToolbarSurfaceDragging = false;
        if (CaptureToolbar.IsMouseCaptured)
        {
            CaptureToolbar.ReleaseMouseCapture();
        }

        SaveCaptureToolbarPosition();
    }

    private void SaveCaptureToolbarPosition()
    {
        var maximumX = GetToolbarMaximumX();
        var maximumY = GetToolbarMaximumY();
        var left = Canvas.GetLeft(CaptureToolbar);
        var top = Canvas.GetTop(CaptureToolbar);
        _toolbarPositionXRatio = maximumX > 0
            ? Math.Clamp(left / maximumX, 0, 1)
            : 0;
        _toolbarPositionYRatio = maximumY > 0
            ? Math.Clamp(top / maximumY, 0, 1)
            : 0;
        _options?.ToolbarPositionChanged?.Invoke(
            _toolbarPositionXRatio,
            _toolbarPositionYRatio);
    }

    private void ResetCaptureToolbarPosition()
    {
        _hasCustomToolbarPosition = false;
        _toolbarPositionXRatio = -1;
        _toolbarPositionYRatio = -1;
        _options?.ToolbarPositionChanged?.Invoke(-1, -1);
        UpdateSelectionControlPositions(GetSelectionBounds());
    }

    private double GetToolbarMaximumX() => Math.Max(
        0,
        CaptureSurface.ActualWidth - GetCaptureToolbarWidth());

    private double GetToolbarMaximumY() => Math.Max(
        0,
        CaptureSurface.ActualHeight - GetCaptureToolbarHeight());

    private double GetCaptureToolbarWidth() =>
        CaptureToolbar.ActualWidth * _toolbarScale;

    private double GetCaptureToolbarHeight() =>
        CaptureToolbar.ActualHeight * _toolbarScale;

    private void SetCaptureToolbarPosition(double x, double y)
    {
        Canvas.SetLeft(CaptureToolbar, Math.Clamp(x, 0, GetToolbarMaximumX()));
        Canvas.SetTop(CaptureToolbar, Math.Clamp(y, 0, GetToolbarMaximumY()));
        if (CaptureToolbar.Visibility == Visibility.Visible)
            CaptureToolbar.UpdateLayout();
        if (_nativeSelectionFrame.IsMaskEnabled &&
            CaptureToolbar.Visibility == Visibility.Visible)
        {
            _nativeSelectionFrame.SetMaskExcludedRegions(
                GetVisibleOverlayScreenBounds());
        }
        UpdateSelectionSizeBadge(GetSelectionBounds());
    }

    private static void SetControlPosition(FrameworkElement control, double centerX, double centerY)
    {
        var width = double.IsNaN(control.Width) ? control.ActualWidth : control.Width;
        var height = double.IsNaN(control.Height) ? control.ActualHeight : control.Height;
        Canvas.SetLeft(control, centerX - (width / 2));
        Canvas.SetTop(control, centerY - (height / 2));
    }

    private void ShowSelectionControls()
    {
        if (_isScrollCaptureSelectionLocked) return;
        // A native move/resize owns the live pixels. Any delayed WPF layout or
        // completion callback must not re-show stale handles or the toolbar
        // until the native gesture has released.
        if (_isSelecting || _isMovingSelection || _isResizeDragging ||
            _resizeControlsSuppressed)
        {
            return;
        }

        _windowSnapTimer.Stop();
        HideWindowSnap();
        // Keep the native frame and mask as the single visual source for an
        // active selection. The WPF rectangle remains hit-testable for moving
        // the region, but is transparent so it cannot leave a second border
        // or a stale mask behind.
        if (_nativeSelectionFrame.IsMaskEnabled)
        {
            _nativeSelectionFrame.SetSizeEnabled(ShouldShowSelectionSize);
            SetNativeSelectionVisualSuppressed(true);
        }
        else
        {
            _nativeSelectionFrame.HideBorderKeepMask();
        }
        SelectionRectangle.Visibility = Visibility.Visible;
        // Restore the transparent move surface after a completed selection.
        // Edited content keeps it disabled so OCR/text controls continue to
        // receive interior clicks; the preview handler still owns the border.
        SelectionRectangle.IsHitTestVisible = CanMoveSelection();
        UpdateSelectionMoveZonePositions(GetSelectionBounds());
        if (_nativeSelectionFrame.IsMaskEnabled)
        {
            SelectionRectangle.Fill = WpfBrushes.Transparent;
        }
        TopLeftResizeThumb.Visibility = Visibility.Visible;
        TopRightResizeThumb.Visibility = Visibility.Visible;
        BottomLeftResizeThumb.Visibility = Visibility.Visible;
        BottomRightResizeThumb.Visibility = Visibility.Visible;
        TopResizeThumb.Visibility = Visibility.Visible;
        LeftResizeThumb.Visibility = Visibility.Visible;
        RightResizeThumb.Visibility = Visibility.Visible;
        BottomResizeThumb.Visibility = Visibility.Visible;
        // Long-capture selection uses the separate progress/preview window;
        // never materialize the normal annotation toolbar on that overlay.
        CaptureToolbar.Visibility = _isScrollCaptureSelection
            ? Visibility.Collapsed
            : Visibility.Visible;
        CaptureToolbar.Opacity = 0;
        if (!_isScrollCaptureSelection)
        {
            CaptureToolbar.UpdateLayout();
        }
        if (_nativeSelectionFrame.IsMaskEnabled)
        {
            _nativeSelectionFrame.SetMaskExclusionsSuspended(true);
            _nativeSelectionFrame.SetMaskExcludedRegions(null);
        }
        UpdateSelectionControlPositions(GetSelectionBounds());
        // Keep the native frame/size windows as the last visual update after
        // WPF has laid out the controls. This prevents a WPF z-order/layout
        // pass from leaving only the handles visible.
        if (_nativeSelectionFrame.IsMaskEnabled && HasValidSelection() &&
            !_isScrollCaptureSelection)
        {
            ScheduleToolbarMaskPublication(GetSelectionBounds());
        }
        else
        {
            CaptureToolbar.Opacity = 1;
        }

        if (TryGetCaptureSurfacePoint(WinForms.Cursor.Position, out var cursorPoint))
        {
            UpdateSelectionInteractionCursor(cursorPoint);
        }
    }

    private void LockSelectionForEditing()
    {
        var bounds = GetSelectionBounds();
        SelectionRectangle.IsHitTestVisible = false;
        SelectionRectangle.Fill = WpfBrushes.Transparent;
        // The editor outline is the single source of truth after the first
        // selection. Keeping the original rectangle visible creates a second,
        // stale border when the editor region is adjusted.
        SelectionRectangle.Visibility = Visibility.Collapsed;
        Canvas.SetLeft(InlineEditorOutline, bounds.X);
        Canvas.SetTop(InlineEditorOutline, bounds.Y);
        InlineEditorOutline.Width = bounds.Width;
        InlineEditorOutline.Height = bounds.Height;
        InlineEditorOutline.Visibility = Visibility.Visible;
        TopLeftResizeThumb.Visibility = Visibility.Visible;
        TopRightResizeThumb.Visibility = Visibility.Visible;
        BottomLeftResizeThumb.Visibility = Visibility.Visible;
        BottomRightResizeThumb.Visibility = Visibility.Visible;
        TopResizeThumb.Visibility = Visibility.Visible;
        LeftResizeThumb.Visibility = Visibility.Visible;
        RightResizeThumb.Visibility = Visibility.Visible;
        BottomResizeThumb.Visibility = Visibility.Visible;
        InlineEditorOptions.Visibility = _hasVisibleInlineEditorTools && _hasSelectedInlineTool
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_nativeSelectionFrame.IsMaskEnabled && HasValidSelection())
        {
            _nativeSelectionFrame.SetSizeEnabled(ShouldShowSelectionSize);
            UpdateNativeSelectionVisuals(bounds);
            _nativeSelectionFrame.RefreshMask();
        }
        CaptureToolbar.UpdateLayout();
        UpdateSelectionControlPositions(GetSelectionBounds());
        UpdateSelectionMoveZonePositions(bounds);
        if (_nativeSelectionFrame.IsMaskEnabled && HasValidSelection())
        {
            UpdateNativeSelectionVisuals(GetSelectionBounds());
        }
    }

    private void ClearSelection()
    {
        HideInlineCustomColorPanel();
        // A new selection must start from the live selection surface. The
        // previous editor image/overlays are otherwise still children of the
        // same overlay window and can appear as a stale frame underneath the
        // new selection rectangle.
        if (InlineEditorCanvas.HasImage || _inlineEditorImage is not null)
        {
            InlineEditorCanvas.Reset();
            _inlineEditorImage?.Dispose();
            _inlineEditorImage = null;
        }
        InlineEditorCanvas.Visibility = Visibility.Collapsed;
        InlineEditorOutline.Visibility = Visibility.Collapsed;
        ClearInlineOcrText();
        _isSelecting = false;
        _isMovingSelection = false;
        _isResizeDragging = false;
        _resizeControlsSuppressed = false;
        _nativeSelectionFrame.StopTracking();
        _pendingSelectionBounds = null;
        _lastRenderedSelectionBounds = null;

        if (CaptureSurface.IsMouseCaptured)
        {
            CaptureSurface.ReleaseMouseCapture();
        }

        if (SelectionRectangle.IsMouseCaptured)
        {
            SelectionRectangle.ReleaseMouseCapture();
        }
        ReleaseOverlayMouseCapture();

        SelectionRectangle.IsHitTestVisible = true;
        SelectionRectangle.Fill = WpfBrushes.Transparent;
        SelectionRectangle.Stroke = WpfBrushes.Transparent;
        SelectionRectangle.Opacity = 0;
        Canvas.SetLeft(SelectionRectangle, 0);
        Canvas.SetTop(SelectionRectangle, 0);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        HideSelectionControls();
        if (IsVisible && !_isCompleted)
        {
            _windowSnapTimer.Start();
        }
        CaptureSurface.Focus();
    }

    private void RequestScrollCaptureCancellation()
    {
        if (!_isScrollCaptureSelection)
        {
            return;
        }

        if (_publishedScrollCaptureSelection?.TryGetTarget(
                out var selection) == true)
        {
            selection.RequestCancel();
        }
        else
        {
            CompleteSelection(result: null);
        }
    }

    private void HideSelectionControls(bool keepNativeMask = false)
    {
        // A queued normal-capture toolbar publication must never resurrect
        // the toolbar while a scroll-capture overlay is being locked.
        _toolbarMaskPublishScheduled = false;
        if (keepNativeMask)
        {
            _nativeSelectionFrame.HideBorderKeepMask();
        }
        else
        {
            _nativeSelectionFrame.Hide();
        }
        SelectionRectangle.Visibility = Visibility.Collapsed;
        InlineEditorOutline.Visibility = Visibility.Collapsed;
        TopLeftResizeThumb.Visibility = Visibility.Collapsed;
        TopRightResizeThumb.Visibility = Visibility.Collapsed;
        BottomLeftResizeThumb.Visibility = Visibility.Collapsed;
        BottomRightResizeThumb.Visibility = Visibility.Collapsed;
        TopResizeThumb.Visibility = Visibility.Collapsed;
        LeftResizeThumb.Visibility = Visibility.Collapsed;
        RightResizeThumb.Visibility = Visibility.Collapsed;
        BottomResizeThumb.Visibility = Visibility.Collapsed;
        SelectionMoveTopLeftZone.Visibility = Visibility.Collapsed;
        SelectionMoveTopRightZone.Visibility = Visibility.Collapsed;
        SelectionMoveBottomLeftZone.Visibility = Visibility.Collapsed;
        SelectionMoveBottomRightZone.Visibility = Visibility.Collapsed;
        ImageQuestionButton.Visibility = Visibility.Collapsed;
        CaptureToolbar.Visibility = Visibility.Collapsed;
        if (_nativeSelectionFrame.IsMaskEnabled)
        {
            _nativeSelectionFrame.SetMaskExcludedRegion(null);
        }
        InlineEditorOptions.Visibility = Visibility.Collapsed;
        SelectionSizeBadge.Visibility = Visibility.Collapsed;
    }

    internal Task SetScrollCaptureSelectionVisibleAsync(
        bool isVisible,
        CancellationToken cancellationToken)
    {
        if (!_isScrollCaptureSelection ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        return Dispatcher.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_isCompleted || !_isScrollCaptureSelectionPublished)
                {
                    return;
                }

                if (!isVisible)
                {
                    if (IsVisible)
                    {
                        _isScrollCaptureTemporarilyHidden = true;
                        Hide();
                    }

                    return;
                }

                if (_isScrollCaptureTemporarilyHidden)
                {
                    _isScrollCaptureTemporarilyHidden = false;
                    Show();
                    _ = NativeMethods.SetWindowPos(
                        _windowHandle,
                        new IntPtr(TopmostWindow),
                        _virtualScreenBounds.X,
                        _virtualScreenBounds.Y,
                        _virtualScreenBounds.Width,
                        _virtualScreenBounds.Height,
                        DoNotActivate | DoNotChangeOwnerZOrder);
                    if (HasValidSelection())
                    {
                        if (_isScrollCaptureSelectionLocked)
                        {
                            // The locked scroll selection keeps its native
                            // border visible for the whole capture session.
                            // The mask hole is restored independently below.
                            _nativeSelectionFrame.EnsureVisible();
                        }
                        else
                        {
                            UpdateNativeSelectionVisuals(GetSelectionBounds());
                        }
                    }
                }

            },
            DispatcherPriority.Render,
            cancellationToken).Task;
    }

    internal void CloseScrollCaptureSelection()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            Close();
            return;
        }

        Dispatcher.Invoke(Close);
    }

    private void PublishScrollCaptureSelection()
    {
        if (!_isScrollCaptureSelection ||
            _isScrollCaptureSelectionPublished ||
            !HasValidSelection())
        {
            return;
        }

        _isScrollCaptureSelectionPublished = true;
        // The first bounds update happens before the selection is published,
        // so the scroll masks and outline have not been laid out yet. Compute
        // them now before the coordinator can lock the selection for scrolling.
        _nativeSelectionFrame.SetMaskEnabled(true);
        _nativeSelectionFrame.RefreshMask();
        UpdateScrollCaptureOutline(GetSelectionBounds());
        ShowSelectionControls();
        CaptureSurface.Background = WpfBrushes.Transparent;
        var selection = new ScrollCaptureSelection(
            this,
            GetPhysicalSelectionBounds());
        _publishedScrollCaptureSelection = new WeakReference<ScrollCaptureSelection>(
            selection);
        _scrollCaptureSelectionCompletionSource?.TrySetResult(
            selection);
    }

    private void ApplyInitialScrollCaptureSelection(ScreenRegion selection)
    {
        var topLeft = CaptureSurface.PointFromScreen(
            new WpfPoint(selection.X, selection.Y));
        var bottomRight = CaptureSurface.PointFromScreen(
            new WpfPoint(
                selection.X + selection.Width,
                selection.Y + selection.Height));
        SyncNativeSelectionFrameColor();
        _nativeSelectionFrame.SetMaskEnabled(true);
        // SelectionRectangle starts collapsed for a fresh scroll overlay. Set
        // it visible before syncing bounds so UpdateSelectionBounds also
        // commits the first native frame position instead of skipping it.
        SelectionRectangle.Visibility = Visibility.Visible;
        UpdateSelectionBounds(new Rect(topLeft, bottomRight));
        PrepareScrollCaptureSelection();
    }

    private async Task ApplyInitialInteractiveSelectionAsync(ScreenRegion selection)
    {
        using var selectionTiming = CaptureTimingDiagnostics.Begin("editor-apply-initial-selection");
        if (_options is null || _isScrollCaptureSelection || _isCompleted)
        {
            return;
        }

        using (_nativeSelectionFrame.DeferPresentation(() => !_isCompleted && !_isDisposed))
        {
            var topLeft = CaptureSurface.PointFromScreen(
                new WpfPoint(selection.X, selection.Y));
            var bottomRight = CaptureSurface.PointFromScreen(
                new WpfPoint(
                    selection.X + selection.Width,
                    selection.Y + selection.Height));
            SyncNativeSelectionFrameColor();
            _nativeSelectionFrame.SetSizeEnabled(ShouldShowSelectionSize);
            // The resident already shows a clear selection. Initialize the hole
            // before enabling the mask, otherwise its first native presentation
            // darkens the entire screen (including the selected pixels).
            SelectionRectangle.Visibility = Visibility.Visible;
            UpdateSelectionBounds(new Rect(topLeft, bottomRight));
            if (!HasValidSelection())
            {
                ClearSelection();
                return;
            }

            _nativeSelectionFrame.SetMaskEnabled(true);
            SyncNativeSelectionFrameColor();
            _nativeSelectionFrame.SetSizeEnabled(ShouldShowSelectionSize);
            _nativeSelectionFrame.Update(ToDrawingRectangle(
                GetPhysicalSelectionBounds()));
            SelectionRectangle.Visibility = Visibility.Visible;
            ShowSelectionControls();
            await EnterInlineEditorForCompletedSelectionAsync(deferRecognition: true);
            if (_isCompleted || _isDisposed) return;
            // Only the final geometry belongs on screen. The initial rectangle,
            // editor outline, options row and toolbar previously submitted the
            // same native windows repeatedly on the cold mouse-up path.
            CaptureToolbar.UpdateLayout();
            _nativeSelectionFrame.SetMaskExclusionsSuspended(false);
            UpdateNativeSelectionVisuals(GetSelectionBounds());
        }
        // A queued/idle dispatcher turn is not proof that WPF has presented
        // the replacement bitmap. Keep the resident alive until the real
        // input surface is present, including on cold/remote rendering paths.
        try
        {
            var token = _lifetimeCancellationSource.Token;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render, token);
            if (_isCompleted || _isDisposed) return;
            var ready = true;
            if (_residentEditorRendered is not null)
                await WaitForResidentEditorCompositionAsync(token);
            else
                ready = await CaptureInputReadiness.WaitAsync(
                    IsInitialInputSurfacePresented,
                    () => CaptureInputReadiness.OwnsForeground(_windowHandle),
                    RecoverInitialScreenRendering, token);
            if (!ready)
            {
                FailInitialScreenStartup(new InvalidOperationException("截图编辑画面尚未就绪。"));
                return;
            }
            if (_isCompleted || _isDisposed || token.IsCancellationRequested) return;
            _options.CapturePresented?.Invoke();
            CaptureTimingDiagnostics.Mark("editor-presented-ack");
            ScheduleSelectionRecognition();
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException exception) { FailInitialScreenStartup(exception); }
        catch (InvalidOperationException exception) { FailInitialScreenStartup(exception); }
    }

    private void PrepareScrollCaptureSelection()
    {
        ShowSelectionControls();
        SaveButton.Visibility = Visibility.Collapsed;
        RecordButton.Visibility = Visibility.Collapsed;
        ScrollCaptureButton.Visibility = Visibility.Collapsed;
        PinButton.Visibility = Visibility.Collapsed;
        ConfirmButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        PublishScrollCaptureSelection();
        ImageQuestionButton.Visibility = Visibility.Collapsed;
        CaptureToolbar.Visibility = Visibility.Collapsed;
    }

    internal CapturedImage CaptureScrollSelectionSnapshot()
    {
        if (!_isScrollCaptureSelection || !HasValidSelection())
        {
            throw new InvalidOperationException("长截图选区无效。");
        }

        var selection = GetPhysicalSelectionBounds();

        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(CaptureScrollSelectionSnapshot);
        }

        var hideNativeFrameForCapture = _screenSnapshot is null &&
            _nativeSelectionFrame.IsMaskEnabled &&
            HasValidSelection();
        var outlineVisibility = ScrollCaptureOutline.Visibility;
        if (hideNativeFrameForCapture)
        {
            _nativeSelectionFrame.HideBorderKeepMask();
        }
        ScrollCaptureOutline.Visibility = Visibility.Collapsed;
        try
        {
            if (_screenSnapshot is not null)
            {
                var sourceRectangle = new System.Drawing.Rectangle(
                    selection.X - _virtualScreenBounds.X,
                    selection.Y - _virtualScreenBounds.Y,
                    selection.Width,
                    selection.Height);
                return new CapturedImage(
                    _screenSnapshot.Bitmap.Clone(
                        sourceRectangle,
                        System.Drawing.Imaging.PixelFormat.Format32bppPArgb),
                    selection);
            }

            return ScreenCaptureService.Capture(selection);
        }
        finally
        {
            ScrollCaptureOutline.Visibility = outlineVisibility;
            if (hideNativeFrameForCapture && !_isCompleted)
            {
                _nativeSelectionFrame.EnsureVisible();
            }
        }
    }

    internal Task LockScrollCaptureSelectionAsync(
        CancellationToken cancellationToken)
    {
        if (Dispatcher.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            LockScrollCaptureSelection();
            return Task.CompletedTask;
        }

        return Dispatcher.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                LockScrollCaptureSelection();
            },
            DispatcherPriority.Render,
            cancellationToken).Task;
    }

    internal bool IsScrollCaptureContextAvailable { get; private set; }
    internal IReadOnlyList<IntPtr> ScrollCaptureWindowHandles { get; private set; } = [];
    internal IReadOnlyList<ScreenRegion> ScrollCaptureBorderRegions { get; private set; } = [];

    private void LockScrollCaptureSelection()
    {
        if (_isScrollCaptureSelectionLocked)
        {
            return;
        }

        _isScrollCaptureSelectionLocked = true;
        HideSelectionControls(keepNativeMask: true);
        SelectionRectangle.Fill = WpfBrushes.Transparent;
        SelectionRectangle.IsHitTestVisible = false;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        // Once the region is locked, all pointer semantics are owned by the
        // native frame/mask and ScrollCaptureWheelMonitor. Keeping the
        // full-screen WPF surface hit-testable would still enqueue a WPF
        // MouseMove for every high-report-rate packet outside the hole.
        CaptureSurface.IsHitTestVisible = false;
        FrozenScreenImage.Source = null;
        FrozenScreenImage.Visibility = Visibility.Collapsed;
        // Keep the themed border visible while the user pauses or changes
        // direction. It is positioned just outside the capture hole below,
        // so frame sampling never needs to hide/show it.
        ScrollCaptureOutline.Visibility = Visibility.Visible;
        var selectionBounds = ToDrawingRectangle(GetPhysicalSelectionBounds());
        var borderBounds = ScrollCaptureBorderGeometry.GetBounds(selectionBounds,
            ToDrawingRectangle(_virtualScreenBounds));
        _nativeSelectionFrame.Update(borderBounds);
        _nativeSelectionFrame.EnsureVisible();
        EnableScrollCaptureClickThrough();

        // Only the full-screen mask/overlay use a stable capture exclusion.
        // The native border must stay in normal desktop composition so remote
        // viewers can see it too. The sampler repairs its narrow covered strips
        // from the target DC instead of hiding it or changing capture affinity.
        var excludedWindows = new[] { new WindowInteropHelper(this).Handle, _nativeSelectionMask.Handle };
        ScrollCaptureWindowHandles = [new WindowInteropHelper(this).Handle,
            _nativeSelectionMask.Handle, .. _nativeSelectionFrame.GetBorderWindowHandles()];
        ScrollCaptureBorderRegions = NativeSelectionBorder.GetStrips(borderBounds)
            .Where(strip => strip.Width > 0 && strip.Height > 0)
            .Select(strip => new ScreenRegion(strip.X, strip.Y, strip.Width, strip.Height)).ToArray();
        IsScrollCaptureContextAvailable = ScrollCaptureWindowExclusion.ExcludeForSession(excludedWindows);

        // Keep the frozen bitmap removed so the selection and its surroundings
        // remain live while it scrolls. The native mask owns the dimming.
        lock (_colorPickerBitmapLifetimeLock)
        {
            _pendingColorPickerBitmap = null;
            _pendingColorPickerResult = null;
            _screenSnapshot?.Dispose();
            _screenSnapshot = null;
        }
    }

    private void EnableScrollCaptureClickThrough()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;

        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(
            windowHandle,
            ExtendedWindowStyleIndex).ToInt64();
        _ = NativeMethods.SetWindowLongPtr(
            windowHandle,
            ExtendedWindowStyleIndex,
            new IntPtr(extendedStyle | ExtendedStyleNoActivate));
        ApplyScrollCaptureInputHole(windowHandle);
        _ = NativeMethods.SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            DoNotResize |
            DoNotMove |
            DoNotChangeZOrder |
            DoNotActivate |
                FrameChanged);
    }

    private void ApplyScrollCaptureInputHole(IntPtr windowHandle)
    {
        if (_publishedScrollCaptureSelection?.TryGetTarget(
                out var selection) != true ||
            selection is null)
        {
            return;
        }

        var captureRegion = selection.CaptureRegion;
        var holeLeft = captureRegion.X - _virtualScreenBounds.X;
        var holeTop = captureRegion.Y - _virtualScreenBounds.Y;
        var windowRegion = NativeMethods.CreateRectRgn(
            0,
            0,
            _virtualScreenBounds.Width,
            _virtualScreenBounds.Height);
        var inputHole = ScrollCaptureBorderGeometry.GetInputHole(new DrawingRectangle(
            holeLeft, holeTop, captureRegion.Width, captureRegion.Height));
        var holeRegion = NativeMethods.CreateRectRgn(
            inputHole.Left, inputHole.Top, inputHole.Right, inputHole.Bottom);

        try
        {
            if (windowRegion == IntPtr.Zero ||
                holeRegion == IntPtr.Zero ||
                NativeMethods.CombineRgn(
                    windowRegion,
                    windowRegion,
                    holeRegion,
                    RegionCombineDifference) == 0 ||
                NativeMethods.SetWindowRgn(
                    windowHandle,
                    windowRegion,
                    redraw: true) == 0)
            {
                return;
            }

            windowRegion = IntPtr.Zero;
        }
        finally
        {
            if (windowRegion != IntPtr.Zero)
            {
                _ = NativeMethods.DeleteObject(windowRegion);
            }

            if (holeRegion != IntPtr.Zero)
            {
                _ = NativeMethods.DeleteObject(holeRegion);
            }
        }
    }

    private IntPtr OnOverlayWindowMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == 0x0046 /* WM_WINDOWPOSCHANGING */) KeepResidentEditorBelowMask(lParam);

        if (_initialInputPassthrough &&
            message == NonClientHitTestMessage)
        {
            handled = true;
            return new IntPtr(TransparentHitTest);
        }

        if (_isScrollCaptureSelectionLocked &&
            message == NonClientHitTestMessage)
        {
            var packedPoint = lParam.ToInt64();
            var x = unchecked((short)(packedPoint & 0xffff));
            var y = unchecked((short)((packedPoint >> 16) & 0xffff));
            if (_publishedScrollCaptureSelection?.TryGetTarget(
                    out var selection) == true &&
                selection.CaptureRegion.Contains(x, y))
            {
                handled = true;
                return new IntPtr(TransparentHitTest);
            }
        }

        return IntPtr.Zero;
    }

    private void SetInitialInputPassthroughWindowStyle(bool enabled)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtr(
            _windowHandle,
            ExtendedWindowStyleIndex).ToInt64();
        // Use WM_NCHITTEST for the short bootstrap pass-through window. The
        // WS_EX_TRANSPARENT bit makes USER32 skip this layered HWND entirely
        // on some systems, so WPF never receives MouseMove/Button messages
        // after the capture bitmap is published. NOACTIVATE is only needed
        // while obtaining the desktop frame; clear it before showing the mask.
        var updatedStyle = enabled
            ? style | ExtendedStyleNoActivate
            : style & ~ExtendedStyleNoActivate;
        if (updatedStyle == style)
        {
            return;
        }

        _ = NativeMethods.SetWindowLongPtr(
            _windowHandle,
            ExtendedWindowStyleIndex,
            new IntPtr(updatedStyle));
        _ = NativeMethods.SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            DoNotResize |
            DoNotMove |
            DoNotChangeZOrder |
            DoNotActivate |
            FrameChanged);
    }

    private bool EnsureInteractiveInputOwnership()
    {
        if (_isCaptureBackgrounded) return false;
        if (_isScrollCaptureSelection) return IsActive;
        if (_windowHandle == IntPtr.Zero ||
            _isDisposed ||
            _isCompleted)
        {
            return false;
        }

        _initialInputPassthrough = false;
        SetInitialInputPassthroughWindowStyle(enabled: false);
        if (!CaptureInputReadiness.OwnsForeground(_windowHandle)) _ = Activate();
        CaptureSurface.Focus();
        Keyboard.Focus(CaptureSurface);
        CaptureTimingDiagnostics.Input(
            $"overlay-input-ownership-acquired active={IsActive} " +
            $"captured={CaptureSurface.IsMouseCaptured}");
        return CaptureInputReadiness.OwnsForeground(_windowHandle);
    }

    private Rect GetSelectionBounds()
    {
        return new Rect(
            Canvas.GetLeft(SelectionRectangle),
            Canvas.GetTop(SelectionRectangle),
            SelectionRectangle.Width,
            SelectionRectangle.Height);
    }

    private bool HasValidSelection()
    {
        var bounds = GetSelectionBounds();
        return bounds.Width >= MinimumSelectionEdge && bounds.Height >= MinimumSelectionEdge;
    }

    private ScreenRegion GetPhysicalSelectionBounds()
    {
        return GetPhysicalSelectionBoundsForBounds(GetSelectionBounds());
    }

    private ScreenRegion GetPhysicalSelectionBoundsForBounds(Rect bounds)
    {
        var start = CaptureSurface.PointToScreen(bounds.TopLeft);
        var end = CaptureSurface.PointToScreen(bounds.BottomRight);

        return ScreenRegion.FromCorners(
            (int)Math.Round(start.X),
            (int)Math.Round(start.Y),
            (int)Math.Round(end.X),
            (int)Math.Round(end.Y));
    }

    private static System.Drawing.Rectangle ToDrawingRectangle(ScreenRegion region) =>
        new(region.X, region.Y, region.Width, region.Height);

    private DrawingPoint ToPhysicalScreenPoint(WpfPoint point)
    {
        var screenPoint = CaptureSurface.PointToScreen(point);
        return new DrawingPoint(
            (int)Math.Round(screenPoint.X),
            (int)Math.Round(screenPoint.Y));
    }

    private Rect ClampBoundsToSurface(Rect bounds)
    {
        var left = Math.Clamp(bounds.Left, 0, CaptureSurface.ActualWidth);
        var top = Math.Clamp(bounds.Top, 0, CaptureSurface.ActualHeight);
        var right = Math.Clamp(bounds.Right, 0, CaptureSurface.ActualWidth);
        var bottom = Math.Clamp(bounds.Bottom, 0, CaptureSurface.ActualHeight);

        return new Rect(
            new WpfPoint(Math.Min(left, right), Math.Min(top, bottom)),
            new WpfPoint(Math.Max(left, right), Math.Max(top, bottom)));
    }

    private Rect ClampMovedBoundsToSurface(Rect requestedBounds)
    {
        var surfaceWidth = Math.Max(0, CaptureSurface.ActualWidth);
        var surfaceHeight = Math.Max(0, CaptureSurface.ActualHeight);
        var width = Math.Min(Math.Max(0, requestedBounds.Width), surfaceWidth);
        var height = Math.Min(Math.Max(0, requestedBounds.Height), surfaceHeight);
        var maxX = Math.Max(0, surfaceWidth - width);
        var maxY = Math.Max(0, surfaceHeight - height);

        return new Rect(
            Math.Clamp(requestedBounds.X, 0, maxX),
            Math.Clamp(requestedBounds.Y, 0, maxY),
            width,
            height);
    }

    private static Rect NormalizeBounds(Rect bounds)
    {
        return new Rect(
            Math.Min(bounds.Left, bounds.Right),
            Math.Min(bounds.Top, bounds.Bottom),
            Math.Abs(bounds.Width),
            Math.Abs(bounds.Height));
    }

    private void ResizeSelectionFromDrag(
        double leftChange,
        double topChange,
        double rightChange,
        double bottomChange)
    {
        // NativeSelectionFrameWindow owns the high-frequency resize geometry.
        // Ignore WPF DragDelta samples so they cannot enqueue stale bounds.
        if (_isResizeDragging)
        {
            return;
        }

        ResizeSelectionCore(
            leftChange,
            topChange,
            rightChange,
            bottomChange,
            deferVisualUpdate: true);
    }

    private void ResizeSelection(
        double leftChange,
        double topChange,
        double rightChange,
        double bottomChange)
    {
        ResizeSelectionCore(
            leftChange,
            topChange,
            rightChange,
            bottomChange,
            deferVisualUpdate: false);
    }

    private void ResizeSelectionCore(
        double leftChange,
        double topChange,
        double rightChange,
        double bottomChange,
        bool deferVisualUpdate)
    {
        BeginSelectionAdjustment();
        var bounds = deferVisualUpdate && _pendingSelectionBounds is { } pending
            ? ClampBoundsToSurface(NormalizeBounds(pending))
            : GetSelectionBounds();
        var left = Math.Clamp(bounds.Left + leftChange, 0, CaptureSurface.ActualWidth);
        var top = Math.Clamp(bounds.Top + topChange, 0, CaptureSurface.ActualHeight);
        var right = Math.Clamp(bounds.Right + rightChange, 0, CaptureSurface.ActualWidth);
        var bottom = Math.Clamp(bounds.Bottom + bottomChange, 0, CaptureSurface.ActualHeight);

        if (right - left < MinimumSelectionEdge)
        {
            if (leftChange != 0 && rightChange == 0)
            {
                left = Math.Max(0, right - MinimumSelectionEdge);
            }
            else
            {
                right = Math.Min(CaptureSurface.ActualWidth, left + MinimumSelectionEdge);
            }
        }

        if (bottom - top < MinimumSelectionEdge)
        {
            if (topChange != 0 && bottomChange == 0)
            {
                top = Math.Max(0, bottom - MinimumSelectionEdge);
            }
            else
            {
                bottom = Math.Min(CaptureSurface.ActualHeight, top + MinimumSelectionEdge);
            }
        }

        if (_selectionAdjustmentProtectedBounds is { } protectedBounds)
        {
            if (leftChange != 0)
            {
                left = Math.Min(left, protectedBounds.Left);
            }

            if (topChange != 0)
            {
                top = Math.Min(top, protectedBounds.Top);
            }

            if (rightChange != 0)
            {
                right = Math.Max(right, protectedBounds.Right);
            }

            if (bottomChange != 0)
            {
                bottom = Math.Max(bottom, protectedBounds.Bottom);
            }
        }

        var resizedBounds = new Rect(
            new WpfPoint(left, top),
            new WpfPoint(right, bottom));
        if (deferVisualUpdate)
        {
            QueueSelectionBoundsUpdate(resizedBounds);
        }
        else
        {
            UpdateSelectionBounds(resizedBounds);
        }
    }

    private static bool IsCaptureSurfaceBackground(object source)
    {
        return source is FrameworkElement element &&
               (element.Name == "CaptureSurface" || element is Canvas);
    }

    private bool CanStartNewSelectionFromBackground(object source)
    {
        // Once a region has been completed, clicks outside it must not start a
        // second selection. UpdateSelectionBounds also repositions the inline
        // editor, which previously made the captured image follow such clicks
        // and hid the toolbar when the new zero-sized selection was released.
        return !_isCompleted &&
               !_isSelecting &&
               !_isActionInProgress &&
               !_isEditorInitializing &&
               !HasValidSelection() &&
               IsCaptureSurfaceBackground(source);
    }

    private void CompleteSelection(ScreenRegion? result)
    {
        if (_isCompleted)
        {
            return;
        }

        _isCompleted = true;
        _isSelecting = false;
        _continuedSelectionButton = null;
        ReleaseOverlayMouseCapture();
        _selectionCompletionSource.TrySetResult(result);

        // Remove the visible/native surfaces immediately, then defer WPF
        // window teardown until the current input burst has drained. Closing
        // a full-screen transparent window synchronously can still make the
        // next cursor packet wait on visual-tree destruction.
        _nativeSelectionFrame.Hide();
        _nativeSelectionMask.HideMask();
        _nativeSelectionSize.HideSize();
        Hide();
        if (!_closeQueued &&
            !Dispatcher.HasShutdownStarted &&
            !Dispatcher.HasShutdownFinished)
        {
            _closeQueued = true;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() =>
                {
                    if (!_isDisposed && !Dispatcher.HasShutdownStarted &&
                        !Dispatcher.HasShutdownFinished)
                    {
                        Close();
                    }
                }));
        }
    }

    private void ReleaseOverlayMouseCapture()
    {
        if (CaptureSurface.IsMouseCaptureWithin)
        {
            Mouse.Capture(null);
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr windowHandle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateRectRgn(
            int left,
            int top,
            int right,
            int bottom);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int CombineRgn(
            IntPtr destinationRegion,
            IntPtr sourceRegion1,
            IntPtr sourceRegion2,
            int combineMode);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr objectHandle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowRgn(
            IntPtr windowHandle,
            IntPtr regionHandle,
            [MarshalAs(UnmanagedType.Bool)] bool redraw);


        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(
            IntPtr windowHandle,
            int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(
            IntPtr windowHandle,
            int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(
            IntPtr windowHandle,
            int index,
            IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(
            IntPtr windowHandle,
            int index,
            int value);

        public static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(windowHandle, index)
                : new IntPtr(GetWindowLong32(windowHandle, index));
        }

        public static IntPtr SetWindowLongPtr(
            IntPtr windowHandle,
            int index,
            IntPtr value)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(windowHandle, index, value)
                : new IntPtr(SetWindowLong32(
                    windowHandle,
                    index,
                    unchecked((int)value.ToInt64())));
        }
    }
}
