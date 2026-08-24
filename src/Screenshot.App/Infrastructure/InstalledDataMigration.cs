using System.IO;
using System.Security.Cryptography;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

public sealed record InstalledDataMigrationResult(bool Migrated, string? Warning);

public static class InstalledDataMigration
{
    public static InstalledDataMigrationResult TryMigrateLegacyData()
    {
        if (!AppMetadata.IsInstalled)
        {
            return new InstalledDataMigrationResult(Migrated: false, Warning: null);
        }

        return TryMigrateDirectory(
            AppMetadata.LegacyInstalledDataDirectoryPath,
            AppMetadata.DataDirectoryPath);
    }

    internal static InstalledDataMigrationResult TryMigrateDirectory(
        string sourceDirectory,
        string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return new InstalledDataMigrationResult(Migrated: false, Warning: null);
        }

        try
        {
            var sourceFiles = Directory.GetFiles(
                sourceDirectory,
                "*",
                SearchOption.AllDirectories);

            foreach (var sourceFile in sourceFiles)
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
                var destinationFile = Path.Combine(destinationDirectory, relativePath);
                if (File.Exists(destinationFile) && !FilesAreEqual(sourceFile, destinationFile))
                {
                    return new InstalledDataMigrationResult(
                        Migrated: false,
                        Warning: "安装目录和旧版位置都存在数据，为避免覆盖，未自动迁移旧版数据。");
                }
            }

            foreach (var sourceFile in sourceFiles)
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
                var destinationFile = Path.Combine(destinationDirectory, relativePath);
                var destinationParent = Path.GetDirectoryName(destinationFile)
                    ?? throw new InvalidOperationException("无法确定迁移目标目录。");

                Directory.CreateDirectory(destinationParent);
                if (!File.Exists(destinationFile))
                {
                    File.Copy(sourceFile, destinationFile);
                }
            }

            Directory.Delete(sourceDirectory, recursive: true);
            return new InstalledDataMigrationResult(Migrated: true, Warning: null);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or CryptographicException)
        {
            return new InstalledDataMigrationResult(
                Migrated: false,
                Warning: "检测到旧版数据，但无法自动迁移到安装目录；旧数据仍保留在原位置。");
        }
    }

    private static bool FilesAreEqual(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        using var firstStream = File.OpenRead(firstPath);
        using var secondStream = File.OpenRead(secondPath);
        var firstHash = SHA256.HashData(firstStream);
        var secondHash = SHA256.HashData(secondStream);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }
}
