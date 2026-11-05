using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;

namespace Screenshot.App.Presentation;

// Application confirmations follow the chosen UI language, including buttons.
// OS message-box button captions otherwise always follow the Windows language.
internal sealed class LocalizedMessageBox : Window
{
    internal MessageBoxResult Result { get; private set; }

    internal LocalizedMessageBox(Window owner, string text, string caption, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult)
    {
        Owner = owner; Title = caption; ShowInTaskbar = false; Topmost = owner.Topmost;
        Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 80);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppWindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "AppTextPrimaryBrush");
        Result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No :
            buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var scroll = new ScrollViewer { Content = message, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(120, MaxHeight - 150) };
        root.Children.Add(scroll);
        var actions = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        Grid.SetRow(actions, 1); root.Children.Add(actions);
        var choices = buttons switch
        {
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK],
        };
        if (defaultResult == MessageBoxResult.None) defaultResult = choices[0];
        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = choice switch { MessageBoxResult.Yes => "是", MessageBoxResult.No => "否", MessageBoxResult.Cancel => "取消", _ => "确定" },
                MinWidth = 84, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0),
                IsDefault = choice == defaultResult, IsCancel = choice == Result, Tag = choice,
            };
            button.Click += (_, _) => { Result = choice; Close(); };
            actions.Children.Add(button);
        }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Content = root;
    }

    internal static MessageBoxResult Show(Window owner, string text, string caption,
        MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new LocalizedMessageBox(owner, text, caption, buttons, image, defaultResult);
        dialog.ShowDialog();
        return dialog.Result;
    }
}
