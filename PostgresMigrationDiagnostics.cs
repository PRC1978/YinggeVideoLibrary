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
            var firstRead = await PostgresLibraryIndexStore.ReadAsync(connection);
            var indexReadEqual = firstRead.Index.Entries.Count == imported.VideoCount &&
                firstRead.Index.Folders.Length == imported.FolderCount;
            var changedIndex = new LibraryIndex
            {
                Folders = firstRead.Index.Folders,
                Entries = firstRead.Index.Entries.Select(entry => new IndexEntry
                {
                    Path = entry.Path, Size = entry.Size, ModifiedTicks = entry.ModifiedTicks,
                    Missing = entry.Missing
                }).ToList()
            };
            if (changedIndex.Entries.Count > 0)
                changedIndex.Entries[0].Missing = !changedIndex.Entries[0].Missing;
            var nextRevision = await PostgresLibraryIndexStore.SaveAsync(
                connection, changedIndex, firstRead.Revision);
            var secondRead = await PostgresLibraryIndexStore.ReadAsync(connection);
            var indexSaved = secondRead.Revision == nextRevision &&
                secondRead.Index.Entries.Count == changedIndex.Entries.Count &&
                (changedIndex.Entries.Count == 0 ||
                 secondRead.Index.Entries.Single(entry => entry.Path == changedIndex.Entries[0].Path).Missing ==
                 changedIndex.Entries[0].Missing);
            var staleRejected = false;
            try { await PostgresLibraryIndexStore.SaveAsync(connection, changedIndex, firstRead.Revision); }
            catch (InvalidOperationException) { staleRejected = true; }
            var metadataStillPresent = (await PostgresLibraryMigration.VerifyAsync(connection)).MetadataCount ==
                imported.MetadataCount;
            var metadataRead = await PostgresLibraryMetadataStore.ReadAsync(connection);
            var metadataReadEqual = metadataRead.Records.Count == imported.MetadataCount;
            var changedMetadata = metadataRead.Records.ToDictionary(
                pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            if (changedMetadata.Count > 0)
                changedMetadata.First().Value.Title = "隔离测试已更新";
            var nextMetadataRevision = await PostgresLibraryMetadataStore.SaveAsync(
                connection, changedMetadata, metadataRead.Revision);
            var metadataAfter = await PostgresLibraryMetadataStore.ReadAsync(connection);
            var metadataSaved = metadataAfter.Revision == nextMetadataRevision &&
                metadataAfter.Records.Count == changedMetadata.Count &&
                (changedMetadata.Count == 0 ||
                 metadataAfter.Records[changedMetadata.First().Key].Title == "隔离测试已更新");
            var staleMetadataRejected = false;
            try { await PostgresLibraryMetadataStore.SaveAsync(
                connection, changedMetadata, metadataRead.Revision); }
            catch (InvalidOperationException) { staleMetadataRejected = true; }
            var indexStillPresent = (await PostgresLibraryIndexStore.ReadAsync(connection)).Revision == nextRevision;
            var renameSaved = true;
            if (changedIndex.Entries.Count > 0)
            {
                var beforePath = changedIndex.Entries[0].Path;
                var afterPath = Path.Combine(Path.GetDirectoryName(beforePath)!,
                    Path.GetFileNameWithoutExtension(beforePath) + "-renamed" + Path.GetExtension(beforePath));
                changedIndex.Entries[0].Path = afterPath;
                var renamedRevision = await PostgresLibraryIndexStore.SaveAsync(
                    connection, changedIndex, nextRevision);
                var renamed = await PostgresLibraryIndexStore.ReadAsync(connection);
                renameSaved = renamed.Revision == renamedRevision &&
                    renamed.Index.Entries.Any(entry => entry.Path == afterPath) &&
                    renamed.Index.Entries.All(entry => entry.Path != beforePath);
            }
            result["passed"] = sourceUnchanged && countsEqual && contentEqual && duplicateRefused &&
                indexReadEqual && indexSaved && staleRejected && metadataStillPresent &&
                metadataReadEqual && metadataSaved && staleMetadataRejected && indexStillPresent && renameSaved;
            result["sourceUnchanged"] = sourceUnchanged;
            result["countsEqual"] = countsEqual;
            result["contentEqual"] = contentEqual;
            result["duplicateImportRefused"] = duplicateRefused;
            result["indexReadEqual"] = indexReadEqual;
            result["indexSaved"] = indexSaved;
            result["staleWriteRejected"] = staleRejected;
            result["metadataStillPresent"] = metadataStillPresent;
            result["metadataReadEqual"] = metadataReadEqual;
            result["metadataSaved"] = metadataSaved;
            result["staleMetadataWriteRejected"] = staleMetadataRejected;
            result["indexStillPresent"] = indexStillPresent;
            result["renameSaved"] = renameSaved;
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
