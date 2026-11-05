using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ImageQuestionLayoutTests
{
    private static readonly string[] Controls = ["PromptBox", "SendButton", "ModelPicker", "SpecificModelBox", "StrengthBox", "ReturnButton", "ConversationScroll"];
    [Theory]
    [InlineData(AppTheme.AuroraMist, 680, 720)]
    [InlineData(AppTheme.ForestNight, 440, 620)]
    [InlineData(AppTheme.GlassInk, 680, 720)]
    public void ConversationFitsThemedWindowWithoutDisplayingDesktopWindows(AppTheme theme, int width, int height)
    {
        WpfTestHost.Invoke(() =>
        {
            using var window = new ImageQuestionWindow(ImageQuestionTests.Pixels(), [ImageQuestionTests.Profile()], () => { });
            try
            {
                var add = typeof(ImageQuestionWindow).GetMethod("AddMessage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                ((TextBlock)window.FindName("WelcomeText")).Visibility = Visibility.Collapsed;
                add.Invoke(window, ["你 · 2 张图片", "请对比两张图片，简要总结主要区别。", true]);
                var answer = (TextBox)add.Invoke(window, ["我的模型", "正在处理…", false])!;
                using var live = new ImageQuestionWindow.LiveAnswer(answer, (ScrollViewer)window.FindName("ConversationScroll"));
                live.Report(new("两张图片的主要区别如下：\n第一张包含较完整的说明，第二张突出了关键步骤。你可以继续补充希望重点比较的内容。", "先核对两张图片的标题和正文，再汇总差异。"));
                live.Complete();
                ((StackPanel)answer.Parent).Children.OfType<Expander>().Single().IsExpanded = true;
                window.StopProcessing();
                ((TextBox)window.FindName("PromptBox")).Text = "只关注第一张图片中的文字";
                _ = window.SendAsync();
                window.AddImage(ImageQuestionTests.Pixels());
                window.AddImage(ImageQuestionTests.Pixels());
                window.AddImage(ImageQuestionTests.Pixels());
                window.AddImage(ImageQuestionTests.Pixels());
                var path = Path.GetFullPath("../../../../../src/Screenshot.App/App.xaml", AppContext.BaseDirectory);
                var app = System.Xml.Linq.XDocument.Load(path);
                var dictionary = app.Root!.Elements().Single(e => e.Name.LocalName == "Application.Resources").Elements().Single();
                dictionary.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                var resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString().Replace(
                    "clr-namespace:Screenshot.App.Presentation", "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut", StringComparison.Ordinal),
                    new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/SnapCut;component/") });
                window.Resources.MergedDictionaries.Add(resources);
                AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                root.Resources = window.Resources;
                System.Windows.Documents.TextElement.SetForeground(root, (Brush)window.Resources["AppTextPrimaryBrush"]);
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                foreach (var name in Controls)
                {
                    var control = (FrameworkElement)window.FindName(name);
                    var p = control.TranslatePoint(new Point(), root);
                    Assert.True(control.ActualHeight > 0, name);
                    Assert.True(p.X >= 0 && p.Y >= 0 && p.X + control.ActualWidth <= width + 1 && p.Y + control.ActualHeight <= height + 1, name);
                }
                var headerY = ((FrameworkElement)window.FindName("ModelPicker")).TranslatePoint(new Point(), root).Y;
                foreach (var name in new[] { "SpecificModelBox", "FetchChatModelsButton", "StrengthBox", "ReturnButton" })
                    Assert.Equal(headerY, ((FrameworkElement)window.FindName(name)).TranslatePoint(new Point(), root).Y);
                var output = Environment.GetEnvironmentVariable("SNAPCUT_IMAGE_QUESTION_RENDER_DIR");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
                    using var file = File.Create(Path.Combine(output, $"image-question-{theme}.png")); encoder.Save(file);
                }
            }
            finally { window.Close(); }
        });
    }
}
