using System.IO;
using System.Security.Cryptography;

namespace YinggeVideoLibrary;

// Recovery intentionally targets a new empty database. It never replaces an
// active library in place, so an interrupted import cannot damage that library.
internal static class PostgresLibraryRecovery
{
    internal static async Task RestoreAssetsToNewDirectoryAsync(string snapshotDirectory,
        string destination, CancellationToken cancellationToken = default)
    {
        snapshotDirectory = Path.GetFullPath(snapshotDirectory);
        destination = Path.GetFullPath(destination);
        var relative = Path.GetRelativePath(snapshotDirectory, destination);
        if (relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative)))
            throw new InvalidDataException("图片恢复目标不能位于快照目录内。");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("图片恢复目标已存在，拒绝覆盖。");
        if (!await PostgresLibraryExport.VerifyOfflineAsync(snapshotDirectory, cancellationToken))
            throw new InvalidDataException("数据库快照校验失败，图片未恢复。");
        var staging = destination + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        foreach (var folderName in new[] { "posters", "thumbs" })
        {
            var source = Path.Combine(snapshotDirectory, folderName);
            if (!Directory.Exists(source)) continue;
            var output = Path.Combine(staging, folderName);
            Directory.CreateDirectory(output);
            foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(output, Path.GetFileName(path));
                File.Copy(path, target);
                using var input = File.OpenRead(path);
                using var copied = File.OpenRead(target);
                if (!SHA256.HashData(input).SequenceEqual(SHA256.HashData(copied)))
                    throw new InvalidDataException("图片复制后校验失败：" + Path.GetFileName(path));
            }
        }
        if (!await PostgresLibraryExport.VerifyOfflineAsync(snapshotDirectory, cancellationToken))
            throw new InvalidDataException("图片复制期间快照发生变化，未完成恢复。");
        Directory.Move(staging, destination);
    }

    internal static async Task<PostgresLibraryMigration.Result> RestoreIntoFreshDatabaseAsync(
        string snapshotDirectory, string connectionString, CancellationToken cancellationToken = default)
    {
        snapshotDirectory = Path.GetFullPath(snapshotDirectory);
        if (!await PostgresLibraryExport.VerifyOfflineAsync(snapshotDirectory, cancellationToken))
            throw new InvalidDataException("数据库快照清单或文件校验失败；未开始恢复。");

        // ImportAsync checks the database migration marker and refuses any
        // existing video or metadata rows before writing, in one transaction.
        // The immutable snapshot manifest covers every file in its directory.
        // Put the exclusive restore lease alongside it, never inside it.
        var restored = await PostgresLibraryMigration.ImportAsync(
            snapshotDirectory, connectionString, cancellationToken,
            sourceLeasePath: snapshotDirectory + ".restore.lock");
        if (!await PostgresLibraryMigration.VerifyContentAsync(
                snapshotDirectory, connectionString, cancellationToken))
            throw new InvalidDataException("数据库恢复后内容核对失败；不得启用此数据库。");
        return restored;
    }
}
