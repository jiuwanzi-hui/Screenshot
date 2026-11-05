using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed partial class ImmediateSettingsTests
{
    [Fact]
    public void VisualOcrChoiceAndModelCardRemainAccessibleWithoutChangingDefault()
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotkeys = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
            using var window = new MainWindow(CreateSettings(), new SettingsStore(Path.Combine(_testDirectory, "visual-ocr.json")),
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            try
            {
                AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.AuroraMist);
                ((ListBox)window.FindName("SettingsNavigation")).SelectedIndex = 2;
                var root = (Border)window.Content;
                root.Background = (Brush)window.FindResource("AppWindowBackgroundBrush");
                root.Measure(new Size(780, 820)); root.Arrange(new Rect(0, 0, 780, 820)); root.UpdateLayout();
                var combo = (ComboBox)window.FindName("OcrEngineComboBox");
                Assert.Equal(3, combo.Items.Count);
                var before = combo.SelectedValue;
                combo.SelectedValue = nameof(OcrEngineMode.QwenVisualOcr);
                var vm = Assert.IsType<SettingsViewModel>(window.DataContext);
                Assert.Equal(OcrEngineMode.QwenVisualOcr, vm.OcrEngine);
                combo.SelectedValue = before;
                var card = Assert.IsType<StackPanel>(((ScrollViewer)window.FindName("OcrSettingsPanel")).Content).Children[^1];
                Assert.IsType<Border>(card);
                root.Measure(new Size(780, 820)); root.Arrange(new Rect(0, 0, 780, 820)); root.UpdateLayout();
                var page = (ScrollViewer)window.FindName("OcrSettingsPanel");
                page.ScrollToEnd(); root.UpdateLayout();
                Assert.True(page.ScrollableWidth < 1);
                var status = (TextBlock)typeof(MainWindow).GetField("_visualOcrStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.NotEmpty(status.Text);
                if (Environment.GetEnvironmentVariable("SNAPCUT_TEST_RENDER_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap(780, 820, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, "visual-ocr-settings.png")); encoder.Save(file);
                }
            }
            finally { window.RequestExit(); }
        });
    }
}
