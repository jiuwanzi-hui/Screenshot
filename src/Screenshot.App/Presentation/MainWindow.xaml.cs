using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;
using Screenshot.App.Update;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Presentation;

public partial class MainWindow : Window, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly IStartupRegistrationService _startupRegistrationService;
    private readonly GlobalHotKeyManager _globalHotKeyManager;
    private readonly ITranslationCredentialStore _translationCredentialStore;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly DispatcherTimer _settingsApplyTimer;
    private readonly HttpClient _modelCatalogHttpClient;
    private readonly bool _ownsModelCatalogHttpClient;
    private readonly ApplicationUpdateService _applicationUpdateService;
    private readonly bool _ownsApplicationUpdateService;
    private readonly CancellationTokenSource _updateCancellationSource = new();
    private AppSettings _savedSettings;
    private IReadOnlyList<HotKeyBinding>? _suspendedHotKeyBindings;
    private bool _exitRequested;
    private bool _isApplyingSettings;
    private bool _translationApiKeyChanged;
    private ApplicationUpdateInfo? _availableUpdate;
    private int _automaticUpdateCheckInProgress;
    private bool _disposed;

    public MainWindow(
        AppSettings initialSettings,
        SettingsStore settingsStore,
        IStartupRegistrationService startupRegistrationService,
        GlobalHotKeyManager globalHotKeyManager,
        ITranslationCredentialStore translationCredentialStore,
        HttpClient? modelCatalogHttpClient = null,
        ApplicationUpdateService? applicationUpdateService = null)
    {
        ArgumentNullException.ThrowIfNull(initialSettings);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(startupRegistrationService);
        ArgumentNullException.ThrowIfNull(globalHotKeyManager);
        ArgumentNullException.ThrowIfNull(translationCredentialStore);

        _savedSettings = initialSettings;
        _settingsStore = settingsStore;
        _startupRegistrationService = startupRegistrationService;
        _globalHotKeyManager = globalHotKeyManager;
        _translationCredentialStore = translationCredentialStore;
        _modelCatalogHttpClient = modelCatalogHttpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _ownsModelCatalogHttpClient = modelCatalogHttpClient is null;
        _applicationUpdateService = applicationUpdateService ??
            new ApplicationUpdateService();
        _ownsApplicationUpdateService = applicationUpdateService is null;
        _settingsViewModel = new SettingsViewModel(initialSettings);
        _settingsApplyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _settingsApplyTimer.Tick += OnSettingsApplyTimerTick;

        InitializeComponent();
        Activated += OnSettingsWindowActivated;
        DataContext = _settingsViewModel;
        UpdateThemeSelection(initialSettings.Theme);
        UpdateCloseBehaviorSelection(initialSettings.CloseBehavior);
        LoadTranslationApiKey(
            TranslationProviderFactory.ResolveProviderId(
                initialSettings.TranslationProvider));
        ShowSettingsSection(sectionIndex: 0);
        ShowOcrLanguageAvailability();
        CurrentVersionText.Text =
            $"当前版本 {AppMetadata.DisplayVersion} · " +
            (AppMetadata.IsInstalled ? "安装版" : "免安装版");
    }

    public void ConfigureTaskbarVisibility(bool showInTaskbar)
    {
        ShowInTaskbar = showInTaskbar;
    }

    public void ApplySettingsPalette(AppTheme theme)
    {
        AppThemeManager.ApplySettingsPalette(Resources, theme);
    }

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    public event EventHandler? ExitRequested;

    public event EventHandler? UpdateInstallationStarted;

    public bool IsCapturingHotKey { get; private set; }

    public void ShowFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnSettingsWindowActivated(object? sender, EventArgs e)
    {
        _ = CheckForUpdatesOnOpenAsync();
    }

    public void ShowStatus(string message)
    {
        _settingsViewModel.SetStatus(message);
    }

    public void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        EndHotKeyCapture(restoreRegistrations: true);
        _settingsApplyTimer.Stop();
        _settingsApplyTimer.Tick -= OnSettingsApplyTimerTick;
        Activated -= OnSettingsWindowActivated;
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsModelCatalogHttpClient)
        {
            _modelCatalogHttpClient.Dispose();
        }
        _updateCancellationSource.Cancel();
        _updateCancellationSource.Dispose();
        if (_ownsApplicationUpdateService)
        {
            _applicationUpdateService.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        EndHotKeyCapture(restoreRegistrations: true);
        base.OnClosing(e);

        if (e.Cancel)
        {
            return;
        }

        if (ApplicationClosePolicy.ShouldHideWindow(
                _exitRequested,
                _savedSettings.CloseBehavior))
        {
            e.Cancel = true;
            Hide();
            // Entering tray residency is the moment the footprint matters.
            _ = Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                Core.MemoryFootprint.TrimAfterHeavyOperation);
        }
        else if (ApplicationClosePolicy.ShouldExitApplication(
                     _exitRequested,
                     _savedSettings.CloseBehavior))
        {
            e.Cancel = true;
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSettingsNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowSettingsSection(SettingsNavigation.SelectedIndex);
    }

    private void OnBrowseSaveDirectoryClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择截图保存位置",
            UseDescriptionForTitle = true,
        };

        if (Directory.Exists(_settingsViewModel.SaveDirectory))
        {
            dialog.InitialDirectory = _settingsViewModel.SaveDirectory;
        }

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            _settingsViewModel.SaveDirectory = dialog.SelectedPath;
            ApplySettings();
        }
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(showBusyState: true);
    }

    private async Task CheckForUpdatesOnOpenAsync()
    {
        if (_disposed ||
            Interlocked.Exchange(ref _automaticUpdateCheckInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            await CheckForUpdatesAsync(showBusyState: false);
        }
        finally
        {
            Volatile.Write(ref _automaticUpdateCheckInProgress, 0);
        }
    }

    private async Task CheckForUpdatesAsync(bool showBusyState)
    {
        if (showBusyState)
        {
            CheckForUpdatesButton.IsEnabled = false;
            InstallUpdateButton.Visibility = Visibility.Collapsed;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "正在检测 Gitee / GitHub 更新源...";
        }

        try
        {
            var result = await _applicationUpdateService.CheckAsync(
                AppMetadata.CurrentVersion,
                _updateCancellationSource.Token);
            _availableUpdate = result.AvailableUpdate;
            UpdateStatusText.Text = result.Message;
            SetUpdateNavigationState(result.AvailableUpdate?.Version);
            if (result.AvailableUpdate is not null)
            {
                InstallUpdateButton.Content =
                    $"下载并更新到 {ApplicationUpdateService.NormalizeVersion(result.AvailableUpdate.Version)}";
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
            else if (!showBusyState)
            {
                InstallUpdateButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
            if (showBusyState)
            {
                UpdateStatusText.Text = "已取消检查更新。";
            }
        }
        finally
        {
            if (showBusyState)
            {
                CheckForUpdatesButton.IsEnabled = true;
            }
        }
    }

    internal void SetUpdateNavigationState(Version? availableVersion)
    {
        if (availableVersion is null)
        {
            UpdateNavigationText.Text = "版本更新";
            UpdateNavigationText.ClearValue(TextBlock.ForegroundProperty);
            UpdateNavigationText.ToolTip = null;
            return;
        }

        var normalizedVersion =
            ApplicationUpdateService.NormalizeVersion(availableVersion);
        UpdateNavigationText.Text = "有新版本";
        UpdateNavigationText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "AppWarmAccentBrush");
        UpdateNavigationText.ToolTip =
            $"发现 {normalizedVersion}，点击查看";
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            this,
            "更新下载完成后程序会自动关闭并覆盖更新，然后重新启动。\n\n" +
            "ScreenshotData 中的设置、历史和截图会保留。是否继续？",
            "更新 Screenshot",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        CheckForUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        var asset = AppMetadata.IsInstalled
            ? _availableUpdate.Installer
            : _availableUpdate.Portable;
        var progress = new Progress<double>(value =>
        {
            UpdateProgressBar.Value = value * 100;
            UpdateStatusText.Text = $"正在下载更新… {value:P0}";
        });

        try
        {
            var packagePath = await _applicationUpdateService.DownloadAsync(
                asset,
                progress,
                _updateCancellationSource.Token);
            UpdateStatusText.Text = "校验完成，正在启动覆盖更新...";
            ApplicationUpdateLauncher.Launch(_availableUpdate, packagePath);
            UpdateInstallationStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "已取消下载更新。";
            RestoreUpdateButtons();
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"更新失败：{exception.Message}";
            RestoreUpdateButtons();
        }
    }

    private void RestoreUpdateButtons()
    {
        CheckForUpdatesButton.IsEnabled = true;
        InstallUpdateButton.IsEnabled = true;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
    }

    private void OnTranslationApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        _settingsViewModel.TranslationApiKey = TranslationApiKeyBox.Password;
        if (_isApplyingSettings)
        {
            return;
        }

        _translationApiKeyChanged = true;
        ScheduleSettingsApply();
    }

    private void LoadTranslationApiKey(string providerId)
    {
        var resolvedProviderId = TranslationProviderFactory.ResolveProviderId(providerId);
        var apiKey = _translationCredentialStore.GetApiKey(resolvedProviderId);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        _isApplyingSettings = true;
        try
        {
            TranslationApiKeyBox.Password = apiKey;
            _settingsViewModel.TranslationApiKey = apiKey;
            _translationApiKeyChanged = false;
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void OnTranslationApiKeyLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        ApplySettingsImmediately();
    }

    private async void OnFetchTranslationModelsClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_isApplyingSettings ||
            sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        if (!ApplySettingsImmediately())
        {
            return;
        }

        button.IsEnabled = false;
        button.Content = "获取中...";
        _settingsViewModel.SetStatus("正在从翻译服务获取模型列表...");
        try
        {
            var result = await TranslationModelCatalogService.FetchAsync(
                _settingsViewModel.TranslationEndpoint,
                TranslationApiKeyBox.Password,
                _modelCatalogHttpClient);
            if (!result.IsSuccess)
            {
                _settingsViewModel.SetStatus(
                    result.ErrorMessage ?? "获取模型失败。");
                return;
            }

            _settingsViewModel.SetTranslationModels(result.Models);
            if (result.Models.Count == 1)
            {
                _settingsViewModel.TranslationModel = result.Models[0];
                ApplySettingsImmediately();
            }

            _settingsViewModel.SetStatus(
                result.Models.Count == 1
                    ? $"已获取并选择模型：{result.Models[0]}。"
                    : $"已获取 {result.Models.Count} 个模型，请在下拉框中选择。" );
        }
        catch
        {
            _settingsViewModel.SetStatus("获取模型失败，请检查服务地址和网络连接。");
        }
        finally
        {
            button.Content = "获取模型";
            button.IsEnabled = true;
        }
    }

    private void OnTextSettingChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isApplyingSettings && IsLoaded)
        {
            ScheduleSettingsApply();
        }
    }

    private void OnImmediateSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isApplyingSettings)
        {
            return;
        }

        if (sender is System.Windows.Controls.CheckBox checkBox)
        {
            checkBox.GetBindingExpression(
                System.Windows.Controls.CheckBox.IsCheckedProperty)?.UpdateSource();
        }
        else if (sender is System.Windows.Controls.ComboBox comboBox)
        {
            var property = comboBox.IsEditable
                ? System.Windows.Controls.ComboBox.TextProperty
                : System.Windows.Controls.ComboBox.SelectedValueProperty;
            comboBox.GetBindingExpression(property)?.UpdateSource();
        }

        ApplySettingsImmediately();
    }

    private void OnThemeOptionChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isApplyingSettings ||
            sender is not System.Windows.Controls.RadioButton { Tag: AppTheme theme })
        {
            return;
        }

        _settingsViewModel.Theme = theme;
        ApplySettingsImmediately();
    }

    private void UpdateThemeSelection(AppTheme theme)
    {
        SystemThemeOption.IsChecked = theme == AppTheme.System;
        LightThemeOption.IsChecked = theme == AppTheme.Light;
        DarkThemeOption.IsChecked = theme == AppTheme.Dark;
    }

    private void OnCloseBehaviorOptionChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isApplyingSettings ||
            sender is not System.Windows.Controls.RadioButton
            {
                Tag: WindowCloseBehavior closeBehavior,
            })
        {
            return;
        }

        _settingsViewModel.CloseBehavior = closeBehavior;
        ApplySettingsImmediately();
    }

    private void UpdateCloseBehaviorSelection(WindowCloseBehavior closeBehavior)
    {
        MinimizeOnCloseOption.IsChecked =
            closeBehavior == WindowCloseBehavior.MinimizeToBackground;
        ExitOnCloseOption.IsChecked =
            closeBehavior == WindowCloseBehavior.ExitApplication;
    }

    private void OnSettingEditorLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        ApplySettingsImmediately();
    }

    private void OnHotKeyCaptured(object? sender, HotKeyCapturedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Control { Tag: string settingName })
        {
            return;
        }

        EndHotKeyCapture(restoreRegistrations: true);

        switch (settingName)
        {
            case nameof(SettingsViewModel.RegionCaptureHotKey):
                _settingsViewModel.RegionCaptureHotKey = e.Gesture;
                break;
            case nameof(SettingsViewModel.ScrollCaptureHotKey):
                _settingsViewModel.ScrollCaptureHotKey = e.Gesture;
                break;
            case nameof(SettingsViewModel.OcrHotKey):
                _settingsViewModel.OcrHotKey = e.Gesture;
                break;
            case nameof(SettingsViewModel.PinHotKey):
                _settingsViewModel.PinHotKey = e.Gesture;
                break;
            case nameof(SettingsViewModel.OpenSettingsHotKey):
                _settingsViewModel.OpenSettingsHotKey = e.Gesture;
                break;
            default:
                return;
        }

        if (!ApplySettingsImmediately())
        {
            RestoreCapturedHotKey(settingName);
            TryRestoreHotKeys();
        }

        Keyboard.ClearFocus();
    }

    private void OnHotKeyCaptureGotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (IsCapturingHotKey)
        {
            return;
        }

        _suspendedHotKeyBindings = _globalHotKeyManager.SuspendRegistrations();
        IsCapturingHotKey = true;
        _settingsViewModel.SetStatus(
            "请直接按下新的快捷键组合，按 Backspace 或 Delete 清空，按 Esc 取消。");
    }

    private void OnHotKeyCaptureLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        EndHotKeyCapture(restoreRegistrations: true);
    }

    private void OnHotKeyCaptureCanceled(object? sender, EventArgs e)
    {
        EndHotKeyCapture(restoreRegistrations: true);
        Keyboard.ClearFocus();
        _settingsViewModel.SetStatus("已取消修改快捷键。");
    }

    private void OnBeginHotKeyEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string captureBoxName } ||
            FindName(captureBoxName) is not HotKeyCaptureBox captureBox)
        {
            return;
        }

        captureBox.Focus();
        Keyboard.Focus(captureBox);
    }

    private void OnClearHotKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string captureBoxName } ||
            FindName(captureBoxName) is not HotKeyCaptureBox captureBox)
        {
            return;
        }

        captureBox.ClearGesture();
    }

    private void EndHotKeyCapture(bool restoreRegistrations)
    {
        if (!IsCapturingHotKey)
        {
            return;
        }

        var suspendedBindings = _suspendedHotKeyBindings;
        _suspendedHotKeyBindings = null;
        IsCapturingHotKey = false;

        if (!restoreRegistrations || suspendedBindings is not { Count: > 0 })
        {
            return;
        }

        var result = _globalHotKeyManager.RestoreRegistrations(suspendedBindings);

        if (!result.IsSuccess)
        {
            _settingsViewModel.SetStatus(result.ErrorMessage ?? "无法恢复快捷键。");
        }
    }

    private void OnSettingsApplyTimerTick(object? sender, EventArgs e)
    {
        _settingsApplyTimer.Stop();
        ApplySettings();
    }

    private void ScheduleSettingsApply()
    {
        _settingsApplyTimer.Stop();
        _settingsApplyTimer.Start();
    }

    private bool ApplySettingsImmediately()
    {
        _settingsApplyTimer.Stop();
        return ApplySettings();
    }

    private bool ApplySettings()
    {
        if (_isApplyingSettings)
        {
            return true;
        }

        _isApplyingSettings = true;

        try
        {
            var settings = SettingsValidation.ValidateAndNormalize(
                _settingsViewModel.CreateSettings());
            var hotKeyBindings = HotKeyConfiguration.CreateBindings(settings);
            var hotKeyValidation = HotKeyConfiguration.Validate(hotKeyBindings);

            if (!hotKeyValidation.IsValid)
            {
                _settingsViewModel.SetStatus(hotKeyValidation.ErrorMessage ?? "快捷键配置无效。");
                return false;
            }

            Directory.CreateDirectory(settings.SaveDirectory);

            var hotKeyRegistration = _globalHotKeyManager.Apply(hotKeyBindings);

            if (!hotKeyRegistration.IsSuccess)
            {
                _settingsViewModel.SetStatus(
                    hotKeyRegistration.ErrorMessage ?? "无法注册快捷键。");
                return false;
            }

            var startupSettingChanged =
                settings.LaunchAtStartup != _savedSettings.LaunchAtStartup;

            try
            {
                if (startupSettingChanged)
                {
                    _startupRegistrationService.SetEnabled(settings.LaunchAtStartup);
                }

                _settingsStore.Save(settings);

                if (_translationApiKeyChanged)
                {
                    _translationCredentialStore.SetApiKey(
                        TranslationProviderFactory.ResolveProviderId(
                            settings.TranslationProvider),
                        _settingsViewModel.TranslationApiKey);
                    _translationApiKeyChanged = false;
                }
            }
            catch
            {
                if (startupSettingChanged)
                {
                    TryRestoreStartupRegistration();
                }

                TryRestoreHotKeys();
                throw;
            }

            _savedSettings = settings;
            _settingsViewModel.Apply(settings);
            ConfigureTaskbarVisibility(settings.ShowTaskbarIcon);
            SettingsSaved?.Invoke(this, new SettingsSavedEventArgs(settings));
            _settingsViewModel.SetStatus("设置已生效。");
            return true;
        }
        catch (Exception exception)
        {
            _settingsViewModel.SetStatus(GetSaveErrorMessage(exception));
            return false;
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void RestoreCapturedHotKey(string settingName)
    {
        switch (settingName)
        {
            case nameof(SettingsViewModel.RegionCaptureHotKey):
                _settingsViewModel.RegionCaptureHotKey = _savedSettings.RegionCaptureHotKey;
                break;
            case nameof(SettingsViewModel.ScrollCaptureHotKey):
                _settingsViewModel.ScrollCaptureHotKey = _savedSettings.ScrollCaptureHotKey;
                break;
            case nameof(SettingsViewModel.OcrHotKey):
                _settingsViewModel.OcrHotKey = _savedSettings.OcrHotKey;
                break;
            case nameof(SettingsViewModel.PinHotKey):
                _settingsViewModel.PinHotKey = _savedSettings.PinHotKey;
                break;
            case nameof(SettingsViewModel.OpenSettingsHotKey):
                _settingsViewModel.OpenSettingsHotKey = _savedSettings.OpenSettingsHotKey;
                break;
        }
    }

    private void ShowSettingsSection(int sectionIndex)
    {
        if (GeneralSettingsPanel is null ||
            HotKeySettingsPanel is null ||
            OcrSettingsPanel is null ||
            TranslationSettingsPanel is null ||
            UpdateSettingsPanel is null)
        {
            return;
        }

        GeneralSettingsPanel.Visibility = sectionIndex == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        HotKeySettingsPanel.Visibility = sectionIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        OcrSettingsPanel.Visibility = sectionIndex == 2
            ? Visibility.Visible
            : Visibility.Collapsed;
        TranslationSettingsPanel.Visibility = sectionIndex == 3
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSettingsPanel.Visibility = sectionIndex == 4
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowOcrLanguageAvailability()
    {
        var availableLanguages = OcrService.GetAvailableLanguageTags();

        if (availableLanguages.Contains(
                _settingsViewModel.OcrLanguageTag,
                StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _settingsViewModel.SetStatus(
            $"Windows 未安装 {_settingsViewModel.OcrLanguageTag} OCR 语言包。");
    }

    private void TryRestoreHotKeys()
    {
        try
        {
            var savedBindings = HotKeyConfiguration.CreateBindings(_savedSettings);
            _ = _globalHotKeyManager.Apply(savedBindings);
        }
        catch (Exception)
        {
            _settingsViewModel.SetStatus("设置保存失败，且无法恢复之前的快捷键。");
        }
    }

    private void TryRestoreStartupRegistration()
    {
        try
        {
            _startupRegistrationService.SetEnabled(_savedSettings.LaunchAtStartup);
        }
        catch (Exception)
        {
            _settingsViewModel.SetStatus("设置保存失败，且无法恢复开机启动状态。");
        }
    }

    private static string GetSaveErrorMessage(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => "没有权限写入所选位置或开机启动配置。",
            IOException => "无法写入设置，请检查保存位置是否可用。",
            ArgumentException => exception.Message,
            _ => "无法保存设置，请稍后重试。",
        };
    }
}
