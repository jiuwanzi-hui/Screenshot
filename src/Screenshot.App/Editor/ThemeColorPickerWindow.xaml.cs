using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;

namespace Screenshot.App.Editor;

public partial class ThemeColorPickerWindow : Window
{
    private readonly List<Window> _outsideClickWindows = [];
    private bool _hasBeenActivated;

    public ThemeColorPickerWindow(
        WpfColor initialColor,
        IEnumerable<int>? recentColors = null)
    {
        SelectedColor = initialColor;
        InitializeComponent();
        ColorPicker.SetState(initialColor, recentColors);
        ColorPicker.ColorCommitted += OnColorCommitted;
        ColorPicker.PaletteChanged += colors => PaletteChanged?.Invoke(this, colors);
        ColorPicker.CloseRequested += (_, _) => Close();
    }

    public WpfColor SelectedColor { get; private set; }

    public event EventHandler<WpfColor>? ColorSelected;

    public event EventHandler<int[]>? PaletteChanged;

    public WpfBrush PreviewBrush => new SolidColorBrush(SelectedColor);

    internal void ShowBelow(FrameworkElement anchor)
    {
        var origin = anchor.PointToScreen(new System.Windows.Point());
        var corner = anchor.PointToScreen(new System.Windows.Point(anchor.ActualWidth, anchor.ActualHeight));
        var reference = System.Drawing.Rectangle.FromLTRB((int)origin.X, (int)origin.Y, (int)corner.X, (int)corner.Y);
        var workArea = MonitorGeometryService.GetWorkArea(reference);
        var dpi = MonitorGeometryService.GetDpiScale(reference);
        var anchorBounds = Owner is not null && MonitorGeometryService.TryGetWindowBounds(Owner, out var ownerBounds)
            ? new Rect(origin.X, ownerBounds.Top, Math.Max(0, corner.X - origin.X), ownerBounds.Height)
            : new Rect(origin, corner);
        var bounds = new Rect(workArea.Left + 8, workArea.Top + 8, Math.Max(0, workArea.Width - 16), Math.Max(0, workArea.Height - 16));
        ColorPicker.Measure(new System.Windows.Size(Width, double.PositiveInfinity));
        var initial = ToolbarPopupPlacement.Below(anchorBounds,
            new System.Windows.Size(Width * dpi.X, ColorPicker.DesiredSize.Height * dpi.Y), bounds);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = initial.X / dpi.X;
        Top = initial.Y / dpi.Y;
        void PositionAfterLayout(object sender, RoutedEventArgs args)
        {
            Loaded -= PositionAfterLayout;
            if (!MonitorGeometryService.TryGetWindowBounds(this, out var actual)) return;
            var position = ToolbarPopupPlacement.Below(anchorBounds, new System.Windows.Size(actual.Width, actual.Height), bounds);
            MonitorGeometryService.TryMoveWindow(this, (int)Math.Round(position.X), (int)Math.Round(position.Y));
        }
        Loaded += PositionAfterLayout;
        Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        ColorPicker.ColorCommitted -= OnColorCommitted;
        Loaded -= OnWindowLoaded;
        foreach (var window in _outsideClickWindows)
        {
            window.PreviewMouseDown -= OnOtherWindowPreviewMouseDown;
        }
        _outsideClickWindows.Clear();
        base.OnClosed(e);
    }

    private void OnColorCommitted(WpfColor color)
    {
        SelectedColor = color;
        ColorSelected?.Invoke(this, color);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var window in System.Windows.Application.Current.Windows
                     .OfType<Window>()
                     .Where(window => !ReferenceEquals(window, this)))
        {
            window.PreviewMouseDown += OnOtherWindowPreviewMouseDown;
            _outsideClickWindows.Add(window);
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (!_hasBeenActivated || !IsVisible)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (IsVisible &&
                    !IsActive &&
                    !IsMouseOver &&
                    Mouse.Captured is null)
                {
                    Close();
                }
            });
    }

    private void OnWindowActivated(object? sender, EventArgs e) =>
        _hasBeenActivated = true;

    private void OnOtherWindowPreviewMouseDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e) => Close();
}
