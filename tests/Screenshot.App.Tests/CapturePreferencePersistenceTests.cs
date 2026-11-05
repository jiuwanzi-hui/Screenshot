using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Screenshot.App.Core;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class CapturePreferencePersistenceTests
{
    [Fact]
    public void TrayOnlyCaptureKeepsShapeArrowAndToolPreferencesBeforeSettingsWindowExists()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-preference-test-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        try
        {
            // Exercise the application's actual persistence callback without
            // creating a second WPF Application or running startup services.
            var app = RuntimeHelpers.GetUninitializedObject(typeof(global::Screenshot.App.App));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(global::Screenshot.App.App).GetField("_currentSettings", flags)!.SetValue(app, AppSettings.CreateDefault());
            typeof(global::Screenshot.App.App).GetField("_captureSettingsStore", flags)!.SetValue(app, store);
            Func<MainWindow> factory = () => throw new InvalidOperationException("Persistence must not create the settings window.");
            typeof(global::Screenshot.App.App).GetField("_mainWindowFactory", flags)!.SetValue(app, factory);
            var persist = typeof(global::Screenshot.App.App).GetMethod("PersistCapturePreference", flags)!;
            void Update(Func<AppSettings, AppSettings> update) => persist.Invoke(app,
                [update, (Action<MainWindow>)(_ => throw new InvalidOperationException("No settings window exists."))]);

            WpfTestHost.Invoke(() =>
            {
                using var pins = new PinnedImageManager();
                CaptureOverlayWindow CreateCapture(AppSettings settings) => new(new CaptureOverlayOptions
                {
                    SaveDirectory = directory, KeepHistory = false, HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(), PinnedImageManager = pins,
                    StartOcrAsync = image => { image.Dispose(); return Task.CompletedTask; },
                    DeferInitialScreenCapture = true,
                    ShapeToolMode = settings.ShapeToolMode, ArrowToolMode = settings.ArrowToolMode,
                    ArrowStyle = settings.ArrowStyle,
                    ShapeToolModeChanged = value => Update(current => current with { ShapeToolMode = value }),
                    ArrowToolModeChanged = value => Update(current => current with { ArrowToolMode = value }),
                    ArrowStyleChanged = value => Update(current => current with { ArrowStyle = value }),
                }, initialScreenSnapshot: new CapturedImage(new System.Drawing.Bitmap(64, 64)));
                using (var first = CreateCapture(AppSettings.CreateDefault()))
                {
                    ((System.Windows.Controls.MenuItem)first.FindName("InlineEllipseShapeMenuItem"))
                        .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    ((System.Windows.Controls.MenuItem)first.FindName("InlineCurvedHollowArrowMenuItem"))
                        .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    first.Close();
                }
                var nextSettings = (AppSettings)typeof(global::Screenshot.App.App).GetField("_currentSettings", flags)!.GetValue(app)!;
                using var next = CreateCapture(nextSettings);
                Assert.Equal("Ellipse", ((System.Windows.Controls.Primitives.ToggleButton)next.FindName("InlineShapeToolButton")).Tag);
                Assert.Equal("CurvedArrow", ((System.Windows.Controls.Primitives.ToggleButton)next.FindName("InlineArrowToolButton")).Tag);
                Assert.True(((System.Windows.Controls.MenuItem)next.FindName("InlineCurvedHollowArrowMenuItem")).IsChecked);
                next.Close();
            });
            Update(current => current with
            {
                AnnotationToolSettings = [new AnnotationToolSetting("Mosaic", "", 5, "RectangleMosaic")],
            });
            var current = (AppSettings)typeof(global::Screenshot.App.App).GetField("_currentSettings", flags)!.GetValue(app)!;
            Assert.Equal(ShapeToolMode.Ellipse, current.ShapeToolMode);
            Assert.Equal(ArrowToolMode.Curved, current.ArrowToolMode);
            Assert.Same(factory, typeof(global::Screenshot.App.App).GetField("_mainWindowFactory", flags)!.GetValue(app));
            store.FlushPendingSave();
            var restarted = new SettingsStore(store.SettingsPath).Load().Settings;
            Assert.Equal(ShapeToolMode.Ellipse, restarted.ShapeToolMode);
            Assert.Equal(ArrowToolMode.Curved, restarted.ArrowToolMode);
            Assert.Equal(ArrowStyle.Hollow, restarted.ArrowStyle);
            Assert.Equal("RectangleMosaic", Assert.Single(restarted.AnnotationToolSettings).Variant);
            Assert.Equal(current.RegionCaptureHotKey, restarted.RegionCaptureHotKey);
        }
        finally
        {
            store.FlushPendingSave();
            if (File.Exists(store.SettingsPath)) File.Delete(store.SettingsPath);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
        }
    }

    [Fact]
    public void ExplicitSettingsSaveCannotBeOverwrittenByAnOlderDeferredPreference()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-preference-order-test-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        try
        {
            var original = AppSettings.CreateDefault();
            store.QueueSave(original with { ShapeToolMode = ShapeToolMode.Ellipse });
            store.Save(original with { ArrowToolMode = ArrowToolMode.Curved });
            store.FlushPendingSave();
            var reloaded = store.Load().Settings;
            Assert.Equal(original.ShapeToolMode, reloaded.ShapeToolMode);
            Assert.Equal(ArrowToolMode.Curved, reloaded.ArrowToolMode);
        }
        finally
        {
            store.FlushPendingSave();
            if (File.Exists(store.SettingsPath)) File.Delete(store.SettingsPath);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
        }
    }
}
