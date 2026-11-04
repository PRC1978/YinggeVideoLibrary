using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace YinggeVideoLibrary;

// PostgreSQL index storage with an optimistic revision token. This is kept
// separate from the UI until every index writer and backup path can use it.
internal static class PostgresLibraryIndexStore
{
    internal sealed record Snapshot(LibraryIndex Index, string Revision);

    internal static async Task<Snapshot> ReadAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        string revision;
        await using (var marker = new NpgsqlCommand(
            "SELECT index_sha256 FROM library_migration WHERE id=1", connection, transaction))
            revision = (string?)await marker.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidDataException("数据库尚未导入视频索引。 ");

        var folders = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT path FROM library_folder ORDER BY path", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) folders.Add(reader.GetString(0));

        var entries = new List<IndexEntry>();
        await using (var command = new NpgsqlCommand(
            "SELECT path,size_bytes,modified_ticks,missing FROM library_video ORDER BY path", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new IndexEntry
                {
                    Path = reader.GetString(0), Size = reader.GetInt64(1),
                    ModifiedTicks = reader.GetInt64(2), Missing = reader.GetBoolean(3)
                });

        await transaction.CommitAsync(cancellationToken);
        return new(new LibraryIndex { Folders = folders.ToArray(), Entries = entries }, revision);
    }

    internal static async Task<string> SaveAsync(string connectionString, LibraryIndex incoming,
        string expectedRevision, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision) || incoming.Folders is null || incoming.Entries is null ||
            incoming.Entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Path) ||
                !Path.IsPathFullyQualified(entry.Path) || entry.Size < 0 ||
                entry.ModifiedTicks < DateTime.MinValue.Ticks || entry.ModifiedTicks > DateTime.MaxValue.Ticks) ||
            incoming.Entries.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != incoming.Entries.Count ||
            incoming.Folders.Any(string.IsNullOrWhiteSpace) ||
            incoming.Folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != incoming.Folders.Length)
            throw new InvalidDataException("视频索引含无效或重复记录，数据库未修改。 ");

        var revision = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(incoming)));
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        await using (var marker = new NpgsqlCommand(
            "UPDATE library_migration SET index_sha256=@next WHERE id=1 AND index_sha256=@expected", connection, transaction))
        {
            marker.Parameters.AddWithValue("next", revision);
            marker.Parameters.AddWithValue("expected", expectedRevision);
            if (await marker.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("视频索引已由另一操作修改，请重新载入后再保存。 ");
        }

        await using (var temp = new NpgsqlCommand("""
            CREATE TEMP TABLE incoming_library_video (
              video_id text PRIMARY KEY, path text NOT NULL UNIQUE,
              size_bytes bigint NOT NULL, modified_ticks bigint NOT NULL, missing boolean NOT NULL
            ) ON COMMIT DROP
            """, connection, transaction))
            await temp.ExecuteNonQueryAsync(cancellationToken);
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY incoming_library_video (video_id,path,size_bytes,modified_ticks,missing) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var entry in incoming.Entries)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(VideoId(entry.Path), NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(entry.Path, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(entry.Size, NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(entry.ModifiedTicks, NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(entry.Missing, NpgsqlDbType.Boolean, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }
        await using (var merge = new NpgsqlCommand("""
            DELETE FROM library_video WHERE NOT EXISTS
              (SELECT 1 FROM incoming_library_video incoming WHERE incoming.video_id=library_video.video_id);
            INSERT INTO library_video(video_id,path,size_bytes,modified_ticks,missing)
              SELECT video_id,path,size_bytes,modified_ticks,missing FROM incoming_library_video
            ON CONFLICT(video_id) DO UPDATE SET path=excluded.path,size_bytes=excluded.size_bytes,
              modified_ticks=excluded.modified_ticks,missing=excluded.missing;
            """, connection, transaction))
            await merge.ExecuteNonQueryAsync(cancellationToken);
        await using (var folders = new NpgsqlCommand("DELETE FROM library_folder", connection, transaction))
            await folders.ExecuteNonQueryAsync(cancellationToken);
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY library_folder (path) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var folder in incoming.Folders)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(folder, NpgsqlDbType.Text, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return revision;
    }

    static string VideoId(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..24].ToLowerInvariant();
}
