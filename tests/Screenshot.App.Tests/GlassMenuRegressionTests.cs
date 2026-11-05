using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Drawing.Color;

namespace Screenshot.App.Tests;

public sealed class GlassMenuRegressionTests
{
    [Fact]
    public void MenuCacheKeepsTheCompletedOpeningFrameInsteadOfTranslucentAnimationPixels()
    {
        var elapsed = TimeSpan.Zero;
        var reads = 0;
        using var frame = GlobalHotKeyManager.CaptureSettledMenuFrame(() => true, () =>
        {
            reads++;
            var bitmap = new Bitmap(4, 4);
            bitmap.SetPixel(0, 0, elapsed.TotalMilliseconds < 200 ? Color.Gray : Color.White);
            return new CapturedImage(bitmap);
        }, wait => elapsed += wait);
        Assert.NotNull(frame);
        Assert.Equal(Color.White.ToArgb(), frame.Bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(1, reads); // No repeated full-desktop sampling during the animation.
        Assert.InRange(elapsed.TotalMilliseconds, 200, 400);
    }

    [Fact]
    public void AMenuDismissedWhileSettlingDoesNotCacheTheDesktopBehindIt()
    {
        var visible = true;
        using var frame = GlobalHotKeyManager.CaptureSettledMenuFrame(() => visible,
            () => throw new InvalidOperationException("The closed menu must not be captured."), _ => visible = false);
        Assert.Null(frame);
    }

    [Fact]
    public void LocalPaletteRefreshImmediatelyRetainsGlassInsteadOfItsOpaqueFallback()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new RecognitionResultPopupWindow("主题切换", "保持当前窗口，无需重新截图。", null);
            try
            {
                _ = new WindowInteropHelper(window).EnsureHandle();
                foreach (var theme in new[] { AppTheme.AuroraMist, AppTheme.GlassMist, AppTheme.GlassInk, AppTheme.GlassMist })
                {
                    // Application chrome is notified before the settings window
                    // refreshes its own resource dictionary.
                    LiquidGlassChrome.Apply(window, theme);
                    AppThemeManager.ApplyWindowPalette(window, theme);
                    if (theme == AppTheme.GlassMist)
                    {
                        Assert.Same(Brushes.Transparent, window.Background);
                        Assert.Same(Brushes.Transparent, window.Resources["AppWindowBackgroundBrush"]);
                        Assert.Same(Brushes.Transparent, window.Resources["ImageEditorWindowBackgroundBrush"]);
                        Assert.Equal(Colors.Transparent, HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.CompositionTarget.BackgroundColor);
                    }
                    else Assert.NotSame(Brushes.Transparent, window.Background);
                }
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void GlassTrayMenuDoesNotActivateExplorerAndKeepsKeyboardCommands()
    {
        WpfTestHost.Invoke(() =>
        {
            var invoked = false;
            var menu = new GlassTrayMenuWindow([
                new("不可用", false, null), new("历史查看", true, () => invoked = true), new("设置", true, null)]);
            try
            {
                var foreground = GetForegroundWindow();
                var handle = new WindowInteropHelper(menu).EnsureHandle();
                Assert.False(menu.ShowActivated);
                Assert.Equal(0x08000080L, GetWindowLongPtrW(handle, -20).ToInt64() & 0x08000080L);
                menu.HandleMenuKey(0x28);
                Assert.True(menu.Commands[1].IsDefault);
                menu.HandleMenuKey(0x26);
                Assert.True(menu.Commands[2].IsDefault);
                menu.HandleMenuKey(0x24);
                Assert.Equal(foreground, GetForegroundWindow());
                Assert.False(menu.IsActive);
                menu.HandleMenuKey(0x0D);
                Assert.True(invoked);
            }
            finally { menu.Close(); }
        });
    }

    [Fact]
    public void AvailableSequenceBadgesKeepDarkInkAndStatusTextStaysReadableOnBareGlass()
    {
        WpfTestHost.Invoke(() =>
        {
            var root = new Border();
            AppThemeManager.ApplySettingsPalette(root.Resources, AppTheme.GlassMist);
            root.SetResourceReference(Border.BackgroundProperty, "AppGlassBackgroundBrush");
            var badge = new Border();
            badge.SetResourceReference(Border.BackgroundProperty, "AppAccentMutedBrush");
            var text = new TextBlock { Text = "1" };
            text.SetResourceReference(TextBlock.ForegroundProperty, "AppAccentTextBrush");
            badge.Child = text; root.Child = badge;
            var darkInk = text.Foreground;
            ClearGlassText.Apply(text, true);
            Assert.Same(darkInk, text.Foreground);
            badge.Background = Brushes.Transparent;
            text.Text = "已安装 · 占用约 2.4 GB";
            ClearGlassText.Apply(text, true);
            Assert.Same(Brushes.White, text.Foreground);
            Assert.NotNull(text.Effect);
        });
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
}
