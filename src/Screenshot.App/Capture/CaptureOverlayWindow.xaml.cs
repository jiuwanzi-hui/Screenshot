using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Screenshot.App.Editor;
using Screenshot.App.Core;
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

    public required int HistoryLimit { get; init; }

    public required CaptureHistoryService HistoryService { get; init; }

    public required PinnedImageManager PinnedImageManager { get; init; }

    public string TranslationTargetLanguage { get; init; } = "zh-Hans";

    public required Func<CapturedImage, Task> StartOcrAsync { get; init; }

    public Func<CapturedImage, Task<OcrRecognitionResult>>? RecognizeTextAsync { get; init; }

    public Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
        TranslateTextAsync { get; init; }

    public Func<CapturedImage, CancellationToken, Task<ContentRecognitionResult>>?
        RecognizeFormulaAsync { get; init; }

    public bool RecognizeTextAfterSelection { get; init; }

    public bool TranslateTextAfterSelection { get; init; }

    public CapturePointerContinuation? InitialPointerContinuation { get; init; }

    public Func<ScreenRegion, Task>? StartScrollCaptureAsync { get; init; }

    public Func<ScreenRegion, Task>? StartVideoRecordingAsync { get; init; }

    public ScreenRegion? InitialSelection { get; init; }

    public Action<ScreenRegion>? CaptureCompleted { get; init; }

    public Action? CaptureClosed { get; init; }

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
        Enum.GetValues<CaptureToolbarFeature>();

    public CaptureToolbarRowCount ToolbarRows { get; init; } =
        CaptureToolbarRowCount.One;
}

public partial class CaptureOverlayWindow : Window, IDisposable
{
    private static CaptureOverlayWindow? _activeInteractiveOverlay;
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

    private readonly ScreenRegion _virtualScreenBounds;
    private readonly CaptureOverlayOptions? _options;
    private readonly HotKeyGesture? _completionHotKeyGesture;
    private readonly bool _isScrollCaptureSelection;
    private readonly CapturePointerContinuation? _initialPointerContinuation;
    private readonly TaskCompletionSource<ScreenRegion?> _selectionCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ScrollCaptureSelection?>?
        _scrollCaptureSelectionCompletionSource;
    private readonly DispatcherTimer _windowSnapTimer;
    private readonly CancellationTokenSource _lifetimeCancellationSource = new();
    private WpfPoint _selectionStartPoint;
    private WpfPoint _dragStartPoint;
    private Rect _dragStartBounds;
    private ScreenRegion? _selectionAdjustmentStartPhysicalBounds;
    private Rect? _selectionAdjustmentProtectedBounds;
    private CapturedImage? _inlineEditorImage;
    private CapturedImage? _screenSnapshot;
    private EditorTool _selectedInlineTool = EditorTool.Rectangle;
    private ArrowStyle _currentArrowStyle = ArrowStyle.Filled;
    private ArrowToolMode _currentArrowToolMode = ArrowToolMode.Straight;
    private ShapeToolMode _currentShapeToolMode = ShapeToolMode.Rectangle;
    private bool _isSelecting;
    private CapturePointerButton? _continuedSelectionButton;
    private Task _continuedSelectionReleaseTask = Task.CompletedTask;
    private bool _isMovingSelection;
    private bool _isSelectionAdjustmentInProgress;
    private bool _isActionInProgress;
    private bool _isEditorInitializing;
    private bool _isOcrInitializing;
    private bool _isQrInitializing;
    private bool _isTranslationInitializing;
    private bool _completeAfterRightButtonUp;
    private bool _cancelScrollCaptureAfterRightButtonUp;
    private bool _isWindowSnapClickPending;
    private bool _isColorPickerActive;
    private DrawingColor _selectedPixelColor = DrawingColor.Black;
    private Rect? _windowSnapBounds;
    private IntPtr _windowHandle;
    private bool _isCompleted;
    private bool _isInitializing = true;
    private OcrRecognitionResult? _inlineOcrResult;
    private ContentRecognitionResult? _inlineQrResult;
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
    private bool _isUpdatingInlineColorPanel;
    private bool _hasCustomToolbarPosition;
    private bool _isToolbarSurfaceDragging;
    private WpfPoint _toolbarSurfaceDragStart;
    private WpfPoint _toolbarSurfaceStartPosition;
    private double _toolbarPositionXRatio = -1;
    private double _toolbarPositionYRatio = -1;
    private readonly ToolbarDragHintBehavior _toolbarDragHint;
    private WpfColor _inlineColorPanelPreviewColor = WpfColor.FromRgb(0, 127, 115);
    private WpfColor? _inlineCustomColor;
    private int[] _inlineCustomColorPalette = [];
    private WeakReference<ScrollCaptureSelection>?
        _publishedScrollCaptureSelection;
    private HwndSource? _windowSource;

    private CaptureOverlayWindow(
        CaptureOverlayOptions? options,
        bool isScrollCaptureSelection = false,
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? initialPointerContinuation = null)
    {
        _options = options;
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
        _initialPointerContinuation =
            initialPointerContinuation ?? options?.InitialPointerContinuation;
        _scrollCaptureSelectionCompletionSource = isScrollCaptureSelection
            ? new TaskCompletionSource<ScrollCaptureSelection?>(
                TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        // The capture toolbar starts from the automatic placement on every
        // capture. Its drag position is intentionally session-local.
        _hasCustomToolbarPosition = false;
        InitializeComponent();
        _toolbarDragHint = new ToolbarDragHintBehavior(
            CaptureToolbar,
            CaptureToolbar);
        ApplyThemedContextMenu(InlineShapeToolButton.ContextMenu);
        ApplyThemedContextMenu(InlineArrowToolButton.ContextMenu);
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
        PopulateInlineEmojiPalette();
        UpdateInlineToolOptionPanels();
        _isInitializing = false;
        ApplyToolbarFeatureVisibility(options?.VisibleToolbarFeatures);
        ApplyToolbarLayout(
            options?.ToolbarFeatureOrder,
            options?.ToolbarRows ?? CaptureToolbarRowCount.One);
        _windowSnapTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _windowSnapTimer.Tick += OnWindowSnapTimerTick;
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

        _virtualScreenBounds = VirtualScreen.GetBounds();
        try
        {
            _screenSnapshot = initialScreenSnapshot ??
                ScreenCaptureService.Capture(_virtualScreenBounds);
            FrozenScreenImage.Source = _screenSnapshot.Preview;
        }
        catch
        {
            _screenSnapshot?.Dispose();
            _screenSnapshot = null;
            FrozenScreenImage.Source = null;
        }
        Left = _virtualScreenBounds.X;
        Top = _virtualScreenBounds.Y;
        Width = _virtualScreenBounds.Width;
        Height = _virtualScreenBounds.Height;
        SourceInitialized += OnSourceInitialized;
    }

    public static Task<ScreenRegion?> SelectAsync(
        CapturePointerContinuation? initialPointerContinuation = null,
        CapturedImage? initialScreenSnapshot = null)
    {
        var overlay = new CaptureOverlayWindow(
            options: null,
            initialScreenSnapshot: initialScreenSnapshot,
            initialPointerContinuation: initialPointerContinuation);
        overlay.Show();
        return overlay._selectionCompletionSource.Task;
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
        var overlay = new CaptureOverlayWindow(
            options: null,
            isScrollCaptureSelection: true);
        overlay.Show();
        overlay.UpdateLayout();
        overlay.ApplyInitialScrollCaptureSelection(initialSelection);
        return overlay._scrollCaptureSelectionCompletionSource!.Task;
    }

    public static CaptureOverlayWindow ShowInteractive(
        CaptureOverlayOptions options,
        CapturedImage? initialScreenSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var overlay = new CaptureOverlayWindow(
            options,
            initialScreenSnapshot: initialScreenSnapshot);
        _activeInteractiveOverlay = overlay;
        overlay.Show();
        if (options.InitialSelection is { } initialSelection)
        {
            overlay.UpdateLayout();
            _ = overlay.ApplyInitialInteractiveSelectionAsync(initialSelection);
        }

        return overlay;
    }

    internal static bool TryCompleteActiveInteractiveSelection()
    {
        var overlay = _activeInteractiveOverlay;
        if (overlay is null || overlay._isDisposed)
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
            overlay._isDisposed ||
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

    protected override void OnClosed(EventArgs e)
    {
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
        _isSelecting = false;
        _continuedSelectionButton = null;
        ReleaseOverlayMouseCapture();
        _windowSnapTimer.Stop();
        _windowSnapTimer.Tick -= OnWindowSnapTimerTick;
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(OnScrollCaptureWindowMessage);
            _windowSource = null;
        }

        InlineEditorCanvas.HistoryChanged -= OnInlineEditorHistoryChanged;
        InlineEditorCanvas.AnnotationSelectionChanged -=
            OnInlineAnnotationSelectionChanged;
        InlineEditorCanvas.PreviewMouseLeftButtonDown -=
            OnInlineEditorCanvasPreviewMouseLeftButtonDown;
        _inlineEditorImage?.Dispose();
        _inlineEditorImage = null;
        FrozenScreenImage.Source = null;
        _screenSnapshot?.Dispose();
        _screenSnapshot = null;

        if (!_isCompleted)
        {
            _isCompleted = true;
            _selectionCompletionSource.TrySetResult(null);
        }

        _scrollCaptureSelectionCompletionSource?.TrySetResult(null);

        _options?.CaptureClosed?.Invoke();

        base.OnClosed(e);
        Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lifetimeCancellationSource.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        _windowHandle = windowHandle;
        _ = NativeMethods.SetWindowPos(
            windowHandle,
            new IntPtr(TopmostWindow),
            _virtualScreenBounds.X,
            _virtualScreenBounds.Y,
            _virtualScreenBounds.Width,
            _virtualScreenBounds.Height,
            DoNotActivate | DoNotChangeOwnerZOrder);

        if (_isScrollCaptureSelection)
        {
            _windowSource = HwndSource.FromHwnd(windowHandle);
            _windowSource?.AddHook(OnScrollCaptureWindowMessage);
        }
    }

    private void OnCaptureSurfaceLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialPointerContinuation is not null)
        {
            CaptureInputDiagnostics.Write(
                $"overlay-loaded continuation={_initialPointerContinuation.DiagnosticId} " +
                $"button={_initialPointerContinuation.Button} active={IsActive}");
            ActivateContinuedSelectionWindow("loaded");
        }

