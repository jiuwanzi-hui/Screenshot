using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Interop;
using Screenshot.App.Infrastructure;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingColor = System.Drawing.Color;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.Brush;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Screenshot.App.Capture;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF window disposes its native frame in OnClosed.")]
public partial class RecordingRegionFrameWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int NonClientHitTestMessage = 0x0084;
    private const int HitTestTransparent = -1;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const int TopmostWindow = -1;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    // Keep the complete stroke outside the encoded region. Matching the
    // margin to the stroke removes the visible gap that looked like a
    // second recording frame.
    private const int FrameMargin = 3;
    private readonly ScreenRegion _windowRegion;
    private readonly NativeSelectionFrameWindow _nativeFrame;
    private HwndSource? _windowSource;

    public RecordingRegionFrameWindow(ScreenRegion recordingRegion)
    {
        _windowRegion = new ScreenRegion(
            recordingRegion.X - FrameMargin,
            recordingRegion.Y - FrameMargin,
            recordingRegion.Width + (FrameMargin * 2),
            recordingRegion.Height + (FrameMargin * 2));
        var dpi = MonitorGeometryService.GetDpiScale(new DrawingRectangle(
            recordingRegion.X,
            recordingRegion.Y,
            recordingRegion.Width,
            recordingRegion.Height));
        Width = _windowRegion.Width / dpi.X;
        Height = _windowRegion.Height / dpi.Y;
        Left = _windowRegion.X / dpi.X;
        Top = _windowRegion.Y / dpi.Y;
        InitializeComponent();
        _nativeFrame = new NativeSelectionFrameWindow();
        if (TryGetAccentColors(out var accentStart, out var accentEnd))
        {
            _nativeFrame.SetBorderColors(
                DrawingColor.FromArgb(accentStart.R, accentStart.G, accentStart.B),
                DrawingColor.FromArgb(accentEnd.R, accentEnd.G, accentEnd.B));
        }
        FrameBorder.Opacity = 0;
        Opacity = 0;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(OnWindowMessage);
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
            _windowRegion.X,
            _windowRegion.Y,
            _windowRegion.Width,
            _windowRegion.Height,
            DoNotActivate | DoNotChangeOwnerZOrder);
        _nativeFrame.Update(new DrawingRectangle(
            _windowRegion.X,
            _windowRegion.Y,
            _windowRegion.Width,
            _windowRegion.Height));
    }

    protected override void OnClosed(EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        _windowSource?.RemoveHook(OnWindowMessage);
        _windowSource = null;
        _nativeFrame.Dispose();
        base.OnClosed(e);
    }

    private IntPtr OnWindowMessage(
        IntPtr window,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message != NonClientHitTestMessage)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HitTestTransparent);
    }

    internal void EnsureTopmost()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(TopmostWindow),
            _windowRegion.X,
            _windowRegion.Y,
            _windowRegion.Width,
            _windowRegion.Height,
            DoNotActivate | DoNotChangeOwnerZOrder);
        _nativeFrame.Update(new DrawingRectangle(
            _windowRegion.X,
            _windowRegion.Y,
            _windowRegion.Width,
            _windowRegion.Height));
    }

    private bool TryGetAccentColors(out WpfColor start, out WpfColor end)
    {
        start = WpfColor.FromRgb(91, 141, 239);
        end = start;
        if (TryFindResource("AppAccentBrush") is not WpfBrush brush)
        {
            return false;
        }

        if (brush is WpfSolidColorBrush solid)
        {
            start = solid.Color;
            end = solid.Color;
            return true;
        }

        if (brush is System.Windows.Media.GradientBrush gradient &&
            gradient.GradientStops.Count > 0)
        {
            start = gradient.GradientStops[0].Color;
            end = gradient.GradientStops[^1].Color;
            return true;
        }

        return false;
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
