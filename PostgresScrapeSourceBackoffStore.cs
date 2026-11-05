using System.IO;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace YinggeVideoLibrary;

// Database-mode source cooldown and rate-limit state. The legacy sidecar is
// imported as bytes, then all reads and writes use the same advisory gate as
// queue mutations and consistent rollback exports.
internal static class PostgresScrapeSourceBackoffStore
{
    private const string Name = "scrape-source-backoff.json";
    internal sealed class SourceState
    {
        public DateTimeOffset Until { get; set; }
        public DateTimeOffset NextRequestAt { get; set; }
        public int Failures { get; set; }
        public string LastError { get; set; } = "";
    }

    private sealed class Document
    {
        public int Version { get; set; }
        public Dictionary<string, SourceState>? Sources { get; set; }
    }

    internal static async Task<IReadOnlyDictionary<string, SourceState>> LoadAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, cancellationToken);
    }

    private static async Task<Dictionary<string, SourceState>> ReadAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT content FROM library_document WHERE name=@name", connection, transaction);
        command.Parameters.AddWithValue("name", Name);
        var bytes = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        if (bytes is null) return new(StringComparer.OrdinalIgnoreCase);
        var data = JsonSerializer.Deserialize<Document>(bytes)
            ?? throw new InvalidDataException("数据库来源冷却记录无效。 ");
        if (data.Version != 1 || data.Sources is null || data.Sources.Count > 64 ||
            data.Sources.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 80 ||
                pair.Value is null || pair.Value.Failures is < 0 or > 16 ||
                pair.Value.LastError is null || pair.Value.LastError.Length > 240 ||
                pair.Value.Until > DateTimeOffset.UtcNow.AddDays(2) ||
                pair.Value.NextRequestAt > DateTimeOffset.UtcNow.AddDays(2)))
            throw new InvalidDataException("数据库来源冷却记录损坏；已停止自动请求。 ");
        return new(data.Sources, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task SaveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Dictionary<string, SourceState> states, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document { Version = 1, Sources = states });
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        await using var command = new NpgsqlCommand("""
            INSERT INTO library_document(name,content,sha256) VALUES (@name,@content,@sha)
            ON CONFLICT(name) DO UPDATE SET content=excluded.content,sha256=excluded.sha256
            """, connection, transaction);
        command.Parameters.AddWithValue("name", Name);
        command.Parameters.AddWithValue("content", NpgsqlDbType.Bytea, bytes);
        command.Parameters.AddWithValue("sha", hash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ChangeAsync<T>(string connectionString,
        Func<Dictionary<string, SourceState>, T> mutate, CancellationToken cancellationToken)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        var states = await ReadAsync(connection, transaction, cancellationToken);
        var result = mutate(states);
        await SaveAsync(connection, transaction, states, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static async Task<DateTimeOffset> GetReadyAtAsync(string connectionString, string source,
        CancellationToken cancellationToken = default)
    {
        var states = await LoadAsync(connectionString, cancellationToken);
        return states.TryGetValue(source, out var current)
            ? Max(current.Until, current.NextRequestAt) : DateTimeOffset.MinValue;
    }

    internal static async Task<DateTimeOffset> GetCooldownAtAsync(string connectionString, string source,
        CancellationToken cancellationToken = default)
    {
        var states = await LoadAsync(connectionString, cancellationToken);
        return states.TryGetValue(source, out var current) ? current.Until : DateTimeOffset.MinValue;
    }

    internal static Task<DateTimeOffset> ReserveAsync(string connectionString, string source,
        TimeSpan minimumSpacing, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("搜刮来源不能为空。", nameof(source));
        if (minimumSpacing < TimeSpan.FromSeconds(1) || minimumSpacing > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(minimumSpacing));
        return ChangeAsync(connectionString, states =>
        {
            var now = DateTimeOffset.UtcNow;
            states.TryGetValue(source, out var previous);
            var ready = previous is null ? now : Max(now, previous.Until, previous.NextRequestAt);
            if (ready > now) return ready;
            states[source] = new SourceState
            {
                Until = previous?.Until ?? DateTimeOffset.MinValue,
                NextRequestAt = now.Add(minimumSpacing),
                Failures = previous?.Failures ?? 0,
                LastError = previous?.LastError ?? ""
            };
            return DateTimeOffset.MinValue;
        }, cancellationToken);
    }

    internal static Task<DateTimeOffset> RecordFailureAsync(string connectionString, string source,
        string error, bool rateLimited, TimeSpan? retryAfter = null, int consecutiveFailureLimit = 4,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("搜刮来源不能为空。", nameof(source));
        return ChangeAsync(connectionString, states =>
        {
            states.TryGetValue(source, out var previous);
            var failures = Math.Min((previous?.Failures ?? 0) + 1, 16);
            var seconds = Math.Min(1200, (rateLimited ? 30 : 10) * Math.Pow(2, Math.Min(failures - 1, 7)));
            var delay = TimeSpan.FromSeconds(seconds);
            if (failures >= Math.Clamp(consecutiveFailureLimit, 1, 10) && delay < TimeSpan.FromHours(1))
                delay = TimeSpan.FromHours(1);
            if (retryAfter is { } requested && requested > delay)
                delay = requested > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : requested;
            var until = DateTimeOffset.UtcNow.Add(delay);
            if (previous is not null && previous.Until > until) until = previous.Until;
            states[source] = new SourceState
            {
                Until = until,
                NextRequestAt = previous?.NextRequestAt ?? DateTimeOffset.MinValue,
                Failures = failures,
                LastError = error.Length > 240 ? error[..240] : error
            };
            return until;
        }, cancellationToken);
    }

    internal static Task<bool> ReportSuccessAsync(string connectionString, string source,
        CancellationToken cancellationToken = default) => ChangeAsync(connectionString, states =>
    {
        if (!states.TryGetValue(source, out var previous)) return false;
        if (previous.NextRequestAt > DateTimeOffset.UtcNow)
            states[source] = new SourceState { NextRequestAt = previous.NextRequestAt };
        else states.Remove(source);
        return true;
    }, cancellationToken);

    private static DateTimeOffset Max(params DateTimeOffset[] values) => values.Max();
}
