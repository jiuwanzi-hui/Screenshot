using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Screenshot.App.Text;

// Derive both execution paths from the same authenticated package. In particular,
// --device none is insufficient: b10276 still initializes dynamic GPU backends.
internal static class LocalInferenceRuntime
{
    internal static bool Include(string name, bool cpuOnly) => name is
        "llama-server.exe" or "llama-server-impl.dll" or "llama-common.dll" or
        "llama.dll" or "mtmd.dll" or "ggml.dll" or "ggml-base.dll" or "libomp140.x86_64.dll" ||
        name.StartsWith("ggml-cpu-", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal) ||
        !cpuOnly && name == "ggml-vulkan.dll";

    internal static async Task<string> PrepareAsync(string archivePath, string expectedHash,
        string destination, bool cpuOnly, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        // Multiple UI processes can briefly overlap. Never launch against another
        // process's half-written DLL; the lock has no persistent owner after exit.
        await using var ownership = await LockAsync(Path.Combine(destination, ".prepare.lock"), token).ConfigureAwait(false);
        await using var package = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(package, token).ConfigureAwait(false));
        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("离线推理程序压缩包校验失败，请在设置 → 翻译重新下载或导入推理程序。");
        package.Position = 0;
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Where(entry => Include(entry.Name, cpuOnly)).ToArray();
        if (!entries.Any(entry => entry.Name == "llama-server.exe") ||
            !entries.Any(entry => entry.Name.StartsWith("ggml-cpu-", StringComparison.Ordinal)) ||
            entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw new InvalidDataException("离线推理程序压缩包缺少组件或包含重复组件。");

        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            // Only the filename is used, so nested ZIP paths cannot escape our
            // private runtime directory. Every byte comes from the verified ZIP.
            var path = Path.Combine(destination, entry.Name);
            // This exact authenticated release has compiled-in component hashes.
            // Still hash the ZIP and installed bytes on each new UI process, but
            // do not decompress every CPU/Vulkan DLL just to rediscover its hash.
            byte[] expected;
            if (LocalInferenceRuntimeManifest.TryGetHash(hash, entry.Name, out var componentHash))
                expected = Convert.FromHexString(componentHash);
            else
            {
                await using var source = entry.Open();
                expected = await SHA256.HashDataAsync(source, token).ConfigureAwait(false);
            }
            if (File.Exists(path) && new FileInfo(path).Length == entry.Length)
            {
                await using var current = File.OpenRead(path);
                if ((await SHA256.HashDataAsync(current, token).ConfigureAwait(false)).AsSpan().SequenceEqual(expected))
                    continue;
            }
            var temporary = path + ".pending";
            try
            {
                await using (var input = entry.Open())
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        // Do not silently load stale or injected backends, especially Vulkan in
        // the CPU path. This new private folder never contains user model data.
        var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in Directory.EnumerateFiles(destination, "ggml-*.dll"))
            if (!names.Contains(Path.GetFileName(dll)))
                throw new InvalidDataException("离线推理隔离目录包含不匹配的后端：" + Path.GetFileName(dll));
        return Path.Combine(destination, "llama-server.exe");
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(5))
            { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }

    internal static void ConfigureEnvironment(ProcessStartInfo start, bool cpuOnly)
    {
        // ggml also accepts an additional backend from this environment variable.
        // Clear it only in our worker so CPU isolation cannot be bypassed by an
        // unrelated parent application's backend configuration.
        start.Environment.Remove("GGML_BACKEND_PATH");
        if (cpuOnly) start.Environment["GGML_VK_VISIBLE_DEVICES"] = string.Empty;
        // Headless compute does not need automatically injected presentation /
        // overlay layers. Keep all ICDs and explicit layers; do not change global
        // driver settings. Unsupported older loaders simply ignore this option.
        var disabled = start.Environment.TryGetValue("VK_LOADER_LAYERS_DISABLE", out var previous) ? previous : null;
        start.Environment["VK_LOADER_LAYERS_DISABLE"] = string.IsNullOrWhiteSpace(disabled)
            ? "~implicit~" : disabled + ",~implicit~";
    }
}
