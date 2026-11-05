using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class WindowActivationDesktopTests
{
    [DesktopFact]
    public void ImageQuestionRequestActivatesAndRestoresWithoutTopmostOrLosingDraft()
    {
        WpfTestHost.Invoke(() =>
        {
            using var chat = new ImageQuestionWindow(null, [ImageQuestionTests.Profile()]);
            var covering = new Window { Title = "SnapCut 窗口切换检查", Width = 360, Height = 180 };
            try
            {
                chat.ShowForUserRequest();
                var handle = new WindowInteropHelper(chat).Handle;
                Assert.Equal(handle, GetForegroundWindow());
                Assert.False(chat.Topmost);
                var prompt = (TextBox)chat.FindName("PromptBox");
                prompt.Text = "保留当前草稿";
                WindowActivation.Show(covering);
                Assert.Equal(new WindowInteropHelper(covering).Handle, GetForegroundWindow());
                chat.ShowForUserRequest();
                Assert.Equal(handle, GetForegroundWindow());
                chat.WindowState = WindowState.Minimized;
                Assert.True(IsIconic(handle));
                chat.ShowForUserRequest();
                chat.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(WindowState.Normal, chat.WindowState);
                Assert.Equal(handle, GetForegroundWindow());
                Assert.True(prompt.IsKeyboardFocused);
                Assert.False(chat.Topmost);
                Assert.Equal("保留当前草稿", prompt.Text);
                Assert.Empty(((StackPanel)chat.FindName("AttachmentsPanel")).Children);
                WindowActivation.Show(covering);
                Assert.Equal(new WindowInteropHelper(covering).Handle, GetForegroundWindow());
                Assert.False(chat.Topmost);
            }
            finally { covering.Close(); chat.Close(); }
        });
    }

    [DesktopFact]
    public void ReopeningMinimizedWindowRestoresItWithoutPermanentTopmost()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new Window
            {
                Title = "SnapCut 窗口恢复检查", Width = 360, Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new TextBlock { Text = "正在检查窗口恢复", Margin = new Thickness(24) },
            };
            try
            {
                WindowActivation.Show(window);
                window.WindowState = WindowState.Minimized;
                Assert.True(IsIconic(new WindowInteropHelper(window).Handle));
                WindowActivation.Show(window);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.True(window.IsVisible);
                Assert.False(window.Topmost);
                Assert.Equal(new WindowInteropHelper(window).Handle, GetForegroundWindow());
            }
            finally { window.Close(); }
        });
    }

    [DesktopFact]
    public void GlassMenuOpensInsideWorkAreaAndClosesBeforeItsCommand()
    {
        WpfTestHost.Invoke(() =>
        {
            using var tray = new TrayIconService(AppTheme.GlassMist);
            var menu = tray.CreateGlassMenu();
            try
            {
                var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                var foreground = GetForegroundWindow();
                menu.ShowAt(new System.Drawing.Point(work.Right - 8, work.Bottom - 8));
                menu.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(menu.IsVisible);
                Assert.Equal(foreground, GetForegroundWindow());
                Assert.True(MonitorGeometryService.TryGetWindowBounds(menu, out var bounds));
                Assert.True(work.Contains(bounds));
                Assert.True(bounds.Width > 100 && bounds.Height > 200);
                var requested = false;
                tray.OpenSettingsRequested += (_, _) => { Assert.False(menu.IsVisible); requested = true; };
                menu.Commands.Single(button => Equals(button.Content, "打开设置"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(requested);
            }
            finally { menu.Close(); }
        });
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
