using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

internal static partial class ReviewTransactionStore
{
    const string PendingFileName = "review-transaction-pending.json";
    const string SessionLeaseFileName = ".library-session.lock";
    static readonly Mutex RecoveryMutex = new(false, "Local\\YinggeVideoLibrary.ReviewRecovery");
    static readonly Mutex RecoveryConfigMutex = new(false, "Local\\YinggeVideoLibrary.ConfigWrite");
    static readonly Mutex RecoveryMetadataMutex = new(false, "Local\\YinggeVideoLibrary.MetadataWrite");

    public static bool HasPending(string dataDir) => File.Exists(Path.Combine(dataDir, PendingFileName));

    // This is written and flushed before config.json or metadata.json changes.
    // A pending intent is kept until both files and the JSONL history agree.
    public static void Begin(string dataDir, ReviewTransaction transaction)
    {
        ValidatePending(transaction);
        Directory.CreateDirectory(dataDir);
        using var gate = Enter(RecoveryMutex);
        var path = Path.Combine(dataDir, PendingFileName);
        if (File.Exists(path)) throw new InvalidOperationException("上一次审核事务尚未完成，请重启软件恢复后再审核。");
        var disk = ReadDisk(dataDir);
        if (!Matches(disk, transaction.Before))
            throw new InvalidOperationException("审核预览已过期：磁盘中的审核、资料或标签已改变。");
        if (HistoryContains(dataDir, transaction))
            throw new InvalidOperationException("审核事务编号已存在于历史中，已停止重复提交。");
        DurableReplace(path, JsonSerializer.Serialize(transaction, Options), backup: false);
    }

