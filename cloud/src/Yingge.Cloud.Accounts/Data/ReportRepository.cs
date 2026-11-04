using Npgsql;
using NpgsqlTypes;

namespace Yingge.Cloud.Accounts.Data;

internal sealed record ReportInsert(
    Guid Id,
    Guid AccountId,
    string ClientVersion,
    string Os,
    string Arch,
    DateTimeOffset? OccurredAt,
    int MessageCount,
    int ByteSize,
    byte[] ContentHash,
    string StorageKey,
    DateTimeOffset RetentionExpiresAt);

internal sealed record ReportRow(
    Guid Id,
    DateTimeOffset ReceivedAt,
    DateTimeOffset ExpiresAt,
    int MessageCount,
    int ByteSize);

internal sealed class DuplicateReportException : Exception { }

internal sealed class ReportRepository(NpgsqlDataSource db)
{
    public async Task<DateTimeOffset> InsertAsync(ReportInsert report, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            insert into error_reports (id, account_id, client_version, os, arch, occurred_at,
                message_count, byte_size, content_hash, storage_key, retention_expires_at)
            values (@id, @account_id, @client_version, @os, @arch, @occurred_at, @message_count, @byte_size, @content_hash, @storage_key, @retention_expires_at)
            returning received_at
            """, connection);
        command.Parameters.Add(Db.Parameter("id", NpgsqlDbType.Uuid, report.Id));
        command.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, report.AccountId));
        command.Parameters.Add(Db.Parameter("client_version", NpgsqlDbType.Text, report.ClientVersion));
        command.Parameters.Add(Db.Parameter("os", NpgsqlDbType.Text, report.Os));
        command.Parameters.Add(Db.Parameter("arch", NpgsqlDbType.Text, report.Arch));
        command.Parameters.Add(Db.Parameter("occurred_at", NpgsqlDbType.TimestampTz, report.OccurredAt));
        command.Parameters.Add(Db.Parameter("message_count", NpgsqlDbType.Integer, report.MessageCount));
        command.Parameters.Add(Db.Parameter("byte_size", NpgsqlDbType.Integer, report.ByteSize));
        command.Parameters.Add(Db.Parameter("content_hash", NpgsqlDbType.Bytea, report.ContentHash));
        command.Parameters.Add(Db.Parameter("storage_key", NpgsqlDbType.Text, report.StorageKey));
        command.Parameters.Add(Db.Parameter("retention_expires_at", NpgsqlDbType.TimestampTz,
            report.RetentionExpiresAt));

        try
        {
            return Db.Utc((DateTime)(await command.ExecuteScalarAsync(ct))!);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DuplicateReportException();
        }
    }

    public async Task<int> CountSinceAsync(Guid accountId, DateTimeOffset since, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select count(*) from error_reports
            where account_id = @account_id and received_at >= @since and deleted_at is null
            """, connection);
        command.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, accountId));
        command.Parameters.Add(Db.Parameter("since", NpgsqlDbType.TimestampTz, since));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<string?> MarkDeletedAsync(Guid accountId, Guid reportId, string reason,
        CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update error_reports
            set deleted_at = now(), deleted_reason = @reason
            where id = @id and account_id = @account_id and deleted_at is null
            returning storage_key
            """, connection);
        command.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, accountId));
        command.Parameters.Add(Db.Parameter("id", NpgsqlDbType.Uuid, reportId));
        command.Parameters.Add(Db.Parameter("reason", NpgsqlDbType.Text, reason));
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<ReportRow>> ListAsync(Guid accountId, int limit, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select id, received_at, retention_expires_at, message_count, byte_size
            from error_reports
            where account_id = @account_id and deleted_at is null
            order by received_at desc
            limit @limit
            """, connection);
        command.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, accountId));
        command.Parameters.Add(Db.Parameter("limit", NpgsqlDbType.Integer, limit));
        var rows = new List<ReportRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new ReportRow(reader.GetGuid(0), Db.Utc(reader.GetDateTime(1)),
                Db.Utc(reader.GetDateTime(2)), reader.GetInt32(3), reader.GetInt32(4)));
        return rows;
    }

    // Claims expired rows for deletion. The row is marked first and the file is
    // removed afterwards; a leftover file is never readable by clients and the
    // next sweep logs it again if the delete failed.
    public async Task<IReadOnlyList<(Guid Id, string StorageKey)>> ClaimExpiredAsync(int limit,
        CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var claimed = new List<(Guid, string)>();
        await using (var select = new NpgsqlCommand("""
            select id, storage_key from error_reports
            where deleted_at is null and retention_expires_at <= now()
            order by retention_expires_at
            limit @limit
            for update skip locked
            """, connection, transaction))
        {
            select.Parameters.Add(Db.Parameter("limit", NpgsqlDbType.Integer, limit));
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) claimed.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        if (claimed.Count > 0)
        {
            var ids = claimed.Select(item => item.Item1).ToArray();
            await using var update = new NpgsqlCommand("""
                update error_reports set deleted_at = now(), deleted_reason = 'retention'
                where id = any(@ids)
                """, connection, transaction);
            update.Parameters.Add(Db.Parameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids));
            await update.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return claimed;
    }

    public async Task<(int Live, int Deleted)> CountAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select count(*) filter (where deleted_at is null),
                   count(*) filter (where deleted_at is not null)
            from error_reports
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (0, 0);
        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }
}
