using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace YinggeVideoLibrary;

// Export an internally consistent database revision to a new, empty directory.
// The old file-based app can read these records during a controlled rollback.
// Optional image inclusion requires an offline lease on the source library.
internal static class PostgresLibraryExport
{
    private const string ManifestName = "database-export-manifest.json";
    private static readonly Regex PosterName = new(@"^[a-zA-Z0-9_-]+\.(?:jpg|jpeg|png|webp|bmp)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CustomThumbName = new(@"^[a-zA-Z0-9_-]+-custom\.(?:jpg|jpeg|png|webp|bmp)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    internal sealed record Result(int VideoCount, int MetadataCount, int DocumentCount);
    internal sealed record ExportedFile(long Length, string Sha256);
    internal sealed record Manifest(int SchemaVersion, Dictionary<string, ExportedFile> Files);

    internal static async Task<Result> ExportAsync(string connectionString, string destination,
        CancellationToken cancellationToken = default, string? assetDirectory = null)
    {
        destination = Path.GetFullPath(destination);
        assetDirectory = assetDirectory is null ? null : Path.GetFullPath(assetDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("导出目录已存在；拒绝覆盖已有数据。 ");
        if (assetDirectory is not null && !Directory.Exists(assetDirectory))
            throw new DirectoryNotFoundException("图片来源目录不存在。 ");
        using var offlineLease = assetDirectory is null ? null : new FileStream(
            Path.Combine(assetDirectory, ".library-session.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
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
        try
        {

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
                if (PostgresLibraryMigration.IsQueueDocumentName(name)) continue;
                var bytes = (byte[])reader[1];
                if (reader.GetString(2) != Convert.ToHexString(SHA256.HashData(bytes)))
                    throw new InvalidDataException("数据库资料校验失败：" + name);
                var output = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllBytesAsync(output, bytes, cancellationToken);
                documentCount++;
            }
        var queueItems = new List<ScrapeQueueItem>();
        await using (var command = new NpgsqlCommand(
            "SELECT video_id,item::text FROM library_scrape_queue ORDER BY ordinal", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var item = JsonSerializer.Deserialize<ScrapeQueueItem>(reader.GetString(1))
                    ?? throw new InvalidDataException("数据库队列含无效任务。 ");
                if (item.VideoId != reader.GetString(0))
                    throw new InvalidDataException("数据库队列身份不一致。 ");
                queueItems.Add(item);
            }
        await File.WriteAllBytesAsync(Path.Combine(staging, "scrape-queue.json"),
            JsonSerializer.SerializeToUtf8Bytes(queueItems), cancellationToken);
        documentCount++;
        await transaction.CommitAsync(cancellationToken);
        if (assetDirectory is not null)
        {
            CopyAssets(assetDirectory, staging, "posters", PosterName);
            CopyAssets(assetDirectory, staging, "thumbs", CustomThumbName);
        }
        var files = new Dictionary<string, ExportedFile>(StringComparer.Ordinal);
        foreach (var path in EnumerateExportFiles(staging))
        {
            var name = Path.GetRelativePath(staging, path).Replace(Path.DirectorySeparatorChar, '/');
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            files.Add(name, new ExportedFile(bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes))));
        }
        await File.WriteAllBytesAsync(Path.Combine(staging, ManifestName),
            JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, files)), cancellationToken);
        if (!await VerifyOfflineAsync(staging, cancellationToken))
            throw new InvalidDataException("数据库回退包离线校验失败，未完成导出。 ");
        Directory.Move(staging, destination);
        return new(entries.Count, metadata.Count, documentCount);
        }
        finally
        {
            // A failed export must not leave a plausible backup behind. The
            // final directory only appears after manifest verification.
            if (Directory.Exists(staging) &&
                Path.GetDirectoryName(staging) == Path.GetDirectoryName(destination) &&
                (File.GetAttributes(staging) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(staging, recursive: true);
        }
    }

    internal static async Task<bool> VerifyOfflineAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllBytesAsync(
                Path.Combine(directory, ManifestName), cancellationToken));
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.Files is null ||
                !manifest.Files.ContainsKey("library-index.json") ||
                !manifest.Files.ContainsKey("metadata.json")) return false;
            var actualNames = EnumerateExportFiles(directory)
                .Select(path => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(name => name != ManifestName).ToHashSet(StringComparer.Ordinal);
            if (!actualNames.SetEquals(manifest.Files.Keys)) return false;
            foreach (var (name, expected) in manifest.Files)
            {
                if (name != "library-index.json" && name != "metadata.json" &&
                    !PostgresLibraryMigration.IsDocumentName(name) && !IsAssetName(name)) return false;
                var path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                if (bytes.LongLength != expected.Length ||
                    Convert.ToHexString(SHA256.HashData(bytes)) != expected.Sha256) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateExportFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            yield return path;
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            if (Path.GetFileName(child) is not ("title-candidates" or "ai-jobs" or "posters" or "thumbs"))
                throw new InvalidDataException("导出目录包含未列入清单的子目录。 ");
        foreach (var name in new[] { "title-candidates", "ai-jobs", "posters", "thumbs" })
        {
            var child = Path.Combine(directory, name);
            if (!Directory.Exists(child)) continue;
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("导出目录包含文件链接：" + name);
            foreach (var path in Directory.EnumerateFiles(child, "*", SearchOption.TopDirectoryOnly))
            {
                if (name is ("posters" or "thumbs") && !IsAssetName(
                    name + "/" + Path.GetFileName(path)))
                    throw new InvalidDataException("导出目录包含不允许的图片文件名。 ");
                yield return path;
            }
        }
    }

    private static bool IsAssetName(string relative)
    {
        var slash = relative.IndexOf('/');
        if (slash < 0 || relative.LastIndexOf('/') != slash) return false;
        var folder = relative[..slash];
        var name = relative[(slash + 1)..];
        return folder switch
        {
            "posters" => PosterName.IsMatch(name),
            "thumbs" => CustomThumbName.IsMatch(name),
            _ => false
        };
    }

    private static void CopyAssets(string sourceDirectory, string staging,
        string folderName, Regex allowed)
    {
        var folder = Path.Combine(sourceDirectory, folderName);
        if (!Directory.Exists(folder)) return;
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("图片目录不能是文件链接：" + folderName);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!allowed.IsMatch(name)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("图片文件不能是文件链接：" + name);
            var target = Path.Combine(staging, folderName, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
    }
}
