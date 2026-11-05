using System.IO;
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
    [Theory]
    [InlineData(AppTheme.GlassInk, 1040)]
    [InlineData(AppTheme.AuroraMist, 520)]
    public void TranslationPageKeepsOnlineProfilesAndGroupsImageQuestionActions(AppTheme theme, int width)
    {
        Directory.CreateDirectory(_testDirectory);
        var store = new SettingsStore(Path.Combine(_testDirectory, "image-question-layout.json"));
        var settings = CreateSettings() with
        {
            Theme = theme,
            TranslationProfiles =
            [
                ImageQuestionTests.Profile() with { Id = "text-profile", Name = "我的在线模型" },
                ImageQuestionTests.Profile() with { Id = "image-profile", Name = "图片处理模型", Model = "images-2" },
            ],
        };
        store.Save(settings);
        WpfTestHost.Invoke(() =>
        {
            using var hotkeys = new GlobalHotKeyManager();
            var window = new MainWindow(settings, store, new FakeStartupRegistrationService(),
                hotkeys, new FakeTranslationCredentialStore());
            try
            {
                var app = System.Xml.Linq.XDocument.Load(Path.GetFullPath("../../../../../src/Screenshot.App/App.xaml", AppContext.BaseDirectory));
                var dictionary = app.Root!.Elements().Single(e => e.Name.LocalName == "Application.Resources").Elements().Single();
                dictionary.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString().Replace(
                    "clr-namespace:Screenshot.App.Presentation", "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut", StringComparison.Ordinal),
                    new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/SnapCut;component/") }));
                AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                var page = (ScrollViewer)window.FindName("TranslationSettingsPanel");
                var root = (StackPanel)page.Content;
                page.Content = null;
                root.Resources = window.Resources;
                var profiles = (ItemsControl)window.FindName("OnlineTranslationProfilesList");
                var online = (StackPanel)window.FindName("OnlineTranslationProfilesPanel");
                var offline = (StackPanel)window.FindName("OfflineTranslationSettingsPanel");
                var question = (StackPanel)window.FindName("ImageQuestionSettingsGroup");
                var toggle = (CheckBox)window.FindName("EnableImageQuestionCheckBox");
                var open = (Button)window.FindName("OpenImageQuestionButton");
                root.DataContext = window.DataContext;
                root.Measure(new Size(width, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height)); root.UpdateLayout();

                Assert.True(root.IsAncestorOf(profiles));
                Assert.True(online.IsAncestorOf(profiles));
                Assert.Equal(2, profiles.Items.Count);
                Assert.True(root.IsAncestorOf(question));
                Assert.False(offline.IsAncestorOf(question));
                Assert.True(offline.IsAncestorOf((ComboBox)window.FindName("OfflineTranslationEngineComboBox")));
                Assert.True(question.IsAncestorOf(toggle)); Assert.True(question.IsAncestorOf(open));
                Assert.InRange(toggle.TranslatePoint(new Point(0, toggle.ActualHeight / 2), question).Y -
                    open.TranslatePoint(new Point(0, open.ActualHeight / 2), question).Y, -1, 1);
                Assert.True(question.ActualHeight < 180);
                Assert.True(profiles.ActualHeight > 50);
                var requested = false;
                window.ImageQuestionRequested += (_, _) => requested = true;
                open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(requested);
                toggle.IsChecked = false;
                Assert.False(open.IsEnabled);
                Assert.Equal(settings.TranslationProfiles.Select(p => (p.Id, p.Name, p.Provider, p.Endpoint, p.Model, p.Purpose, p.IsEnabled)),
                    ((SettingsViewModel)window.DataContext).CreateSettings().TranslationProfiles.Select(p => (p.Id, p.Name, p.Provider, p.Endpoint, p.Model, p.Purpose, p.IsEnabled)));
                Assert.Equal(2, store.Load().Settings.TranslationProfiles.Length);

                if (Environment.GetEnvironmentVariable("SNAPCUT_IMAGE_QUESTION_RENDER_DIR") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    var rendered = new RenderTargetBitmap(width, (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    var backdrop = new DrawingVisual();
                    using (var dc = backdrop.RenderOpen()) dc.DrawRectangle((Brush)window.FindResource("AppWindowBackgroundBrush"), null, new Rect(root.RenderSize));
                    rendered.Render(backdrop); rendered.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
                    using var file = File.Create(Path.Combine(output, $"translation-settings-{theme}.png")); encoder.Save(file);
                }
            }
            finally { window.RequestExit(); }
        });
    }
}
