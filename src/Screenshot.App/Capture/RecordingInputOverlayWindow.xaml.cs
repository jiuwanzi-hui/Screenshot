using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Screenshot.App.Infrastructure;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Screenshot.App.Capture;

public partial class RecordingInputOverlayWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const int TopmostWindow = -1;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const int OverlayHeight = 48;
    private const int HorizontalMargin = 12;
    private const int BottomMargin = 16;

    private readonly DispatcherTimer _clearTimer;
    private readonly DrawingRectangle _windowBounds;
    private bool _isPaused;

    public RecordingInputOverlayWindow(ScreenRegion recordingRegion)
    {
        var width = Math.Min(
            420,
            Math.Max(2, recordingRegion.Width - (HorizontalMargin * 2)));
        var height = Math.Min(
            OverlayHeight,
            Math.Max(2, recordingRegion.Height - 8));
        var bottomMargin = Math.Min(
            BottomMargin,
            Math.Max(0, recordingRegion.Height - height));
        _windowBounds = new DrawingRectangle(
            recordingRegion.X + ((recordingRegion.Width - width) / 2),
            recordingRegion.Y + recordingRegion.Height - height - bottomMargin,
            width,
            height);
        var dpi = MonitorGeometryService.GetDpiScale(new DrawingRectangle(
            recordingRegion.X,
            recordingRegion.Y,
            recordingRegion.Width,
            recordingRegion.Height));
        Width = _windowBounds.Width / dpi.X;
        Height = _windowBounds.Height / dpi.Y;
        Left = _windowBounds.X / dpi.X;
        Top = _windowBounds.Y / dpi.Y;
        InitializeComponent();
        _clearTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(850),
        };
        _clearTimer.Tick += OnClearTimerTick;
        SourceInitialized += OnSourceInitialized;
    }

    public void ShowInput(string displayText, bool isTransient)
    {
        if (_isPaused)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(displayText))
        {
            _clearTimer.Stop();
            _clearTimer.Start();
            return;
        }

        _clearTimer.Stop();
        InputText.Text = displayText;
        InputSurface.Visibility = Visibility.Visible;
        InputSurface.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
        if (isTransient)
        {
            _clearTimer.Start();
        }
    }

    public void SetPaused(bool isPaused)
    {
        _isPaused = isPaused;
        if (isPaused)
        {
            ClearInput();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        _clearTimer.Stop();
        _clearTimer.Tick -= OnClearTimerTick;
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetWindowLongPtr(
            handle,
            ExtendedWindowStyleIndex).ToInt64();
        _ = NativeMethods.SetWindowLongPtr(
            handle,
            ExtendedWindowStyleIndex,
            new IntPtr(
                extendedStyle |
                ExtendedStyleTransparent |
                ExtendedStyleToolWindow |
                ExtendedStyleNoActivate));
        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(TopmostWindow),
            _windowBounds.X,
            _windowBounds.Y,
            _windowBounds.Width,
            _windowBounds.Height,
            DoNotActivate | DoNotChangeOwnerZOrder);
    }

    private void OnClearTimerTick(object? sender, EventArgs e)
    {
        _clearTimer.Stop();
        ClearInput();
    }

    private void ClearInput()
    {
        _clearTimer.Stop();
        InputSurface.BeginAnimation(OpacityProperty, null);
        InputSurface.Opacity = 0;
        InputSurface.Visibility = Visibility.Collapsed;
        InputText.Text = string.Empty;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(
            IntPtr window,
            int index,
            IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(
            IntPtr window,
            int index,
            int value);

        public static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
            IntPtr.Size == 8
                ? GetWindowLongPtr64(window, index)
                : new IntPtr(GetWindowLong32(window, index));

        public static IntPtr SetWindowLongPtr(
            IntPtr window,
            int index,
            IntPtr value) =>
            IntPtr.Size == 8
                ? SetWindowLongPtr64(window, index, value)
                : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));
    }
}
