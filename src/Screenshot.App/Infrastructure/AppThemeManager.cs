using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Screenshot.App.Core;
using WpfApplication = System.Windows.Application;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfColors = System.Windows.Media.Colors;

namespace Screenshot.App.Infrastructure;

internal readonly record struct ThemeChromeColors(
    bool IsDark,
    string Background,
    string Foreground,
    string Selection,
    string Border);

public sealed class AppThemeManager : IDisposable
{
    private AppTheme _resolvedTheme = AppTheme.AuroraMist;
    private bool _disposed;
    private bool _paletteApplied;
    private readonly bool _residentShell;
    private DispatcherOperation? _pendingChromeRefresh;

    public AppTheme ResolvedTheme => _resolvedTheme;

    public event EventHandler<AppTheme>? ThemeChanged;

    public static void ApplySettingsPalette(
        ResourceDictionary resources,
        AppTheme theme,
        bool residentShell = false)
    {
        ArgumentNullException.ThrowIfNull(resources);
        // Publish a complete palette once. Updating a live dictionary one key
        // at a time repeatedly invalidates every DynamicResource in the window.
        var palette = new ResourceDictionary();
        ApplyPalette(palette, GetPalette(theme), residentShell);
        resources.BeginInit();
        try
        {
            foreach (System.Collections.DictionaryEntry entry in palette)
                resources[entry.Key] = entry.Value;
        }
        finally { resources.EndInit(); }
    }

    internal static void ApplyWindowPalette(Window window, AppTheme theme)
    {
        ApplySettingsPalette(window.Resources, theme);
        // A per-window palette refresh follows the application theme event.
        // Reapply the material last so its transparent surface overrides are
        // not replaced by the palette's opaque fallback until the next Show.
        LiquidGlassChrome.Apply(window, theme);
    }

