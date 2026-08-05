using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
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

    public AppTheme ResolvedTheme => _resolvedTheme;

    public event EventHandler<AppTheme>? ThemeChanged;

    public static void ApplySettingsPalette(
        ResourceDictionary resources,
        AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ApplyPalette(resources, GetPalette(theme));
    }

    public AppThemeManager()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
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

    internal static ThemeChromeColors GetChromeColors(AppTheme theme)
    {
        var palette = GetPalette(theme);
        var accent = ParseColor(palette.AccentStart);
        var selection = palette.IsDark
            ? Blend(accent, ParseColor(palette.PanelStart), 0.58)
            : Blend(accent, WpfColors.White, 0.80);
        return new ThemeChromeColors(
            palette.IsDark,
            palette.PanelStart,
            palette.ControlForeground,
            ToHex(selection),
            palette.Border);
    }

    private static string ToHex(WpfColor color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void ApplyResolvedTheme(AppTheme theme)
    {
        _resolvedTheme = theme;
        var application = WpfApplication.Current;
        if (application is null)
        {
            return;
        }

        if (!application.Dispatcher.CheckAccess())
        {
            _ = application.Dispatcher.BeginInvoke(() => ApplyResolvedTheme(theme));
            return;
        }

        ApplyPalette(application.Resources, GetPalette(theme));

        foreach (Window window in application.Windows)
        {
            ApplyWindowChromeTheme(window, theme);
        }

        ThemeChanged?.Invoke(this, theme);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            ApplyWindowChromeTheme(window, _resolvedTheme);
        }
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
        ThemePalette palette)
    {
        var colors = palette.IsDark ? DarkColors : LightColors;
        foreach (var (key, color) in colors)
        {
            SetSettingsColor(resources, key, color);
        }

        resources["AppWindowBackgroundBrush"] = CreateGradientBrush(
            palette.WindowStart,
            palette.WindowEnd);
        resources["AppGlassBackgroundBrush"] = CreateGradientBrush(
            palette.GlassStart,
            palette.GlassEnd);
        resources["AppSidebarBackgroundBrush"] = CreateGradientBrush(
            palette.SidebarStart,
            palette.SidebarEnd);
        resources["AppPanelBackgroundBrush"] = CreateGradientBrush(
            palette.PanelStart,
            palette.PanelEnd);
        resources["AppInputBackgroundBrush"] = CreateGradientBrush(
            palette.InputStart,
            palette.InputEnd);
        SetSettingsColor(resources, "AppBorderBrush", palette.Border);
        SetSettingsColor(resources, "AppSubtleBorderBrush", palette.SubtleBorder);
        SetSettingsColor(resources, "AppTextPrimaryBrush", palette.TextPrimary);
        SetSettingsColor(resources, "AppTextSecondaryBrush", palette.TextSecondary);
        SetSettingsColor(resources, "AppMutedTextBrush", palette.TextSecondary);
        SetSettingsColor(resources, "AppControlForegroundBrush", palette.ControlForeground);
        SetSettingsColor(resources, "AppSeparatorBrush", palette.Separator);
        SetSettingsColor(resources, "AppTooltipBackgroundBrush", palette.TooltipBackground);
        SetSettingsColor(resources, "AppTooltipForegroundBrush", palette.TooltipForeground);
        SetSettingsColor(resources, "AppWarmAccentBrush", palette.WarmAccent);

        resources["ImageEditorWindowBackgroundBrush"] = CreateGradientBrush(
            palette.WindowStart,
            palette.WindowEnd);
        resources["ImageEditorShellBackgroundBrush"] = CreateGradientBrush(
            palette.PanelStart,
            palette.PanelEnd);
        resources["ImageEditorToolbarBackgroundBrush"] = CreateGradientBrush(
            palette.InputStart,
            palette.InputEnd);
        SetSettingsColor(resources, "ImageEditorShellBorderBrush", palette.Border);
        SetSettingsColor(resources, "ImageEditorToolbarBorderBrush", palette.SubtleBorder);
        SetSettingsColor(resources, "ImageEditorTitleBrush", palette.TextPrimary);
        SetSettingsColor(resources, "ImageEditorMutedTextBrush", palette.TextSecondary);
        SetSettingsColor(resources, "ImageEditorSecondaryTextBrush", palette.ControlForeground);
        SetSettingsColor(resources, "ImageEditorSliderTrackBrush", palette.SubtleBorder);
        SetSettingsColor(resources, "ImageEditorSliderThumbBorderBrush", palette.IsDark ? "#EAF8F5" : "#FFFFFF");

        resources["EditorToolbarButtonBackgroundBrush"] = CreateGradientBrush(
            palette.InputStart,
            palette.InputEnd);
        SetSettingsColor(resources, "EditorToolbarButtonBorderBrush", palette.SubtleBorder);
        SetSettingsColor(resources, "EditorToolbarIconBrush", palette.ControlForeground);
        resources["EditorToolbarButtonHoverBackgroundBrush"] = CreateGradientBrush(
            palette.PanelStart,
            palette.PanelEnd);
        resources["ImageEditorToolbarButtonBackgroundBrush"] = CreateGradientBrush(
            palette.InputStart,
            palette.InputEnd);
        SetSettingsColor(resources, "ImageEditorToolbarButtonBorderBrush", palette.SubtleBorder);
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
            ? WpfColor.FromArgb(0x72, accent.R, accent.G, accent.B)
            : Blend(accent, WpfColors.White, 0.78);
        var mutedEnd = palette.IsDark
            ? WpfColor.FromArgb(0x72, accentEnd.R, accentEnd.G, accentEnd.B)
            : Blend(accentEnd, WpfColors.White, 0.86);
        var foreground = palette.IsDark
            ? Blend(accent, WpfColors.White, 0.72)
            : Blend(accent, WpfColors.Black, 0.38);
        var selectedStart = palette.IsDark
            ? Blend(accent, WpfColors.Black, 0.18)
            : Blend(accent, WpfColors.White, 0.72);
        var selectedEnd = palette.IsDark
            ? Blend(accentEnd, WpfColors.Black, 0.12)
            : Blend(accentEnd, WpfColors.White, 0.76);

        resources["AppAccentBrush"] = CreateGradientBrush(accent, accentEnd);
        resources["AppAccentMutedBrush"] = CreateGradientBrush(mutedStart, mutedEnd);
        SetSettingsBrush(resources, "AppAccentForegroundBrush", foreground);
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
        WindowStart: "#F7F8FB",
        WindowEnd: "#EDF1F6",
        GlassStart: "#FAFFFFFF",
        GlassEnd: "#F2F3F6FA",
        SidebarStart: "#EDF1F6",
        SidebarEnd: "#E7ECF4",
        PanelStart: "#FFFFFFFF",
        PanelEnd: "#F5F7FA",
        InputStart: "#FFFFFF",
        InputEnd: "#F2F4F8",
        AccentStart: "#2878D0",
        AccentEnd: "#6268C8",
        Border: "#B8C4D2",
        SubtleBorder: "#DCE3EB",
        TextPrimary: "#202734",
        TextSecondary: "#687385",
        ControlForeground: "#303A4A",
        Separator: "#E1E6ED",
        TooltipBackground: "#FAFFFFFF",
        TooltipForeground: "#252D3A",
        WarmAccent: "#C08732");

    private static readonly ThemePalette CoralSkyPalette = new(
        IsDark: false,
        WindowStart: "#FCF8F9",
        WindowEnd: "#F0F3F8",
        GlassStart: "#FCFFFFFF",
        GlassEnd: "#F5F8F4F6",
        SidebarStart: "#F6E9EC",
        SidebarEnd: "#E8EEF6",
        PanelStart: "#FFFFFFFF",
        PanelEnd: "#F7F5F7",
        InputStart: "#FFFFFF",
        InputEnd: "#F8F3F5",
        AccentStart: "#D05268",
        AccentEnd: "#4688C5",
        Border: "#D1BEC5",
        SubtleBorder: "#E8DCE1",
        TextPrimary: "#30262A",
        TextSecondary: "#726771",
        ControlForeground: "#453A43",
        Separator: "#ECE2E6",
        TooltipBackground: "#FCFFFFFF",
        TooltipForeground: "#372D33",
        WarmAccent: "#CF7B32");

    private static readonly ThemePalette GinkgoPaperPalette = new(
        IsDark: false,
        WindowStart: "#FAFAF7",
        WindowEnd: "#F0F2F4",
        GlassStart: "#FCFFFFFF",
        GlassEnd: "#F5F7F6F2",
        SidebarStart: "#EFF1EC",
        SidebarEnd: "#F2ECE0",
        PanelStart: "#FFFFFFFF",
        PanelEnd: "#F6F6F2",
        InputStart: "#FFFFFF",
        InputEnd: "#F5F4EF",
        AccentStart: "#A87820",
        AccentEnd: "#6476A6",
        Border: "#C8C4B9",
        SubtleBorder: "#E4E1D9",
        TextPrimary: "#2C3034",
        TextSecondary: "#6C7074",
        ControlForeground: "#42474D",
        Separator: "#E7E4DC",
        TooltipBackground: "#FCFFFFFF",
        TooltipForeground: "#33363A",
        WarmAccent: "#B47D22");

    private static readonly ThemePalette ForestNightPalette = new(
        IsDark: true,
        WindowStart: "#15181C",
        WindowEnd: "#1D2329",
        GlassStart: "#F21B1F24",
        GlassEnd: "#F2283037",
        SidebarStart: "#1D2329",
        SidebarEnd: "#29323A",
        PanelStart: "#20262C",
        PanelEnd: "#2B343C",
        InputStart: "#1A2026",
        InputEnd: "#252D34",
        AccentStart: "#3C9A78",
        AccentEnd: "#5B7FC0",
        Border: "#5E6D76",
        SubtleBorder: "#414B53",
        TextPrimary: "#F0F3F5",
        TextSecondary: "#ADB7BE",
        ControlForeground: "#DCE2E6",
        Separator: "#3C454D",
        TooltipBackground: "#F21D2329",
        TooltipForeground: "#EDF2F4",
        WarmAccent: "#D5A354");

    private static readonly ThemePalette ObsidianGoldPalette = new(
        IsDark: true,
        WindowStart: "#121314",
        WindowEnd: "#1C1E21",
        GlassStart: "#F218191B",
        GlassEnd: "#F226292D",
        SidebarStart: "#202226",
        SidebarEnd: "#30333A",
        PanelStart: "#24262A",
        PanelEnd: "#31343A",
        InputStart: "#1C1E21",
        InputEnd: "#292C31",
        AccentStart: "#B78328",
        AccentEnd: "#75659A",
        Border: "#706B63",
        SubtleBorder: "#4D5055",
        TextPrimary: "#F4F1E9",
        TextSecondary: "#BBB6AA",
        ControlForeground: "#E7E2D7",
        Separator: "#484A4D",
        TooltipBackground: "#F21D1F22",
        TooltipForeground: "#F4EFE4",
        WarmAccent: "#D4A64B");

    private static readonly ThemePalette NeonDeepPalette = new(
        IsDark: true,
        WindowStart: "#10151E",
        WindowEnd: "#192331",
        GlassStart: "#F2151B27",
        GlassEnd: "#F2232D3C",
        SidebarStart: "#192231",
        SidebarEnd: "#283446",
        PanelStart: "#1C2633",
        PanelEnd: "#2A3745",
        InputStart: "#17202B",
        InputEnd: "#242F3C",
        AccentStart: "#357FD0",
        AccentEnd: "#C05278",
        Border: "#5D6F86",
        SubtleBorder: "#414F62",
        TextPrimary: "#EDF4F7",
        TextSecondary: "#ADB9C8",
        ControlForeground: "#DEE5EE",
        Separator: "#3C495B",
        TooltipBackground: "#F218222F",
        TooltipForeground: "#E8F3F6",
        WarmAccent: "#D39A4C");

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
