using Screenshot.App.Core;
using System.Net.Http;
using System.IO;

namespace Screenshot.App.Text;

public sealed record HighQualityOcrModelStatus(
    bool IsInstalled,
    string InstallationDirectory,
    long DownloadSize,
    long InstalledSize,
    long AvailableSpace);

public sealed class HighQualityOcrModelManager : IDisposable
{
    private static readonly DownloadableModelFile[] ModelFiles =
    [
        new(
            "文字检测模型",
            "PP-OCRv6_det_small.onnx",
            9_929_594,
            "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f",
            ["https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx"]),
        new(
            "多语言文字识别模型",
            "PP-OCRv6_rec_small.onnx",
            21_234_383,
            "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884",
            ["https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx"]),
        new(
            "文字方向模型",
            "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
            1_018_508,
            "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7",
            ["https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"]),
        new(
            "多语言字符表",
            "ppocrv6_dict.txt",
            74_947,
            "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d",
            ["https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt"]),
    ];

    private static readonly Lazy<HighQualityOcrModelManager> SharedManager =
        new(() => new HighQualityOcrModelManager());
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _installationLock = new(1, 1);
    private bool _disposed;

    public HighQualityOcrModelManager(
        string? installationDirectory = null,
        HttpClient? httpClient = null)
    {
        InstallationDirectory = Path.GetFullPath(
            installationDirectory ?? Path.Combine(
                AppMetadata.TranslationModelsDirectoryPath,
                AppMetadata.HighQualityOcrModelDirectoryName));
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromHours(6),
        };
        _ownsHttpClient = httpClient is null;
    }

    public static HighQualityOcrModelManager Shared => SharedManager.Value;

    public string InstallationDirectory { get; }

    public string DetectionModelPath => Path.Combine(
        InstallationDirectory,
        ModelFiles[0].FileName);

    public string RecognitionModelPath => Path.Combine(
        InstallationDirectory,
        ModelFiles[1].FileName);

    public string ClassificationModelPath => Path.Combine(
        InstallationDirectory,
        ModelFiles[2].FileName);

    public string DictionaryPath => Path.Combine(
        InstallationDirectory,
        ModelFiles[3].FileName);

    public HighQualityOcrModelStatus GetStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var installed = ModelFiles.All(file =>
            ResumableModelDownloader.IsFileComplete(
                InstallationDirectory.TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar,
                file));
        var size = ModelFiles.Sum(file => file.Size);
        return new HighQualityOcrModelStatus(
            installed,
            InstallationDirectory,
            installed ? 0 : size,
            size,
            GetAvailableSpace());
    }

    public async Task<(bool IsSuccess, string? ErrorMessage)> InstallAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _installationLock.WaitAsync(cancellationToken);
        try
        {
            var downloader = new ResumableModelDownloader(_httpClient);
            await downloader.DownloadAsync(
                ModelFiles,
                InstallationDirectory,
                progress,
                cancellationToken);
            return (true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, "高质量识别模型下载已暂停，下次会从断点继续。");
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or
                UnauthorizedAccessException or InvalidDataException)
        {
            return (false, exception.Message);
        }
        finally
        {
            _installationLock.Release();
        }
    }

    private long GetAvailableSpace()
    {
        try
        {
            var root = Path.GetPathRoot(InstallationDirectory);
            return string.IsNullOrWhiteSpace(root)
                ? 0
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _installationLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
