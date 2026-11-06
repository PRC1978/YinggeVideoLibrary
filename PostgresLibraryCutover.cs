using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Offline conversion only. The caller must not have a normal library window
// open. An exclusive lease and a verified snapshot precede the active marker.
internal static class PostgresLibraryCutover
{
    internal const string PendingName = "postgres-cutover.pending";
    internal sealed record Result(string DataDirectory, string BackupDirectory,
        int VideoCount, int MetadataCount, bool Reopened);

    internal static async Task<Result> ConvertAsync(string runtimeDirectory,
        string dataDirectory, CancellationToken cancellationToken = default)
    {
        runtimeDirectory = Path.GetFullPath(runtimeDirectory);
        dataDirectory = Path.GetFullPath(dataDirectory);
        if (PostgresStorageModeGuard.Current(dataDirectory) !=
            PostgresStorageModeGuard.StorageMode.LegacyFiles)
            throw new InvalidDataException("该视频库不是可转换的文件模式；未修改数据库。");
        foreach (var name in new[] { "config.json", "library-index.json", "metadata.json" })
            if (!File.Exists(Path.Combine(dataDirectory, name)))
                throw new InvalidDataException("缺少文件模式资料：" + name);
        foreach (var name in new[] { "restore-pending.json", "restore-active.json",
            "import-pending.json", "import-active.json", "backup-pending.json",
            "review-transaction-pending.json", "duplicate-metadata-pending.json",
            "library-relocation-pending.json", ".postgres-restore-in-progress" })
            if (File.Exists(Path.Combine(dataDirectory, name)))
                throw new InvalidDataException("旧库尚有待完成操作：" + name + "。请先用文件模式完成该操作。");
        var hasStagedDatabase = Directory.Exists(Path.Combine(dataDirectory, "postgres-data"));
        using var lease = LibraryBackupService.AcquireSessionLease(dataDirectory);
        if (PostgresStorageModeGuard.Current(dataDirectory) !=
            PostgresStorageModeGuard.StorageMode.LegacyFiles)
            throw new InvalidDataException("视频库存储状态已变化，请重新检查。");

        var backup = dataDirectory + ".pre-postgres-" +
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" +
            Guid.NewGuid().ToString("N")[..8];
        await using (var cluster = await PostgresPrivateCluster.OpenAsync(
            runtimeDirectory, dataDirectory, cancellationToken))
        {
            if (!hasStagedDatabase)
            {
                var readerLease = dataDirectory + ".cutover-import-" +
                    Guid.NewGuid().ToString("N") + ".lock";
                try
                {
                    await PostgresLibraryMigration.ImportAsync(dataDirectory,
                        cluster.ConnectionString, cancellationToken: cancellationToken,
                        // The converter already owns .library-session.lock.
                        sourceLeasePath: readerLease);
                }
                finally
                {
                    try { File.Delete(readerLease); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            if (!await PostgresLibraryMigration.VerifyContentAsync(dataDirectory,
                cluster.ConnectionString, cancellationToken))
                throw new InvalidDataException("数据库与原文件资料不一致；未启用数据库模式。已有暂存库不会被覆盖。");
            await PostgresLibraryBackup.CreateLockedAsync(cluster.ConnectionString,
                dataDirectory, backup, cancellationToken);
            await PostgresStorageModeActivation.ActivateAsync(cluster.ConnectionString,
                dataDirectory, backup, cancellationToken);
        }
        // A marker is useful only if the application can actually reopen it.
        await using var reopened = await PostgresLibraryRuntimeSession.OpenAsync(
            runtimeDirectory, dataDirectory, cancellationToken);
        return new(dataDirectory, backup, reopened.Index.Index.Entries.Count,
            reopened.Metadata.Records.Count, true);
    }

    internal static void Stage(string dataDirectory)
    {
        dataDirectory = Path.GetFullPath(dataDirectory);
        if (PostgresStorageModeGuard.Current(dataDirectory) !=
            PostgresStorageModeGuard.StorageMode.LegacyFiles)
            throw new InvalidDataException("当前视频库已不是文件模式。");
        var path = Path.Combine(dataDirectory, PendingName);
        using var stream = new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        stream.Write("postgresql-v1\n"u8);
        stream.Flush(true);
    }

    internal static async Task<bool> ApplyPendingAsync(string runtimeDirectory,
        string dataDirectory, CancellationToken cancellationToken = default)
    {
        var pending = Path.Combine(dataDirectory, PendingName);
        if (!File.Exists(pending)) return false;
        if (File.ReadAllText(pending) != "postgresql-v1\n")
            throw new InvalidDataException("数据库转换待办标记内容无效。");
        if (PostgresStorageModeGuard.Current(dataDirectory) ==
            PostgresStorageModeGuard.StorageMode.LegacyFiles)
            await ConvertAsync(runtimeDirectory, dataDirectory, cancellationToken);
        if (!PostgresStorageModeGuard.PostgresMayOpen(dataDirectory))
            throw new InvalidDataException("数据库转换尚未完成，拒绝载入资料。");
        File.Delete(pending);
        return true;
    }

    internal static async Task<int> TestPendingToFileAsync(string runtimeDirectory,
        string dataDirectory, string reportPath)
    {
        var report = new Dictionary<string, object?> { ["passed"] = false,
            ["realLibraryRead"] = false };
        try
        {
            dataDirectory = Path.GetFullPath(dataDirectory);
            if (!dataDirectory.StartsWith(Path.GetFullPath(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("待办诊断仅允许临时目录。");
            Stage(dataDirectory);
            var blocking = Path.Combine(dataDirectory, "restore-pending.json");
            File.WriteAllText(blocking, "{}");
            try
            {
                try { await ApplyPendingAsync(runtimeDirectory, dataDirectory); }
                catch (InvalidDataException) { }
                report["pendingPreflightBlocked"] =
                    !Directory.Exists(Path.Combine(dataDirectory, "postgres-data")) &&
                    File.Exists(Path.Combine(dataDirectory, PendingName));
            }
            finally { File.Delete(blocking); }
            var applied = await ApplyPendingAsync(runtimeDirectory, dataDirectory);
            await using var session = await PostgresLibraryRuntimeSession.OpenAsync(
                runtimeDirectory, dataDirectory);
            report["videos"] = session.Index.Index.Entries.Count;
            report["pendingGone"] = !File.Exists(Path.Combine(dataDirectory, PendingName));
            report["oldFilesKept"] = new[] { "config.json", "library-index.json",
                "metadata.json" }.All(name => File.Exists(Path.Combine(dataDirectory, name)));
            report["passed"] = applied && Equals(report["pendingPreflightBlocked"], true) &&
                Equals(report["pendingGone"], true) &&
                Equals(report["oldFilesKept"], true);
        }
        catch (Exception error) { report["error"] = error.ToString(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report));
        return Equals(report["passed"], true) ? 0 : 1;
    }

    internal static async Task<int> RunToFileAsync(string runtimeDirectory,
        string dataDirectory, string reportPath)
    {
        var report = new Dictionary<string, object?> { ["passed"] = false };
        try
        {
            report["result"] = await ConvertAsync(runtimeDirectory, dataDirectory);
            report["passed"] = true;
        }
        catch (Exception error) { report["error"] = error.ToString(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report));
        return Equals(report["passed"], true) ? 0 : 1;
    }
}
