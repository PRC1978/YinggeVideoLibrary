using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace YinggeVideoLibrary;

// An explicit, non-destructive first migration step. The application continues
// to use its existing files until every writer has been moved to the database.
internal static class PostgresLibraryMigration
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS library_migration (
            id integer PRIMARY KEY CHECK (id = 1),
            index_sha256 text NOT NULL,
            metadata_sha256 text NOT NULL,
            imported_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS library_video (
            video_id text PRIMARY KEY,
            path text NOT NULL UNIQUE,
            size_bytes bigint NOT NULL CHECK (size_bytes >= 0),
            modified_ticks bigint NOT NULL,
            missing boolean NOT NULL
        );
        CREATE TABLE IF NOT EXISTS library_metadata (
            video_id text PRIMARY KEY,
            document jsonb NOT NULL
        );
        CREATE TABLE IF NOT EXISTS library_folder (
            path text PRIMARY KEY
        );
        """;

    internal sealed record Result(int VideoCount, int MetadataCount, int FolderCount,
        string IndexSha256, string MetadataSha256);

    internal static async Task<Result> ImportAsync(string directory, string connectionString,
        CancellationToken cancellationToken = default)
    {
        var indexPath = Path.Combine(directory, "library-index.json");
        var metadataPath = Path.Combine(directory, "metadata.json");
        var indexBytes = await File.ReadAllBytesAsync(indexPath, cancellationToken);
        var metadataBytes = await File.ReadAllBytesAsync(metadataPath, cancellationToken);
        var index = JsonSerializer.Deserialize<LibraryIndex>(indexBytes)
            ?? throw new InvalidDataException("视频索引为空，未导入。 ");
        var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataBytes)
            ?? throw new InvalidDataException("资料文件为空，未导入。 ");
        var checkedMetadata = JsonSerializer.Deserialize<Dictionary<string, MovieMetadata>>(metadataBytes)
            ?? throw new InvalidDataException("资料文件格式无效，未导入。 ");
        if (checkedMetadata.Values.Any(value => value is null || value.SchemaVersion is < 1 or > 2))
            throw new InvalidDataException("资料文件含无效版本，未导入。 ");
        if (index.Folders is null || index.Entries is null || index.Entries.Any(e =>
                string.IsNullOrWhiteSpace(e.Path) || e.Size < 0))
            throw new InvalidDataException("视频索引含无效记录，未导入。 ");
        if (index.Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != index.Entries.Count)
            throw new InvalidDataException("视频索引含重复路径，未导入。 ");

        var indexHash = Convert.ToHexString(SHA256.HashData(indexBytes));
        var metadataHash = Convert.ToHexString(SHA256.HashData(metadataBytes));
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // A single local library has one import owner. Hold the lock until commit.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        await using (var schemaCommand = new NpgsqlCommand(Schema, connection, transaction))
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        await using (var present = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM library_migration)", connection, transaction))
            if ((bool)(await present.ExecuteScalarAsync(cancellationToken) ?? false))
                throw new InvalidOperationException("此数据库已有导入记录；未覆盖任何数据。 ");
        await using (var nonempty = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM library_video) OR EXISTS(SELECT 1 FROM library_metadata)", connection, transaction))
            if ((bool)(await nonempty.ExecuteScalarAsync(cancellationToken) ?? false))
                throw new InvalidOperationException("目标数据库已有视频或资料；未覆盖任何数据。 ");

        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY library_video (video_id, path, size_bytes, modified_ticks, missing) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var entry in index.Entries)
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
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY library_folder (path) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var folder in index.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(folder, NpgsqlDbType.Text, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY library_metadata (video_id, document) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var (id, document) in metadata)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(id, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(document.GetRawText(), NpgsqlDbType.Jsonb, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }
        await using (var marker = new NpgsqlCommand(
            "INSERT INTO library_migration(id,index_sha256,metadata_sha256) VALUES (1,@i,@m)", connection, transaction))
        {
            marker.Parameters.AddWithValue("i", indexHash);
            marker.Parameters.AddWithValue("m", metadataHash);
            await marker.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(index.Entries.Count, metadata.Count, index.Folders.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            indexHash, metadataHash);
    }

    internal static async Task<Result> VerifyAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM library_video),
                   (SELECT count(*) FROM library_metadata),
                   (SELECT count(*) FROM library_folder),
                   index_sha256, metadata_sha256
            FROM library_migration WHERE id = 1
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("数据库中没有导入标记。 ");
        return new(checked((int)reader.GetInt64(0)), checked((int)reader.GetInt64(1)),
            checked((int)reader.GetInt64(2)),
            reader.GetString(3), reader.GetString(4));
    }

    internal static async Task<bool> VerifyContentAsync(string directory, string connectionString,
        CancellationToken cancellationToken = default)
    {
        var sourceIndex = JsonSerializer.Deserialize<LibraryIndex>(
            await File.ReadAllBytesAsync(Path.Combine(directory, "library-index.json"), cancellationToken));
        var sourceMetadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            await File.ReadAllBytesAsync(Path.Combine(directory, "metadata.json"), cancellationToken));
        if (sourceIndex is null || sourceMetadata is null) return false;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var videos = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            "SELECT video_id,path,size_bytes,modified_ticks,missing FROM library_video", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var path = reader.GetString(1);
                if (reader.GetString(0) != VideoId(path)) return false;
                videos[path] = new IndexEntry { Path = path, Size = reader.GetInt64(2),
                    ModifiedTicks = reader.GetInt64(3), Missing = reader.GetBoolean(4) };
            }
        if (videos.Count != sourceIndex.Entries.Count || sourceIndex.Entries.Any(e =>
                !videos.TryGetValue(e.Path, out var saved) || saved.Size != e.Size ||
                saved.ModifiedTicks != e.ModifiedTicks || saved.Missing != e.Missing)) return false;
        var folders = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT path FROM library_folder", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) folders.Add(reader.GetString(0));
        if (!folders.SetEquals(sourceIndex.Folders)) return false;
        var found = 0;
        await using (var command = new NpgsqlCommand("SELECT video_id, document::text FROM library_metadata", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!sourceMetadata.TryGetValue(reader.GetString(0), out var expected)) return false;
                using var actual = JsonDocument.Parse(reader.GetString(1));
                if (!JsonElement.DeepEquals(expected, actual.RootElement)) return false;
                found++;
            }
        return found == sourceMetadata.Count;
    }

    private static string VideoId(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..24].ToLowerInvariant();
}
