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
    // Undo intent is recorded before the index is rewritten. A crash between
    // the two leaves this set; the undo simply stays available and retrying is
    // idempotent because restored rows are skipped when already present.
    [JsonPropertyName("undoingId")] public string? UndoingId { get; set; }
    [JsonPropertyName("history")] public List<MissingCleanupRecord> History { get; set; } = [];
}

internal enum UndoBeginResult { Started, NothingToUndo, JournalFailed }

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

    // Returns null when the journal could not be written. The caller must not
    // touch the index in that case, otherwise a cleanup would exist with no
    // record of what was removed.
    internal static MissingCleanupRecord? Begin(string dataDir, IEnumerable<MissingCleanupEntry> entries)
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
        return Save(dataDir, state) ? record : null;
    }

    // Returns false when the "completed" marker could not be written; the
    // record then stays pending and the next start restores the rows, which is
    // safe because the index rows are the only thing that changed.
    internal static bool Complete(string dataDir, MissingCleanupRecord record)
    {
        var state = Load(dataDir);
        var stored = state.History.FirstOrDefault(item => item.Id == record.Id) ?? record;
        stored.CompletedAt = DateTimeOffset.Now;
        if (!state.History.Contains(stored)) state.History.Insert(0, stored);
        if (state.PendingId == record.Id) state.PendingId = null;
        return Save(dataDir, state);
    }

    internal static MissingCleanupRecord? Pending(string dataDir)
    {
        var state = Load(dataDir);
        if (state.PendingId is null) return null;
        return state.History.FirstOrDefault(item => item.Id == state.PendingId);
    }

    internal static bool MarkRolledBack(string dataDir, string recordId)
    {
        var state = Load(dataDir);
        var record = state.History.FirstOrDefault(item => item.Id == recordId);
        if (record is not null) record.RolledBack = true;
        if (state.PendingId == recordId) state.PendingId = null;
        return Save(dataDir, state);
    }

    internal static bool HasUndoable(string dataDir) =>
        Load(dataDir).History.Any(item => item.CompletedAt is not null && item.UndoneAt is null && !item.RolledBack);

    // Undo is two-phase: record the intent first, mark it done only after the
    // index was written. If the index write fails the intent is aborted, so the
    // cleanup stays undoable instead of losing its only chance.
    internal static UndoBeginResult BeginUndo(string dataDir, out MissingCleanupRecord? record)
    {
        record = null;
        var state = Load(dataDir);
        record = state.History.FirstOrDefault(item => item.CompletedAt is not null && item.UndoneAt is null && !item.RolledBack);
        if (record is null) return UndoBeginResult.NothingToUndo;
        if (state.UndoingId is not null) return UndoBeginResult.JournalFailed;
        state.UndoingId = record.Id;
        if (!Save(dataDir, state))
        {
            record = null;
            return UndoBeginResult.JournalFailed;
        }
        return UndoBeginResult.Started;
    }

    internal static bool CompleteUndo(string dataDir, string recordId)
    {
        var state = Load(dataDir);
        var record = state.History.FirstOrDefault(item => item.Id == recordId);
        if (record is null) return false;
        record.UndoneAt = DateTimeOffset.Now;
        if (state.UndoingId == recordId) state.UndoingId = null;
        return Save(dataDir, state);
    }

    internal static bool AbortUndo(string dataDir, string recordId)
    {
        var state = Load(dataDir);
        if (state.UndoingId != recordId) return true;
        state.UndoingId = null;
        return Save(dataDir, state);
    }

    // A crash during undo leaves the intent behind. Clearing it keeps the
    // action available; rows already restored are skipped on the retry.
    internal static void ResolveInterruptedUndo(string dataDir)
    {
        var state = Load(dataDir);
        if (state.UndoingId is null) return;
        state.UndoingId = null;
        Save(dataDir, state);
    }
}
