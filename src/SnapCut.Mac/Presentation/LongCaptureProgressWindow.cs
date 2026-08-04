using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Mac.Capture;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Presentation;

internal sealed class LongCaptureProgressWindow : Window
{
    private readonly TextBlock _status;
    private bool _allowClose;

    public LongCaptureProgressWindow()
    {
        Title = "SnapCut 长截图";
        Width = 430;
        Height = 138;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;

        _status = new TextBlock
        {
            Text = "请在框选区域内滚动，完成后点停止",
            Foreground = new SolidColorBrush(MacTheme.PrimaryText),
            FontSize = 14,
        };
        var stop = MacTheme.CreateButton("停止并生成", primary: true);
        stop.Click += (_, _) => StopRequested?.Invoke();
        var cancel = MacTheme.CreateButton("取消");
        cancel.Click += (_, _) => CancelRequested?.Invoke();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                cancel,
                stop,
            },
        };
        Content = MacTheme.CreatePanel(
            new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    _status,
                    buttons,
                },
            });
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
        Closing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                CancelRequested?.Invoke();
            }
        };
    }

    public event Action? StopRequested;

    public event Action? CancelRequested;

    public void UpdateProgress(ScrollCaptureEngine.Progress progress)
    {
        _status.Text = progress.StitchedFrames <= 1
            ? "等待滚动…"
            : $"已拼接 {progress.StitchedFrames} 段 · {progress.OutputHeight}px";
    }

    public void Finish()
    {
        _allowClose = true;
        Hide();
        Close();
    }
}
