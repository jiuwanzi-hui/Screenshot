using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using Screenshot.App.Core;
using Screenshot.App.Presentation;
using TextBox = System.Windows.Controls.TextBox;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class UiLocalizationTests(ITestOutputHelper output)
{
    [Fact]
    public void ChineseFirstLayoutDoesNotTrackLabelsOrOverrideTheirDirection()
    {
        WpfTestHost.Invoke(() =>
        {
            UiLocalization.Apply("zh-Hans");
            var root = new StackPanel();
            var labels = Enumerable.Range(0, 200).Select(_ => new TextBlock { Text = "常规设置" }).ToArray();
            foreach (var label in labels) root.Children.Add(label);
            var timer = Stopwatch.StartNew();
            root.Measure(new Size(500, 6000)); root.Arrange(new Rect(0, 0, 500, 6000)); root.UpdateLayout();
            foreach (var label in labels)
                label.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, label));
            output.WriteLine($"Chinese first layout and Loaded, 200 labels: {timer.Elapsed.TotalMilliseconds:F2} ms.");
            Assert.Equal(0, TrackedElementCount());
            Assert.All(labels, label => Assert.Same(DependencyProperty.UnsetValue,
                label.ReadLocalValue(FrameworkElement.FlowDirectionProperty)));
            for (var index = 0; index < 200; index++)
                Assert.Equal($"已复制 #AB{index:D4}", UiLocalization.Get($"已复制 #AB{index:D4}"));
            var cache = typeof(UiLocalization).GetField("DynamicCache", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
            Assert.Equal(0, (int)cache.GetType().GetProperty("Count")!.GetValue(cache)!);
        });
    }

    [Fact]
    public void ExistingChineseUiAttachesOnlyWhenTranslatedAndReleasesObserversOnReturn()
    {
        WpfTestHost.Invoke(() =>
        {
            UiLocalization.Apply("zh-Hans");
            var label = new TextBlock { Text = "常规设置" };
            var authoredDirection = new TextBlock { Text = "快捷键", FlowDirection = System.Windows.FlowDirection.RightToLeft };
            var root = new StackPanel(); root.Children.Add(label); root.Children.Add(authoredDirection);
            var window = new Window { Content = root }; // No HWND or visible desktop surface.
            try
            {
                root.Measure(new Size(400, 120)); root.Arrange(new Rect(0, 0, 400, 120)); root.UpdateLayout();
                Assert.Equal(0, TrackedElementCount());
                UiLocalization.Apply("en");
                Assert.Equal("General settings", label.Text);
                Assert.True(TrackedElementCount() > 0);
                UiLocalization.Apply("ar");
                Assert.Equal(UiLocalization.Get("常规设置"), label.Text);
                Assert.Equal(System.Windows.FlowDirection.RightToLeft, label.FlowDirection);
                UiLocalization.Apply("zh-Hans");
                Assert.Equal("常规设置", label.Text);
                Assert.Equal(0, TrackedElementCount());
                Assert.Same(DependencyProperty.UnsetValue, label.ReadLocalValue(FrameworkElement.FlowDirectionProperty));
                Assert.Equal(System.Windows.FlowDirection.RightToLeft, authoredDirection.FlowDirection);
                label.Text = "快捷键";
                UiLocalization.Apply("en");
                Assert.Equal(UiLocalization.Get("快捷键"), label.Text);
            }
            finally { window.Close(); UiLocalization.Apply("zh-Hans"); }
        });
    }

    [Fact]
    public void DirectionBindingSurvivesLanguageOverridesAndReturnsToItsSourceValue()
    {
        WpfTestHost.Invoke(() =>
        {
            UiLocalization.Apply("zh-Hans");
            var source = new DirectionSource { Direction = System.Windows.FlowDirection.LeftToRight };
            var label = new TextBlock { Text = "常规设置" };
            label.SetBinding(FrameworkElement.FlowDirectionProperty,
                new Binding(nameof(DirectionSource.Direction)) { Source = source });
            var window = new Window { Content = label };
            try
            {
                UiLocalization.Apply("en");
                Assert.Equal(System.Windows.FlowDirection.LeftToRight, label.FlowDirection);
                UiLocalization.Apply("ar");
                Assert.Equal(System.Windows.FlowDirection.RightToLeft, label.FlowDirection);
                Assert.True(BindingOperations.IsDataBound(label, FrameworkElement.FlowDirectionProperty));
                UiLocalization.Apply("zh-Hans");
                Assert.Equal("常规设置", label.Text);
                Assert.Equal(source.Direction, label.FlowDirection);
                Assert.True(BindingOperations.IsDataBound(label, FrameworkElement.FlowDirectionProperty));
                source.Direction = System.Windows.FlowDirection.RightToLeft;
                Assert.Equal(source.Direction, label.FlowDirection);
                source.Direction = System.Windows.FlowDirection.LeftToRight;
                Assert.Equal(source.Direction, label.FlowDirection);
                Assert.Equal(0, TrackedElementCount());
            }
            finally { window.Close(); UiLocalization.Apply("zh-Hans"); }
        });
    }

    [Fact]
    public void ClosingNeverShownTranslatedWindowRestoresLabelsAndReleasesObservers()
    {
        WpfTestHost.Invoke(() =>
        {
            UiLocalization.Apply("zh-Hans");
            var label = new TextBlock { Text = "常规设置" };
            var window = new Window { Content = label };
            try
            {
                UiLocalization.Apply("en");
                Assert.Equal("General settings", label.Text);
                Assert.True(TrackedElementCount() > 0);
                Assert.False(window.IsLoaded);
                window.Close();
                // A never-shown tree need not raise Unloaded. Closing the
                // window must release observers without a language reset.
                Assert.Equal("en", UiLocalization.Language);
                // Other tests may leave reusable windows in the shared WPF
                // host. Closing this window must detach its own label, not
                // remove observers belonging to those other windows.
                var states = typeof(UiLocalization).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetValue(null)!;
                Assert.False((bool)states.GetType().GetMethod("TryGetValue")!
                    .Invoke(states, [label, null])!);
                Assert.Equal("常规设置", label.Text);
                label.Text = "快捷键";
                Assert.Equal("快捷键", label.Text);
            }
            finally { window.Close(); UiLocalization.Apply("zh-Hans"); }
        });
    }

    [Fact]
    public void FormattedHistoryTextAndDynamicTooltipsFollowLanguageWithoutChangingBoundData()
    {
        WpfTestHost.Invoke(() =>
        {
            UiLocalization.Apply("zh-Hans");
            var fileSize = new Run();
            fileSize.SetBinding(Run.TextProperty, new Binding(nameof(Caption.Text)) { Source = new Caption { Text = "1.25 MB" } });
            var caption = new Run("文件大小 ");
            var label = new TextBlock(); label.Inlines.Add(caption); label.Inlines.Add(fileSize);
            var root = new StackPanel(); root.Children.Add(label);
            var window = new Window { Content = root };
            root.Measure(new Size(400, 100)); root.Arrange(new Rect(0, 0, 400, 100)); root.UpdateLayout();
            try
            {
                foreach (var language in UiLocalization.Languages.Where(option => option.Code != "zh-Hans"))
                {
                    UiLocalization.Apply(language.Code);
                    Assert.Equal(UiLocalization.Get("文件大小 "), caption.Text);
                    Assert.Equal("1.25 MB", fileSize.Text);
                    Assert.True(BindingOperations.IsDataBound(fileSize, Run.TextProperty));
                    Assert.Equal(2, label.Inlines.Count);
                    var tooltip = UiLocalization.Get("结束并保存（Ctrl+1）");
                    Assert.Contains("Ctrl+1", tooltip); Assert.DoesNotContain("结束", tooltip);
                    var error = UiLocalization.Get("Windows 未安装 en-US 的 OCR 语言包。");
                    Assert.Contains("en-US", error); Assert.DoesNotContain("未安装", error);
                }
            }
            finally { window.Close(); UiLocalization.Apply("zh-Hans"); }
        });
    }

    [DesktopFact]
    public void VisibleWindowLateContextMenuAndDialogSwitchImmediately()
    {
        WpfTestHost.Invoke(() =>
        {
            UiLocalization.Apply("zh-Hans");
            var window = new Window { Title = "翻译", Width = 320, Height = 140, ShowActivated = false, ShowInTaskbar = false };
            var label = new TextBlock { Text = "常规设置", Margin = new Thickness(20) };
            window.Content = label;
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "复制", ToolTip = "使用当前选区开始长截图" }; menu.Items.Add(item);
            try
            {
                window.Show(); window.UpdateLayout();
                foreach (var option in UiLocalization.Languages)
                {
                    // Include a popup that already existed in the authored
                    // language: it has its own presentation-source root.
                    menu.PlacementTarget = label; menu.IsOpen = true; menu.UpdateLayout();
                    UiLocalization.Apply(option.Code);
                    Assert.Equal(UiLocalization.Get("常规设置"), label.Text);
                    Assert.Equal(UiLocalization.Get("翻译"), window.Title);
                    menu.UpdateLayout();
                    Assert.Equal(UiLocalization.Get("复制"), item.Header);
                    Assert.Equal(UiLocalization.Get("使用当前选区开始长截图"), item.ToolTip);
                    menu.IsOpen = false;
                    var dialog = new LocalizedMessageBox(window, "是否继续？", "下载离线翻译模型",
                        MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
                    try
                    {
                        dialog.ShowActivated = false; dialog.Show(); dialog.UpdateLayout();
                        Assert.Equal(UiLocalization.Get("下载离线翻译模型"), dialog.Title);
                        var actions = (WrapPanel)((Grid)dialog.Content).Children[1];
                        var yes = (System.Windows.Controls.Button)actions.Children[0]; var no = (System.Windows.Controls.Button)actions.Children[1];
                        Assert.Equal(UiLocalization.Get("是"), yes.Content); Assert.Equal(UiLocalization.Get("否"), no.Content);
                        Assert.True(no.IsDefault); Assert.False(yes.IsDefault); Assert.Equal(MessageBoxResult.No, dialog.Result);
                    }
                    finally { dialog.Close(); }
                }
            }
            finally { menu.IsOpen = false; window.Close(); UiLocalization.Apply("zh-Hans"); }
        });
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("ru")]
    [InlineData("es")]
    [InlineData("ar")]
    public void InterfaceLanguageRoundTripsIndependentlyOfTranslationTarget(string language)
    {
        var settings = (AppSettings.CreateDefault() with { InterfaceLanguage = language, TranslationTargetLanguage = "ja" }).Normalize();
        var viewModel = new SettingsViewModel(settings);
        var saved = viewModel.CreateSettings();
        Assert.Equal(language, saved.InterfaceLanguage);
        Assert.Equal("ja", saved.TranslationTargetLanguage);
    }

    [Fact]
    public void LanguageSwitchKeepsBindingsAndUserTextAndReleasesUnloadedLabels()
    {
        WpfTestHost.Invoke(() =>
        {
            UiLocalization.Apply("zh-Hans");
            var source = new Caption { Text = "常规设置" };
            var label = new TextBlock();
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Caption.Text)) { Source = source });
            var input = new TextBox { Text = "常规设置" };
            var result = new TextBlock { Text = "常规设置", Tag = "UserContent" };
            var root = new StackPanel(); root.Children.Add(label); root.Children.Add(input); root.Children.Add(result);
            var window = new Window { Content = root };
            foreach (var element in new FrameworkElement[] { label, input, result })
                element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, element));
            try
            {
                UiLocalization.Apply("en");
                Assert.NotEqual("常规设置", label.Text);
                Assert.Equal("常规设置", input.Text);
                Assert.Equal("常规设置", result.Text);
                Assert.True(BindingOperations.IsDataBound(label, TextBlock.TextProperty));
                Assert.Equal("Copied #AB12EF", UiLocalization.Get("已复制 #AB12EF"));
                Assert.Equal("Loading TranslateGemma · Vulkan0", UiLocalization.Get("正在加载 TranslateGemma · Vulkan0"));
                source.Text = "快捷键";
                Assert.Equal(UiLocalization.Get("快捷键"), label.Text);
                UiLocalization.Apply("zh-Hans");
                Assert.Equal("快捷键", label.Text);
                root.Children.Remove(label);
                label.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, label));
                UiLocalization.Apply("en");
                Assert.Equal("快捷键", label.Text);
            }
            finally { window.Close(); UiLocalization.Apply("zh-Hans"); }
        });
    }

    [Fact]
    public void LayoutAndLanguageSwitchTranslatePlainLabelsAndLateTooltipsWithoutSyntheticLoadedEvents()
    {
        WpfTestHost.Invoke(() =>
        {
            UiLocalization.Apply("zh-Hans");
            var label = new TextBlock { Text = "常规设置" };
            var button = new System.Windows.Controls.Button { Content = "翻译" };
            var root = new StackPanel(); root.Children.Add(label); root.Children.Add(button);
            var window = new Window { Content = root };
            root.Measure(new Size(400, 200)); root.Arrange(new Rect(0, 0, 400, 200)); root.UpdateLayout();
            try
            {
                UiLocalization.Apply("en");
                Assert.Equal("General settings", label.Text);
                button.ToolTip = "使用当前选区开始长截图";
                Assert.Equal(UiLocalization.Get("使用当前选区开始长截图"), button.ToolTip);
                UiLocalization.Apply("fr");
                Assert.Equal(UiLocalization.Get("常规设置"), label.Text);
            }
            finally
            {
                foreach (var item in new FrameworkElement[] { label, button })
                    item.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, item));
                window.Close();
                UiLocalization.Apply("zh-Hans");
            }
        });
    }

    [Fact]
    public void AllSixLanguagesHaveTheSameCatalogKeysAndEssentialCaptions()
    {
        var english = UiLocalization.LoadCatalog("en");
        Assert.True(english.Count > 1000);
        foreach (var option in UiLocalization.Languages.Where(l => l.Code != "zh-Hans"))
        {
            var catalog = UiLocalization.LoadCatalog(option.Code);
            Assert.Equal(english.Keys.Order(), catalog.Keys.Order());
            foreach (var caption in new[] { "常规设置", "快捷键", "界面语言", "翻译", "区域截图", "退出" })
                Assert.NotEqual(caption, catalog[caption]);
            foreach (var (source, translated) in catalog)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated));
                Assert.True(translated.Length < Math.Max(180, source.Length * 14), $"Excessive UI expansion in {option.Code}: {source}");
                var placeholders = System.Text.RegularExpressions.Regex.Matches(source, @"\{\d+\}").Select(m => m.Value).Order();
                Assert.Equal(placeholders, System.Text.RegularExpressions.Regex.Matches(translated, @"\{\d+\}").Select(m => m.Value).Order());
            }
        }
    }

    private static int TrackedElementCount() =>
        ((System.Collections.IEnumerable)typeof(UiLocalization)
            .GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Cast<object>().Count();

    private sealed class DirectionSource : INotifyPropertyChanged
    {
        private System.Windows.FlowDirection _direction;
        public System.Windows.FlowDirection Direction
        {
            get => _direction;
            set { _direction = value; PropertyChanged?.Invoke(this, new(nameof(Direction))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class Caption : INotifyPropertyChanged
    {
        private string _text = "";
        public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
