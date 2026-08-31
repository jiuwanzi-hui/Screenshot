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
    // Layout minimums are logical (DIP) sizes: the header text and the action
    // buttons need this much room regardless of monitor scale. Physical sizes
    // are derived from these with the monitor's DPI scale — sizing the window
    // in raw pixels clipped the buttons in half at 200% scaling.
    private const int PreviewWidthDip = 300;
    private const int MinimumPreviewWidthDip = 200;
    private const int MinimumWindowHeightDip = 280;
    private const int PreferredMaximumWindowHeightDip = 640;
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
    private double _maximumHeightDip;
    private double _workAreaTopDip;
    private double _workAreaBottomDip;
    private bool _captureExcluded;

    public ScrollCaptureProgressWindow()
    {
        InitializeComponent();
    }

    public void ExcludeFromScreenCapture()
    {
        if (_captureExcluded)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (handle != IntPtr.Zero)
        {
            _captureExcluded = NativeMethods.SetWindowDisplayAffinity(
                handle,
                WindowDisplayAffinityExcludeFromCapture);
        }
    }

    public event EventHandler? CompleteRequested;

    public event EventHandler? EditRequested;

    public event EventHandler? CancelRequested;

    public void ConfigureManualWheelMode()
    {
        StatusText.Text = "手动滚轮长截图";
        InstructionText.Text = "在选区内自由滚动鼠标滚轮，可随时上下往返；完成后点击下方完成按钮";
    }

    public void ConfigureForCaptureRegion(ScreenRegion captureRegion)
    {
        var monitorBounds = GetMonitorWorkArea(captureRegion);
        var dpi = VisualTreeHelper.GetDpi(this);
        var minimumPhysicalHeight = (int)Math.Round(
            MinimumWindowHeightDip * dpi.DpiScaleY);
        var preferredMaximumPhysicalHeight = (int)Math.Round(
            PreferredMaximumWindowHeightDip * dpi.DpiScaleY);
        var maximumHeight = Math.Max(
            minimumPhysicalHeight,
            monitorBounds.Height - (PositionGap * 2));
        var physicalHeight = Math.Clamp(
            captureRegion.Height,
            Math.Min(minimumPhysicalHeight, maximumHeight),
            Math.Min(preferredMaximumPhysicalHeight, maximumHeight));
        var physicalWidth = GetPreviewPhysicalWidth(
            captureRegion,
            monitorBounds,
            dpi.DpiScaleX);
        Width = physicalWidth / dpi.DpiScaleX;
        Height = physicalHeight / dpi.DpiScaleY;
        _maximumHeightDip = maximumHeight / dpi.DpiScaleY;
        _workAreaTopDip = monitorBounds.Y / dpi.DpiScaleY;
        _workAreaBottomDip =
            (monitorBounds.Y + monitorBounds.Height) / dpi.DpiScaleY;
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
        GrowToFitPreview(previewState);
    }

    /// <summary>
    /// Grows the window toward the work-area height as the stitched image
    /// gets taller, so the whole-image preview keeps as much width as the
    /// screen allows instead of shrinking inside a selection-sized pane.
    /// Grow-only: shrinking mid-capture would make the window jitter.
    /// </summary>
    private void GrowToFitPreview(ScrollCapturePreviewState previewState)
    {
        if (previewState.PixelWidth <= 0 ||
            previewState.PixelHeight <= 0 ||
            _maximumHeightDip <= 0)
        {
            return;
        }

        // Header row (78) + action row (54) + image margins and border.
        const double ChromeHeight = 78 + 54 + 16;
        var imagePaneWidth = Math.Max(80, ActualWidth - 36);
        var aspect = previewState.PixelHeight / (double)previewState.PixelWidth;
        var desiredHeight = Math.Clamp(
            (imagePaneWidth * aspect) + ChromeHeight,
            ActualHeight,
            _maximumHeightDip);

        if (desiredHeight <= ActualHeight + 4)
        {
            return;
        }

        Height = desiredHeight;

        if (_workAreaBottomDip > _workAreaTopDip)
        {
            var maximumTop = _workAreaBottomDip - desiredHeight;
            Top = Math.Max(_workAreaTopDip, Math.Min(Top, maximumTop));
        }
    }

    public void QueuePreview(ScrollCapturePreviewState previewState)
    {
        ArgumentNullException.ThrowIfNull(previewState);
        Interlocked.Exchange(ref _pendingPreviewState, previewState);
        SchedulePreviewUpdate();
    }

    public void QueueInteractionState(ControlledScrollCaptureState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                () => QueueInteractionState(state));
            return;
        }

        if (_isClosed)
        {
            return;
        }

        (StatusText.Text, InstructionText.Text) = state switch
        {
            ControlledScrollCaptureState.WaitingToStart =>
                ("滚动截图 · 等待选择方向", "单击向下；双击向上；右键取消"),
            ControlledScrollCaptureState.ScrollingDown =>
                ("正在匀速向下滚动", "单击暂停；双击自动停稳后返回"),
            ControlledScrollCaptureState.PreparingPauseDown =>
                ("正在停止向下滚动", "正在补齐暂停位置的最后内容"),
            ControlledScrollCaptureState.PausedDown =>
                ("已暂停 · 向下阶段", "单击继续；双击返回并向上拼接"),
            ControlledScrollCaptureState.BottomReached =>
                ("已到达底部", "双击截取区域，开始向上拼接"),
            ControlledScrollCaptureState.PreparingReturnFromDown =>
                ("正在停止向下滚动", "停稳后自动返回初始位置"),
            ControlledScrollCaptureState.ReturningToStart =>
                ("正在快速返回初始位置", "单击暂停；返回期间不重复写入"),
            ControlledScrollCaptureState.PausedReturning =>
                ("已暂停 · 返回阶段", "单击继续快速返回初始位置"),
            ControlledScrollCaptureState.AligningUpwardStart =>
                ("正在对齐初始位置", "停稳后开始向上采集"),
            ControlledScrollCaptureState.ScrollingUp =>
                ("正在匀速向上采集", "单击暂停"),
            ControlledScrollCaptureState.PreparingPauseUp =>
                ("正在停止向上滚动", "正在补齐暂停位置的最后内容"),
            ControlledScrollCaptureState.PausedUp =>
                ("已暂停 · 向上采集", "单击继续"),
            ControlledScrollCaptureState.ScrollingUpFirst =>
                ("正在匀速向上滚动", "单击暂停；双击自动停稳后返回"),
            ControlledScrollCaptureState.PreparingPauseUpFirst =>
                ("正在停止向上滚动", "正在补齐暂停位置的最后内容"),
            ControlledScrollCaptureState.PausedUpFirst =>
                ("已暂停 · 向上阶段", "单击继续；双击返回并向下拼接"),
            ControlledScrollCaptureState.TopReached =>
                ("已到达顶部", "双击截取区域，开始向下拼接"),
            ControlledScrollCaptureState.PreparingReturnFromUp =>
                ("正在停止向上滚动", "停稳后自动返回初始位置"),
            ControlledScrollCaptureState.ReturningDownToStart =>
                ("正在快速返回初始位置", "单击暂停；返回期间不重复写入"),
            ControlledScrollCaptureState.PausedReturningDown =>
                ("已暂停 · 返回阶段", "单击继续快速返回初始位置"),
            ControlledScrollCaptureState.AligningDownwardStart =>
                ("正在对齐初始位置", "停稳后开始向下采集"),
            ControlledScrollCaptureState.ScrollingDownSecond =>
                ("正在匀速向下采集", "单击暂停"),
            ControlledScrollCaptureState.PreparingPauseDownSecond =>
                ("正在停止向下滚动", "正在补齐暂停位置的最后内容"),
            ControlledScrollCaptureState.PausedDownSecond =>
                ("已暂停 · 向下采集", "单击继续"),
            ControlledScrollCaptureState.FinalTopReached =>
                ("已到达顶部", "请选择编辑、完成或取消"),
            ControlledScrollCaptureState.FinalBottomReached =>
                ("已到达底部", "请选择编辑、完成或取消"),
            ControlledScrollCaptureState.InputUnavailable =>
                ("连续滚动输入不可用", "请选择编辑、完成或取消"),
            ControlledScrollCaptureState.Completing =>
                ("正在生成滚动截图", "请稍候"),
            _ => (StatusText.Text, InstructionText.Text),
        };
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
            // The selection mask is a separate topmost native HWND and may
            // reorder this WPF window below it while a frame is being sampled.
            // Restore z-order on the dispatcher immediately before painting
            // the coalesced preview update.
            BringToFront();
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
        var dpi = VisualTreeHelper.GetDpi(this);
        var previewBounds = ChoosePreviewBounds(
            captureRegion,
            monitorBounds,
            width,
            height,
            (int)Math.Round(MinimumPreviewWidthDip * dpi.DpiScaleX));
        MoveWindow(windowHandle, previewBounds.X, previewBounds.Y);

        // Also set WPF coordinates so layout/measure does not snap the window
        // back to the default Manual origin on the next render pass.
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
        int height,
        int minimumPhysicalWidth)
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
        if (rightSpace >= Math.Min(width, minimumPhysicalWidth))
        {
            var clampedWidth = Math.Min(width, Math.Max(minimumPhysicalWidth, rightSpace));
            return new ScreenRegion(rightX, centeredY, clampedWidth, height);
        }

        var leftX = captureRegion.X - width - PositionGap;
        var leftSpace = captureRegion.X - monitorBounds.X - PositionGap;
        if (leftSpace >= Math.Min(width, minimumPhysicalWidth))
        {
            var clampedWidth = Math.Min(width, Math.Max(minimumPhysicalWidth, leftSpace));
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
        ScreenRegion monitorBounds,
        double dpiScaleX)
    {
        var preferredWidth = (int)Math.Round(PreviewWidthDip * dpiScaleX);
        var minimumWidth = (int)Math.Round(MinimumPreviewWidthDip * dpiScaleX);
        var rightSpace = monitorBounds.X + monitorBounds.Width -
                         (captureRegion.X + captureRegion.Width) -
                         PositionGap;
        if (rightSpace >= minimumWidth)
        {
            return Math.Min(preferredWidth, rightSpace);
        }

        var leftSpace = captureRegion.X - monitorBounds.X - PositionGap;
        if (leftSpace >= minimumWidth)
        {
            return Math.Min(preferredWidth, leftSpace);
        }

        return Math.Min(preferredWidth, monitorBounds.Width);
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

    private const uint WindowDisplayAffinityExcludeFromCapture = 0x00000011;

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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowDisplayAffinity(
            IntPtr windowHandle,
            uint affinity);
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
