using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Recording;

internal enum MacRecordingCompletion
{
    Stop,
    StopAndEdit,
    Cancel,
}

internal sealed class MacRecordingControlWindow : Window
{
    private readonly TaskCompletionSource<MacRecordingCompletion> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _timer;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly TextBlock _elapsed;

    public MacRecordingControlWindow()
    {
        Title = "SnapCut 录屏";
        Width = 392;
        Height = 52;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        _elapsed = new TextBlock
        {
            Text = "00:00",
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 56,
        };
        var stop = CreateButton("■", "结束录制");
        stop.Click += (_, _) => _completion.TrySetResult(MacRecordingCompletion.Stop);
        var edit = CreateButton("✎", "结束并编辑");
        edit.Click += (_, _) => _completion.TrySetResult(MacRecordingCompletion.StopAndEdit);
        var cancel = CreateButton("×", "取消并删除");
        cancel.Foreground = new SolidColorBrush(Color.Parse("#F87171"));
        cancel.Click += (_, _) => _completion.TrySetResult(MacRecordingCompletion.Cancel);
        var dragSurface = new Border
        {
            Background = Brushes.Transparent,
            MinWidth = 115,
            Child = new TextBlock
            {
                Text = "长按拖拽 · 双击居中",
                Foreground = new SolidColorBrush(Color.Parse("#B9C2CF")),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        dragSurface.PointerPressed += HandleDragSurfacePressed;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children = { _elapsed, dragSurface, stop, edit, cancel },
        };
        Content = new Border
        {
            Padding = new Thickness(10, 7),
            Background = new SolidColorBrush(Color.Parse("#F0212A38")),
            BorderBrush = new SolidColorBrush(Color.Parse("#64748799")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = row,
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            var elapsed = DateTimeOffset.Now - _startedAt;
            _elapsed.Text = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
        };
        Opened += (_, _) =>
        {
            MacNativeUi.ExcludeFromScreenCapture(this);
            _timer.Start();
        };
        Closed += (_, _) => _completion.TrySetResult(MacRecordingCompletion.Stop);
    }

    public async Task<MacRecordingCompletion> WaitAsync()
    {
        Show();
        Activate();
        var result = await _completion.Task;
        _timer.Stop();
        Close();
        return result;
    }

    private void HandleDragSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Position = new PixelPoint(
                Math.Max(0, (Screens.Primary?.Bounds.Width ?? 800) / 2 - (int)Width / 2),
                Math.Max(0, (Screens.Primary?.Bounds.Height ?? 600) / 2 - (int)Height / 2));
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private static Button CreateButton(string icon, string tooltip)
    {
        var button = new Button
        {
            Content = icon,
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            FontSize = 15,
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }
}
