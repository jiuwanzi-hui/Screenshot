using System.Runtime.InteropServices;
using System.Net.Http;
using System.Windows;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public partial class OcrResultWindow : Window
{
    private readonly Func<AppSettings> _settingsProvider;
    private readonly ITranslationCredentialStore _credentialStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ocrSucceeded;
    private bool _isTranslating;
    private bool _hasAutoTranslated;

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
        ResultTextBox.Text = result.Text;
        StatusText.Text = result.IsSuccess
            ? "识别完成"
            : result.ErrorMessage ?? "文字识别失败。";
        _ocrSucceeded = result.IsSuccess;
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

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(ResultTextBox.Text);
            StatusText.Text = "已复制识别文字。";
        }
        catch (COMException)
        {
            StatusText.Text = "剪贴板正被其他程序使用，请重试。";
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // Auto-translate once, matching the WeChat flow where recognized text is
        // rendered translated without a second click. This only runs when the user
        // has already opted into online translation and the OCR pass produced text.
        if (_hasAutoTranslated ||
            !_ocrSucceeded ||
            string.IsNullOrWhiteSpace(ResultTextBox.Text) ||
            !_settingsProvider().SendTextToOnlineTranslation)
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

        _isTranslating = true;
        StatusText.Text = "正在翻译...";

        try
        {
            var settings = _settingsProvider();
            var provider = TranslationProviderFactory.Create(
                settings,
                _credentialStore,
                _httpClient);
            var result = await provider.TranslateAsync(
                ResultTextBox.Text,
                settings.OcrLanguageTag,
                settings.TranslationTargetLanguage);

            if (result.IsSuccess)
            {
                TranslationTextBox.Text = result.Text;
                StatusText.Text = "翻译完成。";
            }
            else
            {
                StatusText.Text = result.ErrorMessage ?? "翻译失败。";
            }
        }
        finally
        {
            _isTranslating = false;
        }
    }
}
