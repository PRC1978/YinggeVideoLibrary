using System.IO;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace YinggeVideoLibrary;

// Durable queue operations for the database-mode cutover. Every change uses
// the same advisory transaction gate as backup export and review writes.
internal static class PostgresScrapeQueueStore
{
    internal static async Task<List<ScrapeQueueItem>> LoadAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var items = new List<ScrapeQueueItem>();
        await using var command = new NpgsqlCommand(
            "SELECT video_id,item::text FROM library_scrape_queue ORDER BY ordinal", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = JsonSerializer.Deserialize<ScrapeQueueItem>(reader.GetString(1))
                ?? throw new InvalidDataException("数据库队列含无效任务。 ");
            if (item.VideoId != reader.GetString(0))
                throw new InvalidDataException("数据库队列身份不一致。 ");
            items.Add(item);
        }
        return items;
    }

    internal static async Task<bool> RemoveAsync(string connectionString, string videoId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId)) throw new ArgumentException("任务身份不能为空。", nameof(videoId));
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM library_scrape_queue WHERE lower(video_id)=lower(@id)", connection, transaction);
        command.Parameters.AddWithValue("id", videoId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    internal static async Task<int> AddAsync(string connectionString,
        IEnumerable<ScrapeQueueItem> candidates, CancellationToken cancellationToken = default)
    {
        var incoming = candidates.Where(item => item is not null &&
            !string.IsNullOrWhiteSpace(item.VideoId) &&
            (item.Kind == "title" ? !string.IsNullOrWhiteSpace(item.SearchTitle)
                : !string.IsNullOrWhiteSpace(item.Code)))
            .DistinctBy(item => item.VideoId, StringComparer.OrdinalIgnoreCase).ToArray();
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        long ordinal;
        await using (var next = new NpgsqlCommand(
            "SELECT coalesce(max(ordinal),-1)+1 FROM library_scrape_queue", connection, transaction))
            ordinal = (long)(await next.ExecuteScalarAsync(cancellationToken) ?? 0L);
        var added = 0;
        foreach (var item in incoming)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO library_scrape_queue
                    (video_id,ordinal,kind,failed,next_attempt_at,item)
                VALUES (@id,@ordinal,@kind,@failed,@at,@item)
                ON CONFLICT DO NOTHING
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", item.VideoId);
            insert.Parameters.AddWithValue("ordinal", ordinal);
            insert.Parameters.AddWithValue("kind", item.Kind);
            insert.Parameters.AddWithValue("failed", item.Failed);
            insert.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz,
                item.NextAttemptAt.ToUniversalTime());
            insert.Parameters.AddWithValue("item", NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(item));
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                added++;
                ordinal++;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    internal static async Task<bool> HasPendingAsync(string connectionString, string videoId,
        CancellationToken cancellationToken = default)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM library_scrape_queue WHERE lower(video_id)=lower(@id) AND NOT failed)",
            connection);
        command.Parameters.AddWithValue("id", videoId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    internal static async Task<int> ClearPendingAsync(string connectionString,
        CancellationToken cancellationToken = default) => await DeleteMatchingAsync(
            connectionString, "NOT failed", cancellationToken);

    private static async Task<int> DeleteMatchingAsync(string connectionString, string condition,
        CancellationToken cancellationToken)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM library_scrape_queue WHERE " + condition, connection, transaction);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    internal static Task<bool> RescheduleAsync(string connectionString, ScrapeQueueItem retry,
        CancellationToken cancellationToken = default) => ChangeItemAsync(
            connectionString, retry.VideoId, item =>
            {
                item.Attempts = retry.Attempts;
                item.NextAttemptAt = retry.NextAttemptAt;
                item.LastError = retry.LastError;
            }, cancellationToken);

    internal static Task<bool> MarkFailedAsync(string connectionString, string videoId, string error,
        CancellationToken cancellationToken = default) => ChangeItemAsync(
            connectionString, videoId, item =>
            {
                item.Failed = true;
                item.LastError = error.Length > 240 ? error[..240] : error;
            }, cancellationToken);

    private static async Task<bool> ChangeItemAsync(string connectionString, string videoId,
        Action<ScrapeQueueItem> change, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoId)) throw new ArgumentException("任务身份不能为空。", nameof(videoId));
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        string? storedId = null;
        ScrapeQueueItem? item = null;
        await using (var read = new NpgsqlCommand(
            "SELECT video_id,item::text FROM library_scrape_queue WHERE lower(video_id)=lower(@id)",
            connection, transaction))
        {
            read.Parameters.AddWithValue("id", videoId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                storedId = reader.GetString(0);
                item = JsonSerializer.Deserialize<ScrapeQueueItem>(reader.GetString(1))
                    ?? throw new InvalidDataException("数据库队列含无效任务。 ");
            }
        }
        if (item is null) { await transaction.CommitAsync(cancellationToken); return false; }
        if (item.VideoId != storedId) throw new InvalidDataException("数据库队列身份不一致。 ");
        change(item);
        await using var update = new NpgsqlCommand("""
            UPDATE library_scrape_queue SET failed=@failed,next_attempt_at=@at,item=@item
            WHERE video_id=@id
            """, connection, transaction);
        update.Parameters.AddWithValue("id", storedId!);
        update.Parameters.AddWithValue("failed", item.Failed);
        update.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, item.NextAttemptAt.ToUniversalTime());
        update.Parameters.AddWithValue("item", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(item));
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("数据库队列任务在写入期间消失。 ");
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal static async Task<int> RetryFailuresAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using var command = new NpgsqlCommand("""
            UPDATE library_scrape_queue SET failed=false,next_attempt_at=@at,
              item=jsonb_set(jsonb_set(jsonb_set(item,'{Failed}','false'::jsonb),
                '{Attempts}','0'::jsonb),'{NextAttemptAt}',to_jsonb(CAST(@at AS timestamptz)))
            WHERE failed
            """, connection, transaction);
        command.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, now);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    internal static async Task<int> DelayAllUntilAsync(string connectionString,
        DateTimeOffset nextRequestAt, CancellationToken cancellationToken = default)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE library_scrape_queue SET next_attempt_at=@at,
              item=jsonb_set(item,'{NextAttemptAt}',to_jsonb(CAST(@at AS timestamptz)))
            WHERE NOT failed AND next_attempt_at < @at
            """, connection, transaction);
        command.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz,
            nextRequestAt.ToUniversalTime());
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction);
        await gate.ExecuteNonQueryAsync(cancellationToken);
    }
}
