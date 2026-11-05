using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace YinggeVideoLibrary;

// One commit boundary for work metadata and its associated user-state files.
// A review decision must not make config visible without metadata or history.
internal static class PostgresLibraryAtomicStateStore
{
    internal sealed record DocumentChange(string Name, byte[] Content, string? ExpectedRevision);

    internal static async Task<string> CommitAsync(string connectionString,
        IReadOnlyDictionary<string, MovieMetadata> metadata, string expectedMetadataRevision,
        IReadOnlyList<DocumentChange> documents, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedMetadataRevision) || metadata.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                pair.Value.SchemaVersion is < 1 or > 2))
            throw new InvalidDataException("作品资料含无效记录，数据库未修改。 ");
        if (documents.Any(change => change is null ||
                !PostgresLibraryMigration.IsDocumentName(change.Name) || change.Content is null) ||
            documents.Select(change => change.Name).Distinct(StringComparer.Ordinal).Count() != documents.Count)
            throw new InvalidDataException("事务资料名称无效或重复，数据库未修改。 ");

        var metadataRevision = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(metadata)));
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);

        await using (var marker = new NpgsqlCommand(
            "UPDATE library_migration SET metadata_sha256=@next WHERE id=1 AND metadata_sha256=@expected",
            connection, transaction))
        {
            marker.Parameters.AddWithValue("next", metadataRevision);
            marker.Parameters.AddWithValue("expected", expectedMetadataRevision);
            if (await marker.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("作品资料已被修改，整次事务未提交。 ");
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
            foreach (var (id, item) in metadata)
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

        foreach (var change in documents)
        {
            var revision = Convert.ToHexString(SHA256.HashData(change.Content));
            var sql = change.ExpectedRevision is null
                ? "INSERT INTO library_document(name,content,sha256) VALUES (@name,@content,@next) ON CONFLICT DO NOTHING"
                : "UPDATE library_document SET content=@content,sha256=@next WHERE name=@name AND sha256=@expected";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("name", change.Name);
            command.Parameters.AddWithValue("content", NpgsqlDbType.Bytea, change.Content);
            command.Parameters.AddWithValue("next", revision);
            if (change.ExpectedRevision is not null)
                command.Parameters.AddWithValue("expected", change.ExpectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("事务资料已被修改，整次事务未提交：" + change.Name);
        }
        await transaction.CommitAsync(cancellationToken);
        return metadataRevision;
    }
}
