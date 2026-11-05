using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Screenshot.App.Core;
using Border = System.Windows.Controls.Border;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Screenshot.App.Presentation;

public partial class MainWindow
{
    private readonly List<FrameworkElement[]> _generalPages = [];
    private WatermarkSettingsControl? _watermarkSettingsControl;
    private readonly Dictionary<AppTheme, RadioButton> _glassThemeOptions = [];

    // Reuse the existing editors so bindings, validation and immediate persistence
    // remain attached when a setting moves to another visual group.
    private void ArrangeSettingsPages()
    {
        ArrangeGeneralPage();
        ArrangeHotKeyPage();
        ArrangeOcrPage();
        ArrangeTranslationPage();
        ArrangeUpdatePage();
        foreach (var scroll in new[] { GeneralSettingsPanel, HotKeySettingsPanel,
                     OcrSettingsPanel, TranslationSettingsPanel, UpdateSettingsPanel,
                     DonateSettingsPanel, ContactSettingsPanel })
        {
            // Constrain each page to the viewport so star columns, wrapped
            // text and right-aligned actions shrink with the window.
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            if (scroll.Content is StackPanel panel)
                panel.Margin = new Thickness(0, 0, 12, 20);
        }
        // Keep all seven navigation entries, with maintenance/community below
        // the four settings entries. Selection indices are unchanged.
        if (SettingsNavigation.Items[4] is ListBoxItem update)
        {
            update.Margin = new Thickness(0, 2, 0, 2);
            update.Tag = "Maintenance";
        }
        RefineSettingsDesign();
    }

