using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Screenshot.App.Core;
using WpfApplication = System.Windows.Application;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace Screenshot.App.Infrastructure;

public sealed class AppThemeManager : IDisposable
{
    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private AppTheme _configuredTheme;
    private AppTheme _resolvedTheme = AppTheme.Light;
    private bool _disposed;

    public AppTheme ResolvedTheme => _resolvedTheme;

    public event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>
    /// Applies the icon palette to a single settings window. Capture and editor
    /// windows continue to resolve the legacy application resources.
    /// </summary>
    public static void ApplySettingsPalette(
        ResourceDictionary resources,
        AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var isDark = theme == AppTheme.Dark;
        resources["AppWindowBackgroundBrush"] = CreateGradientBrush(
            isDark ? "#10191D" : "#F7F7FC",
            isDark ? "#14272B" : "#F7F7FC");
        resources["AppGlassBackgroundBrush"] = CreateGradientBrush(
            isDark ? "#E617292E" : "#ECEDF8FF",
            isDark ? "#E61D3539" : "#ECF0E9FF");
        resources["AppSidebarBackgroundBrush"] = CreateGradientBrush(
            isDark ? "#1A2D32" : "#E9EDFC",
            isDark ? "#214044" : "#F0EBFB");
        resources["AppPanelBackgroundBrush"] = CreateGradientBrush(
            isDark ? "#20343A" : "#FCFBFF",
            isDark ? "#285057" : "#F7F3FC");
        resources["AppInputBackgroundBrush"] = CreateGradientBrush(
            isDark ? "#1C3035" : "#FAF9FE",
            isDark ? "#234147" : "#F3F0FC");
        resources["AppAccentBrush"] = CreateGradientBrush(
            isDark ? "#25B9AD" : "#617FF0",
            isDark ? "#54D8CF" : "#9663E9");
        resources["AppAccentMutedBrush"] = CreateGradientBrush(
            isDark ? "#66305D5B" : "#E5E9FF",
            isDark ? "#66407673" : "#F1E5FB");

        SetSettingsColor(resources, "AppBorderBrush", isDark ? "#6684AAA5" : "#B4BCE6");
        SetSettingsColor(resources, "AppSubtleBorderBrush", isDark ? "#526C918D" : "#D6D9EE");
        SetSettingsColor(resources, "AppTextPrimaryBrush", isDark ? "#ECF8F6" : "#28243B");
        SetSettingsColor(resources, "AppTextSecondaryBrush", isDark ? "#A8C5C2" : "#6B6A7C");
        SetSettingsColor(resources, "AppMutedTextBrush", isDark ? "#A8C5C2" : "#6B6A7C");
        SetSettingsColor(resources, "AppControlForegroundBrush", isDark ? "#DDF2EF" : "#3A3651");
        SetSettingsColor(resources, "AppAccentForegroundBrush", isDark ? "#C6FFF8" : "#5548B8");
        SetSettingsColor(resources, "AppSeparatorBrush", isDark ? "#465C706E" : "#E1E2F0");
        SetSettingsColor(resources, "AppTooltipBackgroundBrush", isDark ? "#F21A2B30" : "#F9F8FFFF");
        SetSettingsColor(resources, "AppTooltipForegroundBrush", isDark ? "#E4F5F2" : "#332C4C");
        SetSettingsColor(resources, "AppWarmAccentBrush", isDark ? "#D7A061" : "#D09A5C");
    }

    public AppThemeManager()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    public void Apply(AppTheme configuredTheme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _configuredTheme = configuredTheme;
        ApplyResolvedTheme(ResolveTheme(configuredTheme, IsSystemLightTheme()));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    internal static AppTheme ResolveTheme(AppTheme configuredTheme, bool systemUsesLightTheme)
    {
        return configuredTheme == AppTheme.System
            ? systemUsesLightTheme ? AppTheme.Light : AppTheme.Dark
            : configuredTheme;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
            return key?.GetValue(AppsUseLightThemeValue) is not int value || value != 0;
        }
        catch
        {
            return true;
        }
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

        var colors = theme == AppTheme.Dark ? DarkColors : LightColors;
        foreach (var (key, color) in colors)
        {
            var brush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(color));
            brush.Freeze();
            application.Resources[key] = brush;
        }

        foreach (Window window in application.Windows)
        {
            ApplyWindowChromeTheme(window, theme);
        }

        ThemeChanged?.Invoke(this, theme);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed || _configuredTheme != AppTheme.System)
        {
            return;
        }

        ApplyResolvedTheme(ResolveTheme(AppTheme.System, IsSystemLightTheme()));
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

            var useDarkMode = theme == AppTheme.Dark ? 1 : 0;
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
        };
}
