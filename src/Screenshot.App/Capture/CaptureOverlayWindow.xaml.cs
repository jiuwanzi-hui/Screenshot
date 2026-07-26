using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Screenshot.App.Editor;
using Screenshot.App.Pin;
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

namespace Screenshot.App.Capture;

public sealed class CaptureOverlayOptions
{
    public required string SaveDirectory { get; init; }

    public required bool KeepHistory { get; init; }

    public required int HistoryLimit { get; init; }

    public required CaptureHistoryService HistoryService { get; init; }

    public required PinnedImageManager PinnedImageManager { get; init; }

    public required Func<CapturedImage, Task> StartOcrAsync { get; init; }

    public Func<CapturedImage, Task<OcrRecognitionResult>>? RecognizeTextAsync { get; init; }

    public Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
        TranslateTextAsync { get; init; }

    public bool RecognizeTextAfterSelection { get; init; }

    public Func<ScreenRegion, Task>? StartScrollCaptureAsync { get; init; }

    public Action? CaptureClosed { get; init; }
}

public partial class CaptureOverlayWindow : Window
{
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
    private readonly bool _isScrollCaptureSelection;
    private readonly TaskCompletionSource<ScreenRegion?> _selectionCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ScrollCaptureSelection?>?
        _scrollCaptureSelectionCompletionSource;
    private readonly DispatcherTimer _windowSnapTimer;
    private WpfPoint _selectionStartPoint;
    private WpfPoint _dragStartPoint;
    private Rect _dragStartBounds;
    private ScreenRegion? _selectionAdjustmentStartPhysicalBounds;
    private Rect? _selectionAdjustmentProtectedBounds;
    private CapturedImage? _inlineEditorImage;
    private CapturedImage? _screenSnapshot;
    private EditorTool _selectedInlineTool = EditorTool.Rectangle;
    private bool _isSelecting;
    private bool _isMovingSelection;
    private bool _isSelectionAdjustmentInProgress;
    private bool _isActionInProgress;
    private bool _isEditorInitializing;
    private bool _isOcrInitializing;
    private bool _isTranslationInitializing;
    private bool _completeAfterRightButtonUp;
    private bool _cancelScrollCaptureAfterRightButtonUp;
    private bool _isWindowSnapClickPending;
    private Rect? _windowSnapBounds;
    private IntPtr _windowHandle;
    private bool _isCompleted;
    private OcrRecognitionResult? _inlineOcrResult;
    private IReadOnlyList<OcrTextRegion>? _inlineTranslatedTextRegions;
    private IReadOnlyList<TranslatedTextAnnotationRegion>?
        _inlineTranslatedAnnotationRegions;
    private bool _isShowingTranslatedText;
    private bool _isScrollCaptureSelectionPublished;
    private bool _isScrollCaptureSelectionLocked;
    private bool _isScrollCaptureTemporarilyHidden;
    private WeakReference<ScrollCaptureSelection>?
        _publishedScrollCaptureSelection;
    private HwndSource? _windowSource;

