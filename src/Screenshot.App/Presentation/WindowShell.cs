using FontFamily = System.Windows.Media.FontFamily;
using Binding = System.Windows.Data.Binding;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Screenshot.App.Presentation;

/// <summary>A single client-drawn title bar, including the glass area behind all caption controls.</summary>
internal static class WindowShell
{
    internal static IDisposable? Attach(Window window, string? subtitle = null, FrameworkElement? footer = null)
    {
        if (window.Content is not FrameworkElement body || window.AllowsTransparency) return null;
        var originalBody = body;
        var originalStyle = window.WindowStyle;
        var originalChrome = WindowChrome.GetWindowChrome(window);
        var originalRow = body.ReadLocalValue(Grid.RowProperty);
        window.Content = null;
        ConfigureChrome(window, 46);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        header.SetResourceReference(Border.BackgroundProperty, "AppHeaderBackgroundBrush");
        header.SetResourceReference(Border.BorderBrushProperty, "AppSubtleBorderBrush");
        var bar = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        if (window.ResizeMode != ResizeMode.NoResize)
            actions.Children.Add(Caption("−", "最小化", () => SystemCommands.MinimizeWindow(window)));
        if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            actions.Children.Add(Caption("□", "最大化 / 还原", () =>
            {
                if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
                else SystemCommands.MaximizeWindow(window);
            }));
        actions.Children.Add(Caption("×", "关闭", window.Close));
        bar.Children.Add(actions);
        var brand = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(18, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(brand, BitmapScalingMode.HighQuality);
        brand.Children.Add(new System.Windows.Controls.Image
        {
            Source = AppBrandIcon.Source,
            Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0),
        });
        var title = new TextBlock { Text = "SnapCut", FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimaryBrush");
        brand.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var detail = new TextBlock { Text = "│  " + subtitle, FontSize = 13,
                Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondaryBrush");
            brand.Children.Add(detail);
        }
        bar.Children.Add(brand);
        header.Child = bar;
        root.Children.Add(header);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        if (footer is not null) { Grid.SetRow(footer, 2); root.Children.Add(footer); }
        var frame = new Border { Child = root, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), SnapsToDevicePixels = true };
        frame.SetResourceReference(Border.BackgroundProperty, "AppGlassBackgroundBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "AppGlassEdgeBrush");
        window.Content = frame;
        void UpdateState(object? sender, EventArgs args)
        {
            frame.CornerRadius = window.WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(14);
        }
        window.StateChanged += UpdateState;
        UpdateState(null, EventArgs.Empty);
        return new ShellAttachment(() =>
        {
            window.StateChanged -= UpdateState;
            if (!ReferenceEquals(window.Content, frame)) return;
            root.Children.Remove(body);
            window.Content = null;
            if (originalRow == DependencyProperty.UnsetValue) originalBody.ClearValue(Grid.RowProperty);
            else originalBody.SetValue(Grid.RowProperty, originalRow);
            WindowChrome.SetWindowChrome(window, originalChrome);
            window.WindowStyle = originalStyle;
            window.Content = originalBody;
        });
    }

    internal static void ConfigureChrome(Window window, double captionHeight)
    {
        // No native caption underneath the translucent client title bar.
        window.WindowStyle = WindowStyle.None;
        // Let DWM own the frame during live resizing. A zero glass thickness
        // sends WindowChrome down its GDI region/reclipping path on every size.
        // This also keeps the full client area, without a native strip above it.
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = captionHeight, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1), CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Infrastructure.WindowWorkArea.Attach(window);
    }

    private static Button Caption(string glyph, string tooltip, Action action)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 21, FontFamily = new FontFamily("Segoe UI") };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AppCaptionForegroundBrush");
        var button = new Button { Content = icon, ToolTip = tooltip, Width = 46, Height = 44,
            MinWidth = 0, Padding = new Thickness(0), FontSize = 21, FontFamily = new FontFamily("Segoe UI"),
            Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Button.Background)) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension("AppCaptionHoverBrush")));
        template.Triggers.Add(hover);
        button.Template = template;
        button.SetResourceReference(Button.ForegroundProperty, "AppCaptionForegroundBrush");
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private sealed class ShellAttachment(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
