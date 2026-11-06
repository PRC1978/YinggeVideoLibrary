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
        Action? interruptBeforeSwap = null, Action? interruptAfterSwap = null,
        Action? interruptAfterMarker = null)
    {
        dataDirectory = Path.GetFullPath(dataDirectory);
        backupDirectory = Path.GetFullPath(backupDirectory);
        if (!Directory.Exists(dataDirectory))
            throw new DirectoryNotFoundException("数据目录不存在，拒绝恢复。 ");
        if (!await PostgresLibraryExport.VerifyOfflineAsync(backupDirectory, cancellationToken))
            throw new InvalidDataException("备份清单或文件校验失败；未开始恢复。 ");

        var markerPath = Path.Combine(dataDirectory, PostgresStorageModeGuard.MarkerName);
        var hasMarker = File.Exists(markerPath);
        var originalMarker = hasMarker ? await File.ReadAllBytesAsync(markerPath, cancellationToken) : null;
        using var offlineLease = new FileStream(
            Path.Combine(dataDirectory, ".library-session.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var rollbackArchive = Path.Combine(dataDirectory, "backups", "pre-restore-" + timestamp);
        var stagingSchema = "yingge_restore_" + Guid.NewGuid().ToString("N")[..12];
        var previousSchema = "yingge_prev_" + timestamp.Replace("-", "");
        var assetStage = Path.Combine(dataDirectory, ".postgres-assets-stage-" + Guid.NewGuid().ToString("N"));
        var assetPrevious = Path.Combine(dataDirectory, ".postgres-assets-prev-" + Guid.NewGuid().ToString("N"));
        var swapped = false;
        var assetsSwapped = false;
        try
        {
            PostgresStorageModeGuard.BeginRestore(dataDirectory);
            StageAssets(backupDirectory, assetStage);

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
            SwapAssets(dataDirectory, assetStage, assetPrevious);
            assetsSwapped = true;

            if (!await PostgresLibraryMigration.VerifyContentAsync(backupDirectory, connectionString,
                    cancellationToken))
                throw new InvalidDataException("替换后内容核对失败；正在回退到原数据库。 ");

            interruptAfterSwap?.Invoke();

            if (hasMarker)
                await PostgresStorageModeActivation.RewriteAfterRestoreAsync(connectionString,
                    dataDirectory, backupDirectory, cancellationToken);
            interruptAfterMarker?.Invoke();
            var counts = await PostgresLibraryMigration.VerifyAsync(connectionString, cancellationToken);
            PostgresStorageModeGuard.EndRestore(dataDirectory);
            if (File.Exists(Path.Combine(dataDirectory, PostgresStorageModeGuard.RestoreSentinelName)))
                throw new IOException("恢复已完成，但阻断标记未能清除。 ");
            try { DeletePrivateAssetDirectory(dataDirectory, assetPrevious); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { DeletePrivateAssetDirectory(dataDirectory, assetStage); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new("public", previousSchema, rollback.Directory, counts.VideoCount,
                counts.MetadataCount, hasMarker);
        }
        catch
        {
            var reverted = true;
            if (assetsSwapped || Directory.Exists(assetPrevious))
            {
                try { RevertAssets(dataDirectory, assetStage, assetPrevious); }
                catch { reverted = false; }
            }
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
            if (reverted && Directory.Exists(assetStage))
                try { DeletePrivateAssetDirectory(dataDirectory, assetStage); }
                catch { reverted = false; }
            if (reverted && originalMarker is not null)
            {
                try
                {
                    var markerTemp = markerPath + ".rollback-" + Guid.NewGuid().ToString("N");
                    await File.WriteAllBytesAsync(markerTemp, originalMarker, CancellationToken.None);
                    File.Move(markerTemp, markerPath, overwrite: true);
                }
                catch { reverted = false; }
            }
            if (reverted) PostgresStorageModeGuard.EndRestore(dataDirectory);
            throw;
        }
    }

    private static void StageAssets(string backupDirectory, string stage)
    {
        Directory.CreateDirectory(stage);
        foreach (var name in new[] { "posters", "thumbs" })
        {
            var target = Path.Combine(stage, name);
            Directory.CreateDirectory(target);
            var source = Path.Combine(backupDirectory, name);
            if (!Directory.Exists(source)) continue;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    private static void SwapAssets(string dataDirectory, string stage, string previous)
    {
        Directory.CreateDirectory(previous);
        foreach (var name in new[] { "posters", "thumbs" })
        {
            var live = Path.Combine(dataDirectory, name);
            var old = Path.Combine(previous, name);
            if (Directory.Exists(live)) Directory.Move(live, old);
            Directory.Move(Path.Combine(stage, name), live);
        }
    }

    private static void RevertAssets(string dataDirectory, string stage, string previous)
    {
        Directory.CreateDirectory(stage);
        foreach (var name in new[] { "posters", "thumbs" })
        {
            var live = Path.Combine(dataDirectory, name);
            var staged = Path.Combine(stage, name);
            var old = Path.Combine(previous, name);
            if (!Directory.Exists(staged))
            {
                if (Directory.Exists(live)) Directory.Move(live, staged);
                if (Directory.Exists(old)) Directory.Move(old, live);
            }
        }
        DeletePrivateAssetDirectory(dataDirectory, previous);
    }

    private static void DeletePrivateAssetDirectory(string dataDirectory, string target)
    {
        var root = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(target);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(resolved)?.TrimEnd(Path.DirectorySeparatorChar) !=
                root.TrimEnd(Path.DirectorySeparatorChar) ||
            !(Path.GetFileName(resolved).StartsWith(".postgres-assets-stage-", StringComparison.Ordinal) ||
              Path.GetFileName(resolved).StartsWith(".postgres-assets-prev-", StringComparison.Ordinal)))
            throw new InvalidDataException("图片暂存目录不在视频库内。 ");
        if (!Directory.Exists(resolved)) return;
        if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateDirectories(resolved, "*", SearchOption.TopDirectoryOnly).Any(path =>
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) ||
            Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories).Any(path =>
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("图片暂存目录包含文件链接。 ");
        Directory.Delete(resolved, recursive: true);
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
