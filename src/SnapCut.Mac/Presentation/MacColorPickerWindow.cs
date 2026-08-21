using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Mac.App;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Presentation;

internal sealed class MacColorPickerWindow : Window
{
    private readonly TaskCompletionSource<Color?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MacSettings _settings;
    private readonly Slider _red;
    private readonly Slider _green;
    private readonly Slider _blue;
    private readonly TextBox _hex;
    private readonly Border _preview;
    private bool _updating;
    private Color _color;

    public MacColorPickerWindow(Color initial, MacSettings settings)
    {
        _settings = settings;
        _color = Color.FromRgb(initial.R, initial.G, initial.B);
        Title = "SnapCut 调色盘";
        Width = 390;
        Height = 430;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(MacTheme.WindowBackground);

        _preview = new Border
        {
            Height = 54,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(_color),
            BorderBrush = new SolidColorBrush(MacTheme.Border),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "当前颜色",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                FontWeight = FontWeight.SemiBold,
            },
        };
        _hex = new TextBox
        {
            Text = ToHex(_color),
            Watermark = "HEX，例如 #2EAFA5",
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _hex.TextChanged += (_, _) => TryApplyHex();
        _red = CreateSlider(_color.R);
        _green = CreateSlider(_color.G);
        _blue = CreateSlider(_color.B);
        _red.PropertyChanged += (_, _) => UpdateFromSliders();
        _green.PropertyChanged += (_, _) => UpdateFromSliders();
        _blue.PropertyChanged += (_, _) => UpdateFromSliders();

        var palette = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 34,
            ItemHeight = 34,
        };
        for (var index = 0; index < _settings.CustomColorPalette.Length; index++)
        {
            var slotIndex = index;
            var color = Color.Parse(_settings.CustomColorPalette[index]);
            var button = new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(MacTheme.Border),
                BorderThickness = new Thickness(1),
            };
            button.Click += (_, _) => SetColor(color);
            button.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(button).Properties.IsRightButtonPressed)
                {
                    _settings.CustomColorPalette[slotIndex] = ToHex(_color);
                    button.Background = new SolidColorBrush(_color);
                    e.Handled = true;
                }
            };
            ToolTip.SetTip(button, "左键选择，右键保存当前颜色");
            palette.Children.Add(button);
        }

        var cancel = MacTheme.CreateButton("取消");
        cancel.Click += (_, _) => _completion.TrySetResult(null);
        var apply = MacTheme.CreateButton("应用", primary: true);
        apply.Click += (_, _) => _completion.TrySetResult(_color);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, apply },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                _preview,
                _hex,
                SliderRow("红", _red),
                SliderRow("绿", _green),
                SliderRow("蓝", _blue),
                new TextBlock
                {
                    Text = "常用颜色（右键保存）",
                    Foreground = new SolidColorBrush(MacTheme.SecondaryText),
                    Margin = new Thickness(0, 5, 0, 0),
                },
                palette,
                actions,
            },
        };
        Closed += (_, _) => _completion.TrySetResult(null);
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
    }

    public async Task<Color?> ShowAsync(Window? owner = null)
    {
        if (owner is null)
        {
            Show();
        }
        else
        {
            Show(owner);
        }

        Activate();
        var result = await _completion.Task;
        Close();
        return result;
    }

    public void CancelAndClose()
    {
        _completion.TrySetResult(null);
        if (IsVisible)
        {
            Close();
        }
    }

    private static Slider CreateSlider(byte value) => new()
    {
        Minimum = 0,
        Maximum = 255,
        Value = value,
        Width = 240,
    };

    private static StackPanel SliderRow(string label, Slider slider) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new TextBlock
            {
                Text = label,
                Width = 22,
                VerticalAlignment = VerticalAlignment.Center,
            },
            slider,
        },
    };

    private void UpdateFromSliders()
    {
        if (_updating)
        {
            return;
        }

        SetColor(Color.FromRgb(
            (byte)Math.Round(_red.Value),
            (byte)Math.Round(_green.Value),
            (byte)Math.Round(_blue.Value)));
    }

    private void TryApplyHex()
    {
        if (_updating || !Color.TryParse(_hex.Text, out var color))
        {
            return;
        }

        SetColor(Color.FromRgb(color.R, color.G, color.B));
    }

    private void SetColor(Color color)
    {
        _color = Color.FromRgb(color.R, color.G, color.B);
        _updating = true;
        _red.Value = _color.R;
        _green.Value = _color.G;
        _blue.Value = _color.B;
        _hex.Text = ToHex(_color);
        _preview.Background = new SolidColorBrush(_color);
        _updating = false;
    }

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
