using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Presentation;

internal sealed class FloatingCaptureButtonWindow : Window
{
    private readonly Button _button;
    private readonly ContextMenu _menu;
    private PixelPoint _dragStart;
    private PixelPoint _windowStart;
    private bool _dragging;
    private bool _moved;

    public FloatingCaptureButtonWindow(
        Action clicked,
        Action<string> menuAction,
        Action close)
    {
        Width = 48;
        Height = 48;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        using var iconStream = AssetLoader.Open(
            new Uri("avares://snapcut/Assets/Screenshot.png"));
        var icon = new Bitmap(iconStream);
        _button = new Button
        {
            Width = 44,
            Height = 44,
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = new Image
            {
                Source = icon,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
            },
        };
        _button.ContextMenu = _menu = CreateMenu(menuAction, close);
        Content = _button;

        _button.PointerPressed += OnPointerPressed;
        _button.PointerMoved += OnPointerMoved;
        _button.PointerReleased += (sender, args) =>
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            args.Pointer.Capture(null);
            if (!_moved && args.InitialPressMouseButton == MouseButton.Left)
            {
                clicked();
            }

            args.Handled = true;
        };
        _button.PointerEntered += (_, _) => _menu.Open(_button);
        _menu.PointerEntered += (_, _) => _menu.Open(_button);
        _menu.PointerExited += (_, _) => _menu.Close();
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_button).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragStart = GetPointerScreenPosition(e);
        _windowStart = Position;
        _dragging = true;
        _moved = false;
        e.Pointer.Capture(_button);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || !e.GetCurrentPoint(_button).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = GetPointerScreenPosition(e);
        var dx = point.X - _dragStart.X;
        var dy = point.Y - _dragStart.Y;
        if (!_moved && Math.Sqrt((dx * dx) + (dy * dy)) < 3)
        {
            return;
        }

        _moved = true;
        Position = new PixelPoint(
            _windowStart.X + dx,
            _windowStart.Y + dy);
        e.Handled = true;
    }

    private PixelPoint GetPointerScreenPosition(PointerEventArgs e)
    {
        var local = e.GetPosition(this);
        return new PixelPoint(
            Position.X + (int)Math.Round((double)(local.X * RenderScaling)),
            Position.Y + (int)Math.Round((double)(local.Y * RenderScaling)));
    }

    private static ContextMenu CreateMenu(Action<string> action, Action close)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Right,
        };
        foreach (var item in new[]
        {
            new MacFloatingMenuItem("Region", "区域截图"),
            new MacFloatingMenuItem("Scroll", "长截图"),
            new MacFloatingMenuItem("Video", "录制视频"),
            new MacFloatingMenuItem("Pin", "钉图"),
            new MacFloatingMenuItem("AllScreens", "全部屏幕截图"),
            new MacFloatingMenuItem("Settings", "打开设置"),
            new MacFloatingMenuItem("Close", "关闭悬浮按钮"),
        })
        {
            var menuItem = new MenuItem
            {
                Header = item.Label,
                Tag = item.Id,
            };
            menuItem.Click += (_, _) =>
            {
                menu.Close();
                if (menuItem.Tag is "Close")
                {
                    close();
                }
                else if (menuItem.Tag is string id)
                {
                    action(id);
                }
            };
            menu.Items.Add(menuItem);
        }

        return menu;
    }

    private sealed record MacFloatingMenuItem(string Id, string Label);
}
