using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SnapCut.Mac.Presentation;

internal static class MacTheme
{
    public static readonly Color AccentStart = Color.Parse("#7657FF");
    public static readonly Color AccentEnd = Color.Parse("#F05AA6");
    public static readonly Color WindowBackground = Color.Parse("#F7F7FA");
    public static readonly Color PanelBackground = Colors.White;
    public static readonly Color PrimaryText = Color.Parse("#20202A");
    public static readonly Color SecondaryText = Color.Parse("#6D6B78");
    public static readonly Color Border = Color.Parse("#E6E3EC");

    public static IBrush AccentBrush { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(AccentStart, 0),
            new GradientStop(AccentEnd, 1),
        },
    };

    public static Button CreateButton(string text, bool primary = false)
    {
        return new Button
        {
            Content = text,
            MinWidth = 86,
            Height = 36,
            Padding = new Thickness(16, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            Background = primary ? AccentBrush : Brushes.White,
            Foreground = primary ? Brushes.White : new SolidColorBrush(PrimaryText),
            BorderBrush = primary ? Brushes.Transparent : new SolidColorBrush(Border),
            BorderThickness = new Thickness(1),
        };
    }

    public static Border CreatePanel(Control content, Thickness? padding = null)
    {
        return new Border
        {
            Background = new SolidColorBrush(PanelBackground),
            BorderBrush = new SolidColorBrush(Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = padding ?? new Thickness(20),
            Child = content,
        };
    }
}
