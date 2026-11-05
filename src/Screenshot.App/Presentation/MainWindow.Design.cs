using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;
using ComboBox = System.Windows.Controls.ComboBox;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;

namespace Screenshot.App.Presentation;

public partial class MainWindow
{
    private void RefineSettingsDesign()
    {
        RefineSaveRows();
        RefineThemePreviews();
        RefineHotKeyRows();
        RefineCommunityPages();
        RefineUpdateCard();
        RefineModelCards();
        RefineOcrPresentationChoice();
        FlattenSettingsSurfaces();
        var outer = (Border)Content;
        var columns = (Grid)outer.Child;
        var sidebar = (Border)columns.Children[0];
        var sidebarGrid = (Grid)sidebar.Child;
        sidebarGrid.Children[0].Visibility = Visibility.Collapsed;
        sidebarGrid.Margin = new Thickness(12, 20, 12, 16);
        sidebar.CornerRadius = new CornerRadius(0);
        outer.Margin = new Thickness(0);
        outer.BorderThickness = new Thickness(0);
        outer.CornerRadius = new CornerRadius(0);
        outer.Background = System.Windows.Media.Brushes.Transparent;
        // Keep navigation and forms together on wide/tall displays. Individual
        // pages still scroll within the available area on smaller monitors.
        outer.MaxWidth = 1200;
        outer.HorizontalAlignment = HorizontalAlignment.Stretch;
        outer.VerticalAlignment = VerticalAlignment.Stretch;
        var content = (Grid)columns.Children[1];
        content.Margin = new Thickness(24, 20, 18, 16);
        content.MaxHeight = 1000;
        content.VerticalAlignment = VerticalAlignment.Top;
        var footer = (Border)content.Children[^1];
        content.Children.Remove(footer);
        footer.Margin = new Thickness(0);
        footer.Padding = new Thickness(16, 4, 16, 4);
        footer.CornerRadius = new CornerRadius(0);
        footer.BorderThickness = new Thickness(0, 1, 0, 0);
        footer.SetResourceReference(Border.BackgroundProperty, "AppHeaderBackgroundBrush");
        var footerGrid = (Grid)footer.Child;
        footerGrid.Children[0].Visibility = Visibility.Collapsed;
        ((TextBlock)footerGrid.Children[1]).Text = "\uE73E";
        ((TextBlock)footerGrid.Children[2]).Text = "设置自动保存";
        WindowShell.Attach(this, footer: footer);
        ConfigureResponsiveSettings(columns, sidebarGrid, content);
    }

