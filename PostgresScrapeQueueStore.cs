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
}
