using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YinggeVideoLibrary;

internal sealed class MissingCleanupEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("modifiedTicks")] public long ModifiedTicks { get; set; }
}

internal sealed class MissingCleanupRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; set; }
    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; set; }
    [JsonPropertyName("undoneAt")] public DateTimeOffset? UndoneAt { get; set; }
    // True when an interrupted run was rolled back on the next start.
    [JsonPropertyName("rolledBack")] public bool RolledBack { get; set; }
    [JsonPropertyName("entries")] public List<MissingCleanupEntry> Entries { get; set; } = [];
}

internal sealed class MissingCleanupState
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("pendingId")] public string? PendingId { get; set; }
    [JsonPropertyName("history")] public List<MissingCleanupRecord> History { get; set; } = [];
}

// Only index records are removed. No file is ever deleted here: the store keeps
// the removed paths so a cleanup can be undone and an interrupted one rolled
// back on the next start.
internal static class MissingCleanupStore
{
    internal const string FileName = "missing-cleanup.json";
    const int MaxHistory = 20;

    static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    internal static MissingCleanupState Load(string dataDir)
    {
        var path = PathFor(dataDir);
        if (!File.Exists(path)) return new MissingCleanupState();
        try
        {
            var state = JsonSerializer.Deserialize<MissingCleanupState>(File.ReadAllText(path));
            if (state is null) return new MissingCleanupState();
            state.History ??= [];
            return state;
        }
        catch
        {
            // A damaged cleanup log must not stop the library from opening.
            return new MissingCleanupState();
        }
    }

    internal static bool Save(string dataDir, MissingCleanupState state)
    {
        var path = PathFor(dataDir);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(dataDir);
            if (state.History.Count > MaxHistory)
                state.History = state.History.OrderByDescending(item => item.StartedAt).Take(MaxHistory).ToList();
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temporary, path, true);
            return true;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return false;
        }
    }

    internal static MissingCleanupRecord Begin(string dataDir, IEnumerable<MissingCleanupEntry> entries)
    {
        var state = Load(dataDir);
        var record = new MissingCleanupRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.Now,
            Entries = entries.ToList()
        };
        state.History.Insert(0, record);
        state.PendingId = record.Id;
        Save(dataDir, state);
        return record;
    }

    internal static void Complete(string dataDir, MissingCleanupRecord record)
    {
        var state = Load(dataDir);
        var stored = state.History.FirstOrDefault(item => item.Id == record.Id) ?? record;
        stored.CompletedAt = DateTimeOffset.Now;
        if (!state.History.Contains(stored)) state.History.Insert(0, stored);
        if (state.PendingId == record.Id) state.PendingId = null;
        Save(dataDir, state);
    }

    internal static MissingCleanupRecord? Pending(string dataDir)
    {
        var state = Load(dataDir);
        if (state.PendingId is null) return null;
        return state.History.FirstOrDefault(item => item.Id == state.PendingId);
    }

    internal static void MarkRolledBack(string dataDir, string recordId)
    {
        var state = Load(dataDir);
        var record = state.History.FirstOrDefault(item => item.Id == recordId);
        if (record is not null) record.RolledBack = true;
        if (state.PendingId == recordId) state.PendingId = null;
        Save(dataDir, state);
    }

    internal static bool HasUndoable(string dataDir) =>
        Load(dataDir).History.Any(item => item.CompletedAt is not null && item.UndoneAt is null && !item.RolledBack);

    internal static MissingCleanupRecord? UndoLast(string dataDir)
    {
        var state = Load(dataDir);
        var record = state.History.FirstOrDefault(item => item.CompletedAt is not null && item.UndoneAt is null && !item.RolledBack);
        if (record is null) return null;
        record.UndoneAt = DateTimeOffset.Now;
        Save(dataDir, state);
        return record;
    }
}
