using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

public sealed class SettingsStore
{
    private readonly object _saveLock = new();
    private readonly object _pendingSaveLock = new();
    private System.Threading.Timer? _saveTimer;
    private AppSettings? _pendingSave;
    private AppSettings? _inFlightSave;
    public event Action<Exception>? DeferredSaveFailed;

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
        // Immediate consumers see the accepted preferences while the disk
        // writer coalesces rapid changes. This does not wait for filesystem IO.
        lock (_pendingSaveLock)
        {
            if ((_pendingSave ?? _inFlightSave) is { } accepted)
                return new SettingsLoadResult(accepted, Warning: null);
        }
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
        lock (_saveLock)
        {
            lock (_pendingSaveLock)
            {
                _pendingSave = null;
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            SaveCore(settings);
        }
    }

    public void QueueSave(AppSettings settings)
    {
        lock (_pendingSaveLock)
        {
            _pendingSave = settings;
            _saveTimer ??= Core.BackgroundTimer.Create(_ => FlushPendingSave());
            _saveTimer.Change(200, Timeout.Infinite);
        }
    }

    public void FlushPendingSave()
    {
        Exception? failure = null;
        lock (_saveLock)
        {
            AppSettings? settings;
            lock (_pendingSaveLock)
            {
                settings = _pendingSave;
                _pendingSave = null;
                _inFlightSave = settings;
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            if (settings is null) return;
            try
            {
                SaveCore(settings);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
            {
                failure = exception;
                lock (_pendingSaveLock) _pendingSave ??= settings;
            }
            finally
            {
                lock (_pendingSaveLock) _inFlightSave = null;
            }
        }
        if (failure is not null) DeferredSaveFailed?.Invoke(failure);
    }

    private void SaveCore(AppSettings settings)
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
