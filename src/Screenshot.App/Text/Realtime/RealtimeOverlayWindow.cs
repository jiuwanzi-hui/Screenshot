using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Core;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;
using Screenshot.App.Editor;
using Border = System.Windows.Controls.Border;

namespace Screenshot.App.Text.Realtime;

internal sealed class RealtimeOverlayWindow : Window
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Grid _surface = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Border _outline = new() { BorderThickness = new Thickness(2), SnapsToDevicePixels = true };
    private readonly RealtimeStatusWindow _status = new();
    private bool _hasFailures;
    private bool _reading;
    internal RealtimeStatusWindow StatusBadge => _status;
    internal RealtimeOverlayWindow()
    {
        Title = "SnapCut 实时译文"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        ShowActivated = false; Focusable = false; Topmost = true;
        _surface.Children.Add(_canvas);
        _surface.Children.Add(_outline);
        Content = _surface;
        SourceInitialized += (_, _) => Configure(new WindowInteropHelper(this).Handle, true);
        Closed += (_, _) => _status.Close();
    }

    internal void ClearTranslation() { _canvas.Children.Clear(); _hasFailures = false; _reading = false; }

    internal void PresentStatus(ScreenRegion region, string status, RealtimeTranslationSettings options)
    {
        Position(region);
        _status.Present(region, status, _hasFailures || status.Contains("失败", StringComparison.Ordinal) ||
            status.Contains("停止", StringComparison.Ordinal) && !status.Contains("再次", StringComparison.Ordinal), _reading);
    }

    private void Position(ScreenRegion bounds, nint controlWindow = 0)
    {
        // Position the HWND before showing it. Native coordinates establish the
        // monitor DPI; layout below uses the corresponding WPF units.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _ = SetWindowPos(hwnd, controlWindow == 0 ? new nint(-1) : controlWindow, bounds.X, bounds.Y,
            bounds.Width, bounds.Height, 0x0010);
        if (!IsVisible) Show();
        _ = SetWindowPos(hwnd, controlWindow == 0 ? new nint(-1) : controlWindow, bounds.X, bounds.Y,
            bounds.Width, bounds.Height, 0x0010 | 0x0040);
        _outline.BorderBrush = TryFindResource("AppAccentBrush") as Brush ?? Brushes.DeepSkyBlue;
    }

    internal void Present(RealtimeTranslationUpdate update, RealtimeTranslationSettings options, bool original, nint controlWindow = 0)
    {
        ClearTranslation();
        if (update.Bounds.IsEmpty) return;
        Position(update.Bounds, controlWindow);
        _hasFailures = update.HasFailures; _reading = update.IsReadingSnapshot || update.ReadingBlocks.Count > 0;
        _status.Present(update.Bounds, update.Status, _hasFailures, _reading);
        if (original) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = update.Bounds.Width / dpi.DpiScaleX;
        var height = update.Bounds.Height / dpi.DpiScaleY;
        var blocks = update.Blocks.Where(block => !string.IsNullOrWhiteSpace(block.Translation) &&
            !string.Equals(block.Region.Text.Trim(), block.Translation.Trim(), StringComparison.Ordinal)).ToArray();
        if (options.Display == RealtimeTranslationDisplay.Subtitle || update.IsReadingSnapshot)
        {
            AddReadingBox(blocks.Concat(update.ReadingBlocks).ToArray(), options, width, height);
        }
        else
        {
            foreach (var block in blocks)
            {
                var x = block.Region.X * width / update.PixelWidth;
                var y = block.Region.Y * height / update.PixelHeight;
                var bounds = Rect.Intersect(new Rect(0, 0, width, height), new Rect(Math.Max(0, x - 2), Math.Max(0, y - 1),
                    block.Region.Width * width / update.PixelWidth + 4, block.Region.Height * height / update.PixelHeight + 2));
                if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4) continue;
                var box = CreateInlineBox(block, bounds, options.FontSize / 18 * height / update.PixelHeight);
                Canvas.SetLeft(box, bounds.X);
                Canvas.SetTop(box, bounds.Y);
                _canvas.Children.Add(box);
            }
            if (update.ReadingBlocks.Count > 0) AddReadingBox(update.ReadingBlocks, options, width, height);
        }
    }

    private void AddReadingBox(IReadOnlyList<RealtimeTranslationBlock> blocks, RealtimeTranslationSettings options, double width, double height)
    {
        var text = string.Join("  ", blocks.Where(block => !string.IsNullOrWhiteSpace(block.Translation) && block.Translation != block.Region.Text)
            .Select(block => block.Translation));
        if (text.Length == 0) return;
        var box = CreateBox(text, options, Math.Max(24, Math.Min(width - 16, 900)));
        var layout = TranslationTextLayout.LayoutParagraph(new(new Rect(0, 0, box.Width - 8, Math.Max(32, height * .45)), text, options.FontSize));
        var content = (TextBlock)box.Child;
        content.Text = string.Join(Environment.NewLine, layout.Lines.Select(line => line.Text));
        content.FontSize = layout.FontSize; content.LineHeight = layout.LineHeight;
        content.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        box.Measure(new Size(width, height));
        Canvas.SetLeft(box, Math.Max(0, (width - box.DesiredSize.Width) * options.SubtitleX));
        Canvas.SetTop(box, Math.Max(0, (height - box.DesiredSize.Height) * options.SubtitleY));
        _canvas.Children.Add(box);
    }

    internal static Border CreateInlineBox(RealtimeTranslationBlock block, Rect bounds, double fontScale)
    {
        var layout = TranslationTextLayout.LayoutParagraph(new(bounds,
            TranslationPresentationLayout.NormalizeTranslatedText(block.Region.Text, block.Translation),
            Math.Max(8, block.Region.EstimatedFontSize > 0 ? block.Region.EstimatedFontSize * fontScale : 18 * fontScale)));
        static SolidColorBrush Brush(int argb) => new(Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        var palette = block.Appearance ?? new RealtimeTextAppearance(unchecked((int)0xff232a2e), unchecked((int)0xfff8fbfb));
        var text = new TextBlock { Text = string.Join(Environment.NewLine, layout.Lines.Select(line => line.Text)), Tag = "UserContent",
            FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = layout.FontSize,
            Foreground = Brush(palette.Foreground), TextWrapping = TextWrapping.NoWrap,
            LineHeight = layout.LineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            ClipToBounds = true };
        return new Border { Width = bounds.Width, Height = bounds.Height, Padding = new Thickness(2, 1, 2, 1),
            Background = Brush(palette.Background), Child = text, ClipToBounds = true, IsHitTestVisible = false };
    }

    private Border CreateBox(string text, RealtimeTranslationSettings options, double width)
    {
        var foreground = TryFindResource("AppTextPrimaryBrush") as Brush ?? Brushes.White;
        var background = (TryFindResource("AppFloatingSurfaceBrush") as Brush ?? Brushes.Black).Clone();
        // Glass palette brushes already contain alpha. Do not multiply that
        // translucency into the user's requested translation opacity: original
        // letters underneath would bleed through at almost full contrast.
        if (background is SolidColorBrush solid)
            solid.Color = Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B);
        else if (background is GradientBrush gradient)
            foreach (var stop in gradient.GradientStops)
                stop.Color = Color.FromRgb(stop.Color.R, stop.Color.G, stop.Color.B);
        background.Opacity = options.BackgroundOpacity;
        var textBlock = new TextBlock { Text = text, Tag = "UserContent", FontSize = options.FontSize, Foreground = foreground,
            TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Microsoft YaHei UI") };
        return new Border { Child = textBlock, Padding = new Thickness(6, 3, 6, 3), Width = width,
            CornerRadius = new CornerRadius(5), Background = background,
            BorderThickness = new Thickness(1), BorderBrush = TryFindResource("AppBorderBrush") as Brush };
    }

    internal static void Configure(nint hwnd, bool clickThrough)
    {
        OverlayShellBehavior.Apply(hwnd);
        var style = GetWindowLongPtr(hwnd, -20).ToInt64() | 0x08000000L | 0x80;
        if (clickThrough) style |= 0x20;
        _ = SetWindowLongPtr(hwnd, -20, new nint(style));
        // This is user-visible content, including in remote desktops and
        // recordings. The recognition source captures the underlying HWND,
        // so hiding this window from every screen consumer is unnecessary.
        if (!SetWindowDisplayAffinity(hwnd, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法显示实时译文窗口。");
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
