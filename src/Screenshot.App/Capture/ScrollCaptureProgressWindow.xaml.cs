using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Screenshot.App.Capture;

public partial class ScrollCaptureProgressWindow : Window
{
    private const int PositionGap = 12;
    private const int PreviewPhysicalWidth = 300;
    private const int MinimumPreviewPhysicalWidth = 200;
    private const int TopmostWindow = -1;
    private const uint DoNotResize = 0x0001;
    private const uint DoNotMove = 0x0002;
    private const uint DoNotChangeZOrder = 0x0004;
    private const uint DoNotActivate = 0x0010;
    private const uint BringToFrontFlags =
        DoNotResize |
        DoNotMove |
        DoNotActivate;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private bool _allowClose;
    private bool _isClosed;
    private bool _isRequestRaised;
    private bool _cancelAfterRightButtonUp;
    private ScrollCapturePreviewState? _pendingPreviewState;
    private int _previewUpdateScheduled;

    public ScrollCaptureProgressWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? CompleteRequested;

    public event EventHandler? EditRequested;

    public event EventHandler? CancelRequested;

    public void ConfigureForCaptureRegion(ScreenRegion captureRegion)
    {
        var monitorBounds = GetMonitorWorkArea(captureRegion);
        var maximumHeight = Math.Max(
            280,
            Math.Min(640, monitorBounds.Height - (PositionGap * 2)));
        var physicalHeight = Math.Clamp(
            captureRegion.Height,
            280,
            maximumHeight);
        var physicalWidth = GetPreviewPhysicalWidth(
            captureRegion,
            monitorBounds);
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = physicalWidth / dpi.DpiScaleX;
        Height = physicalHeight / dpi.DpiScaleY;
        UpdateLayout();
    }

    public void UpdatePreview(ScrollCapturePreviewState previewState)
    {
        ArgumentNullException.ThrowIfNull(previewState);
        PreviewImage.Source = previewState.Preview;
        FrameCountText.Text =
            $"{previewState.FrameCount} 帧 · 上 {previewState.AddedAboveFrameCount} · " +
            $"下 {previewState.AddedBelowFrameCount} · " +
            $"{previewState.PixelWidth}×{previewState.PixelHeight}";
    }

    public void QueuePreview(ScrollCapturePreviewState previewState)
    {
        ArgumentNullException.ThrowIfNull(previewState);
        Interlocked.Exchange(ref _pendingPreviewState, previewState);
        SchedulePreviewUpdate();
    }

    private void SchedulePreviewUpdate()
    {
        if (Interlocked.CompareExchange(ref _previewUpdateScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            ApplyLatestPreview);
    }

    private void ApplyLatestPreview()
    {
        var latest = Interlocked.Exchange(ref _pendingPreviewState, null);
        if (latest is not null && IsVisible)
        {
            UpdatePreview(latest);
        }

        Interlocked.Exchange(ref _previewUpdateScheduled, 0);
        if (Volatile.Read(ref _pendingPreviewState) is not null)
        {
            SchedulePreviewUpdate();
        }
    }

    public bool TryPositionOutside(ScreenRegion captureRegion)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;

        if (windowHandle == IntPtr.Zero ||
            !NativeMethods.GetWindowRect(windowHandle, out var windowBounds))
        {
            return false;
        }

        var width = windowBounds.Right - windowBounds.Left;
        var height = windowBounds.Bottom - windowBounds.Top;
        var monitorBounds = GetMonitorWorkArea(captureRegion);
        var previewBounds = ChoosePreviewBounds(
            captureRegion,
            monitorBounds,
            width,
            height);
        MoveWindow(windowHandle, previewBounds.X, previewBounds.Y);

        // Also set WPF coordinates so layout/measure does not snap the window
        // back to the default Manual origin on the next render pass.
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = previewBounds.X / dpi.DpiScaleX;
        Top = previewBounds.Y / dpi.DpiScaleY;
        return ScreenRegion.Intersect(previewBounds, captureRegion).IsEmpty;
    }

