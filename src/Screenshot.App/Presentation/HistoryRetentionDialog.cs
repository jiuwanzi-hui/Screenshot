using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Screenshot.App.Presentation;

internal sealed class HistoryRetentionDialog : Window
{
    private readonly WpfTextBox _retentionDaysTextBox;
    private readonly WpfTextBox _historyLimitTextBox;

    public HistoryRetentionDialog(
        Window owner,
        string historyName,
        int retentionDays,
        int historyLimit)
    {
        Owner = owner;
        Title = $"设置{historyName}保留策略";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        MinHeight = 270;
        MaxHeight = SystemParameters.WorkArea.Height - 80;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        SetResourceReference(BackgroundProperty, "AppWindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "AppTextPrimaryBrush");
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;

        _retentionDaysTextBox = CreateValueTextBox(retentionDays);
        _historyLimitTextBox = CreateValueTextBox(historyLimit);

        var root = new StackPanel
        {
            Margin = new Thickness(24),
        };
        var primaryText = new TextBlock
        {
            Text = $"取消“全部保留”后，{historyName}将按下面的策略清理。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        };
        primaryText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "AppTextPrimaryBrush");
        root.Children.Add(primaryText);
        var secondaryText = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 16),
            Text = "直接点击“继续”可沿用之前的设置；点击“取消”则继续全部保留。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        secondaryText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "AppTextSecondaryBrush");
        root.Children.Add(secondaryText);

        var fields = new Grid();
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var countSuffix = historyName == "录屏历史" ? "个" : "张";
        AddField(fields, 0, "保留天数", _retentionDaysTextBox, "天");
        AddField(fields, 1, "最多数量", _historyLimitTextBox, countSuffix);
        root.Children.Add(fields);

        var buttons = new StackPanel
        {
            Margin = new Thickness(0, 22, 0, 0),
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "取消",
            Width = 86,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
        };
        cancelButton.Click += (_, _) => Cancel();
        var continueButton = new System.Windows.Controls.Button
        {
            Content = "继续",
            Width = 86,
            Height = 32,
            IsDefault = true,
        };
        continueButton.Click += OnContinueClick;
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(continueButton);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            _retentionDaysTextBox.Focus();
            _retentionDaysTextBox.SelectAll();
        };
    }

    public int RetentionDays { get; private set; }

    public int HistoryLimit { get; private set; }

    public bool? ConfirmationResult { get; private set; }

    private static WpfTextBox CreateValueTextBox(int value) => new()
    {
        Text = value.ToString(CultureInfo.InvariantCulture),
        Width = 90,
        Height = 32,
        Margin = new Thickness(12, 0, 8, 10),
        Padding = new Thickness(8, 3, 8, 3),
        VerticalContentAlignment = VerticalAlignment.Center,
        MaxLength = 4,
    };

    private static void AddField(
        Grid grid,
        int row,
        string label,
        WpfTextBox textBox,
        string suffix)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetRow(labelBlock, row);
        grid.Children.Add(labelBlock);
        Grid.SetRow(textBox, row);
        Grid.SetColumn(textBox, 1);
        grid.Children.Add(textBox);
        var suffixBlock = new TextBlock
        {
            Text = suffix,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetRow(suffixBlock, row);
        Grid.SetColumn(suffixBlock, 2);
        grid.Children.Add(suffixBlock);
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(
                _retentionDaysTextBox.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var retentionDays) ||
            retentionDays is < 1 or > 3650)
        {
            ShowValidationMessage("保留天数必须是 1 到 3650 之间的整数。", _retentionDaysTextBox);
            return;
        }

        if (!int.TryParse(
                _historyLimitTextBox.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var historyLimit) ||
            historyLimit is < 1 or > 100)
        {
            ShowValidationMessage("最多数量必须是 1 到 100 之间的整数。", _historyLimitTextBox);
            return;
        }

        RetentionDays = retentionDays;
        HistoryLimit = historyLimit;
        ConfirmationResult = true;
        Close();
    }

    private void ShowValidationMessage(string message, WpfTextBox target)
    {
        System.Windows.MessageBox.Show(
            this,
            message,
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        target.Focus();
        target.SelectAll();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Owner?.IsActive == true && IsVisible)
            {
                Cancel();
            }
        });
    }

    private void OnPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    private void Cancel()
    {
        ConfirmationResult = false;
        Close();
    }
}
