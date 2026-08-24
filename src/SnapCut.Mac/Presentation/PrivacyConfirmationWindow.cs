using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Mac.Native;
using SnapCut.Mac.Text;

namespace SnapCut.Mac.Presentation;

internal sealed class PrivacyConfirmationWindow : Window
{
    private readonly TaskCompletionSource<IReadOnlyList<MacPrivacyCandidate>?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(CheckBox CheckBox, MacPrivacyCandidate Candidate)> _items = [];

    public PrivacyConfirmationWindow(IReadOnlyList<MacPrivacyCandidate> candidates)
    {
        Title = "SnapCut 隐私打码确认";
        Width = 600;
        Height = 480;
        MinWidth = 480;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(MacTheme.WindowBackground);
        var list = new StackPanel { Spacing = 7 };
        foreach (var candidate in candidates)
        {
            var checkBox = new CheckBox
            {
                IsChecked = true,
                Content = $"{candidate.KindLabel} · {candidate.Value}",
            };
            _items.Add((checkBox, candidate));
            list.Children.Add(checkBox);
        }

        var apply = MacTheme.CreateButton("确认并打码", primary: true);
        apply.Click += (_, _) => _completion.TrySetResult(
            _items.Where(item => item.CheckBox.IsChecked == true)
                .Select(item => item.Candidate)
                .ToArray());
        var cancel = MacTheme.CreateButton("取消");
        cancel.Click += (_, _) => _completion.TrySetResult(null);
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, apply },
        };
        Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "请确认需要处理的项目，未选中的内容不会被修改。",
                    Foreground = new SolidColorBrush(MacTheme.PrimaryText),
                    FontWeight = FontWeight.SemiBold,
                },
                new ScrollViewer { Content = list },
                toolbar,
            },
        };
        Grid.SetRow(((Grid)Content).Children[1], 1);
        Grid.SetRow(toolbar, 2);
        Closed += (_, _) => _completion.TrySetResult(null);
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
    }

    public async Task<IReadOnlyList<MacPrivacyCandidate>?> ShowAsync()
    {
        Show();
        Activate();
        var result = await _completion.Task;
        Close();
        return result;
    }
}