    /// <summary>
    /// Keeps the live preview above the full-screen capture overlay without
    /// activating it or taking focus away from the scroll target.
    /// </summary>
    public void BringToFront()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            windowHandle,
            new IntPtr(TopmostWindow),
            0,
            0,
            0,
            0,
            BringToFrontFlags);
    }

    public bool ContainsScreenPoint(int x, int y)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        return windowHandle != IntPtr.Zero &&
               NativeMethods.GetWindowRect(windowHandle, out var bounds) &&
               x >= bounds.Left &&
               y >= bounds.Top &&
               x < bounds.Right &&
               y < bounds.Bottom;
    }

    public void CloseFromCoordinator()
    {
        if (_isClosed)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;

            if (BeginRequest("正在取消"))
            {
                CancelRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        base.OnClosing(e);
    }

    private void OnCompleteClick(object sender, RoutedEventArgs e)
    {
        if (!BeginRequest("正在生成滚动截图"))
        {
            return;
        }

        CompleteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (!BeginRequest("正在生成滚动截图"))
        {
            return;
        }

        EditRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (!BeginRequest("正在取消"))
        {
            return;
        }

        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviewMouseRightButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        _cancelAfterRightButtonUp = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseRightButtonUp(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_cancelAfterRightButtonUp)
        {
            return;
        }

        _cancelAfterRightButtonUp = false;
        ReleaseMouseCapture();
        e.Handled = true;
        if (BeginRequest("正在取消"))
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool BeginRequest(string status)
    {
        if (_isRequestRaised)
        {
            return false;
        }

        _isRequestRaised = true;
        StatusText.Text = status;
        ActionPanel.IsEnabled = false;
        return true;
    }

    private static bool Contains(ScreenRegion outer, ScreenRegion inner)
    {
        return inner.X >= outer.X &&
               inner.Y >= outer.Y &&
               inner.X + inner.Width <= outer.X + outer.Width &&
               inner.Y + inner.Height <= outer.Y + outer.Height;
    }

    private static ScreenRegion ChoosePreviewBounds(
        ScreenRegion captureRegion,
        ScreenRegion monitorBounds,
        int width,
        int height)
    {
        // Prefer vertically aligned with the selection, clamped into the work area.
        var maxY = Math.Max(
            monitorBounds.Y,
            monitorBounds.Y + monitorBounds.Height - height);
        var centeredY = Math.Clamp(
            captureRegion.Y + ((captureRegion.Height - height) / 2),
            monitorBounds.Y,
            maxY);

        // Stick to the right edge of the selection whenever any usable gap exists.
        // Previously a full-rectangle Contains check would reject a near-fit right
        // placement and dump the panel to the far monitor edge, away from the region.
        var rightX = captureRegion.X + captureRegion.Width + PositionGap;
        var rightSpace = monitorBounds.X + monitorBounds.Width - rightX;
        if (rightSpace >= Math.Min(width, MinimumPreviewPhysicalWidth))
        {
            var clampedWidth = Math.Min(width, Math.Max(MinimumPreviewPhysicalWidth, rightSpace));
            return new ScreenRegion(rightX, centeredY, clampedWidth, height);
        }

        var leftX = captureRegion.X - width - PositionGap;
        var leftSpace = captureRegion.X - monitorBounds.X - PositionGap;
        if (leftSpace >= Math.Min(width, MinimumPreviewPhysicalWidth))
        {
            var clampedWidth = Math.Min(width, Math.Max(MinimumPreviewPhysicalWidth, leftSpace));
            var x = captureRegion.X - clampedWidth - PositionGap;
            return new ScreenRegion(x, centeredY, clampedWidth, height);
        }

        // Last resort: still prefer the side with more room, glued as close as possible.
        var preferRight = rightSpace >= leftSpace;
        if (preferRight)
        {
            var x = Math.Min(
                rightX,
                monitorBounds.X + monitorBounds.Width - width);
            x = Math.Max(monitorBounds.X, x);
            return new ScreenRegion(x, centeredY, width, height);
        }

        var leftFallbackX = Math.Max(monitorBounds.X, leftX);
        return new ScreenRegion(leftFallbackX, centeredY, width, height);
    }

    private static int GetPreviewPhysicalWidth(
        ScreenRegion captureRegion,
        ScreenRegion monitorBounds)
    {
        var rightSpace = monitorBounds.X + monitorBounds.Width -
                         (captureRegion.X + captureRegion.Width) -
                         PositionGap;
        if (rightSpace >= MinimumPreviewPhysicalWidth)
        {
            return Math.Min(PreviewPhysicalWidth, rightSpace);
        }

        var leftSpace = captureRegion.X - monitorBounds.X - PositionGap;
        if (leftSpace >= MinimumPreviewPhysicalWidth)
        {
            return Math.Min(PreviewPhysicalWidth, leftSpace);
        }

        return Math.Min(PreviewPhysicalWidth, monitorBounds.Width);
    }

    private static ScreenRegion GetMonitorWorkArea(ScreenRegion captureRegion)
    {
        var captureBounds = new NativeRect
        {
            Left = captureRegion.X,
            Top = captureRegion.Y,
            Right = captureRegion.X + captureRegion.Width,
            Bottom = captureRegion.Y + captureRegion.Height,
        };
        var monitorHandle = NativeMethods.MonitorFromRect(
            ref captureBounds,
            MonitorDefaultToNearest);
        var monitorInfo = new NativeMonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMonitorInfo>(),
        };
        if (monitorHandle == IntPtr.Zero ||
            !NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return VirtualScreen.GetBounds();
        }

        return new ScreenRegion(
            monitorInfo.Work.Left,
            monitorInfo.Work.Top,
            monitorInfo.Work.Right - monitorInfo.Work.Left,
            monitorInfo.Work.Bottom - monitorInfo.Work.Top);
    }

    private static void MoveWindow(IntPtr windowHandle, int x, int y)
    {
        _ = NativeMethods.SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            DoNotResize | DoNotChangeZOrder | DoNotActivate);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(
            IntPtr windowHandle,
            out NativeRect rectangle);

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

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromRect(
            ref NativeRect rectangle,
            uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(
            IntPtr monitorHandle,
            ref NativeMonitorInfo monitorInfo);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
