using System.Windows;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Screenshot.App.Infrastructure;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Capture;

internal sealed partial class CameraPreviewOverlayWindow : Window
{
    private readonly ScreenRegion _recordingRegion;
    private readonly CameraCaptureService _camera;
    private readonly double _dpiX;
    private readonly double _dpiY;
    private readonly System.Windows.Threading.DispatcherTimer _frameTimer;
    private readonly System.Windows.Threading.DispatcherTimer _pointerUpdateTimer;
    private readonly object _frameSync = new();
    private readonly TaskCompletionSource<bool> _firstFrameReceived = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private BitmapSource? _pendingFrame;
    private bool _dragging;
    private bool _resizing;
    private bool _pointerUpdatePending;
    private System.Drawing.Point _lastDragCursorPx;
    private System.Drawing.Point _pendingCursorPx;
    private int _leftPx;
    private int _topPx;
    private int _widthPx;
    private int _heightPx;
    private int _initialLeftPx;
    private int _initialTopPx;
    private int _initialWidthPx;
    private int _initialHeightPx;
    private System.Drawing.Point _resizeStartCursorPx;
    private int _resizeStartLeftPx;
    private int _resizeStartTopPx;
    private int _resizeStartWidthPx;
    private int _resizeStartHeightPx;
    private string? _activeResizeName;
    private double _sourceAspectRatio = 4d / 3d;
    private bool _sourceAspectApplied;

    private CameraPreviewOverlayWindow(
        ScreenRegion recordingRegion,
        CameraCaptureService camera)
    {
        _recordingRegion = recordingRegion;
        _camera = camera;
        var dpi = MonitorGeometryService.GetDpiScale(new DrawingRectangle(
            recordingRegion.X,
            recordingRegion.Y,
            recordingRegion.Width,
            recordingRegion.Height));
        _dpiX = Math.Max(1, dpi.X);
        _dpiY = Math.Max(1, dpi.Y);
        InitializeComponent();
        _frameTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _frameTimer.Tick += OnFrameTimerTick;
        _frameTimer.Start();
        _pointerUpdateTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _pointerUpdateTimer.Tick += OnPointerUpdateTimerTick;
        _widthPx = Math.Clamp((int)Math.Round(recordingRegion.Width * 0.28), 160, 360);
        _heightPx = Math.Max(120, (int)Math.Round(_widthPx * 0.75));
        _leftPx = recordingRegion.X + recordingRegion.Width - _widthPx - 18;
        _topPx = recordingRegion.Y + recordingRegion.Height - _heightPx - 18;
        _initialLeftPx = _leftPx;
        _initialTopPx = _topPx;
        _initialWidthPx = _widthPx;
        _initialHeightPx = _heightPx;
        ApplyNativeBounds();
        _camera.FrameReady += OnFrameReady;
        SourceInitialized += (_, _) => ApplyNativeBounds();
    }

    internal static async Task<CameraPreviewOverlayWindow?> CreateAsync(
        ScreenRegion recordingRegion,
        string? cameraDeviceId = null)
    {
        var camera = await CameraCaptureService.CreateAsync(cameraDeviceId);
        if (camera is null)
        {
            return null;
        }

        return new CameraPreviewOverlayWindow(recordingRegion, camera);
    }

    internal void SetCameraVisible(bool visible)
    {
        Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        if (visible)
        {
            ClampToRecordingRegion();
            EnsureTopmost();
        }
    }

