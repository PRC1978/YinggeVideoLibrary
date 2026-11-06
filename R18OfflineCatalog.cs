using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace YinggeVideoLibrary;

// r18.dev publishes a plain pg_dump. Never execute that SQL in the library's
// privileged connection: import only the documented COPY data into tables we
// create ourselves. The staging schema is invisible until the final rename.
internal static class R18OfflineCatalog
{
    internal const string Source = "R18 离线目录";
    const string Active = "yingge_r18_active";
    const string Previous = "yingge_r18_previous";
    const long MaxCompressedBytes = 2L * 1024 * 1024 * 1024;
    const long MaxExpandedBytes = 20L * 1024 * 1024 * 1024;
    static readonly Regex CopyHeader = new(
        @"^COPY public\.(?<table>[a-z_]+) \((?<columns>[a-z_, ]+)\) FROM stdin;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        ["derived_video"] = "content_id text NOT NULL, dvd_id text, title_en text, title_ja text, comment_en text, comment_ja text, runtime_mins integer, release_date date, sample_url text, maker_id integer, label_id integer, series_id integer, jacket_full_url text, jacket_thumb_url text, gallery_full_first text, gallery_full_last text, gallery_thumb_first text, gallery_thumb_last text, site_id integer NOT NULL, service_code text NOT NULL",
        ["derived_actor"] = "id integer NOT NULL, name_kanji text, name_kana text",
        ["derived_actress"] = "id integer NOT NULL, name_romaji text, image_url text, name_kanji text, name_kana text",
        ["derived_director"] = "id integer NOT NULL, name_kanji text, name_kana text, name_romaji text",
        ["derived_maker"] = "id integer NOT NULL, name_en text, name_ja text",
        ["derived_label"] = "id integer NOT NULL, name_en text, name_ja text",
        ["derived_series"] = "id integer NOT NULL, name_en text, name_ja text",
        ["derived_category"] = "id integer NOT NULL, name_en text, name_ja text",
        ["derived_video_actor"] = "content_id text NOT NULL, actor_id integer NOT NULL, ordinality integer, release_date date",
        ["derived_video_actress"] = "content_id text NOT NULL, actress_id integer NOT NULL, ordinality integer, release_date date",
        ["derived_video_director"] = "content_id text NOT NULL, director_id integer NOT NULL",
        ["derived_video_category"] = "content_id text NOT NULL, category_id integer NOT NULL, release_date date"
    };

    internal sealed record ImportResult(long VideoCount, string Sha256, string Version);

    internal static bool TrustedWorkUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "r18.dev" && uri.Port == 443 &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 &&
        Regex.IsMatch(uri.Fragment, @"^#offline-[a-zA-Z0-9_-]{2,100}$", RegexOptions.CultureInvariant) &&
        url == uri.AbsoluteUri;

    internal static bool TrustedPersonIdentity(string? id, string? url)
    {
        var match = Regex.Match(id ?? "", @"^(actress|actor|director):([1-9][0-9]{0,9})$",
            RegexOptions.CultureInvariant);
        return match.Success && url == "https://r18.dev/#offline-person-" +
            match.Groups[1].Value + "-" + match.Groups[2].Value;
    }

    internal static bool IsInstalled(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT to_regclass('yingge_r18_active.yingge_import') IS NOT NULL AND to_regclass('yingge_r18_active.derived_video') IS NOT NULL", connection);
        return (bool)(command.ExecuteScalar() ?? false);
    }

