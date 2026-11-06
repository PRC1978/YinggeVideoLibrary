using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace YinggeVideoLibrary;

// The index change and its undo record commit in one PostgreSQL transaction.
// No video file is opened, moved or deleted by this store.
internal static class PostgresMissingCleanupStore
{
    private const string DocumentName = MissingCleanupStore.FileName;
    private const long LibraryLock = 178942601;

    internal static async Task<bool> HasUndoableAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var state = await ReadStateAsync(connection, null, cancellationToken);
        return state.Value.History.Any(item => item.CompletedAt is not null &&
            item.UndoneAt is null && !item.RolledBack);
    }

    internal static async Task EnsureReadyForActivationAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var state = await ReadStateAsync(connection, null, cancellationToken);
        EnsureSettled(state.Value);
    }

    internal static async Task<MissingCleanupRecord> CleanupAsync(
        PostgresLibraryRuntimeSession session, IReadOnlyList<MissingCleanupEntry> entries,
        IReadOnlyCollection<string> configuredRoots, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0 || entries.Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidDataException("待清理记录为空或存在重复路径。 ");
        CheckRoots(session.Index.Index.Folders, configuredRoots);
        var known = session.Index.Index.Entries.ToDictionary(item => item.Path,
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            if (!WithinRoots(entry.Path, configuredRoots) ||
                !known.TryGetValue(entry.Path, out var indexed) || !indexed.Missing ||
                indexed.Size != entry.Size || indexed.ModifiedTicks != entry.ModifiedTicks)
                throw new InvalidOperationException("缺失状态或索引内容已变化，请重新扫描后预览。 ");

        var removed = entries.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = new LibraryIndex { Folders = session.Index.Index.Folders,
            Entries = session.Index.Index.Entries.Where(item => !removed.Contains(item.Path)).ToList() };
        var record = new MissingCleanupRecord { Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            Entries = entries.Select(item => new MissingCleanupEntry { Path = item.Path,
                Size = item.Size, ModifiedTicks = item.ModifiedTicks }).ToList() };

        await using var source = NpgsqlDataSource.Create(session.ConnectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        var state = await ReadStateAsync(connection, transaction, cancellationToken);
        EnsureSettled(state.Value);
        state.Value.History.Insert(0, record);
        Trim(state.Value);
        await AdvanceIndexAsync(connection, transaction, session.Index.Revision, next,
            cancellationToken);
        foreach (var entry in entries)
        {
            await using var command = new NpgsqlCommand("""
                DELETE FROM library_video WHERE path=@path AND size_bytes=@size
                  AND modified_ticks=@ticks AND missing=true
                """, connection, transaction);
            command.Parameters.AddWithValue("path", entry.Path);
            command.Parameters.AddWithValue("size", entry.Size);
            command.Parameters.AddWithValue("ticks", entry.ModifiedTicks);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("缺失记录已变化；清理事务已回滚。 ");
        }
        await WriteStateAsync(connection, transaction, state, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    internal static async Task<MissingCleanupRecord?> UndoAsync(
        PostgresLibraryRuntimeSession session, IReadOnlyCollection<string> configuredRoots,
        CancellationToken cancellationToken = default)
    {
        CheckRoots(session.Index.Index.Folders, configuredRoots);
        await using var source = NpgsqlDataSource.Create(session.ConnectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        var state = await ReadStateAsync(connection, transaction, cancellationToken);
        EnsureSettled(state.Value);
        var record = state.Value.History.FirstOrDefault(item => item.CompletedAt is not null &&
            item.UndoneAt is null && !item.RolledBack);
        if (record is null) return null;
        if (record.Entries.Any(item => !WithinRoots(item.Path, configuredRoots)))
            throw new InvalidOperationException("清理历史包含当前扫描根之外的路径，不能撤销。 ");
        var known = session.Index.Index.Entries.Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (record.Entries.Any(item => known.Contains(item.Path)))
            throw new InvalidOperationException("待恢复的路径已在索引中；请先重新扫描并检查。 ");
        var next = new LibraryIndex { Folders = session.Index.Index.Folders,
            Entries = session.Index.Index.Entries.Concat(record.Entries.Select(item => new IndexEntry
            { Path = item.Path, Size = item.Size, ModifiedTicks = item.ModifiedTicks, Missing = true })).ToList() };
        await AdvanceIndexAsync(connection, transaction, session.Index.Revision, next,
            cancellationToken);
        foreach (var entry in record.Entries)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO library_video(video_id,path,size_bytes,modified_ticks,missing)
                VALUES (@id,@path,@size,@ticks,true)
                """, connection, transaction);
            command.Parameters.AddWithValue("id", VideoId(entry.Path));
            command.Parameters.AddWithValue("path", entry.Path);
            command.Parameters.AddWithValue("size", entry.Size);
            command.Parameters.AddWithValue("ticks", entry.ModifiedTicks);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        record.UndoneAt = DateTimeOffset.UtcNow;
        await WriteStateAsync(connection, transaction, state, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    private static void CheckRoots(IEnumerable<string> indexed,
        IEnumerable<string> configured)
    {
        static string[] Normalize(IEnumerable<string> roots) => roots
            .Select(item => Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Trim())))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!Normalize(indexed).SequenceEqual(Normalize(configured),
                StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置文件夹与数据库索引不一致，已保护旧索引。 ");
    }

    private static void EnsureSettled(MissingCleanupState state)
    {
        if (state.PendingId is not null || state.UndoingId is not null)
            throw new InvalidOperationException("旧版清理仍有未完成操作；请先在文件模式恢复。 ");
    }

    private static bool WithinRoots(string path, IEnumerable<string> roots)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        foreach (var root in roots)
        {
            var relative = Path.GetRelativePath(root, path);
            if (!Path.IsPathFullyQualified(relative) && relative != "." &&
                relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar,
                    StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void Trim(MissingCleanupState state)
    {
        if (state.History.Count > 20)
            state.History = state.History.OrderByDescending(item => item.StartedAt).Take(20).ToList();
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT pg_advisory_xact_lock({LibraryLock})", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(MissingCleanupState Value, string? Revision)> ReadStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT content,sha256 FROM library_document WHERE name=@name", connection, transaction);
        command.Parameters.AddWithValue("name", DocumentName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (new MissingCleanupState(), null);
        var bytes = (byte[])reader[0];
        var revision = reader.GetString(1);
        if (Hash(bytes) != revision)
            throw new InvalidDataException("数据库清理历史校验失败。 ");
        var value = JsonSerializer.Deserialize<MissingCleanupState>(bytes)
            ?? throw new InvalidDataException("数据库清理历史无效。 ");
        if (value.SchemaVersion != 1 || value.History is null)
            throw new InvalidDataException("数据库清理历史版本不受支持。 ");
        return (value, revision);
    }

    private static async Task WriteStateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, (MissingCleanupState Value, string? Revision) state,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state.Value);
        var sql = state.Revision is null
            ? "INSERT INTO library_document(name,content,sha256) VALUES (@name,@content,@next) ON CONFLICT DO NOTHING"
            : "UPDATE library_document SET content=@content,sha256=@next WHERE name=@name AND sha256=@prior";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("name", DocumentName);
        command.Parameters.AddWithValue("content", NpgsqlDbType.Bytea, bytes);
        command.Parameters.AddWithValue("next", Hash(bytes));
        if (state.Revision is not null) command.Parameters.AddWithValue("prior", state.Revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("清理历史已改变；数据库事务已回滚。 ");
    }

    private static async Task AdvanceIndexAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string expectedRevision, LibraryIndex next,
        CancellationToken cancellationToken)
    {
        var revision = Hash(JsonSerializer.SerializeToUtf8Bytes(next));
        await using var command = new NpgsqlCommand("""
            UPDATE library_migration SET index_sha256=@next
            WHERE id=1 AND index_sha256=@prior
            """, connection, transaction);
        command.Parameters.AddWithValue("next", revision);
        command.Parameters.AddWithValue("prior", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("索引已由另一操作修改；数据库事务已回滚。 ");
    }

    private static string VideoId(string path) => Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..24].ToLowerInvariant();

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
}
