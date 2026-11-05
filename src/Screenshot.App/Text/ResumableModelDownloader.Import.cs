using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Screenshot.App.Text;

internal sealed partial class ResumableModelDownloader
{
    // Import never makes network requests and never installs an unverified
    // model/runtime. Stage the entire selection before replacing any files.
    internal static async Task ImportAsync(IReadOnlyList<DownloadableModelFile> files,
        string installationDirectory, IReadOnlyList<string> paths,
        IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) throw new InvalidDataException("请选择本地模型文件或推理程序压缩包。");
        var selection = paths.Select(path => (Path: Path.GetFullPath(path), File: files.FirstOrDefault(file =>
            string.Equals(file.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))).ToArray();
        if (selection.Any(item => item.File is null))
            throw new InvalidDataException("所选文件不属于当前版本。需要：" + string.Join("、", files.Select(file => file.FileName)));
        if (selection.Select(item => item.File!.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selection.Length)
            throw new InvalidDataException("同一模型文件请只选择一份。");
        var total = selection.Sum(item => item.File!.Size);
        var completed = 0L;
        var staged = new List<(string Temporary, string Destination)>();
        Directory.CreateDirectory(installationDirectory);
        try
        {
            foreach (var (path, descriptor) in selection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = descriptor!;
                if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
                    throw new InvalidDataException($"{file.FileName} 大小不正确，需要 {file.Size:N0} 字节。请使用当前版本的完整文件。");
                var destination = ResolveInside(installationDirectory, file.FileName);
                var temporary = destination + ".import-" + Guid.NewGuid().ToString("N");
                staged.Add((temporary, destination));
                progress?.Report(new(completed, total, file.DisplayName, "正在本地复制并校验"));
                await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                    try
                    {
                        var written = 0L;
                        var lastReport = Stopwatch.GetTimestamp();
                        while (true)
                        {
                            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (read == 0) break;
                            written += read;
                            if (written > file.Size) throw new InvalidDataException("本地文件在导入期间发生了变化。");
                            hash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            if (Stopwatch.GetElapsedTime(lastReport) >= ProgressTimeInterval || written == file.Size)
                            {
                                progress?.Report(new(completed + written, total, file.DisplayName, "正在本地复制并校验"));
                                lastReport = Stopwatch.GetTimestamp();
                            }
                        }
                        if (written != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"{file.FileName} 的 SHA-256 校验失败，未替换已安装文件。请重新获取当前版本的原始文件。");
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
                completed += file.Size;
            }
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (temporary, destination) in staged) File.Move(temporary, destination, overwrite: true);
            progress?.Report(new(completed, total, "本地文件", "校验通过，正在安装"));
        }
        finally
        {
            foreach (var (temporary, _) in staged)
                if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
