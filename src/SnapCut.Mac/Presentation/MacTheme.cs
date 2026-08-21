using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace SnapCut.Mac.Presentation;

internal static class MacTheme
{
    public static Color AccentStart { get; private set; } = Color.Parse("#2878D0");
    public static Color AccentEnd { get; private set; } = Color.Parse("#6268C8");
    public static Color WindowBackground { get; private set; } = Color.Parse("#F2F5F9");
    public static Color SidebarBackground { get; private set; } = Color.Parse("#EAF0F6");
    public static Color PanelBackground { get; private set; } = Colors.White;
    public static Color PrimaryText { get; private set; } = Color.Parse("#202734");
    public static Color SecondaryText { get; private set; } = Color.Parse("#687385");
    public static Color Border { get; private set; } = Color.Parse("#B8C4D2");
    public static Color SubtleBorder { get; private set; } = Color.Parse("#DCE3EB");
    public static Color Separator { get; private set; } = Color.Parse("#E1E6ED");
    public static Color AccentMuted { get; private set; } = Color.Parse("#DDE9F8");

    public static IBrush AccentBrush => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(AccentStart, 0),
            new GradientStop(AccentEnd, 1),
        },
    };

    public static void Apply(string theme)
    {
        var dark = theme is "Dark" or "ForestNight" or "ObsidianGold" or "NeonDeep" ||
            (theme == "System" && Application.Current?.ActualThemeVariant == ThemeVariant.Dark);
        if (theme == "NeonDeep")
        {
            AccentStart = Color.Parse("#2EAFA5");
            AccentEnd = Color.Parse("#E0529F");
        }
        else if (theme == "ObsidianGold")
        {
            AccentStart = Color.Parse("#D9A441");
            AccentEnd = Color.Parse("#F0D38A");
        }
        else if (theme == "CoralSky")
        {
            AccentStart = Color.Parse("#FF7A59");
            AccentEnd = Color.Parse("#F6B6D6");
        }
        else
        {
            AccentStart = Color.Parse("#2878D0");
            AccentEnd = Color.Parse("#6268C8");
        }
        if (dark)
        {
            WindowBackground = theme == "NeonDeep"
                ? Color.Parse("#101722")
                : theme == "ObsidianGold"
                    ? Color.Parse("#171513")
                    : Color.Parse("#171A20");
            SidebarBackground = theme == "NeonDeep"
                ? Color.Parse("#172335")
                : theme == "ObsidianGold"
                    ? Color.Parse("#211E18")
                    : Color.Parse("#1E232B");
            PanelBackground = theme == "NeonDeep"
                ? Color.Parse("#1D2A3B")
                : theme == "ObsidianGold"
                    ? Color.Parse("#2A241C")
                    : Color.Parse("#242A33");
            PrimaryText = Color.Parse("#F1F4F8");
            SecondaryText = Color.Parse("#AAB4C2");
            Border = Color.Parse("#4B5665");
            SubtleBorder = Color.Parse("#343D49");
            Separator = Color.Parse("#38424E");
            AccentMuted = theme == "NeonDeep"
                ? Color.Parse("#263F64")
                : Color.Parse("#263B55");
            if (Application.Current is not null)
            {
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            }
            return;
        }

        WindowBackground = theme == "GinkgoPaper"
            ? Color.Parse("#F3F0E6")
            : Color.Parse("#F2F5F9");
        SidebarBackground = Color.Parse("#EAF0F6");
        PanelBackground = Colors.White;
        PrimaryText = Color.Parse("#202734");
        SecondaryText = Color.Parse("#687385");
        Border = Color.Parse("#B8C4D2");
        SubtleBorder = Color.Parse("#DCE3EB");
        Separator = Color.Parse("#E1E6ED");
        AccentMuted = Color.Parse("#DDE9F8");
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = theme == "System"
                ? ThemeVariant.Default
                : ThemeVariant.Light;
        }
    }

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
            CornerRadius = new CornerRadius(8),
            Background = primary ? AccentBrush : new SolidColorBrush(PanelBackground),
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
