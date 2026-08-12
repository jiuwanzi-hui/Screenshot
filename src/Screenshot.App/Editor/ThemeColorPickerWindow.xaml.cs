using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Screenshot.App.Editor;

public partial class ThemeColorPickerWindow : Window
{
    private bool _hasActivated;

    public WpfColor SelectedColor { get; private set; }

    public event EventHandler<WpfColor>? ColorSelected;

    public WpfBrush PreviewBrush => new SolidColorBrush(SelectedColor);

    public ThemeColorPickerWindow(WpfColor initialColor, IEnumerable<int>? recentColors = null)
    {
        SelectedColor = initialColor;
        InitializeComponent();
        HexTextBox.Text = FormatColor(initialColor);
        DataContext = this;
        PopulateRecentColors(recentColors);
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string value } && TrySelectColor(value))
        {
            ColorSelected?.Invoke(this, SelectedColor);
            Close();
        }
    }

    private void OnHexTextBoxKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter && TrySelectColor(HexTextBox.Text))
        {
            e.Handled = true;
            ColorSelected?.Invoke(this, SelectedColor);
            Close();
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e) => _hasActivated = true;

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_hasActivated)
        {
            Close();
        }
    }

    private bool TrySelectColor(string value)
    {
        if (WpfColorConverter.ConvertFromString(value.Trim()) is not WpfColor color)
        {
            HexTextBox.Focus();
            return false;
        }

        SelectedColor = color;
        return true;
    }

    private void PopulateRecentColors(IEnumerable<int>? recentColors)
    {
        var colors = (recentColors ?? [])
            .Where(color => color is >= 0 and <= 0xFFFFFF)
            .Distinct()
            .Reverse()
            .Take(16)
            .ToArray();
        RecentColorsLabel.Visibility = colors.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        RecentColorsPanel.Visibility = colors.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        foreach (var value in colors)
        {
            var color = WpfColor.FromRgb(
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value);
            RecentColorsPanel.Children.Add(new WpfButton
            {
                Tag = FormatColor(color),
                Background = new SolidColorBrush(color),
                Style = (Style)FindResource("ColorSwatch"),
            });
            ((WpfButton)RecentColorsPanel.Children[^1]).Click += OnSwatchClick;
        }
    }

    private static string FormatColor(WpfColor color) =>
        color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
