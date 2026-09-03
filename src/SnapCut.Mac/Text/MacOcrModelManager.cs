using System.Security.Cryptography;

namespace SnapCut.Mac.Text;

internal sealed record MacOcrModelStatus(
    bool IsInstalled,
    string InstallationDirectory,
    long DownloadSize,
    long InstalledSize);

internal sealed record MacModelDownloadProgress(
    string FileName,
    long DownloadedBytes,
    long TotalBytes);

internal sealed class MacOcrModelManager : IDisposable
{
    private static readonly ModelFile[] ModelFiles =
    [
        new(
            "PP-OCRv6_det_small.onnx",
            9_929_594,
            "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f",
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx"),
        new(
            "PP-OCRv6_rec_small.onnx",
            21_234_383,
            "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884",
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx"),
        new(
            "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
            1_018_508,
            "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7",
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
        new(
            "ppocrv6_dict.txt",
            74_947,
            "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d",
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt"),
    ];

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromHours(6),
    };
    private readonly SemaphoreSlim _installationLock = new(1, 1);
    private bool _disposed;

    public MacOcrModelManager(string? installationDirectory = null)
    {
        InstallationDirectory = installationDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "SnapCut",
            "Models",
            "PP-OCRv6-Small");
    }

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

    public MacOcrModelStatus GetStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var total = ModelFiles.Sum(file => file.Size);
        var installed = ModelFiles.All(file =>
        {
            var path = Path.Combine(InstallationDirectory, file.FileName);
            return File.Exists(path) && new FileInfo(path).Length == file.Size;
        });
        return new MacOcrModelStatus(
            installed,
            InstallationDirectory,
            installed ? 0 : total,
            total);
    }

    public async Task<(bool IsSuccess, string? ErrorMessage)> InstallAsync(
        IProgress<MacModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _installationLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(InstallationDirectory);
            foreach (var file in ModelFiles)
            {
                await DownloadFileAsync(file, progress, cancellationToken);
            }

            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "模型下载已暂停，下次会从断点继续。");
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

    private async Task DownloadFileAsync(
        ModelFile file,
        IProgress<MacModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var finalPath = Path.Combine(InstallationDirectory, file.FileName);
        if (await IsValidAsync(finalPath, file, cancellationToken))
        {
            progress?.Report(new MacModelDownloadProgress(
                file.FileName,
                file.Size,
                file.Size));
            return;
        }

        var partialPath = finalPath + ".part";
        var existingLength = File.Exists(partialPath)
            ? new FileInfo(partialPath).Length
            : 0;
        if (existingLength > file.Size)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (existingLength > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                existingLength,
                null);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (existingLength > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            partialPath,
            existingLength > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        var downloaded = existingLength;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            downloaded += count;
            progress?.Report(new MacModelDownloadProgress(
                file.FileName,
                downloaded,
                file.Size));
        }

        await destination.FlushAsync(cancellationToken);
        destination.Close();
        if (!await IsValidAsync(partialPath, file, cancellationToken))
        {
            throw new InvalidDataException($"模型文件校验失败：{file.FileName}");
        }

        File.Move(partialPath, finalPath, overwrite: true);
    }

    private static async Task<bool> IsValidAsync(
        string path,
        ModelFile file,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(
            Convert.ToHexString(hash),
            file.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _installationLock.Dispose();
        _httpClient.Dispose();
    }

    private sealed record ModelFile(
        string FileName,
        long Size,
        string Sha256,
        string Url);
}
