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
    private bool _closed;

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
        Screenshot.App.Presentation.WindowShell.Attach(this);
        WindowPlacementService.Track(this, WindowPlacementKeys.TextTranslation);

        SourceLanguageComboBox.ItemsSource = new[]
        {
            new LanguageOption("auto", "自动检测"),
        }.Concat(TranslationLanguageCatalog.Languages.Select(language =>
            new LanguageOption(language.Tag, language.DisplayName)));
        TargetLanguageComboBox.ItemsSource =
            TranslationLanguageCatalog.Languages.Select(language =>
                new LanguageOption(language.Tag, language.DisplayName));
        SourceLanguageComboBox.SelectedValue = "auto";
        TargetLanguageComboBox.SelectedValue =
            _settingsProvider().TranslationTargetLanguage;
        if (TargetLanguageComboBox.SelectedIndex < 0)
        {
            TargetLanguageComboBox.SelectedValue = "zh-Hans";
        }

        TranslationTargetPreferences.Changed += OnSharedTargetChanged;
        _isInitialized = true;
        RefreshModelRequirement();
    }

    private void OnSharedTargetChanged(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        if (TranslationTargetPreferences.Language is { } language &&
            TargetLanguageComboBox.SelectedValue as string != language)
            TargetLanguageComboBox.SelectedValue = language;
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
        Core.MemoryFootprint.TrimAfterHeavyOperation();
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Interlocked.Increment(ref _translationVersion);
        TranslationTargetPreferences.Changed -= OnSharedTargetChanged;
        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = null;
        SourceTextBox.Clear();
        ResultTextBox.Clear();
        SourceLanguageComboBox.ItemsSource = null;
        TargetLanguageComboBox.ItemsSource = null;
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
        if (_closed || !_isInitialized)
        {
            return;
        }

        _ = BeginTranslationAsync(delayBeforeTranslation: true);
    }

    private void OnLanguageSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_closed || !_isInitialized)
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
            RefreshModelRequirement();
            return;
        }

        _ = BeginTranslationAsync(delayBeforeTranslation: true);
    }

    private void RefreshModelRequirement()
    {
        var engine = _settingsProvider().OfflineTranslationEngine;
        var manager = engine == OfflineTranslationEngine.HunyuanLightweight
            ? LocalLargeTranslationModelManager.LightweightShared : LocalLargeTranslationModelManager.Shared;
        if (engine != OfflineTranslationEngine.Mozilla && !manager.GetStatus().IsInstalled)
        {
            StatusText.Text = $"需要下载 {manager.DisplayName} 多语言模型（模型约 {manager.ModelDownloadSize / 1073741824d:0.00} GiB）。多种语言共用一份，下载后离线运行。";
            InstallModelButton.Visibility = Visibility.Visible;
            CopyButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task BeginTranslationAsync(bool delayBeforeTranslation)
    {
        if (_closed)
        {
            return;
        }

        TranslationActivity.IsActive = false;
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
        if (_closed)
        {
            return;
        }

        TranslateButton.IsEnabled = false;
        ResultTextBox.Clear();
        InstallModelButton.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Visible;
        TranslationActivity.IsActive = true;
        StatusText.ToolTip = null;
        StatusText.Text = "正在使用本机离线模型翻译…";
        InstallModelButton.Content = "下载所需离线模型";
        using var progress = TranslationProgress.Begin(message =>
        {
            if (_closed || version != Volatile.Read(ref _translationVersion) || cancellationToken.IsCancellationRequested) return;
            StatusText.Text = message;
            StatusText.ToolTip = message;
            if (message.Contains("设置", StringComparison.Ordinal))
            {
                InstallModelButton.Content = "打开翻译设置";
                InstallModelButton.Visibility = Visibility.Visible;
            }
        });
        try
        {
            var settings = _settingsProvider();
            var bergamot = new OfflineTranslationProvider(
                OfflineTranslationModelManager.Shared,
                settings.OfflineTranslationQuality);
            ITranslationProvider provider = settings.OfflineTranslationEngine switch
            {
                OfflineTranslationEngine.HunyuanLightweight => new LocalLargeModelTranslationProvider(
                    LocalLargeTranslationModelManager.LightweightShared, bergamot),
                OfflineTranslationEngine.QwenLargeModel => new LocalLargeModelTranslationProvider(
                    LocalLargeTranslationModelManager.Shared, bergamot),
                _ => bergamot,
            };
            var result = await provider.TranslateAsync(
                sourceText,
                SourceLanguageComboBox.SelectedValue as string ?? "auto",
                TargetLanguageComboBox.SelectedValue as string ?? "zh-Hans",
                cancellationToken);
            if (_closed || version != Volatile.Read(ref _translationVersion) ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                ResultTextBox.Clear();
                StatusText.Text = result.ErrorMessage ?? "离线翻译失败。";
                StatusText.ToolTip = StatusText.Text;
                if ((result.ErrorMessage ?? string.Empty).Contains(
                        "下载",
                        StringComparison.Ordinal) || result.ErrorMessage?.Contains("设置", StringComparison.Ordinal) == true)
                {
                    if (result.ErrorMessage?.Contains("设置", StringComparison.Ordinal) == true) InstallModelButton.Content = "打开翻译设置";
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
            StatusText.ToolTip = result.Notice;
        }
        catch (OperationCanceledException)
        {
            // A newer edit superseded this request. Do not flash a cancellation
            // message over the status of the newer translation.
        }
        catch (Exception exception)
        {
            if (!_closed && version == Volatile.Read(ref _translationVersion))
            {
                StatusText.Text = $"离线翻译失败：{exception.Message}";
            }
        }
        finally
        {
            if (!_closed && version == Volatile.Read(ref _translationVersion))
            {
                TranslateButton.IsEnabled = true;
                TranslationActivity.IsActive = false;
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
