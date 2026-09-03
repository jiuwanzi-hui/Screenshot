using Screenshot.App.Core;

namespace Screenshot.App.Text;

internal sealed record OfflineTranslationModelFile(
    string DownloadPath,
    string InstalledFileName,
    long DownloadSize,
    long InstalledSize,
    string? InstalledSha256,
    string? DownloadMd5 = null);

internal sealed record OfflineTranslationDirection(
    string Id,
    string DisplayName,
    IReadOnlyList<OfflineTranslationModelFile> Files,
    string Configuration,
    string Version = "");

internal static class OfflineTranslationModelCatalog
{
    private static readonly string CommonConfiguration =
        "beam-size: 1\n" +
        "normalize: 1.0\n" +
        "word-penalty: 0\n" +
        "max-length-break: 128\n" +
        "mini-batch-words: 1024\n" +
        "workspace: 128\n" +
        "max-length-factor: 2.0\n" +
        "skip-cost: true\n" +
        $"cpu-threads: {HeavyWorkloadBudget.BurstCpuThreadCount}\n" +
        "quiet: true\n" +
        "quiet-translation: true\n" +
        "gemm-precision: int8shiftAlphaAll\n";

    internal static string CreateConfiguration(
        string modelFileName,
        string sourceVocabFileName,
        string targetVocabFileName,
        string? shortlistFileName)
    {
        var shortlist = string.IsNullOrWhiteSpace(shortlistFileName)
            ? string.Empty
            : $"shortlist:\n- {shortlistFileName}\n- false\n";
        return "relative-paths: true\n" +
               $"models:\n- {modelFileName}\n" +
               "vocabs:\n" +
               $"- {sourceVocabFileName}\n" +
               $"- {targetVocabFileName}\n" +
               shortlist +
               CommonConfiguration;
    }

    internal static string ApplyQuality(
        string configuration,
        OfflineTranslationQuality quality)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        var beamSize = quality switch
        {
            OfflineTranslationQuality.Fast => 1,
            OfflineTranslationQuality.High => 4,
            OfflineTranslationQuality.Ultra => 8,
            _ => 4,
        };
        var adjusted = System.Text.RegularExpressions.Regex.Replace(
            configuration,
            "(?m)^beam-size:\\s*\\d+\\s*$",
            $"beam-size: {beamSize}");
        // The installed config.yml froze the conservative background thread
        // count at download time. Translation is a user-blocking operation,
        // so rewrite the thread budget at load time: use nearly every core
        // while translating and let the engine idle-unload return them.
        return System.Text.RegularExpressions.Regex.Replace(
            adjusted,
            "(?m)^cpu-threads:\\s*\\d+\\s*$",
            $"cpu-threads: {HeavyWorkloadBudget.BurstCpuThreadCount}");
    }

    internal static string GetQualityDisplayName(OfflineTranslationQuality quality)
    {
        return quality switch
        {
            OfflineTranslationQuality.Fast => "快速",
            OfflineTranslationQuality.High => "高质量",
            OfflineTranslationQuality.Ultra => "超高质量",
            _ => "高质量",
        };
    }
}
