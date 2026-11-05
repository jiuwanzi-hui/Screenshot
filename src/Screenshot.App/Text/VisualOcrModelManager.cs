using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public sealed class VisualOcrModelManager : IDisposable
{
    public const string DisplayName = "Qwen3-VL 4B";
    internal const string DirectoryName = "Qwen3-VL-4B-OCR";
    private const string Revision = "1cd86afb9a95c410a6038ab3b40d8b578c892266";
    internal static readonly DownloadableModelFile[] Files =
    [
        Model("视觉文字识别模型", "Qwen3VL-4B-Instruct-Q4_K_M.gguf", 2_497_281_664,
            "66358cb18bb6b3b1b6675aa412c7a88ef01d228f481184d13668e5201c730a0a"),
        Model("视觉编码器", "mmproj-Qwen3VL-4B-Instruct-F16.gguf", 836_180_256,
            "256f3a43bd4205ffef48d6b92715e1e70b5b0e9aef06522584967513a9985331"),
        new("显卡 / CPU 推理程序", "llama-b10276-bin-win-vulkan-x64.zip", 34_096_096,
            "e9f0f8cd772258a1bbc3c1f39bedbb63b2ce176ddfd1d2eecdd2be65406eba70",
            ["https://github.com/ggml-org/llama.cpp/releases/download/b10276/llama-b10276-bin-win-vulkan-x64.zip"]),
    ];
    private static DownloadableModelFile Model(string label, string file, long size, string hash) =>
        new(label, file, size, hash,
            // All mirrors must match the pinned official size and SHA-256.
            [$"https://modelscope.cn/models/Qwen/Qwen3-VL-4B-Instruct-GGUF/resolve/master/{file}",
             $"https://huggingface.co/Qwen/Qwen3-VL-4B-Instruct-GGUF/resolve/{Revision}/{file}",
             $"https://hf-mirror.com/Qwen/Qwen3-VL-4B-Instruct-GGUF/resolve/{Revision}/{file}"]);

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromHours(12) };
    private readonly SemaphoreSlim _installation = new(1, 1);
    private static readonly Lazy<VisualOcrModelManager> SharedInstance = new(() => new());
    public static VisualOcrModelManager Shared => SharedInstance.Value;
    public string InstallationDirectory { get; }
    internal string ModelPath => Path.Combine(InstallationDirectory, Files[0].FileName);
    internal string ProjectorPath => Path.Combine(InstallationDirectory, Files[1].FileName);
    internal string ExecutablePath => Path.Combine(InstallationDirectory, "runtime", "llama-server.exe");

    public VisualOcrModelManager(string? directory = null) => InstallationDirectory = Path.GetFullPath(
        directory ?? Path.Combine(AppMetadata.TranslationModelsDirectoryPath, DirectoryName));

    public HighQualityOcrModelStatus GetStatus()
    {
        long available;
        try { available = new DriveInfo(Path.GetPathRoot(InstallationDirectory)!).AvailableFreeSpace; }
        catch (IOException) { available = 0; }
        var root = InstallationDirectory + Path.DirectorySeparatorChar;
        var runtime = RuntimeIsComplete();
        return new(Files.Take(2).All(f => ResumableModelDownloader.IsFileComplete(root, f)) && runtime,
            InstallationDirectory,
            Files.Where(f => !ResumableModelDownloader.IsFileComplete(root, f)).Sum(f => f.Size),
            Files.Sum(f => f.Size) + 100_000_000, available);
    }

    internal bool RuntimeIsComplete()
    {
        var root = Path.Combine(InstallationDirectory, "runtime") + Path.DirectorySeparatorChar;
        var marker = Path.Combine(root, ".verified");
        if (!File.Exists(ExecutablePath) || !File.Exists(marker)) return false;
        try
        {
            var files = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(marker));
            return files is { Count: > 0 } && files.All(file =>
            {
                var path = Path.GetFullPath(Path.Combine(root, file.Key));
                return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path) &&
                    new FileInfo(path).Length == file.Value;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    public async Task<(bool IsSuccess, string? ErrorMessage)> InstallAsync(
        IProgress<ModelDownloadProgress>? progress, CancellationToken token)
    {
        await _installation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(InstallationDirectory);
            // Verify weights as well as the runtime when repairing. A matching
            // length alone is insufficient for imported or interrupted files.
            foreach (var file in Files)
            {
                var path = Path.Combine(InstallationDirectory, file.FileName);
                if (File.Exists(path) && new FileInfo(path).Length == file.Size &&
                    !await ResumableModelDownloader.HasExpectedHashAsync(path, file.Sha256, token).ConfigureAwait(false))
                    File.Delete(path);
            }
            var bundled = Path.Combine(AppContext.BaseDirectory, "OfflinePackages", Files[2].FileName);
            if (File.Exists(bundled))
                await ResumableModelDownloader.ImportAsync([Files[2]], InstallationDirectory, [bundled], progress, token)
                    .ConfigureAwait(false);
            await new ResumableModelDownloader(_client, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30))
                .DownloadAsync(Files, InstallationDirectory, progress, token)
                .ConfigureAwait(false);
            await Task.Run(() => ExtractRuntime(token), token).ConfigureAwait(false);
            return (true, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return (false, "视觉识别模型下载已暂停，下次继续下载。"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
        { return (false, e.Message); }
        finally { _installation.Release(); }
    }

    private void ExtractRuntime(CancellationToken token)
    {
        var directory = Path.Combine(InstallationDirectory, "runtime");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, ".verified");
        if (File.Exists(marker)) File.Delete(marker);
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(Path.Combine(InstallationDirectory, Files[2].FileName));
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var path = Path.GetFullPath(Path.Combine(directory, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("推理程序压缩包包含不安全路径。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
        if (!File.Exists(ExecutablePath)) throw new InvalidDataException("视觉识别推理程序不完整。");
        File.WriteAllText(marker, JsonSerializer.Serialize(zip.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToDictionary(entry => entry.FullName, entry => entry.Length)));
    }

    public void Dispose() { _client.Dispose(); _installation.Dispose(); }
}
