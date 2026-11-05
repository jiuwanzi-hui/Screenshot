using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Threading;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class CaptureAspectRatioPersistenceTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolbarRatioReachesSettingsResidentNextCaptureAndDisk(bool settingsWindowExists)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-ratio-test-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        var initial = AppSettings.CreateDefault().Normalize();
        using var wire = new MemoryStream();
        using var connection = new FeatureProcessConnection(wire);
        FeatureProcessWorker? worker = null;
        MainWindow? window = null;
        GlobalHotKeyManager? hotkeys = null;
        object? app = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                app = RuntimeHelpers.GetUninitializedObject(typeof(global::Screenshot.App.App));
                Set(app, "_currentSettings", initial);
                Set(app, "_captureSettingsStore", store);
                Set(app, "_mainWindowFactory", (Func<MainWindow>)(() =>
                    throw new InvalidOperationException("Saving a toolbar choice must not open settings.")));
                worker = new FeatureProcessWorker(Dispatcher.CurrentDispatcher, () => false,
                    _ => Task.CompletedTask, () => { }, () => { });
                Set(worker, "_connection", connection);
                Set(app, "_featureWorker", worker);

                if (settingsWindowExists)
                {
                    hotkeys = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
                    window = new MainWindow(initial, store, new UnusedStartupService(), hotkeys, new UnusedCredentials());
                    Set(app, "_mainWindow", window);
                    window.SettingsSaved += typeof(global::Screenshot.App.App)
                        .GetMethod("OnSettingsSaved", PrivateInstance)!
                        .CreateDelegate<EventHandler<SettingsSavedEventArgs>>(app);
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                }

                var save = typeof(global::Screenshot.App.App).GetMethod("SaveCaptureAspectRatio", PrivateInstance)!
                    .CreateDelegate<Action<CaptureAspectRatio>>(app);
                using var pins = new PinnedImageManager();
                using (var first = CreateCapture(pins, initial, save))
                {
                    first.RememberAspectRatio(CaptureAspectRatio.Ratio16x9);
                    first.Close();
                }

                var updated = CurrentSettings(app);
                Assert.Equal(CaptureAspectRatio.Ratio16x9, updated.CaptureAspectRatio);
                Assert.Equal(initial.RegionCaptureHotKey, updated.RegionCaptureHotKey);
                Assert.Equal(initial.Theme, updated.Theme);
                Assert.Equal(initial.TranslationTargetLanguage, updated.TranslationTargetLanguage);
                if (window is not null)
                {
                    Assert.Equal(CaptureAspectRatio.Ratio16x9, ((SettingsViewModel)window.DataContext).CaptureAspectRatio);
                    var dropdown = (ComboBox)window.FindName("CaptureAspectRatioComboBox");
                    // The settings window stays offscreen; drain its pending binding updates.
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                    Assert.Equal(nameof(CaptureAspectRatio.Ratio16x9), dropdown.SelectedValue?.ToString());
                }

                // The very next warm capture reads the shared value without waiting for disk I/O.
                using var next = CreateCapture(pins, updated, save);
                Assert.Equal(CaptureAspectRatio.Ratio16x9,
                    Assert.Single(next.CreateAspectRatioMenu().Items.Cast<MenuItem>(), item => item.IsChecked).Tag);
                next.Close();
            });

            // Exercise the real worker message serialization and resident receiver too.
            wire.Position = 0;
            var message = await connection.ReadAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("settings", message.Kind);
            WpfTestHost.Invoke(() =>
            {
                var resident = RuntimeHelpers.GetUninitializedObject(typeof(global::Screenshot.App.App));
                Set(resident, "_currentSettings", initial);
                typeof(global::Screenshot.App.App).GetMethod("ReceiveFeatureState", PrivateInstance)!.Invoke(resident, [message]);
                Assert.Equal(CaptureAspectRatio.Ratio16x9, CurrentSettings(resident).CaptureAspectRatio);
            });

            store.FlushPendingSave();
            var restarted = new SettingsStore(store.SettingsPath).Load().Settings;
            Assert.Equal(CaptureAspectRatio.Ratio16x9, restarted.CaptureAspectRatio);

            WpfTestHost.Invoke(() =>
            {
                using var pins = new PinnedImageManager();
                var save = typeof(global::Screenshot.App.App).GetMethod("SaveCaptureAspectRatio", PrivateInstance)!
                    .CreateDelegate<Action<CaptureAspectRatio>>(app!);
                using var capture = CreateCapture(pins, restarted, save);
                // Unlocking needs persistence even though it leaves the selection dimensions unchanged.
                capture.RememberAspectRatio(CaptureAspectRatio.Free);
                Assert.Equal(CaptureAspectRatio.Free, CurrentSettings(app!).CaptureAspectRatio);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                if (window is not null)
                    Assert.Equal(nameof(CaptureAspectRatio.Free), ((ComboBox)window.FindName("CaptureAspectRatioComboBox")).SelectedValue?.ToString());
                capture.Close();
            });
            store.FlushPendingSave();
            Assert.Equal(CaptureAspectRatio.Free, new SettingsStore(store.SettingsPath).Load().Settings.CaptureAspectRatio);
        }
        finally
        {
            WpfTestHost.Invoke(() => { window?.RequestExit(); hotkeys?.Dispose(); worker?.Dispose(); });
            store.FlushPendingSave();
            if (File.Exists(store.SettingsPath)) File.Delete(store.SettingsPath);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
        }
    }

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, PrivateInstance)!.SetValue(target, value);

    private static AppSettings CurrentSettings(object app) =>
        (AppSettings)app.GetType().GetField("_currentSettings", PrivateInstance)!.GetValue(app)!;

    private static CaptureOverlayWindow CreateCapture(PinnedImageManager pins, AppSettings settings, Action<CaptureAspectRatio> save) =>
        new(new CaptureOverlayOptions
        {
            SaveDirectory = Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
            HistoryService = new CaptureHistoryService(), PinnedImageManager = pins,
            StartOcrAsync = image => { image.Dispose(); return Task.CompletedTask; },
            DeferInitialScreenCapture = true, CaptureAspectRatio = settings.CaptureAspectRatio,
            CaptureAspectRatioChanged = save,
        }, initialScreenSnapshot: new CapturedImage(new System.Drawing.Bitmap(64, 64)),
        deferInitialColorPickerActivation: true);

    private sealed class UnusedStartupService : IStartupRegistrationService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) => throw new InvalidOperationException("Startup setting was not changed.");
    }

    private sealed class UnusedCredentials : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => null;
        public void SetApiKey(string providerId, string? apiKey) => throw new InvalidOperationException("Credentials were not changed.");
    }
}
