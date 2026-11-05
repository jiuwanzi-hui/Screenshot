using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Presentation;

// Small owned surface outside a pin, independent of its exported image/layout.
internal sealed class ImageQuestionSideWindow : Window
{
    private readonly Window _anchor;
    internal ImageQuestionSideWindow(Window anchor, Action clicked)
    {
        _anchor = anchor;
        Owner = anchor; Width = 38; Height = 100; ShowActivated = false;
        ShowInTaskbar = false; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = anchor.Topmost;
        AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent;
        var button = new ImageQuestionEntry();
        button.Click += (_, _) => clicked();
        Content = button;
        anchor.LocationChanged += Reposition;
        anchor.SizeChanged += Reposition;
        anchor.IsVisibleChanged += OnAnchorVisibilityChanged;
        anchor.StateChanged += OnAnchorStateChanged;
        Loaded += Reposition;
    }
    private void OnAnchorVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (_anchor.IsVisible && _anchor.WindowState != WindowState.Minimized) { Show(); Reposition(null, EventArgs.Empty); } else Hide(); }
    private void OnAnchorStateChanged(object? sender, EventArgs e)
    { if (_anchor.IsVisible && _anchor.WindowState != WindowState.Minimized) { Show(); Reposition(null, EventArgs.Empty); } else Hide(); }
    private void Reposition(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !_anchor.IsVisible) return;
        var point = _anchor.PointToScreen(new System.Windows.Point(_anchor.ActualWidth, _anchor.ActualHeight / 2));
        var dpi = VisualTreeHelper.GetDpi(_anchor);
        var width = (int)Math.Ceiling(Width * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        var area = System.Windows.Forms.Screen.GetWorkingArea(new System.Drawing.Point((int)point.X - 1, (int)point.Y));
        var left = Math.Clamp((int)(point.X + 6 * dpi.DpiScaleX), area.Left, Math.Max(area.Left, area.Right - width));
        var top = Math.Clamp((int)point.Y - height / 2, area.Top, Math.Max(area.Top, area.Bottom - height));
        SetWindowPos(handle, IntPtr.Zero, left, top, width, height, 0x14);
    }
    protected override void OnClosed(EventArgs e)
    {
        _anchor.LocationChanged -= Reposition; _anchor.SizeChanged -= Reposition;
        _anchor.IsVisibleChanged -= OnAnchorVisibilityChanged;
        _anchor.StateChanged -= OnAnchorStateChanged;
        base.OnClosed(e);
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
