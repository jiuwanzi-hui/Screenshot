using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Recording;

internal sealed class MacRecordingInputOverlayWindow : Window
{
    private readonly TextBlock _text;
    private readonly DispatcherTimer _hideTimer;
    private readonly CGRect _region;

    public MacRecordingInputOverlayWindow(CGRect region)
    {
        _region = region;
        Width = 150;
        Height = 44;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        IsHitTestVisible = false;
        _text = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#C8181B22")),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 7),
            Child = _text,
        };
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    public void ShowKey(string key)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _text.Text = key;
            Position = new PixelPoint(
                (int)Math.Round(_region.Left + 18),
                (int)Math.Round(_region.Bottom - Height - 18));
            ShowOverlay();
        });
    }

    public void ShowMouse(CGPoint point, bool right)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _text.Text = right ? "右键" : "左键";
            Position = new PixelPoint(
                (int)Math.Round(point.X + 12),
                (int)Math.Round(point.Y + 12));
            ShowOverlay();
        });
    }

    private void ShowOverlay()
    {
        if (!IsVisible)
        {
            Show();
        }
        _hideTimer.Stop();
        _hideTimer.Start();
    }
}
