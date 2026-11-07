using Npgsql;
using NpgsqlTypes;

namespace Yingge.Cloud.Accounts.Data;

internal sealed class AdminRepository(NpgsqlDataSource db)
{
    public async Task<IReadOnlyList<object>> InvitesAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select id, note, created_at, expires_at, used_at, revoked_at
            from invites order by created_at desc limit 100
            """, connection);
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new
        {
            id = reader.GetGuid(0), note = reader.IsDBNull(1) ? null : reader.GetString(1),
            createdAt = Db.Utc(reader.GetDateTime(2)), expiresAt = Db.Utc(reader.GetDateTime(3)),
            usedAt = reader.IsDBNull(4) ? (DateTimeOffset?)null : Db.Utc(reader.GetDateTime(4)),
            revokedAt = reader.IsDBNull(5) ? (DateTimeOffset?)null : Db.Utc(reader.GetDateTime(5))
        });
        return rows;
    }

    public async Task<bool> RevokeInviteByIdAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update invites set revoked_at = now()
            where id = @id and used_at is null and revoked_at is null
            """, connection);
        command.Parameters.Add(Db.Parameter("id", NpgsqlDbType.Uuid, id));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<object>> ReportsAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select r.id, a.username, r.received_at, r.client_version, r.message_count,
                   r.byte_size, r.deleted_at
            from error_reports r join accounts a on a.id = r.account_id
            order by r.received_at desc limit 100
            """, connection);
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new
        {
            id = reader.GetGuid(0), username = reader.GetString(1),
            receivedAt = Db.Utc(reader.GetDateTime(2)), clientVersion = reader.GetString(3),
            messageCount = reader.GetInt32(4), byteSize = reader.GetInt32(5),
            deletedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : Db.Utc(reader.GetDateTime(6))
        });
        return rows;
    }

    public async Task<IReadOnlyList<object>> AuditAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select at, event, account_id from audit_events order by at desc limit 100
            """, connection);
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new
        {
            at = Db.Utc(reader.GetDateTime(0)), eventName = reader.GetString(1),
            accountId = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2)
        });
        return rows;
    }
}
