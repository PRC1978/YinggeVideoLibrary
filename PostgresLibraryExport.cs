using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace YinggeVideoLibrary;

// Export an internally consistent database revision to a new, empty directory.
// The old file-based app can read these records during a controlled rollback.
// Posters and thumbnails are deliberately outside this record export.
internal static class PostgresLibraryExport
{
    internal sealed record Result(int VideoCount, int MetadataCount, int DocumentCount);

    internal static async Task<Result> ExportAsync(string connectionString, string destination,
        CancellationToken cancellationToken = default)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("导出目录已存在；拒绝覆盖已有数据。 ");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        await using (var marker = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM library_migration WHERE id=1)", connection, transaction))
            if ((bool)(await marker.ExecuteScalarAsync(cancellationToken) ?? false) is false)
                throw new InvalidDataException("数据库没有完整导入标记，拒绝导出。 ");

        // A failed export must never look like a complete rollback directory.
        var staging = destination + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);

        var folders = new List<string>();
        await using (var command = new NpgsqlCommand(
            "SELECT path FROM library_folder ORDER BY path", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) folders.Add(reader.GetString(0));

        var entries = new List<IndexEntry>();
        await using (var command = new NpgsqlCommand(
            "SELECT path,size_bytes,modified_ticks,missing FROM library_video ORDER BY path",
            connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new IndexEntry
                {
                    Path = reader.GetString(0), Size = reader.GetInt64(1),
                    ModifiedTicks = reader.GetInt64(2), Missing = reader.GetBoolean(3)
                });
        await File.WriteAllBytesAsync(Path.Combine(staging, "library-index.json"),
            JsonSerializer.SerializeToUtf8Bytes(new LibraryIndex
                { Folders = folders.ToArray(), Entries = entries }), cancellationToken);

        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            "SELECT video_id,document::text FROM library_metadata ORDER BY video_id",
            connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                using var document = JsonDocument.Parse(reader.GetString(1));
                metadata.Add(reader.GetString(0), document.RootElement.Clone());
            }
        await File.WriteAllBytesAsync(Path.Combine(staging, "metadata.json"),
            JsonSerializer.SerializeToUtf8Bytes(metadata), cancellationToken);

        var documentCount = 0;
        await using (var command = new NpgsqlCommand(
            "SELECT name,content,sha256 FROM library_document ORDER BY name",
            connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                if (!PostgresLibraryMigration.IsDocumentName(name))
                    throw new InvalidDataException("数据库含不允许导出的文件名。 ");
                var bytes = (byte[])reader[1];
                if (reader.GetString(2) != Convert.ToHexString(SHA256.HashData(bytes)))
                    throw new InvalidDataException("数据库资料校验失败：" + name);
                await File.WriteAllBytesAsync(Path.Combine(staging, name), bytes, cancellationToken);
                documentCount++;
            }
        await transaction.CommitAsync(cancellationToken);
        Directory.Move(staging, destination);
        return new(entries.Count, metadata.Count, documentCount);
    }
}
