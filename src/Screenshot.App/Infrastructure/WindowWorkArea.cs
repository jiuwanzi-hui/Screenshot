using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Screenshot.App.Infrastructure;

// Client-drawn captions must explicitly use the current monitor's work area.
// Native coordinates already include DPI scaling and secondary-monitor offsets.
internal sealed class WindowWorkArea
{
    private static readonly ConditionalWeakTable<Window, WindowWorkArea> Attachments = new();
    private readonly Window _window;
    private HwndSource? _source;

    internal static void Attach(Window window) => Attachments.GetValue(window, owner => new WindowWorkArea(owner));

    private WindowWorkArea(Window window)
    {
        _window = window;
        window.SourceInitialized += OnSourceInitialized;
        window.Closed += OnClosed;
        Connect();
    }

    private void OnSourceInitialized(object? sender, EventArgs args) => Connect();

    private void Connect()
    {
        if (_source is not null) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnMessage);
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnClosed;
        _source?.RemoveHook(OnMessage);
        _source = null;
        Attachments.Remove(_window);
    }

    private IntPtr OnMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0024 || lParam == IntPtr.Zero ||
            _window.WindowStyle != WindowStyle.None || WindowChrome.GetWindowChrome(_window) is null)
            return IntPtr.Zero;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = GetMaximizedBounds(info.Monitor.ToRectangle(), info.Work.ToRectangle());
        limits.MaxPosition = work.Location;
        limits.MaxSize = new DrawingPoint(work.Width, work.Height);
        var dpi = _source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        // Preserve WPF's declared resize limits when handling this message.
        limits.MinTrackSize = new DrawingPoint(
            Math.Min(work.Width, (int)Math.Ceiling(_window.MinWidth * dpi.M11)),
            Math.Min(work.Height, (int)Math.Ceiling(_window.MinHeight * dpi.M22)));
        if (double.IsFinite(_window.MaxWidth))
            limits.MaxTrackSize.X = (int)Math.Ceiling(_window.MaxWidth * dpi.M11);
        if (double.IsFinite(_window.MaxHeight))
            limits.MaxTrackSize.Y = (int)Math.Ceiling(_window.MaxHeight * dpi.M22);
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    internal static DrawingRectangle GetMaximizedBounds(DrawingRectangle monitor, DrawingRectangle work) =>
        new(work.Left - monitor.Left, work.Top - monitor.Top, work.Width, work.Height);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public DrawingPoint Reserved;
        public DrawingPoint MaxSize;
        public DrawingPoint MaxPosition;
        public DrawingPoint MinTrackSize;
        public DrawingPoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly DrawingRectangle ToRectangle() => DrawingRectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
