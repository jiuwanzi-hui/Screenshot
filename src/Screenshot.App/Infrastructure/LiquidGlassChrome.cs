using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Core;
using Screenshot.App.Presentation;
using Brushes = System.Windows.Media.Brushes;

namespace Screenshot.App.Infrastructure;

internal static class LiquidGlassChrome
{
    private sealed class MaterialState
    {
        internal LiquidGlassHighlight? Highlight;
        internal bool ClearBackdrop;
        internal object? EditorBackground;
        internal bool HadEditorBackground;
        internal object? WindowBackground;
        internal bool HadWindowBackground;
        internal object? OriginalLocalBackground;
        internal string? OriginalBackgroundKey;
        internal int? OriginalBackdropType;
        internal int? OriginalCornerPreference;
        internal bool WatchingSelection;
        internal bool TextRefreshPending;
        internal AppTheme Theme;
        internal IDisposable? CaptionShell;
    }
    private static readonly ConditionalWeakTable<Window, MaterialState> States = new();
    internal static bool IsGlassTheme(AppTheme theme) => theme is AppTheme.GlassMist or AppTheme.GlassInk;

    internal static void ProtectText(TextBlock text, AppTheme theme)
    {
        var clear = IsClearUi(text, theme);
        var realBackdrop = Window.GetWindow(text) is { } owner && States.TryGetValue(owner, out var state) && state.ClearBackdrop;
        ClearGlassText.Apply(text, clear, realBackdrop);
    }

    internal static void ProtectGlyph(FrameworkElement element, AppTheme theme) =>
        ClearGlassRelief.ApplyControlPart(element, IsClearUi(element, theme));

    private static bool IsClearUi(FrameworkElement element, AppTheme theme)
    {
        if (theme != AppTheme.GlassMist) return false;
        var inCaptureToolbar = false;
        for (DependencyObject? parent = element; parent is not null; parent =
            (parent is Visual ? VisualTreeHelper.GetParent(parent) : null) ?? LogicalTreeHelper.GetParent(parent))
        {
            // Annotation content must keep its exact exported appearance.
            if (parent is Screenshot.App.Editor.ImageEditorCanvas) return false;
            if (parent is FrameworkElement { Name: "CaptureToolbar" }) inCaptureToolbar = true;
        }
        return inCaptureToolbar || (Window.GetWindow(element) is { } owner && States.TryGetValue(owner, out var state) &&
            state.Theme == AppTheme.GlassMist);
    }

