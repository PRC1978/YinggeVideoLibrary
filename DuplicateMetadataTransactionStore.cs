using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

internal sealed class DuplicateLocalState
{
    public string? PrimaryCategory { get; set; }
    public string[]? CategoryPaths { get; set; }
    public string[]? Tags { get; set; }
    public PersonalRatingEntry? Rating { get; set; }

    public string[] VisibleCategories => CategoryPaths is { Length: > 0 } ? CategoryPaths :
        string.IsNullOrWhiteSpace(PrimaryCategory) ? ["未分类"] : [PrimaryCategory];
    public string[] VisibleTags => Tags ?? [];
    public double? Score => Rating?.Score;
}

internal sealed record DuplicateFileEvidence(string VideoId, string Path, long Size, string Sha256,
    string[] Categories, string[] Tags, double? Score);

internal sealed class DuplicateMetadataTransaction
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = "merge";
    public string? UndoesId { get; set; }
    public string KeepId { get; set; } = "";
    public string KeepPath { get; set; } = "";
    public DuplicateFileEvidence[] Evidence { get; set; } = [];
    public DuplicateLocalState Before { get; set; } = new();
    public DuplicateLocalState After { get; set; } = new();
    public string Sha256 { get; set; } = "";
}

// This journal covers exactly one retained video's local categories, tags and
// personal rating. The media files and other videos are never changed.
internal static class DuplicateMetadataTransactionStore
{
    const string PendingName = "duplicate-metadata-pending.json";
    const string HistoryName = "duplicate-metadata-history.json";
    const long MaxHistoryBytes = 32L * 1024 * 1024;
    const int MaxHistoryEntries = 50_000;
    static readonly Mutex ConfigGate = new(false, @"Local\YinggeVideoLibrary.ConfigWrite");
    static readonly Mutex RatingGate = new(false, @"Local\YinggeVideoLibrary.PersonalRatingWrite");
    static readonly Mutex JournalGate = new(false, @"Local\YinggeVideoLibrary.DuplicateMetadataTransaction");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    internal static bool HasPending(string dataDir) => File.Exists(Path.Combine(dataDir, PendingName));

    internal static DuplicateMetadataTransaction Prepare(string dataDir, DuplicateVideoGroup group,
        DuplicateVideoRecord keep, double? selectedScore, bool explicitScoreChoice)
    {
        if (group.Videos.Count < 2 || group.Videos.Any(video => video.Size != group.Size) ||
            !group.Videos.Any(video => video.Id == keep.Id && video.Path == keep.Path))
            throw new InvalidOperationException("重复组已变更，请重新检测。 ");
        if (selectedScore is { } score && (!double.IsFinite(score) || score is < 0 or > 10 ||
            !group.Videos.Any(video => video.PersonalScore == score)))
            throw new InvalidOperationException("请选择重复组中已有的个人评分。");
        var proposal = DuplicateVideoScanner.ProposeLocalMerge(group, keep);
        if (proposal.RatingNeedsDecision && !explicitScoreChoice)
            throw new InvalidOperationException("个人评分不一致，必须先手动选择保留的评分或不评分。");
        if (!proposal.RatingNeedsDecision && selectedScore != proposal.PersonalScore)
            throw new InvalidOperationException("评分选择与预览不一致，请重新预览。");
        using var config = Enter(ConfigGate);
        using var rating = Enter(RatingGate);
        using var journal = Enter(JournalGate);
        EnsureNoOfflineReplacementPending(dataDir);
        if (HasPending(dataDir)) throw new InvalidOperationException("上一次重复资料合并尚未恢复，请重启软件。");
        var disk = ReadDisk(dataDir);
        foreach (var video in group.Videos)
        {
            var state = ReadState(disk, video.Id);
            if (!state.VisibleCategories.SequenceEqual(video.Categories, StringComparer.OrdinalIgnoreCase) ||
                !state.VisibleTags.SequenceEqual(video.Tags, StringComparer.OrdinalIgnoreCase) ||
                state.Score != video.PersonalScore)
                throw new InvalidOperationException("重复组的分类、标签或评分已变化，请重新检测后再预览。");
        }
        var before = ReadState(disk, keep.Id);
        var after = new DuplicateLocalState
        {
            PrimaryCategory = proposal.Categories.FirstOrDefault(),
            CategoryPaths = proposal.Categories.Length == 0 ? null : proposal.Categories,
            Tags = proposal.Tags.Length == 0 ? null : proposal.Tags,
            Rating = selectedScore == before.Score ? before.Rating : selectedScore is { } value
                ? new PersonalRatingEntry { Score = value, UpdatedAtUtc = DateTime.UtcNow } : null
        };
        var record = new DuplicateMetadataTransaction
        {
            KeepId = keep.Id, KeepPath = keep.Path, Before = before, After = after,
            Evidence = group.Videos.Select(video => new DuplicateFileEvidence(video.Id, video.Path,
                group.Size, group.Sha256, video.Categories, video.Tags, video.PersonalScore)).ToArray()
        };
        record.Sha256 = Digest(record);
        return record;
    }

