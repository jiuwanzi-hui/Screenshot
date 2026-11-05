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
    [Fact]
    public void ExistingSettingsNavigationPagesAndNewWatermarkPanelSwitchAllVisibleCaptions()
    {
        WpfTestHost.Invoke(() =>
        {
            UiLocalization.Apply("zh-Hans");
            using var hotkeys = new GlobalHotKeyManager();
            var window = new MainWindow(CreateSettings(), new SettingsStore(Path.Combine(_testDirectory, "all-locales.json")),
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            var inspected = new HashSet<FrameworkElement>();
            try
            {
                var root = (FrameworkElement)window.Content;
                var english = UiLocalization.LoadCatalog("en");
                void Layout() { root.Measure(new Size(1100, 840)); root.Arrange(new Rect(0, 0, 1100, 840)); root.UpdateLayout(); }
                IEnumerable<FrameworkElement> Elements(DependencyObject node)
                {
                    if (node is FrameworkElement element) yield return element;
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                        foreach (var child in Elements(VisualTreeHelper.GetChild(node, i))) yield return child;
                }
                static bool IsPresentationLabel(TextBlock text)
                {
                    for (DependencyObject? current = text; current is not null; current = VisualTreeHelper.GetParent(current))
                        if (current is System.Windows.Controls.Primitives.TextBoxBase ||
                            current is FrameworkElement element && element.Tag as string == "UserContent") return false;
                    return true;
                }
                for (var section = 0; section < 11; section++)
                {
                    UiLocalization.Apply("zh-Hans");
                    var page = Math.Max(0, section - 4);
                    ((ListBox)window.FindName("SettingsNavigation")).SelectedIndex = page;
                    Layout();
                    if (section < 5)
                        Elements(root).OfType<System.Windows.Controls.RadioButton>()
                            .Where(tab => tab.GroupName == "GeneralSettingsCategory").ElementAt(section).IsChecked = true;
                    Layout();
                    var elements = Elements(root).ToArray();
                    foreach (var element in elements) inspected.Add(element);
                    var captions = elements.OfType<TextBlock>().Where(label => label.ActualWidth > 0 &&
                        IsPresentationLabel(label) && english.ContainsKey(label.Text))
                        .Select(label => (Label: label, Source: label.Text)).ToArray();
                    var tooltips = elements.Where(element => element.ActualWidth > 0 && element.ToolTip is string)
                        .Select(element => (Element: element, Source: (string)element.ToolTip)).ToArray();
                    Assert.True(captions.Length >= 7);
                    foreach (var language in UiLocalization.Languages.Where(option => option.Code != "zh-Hans"))
                    {
                        UiLocalization.Apply(language.Code); Layout();
                        foreach (var caption in captions)
                            Assert.Equal(UiLocalization.Get(caption.Source), caption.Label.Text);
                        foreach (var tooltip in tooltips)
                            Assert.Equal(UiLocalization.Get(tooltip.Source), tooltip.Element.ToolTip);
                    }
                }
            }
            finally
            {
                foreach (var element in inspected) element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, element));
                window.RequestExit(); UiLocalization.Apply("zh-Hans");
            }
        });
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("ru")]
    [InlineData("es")]
    [InlineData("ar")]
    public void InterfaceLanguagesRenderTheActualThemedSettingsWithoutOpeningWindows(string language)
    {
        WpfTestHost.Invoke(() =>
        {
            UiLocalization.Apply(language);
            using var hotkeys = new GlobalHotKeyManager();
            var window = new MainWindow(CreateSettings() with { Theme = AppTheme.GlassInk, InterfaceLanguage = language },
                new SettingsStore(Path.Combine(_testDirectory, "locale.json")),
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            var loaded = new List<FrameworkElement>();
            try
            {
                var appXaml = System.Xml.Linq.XDocument.Load(Path.GetFullPath("../../../../../src/Screenshot.App/App.xaml", AppContext.BaseDirectory));
                var dictionary = appXaml.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources").Elements().Single();
                dictionary.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString().Replace(
                    "clr-namespace:Screenshot.App.Presentation", "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut", StringComparison.Ordinal),
                    new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/SnapCut;component/") }));
                AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.GlassInk);
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1100, 840)); root.Arrange(new Rect(0, 0, 1100, 840)); root.UpdateLayout();
                void InspectTree(DependencyObject node)
                {
                    if (node is FrameworkElement element)
                    {
                        loaded.Add(element);
                    }
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) InspectTree(VisualTreeHelper.GetChild(node, i));
                }
                InspectTree(root); root.UpdateLayout();
                Assert.Contains(loaded.OfType<TextBlock>(), text => text.Text == UiLocalization.Get("常规设置"));
                if (language != "zh-Hans")
                    Assert.DoesNotContain(loaded.OfType<TextBlock>().Where(text => text.ActualWidth > 0),
                        text => text.Text == "常规设置" || text.Text == "界面主题" || text.Text == "保存、外观和运行方式");
                Assert.Contains(loaded.OfType<ComboBox>(), box => box.ItemsSource == UiLocalization.Languages);
                Assert.False(window.IsVisible);
                var page = (ScrollViewer)window.FindName("GeneralSettingsPanel");
                Assert.True(page.ScrollableWidth < 1);
                if (Environment.GetEnvironmentVariable("SNAPCUT_TEST_RENDER_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap(1100, 840, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, "settings-" + language + ".png")); encoder.Save(file);
                }
            }
            finally
            {
                foreach (var element in loaded) element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, element));
                window.RequestExit(); UiLocalization.Apply("zh-Hans");
            }
        });
    }
}
