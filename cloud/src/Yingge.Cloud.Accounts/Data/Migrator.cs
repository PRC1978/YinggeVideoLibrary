using Npgsql;

namespace Yingge.Cloud.Accounts.Data;

// Applies and rolls back the embedded migrations under a PostgreSQL advisory
// lock so two deployment instances can never migrate at the same time.
internal static class Migrator
{
    const long AdvisoryLockKey = 728_311_902;

    internal sealed record AppliedMigration(string Version, string Checksum);

    public static async Task<IReadOnlyList<string>> ApplyAsync(NpgsqlDataSource admin,
        Action<string>? log, CancellationToken ct)
    {
        var serverVersion = await Db.ReadServerVersionNumberAsync(admin, ct);
        if (serverVersion < Db.MinimumServerVersionNumber)
            throw new InvalidOperationException(
                $"需要 PostgreSQL 14 或更高版本，当前服务器为 {Db.DescribeVersion(serverVersion)}。");

        var migrations = MigrationCatalog.Load();
        var applied = new List<string>();
        await using var connection = await admin.OpenConnectionAsync(ct);
        await AcquireAsync(connection, ct);
        try
        {
            await EnsureLedgerAsync(connection, ct);
            var existing = await ReadLedgerAsync(connection, ct);

            foreach (var migration in migrations)
            {
                if (existing.TryGetValue(migration.Version, out var checksum))
                {
                    if (!string.Equals(checksum, migration.UpChecksum, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"迁移 {migration.Version}_{migration.Name} 已应用但文件内容已改变（校验和不一致）。");
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using (var command = new NpgsqlCommand(migration.UpSql, connection, transaction))
                    await command.ExecuteNonQueryAsync(ct);
                await using (var ledger = new NpgsqlCommand(
                                 "insert into schema_migrations (version, name, checksum) values (@version, @name, @checksum)",
                                 connection, transaction))
                {
                    ledger.Parameters.Add(Db.Parameter("version", NpgsqlTypes.NpgsqlDbType.Text, migration.Version));
                    ledger.Parameters.Add(Db.Parameter("name", NpgsqlTypes.NpgsqlDbType.Text, migration.Name));
                    ledger.Parameters.Add(Db.Parameter("checksum", NpgsqlTypes.NpgsqlDbType.Text, migration.UpChecksum));
                    await ledger.ExecuteNonQueryAsync(ct);
                }
                await transaction.CommitAsync(ct);
                applied.Add($"{migration.Version}_{migration.Name}");
                log?.Invoke($"applied {migration.Version}_{migration.Name}");
            }
        }
        finally
        {
            await ReleaseAsync(connection, ct);
        }

        return applied;
    }

    // Rolls back every migration newer than targetVersion. Passing null rolls
    // the database back to empty. Down scripts are intentionally explicit:
    // dropping a table with uploads must be an operator decision.
    public static async Task<IReadOnlyList<string>> RollbackAsync(NpgsqlDataSource admin,
        string? targetVersion, Action<string>? log, CancellationToken ct)
    {
        var migrations = MigrationCatalog.Load();
        var rolledBack = new List<string>();
        await using var connection = await admin.OpenConnectionAsync(ct);
        await AcquireAsync(connection, ct);
        try
        {
            await EnsureLedgerAsync(connection, ct);
            var existing = await ReadLedgerAsync(connection, ct);

            foreach (var migration in migrations.OrderByDescending(item => item.Version, StringComparer.Ordinal))
            {
                if (!existing.ContainsKey(migration.Version)) continue;
                if (targetVersion is not null &&
                    string.CompareOrdinal(migration.Version, targetVersion) <= 0) continue;
                if (string.IsNullOrWhiteSpace(migration.DownSql))
                    throw new InvalidOperationException($"迁移 {migration.Version} 没有回滚脚本。");

                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using (var command = new NpgsqlCommand(migration.DownSql, connection, transaction))
                    await command.ExecuteNonQueryAsync(ct);
                await using (var ledger = new NpgsqlCommand(
                                 "delete from schema_migrations where version = @version", connection, transaction))
                {
                    ledger.Parameters.Add(Db.Parameter("version", NpgsqlTypes.NpgsqlDbType.Text, migration.Version));
                    await ledger.ExecuteNonQueryAsync(ct);
                }
                await transaction.CommitAsync(ct);
                rolledBack.Add($"{migration.Version}_{migration.Name}");
                log?.Invoke($"rolled back {migration.Version}_{migration.Name}");
            }
        }
        finally
        {
            await ReleaseAsync(connection, ct);
        }

        return rolledBack;
    }

    public static async Task<IReadOnlyList<AppliedMigration>> ListAppliedAsync(NpgsqlDataSource admin,
        CancellationToken ct)
    {
        await using var connection = await admin.OpenConnectionAsync(ct);
        await EnsureLedgerAsync(connection, ct);
        var ledger = await ReadLedgerAsync(connection, ct);
        return ledger.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new AppliedMigration(pair.Key, pair.Value)).ToArray();
    }

    static async Task EnsureLedgerAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            create table if not exists schema_migrations (
                version text primary key,
                name text not null,
                applied_at timestamptz not null default now(),
                checksum text not null
            )
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    static async Task<Dictionary<string, string>> ReadLedgerAsync(NpgsqlConnection connection,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("select version, checksum from schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    static async Task AcquireAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("select pg_advisory_lock(@key)", connection);
        command.Parameters.Add(Db.Parameter("key", NpgsqlTypes.NpgsqlDbType.Bigint, AdvisoryLockKey));
        await command.ExecuteNonQueryAsync(ct);
    }

    static async Task ReleaseAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        try
        {
            await using var command = new NpgsqlCommand("select pg_advisory_unlock(@key)", connection);
            command.Parameters.Add(Db.Parameter("key", NpgsqlTypes.NpgsqlDbType.Bigint, AdvisoryLockKey));
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (NpgsqlException)
        {
            // The connection is closing anyway; the lock dies with the session.
        }
    }
}