    public AppThemeManager(bool residentShell = false)
    {
        _residentShell = residentShell;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.TextBlock),
            FrameworkElement.LoadedEvent, new RoutedEventHandler(OnTextLoaded));
        EventManager.RegisterClassHandler(typeof(System.Windows.Shapes.Path),
            FrameworkElement.LoadedEvent, new RoutedEventHandler(OnGlyphLoaded));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ScrollViewer),
            FrameworkElement.LoadedEvent, new RoutedEventHandler(OnGlyphLoaded));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ToggleButton),
            System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(OnControlSelectionChanged));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ToggleButton),
            System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(OnControlSelectionChanged));
    }

    public void Apply(AppTheme configuredTheme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ApplyResolvedTheme(AppSettings.NormalizeTheme(configuredTheme));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pendingChromeRefresh?.Abort();
        _pendingChromeRefresh = null;
    }

    internal static AppTheme ResolveTheme(AppTheme configuredTheme, bool systemUsesLightTheme)
    {
        _ = systemUsesLightTheme;
        return AppSettings.NormalizeTheme(configuredTheme);
    }

    internal static bool IsDarkTheme(AppTheme theme)
    {
        return GetPalette(theme).IsDark;
    }

    internal static string GetSelectionBorderColor(AppTheme theme) => GetPalette(theme).AccentStart;

    internal static ThemeChromeColors GetChromeColors(AppTheme theme)
    {
        var palette = GetPalette(theme);
        var accent = ParseColor(palette.AccentStart);
        var selection = palette.IsDark
            ? Blend(accent, ParseColor(palette.PanelStart), 0.80)
            : Blend(accent, WpfColors.White, 0.80);
        return new ThemeChromeColors(
            palette.IsDark,
            ToHex(ParseColor(palette.PanelStart)),
            palette.ControlForeground,
            ToHex(selection),
            ToHex(ParseColor(palette.Border)));
    }

    private static string ToHex(WpfColor color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void ApplyResolvedTheme(AppTheme theme)
    {
        if (_disposed) return;
        var application = WpfApplication.Current;
        if (application is null)
        {
            _resolvedTheme = theme;
            return;
        }

        if (!application.Dispatcher.CheckAccess())
        {
            _ = application.Dispatcher.BeginInvoke(() => ApplyResolvedTheme(theme));
            return;
        }

        if (_paletteApplied && _resolvedTheme == theme) return;
        _resolvedTheme = theme;
        _paletteApplied = true;

        ApplySettingsPalette(application.Resources, theme, _residentShell);

        // Window-local resources must be current before the single chrome/text
        // pass below; otherwise settings are refreshed twice with stale ink.
        ThemeChanged?.Invoke(this, theme);

        // Resource bindings and WindowChrome can update the native surface in
        // the pending layout/render pass. Commit the backdrop after that pass,
        // or its transparent target can be replaced until the next window Show.
        // Coalesce rapid theme selections; never walk the text tree twice or
        // retain a snapshot of windows that may close before this callback.
        _pendingChromeRefresh?.Abort();
        _pendingChromeRefresh = application.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _pendingChromeRefresh = null;
            if (_disposed) return;
            foreach (Window window in application.Windows)
                ApplyWindowChromeTheme(window, _resolvedTheme);
        }));

    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (!_disposed && _pendingChromeRefresh is null && sender is Window window &&
            ReferenceEquals(sender, e.OriginalSource))
        {
            ApplyWindowChromeTheme(window, _resolvedTheme);
        }
    }

    private void OnTextLoaded(object sender, RoutedEventArgs e)
    {
        if (!_disposed && sender is System.Windows.Controls.TextBlock text)
            LiquidGlassChrome.ProtectText(text, _resolvedTheme);
    }

    private void OnGlyphLoaded(object sender, RoutedEventArgs e)
    {
        if (!_disposed && sender is FrameworkElement element && ReferenceEquals(sender, e.OriginalSource))
            LiquidGlassChrome.ProtectGlyph(element, _resolvedTheme);
    }

    private void OnControlSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_disposed && _resolvedTheme == AppTheme.GlassMist && sender is FrameworkElement { IsLoaded: true } control &&
            ReferenceEquals(sender, e.OriginalSource))
            _ = control.Dispatcher.BeginInvoke(() => LiquidGlassChrome.ProtectTextTree(control, _resolvedTheme));
    }

    private static void ApplyWindowChromeTheme(Window window, AppTheme theme)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var useDarkMode = IsDarkTheme(theme) ? 1 : 0;
            _ = DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkMode,
                ref useDarkMode,
                Marshal.SizeOf<int>());
            LiquidGlassChrome.Apply(window, theme);
        }
        catch (DllNotFoundException)
        {
            // Older Windows versions do not expose DWM attributes.
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows versions do not expose DwmSetWindowAttribute.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    private const int DwmwaUseImmersiveDarkMode = 20;

    private static void SetSettingsColor(
        ResourceDictionary resources,
        string key,
        string color)
    {
        var brush = new SolidColorBrush(
            (WpfColor)WpfColorConverter.ConvertFromString(color));
        brush.Freeze();
        resources[key] = brush;
    }

    private static void SetSettingsBrush(
        ResourceDictionary resources,
        string key,
        WpfColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static void ApplyPalette(
        ResourceDictionary resources,
        ThemePalette palette,
        bool residentShell = false)
    {
        var colors = palette.IsDark ? DarkColors : LightColors;
        var isTonalTheme = palette != GlassMistPalette && palette != GlassInkPalette;
        foreach (var (key, color) in colors)
        {
            SetSettingsColor(resources, key, color);
        }

        resources["AppWindowBackgroundBrush"] = CreateGradientBrush(palette.WindowStart, palette.WindowEnd);
        resources["AppGlassBackgroundBrush"] = CreateGradientBrush(palette.GlassStart, palette.GlassEnd);
        resources["AppSidebarBackgroundBrush"] = CreateGradientBrush(palette.SidebarStart, palette.SidebarEnd);
        resources["AppPanelBackgroundBrush"] = CreateGradientBrush(palette.PanelStart, palette.PanelEnd);
        resources["AppFloatingSurfaceBrush"] = resources["AppPanelBackgroundBrush"];
        resources["AppSettingsTabBackgroundBrush"] = resources["AppPanelBackgroundBrush"];
        // A WPF Popup is a separate layered HWND, without the owner's DWM
        // backdrop. Give it its own opaque material so text below cannot bleed.
        resources["AppPopupBackgroundBrush"] = CreateGradientBrush(
            ToHex(ParseColor(palette.PanelStart)), ToHex(ParseColor(palette.PanelEnd)));
        // Native selection badges use a colour-keyed GDI surface. Keep their
        // fill opaque and identical to the WPF badge, independently of menus.
        resources["AppSizeBadgeBackgroundBrush"] = resources["AppPopupBackgroundBrush"];
        resources["AppInputBackgroundBrush"] = CreateGradientBrush(palette.InputStart, palette.InputEnd);
        SetSettingsColor(resources, "AppHeaderBackgroundBrush",
            palette == GlassInkPalette ? "#08FFFFFF" : palette.GlassStart);
        SetSettingsColor(resources, "AppGlassEdgeBrush", palette.Border);
        resources["ToolbarDesignerSurfaceBrush"] = resources["AppPanelBackgroundBrush"];
        resources["ToolbarDesignerVisibleBackgroundBrush"] = resources["EditorToolbarButtonSelectedBackgroundBrush"];

        if (palette == GlassInkPalette)
            resources["AppGlassEdgeBrush"] = CreateGradientBrush("#65CFE5FF", "#163E547D");
        SetSettingsColor(resources, "AppBorderBrush", palette.Border);
        resources["AppControlEdgeBrush"] = resources["AppBorderBrush"];
        resources["AppControlCornerRadius"] = new CornerRadius(8);
        resources["EditorToolbarButtonCornerRadius"] = new CornerRadius(8);
        resources["AppControlHighlightBrush"] = System.Windows.Media.Brushes.Transparent;
        resources["AppControlReflectionBrush"] = System.Windows.Media.Brushes.Transparent;
        resources["AppLiquidLensVisibility"] = Visibility.Collapsed;
        // Selection movement is shared by all themes; glass optics remain material-specific.
        resources["AppSelectionMotionVisibility"] = Visibility.Visible;
        resources["AppButtonHoverOpacity"] = .92;
        resources["AppButtonPressedOpacity"] = .78;
        resources["AppInputFocusBrush"] = resources["AppInputBackgroundBrush"];
        SetSettingsColor(resources, "AppSubtleBorderBrush", palette.SubtleBorder);
        SetSettingsColor(resources, "AppTextPrimaryBrush", palette.TextPrimary);
        SetSettingsColor(resources, "AppReadingForegroundBrush", palette.TextPrimary);
        SetSettingsColor(resources, "AppTextSecondaryBrush", palette.TextSecondary);
        SetSettingsColor(resources, "AppNavigationForegroundBrush", palette == GlassMistPalette ? "#FFFFFF" : palette.TextSecondary);
        SetSettingsColor(resources, "AppCaptionForegroundBrush", palette == GlassMistPalette ? "#FFFFFF" : palette.TextPrimary);
        SetSettingsColor(resources, "AppMutedTextBrush", palette.TextSecondary);
        SetSettingsColor(resources, "AppControlForegroundBrush", palette.ControlForeground);
        SetSettingsColor(resources, "AppAccentTextBrush", palette.AccentStart);
        SetSettingsColor(resources, "AppOnAccentBrush", "#FFFFFF");
        resources["AppSelectedControlForegroundBrush"] = resources["AppControlForegroundBrush"];
        SetSettingsColor(
            resources,
            "AppDangerBrush",
            palette.IsDark ? "#FF9B8F" : "#B42318");
        SetSettingsColor(resources, "AppSeparatorBrush", palette.Separator);
        SetSettingsColor(resources, "AppTooltipBackgroundBrush", palette.TooltipBackground);
        SetSettingsColor(resources, "AppTooltipForegroundBrush", palette.TooltipForeground);
        SetSettingsColor(resources, "AppWarmAccentBrush", palette.WarmAccent);

        resources["ImageEditorWindowBackgroundBrush"] = CreateGradientBrush(palette.WindowStart, palette.WindowEnd);
        resources["ImageEditorShellBackgroundBrush"] = CreateGradientBrush(palette.PanelStart, palette.PanelEnd);
        resources["ImageEditorToolbarBackgroundBrush"] = CreateGradientBrush(palette.InputStart, palette.InputEnd);
        resources["ImageEditorToolbarPanelBackgroundBrush"] = System.Windows.Media.Brushes.Transparent;
        resources["ImageEditorToolbarPanelBorderThickness"] = new Thickness(0, 0, 0, 1);
        resources["ImageEditorToolbarPanelCornerRadius"] = new CornerRadius(0);
        SetSettingsColor(resources, "ImageEditorShellBorderBrush", palette.Border);
        SetSettingsColor(resources, "ImageEditorToolbarBorderBrush", palette.SubtleBorder);
        SetSettingsColor(resources, "ImageEditorTitleBrush", palette.TextPrimary);
        SetSettingsColor(resources, "ImageEditorMutedTextBrush", palette.TextSecondary);
        SetSettingsColor(resources, "ImageEditorSecondaryTextBrush", palette.ControlForeground);
        SetSettingsColor(resources, "ImageEditorSliderTrackBrush", palette.SubtleBorder);
        SetSettingsColor(resources, "ImageEditorSliderThumbBorderBrush", palette.IsDark ? "#EAF8F5" : "#FFFFFF");

        SetSettingsColor(resources, "EditorToolbarButtonBackgroundBrush", "#00000000");
        SetSettingsColor(resources, "EditorToolbarButtonBorderBrush", "#00000000");
        SetSettingsColor(resources, "EditorToolbarIconBrush", palette.ControlForeground);
        resources["EditorToolbarButtonHoverBackgroundBrush"] = CreateGradientBrush(
            palette.PanelStart,
            palette.PanelEnd);
        SetSettingsColor(resources, "ImageEditorToolbarButtonBackgroundBrush", "#00000000");
        SetSettingsColor(resources, "ImageEditorToolbarButtonBorderBrush", "#00000000");
        SetSettingsColor(resources, "ImageEditorToolbarIconBrush", palette.ControlForeground);
        resources["ImageEditorToolbarButtonHoverBackgroundBrush"] = CreateGradientBrush(
            palette.PanelStart,
            palette.PanelEnd);
        resources["ImageEditorWorkspaceBackgroundBrush"] = CreateGradientBrush(
            palette.WindowEnd,
            palette.PanelEnd);
        SetSettingsColor(resources, "ImageEditorWorkspaceBorderBrush", palette.SubtleBorder);
        resources["ImageEditorViewportBackgroundBrush"] = CreateGradientBrush(
            palette.InputEnd,
            palette.PanelStart);
        SetSettingsColor(resources, "ImageEditorViewportBorderBrush", palette.Border);
        SetSettingsColor(resources, "ImageEditorSwatchBorderBrush", palette.Border);
        SetSettingsColor(resources, "ImageEditorSwatchHoverBorderBrush", palette.ControlForeground);

        var accent = ParseColor(palette.AccentStart);
        var accentEnd = ParseColor(palette.AccentEnd);
        var mutedStart = palette.IsDark
            ? WpfColor.FromArgb(isTonalTheme ? (byte)0x33 : (byte)0x72, accent.R, accent.G, accent.B)
            : Blend(accent, WpfColors.White, 0.78);
        var mutedEnd = palette.IsDark
            ? WpfColor.FromArgb(isTonalTheme ? (byte)0x33 : (byte)0x72, accentEnd.R, accentEnd.G, accentEnd.B)
            : Blend(accentEnd, WpfColors.White, 0.86);
        var foreground = palette.IsDark
            ? Blend(accent, WpfColors.White, 0.72)
            : Blend(accent, WpfColors.Black, 0.38);
        var selectedStart = palette.IsDark
            ? isTonalTheme ? Blend(accent, ParseColor(palette.PanelStart), 0.80) : Blend(accent, WpfColors.Black, 0.18)
            : Blend(accent, WpfColors.White, 0.72);
        var selectedEnd = palette.IsDark
            ? isTonalTheme ? Blend(accentEnd, ParseColor(palette.PanelEnd), 0.80) : Blend(accentEnd, WpfColors.Black, 0.12)
            : Blend(accentEnd, WpfColors.White, 0.76);

        SetSettingsBrush(resources, "SelectionBorderBrush", accent);
        resources["AppAccentBrush"] = CreateGradientBrush(accent, accentEnd);
        resources["AppAccentMutedBrush"] = CreateGradientBrush(mutedStart, mutedEnd);
        resources["AppCaptionHoverBrush"] = palette == GlassMistPalette
            ? resources["AppAccentBrush"] : resources["AppAccentMutedBrush"];
        SetSettingsBrush(resources, "AppAccentForegroundBrush", foreground);
        resources["AppSelectedEdgeBrush"] = resources["AppAccentBrush"];
        SetSettingsBrush(resources, "EditorToolbarButtonHoverBorderBrush", accent);
        resources["EditorToolbarButtonPressedBackgroundBrush"] =
            CreateGradientBrush(mutedStart, mutedEnd);
        SetSettingsBrush(resources, "ImageEditorToolbarButtonHoverBorderBrush", accent);
        resources["ImageEditorToolbarButtonPressedBackgroundBrush"] =
            CreateGradientBrush(mutedStart, mutedEnd);
        resources["EditorToolbarButtonSelectedBackgroundBrush"] =
            CreateGradientBrush(selectedStart, selectedEnd);
        SetSettingsBrush(resources, "EditorToolbarButtonSelectedBorderBrush", accent);
        resources["EditorToolbarConfirmBackgroundBrush"] =
            CreateGradientBrush(selectedStart, selectedEnd);
        SetSettingsBrush(resources, "EditorToolbarConfirmBorderBrush", accent);
        SetSettingsBrush(resources, "EditorToolbarConfirmIconBrush", palette.IsDark ? foreground : accent);
        resources["ImageEditorToolbarButtonSelectedBackgroundBrush"] =
            CreateGradientBrush(selectedStart, selectedEnd);
        SetSettingsBrush(resources, "ImageEditorToolbarButtonSelectedBorderBrush", accent);
        SetSettingsBrush(
            resources,
            "ImageEditorToolbarSelectedIconBrush",
            palette.IsDark ? WpfColors.White : GetContrastColor(selectedStart));
        resources["AppMenuCheckedForegroundBrush"] = resources["ImageEditorToolbarSelectedIconBrush"];

        if (isTonalTheme)
        {
            // Shared frozen vector paint adds broad colour and quiet folds;
            // no bitmap textures, effect passes or background animation.
            var backdrop = CreateTonalBackground(palette);
            resources["AppWindowBackgroundBrush"] = backdrop;
            resources["AppGlassBackgroundBrush"] = backdrop;
            resources["ImageEditorWindowBackgroundBrush"] = backdrop;
            resources["ToolbarDesignerVisibleBackgroundBrush"] = resources["EditorToolbarButtonSelectedBackgroundBrush"];
            resources["AppSettingsTabBackgroundBrush"] = System.Windows.Media.Brushes.Transparent;
            resources["AppControlCornerRadius"] = new CornerRadius(10);
            resources["AppControlHighlightBrush"] = IsMattePalette(palette)
                ? System.Windows.Media.Brushes.Transparent
                : CreateGradientBrush(palette.IsDark ? "#20FFF5EA" : "#B0FFFFFF", "#00FFFFFF");
            SetSettingsBrush(resources, "AppOnAccentBrush", GetAccentContrastColor(accent, accentEnd));
            SetSettingsBrush(resources, "AppAccentTextBrush", foreground);
        }

        if (palette == GlassInkPalette)
        {
            System.Windows.Media.Brush background = residentShell
                ? Presentation.LiquidGlassMaterial.CreateVectorBackground(palette.IsDark)
                : Presentation.LiquidGlassMaterial.CreateBackground(palette.IsDark);
            System.Windows.Media.Brush glass = residentShell
                ? Presentation.LiquidGlassMaterial.CreateVectorBackground(palette.IsDark, glassLayer: true)
                : Presentation.LiquidGlassMaterial.CreateBackground(palette.IsDark, glassLayer: true);
            resources["AppWindowBackgroundBrush"] = background;
            resources["AppFloatingSurfaceBrush"] = residentShell ? background : Presentation.LiquidGlassMaterial.CreateFloatingBackground(palette.IsDark);
            resources["AppGlassBackgroundBrush"] = glass;
            // Secondary labels also need sufficient contrast across the
            // brightest/darkest parts of the optical texture.
            SetSettingsColor(resources, "AppTextSecondaryBrush", palette.IsDark ? "#D6E5ED" : "#281F30");
            SetSettingsColor(resources, "AppMutedTextBrush", palette.IsDark ? "#D6E5ED" : "#281F30");
            resources["AppGlassEdgeBrush"] = Presentation.LiquidGlassMaterial.CreateEdge(palette.IsDark);
            resources["ImageEditorWindowBackgroundBrush"] = background;
            resources["ImageEditorShellBackgroundBrush"] = glass;
            // Floating toolbars need their own readable base over arbitrary
            // desktop content. Only the window's glass layer is translucent.
            resources["ImageEditorToolbarBackgroundBrush"] = resources["AppFloatingSurfaceBrush"];
        }
        else if (palette == GlassMistPalette)
        {
            // One compositor backdrop; controls add only a local tint. Do not
            // multiply opaque cards or reduce the opacity of text and icons.
            SetSettingsColor(resources, "AppHeaderBackgroundBrush", "#08FFFFFF");
            resources["AppFloatingSurfaceBrush"] = Presentation.LiquidGlassMaterial.CreateClearSurface(floating: true);
            resources["AppPopupBackgroundBrush"] = CreateGradientBrush("#C8F8FAFB", "#C0E7ECEF");
            resources["ToolbarDesignerSurfaceBrush"] = resources["AppFloatingSurfaceBrush"];
            resources["ToolbarDesignerVisibleBackgroundBrush"] = CreateGradientBrush("#48FFFFFF", "#16E7ECEF");
            foreach (var key in new[] { "EditorToolbarButtonBackgroundBrush", "ImageEditorToolbarButtonBackgroundBrush" })
                resources[key] = CreateGradientBrush("#38FFFFFF", "#10E7ECEF");
            foreach (var key in new[] { "EditorToolbarButtonHoverBackgroundBrush", "ImageEditorToolbarButtonHoverBackgroundBrush" })
                resources[key] = CreateGradientBrush("#80FFFFFF", "#40E7ECEF");
            foreach (var key in new[] { "EditorToolbarButtonBorderBrush", "ImageEditorToolbarButtonBorderBrush" })
                resources[key] = Presentation.LiquidGlassMaterial.CreateEdge(false);
            resources["AppControlCornerRadius"] = new CornerRadius(11);
            resources["EditorToolbarButtonCornerRadius"] = new CornerRadius(11);
            resources["AppLiquidLensVisibility"] = Visibility.Visible;
            SetSettingsColor(resources, "AppReadingForegroundBrush", "#FFFFFF");
            resources["AppButtonHoverOpacity"] = 1d;
            resources["AppButtonPressedOpacity"] = 1d;
            resources["AppControlEdgeBrush"] = Presentation.LiquidGlassMaterial.CreateEdge(false);
            resources["AppControlHighlightBrush"] = CreateGradientBrush("#A8FFFFFF", "#12535F68");
            resources["AppControlReflectionBrush"] = Presentation.LiquidGlassMaterial.CreateClearSurface(floating: false);
            resources["AppInputFocusBrush"] = CreateGradientBrush("#A0F5F8FA", "#94DEE7EC");
            resources["AppAccentBrush"] = CreateGradientBrush("#D0FFFFFF", "#A8DCECF3");
            resources["AppAccentMutedBrush"] = CreateGradientBrush("#C8F8FDFF", "#A4DCECF3");
            SetSettingsColor(resources, "AppCaptionHoverBrush", "#24FFFFFF");
            resources["AppSelectedEdgeBrush"] = CreateGradientBrush("#FFFFFFFF", "#F073A2B8");
            SetSettingsColor(resources, "AppAccentForegroundBrush", "#101820");
            SetSettingsColor(resources, "AppAccentTextBrush", "#101820");
            SetSettingsColor(resources, "AppOnAccentBrush", "#101820");
            SetSettingsColor(resources, "AppSelectedControlForegroundBrush", "#101820");
            resources["AppMenuCheckedForegroundBrush"] = resources["AppSelectedControlForegroundBrush"];
            foreach (var key in new[] { "EditorToolbarButtonSelectedBackgroundBrush", "ImageEditorToolbarButtonSelectedBackgroundBrush", "EditorToolbarConfirmBackgroundBrush" })
                resources[key] = CreateGradientBrush("#A8FFFFFF", "#68DCE6EB");
            foreach (var key in new[] { "EditorToolbarButtonPressedBackgroundBrush", "ImageEditorToolbarButtonPressedBackgroundBrush" })
                resources[key] = resources["AppAccentMutedBrush"];
            foreach (var key in new[] { "EditorToolbarButtonSelectedBorderBrush", "ImageEditorToolbarButtonSelectedBorderBrush", "EditorToolbarConfirmBorderBrush" })
                SetSettingsColor(resources, key, "#FF4B606D");
            SetSettingsColor(resources, "ImageEditorToolbarSelectedIconBrush", palette.ControlForeground);
            SetSettingsColor(resources, "EditorToolbarConfirmIconBrush", palette.ControlForeground);
            resources["AppGlassEdgeBrush"] = Presentation.LiquidGlassMaterial.CreateEdge(false);
            resources["ImageEditorShellBackgroundBrush"] = resources["AppGlassBackgroundBrush"];
            resources["ImageEditorToolbarBackgroundBrush"] = resources["AppFloatingSurfaceBrush"];
            resources["ImageEditorToolbarBorderBrush"] = resources["AppControlEdgeBrush"];
            resources["ImageEditorToolbarPanelBackgroundBrush"] = resources["AppFloatingSurfaceBrush"];
            resources["ImageEditorToolbarPanelBorderThickness"] = new Thickness(1);
            resources["ImageEditorToolbarPanelCornerRadius"] = new CornerRadius(11);
        }
    }

    private static WpfColor ParseColor(string value)
    {
        return (WpfColor)WpfColorConverter.ConvertFromString(value);
    }

    private static WpfColor Blend(WpfColor source, WpfColor target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return WpfColor.FromArgb(
            0xFF,
            (byte)Math.Round(source.R + ((target.R - source.R) * amount)),
            (byte)Math.Round(source.G + ((target.G - source.G) * amount)),
            (byte)Math.Round(source.B + ((target.B - source.B) * amount)));
    }

    private static WpfColor GetContrastColor(WpfColor color)
    {
        var luminance = ((0.2126 * color.R) +
                         (0.7152 * color.G) +
                         (0.0722 * color.B)) / 255;
        return luminance > 0.58 ? WpfColors.Black : WpfColors.White;
    }

    private static WpfColor GetAccentContrastColor(WpfColor start, WpfColor end)
    {
        static double Linear(byte value) => value / 255d <= .04045
            ? value / 255d / 12.92 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
        static double Luminance(WpfColor color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var minimum = Math.Min(Luminance(start), Luminance(end));
        var maximum = Math.Max(Luminance(start), Luminance(end));
        // Compare the worst endpoint so pale rose/purple actions don't retain
        // white labels intended for the old dark accent colours.
        return (minimum + .05) / .05 >= 1.05 / (maximum + .05) ? WpfColors.Black : WpfColors.White;
    }

    private static bool IsMattePalette(ThemePalette palette) =>
        palette == GinkgoPaperPalette || palette == NeonDeepPalette;

    private static DrawingBrush CreateTonalBackground(ThemePalette palette)
    {
        static WpfColor Tint(string hex, byte alpha)
        {
            var color = ParseColor(hex);
            return WpfColor.FromArgb(alpha, color.R, color.G, color.B);
        }
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, 1000, 800)) };
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(CreateGradientBrush(palette.WindowStart, palette.WindowEnd), null, new Rect(0, 0, 1000, 800));
            if (!IsMattePalette(palette))
            {
                var cool = new RadialGradientBrush(Tint(palette.AccentStart, palette.IsDark ? (byte)24 : (byte)30), WpfColors.Transparent)
                { Center = new System.Windows.Point(.06, .05), GradientOrigin = new System.Windows.Point(.06, .05), RadiusX = .95, RadiusY = .85 };
                dc.DrawRectangle(cool, null, new Rect(0, 0, 1000, 800));
                var warm = new RadialGradientBrush(Tint(palette.WarmAccent, palette.IsDark ? (byte)14 : (byte)62), WpfColors.Transparent)
                { Center = new System.Windows.Point(.96, .88), GradientOrigin = new System.Windows.Point(.96, .88), RadiusX = .72, RadiusY = .6 };
                dc.DrawRectangle(warm, null, new Rect(0, 0, 1000, 800));
                var fold = Geometry.Parse("M 0,640 C 280,470 570,810 1000,555 L 1000,800 L 0,800 Z");
                dc.DrawGeometry(new SolidColorBrush(Tint(palette.WarmAccent, palette.IsDark ? (byte)7 : (byte)22)), null, fold);
                var edge = Geometry.Parse("M 0,640 C 280,470 570,810 1000,555");
                dc.DrawGeometry(null, new System.Windows.Media.Pen(new SolidColorBrush(Tint(palette.WarmAccent, palette.IsDark ? (byte)22 : (byte)95)), 1), edge);
            }
        }
        drawing.Freeze();
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateGradientBrush(
        string startColor,
        string endColor)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(
            (WpfColor)WpfColorConverter.ConvertFromString(startColor),
            0));
        brush.GradientStops.Add(new GradientStop(
            (WpfColor)WpfColorConverter.ConvertFromString(endColor),
            1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateGradientBrush(
        WpfColor startColor,
        WpfColor endColor)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(startColor, 0));
        brush.GradientStops.Add(new GradientStop(endColor, 1));
        brush.Freeze();
        return brush;
    }

    private static ThemePalette GetPalette(AppTheme theme)
    {
        return AppSettings.NormalizeTheme(theme) switch
        {
            AppTheme.CoralSky => CoralSkyPalette,
            AppTheme.GinkgoPaper => GinkgoPaperPalette,
            AppTheme.ForestNight => ForestNightPalette,
            AppTheme.ObsidianGold => ObsidianGoldPalette,
            AppTheme.NeonDeep => NeonDeepPalette,
            AppTheme.GlassMist => GlassMistPalette,
            AppTheme.GlassInk => GlassInkPalette,
            _ => AuroraMistPalette,
        };
    }

    private sealed record ThemePalette(
        bool IsDark,
        string WindowStart,
        string WindowEnd,
        string GlassStart,
        string GlassEnd,
        string SidebarStart,
        string SidebarEnd,
        string PanelStart,
        string PanelEnd,
        string InputStart,
        string InputEnd,
        string AccentStart,
        string AccentEnd,
        string Border,
        string SubtleBorder,
        string TextPrimary,
        string TextSecondary,
        string ControlForeground,
        string Separator,
        string TooltipBackground,
        string TooltipForeground,
        string WarmAccent);

    private static readonly ThemePalette AuroraMistPalette = new(
        IsDark: false,
        WindowStart: "#F5F2E8",
        WindowEnd: "#DFEBE4",
        GlassStart: "#F5F2E8",
        GlassEnd: "#DFEBE4",
        SidebarStart: "#CDE2D8",
        SidebarEnd: "#E8DFC7",
        PanelStart: "#FAF8F0",
        PanelEnd: "#EAF1E9",
        InputStart: "#FCFBF5",
        InputEnd: "#E9F1EA",
        AccentStart: "#3FA796",
        AccentEnd: "#76B4A2",
        Border: "#A4BEB0",
        SubtleBorder: "#CAD9CD",
        TextPrimary: "#263C35",
        TextSecondary: "#4B6157",
        ControlForeground: "#2B443A",
        Separator: "#BFCEBE",
        TooltipBackground: "#F5F5EA",
        TooltipForeground: "#263C35",
        WarmAccent: "#E8D9B5");

    private static readonly ThemePalette CoralSkyPalette = new(
        IsDark: false,
        WindowStart: "#FBF0E8",
        WindowEnd: "#E6EAF1",
        GlassStart: "#FBF0E8",
        GlassEnd: "#E6EAF1",
        SidebarStart: "#EDD0C4",
        SidebarEnd: "#CED8E6",
        PanelStart: "#FFF7F0",
        PanelEnd: "#EEF0F5",
        InputStart: "#FFFAF5",
        InputEnd: "#F0EDF0",
        AccentStart: "#4A5F8C",
        AccentEnd: "#536D98",
        Border: "#BDB7C2",
        SubtleBorder: "#DAD5DE",
        TextPrimary: "#303C53",
        TextSecondary: "#535A6C",
        ControlForeground: "#3B455A",
        Separator: "#D5CCD0",
        TooltipBackground: "#FBF4EF",
        TooltipForeground: "#303C53",
        WarmAccent: "#E8B4A2");

    private static readonly ThemePalette GinkgoPaperPalette = new(
        IsDark: false,
        WindowStart: "#ECEAE6",
        WindowEnd: "#E6E3E8",
        GlassStart: "#ECEAE6",
        GlassEnd: "#E6E3E8",
        SidebarStart: "#D9D6DD",
        SidebarEnd: "#DFDAD3",
        PanelStart: "#F0EEEA",
        PanelEnd: "#E8E5EC",
        InputStart: "#F2F0ED",
        InputEnd: "#EAE7ED",
        AccentStart: "#817B92",
        AccentEnd: "#918B9F",
        Border: "#B8B2C0",
        SubtleBorder: "#D0CBD5",
        TextPrimary: "#3F3B46",
        TextSecondary: "#5A5662",
        ControlForeground: "#49434F",
        Separator: "#CAC4CE",
        TooltipBackground: "#ECE9E8",
        TooltipForeground: "#3F3B46",
        WarmAccent: "#DCD6CC");

    private static readonly ThemePalette ForestNightPalette = new(
        IsDark: true,
        WindowStart: "#24333D",
        WindowEnd: "#31424D",
        GlassStart: "#24333D",
        GlassEnd: "#31424D",
        SidebarStart: "#31424D",
        SidebarEnd: "#49414B",
        PanelStart: "#354650",
        PanelEnd: "#404550",
        InputStart: "#293B45",
        InputEnd: "#34424C",
        AccentStart: "#C88697",
        AccentEnd: "#D7A18F",
        Border: "#827781",
        SubtleBorder: "#56616A",
        TextPrimary: "#FAF0E8",
        TextSecondary: "#CABEC1",
        ControlForeground: "#EDE2DF",
        Separator: "#55606A",
        TooltipBackground: "#303E48",
        TooltipForeground: "#FAF0E8",
        WarmAccent: "#E8B4A2");

    private static readonly ThemePalette ObsidianGoldPalette = new(
        IsDark: true,
        WindowStart: "#202B42",
        WindowEnd: "#344565",
        GlassStart: "#202B42",
        GlassEnd: "#344565",
        SidebarStart: "#314263",
        SidebarEnd: "#40547D",
        PanelStart: "#334563",
        PanelEnd: "#3E4E6B",
        InputStart: "#28384F",
        InputEnd: "#34445F",
        AccentStart: "#E8B4A2",
        AccentEnd: "#D3BDC9",
        Border: "#7D88A2",
        SubtleBorder: "#566783",
        TextPrimary: "#F7F0E8",
        TextSecondary: "#D8DEEB",
        ControlForeground: "#E9E5E5",
        Separator: "#526481",
        TooltipBackground: "#2C3C57",
        TooltipForeground: "#F7F0E8",
        WarmAccent: "#E8B4A2");

    private static readonly ThemePalette NeonDeepPalette = new(
        IsDark: true,
        WindowStart: "#2C2B30",
        WindowEnd: "#37343D",
        GlassStart: "#2C2B30",
        GlassEnd: "#37343D",
        SidebarStart: "#35333C",
        SidebarEnd: "#413D49",
        PanelStart: "#3B3842",
        PanelEnd: "#413D47",
        InputStart: "#323137",
        InputEnd: "#38353E",
        AccentStart: "#9B94AA",
        AccentEnd: "#ABA3B3",
        Border: "#756E80",
        SubtleBorder: "#514B5B",
        TextPrimary: "#E3E0DD",
        TextSecondary: "#BBB6C2",
        ControlForeground: "#D7D3DC",
        Separator: "#514C59",
        TooltipBackground: "#333139",
        TooltipForeground: "#E3E0DD",
        WarmAccent: "#C3BDB3");

    private static readonly ThemePalette GlassMistPalette = AuroraMistPalette with
    {
        WindowStart = "#F7F8F9", WindowEnd = "#F7F8F9",
        GlassStart = "#26FFFFFF", GlassEnd = "#22F3F8FA",
        SidebarStart = "#14EDF0F2", SidebarEnd = "#1CE8ECEF",
        PanelStart = "#84FCFEFF", PanelEnd = "#80F1F8FB",
        InputStart = "#88FCFEFF", InputEnd = "#80F1F8FB",
        AccentStart = "#56636D", AccentEnd = "#56636D",
        Border = "#A893A0A8", SubtleBorder = "#62C6D1D8",
        TextPrimary = "#1B2227", TextSecondary = "#252E34",
        ControlForeground = "#15191D", Separator = "#38DFE7EB",
        TooltipBackground = "#DEF3F6F8", TooltipForeground = "#1E262C",
        WarmAccent = "#C08732",
    };

    private static readonly ThemePalette GlassInkPalette = ForestNightPalette with
    {
        WindowStart = "#172A36", WindowEnd = "#243C45",
        GlassStart = "#58202945", GlassEnd = "#78101D35",
        SidebarStart = "#101A2643", SidebarEnd = "#28202F4C",
        PanelStart = "#40202F48", PanelEnd = "#55283957",
        InputStart = "#40131F38", InputEnd = "#50263751",
        AccentStart = "#509ED3", AccentEnd = "#758CC8",
        Border = "#688B9DBC", SubtleBorder = "#307D93B8",
        TextPrimary = "#E4F0F4", TextSecondary = "#A7C0CB",
        ControlForeground = "#D3E7EE", Separator = "#3F606D",
        TooltipBackground = "#F0233C49", TooltipForeground = "#E4F0F4",
        WarmAccent = "#D5A354",
    };

    private static readonly IReadOnlyDictionary<string, string> LightColors =
        new Dictionary<string, string>
        {
            ["AppWindowBackgroundBrush"] = "#E8F3F1",
            ["AppGlassBackgroundBrush"] = "#DDEEF1F2",
            ["AppSidebarBackgroundBrush"] = "#D9ECEA",
            ["AppPanelBackgroundBrush"] = "#F4FBFA",
            ["AppInputBackgroundBrush"] = "#F4FBFA",
            ["AppBorderBrush"] = "#8EB9B7",
            ["AppSubtleBorderBrush"] = "#A9CECA",
            ["AppTextPrimaryBrush"] = "#1C252D",
            ["AppTextSecondaryBrush"] = "#66757F",
            ["AppMutedTextBrush"] = "#66757F",
            ["AppControlForegroundBrush"] = "#26464B",
            ["AppAccentBrush"] = "#2EAFA5",
            ["AppAccentMutedBrush"] = "#BFE8E2",
            ["AppAccentForegroundBrush"] = "#00695F",
            ["AppDangerBrush"] = "#B42318",
            ["AppSeparatorBrush"] = "#DCE4E8",
            ["AppTooltipBackgroundBrush"] = "#F2F4FBFA",
            ["AppTooltipForegroundBrush"] = "#183F43",
            ["EditorToolbarButtonBackgroundBrush"] = "#FCFDFD",
            ["EditorToolbarButtonBorderBrush"] = "#AEBBBD",
            ["EditorToolbarIconBrush"] = "#20272B",
            ["EditorToolbarButtonHoverBackgroundBrush"] = "#F0F8F7",
            ["EditorToolbarButtonHoverBorderBrush"] = "#62AFA9",
            ["EditorToolbarButtonPressedBackgroundBrush"] = "#DDEDEA",
            ["EditorToolbarButtonSelectedBackgroundBrush"] = "#DDF3F0",
            ["EditorToolbarButtonSelectedBorderBrush"] = "#2EAFA5",
            ["EditorToolbarConfirmBackgroundBrush"] = "#E4F6F2",
            ["EditorToolbarConfirmBorderBrush"] = "#58B8AC",
            ["EditorToolbarConfirmIconBrush"] = "#167A70",
            ["EditorToolbarCancelIconBrush"] = "#D84B57",
            ["ImageEditorToolbarButtonBackgroundBrush"] = "#FCFDFD",
            ["ImageEditorToolbarButtonBorderBrush"] = "#AEBBBD",
            ["ImageEditorToolbarIconBrush"] = "#20272B",
            ["ImageEditorToolbarButtonHoverBackgroundBrush"] = "#F0F8F7",
            ["ImageEditorToolbarButtonHoverBorderBrush"] = "#62AFA9",
            ["ImageEditorToolbarButtonPressedBackgroundBrush"] = "#DDEDEA",
            ["ImageEditorToolbarButtonSelectedBackgroundBrush"] = "#DDF3F0",
            ["ImageEditorToolbarButtonSelectedBorderBrush"] = "#2EAFA5",
            ["ImageEditorToolbarSelectedIconBrush"] = "#20272B",
            ["ImageEditorWindowBackgroundBrush"] = "#EEF7F6",
            ["ImageEditorShellBackgroundBrush"] = "#FCFEFE",
            ["ImageEditorShellBorderBrush"] = "#86B7B2",
            ["ImageEditorToolbarBackgroundBrush"] = "#F4FAF9",
            ["ImageEditorToolbarBorderBrush"] = "#B9D4D1",
            ["ImageEditorTitleBrush"] = "#1C252D",
            ["ImageEditorSeparatorBrush"] = "#CADCDA",
            ["ImageEditorWorkspaceBackgroundBrush"] = "#E5EFEE",
            ["ImageEditorWorkspaceBorderBrush"] = "#A9C4C1",
            ["ImageEditorViewportBackgroundBrush"] = "#DCE8E6",
            ["ImageEditorViewportBorderBrush"] = "#A5BFBC",
            ["ImageEditorMutedTextBrush"] = "#536B6F",
            ["ImageEditorSecondaryTextBrush"] = "#35575B",
            ["ImageEditorSliderTrackBrush"] = "#C9DAD7",
            ["ImageEditorSliderThumbBorderBrush"] = "#F8FFFE",
            ["ImageEditorSwatchBorderBrush"] = "#6E898B",
            ["ImageEditorSwatchHoverBorderBrush"] = "#1F3034",
        };

    private static readonly IReadOnlyDictionary<string, string> DarkColors =
        new Dictionary<string, string>
        {
            ["AppWindowBackgroundBrush"] = "#10191D",
            ["AppGlassBackgroundBrush"] = "#E618292E",
            ["AppSidebarBackgroundBrush"] = "#E01B3035",
            ["AppPanelBackgroundBrush"] = "#E025383D",
            ["AppInputBackgroundBrush"] = "#E0203237",
            ["AppBorderBrush"] = "#667C9D99",
            ["AppSubtleBorderBrush"] = "#526B8784",
            ["AppTextPrimaryBrush"] = "#ECF8F6",
            ["AppTextSecondaryBrush"] = "#A8C5C2",
            ["AppMutedTextBrush"] = "#A8C5C2",
            ["AppControlForegroundBrush"] = "#DDF2EF",
            ["AppAccentBrush"] = "#2EAFA5",
            ["AppAccentMutedBrush"] = "#4A2E6762",
            ["AppAccentForegroundBrush"] = "#B5F4ED",
            ["AppDangerBrush"] = "#FF9B8F",
            ["AppSeparatorBrush"] = "#465C706E",
            ["AppTooltipBackgroundBrush"] = "#F21A2B30",
            ["AppTooltipForegroundBrush"] = "#E4F5F2",
            ["EditorToolbarButtonBackgroundBrush"] = "#D91F252D",
            ["EditorToolbarButtonBorderBrush"] = "#526C7780",
            ["EditorToolbarIconBrush"] = "#E7F3F1",
            ["EditorToolbarButtonHoverBackgroundBrush"] = "#F02F3944",
            ["EditorToolbarButtonHoverBorderBrush"] = "#82D8CC",
            ["EditorToolbarButtonPressedBackgroundBrush"] = "#F0446064",
            ["EditorToolbarButtonSelectedBackgroundBrush"] = "#B44A666B",
            ["EditorToolbarButtonSelectedBorderBrush"] = "#82D8CC",
            ["EditorToolbarConfirmBackgroundBrush"] = "#B44A9E95",
            ["EditorToolbarConfirmBorderBrush"] = "#82D8CC",
            ["EditorToolbarConfirmIconBrush"] = "#72E0BD",
            ["EditorToolbarCancelIconBrush"] = "#F26D78",
            ["ImageEditorToolbarButtonBackgroundBrush"] = "#D91A3037",
            ["ImageEditorToolbarButtonBorderBrush"] = "#5C6B9696",
            ["ImageEditorToolbarIconBrush"] = "#D9F5F1",
            ["ImageEditorToolbarButtonHoverBackgroundBrush"] = "#F02A4B50",
            ["ImageEditorToolbarButtonHoverBorderBrush"] = "#8AE1D8D0",
            ["ImageEditorToolbarButtonPressedBackgroundBrush"] = "#F0396666",
            ["ImageEditorToolbarButtonSelectedBackgroundBrush"] = "#E62EAFA5",
            ["ImageEditorToolbarButtonSelectedBorderBrush"] = "#B7F1E7E0",
            ["ImageEditorToolbarSelectedIconBrush"] = "#092A2D",
            ["ImageEditorWindowBackgroundBrush"] = "#102027",
            ["ImageEditorShellBackgroundBrush"] = "#F212252C",
            ["ImageEditorShellBorderBrush"] = "#7A5BBDB5",
            ["ImageEditorToolbarBackgroundBrush"] = "#D9183037",
            ["ImageEditorToolbarBorderBrush"] = "#3E698084",
            ["ImageEditorTitleBrush"] = "#E0F4F1",
            ["ImageEditorSeparatorBrush"] = "#4B829095",
            ["ImageEditorWorkspaceBackgroundBrush"] = "#AA0C171C",
            ["ImageEditorWorkspaceBorderBrush"] = "#3F668087",
            ["ImageEditorViewportBackgroundBrush"] = "#E61B2A30",
            ["ImageEditorViewportBorderBrush"] = "#485A7C7C",
            ["ImageEditorMutedTextBrush"] = "#91C8C5",
            ["ImageEditorSecondaryTextBrush"] = "#BCEAE5",
            ["ImageEditorSliderTrackBrush"] = "#405C7472",
            ["ImageEditorSliderThumbBorderBrush"] = "#C7FFF9",
            ["ImageEditorSwatchBorderBrush"] = "#8AE1D8D0",
            ["ImageEditorSwatchHoverBorderBrush"] = "#E8FFFC",
        };
}
