using System.Net;
using Npgsql;
using Yingge.Cloud.Accounts;
using Yingge.Cloud.Accounts.Data;

namespace Yingge.Cloud.Tests;

[Collection(CloudCollection.Name)]
public sealed class DatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Migrations_are_recorded_with_checksums_and_reapplying_is_a_no_op()
    {
        var catalog = MigrationCatalog.Load();
        Assert.Equal(2, catalog.Count);

        var applied = await Migrator.ListAppliedAsync(fixture.OwnerDataSource, CancellationToken.None);
        Assert.Equal(catalog.Count, applied.Count);

        foreach (var migration in catalog)
        {
            var row = Assert.Single(applied, item => item.Version == migration.Version);
            Assert.Equal(migration.UpChecksum, row.Checksum);
        }

        var second = await Migrator.ApplyAsync(fixture.OwnerDataSource, null, CancellationToken.None);
        Assert.Empty(second);
    }

    [Fact]
    public async Task Rollback_drops_everything_and_a_fresh_apply_recreates_it()
    {
        var database = "yingge_cloud_rollback_" + Guid.NewGuid().ToString("N")[..6];
        var connectionString = await fixture.CreateDatabaseAsync(database);
        await using var dataSource = Db.Create(connectionString, 2, "tests-rollback");

        var applied = await Migrator.ApplyAsync(dataSource, null, CancellationToken.None);
        Assert.NotEmpty(applied);
        Assert.Equal(1, await ScalarAsync<int>(dataSource,
            "select count(*)::int from pg_tables where schemaname = 'public' and tablename = 'error_reports'"));

        var rolledBack = await Migrator.RollbackAsync(dataSource, null, null, CancellationToken.None);
        Assert.Equal(applied.Count, rolledBack.Count);
        Assert.Equal(0, await ScalarAsync<int>(dataSource,
            "select count(*)::int from pg_tables where schemaname = 'public' and tablename = 'error_reports'"));
        Assert.Equal(0, await ScalarAsync<int>(dataSource,
            "select count(*)::int from pg_tables where schemaname = 'public' and tablename = 'accounts'"));

        var reapplied = await Migrator.ApplyAsync(dataSource, null, CancellationToken.None);
        Assert.Equal(applied.Count, reapplied.Count);
        Assert.Equal(1, await ScalarAsync<int>(dataSource,
            "select count(*)::int from pg_tables where schemaname = 'public' and tablename = 'accounts'"));
    }

    [Fact]
    public async Task Partial_rollback_keeps_the_target_migration()
    {
        var database = "yingge_cloud_partial_" + Guid.NewGuid().ToString("N")[..6];
        var connectionString = await fixture.CreateDatabaseAsync(database);
        await using var dataSource = Db.Create(connectionString, 2, "tests-partial");

        await Migrator.ApplyAsync(dataSource, null, CancellationToken.None);
        var rolledBack = await Migrator.RollbackAsync(dataSource, "0001", null, CancellationToken.None);

        Assert.Single(rolledBack);
        Assert.Equal(0, await ScalarAsync<int>(dataSource,
            "select count(*)::int from pg_tables where schemaname = 'public' and tablename = 'error_reports'"));
        Assert.Equal(1, await ScalarAsync<int>(dataSource,
            "select count(*)::int from pg_tables where schemaname = 'public' and tablename = 'accounts'"));
    }

    [Fact]
    public async Task Backup_and_restore_rehearsal_keeps_rows_and_ledger()
    {
        await TestSupport.CreateInviteAsync(fixture, note: "backup-rehearsal");
        var dumpPath = Path.Combine(fixture.Root, "dump-" + Guid.NewGuid().ToString("N")[..6] + ".sql");
        await fixture.RunToolAsync("pg_dump",
            $"-h 127.0.0.1 -p {fixture.Port} -U yingge_owner -d yingge_cloud_test " +
            $"--no-owner --no-privileges -f \"{dumpPath}\"");
        Assert.True(File.Exists(dumpPath));
        Assert.True(new FileInfo(dumpPath).Length > 0);

        var restoreDatabase = "yingge_cloud_restore_" + Guid.NewGuid().ToString("N")[..6];
        await fixture.CreateDatabaseAsync(restoreDatabase);
        await fixture.RunToolAsync("psql",
            $"-h 127.0.0.1 -p {fixture.Port} -U yingge_owner -d {restoreDatabase} " +
            $"-v ON_ERROR_STOP=1 -q -f \"{dumpPath}\"");

        await using var restored = new NpgsqlConnection(fixture.ConnectionStringFor(restoreDatabase));
        await restored.OpenAsync();
        await using var invites = new NpgsqlCommand(
            "select count(*)::int from invites where note = 'backup-rehearsal'", restored);
        Assert.Equal(1, (int)(await invites.ExecuteScalarAsync())!);
        await using var ledger = new NpgsqlCommand("select count(*)::int from schema_migrations", restored);
        Assert.Equal(2, (int)(await ledger.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Application_role_has_no_superuser_and_no_ddl()
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();

        await using (var role = new NpgsqlCommand(
                         "select rolsuper from pg_roles where rolname = current_user", connection))
            Assert.False((bool)(await role.ExecuteScalarAsync())!);

        await using (var ddl = new NpgsqlCommand("create table should_not_exist (id int)", connection))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ddl.ExecuteNonQueryAsync());
            Assert.Equal("42501", exception.SqlState);
        }

        await using (var authid = new NpgsqlCommand("select count(*) from pg_authid", connection))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => authid.ExecuteScalarAsync());
            Assert.Equal("42501", exception.SqlState);
        }
    }

    [Fact]
    public async Task Database_listens_on_loopback_only()
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select current_setting('listen_addresses'), current_setting('port')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("127.0.0.1", reader.GetString(0));
        Assert.Equal(fixture.Port, int.Parse(reader.GetString(1)));
    }

    static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }
}