        CaptureSurface.Focus();
        Keyboard.Focus(CaptureSurface);
        _windowSnapTimer.Start();
        BeginContinuedSelectionIfNeeded();
        if (_options is not null &&
            !_isSelecting &&
            !HasValidSelection())
        {
            EnterColorPicker();
        }
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
        if (_isCompleted ||
            _isScrollCaptureSelection ||
            _isColorPickerActive ||
            _isActionInProgress ||
            _isEditorInitializing ||
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
        if (InlineCustomColorPanel.Visibility == Visibility.Visible &&
            InlineSharedColorPicker.TryHandlePaletteRightClick(e.OriginalSource))
        {
            e.Handled = true;
            return;
        }

        if (TryGetInlineCustomPaletteSlotIndex(e.OriginalSource, out var slotIndex))
        {
            SaveInlineColorToPaletteSlot(slotIndex);
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
            CaptureStatusText.Text = "已隐藏内容识别结果。";
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

    private void OnCaptureSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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
            SelectionRectangle.Visibility = Visibility.Visible;
        }

        CaptureSurface.CaptureMouse();
        CaptureSurface.Focus();
        Mouse.UpdateCursor();
        if (continuedButton.HasValue)
        {
            CaptureInputDiagnostics.Write(
                $"overlay-pointer-selection id={_initialPointerContinuation?.DiagnosticId} " +
                $"button={continuedButton} captured={CaptureSurface.IsMouseCaptured} " +
                $"start={_selectionStartPoint.X:0.##},{_selectionStartPoint.Y:0.##}");
        }
    }