    internal static void ProtectTextTree(DependencyObject root, AppTheme theme)
    {
        var visited = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.TryPop(out var element))
        {
            if (!visited.Add(element)) continue;
            if (element is TextBlock text) ProtectText(text, theme);
            else if (element is FrameworkElement glyph) ProtectGlyph(glyph, theme);
            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
                pending.Push(child);
            if (element is Visual)
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                    pending.Push(VisualTreeHelper.GetChild(element, index));
        }
    }

    internal static void Apply(Window window, AppTheme theme)
    {
        var type = window.GetType().Name;
        var originalSurface = type is "MainWindow" or "ImageEditorWindow" or "VideoPostProcessWindow" or
            "RecognitionResultPopupWindow" or "TextTranslationWindow" or "OcrResultWindow" or
            "PinnedImageWindow" or "PinnedImageEditorToolbarWindow" or "VideoRecordingControlWindow";
        var clearOnlySurface = type is "TranslationProfileEditorWindow" or "CapturePreviewWindow" or "CaptureHistoryWindow" or
            "ImageCropWindow" or "ContentRecognitionWindow" or "PinnedEditorCloseDialog" or
            "ThemeColorPickerWindow" or "StartupFeedbackWindow" or "CaptureFeedbackWindow" or "GlassTrayMenuWindow" or
            "HistoryRetentionDialog" or "PrivacyRedactionWindow" or "PresetCaptureWindow" or "PresetCaptureExecuteWindow";
        // Capture only receives UI glyph relief; never change its native
        // overlay, capture exclusion, mask, border or composition settings.
        if (type == "CaptureOverlayWindow")
        {
            ProtectTextTree(window, theme);
            return;
        }
        if (!originalSurface && !clearOnlySurface) return;
        var handle = new WindowInteropHelper(window).Handle;
        var state = States.GetOrCreateValue(window);
        if (clearOnlySurface && theme != AppTheme.GlassMist && state.Theme != AppTheme.GlassMist) return;
        state.Theme = theme;
        // Native captions cannot share the alpha-composited client glass.
        // Wrap only ordinary captioned dialogs, and restore their original
        // content/chrome when switching away from the clear theme.
        if (clearOnlySurface && theme == AppTheme.GlassMist && state.CaptionShell is null &&
            window.WindowStyle != WindowStyle.None && !window.AllowsTransparency)
        {
            state.CaptionShell = WindowShell.Attach(window, window.Title);
        }
        else if (theme != AppTheme.GlassMist && state.CaptionShell is not null)
        {
            state.Highlight?.Dispose();
            state.Highlight = null;
            state.CaptionShell.Dispose();
            state.CaptionShell = null;
        }
        if (!state.WatchingSelection)
        {
            state.WatchingSelection = true;
            void RefreshText()
            {
                if (state.Theme != AppTheme.GlassMist || state.TextRefreshPending) return;
                state.TextRefreshPending = true;
                _ = window.Dispatcher.BeginInvoke(() =>
                {
                    state.TextRefreshPending = false;
                    ProtectTextTree(window, state.Theme);
                });
            }
            window.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => RefreshText()));
        }
        if (handle != IntPtr.Zero && !window.AllowsTransparency &&
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            if (clearOnlySurface && theme == AppTheme.GlassMist && !state.ClearBackdrop)
            {
                if (DwmGetWindowAttribute(handle, 38, out var oldBackdrop, sizeof(int)) == 0) state.OriginalBackdropType = oldBackdrop;
                if (DwmGetWindowAttribute(handle, 33, out var oldCorner, sizeof(int)) == 0) state.OriginalCornerPreference = oldCorner;
            }
            var backdrop = clearOnlySurface && theme != AppTheme.GlassMist ? state.OriginalBackdropType ?? 0 : 1;
            _ = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
            var round = clearOnlySurface && theme != AppTheme.GlassMist ? state.OriginalCornerPreference ?? 0 : 2;
            _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        }
        if (!window.AllowsTransparency)
        {
            var clear = theme == AppTheme.GlassMist && ClearGlassBackdrop.SetEnabled(handle, true);
            if (!clear && state.ClearBackdrop) _ = ClearGlassBackdrop.SetEnabled(handle, false);
            if (clear)
            {
                if (clearOnlySurface && !state.ClearBackdrop)
                {
                    state.OriginalLocalBackground = window.ReadLocalValue(Window.BackgroundProperty);
                    state.OriginalBackgroundKey = ReferenceEquals(window.TryFindResource("AppWindowBackgroundBrush"), window.Background)
                        ? "AppWindowBackgroundBrush"
                        : ReferenceEquals(window.TryFindResource("AppPanelBackgroundBrush"), window.Background) ? "AppPanelBackgroundBrush"
                        : ReferenceEquals(window.TryFindResource("AppBackgroundBrush"), window.Background) ? "AppBackgroundBrush" : null;
                }
                window.Background = Brushes.Transparent;
                if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                    target.BackgroundColor = Colors.Transparent;
                if (!state.ClearBackdrop)
                {
                    state.HadEditorBackground = window.Resources.Contains("ImageEditorWindowBackgroundBrush");
                    state.EditorBackground = window.Resources["ImageEditorWindowBackgroundBrush"];
                    state.HadWindowBackground = window.Resources.Contains("AppWindowBackgroundBrush");
                    state.WindowBackground = window.Resources["AppWindowBackgroundBrush"];
                }
                window.Resources["ImageEditorWindowBackgroundBrush"] = Brushes.Transparent;
                // Inner grids must not repaint the opaque fallback.
                window.Resources["AppWindowBackgroundBrush"] = Brushes.Transparent;
            }
            else
            {
                window.SetResourceReference(Window.BackgroundProperty, "AppWindowBackgroundBrush");
                if (state.ClearBackdrop && ReferenceEquals(window.Resources["ImageEditorWindowBackgroundBrush"], Brushes.Transparent))
                {
                    if (state.HadEditorBackground)
                        window.Resources["ImageEditorWindowBackgroundBrush"] = state.EditorBackground;
                    else window.Resources.Remove("ImageEditorWindowBackgroundBrush");
                }
                state.EditorBackground = null;
                if (state.ClearBackdrop && ReferenceEquals(window.Resources["AppWindowBackgroundBrush"], Brushes.Transparent))
                {
                    if (state.HadWindowBackground) window.Resources["AppWindowBackgroundBrush"] = state.WindowBackground;
                    else window.Resources.Remove("AppWindowBackgroundBrush");
                }
                state.WindowBackground = null;
                if (clearOnlySurface && state.ClearBackdrop)
                {
                    if (state.OriginalLocalBackground == DependencyProperty.UnsetValue) window.ClearValue(Window.BackgroundProperty);
                    else if (state.OriginalBackgroundKey is not null) window.SetResourceReference(Window.BackgroundProperty, state.OriginalBackgroundKey);
                    else if (state.OriginalLocalBackground is System.Windows.Media.Brush original) window.Background = original;
                    state.OriginalLocalBackground = null;
                    state.OriginalBackgroundKey = null;
                }
            }
            state.ClearBackdrop = clear;
        }
        var enabled = theme == AppTheme.GlassMist || (originalSurface && theme == AppTheme.GlassInk);
        if (window.AllowsTransparency)
        {
            // Independent layered windows have no backing visual. Provide
            // their own optical background so text stays legible on any app.
            foreach (var key in new[] { "AppGlassBackgroundBrush", "AppPanelBackgroundBrush" })
            {
                if (theme == AppTheme.GlassMist) window.Resources[key] = window.TryFindResource("AppFloatingSurfaceBrush");
                else if (originalSurface && theme == AppTheme.GlassInk) window.Resources[key] = LiquidGlassMaterial.CreateFloatingBackground(true);
                else window.Resources.Remove(key);
            }
        }
        if (enabled && state.Highlight is null)
        {
            // Highlight stays under the controls, inside the existing shell.
            var shell = window.Content as Border ?? (window.Content as Grid)?.Children.OfType<Border>().FirstOrDefault();
            if (shell?.Child is Grid grid)
            {
                state.Highlight = new LiquidGlassHighlight(window);
                Grid.SetRowSpan(state.Highlight, Math.Max(1, grid.RowDefinitions.Count));
                Grid.SetColumnSpan(state.Highlight, Math.Max(1, grid.ColumnDefinitions.Count));
                grid.Children.Insert(0, state.Highlight);
            }
        }
        state.Highlight?.SetTheme(enabled, theme == AppTheme.GlassInk);
        ProtectTextTree(window, theme);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
