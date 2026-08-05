using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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

internal sealed class ReleaseHistoryItemViewModel
{
    public required ApplicationReleaseInfo Release { get; init; }

    public required string VersionText { get; init; }

    public required string Title { get; init; }

    public required string PublishedText { get; init; }

    public required string StateText { get; init; }

    public string DisplayText { get; set; } = string.Empty;
}

public partial class MainWindow : Window, IDisposable
{
    private static readonly Version MinimumAutomaticRollbackVersion = new(2, 0, 0);
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
    private readonly OfflineTranslationModelManager _offlineTranslationModelManager;
    private readonly HighQualityOcrModelManager _highQualityOcrModelManager;
    private readonly LocalLargeTranslationModelManager
        _localLargeTranslationModelManager;
    private readonly CancellationTokenSource _updateCancellationSource = new();
    private AppSettings _savedSettings;
    private IReadOnlyList<HotKeyBinding>? _suspendedHotKeyBindings;
    private bool _exitRequested;
    private bool _isApplyingSettings;
    private bool _translationApiKeyChanged;
    private ApplicationUpdateInfo? _availableUpdate;
    private ApplicationReleaseInfo? _selectedRelease;
    private OfflineTranslationModelPlan? _offlineTranslationPlan;
    private string? _onlineModelCatalogFingerprint;
    private IReadOnlyList<string> _verifiedTranslationModels = [];
    private string? _onlineModelCatalogError;
    private HotKeyCaptureBox? _activeHotKeyCaptureBox;
    private int _automaticUpdateCheckInProgress;
    private int _onlineAvailabilityCheckInProgress;
    private int _offlineModelPlanGeneration;
    private bool _isFolderDialogOpen;
    private bool? _pendingShowInTaskbar;
    private bool _disposed;

    public MainWindow(
        AppSettings initialSettings,
        SettingsStore settingsStore,
        IStartupRegistrationService startupRegistrationService,
        GlobalHotKeyManager globalHotKeyManager,
        ITranslationCredentialStore translationCredentialStore,
        HttpClient? modelCatalogHttpClient = null,
        ApplicationUpdateService? applicationUpdateService = null,
        OfflineTranslationModelManager? offlineTranslationModelManager = null,
        HighQualityOcrModelManager? highQualityOcrModelManager = null,
        LocalLargeTranslationModelManager? localLargeTranslationModelManager = null)
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
        _globalHotKeyManager.ConfigureMouseLongPress(
            initialSettings.MouseLongPressMilliseconds,
            initialSettings.MouseSideButtonsUseLongPress);
        _translationCredentialStore = translationCredentialStore;
        _modelCatalogHttpClient = modelCatalogHttpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _ownsModelCatalogHttpClient = modelCatalogHttpClient is null;
        _applicationUpdateService = applicationUpdateService ??
            new ApplicationUpdateService();
        _ownsApplicationUpdateService = applicationUpdateService is null;
        _offlineTranslationModelManager = offlineTranslationModelManager ??
            OfflineTranslationModelManager.Shared;
        _highQualityOcrModelManager = highQualityOcrModelManager ??
            HighQualityOcrModelManager.Shared;
        _localLargeTranslationModelManager = localLargeTranslationModelManager ??
            LocalLargeTranslationModelManager.Shared;
        _settingsViewModel = new SettingsViewModel(initialSettings);
        _settingsApplyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _settingsApplyTimer.Tick += OnSettingsApplyTimerTick;

