using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed partial class ImmediateSettingsTests
{
    [Theory]
    [InlineData(AppTheme.GlassInk)]
    [InlineData(AppTheme.AuroraMist)]
    public void WatermarkPageEditsPersistAndPreviewReleasesOnLeavingPage(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            Directory.CreateDirectory(_testDirectory);
            using var hotkeys = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
            var store = new SettingsStore(Path.Combine(_testDirectory, "watermark.json"));
            var window = new MainWindow(CreateSettings() with { Theme = theme }, store,
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            try
            {
                AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                var handle = new WindowInteropHelper(window).EnsureHandle();
                HwndSource.FromHwnd(handle)!.RootVisual = window;
                var root = (Border)window.Content;
                // Keep the HWND hidden, but detach the tree from Window.Visibility so
                // WPF exercises the control's real IsVisible lifetime offscreen.
                window.Content = null;
                root.Resources = window.Resources;
                HwndSource.FromHwnd(handle)!.RootVisual = root;
                var size = new Size(1000, 780);
                root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                DrainDispatcher();
                typeof(MainWindow).GetMethod("ShowGeneralCategory", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [4]);
                var control = (WatermarkSettingsControl)typeof(MainWindow).GetField("_watermarkSettingsControl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                Assert.True(control.IsVisible);
                ((TextBox)control.FindName("WatermarkText")).Text = "SnapCut · 我的截图";
                ((CheckBox)control.FindName("AutoApply")).IsChecked = true;
                ((CheckBox)control.FindName("IncludeTime")).IsChecked = true;
                ((CheckBox)control.FindName("IncludeLocation")).IsChecked = true;
                ((ComboBox)control.FindName("LocationMode")).SelectedIndex = 2;
                ((TextBox)control.FindName("ManualLocation")).Text = "上海 · 工作室";
                Assert.Null(control.FindName("WatermarkWidth"));
                ((Slider)control.FindName("WatermarkOpacity")).Value = 82;
                root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                DrainDispatcher();
                var preview = (WatermarkPreview)((ContentControl)control.FindName("PreviewHost")).Content;
                Assert.NotNull(preview.Composition);
                Assert.Contains("上海 · 工作室", preview.Composition.Text);
                Assert.InRange(preview.ActualWidth, 100, 780);
                Assert.Equal(3, preview.Composition.Parts.Count);
                Assert.Equal(.82, preview.Placement.Opacity);
                Assert.True(preview.Composition.GetBounds(preview.RenderSize).Right <= preview.ActualWidth);
                var settings = (WatermarkSettings)typeof(WatermarkSettingsControl).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control)!;
                var asset = new WatermarkImage { Id = Guid.NewGuid().ToString("N"), Name = "自定义透明水印.png" };
                typeof(WatermarkSettingsControl).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, [settings with { Images = [asset], SelectedImageId = asset.Id }]);
                typeof(WatermarkSettingsControl).GetMethod("RebuildImageSlots", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, null);
                var slots = (WrapPanel)control.FindName("ImageSlots");
                slots.Measure(new Size(600, double.PositiveInfinity)); slots.Arrange(new Rect(new Point(), slots.DesiredSize)); slots.UpdateLayout();
                var card = ((StackPanel)slots.Children[0]).Children.OfType<RadioButton>().Single();
                Assert.True(card.ActualHeight >= 110, "图片和名称不能被单行按钮高度裁切。");
                Assert.True(card.IsChecked);
                if (Environment.GetEnvironmentVariable("SNAPCUT_TEST_WATERMARK_RENDER_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    // Render the page by itself so the full editor and preview can be inspected without showing a window.
                    control.Measure(new Size(740, double.PositiveInfinity));
                    control.Arrange(new Rect(new Point(), control.DesiredSize));
                    control.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(740, (int)Math.Ceiling(control.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(control);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, $"watermark-{theme}.png")); encoder.Save(output);
                }
                typeof(MainWindow).GetMethod("ShowGeneralCategory", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [0]);
                Assert.Null(preview.Composition);
                window.RequestExit();
                var saved = store.Load().Settings.Watermark;
                Assert.Equal("SnapCut · 我的截图", saved.Text);
                Assert.True(saved.AutoApply);
                Assert.True(saved.IncludeTime);
                Assert.True(saved.IncludeLocation);
                Assert.False(saved.UseSystemLocation);
                Assert.Equal("上海 · 工作室", saved.ManualLocation);
                Assert.NotNull(saved.TextPlacement);
                Assert.Equal(.82, saved.Opacity);
            }
            finally { window.RequestExit(); }
        });
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