    private void RefineSaveRows()
    {
        var values = GeneralAppearanceGroup.Children.Cast<UIElement>().ToArray();
        GeneralAppearanceGroup.Children.Clear();
        GeneralAppearanceGroup.Children.Add(values[0]);
        ((FrameworkElement)values[1]).Margin = new Thickness(0, 6, 0, 10);
        GeneralAppearanceGroup.Children.Add(values[1]);
        var paths = (Grid)values[2];
        var pathGroup = (StackPanel)paths.Children[0];
        var pathLabel = pathGroup.Children[0];
        var pathBox = pathGroup.Children[1];
        pathGroup.Children.Clear();
        var browse = paths.Children[1];
        var pngGroup = (StackPanel)paths.Children[2];
        var pngLabel = pngGroup.Children[0];
        var pngCombo = pngGroup.Children[1];
        pngGroup.Children.Clear();
        paths.Children.Clear();
        var pathEditor = new Grid();
        pathEditor.ColumnDefinitions.Add(new ColumnDefinition());
        pathEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ((FrameworkElement)pathBox).Margin = new Thickness(0);
        ((FrameworkElement)browse).Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browse, 1);
        pathEditor.Children.Add(pathBox);
        pathEditor.Children.Add(browse);
        GeneralAppearanceGroup.Children.Add(FormRow(pathLabel, pathEditor));
        GeneralAppearanceGroup.Children.Add(FormRow(values[4], values[5]));
        GeneralAppearanceGroup.Children.Add(FormRow(pngLabel, pngCombo));
        var autoSave = (CheckBox)values[3];
        autoSave.Margin = new Thickness(0, 8, 0, 8);
        autoSave.Width = 420;
        autoSave.HorizontalAlignment = HorizontalAlignment.Left;
        autoSave.Style = (Style)FindResource("SettingsSwitch");
        GeneralAppearanceGroup.Children.Add(autoSave);
        GeneralAppearanceGroup.Children.Add(FormRow(values[6], values[7]));
        ((FrameworkElement)values[7]).ToolTip = ((TextBlock)values[8]).Text;
        GeneralAppearanceGroup.Children.Add(FormRow(values[9], values[10]));
        ((FrameworkElement)values[10]).ToolTip = ((TextBlock)values[11]).Text;
    }

    private void RefineThemePreviews()
    {
        var themes = (StackPanel)((Border)_generalPages[0][0]).Child;
        var grid = themes.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>().Single();
        var options = new SettingsViewModel(AppSettings.CreateDefault()).ThemeOptions;
        foreach (var option in grid.Children.OfType<RadioButton>())
        {
            if (option.Tag is not AppTheme theme) continue;
            var palette = new ResourceDictionary();
            AppThemeManager.ApplySettingsPalette(palette, theme);
            var settingsOption = options.Single(item => item.Value == theme.ToString());
            option.ToolTip = settingsOption.Description;
            var stack = new StackPanel();
            stack.Children.Add(new System.Windows.Controls.Image
            {
                Source = ThemePreview(palette, theme is not (AppTheme.GlassMist or AppTheme.GlassInk)), Height = 52, Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 7),
            });
            stack.Children.Add(new TextBlock { Text = settingsOption.Label,
                HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12,
                FontWeight = FontWeights.SemiBold });
            option.Content = stack;
            option.Padding = new Thickness(6);
        }
    }

    private static DrawingImage ThemePreview(ResourceDictionary colors, bool tonal)
    {
        Brush Brush(string key) => (Brush)colors[key];
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRoundedRectangle(Brush("AppWindowBackgroundBrush"), null, new Rect(0, 0, 150, 60), 5, 5);
            if (tonal) dc.DrawRoundedRectangle(Brush("AppSidebarBackgroundBrush"), null, new Rect(6, 6, 36, 48), 4, 4);
            dc.DrawRoundedRectangle(Brush("AppAccentMutedBrush"), null, new Rect(10, 10, tonal ? 27 : 31, 26), 4, 4);
            dc.DrawRoundedRectangle(Brush("AppSubtleBorderBrush"), null, new Rect(51, 10, 81, 8), 3, 3);
            dc.DrawRoundedRectangle(Brush("AppSubtleBorderBrush"), null, new Rect(51, 25, 66, 5), 2, 2);
            dc.DrawEllipse(Brush("AppAccentBrush"), null, new Point(15, 47), 4, 4);
            dc.DrawRoundedRectangle(Brush("AppSubtleBorderBrush"), null, new Rect(25, 43, 16, 7), 3, 3);
            dc.DrawRoundedRectangle(Brush(tonal ? "AppWarmAccentBrush" : "AppAccentMutedBrush"), null, new Rect(61, 41, 18, 11), 5, 5);
            dc.DrawRoundedRectangle(Brush("AppAccentBrush"), null, new Rect(85, 41, 48, 11), 5, 5);
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private void RefineHotKeyRows()
    {
        var root = (StackPanel)HotKeySettingsPanel.Content;
        foreach (var card in root.Children.OfType<Border>())
        {
            if (card.Child is not StackPanel group) continue;
            foreach (var row in group.Children.OfType<StackPanel>().ToArray())
            {
                if (row.Children[0] is not TextBlock label || row.Children.OfType<Grid>().LastOrDefault() is not { } editor) continue;
                if (!editor.Children.OfType<HotKeyCaptureBox>().Any()) continue;
                var hint = string.Join(Environment.NewLine, row.Children.OfType<TextBlock>().Skip(1).Select(text => text.Text));
                if (!string.IsNullOrWhiteSpace(hint)) { label.ToolTip = hint; editor.ToolTip = hint; }
                row.Children.Clear();
                editor.Width = 320;
                editor.HorizontalAlignment = HorizontalAlignment.Left;
                foreach (var box in editor.Children.OfType<HotKeyCaptureBox>())
                { box.Width = 240; box.Height = 36; box.MinWidth = 0; }
                foreach (var button in editor.Children.OfType<Button>())
                {
                    button.Width = 32; button.MinWidth = 0; button.Padding = new Thickness(0);
                    if (button.Content is string)
                        button.Content = new TextBlock { Text = "\uE70F", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16 };
                }
                label.FontWeight = FontWeights.Normal;
                var line = FormRow(label, editor);
                row.Margin = new Thickness(0);
                row.Children.Add(line);
            }
        }
    }

    private void RefineUpdateCard()
    {
        var root = (StackPanel)UpdateSettingsPanel.Content;
        var card = root.Children.OfType<Border>().First();
        var panel = (StackPanel)card.Child;
        var grid = (Grid)panel.Children[0];
        var identity = (StackPanel)grid.Children[0];
        var brand = new DockPanel();
        var icon = new System.Windows.Controls.Image
        {
            Source = AppBrandIcon.Source,
            Width = 52, Height = 52, Margin = new Thickness(0, 0, 18, 0),
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Remove(identity);
        ((TextBlock)identity.Children[0]).FontSize = 25;
        brand.Children.Add(icon);
        brand.Children.Add(identity);
        grid.Children.Insert(0, brand);
        CheckForUpdatesButton.Style = (Style)FindResource("PrimaryButton");
    }

    private void RefineOcrPresentationChoice()
    {
        var combo = RecognitionResultPresentationComboBox;
        if (combo.Parent is not Grid row) return;
        combo.Visibility = Visibility.Collapsed;
        var choices = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2, Width = 240, Height = 36 };
        combo.SelectionChanged -= OnImmediateSettingChanged;
        var popup = new RadioButton { Name = "RecognitionPopupChoice", Content = "独立弹窗" };
        var overlay = new RadioButton { Name = "RecognitionOverlayChoice", Content = "覆盖原图" };
        foreach (var choice in new[] { popup, overlay })
        {
            choice.Style = (Style)FindResource("ThemeOptionButton");
            choice.Margin = new Thickness(0);
            choice.Padding = new Thickness(12, 6, 12, 6);
            choices.Children.Add(choice);
            RegisterName(choice.Name, choice);
            choice.SetBinding(RadioButton.IsCheckedProperty, new System.Windows.Data.Binding(
                nameof(SettingsViewModel.RecognitionResultPresentation))
            {
                Source = _settingsViewModel,
                Mode = System.Windows.Data.BindingMode.TwoWay,
                Converter = EnumSelectionConverter.Instance,
                ConverterParameter = choice == popup
                    ? RecognitionResultPresentationMode.Popup : RecognitionResultPresentationMode.Overlay,
            });
            // Click runs after ToggleButton has updated its binding. Checked can
            // run in the middle of selection initialization or an uncheck cascade.
            choice.Click += (_, e) =>
            {
                choice.GetBindingExpression(RadioButton.IsCheckedProperty)?.UpdateSource();
                OnImmediateSettingChanged(choice, e);
            };
        }
        Grid.SetColumn(choices, 1);
        row.Children.Add(choices);
    }

    private void RefineModelCards()
    {
        // The compact identity row is separate from the original controls so
        // progress, import, cancellation and status updates retain their bindings.
        foreach (var status in new[] { HighQualityOcrModelStatusText, LocalLargeModelStatusText })
        {
            var content = (StackPanel)status.Parent;
            var heading = (TextBlock)content.Children[0];
            content.Children.Remove(heading);
            content.Children.Remove(status);
            var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heading.FontSize = 18;
            heading.TextWrapping = TextWrapping.Wrap;
            heading.Text = status == HighQualityOcrModelStatusText ? "PP-OCRv6 Small + 艺术字增强" : "TranslateGemma 4B";
            identity.Children.Add(heading);
            identity.Children.Add(status);
            var cube = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 20,2 L 38,12 L 38,32 L 20,42 L 2,32 L 2,12 Z M 2,12 L 20,22 L 38,12 M 20,22 L 20,42"),
                StrokeThickness = 2.2, Width = 42, Height = 46, Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            cube.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SelectionBorderBrush");
            var header = new DockPanel { Margin = new Thickness(0, 4, 0, 12), Tag = "ModelIdentityHeader" };
            header.Children.Add(cube);
            header.Children.Add(identity);
            content.Children.Insert(0, header);
            if (status == LocalLargeModelStatusText && content.Children.OfType<StackPanel>().LastOrDefault() is { } actions)
            {
                content.Children.Remove(actions);
                var commands = new WrapPanel { Width = 258, VerticalAlignment = VerticalAlignment.Center, Tag = "ModelActions",
                    Margin = new Thickness(12, 0, 0, 0) };
                foreach (var button in actions.Children.OfType<Button>().ToArray())
                {
                    actions.Children.Remove(button);
                    button.Margin = new Thickness(4, 3, 0, 3);
                    button.Padding = new Thickness(10, 4, 10, 4);
                    button.MinWidth = 0;
                    commands.Children.Add(button);
                }
                heading.FontSize = 16;
                cube.Width = 32; cube.Height = 36; cube.Margin = new Thickness(0, 0, 12, 0);
                DockPanel.SetDock(commands, Dock.Right);
                header.Children.Insert(0, commands);
                header.Margin = new Thickness(0);
            }
        }
        DownloadHighQualityOcrModelButton.Style = (Style)FindResource("PrimaryButton");
        HighQualityOcrModelPathText.FontSize = 11;
    }

    private void RefineCommunityPages()
    {
        // The source images and all author/contact/licence details remain the original controls.
        var author = (Grid)CreatorProfileCard.Child;
        var identity = (StackPanel)author.Children[1];
        var actions = (StackPanel)author.Children[2];
        author.Children.Remove(actions);
        actions.Orientation = Orientation.Horizontal;
        foreach (var button in actions.Children.OfType<Button>())
        { button.MinWidth = 0; button.Padding = new Thickness(12, 6, 12, 6); button.Margin = new Thickness(0, 0, 8, 0); }
        actions.Margin = new Thickness(0, 12, 0, 0);
        identity.Children.Add(actions);
        author.ColumnDefinitions[0].Width = new GridLength(80);
        author.ColumnDefinitions[2].Width = new GridLength(0);
        var donate = (StackPanel)DonateSettingsPanel.Content;
        var qrCard = donate.Children.OfType<Border>().Last();
        var qrGrid = (Grid)qrCard.Child;
        var qr = (StackPanel)qrGrid.Children[0];
        var description = (StackPanel)qrGrid.Children[1];
        qrGrid.Children.Clear();
        qrCard.Child = null;
        var centered = new StackPanel();
        qr.HorizontalAlignment = HorizontalAlignment.Center;
        description.Margin = new Thickness(0, 14, 0, 0);
        description.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var text in description.Children.OfType<TextBlock>()) text.TextAlignment = TextAlignment.Center;
        centered.Children.Add(qr);
        centered.Children.Add(description);
        qrCard.Child = centered;
        var contact = (StackPanel)ContactSettingsPanel.Content;
        var community = contact.Children.OfType<Border>().First();
        community.HorizontalAlignment = HorizontalAlignment.Stretch;
        var communityContents = (StackPanel)community.Child;
        var contactText = (TextBlock)communityContents.Children[1];
        communityContents.Children.Remove(contactText);
        foreach (var text in communityContents.Children.OfType<TextBlock>())
        { text.HorizontalAlignment = HorizontalAlignment.Center; text.TextAlignment = TextAlignment.Center; }
        var imageFrame = (Border)CommunityQrImage.Parent;
        imageFrame.Width = 270; imageFrame.Height = 270;
        var contactDetails = Group("微信联系", contactText);
        contactText.FontSize = 16;
        contactText.SetResourceReference(TextBlock.ForegroundProperty, "AppAccentForegroundBrush");
        contact.Children.Add(SettingsCard(contactDetails));
    }

    private static Border FormRow(UIElement label, UIElement editor)
    {
        var row = new Grid { Tag = "SettingsFormRow", MinHeight = 38, Margin = new Thickness(0, 5, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = (FrameworkElement)label;
        title.Margin = new Thickness(0, 0, 14, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        if (title is TextBlock text) { text.FontSize = 13; text.TextWrapping = TextWrapping.Wrap; text.FontWeight = FontWeights.Normal; }
        var control = (FrameworkElement)editor;
        control.Margin = new Thickness(0);
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control is ComboBox) { control.Width = 240; control.MinWidth = 0; control.Height = 36; }
        control.HorizontalAlignment = HorizontalAlignment.Left;
        if (control is Grid path)
        {
            foreach (var box in path.Children.OfType<System.Windows.Controls.TextBox>())
            { box.Width = 240; box.Height = 36; box.MinWidth = 0; box.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("Text") { Source = box }); }
        }
        Grid.SetColumn(editor, 1);
        row.Children.Add(label); row.Children.Add(editor);
        var separator = new Border { Child = row, BorderThickness = new Thickness(0) };
        return separator;
    }

    private void FlattenSettingsSurfaces()
    {
        static void Flatten(Border border)
        {
            border.Background = System.Windows.Media.Brushes.Transparent;
            border.BorderThickness = new Thickness(0);
            border.Padding = new Thickness(0);
        }
        foreach (var scroll in new[] { OcrSettingsPanel, DonateSettingsPanel, ContactSettingsPanel, UpdateSettingsPanel })
            if (scroll.Content is StackPanel panel)
                foreach (var border in panel.Children.OfType<Border>()) Flatten(border);
        Flatten(SelectedReleaseDetailsPanel);
        if (OfflineTranslationSettingsPanel.Children.OfType<StackPanel>().FirstOrDefault() is { } models)
            foreach (var model in models.Children.OfType<Border>()) Flatten(model);
        var author = (Grid)CreatorProfileCard.Child;
        author.Margin = new Thickness(0, 10, 0, 16);
        TranslationTargetLanguageComboBox.Width = 240;
        TranslationTargetLanguageComboBox.Height = 36;
        TranslationTargetLanguageComboBox.HorizontalAlignment = HorizontalAlignment.Left;
        foreach (var page in new[] { GeneralSettingsPanel, HotKeySettingsPanel, OcrSettingsPanel,
                     TranslationSettingsPanel, UpdateSettingsPanel, DonateSettingsPanel, ContactSettingsPanel })
        {
            // Only authored controls: do not alter popup/list item templates.
            foreach (var combo in LogicalDescendants(page).OfType<ComboBox>())
            {
                combo.Width = 240; combo.MinWidth = 0; combo.Height = 36;
                combo.HorizontalAlignment = HorizontalAlignment.Left;
            }
        }
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        foreach (var item in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return item;
            foreach (var child in LogicalDescendants(item)) yield return child;
        }
    }
}
