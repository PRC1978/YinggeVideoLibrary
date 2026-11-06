using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace YinggeVideoLibrary;

// Transactional metadata storage. It is not connected to normal UI writes
// until the review journal and backup flow can move in the same cutover.
internal static class PostgresLibraryMetadataStore
{
    internal sealed record Snapshot(Dictionary<string, MovieMetadata> Records, string Revision);

    internal static async Task<string> ReadRevisionAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT metadata_sha256 FROM library_migration WHERE id=1", connection);
        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("数据库尚未导入作品资料。 ");
    }

    internal static async Task<Snapshot> ReadAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        string revision;
        await using (var marker = new NpgsqlCommand(
            "SELECT metadata_sha256 FROM library_migration WHERE id=1", connection, transaction))
            revision = (string?)await marker.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidDataException("数据库尚未导入作品资料。 ");
        var records = new Dictionary<string, MovieMetadata>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            "SELECT video_id,document::text FROM library_metadata ORDER BY video_id", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var item = JsonSerializer.Deserialize<MovieMetadata>(reader.GetString(1))
                    ?? throw new InvalidDataException("数据库作品资料为空。 ");
                if (item.SchemaVersion is < 1 or > 2)
                    throw new InvalidDataException("数据库作品资料版本不受支持。 ");
                records.Add(reader.GetString(0), item);
            }
        await transaction.CommitAsync(cancellationToken);
        return new(records, revision);
    }

    internal static async Task<string> SaveAsync(string connectionString,
        IReadOnlyDictionary<string, MovieMetadata> incoming, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision) || incoming.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                pair.Value.SchemaVersion is < 1 or > 2))
            throw new InvalidDataException("作品资料含无效记录，数据库未修改。 ");
        var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(incoming)));
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        await using (var marker = new NpgsqlCommand(
            "UPDATE library_migration SET metadata_sha256=@next WHERE id=1 AND metadata_sha256=@expected", connection, transaction))
        {
            marker.Parameters.AddWithValue("next", revision);
            marker.Parameters.AddWithValue("expected", expectedRevision);
            if (await marker.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("作品资料已由另一操作修改，请重新载入后再保存。 ");
        }
        await using (var temp = new NpgsqlCommand("""
            CREATE TEMP TABLE incoming_library_metadata (
              video_id text PRIMARY KEY, document jsonb NOT NULL
            ) ON COMMIT DROP
            """, connection, transaction))
            await temp.ExecuteNonQueryAsync(cancellationToken);
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY incoming_library_metadata (video_id,document) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var (id, item) in incoming)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(id, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(JsonSerializer.Serialize(item), NpgsqlDbType.Jsonb, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }
        await using (var merge = new NpgsqlCommand("""
            INSERT INTO library_metadata(video_id,document)
              SELECT video_id,document FROM incoming_library_metadata
            ON CONFLICT(video_id) DO UPDATE SET document=excluded.document;
            DELETE FROM library_metadata WHERE NOT EXISTS
              (SELECT 1 FROM incoming_library_metadata incoming WHERE incoming.video_id=library_metadata.video_id);
            """, connection, transaction))
            await merge.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return revision;
    }
}