    internal static async Task CommitAsync(string dataDir, DuplicateMetadataTransaction record,
        CancellationToken cancellationToken, Action? beforeWrite = null)
    {
        Validate(record);
        var fileLeases = await OpenVerifiedFilesAsync(record.Evidence, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeWrite?.Invoke();
            await Task.Run(() => CommitValidated(dataDir, record, validateSources: true), CancellationToken.None);
        }
        finally { foreach (var stream in fileLeases) await stream.DisposeAsync(); }
    }

    internal static IReadOnlyList<DuplicateMetadataTransaction> History(string dataDir)
    {
        using var journal = Enter(JournalGate);
        return ReadHistory(dataDir);
    }

    internal static void VerifyHistoryFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var entries = ReadHistoryFile(path);
            var seen = new Dictionary<string, DuplicateMetadataTransaction>(StringComparer.OrdinalIgnoreCase);
            var undone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!seen.TryAdd(entry.Id, entry))
                    throw new InvalidDataException("重复资料历史存在重复事务编号。");
                if (entry.Action != "undo") continue;
                if (!seen.TryGetValue(entry.UndoesId!, out var source) || source.Action != "merge" ||
                    !undone.Add(source.Id) || entry.KeepId != source.KeepId ||
                    entry.KeepPath != source.KeepPath ||
                    !Equal(entry.Before, source.After) || !Equal(entry.After, source.Before) ||
                    JsonSerializer.Serialize(entry.Evidence, JsonOptions) !=
                    JsonSerializer.Serialize(source.Evidence, JsonOptions))
                    throw new InvalidDataException("重复资料历史的撤销记录无法对应原合并记录。");
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or ArgumentException)
        {
            throw new InvalidDataException("重复资料历史记录损坏。", ex);
        }
    }

    // Operates only on a verified, offline relocation package. The old
    // history is validated first; each rewritten transaction is resealed so
    // cross-restart undo still has intact file and path evidence.
    internal static void RelocateHistoryFile(string path, Func<string, string> map)
    {
        if (!File.Exists(path)) return;
        VerifyHistoryFile(path);
        var entries = ReadHistoryFile(path);
        foreach (var entry in entries)
        {
            entry.KeepId = map(entry.KeepId);
            entry.KeepPath = map(entry.KeepPath);
            entry.Evidence = entry.Evidence.Select(file => file with
            {
                VideoId = map(file.VideoId), Path = map(file.Path)
            }).ToArray();
            entry.Sha256 = Digest(entry);
            Validate(entry);
        }
        if (entries.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidDataException("重复资料历史存在重复事务编号，迁移已停止。");
        DurableReplace(path, JsonSerializer.Serialize(entries, JsonOptions), backup: false);
        VerifyHistoryFile(path);
    }

    internal static DuplicateMetadataTransaction? LastUndoable(string dataDir)
    {
        var history = History(dataDir);
        var undone = history.Where(entry => entry.Action == "undo" && entry.UndoesId is not null)
            .Select(entry => entry.UndoesId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return history.LastOrDefault(entry => entry.Action == "merge" && !undone.Contains(entry.Id));
    }

    internal static async Task<DuplicateMetadataTransaction> UndoLastAsync(string dataDir,
        CancellationToken cancellationToken, Action? beforeWrite = null)
    {
        var prior = await Task.Run(() => LastUndoable(dataDir), cancellationToken)
            ?? throw new InvalidOperationException("没有可撤销的重复资料合并。 ");
        var fileLeases = await OpenVerifiedFilesAsync(prior.Evidence, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeWrite?.Invoke();
            var undo = new DuplicateMetadataTransaction
            {
                Action = "undo", UndoesId = prior.Id, KeepId = prior.KeepId, KeepPath = prior.KeepPath,
                Evidence = prior.Evidence, Before = prior.After, After = prior.Before
            };
            undo.Sha256 = Digest(undo);
            await Task.Run(() => CommitValidated(dataDir, undo, validateSources: false), CancellationToken.None);
            return undo;
        }
        finally { foreach (var stream in fileLeases) await stream.DisposeAsync(); }
    }

    static void CommitValidated(string dataDir, DuplicateMetadataTransaction record, bool validateSources,
        Action? afterConfigForDiagnostics = null)
    {
        Validate(record);
        using var config = Enter(ConfigGate);
        using var rating = Enter(RatingGate);
        using var journal = Enter(JournalGate);
        EnsureNoOfflineReplacementPending(dataDir);
        if (HasPending(dataDir)) throw new InvalidOperationException("已有待恢复的重复资料事务，已停止写入。");
        var history = ReadHistory(dataDir);
        if (history.Any(item => item.Id == record.Id))
            throw new InvalidOperationException("重复资料事务已提交，未重复写入。");
        if (record.Action == "undo")
        {
            var source = history.LastOrDefault(item => item.Id == record.UndoesId && item.Action == "merge");
            if (source is null || history.Any(item => item.Action == "undo" && item.UndoesId == source.Id))
                throw new InvalidOperationException("撤销记录已改变，请重新载入后再试。");
        }
        var disk = ReadDisk(dataDir);
        if (!Equal(ReadState(disk, record.KeepId), record.Before))
            throw new InvalidOperationException("保留项资料已被修改，未写入任何内容；请重新检测。");
        if (validateSources)
            foreach (var evidence in record.Evidence)
            {
                var state = ReadState(disk, evidence.VideoId);
                if (!state.VisibleCategories.SequenceEqual(evidence.Categories, StringComparer.OrdinalIgnoreCase) ||
                    !state.VisibleTags.SequenceEqual(evidence.Tags, StringComparer.OrdinalIgnoreCase) ||
                    state.Score != evidence.Score)
                    throw new InvalidOperationException("重复组资料在确认后变化，未写入任何内容。");
            }
        Directory.CreateDirectory(dataDir);
        DurableReplace(Path.Combine(dataDir, PendingName), JsonSerializer.Serialize(record, JsonOptions), backup: false);
        // Once the intent is durable, startup recovery finishes it after a
        // crash. Do not honor cancellation while the two files are changing.
        PatchAndWrite(dataDir, disk, record.KeepId, record.After, afterConfigForDiagnostics);
        AppendHistory(dataDir, record);
        File.Delete(Path.Combine(dataDir, PendingName));
    }

    internal static bool RecoverPendingAtStartup(string dataDir, out string error)
    {
        error = "";
        if (!HasPending(dataDir)) return true;
        try
        {
            using var config = Enter(ConfigGate);
            using var rating = Enter(RatingGate);
            using var journal = Enter(JournalGate);
            var record = JsonSerializer.Deserialize<DuplicateMetadataTransaction>(
                File.ReadAllText(Path.Combine(dataDir, PendingName)), JsonOptions)
                ?? throw new InvalidDataException("重复资料恢复记录为空。");
            Validate(record);
            // The media evidence was held open through the original commit, but
            // may have changed while this process was stopped. Keep the leases
            // until recovery completes so a Windows writer cannot replace it.
            using var evidence = OpenVerifiedFilesForRecovery(record.Evidence);
            var disk = ReadDisk(dataDir);
            var current = ReadState(disk, record.KeepId);
            if (!FieldsRecoverable(current, record.Before, record.After))
                throw new InvalidOperationException("资料已被其他程序修改，已保留待恢复记录，不会覆盖这些改动。");
            var recorded = ReadHistory(dataDir).FirstOrDefault(item => item.Id == record.Id);
            if (recorded is not null && !recorded.Sha256.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("重复资料历史存在同编号但不同内容，已停止恢复。");
            PatchAndWrite(dataDir, disk, record.KeepId, record.After);
            if (recorded is null) AppendHistory(dataDir, record);
            File.Delete(Path.Combine(dataDir, PendingName));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static string Preview(DuplicateMetadataTransaction record) =>
        $"保留文件：{record.KeepPath}\n校验值：{record.Evidence[0].Sha256}\n" +
        $"分类：{string.Join(" / ", record.Before.VisibleCategories)} → {string.Join(" / ", record.After.VisibleCategories)}\n" +
        $"标签：{string.Join("、", record.Before.VisibleTags)} → {string.Join("、", record.After.VisibleTags)}\n" +
        $"个人评分：{(record.Before.Score?.ToString("0.#") ?? "未评分")} → {(record.After.Score?.ToString("0.#") ?? "未评分")}\n" +
        "仅修改保留项的本地资料。其他资料与所有视频文件保持原样；可从本窗口撤销。";

    static async Task<List<FileStream>> OpenVerifiedFilesAsync(
        IEnumerable<DuplicateFileEvidence> evidence, CancellationToken token)
    {
        var leases = new List<FileStream>();
        try
        {
            foreach (var file in evidence)
            {
                token.ThrowIfCancellationRequested();
                var before = new FileInfo(file.Path);
                if (!before.Exists || before.Length != file.Size)
                    throw new InvalidOperationException("重复文件已移动或变化，请重新检测：" + file.Path);
                var changedAt = before.LastWriteTimeUtc;
                var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                leases.Add(stream);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                before.Refresh();
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !before.Exists || before.Length != file.Size || before.LastWriteTimeUtc != changedAt)
                    throw new InvalidOperationException("重复文件内容已变化，请重新检测：" + file.Path);
            }
            return leases;
        }
        catch
        {
            foreach (var stream in leases) await stream.DisposeAsync();
            throw;
        }
    }

    static FileLeases OpenVerifiedFilesForRecovery(IEnumerable<DuplicateFileEvidence> evidence)
    {
        var leases = new FileLeases();
        try
        {
            foreach (var file in evidence)
            {
                var before = new FileInfo(file.Path);
                if (!before.Exists || before.Length != file.Size)
                    throw new InvalidOperationException("重复视频已移动或变化，已保留待恢复记录：" + file.Path);
                var changedAt = before.LastWriteTimeUtc;
                var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                leases.Add(stream);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                before.Refresh();
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !before.Exists || before.Length != file.Size || before.LastWriteTimeUtc != changedAt)
                    throw new InvalidOperationException("重复视频内容已变化，已保留待恢复记录：" + file.Path);
            }
            return leases;
        }
        catch { leases.Dispose(); throw; }
    }

    sealed class FileLeases : IDisposable
    {
        readonly List<FileStream> streams = [];
        public void Add(FileStream stream) => streams.Add(stream);
        public void Dispose()
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }

    sealed record Disk(JsonObject Config, JsonObject Ratings);

    static Disk ReadDisk(string dataDir)
    {
        var configPath = Path.Combine(dataDir, "config.json");
        if (!File.Exists(configPath) || JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject config)
            throw new InvalidDataException("配置文件无法读取；没有写入合并资料。");
        var ratingPath = Path.Combine(dataDir, "personal-ratings.json");
        var ratings = File.Exists(ratingPath)
            ? JsonNode.Parse(File.ReadAllText(ratingPath)) as JsonObject
                ?? throw new InvalidDataException("个人评分文件无法读取；没有写入合并资料。")
            : new JsonObject();
        return new Disk(config, ratings);
    }

    static DuplicateLocalState ReadState(Disk disk, string id)
    {
        var category = ReadMapValue(disk.Config, "categories", id)?.GetValue<string>();
        var paths = ReadStringArray(ReadMapValue(disk.Config, "multiCategories", id));
        var tags = ReadStringArray(ReadMapValue(disk.Config, "tags", id));
        var ratingNode = Find(disk.Ratings, id);
        var rating = ratingNode?.Deserialize<PersonalRatingEntry>(JsonOptions);
        if (rating is not null && ratingNode is JsonObject ratingObject &&
            !ratingObject.ContainsKey(nameof(PersonalRatingEntry.UpdatedAtUtc)))
            rating.UpdatedAtUtc = DateTime.UnixEpoch;
        if (rating is not null && (!double.IsFinite(rating.Score) || rating.Score is < 0 or > 10))
            throw new InvalidDataException("个人评分记录无效，已停止合并。");
        return new DuplicateLocalState { PrimaryCategory = category, CategoryPaths = paths,
            Tags = tags, Rating = rating };
    }

    static string[]? ReadStringArray(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonArray array) throw new InvalidDataException("本地分类或标签格式无效。");
        return array.Select(item => item?.GetValue<string>() ?? throw new InvalidDataException("本地分类或标签格式无效。"))
            .ToArray();
    }

    static JsonNode? ReadMapValue(JsonObject root, string name, string id)
    {
        if (root[name] is null) return null;
        if (root[name] is not JsonObject map) throw new InvalidDataException("配置资料格式无效：" + name);
        return Find(map, id);
    }

    static JsonNode? Find(JsonObject map, string id) =>
        map.FirstOrDefault(pair => pair.Key.Equals(id, StringComparison.OrdinalIgnoreCase)).Value;

    static void PatchAndWrite(string dataDir, Disk disk, string id, DuplicateLocalState state,
        Action? afterConfigForDiagnostics = null)
    {
        var current = ReadState(disk, id);
        if (!EqualConfig(current, state))
        {
            var changed = (JsonObject)disk.Config.DeepClone();
            SetMapValue(changed, "categories", id, state.PrimaryCategory is null
                ? null : JsonValue.Create(state.PrimaryCategory));
            SetMapValue(changed, "multiCategories", id, state.CategoryPaths is null
                ? null : JsonSerializer.SerializeToNode(state.CategoryPaths));
            SetMapValue(changed, "tags", id, state.Tags is null
                ? null : JsonSerializer.SerializeToNode(state.Tags));
            DurableReplace(Path.Combine(dataDir, "config.json"), changed.ToJsonString());
        }
        afterConfigForDiagnostics?.Invoke();
        if (!EqualRating(current.Rating, state.Rating))
        {
            var changed = (JsonObject)disk.Ratings.DeepClone();
            SetValue(changed, id, state.Rating is null ? null : JsonSerializer.SerializeToNode(state.Rating));
            DurableReplace(Path.Combine(dataDir, "personal-ratings.json"), changed.ToJsonString());
        }
    }

    static void SetMapValue(JsonObject root, string name, string id, JsonNode? value)
    {
        if (root[name] is null) root[name] = new JsonObject();
        if (root[name] is not JsonObject map) throw new InvalidDataException("配置资料格式无效：" + name);
        SetValue(map, id, value);
    }

    static void SetValue(JsonObject map, string id, JsonNode? value)
    {
        var existing = map.FirstOrDefault(pair => pair.Key.Equals(id, StringComparison.OrdinalIgnoreCase)).Key;
        if (existing is not null) map.Remove(existing);
        if (value is not null) map[id] = value;
    }

    static void EnsureNoOfflineReplacementPending(string dataDir)
    {
        foreach (var name in new[]
        {
            "restore-pending.json", "restore-active.json", "import-pending.json", "import-active.json",
            "library-relocation-pending.json"
        })
            if (File.Exists(Path.Combine(dataDir, name)))
                throw new InvalidOperationException("资料恢复、导入或迁移已排定；请完成后再合并或撤销重复资料。");
    }

    static bool Equal(DuplicateLocalState a, DuplicateLocalState b) =>
        JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions);

    static bool EqualConfig(DuplicateLocalState a, DuplicateLocalState b) =>
        a.PrimaryCategory == b.PrimaryCategory &&
        EqualArray(a.CategoryPaths, b.CategoryPaths) && EqualArray(a.Tags, b.Tags);

    static bool EqualArray(string[]? a, string[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);

    static bool EqualRating(PersonalRatingEntry? a, PersonalRatingEntry? b) =>
        a is null ? b is null : b is not null && a.Score == b.Score && a.UpdatedAtUtc == b.UpdatedAtUtc;

    static bool FieldsRecoverable(DuplicateLocalState current, DuplicateLocalState before,
        DuplicateLocalState after) =>
        (EqualConfig(current, before) || EqualConfig(current, after)) &&
        (EqualRating(current.Rating, before.Rating) || EqualRating(current.Rating, after.Rating));

    static void Validate(DuplicateMetadataTransaction record)
    {
        if (record.Version != 1 || record.Id.Length != 32 || record.Action is not ("merge" or "undo") ||
            (record.Action == "undo") != (record.UndoesId is not null) ||
            string.IsNullOrWhiteSpace(record.KeepId) || string.IsNullOrWhiteSpace(record.KeepPath) ||
            record.Evidence.Length < 2 || record.Before is null || record.After is null ||
            !record.Evidence.Any(item => item.VideoId == record.KeepId && item.Path == record.KeepPath) ||
            record.Evidence.Any(item => string.IsNullOrWhiteSpace(item.Path) || item.Size <= 0 ||
                item.Sha256.Length != 64 || item.Size != record.Evidence[0].Size ||
                !item.Sha256.Equals(record.Evidence[0].Sha256, StringComparison.OrdinalIgnoreCase)) ||
            record.Evidence.Select(item => item.VideoId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != record.Evidence.Length ||
            !record.Sha256.Equals(Digest(record), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("重复资料事务证据或校验值无效。");
    }

    static string Digest(DuplicateMetadataTransaction record)
    {
        var old = record.Sha256;
        try
        {
            record.Sha256 = "";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, JsonOptions))));
        }
        finally { record.Sha256 = old; }
    }

    static List<DuplicateMetadataTransaction> ReadHistory(string dataDir)
        => ReadHistoryFile(Path.Combine(dataDir, HistoryName));

    static List<DuplicateMetadataTransaction> ReadHistoryFile(string path)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > MaxHistoryBytes)
            throw new InvalidDataException("重复资料历史文件超过安全读取上限。");
        var entries = JsonSerializer.Deserialize<List<DuplicateMetadataTransaction>>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("重复资料历史记录为空。");
        if (entries.Count > MaxHistoryEntries)
            throw new InvalidDataException("重复资料历史记录数量超过安全读取上限。");
        foreach (var item in entries) Validate(item);
        return entries;
    }

    static void AppendHistory(string dataDir, DuplicateMetadataTransaction record)
    {
        var path = Path.Combine(dataDir, HistoryName);
        var entries = ReadHistory(dataDir);
        if (entries.Any(item => item.Id == record.Id))
            throw new InvalidDataException("重复资料历史中已有相同事务编号。");
        entries.Add(record);
        DurableReplace(path, JsonSerializer.Serialize(entries, JsonOptions));
    }

    internal static void SimulateCrashAfterConfigForDiagnostics(string dataDir, DuplicateMetadataTransaction record)
    {
        try
        {
            CommitValidated(dataDir, record, validateSources: true,
                afterConfigForDiagnostics: () => throw new IOException("模拟配置已写入后的进程中断"));
            throw new InvalidOperationException("未触发诊断中断点。");
        }
        catch (IOException ex) when (ex.Message == "模拟配置已写入后的进程中断") { }
    }

    static void DurableReplace(string path, string content, bool backup = true)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 65536, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (backup && File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    static IDisposable Enter(Mutex mutex)
    {
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new TimeoutException("资料正在由另一程序写入，请稍后重试。");
            return new MutexLease(mutex);
        }
        catch { if (owned) mutex.ReleaseMutex(); throw; }
    }

    sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() => mutex.ReleaseMutex();
    }
}
