using System.IO;

namespace YinggeVideoLibrary;

// Offline, whole-library operations. An active file-mode library is never
// overwritten: restore publishes a verified new PostgreSQL library directory.
internal static class PostgresLibraryBackupCoordinator
{
    internal static async Task<PostgresLibraryExport.Result> CreateOfflineSnapshotAsync(
        string connectionString, string libraryDirectory, string snapshotDirectory,
        CancellationToken cancellationToken = default)
    {
        libraryDirectory = Path.GetFullPath(libraryDirectory);
        snapshotDirectory = Path.GetFullPath(snapshotDirectory);
        if (!File.Exists(Path.Combine(libraryDirectory, PostgresStorageModeGuard.MarkerName)))
            throw new InvalidDataException("视频库尚未启用 PostgreSQL，不能按数据库模式备份。");
        if (Path.GetRelativePath(libraryDirectory, snapshotDirectory) is var relative &&
            (relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) &&
                                  !Path.IsPathRooted(relative))))
            throw new InvalidDataException("备份目录不能位于视频库内部。");
        return await PostgresLibraryExport.ExportAsync(connectionString,
            snapshotDirectory, cancellationToken, assetDirectory: libraryDirectory);
    }

    internal static async Task<PostgresLibraryMigration.Result> RestoreToFreshPrivateLibraryAsync(
        string runtimeDirectory, string snapshotDirectory, string destination,
        CancellationToken cancellationToken = default)
    {
        runtimeDirectory = Path.GetFullPath(runtimeDirectory);
        snapshotDirectory = Path.GetFullPath(snapshotDirectory);
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("恢复目标已存在；不会覆盖原视频库。");
        var relative = Path.GetRelativePath(snapshotDirectory, destination);
        if (relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative)))
            throw new InvalidDataException("恢复目标不能位于快照目录内部。");
        if (!await PostgresLibraryExport.VerifyOfflineAsync(snapshotDirectory, cancellationToken))
            throw new InvalidDataException("快照校验失败；未创建目标视频库。");

        var staging = destination + ".restore-" + Guid.NewGuid().ToString("N");
        var sourceCredentialName = PostgresPrivateCluster.CredentialNameFor(staging);
        var targetCredentialName = PostgresPrivateCluster.CredentialNameFor(destination);
        if (WindowsCredentialStore.Read(targetCredentialName) is not null)
            throw new InvalidDataException("恢复目标已有数据库凭据，拒绝覆盖。");
        var targetCredentialCreated = false;
        var published = false;
        try
        {
            PostgresLibraryMigration.Result result;
            await using (var cluster = await PostgresPrivateCluster.OpenAsync(
                runtimeDirectory, staging, cancellationToken))
            {
                result = await PostgresLibraryRecovery.RestoreIntoFreshDatabaseAsync(
                    snapshotDirectory, cluster.ConnectionString, cancellationToken);
                var assets = Path.Combine(staging, "restored-assets");
                await PostgresLibraryRecovery.RestoreAssetsToNewDirectoryAsync(
                    snapshotDirectory, assets, cancellationToken);
                foreach (var folder in new[] { "posters", "thumbs" })
                {
                    var source = Path.Combine(assets, folder);
                    if (Directory.Exists(source)) Directory.Move(source, Path.Combine(staging, folder));
                }
                Directory.Delete(assets);
                if (!await PostgresLibraryMigration.VerifyContentAsync(
                    snapshotDirectory, cluster.ConnectionString, cancellationToken))
                    throw new InvalidDataException("数据库恢复内容不一致；目标未启用。");
            }

            var secret = WindowsCredentialStore.Read(sourceCredentialName)
                ?? throw new InvalidDataException("恢复用数据库凭据丢失；目标未启用。");
            WindowsCredentialStore.Write(targetCredentialName, secret);
            targetCredentialCreated = true;
            // The marker travels with the verified staging directory. There is
            // no interval in which the published target looks like a file-mode
            // library and could be opened by the legacy writer.
            await File.WriteAllTextAsync(Path.Combine(staging,
                PostgresStorageModeGuard.MarkerName), "postgresql-v1\n", cancellationToken);
            Directory.Move(staging, destination);
            published = true;

            try
            {
                await using var reopened = await PostgresPrivateCluster.OpenAsync(
                    runtimeDirectory, destination, cancellationToken);
                if (!await PostgresLibraryMigration.VerifyContentAsync(
                    snapshotDirectory, reopened.ConnectionString, cancellationToken))
                    throw new InvalidDataException("目标目录重开后校验失败。");
            }
            catch
            {
                // Keep the active marker if a concurrent process prevents a
                // safe rename back; the old file writer will still refuse it.
                try { Directory.Move(destination, staging); published = false; }
                catch { throw new InvalidDataException(
                    "恢复目标无法重开且回退目录失败；保留 PostgreSQL 标记，禁止旧版写入。"); }
                throw;
            }
            try { WindowsCredentialStore.Write(sourceCredentialName, ""); }
            catch { /* The target credential is verified; an orphan staging key must not report restore failure. */ }
            return result;
        }
        finally
        {
            if (!published)
            {
                if (targetCredentialCreated) WindowsCredentialStore.Write(targetCredentialName, "");
                if (Directory.Exists(staging) &&
                    Path.GetDirectoryName(staging) == Path.GetDirectoryName(destination) &&
                    (File.GetAttributes(staging) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(staging, recursive: true);
                WindowsCredentialStore.Write(sourceCredentialName, "");
            }
        }
    }
}
