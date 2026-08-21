using System.Text.Json;

namespace SnapCut.Mac.App;

[Flags]
internal enum MacHotkeyModifiers : ulong
{
    None = 0,
    Shift = 1UL << 17,
    Control = 1UL << 18,
    Option = 1UL << 19,
    Command = 1UL << 20,
}

internal sealed record MacHotkeyGesture(
    ushort KeyCode,
    MacHotkeyModifiers Modifiers,
    string DisplayText)
{
    public static MacHotkeyGesture CaptureDefault { get; } =
        new(0, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧A");

    public static MacHotkeyGesture ScrollDefault { get; } =
        new(1, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧S");

    public static MacHotkeyGesture RecordingDefault { get; } =
        new(15, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧R");

    public static MacHotkeyGesture OcrDefault { get; } =
        new(31, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧O");

    public static MacHotkeyGesture TranslationDefault { get; } =
        new(17, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧T");

    public static MacHotkeyGesture PinDefault { get; } =
        new(35, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧P");

    public static MacHotkeyGesture SettingsDefault { get; } =
        new(43, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧,");

    public bool Matches(ushort keyCode, ulong eventFlags)
    {
        const MacHotkeyModifiers supported =
            MacHotkeyModifiers.Shift |
            MacHotkeyModifiers.Control |
            MacHotkeyModifiers.Option |
            MacHotkeyModifiers.Command;
        var actual = (MacHotkeyModifiers)eventFlags & supported;
        return KeyCode == keyCode && actual == Modifiers;
    }

    public override string ToString() => DisplayText;
}

internal sealed class MacSettings
{
    internal static string DefaultSaveDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "SnapCut");

    internal static string DefaultVideoSaveDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "SnapCut");

    public static readonly string[] DefaultToolbarFeatures =
    [
        "Shape", "Arrow", "Emoji", "Number", "Brush", "Text", "Mosaic",
        "VideoRecording", "Save", "ScrollCapture", "RecognizeText",
        "CopyRecognizedText", "Translation", "PrivacyRedaction", "PinImage",
        "UndoRedo", "QrRecognition",
    ];
    public MacHotkeyGesture CaptureHotkey { get; set; } =
        MacHotkeyGesture.CaptureDefault;

    public MacHotkeyGesture ScrollHotkey { get; set; } =
        MacHotkeyGesture.ScrollDefault;

    public MacHotkeyGesture RecordingHotkey { get; set; } =
        MacHotkeyGesture.RecordingDefault;

    public MacHotkeyGesture OcrHotkey { get; set; } = MacHotkeyGesture.OcrDefault;

    public MacHotkeyGesture TranslationHotkey { get; set; } =
        MacHotkeyGesture.TranslationDefault;

    public MacHotkeyGesture PinHotkey { get; set; } = MacHotkeyGesture.PinDefault;

    public MacHotkeyGesture SettingsHotkey { get; set; } =
        MacHotkeyGesture.SettingsDefault;

    public int HistoryLimit { get; set; } = 100;

    public string SaveDirectory { get; set; } = DefaultSaveDirectory();

    public string VideoSaveDirectory { get; set; } = DefaultVideoSaveDirectory();

    public bool KeepHistory { get; set; } = true;

    public bool PersistHistoryAcrossRestarts { get; set; } = true;

    public bool ShowPreviewAfterCapture { get; set; } = true;

    public string ArrowStyle { get; set; } = "Filled";

    public string AnnotationColor { get; set; } = "#FF3B30";

    public double AnnotationWidth { get; set; } = 3;

    public string TranslationEndpoint { get; set; } =
        "https://api.openai.com/v1/chat/completions";

    public string TranslationModel { get; set; } = "gpt-4.1-mini";

    public string TranslationTargetLanguage { get; set; } = "zh-Hans";

    public bool SendTextToOnlineTranslation { get; set; }

    public string OfflineTranslationConfigPath { get; set; } = string.Empty;

    public string[] VisibleToolbarFeatures { get; set; } = DefaultToolbarFeatures.ToArray();

    public string[] ToolbarFeatureOrder { get; set; } = DefaultToolbarFeatures.ToArray();

    public bool? ToolbarFeaturesInitialized { get; set; }

    public int ToolbarRows { get; set; } = 1;

    public double ToolbarPositionXRatio { get; set; } = -1;

    public double ToolbarPositionYRatio { get; set; } = -1;

    public string VideoOutputFormat { get; set; } = "Mp4";

    public string VideoCodec { get; set; } = "H264";

    public int VideoFrameRate { get; set; } = 30;

    public bool RecordSystemAudio { get; set; } = true;

    public bool RecordMicrophone { get; set; }

    public bool ShowMouseInputInRecording { get; set; }

    public bool ShowKeyboardInputInRecording { get; set; }

    public string Theme { get; set; } = "NeonDeep";

    public bool LaunchAtStartup { get; set; }

    public bool ShowNotificationIcon { get; set; } = true;

    public bool ShowFloatingCaptureButton { get; set; }

    public string FloatingCaptureClickBehavior { get; set; } = "ShowSelection";

    public string CloseBehavior { get; set; } = "MinimizeToBackground";

    public string[] CustomColorPalette { get; set; } =
    [
        "#FF3B30", "#FFCC00", "#34C759", "#0A84FF",
        "#AF52DE", "#FFFFFF", "#111827", "#8B5CF6",
    ];

    public string ScrollCaptureMode { get; set; } = "Automatic";
}

internal sealed class MacSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public MacSettingsStore(string? applicationDataDirectory = null)
    {
        var root = applicationDataDirectory ?? DefaultApplicationDataDirectory();
        SettingsPath = Path.Combine(root, "settings.json");
    }

    public string SettingsPath { get; }

    public MacSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new MacSettings();
            }

            var settings = JsonSerializer.Deserialize<MacSettings>(
                               File.ReadAllText(SettingsPath),
                               JsonOptions)
                           ?? new MacSettings();
            settings.CaptureHotkey ??= MacHotkeyGesture.CaptureDefault;
            settings.ScrollHotkey ??= MacHotkeyGesture.ScrollDefault;
            settings.RecordingHotkey ??= MacHotkeyGesture.RecordingDefault;
            settings.OcrHotkey ??= MacHotkeyGesture.OcrDefault;
            settings.TranslationHotkey ??= MacHotkeyGesture.TranslationDefault;
            settings.PinHotkey ??= MacHotkeyGesture.PinDefault;
            settings.SettingsHotkey ??= MacHotkeyGesture.SettingsDefault;
            settings.HistoryLimit = Math.Clamp(settings.HistoryLimit, 1, 100);
            settings.SaveDirectory = string.IsNullOrWhiteSpace(settings.SaveDirectory)
                ? MacSettings.DefaultSaveDirectory()
                : settings.SaveDirectory.Trim();
            settings.VideoSaveDirectory = string.IsNullOrWhiteSpace(settings.VideoSaveDirectory)
                ? MacSettings.DefaultVideoSaveDirectory()
                : settings.VideoSaveDirectory.Trim();
            settings.PersistHistoryAcrossRestarts =
                settings.KeepHistory && settings.PersistHistoryAcrossRestarts;
            settings.FloatingCaptureClickBehavior = settings.FloatingCaptureClickBehavior is
                "CaptureImmediately" or "ShowSelection" or "VideoRecording" or
                "ScrollCapture" or "PinCapture" or "CaptureAllScreens"
                ? settings.FloatingCaptureClickBehavior
                : "ShowSelection";
            settings.CloseBehavior = settings.CloseBehavior is
                "MinimizeToBackground" or "ExitApplication"
                ? settings.CloseBehavior
                : "MinimizeToBackground";
            if (!IsColor(settings.AnnotationColor))
            {
                settings.AnnotationColor = "#FF3B30";
            }
            settings.AnnotationWidth = Math.Clamp(settings.AnnotationWidth, 1, 10);
            settings.TranslationEndpoint = settings.TranslationEndpoint?.Trim()
                ?? string.Empty;
            settings.TranslationModel = settings.TranslationModel?.Trim()
                ?? string.Empty;
            settings.TranslationTargetLanguage =
                string.IsNullOrWhiteSpace(settings.TranslationTargetLanguage)
                    ? "zh-Hans"
                    : settings.TranslationTargetLanguage.Trim();
            settings.VisibleToolbarFeatures = (settings.VisibleToolbarFeatures ?? [])
                .Intersect(MacSettings.DefaultToolbarFeatures, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (settings.VisibleToolbarFeatures.Length == 0)
            {
                settings.VisibleToolbarFeatures = MacSettings.DefaultToolbarFeatures.ToArray();
            }
            settings.ToolbarFeatureOrder = (settings.ToolbarFeatureOrder ?? [])
                .Intersect(MacSettings.DefaultToolbarFeatures, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .Concat(MacSettings.DefaultToolbarFeatures.Where(feature =>
                    !(settings.ToolbarFeatureOrder ?? []).Contains(feature, StringComparer.Ordinal)))
                .ToArray();
            // Older files have no migration marker. Their normalized order and
            // visibility are still valid user choices; only missing features
            // are appended above so the Windows-parity controls become
            // available without resetting the user's layout.
            settings.ToolbarFeaturesInitialized = true;
            settings.ToolbarRows = Math.Clamp(settings.ToolbarRows, 1, 2);
            settings.ToolbarPositionXRatio = NormalizeRatio(settings.ToolbarPositionXRatio);
            settings.ToolbarPositionYRatio = NormalizeRatio(settings.ToolbarPositionYRatio);
            settings.ArrowStyle = settings.ArrowStyle is "Filled" or "Hollow"
                ? settings.ArrowStyle
                : "Filled";
            settings.VideoOutputFormat = settings.VideoOutputFormat is "Mp4" or "Gif"
                ? settings.VideoOutputFormat
                : "Mp4";
            settings.VideoCodec = settings.VideoCodec is "H264" or "H265"
                ? settings.VideoCodec
                : "H264";
            settings.VideoFrameRate = Math.Clamp(settings.VideoFrameRate, 10, 60);
            settings.Theme = settings.Theme is "System" or "Light" or "Dark" or
                "AuroraMist" or "CoralSky" or "GinkgoPaper" or "ForestNight" or
                "ObsidianGold" or "NeonDeep"
                ? settings.Theme
                : "NeonDeep";
            settings.CustomColorPalette = NormalizePalette(settings.CustomColorPalette);
            settings.ScrollCaptureMode = settings.ScrollCaptureMode is "Automatic" or "Manual"
                ? settings.ScrollCaptureMode
                : "Automatic";
            return settings;
        }
        catch (JsonException)
        {
            return new MacSettings();
        }
        catch (IOException)
        {
            return new MacSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new MacSettings();
        }
    }

    public void Save(MacSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ToolbarFeaturesInitialized = true;
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("无法确定 macOS 设置目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private static string DefaultApplicationDataDirectory()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "SnapCut");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SnapCut");
    }

    private static bool IsColor(string? value) =>
        value is { Length: 7 or 9 } &&
        value[0] == '#' &&
        int.TryParse(
            value.AsSpan(1),
            System.Globalization.NumberStyles.HexNumber,
            provider: null,
            out _);

    private static string[] NormalizePalette(string[]? palette)
    {
        var defaults = new MacSettings().CustomColorPalette;
        var values = (palette ?? [])
            .Where(IsColor)
            .Take(8)
            .ToList();
        while (values.Count < 8)
        {
            values.Add(defaults[values.Count]);
        }
        return values.ToArray();
    }

    private static double NormalizeRatio(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1 ? value : -1;
}
