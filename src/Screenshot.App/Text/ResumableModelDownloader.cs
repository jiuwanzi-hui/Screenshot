using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.IO;
using Screenshot.App.Core;
using System.Diagnostics;

namespace Screenshot.App.Text;

internal sealed record DownloadableModelFile(
    string DisplayName,
    string FileName,
    long Size,
    string Sha256,
    IReadOnlyList<string> DownloadUrls);

public sealed record ModelDownloadProgress(
    long DownloadedBytes,
    long TotalBytes,
    string CurrentFileName);

internal sealed class ResumableModelDownloader
{
    private const int MaximumAttempts = 12;
    private const long ProgressByteInterval = 4L * 1024 * 1024;
    private static readonly TimeSpan ProgressTimeInterval =
        TimeSpan.FromMilliseconds(250);
    private readonly HttpClient _httpClient;

    private sealed class ModelHashMismatchException(string downloadUrl)
        : IOException("下载文件的完整性校验失败。")
    {
        public string DownloadUrl { get; } = downloadUrl;
    }

    public ResumableModelDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient ??
            throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task DownloadAsync(
        IReadOnlyList<DownloadableModelFile> files,
        string installationDirectory,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        Directory.CreateDirectory(installationDirectory);
        var root = Path.GetFullPath(installationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var completedBytes = files
            .Where(file => IsFileComplete(root, file))
            .Sum(file => file.Size);
        var totalBytes = files.Sum(file => file.Size);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsFileComplete(root, file))
            {
                progress?.Report(new ModelDownloadProgress(
                    completedBytes,
                    totalBytes,
                    file.DisplayName));
                continue;
            }

            var destinationPath = ResolveInside(root, file.FileName);
            var partialPath = destinationPath + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            Exception? lastError = null;
            var rejectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                try
                {
                    var source = await DownloadOneAsync(
                        file,
                        partialPath,
                        completedBytes,
                        totalBytes,
                        progress,
                        attempt - 1,
                        cancellationToken);
                    progress?.Report(new ModelDownloadProgress(
                        completedBytes + file.Size,
                        totalBytes,
                        $"{file.DisplayName}（正在校验）"));
                    if (!await HasExpectedHashAsync(
                            partialPath,
                            file.Sha256,
                            cancellationToken))
                    {
                        File.Delete(partialPath);
                        throw new ModelHashMismatchException(source);
                    }

                    File.Move(partialPath, destinationPath, overwrite: true);
                    completedBytes += file.Size;
                    lastError = null;
                    break;
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException or
                        UnauthorizedAccessException or InvalidDataException or
                        OperationCanceledException)
                {
                    lastError = exception;
                    if (exception is ModelHashMismatchException hashMismatch)
                    {
                        rejectedSources.Add(hashMismatch.DownloadUrl);
                        if (rejectedSources.Count >= file.DownloadUrls.Count)
                        {
                            break;
                        }
                    }
                    if (attempt < MaximumAttempts)
                    {
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(
                                Math.Min(5_000, 500 * attempt)),
                            cancellationToken);
                    }
                }
            }

            if (lastError is not null)
            {
                if (lastError is ModelHashMismatchException &&
                    rejectedSources.Count > 0)
                {
                    throw new IOException(
                        $"{file.DisplayName} 已从 {rejectedSources.Count} 个下载源完成传输，" +
                        "但完整性校验均未通过。请稍后重试，或检查网络代理、下载加速器是否篡改了文件。",
                        lastError);
                }
                throw new IOException(
                    $"{file.DisplayName} 下载失败：{GetUsefulErrorMessage(lastError)}" +
                    "。已保留断点文件供下次继续。",
                    lastError);
            }
        }
    }

    private async Task<string> DownloadOneAsync(
        DownloadableModelFile file,
        string partialPath,
        long completedBytes,
        long totalBytes,
        IProgress<ModelDownloadProgress>? progress,
        int sourceOffset,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partialPath)
            ? new FileInfo(partialPath).Length
            : 0;
        if (existingLength > file.Size)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }

        if (existingLength == file.Size)
        {
            progress?.Report(new ModelDownloadProgress(
                completedBytes + file.Size,
                totalBytes,
                file.DisplayName));
            return partialPath;
        }

        if (existingLength > 0)
        {
            progress?.Report(new ModelDownloadProgress(
                completedBytes + existingLength,
                totalBytes,
                file.DisplayName));
        }

        Exception? lastError = null;
        for (var sourceIndex = 0; sourceIndex < file.DownloadUrls.Count; sourceIndex++)
        {
            var downloadUrl = file.DownloadUrls[
                (sourceOffset + sourceIndex) % file.DownloadUrls.Count];
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    downloadUrl);
                request.Headers.UserAgent.ParseAdd(
                    $"SnapCut/{AppMetadata.CurrentVersion}");
                request.Headers.Accept.ParseAdd("application/octet-stream");
                if (existingLength > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(
                        existingLength,
                        null);
                }

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"服务器返回 HTTP {(int)response.StatusCode} " +
                        $"({response.ReasonPhrase})",
                        inner: null,
                        response.StatusCode);
                }
                var append = existingLength > 0 &&
                             response.StatusCode == HttpStatusCode.PartialContent;
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.Length is { } totalLength &&
                    totalLength != file.Size)
                {
                    throw new IOException(
                        $"{file.DisplayName} 的服务器返回了错误的文件大小。");
                }
                if (append && contentRange?.From != existingLength)
                {
                    throw new IOException(
                        $"{file.DisplayName} 的服务器续传起点不正确。");
                }
                if (!append)
                {
                    existingLength = 0;
                }
                var expectedContentLength = file.Size - existingLength;
                if (response.Content.Headers.ContentLength is { } contentLength &&
                    contentLength != expectedContentLength)
                {
                    throw new IOException(
                        $"{file.DisplayName} 的服务器返回了不完整的文件内容。");
                }

                await using var source = await response.Content
                    .ReadAsStreamAsync(cancellationToken);
                await using var destination = new FileStream(
                    partialPath,
                    append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                try
                {
                    var downloaded = existingLength;
                    var lastReportedBytes = existingLength;
                    var lastReportedTime = Stopwatch.GetTimestamp();
                    while (true)
                    {
                        var read = await source.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        await destination.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken);
                        downloaded += read;
                        var shouldReport = downloaded >= file.Size ||
                                           downloaded - lastReportedBytes >=
                                           ProgressByteInterval ||
                                           Stopwatch.GetElapsedTime(
                                               lastReportedTime) >=
                                           ProgressTimeInterval;
                        if (shouldReport)
                        {
                            progress?.Report(new ModelDownloadProgress(
                                completedBytes + Math.Min(downloaded, file.Size),
                                totalBytes,
                                file.DisplayName));
                            lastReportedBytes = downloaded;
                            lastReportedTime = Stopwatch.GetTimestamp();
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (new FileInfo(partialPath).Length != file.Size)
                {
                    throw new IOException(
                        $"{file.DisplayName} 文件大小不完整。");
                }

                return downloadUrl;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or
                    OperationCanceledException)
            {
                lastError = exception;
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                // A network failure may happen after part of the response was
                // already committed to disk. Continue the next mirror from the
                // file's actual length, rather than appending that range twice.
                existingLength = File.Exists(partialPath)
                    ? new FileInfo(partialPath).Length
                    : 0;
                if (existingLength > file.Size)
                {
                    throw new IOException(
                        $"{file.DisplayName} 的断点文件大小异常，已停止自动重试。",
                        exception);
                }
                if (existingLength == file.Size)
                {
                    return downloadUrl;
                }
            }
        }

        throw new IOException(
            $"{file.DisplayName} 的所有下载地址均不可用。",
            lastError);
    }

    private static string GetUsefulErrorMessage(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return string.IsNullOrWhiteSpace(current.Message)
            ? exception.Message
            : current.Message.Trim().TrimEnd('.');
    }

    internal static bool IsFileComplete(
        string root,
        DownloadableModelFile file)
    {
        var path = ResolveInside(root, file.FileName);
        return File.Exists(path) && new FileInfo(path).Length == file.Size;
    }

    internal static async Task<bool> HasExpectedHashAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(
            expectedSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveInside(string root, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("模型文件路径超出了安装目录。");
        }

        return path;
    }
}
