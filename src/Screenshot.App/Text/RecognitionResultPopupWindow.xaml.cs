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
        TitleText.Text = title;
        ResultTextBox.Text = string.IsNullOrWhiteSpace(translatedText)
            ? sourceText
            : translatedText;
        SourceText.Text = string.IsNullOrWhiteSpace(translatedText)
            ? "识别内容"
            : "译文 · 已保留原文段落顺序";
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Left = SystemParameters.WorkArea.Right - ActualWidth - 18;
        Top = SystemParameters.WorkArea.Bottom - ActualHeight - 18;
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(
            0, 1, TimeSpan.FromMilliseconds(180)));
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
