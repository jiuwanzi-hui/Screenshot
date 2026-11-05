using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Screenshot.App.Presentation;
using Point = System.Windows.Point;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Cursors = System.Windows.Input.Cursors;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Border = System.Windows.Controls.Border;

namespace Screenshot.App.Editor;

internal static class NumberAnnotationOptions
{
    internal static void Configure(ToggleButton button, Func<ImageEditorCanvas?> getCanvas, Action? selectTool = null, bool inlineOptions = false)
    {
        button.Width = 42;
        button.Padding = new Thickness(0);
        button.ToolTip = inlineOptions ? "序号标注；在底部调整格式和起始序号，双击已有序号可编辑"
            : "序号标注；下拉设置格式和起始序号，双击已有序号可编辑";
        var content = new Grid { Width = 30, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = AnnotationToolIcons.Create(AnnotationToolIcons.Number);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(icon);
        Popup? openPopup = null;
        void Open()
        {
            if (openPopup?.IsOpen == true) { openPopup.IsOpen = false; return; }
            if (getCanvas() is { } canvas)
                openPopup = Show(button, canvas, selectTool: selectTool ?? (() => button.IsChecked = true));
        }
        button.PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
        button.PreviewMouseRightButtonUp += (_, e) =>
        {
            Open();
            e.Handled = true;
        };
        if (!inlineOptions)
        {
            var arrow = new ToolbarDropDownButton { ToolTip = "序号设置", FontSize = 10 };
            arrow.Click += (_, e) => { Open(); e.Handled = true; };
            content.Children.Add(arrow);
        }
        else { content.Width = 20; icon.HorizontalAlignment = HorizontalAlignment.Center; }
        button.Content = content;
    }

    internal static FrameworkElement CreateInline(Func<ImageEditorCanvas?> getCanvas)
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var styles = new[]
        {
            new StyleChoice(NumberingStyle.Digits, "1, 2, 3"),
            new StyleChoice(NumberingStyle.UpperLetters, "A, B, C"),
            new StyleChoice(NumberingStyle.LowerLetters, "a, b, c"),
            new StyleChoice(NumberingStyle.UpperRoman, "I, II, III"),
            new StyleChoice(NumberingStyle.LowerRoman, "i, ii, iii"),
        };
        var options = getCanvas()?.GetNumberOptions();
        var format = new ComboBox
        {
            ItemsSource = styles, DisplayMemberPath = nameof(StyleChoice.Label),
            SelectedValuePath = nameof(StyleChoice.Style), SelectedValue = options?.Style ?? NumberingStyle.Digits,
            Width = 112, Height = 28, Margin = new Thickness(0, 0, 8, 0), ToolTip = "序号格式",
        };
        var decrease = CreateStepButton("−", "上一个序号");
        var increase = CreateStepButton("+", "下一个序号");
        decrease.Width = increase.Width = 28;
        decrease.Height = increase.Height = 28;
        var current = new TextBlock { MinWidth = 40, Margin = new Thickness(8, 0, 8, 0), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        current.SetResourceReference(TextBlock.ForegroundProperty, "EditorToolbarIconBrush");
        var refreshing = false;
        void Refresh()
        {
            var state = getCanvas()?.GetNumberOptions();
            refreshing = true;
            format.SelectedValue = state?.Style ?? NumberingStyle.Digits;
            refreshing = false;
            current.Text = AnnotationNumbering.Format(state?.Value ?? 1, state?.Style ?? NumberingStyle.Digits);
            decrease.IsEnabled = state is { Value: > 1 };
            increase.IsEnabled = state is null || state.Value.Value < AnnotationNumbering.Maximum(state.Value.Style);
        }
        void Change(int delta)
        {
            if (getCanvas() is not { } canvas) return;
            var state = canvas.GetNumberOptions();
            var style = format.SelectedValue is NumberingStyle selected ? selected : state.Style;
            canvas.SetNumberOptions(style, Math.Clamp(state.Value + delta, 1, AnnotationNumbering.Maximum(style)), state.Editing);
            Refresh();
        }
        decrease.Click += (_, _) => Change(-1);
        increase.Click += (_, _) => Change(1);
        format.SelectionChanged += (_, _) => { if (!refreshing) Change(0); };
        panel.Children.Add(format); panel.Children.Add(decrease); panel.Children.Add(current); panel.Children.Add(increase);
        panel.Tag = (Action)Refresh;
        Refresh();
        return panel;
    }

    internal static Popup Show(FrameworkElement anchor, ImageEditorCanvas canvas, Point? position = null, Action? selectTool = null)
    {
        canvas.CommitPendingText();
        var options = canvas.GetNumberOptions();
        // Select once before opening: recording tool selection raises its windows,
        // which must not steal the popup's capture during a repeat-button press.
        if (!options.Editing) selectTool?.Invoke();
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = position.HasValue ? PlacementMode.RelativePoint : PlacementMode.Bottom,
            HorizontalOffset = position?.X ?? 0, VerticalOffset = position?.Y ?? 5,
            AllowsTransparency = true, StaysOpen = false,
        };
        var panel = new StackPanel { Width = 250 };
        var heading = new TextBlock
        {
            Text = options.Editing ? "编辑序号" : "序号设置", FontSize = 15, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimaryBrush");
        panel.Children.Add(heading);
        var styles = new[]
        {
            new StyleChoice(NumberingStyle.Digits, "数字 · 1, 2, 3"),
            new StyleChoice(NumberingStyle.UpperLetters, "大写字母 · A, B, C"),
            new StyleChoice(NumberingStyle.LowerLetters, "小写字母 · a, b, c"),
            new StyleChoice(NumberingStyle.UpperRoman, "罗马数字 · I, II, III"),
            new StyleChoice(NumberingStyle.LowerRoman, "小写罗马数字 · i, ii, iii"),
        };
        var format = new ComboBox
        {
            ItemsSource = styles, DisplayMemberPath = nameof(StyleChoice.Label),
            SelectedValuePath = nameof(StyleChoice.Style), SelectedValue = options.Style,
            Height = 36, Width = 250, Margin = new Thickness(0, 0, 0, 10),
        };
        panel.Children.Add(format);
        var label = new TextBlock { Text = options.Editing ? "当前序号" : "下一个序号", Margin = new Thickness(0, 0, 0, 5) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondaryBrush");
        panel.Children.Add(label);
        var value = options.Value;
        var currentStyle = options.Style;
        var stepper = new Grid { Height = 38 };
        stepper.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(38) });
        stepper.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        stepper.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(38) });
        var decrease = CreateStepButton("−", "上一个序号");
        var increase = CreateStepButton("+", "下一个序号");
        var current = new TextBlock
        {
            FontSize = 17, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        current.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimaryBrush");
        var preview = new System.Windows.Controls.Viewbox
        {
            Child = current, Margin = new Thickness(10, 5, 10, 5),
            Stretch = System.Windows.Media.Stretch.Uniform,
            StretchDirection = System.Windows.Controls.StretchDirection.DownOnly,
        };
        Grid.SetColumn(preview, 1);
        Grid.SetColumn(increase, 2);
        stepper.Children.Add(decrease);
        stepper.Children.Add(preview);
        stepper.Children.Add(increase);
        panel.Children.Add(stepper);
        var hint = new TextBlock
        {
            Text = options.Editing ? "修改立即生效，长按可连续调整。" : "长按可连续调整，放置后自动递增。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondaryBrush");
        panel.Children.Add(hint);
        void Refresh()
        {
            current.Text = AnnotationNumbering.Format(value, currentStyle);
            decrease.IsEnabled = value > 1;
            increase.IsEnabled = value < AnnotationNumbering.Maximum(currentStyle);
        }
        void Change(NumberingStyle style, int nextValue)
        {
            nextValue = Math.Clamp(nextValue, 1, AnnotationNumbering.Maximum(style));
            if (style == currentStyle && nextValue == value) return;
            currentStyle = style;
            value = nextValue;
            canvas.SetNumberOptions(style, value, options.Editing);
            Refresh();
        }
        decrease.Click += (_, _) => Change(currentStyle, value - 1);
        increase.Click += (_, _) => Change(currentStyle, value + 1);
        format.SelectionChanged += (_, _) =>
        {
            if (format.SelectedValue is NumberingStyle style) Change(style, value);
        };
        Refresh();
        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
        };
        var surface = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = panel };
        surface.SetResourceReference(Border.BackgroundProperty, "AppFloatingSurfaceBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");
        popup.Child = surface;
        var owner = Window.GetWindow(anchor);
        void OwnerClosed(object? sender, EventArgs e) => popup.IsOpen = false;
        if (owner is not null) owner.Closed += OwnerClosed;
        popup.Closed += (_, _) => { if (owner is not null) owner.Closed -= OwnerClosed; };
        popup.Opened += (_, _) => format.Focus();
        popup.IsOpen = true;
        return popup;
    }

    private static RepeatButton CreateStepButton(string glyph, string label)
    {
        var button = new RepeatButton
        {
            Content = glyph, FontSize = 20, Delay = 400, Interval = 80,
            ToolTip = label, Cursor = Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.SetResourceReference(RepeatButton.ForegroundProperty, "AppControlForegroundBrush");
        var surface = new FrameworkElementFactory(typeof(Border)) { Name = "StepSurface" };
        surface.SetResourceReference(Border.BackgroundProperty, "AppInputBackgroundBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");
        surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var content = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(content);
        var template = new System.Windows.Controls.ControlTemplate(typeof(RepeatButton)) { VisualTree = surface };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AppAccentBrush"), "StepSurface"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.75, "StepSurface"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "StepSurface"));
        template.Triggers.Add(disabled);
        button.Template = template;
        return button;
    }

    private sealed record StyleChoice(NumberingStyle Style, string Label);
}
