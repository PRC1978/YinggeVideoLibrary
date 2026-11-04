using Npgsql;

namespace Yingge.Cloud.Accounts;

internal static class Db
{
    // Ubuntu 22.04 ships PostgreSQL 14; older servers lack generated columns and
    // the jsonb/index features the schema relies on.
    public const int MinimumServerVersionNumber = 140000;

    public static NpgsqlDataSource Create(string connectionString, int maxPoolSize,
        string applicationName = "yingge-cloud-accounts")
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.ApplicationName = applicationName;
        builder.ConnectionStringBuilder.Timeout = 10;
        builder.ConnectionStringBuilder.CommandTimeout = 15;
        if (maxPoolSize > 0) builder.ConnectionStringBuilder.MaxPoolSize = maxPoolSize;
        return builder.Build();
    }

    public static NpgsqlParameter Parameter(string name, NpgsqlTypes.NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    public static async Task<int> ReadServerVersionNumberAsync(NpgsqlDataSource dataSource,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "select current_setting('server_version_num')::int", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public static string DescribeVersion(int versionNumber) =>
        $"{versionNumber / 10000}.{versionNumber % 10000 / 100}.{versionNumber % 100}";

    public static DateTimeOffset Utc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? new DateTimeOffset(value)
        : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
