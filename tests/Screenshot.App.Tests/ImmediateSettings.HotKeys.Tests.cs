using System.IO;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed partial class ImmediateSettingsTests
{
    [Fact]
    public void CompletionLetterIsSavedBySettingsAndCanBeChangedAfterReopening()
    {
        var path = Path.Combine(_testDirectory, "completion-letter.json");
        WpfTestHost.Invoke(() =>
        {
            using var hotkeys = new GlobalHotKeyManager();
            var store = new SettingsStore(path);
            var window = new MainWindow(CreateSettings(), store,
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            try
            {
                var box = Assert.IsType<HotKeyCaptureBox>(window.FindName("CompleteCaptureHotKeyBox"));
                box.ProcessCapturedVirtualKey('C', HotKeyModifiers.None);
                Assert.Equal("C", ((SettingsViewModel)window.DataContext).CompleteCaptureHotKey);
                store.FlushPendingSave();
            }
            finally { window.RequestExit(); }

            var reopened = new SettingsStore(path);
            var result = reopened.Load();
            Assert.Null(result.Warning);
            Assert.Equal("C", result.Settings.CompleteCaptureHotKey);
            var secondWindow = new MainWindow(result.Settings, reopened,
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            try
            {
                var box = Assert.IsType<HotKeyCaptureBox>(secondWindow.FindName("CompleteCaptureHotKeyBox"));
                box.ProcessCapturedVirtualKey('Q', HotKeyModifiers.None);
                Assert.Equal("Q", ((SettingsViewModel)secondWindow.DataContext).CompleteCaptureHotKey);
                reopened.FlushPendingSave();
                var saved = new SettingsStore(path).Load();
                Assert.Null(saved.Warning);
                Assert.Equal("Q", saved.Settings.CompleteCaptureHotKey);
                var bindings = HotKeyConfiguration.CreateBindings(saved.Settings);
                Assert.True(HotKeyConfiguration.Validate(bindings).IsValid);
                Assert.Equal(new HotKeyGesture(HotKeyModifiers.None, 'Q'),
                    Assert.Single(bindings, binding => binding.Action == HotKeyAction.CompleteCapture).Gesture);
            }
            finally { secondWindow.RequestExit(); }
        });
    }
}
