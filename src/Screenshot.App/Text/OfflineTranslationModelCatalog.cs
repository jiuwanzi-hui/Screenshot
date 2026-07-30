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
    private const string CommonConfiguration =
        "beam-size: 1\n" +
        "normalize: 1.0\n" +
        "word-penalty: 0\n" +
        "max-length-break: 128\n" +
        "mini-batch-words: 1024\n" +
        "workspace: 128\n" +
        "max-length-factor: 2.0\n" +
        "skip-cost: true\n" +
        "cpu-threads: 0\n" +
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
}
