using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Core;
using SnapCut.Mac.Capture;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Presentation;

internal sealed class RegionSelectionWindow : Window
{
    private readonly TaskCompletionSource<Rect?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RegionSelectionWindow(MacDisplay display, PixelImage desktop, bool scrollCapture)
    {
        Display = display;
        Title = "SnapCut 框选";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(
            (int)Math.Round(display.Bounds.Left),
            (int)Math.Round(display.Bounds.Top));
        Width = display.Bounds.Size.Width;
        Height = display.Bounds.Size.Height;
        Background = Brushes.Black;

        var canvas = new SelectionCanvas(PixelImageBitmap.Create(desktop));
        canvas.SelectionCompleted += selection => _completion.TrySetResult(selection);
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 30, 29, 37)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 20, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = scrollCapture
                    ? "拖动框选长截图区域 · Esc 取消"
                    : "拖动框选截图区域 · Esc 取消",
                Foreground = Brushes.White,
                FontSize = 13,
            },
        };
        Content = new Grid
        {
            Children =
            {
                canvas,
                hint,
            },
        };
        KeyDown += HandleKeyDown;
        Closed += (_, _) => _completion.TrySetResult(null);
    }

    public MacDisplay Display { get; }

    public Size SelectionSurfaceSize { get; private set; }

    public async Task<Rect?> SelectAsync()
    {
        Show();
        Activate();
        var result = await _completion.Task;
        SelectionSurfaceSize = ClientSize;
        Hide();
        Close();
        return result;
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _completion.TrySetResult(null);
            e.Handled = true;
        }
    }
}
