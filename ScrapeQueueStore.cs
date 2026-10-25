using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YinggeVideoLibrary;

// Pending requests contain the local video ID and a public code or cleaned title.
// The queue is shared by published copies running under the same Windows user.
public sealed class ScrapeQueueItem
{
    public string VideoId { get; set; } = "";
    // Older queue files do not contain Kind and therefore remain code tasks.
    public string Kind { get; set; } = "code";
    public string Code { get; set; } = "";
    public string SearchTitle { get; set; } = "";
    // Explicitly selected re-scrapes keep their intent after an app restart.
    // Old queue entries default to false and keep their original eligibility.
    public bool RefreshExisting { get; set; }
    [JsonIgnore]
    public string DisplayQuery => Kind == "title" ? SearchTitle : Code;
    public int Attempts { get; set; }
    public bool Failed { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string LastError { get; set; } = "";
}

public sealed class ScrapeSourceUnavailableException(string message, DateTimeOffset? retryAt = null) : Exception(message)
{
    public DateTimeOffset? RetryAt { get; } = retryAt;
}

public static class ScrapeQueueStore
{
    const long CheckpointAfterBytes = 32L * 1024 * 1024;
    const int MaxEventBytes = 256 * 1024 * 1024;
    const int DigestBytes = 32;
    static readonly Mutex WriteMutex = new(false, "Local\\YinggeVideoLibrary.ScrapeQueueWrite");
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };
    static readonly ConcurrentDictionary<string, QueueCache> Caches = new(StringComparer.OrdinalIgnoreCase);

    sealed class QueueEntry
    {
        public required ScrapeQueueItem Item { get; init; }
        public long Sequence { get; init; }
    }

    sealed class QueueEntryComparer : IComparer<QueueEntry>
    {
        public int Compare(QueueEntry? left, QueueEntry? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var byTime = left.Item.NextAttemptAt.CompareTo(right.Item.NextAttemptAt);
            return byTime != 0 ? byTime : left.Sequence.CompareTo(right.Sequence);
        }
    }

    sealed class QueueCache
    {
        public readonly object Gate = new();
        public readonly LinkedList<QueueEntry> Order = new();
        public readonly Dictionary<string, LinkedListNode<QueueEntry>> Index = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedSet<QueueEntry> Pending = new(new QueueEntryComparer());
        public readonly SortedSet<QueueEntry> PendingTitles = new(new QueueEntryComparer());
        public readonly SortedSet<QueueEntry> PendingCodes = new(new QueueEntryComparer());
        public readonly Dictionary<string, SourceBackoff> SourceBackoffs = new(StringComparer.OrdinalIgnoreCase);
        public string LastSelectedKind = "title";
        public int Revision;
        public QueueEntry? CachedReadyTitle;
        public string CachedReadyTitleSources = "";
        public int CachedReadyTitleRevision = -1;
        public long CachedReadyTitleCacheRevision = -1;
        public DateTimeOffset CachedReadyTitleCheckedUntil;
        public QueueEntry? CachedTitleScanAfter;
        public bool CachedTitleScanFinished;
        public HashSet<string>? CachedTitleKnownFiles;
        public int CachedTitleSnapshotCount;
        public int CachedTitleMaxBatchSize;
        public long NextSequence;
        public bool Initialized;
        public bool LegacyValid;
        public int Generation;
        public string JournalPath = "";
        public string? SnapshotPath;
        public long JournalOffset;
        public FileStamp PrimaryManifestStamp, BackupManifestStamp, PrimaryLegacyStamp, BackupLegacyStamp, SnapshotStamp, JournalStamp;
        public FileStamp SourceBackoffStamp, SourceBackoffBackupStamp;
    }

    readonly record struct FileStamp(bool Exists, long Length, long LastWriteTicks);
    sealed class QueueManifest { public int Generation { get; set; } }
    sealed class SourceBackoff
    {
        public DateTimeOffset Until { get; set; }
        public DateTimeOffset NextRequestAt { get; set; }
        public int Failures { get; set; }
        public string LastError { get; set; } = "";
    }
    sealed class QueueSnapshot { public int Generation { get; set; } public List<ScrapeQueueItem> Items { get; set; } = []; }
    sealed class SourceBackoffDocument
    {
        // Require both fields in a restored sidecar. Property initializers
        // would otherwise make malformed-but-parseable `{}` look valid.
        public int Version { get; set; }
        public Dictionary<string, SourceBackoff>? Sources { get; set; }
    }
    sealed class QueueEvent
    {
        public string Type { get; set; } = "";
        public List<ScrapeQueueItem>? Items { get; set; }
        public string VideoId { get; set; } = "";
        public int Attempts { get; set; }
        public DateTimeOffset At { get; set; }
        public string LastError { get; set; } = "";
    }

