using System.Runtime.InteropServices;
using System.Windows;

namespace Screenshot.App.Capture;

public partial class CapturePreviewWindow : Window
{
    private readonly CapturedImage _capturedImage;
    private readonly string _saveDirectory;
    private readonly CaptureHistoryItem? _historyItem;

    public CapturePreviewWindow(
        CapturedImage capturedImage,
        string saveDirectory,
        CaptureHistoryItem? historyItem)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);

        _capturedImage = capturedImage;
        _saveDirectory = saveDirectory;
        _historyItem = historyItem;

        InitializeComponent();
        DataContext = _capturedImage;
    }

    public event EventHandler? ReselectRequested;

    public event EventHandler? EditRequested;

    public event EventHandler? PinRequested;

    public event EventHandler? OcrRequested;

    public CapturedImage CloneImage()
    {
        return _capturedImage.Clone();
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    public void PositionToRightOf(ScreenRegion captureRegion)
    {
        var virtualScreen = VirtualScreen.GetBounds();
        WindowStartupLocation = WindowStartupLocation.Manual;
        UpdateLayout();
        var gap = 12d;
        var preferredX = captureRegion.X + captureRegion.Width + gap;
        var preferredY = captureRegion.Y + ((captureRegion.Height - ActualHeight) / 2d);
        Left = Math.Clamp(
            preferredX,
            virtualScreen.X + gap,
            virtualScreen.X + virtualScreen.Width - ActualWidth - gap);
        Top = Math.Clamp(
            preferredY,
            virtualScreen.Y + gap,
            virtualScreen.Y + virtualScreen.Height - ActualHeight - gap);
    }

    protected override void OnClosed(EventArgs e)
    {
        _capturedImage.Dispose();
        base.OnClosed(e);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetImage(_capturedImage.Preview);
            _historyItem?.MarkCopied();
            StatusText.Text = "已复制到剪贴板。";
        }
        catch (COMException)
        {
            StatusText.Text = "剪贴板正被其他程序使用，请重试。";
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetImage(_capturedImage.Preview);
            _historyItem?.MarkCopied();
            Close();
        }
        catch (COMException)
        {
            StatusText.Text = "剪贴板正被其他程序使用，请重试。";
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var savedPath = CaptureFileService.SaveAsPng(
                _capturedImage,
                _saveDirectory);
            _historyItem?.MarkSaved(savedPath);
            StatusText.Text = $"已保存到 {savedPath}";
        }
        catch (Exception)
        {
            StatusText.Text = "保存失败，请检查保存位置和权限。";
        }
    }

    private void OnReselectClick(object sender, RoutedEventArgs e)
    {
        ReselectRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        EditRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        PinRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOcrClick(object sender, RoutedEventArgs e)
    {
        OcrRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
