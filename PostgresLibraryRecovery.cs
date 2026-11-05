using System.IO;

namespace YinggeVideoLibrary;

// Recovery intentionally targets a new empty database. It never replaces an
// active library in place, so an interrupted import cannot damage that library.
internal static class PostgresLibraryRecovery
{
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
