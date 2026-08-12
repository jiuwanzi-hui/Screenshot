using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Text;

public partial class TextTranslationWindow : Window, IDisposable
{
    private static readonly TimeSpan AutomaticTranslationDelay =
        TimeSpan.FromMilliseconds(900);
    private readonly Func<AppSettings> _settingsProvider;
    private readonly Action<string>? _targetLanguageChanged;
    private readonly Action<string>? _installModelRequested;
    private CancellationTokenSource? _translationCancellation;
    private long _translationVersion;
    private bool _isInitialized;

    public TextTranslationWindow(
        Func<AppSettings> settingsProvider,
        Action<string>? targetLanguageChanged = null,
        Action<string>? installModelRequested = null)
    {
        ArgumentNullException.ThrowIfNull(settingsProvider);
        _settingsProvider = settingsProvider;
        _targetLanguageChanged = targetLanguageChanged;
        _installModelRequested = installModelRequested;
        InitializeComponent();
        WindowPlacementService.Track(this, WindowPlacementKeys.TextTranslation);

        SourceLanguageComboBox.ItemsSource = new[]
        {
            new LanguageOption("auto", "自动检测"),
        }.Concat(TranslationLanguageCatalog.Languages.Select(language =>
            new LanguageOption(language.Tag, language.DisplayName)));
        TargetLanguageComboBox.ItemsSource =
            TranslationLanguageCatalog.OfflineTargetLanguages.Select(language =>
                new LanguageOption(language.Tag, language.DisplayName));
        SourceLanguageComboBox.SelectedValue = "auto";
        TargetLanguageComboBox.SelectedValue =
            _settingsProvider().TranslationTargetLanguage;
        if (TargetLanguageComboBox.SelectedIndex < 0)
        {
            TargetLanguageComboBox.SelectedValue = "zh-Hans";
        }

        _isInitialized = true;
    }

    public void SetSourceText(string text, bool translateImmediately)
    {
        SourceTextBox.Text = text;
        SourceTextBox.SelectAll();
        SourceTextBox.Focus();
        if (translateImmediately)
        {
            _ = BeginTranslationAsync(delayBeforeTranslation: false);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = null;
        GC.SuppressFinalize(this);
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        await BeginTranslationAsync(delayBeforeTranslation: false);
    }

    private void OnSourceTextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        _ = BeginTranslationAsync(delayBeforeTranslation: true);
    }

    private void OnLanguageSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        if (ReferenceEquals(sender, TargetLanguageComboBox) &&
            TargetLanguageComboBox.SelectedValue is string targetLanguage &&
            !string.IsNullOrWhiteSpace(targetLanguage))
        {
            _targetLanguageChanged?.Invoke(targetLanguage);
        }

        if (string.IsNullOrWhiteSpace(SourceTextBox.Text))
        {
            return;
        }

        _ = BeginTranslationAsync(delayBeforeTranslation: true);
    }

    private async Task BeginTranslationAsync(bool delayBeforeTranslation)
    {
        var sourceText = SourceTextBox.Text.Trim();
        var version = Interlocked.Increment(ref _translationVersion);
        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = new CancellationTokenSource();
        var cancellationToken = _translationCancellation.Token;

        if (sourceText.Length == 0)
        {
            ResultTextBox.Clear();
            StatusText.Text = "输入文字后将自动翻译。";
            return;
        }

        if (delayBeforeTranslation)
        {
            StatusText.Text = "等待输入完成…";
            try
            {
                await Task.Delay(AutomaticTranslationDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        await TranslateAsync(sourceText, version, cancellationToken);
    }

    private async Task TranslateAsync(
        string sourceText,
        long version,
        CancellationToken cancellationToken)
    {
        TranslateButton.IsEnabled = false;
        ResultTextBox.Clear();
        InstallModelButton.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Visible;
        StatusText.Text = "正在使用本机离线模型翻译…";
        try
        {
            var settings = _settingsProvider();
            var bergamot = new OfflineTranslationProvider(
                OfflineTranslationModelManager.Shared,
                settings.OfflineTranslationQuality);
            ITranslationProvider provider = settings.OfflineTranslationEngine ==
                OfflineTranslationEngine.QwenLargeModel
                ? new OrderedTranslationProvider(
                    [
                        bergamot,
                        new LocalLargeModelTranslationProvider(
                            LocalLargeTranslationModelManager.Shared),
                    ])
                : bergamot;
            var result = await provider.TranslateAsync(
                sourceText,
                SourceLanguageComboBox.SelectedValue as string ?? "auto",
                TargetLanguageComboBox.SelectedValue as string ?? "zh-Hans",
                cancellationToken);
            if (version != Volatile.Read(ref _translationVersion) ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                ResultTextBox.Clear();
                StatusText.Text = result.ErrorMessage ?? "离线翻译失败。";
                if ((result.ErrorMessage ?? string.Empty).Contains(
                        "目标语言包",
                        StringComparison.Ordinal))
                {
                    InstallModelButton.Visibility = Visibility.Visible;
                    CopyButton.Visibility = Visibility.Collapsed;
                }
                return;
            }

            var targetLanguage =
                TargetLanguageComboBox.SelectedValue as string ?? "zh-Hans";
            if (!OrderedTranslationProvider.HasMeaningfulTranslation(
                    [sourceText],
                    [result.Text],
                    targetLanguage) ||
                OrderedTranslationProvider.ContainsUntranslatedHanText(
                    [sourceText],
                    [result.Text],
                    targetLanguage) ||
                !OrderedTranslationProvider.HasPlausibleTargetLanguage(
                    [sourceText],
                    [result.Text],
                    targetLanguage))
            {
                ResultTextBox.Clear();
                StatusText.Text = "离线模型未产生目标语言译文，请确认已下载对应语言包。";
                InstallModelButton.Visibility = Visibility.Visible;
                CopyButton.Visibility = Visibility.Collapsed;
                return;
            }

            ResultTextBox.Text = result.Text;
            StatusText.Text = "离线翻译完成。";
        }
        catch (OperationCanceledException)
        {
            // A newer edit superseded this request. Do not flash a cancellation
            // message over the status of the newer translation.
        }
        catch (Exception exception)
        {
            if (version == Volatile.Read(ref _translationVersion))
            {
                StatusText.Text = $"离线翻译失败：{exception.Message}";
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _translationVersion))
            {
                TranslateButton.IsEnabled = true;
            }
        }
    }

    private void OnInstallModelClick(object sender, RoutedEventArgs e)
    {
        var targetLanguage = TargetLanguageComboBox.SelectedValue as string;
        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            _installModelRequested?.Invoke(targetLanguage);
        }
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultTextBox.Text))
        {
            StatusText.Text = "当前没有可复制的译文。";
            return;
        }

        try
        {
            await ClipboardTextService.SetTextAsync(ResultTextBox.Text);
            StatusText.Text = "译文已复制。";
        }
        catch
        {
            StatusText.Text = "剪贴板正被其他程序使用，请重试。";
        }
    }

    private sealed record LanguageOption(string Value, string Label);
}
