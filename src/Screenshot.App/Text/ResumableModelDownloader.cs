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
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                try
                {
                    await DownloadOneAsync(
                        file,
                        partialPath,
                        completedBytes,
                        totalBytes,
                        progress,
                        cancellationToken);
                    if (!await HasExpectedHashAsync(
                            partialPath,
                            file.Sha256,
                            cancellationToken))
                    {
                        File.Delete(partialPath);
                        throw new InvalidDataException(
                            $"{file.DisplayName} 校验失败，已丢弃损坏文件。");
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
                throw new IOException(
                    $"{file.DisplayName} 下载失败：{GetUsefulErrorMessage(lastError)}" +
                    "。已保留断点文件供下次继续。",
                    lastError);
            }
        }
    }

    private async Task DownloadOneAsync(
        DownloadableModelFile file,
        string partialPath,
        long completedBytes,
        long totalBytes,
        IProgress<ModelDownloadProgress>? progress,
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
            return;
        }

        if (existingLength > 0)
        {
            progress?.Report(new ModelDownloadProgress(
                completedBytes + existingLength,
                totalBytes,
                file.DisplayName));
        }

        Exception? lastError = null;
        foreach (var downloadUrl in file.DownloadUrls)
        {
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
                if (append && response.Content.Headers.ContentRange?.From !=
                    existingLength)
                {
                    throw new IOException(
                        $"{file.DisplayName} 的服务器续传起点不正确。");
                }
                if (!append)
                {
                    existingLength = 0;
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

                return;
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
