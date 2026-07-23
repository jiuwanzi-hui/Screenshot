using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Presentation;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly IStartupRegistrationService _startupRegistrationService;
    private readonly GlobalHotKeyManager _globalHotKeyManager;
    private readonly ITranslationCredentialStore _translationCredentialStore;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly DispatcherTimer _settingsApplyTimer;
    private AppSettings _savedSettings;
    private IReadOnlyList<HotKeyBinding>? _suspendedHotKeyBindings;
    private bool _exitRequested;
    private bool _isApplyingSettings;
    private bool _translationApiKeyChanged;

    public MainWindow(
        AppSettings initialSettings,
        SettingsStore settingsStore,
        IStartupRegistrationService startupRegistrationService,
        GlobalHotKeyManager globalHotKeyManager,
        ITranslationCredentialStore translationCredentialStore)
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
        _settingsViewModel = new SettingsViewModel(initialSettings);
        _settingsApplyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _settingsApplyTimer.Tick += OnSettingsApplyTimerTick;

        InitializeComponent();
        DataContext = _settingsViewModel;
        UpdateThemeSelection(initialSettings.Theme);
        UpdateCloseBehaviorSelection(initialSettings.CloseBehavior);
        LoadTranslationApiKey(
            TranslationProviderFactory.ResolveProviderId(
                initialSettings.TranslationProvider));
        ShowSettingsSection(sectionIndex: 0);
        ShowOcrLanguageAvailability();
    }

    public void ConfigureTaskbarVisibility(bool showInTaskbar)
    {
        ShowInTaskbar = showInTaskbar;
    }

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    public event EventHandler? ExitRequested;

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
        base.OnClosed(e);
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

    private void OnTextSettingChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isApplyingSettings && IsLoaded)
        {
            ScheduleSettingsApply();
        }
    }

    private void OnImmediateSettingChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox checkBox)
        {
            checkBox.GetBindingExpression(
                System.Windows.Controls.CheckBox.IsCheckedProperty)?.UpdateSource();
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
            TranslationSettingsPanel is null)
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
