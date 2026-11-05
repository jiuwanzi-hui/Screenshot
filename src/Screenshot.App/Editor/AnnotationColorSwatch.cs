using System.Windows;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;

namespace Screenshot.App.Editor;

public static class AnnotationColorSwatch
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(AnnotationColorSwatch), new PropertyMetadata(false));

    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);

    internal static void ClosePickers(Window owner)
    {
        foreach (var picker in owner.OwnedWindows.OfType<ThemeColorPickerWindow>().ToArray())
            picker.Close();
    }

    internal static void Update(System.Windows.Media.Color color, Button custom, params Button[] presets)
    {
        var selected = presets.FirstOrDefault(button =>
            button.Background is SolidColorBrush brush && brush.Color == color) ?? custom;
        if (selected == custom)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            custom.Background = brush;
        }
        foreach (var button in presets.Append(custom))
        {
            SetIsSelected(button, button == selected);
            button.ToolTip = button == selected
                ? $"当前颜色：{color}（已选中）"
                : button == custom ? "选择自定义颜色" : $"选择颜色：{button.Tag}";
        }
    }
}