    internal async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout)
    {
        if (_firstFrameReceived.Task.IsCompleted)
        {
            return await _firstFrameReceived.Task;
        }

        var completed = await Task.WhenAny(
            _firstFrameReceived.Task,
            Task.Delay(timeout));
        return completed == _firstFrameReceived.Task &&
               await _firstFrameReceived.Task;
    }

    protected override async void OnClosed(EventArgs e)
    {
        _frameTimer.Stop();
        _pointerUpdateTimer.Stop();
        _pointerUpdateTimer.Tick -= OnPointerUpdateTimerTick;
        _camera.FrameReady -= OnFrameReady;
        _firstFrameReceived.TrySetResult(false);
        await _camera.DisposeAsync();
        base.OnClosed(e);
    }

    private void OnFrameReady(BitmapSource image)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        lock (_frameSync)
        {
            _pendingFrame = image;
        }
        _firstFrameReceived.TrySetResult(true);
    }

    private void OnFrameTimerTick(object? sender, EventArgs e)
    {
        // Camera frames arrive independently of pointer input. Avoid
        // replacing the Image source while the preview is being moved or
        // resized; that extra decode/render work competes with the drag and
        // makes the frame feel delayed. The newest frame remains queued.
        if (_dragging || _resizing)
        {
            return;
        }

        BitmapSource? image;
        lock (_frameSync)
        {
            image = _pendingFrame;
            _pendingFrame = null;
        }

        if (image is not null && IsLoaded)
        {
            ApplySourceAspectRatio(image.PixelWidth, image.PixelHeight);
            CameraImage.Source = image;
            CameraStatusText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Thumb)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            _dragging = false;
            _pointerUpdatePending = false;
            _pointerUpdateTimer.Stop();
            if (Mouse.Captured == this)
            {
                ReleaseMouseCapture();
            }

            _leftPx = _initialLeftPx;
            _topPx = _initialTopPx;
            _widthPx = _initialWidthPx;
            _heightPx = _initialHeightPx;
            ClampToRecordingRegion();
            ApplyNativeBounds();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _lastDragCursorPx = WinForms.Cursor.Position;
        _pendingCursorPx = _lastDragCursorPx;
        _pointerUpdateTimer.Start();
        Mouse.Capture(this, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = WinForms.Cursor.Position;
        _pendingCursorPx = current;
        _pointerUpdatePending = true;
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ApplyPendingPointerUpdate();
        _pointerUpdateTimer.Stop();
        ReleaseMouseCapture();
        ClampToRecordingRegion();
        ApplyNativeBounds(applySize: false);
        e.Handled = true;
    }

    private void OnPreviewMouseCaptureLost(object sender, WpfMouseEventArgs e)
    {
        // Repositioning a borderless HWND can make Windows revoke capture.
        // Clear the drag state immediately so a lost button-up cannot leave
        // the preview consuming subsequent input indefinitely.
        if (_resizing || !_dragging)
        {
            return;
        }

        _dragging = false;
        _pointerUpdatePending = false;
        _pointerUpdateTimer.Stop();
    }

    private void OnResizeDragStarted(object sender, DragStartedEventArgs e)
    {
        _resizing = true;
        _activeResizeName = ((FrameworkElement)sender).Name;
        _resizeStartCursorPx = WinForms.Cursor.Position;
        _resizeStartLeftPx = _leftPx;
        _resizeStartTopPx = _topPx;
        _resizeStartWidthPx = _widthPx;
        _resizeStartHeightPx = _heightPx;
    }

    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_resizing || string.IsNullOrEmpty(_activeResizeName))
        {
            OnResizeDragStarted(sender, new DragStartedEventArgs(0, 0));
        }

        var current = WinForms.Cursor.Position;
        var dx = current.X - _resizeStartCursorPx.X;
        var dy = current.Y - _resizeStartCursorPx.Y;
        var name = _activeResizeName!;
        var left = _resizeStartLeftPx;
        var top = _resizeStartTopPx;
        var right = left + _resizeStartWidthPx;
        var bottom = top + _resizeStartHeightPx;

        var isCorner =
            (name.Contains("Left", StringComparison.Ordinal) ||
             name.Contains("Right", StringComparison.Ordinal)) &&
            (name.Contains("Top", StringComparison.Ordinal) ||
             name.Contains("Bottom", StringComparison.Ordinal));
        if (isCorner)
        {
            ResizeCornerPreservingAspectFromStart(
                name,
                dx,
                dy,
                left,
                top,
                right,
                bottom);
            ApplyNativeBounds(applySize: false);
            return;
        }

        if (name.Contains("Left", StringComparison.Ordinal))
        {
            left += dx;
        }
        else if (name.Contains("Right", StringComparison.Ordinal))
        {
            right += dx;
        }

        if (name.Contains("Top", StringComparison.Ordinal))
        {
            top += dy;
        }
        else if (name.Contains("Bottom", StringComparison.Ordinal))
        {
            bottom += dy;
        }

        var maxWidth = Math.Max(160, _recordingRegion.Width);
        var maxHeight = Math.Max(120, _recordingRegion.Height);
        if (right - left < 160)
        {
            if (name.Contains("Left", StringComparison.Ordinal)) left = right - 160;
            else right = left + 160;
        }
        if (bottom - top < 120)
        {
            if (name.Contains("Top", StringComparison.Ordinal)) top = bottom - 120;
            else bottom = top + 120;
        }

        _leftPx = left;
        _topPx = top;
        _widthPx = Math.Min(maxWidth, right - left);
        _heightPx = Math.Min(maxHeight, bottom - top);
        if (_widthPx < 160) _widthPx = 160;
        if (_heightPx < 120) _heightPx = 120;
        ClampToRecordingRegion();
        // Resize synchronously so the border and cursor stay under the
        // pointer. The native window move avoids a WPF layout pass.
        ApplyNativeBounds(applySize: false);
    }

    private void ResizeCornerPreservingAspectFromStart(
        string name,
        int dx,
        int dy,
        int left,
        int top,
        int right,
        int bottom)
    {
        var horizontalChange = name.Contains("Left", StringComparison.Ordinal)
            ? -dx
            : dx;
        var verticalChange = name.Contains("Top", StringComparison.Ordinal)
            ? -dy
            : dy;
        var widthFromHorizontal = _resizeStartWidthPx + horizontalChange;
        var widthFromVertical =
            (_resizeStartHeightPx + verticalChange) * _sourceAspectRatio;
        var requestedWidth = Math.Abs(horizontalChange) >=
                             Math.Abs(verticalChange * _sourceAspectRatio)
            ? widthFromHorizontal
            : widthFromVertical;

        var minimumWidth = Math.Max(80, (int)Math.Ceiling(90 * _sourceAspectRatio));
        var maximumWidth = GetMaximumCornerWidth(name, left, top, right, bottom);
        var width = (int)Math.Round(Math.Clamp(
            requestedWidth,
            Math.Min(minimumWidth, maximumWidth),
            maximumWidth));
        var height = Math.Max(1, (int)Math.Round(width / _sourceAspectRatio));

        if (name.Contains("Left", StringComparison.Ordinal))
        {
            _leftPx = right - width;
        }
        else
        {
            _leftPx = left;
        }

        if (name.Contains("Top", StringComparison.Ordinal))
        {
            _topPx = bottom - height;
        }
        else
        {
            _topPx = top;
        }

        _widthPx = width;
        _heightPx = height;
        ClampToRecordingRegion();
    }

    private int GetMaximumCornerWidth(
        string name,
        int left,
        int top,
        int right,
        int bottom)
    {
        var horizontalRoom = name.Contains("Left", StringComparison.Ordinal)
            ? right - _recordingRegion.X
            : _recordingRegion.X + _recordingRegion.Width - left;
        var verticalRoom = name.Contains("Top", StringComparison.Ordinal)
            ? bottom - _recordingRegion.Y
            : _recordingRegion.Y + _recordingRegion.Height - top;
        return Math.Max(1, Math.Min(
            horizontalRoom,
            (int)Math.Floor(verticalRoom * _sourceAspectRatio)));
    }

    private void ApplySourceAspectRatio(int pixelWidth, int pixelHeight)
    {
        if (_sourceAspectApplied || pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        var ratio = (double)pixelWidth / pixelHeight;
        if (!double.IsFinite(ratio) || ratio < 0.25 || ratio > 4)
        {
            return;
        }

        _sourceAspectRatio = ratio;
        _sourceAspectApplied = true;
        var availableWidth = Math.Max(1, _recordingRegion.Width - 36);
        var availableHeight = Math.Max(1, _recordingRegion.Height - 36);
        var width = Math.Min(_widthPx, availableWidth);
        var height = (int)Math.Round(width / ratio);
        if (height > availableHeight)
        {
            height = availableHeight;
            width = (int)Math.Round(height * ratio);
        }

        _widthPx = Math.Max(1, width);
        _heightPx = Math.Max(1, height);
        _leftPx = _recordingRegion.X + _recordingRegion.Width - _widthPx -
                  Math.Min(18, Math.Max(0, _recordingRegion.Width - _widthPx));
        _topPx = _recordingRegion.Y + _recordingRegion.Height - _heightPx -
                 Math.Min(18, Math.Max(0, _recordingRegion.Height - _heightPx));
        ClampToRecordingRegion();
        _initialLeftPx = _leftPx;
        _initialTopPx = _topPx;
        _initialWidthPx = _widthPx;
        _initialHeightPx = _heightPx;
        ApplyNativeBounds();
    }

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _resizing = false;
        _activeResizeName = null;
        _pointerUpdateTimer.Stop();
        ClampToRecordingRegion();
        ApplyNativeBounds();
    }

    private void OnPointerUpdateTimerTick(object? sender, EventArgs e)
    {
        if (!_pointerUpdatePending)
        {
            return;
        }

        ApplyPendingPointerUpdate();
    }

    private void ApplyPendingPointerUpdate()
    {
        if (!_pointerUpdatePending)
        {
            return;
        }

        _pointerUpdatePending = false;
        if (_dragging)
        {
            var current = _pendingCursorPx;
            _leftPx += current.X - _lastDragCursorPx.X;
            _topPx += current.Y - _lastDragCursorPx.Y;
            _lastDragCursorPx = current;
            ClampToRecordingRegion();
        }

        ApplyNativeBounds(applySize: false);
    }

    private void ClampToRecordingRegion()
    {
        var right = _recordingRegion.X + _recordingRegion.Width;
        var bottom = _recordingRegion.Y + _recordingRegion.Height;
        _widthPx = Math.Min(_widthPx, Math.Max(1, _recordingRegion.Width));
        _heightPx = Math.Min(_heightPx, Math.Max(1, _recordingRegion.Height));
        _leftPx = Math.Clamp(_leftPx, _recordingRegion.X, Math.Max(_recordingRegion.X, right - _widthPx));
        _topPx = Math.Clamp(_topPx, _recordingRegion.Y, Math.Max(_recordingRegion.Y, bottom - _heightPx));
    }

    private void ApplyNativeBounds(bool applySize = true)
    {
        if (applySize)
        {
            Width = _widthPx / _dpiX;
            Height = _heightPx / _dpiY;
        }
        if (!IsLoaded)
        {
            if (applySize)
            {
                Left = _leftPx / _dpiX;
                Top = _topPx / _dpiY;
            }
            return;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            new IntPtr(-1),
            _leftPx,
            _topPx,
            _widthPx,
            _heightPx,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpNoOwnerZOrder |
            NativeMethods.SwpNoSendChanging);
    }

    internal void EnsureTopmost()
    {
        if (!IsLoaded)
        {
            return;
        }

        Topmost = true;
        ApplyNativeBounds();
    }

    private static class NativeMethods
    {
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpNoOwnerZOrder = 0x0200;
        internal const uint SwpNoSendChanging = 0x0400;
        internal const uint SwpAsyncWindowPos = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);
    }
}
