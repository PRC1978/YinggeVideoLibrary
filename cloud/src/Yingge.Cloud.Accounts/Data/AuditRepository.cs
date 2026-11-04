using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Yingge.Cloud.Accounts.Data;

// Audit rows describe what happened without copying report bodies, passwords
// or tokens. Detail payloads are short operator-facing metadata.
internal sealed class AuditRepository(NpgsqlDataSource db)
{
    const string InsertSql =
        "insert into audit_events (event, account_id, ip, detail) values (@event, @account_id, @ip, @detail)";

    public Task LogAsync(string eventName, Guid? accountId, string? ip, object? detail, CancellationToken ct) =>
        LogAsync(null, null, eventName, accountId, ip, detail, ct);

    public async Task LogAsync(NpgsqlConnection? connection, NpgsqlTransaction? transaction,
        string eventName, Guid? accountId, string? ip, object? detail, CancellationToken ct)
    {
        var ownsConnection = connection is null;
        connection ??= await db.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand(InsertSql, connection, transaction);
            command.Parameters.Add(Db.Parameter("event", NpgsqlDbType.Text, eventName));
            command.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, accountId));
            command.Parameters.Add(Db.Parameter("ip", NpgsqlDbType.Text, ip));
            command.Parameters.Add(Db.Parameter("detail", NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(detail ?? new { })));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (ownsConnection) await connection.DisposeAsync();
        }
    }
}
