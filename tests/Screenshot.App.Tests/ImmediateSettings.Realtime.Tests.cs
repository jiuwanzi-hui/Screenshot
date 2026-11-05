using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed partial class ImmediateSettingsTests
{
    [Theory]
    [InlineData(AppTheme.GlassInk)]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.AuroraMist)]
    public void RealtimeSettingsRemainInTranslationPageAndPersistWithoutChangingOtherOptions(AppTheme theme)
    {
        Directory.CreateDirectory(_testDirectory);
        var store = new SettingsStore(Path.Combine(_testDirectory, "realtime.json"));
        WpfTestHost.Invoke(() =>
        {
            using var hotkeys = new GlobalHotKeyManager();
            var window = new MainWindow(CreateSettings() with { Theme = theme }, store,
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            try
            {
                var appXaml = System.Xml.Linq.XDocument.Load(Path.GetFullPath("../../../../../src/Screenshot.App/App.xaml", AppContext.BaseDirectory));
                var dictionary = appXaml.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources").Elements().Single();
                dictionary.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString().Replace(
                    "clr-namespace:Screenshot.App.Presentation", "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut", StringComparison.Ordinal),
                    new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/SnapCut;component/") }));
                AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                var page = (ScrollViewer)window.FindName("TranslationSettingsPanel");
                var shortcutPage = (ScrollViewer)window.FindName("HotKeySettingsPanel");
                var start = (Button)window.FindName("OpenRealtimeTranslationButton");
                var expander = (Expander)window.FindName("RealtimeTranslationSettingsGroup");
                var host = (ContentControl)window.FindName("RealtimeSettingsHost");
                var panel = Assert.IsType<RealtimeTranslationSettingsPanel>(host.Content);
                page.Visibility = Visibility.Visible; shortcutPage.Visibility = Visibility.Visible; expander.IsExpanded = true;
                page.Measure(new Size(780, 1000)); page.Arrange(new Rect(0, 0, 780, 1000)); page.UpdateLayout();
                shortcutPage.Measure(new Size(780, 1000)); shortcutPage.Arrange(new Rect(0, 0, 780, 1000)); shortcutPage.UpdateLayout();
                Assert.True(page.IsAncestorOf(start)); Assert.True(page.IsAncestorOf(expander));
                Assert.True(shortcutPage.IsAncestorOf((HotKeyCaptureBox)window.FindName("RealtimeTranslationHotKeyBox")));
                var viewModel = (SettingsViewModel)window.DataContext;
                var originalProvider = viewModel.TranslationProvider;
                var originalHotkey = viewModel.RegionCaptureHotKey;
                panel.Children.OfType<TextBox>().Single().Text = "Mana=法力值\nshield=护盾";
                panel.Children.OfType<ComboBox>().Last().SelectedIndex = 1;
                var requested = false;
                window.RealtimeTranslationRequested += (_, _) => requested = true;
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(requested);
                Assert.Equal("Mana=法力值\nshield=护盾", store.Load().Settings.RealtimeTranslation.Glossary);
                Assert.Equal(RealtimeTranslationDisplay.Subtitle, store.Load().Settings.RealtimeTranslation.Display);
                Assert.Equal(originalProvider, viewModel.TranslationProvider);
                Assert.Equal(originalHotkey, viewModel.RegionCaptureHotKey);
                page.Visibility = Visibility.Visible; expander.IsExpanded = true;
                panel.Measure(new Size(600, 1100)); panel.Arrange(new Rect(0, 0, 600, panel.DesiredSize.Height)); panel.UpdateLayout();
                Assert.True(panel.ActualHeight > 400);
                if (Environment.GetEnvironmentVariable("SNAPCUT_TEST_RENDER_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(600, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    var backdrop = new DrawingVisual();
                    using (var dc = backdrop.RenderOpen()) dc.DrawRectangle((Brush)window.FindResource("AppWindowBackgroundBrush"), null, new Rect(0, 0, 600, panel.ActualHeight));
                    bitmap.Render(backdrop); bitmap.Render(panel);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, $"realtime-settings-{theme}.png"));
                    encoder.Save(output);
                }
            }
            finally { window.RequestExit(); }
        });
    }
}
