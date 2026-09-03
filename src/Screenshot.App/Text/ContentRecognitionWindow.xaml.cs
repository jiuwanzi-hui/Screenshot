using System.Windows;
using System.Windows.Input;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Text;

public partial class ContentRecognitionWindow : Window
{
    public ContentRecognitionWindow(ContentRecognitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        InitializeComponent();
        Title = result.Title;
        TitleText.Text = result.Title;
        ResultTextBox.Text = result.IsSuccess
            ? result.Content
            : result.ErrorMessage ?? "识别失败。";
        ResultTextBox.IsEnabled = result.IsSuccess;
        StatusText.Text = result.IsSuccess
            ? "识别完成"
            : "未能识别";
        // Do not show the centered default frame before WPF has measured the
        // content. Reveal the dialog only after its first layout pass.
        Opacity = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = (SystemParameters.WorkArea.Width - Width) / 2 +
               SystemParameters.WorkArea.Left;
        Top = (SystemParameters.WorkArea.Height - Height) / 2 +
              SystemParameters.WorkArea.Top;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        UpdateLayout();
        Opacity = 1;
    }

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultTextBox.Text) ||
            !ResultTextBox.IsEnabled)
        {
            return;
        }

        try
        {
            await ClipboardTextService.SetTextAsync(ResultTextBox.Text);
            StatusText.Text = "已复制识别内容。";
        }
        catch
        {
            StatusText.Text = "剪贴板正被其他程序使用，请重试。";
        }
    }
}
