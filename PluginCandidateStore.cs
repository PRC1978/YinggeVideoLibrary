using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YinggeVideoLibrary;

internal sealed class PluginCandidateRecord
{
    [JsonPropertyName("pluginId")] public string PluginId { get; set; } = "";
    [JsonPropertyName("savedAt")] public DateTimeOffset SavedAt { get; set; }
    [JsonPropertyName("hint")] public int Hint { get; set; }
    [JsonPropertyName("evidence")] public string Evidence { get; set; } = "";
    [JsonPropertyName("metadata")] public MovieMetadata Metadata { get; set; } = new();
}

internal sealed class PluginCandidateState
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("videos")] public Dictionary<string, List<PluginCandidateRecord>> Videos { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

// A video can be returned by several plugins, or by one plugin several times.
// The review record keeps the selected candidate; this store keeps all of them
// so the review page can switch between them after a restart.
internal static class PluginCandidateStore
{
    internal const string FileName = "plugin-candidates.json";
    const int MaxPerVideo = 8;

    static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    internal static PluginCandidateState Load(string dataDir)
    {
        var path = PathFor(dataDir);
        if (!File.Exists(path)) return new PluginCandidateState();
        try
        {
            var state = JsonSerializer.Deserialize<PluginCandidateState>(File.ReadAllText(path));
            if (state is null) return new PluginCandidateState();
            state.Videos ??= new(StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch
        {
            return new PluginCandidateState();
        }
    }

    internal static bool Save(string dataDir, PluginCandidateState state)
    {
        var path = PathFor(dataDir);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
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

    static string Key(PluginCandidateRecord record) =>
        $"{record.PluginId}|{record.Metadata.SourceUrl}|{record.Metadata.Code}|{record.Metadata.Title}";

    // Returns the number of candidates stored for this video after the call.
    // Duplicates are counted, not appended, so the number the caller reports is
    // exactly what a restart will show.
    // Saved is false when the candidate file could not be written; the caller
    // must not report the candidate as queued in that case.
    internal static (int Count, bool Saved) Add(string dataDir, string videoId, PluginCandidateRecord record)
    {
        var state = Load(dataDir);
        if (!state.Videos.TryGetValue(videoId, out var list))
        {
            list = [];
            state.Videos[videoId] = list;
        }
        if (list.Any(existing => string.Equals(Key(existing), Key(record), StringComparison.OrdinalIgnoreCase)))
            return (list.Count, true);
        list.Add(record);
        if (list.Count > MaxPerVideo) list.RemoveRange(0, list.Count - MaxPerVideo);
        if (Save(dataDir, state)) return (list.Count, true);
        list.Remove(record);
        return (list.Count, false);
    }

    internal static IReadOnlyList<PluginCandidateRecord> Get(string dataDir, string videoId) =>
        Load(dataDir).Videos.TryGetValue(videoId, out var list) ? list : [];

    internal static int CountFor(string dataDir, string videoId) =>
        Load(dataDir).Videos.TryGetValue(videoId, out var list) ? list.Count : 0;

    internal static int IndexOf(IReadOnlyList<PluginCandidateRecord> list, string sourceUrl, string code)
    {
        for (var index = 0; index < list.Count; index++)
            if (string.Equals(list[index].Metadata.SourceUrl, sourceUrl, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(list[index].Metadata.Code ?? "", code ?? "", StringComparison.OrdinalIgnoreCase))
                return index;
        return -1;
    }
}

// What the user is told after a plugin search. Queued is false unless a review
// record actually points at a stored candidate.
internal enum PluginQueueState { Queued, KeptSelection, AlreadyApproved, Failed }

internal sealed record PluginCandidateSaveResult(int Stored, PluginQueueState State, string Error = "")
{
    internal bool Queued => State is PluginQueueState.Queued or PluginQueueState.KeptSelection;

    internal string Describe(int elapsedMs) => State switch
    {
        PluginQueueState.Queued =>
            $"该视频现有 {Stored} 条候选，已加入人工审核队列（{elapsedMs} ms）。原始资料没有被修改。",
        PluginQueueState.KeptSelection =>
            $"该视频现有 {Stored} 条候选；已有待审记录，保留你选择的那条（{elapsedMs} ms）。",
        PluginQueueState.AlreadyApproved =>
            $"该视频已有通过审核的资料；{Stored} 条候选已保存但不会入队。",
        _ => Stored > 0
            ? $"已保存 {Stored} 条候选，但{Error}；可稍后重试。"
            : $"候选未能保存：{Error}"
    };
}
