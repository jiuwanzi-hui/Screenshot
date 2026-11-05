using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Core;
using Screenshot.App.Text;
using Orientation = System.Windows.Controls.Orientation;

namespace Screenshot.App.Presentation;

internal sealed class RealtimeTranslationSettingsPanel : StackPanel
{
    internal RealtimeTranslationSettingsPanel(Func<RealtimeTranslationSettings> read,
        Action<RealtimeTranslationSettings> save, Func<string> language)
    {
        var options = read().Normalize();
        AddLabel("点击开始或按快捷键后直接框选区域，再次触发即可停止。", true);
        AddLabel("译文显示");
        var display = new ComboBox { ItemsSource = new[] { "原位覆盖", "悬浮字幕" }, SelectedIndex = (int)options.Display };
        display.SelectionChanged += (_, _) => save(read() with { Display = (RealtimeTranslationDisplay)display.SelectedIndex });
        Children.Add(display);
        AddSlider("译文字号", 12, 48, options.FontSize, value => save(read() with { FontSize = value }));
        AddSlider("字幕底板不透明度", .35, 1, options.BackgroundOpacity, value => save(read() with { BackgroundOpacity = value }));
        AddLabel("原位覆盖沿用原文背景和位置，并自动适配字号。画面持续移动时保留可阅读的译文；状态图标悬停可查看详情。", true);
        AddSlider("字幕水平位置", 0, 1, options.SubtitleX, value => save(read() with { SubtitleX = value }));
        AddSlider("字幕垂直位置", 0, 1, options.SubtitleY, value => save(read() with { SubtitleY = value }));
        var glossaryLanguage = options.GlossaryLanguage;
        var label = AddLabel("术语表 · " + TranslationLanguageCatalog.GetDisplayName(glossaryLanguage));
        var glossary = new TextBox { Text = options.Glossary, AcceptsReturn = true, Height = 110, MaxLength = 16000,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10, 8, 10, 8),
            ToolTip = "每行填写 原文=固定译名，例如 Mana=法力值；填写相同原文与译名可保留名称。" };
        glossary.TextChanged += (_, _) =>
        {
            glossaryLanguage = language();
            label.Text = "术语表 · " + TranslationLanguageCatalog.GetDisplayName(glossaryLanguage);
            save(read() with { Glossary = glossary.Text, GlossaryLanguage = glossaryLanguage });
        };
        Children.Add(glossary);
        AddLabel("每行：原文=固定译名。术语按完整单词匹配，区分大小写，仅用于标注的目标语言。", true);
        AddLabel("沿用翻译设置中的模型和优先顺序。在线服务接收识别文字；离线模型在本机处理。开启时按需加载，连续使用短暂复用，空闲约 10 秒释放识别及本地大模型；停止后结束采集并释放画面和译文。首次加载可能稍慢。", true);
    }

    private TextBlock AddLabel(string text, bool help = false)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 7),
            FontSize = help ? 12 : 14, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, help ? "AppTextSecondaryBrush" : "AppTextPrimaryBrush");
        Children.Add(label);
        return label;
    }
    private void AddSlider(string label, double minimum, double maximum, double value, Action<double> changed)
    {
        var title = AddLabel(label);
        string Caption() => maximum <= 1 ? $"{label} · {value:P0}" : $"{label} · {value:0}";
        title.Text = Caption();
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, Margin = new Thickness(2, 0, 2, 4) };
        slider.SetResourceReference(StyleProperty, "VideoTrimSlider");
        slider.ValueChanged += (_, _) => { value = slider.Value; title.Text = Caption(); changed(value); };
        Children.Add(slider);
    }
}
