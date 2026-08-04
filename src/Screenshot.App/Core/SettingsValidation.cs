using System.IO;

namespace Screenshot.App.Core;

public static class SettingsValidation
{
    public static AppSettings ValidateAndNormalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings.Normalize();

        if (normalized.SaveDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("保存位置包含无效字符。", nameof(settings));
        }

        if (normalized.VideoSaveDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("视频保存位置包含无效字符。", nameof(settings));
        }

        try
        {
            var fullSaveDirectory = Path.GetFullPath(normalized.SaveDirectory);
            var fullVideoSaveDirectory = Path.GetFullPath(
                normalized.VideoSaveDirectory);
            return normalized with
            {
                SaveDirectory = fullSaveDirectory,
                VideoSaveDirectory = fullVideoSaveDirectory,
            };
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new ArgumentException("保存位置无效。", nameof(settings), exception);
        }
    }
}
