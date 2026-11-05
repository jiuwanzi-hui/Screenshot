using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Core;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using Screenshot.App.Infrastructure;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Tests;

public sealed class TranslationTargetMenuTests
{
    [Fact]
    public void LanguageMenuUsesScrollbarAndHoverCannotBringClippedItemsIntoView()
    {
        WpfTestHost.Invoke(() =>
        {
            var menu = TranslationLanguageMenu.CreateMenu();
            menu.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/SnapCut;component/Themes/ThemedContextMenus.xaml") });
            menu.Measure(new Size(320, 440));
            menu.Arrange(new Rect(0, 0, 320, 440));
            menu.UpdateLayout();
            var scroll = Assert.IsType<ScrollViewer>(menu.Template.FindName("MenuScrollViewer", menu));
            Assert.IsType<System.Windows.Controls.Primitives.ScrollBar>(scroll.Template.FindName("PART_VerticalScrollBar", scroll));
            Assert.True(scroll.ScrollableHeight > 0);
            var suppressed = false;
            menu.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler((_, e) => suppressed = e.Handled), true);
            menu.Items.OfType<MenuItem>().Last().BringIntoView();
            Assert.True(suppressed);
            Assert.Equal(0, scroll.VerticalOffset);
            menu.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                new OffscreenInputSource(), 0, System.Windows.Input.Key.End)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            menu.Items.OfType<MenuItem>().Last().BringIntoView();
            menu.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
            scroll.ScrollToTop();
            menu.UpdateLayout();
            menu.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseMoveEvent });
            menu.Items.OfType<MenuItem>().Last().BringIntoView();
            menu.UpdateLayout();
            Assert.Equal(0, scroll.VerticalOffset);
            scroll.ScrollToBottom();
            menu.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
        });
    }

    private sealed class OffscreenInputSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }

    [Fact]
    public void TranslationPageAndSplitButtonFitOffscreen()
    {
        WpfTestHost.Invoke(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"snapcut-translation-ui-{Guid.NewGuid():N}.json");
            using var hotkeys = new GlobalHotKeyManager();
            var settings = AppSettings.CreateDefault() with { Theme = AppTheme.GlassMist,
                CloseBehavior = WindowCloseBehavior.ExitApplication,
                TranslationProfiles = [new() { Provider = "Baidu", Name = "百度翻译", Model = "Standard" }] };
            var window = new MainWindow(settings, new SettingsStore(path), new NoStartup(), hotkeys, new NoCredentials());
            try
            {
                AppThemeManager.ApplySettingsPalette(window.Resources, settings.Theme);
                var document = System.Xml.Linq.XDocument.Load(Path.GetFullPath("../../../../../src/Screenshot.App/App.xaml", AppContext.BaseDirectory));
                var dictionary = document.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources").Elements().Single();
                dictionary.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString().Replace(
                    "clr-namespace:Screenshot.App.Presentation", "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut", StringComparison.Ordinal),
                    new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/SnapCut;component/") }));
                ((ListBox)window.FindName("SettingsNavigation")).SelectedIndex = 3;
                var shell = (FrameworkElement)window.Content;
                window.Content = null;
                shell.DataContext = window.DataContext;
                shell.Resources.MergedDictionaries.Add(window.Resources);
                var host = new Border { Background = Brushes.DimGray, Child = shell };
                host.Measure(new Size(940, 780));
                host.Arrange(new Rect(0, 0, 940, 780));
                host.UpdateLayout();
                var page = (ScrollViewer)window.FindName("TranslationSettingsPanel");
                Assert.Equal(Visibility.Visible, page.Visibility);
                Assert.InRange(page.ActualWidth, 450, 780);
                Assert.False(window.IsVisible);
                if (Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_UI_PREVIEW") is { Length: > 0 } output)
                {
                    var bitmap = new RenderTargetBitmap(940, 780, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(output);
                    encoder.Save(stream);
                }
                var label = new TextBlock { Text = "译", FontSize = 18 };
                var button = new Button { Content = label, Width = 34, Height = 32 };
                TranslationLanguageMenu.SetEnabled(button, true);
                button.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                button.Measure(new Size(42, 32));
                button.Arrange(new Rect(0, 0, 42, 32));
                button.UpdateLayout();
                var content = Assert.IsType<Grid>(button.Content);
                Assert.InRange(content.DesiredSize.Width, 30, 38);
                Assert.Equal("▾", Assert.IsType<ToolbarDropDownButton>(content.Children[1]).Content);
            }
            finally { window.Close(); if (File.Exists(path)) File.Delete(path); }
        });
    }

    [Fact]
    public void MenuPersistsTargetAndSynchronizesAnAlreadyOpenTextWindow()
    {
        WpfTestHost.Invoke(() =>
        {
            var previous = TranslationTargetPreferences.Language ?? "zh-Hans";
            var settings = AppSettings.CreateDefault();
            var saves = 0;
            TranslationTargetPreferences.Configure("zh-Hans", language => { saves++; settings = settings with { TranslationTargetLanguage = language }; });
            using var window = new TextTranslationWindow(() => settings);
            try
            {
                var menu = TranslationLanguageMenu.CreateMenu();
                var english = menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "en");
                english.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("en", settings.TranslationTargetLanguage);
                Assert.Equal("en", ((ComboBox)window.FindName("TargetLanguageComboBox")).SelectedValue);
                Assert.Equal(1, saves);
                Assert.True(TranslationLanguageMenu.CreateMenu().Items.OfType<MenuItem>().Single(item => item.Tag as string == "en").IsChecked);
                TranslationTargetPreferences.Publish("ja");
                Assert.Equal("ja", ((ComboBox)window.FindName("TargetLanguageComboBox")).SelectedValue);
            }
            finally { window.Close(); TranslationTargetPreferences.Configure(previous, _ => { }); }
        });
    }

    [Fact]
    public void PublicProfileViewsAndPriorityRemainIndependent()
    {
        var settings = AppSettings.CreateDefault() with { TranslationProviderPriority = [TranslationProviderKind.Offline, TranslationProviderKind.Online],
            TranslationProfiles = [new() { Provider = "Baidu", AppId = "id" }, new() { Provider = "Zhipu", Model = "selected" }] };
        Assert.Equal([TranslationProviderKind.Offline, TranslationProviderKind.Online, TranslationProviderKind.Public], settings.ResolveTranslationProviderPriority());
        var model = new SettingsViewModel(settings);
        Assert.Equal("Baidu", Assert.Single(model.PublicTranslationProfiles).Provider);
        Assert.Equal("Zhipu", Assert.Single(model.OnlineTranslationProfiles).Provider);
        Assert.Contains(model.TranslationPriorityItems, item => item.Provider == TranslationProviderKind.Public);
    }

    private sealed class NoStartup : IStartupRegistrationService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) { }
    }
    private sealed class NoCredentials : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => null;
        public void SetApiKey(string providerId, string? apiKey) { }
    }
}