    private CaptureOverlayWindow(
        CaptureOverlayOptions? options,
        bool isScrollCaptureSelection = false,
        CapturedImage? initialScreenSnapshot = null)
    {
        _options = options;
        _isScrollCaptureSelection = isScrollCaptureSelection;
        _scrollCaptureSelectionCompletionSource = isScrollCaptureSelection
            ? new TaskCompletionSource<ScrollCaptureSelection?>(
                TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        InitializeComponent();
        PopulateInlineEmojiPalette();
        _windowSnapTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _windowSnapTimer.Tick += OnWindowSnapTimerTick;
        ScrollCaptureButton.Visibility = options?.StartScrollCaptureAsync is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        InlineEditorCanvas.HistoryChanged += OnInlineEditorHistoryChanged;

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

    public static Task<ScreenRegion?> SelectAsync()
    {
        var overlay = new CaptureOverlayWindow(options: null);
        overlay.Show();
        return overlay._selectionCompletionSource.Task;
    }

    public static Task<ScrollCaptureSelection?> SelectForScrollCaptureAsync()
    {
        var overlay = new CaptureOverlayWindow(
            options: null,
            isScrollCaptureSelection: true);
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
        overlay.Show();
        return overlay;
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSnapTimer.Stop();
        _windowSnapTimer.Tick -= OnWindowSnapTimerTick;
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(OnScrollCaptureWindowMessage);
            _windowSource = null;
        }

        InlineEditorCanvas.HistoryChanged -= OnInlineEditorHistoryChanged;
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
        CaptureSurface.Focus();
        Keyboard.Focus(CaptureSurface);
        _windowSnapTimer.Start();
    }

    private void OnCaptureSurfaceKeyDown(object sender, WpfKeyEventArgs e)
    {
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
        else if (e.Key == Key.Enter && HasValidSelection())
        {
            ConfirmCurrentSelection();
            e.Handled = true;
        }
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
        if (_isScrollCaptureSelection && _isScrollCaptureSelectionPublished)
        {
            _cancelScrollCaptureAfterRightButtonUp = true;
            CaptureSurface.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (OcrTextOverlay.Visibility == Visibility.Visible)
        {
            OcrTextOverlay.Visibility = Visibility.Collapsed;
            CaptureStatusText.Text = "已隐藏可选择文字层。";
            CaptureStatusText.Visibility = Visibility.Visible;
            e.Handled = true;
            return;
        }

        e.Handled = true;
        ReturnToPreviousCaptureStateCore(deferFinalClose: true);
    }

    private void OnCaptureSurfacePreviewMouseRightButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
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
        if (!CanStartNewSelectionFromBackground(e.OriginalSource))
        {
            return;
        }

        _selectionStartPoint = e.GetPosition(CaptureSurface);
        _isSelecting = true;
        _windowSnapTimer.Stop();
        _isWindowSnapClickPending =
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
        e.Handled = true;
    }

    private void OnCaptureSurfaceMouseMove(object sender, WpfMouseEventArgs e)
    {
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
        if (!_isSelecting)
        {
            return;
        }

        var snappedBounds = _isWindowSnapClickPending
            ? _windowSnapBounds
            : null;
        _isWindowSnapClickPending = false;
        _isSelecting = false;
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
                e.GetPosition(CaptureSurface)));
        }

        if (!HasValidSelection())
        {
            HideSelectionControls();
            return;
        }

        e.Handled = true;
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
        await EnterInlineEditorForCompletedSelectionAsync();
    }

    private async Task EnterInlineEditorForCompletedSelectionAsync()
    {
        await EnterInlineEditorAsync();
        if (_options?.RecognizeTextAfterSelection == true &&
            _options.RecognizeTextAsync is not null &&
            InlineEditorCanvas.HasImage &&
            !_isCompleted)
        {
            await RecognizeInlineTextAsync(_options.RecognizeTextAsync);
        }
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
        // toolbar checkmark. In editor mode we let the canvas handle the click.
        if (e.ClickCount == 2 && !InlineEditorCanvas.HasImage)
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
            using var image = await CaptureCurrentResultAsync(restoreOverlay: false);
            _ = CaptureFileService.SaveAsPng(image, _options.SaveDirectory);
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
            Canvas.SetLeft(InlineEditorCanvas, selectionBounds.X);
            Canvas.SetTop(InlineEditorCanvas, selectionBounds.Y);
            InlineEditorCanvas.Visibility = Visibility.Visible;
            InlineEditorCanvas.Focus();
            LockSelectionForEditing();
        }
        catch
        {
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
        }
    }

    private async void OnSelectionResizeThumbDragCompleted(
        object sender,
        DragCompletedEventArgs e)
    {
        await CompleteSelectionAdjustmentAsync();
    }

    private async void OnOcrClick(object sender, RoutedEventArgs e)
    {
        if (_options is null)
        {
            return;
        }

        if (OcrTextOverlay.Visibility == Visibility.Visible)
        {
            OcrTextOverlay.Visibility = Visibility.Collapsed;
            CaptureStatusText.Text = "已隐藏可选择文字层。";
            CaptureStatusText.Visibility = Visibility.Visible;
            InlineEditorCanvas.Focus();
            return;
        }

        if (_inlineOcrResult is { IsSuccess: true, Regions.Count: > 0 })
        {
            ShowInlineOcrText(_inlineOcrResult);
            return;
        }

        if (_options.RecognizeTextAsync is not null)
        {
            await RecognizeInlineTextAsync(_options.RecognizeTextAsync);
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
            var ocrTask = _options.StartOcrAsync(image);
            image = null;
            CompleteSelection(result: null);
            await ocrTask;
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

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        if (_options?.RecognizeTextAsync is null ||
            _options.TranslateTextAsync is null)
        {
            CaptureStatusText.Text = "当前截图模式未配置翻译功能。";
            CaptureStatusText.Visibility = Visibility.Visible;
            return;
        }

        if (_inlineTranslatedTextRegions is { Count: > 0 } &&
            _inlineTranslatedAnnotationRegions is { Count: > 0 })
        {
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

            CaptureStatusText.Text = "文字识别完成，正在等待在线翻译服务返回...";
            var translationTimer = System.Diagnostics.Stopwatch.StartNew();
            var translation = await _options.TranslateTextAsync(recognition);
            translationTimer.Stop();
            if (!translation.IsSuccess)
            {
                CaptureStatusText.Text =
                    translation.ErrorMessage ?? "翻译失败。";
                return;
            }

            if (translation.Segments.Count != recognition.Regions.Count)
            {
                CaptureStatusText.Text = "翻译服务返回的分段结果不完整。";
                return;
            }

            _inlineTranslatedAnnotationRegions = recognition.Regions
                .Select((region, index) => new TranslatedTextAnnotationRegion(
                     new Rect(
                         Math.Max(0, region.X),
                         Math.Max(0, region.Y - 2),
                         Math.Max(12, region.Width),
                         Math.Max(22, region.Height + 10)),
                     translation.Segments[index],
                     Math.Max(10, region.Height * 0.78)))
                .ToArray();
            _inlineTranslatedTextRegions = recognition.Regions
                .Select((region, index) => new OcrTextRegion(
                    translation.Segments[index],
                    region.X,
                    Math.Max(0, region.Y - 2),
                    region.Width,
                    Math.Max(22, region.Height + 10)))
                .ToArray();
            InlineEditorCanvas.AddTranslationOverlay(
                _inlineTranslatedAnnotationRegions);
            SetInlineTranslationVisibility(isVisible: true);
            CaptureStatusText.Text +=
                $" 在线翻译耗时 {translationTimer.Elapsed.TotalSeconds:F1} 秒。";
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

        foreach (var region in regions)
        {
            var textWidth = Math.Max(12, region.Width * scaleX + 2);
            var textHeight = Math.Max(16, region.Height * scaleY + 2);
            var preferredFontSize = Math.Max(
                10,
                region.Height * scaleY * 0.78);
            var fontSize = isTranslation
                ? TranslationTextLayout.FitFontSize(
                    region.Text,
                    Math.Max(8, textWidth - 4),
                    Math.Max(8, textHeight - 2),
                    preferredFontSize)
                : preferredFontSize;
            var textBox = new System.Windows.Controls.TextBox
            {
                Text = region.Text,
                Width = textWidth,
                Height = textHeight,
                Padding = new Thickness(0),
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.IBeam,
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                FontSize = fontSize,
                Foreground = WpfBrushes.Transparent,
                IsReadOnly = true,
                SelectionBrush = new WpfSolidColorBrush(
                    WpfColor.FromArgb(120, 46, 175, 165)),
                SelectionTextBrush = WpfBrushes.Transparent,
                TextWrapping = isTranslation
                    ? TextWrapping.Wrap
                    : TextWrapping.NoWrap,
            };
            textBox.PreviewKeyDown += OnSelectableTextPreviewKeyDown;
            Canvas.SetLeft(textBox, region.X * scaleX);
            Canvas.SetTop(textBox, region.Y * scaleY);
            OcrTextOverlay.Children.Add(textBox);
        }

        _isShowingTranslatedText = isTranslation;
        OcrTextOverlay.Visibility = Visibility.Visible;
        CaptureStatusText.Text = isTranslation
            ? "译文已覆盖到截图；可直接拖选译文并按 Ctrl+C 复制，复制和保存会包含译文。"
            : "可直接拖选图片文字并按 Ctrl+C 复制；再次点击识字可隐藏文字层。";
        CaptureStatusText.Visibility = Visibility.Visible;
    }

    private async void OnSelectableTextPreviewKeyDown(
        object sender,
        WpfKeyEventArgs e)
    {
        if (e.Key != Key.C ||
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            sender is not System.Windows.Controls.TextBox textBox ||
            string.IsNullOrEmpty(textBox.SelectedText))
        {
            return;
        }

        e.Handled = true;
        try
        {
            await ClipboardTextService.SetTextAsync(textBox.SelectedText);
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
        _inlineOcrResult = null;
        _inlineTranslatedTextRegions = null;
        _inlineTranslatedAnnotationRegions = null;
        _isShowingTranslatedText = false;
        TranslateButtonText.Text = "译";
        TranslateButton.ToolTip = "识别并翻译，覆盖到截图";
        OcrTextOverlay.Children.Clear();
        OcrTextOverlay.Visibility = Visibility.Collapsed;
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
                using var image = await CaptureCurrentResultAsync(restoreOverlay: false);
                await ClipboardImageService.SetImageAsync(image.Preview);

                if (_options.KeepHistory)
                {
                    _ = _options.HistoryService.Add(image, _options.HistoryLimit);
                }
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

    private void OnInlineEditorToolSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string toolName } ||
            !Enum.TryParse<EditorTool>(toolName, out var tool))
        {
            return;
        }

        _selectedInlineTool = tool;
        OcrTextOverlay.Visibility = Visibility.Collapsed;
        UpdateInlineStrokeWidthText(InlineStrokeWidthSlider?.Value ?? 3);

        if (InlineEmojiPalette is not null && InlineStrokeOptions is not null)
        {
            var isEmoji = tool == EditorTool.Emoji;
            InlineEmojiPalette.Visibility = isEmoji
                ? Visibility.Visible
                : Visibility.Collapsed;
            InlineStrokeOptions.Visibility = isEmoji
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        if (InlineEditorCanvas.HasImage)
        {
            InlineEditorCanvas.SelectTool(tool);
            InlineEditorCanvas.Focus();
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
            UpdateInlineSelectedColorButton((System.Windows.Controls.Button)sender);
            InlineEditorCanvas.SelectColor(color);
            InlineEditorCanvas.Focus();
        }
    }

    private void OnInlineCustomColorClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            AnyColor = true,
            Color = DrawingColor.FromArgb(0, 127, 115),
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        var color = WpfColor.FromArgb(
            dialog.Color.A,
            dialog.Color.R,
            dialog.Color.G,
            dialog.Color.B);
        InlineCustomColorButton.Background = new WpfSolidColorBrush(color);
        UpdateInlineSelectedColorButton(InlineCustomColorButton);
        InlineEditorCanvas.SelectColor(color);
        InlineEditorCanvas.Focus();
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
        if (!InlineEditorCanvas.HasTranslationOverlay &&
            _inlineTranslatedTextRegions is not null &&
            _isShowingTranslatedText)
        {
            if (_inlineOcrResult is { IsSuccess: true, Regions.Count: > 0 } recognition)
            {
                ShowSelectableTextOverlay(recognition.Regions, isTranslation: false);
            }

            _isShowingTranslatedText = false;
            TranslateButtonText.Text = "译";
            TranslateButton.ToolTip = "显示已缓存的译文";
        }
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
        Canvas.SetLeft(SelectionRectangle, bounds.X);
        Canvas.SetTop(SelectionRectangle, bounds.Y);
        SelectionRectangle.Width = bounds.Width;
        SelectionRectangle.Height = bounds.Height;

        UpdateSelectionControlPositions(bounds);
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
            }
        }

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

        var toolbarX = Math.Min(
            Math.Max(0, bounds.Right - CaptureToolbar.ActualWidth),
            Math.Max(0, CaptureSurface.ActualWidth - CaptureToolbar.ActualWidth));
        var toolbarY = bounds.Bottom + 10;

        if (toolbarY + CaptureToolbar.ActualHeight > CaptureSurface.ActualHeight)
        {
            toolbarY = Math.Max(0, bounds.Y - CaptureToolbar.ActualHeight - 10);
        }

        Canvas.SetLeft(CaptureToolbar, toolbarX);
        Canvas.SetTop(CaptureToolbar, toolbarY);
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
        InlineEditorOptions.Visibility = Visibility.Visible;
        CaptureToolbar.UpdateLayout();
        UpdateSelectionControlPositions(GetSelectionBounds());
    }

    private void ClearSelection()
    {
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

        SelectionRectangle.IsHitTestVisible = true;
        SelectionRectangle.Fill = new WpfSolidColorBrush(
            WpfColor.FromArgb(24, 0, 127, 115));
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

    private void PrepareScrollCaptureSelection()
    {
        ShowSelectionControls();
        CaptureShade.Visibility = Visibility.Collapsed;
        SetSelectionMaskVisibility(Visibility.Visible);
        SaveButton.Visibility = Visibility.Collapsed;
        ScrollCaptureButton.Visibility = Visibility.Collapsed;
        OcrButton.Visibility = Visibility.Collapsed;
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
        ScrollCaptureOutline.Visibility = Visibility.Visible;
        EnableScrollCaptureClickThrough();

        // Drop the pre-overlay frozen snapshot once the selection is click-through.
        // Subsequent CaptureScrollSelectionSnapshot / live sample frames must read
        // the real window content under the hole, not the hotkey-time desktop image.
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

        // A transparent layered WPF window can still win native mouse hit
        // testing. Remove the selected viewport from the HWND region itself so
        // wheel and button input is routed directly to the application below.
        // The four shaded areas and the outline remain part of the window.
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

            // SetWindowRgn transfers ownership of a successful region to the OS.
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
        _selectionCompletionSource.TrySetResult(result);
        Close();
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
