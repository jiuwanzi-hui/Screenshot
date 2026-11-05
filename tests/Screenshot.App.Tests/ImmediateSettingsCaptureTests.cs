using System.IO;
using System.Windows;
using System.Windows.Interop;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text.Realtime;

namespace Screenshot.App.Tests;

public sealed partial class ImmediateSettingsTests
{
    [DesktopFact]
    public void ScrollSelectionKeepsSettingsAsItsTargetBehindCaptureChrome()
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotkeys = new GlobalHotKeyManager();
            var window = new MainWindow(CreateSettings(), new SettingsStore(Path.Combine(_testDirectory, "scroll-target.json")),
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            window.Left = 200; window.Top = 150; window.Width = 720; window.Height = 480;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowActivated = false;
            var overlay = new Window { Width = 160, Height = 120, Left = 430, Top = 320,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Topmost = true,
                Background = System.Windows.Media.Brushes.Transparent, AllowsTransparency = true };
            try
            {
                window.Show(); overlay.Show();
                var region = RealtimeWindowTarget.Bounds(new WindowInteropHelper(overlay).Handle);
                Assert.True(ForegroundWindowCaptureService.TryCreateScrollCaptureTargetFromSelection(region,
                    out var target, ignoreCaptureProcess: true));
                Assert.NotNull(target);
                Assert.Equal(new WindowInteropHelper(window).Handle, target.WindowHandle);
                Assert.Equal(region, target.CaptureRegion);
                Assert.True(window.IsVisible);
                Assert.NotEqual(WindowState.Minimized, window.WindowState);
            }
            finally { overlay.Close(); window.RequestExit(); }
        });
    }
}