        InitializeComponent();
        WindowPlacementService.Track(this, WindowPlacementKeys.Settings);
        _globalHotKeyManager.HotKeyCaptureInputReceived +=
            OnGlobalHotKeyCaptureInputReceived;
        Activated += OnSettingsWindowActivated;
        DataContext = _settingsViewModel;
        RefreshTranslationProviderDetails();
        RefreshOfflineTranslationModelStatus();
        RefreshHighQualityOcrModelStatus();
        RefreshLocalLargeModelStatus();
        UpdateThemeSelection(initialSettings.Theme);
        UpdateCloseBehaviorSelection(initialSettings.CloseBehavior);
        UpdateFloatingCaptureClickBehaviorSelection(
            initialSettings.FloatingCaptureClickBehavior);
        LoadTranslationApiKey(
            TranslationProviderFactory.ResolveProviderId(
                initialSettings.TranslationProvider));
        RefreshOnlineTranslationAvailability();
        ShowSettingsSection(sectionIndex: 0);
        ShowOcrLanguageAvailability();
        CurrentVersionText.Text =
            $"当前版本 {AppMetadata.DisplayVersion} · " +
            (AppMetadata.IsInstalled ? "安装版" : "免安装版");
    }

    public void ConfigureTaskbarVisibility(bool showInTaskbar)
    {
        if (!IsVisible)
        {
            ShowInTaskbar = showInTaskbar;
            _pendingShowInTaskbar = null;
            return;
        }

        // Changing ShowInTaskbar on a visible WPF window updates native window
        // styles and can briefly expose an unpainted white surface.
        _pendingShowInTaskbar = ShowInTaskbar == showInTaskbar
            ? null
            : showInTaskbar;
    }

    private void ApplyPendingTaskbarVisibility()
    {
        if (IsVisible || _pendingShowInTaskbar is not { } showInTaskbar)
        {
            return;
        }

        ShowInTaskbar = showInTaskbar;
        _pendingShowInTaskbar = null;
    }

    public void ApplySettingsPalette(AppTheme theme)
    {
        AppThemeManager.ApplySettingsPalette(Resources, theme);
    }

    internal void SaveVideoRecordingPreferences(
        VideoRecordingPreferences preferences)
    {
        _settingsViewModel.VideoRecordingCodec = preferences.Codec;
        _settingsViewModel.VideoRecordingFrameRate = preferences.FrameRate;
        _settingsViewModel.RecordSystemAudio = preferences.RecordSystemAudio;
        _settingsViewModel.RecordMicrophone = preferences.RecordMicrophone;
        _settingsViewModel.ShowKeyboardInputInRecording =
            preferences.ShowKeyboardInput;
        _settingsViewModel.ShowMouseInputInRecording =
            preferences.ShowMouseInput;
        ApplySettingsImmediately();
    }

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    public event EventHandler? ExitRequested;

    public event EventHandler? UpdateInstallationStarted;

    public bool IsCapturingHotKey { get; private set; }

    public void ShowFromTray()
    {
        ApplyPendingTaskbarVisibility();
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
        RefreshCachedOfflineTranslationInstallationState();
        RefreshHighQualityOcrModelStatus();
        RefreshLocalLargeModelStatus();
        _ = VerifyOnlineTranslationAvailabilityAsync();
    }

    public void ShowStatus(string message)
    {
        _settingsViewModel.SetStatus(message);
    }

    public void SetFloatingCaptureButtonEnabled(bool enabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => SetFloatingCaptureButtonEnabled(enabled));
            return;
        }

        if (_settingsViewModel.ShowFloatingCaptureButton == enabled)
        {
            return;
        }

        _settingsViewModel.ShowFloatingCaptureButton = enabled;
        ApplySettingsImmediately();
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
        _globalHotKeyManager.HotKeyCaptureInputReceived -=
            OnGlobalHotKeyCaptureInputReceived;
        _globalHotKeyManager.EndKeyboardCapture();
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
            ApplyPendingTaskbarVisibility();
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

    private async void OnBrowseSaveDirectoryClick(object sender, RoutedEventArgs e)
    {
        var selectedPath = await BrowseForFolderAsync(
            "选择截图保存位置",
            _settingsViewModel.SaveDirectory);
        if (selectedPath is not null)
        {
            _settingsViewModel.SaveDirectory = selectedPath;
            ApplySettings();
        }
    }

    private async void OnBrowseVideoSaveDirectoryClick(object sender, RoutedEventArgs e)
    {
        var selectedPath = await BrowseForFolderAsync(
            "选择视频保存位置",
            _settingsViewModel.VideoSaveDirectory);
        if (selectedPath is not null)
        {
            _settingsViewModel.VideoSaveDirectory = selectedPath;
            ApplySettings();
        }
    }

    private async Task<string?> BrowseForFolderAsync(
        string description,
        string initialDirectory)
    {
        if (_isFolderDialogOpen)
        {
            return null;
        }

        _isFolderDialogOpen = true;
        BrowseSaveDirectoryButton.IsEnabled = false;
        BrowseVideoSaveDirectoryButton.IsEnabled = false;
        try
        {
            return await RunOnStaThreadAsync(() =>
            {
                using var dialog = new WinForms.FolderBrowserDialog
                {
                    Description = description,
                    UseDescriptionForTitle = true,
                };
                if (Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }

                return dialog.ShowDialog() == WinForms.DialogResult.OK
                    ? dialog.SelectedPath
                    : null;
            });
        }
        finally
        {
            _isFolderDialogOpen = false;
            BrowseSaveDirectoryButton.IsEnabled = true;
            BrowseVideoSaveDirectoryButton.IsEnabled = true;
        }
    }

    internal static Task<T> RunOnStaThreadAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "SnapCut Folder Picker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
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
            ReleaseHistoryStatusText.Text = "正在读取历史正式版本...";
        }

        try
        {
            var updateTask = _applicationUpdateService.CheckAsync(
                AppMetadata.CurrentVersion,
                _updateCancellationSource.Token);
            var historyTask = _applicationUpdateService.GetReleaseHistoryAsync(
                _updateCancellationSource.Token);
            await Task.WhenAll(updateTask, historyTask);
            var result = await updateTask;
            var historyResult = await historyTask;
            _availableUpdate = result.AvailableUpdate;
            UpdateStatusText.Text = result.Message;
            SetUpdateNavigationState(result.AvailableUpdate?.Version);
            UpdateReleaseHistory(historyResult);
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

        await InstallReleaseAsync(_availableUpdate);
    }

    private async void OnInstallSelectedReleaseClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRelease?.InstallableUpdate is not { } update ||
            !CanAutomaticallyInstall(_selectedRelease.Version))
        {
            return;
        }

        await InstallReleaseAsync(update);
    }

    private void OnOpenSelectedReleasePageClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRelease is null)
        {
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = _selectedRelease.ReleasePage.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _settingsViewModel.SetStatus($"无法打开发布页：{exception.Message}");
        }
    }

    private async Task InstallReleaseAsync(ApplicationUpdateInfo update)
    {
        var targetVersion = ApplicationUpdateService.NormalizeVersion(update.Version);
        var currentVersion = ApplicationUpdateService.NormalizeVersion(
            AppMetadata.CurrentVersion);
        var isRollback = CompareVersions(update.Version, AppMetadata.CurrentVersion) < 0;
        var prompt = isRollback
            ? $"即将从 {currentVersion} 回退到 {targetVersion}。\n\n" +
              "旧版本可能无法识别新版本新增的设置字段，建议先备份 ScreenshotData。" +
              "现有设置、历史和截图不会主动删除。\n\n" +
              "下载完成后程序会自动关闭、覆盖并重新启动。是否继续？"
            : "更新下载完成后程序会自动关闭并覆盖更新，然后重新启动。\n\n" +
              "ScreenshotData 中的设置、历史和截图会保留。是否继续？";

        var confirmation = System.Windows.MessageBox.Show(
            this,
            prompt,
            isRollback ? $"回退到 SnapCut {targetVersion}" : "更新 SnapCut",
            MessageBoxButton.YesNo,
            isRollback ? MessageBoxImage.Warning : MessageBoxImage.Information,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        CheckForUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        SelectedReleaseActionButton.IsEnabled = false;
        ReleaseHistorySelector.IsEnabled = false;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        var asset = AppMetadata.IsInstalled
            ? update.Installer
            : update.Portable;
        var progress = new Progress<double>(value =>
        {
            UpdateProgressBar.Value = value * 100;
            UpdateStatusText.Text = isRollback
                ? $"正在下载回退版本 {targetVersion}… {value:P0}"
                : $"正在下载更新… {value:P0}";
        });

        try
        {
            var packagePath = await _applicationUpdateService.DownloadAsync(
                asset,
                progress,
                _updateCancellationSource.Token);
            UpdateStatusText.Text = isRollback
                ? "校验完成，正在启动版本回退..."
                : "校验完成，正在启动覆盖更新...";
            ApplicationUpdateLauncher.Launch(update, packagePath);
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
        SelectedReleaseActionButton.IsEnabled =
            _selectedRelease?.InstallableUpdate is not null &&
            CanAutomaticallyInstall(_selectedRelease.Version) &&
            CompareVersions(_selectedRelease.Version, AppMetadata.CurrentVersion) != 0;
        ReleaseHistorySelector.IsEnabled = true;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
    }

    internal void UpdateReleaseHistory(ApplicationReleaseHistoryResult result)
    {
        ReleaseHistoryStatusText.Text = result.Message;
        if (!result.IsSuccess || result.Releases.Count == 0)
        {
            if (ReleaseHistorySelector.ItemsSource is not null)
            {
                ReleaseHistoryStatusText.Text += " 已保留上次成功读取的版本列表。";
                return;
            }

            ReleaseHistorySelector.ItemsSource = null;
            SelectedReleaseDetailsPanel.Visibility = Visibility.Collapsed;
            _selectedRelease = null;
            return;
        }

        var items = result.Releases
            .Select(release => new ReleaseHistoryItemViewModel
            {
                Release = release,
                VersionText = $"v{ApplicationUpdateService.NormalizeVersion(release.Version)}",
                Title = release.Title,
                PublishedText = release.PublishedAt == DateTimeOffset.MinValue
                    ? "发布时间未知"
                    : release.PublishedAt.ToLocalTime().ToString(
                        "yyyy年M月d日 HH:mm",
                        CultureInfo.GetCultureInfo("zh-CN")),
                StateText = GetReleaseStateText(release),
            })
            .ToArray();
        foreach (var item in items)
        {
            item.DisplayText =
                $"{item.VersionText}　·　{item.PublishedText}　·　{item.StateText}";
        }

        ReleaseHistorySelector.ItemsSource = items;
        ReleaseHistorySelector.SelectedItem = items.FirstOrDefault(item =>
            CompareVersions(item.Release.Version, AppMetadata.CurrentVersion) == 0) ??
            items[0];
    }

    private void OnReleaseHistorySelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ReleaseHistorySelector.SelectedItem is not ReleaseHistoryItemViewModel item)
        {
            _selectedRelease = null;
            SelectedReleaseDetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var release = item.Release;
        _selectedRelease = release;
        SelectedReleaseDetailsPanel.Visibility = Visibility.Visible;
        SelectedReleaseTitleText.Text =
            $"{item.VersionText} · {release.Title}";
        SelectedReleaseDateText.Text = $"发布时间：{item.PublishedText}";
        SelectedReleaseStateText.Text = item.StateText;
        SelectedReleaseNotesText.Text = release.ReleaseNotes;

        var comparison = CompareVersions(release.Version, AppMetadata.CurrentVersion);
        SelectedReleaseActionButton.Visibility = comparison == 0 ||
            release.InstallableUpdate is null ||
            !CanAutomaticallyInstall(release.Version)
            ? Visibility.Collapsed
            : Visibility.Visible;
        SelectedReleaseActionButton.Content = comparison < 0
            ? $"回退到 {ApplicationUpdateService.NormalizeVersion(release.Version)}"
            : $"更新到 {ApplicationUpdateService.NormalizeVersion(release.Version)}";
        SelectedReleaseActionButton.IsEnabled =
            SelectedReleaseActionButton.Visibility == Visibility.Visible;
        SelectedReleasePackageStatusText.Text = GetReleasePackageStatus(release);
    }

    private static string GetReleaseStateText(ApplicationReleaseInfo release)
    {
        var comparison = CompareVersions(release.Version, AppMetadata.CurrentVersion);
        if (comparison == 0)
        {
            return "当前版本";
        }

        if (release.InstallableUpdate is null)
        {
            return "仅查看";
        }

        if (!CanAutomaticallyInstall(release.Version))
        {
            return "需手动安装";
        }

        return comparison < 0 ? "可回退" : "可更新";
    }

    private static string GetReleasePackageStatus(ApplicationReleaseInfo release)
    {
        if (!string.IsNullOrWhiteSpace(release.PackageWarning))
        {
            return release.PackageWarning;
        }

        var comparison = CompareVersions(release.Version, AppMetadata.CurrentVersion);
        if (comparison == 0)
        {
            return "这是当前正在运行的版本。";
        }

        if (!CanAutomaticallyInstall(release.Version))
        {
            return "2.0.0 以前的程序文件名和更新器结构不同，为避免留下冲突文件，只支持从发布页手动安装。";
        }

        return comparison < 0
            ? "可使用经过大小和 SHA-256 校验的正式包回退；操作前建议备份 ScreenshotData。"
            : "可使用经过大小和 SHA-256 校验的正式包更新。";
    }

    private static bool CanAutomaticallyInstall(Version version) =>
        CompareVersions(version, MinimumAutomaticRollbackVersion) >= 0;

    private static int CompareVersions(Version left, Version right)
    {
        static Version Comparable(Version version) => new(
            version.Major,
            version.Minor,
            Math.Max(0, version.Build));

        return Comparable(left).CompareTo(Comparable(right));
    }

    private void OnTranslationApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        _settingsViewModel.TranslationApiKey = TranslationApiKeyBox.Password;
        RefreshOnlineTranslationAvailability();
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

        _isApplyingSettings = true;
        try
        {
            TranslationApiKeyBox.Password = apiKey ?? string.Empty;
            _settingsViewModel.TranslationApiKey = apiKey ?? string.Empty;
            _translationApiKeyChanged = false;
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void OnTranslationProviderSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isApplyingSettings ||
            !ReferenceEquals(sender, TranslationProviderComboBox))
        {
            return;
        }

        TranslationProviderComboBox.GetBindingExpression(
            System.Windows.Controls.ComboBox.SelectedValueProperty)?.UpdateSource();

        // Persist a key typed for the previous provider before switching the
        // credential slot, so it can never be stored under the new provider.
        if (_translationApiKeyChanged)
        {
            _translationCredentialStore.SetApiKey(
                TranslationProviderFactory.ResolveProviderId(
                    _savedSettings.TranslationProvider),
                _settingsViewModel.TranslationApiKey);
            _translationApiKeyChanged = false;
        }

        var previousProvider = TranslationProviderFactory.GetDefinition(
            _savedSettings.TranslationProvider);
        var provider = _settingsViewModel.SelectedTranslationProvider;
        _isApplyingSettings = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(provider.OfficialEndpoint))
            {
                _settingsViewModel.TranslationEndpoint = provider.OfficialEndpoint;
            }

            var currentModel = _settingsViewModel.TranslationModel.Trim();
            if (!string.IsNullOrWhiteSpace(provider.DefaultModel) &&
                (string.IsNullOrWhiteSpace(currentModel) ||
                 currentModel.Equals(
                     previousProvider.DefaultModel,
                     StringComparison.OrdinalIgnoreCase)))
            {
                _settingsViewModel.TranslationModel = provider.DefaultModel;
            }

            TranslationProviderOfficialSiteText.Text = provider.OfficialSite;
            LoadTranslationApiKey(provider.Id);
        }
        finally
        {
            _isApplyingSettings = false;
        }

        ApplySettingsImmediately();
        RefreshOnlineTranslationAvailability();
    }

    private void RefreshTranslationProviderDetails()
    {
        var provider = _settingsViewModel.SelectedTranslationProvider;
        TranslationProviderOfficialSiteText.Text = provider.OfficialSite;
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

        _settingsViewModel.UpdateTranslationProviderAvailability(
            TranslationProviderKind.Online,
            isAvailable: false,
            "正在验证服务并获取模型列表");
        button.IsEnabled = false;
        button.Content = "获取中...";
        _settingsViewModel.SetStatus("正在从翻译服务获取模型列表...");
        try
        {
            var requestFingerprint = CreateOnlineConfigurationFingerprint();
            var result = await TranslationModelCatalogService.FetchAsync(
                _settingsViewModel.TranslationEndpoint,
                TranslationApiKeyBox.Password,
                _modelCatalogHttpClient);
            if (!string.Equals(
                    requestFingerprint,
                    CreateOnlineConfigurationFingerprint(),
                    StringComparison.Ordinal))
            {
                RefreshOnlineTranslationAvailability();
                return;
            }

            _onlineModelCatalogFingerprint = requestFingerprint;
            if (!result.IsSuccess)
            {
                _verifiedTranslationModels = [];
                _onlineModelCatalogError =
                    result.ErrorMessage ?? "获取模型失败。";
                RefreshOnlineTranslationAvailability();
                _settingsViewModel.SetStatus(
                    result.ErrorMessage ?? "获取模型失败。");
                return;
            }

            _onlineModelCatalogError = null;
            _verifiedTranslationModels = result.Models;
            SetTranslationModelsPreservingSelection(result.Models);
            if (result.Models.Count == 1)
            {
                _settingsViewModel.TranslationModel = result.Models[0];
                ApplySettingsImmediately();
            }

            _settingsViewModel.SetStatus(
                result.Models.Count == 1
                    ? $"已获取并选择模型：{result.Models[0]}。"
                    : $"已获取 {result.Models.Count} 个模型，请在下拉框中选择。" );
            RefreshOnlineTranslationAvailability();
        }
        catch
        {
            _onlineModelCatalogFingerprint =
                CreateOnlineConfigurationFingerprint();
            _verifiedTranslationModels = [];
            _onlineModelCatalogError =
                "获取模型失败，请检查服务地址和网络连接。";
            RefreshOnlineTranslationAvailability();
            _settingsViewModel.SetStatus("获取模型失败，请检查服务地址和网络连接。");
        }
        finally
        {
            button.Content = "获取模型";
            button.IsEnabled = true;
        }
    }

    private void OnMoveTranslationProviderUpClick(
        object sender,
        RoutedEventArgs e)
    {
        MoveTranslationProvider(sender, -1);
    }

    private void OnMoveTranslationProviderDownClick(
        object sender,
        RoutedEventArgs e)
    {
        MoveTranslationProvider(sender, 1);
    }

    private void MoveTranslationProvider(object sender, int offset)
    {
        if (sender is not System.Windows.Controls.Button
            {
                Tag: TranslationProviderKind provider,
            } || !_settingsViewModel.MoveTranslationProvider(provider, offset))
        {
            return;
        }

        ApplySettingsImmediately();
        var firstProvider = _settingsViewModel.TranslationPriorityItems[0].Label;
        _settingsViewModel.SetStatus(
            $"翻译优先顺序已更新，将先尝试{firstProvider}。");
    }

    private async void OnDownloadOfflineModelClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        var plan = _offlineTranslationPlan;
        if (plan is null)
        {
            RefreshOfflineTranslationModelStatus();
            _settingsViewModel.SetStatus(
                "正在计算当前目标语言包所需的离线模型信息，请稍候。");
            return;
        }

        var status = _offlineTranslationModelManager.GetStatus(plan);
        if (status.IsInstalled)
        {
            RefreshOfflineTranslationModelStatus();
            _settingsViewModel.SetStatus(
                $"“{plan.DisplayName}”离线语言包已经安装。");
            return;
        }

        var availableSpace = status.AvailableSpace > 0
            ? FormatFileSize(status.AvailableSpace)
            : "无法读取";
        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"将下载“{plan.DisplayName}”所需离线模型。\n" +
            "源语言将在翻译时完全离线自动检测；非英语语言之间将通过 English 中转。\n" +
            "\n" +
            $"下载流量：{FormatFileSize(status.DownloadSize)}\n" +
            $"新增安装占用：约 {FormatFileSize(status.InstalledSize)}\n" +
            $"磁盘可用：{availableSpace}\n\n" +
            $"安装位置：\n{status.InstallationDirectory}\n\n" +
            "模型来自 Mozilla Firefox Translations。是否继续下载？",
            "下载离线翻译模型",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        button.IsEnabled = false;
        OfflineModelDownloadProgressBar.Value = 0;
        OfflineModelDownloadProgressBar.Visibility = Visibility.Visible;
        OfflineModelDownloadProgressText.Visibility = Visibility.Visible;
        OfflineModelDownloadProgressText.Text = "正在连接模型服务器...";
        _settingsViewModel.SetStatus("正在下载并校验离线翻译模型...");
        var progress = new Progress<OfflineTranslationDownloadProgress>(value =>
        {
            var percent = value.TotalBytes <= 0
                ? 0
                : Math.Clamp(
                    (value.DownloadedBytes * 100d) / value.TotalBytes,
                    0,
                    100);
            OfflineModelDownloadProgressBar.Value = percent;
            OfflineModelDownloadProgressText.Text =
                $"{percent:0}% · {FormatFileSize(value.DownloadedBytes)} / " +
                $"{FormatFileSize(value.TotalBytes)} · {value.CurrentFileName}";
        });

        try
        {
            var result = await _offlineTranslationModelManager.InstallAsync(
                plan,
                progress,
                _updateCancellationSource.Token);
            RefreshOfflineTranslationModelStatus();
            _settingsViewModel.SetStatus(result.IsSuccess
                ? "离线翻译模型安装完成，现在可以断网翻译。"
                : result.ErrorMessage ?? "离线翻译模型安装失败。");
        }
        finally
        {
            button.IsEnabled = true;
            OfflineModelDownloadProgressBar.Visibility = Visibility.Collapsed;
            OfflineModelDownloadProgressText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOpenOfflineModelDirectoryClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(
                _offlineTranslationModelManager.InstallationDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _offlineTranslationModelManager.InstallationDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception)
        {
            _settingsViewModel.SetStatus("无法打开离线模型目录。");
        }
    }

    private async void OnDownloadHighQualityOcrModelClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        var status = _highQualityOcrModelManager.GetStatus();
        if (status.IsInstalled)
        {
            RefreshHighQualityOcrModelStatus();
            _settingsViewModel.SetStatus("PP-OCRv6 高质量识别模型已经安装。");
            return;
        }

        var availableSpace = status.AvailableSpace > 0
            ? FormatFileSize(status.AvailableSpace)
            : "无法读取";
        var confirmation = System.Windows.MessageBox.Show(
            this,
            "将下载 PP-OCRv6 Small 多语言识别模型。\n\n" +
            $"下载流量：{FormatFileSize(status.DownloadSize)}\n" +
            $"安装占用：约 {FormatFileSize(status.InstalledSize)}\n" +
            $"磁盘可用：{availableSpace}\n\n" +
            $"安装位置：\n{status.InstallationDirectory}\n\n" +
            "模型来自 PaddleOCR / RapidOCR（Apache-2.0），完全本机运行。是否继续？",
            "下载高质量识别模型",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        button.IsEnabled = false;
        HighQualityOcrDownloadProgressBar.Value = 0;
        HighQualityOcrDownloadProgressBar.Visibility = Visibility.Visible;
        HighQualityOcrDownloadProgressText.Visibility = Visibility.Visible;
        var progress = new Progress<ModelDownloadProgress>(value =>
        {
            var percent = value.TotalBytes <= 0
                ? 0
                : Math.Clamp(
                    value.DownloadedBytes * 100d / value.TotalBytes,
                    0,
                    100);
            HighQualityOcrDownloadProgressBar.Value = percent;
            HighQualityOcrDownloadProgressText.Text =
                $"{percent:0}% · {FormatFileSize(value.DownloadedBytes)} / " +
                $"{FormatFileSize(value.TotalBytes)} · {value.CurrentFileName}";
        });
        try
        {
            var result = await _highQualityOcrModelManager.InstallAsync(
                progress,
                _updateCancellationSource.Token);
            RefreshHighQualityOcrModelStatus();
            _settingsViewModel.SetStatus(result.IsSuccess
                ? "高质量识别模型安装完成，已可在内容识别中选择。"
                : result.ErrorMessage ?? "高质量识别模型安装失败。");
        }
        finally
        {
            button.IsEnabled = true;
            HighQualityOcrDownloadProgressBar.Visibility = Visibility.Collapsed;
            HighQualityOcrDownloadProgressText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOpenHighQualityOcrModelDirectoryClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(
                _highQualityOcrModelManager.InstallationDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _highQualityOcrModelManager.InstallationDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception)
        {
            _settingsViewModel.SetStatus("无法打开高质量识别模型目录。");
        }
    }

    private void RefreshHighQualityOcrModelStatus()
    {
        if (HighQualityOcrModelStatusText is null ||
            HighQualityOcrModelPathText is null ||
            DownloadHighQualityOcrModelButton is null)
        {
            return;
        }

        var status = _highQualityOcrModelManager.GetStatus();
        HighQualityOcrModelStatusText.Text = status.IsInstalled
            ? $"已安装 · 占用约 {FormatFileSize(status.InstalledSize)}"
            : $"未安装 · 需下载 {FormatFileSize(status.DownloadSize)}";
        HighQualityOcrModelPathText.Text =
            $"安装目录：{status.InstallationDirectory}";
        DownloadHighQualityOcrModelButton.Content = status.IsInstalled
            ? "模型已安装"
            : "下载识别模型";
    }

    private async void OnDownloadLocalLargeModelClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        var status = _localLargeTranslationModelManager.GetStatus();
        if (status.IsInstalled)
        {
            RefreshLocalLargeModelStatus();
            _settingsViewModel.SetStatus("Qwen 本机翻译大模型已经安装。");
            return;
        }

        var availableSpace = status.AvailableSpace > 0
            ? FormatFileSize(status.AvailableSpace)
            : "无法读取";
        var confirmation = System.Windows.MessageBox.Show(
            this,
            "将下载 Qwen2.5 1.5B 4-bit 本机翻译大模型及 CPU 推理程序。\n\n" +
            $"本次需下载：{FormatFileSize(status.DownloadSize)}\n" +
            $"安装占用：约 {FormatFileSize(status.InstalledSize)}\n" +
            $"磁盘可用：{availableSpace}\n" +
            "运行建议：至少 4 GB 可用内存，纯 CPU 翻译会比在线模型慢。\n\n" +
            $"安装位置：\n{status.InstallationDirectory}\n\n" +
            "模型为 Qwen2.5（Apache-2.0），推理程序为 llama.cpp（MIT）。是否继续？",
            "下载本机翻译大模型",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        button.IsEnabled = false;
        LocalLargeModelDownloadProgressBar.Value = 0;
        LocalLargeModelDownloadProgressBar.Visibility = Visibility.Visible;
        LocalLargeModelDownloadProgressText.Visibility = Visibility.Visible;
        var progress = new Progress<ModelDownloadProgress>(value =>
        {
            var percent = value.TotalBytes <= 0
                ? 0
                : Math.Clamp(
                    value.DownloadedBytes * 100d / value.TotalBytes,
                    0,
                    100);
            LocalLargeModelDownloadProgressBar.Value = percent;
            LocalLargeModelDownloadProgressText.Text =
                $"{percent:0.0}% · {FormatFileSize(value.DownloadedBytes)} / " +
                $"{FormatFileSize(value.TotalBytes)} · {value.CurrentFileName}";
        });
        try
        {
            var result = await _localLargeTranslationModelManager.InstallAsync(
                progress,
                _updateCancellationSource.Token);
            RefreshLocalLargeModelStatus();
            _settingsViewModel.SetStatus(result.IsSuccess
                ? "Qwen 本机翻译大模型安装完成。"
                : result.ErrorMessage ?? "本机翻译大模型安装失败。");
        }
        finally
        {
            button.IsEnabled = true;
            LocalLargeModelDownloadProgressBar.Visibility = Visibility.Collapsed;
            LocalLargeModelDownloadProgressText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOpenLocalLargeModelDirectoryClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(
                _localLargeTranslationModelManager.InstallationDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName =
                    _localLargeTranslationModelManager.InstallationDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception)
        {
            _settingsViewModel.SetStatus("无法打开本机翻译大模型目录。");
        }
    }

    private void RefreshLocalLargeModelStatus()
    {
        if (LocalLargeModelStatusText is null ||
            LocalLargeModelPathText is null ||
            DownloadLocalLargeModelButton is null)
        {
            return;
        }

        var status = _localLargeTranslationModelManager.GetStatus();
        LocalLargeModelStatusText.Text = status.IsInstalled
            ? $"已安装 · 占用约 {FormatFileSize(status.InstalledSize)}"
            : $"未安装 · 本次需下载 {FormatFileSize(status.DownloadSize)}";
        LocalLargeModelPathText.Text =
            $"安装目录：{status.InstallationDirectory}";
        DownloadLocalLargeModelButton.Content = status.IsInstalled
            ? "模型已安装"
            : "下载翻译大模型";

        if (_settingsViewModel.OfflineTranslationEngine ==
            OfflineTranslationEngine.QwenLargeModel)
        {
            _settingsViewModel.UpdateTranslationProviderAvailability(
                TranslationProviderKind.Offline,
                status.IsInstalled,
                status.IsInstalled
                    ? "Qwen 本机翻译大模型已安装"
                    : "Qwen 本机翻译大模型尚未下载");
        }
    }

    private async void RefreshOfflineTranslationModelStatus()
    {
        if (OfflineModelStatusText is null || OfflineModelPathText is null ||
            DownloadOfflineModelButton is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _offlineModelPlanGeneration);
        _offlineTranslationPlan = null;
        UpdateMozillaTranslationAvailability(
            isAvailable: false,
            "正在检查当前目标语言所需的离线模型");
        DownloadOfflineModelButton.IsEnabled = false;
        DownloadOfflineModelButton.Content = "正在计算...";
        OfflineModelStatusText.Text = "正在读取 Mozilla 模型清单并计算所需空间...";
        OfflineModelPathText.Text =
            $"安装目录：{_offlineTranslationModelManager.InstallationDirectory}";
        if (OfflineModelRouteText is not null)
        {
            OfflineModelRouteText.Text =
                "源语言：自动检测（与文字识别语言设置无关） · " +
                $"目标语言：{TranslationLanguageCatalog.GetDisplayName(_settingsViewModel.TranslationTargetLanguage)} · " +
                $"精度：{OfflineTranslationModelCatalog.GetQualityDisplayName(_settingsViewModel.OfflineTranslationQuality)}";
        }

        OfflineTranslationModelPlanResult result;
        try
        {
            result = await _offlineTranslationModelManager.PrepareTargetPlanAsync(
                _settingsViewModel.TranslationTargetLanguage,
                _updateCancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (generation != Volatile.Read(ref _offlineModelPlanGeneration) || _disposed)
        {
            return;
        }

        if (!result.IsSuccess || result.Plan is null)
        {
            var error = result.ErrorMessage ??
                "当前目标语言暂不支持离线翻译。";
            OfflineModelStatusText.Text = error;
            UpdateMozillaTranslationAvailability(
                isAvailable: false,
                error);
            DownloadOfflineModelButton.Content = "暂不支持";
            DownloadOfflineModelButton.IsEnabled = false;
            return;
        }

        var plan = result.Plan;
        _offlineTranslationPlan = plan;
        RefreshCachedOfflineTranslationInstallationState();
    }

    private void RefreshCachedOfflineTranslationInstallationState()
    {
        if (_offlineTranslationPlan is not { } plan ||
            OfflineModelStatusText is null ||
            DownloadOfflineModelButton is null)
        {
            return;
        }

        var status = _offlineTranslationModelManager.GetStatus(plan);
        UpdateMozillaTranslationAvailability(
            status.IsInstalled,
            status.IsInstalled
                ? $"当前目标语言所需的离线模型已安装（{plan.DisplayName}）"
                : "当前目标语言所需的离线模型尚未下载");
        OfflineModelStatusText.Text = status.IsInstalled
            ? $"已安装 · 目标语言包占用约 {FormatFileSize(plan.InstalledSize)}"
            : $"未安装 · 本次需下载 {FormatFileSize(status.DownloadSize)}，" +
              $"新增占用约 {FormatFileSize(status.InstalledSize)}";
        DownloadOfflineModelButton.Content = status.IsInstalled
            ? "模型已安装"
            : "下载目标语言包";
        DownloadOfflineModelButton.IsEnabled = true;
    }

    private void UpdateMozillaTranslationAvailability(
        bool isAvailable,
        string reason)
    {
        if (_settingsViewModel.OfflineTranslationEngine !=
            OfflineTranslationEngine.Mozilla)
        {
            return;
        }

        _settingsViewModel.UpdateTranslationProviderAvailability(
            TranslationProviderKind.Offline,
            isAvailable,
            reason);
    }

    private static string FormatFileSize(long bytes)
    {
        const double mebibyte = 1024d * 1024d;
        const double gibibyte = mebibyte * 1024d;
        return bytes >= gibibyte
            ? $"{bytes / gibibyte:0.0} GB"
            : $"{bytes / mebibyte:0.0} MB";
    }

    private void OnTextSettingChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isApplyingSettings && IsLoaded)
        {
            if (ReferenceEquals(sender, TranslationEndpointTextBox))
            {
                RefreshOnlineTranslationAvailability();
            }

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

        var applied = ApplySettingsImmediately();
        if (applied &&
            ReferenceEquals(sender, ShowTaskbarIconCheckBox) &&
            _pendingShowInTaskbar.HasValue)
        {
            _settingsViewModel.SetStatus(
                "任务栏图标将在设置窗口下次打开时更新。");
        }
        if (ReferenceEquals(sender, TranslationModelComboBox))
        {
            RefreshOnlineTranslationAvailability();
        }
        if (ReferenceEquals(sender, TranslationTargetLanguageComboBox))
        {
            RefreshOfflineTranslationModelStatus();
        }
        else if (ReferenceEquals(sender, OfflineTranslationQualityComboBox))
        {
            RefreshCachedOfflineTranslationInstallationState();
            _settingsViewModel.SetStatus(
                $"离线翻译精度已切换为{OfflineTranslationModelCatalog.GetQualityDisplayName(_settingsViewModel.OfflineTranslationQuality)}。");
        }
        else if (ReferenceEquals(sender, OcrEngineComboBox))
        {
            RefreshHighQualityOcrModelStatus();
            ShowOcrLanguageAvailability();
        }
        else if (ReferenceEquals(sender, OfflineTranslationEngineComboBox))
        {
            RefreshCachedOfflineTranslationInstallationState();
            RefreshLocalLargeModelStatus();
            _settingsViewModel.SetStatus(
                _settingsViewModel.OfflineTranslationEngine ==
                OfflineTranslationEngine.QwenLargeModel
                    ? "本机离线翻译已切换为 Qwen 大模型。"
                    : "本机离线翻译已切换为 Mozilla 轻量模型。");
        }
    }

    private void OnMouseLongPressDurationChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _isApplyingSettings ||
            sender is not System.Windows.Controls.Slider slider)
        {
            return;
        }

        slider.GetBindingExpression(
            System.Windows.Controls.Slider.ValueProperty)?.UpdateSource();
        ScheduleSettingsApply();
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
        theme = AppSettings.NormalizeTheme(theme);
        AuroraMistThemeOption.IsChecked = theme == AppTheme.AuroraMist;
        CoralSkyThemeOption.IsChecked = theme == AppTheme.CoralSky;
        GinkgoPaperThemeOption.IsChecked = theme == AppTheme.GinkgoPaper;
        ForestNightThemeOption.IsChecked = theme == AppTheme.ForestNight;
        ObsidianGoldThemeOption.IsChecked = theme == AppTheme.ObsidianGold;
        NeonDeepThemeOption.IsChecked = theme == AppTheme.NeonDeep;
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

    private void OnFloatingCaptureClickBehaviorSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isApplyingSettings ||
            sender is not System.Windows.Controls.ComboBox
            {
                SelectedItem: ComboBoxItem
                {
                    Tag: FloatingCaptureClickBehavior behavior,
                },
            })
        {
            return;
        }

        _settingsViewModel.FloatingCaptureClickBehavior = behavior;
        ApplySettingsImmediately();
    }

    private void UpdateFloatingCaptureClickBehaviorSelection(
        FloatingCaptureClickBehavior behavior)
    {
        FloatingCaptureClickBehaviorComboBox.SelectedValue = behavior;
    }

    private void OnSettingEditorLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        ApplySettingsImmediately();
        RefreshOnlineTranslationAvailability();
    }

    private void RefreshOnlineTranslationAvailability()
    {
        if (TranslationApiKeyBox is null)
        {
            return;
        }

        var endpoint = _settingsViewModel.TranslationEndpoint.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            SetOnlineTranslationUnavailable("请填写 API 接口地址");
            return;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            !endpointUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            SetOnlineTranslationUnavailable("API 接口必须使用有效的 HTTPS 地址");
            return;
        }

        if (string.IsNullOrWhiteSpace(TranslationApiKeyBox.Password))
        {
            SetOnlineTranslationUnavailable("请填写 API Key");
            return;
        }

        var model = _settingsViewModel.TranslationModel.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            SetOnlineTranslationUnavailable("请选择或填写翻译模型");
            return;
        }

        var fingerprint = CreateOnlineConfigurationFingerprint();
        if (!string.Equals(
                fingerprint,
                _onlineModelCatalogFingerprint,
                StringComparison.Ordinal))
        {
            SetOnlineTranslationUnavailable("尚未验证模型，请点击“获取模型”");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_onlineModelCatalogError))
        {
            SetOnlineTranslationUnavailable(_onlineModelCatalogError);
            return;
        }

        if (!_verifiedTranslationModels.Contains(
                model,
                StringComparer.OrdinalIgnoreCase))
        {
            SetOnlineTranslationUnavailable(
                "当前模型不在接口返回的可用模型中，请重新选择或获取模型");
            return;
        }

        _settingsViewModel.UpdateTranslationProviderAvailability(
            TranslationProviderKind.Online,
            isAvailable: true,
            $"已验证，模型 {model} 可用");
    }

    private async Task VerifyOnlineTranslationAvailabilityAsync()
    {
        if (Interlocked.Exchange(
                ref _onlineAvailabilityCheckInProgress,
                1) != 0)
        {
            return;
        }

        try
        {
            var endpoint = _settingsViewModel.TranslationEndpoint.Trim();
            var apiKey = TranslationApiKeyBox.Password;
            var model = _settingsViewModel.TranslationModel.Trim();
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
                !endpointUri.Scheme.Equals(
                    "https",
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(model))
            {
                RefreshOnlineTranslationAvailability();
                return;
            }

            var requestFingerprint = CreateOnlineConfigurationFingerprint();
            _settingsViewModel.UpdateTranslationProviderAvailability(
                TranslationProviderKind.Online,
                isAvailable: false,
                "正在自动验证在线模型");
            var result = await TranslationModelCatalogService.FetchAsync(
                endpoint,
                apiKey,
                _modelCatalogHttpClient,
                _updateCancellationSource.Token);
            if (_disposed || !string.Equals(
                    requestFingerprint,
                    CreateOnlineConfigurationFingerprint(),
                    StringComparison.Ordinal))
            {
                return;
            }

            _onlineModelCatalogFingerprint = requestFingerprint;
            _verifiedTranslationModels = result.IsSuccess
                ? result.Models
                : [];
            _onlineModelCatalogError = result.IsSuccess
                ? null
                : result.ErrorMessage ?? "无法获取在线模型列表";
            RefreshOnlineTranslationAvailability();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            Interlocked.Exchange(
                ref _onlineAvailabilityCheckInProgress,
                0);
        }
    }

    private void SetOnlineTranslationUnavailable(string reason)
    {
        _settingsViewModel.UpdateTranslationProviderAvailability(
            TranslationProviderKind.Online,
            isAvailable: false,
            reason);
    }

    private void SetTranslationModelsPreservingSelection(
        IReadOnlyList<string> models)
    {
        var selectedModel = _settingsViewModel.TranslationModel.Trim();
        _isApplyingSettings = true;
        try
        {
            _settingsViewModel.SetTranslationModels(models);
            if (!string.IsNullOrWhiteSpace(selectedModel))
            {
                _settingsViewModel.TranslationModel = selectedModel;
                TranslationModelComboBox.Text = selectedModel;
            }
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void OnTranslationSettingPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox
            {
                IsDropDownOpen: true,
            } || TranslationSettingsPanel is null)
        {
            return;
        }

        e.Handled = true;
        TranslationSettingsPanel.ScrollToVerticalOffset(
            TranslationSettingsPanel.VerticalOffset - (e.Delta / 3d));
    }

    private string CreateOnlineConfigurationFingerprint()
    {
        var value = string.Join(
            "\n",
            TranslationProviderFactory.ResolveProviderId(
                _settingsViewModel.TranslationProvider),
            _settingsViewModel.TranslationEndpoint.Trim(),
            TranslationApiKeyBox.Password.Trim());
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
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
            case nameof(SettingsViewModel.VideoRecordingHotKey):
                _settingsViewModel.VideoRecordingHotKey = e.Gesture;
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

        if (!ApplySettingsImmediately(settingName))
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
        if (IsCapturingHotKey || sender is not HotKeyCaptureBox captureBox)
        {
            return;
        }

        _suspendedHotKeyBindings = _globalHotKeyManager.SuspendRegistrations();
        _activeHotKeyCaptureBox = captureBox;
        _globalHotKeyManager.BeginKeyboardCapture();
        IsCapturingHotKey = true;
        _settingsViewModel.SetStatus(
            "请按下键盘或鼠标组合；单独按左、中、右键会录为长按。" +
            "录入期间会屏蔽其他应用的输入。" +
            "按 Backspace 或 Delete 清空，按 Esc 取消。");
    }

    private void OnGlobalHotKeyCaptureInputReceived(
        object? sender,
        HotKeyCaptureInputEventArgs e)
    {
        if (!IsCapturingHotKey || _activeHotKeyCaptureBox is null)
        {
            return;
        }

        _activeHotKeyCaptureBox.ProcessCapturedGesture(e.Gesture);
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
        _globalHotKeyManager.EndKeyboardCapture();
        _activeHotKeyCaptureBox = null;

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

    private bool ApplySettingsImmediately(string? editedHotKeySettingName = null)
    {
        _settingsApplyTimer.Stop();
        return ApplySettings(editedHotKeySettingName);
    }

    private bool ApplySettings(string? editedHotKeySettingName = null)
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
            Directory.CreateDirectory(settings.VideoSaveDirectory);

            var hotKeyRegistration =
                _globalHotKeyManager.ApplyAvailable(hotKeyBindings);
            var hotKeyWarning = hotKeyRegistration.IsSuccess
                ? null
                : hotKeyRegistration.ErrorMessage ?? "部分快捷键无法注册。";

            if (GetHotKeyAction(editedHotKeySettingName) is { } editedAction &&
                hotKeyBindings.FirstOrDefault(
                    binding => binding.Action == editedAction) is { } editedBinding &&
                !_globalHotKeyManager.RegisteredBindings.Contains(editedBinding))
            {
                _settingsViewModel.SetStatus(
                    hotKeyRegistration.ErrorMessage ??
                    $"快捷键 {editedBinding.Gesture} 无法注册，请改用其他组合键。");
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
            _globalHotKeyManager.ConfigureMouseLongPress(
                settings.MouseLongPressMilliseconds,
                settings.MouseSideButtonsUseLongPress);
            _settingsViewModel.Apply(settings);
            ConfigureTaskbarVisibility(settings.ShowTaskbarIcon);
            SettingsSaved?.Invoke(this, new SettingsSavedEventArgs(settings));
            _settingsViewModel.SetStatus(hotKeyWarning is null
                ? "设置已生效。"
                : $"其他设置已生效；{hotKeyWarning}");
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
            case nameof(SettingsViewModel.VideoRecordingHotKey):
                _settingsViewModel.VideoRecordingHotKey = _savedSettings.VideoRecordingHotKey;
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

    private static HotKeyAction? GetHotKeyAction(string? settingName)
    {
        return settingName switch
        {
            nameof(SettingsViewModel.RegionCaptureHotKey) =>
                HotKeyAction.RegionCapture,
            nameof(SettingsViewModel.VideoRecordingHotKey) =>
                HotKeyAction.VideoRecording,
            nameof(SettingsViewModel.ScrollCaptureHotKey) =>
                HotKeyAction.ScrollCapture,
            nameof(SettingsViewModel.OcrHotKey) =>
                HotKeyAction.RecognizeText,
            nameof(SettingsViewModel.PinHotKey) =>
                HotKeyAction.PinImage,
            nameof(SettingsViewModel.OpenSettingsHotKey) =>
                HotKeyAction.OpenSettings,
            _ => null,
        };
    }

    private void ShowSettingsSection(int sectionIndex)
    {
        if (GeneralSettingsPanel is null ||
            HotKeySettingsPanel is null ||
            OcrSettingsPanel is null ||
            TranslationSettingsPanel is null ||
            UpdateSettingsPanel is null ||
            DonateSettingsPanel is null)
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
        DonateSettingsPanel.Visibility = sectionIndex == 5
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowOcrLanguageAvailability()
    {
        if (_settingsViewModel.OcrEngine == OcrEngineMode.PaddleOcrV6)
        {
            if (!_highQualityOcrModelManager.GetStatus().IsInstalled)
            {
                _settingsViewModel.SetStatus(
                    "PP-OCRv6 高质量识别模型尚未下载。");
            }

            return;
        }

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
            _ = _globalHotKeyManager.ApplyAvailable(savedBindings);
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
