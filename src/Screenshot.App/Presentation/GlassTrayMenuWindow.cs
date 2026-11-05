using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Runtime.InteropServices;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Drawing.Point;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Screenshot.App.Presentation;

internal sealed record TrayMenuEntry(string? Label, bool Enabled, Action? Execute);

/// <summary>A short-lived desktop-blurred menu; text never inherits window opacity.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Window.Closed disposes the input monitor and removes its native hooks.")]
internal sealed class GlassTrayMenuWindow : Window
{
    private readonly List<Button> _commands = [];
    private bool _closed;
    private bool _closing;
    private HwndSource? _source;
    private TrayMenuInputMonitor? _inputMonitor;
    private int _selectedIndex = -1;

    internal GlassTrayMenuWindow(IEnumerable<TrayMenuEntry> entries)
    {
        Title = "SnapCut 菜单";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        AppThemeManager.ApplySettingsPalette(Resources, AppTheme.GlassMist);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = false,
        });
        var panel = new StackPanel { Margin = new Thickness(6), MinWidth = 124 };
        foreach (var entry in entries)
        {
            if (entry.Label is null)
            {
                var separator = new Border { Height = 1, Margin = new Thickness(9, 5, 9, 5) };
                separator.SetResourceReference(Border.BackgroundProperty, "AppSeparatorBrush");
                panel.Children.Add(separator);
                continue;
            }
            var button = new Button
            {
                Content = entry.Label, IsEnabled = entry.Enabled, Height = 38,
                Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 1, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                BorderThickness = new Thickness(0),
                FontSize = 13, Style = CreateCommandStyle(),
                Focusable = false,
            };
            button.Click += (_, _) =>
            {
                Close(); // Release the temporary input hooks before opening the requested window.
                entry.Execute?.Invoke();
            };
            _commands.Add(button);
            panel.Children.Add(button);
        }
        var grid = new Grid();
        grid.Children.Add(panel);
        var frame = new Border { Child = grid, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        frame.SetResourceReference(Border.BackgroundProperty, "AppPopupBackgroundBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "AppControlEdgeBrush");
        Content = frame;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _ = SetWindowLongPtrW(handle, -20, new IntPtr(GetWindowLongPtrW(handle, -20).ToInt64() | 0x08000080));
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(OnNativeMessage);
            LiquidGlassChrome.Apply(this, AppTheme.GlassMist);
        };
        Deactivated += (_, _) => { if (!_closed && !_closing) Close(); };
        Closed += (_, _) =>
        {
            _closed = true;
            _inputMonitor?.Dispose();
            _inputMonitor = null;
            _source?.RemoveHook(OnNativeMessage);
        };
        PreviewKeyDown += OnMenuKeyDown;
    }

    internal IReadOnlyList<Button> Commands => _commands;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Destroying an active HWND emits Deactivated before Closed. Do not
        // re-enter Close from that notification while WPF is already closing.
        _closing = true;
        base.OnClosing(e);
        if (e.Cancel) _closing = false;
    }

    internal void ShowAt(Point pointer)
    {
        // Establish monitor DPI while still hidden, then measure/clamp the
        // complete menu before its first visible frame.
        _ = new WindowInteropHelper(this).EnsureHandle();
        _ = MonitorGeometryService.TryMoveWindow(this, pointer.X, pointer.Y);
        var surface = (FrameworkElement)Content;
        surface.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var scale = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(surface.DesiredSize.Width * scale.DpiScaleX);
        var height = (int)Math.Ceiling(surface.DesiredSize.Height * scale.DpiScaleY);
        var work = System.Windows.Forms.Screen.FromPoint(pointer).WorkingArea;
        var location = ConstrainToWorkArea(pointer, work, width, height);
        _ = MonitorGeometryService.TryMoveWindow(this, location.X, location.Y);
        Show(); // Preserve Explorer's overflow panel and its foreground window.
        _inputMonitor = new TrayMenuInputMonitor(new WindowInteropHelper(this).Handle, Dispatcher, Close, HandleMenuKey);
    }

    internal static Point ConstrainToWorkArea(Point pointer, System.Drawing.Rectangle work, int width, int height) => new(
        Math.Clamp(pointer.X - width, work.Left, Math.Max(work.Left, work.Right - width)),
        Math.Clamp(pointer.Y - height, work.Top, Math.Max(work.Top, work.Bottom - height)));

    private void OnMenuKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = KeyInterop.VirtualKeyFromKey(e.Key);
        if (!TrayMenuInputMonitor.IsMenuKey(key)) return;
        HandleMenuKey(key);
        e.Handled = true;
    }

    internal void HandleMenuKey(int key)
    {
        if (_closed || _closing) return;
        if (key == 0x1B) { Close(); return; }
        var enabled = _commands.Where(button => button.IsEnabled).ToArray();
        if (enabled.Length == 0) return;
        if (key is 0x0D or 0x20)
        {
            var command = enabled.FirstOrDefault(button => button.IsMouseOver) ?? enabled[Math.Clamp(_selectedIndex, 0, enabled.Length - 1)];
            command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return;
        }
        _selectedIndex = key switch
        {
            0x24 => 0, 0x23 => enabled.Length - 1,
            0x26 => _selectedIndex < 0 ? enabled.Length - 1 : (_selectedIndex - 1 + enabled.Length) % enabled.Length,
            _ => (_selectedIndex + 1) % enabled.Length,
        };
        foreach (var button in _commands) button.IsDefault = false;
        enabled[_selectedIndex].IsDefault = true; // Visual keyboard selection without SetFocus/Activate.
    }

    private IntPtr OnNativeMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x21) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE: hovering/clicking must not collapse the tray overflow.
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);

    private static Style CreateCommandStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AppControlForegroundBrush")));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = border }));
        foreach (var property in new[] { IsMouseOverProperty, Button.IsDefaultProperty })
        {
            var selected = new Trigger { Property = property, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("AppAccentMutedBrush")));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AppSelectedControlForegroundBrush")));
            style.Triggers.Add(selected);
        }
        return style;
    }
}
