using System.Data;
using System.IO;
using Npgsql;

namespace YinggeVideoLibrary;

// In-place restore for a library whose authoritative store is PostgreSQL.
//
// Order of operations, chosen so that every interruption point is recoverable:
//   1. verify the snapshot offline (manifest + SHA-256);
//   2. block both storage modes with the restore sentinel and take the exclusive
//      session lease, so no writer can be running;
//   3. write a verified pre-restore archive of the *current* database;
//   4. import the snapshot into a staging schema and compare it row by row;
//   5. rename public -> <previous>, staging -> public inside one transaction;
//   6. verify the promoted schema, then rewrite the activation marker.
// A failure before step 5 leaves public untouched. A failure after step 5 swaps
// back. The sentinel is only removed once the library is proven consistent, so
// an interrupted restore refuses to open in either mode instead of looking like
// a clean file-mode library.
internal static class PostgresLibraryRestore
{
    internal sealed record Result(string RestoredSchema, string PreviousSchema,
        string RollbackArchive, int VideoCount, int MetadataCount, bool MarkerRewritten);

    internal static async Task<Result> RestoreInPlaceAsync(string connectionString,
        string dataDirectory, string backupDirectory, CancellationToken cancellationToken = default,
        Action? interruptBeforeSwap = null, Action? interruptAfterSwap = null)
    {
        dataDirectory = Path.GetFullPath(dataDirectory);
        backupDirectory = Path.GetFullPath(backupDirectory);
        if (!Directory.Exists(dataDirectory))
            throw new DirectoryNotFoundException("数据目录不存在，拒绝恢复。 ");
        if (!await PostgresLibraryExport.VerifyOfflineAsync(backupDirectory, cancellationToken))
            throw new InvalidDataException("备份清单或文件校验失败；未开始恢复。 ");

        var hasMarker = File.Exists(Path.Combine(dataDirectory,
            PostgresStorageModeGuard.MarkerName));
        using var offlineLease = new FileStream(
            Path.Combine(dataDirectory, ".library-session.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var rollbackArchive = Path.Combine(dataDirectory, "backups", "pre-restore-" + timestamp);
        var stagingSchema = "yingge_restore_" + Guid.NewGuid().ToString("N")[..12];
        var previousSchema = "yingge_prev_" + timestamp.Replace("-", "");
        var swapped = false;
        try
        {
            PostgresStorageModeGuard.BeginRestore(dataDirectory);

            // Step 3: a verified copy of what is live right now. The caller
            // already holds the exclusive lease, so the backup takes it as an
            // assumption instead of trying to open the same file twice.
            var rollback = await PostgresLibraryBackup.CreateLockedAsync(connectionString,
                dataDirectory, rollbackArchive, cancellationToken);

            await PostgresLibraryMigration.ImportAsync(backupDirectory, connectionString,
                cancellationToken, sourceLeasePath: backupDirectory + ".restore.lock",
                schema: stagingSchema);
            if (!await PostgresLibraryMigration.VerifyContentAsync(backupDirectory, connectionString,
                    cancellationToken, stagingSchema))
                throw new InvalidDataException("暂存 schema 内容与备份不一致；未替换现有数据库。 ");

            interruptBeforeSwap?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            await SwapSchemasAsync(connectionString, stagingSchema, previousSchema,
                cancellationToken);
            swapped = true;

            if (!await PostgresLibraryMigration.VerifyContentAsync(backupDirectory, connectionString,
                    cancellationToken))
                throw new InvalidDataException("替换后内容核对失败；正在回退到原数据库。 ");

            interruptAfterSwap?.Invoke();

            if (hasMarker)
                await PostgresStorageModeActivation.RewriteAfterRestoreAsync(connectionString,
                    dataDirectory, backupDirectory, cancellationToken);
            var counts = await PostgresLibraryMigration.VerifyAsync(connectionString, cancellationToken);
            PostgresStorageModeGuard.EndRestore(dataDirectory);
            return new("public", previousSchema, rollback.Directory, counts.VideoCount,
                counts.MetadataCount, hasMarker);
        }
        catch
        {
            var reverted = true;
            if (swapped)
            {
                try
                {
                    await RevertSwapAsync(connectionString, previousSchema, stagingSchema,
                        CancellationToken.None);
                }
                catch { reverted = false; }
            }
            // The staging schema is the only new object a failed attempt may
            // leave behind; it never holds the live data.
            try { await DropSchemaAsync(connectionString, stagingSchema, CancellationToken.None); }
            catch { }
            if (reverted) PostgresStorageModeGuard.EndRestore(dataDirectory);
            throw;
        }
    }

    // Removes leftovers from an interrupted restore. Staging schemas are always
    // safe to drop: they never contain the live data. The sentinel is cleared
    // only when public still has a complete import marker and no swap happened.
    internal static async Task<bool> CleanupInterruptedAsync(string connectionString,
        string dataDirectory, CancellationToken cancellationToken = default)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var staging = new List<string>();
        await using (var command = new NpgsqlCommand(
            "SELECT nspname FROM pg_namespace WHERE nspname LIKE 'yingge!_restore!_%' ESCAPE '!'",
            connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) staging.Add(reader.GetString(0));
        foreach (var schema in staging)
            await DropSchemaAsync(connection, schema, cancellationToken);
        await using var previousCount = new NpgsqlCommand(
            "SELECT count(*) FROM pg_namespace WHERE nspname LIKE 'yingge!_prev!_%' ESCAPE '!'",
            connection);
        var swapped = Convert.ToInt64(await previousCount.ExecuteScalarAsync(cancellationToken) ?? 0L) > 0;
        await using var present = new NpgsqlCommand(
            "SELECT to_regclass('public.library_migration') IS NOT NULL", connection);
        var hasMarker = (bool)(await present.ExecuteScalarAsync(cancellationToken) ?? false);
        if (!hasMarker || swapped) return false;
        PostgresStorageModeGuard.EndRestore(dataDirectory);
        return true;
    }

    private static async Task SwapSchemasAsync(string connectionString, string fromSchema,
        string toSchema, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockLiveTablesAsync(connection, transaction, cancellationToken);
        await using var swap = new NpgsqlCommand(
            $"ALTER SCHEMA public RENAME TO \"{toSchema}\"; " +
            $"ALTER SCHEMA \"{fromSchema}\" RENAME TO public;", connection, transaction);
        await swap.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RevertSwapAsync(string connectionString, string previousSchema,
        string parkSchema, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockLiveTablesAsync(connection, transaction, cancellationToken);
        await using var swap = new NpgsqlCommand(
            $"ALTER SCHEMA public RENAME TO \"{parkSchema}\"; " +
            $"ALTER SCHEMA \"{previousSchema}\" RENAME TO public;", connection, transaction);
        await swap.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task LockLiveTablesAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var lockTables = new NpgsqlCommand("""
            LOCK TABLE public.library_video, public.library_metadata, public.library_document,
                       public.library_scrape_queue, public.library_folder, public.library_migration,
                       public.library_schema
            IN ACCESS EXCLUSIVE MODE
            """, connection, transaction);
        await lockTables.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DropSchemaAsync(string connectionString, string schema,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await DropSchemaAsync(connection, schema, cancellationToken);
    }

    private static async Task DropSchemaAsync(NpgsqlConnection connection, string schema,
        CancellationToken cancellationToken)
    {
        PostgresLibraryMigration.ValidateSchema(schema);
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE",
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
