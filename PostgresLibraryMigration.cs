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
        CREATE TABLE IF NOT EXISTS library_document (
            name text PRIMARY KEY,
            content bytea NOT NULL,
            sha256 text NOT NULL
        );
        CREATE TABLE IF NOT EXISTS library_scrape_queue (
            video_id text PRIMARY KEY,
            ordinal bigint NOT NULL UNIQUE,
            kind text NOT NULL,
            failed boolean NOT NULL,
            next_attempt_at timestamptz NOT NULL,
            item jsonb NOT NULL
        );
        CREATE INDEX IF NOT EXISTS library_scrape_queue_ready
            ON library_scrape_queue (failed, next_attempt_at, ordinal);
        CREATE UNIQUE INDEX IF NOT EXISTS library_scrape_queue_identity_ci
            ON library_scrape_queue (lower(video_id));
        """;

    // These files are user state, not regenerated caches. Keep their exact bytes
    // until each writer has a typed PostgreSQL implementation. The index and
    // metadata have dedicated tables and must not be duplicated here.
    private static readonly string[] DocumentNames =
    [
        "config.json", "personal-ratings.json", "review-history.jsonl",
        "review-views.json", "graph-move-history.json",
        "duplicate-metadata-history.json", "site-list-import.json",
        "role-profiles.json", "ai-budget.json", "javinfo-usage.json",
        "scrape-source-backoff.json", "scrape-queue.json",
        "scrape-queue.state.json", "plugin-candidates.json",
        "missing-cleanup.json", "plugins.json",
        "scrape-queue.state.json.bak", "scrape-queue.json.bak",
        "scrape-source-backoff.json.bak"
    ];

    internal static bool IsDocumentName(string? name) => name is not null &&
        (DocumentNames.Contains(name, StringComparer.Ordinal) ||
         IsQueueGenerationName(name, "scrape-queue.snapshot.", ".json") ||
         IsQueueGenerationName(name, "scrape-queue.journal.", ".bin") ||
         IsHashDocumentName(name, "title-candidates/", 64) ||
         IsHashDocumentName(name, "ai-jobs/", 32));

    internal static bool IsQueueDocumentName(string name) =>
        name is "scrape-queue.json" or "scrape-queue.json.bak" or
            "scrape-queue.state.json" or "scrape-queue.state.json.bak" ||
        IsQueueGenerationName(name, "scrape-queue.snapshot.", ".json") ||
        IsQueueGenerationName(name, "scrape-queue.journal.", ".bin");

    private static bool IsQueueGenerationName(string name, string prefix, string suffix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) &&
        name.EndsWith(suffix, StringComparison.Ordinal) &&
        name.Length == prefix.Length + 8 + suffix.Length &&
        name.AsSpan(prefix.Length, 8).ToArray().All(char.IsAsciiDigit);

    private static bool IsHashDocumentName(string name, string prefix, int hexLength) =>
        name.StartsWith(prefix, StringComparison.Ordinal) &&
        name.EndsWith(".json", StringComparison.Ordinal) &&
        name.Length == prefix.Length + hexLength + ".json".Length &&
        name.AsSpan(prefix.Length, hexLength).ToArray().All(Uri.IsHexDigit);

    internal static IEnumerable<string> EnumerateDocumentNames(string directory)
    {
        foreach (var name in DocumentNames)
            if (File.Exists(Path.Combine(directory, name))) yield return name;
        foreach (var pattern in new[] { "scrape-queue.snapshot.*.json", "scrape-queue.journal.*.bin" })
        foreach (var path in Directory.EnumerateFiles(directory,
                     pattern, SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (IsDocumentName(name)) yield return name;
        }
        foreach (var subdir in new[] { "title-candidates", "ai-jobs" })
        {
            var path = Path.Combine(directory, subdir);
            if (!Directory.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("资料目录不能是链接：" + subdir);
            foreach (var file in Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly))
            {
                var name = subdir + "/" + Path.GetFileName(file);
                if (IsDocumentName(name)) yield return name;
            }
        }
    }

    internal sealed record Result(int VideoCount, int MetadataCount, int FolderCount,
        string IndexSha256, string MetadataSha256);

    internal static async Task<Result> ImportAsync(string directory, string connectionString,
        CancellationToken cancellationToken = default, string? sourceLeasePath = null)
    {
        // Take the same exclusive offline lease as backup/restore before
        // reading any source bytes. A running app holds a shared lease for its
        // whole session, so an import cannot observe a half-written library.
        using var offlineLease = new FileStream(sourceLeasePath ?? Path.Combine(directory, ".library-session.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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
        ValidateSource(index, checkedMetadata);

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
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY library_document (name, content, sha256) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            var names = EnumerateDocumentNames(directory).Distinct(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("资料文件不能是文件链接：" + name);
                var content = await File.ReadAllBytesAsync(path, cancellationToken);
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(name, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(content, NpgsqlDbType.Bytea, cancellationToken);
                await writer.WriteAsync(Convert.ToHexString(SHA256.HashData(content)),
                    NpgsqlDbType.Text, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }
        // The queue's latest tasks may live only in the binary journal after
        // its JSON checkpoint. Load the verified replay result, not the stale
        // scrape-queue.json snapshot, while the offline source lease is held.
        var queue = ScrapeQueueStore.Load(directory);
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY library_scrape_queue (video_id, ordinal, kind, failed, next_attempt_at, item) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            for (var ordinal = 0; ordinal < queue.Count; ordinal++)
            {
                var item = queue[ordinal];
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(item.VideoId, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync((long)ordinal, NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(item.Kind, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(item.Failed, NpgsqlDbType.Boolean, cancellationToken);
                await writer.WriteAsync(item.NextAttemptAt.ToUniversalTime(),
                    NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteAsync(JsonSerializer.Serialize(item), NpgsqlDbType.Jsonb,
                    cancellationToken);
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

    internal static void ValidateSource(LibraryIndex index, IReadOnlyDictionary<string, MovieMetadata> checkedMetadata)
    {
        if (checkedMetadata.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Value is null || pair.Value.SchemaVersion is < 1 or > 2))
            throw new InvalidDataException("资料文件含无效版本，未导入。 ");
        if (index.Folders is null || index.Entries is null ||
            index.Folders.Any(string.IsNullOrWhiteSpace) ||
            index.Folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != index.Folders.Length ||
            index.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Path) ||
                !Path.IsPathFullyQualified(e.Path) || e.Size < 0 ||
                e.ModifiedTicks < DateTime.MinValue.Ticks || e.ModifiedTicks > DateTime.MaxValue.Ticks))
            throw new InvalidDataException("视频索引含无效记录，未导入。 ");
        if (index.Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != index.Entries.Count)
            throw new InvalidDataException("视频索引含重复路径，未导入。 ");
        if (index.Entries.Select(e => VideoId(e.Path)).Distinct(StringComparer.Ordinal).Count()
            != index.Entries.Count)
            throw new InvalidDataException("视频索引含重复身份 ID，未导入。 ");

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
        var documentNames = EnumerateDocumentNames(directory).ToHashSet(StringComparer.Ordinal);
        var canonicalQueueExport = File.Exists(Path.Combine(directory,
            "database-export-manifest.json"));
        await using (var command = new NpgsqlCommand(
            "SELECT name,content,sha256 FROM library_document", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                if (canonicalQueueExport && IsQueueDocumentName(name))
                {
                    documentNames.Remove(name);
                    continue;
                }
                if (!documentNames.Remove(name)) return false;
                var actual = (byte[])reader[1];
                if (reader.GetString(2) != Convert.ToHexString(SHA256.HashData(actual)) ||
                    !actual.SequenceEqual(await File.ReadAllBytesAsync(
                        Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar)),
                        cancellationToken))) return false;
            }
        if (canonicalQueueExport) documentNames.Remove("scrape-queue.json");
        if (documentNames.Count != 0) return false;
        var sourceQueue = ScrapeQueueStore.Load(directory);
        var queueOrdinal = 0;
        await using (var command = new NpgsqlCommand(
            "SELECT video_id,ordinal,item::text FROM library_scrape_queue ORDER BY ordinal", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                if (queueOrdinal >= sourceQueue.Count ||
                    reader.GetInt64(1) != queueOrdinal ||
                    reader.GetString(0) != sourceQueue[queueOrdinal].VideoId) return false;
                using var saved = JsonDocument.Parse(reader.GetString(2));
                using var expected = JsonDocument.Parse(JsonSerializer.Serialize(sourceQueue[queueOrdinal]));
                if (!JsonElement.DeepEquals(saved.RootElement, expected.RootElement)) return false;
                queueOrdinal++;
            }
        if (queueOrdinal != sourceQueue.Count) return false;
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
