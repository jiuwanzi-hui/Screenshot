using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfToolTip = System.Windows.Controls.ToolTip;
using WpfPoint = System.Windows.Point;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseEventArgs;

namespace Screenshot.App.Presentation;

internal sealed class HoverIdleHintBehavior : IDisposable
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan InitialDisplayDuration =
        TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan IdleDisplayDuration =
        TimeSpan.FromSeconds(2);

    private readonly FrameworkElement _surface;
    private readonly Func<DependencyObject?, bool>? _isEligible;
    private readonly DispatcherTimer _showTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly WpfToolTip _toolTip;
    private WpfPoint _lastPoint;
    private bool _isOver;
    private bool _hasPoint;
    private bool _disposed;

    public HoverIdleHintBehavior(
        FrameworkElement surface,
        string hintText,
        Func<DependencyObject?, bool>? isEligible = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentException.ThrowIfNullOrWhiteSpace(hintText);

        _surface = surface;
        _isEligible = isEligible;
        _showTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = IdleDelay,
        };
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background);
        _toolTip = new WpfToolTip
        {
            Content = hintText,
            Placement = PlacementMode.Mouse,
            PlacementTarget = surface,
            StaysOpen = true,
        };

        _showTimer.Tick += OnShowTimerTick;
        _hideTimer.Tick += OnHideTimerTick;
        _surface.MouseEnter += OnMouseEnter;
        _surface.PreviewMouseMove += OnPreviewMouseMove;
        _surface.MouseLeave += OnMouseLeave;
    }

    private void OnMouseEnter(object sender, WpfMouseButtonEventArgs e)
    {
        _isOver = true;
        _hasPoint = true;
        _lastPoint = e.GetPosition(_surface);
        if (IsEligible(e.OriginalSource as DependencyObject))
        {
            OpenHint(InitialDisplayDuration);
        }
    }

    private void OnPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isOver)
        {
            return;
        }

        if (!IsEligible(e.OriginalSource as DependencyObject))
        {
            CloseHint();
            return;
        }

        var point = e.GetPosition(_surface);
        if (_hasPoint &&
            Math.Abs(point.X - _lastPoint.X) < 2 &&
            Math.Abs(point.Y - _lastPoint.Y) < 2)
        {
            return;
        }

        _lastPoint = point;
        _hasPoint = true;
        CloseHint();
        _showTimer.Start();
    }

    private void OnMouseLeave(object sender, WpfMouseButtonEventArgs e)
    {
        _isOver = false;
        _hasPoint = false;
        CloseHint();
    }

    private void OnShowTimerTick(object? sender, EventArgs e)
    {
        _showTimer.Stop();
        if (!_isOver || !_surface.IsMouseOver)
        {
            return;
        }

        OpenHint(IdleDisplayDuration);
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        _toolTip.IsOpen = false;
    }

    private bool IsEligible(DependencyObject? source) =>
        _isEligible?.Invoke(source) ?? true;

    private void OpenHint(TimeSpan displayDuration)
    {
        _showTimer.Stop();
        _hideTimer.Stop();
        _toolTip.IsOpen = true;
        _hideTimer.Interval = displayDuration;
        _hideTimer.Start();
    }

    private void CloseHint()
    {
        _showTimer.Stop();
        _hideTimer.Stop();
        _toolTip.IsOpen = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseHint();
        _showTimer.Tick -= OnShowTimerTick;
        _hideTimer.Tick -= OnHideTimerTick;
        _surface.MouseEnter -= OnMouseEnter;
        _surface.PreviewMouseMove -= OnPreviewMouseMove;
        _surface.MouseLeave -= OnMouseLeave;
    }
}
