using Npgsql;
using NpgsqlTypes;

namespace Yingge.Cloud.Accounts.Data;

internal sealed record SessionRow(
    Guid Id,
    Guid AccountId,
    string Username,
    DateTimeOffset ExpiresAt,
    string AccountStatus);

internal sealed class SessionRepository(NpgsqlDataSource db)
{
    public async Task<SessionRow?> FindByHashAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select s.id, s.account_id, a.username, s.expires_at, a.status
            from sessions s
            join accounts a on a.id = s.account_id
            where s.token_hash = @token_hash and s.revoked_at is null and s.expires_at > now()
            """, connection);
        command.Parameters.Add(Db.Parameter("token_hash", NpgsqlDbType.Bytea, tokenHash));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new SessionRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
            Db.Utc(reader.GetDateTime(3)), reader.GetString(4));
    }

    public async Task<DateTimeOffset> CreateAsync(Guid accountId, byte[] tokenHash, TimeSpan lifetime,
        string? clientVersion, string? ip, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            insert into sessions (account_id, token_hash, expires_at, client_version, created_ip)
            values (@account_id, @token_hash, now() + @lifetime, @client_version, @ip)
            returning expires_at
            """, connection);
        command.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, accountId));
        command.Parameters.Add(Db.Parameter("token_hash", NpgsqlDbType.Bytea, tokenHash));
        command.Parameters.Add(Db.Parameter("lifetime", NpgsqlDbType.Interval, lifetime));
        command.Parameters.Add(Db.Parameter("client_version", NpgsqlDbType.Text, clientVersion));
        command.Parameters.Add(Db.Parameter("ip", NpgsqlDbType.Text, ip));
        var result = await command.ExecuteScalarAsync(ct);
        return Db.Utc((DateTime)result!);
    }

    public async Task<Guid?> RevokeByHashAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update sessions set revoked_at = now()
            where token_hash = @token_hash and revoked_at is null
            returning id
            """, connection);
        command.Parameters.Add(Db.Parameter("token_hash", NpgsqlDbType.Bytea, tokenHash));
        return await command.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<int> RevokeAllForAccountAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update sessions set revoked_at = now()
            where account_id = @account_id and revoked_at is null
            """, connection);
        command.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, accountId));
        return await command.ExecuteNonQueryAsync(ct);
    }

    // last_seen_at is best-effort telemetry; writing it on every request would
    // turn a read endpoint into a write endpoint.
    public async Task TouchAsync(Guid sessionId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update sessions set last_seen_at = now()
            where id = @id and (last_seen_at is null or last_seen_at < now() - interval '1 minute')
            """, connection);
        command.Parameters.Add(Db.Parameter("id", NpgsqlDbType.Uuid, sessionId));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountActiveAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select count(*) from sessions where revoked_at is null and expires_at > now()
            """, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }
}