    public static void Complete(string dataDir, ReviewTransaction transaction)
    {
        using var gate = Enter(RecoveryMutex);
        var pending = ReadPending(dataDir);
        if (pending.Id != transaction.Id || !string.Equals(pending.Sha256, transaction.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("待完成审核事务与当前操作不一致。");
        if (!Matches(ReadDisk(dataDir), transaction.After) || !HistoryContains(dataDir, transaction))
            throw new InvalidOperationException("审核数据或历史尚未完整落盘，保留恢复记录。");
        File.Delete(Path.Combine(dataDir, PendingFileName));
    }

    public static bool AbandonIfRestored(string dataDir, ReviewTransaction transaction)
    {
        using var gate = Enter(RecoveryMutex);
        var pending = ReadPending(dataDir);
        if (pending.Id != transaction.Id || HistoryContains(dataDir, transaction) ||
            !Matches(ReadDisk(dataDir), transaction.Before)) return false;
        File.Delete(Path.Combine(dataDir, PendingFileName));
        return true;
    }

    public static bool IsRecorded(string dataDir, ReviewTransaction transaction)
        => HistoryContains(dataDir, transaction);

    // Used for an ordinary write exception before history append. Only
    // target values that still equal the old or intended new snapshot may be
    // changed; an unrelated concurrent edit leaves the journal in place.
    public static void RollbackUncommitted(string dataDir, ReviewTransaction transaction)
    {
        using var configGate = Enter(RecoveryConfigMutex);
        using var metadataGate = Enter(RecoveryMetadataMutex);
        using var gate = Enter(RecoveryMutex);
        var pending = ReadPending(dataDir);
        if (pending.Id != transaction.Id || HistoryContains(dataDir, transaction))
            throw new InvalidOperationException("审核事务已经进入历史，不能作为失败操作回滚。");
        var disk = ReadDisk(dataDir);
        ValidateRecoverable(disk, transaction);
        if (!ConfigMatches(disk, transaction.Before))
        {
            PatchConfig(disk.Config, transaction.Before);
            DurableReplace(Path.Combine(dataDir, "config.json"), disk.Config.ToJsonString(), backup: true);
        }
        if (!MetadataMatches(disk, transaction.Before))
        {
            PatchMetadata(disk.Metadata, transaction.Before);
            DurableReplace(Path.Combine(dataDir, "metadata.json"), disk.Metadata.ToJsonString(), backup: true);
        }
        if (!Matches(ReadDisk(dataDir), transaction.Before))
            throw new InvalidDataException("审核回滚后的快照仍不一致，保留恢复记录。");
        File.Delete(Path.Combine(dataDir, PendingFileName));
    }

    // Called after a staged full restore, before selective import, offline
    // backup and MainWindow construction. An exclusive session lease prevents
    // recovery while a current application copy still has the library open.
    public static bool RecoverPendingAtStartup(string dataDir, out string error)
        => RecoverPendingAtStartupForDiagnostics(dataDir, out error, null);

    internal static bool RecoverPendingAtStartupForDiagnostics(string dataDir, out string error,
        Action<string>? afterStep)
    {
        error = "";
        if (!HasPending(dataDir)) return true;
        try
        {
            using var lease = new FileStream(Path.Combine(dataDir, SessionLeaseFileName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var configGate = Enter(RecoveryConfigMutex);
            using var metadataGate = Enter(RecoveryMetadataMutex);
            using var gate = Enter(RecoveryMutex);
            var transaction = ReadPending(dataDir);
            var disk = ReadDisk(dataDir);
            ValidateRecoverable(disk, transaction);
            // Detect a forged or conflicting commit marker before changing
            // either data file. Otherwise a duplicate Id with different
            // contents could leave partially replayed data and a pending
            // journal that will fail again on every subsequent startup.
            var alreadyRecorded = HistoryContains(dataDir, transaction);
            if (!ConfigMatches(disk, transaction.After))
            {
                PatchConfig(disk.Config, transaction.After);
                DurableReplace(Path.Combine(dataDir, "config.json"), disk.Config.ToJsonString(), backup: true);
                afterStep?.Invoke("config");
            }
            if (!MetadataMatches(disk, transaction.After))
            {
                PatchMetadata(disk.Metadata, transaction.After);
                DurableReplace(Path.Combine(dataDir, "metadata.json"), disk.Metadata.ToJsonString(), backup: true);
                afterStep?.Invoke("metadata");
            }
            if (!alreadyRecorded)
            {
                Append(dataDir, transaction);
                afterStep?.Invoke("history");
            }
            if (!Matches(ReadDisk(dataDir), transaction.After) || !HistoryContains(dataDir, transaction))
                throw new InvalidDataException("恢复后的审核数据与事务快照不一致。");
            File.Delete(Path.Combine(dataDir, PendingFileName));
            return true;
        }
        catch (Exception ex)
        {
            error = "待恢复的审核操作尚未完成；已停止加载，未覆盖有冲突的数据。" + ex.Message;
            return false;
        }
    }

    static void ValidatePending(ReviewTransaction transaction)
    {
        if (transaction.SchemaVersion != SchemaVersion || !transaction.Complete ||
            transaction.Before.Count == 0 ||
            !transaction.Before.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(transaction.After.Keys) ||
            transaction.Action is not ("field-approve" or "reject" or "undo" or
                "auto-approved" or "candidate" or "unmatched" or "ai-evaluate") ||
            (transaction.Action == "undo") != (transaction.UndoesTransactionId is not null) ||
            !string.Equals(transaction.Sha256, Digest(transaction), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("审核恢复记录缺少完整且已校验的事务快照。");
    }

    static ReviewTransaction ReadPending(string dataDir)
    {
        var path = Path.Combine(dataDir, PendingFileName);
        var transaction = JsonSerializer.Deserialize<ReviewTransaction>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("审核恢复记录为空。");
        ValidatePending(transaction);
        return transaction;
    }

    static bool HistoryContains(string dataDir, ReviewTransaction transaction)
    {
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) return false;
        var found = false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            // Most history rows are unrelated. Inspect only rows containing
            // this random transaction id; a torn row is not a commit marker.
            if (!line.Contains(transaction.Id, StringComparison.OrdinalIgnoreCase)) continue;
            ReviewTransaction? candidate;
            try { candidate = JsonSerializer.Deserialize<ReviewTransaction>(line, Options); }
            catch (JsonException) { continue; }
            if (candidate?.Id != transaction.Id) continue;
            if (!string.Equals(candidate.Sha256, Digest(candidate), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.Sha256, transaction.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("审核历史存在相同编号但内容不同的事务。");
            found = true;
        }
        return found;
    }

    sealed record DiskState(JsonObject Config, JsonObject Metadata);

    static DiskState ReadDisk(string dataDir)
        => new(ReadObject(Path.Combine(dataDir, "config.json")), ReadObject(Path.Combine(dataDir, "metadata.json")));

    static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("JSON 根节点不是对象。" + path); }
        catch (JsonException ex) { throw new InvalidDataException("无法读取审核所需的数据文件：" + path, ex); }
    }

    static void ValidateRecoverable(DiskState disk, ReviewTransaction transaction)
    {
        foreach (var (id, before) in transaction.Before)
        {
            var after = transaction.After[id];
            var live = ReadSnapshot(disk, id);
            if (!SameConfig(live, before) && !SameConfig(live, after))
                throw new InvalidOperationException("审核配置已被其他操作修改：" + id);
            if (!SameMetadata(live.Metadata, before.Metadata) && !SameMetadata(live.Metadata, after.Metadata))
                throw new InvalidOperationException("审核元数据已被其他操作修改：" + id);
        }
    }

    static bool Matches(DiskState disk, IReadOnlyDictionary<string, ReviewItemSnapshot> expected)
        => expected.All(pair => Equal(ReadSnapshot(disk, pair.Key), pair.Value));

    static bool ConfigMatches(DiskState disk, IReadOnlyDictionary<string, ReviewItemSnapshot> expected)
        => expected.All(pair => SameConfig(ReadSnapshot(disk, pair.Key), pair.Value));

    static bool MetadataMatches(DiskState disk, IReadOnlyDictionary<string, ReviewItemSnapshot> expected)
        => expected.All(pair => SameMetadata(ReadSnapshot(disk, pair.Key).Metadata, pair.Value.Metadata));

    static bool SameConfig(ReviewItemSnapshot left, ReviewItemSnapshot right)
        => Equal(new ReviewItemSnapshot { Review = left.Review, Tags = left.Tags, AutoAmateur = left.AutoAmateur },
            new ReviewItemSnapshot { Review = right.Review, Tags = right.Tags, AutoAmateur = right.AutoAmateur });

    static bool SameMetadata(MovieMetadata? left, MovieMetadata? right)
        => JsonSerializer.Serialize(left, Options) == JsonSerializer.Serialize(right, Options);

    static ReviewItemSnapshot ReadSnapshot(DiskState disk, string id)
    {
        var reviews = ReadMap(disk.Config, "aiReviews");
        var tags = ReadMap(disk.Config, "tags");
        var membership = ReadArray(disk.Config, "autoAmateurTags");
        return new ReviewItemSnapshot
        {
            Review = ReadValue<AiReviewRecord>(reviews, id),
            Metadata = ReadValue<MovieMetadata>(disk.Metadata, id),
            Tags = ReadValue<List<string>>(tags, id),
            AutoAmateur = membership.Any(item => string.Equals(item?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
        };
    }

    static T? ReadValue<T>(JsonObject map, string key) where T : class
    {
        var name = FindName(map, key);
        return name is null || map[name] is not { } node ? null : node.Deserialize<T>(Options);
    }

    static JsonObject ReadMap(JsonObject root, string key)
    {
        var name = FindName(root, key);
        if (name is null) { var created = new JsonObject(); root[key] = created; return created; }
        return root[name] as JsonObject ?? throw new InvalidDataException("审核配置的 " + key + " 不是对象。");
    }

    static JsonArray ReadArray(JsonObject root, string key)
    {
        var name = FindName(root, key);
        if (name is null) { var created = new JsonArray(); root[key] = created; return created; }
        return root[name] as JsonArray ?? throw new InvalidDataException("审核配置的 " + key + " 不是列表。");
    }

    static string? FindName(JsonObject map, string key)
        => map.ContainsKey(key) ? key :
            map.Select(pair => pair.Key).FirstOrDefault(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase));

    static void PatchConfig(JsonObject config, IReadOnlyDictionary<string, ReviewItemSnapshot> targets)
    {
        var reviews = ReadMap(config, "aiReviews");
        var tags = ReadMap(config, "tags");
        var membership = ReadArray(config, "autoAmateurTags");
        var affected = targets.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = membership.Count - 1; index >= 0; index--)
            if (membership[index] is JsonNode node && affected.Contains(node.GetValue<string>())) membership.RemoveAt(index);
        foreach (var (id, state) in targets)
        {
            ReplaceItem(reviews, id, state.Review);
            ReplaceItem(tags, id, state.Tags is { Count: > 0 } ? state.Tags : null);
            if (state.AutoAmateur) membership.Add(id);
        }
    }

    static void PatchMetadata(JsonObject metadata, IReadOnlyDictionary<string, ReviewItemSnapshot> targets)
    {
        foreach (var (id, state) in targets) ReplaceItem(metadata, id, state.Metadata);
    }

    static void ReplaceItem<T>(JsonObject map, string id, T? value)
    {
        var actual = FindName(map, id);
        if (actual is not null) map.Remove(actual);
        if (value is not null) map[id] = JsonSerializer.SerializeToNode(value, Options);
    }

    static void DurableReplace(string path, string content, bool backup)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path) && backup) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    static IDisposable Enter(Mutex mutex)
    {
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("审核文件正在被其他程序使用。"); }
        catch (AbandonedMutexException) { }
        return new MutexLease(mutex);
    }

    sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() => mutex.ReleaseMutex();
    }
}
