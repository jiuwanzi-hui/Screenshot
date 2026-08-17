using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace Screenshot.App.Presentation;

internal sealed class ToolbarDragHintBehavior
{
    internal const string HintText = "长按拖拽，双击自动吸附";
    internal static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(650);
    internal static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(2);

    private readonly FrameworkElement _surface;
    private readonly DependencyObject _toolbarRoot;
    private readonly DispatcherTimer _showTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly WpfToolTip _toolTip;
    private bool _isOverBlankSurface;
    private bool _isDisposed;

    public ToolbarDragHintBehavior(
        FrameworkElement surface,
        DependencyObject toolbarRoot)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(toolbarRoot);
        _surface = surface;
        _toolbarRoot = toolbarRoot;
        _showTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = IdleDelay,
        };
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = DisplayDuration,
        };
        _toolTip = new WpfToolTip
        {
            Content = HintText,
            Placement = PlacementMode.Mouse,
            PlacementTarget = surface,
            StaysOpen = true,
        };
        _showTimer.Tick += OnShowTimerTick;
        _hideTimer.Tick += OnHideTimerTick;
        _surface.PreviewMouseMove += OnPreviewMouseMove;
        _surface.MouseLeave += OnMouseLeave;
    }

    private void OnPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        _isOverBlankSurface = ToolbarDragInteraction.IsBlankSurface(
            e.OriginalSource as DependencyObject,
            _toolbarRoot);
        CloseHint();
        if (_isOverBlankSurface)
        {
            _showTimer.Start();
        }
    }

    private void OnMouseLeave(object sender, WpfMouseEventArgs e)
    {
        _isOverBlankSurface = false;
        CloseHint();
    }

    private void OnShowTimerTick(object? sender, EventArgs e)
    {
        _showTimer.Stop();
        if (!_isOverBlankSurface || !_surface.IsMouseOver)
        {
            return;
        }

        _toolTip.IsOpen = true;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        _toolTip.IsOpen = false;
    }

    private void CloseHint()
    {
        _showTimer.Stop();
        _hideTimer.Stop();
        _toolTip.IsOpen = false;
    }

    public void Detach()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CloseHint();
        _showTimer.Tick -= OnShowTimerTick;
        _hideTimer.Tick -= OnHideTimerTick;
        _surface.PreviewMouseMove -= OnPreviewMouseMove;
        _surface.MouseLeave -= OnMouseLeave;
    }
}
