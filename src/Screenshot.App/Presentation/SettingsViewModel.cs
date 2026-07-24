using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Presentation;

public sealed record SettingOption(string Value, string Label);

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private AppSettings _baseSettings;
    private string _saveDirectory;
    private bool _showTaskbarIcon;
    private bool _showNotificationIcon;
    private bool _launchAtStartup;
    private WindowCloseBehavior _closeBehavior;
    private AppTheme _theme;
    private bool _keepHistory;
    private string _regionCaptureHotKey;
    private string _scrollCaptureHotKey;
    private string _ocrHotKey;
    private string _pinHotKey;
    private string _openSettingsHotKey;
    private string _ocrLanguageTag;
    private bool _sendTextToOnlineTranslation;
    private string _translationProvider;
    private string _translationEndpoint;
    private string _translationTargetLanguage;
    private string _translationModel;
    private string _translationApiKey = string.Empty;
    private string _statusMessage = string.Empty;

    public SettingsViewModel(AppSettings settings)
    {
        _baseSettings = settings;
        _saveDirectory = settings.SaveDirectory;
        _showTaskbarIcon = settings.ShowTaskbarIcon;
        _showNotificationIcon = settings.ShowNotificationIcon;
        _launchAtStartup = settings.LaunchAtStartup;
        _closeBehavior = settings.CloseBehavior;
        _theme = settings.Theme;
        _keepHistory = settings.KeepHistory;
        _regionCaptureHotKey = settings.RegionCaptureHotKey;
        _scrollCaptureHotKey = settings.ScrollCaptureHotKey;
        _ocrHotKey = settings.OcrHotKey;
        _pinHotKey = settings.PinHotKey;
        _openSettingsHotKey = settings.OpenSettingsHotKey;
        _ocrLanguageTag = settings.OcrLanguageTag;
        _sendTextToOnlineTranslation = settings.SendTextToOnlineTranslation;
        _translationProvider = TranslationProviderFactory.ResolveProviderId(
            settings.TranslationProvider);
        _translationEndpoint = settings.TranslationEndpoint;
        _translationTargetLanguage = settings.TranslationTargetLanguage;
        _translationModel = TranslationProviderFactory.NormalizeModel(
            settings.TranslationEndpoint,
            settings.TranslationModel);
        OcrLanguageOptions = CreateOcrLanguageOptions(settings.OcrLanguageTag);
        if (!string.IsNullOrWhiteSpace(_translationModel) &&
            !TranslationModelOptions.Contains(
                _translationModel,
                StringComparer.OrdinalIgnoreCase))
        {
            TranslationModelOptions.Insert(0, _translationModel);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SettingOption> OcrLanguageOptions { get; }

    public IReadOnlyList<SettingOption> TranslationProviderOptions { get; } =
    [
        new(
            TranslationProviderFactory.OpenAiCompatibleProviderId,
            "OpenAI 兼容接口"),
    ];

    public IReadOnlyList<SettingOption> TranslationTargetLanguageOptions { get; } =
    [
        new("zh-Hans", "简体中文（zh-Hans）"),
        new("zh-Hant", "繁體中文（zh-Hant）"),
        new("en", "English（en）"),
        new("ja", "日本語（ja）"),
        new("ko", "한국어（ko）"),
        new("fr", "Français（fr）"),
        new("de", "Deutsch（de）"),
        new("es", "Español（es）"),
        new("ru", "Русский（ru）"),
    ];

    public ObservableCollection<string> TranslationModelOptions { get; } = new(
    [
        "deepseek-v4-flash",
        "deepseek-v4-pro",
        "gpt-4.1-mini",
        "gpt-4.1",
        "gpt-4o-mini",
    ]);

    public string SaveDirectory
    {
        get => _saveDirectory;
        set => SetProperty(ref _saveDirectory, value);
    }

    public bool ShowTaskbarIcon
    {
        get => _showTaskbarIcon;
        set => SetProperty(ref _showTaskbarIcon, value);
    }

    public bool ShowNotificationIcon
    {
        get => _showNotificationIcon;
        set => SetProperty(ref _showNotificationIcon, value);
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => SetProperty(ref _launchAtStartup, value);
    }

    public WindowCloseBehavior CloseBehavior
    {
        get => _closeBehavior;
        set => SetProperty(ref _closeBehavior, value);
    }

    public AppTheme Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public bool KeepHistory
    {
        get => _keepHistory;
        set => SetProperty(ref _keepHistory, value);
    }

    public string RegionCaptureHotKey
    {
        get => _regionCaptureHotKey;
        set => SetProperty(ref _regionCaptureHotKey, value);
    }

    public string ScrollCaptureHotKey
    {
        get => _scrollCaptureHotKey;
        set => SetProperty(ref _scrollCaptureHotKey, value);
    }

    public string OcrHotKey
    {
        get => _ocrHotKey;
        set => SetProperty(ref _ocrHotKey, value);
    }

    public string PinHotKey
    {
        get => _pinHotKey;
        set => SetProperty(ref _pinHotKey, value);
    }

    public string OpenSettingsHotKey
    {
        get => _openSettingsHotKey;
        set => SetProperty(ref _openSettingsHotKey, value);
    }

    public string OcrLanguageTag
    {
        get => _ocrLanguageTag;
        set => SetProperty(ref _ocrLanguageTag, value);
    }

    public bool SendTextToOnlineTranslation
    {
        get => _sendTextToOnlineTranslation;
        set => SetProperty(ref _sendTextToOnlineTranslation, value);
    }

    public string TranslationProvider
    {
        get => _translationProvider;
        set => SetProperty(ref _translationProvider, value);
    }

    public string TranslationEndpoint
    {
        get => _translationEndpoint;
        set => SetProperty(ref _translationEndpoint, value);
    }

    public string TranslationTargetLanguage
    {
        get => _translationTargetLanguage;
        set => SetProperty(ref _translationTargetLanguage, value);
    }

    public string TranslationModel
    {
        get => _translationModel;
        set => SetProperty(ref _translationModel, value);
    }

    public string TranslationApiKey
    {
        get => _translationApiKey;
        set => SetProperty(ref _translationApiKey, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public AppSettings CreateSettings()
    {
        return _baseSettings with
        {
            SaveDirectory = SaveDirectory,
            ShowTaskbarIcon = ShowTaskbarIcon,
            ShowNotificationIcon = ShowNotificationIcon,
            LaunchAtStartup = LaunchAtStartup,
            CloseBehavior = CloseBehavior,
            Theme = Theme,
            KeepHistory = KeepHistory,
            RegionCaptureHotKey = RegionCaptureHotKey,
            ScrollCaptureHotKey = ScrollCaptureHotKey,
            OcrHotKey = OcrHotKey,
            PinHotKey = PinHotKey,
            OpenSettingsHotKey = OpenSettingsHotKey,
            OcrLanguageTag = OcrLanguageTag,
            SendTextToOnlineTranslation = SendTextToOnlineTranslation,
            TranslationProvider = TranslationProvider,
            TranslationEndpoint = TranslationEndpoint,
            TranslationTargetLanguage = TranslationTargetLanguage,
            TranslationModel = TranslationModel,
        };
    }

    public void Apply(AppSettings settings)
    {
        _baseSettings = settings;
        SaveDirectory = settings.SaveDirectory;
        ShowTaskbarIcon = settings.ShowTaskbarIcon;
        ShowNotificationIcon = settings.ShowNotificationIcon;
        LaunchAtStartup = settings.LaunchAtStartup;
        CloseBehavior = settings.CloseBehavior;
        Theme = settings.Theme;
        KeepHistory = settings.KeepHistory;
        RegionCaptureHotKey = settings.RegionCaptureHotKey;
        ScrollCaptureHotKey = settings.ScrollCaptureHotKey;
        OcrHotKey = settings.OcrHotKey;
        PinHotKey = settings.PinHotKey;
        OpenSettingsHotKey = settings.OpenSettingsHotKey;
        OcrLanguageTag = settings.OcrLanguageTag;
        SendTextToOnlineTranslation = settings.SendTextToOnlineTranslation;
        TranslationProvider = TranslationProviderFactory.ResolveProviderId(
            settings.TranslationProvider);
        TranslationEndpoint = settings.TranslationEndpoint;
        TranslationTargetLanguage = settings.TranslationTargetLanguage;
        TranslationModel = TranslationProviderFactory.NormalizeModel(
            settings.TranslationEndpoint,
            settings.TranslationModel);
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
    }

    public void SetTranslationModels(IReadOnlyList<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var values = models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(TranslationModel) &&
            !values.Contains(
                TranslationModel,
                StringComparer.OrdinalIgnoreCase))
        {
            values.Insert(0, TranslationModel);
        }

        TranslationModelOptions.Clear();
        foreach (var model in values)
        {
            TranslationModelOptions.Add(model);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private static SettingOption[] CreateOcrLanguageOptions(
        string configuredLanguageTag)
    {
        IReadOnlyList<string> availableLanguageTags;
        try
        {
            availableLanguageTags = OcrService.GetAvailableLanguageTags();
        }
        catch
        {
            availableLanguageTags = [];
        }

        var tags = availableLanguageTags
            .Concat([configuredLanguageTag])
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase);
        return tags
            .Select(tag => new SettingOption(tag, CreateLanguageLabel(tag)))
            .ToArray();
    }

    private static string CreateLanguageLabel(string languageTag)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageTag);
            return $"{culture.NativeName}（{languageTag}）";
        }
        catch (CultureNotFoundException)
        {
            return languageTag;
        }
    }
}
