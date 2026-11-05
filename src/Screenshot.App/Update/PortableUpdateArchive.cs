using System.IO;
using System.IO.Compression;
using SharpCompress.Archives.SevenZip;
using Screenshot.App.Core;

namespace Screenshot.App.Update;

internal static class PortableUpdateArchive
{
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;

    internal static void ExtractExecutable(string packagePath, string destination)
    {
        var found = false;
        VisitFiles(packagePath, (name, size, copy) =>
        {
            if (!name.Equals("SnapCut.exe", StringComparison.OrdinalIgnoreCase)) return true;
            WriteFile(destination, size, copy);
            found = true;
            return false;
        });
        if (!found) throw new InvalidDataException("免安装更新包中缺少 SnapCut.exe。");
    }

    internal static void ExtractToDirectory(string packagePath, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        VisitFiles(packagePath, (name, size, copy) =>
        {
            if (IsUserDataPath(name)) return true;
            var destination = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包包含不安全的文件路径。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            WriteFile(destination, size, copy);
            return true;
        });
        if (!File.Exists(Path.Combine(root, "SnapCut.exe")))
            throw new InvalidDataException("免安装更新包中缺少 SnapCut.exe。");
    }

    private static void WriteFile(string destination, long expectedSize, Action<Stream> copy)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        copy(output);
        if (output.Length != expectedSize ||
            (output.Length == 0 && Path.GetExtension(destination).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("更新包文件长度不正确。");
    }

    private static void VisitFiles(string packagePath, Func<string, long, Action<Stream>, bool> visit)
    {
        if (Path.GetExtension(packagePath).Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = SevenZipArchive.OpenArchive(packagePath);
            ValidateEntries(archive.Entries.Select(entry =>
                (entry.Key, entry.Size, entry.IsDirectory, entry.IsEncrypted || !string.IsNullOrEmpty(entry.LinkTarget))));
            // Sequential extraction avoids restarting decompression for every file in a solid archive.
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                if (!visit(NormalizeName(reader.Entry.Key!), reader.Entry.Size, reader.WriteEntryTo)) break;
            }
            return;
        }

        if (!Path.GetExtension(packagePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不支持的便携更新包格式。");
        using var zip = ZipFile.OpenRead(packagePath);
        ValidateEntries(zip.Entries.Select(entry =>
            ((string?)entry.FullName, entry.Length, string.IsNullOrEmpty(entry.Name),
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)));
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!visit(NormalizeName(entry.FullName), entry.Length, output =>
                {
                    using var input = entry.Open();
                    input.CopyTo(output);
                })) break;
        }
    }

    private static void ValidateEntries(IEnumerable<(string? Name, long Size, bool Directory, bool Unsupported)> entries)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var count = 0;
        foreach (var entry in entries)
        {
            var name = NormalizeName(entry.Name ?? string.Empty).TrimEnd('/');
            if (++count > 4096 || entry.Unsupported || string.IsNullOrWhiteSpace(name) ||
                name.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                    segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
                !names.Add(name) || entry.Size < 0 || entry.Size > MaximumExpandedBytes - total)
                throw new InvalidDataException("更新包包含无效、重复或不安全的文件。");
            total += entry.Size;
        }
    }

    private static string NormalizeName(string name) => name.Replace('\\', '/');

    internal static bool IsUserDataPath(string name)
    {
        var firstSegment = NormalizeName(name).Split('/')[0];
        return firstSegment.Equals(AppMetadata.DataDirectoryName, StringComparison.OrdinalIgnoreCase) ||
               firstSegment.Equals(AppMetadata.TranslationModelsDirectoryName, StringComparison.OrdinalIgnoreCase);
    }
}
