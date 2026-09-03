using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Screenshot.App.Capture;

namespace Screenshot.App.Text;

public partial class RecognitionResultPopupWindow : Window
{
    private readonly bool _closeAfterCopy;

    public RecognitionResultPopupWindow(
        string title,
        string sourceText,
        string? translatedText,
        bool closeAfterCopy = true)
    {
        _closeAfterCopy = closeAfterCopy;
        InitializeComponent();
        // Do not let WPF present the window at its default location for one
        // compositor frame. Position it first, then reveal it in OnLoaded.
        Opacity = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Right - Width - 18;
        Top = SystemParameters.WorkArea.Bottom - Height - 18;
        TitleText.Text = title;
        ResultTextBox.Text = string.IsNullOrWhiteSpace(translatedText)
            ? sourceText
            : translatedText;
        // WPF's default TextBox copy command calls Clipboard.SetText
        // synchronously on the UI thread. On machines with a clipboard
        // manager or Office integration that can block the whole popup for
        // several seconds. Route both Ctrl+C and the context-menu Copy item
        // through the existing asynchronous clipboard service instead.
        ResultTextBox.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Copy,
            OnCopySelectionCommand,
            OnCanCopySelectionCommand));
        SourceText.Text = string.IsNullOrWhiteSpace(translatedText)
            ? "识别内容"
            : "译文 · 已保留原文段落顺序";
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        UpdateLayout();
        Left = SystemParameters.WorkArea.Right - ActualWidth - 18;
        Top = SystemParameters.WorkArea.Bottom - ActualHeight - 18;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            !IsHeaderCommandSource(e.OriginalSource))
        {
            DragMove();
        }
    }

    private static bool IsHeaderCommandSource(object source)
    {
        for (var current = source as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCanCopySelectionCommand(
        object sender,
        CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !string.IsNullOrEmpty(ResultTextBox.SelectedText);
        e.Handled = true;
    }

    private async void OnCopySelectionCommand(
        object sender,
        ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        var selectedText = ResultTextBox.SelectedText;
        if (string.IsNullOrEmpty(selectedText))
        {
            return;
        }

        try
        {
            await ClipboardTextService.SetTextAsync(selectedText);
            SourceText.Text = "已复制所选文字";
        }
        catch
        {
            SourceText.Text = "剪贴板正被其他程序使用，请重试";
        }
    }

    private void OnResizeThumbDragDelta(
        object sender,
        System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var edge = (sender as FrameworkElement)?.Tag as string;
        var deltaX = e.HorizontalChange;
        var deltaY = e.VerticalChange;

        if (edge is "TopLeft" or "BottomLeft")
        {
            var newWidth = Math.Max(MinWidth, Width - deltaX);
            Left += Width - newWidth;
            Width = newWidth;
        }
        else if (edge is "TopRight" or "BottomRight")
        {
            Width = Math.Max(MinWidth, Width + deltaX);
        }

        if (edge is "TopLeft" or "TopRight")
        {
            var newHeight = Math.Max(MinHeight, Height - deltaY);
            Top += Height - newHeight;
            Height = newHeight;
        }
        else if (edge is "BottomLeft" or "BottomRight")
        {
            Height = Math.Max(MinHeight, Height + deltaY);
        }
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ClipboardTextService.SetTextAsync(ResultTextBox.Text);
            SourceText.Text = "已复制全部内容";
            // The user can pin an ordinary result popup after it is shown.  The
            // current pin state must take precedence over the creation default.
            if (_closeAfterCopy && PinButton.IsChecked != true)
            {
                Close();
            }
        }
        catch
        {
            SourceText.Text = "剪贴板正被其他程序使用，请重试";
        }
    }
}
