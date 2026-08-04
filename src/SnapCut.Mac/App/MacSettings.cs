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
    public MacHotkeyGesture CaptureHotkey { get; set; } =
        MacHotkeyGesture.CaptureDefault;

    public MacHotkeyGesture ScrollHotkey { get; set; } =
        MacHotkeyGesture.ScrollDefault;

    public int HistoryLimit { get; set; } = 50;

    public bool ShowPreviewAfterCapture { get; set; } = true;
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
            settings.HistoryLimit = Math.Clamp(settings.HistoryLimit, 1, 500);
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
}
