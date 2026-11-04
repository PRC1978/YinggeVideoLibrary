using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class PostgresMigrationDiagnostics
{
    internal static async Task<int> RunToFileAsync(string sourceDirectory, string resultPath)
    {
        var result = new Dictionary<string, object?>();
        try
        {
            var connection = Environment.GetEnvironmentVariable("YINGGE_POSTGRES_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("缺少隔离测试数据库连接设置。 ");
            var indexPath = Path.Combine(sourceDirectory, "library-index.json");
            var metadataPath = Path.Combine(sourceDirectory, "metadata.json");
            var beforeIndex = SHA256.HashData(await File.ReadAllBytesAsync(indexPath));
            var beforeMetadata = SHA256.HashData(await File.ReadAllBytesAsync(metadataPath));
            var imported = await PostgresLibraryMigration.ImportAsync(sourceDirectory, connection);
            var loaded = await PostgresLibraryMigration.VerifyAsync(connection);
            var contentEqual = await PostgresLibraryMigration.VerifyContentAsync(sourceDirectory, connection);
            var sourceUnchanged = beforeIndex.SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(indexPath)))
                && beforeMetadata.SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(metadataPath)));
            var countsEqual = imported == loaded;
            var duplicateRefused = false;
            try { await PostgresLibraryMigration.ImportAsync(sourceDirectory, connection); }
            catch (InvalidOperationException) { duplicateRefused = true; }
            result["passed"] = sourceUnchanged && countsEqual && contentEqual && duplicateRefused;
            result["sourceUnchanged"] = sourceUnchanged;
            result["countsEqual"] = countsEqual;
            result["contentEqual"] = contentEqual;
            result["duplicateImportRefused"] = duplicateRefused;
            result["videoCount"] = loaded.VideoCount;
            result["metadataCount"] = loaded.MetadataCount;
            result["folderCount"] = loaded.FolderCount;
        }
        catch (Exception ex)
        {
            result["passed"] = false;
            result["error"] = ex.GetType().Name + ": " + ex.Message;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
        return result.TryGetValue("passed", out var passed) && passed is true ? 0 : 1;
    }
}
