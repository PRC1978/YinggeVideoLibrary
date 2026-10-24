using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

public sealed class PersonalRatingEntry
{
    public double Score { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

// Personal ratings belong to the video, not to a scrape candidate. Keeping
// them in a separate shared file prevents a rescrape or review rejection from
// changing the user's own score.
public static class PersonalRatingStore
{
    static readonly Mutex WriteMutex = new(false, @"Local\YinggeVideoLibrary.PersonalRatingWrite");
    static string FilePath(string dataDir) => Path.Combine(dataDir, "personal-ratings.json");

    public static double? Get(string dataDir, string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return null;
        var entries = Load(dataDir);
        return entries.TryGetValue(videoId, out var entry) && IsValid(entry.Score) ? entry.Score : null;
    }

    public static Dictionary<string, PersonalRatingEntry> Load(string dataDir)
    {
        var path = FilePath(dataDir);
        if (TryLoad(path, out var entries)) return entries;
        if (TryLoad(path + ".bak", out entries)) return entries;
        return [];
    }

    static bool TryLoad(string path, out Dictionary<string, PersonalRatingEntry> entries)
    {
        entries = [];
        try
        {
            if (!File.Exists(path)) return false;
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, PersonalRatingEntry>>(File.ReadAllText(path));
            if (deserialized is null) return false;
            entries = new Dictionary<string, PersonalRatingEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, entry) in deserialized)
                if (!string.IsNullOrWhiteSpace(id) && entry is not null && IsValid(entry.Score)) entries[id] = entry;
            return true;
        }
        catch { return false; }
    }

    public static void Set(string dataDir, string videoId, double? score)
    {
        if (string.IsNullOrWhiteSpace(videoId)) throw new ArgumentException("视频 ID 不能为空。", nameof(videoId));
        if (score is double value && !IsValid(value)) throw new ArgumentOutOfRangeException(nameof(score), "个人评分必须在 0 到 10 之间。");
        Directory.CreateDirectory(dataDir);
        var path = FilePath(dataDir);
        var backup = path + ".bak";
        var temporary = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.{Guid.NewGuid():N}.tmp";
        var ownsMutex = false;
        try
        {
            try { WriteMutex.WaitOne(); ownsMutex = true; }
            catch (AbandonedMutexException) { ownsMutex = true; }
            var exists = File.Exists(path) || File.Exists(backup);
            var loaded = TryLoad(path, out var entries) || TryLoad(backup, out entries);
            if (exists && !loaded) throw new InvalidDataException("个人评分文件无法读取，已保留原文件，请检查数据目录。");
            if (!loaded) entries = [];
            if (score is double selected) entries[videoId] = new PersonalRatingEntry { Score = selected, UpdatedAtUtc = DateTime.UtcNow };
            else entries.Remove(videoId);
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path) && TryLoad(path, out _)) File.Copy(path, backup, true);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            if (ownsMutex) try { WriteMutex.ReleaseMutex(); } catch { }
        }
    }

    static bool IsValid(double score) => double.IsFinite(score) && score >= 0 && score <= 10;

    internal static bool VerifyForDiagnostics(string parentDirectory)
    {
        var directory = Path.Combine(parentDirectory, "personal-ratings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Set(directory, "a", 8.5);
            Set(directory, "b", 0);
            if (Get(directory, "a") != 8.5 || Get(directory, "b") != 0) return false;
            Set(directory, "a", 9);
            if (Get(directory, "b") != 0 || Get(directory, "a") != 9) return false;
            Set(directory, "a", null);
            if (Get(directory, "a") is not null || Get(directory, "b") != 0) return false;
            File.WriteAllText(FilePath(directory), "damaged");
            if (Get(directory, "a") != 9 || Get(directory, "b") != 0) return false;
            Set(directory, "a", null);
            if (Get(directory, "a") is not null || Get(directory, "b") != 0) return false;
            try { Set(directory, "b", 11); return false; }
            catch (ArgumentOutOfRangeException) { return Get(directory, "b") == 0; }
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }
}
