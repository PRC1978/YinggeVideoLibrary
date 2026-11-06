using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// A legacy selective-import package is merged into a disposable PostgreSQL
// export. The only path back to the live library is the verified offline
// restore, which keeps an independently verified pre-restore rollback archive.
internal static class PostgresSelectiveImport
{
    internal const string DocumentName = "postgres-selective-import.json";

    internal sealed record Request(string BackupPath, string BackupManifestSha256,
        string LiveStateSha256, LibraryImportScope Scopes,
        LibraryImportConflictPolicy Policy, string MappingDigest,
        bool SkipUnindexedCategories, Dictionary<string, string> ManualChoices);

    internal static async Task<LibraryBackupService.ImportPreview> PreviewAsync(
        string connectionString, string dataDirectory, string backupDirectory,
        LibraryImportScope scopes, LibraryImportConflictPolicy policy,
        IReadOnlyDictionary<string, string>? choices, bool skipUnindexed,
        CancellationToken cancellationToken = default)
    {
        var root = TemporaryRoot();
        try
        {
            var export = Path.Combine(root, "snapshot");
            await PostgresLibraryExport.ExportAsync(connectionString, export, cancellationToken);
            return LibraryBackupService.CreateImportPreview(export, backupDirectory,
                scopes, policy, choices, skipUnindexed, dataDirectory);
        }
        finally { DeleteTemporaryRoot(root); }
    }

    internal static async Task StageAsync(string connectionString,
        string dataDirectory, LibraryBackupService.ImportPreview preview,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetFullPath(dataDirectory),
                Path.GetFullPath(preview.LogicalDestinationPath ?? ""),
                StringComparison.OrdinalIgnoreCase) || !preview.HasChanges)
            throw new InvalidOperationException("导入目标或匹配尚未确认，请重新预览。");
        var fresh = await PreviewAsync(connectionString, dataDirectory, preview.BackupPath,
            preview.Scopes, preview.ConflictPolicy, preview.ManualChoices,
            preview.SkipUnindexedCategories, cancellationToken);
        EnsureSamePreview(preview, fresh);
        var request = new Request(preview.BackupPath, preview.BackupManifestSha256,
            preview.LiveStateSha256, preview.Scopes, preview.ConflictPolicy,
            preview.MappingDigest, preview.SkipUnindexedCategories,
            new Dictionary<string, string>(preview.ManualChoices, StringComparer.OrdinalIgnoreCase));
        await PostgresLibraryDocumentStore.SaveAsync(connectionString, DocumentName,
            JsonSerializer.SerializeToUtf8Bytes(request), null, cancellationToken);
    }

    internal static async Task<bool> HasPendingAsync(string connectionString,
        CancellationToken cancellationToken = default) =>
        await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            DocumentName, cancellationToken) is not null;

    internal static async Task CancelAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        var pending = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            DocumentName, cancellationToken);
        if (pending is not null)
            await PostgresLibraryDocumentStore.DeleteAsync(connectionString,
                DocumentName, pending.Revision, cancellationToken);
    }

    // Called before opening the normal library session and before taking its
    // exclusive file lease. A preflight error leaves the database unchanged;
    // a restore error leaves the restore sentinel to fail closed if needed.
    internal static async Task<bool> ApplyPendingAtStartupAsync(string runtimeDirectory,
        string dataDirectory, CancellationToken cancellationToken = default)
    {
        if (!PostgresStorageModeGuard.PostgresMayOpen(dataDirectory))
            throw new InvalidDataException("视频库未启用数据库模式，拒绝执行数据库导入。");
        await using var cluster = await PostgresPrivateCluster.OpenAsync(runtimeDirectory,
            dataDirectory, cancellationToken);
        var pending = await PostgresLibraryDocumentStore.ReadAsync(cluster.ConnectionString,
            DocumentName, cancellationToken);
        if (pending is null) return false;
        var request = JsonSerializer.Deserialize<Request>(pending.Content)
            ?? throw new InvalidDataException("数据库选择性导入请求损坏。");
        if (request.BackupPath.Length == 0 || request.BackupManifestSha256.Length != 64 ||
            request.LiveStateSha256.Length != 64 || request.MappingDigest.Length != 64 ||
            request.ManualChoices is null)
            throw new InvalidDataException("数据库选择性导入请求不完整。");
        var plan = await PostgresBackupPlanStore.LoadAsync(cluster.ConnectionString,
            cancellationToken);
        var backupRoot = PostgresBackupPlanStore.BackupRoot(dataDirectory, plan);
        var configDocument = await PostgresLibraryDocumentStore.ReadAsync(cluster.ConnectionString,
            "config.json", cancellationToken) ?? throw new InvalidDataException("数据库缺少配置。");
        var config = JsonSerializer.Deserialize<LibraryConfig>(configDocument.Content)
            ?? throw new InvalidDataException("数据库配置无效。");
        PostgresBackupPlanStore.ValidateDestination(dataDirectory, backupRoot, config.Folders);
        Directory.CreateDirectory(backupRoot);
        var root = Path.Combine(backupRoot, ".selective-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = Path.Combine(root, "snapshot");
            await PostgresLibraryBackup.CreateAsync(cluster.ConnectionString,
                dataDirectory, snapshot, cancellationToken);
            var fresh = LibraryBackupService.CreateImportPreview(snapshot,
                request.BackupPath, request.Scopes, request.Policy,
                request.ManualChoices, request.SkipUnindexedCategories, dataDirectory);
            if (!fresh.MappingComplete || !fresh.HasChanges ||
                fresh.BackupManifestSha256 != request.BackupManifestSha256 ||
                fresh.LiveStateSha256 != request.LiveStateSha256 ||
                fresh.MappingDigest != request.MappingDigest)
                throw new InvalidOperationException("导入确认后资料或备份发生变化；数据库未修改，可取消后重新预览。");
            LibraryBackupService.ApplyImportToDisposableSnapshot(snapshot, fresh);
            File.Delete(Path.Combine(snapshot, DocumentName));
            await PostgresLibraryExport.ReSignSnapshotAsync(snapshot, cancellationToken);
            var retained = Path.Combine(backupRoot, "yingge-db-selective-" +
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                Guid.NewGuid().ToString("N"));
            Directory.Move(snapshot, retained);
            await PostgresLibraryRestore.RestoreInPlaceAsync(cluster.ConnectionString,
                dataDirectory, retained, cancellationToken);
            return true;
        }
        finally { DeleteTemporaryRoot(root); }
    }

    private static void EnsureSamePreview(LibraryBackupService.ImportPreview expected,
        LibraryBackupService.ImportPreview actual)
    {
        if (!actual.MappingComplete || !actual.HasChanges ||
            actual.BackupManifestSha256 != expected.BackupManifestSha256 ||
            actual.LiveStateSha256 != expected.LiveStateSha256 ||
            actual.MappingDigest != expected.MappingDigest)
            throw new InvalidOperationException("资料或备份在预览后变化，请重新预览。");
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "YinggePgSelective-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
