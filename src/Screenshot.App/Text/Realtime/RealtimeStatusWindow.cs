using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Presentation;
using Brushes = System.Windows.Media.Brushes;
using ToolTip = System.Windows.Controls.ToolTip;

namespace Screenshot.App.Text.Realtime;

// A tiny independent hit target keeps the entire translated canvas click-through.
// Full provider/quality diagnostics are available on hover, never a paragraph
// permanently covering the user's document. No polling or animation is needed.
internal sealed class RealtimeStatusWindow : Window
{
    private readonly TextBlock _label = new() { FontSize = 11, Text = "…" };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 390, FontSize = 12 };
    private readonly Border _badge;
    private readonly ToolTip _tip;
    internal string Details => _details.Text;

    internal RealtimeStatusWindow()
    {
        Title = "SnapCut 实时翻译状态";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
        Width = 34; Height = 22;
        _badge = new Border { Child = _label, Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4) };
        _tip = new ToolTip { Content = _details, Placement = PlacementMode.Top, StaysOpen = true };
        _badge.ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(_badge, 200);
        ToolTipService.SetShowDuration(_badge, 60000);
        _badge.MouseLeftButtonUp += (_, _) => _tip.IsOpen = !_tip.IsOpen;
        Content = _badge;
        SourceInitialized += (_, _) => RealtimeOverlayWindow.Configure(new WindowInteropHelper(this).Handle, false);
        Closed += (_, _) => _tip.IsOpen = false;
    }

    internal void Present(ScreenRegion region, string status, bool hasFailures, bool reading)
    {
        _details.Text = UiLocalization.Get(status);
        _label.Text = hasFailures ? "!" : reading ? "≋" : status.Contains('…') ? "…" : "✓";
        var background = TryFindResource("AppFloatingSurfaceBrush") as System.Windows.Media.Brush ?? Brushes.DimGray;
        var foreground = TryFindResource("AppTextPrimaryBrush") as System.Windows.Media.Brush ?? Brushes.White;
        _badge.Background = background; _label.Foreground = foreground;
        _tip.Background = background; _details.Foreground = foreground;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height)).Bounds;
        var scale = GetDpiForWindow(hwnd) / 96d;
        var width = (int)Math.Ceiling(34 * scale); var height = (int)Math.Ceiling(22 * scale);
        var x = Math.Clamp(region.X, screen.Left, Math.Max(screen.Left, screen.Right - width));
        var y = region.Y + region.Height + 3;
        if (y + height > screen.Bottom) y = region.Y - height - 3;
        if (y < screen.Top) y = Math.Max(screen.Top, region.Y + region.Height - height - 3);
        _ = SetWindowPos(hwnd, new nint(-1), x, y, width, height, 0x0010);
        if (!IsVisible) Show();
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
