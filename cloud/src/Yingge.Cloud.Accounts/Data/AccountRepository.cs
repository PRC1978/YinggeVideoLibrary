using Npgsql;
using NpgsqlTypes;

namespace Yingge.Cloud.Accounts.Data;

internal sealed record AccountRow(
    Guid Id,
    string Username,
    string PasswordHash,
    string Status,
    int FailedLoginCount,
    DateTimeOffset? LockedUntil,
    bool IsAdmin,
    bool MustChangePassword);

internal sealed record AdminAccountRow(Guid Id, string Username, string Status,
    bool IsAdmin, DateTimeOffset CreatedAt);

internal sealed class InviteNotUsableException : Exception { }

internal sealed class UsernameTakenException : Exception { }

internal sealed class AccountRepository(NpgsqlDataSource db)
{
    const string Columns = "id, username, password_hash, status, failed_login_count, locked_until, is_admin, must_change_password";

    public async Task<AccountRow?> FindByUsernameAsync(string username, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            $"select {Columns} from accounts where username = @username", connection);
        command.Parameters.Add(Db.Parameter("username", NpgsqlDbType.Text, username));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<AccountRow?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            $"select {Columns} from accounts where id = @id", connection);
        command.Parameters.Add(Db.Parameter("id", NpgsqlDbType.Uuid, id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    // The invite is consumed and the account created in one transaction, so a
    // unique-username clash can never burn an invitation.
    public async Task<Guid> RegisterAsync(byte[] inviteHash, string username, string passwordHash,
        string? ip, AuditRepository audit, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var inviteId = await LockInviteAsync(connection, transaction, inviteHash, ct)
            ?? throw new InviteNotUsableException();

        Guid accountId;
        try
        {
            await using var insert = new NpgsqlCommand("""
                insert into accounts (username, password_hash)
                values (@username, @password_hash)
                returning id
                """, connection, transaction);
            insert.Parameters.Add(Db.Parameter("username", NpgsqlDbType.Text, username));
            insert.Parameters.Add(Db.Parameter("password_hash", NpgsqlDbType.Text, passwordHash));
            accountId = (Guid)(await insert.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new UsernameTakenException();
        }

        await using (var consume = new NpgsqlCommand("""
            update invites set used_at = now(), used_by_account = @account_id where id = @id
            """, connection, transaction))
        {
            consume.Parameters.Add(Db.Parameter("id", NpgsqlDbType.Uuid, inviteId));
            consume.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, accountId));
            await consume.ExecuteNonQueryAsync(ct);
        }

        await audit.LogAsync(connection, transaction, "account.registered", accountId, ip,
            new { invite_id = inviteId }, ct);
        await transaction.CommitAsync(ct);
        return accountId;
    }

    public async Task<(int Failures, bool Locked)> RecordFailedLoginAsync(Guid accountId,
        int maxFailures, TimeSpan lockout, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update accounts
            set failed_login_count = failed_login_count + 1,
                locked_until = case
                    when failed_login_count + 1 >= @max_failures then now() + @lockout
                    else locked_until
                end
            where id = @id
            returning failed_login_count, (locked_until is not null and locked_until > now())
            """, connection);
        command.Parameters.Add(Db.Parameter("id", NpgsqlDbType.Uuid, accountId));
        command.Parameters.Add(Db.Parameter("max_failures", NpgsqlDbType.Integer, maxFailures));
        command.Parameters.Add(Db.Parameter("lockout", NpgsqlDbType.Interval, lockout));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (0, false);
        return (reader.GetInt32(0), reader.GetBoolean(1));
    }

    public async Task ResetFailedLoginAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update accounts set failed_login_count = 0, locked_until = null where id = @id
            """, connection);
        command.Parameters.Add(Db.Parameter("id", NpgsqlDbType.Uuid, accountId));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdatePasswordAsync(Guid accountId, string passwordHash, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update accounts set password_hash = @password_hash, password_updated_at = now() where id = @id
            """, connection);
        command.Parameters.Add(Db.Parameter("id", NpgsqlDbType.Uuid, accountId));
        command.Parameters.Add(Db.Parameter("password_hash", NpgsqlDbType.Text, passwordHash));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ChangeAdminPasswordAsync(Guid accountId, string passwordHash, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update accounts set password_hash = @password_hash,
                password_updated_at = now(), must_change_password = false
            where id = @id and is_admin and status = 'active'
            """, connection);
        command.Parameters.Add(Db.Parameter("id", NpgsqlDbType.Uuid, accountId));
        command.Parameters.Add(Db.Parameter("password_hash", NpgsqlDbType.Text, passwordHash));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> CreateAdminAsync(string username, string passwordHash, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            insert into accounts (username, password_hash, is_admin, must_change_password)
            values (@username, @password_hash, true, true)
            on conflict (username) do nothing
            """, connection);
        command.Parameters.Add(Db.Parameter("username", NpgsqlDbType.Text, username));
        command.Parameters.Add(Db.Parameter("password_hash", NpgsqlDbType.Text, passwordHash));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<AdminAccountRow>>
        ListForAdminAsync(int limit, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select id, username, status, is_admin, created_at from accounts
            order by created_at desc limit @limit
            """, connection);
        command.Parameters.Add(Db.Parameter("limit", NpgsqlDbType.Integer, limit));
        var rows = new List<AdminAccountRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new AdminAccountRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), Db.Utc(reader.GetDateTime(4))));
        return rows;
    }

    public async Task<bool> SetStatusAsync(string username, bool active, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update accounts
            set status = @status, disabled_at = case when @status = 'disabled' then now() else null end
            where username = @username
            """, connection);
        command.Parameters.Add(Db.Parameter("username", NpgsqlDbType.Text, username));
        command.Parameters.Add(Db.Parameter("status", NpgsqlDbType.Text, active ? "active" : "disabled"));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("select count(*) from accounts", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    static async Task<Guid?> LockInviteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        byte[] inviteHash, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            select id from invites
            where code_hash = @code_hash and used_at is null and revoked_at is null and expires_at > now()
            for update
            """, connection, transaction);
        command.Parameters.Add(Db.Parameter("code_hash", NpgsqlDbType.Bytea, inviteHash));
        var scalar = await command.ExecuteScalarAsync(ct);
        return scalar as Guid?;
    }

    static AccountRow Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : Db.Utc(reader.GetDateTime(5)),
        reader.GetBoolean(6), reader.GetBoolean(7));
}
