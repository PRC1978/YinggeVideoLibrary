using Npgsql;
using NpgsqlTypes;

namespace Yingge.Cloud.Accounts.Data;

internal sealed class InviteRepository(NpgsqlDataSource db)
{
    public async Task<Guid?> FindUsableAsync(byte[] codeHash, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select id from invites
            where code_hash = @code_hash and used_at is null and revoked_at is null and expires_at > now()
            """, connection);
        command.Parameters.Add(Db.Parameter("code_hash", NpgsqlDbType.Bytea, codeHash));
        return await command.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<Guid> CreateAsync(byte[] codeHash, string? note, DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            insert into invites (code_hash, note, expires_at) values (@code_hash, @note, @expires_at)
            returning id
            """, connection);
        command.Parameters.Add(Db.Parameter("code_hash", NpgsqlDbType.Bytea, codeHash));
        command.Parameters.Add(Db.Parameter("note", NpgsqlDbType.Text, note));
        command.Parameters.Add(Db.Parameter("expires_at", NpgsqlDbType.TimestampTz, expiresAt));
        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> RevokeAsync(byte[] codeHash, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update invites set revoked_at = now()
            where code_hash = @code_hash and revoked_at is null and used_at is null
            """, connection);
        command.Parameters.Add(Db.Parameter("code_hash", NpgsqlDbType.Bytea, codeHash));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<int> RevokeAllPendingAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update invites set revoked_at = now()
            where revoked_at is null and used_at is null
            """, connection);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountPendingAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select count(*) from invites
            where used_at is null and revoked_at is null and expires_at > now()
            """, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }
}