    private void ArrangeGeneralPage()
    {
        var root = (StackPanel)GeneralSettingsPanel.Content;
        var header = (StackPanel)root.Children[0];
        ((TextBlock)header.Children[0]).Text = "常规设置";
        // The existing combo remains bound but only the theme cards are shown.
        ((FrameworkElement)header.Children[2]).Visibility = Visibility.Collapsed;
        var themes = (StackPanel)GeneralAppearanceGroup.Children[^1];
        GeneralAppearanceGroup.Children.Remove(themes);
        themes.Visibility = Visibility.Visible;
        themes.Margin = new Thickness(0);
        if (themes.Children.OfType<UniformGrid>().FirstOrDefault() is { } themeGrid)
        {
            themeGrid.Columns = 4;
            foreach (var option in themeGrid.Children.OfType<RadioButton>())
            {
                if (option.Content is not System.Windows.Controls.Panel old) continue;
                var preview = old.Children.OfType<Border>().FirstOrDefault();
                var themeLabels = old.Children.OfType<StackPanel>().FirstOrDefault();
                if (preview is null || themeLabels is null) continue;
                old.Children.Remove(preview);
                old.Children.Remove(themeLabels);
                preview.Width = double.NaN;
                preview.Height = 24;
                preview.Margin = new Thickness(0, 0, 0, 8);
                themeLabels.Margin = new Thickness(0);
                var compact = new StackPanel();
                compact.Children.Add(preview);
                compact.Children.Add(themeLabels);
                option.Content = compact;
            }
        AddGlassThemeCard(themeGrid, AppTheme.GlassMist, "清透琉璃", "中性清透玻璃与边缘反光", "#38FFFFFF", "#A8CBDC");
            AddGlassThemeCard(themeGrid, AppTheme.GlassInk, "墨湾流光", "深色液态玻璃", "#203843", "#70BAC8");
        }
        root.Children.Clear();
        root.Children.Add(header);
        var tabs = new WrapPanel { Margin = new Thickness(0, 20, 0, 16) };
        SelectionMotion.SetIsScope(tabs, true);
        root.Children.Add(tabs);
        var languagePanel = new StackPanel();
        var languageTitle = new TextBlock { Text = "界面语言", Margin = new Thickness(0, 0, 0, 8), FontSize = 14, FontWeight = FontWeights.SemiBold };
        languageTitle.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimaryBrush");
        languagePanel.Children.Add(languageTitle);
        var languagePicker = new System.Windows.Controls.ComboBox
        {
            ItemsSource = UiLocalization.Languages, DisplayMemberPath = "Name", SelectedValuePath = "Code",
            Tag = "UserContent",
            SelectedValue = _settingsViewModel.InterfaceLanguage, Width = 270, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        languagePicker.SelectionChanged += (_, _) =>
        {
            if (languagePicker.SelectedValue is string language)
            { _settingsViewModel.InterfaceLanguage = language; ApplySettingsImmediately(); }
        };
        languagePanel.Children.Add(languagePicker);
        var languageHelp = new TextBlock { Text = "切换后立即生效，不改变翻译目标语言。", Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        languageHelp.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondaryBrush");
        languagePanel.Children.Add(languageHelp);
        _generalPages.Add([SettingsCard(themes), SettingsCard(languagePanel), SettingsCard(GeneralAppearanceGroup)]);
        _generalPages.Add([SettingsCard(GeneralCaptureToolbarGroup)]);
        var efficiencyHint = new TextBlock
        {
            Text = "后台效率：纯待机约 20 秒后自动使用 Windows 效率模式；截图、录屏和打开操作界面前恢复正常调度。此模式减少后台资源争用与功耗，不限制截图质量，也不代表内存固定在 20 MB 内。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        };
        efficiencyHint.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondaryBrush");
        GeneralWindowGroup.Children.Add(efficiencyHint);
        _generalPages.Add([SettingsCard(GeneralWindowGroup), SettingsCard(GeneralFloatingCaptureGroup)]);
        _generalPages.Add([SettingsCard(GeneralHistoryGroup), SettingsCard(GeneralScrollCaptureGroup)]);
        _watermarkSettingsControl = new WatermarkSettingsControl(_settingsViewModel.Watermark, settings =>
        {
            _settingsViewModel.Watermark = settings;
            ScheduleSettingsApply();
        });
        _generalPages.Add([SettingsCard(_watermarkSettingsControl)]);
        var labels = new[] { "外观与保存", "工具栏", "运行与悬浮", "历史与滚动", "水印" };
        for (var i = 0; i < labels.Length; i++)
        {
            var pageIndex = i;
            var tab = new RadioButton
            {
                Content = labels[i], GroupName = "GeneralSettingsCategory",
                Style = (Style)FindResource("ThemeOptionButton"),
                Margin = new Thickness(0, 0, 8, 6), MinWidth = 0,
                Padding = new Thickness(12, 5, 12, 5), FontSize = 13,
                FontWeight = FontWeights.SemiBold,
            };
            tab.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AppSettingsTabBackgroundBrush");
            tab.Checked += (_, _) => ShowGeneralCategory(pageIndex);
            tabs.Children.Add(tab);
            foreach (var section in _generalPages[i]) root.Children.Add(section);
        }
        ((RadioButton)tabs.Children[0]).IsChecked = true;
    }

    private void ShowGeneralCategory(int selected)
    {
        // Let shortcut tiles wrap to the page width. The two toolbar previews
        // keep their own horizontal scrollbars for an intentionally long row.
        GeneralSettingsPanel.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        for (var i = 0; i < _generalPages.Count; i++)
            foreach (var section in _generalPages[i])
            {
                var appearing = i == selected && section.Visibility != Visibility.Visible;
                section.Visibility = i == selected ? Visibility.Visible : Visibility.Collapsed;
                if (IsLoaded && appearing) UiTransitions.RevealPage(section);
            }
        GeneralSettingsPanel.ScrollToTop();
    }

    private void AddGlassThemeCard(UniformGrid grid, AppTheme theme, string name,
        string description, string start, string end)
    {
        var content = new StackPanel();
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(start),
            (Color)ColorConverter.ConvertFromString(end), 25);
        brush.Freeze();
        content.Children.Add(new Border { Height = 24, CornerRadius = new CornerRadius(4),
            Background = brush, Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = description, FontSize = 11, Opacity = .68,
            TextWrapping = TextWrapping.Wrap });
        var option = new RadioButton { Content = content, Tag = theme, GroupName = "AppTheme",
            Style = (Style)FindResource("ThemeCardButton") };
        option.Checked += OnThemeOptionChecked;
        _glassThemeOptions.Add(theme, option);
        grid.Children.Add(option);
    }

    private void ArrangeHotKeyPage()
    {
        var root = (StackPanel)HotKeySettingsPanel.Content;
        var realtimeHotKey = (StackPanel)((Grid)RealtimeTranslationHotKeyBox.Parent).Parent;
        root.Children.Remove(realtimeHotKey);
        var imageQuestionHotKey = (StackPanel)((Grid)ImageQuestionHotKeyBox.Parent).Parent;
        root.Children.Remove(imageQuestionHotKey);
        var items = root.Children.Cast<UIElement>().ToArray();
        root.Children.Clear();
        root.Children.Add(items[0]);
        root.Children.Add(items[1]);
        var intro = (TextBlock)items[1];
        intro.ToolTip = intro.Text;
        intro.Text = "点击快捷键即可修改";
        intro.Margin = new Thickness(0, 8, 0, 20);
        root.Children.Add(SettingsCard(Group("截图相关", items[2], items[3], items[8])));
        root.Children.Add(SettingsCard(Group("录制相关", items[4], items[5])));
        root.Children.Add(SettingsCard(Group("其他操作", items[6], items[7], items[9])));
        root.Children.Add(SettingsCard(Group("实时翻译", realtimeHotKey)));
        root.Children.Add(SettingsCard(Group("AI 问图", imageQuestionHotKey)));
        root.Children.Add(SettingsCard(items[10]));
    }

    private void ArrangeOcrPage()
    {
        var root = (StackPanel)OcrSettingsPanel.Content;
        var items = root.Children.Cast<UIElement>().ToArray();
        root.Children.Clear();
        root.Children.Add(items[0]);
        root.Children.Add(items[1]);
        ((FrameworkElement)items[1]).Margin = new Thickness(0, 8, 0, 20);
        var preferences = Group("识别设置");
        var automaticOcr = new System.Windows.Controls.CheckBox
        {
            Name = "AutoOcrAfterCaptureCheckBox",
            Style = (Style)FindResource("SettingsSwitch"), FontSize = 14,
            Width = 240, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        System.Windows.Automation.AutomationProperties.SetName(automaticOcr, "框选后自动识别文字");
        RegisterName(automaticOcr.Name, automaticOcr);
        automaticOcr.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(SettingsViewModel.AutoOcrAfterCapture))
        { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        automaticOcr.Checked += OnImmediateSettingChanged;
        automaticOcr.Unchecked += OnImmediateSettingChanged;
        var automaticOcrLabel = new TextBlock { Text = "框选后自动识别文字" };
        automaticOcrLabel.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimaryBrush");
        preferences.Children.Add(SettingRow(automaticOcrLabel, automaticOcr));
        var automaticOcrHint = new TextBlock
        {
            Text = "普通截图和钉图共用。开启后可拖选文字，按 Ctrl+C 复制；空白处仍可拖动。\n调整选区时暂停，松开鼠标后再识别。自动识别直接在原图上选字。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        };
        automaticOcrHint.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondaryBrush");
        preferences.Children.Add(automaticOcrHint);
        var recognitionMemoryHint = new TextBlock
        {
            Text = "资源提示：普通图片使用 Small；困难小图、横竖混排海报和描边大字按需调用 Medium 增强，会额外等待并增加运行内存，艺术字体和标点仍需核对。按物理核心、性能核、可用内存与当前负载分配 CPU 线程。主动开始识别框选时可准备 Small，仅打开自动识别开关不会预加载。识别在独立工作进程中运行，连续使用短暂复用，空闲约 10 秒或取消后退出并释放模型及运行库；关闭图片释放选字结果。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        };
        recognitionMemoryHint.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondaryBrush");
        preferences.Children.Add(recognitionMemoryHint);
        preferences.Children.Add(SettingRow(items[2], items[3]));
        preferences.Children.Add(SettingRow(items[7], items[8]));
        preferences.Children.Add(SettingRow(items[4], items[5]));
        preferences.Children.Add(items[6]);
        root.Children.Add(SettingsCard(preferences));
        ((FrameworkElement)items[9]).Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(items[9]);
        root.Children.Add(CreateVisualOcrModelCard());
    }

    private void ArrangeTranslationPage()
    {
        var root = (StackPanel)TranslationSettingsPanel.Content;
        // Keep explicit section references: inserting a setting must not detach
        // the configured profiles or move unrelated controls into the engine row.
        root.Children.Clear();
        root.Children.Add(TranslationPageTitle);
        root.Children.Add(TranslationPageIntroduction);
        TranslationPageIntroduction.Text = "按你的习惯选择翻译方式";
        TranslationPageIntroduction.Margin = new Thickness(0, 8, 0, 22);
        TranslationPriorityPanel.Margin = new Thickness(0, 0, 0, 18);
        var priorityNotes = TranslationPriorityPanel.Children.OfType<TextBlock>().Skip(1).ToArray();
        var priorityDetail = string.Join(Environment.NewLine, priorityNotes.Select(text => text.Text));
        foreach (var text in priorityNotes) text.Visibility = Visibility.Collapsed;
        TranslationPriorityPanel.ToolTip = priorityDetail;
        root.Children.Add(TranslationPriorityPanel);
        TranslationTargetLanguagePanel.Margin = new Thickness(0, 0, 0, 22);
        root.Children.Add(TranslationTargetLanguagePanel);
        root.Children.Add(SettingsCard(Group("实时翻译", OpenRealtimeTranslationButton, RealtimeTranslationSettingsGroup)));
        var publicPanel = Group("公共翻译");
        var addPublic = new System.Windows.Controls.Button { Content = "＋ 添加公共翻译", HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 8) };
        addPublic.SetResourceReference(StyleProperty, "SecondaryButton");
        addPublic.Click += OnAddPublicTranslationProfileClick;
        publicPanel.Children.Add(addPublic);
        var publicProfiles = new ItemsControl { ItemTemplate = OnlineTranslationProfilesList.ItemTemplate };
        publicProfiles.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("PublicTranslationProfiles"));
        publicPanel.Children.Add(publicProfiles);
        var publicNote = new TextBlock { Text = "可添加百度通用翻译、百度大模型文本翻译或有道。百度大模型使用 APP ID 和 API Key，其他服务使用各自的应用 ID 和密钥；共用上方目标语言。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18), FontSize = 12 };
        publicNote.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondaryBrush");
        publicPanel.Children.Add(publicNote);
        root.Children.Add(publicPanel);
        var engine = Group("离线模型");
        var offlineTitle = (TextBlock)engine.Children[0];
        engine.Children.Remove(offlineTitle);
        var offlineHeader = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        offlineHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        offlineHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        offlineTitle.Margin = new Thickness(0);
        offlineTitle.VerticalAlignment = VerticalAlignment.Center;
        offlineHeader.Children.Add(offlineTitle);
        OpenOfflineTextTranslationButton.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(OpenOfflineTextTranslationButton, 1);
        offlineHeader.Children.Add(OpenOfflineTextTranslationButton);
        engine.Children.Add(offlineHeader);
        engine.Children.Add(SettingRow(OfflineTranslationEngineLabel, OfflineTranslationEngineComboBox));
        var translationMemoryHint = new TextBlock
        {
            Text = "资源提示：选择或安装模型不会使其一直驻留。离线大模型按当前显卡及显存余量选择 GPU／CPU 路径，并按 CPU 能力、可用内存、系统负载和待译段落分配线程与并发，保留所选模型。首次加载、正文推理及回退原因会在操作界面显示；低配电脑和长文本仍需更多时间。连续使用短暂复用，大模型空闲约 10 秒或取消后退出工作进程，Mozilla 语言包空闲约 10 秒后释放。在线和公共翻译不加载本地模型。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        };
        translationMemoryHint.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondaryBrush");
        engine.Children.Add(translationMemoryHint);
        var models = OfflineTranslationSettingsPanel.Children.OfType<Border>().ToArray();
        if (models.Length == 2)
        {
            foreach (var model in models)
            {
                OfflineTranslationSettingsPanel.Children.Remove(model);
                model.Margin = new Thickness(0, 8, 0, 0);
                model.CornerRadius = new CornerRadius(8);
                CollapseModelDetails(model);
            }
            var gemma = (StackPanel)models[1].Child;
            ((TextBlock)gemma.Children[0]).Text = "TranslateGemma 4B";
            engine.Children.Add(models[1]);
            var otherEngine = new Expander { Header = "Mozilla 多语言离线语言包", Content = models[0], Margin = new Thickness(0, 12, 0, 0) };
            foreach (var note in OfflineTranslationSettingsPanel.Children.OfType<TextBlock>().ToArray())
            {
                OfflineTranslationSettingsPanel.Children.Remove(note);
                ((StackPanel)models[0].Child).Children.Add(note);
            }
            otherEngine.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AppTextSecondaryBrush");
            engine.Children.Add(otherEngine);
        }
        OfflineTranslationSettingsPanel.Children.Insert(0, engine);
        OfflineTranslationSettingsPanel.Margin = new Thickness(0, 0, 0, 22);
        // Licence and detailed routing remain available with the model details.
        foreach (var note in OfflineTranslationSettingsPanel.Children.OfType<TextBlock>())
            note.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(OfflineTranslationSettingsPanel);
        root.Children.Add(SettingsCard(Group("AI 问图", ImageQuestionSettingsGroup)));
        OnlineTranslationProfilesPanel.Margin = new Thickness(0);
        root.Children.Add(SettingsCard(OnlineTranslationProfilesPanel));
        root.Children.Add(OnlineTranslationSettingsPanel);
    }

    private static void CollapseModelDetails(Border card)
    {
        if (card.Child is not StackPanel content) return;
        // Keep status, progress and actions visible; retain all long descriptions
        // and installation paths in an explicitly labelled details expander.
        var details = new StackPanel();
        foreach (var text in content.Children.OfType<TextBlock>().Skip(1).ToArray())
        {
            if (text.Text == "翻译精度" || text.Name.Contains("Status", StringComparison.Ordinal) ||
                text.Name.Contains("Progress", StringComparison.Ordinal)) continue;
            content.Children.Remove(text);
            details.Children.Add(text);
        }
        var expander = new Expander
        {
            Header = "模型说明与安装位置", Content = details,
            Margin = new Thickness(0, 14, 0, 0),
        };
        expander.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AppTextSecondaryBrush");
        content.Children.Add(expander);
    }

    private void ArrangeUpdatePage()
    {
        var root = (StackPanel)UpdateSettingsPanel.Content;
        var items = root.Children.Cast<UIElement>().ToArray();
        root.Children.Clear();
        foreach (var item in items.Take(3)) root.Children.Add(item);
        var history = new StackPanel();
        foreach (var item in items.Skip(4).Take(5)) history.Children.Add(item);
        root.Children.Add(SettingsCard(history));
        root.Children.Add(items[^1]);
    }

    private static StackPanel Group(string heading, params UIElement[] children)
    {
        var group = new StackPanel();
        var title = new TextBlock { Text = heading, FontSize = 15,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimaryBrush");
        group.Children.Add(title);
        foreach (var child in children) group.Children.Add(child);
        return group;
    }

    private static Border SettingsCard(UIElement child)
    {
        if (child is FrameworkElement element) element.Margin = new Thickness(0);
        var card = new Border
        {
            Child = child, BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 28),
        };
        card.Background = System.Windows.Media.Brushes.Transparent;
        return card;
    }

    private static Grid SettingRow(UIElement label, UIElement control)
    {
        var row = new Grid { Tag = "SettingsFormRow", Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ((FrameworkElement)label).Margin = new Thickness(0, 0, 12, 0);
        ((FrameworkElement)label).VerticalAlignment = VerticalAlignment.Center;
        if (label is TextBlock text) { text.FontWeight = FontWeights.Normal; text.FontSize = 14; }
        ((FrameworkElement)control).Margin = new Thickness(0);
        if (control is System.Windows.Controls.ComboBox combo)
        { combo.Width = 240; combo.MinWidth = 0; combo.Height = 36; combo.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; }
        Grid.SetColumn(control, 1);
        row.Children.Add(label);
        row.Children.Add(control);
        return row;
    }
}