    static string QueuePath(string dataDir) => Path.Combine(dataDir, "scrape-queue.json");
    static string SourceBackoffPath(string dataDir) => Path.Combine(dataDir, "scrape-source-backoff.json");
    static string ManifestPath(string dataDir) => Path.Combine(dataDir, "scrape-queue.state.json");
    static string SnapshotPath(string dataDir, int generation) => Path.Combine(dataDir, $"scrape-queue.snapshot.{generation:D8}.json");
    static string JournalPath(string dataDir, int generation) => Path.Combine(dataDir, $"scrape-queue.journal.{generation:D8}.bin");
    static QueueCache Cache(string dataDir) => Caches.GetOrAdd(Path.GetFullPath(dataDir), _ => new QueueCache());

    static FileStamp Stamp(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists ? new FileStamp(true, file.Length, file.LastWriteTimeUtc.Ticks) : default;
    }

    static bool TryReadSourceBackoffs(string path, out Dictionary<string, SourceBackoff> sources)
    {
        sources = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).Length > 128 * 1024) return false;
            var document = JsonSerializer.Deserialize<SourceBackoffDocument>(File.ReadAllText(path));
            if (document?.Version != 1 || document.Sources is null || document.Sources.Count > 64)
                return false;
            foreach (var pair in document.Sources)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 80 || pair.Value is null
                    || pair.Value.Failures is < 0 or > 16 || pair.Value.LastError is null
                    || pair.Value.LastError.Length > 240 || pair.Value.Until > DateTimeOffset.UtcNow.AddDays(2)
                    || pair.Value.NextRequestAt > DateTimeOffset.UtcNow.AddDays(2)) return false;
                if (!sources.TryAdd(pair.Key, pair.Value)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    // Backup verification must validate the sidecar schema and its bounded
    // values, not merely check whether the file contains parseable JSON.
    internal static bool ValidateSourceBackoffSnapshot(string path) => TryReadSourceBackoffs(path, out _);

    static void LoadSourceBackoffs(QueueCache cache, string dataDir)
    {
        var path = SourceBackoffPath(dataDir);
        var backup = path + ".bak";
        cache.SourceBackoffs.Clear();
        if (TryReadSourceBackoffs(path, out var sources) || TryReadSourceBackoffs(backup, out sources))
            foreach (var pair in sources) cache.SourceBackoffs[pair.Key] = pair.Value;
        else if (File.Exists(path) || File.Exists(backup))
            throw new InvalidDataException("来源限速记录损坏，已停止自动搜刮以防重复请求。");
        cache.SourceBackoffStamp = Stamp(path);
        cache.SourceBackoffBackupStamp = Stamp(backup);
    }

    static void PersistSourceBackoffs(QueueCache cache, string dataDir)
    {
        var path = SourceBackoffPath(dataDir);
        var backup = path + ".bak";
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new SourceBackoffDocument
                {
                    Version = 1,
                    Sources = new Dictionary<string, SourceBackoff>(cache.SourceBackoffs, StringComparer.OrdinalIgnoreCase)
                }, JsonOptions);
                stream.Flush(true);
            }
            if (TryReadSourceBackoffs(path, out _)) File.Copy(path, backup, true);
            File.Move(temporary, path, true);
            cache.SourceBackoffStamp = Stamp(path);
            cache.SourceBackoffBackupStamp = Stamp(backup);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    static ScrapeQueueItem Copy(ScrapeQueueItem item) => new()
    {
        VideoId = item.VideoId, Kind = item.Kind, Code = item.Code, SearchTitle = item.SearchTitle,
        RefreshExisting = item.RefreshExisting,
        Attempts = item.Attempts, Failed = item.Failed, NextAttemptAt = item.NextAttemptAt, LastError = item.LastError
    };

    static bool Valid(ScrapeQueueItem? item) => item is not null && !string.IsNullOrWhiteSpace(item.VideoId)
        && (item.Kind == "title" ? !string.IsNullOrWhiteSpace(item.SearchTitle)
            : item.Kind == "code" && !string.IsNullOrWhiteSpace(item.Code));

    static void AddPending(QueueCache cache, QueueEntry entry)
    {
        if (entry.Item.Failed) return;
        cache.Pending.Add(entry);
        if (entry.Item.Kind == "title") cache.PendingTitles.Add(entry);
        else cache.PendingCodes.Add(entry);
    }

    static void RemovePending(QueueCache cache, QueueEntry entry)
    {
        cache.Pending.Remove(entry);
        if (entry.Item.Kind == "title") cache.PendingTitles.Remove(entry);
        else cache.PendingCodes.Remove(entry);
    }

    static void AddToCache(QueueCache cache, ScrapeQueueItem item)
    {
        if (!Valid(item) || cache.Index.ContainsKey(item.VideoId)) return;
        var entry = new QueueEntry { Item = Copy(item), Sequence = cache.NextSequence++ };
        cache.Index.Add(item.VideoId, cache.Order.AddLast(entry));
        AddPending(cache, entry);
    }

    static bool RemoveFromCache(QueueCache cache, string videoId)
    {
        if (!cache.Index.Remove(videoId, out var node)) return false;
        RemovePending(cache, node.Value);
        cache.Order.Remove(node);
        return true;
    }

    static bool TryReadLegacy(string path, out List<ScrapeQueueItem>? items)
    {
        items = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return false;
                items = JsonSerializer.Deserialize<List<ScrapeQueueItem>>(File.ReadAllText(path));
                return items is not null;
            }
            catch (IOException) when (attempt < 2) { Thread.Sleep(30 * (attempt + 1)); }
            catch (JsonException) when (attempt < 2) { Thread.Sleep(30 * (attempt + 1)); }
            catch (UnauthorizedAccessException) when (attempt < 2) { Thread.Sleep(30 * (attempt + 1)); }
            catch { return false; }
        }
        return false;
    }

    static bool TryReadSnapshot(string path, int generation, out List<ScrapeQueueItem>? items)
    {
        items = null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<QueueSnapshot>(File.ReadAllText(path));
            if (snapshot is null || snapshot.Generation != generation || snapshot.Items is null) return false;
            items = snapshot.Items;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    static bool TryReadManifestGeneration(string manifestPath, out int generation)
    {
        generation = 0;
        try
        {
            if (!File.Exists(manifestPath)) return false;
            var manifest = JsonSerializer.Deserialize<QueueManifest>(File.ReadAllText(manifestPath));
            if (manifest is null || manifest.Generation <= 0) return false;
            generation = manifest.Generation;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    static bool TryReadGeneration(string dataDir, string manifestPath, out int generation, out List<ScrapeQueueItem>? items)
    {
        items = null;
        if (!TryReadManifestGeneration(manifestPath, out generation)) return false;
        return File.Exists(JournalPath(dataDir, generation))
            && TryReadSnapshot(SnapshotPath(dataDir, generation), generation, out items);
    }

    static void Apply(QueueCache cache, QueueEvent change)
    {
        switch (change.Type)
        {
            case "add":
                foreach (var item in change.Items ?? []) AddToCache(cache, item);
                break;
            case "remove":
                RemoveFromCache(cache, change.VideoId);
                break;
            case "clearPending":
                foreach (var id in cache.Order.Where(entry => !entry.Item.Failed).Select(entry => entry.Item.VideoId).ToArray())
                    RemoveFromCache(cache, id);
                break;
            case "reschedule":
                if (cache.Index.TryGetValue(change.VideoId, out var retry))
                {
                    RemovePending(cache, retry.Value);
                    retry.Value.Item.Attempts = change.Attempts;
                    retry.Value.Item.NextAttemptAt = change.At;
                    retry.Value.Item.LastError = change.LastError;
                    AddPending(cache, retry.Value);
                }
                break;
            case "markFailed":
                if (cache.Index.TryGetValue(change.VideoId, out var failed))
                {
                    RemovePending(cache, failed.Value);
                    failed.Value.Item.Failed = true;
                    failed.Value.Item.LastError = change.LastError;
                }
                break;
            case "retryFailures":
                foreach (var entry in cache.Order.Where(entry => entry.Item.Failed))
                {
                    entry.Item.Failed = false;
                    entry.Item.Attempts = 0;
                    entry.Item.NextAttemptAt = change.At;
                    AddPending(cache, entry);
                }
                break;
            case "delayAll":
                foreach (var entry in cache.Order)
                    if (!entry.Item.Failed && entry.Item.NextAttemptAt < change.At)
                    {
                        RemovePending(cache, entry);
                        entry.Item.NextAttemptAt = change.At;
                        AddPending(cache, entry);
                    }
                break;
            default:
                throw new JsonException("未知的搜刮队列日志操作。");
        }
        cache.Revision++;
    }

    static bool KnownEvent(string type) => type is "add" or "remove" or "clearPending" or "reschedule" or "markFailed" or "retryFailures" or "delayAll";

    // Frames include length and SHA-256 digest. A torn final write is never replayed.
    static void Replay(QueueCache cache)
    {
        if (!File.Exists(cache.JournalPath)) return;
        using var stream = new FileStream(cache.JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = cache.JournalOffset;
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        while (stream.Length - stream.Position >= sizeof(int))
        {
            stream.ReadExactly(lengthBytes);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is <= 0 or > MaxEventBytes || stream.Length - stream.Position < (long)length + DigestBytes) break;
            var payload = new byte[length];
            stream.ReadExactly(payload);
            var digest = new byte[DigestBytes];
            stream.ReadExactly(digest);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), digest)) break;
            QueueEvent? change;
            try { change = JsonSerializer.Deserialize<QueueEvent>(payload); }
            catch (JsonException) { break; }
            if (change is null || !KnownEvent(change.Type)) break;
            Apply(cache, change);
            cache.JournalOffset = stream.Position;
        }
    }

    static void Reload(QueueCache cache, string dataDir)
    {
        cache.Order.Clear();
        cache.Index.Clear();
        cache.Pending.Clear();
        cache.PendingTitles.Clear();
        cache.PendingCodes.Clear();
        cache.SourceBackoffs.Clear();
        cache.LastSelectedKind = "title";
        cache.CachedReadyTitle = null;
        cache.CachedReadyTitleCheckedUntil = DateTimeOffset.MinValue;
        cache.CachedReadyTitleRevision = -1;
        cache.CachedTitleScanAfter = null;
        cache.CachedTitleKnownFiles = null;
        cache.CachedTitleScanFinished = false;
        cache.NextSequence = 0;
        cache.JournalOffset = 0;
        var primaryManifest = ManifestPath(dataDir);
        var backupManifest = primaryManifest + ".bak";
        var primaryValid = TryReadGeneration(dataDir, primaryManifest, out var primaryGeneration, out var primaryItems);
        var backupValid = TryReadGeneration(dataDir, backupManifest, out var backupGeneration, out var backupItems);
        List<ScrapeQueueItem>? items;
        if (primaryValid || backupValid)
        {
            cache.LegacyValid = false;
            cache.Generation = primaryValid && (!backupValid || primaryGeneration >= backupGeneration) ? primaryGeneration : backupGeneration;
            items = cache.Generation == primaryGeneration && primaryValid ? primaryItems : backupItems;
            cache.SnapshotPath = SnapshotPath(dataDir, cache.Generation);
        }
        else
        {
            cache.Generation = 0;
            cache.SnapshotPath = null;
            cache.LegacyValid = TryReadLegacy(QueuePath(dataDir), out items);
            if (!cache.LegacyValid) cache.LegacyValid = TryReadLegacy(QueuePath(dataDir) + ".bak", out items);
        }
        foreach (var item in items ?? []) if (Valid(item)) AddToCache(cache, item);
        LoadSourceBackoffs(cache, dataDir);
        cache.JournalPath = JournalPath(dataDir, cache.Generation);
        Replay(cache);
        cache.PrimaryManifestStamp = Stamp(primaryManifest);
        cache.BackupManifestStamp = Stamp(backupManifest);
        cache.PrimaryLegacyStamp = Stamp(QueuePath(dataDir));
        cache.BackupLegacyStamp = Stamp(QueuePath(dataDir) + ".bak");
        cache.SnapshotStamp = cache.SnapshotPath is null ? default : Stamp(cache.SnapshotPath);
        cache.JournalStamp = Stamp(cache.JournalPath);
        cache.Initialized = true;
    }

    static void Sync(QueueCache cache, string dataDir)
    {
        if (!cache.Initialized || cache.PrimaryManifestStamp != Stamp(ManifestPath(dataDir))
            || cache.BackupManifestStamp != Stamp(ManifestPath(dataDir) + ".bak")
            || (cache.Generation == 0 && (cache.PrimaryLegacyStamp != Stamp(QueuePath(dataDir))
                || cache.BackupLegacyStamp != Stamp(QueuePath(dataDir) + ".bak")))
            || (cache.Generation > 0 && cache.SnapshotStamp != Stamp(cache.SnapshotPath!)))
        {
            Reload(cache, dataDir);
            return;
        }
        var journalStamp = Stamp(cache.JournalPath);
        if (journalStamp.Length < cache.JournalOffset
            || (journalStamp.Length == cache.JournalOffset && journalStamp != cache.JournalStamp))
        {
            Reload(cache, dataDir);
            return;
        }
        if (journalStamp.Length > cache.JournalOffset) Replay(cache);
        cache.JournalStamp = Stamp(cache.JournalPath);
        if (cache.SourceBackoffStamp != Stamp(SourceBackoffPath(dataDir))
            || cache.SourceBackoffBackupStamp != Stamp(SourceBackoffPath(dataDir) + ".bak"))
            LoadSourceBackoffs(cache, dataDir);
    }

    static void WriteManifest(string target, int generation)
    {
        var temp = target + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new QueueManifest { Generation = generation }, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, target, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    static void RepairManifest(QueueCache cache, string dataDir)
    {
        if (cache.Generation == 0) return;
        var primary = ManifestPath(dataDir);
        if (TryReadManifestGeneration(primary, out var generation) && generation == cache.Generation) return;
        WriteManifest(primary, cache.Generation);
        cache.PrimaryManifestStamp = Stamp(primary);
    }

    static void Checkpoint(QueueCache cache, string dataDir)
    {
        var previousGeneration = cache.Generation;
        var generation = Math.Max(1, cache.Generation + 1);
        while (File.Exists(SnapshotPath(dataDir, generation)) || File.Exists(JournalPath(dataDir, generation))) generation++;
        var snapshotPath = SnapshotPath(dataDir, generation);
        var journalPath = JournalPath(dataDir, generation);
        var temp = snapshotPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new QueueSnapshot
                {
                    Generation = generation, Items = cache.Order.Select(entry => Copy(entry.Item)).ToList()
                }, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, snapshotPath);
            using (var journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) journal.Flush(true);
            // Publish the backup pointer first. Either pointer can recover the complete checkpoint.
            WriteManifest(ManifestPath(dataDir) + ".bak", generation);
            WriteManifest(ManifestPath(dataDir), generation);
            cache.Generation = generation;
            cache.SnapshotPath = snapshotPath;
            cache.JournalPath = journalPath;
            cache.JournalOffset = 0;
            cache.PrimaryManifestStamp = Stamp(ManifestPath(dataDir));
            cache.BackupManifestStamp = Stamp(ManifestPath(dataDir) + ".bak");
            cache.SnapshotStamp = Stamp(snapshotPath);
            cache.JournalStamp = Stamp(journalPath);
            PruneOldGenerations(dataDir, generation, previousGeneration);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    static void PruneOldGenerations(string dataDir, int currentGeneration, int previousGeneration)
    {
        // Keep the active snapshot, one prior generation for recovery, and the
        // legacy journal. Only exact generated file names in this directory qualify.
        foreach (var (pattern, prefix, suffix) in new[]
        {
            ("scrape-queue.snapshot.*.json", "scrape-queue.snapshot.", ".json"),
            ("scrape-queue.journal.*.bin", "scrape-queue.journal.", ".bin")
        })
        {
            foreach (var path in Directory.EnumerateFiles(dataDir, pattern, SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var number = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
                if (!int.TryParse(number, out var generation) || generation <= 0 || generation >= currentGeneration
                    || generation == previousGeneration) continue;
                try { File.Delete(path); } catch { /* Old recovery copies may be in use by another reader. */ }
            }
        }
    }

    static void Persist(QueueCache cache, string dataDir, QueueEvent change)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(change, JsonOptions);
        if (payload.Length is <= 0 or > MaxEventBytes) throw new IOException("搜刮队列批次过大，请分批入队。");
        var digest = SHA256.HashData(payload);
        using (var stream = new FileStream(cache.JournalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            // A previous process may have exited after writing an incomplete final frame.
            if (stream.Length != cache.JournalOffset) stream.SetLength(cache.JournalOffset);
            stream.Position = cache.JournalOffset;
            Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, payload.Length);
            stream.Write(lengthBytes);
            stream.Write(payload);
            stream.Write(digest);
            stream.Flush(true);
            cache.JournalOffset = stream.Position;
        }
        Apply(cache, change);
        cache.JournalStamp = Stamp(cache.JournalPath);
        if (cache.JournalOffset >= CheckpointAfterBytes)
            try { Checkpoint(cache, dataDir); }
            catch { /* The flushed journal still owns the change; retry checkpoint later. */ }
    }

    static T WithWriteLock<T>(string dataDir, Func<QueueCache, T> action)
    {
        Directory.CreateDirectory(dataDir);
        var ownsMutex = false;
        try
        {
            try { ownsMutex = WriteMutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) throw new IOException("搜刮队列正在被另一份程序保存，请稍后重试。");
            var cache = Cache(dataDir);
            lock (cache.Gate)
            {
                Sync(cache, dataDir);
                RepairManifest(cache, dataDir);
                // Freeze a valid old JSON (or its backup) once before recording changes.
                // Subsequent damage to that mutable legacy file cannot then lose migrated work.
                if (cache.Generation == 0 && cache.LegacyValid) Checkpoint(cache, dataDir);
                return action(cache);
            }
        }
        finally { if (ownsMutex) WriteMutex.ReleaseMutex(); }
    }

    public static List<ScrapeQueueItem> Load(string dataDir)
    {
        var cache = Cache(dataDir);
        lock (cache.Gate)
        {
            Sync(cache, dataDir);
            return cache.Order.Select(entry => Copy(entry.Item)).ToList();
        }
    }

    public static (ScrapeQueueItem? Item, int PendingCount, bool HasBlocked) PeekNext(string dataDir, bool hasEnabledCodeSource)
    {
        var cache = Cache(dataDir);
        lock (cache.Gate)
        {
            Sync(cache, dataDir);
            var next = hasEnabledCodeSource ? cache.Pending.Min : cache.PendingTitles.Min;
            return (next is null ? null : Copy(next.Item), cache.Pending.Count, cache.Pending.Count > 0 && next is null);
        }
    }

    static DateTimeOffset? EarliestSourceAt(QueueCache cache, IReadOnlyCollection<string> sources, DateTimeOffset now)
    {
        if (sources.Count == 0) return null;
        var earliest = DateTimeOffset.MaxValue;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            var ready = cache.SourceBackoffs.TryGetValue(source, out var backoff)
                ? Max(now, backoff.Until, backoff.NextRequestAt) : now;
            if (ready < earliest) earliest = ready;
        }
        return earliest == DateTimeOffset.MaxValue ? null : earliest;
    }

    static DateTimeOffset? EffectiveAt(QueueCache cache, QueueEntry? entry, IReadOnlyCollection<string> sources, DateTimeOffset now)
    {
        if (entry is null || EarliestSourceAt(cache, sources, now) is not { } sourceAt) return null;
        return entry.Item.NextAttemptAt > sourceAt ? entry.Item.NextAttemptAt : sourceAt;
    }

    static QueueEntry? FindCachedReadyTitle(QueueCache cache, string dataDir,
        IReadOnlyCollection<string> sources, DateTimeOffset now)
    {
        var sourceKey = string.Join('\u001f', sources.OrderBy(source => source, StringComparer.OrdinalIgnoreCase));
        var cacheRevision = ScrapeCandidateCache.CacheRevision;
        var sameScan = cache.CachedReadyTitleRevision == cache.Revision &&
            cache.CachedReadyTitleCacheRevision == cacheRevision &&
            string.Equals(cache.CachedReadyTitleSources, sourceKey, StringComparison.Ordinal);
        if (sameScan && cache.CachedReadyTitleCheckedUntil > now &&
            (cache.CachedReadyTitle is not null || cache.CachedTitleScanFinished))
            return cache.CachedReadyTitle;
        if (!sameScan || cache.CachedReadyTitle is not null || cache.CachedTitleScanFinished)
        {
            cache.CachedReadyTitle = null;
            cache.CachedTitleScanAfter = null;
            cache.CachedTitleScanFinished = false;
            cache.CachedTitleKnownFiles = ScrapeCandidateCache.SnapshotCacheFiles(dataDir);
            cache.CachedTitleSnapshotCount++;
            cache.CachedReadyTitleSources = sourceKey;
            cache.CachedReadyTitleRevision = cache.Revision;
            cache.CachedReadyTitleCacheRevision = cacheRevision;
        }

        // A large queue is scanned progressively. One poll checks at most
        // 1,024 titles; the cache directory is enumerated once per scan, not
        // every five seconds while a source is cooling.
        const int scanBatchSize = 1024;
        var batch = new List<QueueEntry>(scanBatchSize);
        var last = cache.CachedTitleScanAfter;
        var through = cache.PendingTitles.Max;
        var remaining = last is null || through is null
            ? cache.PendingTitles.AsEnumerable()
            : cache.PendingTitles.GetViewBetween(last, through);
        var finished = true;
        foreach (var entry in remaining)
        {
            if (ReferenceEquals(entry, last)) continue;
            if (entry.Item.NextAttemptAt > now) break;
            if (batch.Count == scanBatchSize) { finished = false; break; }
            batch.Add(entry);
        }
        if (batch.Count > 0) cache.CachedTitleScanAfter = batch[^1];
        if (batch.Count > cache.CachedTitleMaxBatchSize) cache.CachedTitleMaxBatchSize = batch.Count;
        var readyId = ScrapeCandidateCache.FirstReadyQueuedTitle(dataDir,
            batch.Select(entry => entry.Item), sources, cache.CachedTitleKnownFiles!);
        var ready = readyId is not null && cache.Index.TryGetValue(readyId, out var node)
            ? node.Value : null;
        cache.CachedReadyTitle = ready;
        cache.CachedTitleScanFinished = finished || ready is not null;
        // Same-process cache writes invalidate this scan immediately. A
        // second app's disk writes are found on the next full scan.
        cache.CachedReadyTitleCheckedUntil = ready is not null ? now.AddSeconds(30)
            : finished ? now.AddMinutes(1) : DateTimeOffset.MinValue;
        return ready;
    }

    internal static (int Snapshots, int MaxBatchSize) CachedTitleScanDiagnostics(string dataDir)
    {
        var cache = Cache(dataDir);
        lock (cache.Gate) return (cache.CachedTitleSnapshotCount, cache.CachedTitleMaxBatchSize);
    }

    // Each route is sorted independently. A cooling source cannot hold up a
    // different route, and ready code/title jobs alternate even if a bulk add
    // placed all code jobs before all title jobs in the durable journal.
    public static (ScrapeQueueItem? Item, int PendingCount, bool HasBlocked) PeekNextFair(
        string dataDir, IReadOnlyCollection<string> codeSources, IReadOnlyCollection<string> titleSources,
        bool includeCachedTitle = true)
    {
        var cache = Cache(dataDir);
        lock (cache.Gate)
        {
            Sync(cache, dataDir);
            var now = DateTimeOffset.UtcNow;
            var code = cache.PendingCodes.Min;
            var title = cache.PendingTitles.Min;
            var codeAt = EffectiveAt(cache, code, codeSources, now);
            var titleAt = EffectiveAt(cache, title, titleSources, now);
            if (includeCachedTitle && titleAt > now &&
                EarliestSourceAt(cache, titleSources, now) > now &&
                FindCachedReadyTitle(cache, dataDir, titleSources, now) is { } cachedTitle)
            {
                title = cachedTitle;
                titleAt = cachedTitle.Item.NextAttemptAt;
            }
            var chooseCode = codeAt is not null && (titleAt is null ||
                (codeAt <= now && titleAt <= now ? cache.LastSelectedKind != "code"
                    : codeAt < titleAt || codeAt == titleAt && cache.LastSelectedKind != "code"));
            var selected = chooseCode ? code : titleAt is not null ? title : null;
            var selectedAt = chooseCode ? codeAt : titleAt;
            if (selected is null || selectedAt is null)
                return (null, cache.Pending.Count, cache.Pending.Count > 0);
            var copy = Copy(selected.Item);
            copy.NextAttemptAt = selectedAt.Value;
            if (selectedAt <= now) cache.LastSelectedKind = copy.Kind;
            return (copy, cache.Pending.Count, false);
        }
    }

    public static DateTimeOffset GetSourceReadyAt(string dataDir, string source)
    {
        var cache = Cache(dataDir);
        lock (cache.Gate)
        {
            Sync(cache, dataDir);
            return cache.SourceBackoffs.TryGetValue(source, out var backoff)
                ? Max(backoff.Until, backoff.NextRequestAt) : DateTimeOffset.MinValue;
        }
    }

    public static DateTimeOffset GetSourceCooldownAt(string dataDir, string source)
    {
        var cache = Cache(dataDir);
        lock (cache.Gate)
        {
            Sync(cache, dataDir);
            return cache.SourceBackoffs.TryGetValue(source, out var backoff)
                ? backoff.Until : DateTimeOffset.MinValue;
        }
    }

    static DateTimeOffset Max(params DateTimeOffset[] values) => values.Max();

    // Reserve the next request slot before opening the network connection.
    // This remains safe if a second process starts during an update/restart.
    public static DateTimeOffset ReserveSourceRequest(string dataDir, string source, TimeSpan minimumSpacing)
        => WithWriteLock(dataDir, cache =>
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("搜刮来源不能为空。", nameof(source));
            if (minimumSpacing < TimeSpan.FromSeconds(1) || minimumSpacing > TimeSpan.FromSeconds(60))
                throw new ArgumentOutOfRangeException(nameof(minimumSpacing));
            var now = DateTimeOffset.UtcNow;
            var previous = cache.SourceBackoffs.TryGetValue(source, out var current) ? current : null;
            var ready = previous is null ? now : Max(now, previous.Until, previous.NextRequestAt);
            if (ready > now) return ready;
            cache.SourceBackoffs[source] = new SourceBackoff
            {
                Until = previous?.Until ?? DateTimeOffset.MinValue,
                NextRequestAt = now.Add(minimumSpacing),
                Failures = previous?.Failures ?? 0,
                LastError = previous?.LastError ?? ""
            };
            try { PersistSourceBackoffs(cache, dataDir); }
            catch
            {
                if (previous is null) cache.SourceBackoffs.Remove(source);
                else cache.SourceBackoffs[source] = previous;
                throw;
            }
            return DateTimeOffset.MinValue;
        });

    public static DateTimeOffset RecordSourceFailure(string dataDir, string source, string error,
        bool rateLimited, TimeSpan? retryAfter = null, int consecutiveFailureLimit = 4) => WithWriteLock(dataDir, cache =>
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("搜刮来源不能为空。", nameof(source));
        var previous = cache.SourceBackoffs.TryGetValue(source, out var current) ? current.Failures : 0;
        var failures = Math.Min(previous + 1, 16);
        var seconds = Math.Min(1200, (rateLimited ? 30 : 10) * Math.Pow(2, Math.Min(failures - 1, 7)));
        var delay = TimeSpan.FromSeconds(seconds);
        if (failures >= Math.Clamp(consecutiveFailureLimit, 1, 10) && delay < TimeSpan.FromHours(1))
            delay = TimeSpan.FromHours(1);
        if (retryAfter is { } header && header > delay)
            delay = header > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : header;
        var until = DateTimeOffset.UtcNow.Add(delay);
        if (current is not null && current.Until > until) until = current.Until;
        cache.SourceBackoffs[source] = new SourceBackoff
        {
            Until = until, NextRequestAt = current?.NextRequestAt ?? DateTimeOffset.MinValue,
            Failures = failures, LastError = error.Length > 240 ? error[..240] : error
        };
        try { PersistSourceBackoffs(cache, dataDir); }
        catch
        {
            if (current is null) cache.SourceBackoffs.Remove(source);
            else cache.SourceBackoffs[source] = current;
            throw;
        }
        return until;
    });

    public static void ReportSourceSuccess(string dataDir, string source) => WithWriteLock(dataDir, cache =>
    {
        if (cache.SourceBackoffs.TryGetValue(source, out var previous))
        {
            if (previous.NextRequestAt > DateTimeOffset.UtcNow)
                cache.SourceBackoffs[source] = new SourceBackoff { NextRequestAt = previous.NextRequestAt };
            else cache.SourceBackoffs.Remove(source);
            try { PersistSourceBackoffs(cache, dataDir); }
            catch { cache.SourceBackoffs[source] = previous; throw; }
        }
        return 0;
    });

    public static (int PendingCount, int FailedCount, DateTimeOffset? NextAttemptAt, List<(string Query, string Error)> FailedExamples)
        GetStatus(string dataDir, IReadOnlyCollection<string> codeSources, IReadOnlyCollection<string> titleSources,
            bool includeCachedTitle = true)
    {
        var cache = Cache(dataDir);
        lock (cache.Gate)
        {
            Sync(cache, dataDir);
            var now = DateTimeOffset.UtcNow;
            var codeAt = EffectiveAt(cache, cache.PendingCodes.Min, codeSources, now);
            var titleAt = EffectiveAt(cache, cache.PendingTitles.Min, titleSources, now);
            if (includeCachedTitle && titleAt > now &&
                EarliestSourceAt(cache, titleSources, now) > now &&
                FindCachedReadyTitle(cache, dataDir, titleSources, now) is { } cachedTitle)
                titleAt = cachedTitle.Item.NextAttemptAt;
            var next = codeAt is null ? titleAt : titleAt is null || codeAt < titleAt ? codeAt : titleAt;
            var failedCount = cache.Index.Count - cache.Pending.Count;
            var examples = failedCount == 0 ? new List<(string Query, string Error)>()
                : cache.Order.Where(entry => entry.Item.Failed).Take(5)
                    .Select(entry => (entry.Item.DisplayQuery, entry.Item.LastError)).ToList();
            return (cache.Pending.Count, failedCount, next, examples);
        }
    }

    public static (int PendingCount, int FailedCount, DateTimeOffset? NextAttemptAt, List<(string Query, string Error)> FailedExamples) GetStatus(string dataDir)
    {
        var cache = Cache(dataDir);
        lock (cache.Gate)
        {
            Sync(cache, dataDir);
            var failedCount = cache.Index.Count - cache.Pending.Count;
            var examples = failedCount == 0 ? new List<(string Query, string Error)>()
                : cache.Order.Where(entry => entry.Item.Failed).Take(5)
                    .Select(entry => (entry.Item.DisplayQuery, entry.Item.LastError)).ToList();
            return (cache.Pending.Count, failedCount, cache.Pending.Min?.Item.NextAttemptAt, examples);
        }
    }

    public static int Add(string dataDir, IEnumerable<ScrapeQueueItem> candidates) => WithWriteLock(dataDir, cache =>
    {
        var known = cache.Index.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = candidates.Where(Valid).Where(item => known.Add(item.VideoId)).Select(Copy).ToList();
        if (added.Count > 0) Persist(cache, dataDir, new QueueEvent { Type = "add", Items = added });
        return added.Count;
    });

    public static void Remove(string dataDir, string videoId) => WithWriteLock(dataDir, cache =>
    {
        if (cache.Index.ContainsKey(videoId)) Persist(cache, dataDir, new QueueEvent { Type = "remove", VideoId = videoId });
        return 0;
    });

    // Leave failed entries available for inspection and explicit retry.
    public static int ClearPending(string dataDir) => WithWriteLock(dataDir, cache =>
    {
        var count = cache.Pending.Count;
        if (count > 0) Persist(cache, dataDir, new QueueEvent { Type = "clearPending" });
        return count;
    });

    public static bool HasPending(string dataDir, string videoId)
    {
        var cache = Cache(dataDir);
        lock (cache.Gate)
        {
            Sync(cache, dataDir);
            return cache.Index.TryGetValue(videoId, out var node) && !node.Value.Item.Failed;
        }
    }

    public static void Reschedule(string dataDir, ScrapeQueueItem retry) => WithWriteLock(dataDir, cache =>
    {
        if (cache.Index.ContainsKey(retry.VideoId)) Persist(cache, dataDir, new QueueEvent
        {
            Type = "reschedule", VideoId = retry.VideoId, Attempts = retry.Attempts,
            At = retry.NextAttemptAt, LastError = retry.LastError
        });
        return 0;
    });

    public static void MarkFailed(string dataDir, string videoId, string error) => WithWriteLock(dataDir, cache =>
    {
        if (cache.Index.ContainsKey(videoId)) Persist(cache, dataDir, new QueueEvent
        { Type = "markFailed", VideoId = videoId, LastError = error.Length > 240 ? error[..240] : error });
        return 0;
    });

    public static int RetryFailures(string dataDir) => WithWriteLock(dataDir, cache =>
    {
        var count = cache.Order.Count(entry => entry.Item.Failed);
        if (count > 0) Persist(cache, dataDir, new QueueEvent { Type = "retryFailures", At = DateTimeOffset.UtcNow });
        return count;
    });

    public static void DelayAllUntil(string dataDir, DateTimeOffset nextRequestAt) => WithWriteLock(dataDir, cache =>
    {
        if (cache.Pending.Min is not null && cache.Pending.Min.Item.NextAttemptAt < nextRequestAt)
            Persist(cache, dataDir, new QueueEvent { Type = "delayAll", At = nextRequestAt });
        return 0;
    });

    // Test-only entry point: exercise generation rotation without creating a 32 MB log.
    internal static void CheckpointForDiagnostics(string dataDir) => WithWriteLock(dataDir, cache =>
    {
        Checkpoint(cache, dataDir);
        return 0;
    });

    // Only one app copy may actively request metadata, while other copies can still add work.
    public static FileStream? TryAcquireWorker(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        try { return new FileStream(Path.Combine(dataDir, "scrape-queue.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    // Restore runs before any library window is created. The on-disk checkpoint
    // family has been replaced with a verified portable queue snapshot.
    internal static void ForgetCachedStateAfterOfflineRestore(string dataDir) =>
        Caches.TryRemove(Path.GetFullPath(dataDir), out _);
}
