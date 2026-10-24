using System.IO;
using System.Text.Json;

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
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayQuery => Kind == "title" ? SearchTitle : Code;
    public int Attempts { get; set; }
    public bool Failed { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string LastError { get; set; } = "";
}

public static class ScrapeQueueStore
{
    static readonly Mutex WriteMutex = new(false, "Local\\YinggeVideoLibrary.ScrapeQueueWrite");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    static string QueuePath(string dataDir) => Path.Combine(dataDir, "scrape-queue.json");

    public static List<ScrapeQueueItem> Load(string dataDir)
    {
        var path = QueuePath(dataDir);
        if (!TryRead(path, out var items) && !TryRead(path + ".bak", out items)) return [];
        return items!;
    }

    static bool TryRead(string path, out List<ScrapeQueueItem>? items)
    {
        items = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return false;
                items = JsonSerializer.Deserialize<List<ScrapeQueueItem>>(File.ReadAllText(path));
                if (items is null) return false;
                items = items.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.VideoId)
                        && (x.Kind == "title" ? !string.IsNullOrWhiteSpace(x.SearchTitle)
                            : x.Kind == "code" && !string.IsNullOrWhiteSpace(x.Code)))
                    .GroupBy(x => x.VideoId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                return true;
            }
            catch (IOException) when (attempt < 2) { Thread.Sleep(30 * (attempt + 1)); }
            catch (JsonException) when (attempt < 2) { Thread.Sleep(30 * (attempt + 1)); }
            catch (UnauthorizedAccessException) when (attempt < 2) { Thread.Sleep(30 * (attempt + 1)); }
            catch { return false; }
        }
        return false;
    }

    static T Change<T>(string dataDir, Func<List<ScrapeQueueItem>, T> change)
    {
        Directory.CreateDirectory(dataDir);
        var path = QueuePath(dataDir);
        var temp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var ownsMutex = false;
        try
        {
            try { ownsMutex = WriteMutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) throw new IOException("搜刮队列正在被另一份程序保存，请稍后重试。");
            var healthyPrimary = TryRead(path, out var primary);
            var items = healthyPrimary ? primary! : Load(dataDir);
            var result = change(items);
            File.WriteAllText(temp, JsonSerializer.Serialize(items, JsonOptions));
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path) && healthyPrimary) File.Replace(temp, path, path + ".bak", true);
                    else File.Move(temp, path, true);
                    break;
                }
                catch (IOException) when (attempt < 2) { Thread.Sleep(30 * (attempt + 1)); }
            }
            return result;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            if (ownsMutex) WriteMutex.ReleaseMutex();
        }
    }

    public static int Add(string dataDir, IEnumerable<ScrapeQueueItem> candidates) => Change(dataDir, items =>
    {
        var known = items.Select(x => x.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var item in candidates)
            if (known.Add(item.VideoId)) { items.Add(item); added++; }
        return added;
    });

    public static void Remove(string dataDir, string videoId) => Change(dataDir, items =>
    {
        items.RemoveAll(x => string.Equals(x.VideoId, videoId, StringComparison.OrdinalIgnoreCase));
        return 0;
    });

    // Leave failed entries available for inspection and explicit retry. A
    // completed request has already been removed from this file.
    public static int ClearPending(string dataDir) => Change(dataDir, items => items.RemoveAll(x => !x.Failed));

    public static bool HasPending(string dataDir, string videoId) => Load(dataDir).Any(item =>
        !item.Failed && string.Equals(item.VideoId, videoId, StringComparison.OrdinalIgnoreCase));

    public static void Reschedule(string dataDir, ScrapeQueueItem retry) => Change(dataDir, items =>
    {
        var item = items.FirstOrDefault(x => string.Equals(x.VideoId, retry.VideoId, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            item.Attempts = retry.Attempts;
            item.NextAttemptAt = retry.NextAttemptAt;
            item.LastError = retry.LastError;
        }
        return 0;
    });

    public static void MarkFailed(string dataDir, string videoId, string error) => Change(dataDir, items =>
    {
        var item = items.FirstOrDefault(x => string.Equals(x.VideoId, videoId, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            item.Failed = true;
            item.LastError = error.Length > 240 ? error[..240] : error;
        }
        return 0;
    });

    public static int RetryFailures(string dataDir) => Change(dataDir, items =>
    {
        var count = 0;
        foreach (var item in items.Where(x => x.Failed))
        {
            item.Failed = false;
            item.Attempts = 0;
            item.NextAttemptAt = DateTimeOffset.UtcNow;
            ++count;
        }
        return count;
    });

    public static void DelayAllUntil(string dataDir, DateTimeOffset nextRequestAt) => Change(dataDir, items =>
    {
        foreach (var item in items)
            if (!item.Failed && item.NextAttemptAt < nextRequestAt) item.NextAttemptAt = nextRequestAt;
        return 0;
    });

    // Only one app copy may actively request metadata, while other copies can
    // still add work to the shared queue. The OS releases this lock on exit.
    public static FileStream? TryAcquireWorker(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        try { return new FileStream(Path.Combine(dataDir, "scrape-queue.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
}
