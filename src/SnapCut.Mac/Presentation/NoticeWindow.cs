using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SnapCut.Mac.Presentation;

internal sealed class NoticeWindow : Window
{
    public NoticeWindow(string title, string message)
    {
        Title = title;
        Width = 440;
        Height = 210;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(MacTheme.WindowBackground);
        var close = MacTheme.CreateButton("知道了", primary: true);
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(MacTheme.PrimaryText),
                },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(MacTheme.SecondaryText),
                },
                close,
            },
        };
    }
}
