using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Text;

public partial class OcrResultWindow : Window
{
    private readonly Func<AppSettings> _settingsProvider;
    private readonly ITranslationCredentialStore _credentialStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ocrSucceeded;
    private bool _isTranslating;
    private bool _hasAutoTranslated;
    private string? _cachedTranslationSource;
    private string? _cachedTranslationText;

    public OcrResultWindow(
        OcrRecognitionResult result,
        AppSettings settings,
        ITranslationCredentialStore credentialStore,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(httpClient);

        _settingsProvider = () => settings;
        _credentialStore = credentialStore;
        _httpClient = httpClient;

        InitializeComponent();
        WindowPlacementService.Track(this, WindowPlacementKeys.OcrResult);
        ResultTextBox.Text = result.Text;
        StatusText.Text = result.IsSuccess
            ? "识别完成"
            : result.ErrorMessage ?? "文字识别失败。";
        _ocrSucceeded = result.IsSuccess;
        // Keep the first layout pass off-screen from the compositor. This
        // prevents a transient white window when the capture overlay closes.
        Opacity = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = (SystemParameters.WorkArea.Width - Width) / 2 +
               SystemParameters.WorkArea.Left;
        Top = (SystemParameters.WorkArea.Height - Height) / 2 +
              SystemParameters.WorkArea.Top;
        Loaded += OnLoaded;
    }

    public OcrResultWindow(
        OcrRecognitionResult result,
        Func<AppSettings> settingsProvider,
        ITranslationCredentialStore credentialStore,
        HttpClient httpClient)
        : this(
            result,
            (settingsProvider ?? throw new ArgumentNullException(
                nameof(settingsProvider)))(),
            credentialStore,
            httpClient)
    {
        _settingsProvider = settingsProvider;
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ClipboardTextService.SetTextAsync(ResultTextBox.Text);
            StatusText.Text = "已复制识别文字。";
        }
        catch
        {
            StatusText.Text = "剪贴板正被其他程序使用，请重试。";
        }
    }

    private async void OnCopyTranslationClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TranslationTextBox.Text))
        {
            StatusText.Text = "当前没有可复制的译文。";
            return;
        }

        try
        {
            await ClipboardTextService.SetTextAsync(TranslationTextBox.Text);
            StatusText.Text = "已复制全部译文。";
        }
        catch
        {
            StatusText.Text = "剪贴板正被其他程序使用，请重试。";
        }
    }

    private async void OnSelectableTextPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.C ||
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            sender is not System.Windows.Controls.TextBox textBox ||
            string.IsNullOrEmpty(textBox.SelectedText))
        {
            return;
        }

        e.Handled = true;
        try
        {
            await ClipboardTextService.SetTextAsync(textBox.SelectedText);
            StatusText.Text = "已复制所选文字。";
        }
        catch
        {
            StatusText.Text = "剪贴板正被其他程序使用，请重试。";
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        UpdateLayout();
        Opacity = 1;

        // Auto-translate once, matching the WeChat flow where recognized text is
        // rendered translated without a second click.
        if (_hasAutoTranslated ||
            !_ocrSucceeded ||
            string.IsNullOrWhiteSpace(ResultTextBox.Text))
        {
            return;
        }

        _hasAutoTranslated = true;
        await TranslateAsync();
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        await TranslateAsync();
    }

    private async Task TranslateAsync()
    {
        if (_isTranslating)
        {
            return;
        }

        var sourceText = ResultTextBox.Text;
        if (!string.IsNullOrWhiteSpace(_cachedTranslationText) &&
            string.Equals(
                _cachedTranslationSource,
                sourceText,
                StringComparison.Ordinal))
        {
            TranslationTextBox.Text = _cachedTranslationText;
            StatusText.Text = "已显示缓存译文，无需重新请求。";
            return;
        }

        _isTranslating = true;
        TranslateButton.IsEnabled = false;
        StatusText.Text = "正在翻译...";

        try
        {
            var settings = _settingsProvider();
            var provider = TranslationProviderFactory.Create(
                settings,
                _credentialStore,
                _httpClient);
            var result = await provider.TranslateAsync(
                sourceText,
                "auto",
                settings.TranslationTargetLanguage);

            if (result.IsSuccess)
            {
                _cachedTranslationSource = sourceText;
                _cachedTranslationText = result.Text;
                TranslationTextBox.Text = result.Text;
                StatusText.Text = "翻译完成。";
                TranslateButton.Content = "显示译文";
                TranslateButton.ToolTip = "直接显示本次识别结果的缓存译文";
            }
            else
            {
                StatusText.Text = result.ErrorMessage ?? "翻译失败。";
            }
        }
        finally
        {
            _isTranslating = false;
            TranslateButton.IsEnabled = true;
        }
    }
}
