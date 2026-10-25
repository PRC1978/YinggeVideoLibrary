using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// This is an import of a file the user already exported. No site login,
// network request, or implicit metadata/favorite mutation occurs here.
internal static class SiteListImportStore
{
    const string FileName = "site-list-import.json";
    const long MaxExportBytes = 8L * 1024 * 1024;
    const long MaxStoredBytes = 30L * 1024 * 1024;
    const int MaxRows = 25_000;
    static readonly Mutex WriteMutex = new(false, "Local\\YinggeVideoLibrary.SiteListImportWrite");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static readonly Regex CodePattern = new(@"^(?:[A-Za-z]{2,}[-_ ]?\d{2,}|tt\d{5,})$", RegexOptions.Compiled);

    internal sealed record LocalVideo(string VideoId, string FileTitle, string DisplayTitle,
        string Code, string MetadataTitle, string OriginalTitle, string SourceUrl,
        string Path = "", long Size = 0, long ModifiedTicks = 0);

    internal sealed class PreviewRow
    {
        public int Number { get; set; }
        public string Source { get; set; } = "";
        public string Title { get; set; } = "";
        public string Code { get; set; } = "";
        public string Url { get; set; } = "";
        public string Key { get; set; } = "";
        public string Status { get; set; } = "";
        public string Reason { get; set; } = "";
        public string VideoId { get; set; } = "";
        public string LocalTitle { get; set; } = "";
        public string LocalFingerprint { get; set; } = "";
        public string ContentSha256 { get; set; } = "";
        public int CandidateCount { get; set; }
        public string MatchSummary => LocalTitle.Length > 0 ? LocalTitle : Reason;
    }

    internal sealed class Preview
    {
        public string FilePath { get; set; } = "";
        public string FileSha256 { get; set; } = "";
        public long StoreRevision { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public List<PreviewRow> Rows { get; set; } = [];
        public int Matched => Rows.Count(row => row.Status == "已匹配");
        public int Pending => Rows.Count(row => row.Status == "待核对");
        public int Unmatched => Rows.Count(row => row.Status == "未匹配");
        public int Duplicates => Rows.Count(row => row.Status == "重复");
        public int Importable => Rows.Count - Duplicates;
    }

    internal readonly record struct HashProgress(int Done, int Total);

    internal sealed class Entry
    {
        public string Key { get; set; } = "";
        public string BatchId { get; set; } = "";
        public string Source { get; set; } = "";
        public string Title { get; set; } = "";
        public string Code { get; set; } = "";
        public string Url { get; set; } = "";
        public string Status { get; set; } = "";
        public string VideoId { get; set; } = "";
        public string LocalTitle { get; set; } = "";
        public string LocalFingerprint { get; set; } = "";
        public string ContentSha256 { get; set; } = "";
        public DateTimeOffset ImportedUtc { get; set; }
    }

    internal sealed class AuditAction
    {
        public string Id { get; set; } = "";
        public string BatchId { get; set; } = "";
        public string Kind { get; set; } = "";
        public DateTimeOffset AtUtc { get; set; }
        public string SourceFileName { get; set; } = "";
        public string FileSha256 { get; set; } = "";
        public int Matched { get; set; }
        public int Pending { get; set; }
        public int Unmatched { get; set; }
        public int Duplicates { get; set; }
        public List<string> EntryKeys { get; set; } = [];
    }

    internal sealed class State
    {
        public int Version { get; set; } = 1;
        public long Revision { get; set; }
        public List<Entry> Entries { get; set; } = [];
        public List<AuditAction> Actions { get; set; } = [];
    }

    internal static State Load(string dataDirectory)
    {
        return WithMutex(() => LoadUnlocked(dataDirectory));
    }