    internal static ImportResult Import(string connectionString, string dataDirectory, string archivePath,
        CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        var archive = new FileInfo(archivePath);
        if (!archive.Exists || archive.Length is < 1024 or > MaxCompressedBytes ||
            !archive.Name.EndsWith(".sql.gz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 r18.dev 提供的 .sql.gz 数据包，文件大小须在 1 KB 至 2 GB 之间。");
        if ((archive.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("数据包不能是文件链接。");
        var versionMatch = Regex.Match(archive.Name, @"r18dotdev_dump_(\d{4}-\d{2}-\d{2})\.sql\.gz$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!versionMatch.Success || !DateOnly.TryParseExact(versionMatch.Groups[1].Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("数据包名称不是 r18.dev 官方发布格式。");
        // COPY expands substantially and PostgreSQL also writes WAL and indexes.
        // A failed preflight must not change the current catalogue.
        var root = Path.GetPathRoot(Path.GetFullPath(dataDirectory))!;
        if (new DriveInfo(root).AvailableFreeSpace < Math.Max(8L * 1024 * 1024 * 1024, archive.Length * 20))
            throw new IOException("导入至少需要约 8 GB 可用空间（含暂存与旧目录库）；未改动现有资料。");
        var stage = "yingge_r18_stage_" + Guid.NewGuid().ToString("N")[..12];
        var found = new HashSet<string>(StringComparer.Ordinal);
        long expandedBytes = 0;
        long videoRows = 0;
        var published = false;
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        try
        {
            // One importer per private cluster. A killed import can leave only
            // an unreferenced staging schema; remove it before starting anew.
            using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(178942602)", connection))
            {
                acquire.CommandTimeout = 0;
                acquire.ExecuteNonQuery();
            }
            using (var stale = new NpgsqlCommand(
                "SELECT nspname FROM pg_namespace WHERE nspname LIKE 'yingge_r18_stage_%'", connection))
            using (var names = stale.ExecuteReader())
            {
                var abandoned = new List<string>();
                while (names.Read()) abandoned.Add(names.GetString(0));
                names.Close();
                foreach (var name in abandoned.Where(name => Regex.IsMatch(name,
                    @"^yingge_r18_stage_[a-f0-9]{12}$", RegexOptions.CultureInvariant)))
                {
                    using var drop = new NpgsqlCommand($"DROP SCHEMA \"{name}\" CASCADE", connection) { CommandTimeout = 0 };
                    drop.ExecuteNonQuery();
                }
            }
            using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{stage}\"", connection))
                create.ExecuteNonQuery();
            foreach (var (table, columns) in Tables)
            {
                using var create = new NpgsqlCommand($"CREATE TABLE \"{stage}\".\"{table}\" ({columns})", connection);
                create.ExecuteNonQuery();
            }
            progress?.Report("正在读取并校验数据包…");
            using var file = File.OpenRead(archive.FullName);
            using var hash = SHA256.Create();
            using var hashed = new CryptoStream(file, hash, CryptoStreamMode.Read);
            using var gzip = new GZipStream(hashed, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false, bufferSize: 65536);
            TextWriter? copy = null;
            string? copying = null;
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    expandedBytes += Encoding.UTF8.GetByteCount(line) + 1;
                    if (expandedBytes > MaxExpandedBytes)
                        throw new InvalidDataException("数据包解压后超过 20 GB，已停止导入。");
                    if (copying is not null)
                    {
                        if (line == @"\.")
                        {
                            copy!.Dispose(); copy = null;
                            progress?.Report($"已读取 {copying}；作品 {videoRows:N0} 条");
                            copying = null;
                        }
                        else
                        {
                            copy!.WriteLine(line);
                            if (copying == "derived_video") videoRows++;
                        }
                        continue;
                    }
                    var match = CopyHeader.Match(line);
                    if (!match.Success) continue;
                    var table = match.Groups["table"].Value;
                    if (!Tables.TryGetValue(table, out var definition)) continue;
                    var expected = string.Join(", ", definition.Split(',').Select(part => part.Trim().Split(' ')[0]));
                    if (!string.Equals(match.Groups["columns"].Value, expected, StringComparison.Ordinal))
                        throw new InvalidDataException($"r18.dev 的 {table} 表结构已变化，旧目录库保持可用。");
                    if (!found.Add(table)) throw new InvalidDataException($"数据包重复包含 {table}。");
                    copying = table;
                    copy = connection.BeginTextImport($"COPY \"{stage}\".\"{table}\" ({expected}) FROM STDIN");
                }
                if (copying is not null) throw new InvalidDataException("数据包在 COPY 段内提前结束。");
            }
            finally { copy?.Dispose(); }
            if (!Tables.Keys.All(found.Contains) || videoRows < 1)
                throw new InvalidDataException("数据包缺少必要的作品或人物表，旧目录库保持可用。");
            // Drain the compressed stream before reading the hash, and require
            // gzip's checksum/end marker to have been processed successfully.
            var consumedHash = hash.Hash ?? throw new InvalidDataException("数据包哈希缺失。");
            using var entireArchive = File.OpenRead(archive.FullName);
            var completeHash = SHA256.HashData(entireArchive);
            if (!CryptographicOperations.FixedTimeEquals(consumedHash, completeHash))
                throw new InvalidDataException("数据包含额外内容或在导入时变化，旧目录库保持可用。");
            var digest = Convert.ToHexString(completeHash);
            using (var index = new NpgsqlCommand($"""
                CREATE INDEX ON "{stage}".derived_video (lower(replace(dvd_id, '-', '')));
                CREATE INDEX ON "{stage}".derived_video (lower(content_id));
                CREATE INDEX ON "{stage}".derived_video (lower(title_en) text_pattern_ops);
                CREATE INDEX ON "{stage}".derived_video (lower(title_ja) text_pattern_ops);
                CREATE INDEX ON "{stage}".derived_video USING GIN (to_tsvector('simple', coalesce(title_en,'')));
                CREATE INDEX ON "{stage}".derived_video_actress (content_id);
                CREATE INDEX ON "{stage}".derived_video_actor (content_id);
                CREATE INDEX ON "{stage}".derived_video_director (content_id);
                CREATE INDEX ON "{stage}".derived_video_category (content_id);
                CREATE UNIQUE INDEX ON "{stage}".derived_actress (id);
                CREATE UNIQUE INDEX ON "{stage}".derived_actor (id);
                CREATE UNIQUE INDEX ON "{stage}".derived_director (id);
                CREATE UNIQUE INDEX ON "{stage}".derived_maker (id);
                CREATE UNIQUE INDEX ON "{stage}".derived_label (id);
                CREATE UNIQUE INDEX ON "{stage}".derived_series (id);
                CREATE UNIQUE INDEX ON "{stage}".derived_category (id);
                """, connection))
            {
                // Millions of work/category relationships take minutes to index.
                // The default 30-second command timeout would discard a valid import.
                index.CommandTimeout = 0;
                using var cancelIndex = cancellationToken.Register(index.Cancel);
                index.ExecuteNonQuery();
            }
            using (var validate = new NpgsqlCommand($"SELECT count(*) FROM \"{stage}\".derived_video", connection))
                if (Convert.ToInt64(validate.ExecuteScalar(), CultureInfo.InvariantCulture) != videoRows)
                    throw new InvalidDataException("作品数量校验失败，旧目录库保持可用。");
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(178942602)", connection, transaction))
                lockCommand.ExecuteNonQuery();
            if (SchemaExists(connection, transaction, Previous))
                new NpgsqlCommand($"DROP SCHEMA \"{Previous}\" CASCADE", connection, transaction).ExecuteNonQuery();
            if (SchemaExists(connection, transaction, Active))
                new NpgsqlCommand($"ALTER SCHEMA \"{Active}\" RENAME TO \"{Previous}\"", connection, transaction).ExecuteNonQuery();
            new NpgsqlCommand($"ALTER SCHEMA \"{stage}\" RENAME TO \"{Active}\"", connection, transaction).ExecuteNonQuery();
            using (var marker = new NpgsqlCommand($"""
                CREATE TABLE IF NOT EXISTS "{Active}".yingge_import (
                    id integer PRIMARY KEY CHECK (id=1), version text NOT NULL,
                    archive_sha256 text NOT NULL, imported_at timestamptz NOT NULL DEFAULT now());
                INSERT INTO "{Active}".yingge_import(id,version,archive_sha256)
                VALUES (1,@version,@hash)
                """, connection, transaction))
            {
                marker.Parameters.AddWithValue("version", versionMatch.Groups[1].Value);
                marker.Parameters.AddWithValue("hash", digest);
                marker.ExecuteNonQuery();
            }
            transaction.Commit();
            published = true;
            progress?.Report($"r18.dev 离线目录已导入：{videoRows:N0} 部");
            return new ImportResult(videoRows, digest, versionMatch.Groups[1].Value);
        }
        finally
        {
            if (!published)
                try { using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{stage}\" CASCADE", connection); drop.ExecuteNonQuery(); }
                catch { /* A later import can remove an abandoned stage. */ }
        }
    }

    static bool SchemaExists(NpgsqlConnection connection, NpgsqlTransaction transaction, string name)
    {
        using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname=@name)", connection, transaction);
        command.Parameters.AddWithValue("name", name);
        return (bool)(command.ExecuteScalar() ?? false);
    }

    internal static IReadOnlyList<MovieMetadata> SearchCode(string connectionString, string code)
    {
        var key = ScrapeCodeIdentity.Key(code).ToLowerInvariant();
        if (key.Length < 4 || key.Length > 32) return [];
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        if (!SchemaExistsWithoutTransaction(connection, Active)) return [];
        return SearchDetails(connection, code, null);
    }

    static IReadOnlyList<MovieMetadata> SearchDetails(NpgsqlConnection connection, string? code, string? exactContentId)
    {
        var key = code is null ? "" : ScrapeCodeIdentity.Key(code).ToLowerInvariant();
        if (exactContentId is null && (key.Length < 4 || key.Length > 32)) return [];
        if (exactContentId is not null && (exactContentId.Length is < 1 or > 128 ||
            exactContentId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))) return [];
        var where = exactContentId is null
            ? "lower(replace(v.dvd_id,'-',''))=@compact OR lower(v.content_id)=@key"
            : "lower(v.content_id)=@contentId";
        using var command = new NpgsqlCommand($"""
            SELECT v.content_id, v.dvd_id, v.title_en, v.title_ja,
                   v.comment_ja, v.comment_en, v.runtime_mins, v.release_date,
                   v.jacket_full_url, v.jacket_thumb_url,
                   coalesce(m.name_ja,m.name_en), coalesce(l.name_ja,l.name_en),
                   coalesce(s.name_ja,s.name_en)
            FROM "{Active}".derived_video v
            LEFT JOIN "{Active}".derived_maker m ON m.id=v.maker_id
            LEFT JOIN "{Active}".derived_label l ON l.id=v.label_id
            LEFT JOIN "{Active}".derived_series s ON s.id=v.series_id
            WHERE {where}
            ORDER BY v.release_date DESC NULLS LAST, v.content_id
            LIMIT 12
            """, connection);
        if (exactContentId is null)
        {
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("compact", key.Replace("-", "", StringComparison.Ordinal));
        }
        else command.Parameters.AddWithValue("contentId", exactContentId.ToLowerInvariant());
        var results = new List<MovieMetadata>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string? Get(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
                var contentId = reader.GetString(0);
                var dvdId = Get(1);
                var matchedContentId = code is not null && ScrapeCodeIdentity.Same(contentId, code);
                var record = new MovieMetadata
                {
                    Code = matchedContentId ? contentId : (string.IsNullOrWhiteSpace(dvdId) ? contentId : dvdId),
                    Title = Get(2) ?? Get(3) ?? contentId,
                    OriginalTitle = Get(3), Summary = Get(4) ?? Get(5),
                    RuntimeMinutes = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    ReleaseDate = reader.IsDBNull(7) ? null : reader.GetDateTime(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    PosterUrl = PosterUrl(Get(8)) ?? PosterUrl(Get(9)), Maker = Get(10), Label = Get(11), Series = Get(12),
                    Source = Source, SourceUrl = "https://r18.dev/#offline-" + Uri.EscapeDataString(contentId),
                    DetailFetched = true, DetailIdentityVerified = true, IsAdultContent = true,
                    DetailNote = "来自用户导入的 r18.dev 离线数据包；仍需核对本地作品身份",
                    ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["R18"] = contentId }
                };
                if (matchedContentId && !string.IsNullOrWhiteSpace(dvdId)) record.ExternalIds["R18.DvdId"] = dvdId;
                if (exactContentId is not null || ScrapeCodeIdentity.Same(record.Code, code) || ScrapeCodeIdentity.Same(contentId, code))
                    results.Add(record);
            }
        }
        foreach (var record in results)
        {
            var id = record.ExternalIds["R18"];
            using var credits = new NpgsqlCommand($"""
                SELECT person_name, role, person_id FROM (
                    SELECT coalesce(a.name_kanji,a.name_romaji,a.name_kana) person_name,
                           'actress' role, va.ordinality ord, a.id person_id
                    FROM "{Active}".derived_video_actress va
                    JOIN "{Active}".derived_actress a ON a.id=va.actress_id
                    WHERE va.content_id=@id
                    UNION ALL
                    SELECT coalesce(a.name_kanji,a.name_kana), 'actor', va.ordinality, a.id
                    FROM "{Active}".derived_video_actor va
                    JOIN "{Active}".derived_actor a ON a.id=va.actor_id
                    WHERE va.content_id=@id
                    UNION ALL
                    SELECT coalesce(d.name_kanji,d.name_romaji,d.name_kana), 'director', 0, d.id
                    FROM "{Active}".derived_video_director vd
                    JOIN "{Active}".derived_director d ON d.id=vd.director_id
                    WHERE vd.content_id=@id
                ) people WHERE person_name IS NOT NULL
                ORDER BY role, ord NULLS LAST LIMIT 80
                """, connection);
            credits.Parameters.AddWithValue("id", id);
            using (var people = credits.ExecuteReader())
                while (people.Read())
                {
                    var name = people.GetString(0);
                    var kind = people.GetString(1);
                    var personId = people.GetInt32(2);
                    if (kind == "director") record.Director ??= name;
                    else if (!record.Actors.Contains(name, StringComparer.Ordinal)) record.Actors.Add(name);
                    if (personId > 0 && !string.IsNullOrWhiteSpace(name))
                    {
                        var identity = kind + ":" + personId.ToString(CultureInfo.InvariantCulture);
                        record.PersonCredits.Add(new VerifiedPersonCredit
                        {
                            Provider = "R18", PersonId = identity, Name = name,
                            PersonUrl = "https://r18.dev/#offline-person-" + kind + "-" + personId,
                            VerifiedFromWorkUrl = record.SourceUrl
                        });
                    }
                }
            using var categories = new NpgsqlCommand($"""
                SELECT coalesce(c.name_ja,c.name_en)
                FROM "{Active}".derived_video_category vc
                JOIN "{Active}".derived_category c ON c.id=vc.category_id
                WHERE vc.content_id=@id AND coalesce(c.name_ja,c.name_en) IS NOT NULL
                LIMIT 40
                """, connection);
            categories.Parameters.AddWithValue("id", id);
            using (var tags = categories.ExecuteReader())
                while (tags.Read()) record.Genres.Add(tags.GetString(0));
            record.StampAvailableFields("本机离线目录");
        }
        return results;
    }

    internal static IReadOnlyList<MovieMetadata> SearchTitle(string connectionString, string title)
    {
        var query = title.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        if (query.Length is < 3 or > 80) return [];
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        if (!SchemaExistsWithoutTransaction(connection, Active)) return [];
        using var command = new NpgsqlCommand($"""
            SELECT content_id
            FROM "{Active}".derived_video
            WHERE lower(title_en) LIKE @prefix ESCAPE '\'
               OR lower(title_ja) LIKE @prefix ESCAPE '\'
               OR to_tsvector('simple',coalesce(title_en,'')) @@ plainto_tsquery('simple',@text)
            ORDER BY CASE WHEN lower(title_en)=@text OR lower(title_ja)=@text THEN 0
                          WHEN lower(title_en) LIKE @prefix ESCAPE '\' OR lower(title_ja) LIKE @prefix ESCAPE '\' THEN 1
                          ELSE 2 END,
                     release_date DESC NULLS LAST
            LIMIT 12
            """, connection) { CommandTimeout = 20 };
        command.Parameters.AddWithValue("prefix", query.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
        command.Parameters.AddWithValue("text", query);
        var contentIds = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) contentIds.Add(reader.GetString(0));
        var results = new List<MovieMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contentId in contentIds)
            foreach (var record in SearchDetails(connection, null, contentId))
                if (seen.Add(record.ExternalIds.GetValueOrDefault("R18") ?? record.Code)) results.Add(record);
        return results.Take(12).ToList();
    }

    // The dump stores DMM jacket paths without a scheme, host, or extension.
    // Accept only a narrow path grammar before constructing a media URL.
    internal static string? PosterUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute))
            return absolute.Scheme == Uri.UriSchemeHttps &&
                absolute.Host.Equals("pics.dmm.co.jp", StringComparison.OrdinalIgnoreCase)
                ? absolute.AbsoluteUri : null;
        if (!Regex.IsMatch(path, @"^(digital/video|mono/movie/adult)/[a-zA-Z0-9_/-]+$", RegexOptions.CultureInvariant) ||
            path.Contains("..", StringComparison.Ordinal)) return null;
        return "https://pics.dmm.co.jp/" + path + ".jpg";
    }

    static bool SchemaExistsWithoutTransaction(NpgsqlConnection connection, string name)
    {
        using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname=@name)", connection);
        command.Parameters.AddWithValue("name", name);
        return (bool)(command.ExecuteScalar() ?? false);
    }
}
