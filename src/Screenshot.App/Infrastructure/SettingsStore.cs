using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public SettingsStore(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? AppMetadata.SettingsPath;
    }

    public string SettingsPath { get; }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new SettingsLoadResult(AppSettings.CreateDefault(), Warning: null);
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                ?? throw new JsonException("配置文件为空。");

            return new SettingsLoadResult(
                SettingsValidation.ValidateAndNormalize(settings),
                Warning: null);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or NotSupportedException)
        {
            return new SettingsLoadResult(
                AppSettings.CreateDefault(),
                "无法读取已有设置，已使用默认值。");
        }
    }

    public void Save(AppSettings settings)
    {
        var normalizedSettings = SettingsValidation.ValidateAndNormalize(settings);
        var settingsDirectory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("无法确定设置文件所在目录。");
        var temporaryPath = Path.Combine(
            settingsDirectory,
            $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

        Directory.CreateDirectory(settingsDirectory);

        try
        {
            var json = JsonSerializer.Serialize(normalizedSettings, SerializerOptions);
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