    internal static Preview CreatePreview(string dataDirectory, string exportPath,
        IReadOnlyList<LocalVideo> library, CancellationToken cancellationToken = default,
        IProgress<HashProgress>? progress = null)
    {
        var fullPath = Path.GetFullPath(exportPath);
        var bytes = ReadExport(fullPath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var rows = ParseExport(fullPath, bytes);
        var state = WithMutex(() => LoadUnlocked(dataDirectory));
        var match = new MatchIndex(library);
        var known = state.Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var preview = new Preview
        {
            FilePath = fullPath, FileSha256 = hash, StoreRevision = state.Revision,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            row.Key = MakeKey(row);
            if (!known.Add(row.Key))
            {
                row.Status = "重复";
                row.Reason = "清单中或本次文件中已有同一条目";
            }
            else match.Apply(row);
            preview.Rows.Add(row);
        }
        // Only unique, matched local works need content identity. Unmatched
        // exports and the rest of a large library are never opened here.
        var matched = preview.Rows.Where(row => row.Status == "已匹配")
            .GroupBy(row => row.VideoId, StringComparer.OrdinalIgnoreCase).ToArray();
        var matchedIds = matched.Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexed = library.Where(video => matchedIds.Contains(video.VideoId))
            .GroupBy(video => video.VideoId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var hashed = 0;
        foreach (var group in matched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!indexed.TryGetValue(group.Key, out var candidates) || candidates.Length != 1)
                throw new InvalidOperationException("本地视频索引在预览时出现重复编号，请重新扫描。");
            var video = candidates[0];
            if (video.Path.Length > 0)
            {
                var fresh = RefreshCurrentFileStamp(video)
                    ?? throw new InvalidOperationException("本地匹配文件已不可访问，请重新扫描。");
                if (ComputeLocalFingerprint(fresh) != group.First().LocalFingerprint)
                    throw new InvalidOperationException("本地文件在预览时发生变化，请重新扫描。");
                var content = SiteListLocalActionStore.ComputeContentSha256(fresh, cancellationToken);
                foreach (var row in group) row.ContentSha256 = content;
            }
            progress?.Report(new HashProgress(++hashed, matched.Length));
        }
        return preview;
    }

    internal static AuditAction Commit(string dataDirectory, Preview preview,
        IReadOnlyList<LocalVideo> currentLibrary, CancellationToken cancellationToken = default,
        IProgress<HashProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preview.Importable == 0) throw new InvalidOperationException("没有可导入的新条目。");
        if (preview.Rows.Count == 0 || preview.Rows.Count > MaxRows ||
            preview.Rows.Any(row => row.Key.Length != 64 ||
                row.Status is not ("已匹配" or "待核对" or "未匹配" or "重复") ||
                (row.Status == "已匹配" && (row.VideoId.Length == 0 || row.LocalFingerprint.Length != 64 ||
                    row.ContentSha256.Length is not (0 or 64)))))
            throw new InvalidDataException("导入预览无效，请重新选择文件并预览。");
        var currentHash = Convert.ToHexString(SHA256.HashData(ReadExport(preview.FilePath)));
        if (!currentHash.Equals(preview.FileSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("导出文件在预览后发生变化，请重新预览。");
        // A rescan can move a path-derived VideoId or make a formerly unique
        // title/code ambiguous. Rebuild the match index from the current UI
        // snapshot, and also detect replacement at the same ID and path.
        var currentMatch = new MatchIndex(currentLibrary);
        var matchedIds = preview.Rows.Where(row => row.Status == "已匹配")
            .Select(row => row.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexed = currentLibrary.Where(video => matchedIds.Contains(video.VideoId))
            .GroupBy(video => video.VideoId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var contentCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in preview.Rows.Where(row => row.Status != "重复"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fresh = new PreviewRow
            {
                Source = row.Source, Title = row.Title, Code = row.Code, Url = row.Url
            };
            currentMatch.Apply(fresh);
            if (fresh.Status != row.Status ||
                !fresh.VideoId.Equals(row.VideoId, StringComparison.OrdinalIgnoreCase) ||
                !fresh.LocalFingerprint.Equals(row.LocalFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("本地视频或匹配结果在预览后发生变化，请重新预览。");
            if (row.Status == "已匹配" && indexed.TryGetValue(row.VideoId, out var candidates) &&
                candidates.Length == 1 && candidates[0].Path.Length > 0)
            {
                if (row.ContentSha256.Length != 64)
                    throw new InvalidOperationException("预览缺少本地内容校验，请重新预览。");
                if (!contentCache.TryGetValue(row.VideoId, out var content))
                {
                    var file = RefreshCurrentFileStamp(candidates[0]);
                    if (file is null || ComputeLocalFingerprint(file) != row.LocalFingerprint)
                        throw new InvalidOperationException("本地文件在预览后发生变化，请重新预览。");
                    content = SiteListLocalActionStore.ComputeContentSha256(file, cancellationToken);
                    contentCache[row.VideoId] = content;
                    progress?.Report(new HashProgress(contentCache.Count, matchedIds.Count));
                }
                if (content != row.ContentSha256)
                    throw new InvalidOperationException("本地文件内容在预览后发生变化，请重新预览。");
            }
        }
        return WithMutex(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = LoadUnlocked(dataDirectory);
            if (state.Revision != preview.StoreRevision)
                throw new InvalidOperationException("导入清单已被其他窗口修改，请重新预览。");
            var existing = state.Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            var batch = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var action = new AuditAction
            {
                Id = Guid.NewGuid().ToString("N"), BatchId = batch, Kind = "import",
                AtUtc = now, SourceFileName = Path.GetFileName(preview.FilePath), FileSha256 = preview.FileSha256,
                Matched = preview.Matched, Pending = preview.Pending,
                Unmatched = preview.Unmatched, Duplicates = preview.Duplicates
            };
            foreach (var row in preview.Rows.Where(row => row.Status != "重复"))
            {
                if (!existing.Add(row.Key))
                    throw new InvalidOperationException("导入清单已发生冲突，请重新预览。");
                state.Entries.Add(new Entry
                {
                    Key = row.Key, BatchId = batch, Source = row.Source, Title = row.Title,
                    Code = row.Code, Url = row.Url, Status = row.Status, VideoId = row.VideoId,
                    LocalTitle = row.LocalTitle, LocalFingerprint = row.LocalFingerprint,
                    ContentSha256 = row.ContentSha256,
                    ImportedUtc = now
                });
                action.EntryKeys.Add(row.Key);
            }
            state.Actions.Add(action);
            state.Revision++;
            cancellationToken.ThrowIfCancellationRequested();
            SaveUnlocked(dataDirectory, state);
            return action;
        });
    }

    internal static AuditAction Undo(string dataDirectory, string batchId)
    {
        return WithMutex(() =>
        {
            var state = LoadUnlocked(dataDirectory);
            var import = state.Actions.SingleOrDefault(action => action.Kind == "import" && action.BatchId == batchId)
                ?? throw new InvalidOperationException("找不到这次导入记录。");
            if (state.Actions.Any(action => action.Kind == "undo" && action.BatchId == batchId))
                throw new InvalidOperationException("这次导入已经撤销。");
            var keys = import.EntryKeys.ToHashSet(StringComparer.Ordinal);
            var removed = state.Entries.RemoveAll(entry => entry.BatchId == batchId && keys.Contains(entry.Key));
            if (removed != keys.Count)
                throw new InvalidDataException("导入清单与审核记录不一致，已停止撤销。");
            var undo = new AuditAction
            {
                Id = Guid.NewGuid().ToString("N"), BatchId = batchId, Kind = "undo",
                AtUtc = DateTimeOffset.UtcNow, SourceFileName = import.SourceFileName,
                FileSha256 = import.FileSha256, EntryKeys = [.. import.EntryKeys],
                Matched = import.Matched, Pending = import.Pending,
                Unmatched = import.Unmatched, Duplicates = import.Duplicates
            };
            state.Actions.Add(undo);
            state.Revision++;
            SaveUnlocked(dataDirectory, state);
            return undo;
        });
    }

    static byte[] ReadExport(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只支持用户导出的 CSV 或 JSON 清单。");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaxExportBytes)
            throw new InvalidDataException("导出文件不存在、为空，或超过 8 MB 上限。");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxExportBytes) throw new InvalidDataException("导出文件超过 8 MB 上限。");
        return bytes;
    }

    static List<PreviewRow> ParseExport(string path, byte[] bytes)
    {
        var text = StrictUtf8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\ufeff') text = text[1..];
        var rows = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? ParseJson(text) : ParseCsv(text);
        if (rows.Count == 0) throw new InvalidDataException("文件中没有可导入的条目。");
        if (rows.Count > MaxRows) throw new InvalidDataException("单次最多预览 25,000 条，请拆分导出文件。");
        return rows;
    }

    static List<PreviewRow> ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        JsonElement items = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            var found = false;
            foreach (var property in root.EnumerateObject())
                if (new[] { "items", "favorites", "list", "data", "作品", "收藏" }
                    .Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                { items = property.Value; found = true; break; }
            if (!found) throw new InvalidDataException("JSON 应是条目数组，或含 items/favorites/list/data 数组。");
        }
        if (items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("JSON 清单需要包含条目数组。");
        var result = new List<PreviewRow>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("JSON 清单中的每个条目必须是对象。");
            var fields = item.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
            result.Add(BuildRow(result.Count + 1, fields));
            if (result.Count > MaxRows) throw new InvalidDataException("单次最多预览 25,000 条。");
        }
        return result;
    }

    static List<PreviewRow> ParseCsv(string text)
    {
        if (text.Length > 0 && text[0] == '\ufeff') text = text[1..];
        var headerEnd = text.IndexOfAny(['\r', '\n']);
        if (headerEnd < 0) headerEnd = text.Length;
        var first = text.AsSpan(0, Math.Min(headerEnd, 4096));
        var tabCount = 0; var commaCount = 0; var semicolonCount = 0;
        foreach (var ch in first)
        {
            if (ch == '\t') tabCount++;
            else if (ch == ',') commaCount++;
            else if (ch == ';') semicolonCount++;
        }
        var delimiter = tabCount > commaCount && tabCount > semicolonCount ? '\t'
            : semicolonCount > commaCount ? ';' : ',';
        var records = CsvRecords(text, delimiter);
        if (records.Count == 0) return [];
        var headers = records[0].Select(header => header.Trim().TrimStart('\ufeff')).ToArray();
        if (headers.Any(header => header.Length == 0) ||
            headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            throw new InvalidDataException("CSV 表头有空列或重复列。");
        if (!headers.Any(header => IsAlias(header, "title", "name", "番名", "片名", "标题", "作品名", "作品", "code", "number", "番号", "url", "link", "链接")))
            throw new InvalidDataException("CSV 表头需要标题、番号或链接字段。");
        var rows = new List<PreviewRow>();
        foreach (var record in records.Skip(1))
        {
            if (record.All(value => value.Length == 0)) continue;
            if (record.Count != headers.Length) throw new InvalidDataException($"CSV 第 {rows.Count + 2} 行的列数与表头不一致。");
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++) fields[headers[i]] = record[i];
            rows.Add(BuildRow(rows.Count + 1, fields));
            if (rows.Count > MaxRows) throw new InvalidDataException("单次最多预览 25,000 条。");
        }
        return rows;
    }

    static List<List<string>> CsvRecords(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"' && field.Length == 0) quoted = true;
            else if (ch == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear(); rows.Add(row); row = [];
                if (rows.Count > MaxRows + 1) throw new InvalidDataException("单次最多预览 25,000 条。");
            }
            else field.Append(ch);
        }
        if (quoted) throw new InvalidDataException("CSV 引号没有正确闭合。");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    static PreviewRow BuildRow(int number, IReadOnlyDictionary<string, string> fields)
    {
        string Value(params string[] aliases)
        {
            foreach (var pair in fields)
                if (aliases.Contains(pair.Key.Trim(), StringComparer.OrdinalIgnoreCase))
                    return pair.Value.Trim();
            return "";
        }
        var row = new PreviewRow
        {
            Number = number,
            Source = Limited(Value("source", "site", "provider", "来源", "站点"), 100, number),
            Title = Limited(Value("title", "name", "番名", "片名", "标题", "作品名", "作品"), 300, number),
            Code = Limited(Value("code", "number", "番号", "编号", "imdb_id"), 80, number),
            Url = Limited(Value("url", "link", "href", "链接", "网址"), 1200, number)
        };
        if (row.Title.Length == 0 && row.Code.Length == 0 && row.Url.Length == 0)
            throw new InvalidDataException($"第 {number} 条没有标题、番号或链接。");
        if (Uri.TryCreate(row.Url, UriKind.Absolute, out var parsedUrl) && parsedUrl.UserInfo.Length > 0)
            throw new InvalidDataException($"第 {number} 条链接包含账户信息，已停止导入。");
        if (Regex.IsMatch(row.Url, @"(?:\?|&)(?:api[_-]?key|access[_-]?token|token|authorization|auth|session|signature|password)=",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException($"第 {number} 条链接疑似含有凭据，已停止导入。");
        if (row.Source.Length == 0 && Uri.TryCreate(row.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            row.Source = uri.Host;
        if (row.Source.Length == 0) row.Source = "未注明来源";
        return row;
    }

    static string Limited(string value, int max, int row)
    {
        if (value.Length > max) throw new InvalidDataException($"第 {row} 条字段过长，已停止导入以免截断作品信息。");
        return value;
    }
    static bool IsAlias(string actual, params string[] aliases) => aliases.Contains(actual, StringComparer.OrdinalIgnoreCase);
    static string NormalizeTitle(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();
    static string NormalizeCode(string value)
    {
        var trimmed = value.Trim().ToUpperInvariant();
        return CodePattern.IsMatch(trimmed) ? string.Concat(trimmed.Where(char.IsLetterOrDigit)) : "";
    }
    static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.UserInfo.Length != 0) return "";
        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
    static string MakeKey(PreviewRow row)
    {
        var identity = NormalizeUrl(row.Url);
        if (identity.Length == 0) identity = NormalizeCode(row.Code);
        if (identity.Length == 0) identity = NormalizeTitle(row.Title);
        if (identity.Length == 0) identity = row.Url.Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            row.Source.Trim().ToUpperInvariant() + "\n" + identity)));
    }

    sealed class MatchIndex
    {
        readonly Dictionary<string, HashSet<string>> urls = new(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> codes = new(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> titles = new(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> prefixes = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> videoTitles = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> videoFingerprints = new(StringComparer.OrdinalIgnoreCase);

        internal MatchIndex(IReadOnlyList<LocalVideo> library)
        {
            foreach (var video in library)
            {
                if (video.VideoId.Length == 0) continue;
                videoTitles[video.VideoId] = video.DisplayTitle;
                var fingerprint = ComputeLocalFingerprint(video);
                if (videoFingerprints.TryGetValue(video.VideoId, out var previous) &&
                    !previous.Equals(fingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("本地视频索引包含冲突编号，请重新扫描后再导入。");
                videoFingerprints[video.VideoId] = fingerprint;
                Add(urls, NormalizeUrl(video.SourceUrl), video.VideoId);
                Add(codes, NormalizeCode(video.Code), video.VideoId);
                foreach (var title in new[] { video.FileTitle, video.DisplayTitle, video.MetadataTitle, video.OriginalTitle })
                {
                    var normalized = NormalizeTitle(title);
                    if (normalized.Length < 5) continue;
                    Add(titles, normalized, video.VideoId);
                    Add(prefixes, normalized[..Math.Min(5, normalized.Length)], video.VideoId);
                }
            }
        }

        static void Add(Dictionary<string, HashSet<string>> map, string key, string id)
        {
            if (key.Length == 0) return;
            if (!map.TryGetValue(key, out var group)) map[key] = group = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            group.Add(id);
        }

        internal void Apply(PreviewRow row)
        {
            var url = NormalizeUrl(row.Url);
            var code = NormalizeCode(row.Code);
            var title = NormalizeTitle(row.Title);
            if (url.Length > 0 && code.Length > 0 &&
                urls.TryGetValue(url, out var urlIds) && codes.TryGetValue(code, out var codeIds) &&
                !urlIds.Overlaps(codeIds))
            {
                row.Status = "待核对";
                row.Reason = "来源链接与编号指向不同本地视频";
                row.CandidateCount = urlIds.Union(codeIds).Count();
                return;
            }
            HashSet<string>? candidates = null;
            string evidence = "";
            if (url.Length > 0 && urls.TryGetValue(url, out var byUrl)) { candidates = byUrl; evidence = "来源链接完全一致"; }
            else if (code.Length > 0 && codes.TryGetValue(code, out var byCode)) { candidates = byCode; evidence = "明确编号完全一致"; }
            else if (title.Length >= 5 && titles.TryGetValue(title, out var byTitle)) { candidates = byTitle; evidence = "完整标题完全一致"; }
            if (candidates is { Count: 1 })
            {
                row.Status = "已匹配";
                row.VideoId = candidates.Single();
                row.LocalTitle = videoTitles[row.VideoId];
                row.LocalFingerprint = videoFingerprints[row.VideoId];
                row.Reason = evidence;
                row.CandidateCount = 1;
            }
            else if (candidates is { Count: > 1 })
            {
                row.Status = "待核对";
                row.Reason = evidence + "，对应多个本地视频";
                row.CandidateCount = candidates.Count;
            }
            else if (title.Length >= 5 && prefixes.TryGetValue(title[..Math.Min(5, title.Length)], out var near))
            {
                row.Status = "待核对";
                row.Reason = "标题前缀相近，尚未确认同一作品";
                row.CandidateCount = near.Count;
            }
            else
            {
                row.Status = "未匹配";
                row.Reason = "本地视频库暂无明确对应项";
            }
        }
    }

    internal static string ComputeLocalFingerprint(LocalVideo video)
    {
        var values = new[]
        {
            video.VideoId.ToUpperInvariant(),
            video.Path.Replace('/', '\\').ToUpperInvariant(),
            video.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            video.ModifiedTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            video.FileTitle, video.DisplayTitle, video.Code,
            video.MetadataTitle, video.OriginalTitle, video.SourceUrl
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", values))));
    }

    internal static LocalVideo? RefreshCurrentFileStamp(LocalVideo video)
    {
        if (video.Path.Length == 0) return null;
        var file = new FileInfo(video.Path);
        if (!file.Exists) return null;
        return video with { Size = file.Length, ModifiedTicks = file.LastWriteTime.Ticks };
    }

    internal static bool CanPlayMatchedEntry(Entry entry, LocalVideo? current,
        CancellationToken cancellationToken = default,
        Dictionary<string, string>? contentCache = null)
    {
        if (current is null || entry.Status != "已匹配" ||
            !entry.VideoId.Equals(current.VideoId, StringComparison.OrdinalIgnoreCase) ||
            entry.LocalFingerprint.Length != 64 || entry.ContentSha256.Length != 64 ||
            !entry.LocalFingerprint.Equals(ComputeLocalFingerprint(current), StringComparison.Ordinal)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        if (contentCache is null || !contentCache.TryGetValue(current.VideoId, out var digest))
        {
            digest = SiteListLocalActionStore.ComputeContentSha256(current, cancellationToken);
            contentCache?.Add(current.VideoId, digest);
        }
        return digest.Equals(entry.ContentSha256, StringComparison.Ordinal);
    }

    static State LoadUnlocked(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path))
        {
            if (File.Exists(path + ".bak"))
                throw new InvalidDataException("站点清单主文件缺失，但仍有恢复副本；已停止写入以免丢失历史。");
            return new State();
        }
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path))
            ?? throw new InvalidDataException("站点清单为空或无法读取。");
        Validate(state);
        return state;
    }

    static void Validate(State state)
    {
        if (state.Version != 1 || state.Revision < 0 || state.Entries is null || state.Actions is null ||
            state.Entries.Count > 500_000 || state.Actions.Count > 100_000)
            throw new InvalidDataException("站点清单版本或结构无效，已停止写入。");
        if (state.Entries.Any(entry => entry is null || entry.Key?.Length != 64 || entry.BatchId?.Length != 32 ||
            entry.Source is null || entry.Title is null || entry.Code is null || entry.Url is null ||
            entry.VideoId is null || entry.LocalTitle is null || entry.LocalFingerprint is null ||
            entry.ContentSha256 is null ||
            (entry.Status == "已匹配" && entry.VideoId.Length == 0) ||
            (entry.Status != "已匹配" && entry.VideoId.Length != 0) ||
            (entry.Status == "已匹配" && entry.LocalFingerprint.Length is not (0 or 64)) ||
            (entry.Status == "已匹配" && entry.ContentSha256.Length is not (0 or 64)) ||
            (entry.Status != "已匹配" && entry.LocalFingerprint.Length != 0) ||
            (entry.Status != "已匹配" && entry.ContentSha256.Length != 0) ||
            entry.Status is not ("已匹配" or "待核对" or "未匹配")) ||
            state.Entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() != state.Entries.Count)
            throw new InvalidDataException("站点清单条目冲突，已停止写入。");
        if (state.Actions.Any(action => action is null || action.Id?.Length != 32 || action.BatchId?.Length != 32 ||
            action.SourceFileName is null || action.FileSha256 is null ||
            action.Kind is not ("import" or "undo") || action.EntryKeys is null ||
            action.EntryKeys.Any(key => key?.Length != 64)) ||
            state.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != state.Actions.Count)
            throw new InvalidDataException("站点清单审核历史无效，已停止写入。");
        var importActions = state.Actions.Where(action => action.Kind == "import").ToArray();
        if (importActions.Select(action => action.BatchId).Distinct(StringComparer.Ordinal).Count() != importActions.Length)
            throw new InvalidDataException("站点清单有重复导入批次，已停止写入。");
        var imports = importActions.ToDictionary(action => action.BatchId, StringComparer.Ordinal);
        var undos = state.Actions.Where(action => action.Kind == "undo").ToArray();
        var undoneBatches = undos.Select(action => action.BatchId).ToHashSet(StringComparer.Ordinal);
        var actualKeys = state.Entries.GroupBy(entry => entry.BatchId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Key)
                .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var expectedKeys = importActions.ToDictionary(action => action.BatchId,
            action => action.EntryKeys.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        if (undoneBatches.Count != undos.Length ||
            undos.Any(action => !imports.ContainsKey(action.BatchId)) ||
            state.Entries.Any(entry => !expectedKeys.TryGetValue(entry.BatchId, out var expected) ||
                !expected.Contains(entry.Key)) ||
            imports.Values.Any(import =>
            {
                var expected = expectedKeys[import.BatchId];
                if (import.EntryKeys.Count != expected.Count) return true;
                actualKeys.TryGetValue(import.BatchId, out var actual);
                return undoneBatches.Contains(import.BatchId)
                    ? actual is { Count: > 0 } : actual is null || !actual.SetEquals(expected);
            }))
            throw new InvalidDataException("站点清单与审核历史不一致，已停止写入。");
    }

    static void SaveUnlocked(string dataDirectory, State state)
    {
        Validate(state);
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            if (bytes.Length > MaxStoredBytes)
                throw new InvalidOperationException("导入清单已达到 30 MB 存储上限，请先撤销不需要的批次。");
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    static T WithMutex<T>(Func<T> action)
    {
        var owned = false;
        try
        {
            try { WriteMutex.WaitOne(); owned = true; }
            catch (AbandonedMutexException) { owned = true; }
            return action();
        }
        finally { if (owned) WriteMutex.ReleaseMutex(); }
    }
}