    private void OnCaptureSurfaceMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_isColorPickerActive)
        {
            UpdateColorPicker(e.GetPosition(CaptureSurface));
            return;
        }

        if (_isSelecting)
        {
            var currentPoint = e.GetPosition(CaptureSurface);
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
                UpdateSelectionBounds(new Rect(
                    _selectionStartPoint,
                    _selectionStartPoint));
                SelectionRectangle.Visibility = Visibility.Visible;
            }

            UpdateSelectionBounds(new Rect(_selectionStartPoint, currentPoint));
            return;
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

    private async Task CompletePointerSelectionCoreAsync(
        WpfPoint endPoint,
        bool enterPickerWhenEmpty)
    {
        var snappedBounds = _isWindowSnapClickPending
            ? _windowSnapBounds
            : null;
        _isWindowSnapClickPending = false;
        _isSelecting = false;
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
            UpdateSelectionBounds(new Rect(
                _selectionStartPoint,
                endPoint));
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
            if (enterPickerWhenEmpty && _options is not null)
            {
                EnterColorPicker();
            }

            return;
        }

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

    private async Task EnterInlineEditorForCompletedSelectionAsync()
    {
        await EnterInlineEditorAsync();
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
    }

    private void OnWindowSnapTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _isSelecting)
        {
            return;
        }

        var cursorPosition = WinForms.Cursor.Position;
        UpdateWindowSnap(cursorPosition.X, cursorPosition.Y);
    }

    private void UpdateWindowSnap(int screenX, int screenY)
    {
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

        _isMovingSelection = true;
        _dragStartPoint = e.GetPosition(CaptureSurface);
        _dragStartBounds = GetSelectionBounds();
        SelectionRectangle.CaptureMouse();
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

    private void EnterColorPicker()
    {
        if (_screenSnapshot is null || _isCompleted || _isActionInProgress)
        {
            return;
        }

        _isColorPickerActive = true;
        _windowSnapTimer.Start();
        CaptureSurface.Cursor = System.Windows.Input.Cursors.Cross;
        ColorPickerPanel.Visibility = Visibility.Visible;
        ColorPickerPanel.UpdateLayout();
        CaptureSurface.CaptureMouse();
        Activate();
        CaptureSurface.Focus();
        UpdateColorPicker(CaptureSurface.PointFromScreen(
            new WpfPoint(WinForms.Cursor.Position.X, WinForms.Cursor.Position.Y)));
        UpdateWindowSnap(
            WinForms.Cursor.Position.X,
            WinForms.Cursor.Position.Y);
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
        ColorPickerPanel.Visibility = Visibility.Collapsed;
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

        var screenPoint = CaptureSurface.PointToScreen(point);
        var pixelX = Math.Clamp(
            (int)Math.Round(screenPoint.X) - _virtualScreenBounds.X,
            0,
            Math.Max(0, _screenSnapshot.Bitmap.Width - 1));
        var pixelY = Math.Clamp(
            (int)Math.Round(screenPoint.Y) - _virtualScreenBounds.Y,
            0,
            Math.Max(0, _screenSnapshot.Bitmap.Height - 1));
        _selectedPixelColor = _screenSnapshot.Bitmap.GetPixel(pixelX, pixelY);
        ColorPickerValueText.Text = $"#{_selectedPixelColor.R:X2}{_selectedPixelColor.G:X2}{_selectedPixelColor.B:X2}";
        ColorPickerRgbText.Text = $"RGB({_selectedPixelColor.R}, {_selectedPixelColor.G}, {_selectedPixelColor.B})";

        const int sampleRadius = 12;
        var sourceLeft = Math.Clamp(pixelX - sampleRadius, 0, Math.Max(0, _screenSnapshot.Bitmap.Width - (sampleRadius * 2 + 1)));
        var sourceTop = Math.Clamp(pixelY - sampleRadius, 0, Math.Max(0, _screenSnapshot.Bitmap.Height - (sampleRadius * 2 + 1)));
        var sample = _screenSnapshot.Bitmap.Clone(
            new System.Drawing.Rectangle(sourceLeft, sourceTop, sampleRadius * 2 + 1, sampleRadius * 2 + 1),
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var magnified = new System.Drawing.Bitmap(104, 104);
            using (var graphics = System.Drawing.Graphics.FromImage(magnified))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(sample, new System.Drawing.Rectangle(0, 0, magnified.Width, magnified.Height));
                using var crossPen = new System.Drawing.Pen(System.Drawing.Color.White, 2);
                graphics.DrawLine(crossPen, 52, 46, 52, 58);
                graphics.DrawLine(crossPen, 46, 52, 58, 52);
                using var outlinePen = new System.Drawing.Pen(System.Drawing.Color.Black, 1);
                graphics.DrawRectangle(outlinePen, 50, 50, 4, 4);
            }

            ColorPickerMagnifier.Source = CapturedImage.ToBitmapSource(magnified);
        }
        finally
        {
            sample.Dispose();
        }

        var panelX = Math.Clamp(point.X + 20, 8, Math.Max(8, CaptureSurface.ActualWidth - ColorPickerPanel.ActualWidth - 8));
        var panelY = Math.Clamp(point.Y + 20, 8, Math.Max(8, CaptureSurface.ActualHeight - ColorPickerPanel.ActualHeight - 8));
        Canvas.SetLeft(ColorPickerPanel, panelX);
        Canvas.SetTop(ColorPickerPanel, panelY);
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

        var left = Canvas.GetLeft(ColorPickerPanel);
        var top = Canvas.GetTop(ColorPickerPanel);
        if (double.IsNaN(left) || double.IsNaN(top))
        {
            return false;
        }

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
        var bounds = ClampBoundsToSurface(new Rect(
            _dragStartBounds.X + delta.X,
            _dragStartBounds.Y + delta.Y,
            _dragStartBounds.Width,
            _dragStartBounds.Height));
        UpdateSelectionBounds(bounds);
    }

    private void OnSelectionRectangleMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMovingSelection)
        {
            return;
        }

        _isMovingSelection = false;
        SelectionRectangle.ReleaseMouseCapture();
        e.Handled = true;
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
        BeginSelectionAdjustment();
        _dragStartPoint = e.GetPosition(CaptureSurface);
        _dragStartBounds = GetSelectionBounds();
        InlineEditorOutline.CaptureMouse();
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
        UpdateSelectionBounds(bounds);
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

        _isMovingSelection = false;
        InlineEditorOutline.ReleaseMouseCapture();
        e.Handled = true;
        await CompleteSelectionAdjustmentAsync();
    }

    private void OnTopLeftResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelection(e.HorizontalChange, e.VerticalChange, 0, 0);
    }

    private void OnTopRightResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelection(0, e.VerticalChange, e.HorizontalChange, 0);
    }

    private void OnBottomLeftResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelection(e.HorizontalChange, 0, 0, e.VerticalChange);
    }

    private void OnBottomRightResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelection(0, 0, e.HorizontalChange, e.VerticalChange);
    }

    private void OnTopResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelection(0, e.VerticalChange, 0, 0);
    }

    private void OnLeftResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelection(e.HorizontalChange, 0, 0, 0);
    }

    private void OnRightResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelection(0, 0, e.HorizontalChange, 0);
    }

    private void OnBottomResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSelection(0, 0, 0, e.VerticalChange);
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

        try
        {
            var completedRegion = GetPhysicalSelectionBounds();
            using var image = await CaptureCurrentResultAsync(restoreOverlay: false);
            var savedPath = CaptureFileService.SaveAsPng(
                image,
                _options.SaveDirectory);
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
        }
        catch
        {
        }
        finally
        {
            CompleteSelection(result: null);
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
        CompleteSelection(result: null);

        try
        {
            await startScrollCaptureAsync(selection);
        }
        catch
        {
            // The coordinator reports scroll-capture failures in the settings window.
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
        CompleteSelection(result: null);

        try
        {
            await startVideoRecordingAsync(selection);
        }
        catch
        {
            // The coordinator surfaces recording failures through app status.
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
            InlineEditorCanvas.SelectTool(_selectedInlineTool);
            if (_inlineCustomColor is { } savedColor)
            {
                InlineEditorCanvas.SelectColor(savedColor);
            }
            Canvas.SetLeft(InlineEditorCanvas, selectionBounds.X);
            Canvas.SetTop(InlineEditorCanvas, selectionBounds.Y);
            InlineEditorCanvas.Visibility = Visibility.Visible;
            LockSelectionForEditing();
            var topmostApplied = ReassertOverlayTopmost();
            _ = Activate();
            InlineEditorCanvas.Focus();
            Keyboard.Focus(InlineEditorCanvas);
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
            Canvas.SetLeft(InlineEditorCanvas, selectionBounds.X);
            Canvas.SetTop(InlineEditorCanvas, selectionBounds.Y);
            InlineEditorCanvas.Visibility = Visibility.Visible;
            InlineEditorCanvas.Focus();
            CaptureStatusText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            CaptureStatusText.Text = "无法刷新当前选区，请重新框选。";
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
        if (!InlineEditorCanvas.HasImage || !InlineEditorCanvas.CanUndo)
        {
            return true;
        }

        CaptureStatusText.Text = "已有标注时不能移动整个选区；可拖动边缘调整，且不会裁掉标注。";
        CaptureStatusText.Visibility = Visibility.Visible;
        return false;
    }

    private void BeginSelectionAdjustment()
    {
        if (_isSelectionAdjustmentInProgress || !InlineEditorCanvas.HasImage)
        {
            return;
        }

        _isSelectionAdjustmentInProgress = true;
        _selectionAdjustmentStartPhysicalBounds = GetPhysicalSelectionBounds();
        _selectionAdjustmentProtectedBounds = GetProtectedAnnotationBounds();
        ClearInlineOcrText();
    }

    private Rect? GetProtectedAnnotationBounds()
    {
        var annotationBounds = InlineEditorCanvas.GetAnnotationBounds();
        if (!annotationBounds.HasValue || annotationBounds.Value.IsEmpty)
        {
            return null;
        }

        var selection = GetSelectionBounds();
        var scaleX = selection.Width / InlineEditorCanvas.Width;
        var scaleY = selection.Height / InlineEditorCanvas.Height;
        var bounds = Rect.Intersect(
            annotationBounds.Value,
            new Rect(0, 0, InlineEditorCanvas.Width, InlineEditorCanvas.Height));
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
        if (previousSelection.HasValue)
        {
            await RefreshInlineEditorForSelectionAsync(previousSelection.Value);
            if (InlineEditorCanvas.HasImage && !_isCompleted)
            {
                _ = RecognizeLocalInlineContentAsync(
                    _automaticRecognitionGeneration,
                    delayMilliseconds: 350);
            }
        }
    }

    private async void OnSelectionResizeThumbDragCompleted(
        object sender,
        DragCompletedEventArgs e)
    {
        await CompleteSelectionAdjustmentAsync();
    }

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
            using var qrImage = image.Clone();
            var qrTask = Task.Run(
                () => QrCodeRecognitionService.Recognize(qrImage),
                _lifetimeCancellationSource.Token);
            await qrTask.WaitAsync(_lifetimeCancellationSource.Token);
            if (expectedGeneration != _automaticRecognitionGeneration ||
                _isCompleted)
            {
                return;
            }

            _inlineQrResult = await qrTask;
            _isUnifiedRecognitionVisible = _inlineQrResult.IsSuccess;
            ShowAutomaticRecognitionResults();
        }
        catch (OperationCanceledException) when (_isCompleted)
        {
            return;
        }
        catch
        {
            _inlineQrResult = null;
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
        _isUnifiedRecognitionVisible = _inlineQrResult?.IsSuccess == true;
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        RebuildContentRecognitionOverlay();
    }

    private void HideUnifiedRecognitionResults()
    {
        _isUnifiedRecognitionVisible = false;
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        ContentRecognitionOverlay.Visibility = Visibility.Collapsed;
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
        if (_isOcrInitializing || _isActionInProgress || !HasValidSelection())
        {
            return;
        }

        _isOcrInitializing = true;
        CaptureToolbar.IsEnabled = false;
        CaptureStatusText.Text = "正在识别图片文字...";
        CaptureStatusText.Visibility = Visibility.Visible;

        try
        {
            using var image = await CaptureCurrentResultAsync(restoreOverlay: true);
            var result = await recognizeTextAsync(image);
            _inlineOcrResult = result;

            if (!result.IsSuccess)
            {
                CaptureStatusText.Text = result.ErrorMessage ?? "文字识别失败。";
                return;
            }

            if (result.Regions.Count == 0)
            {
                CaptureStatusText.Text = "没有识别到可选择的文字。";
                return;
            }

            ShowInlineOcrText(result);
        }
        catch
        {
            CaptureStatusText.Text = "文字识别失败，请检查 OCR 语言设置。";
        }
        finally
        {
            _isOcrInitializing = false;

            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = true;
            }
        }
    }

    private async void OnOcrClick(object sender, RoutedEventArgs e)
    {
        if (_inlineOcrResult is { IsSuccess: true, Regions.Count: > 0 } cached)
        {
            ShowInlineOcrText(cached);
            return;
        }

        if (_isOcrInitializing)
        {
            CaptureStatusText.Text = "正在后台分析当前选区，完成后再点击“文”即可选择文字。";
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
    {
        if (_options?.RecognizeTextAsync is null ||
            _isOcrInitializing ||
            _isActionInProgress ||
            !HasValidSelection())
        {
            return;
        }

        _isOcrInitializing = true;
        CaptureToolbar.IsEnabled = false;
        ShowSelectionMessage("正在识别文字...");
        try
        {
            using var image = _inlineEditorImage?.Clone() ??
                await CaptureCurrentSelectionAsync(restoreOverlay: true);
            var recognition = _inlineOcrResult;
            if (recognition is not { IsSuccess: true, Regions.Count: > 0 })
            {
                recognition = await _options.RecognizeTextAsync(image);
                _inlineOcrResult = recognition;
            }

            if (!recognition.IsSuccess || recognition.Regions.Count == 0)
            {
                await ShowSelectionMessageForAsync(
                    recognition.ErrorMessage ?? "当前选区未识别到文字",
                    1500);
                return;
            }

            if (_inlineQrResult is null)
            {
                using var qrImage = image.Clone();
                _inlineQrResult = await Task.Run(
                    () => QrCodeRecognitionService.Recognize(qrImage),
                    _lifetimeCancellationSource.Token);
            }

            _inlineTableResult = TableRecognitionService.BuildTsv(
                recognition,
                image.Bitmap);
            if (GetSpecialContentReason() is { } specialContentReason)
            {
                CopyRecognizedTextButton.ToolTip = specialContentReason;
                await ShowSelectionMessageForAsync(
                    "检测到特殊内容，请分别复制",
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
            await ClipboardTextService.SetTextAsync(text.Trim());
            await ShowSelectionMessageForAsync(
                "已识别并复制全部文字",
                650);
            CompleteSelection(result: null);
        }
        catch (OperationCanceledException) when (_isCompleted || _isDisposed)
        {
        }
        catch
        {
            await ShowSelectionMessageForAsync(
                "文字识别或复制失败，请重试",
                1500);
        }
        finally
        {
            _isOcrInitializing = false;
            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = true;
            }
        }
    }

    private async void OnCopyTableMenuItemClick(
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
        CaptureToolbar.IsEnabled = false;
        ShowSelectionMessage("正在识别表格...");
        try
        {
            using var image = _inlineEditorImage?.Clone() ??
                await CaptureCurrentSelectionAsync(restoreOverlay: true);
            var recognition = _inlineOcrResult;
            if (recognition is not { IsSuccess: true, Words.Count: > 0 })
            {
                recognition = await _options.RecognizeTextAsync(image);
                _inlineOcrResult = recognition;
            }

            if (!recognition.IsSuccess)
            {
                await ShowSelectionMessageForAsync(
                    recognition.ErrorMessage ?? "表格识别失败。",
                    1500);
                return;
            }

            var table = TableRecognitionService.BuildTsv(
                recognition,
                image.Bitmap);
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
                await ClipboardTextService.SetTextAsync(table.Content);
            }
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
            CompleteSelection(result: null);
        }
        catch (OperationCanceledException) when (_isCompleted || _isDisposed)
        {
        }
        catch
        {
            await ShowSelectionMessageForAsync(
                "表格识别失败，请重新尝试",
                1500);
        }
        finally
        {
            _isOcrInitializing = false;
            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = true;
                InlineEditorCanvas.Focus();
            }
        }
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        await TranslateInlineTextAsync();
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
                CaptureStatusText.Text = "已取消隐私打码。";
                CaptureStatusText.Visibility = Visibility.Visible;
                return;
            }

            var selected = confirmation.SelectedCandidates;
            if (selected.Count == 0)
            {
                CaptureStatusText.Text = "未选择需要打码的项目。";
                CaptureStatusText.Visibility = Visibility.Visible;
                return;
            }

            InlineEditorCanvas.AddMosaicRegions(
                selected.Select(candidate => candidate.Bounds));
            CaptureStatusText.Text =
                $"已添加 {selected.Count} 处隐私马赛克，可撤销或继续编辑。";
            CaptureStatusText.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (_isCompleted || _isDisposed)
        {
        }
        catch
        {
            CaptureStatusText.Text = "隐私信息检测失败，请重试。";
            CaptureStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            _isOcrInitializing = false;
            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = true;
                InlineEditorCanvas.Focus();
            }
        }
    }

    private async Task TranslateInlineTextAsync()
    {
        if (_options?.RecognizeTextAsync is null ||
            _options.TranslateTextAsync is null)
        {
            CaptureStatusText.Text = "当前截图模式未配置翻译功能。";
            CaptureStatusText.Visibility = Visibility.Visible;
            return;
        }

        if (_inlineTranslatedTextRegions is not null &&
            _inlineTranslatedAnnotationRegions is not null)
        {
            if (_inlineTranslatedTextRegions.Count == 0 ||
                _inlineTranslatedAnnotationRegions.Count == 0)
            {
                CaptureStatusText.Text =
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

        _isTranslationInitializing = true;
        CaptureToolbar.IsEnabled = false;
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        CaptureStatusText.Text = "正在识别图片文字（本机处理）...";
        CaptureStatusText.Visibility = Visibility.Visible;

        try
        {
            var recognition = _inlineOcrResult;
            if (recognition is not { IsSuccess: true, Regions.Count: > 0 })
            {
                using var image = await CaptureCurrentResultAsync(
                    restoreOverlay: true);
                recognition = await _options.RecognizeTextAsync(image);
                _inlineOcrResult = recognition;
            }

            if (!recognition.IsSuccess)
            {
                CaptureStatusText.Text =
                    recognition.ErrorMessage ?? "文字识别失败。";
                return;
            }

            if (recognition.Regions.Count == 0)
            {
                CaptureStatusText.Text = "没有识别到可翻译的文字。";
                return;
            }

            var tightenedRegions = TranslationPresentationLayout
                .TightenToWordBounds(recognition.Regions, recognition.Words)
                .Where(region => !string.IsNullOrWhiteSpace(region.Text))
                .OrderBy(region => region.Y)
                .ThenBy(region => region.X)
                .ToArray();
            // OCR often returns each wrapped line as a separate region, even
            // for a small selection. Always group compatible lines first so
            // the translator and overlay calculate one font size and line
            // height for the complete paragraph.
            var translationRegions = TranslationPresentationLayout
                .GroupParagraphs(tightenedRegions)
                .ToArray();
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
                        _options.TranslationTargetLanguage)))
            {
                _inlineTranslatedTextRegions = translationRegions;
                _inlineTranslatedAnnotationRegions = [];
                CaptureStatusText.Text =
                    "识别到的文字已经是目标语言，无需覆盖翻译。";
                return;
            }

            CaptureStatusText.Text =
                $"文字识别完成，正在翻译 {translationRegions.Length} 段文字...";
            var targetLanguageRegionCount = translationRegions.Count(region =>
                TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(
                    region.Text,
                    _options.TranslationTargetLanguage));
            var mostlyTargetLanguage = targetLanguageRegionCount >=
                Math.Max(1, (int)Math.Ceiling(translationRegions.Length * 0.75));
            var translationTimer = System.Diagnostics.Stopwatch.StartNew();
            // Large captures translate for a while; a static label reads as a
            // freeze, so keep the elapsed time visibly moving.
            var waitTicker = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            waitTicker.Tick += (_, _) =>
                CaptureStatusText.Text =
                    $"正在翻译 {translationRegions.Length} 段文字，" +
                    $"已等待 {translationTimer.Elapsed.TotalSeconds:F0} 秒" +
                    "（段落较多时需要更长时间，按 Esc 取消）...";
            waitTicker.Start();
            TranslationSegmentsResult translation;
            try
            {
                translation = await _options.TranslateTextAsync(translationInput)
                    .WaitAsync(_lifetimeCancellationSource.Token);
            }
            finally
            {
                waitTicker.Stop();
            }

            translationTimer.Stop();
            if (!translation.IsSuccess)
            {
                CaptureStatusText.Text = mostlyTargetLanguage
                    ? "识别到的大部分文字已经是目标语言，未生成覆盖翻译。"
                    : translation.ErrorMessage ?? "翻译失败。";
                return;
            }

            if (!string.IsNullOrWhiteSpace(translation.ErrorMessage))
            {
                CaptureStatusText.Text = translation.ErrorMessage;
            }

            if (translation.Segments.Count != translationRegions.Length)
            {
                CaptureStatusText.Text = "翻译服务返回的分段结果不完整。";
                return;
            }

            var normalizedTranslations = translation.Segments
                .Select((text, index) => TranslationPresentationLayout
                    .NormalizeTranslatedText(
                        translationRegions[index].Text,
                        text))
                .ToArray();
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
                         Math.Max(24, item.Region.Height + 4)),
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
                CaptureStatusText.Text = mostlyTargetLanguage
                    ? "识别到的文字已经是目标语言，无需覆盖翻译。"
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
            InlineEditorCanvas.AddTranslationOverlay(
                _inlineTranslatedAnnotationRegions);
            SetInlineTranslationVisibility(isVisible: true);
            CaptureStatusText.Text +=
                $" 翻译耗时 {translationTimer.Elapsed.TotalSeconds:F1} 秒。";
        }
        catch (OperationCanceledException) when (_isCompleted)
        {
            return;
        }
        catch
        {
            CaptureStatusText.Text = "识别或翻译失败，请检查 OCR 与翻译设置。";
        }
        finally
        {
            _isTranslationInitializing = false;
            CaptureStatusText.Visibility = Visibility.Visible;
            if (!_isCompleted)
            {
                CaptureToolbar.IsEnabled = true;
            }
        }
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
            TranslateButton.ToolTip = "显示原文";
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
        TranslateButton.ToolTip = "显示已缓存的译文";
        CaptureStatusText.Text =
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
        CaptureStatusText.Text = isTranslation
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
            CaptureStatusText.Text = _isShowingTranslatedText
                ? "已复制所选译文，可在 Win+V 中查看。"
                : "已复制所选识别文字，可在 Win+V 中查看。";
        }
        catch
        {
            CaptureStatusText.Text = "复制文字失败，剪贴板可能正被其他程序使用，请重试。";
        }

        CaptureStatusText.Visibility = Visibility.Visible;
    }

    private void ClearInlineOcrText()
    {
        _automaticRecognitionGeneration++;
        _selectionMessageGeneration++;
        _inlineOcrResult = null;
        _inlineQrResult = null;
        _inlineTableResult = null;
        _inlineFormulaResult = null;
        _inlineTranslatedTextRegions = null;
        _inlineTranslatedAnnotationRegions = null;
        _isShowingTranslatedText = false;
        _isUnifiedRecognitionVisible = false;
        TranslateButtonText.Text = "译";
        TranslateButton.ToolTip = "识别并翻译，覆盖到截图";
        OcrTextOverlay.Children.Clear();
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        ContentRecognitionOverlay.Children.Clear();
        ContentRecognitionOverlay.Visibility = Visibility.Collapsed;
        SelectionMessageToast.Visibility = Visibility.Collapsed;
    }

    private void RebuildContentRecognitionOverlay()
    {
        ContentRecognitionOverlay.Children.Clear();
        if (!_isUnifiedRecognitionVisible || _inlineEditorImage is null)
        {
            ContentRecognitionOverlay.Visibility = Visibility.Collapsed;
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

        if (_inlineQrResult is { IsSuccess: true, Region: { } region } qrResult)
        {
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
            CaptureStatusText.Text = "表格复制失败，剪贴板可能正被其他程序使用。";
        }

        CaptureStatusText.Visibility = Visibility.Visible;
        InlineEditorCanvas.Focus();
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
            CaptureStatusText.Text = "已复制二维码内容。";
        }
        catch
        {
            CaptureStatusText.Text = "二维码内容复制失败，请重试。";
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
            image = await CaptureCurrentResultAsync(restoreOverlay: false);
            var imageToPin = image;
            image = null;
            _options.PinnedImageManager.Pin(imageToPin);
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
        if (!BeginAction())
        {
            return;
        }

        try
        {
            if (_options is not null)
            {
                var completedRegion = GetPhysicalSelectionBounds();
                using var image = await CaptureCurrentResultAsync(restoreOverlay: false);
                await ClipboardImageService.SetImageAsync(image.Preview);

                if (_options.KeepHistory)
                {
                    _ = _options.HistoryService.Add(image, _options.HistoryLimit);
                }

                _options.CaptureCompleted?.Invoke(completedRegion);
            }

            CompleteSelection(result: null);
        }
        catch
        {
            _isActionInProgress = false;
            CaptureToolbar.IsEnabled = true;
            CaptureStatusText.Text = "复制失败，剪贴板可能正被其他程序使用，请重试。";
            CaptureStatusText.Visibility = Visibility.Visible;
            CaptureToolbar.UpdateLayout();
            UpdateSelectionControlPositions(GetSelectionBounds());
        }
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
        SetVisibility(InlineBrushToolButton, CaptureToolbarFeature.Brush);
        SetVisibility(InlineTextToolButton, CaptureToolbarFeature.Text);
        SetVisibility(InlineMosaicToolButton, CaptureToolbarFeature.Mosaic);
        SetVisibility(RecordButton, CaptureToolbarFeature.VideoRecording);
        SetVisibility(SaveButton, CaptureToolbarFeature.Save);
        SetVisibility(ScrollCaptureButton, CaptureToolbarFeature.ScrollCapture);
        SetVisibility(OcrButton, CaptureToolbarFeature.TextRecognition);
        SetVisibility(
            CopyRecognizedTextButton,
            CaptureToolbarFeature.CopyRecognizedText);
        SetVisibility(TranslateButton, CaptureToolbarFeature.Translation);
        SetVisibility(
            PrivacyRedactionButton,
            CaptureToolbarFeature.PrivacyRedaction);
        SetVisibility(PinButton, CaptureToolbarFeature.PinImage);
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
            (InlineBrushToolButton, EditorTool.Brush),
            (InlineTextToolButton, EditorTool.Text),
            (InlineMosaicToolButton, EditorTool.Mosaic),
        };
        var selected = editorButtons
            .FirstOrDefault(item => item.Tool == _selectedInlineTool ||
                item.Tool == EditorTool.Rectangle &&
                    _selectedInlineTool == EditorTool.Ellipse ||
                item.Tool == EditorTool.Arrow &&
                    _selectedInlineTool == EditorTool.CurvedArrow);
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
                    : selected.Tool;
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
            [CaptureToolbarFeature.Brush] = [InlineBrushToolButton],
            [CaptureToolbarFeature.Text] = [InlineTextToolButton],
            [CaptureToolbarFeature.Mosaic] = [InlineMosaicToolButton],
            [CaptureToolbarFeature.VideoRecording] = [RecordButton],
            [CaptureToolbarFeature.Save] = [SaveButton],
            [CaptureToolbarFeature.ScrollCapture] = [ScrollCaptureButton],
            [CaptureToolbarFeature.TextRecognition] = [OcrButton],
            [CaptureToolbarFeature.CopyRecognizedText] =
                [CopyRecognizedTextButton],
            [CaptureToolbarFeature.Translation] = [TranslateButton],
            [CaptureToolbarFeature.PrivacyRedaction] =
                [PrivacyRedactionButton],
            [CaptureToolbarFeature.PinImage] = [PinButton],
            [CaptureToolbarFeature.UndoRedo] =
                [InlineUndoButton, InlineRedoButton],
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
            CaptureToolbarFeature.VideoRecording,
            CaptureToolbarFeature.Save,
            CaptureToolbarFeature.ScrollCapture,
            CaptureToolbarFeature.TextRecognition,
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
            InlineBrushToolButton,
            InlineTextToolButton,
            InlineMosaicToolButton,
        }.Any(IsVisible);
        var hasActions = new FrameworkElement[]
        {
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

        if (InlineEditorCanvas.HasImage)
        {
        InlineEditorCanvas.SelectTool(tool);
            InlineEditorCanvas.Focus();
        }
        if (tool is EditorTool.Arrow or EditorTool.CurvedArrow)
        {
            _options?.ArrowToolModeChanged?.Invoke(tool == EditorTool.CurvedArrow
                ? ArrowToolMode.Curved
                : ArrowToolMode.Straight);
        }
        _options?.LastAnnotationToolChanged?.Invoke(ToAnnotationToolMode(tool));
    }

    private void OnInlineShapeMenuArrowMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (InlineShapeToolButton.ContextMenu is not { } menu)
        {
            return;
        }

        UpdateInlineShapeMenuState();
        menu.PlacementTarget = InlineShapeToolButton;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnInlineArrowMenuArrowMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (InlineArrowToolButton.ContextMenu is not { } menu)
        {
            return;
        }

        UpdateInlineArrowMenuState();
        menu.PlacementTarget = InlineArrowToolButton;
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
            InlineEditorCanvas.Focus();
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

    private System.Windows.Controls.RadioButton GetInlineToolButton(EditorTool tool) => tool switch
    {
        EditorTool.Emoji => InlineEmojiToolButton,
        EditorTool.Number => InlineNumberToolButton,
        EditorTool.Brush => InlineBrushToolButton,
        EditorTool.Mosaic => InlineMosaicToolButton,
        EditorTool.Text => InlineTextToolButton,
        _ => InlineShapeToolButton,
    };

    private void UpdateInlineToolOptionPanels()
    {
        if (InlineEmojiPalette is null || InlineStrokeOptions is null)
        {
            return;
        }

        var isEmoji = _selectedInlineTool == EditorTool.Emoji;
        InlineEmojiPalette.Visibility = isEmoji ? Visibility.Visible : Visibility.Collapsed;
        InlineStrokeOptions.Visibility = isEmoji ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnInlineAnnotationSelectionChanged(object? sender, EventArgs e)
    {
        CaptureStatusText.Text = InlineEditorCanvas.HasSelectedAnnotation
            ? "已选中标注：可拖动或缩放，按 Delete 删除。"
            : "可继续编辑当前截图。";
        CaptureStatusText.Visibility = Visibility.Visible;
    }

    private void OnInlineEmojiClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string sticker } ||
            string.IsNullOrWhiteSpace(sticker))
        {
            return;
        }

        InlineEditorCanvas.SelectEmoji(sticker);
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
            ApplyInlineCustomColor(color);
        }
    }

    private void OnInlineCustomColorClick(object sender, RoutedEventArgs e)
    {
        ShowInlineCustomColorPanel();
        e.Handled = true;
    }

    private void OnInlineCustomPaletteColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: int slotIndex })
        {
            if (slotIndex < _inlineCustomColorPalette.Length)
            {
                var colorValue = _inlineCustomColorPalette[slotIndex];
                ApplyInlineCustomColor(WpfColor.FromRgb(
                    (byte)(colorValue >> 16),
                    (byte)(colorValue >> 8),
                    (byte)colorValue));
                HideInlineCustomColorPanel();
            }
            else
            {
                SaveInlineColorToPaletteSlot(slotIndex);
            }

            e.Handled = true;
        }
    }

    private void ShowInlineCustomColorPanel()
    {
        InlineSharedColorPicker.SetState(
            _inlineCustomColor ?? WpfColor.FromRgb(0, 127, 115),
            _inlineCustomColorPalette);
        InlineCustomColorPanel.Visibility = Visibility.Visible;
        InlineCustomColorPanel.UpdateLayout();
        var toolbarX = Canvas.GetLeft(CaptureToolbar);
        var toolbarY = Canvas.GetTop(CaptureToolbar);
        var panelX = Math.Clamp(
            InlineCustomColorButton.TranslatePoint(
                new WpfPoint(0, 0), CaptureSurface).X,
            8,
            Math.Max(8, CaptureSurface.ActualWidth - InlineCustomColorPanel.ActualWidth - 8));
        var panelY = toolbarY - InlineCustomColorPanel.ActualHeight - 8;
        if (panelY < 8)
        {
            panelY = toolbarY + CaptureToolbar.ActualHeight + 8;
        }

        Canvas.SetLeft(InlineCustomColorPanel, panelX);
        Canvas.SetTop(InlineCustomColorPanel, panelY);
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
        InlineCustomColorPanel.Visibility = Visibility.Collapsed;
    }

    private bool IsSourceInsideInlineCustomColorPanel(object source)
    {
        return source is DependencyObject element &&
            InlineCustomColorPanel.IsAncestorOf(element);
    }

    private void OnInlineColorComponentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingInlineColorPanel)
        {
            return;
        }

        var color = ColorFromHsv(
            InlineHueSlider.Value,
            InlineSaturationSlider.Value / 100d,
            InlineValueSlider.Value / 100d,
            (byte)Math.Round(InlineAlphaSlider.Value * 2.55d));
        UpdateInlineColorPanelPreview(color);
    }

    private void OnInlineColorSliderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ApplyInlineCustomColor(_inlineColorPanelPreviewColor);
        e.Handled = true;
    }

    private void OnInlineColorSliderLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        ApplyInlineCustomColor(_inlineColorPanelPreviewColor);
    }

    private void SaveInlineColorToPaletteSlot(int slotIndex)
    {
        var colorValue = _inlineColorPanelPreviewColor.R << 16 |
            _inlineColorPanelPreviewColor.G << 8 |
            _inlineColorPanelPreviewColor.B;
        var slots = _inlineCustomColorPalette.Take(8).ToList();
        while (slots.Count <= slotIndex)
        {
            slots.Add(colorValue);
        }

        slots[slotIndex] = colorValue;
        _inlineCustomColorPalette = NormalizeCustomColorPalette(
            slots.Concat(_inlineCustomColorPalette.Skip(8)));
        _options?.CustomColorPaletteChanged?.Invoke(_inlineCustomColorPalette.ToArray());
        ShowInlineCustomColorPanel();
    }

    private bool TryGetInlineCustomPaletteSlotIndex(object source, out int slotIndex)
    {
        for (var element = source as DependencyObject;
             element is not null;
             element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is System.Windows.Controls.Button { Tag: int index } &&
                InlineRecentColorsPanel.IsAncestorOf(element))
            {
                slotIndex = index;
                return true;
            }

            if (ReferenceEquals(element, InlineCustomColorPanel))
            {
                break;
            }
        }

        slotIndex = -1;
        return false;
    }

    private void OnInlineColorHexTextBoxPreviewKeyDown(
        object sender,
        WpfKeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = ClipboardTextService.SetTextAsync(InlineColorHexTextBox.Text);
            e.Handled = true;
        }
    }

    private void OnInlineColorHexTextBoxKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            WpfColorConverter.ConvertFromString(InlineColorHexTextBox.Text.Trim()) is not WpfColor color)
        {
            return;
        }

        SetInlineColorPanelValues(color);
        ApplyInlineCustomColor(color);
        HideInlineCustomColorPanel();
        e.Handled = true;
    }

    private void SetInlineColorPanelValues(WpfColor color)
    {
        _isUpdatingInlineColorPanel = true;
        try
        {
            var (hue, saturation, value) = ColorToHsv(color);
            InlineHueSlider.Value = hue;
            InlineSaturationSlider.Value = saturation * 100d;
            InlineValueSlider.Value = value * 100d;
            InlineAlphaSlider.Value = color.A / 2.55d;
            UpdateInlineColorPanelPreview(color);
        }
        finally
        {
            _isUpdatingInlineColorPanel = false;
        }
    }

    private void UpdateInlineColorPanelPreview(WpfColor color)
    {
        _inlineColorPanelPreviewColor = color;
        InlineColorPreview.Background = new WpfSolidColorBrush(color);
        InlineColorHexTextBox.Text = FormatInlineColorText(color);
        InlineHueText.Text = $"{Math.Round(InlineHueSlider.Value):0}";
        InlineSaturationText.Text = $"{Math.Round(InlineSaturationSlider.Value):0}%";
        InlineValueText.Text = $"{Math.Round(InlineValueSlider.Value):0}%";
        InlineAlphaText.Text = $"{Math.Round(InlineAlphaSlider.Value):0}%";
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
        InlineStrokeWidthText.Text = $"{displayedWidth:0} px";
    }

    private void UpdateInlineSelectedColorButton(
        System.Windows.Controls.Button selectedButton)
    {
        foreach (var button in new[]
                 {
                     InlineRedColorButton,
                     InlineCyanColorButton,
                     InlineDarkColorButton,
                     InlineCustomColorButton,
                 })
        {
            button.BorderBrush = new WpfSolidColorBrush(
                WpfColor.FromArgb(0x8A, 0xE1, 0xD8, 0xD0));
            button.BorderThickness = new Thickness(1);
        }

        selectedButton.BorderBrush = WpfBrushes.White;
        selectedButton.BorderThickness = new Thickness(2);
    }

    private void OnInlineEditorHistoryChanged(object? sender, EventArgs e)
    {
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
            TranslateButton.ToolTip = "显示原文";
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
        TranslateButton.ToolTip = _inlineTranslatedTextRegions is { Count: > 0 }
            ? "显示已缓存的译文"
            : "识别并翻译，覆盖到截图";
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
            await Task.Delay(80);
            return ScreenCaptureService.Capture(selection);
        }
        finally
        {
            if (restoreOverlay && !_isCompleted)
            {
                Show();
                Activate();
                CaptureSurface.Focus();
            }
        }
    }

    private async Task<CapturedImage> CaptureCurrentResultAsync(
        bool restoreOverlay)
    {
        if (InlineEditorCanvas.HasImage)
        {
            return CapturedImage.FromBitmapSource(
                InlineEditorCanvas.RenderEditedImage(),
                GetPhysicalSelectionBounds());
        }

        return await CaptureCurrentSelectionAsync(restoreOverlay);
    }

    private void UpdateSelectionBounds(Rect requestedBounds)
    {
        var bounds = ClampBoundsToSurface(NormalizeBounds(requestedBounds));
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
        UpdateSelectionMask(bounds);

        if (bounds.Width >= MinimumSelectionEdge &&
            bounds.Height >= MinimumSelectionEdge)
        {
            CaptureShade.Visibility = Visibility.Collapsed;
            SetSelectionMaskVisibility(Visibility.Visible);
        }
        else
        {
            CaptureShade.Visibility = Visibility.Visible;
            SetSelectionMaskVisibility(Visibility.Collapsed);
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

    private void UpdateSelectionControlPositions(Rect bounds)
    {
        SetControlPosition(TopLeftResizeThumb, bounds.X, bounds.Y);
        SetControlPosition(TopRightResizeThumb, bounds.Right, bounds.Y);
        SetControlPosition(BottomLeftResizeThumb, bounds.X, bounds.Bottom);
        SetControlPosition(BottomRightResizeThumb, bounds.Right, bounds.Bottom);
        SetControlPosition(TopResizeThumb, bounds.X + (bounds.Width / 2), bounds.Y);
        SetControlPosition(LeftResizeThumb, bounds.X, bounds.Y + (bounds.Height / 2));
        SetControlPosition(RightResizeThumb, bounds.Right, bounds.Y + (bounds.Height / 2));
        SetControlPosition(BottomResizeThumb, bounds.X + (bounds.Width / 2), bounds.Bottom);

        if (_hasCustomToolbarPosition)
        {
            SetCaptureToolbarPosition(
                _toolbarPositionXRatio * GetToolbarMaximumX(),
                _toolbarPositionYRatio * GetToolbarMaximumY());
            return;
        }

        var toolbarX = CalculateAutomaticToolbarX(
            bounds,
            CaptureToolbar.ActualWidth,
            CaptureSurface.ActualWidth);
        var toolbarY = bounds.Bottom + 10;

        if (toolbarY + CaptureToolbar.ActualHeight > CaptureSurface.ActualHeight)
        {
            toolbarY = Math.Max(0, bounds.Y - CaptureToolbar.ActualHeight - 10);
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
        // Toolbar placement is deliberately not persisted. Keep the helper so
        // the drag lifecycle remains symmetrical and future position changes
        // stay local to the current capture window.
        _ = _options;
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
        CaptureSurface.ActualWidth - CaptureToolbar.ActualWidth);

    private double GetToolbarMaximumY() => Math.Max(
        0,
        CaptureSurface.ActualHeight - CaptureToolbar.ActualHeight);

    private void SetCaptureToolbarPosition(double x, double y)
    {
        Canvas.SetLeft(CaptureToolbar, Math.Clamp(x, 0, GetToolbarMaximumX()));
        Canvas.SetTop(CaptureToolbar, Math.Clamp(y, 0, GetToolbarMaximumY()));
    }

    private void UpdateSelectionSizeBadge(Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            SelectionSizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var physicalBounds = GetPhysicalSelectionBounds();
        if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
        {
            SelectionSizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionSizeText.Text = $"{physicalBounds.Width} × {physicalBounds.Height}";
        SelectionSizeBadge.Visibility = Visibility.Visible;
        SelectionSizeBadge.Measure(new System.Windows.Size(
            double.PositiveInfinity,
            double.PositiveInfinity));

        var badgeWidth = SelectionSizeBadge.DesiredSize.Width;
        var badgeHeight = SelectionSizeBadge.DesiredSize.Height;
        var badgeX = Math.Clamp(
            bounds.X,
            0,
            Math.Max(0, CaptureSurface.ActualWidth - badgeWidth));
        var badgeY = bounds.Y - badgeHeight - 7;
        if (badgeY < 0)
        {
            badgeY = bounds.Bottom + 7;
        }

        badgeY = Math.Clamp(
            badgeY,
            0,
            Math.Max(0, CaptureSurface.ActualHeight - badgeHeight));
        Canvas.SetLeft(SelectionSizeBadge, badgeX);
        Canvas.SetTop(SelectionSizeBadge, badgeY);
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
        _windowSnapTimer.Stop();
        HideWindowSnap();
        SelectionRectangle.Visibility = Visibility.Visible;
        TopLeftResizeThumb.Visibility = Visibility.Visible;
        TopRightResizeThumb.Visibility = Visibility.Visible;
        BottomLeftResizeThumb.Visibility = Visibility.Visible;
        BottomRightResizeThumb.Visibility = Visibility.Visible;
        TopResizeThumb.Visibility = Visibility.Visible;
        LeftResizeThumb.Visibility = Visibility.Visible;
        RightResizeThumb.Visibility = Visibility.Visible;
        BottomResizeThumb.Visibility = Visibility.Visible;
        CaptureToolbar.Visibility = Visibility.Visible;
        CaptureToolbar.UpdateLayout();
        UpdateSelectionControlPositions(GetSelectionBounds());
    }

    private void LockSelectionForEditing()
    {
        var bounds = GetSelectionBounds();
        SelectionRectangle.IsHitTestVisible = false;
        SelectionRectangle.Fill = WpfBrushes.Transparent;
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
        InlineEditorOptions.Visibility = _hasVisibleInlineEditorTools
            ? Visibility.Visible
            : Visibility.Collapsed;
        CaptureToolbar.UpdateLayout();
        UpdateSelectionControlPositions(GetSelectionBounds());
    }

    private void ClearSelection()
    {
        HideInlineCustomColorPanel();
        _isSelecting = false;
        _isMovingSelection = false;

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
        SelectionRectangle.SetResourceReference(
            System.Windows.Shapes.Shape.FillProperty,
            "AppAccentMutedBrush");
        Canvas.SetLeft(SelectionRectangle, 0);
        Canvas.SetTop(SelectionRectangle, 0);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        HideSelectionControls();
        if (!_isScrollCaptureSelectionLocked)
        {
            CaptureShade.Visibility = Visibility.Visible;
            SetSelectionMaskVisibility(Visibility.Collapsed);
        }
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

    private void HideSelectionControls()
    {
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
        CaptureToolbar.Visibility = Visibility.Collapsed;
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
        UpdateSelectionMask(GetSelectionBounds());
        ShowSelectionControls();
        CaptureSurface.Background = WpfBrushes.Transparent;
        CaptureShade.Visibility = Visibility.Collapsed;
        SetSelectionMaskVisibility(Visibility.Visible);
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
        UpdateSelectionBounds(new Rect(topLeft, bottomRight));
        SelectionRectangle.Visibility = Visibility.Visible;
        PrepareScrollCaptureSelection();
    }

    private async Task ApplyInitialInteractiveSelectionAsync(ScreenRegion selection)
    {
        if (_options is null || _isScrollCaptureSelection || _isCompleted)
        {
            return;
        }

        var topLeft = CaptureSurface.PointFromScreen(
            new WpfPoint(selection.X, selection.Y));
        var bottomRight = CaptureSurface.PointFromScreen(
            new WpfPoint(
                selection.X + selection.Width,
                selection.Y + selection.Height));
        UpdateSelectionBounds(new Rect(topLeft, bottomRight));
        if (!HasValidSelection())
        {
            ClearSelection();
            return;
        }

        SelectionRectangle.Visibility = Visibility.Visible;
        ShowSelectionControls();
        await EnterInlineEditorForCompletedSelectionAsync();
    }

    private void PrepareScrollCaptureSelection()
    {
        ShowSelectionControls();
        CaptureShade.Visibility = Visibility.Collapsed;
        SetSelectionMaskVisibility(Visibility.Visible);
        SaveButton.Visibility = Visibility.Collapsed;
        RecordButton.Visibility = Visibility.Collapsed;
        ScrollCaptureButton.Visibility = Visibility.Collapsed;
        PinButton.Visibility = Visibility.Collapsed;
        ConfirmButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        PublishScrollCaptureSelection();
        CaptureToolbar.Visibility = Visibility.Collapsed;
    }

    internal CapturedImage CaptureScrollSelectionSnapshot()
    {
        if (!_isScrollCaptureSelection || !HasValidSelection())
        {
            throw new InvalidOperationException("长截图选区无效。");
        }

        var selection = GetPhysicalSelectionBounds();

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

    private void LockScrollCaptureSelection()
    {
        if (_isScrollCaptureSelectionLocked)
        {
            return;
        }

        _isScrollCaptureSelectionLocked = true;
        HideSelectionControls();
        SelectionRectangle.Fill = WpfBrushes.Transparent;
        SelectionRectangle.IsHitTestVisible = false;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        FrozenScreenImage.Source = null;
        FrozenScreenImage.Visibility = Visibility.Collapsed;
        CaptureShade.Visibility = Visibility.Collapsed;
        SetSelectionMaskVisibility(Visibility.Visible);
        ScrollCaptureOutline.Visibility = Visibility.Visible;
        EnableScrollCaptureClickThrough();

        // Keep the frozen bitmap removed so both the selection and its shaded
        // surroundings show the same live window while it scrolls. The four
        // masks dim only the area outside the selected viewport.
        _screenSnapshot?.Dispose();
        _screenSnapshot = null;
    }

    private void UpdateSelectionMask(Rect bounds)
    {
        TopMask.Width = CaptureSurface.ActualWidth;
        TopMask.Height = Math.Max(0, bounds.Top);
        Canvas.SetLeft(TopMask, 0);
        Canvas.SetTop(TopMask, 0);

        BottomMask.Width = CaptureSurface.ActualWidth;
        BottomMask.Height = Math.Max(0, CaptureSurface.ActualHeight - bounds.Bottom);
        Canvas.SetLeft(BottomMask, 0);
        Canvas.SetTop(BottomMask, bounds.Bottom);

        LeftMask.Width = Math.Max(0, bounds.Left);
        LeftMask.Height = bounds.Height;
        Canvas.SetLeft(LeftMask, 0);
        Canvas.SetTop(LeftMask, bounds.Top);

        RightMask.Width = Math.Max(0, CaptureSurface.ActualWidth - bounds.Right);
        RightMask.Height = bounds.Height;
        Canvas.SetLeft(RightMask, bounds.Right);
        Canvas.SetTop(RightMask, bounds.Top);

        // Rectangle strokes occupy the inside of their layout bounds. Keep the
        // complete stroke plus an anti-aliasing gap outside the capture hole;
        // otherwise its cyan inner edge becomes a row in every sampled frame.
        if (_isScrollCaptureSelectionPublished)
        {
            var outlineOffset = ScrollCaptureOutline.StrokeThickness + 2;
            ScrollCaptureOutline.Width = bounds.Width + (outlineOffset * 2);
            ScrollCaptureOutline.Height = bounds.Height + (outlineOffset * 2);
            Canvas.SetLeft(ScrollCaptureOutline, bounds.Left - outlineOffset);
            Canvas.SetTop(ScrollCaptureOutline, bounds.Top - outlineOffset);
        }
    }

    private void SetSelectionMaskVisibility(Visibility visibility)
    {
        TopMask.Visibility = visibility;
        LeftMask.Visibility = visibility;
        RightMask.Visibility = visibility;
        BottomMask.Visibility = visibility;
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
        var holeRegion = NativeMethods.CreateRectRgn(
            holeLeft,
            holeTop,
            holeLeft + captureRegion.Width,
            holeTop + captureRegion.Height);

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

    private IntPtr OnScrollCaptureWindowMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
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
        var bounds = GetSelectionBounds();
        var start = CaptureSurface.PointToScreen(bounds.TopLeft);
        var end = CaptureSurface.PointToScreen(bounds.BottomRight);

        return ScreenRegion.FromCorners(
            (int)Math.Round(start.X),
            (int)Math.Round(start.Y),
            (int)Math.Round(end.X),
            (int)Math.Round(end.Y));
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

    private static Rect NormalizeBounds(Rect bounds)
    {
        return new Rect(
            Math.Min(bounds.Left, bounds.Right),
            Math.Min(bounds.Top, bounds.Bottom),
            Math.Abs(bounds.Width),
            Math.Abs(bounds.Height));
    }

    private void ResizeSelection(
        double leftChange,
        double topChange,
        double rightChange,
        double bottomChange)
    {
        BeginSelectionAdjustment();
        var bounds = GetSelectionBounds();
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

        UpdateSelectionBounds(new Rect(
            new WpfPoint(left, top),
            new WpfPoint(right, bottom)));
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
        Close();
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
