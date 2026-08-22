using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
