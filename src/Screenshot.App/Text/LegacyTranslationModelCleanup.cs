using System.IO;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

internal static class LegacyTranslationModelCleanup
{
    internal const string DirectoryName = "Qwen2.5-1.5B-Instruct-Q4-K-M";

    // Only the app-owned sibling of the replacement is eligible. In particular,
    // never follow a junction in a portable installation or in the old runtime.
    internal static async Task<long> TryCleanAsync(
        string replacementDirectory,
        Func<CancellationToken, Task<bool>> validateReplacement,
        CancellationToken cancellationToken)
    {
        try
        {
            var replacement = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(replacementDirectory));
            if ((!Path.GetFileName(replacement).Equals(
                    AppMetadata.LocalLargeTranslationModelDirectoryName,
                    StringComparison.OrdinalIgnoreCase) &&
                 !Path.GetFileName(replacement).Equals("Hy-MT2-1.8B", StringComparison.OrdinalIgnoreCase)) ||
                !Directory.Exists(replacement) || HasLinkedAncestor(replacement))
            {
                return 0;
            }

            var root = Path.GetDirectoryName(replacement)!;
            var legacy = Path.GetFullPath(Path.Combine(root, DirectoryName));
            if (!string.Equals(Path.GetDirectoryName(legacy), root,
                    StringComparison.OrdinalIgnoreCase) || !Directory.Exists(legacy))
            {
                return 0;
            }

            if (!TryInspectTree(legacy, out _) || !TryInspectTree(replacement, out _, requireExclusiveAccess: false) ||
                !await validateReplacement(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Recheck after the potentially lengthy validation, before deletion.
            if (HasLinkedAncestor(legacy) ||
                !TryInspectTree(legacy, out var releasedBytes))
            {
                return 0;
            }

            Directory.Delete(legacy, recursive: true);
            return releasedBytes;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            // A busy old runtime or interrupted check must not fail installation
            // of the replacement. A later startup can retry the remaining files.
            return 0;
        }
    }

    private static bool HasLinkedAncestor(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null;
             directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryInspectTree(string path, out long bytes, bool requireExclusiveAccess = true)
    {
        bytes = 0;
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (entry is DirectoryInfo child)
            {
                if (!TryInspectTree(child.FullName, out var childBytes, requireExclusiveAccess)) return false;
                bytes += childBytes;
            }
            else
            {
                // Probe sharing before changing anything: a running old model
                // commonly holds its GGUF open, even when the EXE is elsewhere.
                using var stream = new FileStream(
                    entry.FullName, FileMode.Open, FileAccess.Read,
                    requireExclusiveAccess ? FileShare.None : FileShare.Read);
                bytes += stream.Length;
            }
        }

        return true;
    }
}
