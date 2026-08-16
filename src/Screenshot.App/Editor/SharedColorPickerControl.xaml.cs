using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Screenshot.App.Capture;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Screenshot.App.Editor;

public partial class SharedColorPickerControl : System.Windows.Controls.UserControl
{
    private bool _isUpdating;
    private int[] _palette = [];
    private WpfColor _previewColor = WpfColor.FromRgb(0, 127, 115);

    public SharedColorPickerControl()
    {
        InitializeComponent();
        SetState(_previewColor, []);
    }

    public event Action<WpfColor>? ColorCommitted;

    public event Action<int[]>? PaletteChanged;

    public event EventHandler? CloseRequested;

    internal WpfColor PreviewColor => _previewColor;

    internal IReadOnlyList<int> Palette => _palette;

    public void SetState(WpfColor color, IEnumerable<int>? palette)
    {
        _palette = NormalizePalette(palette);
        PopulateRecentColors();
        SetColorValues(color);
    }

    internal bool TryHandlePaletteRightClick(object? source)
    {
        for (var element = source as DependencyObject;
             element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is WpfButton { Tag: int index } &&
                RecentColorsPanel.IsAncestorOf(element))
            {
                SaveColorToSlot(index);
                return true;
            }
            if (ReferenceEquals(element, this))
            {
                break;
            }
        }
        return false;
    }

    private void PopulateRecentColors()
    {
        RecentColorsPanel.Children.Clear();
        for (var index = 0; index < 8; index++)
        {
            var hasColor = index < _palette.Length;
            var value = hasColor ? _palette[index] : 0;
            var color = hasColor
                ? FromColorValue(value)
                : WpfColor.FromArgb(0, 0, 0, 0);
            var button = new WpfButton
            {
                Tag = index,
                Background = new WpfSolidColorBrush(color),
                BorderBrush = new WpfSolidColorBrush(
                    hasColor
                        ? WpfColor.FromArgb(0x8A, 0xE1, 0xD8, 0xD0)
                        : WpfColor.FromArgb(0x80, 0xA9, 0xCE, 0xCA)),
                Style = (Style)FindResource("ColorSwatch"),
                ToolTip = hasColor
                    ? "左键使用，右键覆盖"
                    : "左键或右键保存当前颜色",
            };
            if (!hasColor)
            {
                button.Content = new TextBlock
                {
                    Text = "+",
                    FontSize = 13,
                    Foreground = System.Windows.Media.Brushes.White,
                };
            }
            button.Click += OnRecentColorClick;
            button.PreviewMouseRightButtonDown += OnRecentColorRightClick;
            RecentColorsPanel.Children.Add(button);
        }
    }

    private void OnFixedColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string text } && TryParseColor(text, out var color))
        {
            SetColorValues(color);
            CommitColor(closeAfterCommit: true);
        }
    }

    private void OnRecentColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: int index })
        {
            return;
        }
        if (index < _palette.Length)
        {
            SetColorValues(FromColorValue(_palette[index]));
            CommitColor(closeAfterCommit: true);
        }
        else
        {
            SaveColorToSlot(index);
        }
        e.Handled = true;
    }

    private void OnRecentColorRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is WpfButton { Tag: int index })
        {
            SaveColorToSlot(index);
            e.Handled = true;
        }
    }

    private void SaveColorToSlot(int index)
    {
        var value = ToColorValue(_previewColor);
        var slots = _palette.Take(8).ToList();
        while (slots.Count <= index)
        {
            slots.Add(value);
        }
        slots[index] = value;
        _palette = NormalizePalette(slots.Concat(_palette.Skip(8)));
        PaletteChanged?.Invoke(_palette.ToArray());
        PopulateRecentColors();
    }

    private void OnColorComponentChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdating || ColorHexTextBox is null)
        {
            return;
        }
        UpdatePreview(ColorFromHsv(
            HueSlider.Value,
            SaturationSlider.Value / 100d,
            ValueSlider.Value / 100d,
            (byte)Math.Round(AlphaSlider.Value * 2.55d)));
    }

    private void OnSliderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CommitColor(closeAfterCommit: false);
        e.Handled = true;
    }

    private void OnSliderLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e) =>
        CommitColor(closeAfterCommit: false);

    private void OnHexTextBoxPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = ClipboardTextService.SetTextAsync(ColorHexTextBox.Text);
            e.Handled = true;
        }
    }

    private void OnHexTextBoxKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter && TryParseColor(ColorHexTextBox.Text, out var color))
        {
            SetColorValues(color);
            CommitColor(closeAfterCommit: true);
            e.Handled = true;
        }
    }

    private void CommitColor(bool closeAfterCommit)
    {
        ColorCommitted?.Invoke(_previewColor);
        if (closeAfterCommit)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetColorValues(WpfColor color)
    {
        _isUpdating = true;
        try
        {
            var (hue, saturation, value) = ColorToHsv(color);
            HueSlider.Value = hue;
            SaturationSlider.Value = saturation * 100d;
            ValueSlider.Value = value * 100d;
            AlphaSlider.Value = color.A / 2.55d;
            UpdatePreview(color);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void UpdatePreview(WpfColor color)
    {
        _previewColor = color;
        ColorPreview.Background = new WpfSolidColorBrush(color);
        ColorHexTextBox.Text = FormatColor(color);
        HueText.Text = $"{HueSlider.Value:0}";
        SaturationText.Text = $"{SaturationSlider.Value:0}%";
        ValueText.Text = $"{ValueSlider.Value:0}%";
        AlphaText.Text = $"{AlphaSlider.Value:0}%";
    }

    private static WpfColor ColorFromHsv(
        double hue,
        double saturation,
        double value,
        byte alpha)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60d % 2) - 1));
        var match = value - chroma;
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        return WpfColor.FromArgb(
            alpha,
            (byte)Math.Round((red + match) * 255),
            (byte)Math.Round((green + match) * 255),
            (byte)Math.Round((blue + match) * 255));
    }

    private static (double Hue, double Saturation, double Value) ColorToHsv(
        WpfColor color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var hue = delta == 0 ? 0 : maximum == red
            ? 60 * ((green - blue) / delta % 6)
            : maximum == green
                ? 60 * ((blue - red) / delta + 2)
                : 60 * ((red - green) / delta + 4);
        return (
            hue < 0 ? hue + 360 : hue,
            maximum == 0 ? 0 : delta / maximum,
            maximum);
    }

    private static bool TryParseColor(string? text, out WpfColor color)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) &&
                WpfColorConverter.ConvertFromString(text.Trim()) is WpfColor parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException)
        {
        }
        color = default;
        return false;
    }

    private static int[] NormalizePalette(IEnumerable<int>? colors) =>
        (colors ?? [])
            .Where(color => color is >= 0 and <= 0xFFFFFF)
            .Take(16)
            .ToArray();

    private static WpfColor FromColorValue(int value) => WpfColor.FromRgb(
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value);

    private static int ToColorValue(WpfColor color) =>
        color.R << 16 | color.G << 8 | color.B;

    private static string FormatColor(WpfColor color) =>
        color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
