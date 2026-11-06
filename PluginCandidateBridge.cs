using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// The candidate document is authoritative in the selected storage mode.
internal sealed class PluginCandidateBridge(string dataDir, PostgresLibraryRuntimeSession? session)
{
    internal PluginCandidateState Load()
    {
        if (session is null) return PluginCandidateStore.Load(dataDir);
        return Task.Run(async () =>
        {
            var document = await PostgresLibraryDocumentStore.ReadAsync(session.ConnectionString,
                PluginCandidateStore.FileName);
            return document is null ? new PluginCandidateState() : Parse(document.Content);
        }).GetAwaiter().GetResult();
    }

    internal IReadOnlyList<PluginCandidateRecord> Get(string videoId) =>
        Load().Videos.TryGetValue(videoId, out var list) ? list : [];

    internal int CountFor(string videoId) => Get(videoId).Count;

    internal (int Count, bool Saved) Add(string videoId, PluginCandidateRecord record)
    {
        if (session is null) return PluginCandidateStore.Add(dataDir, videoId, record);
        try
        {
            return Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var document = await PostgresLibraryDocumentStore.ReadAsync(
                        session.ConnectionString, PluginCandidateStore.FileName);
                    var state = document is null ? new PluginCandidateState() : Parse(document.Content);
                    if (!state.Videos.TryGetValue(videoId, out var list))
                        state.Videos[videoId] = list = [];
                    var key = Key(record);
                    if (list.Any(item => string.Equals(Key(item), key,
                        StringComparison.OrdinalIgnoreCase))) return (list.Count, true);
                    list.Add(record);
                    if (list.Count > 8) list.RemoveRange(0, list.Count - 8);
                    try
                    {
                        await PostgresLibraryDocumentStore.SaveAsync(session.ConnectionString,
                            PluginCandidateStore.FileName, JsonSerializer.SerializeToUtf8Bytes(state),
                            document?.Revision);
                        return (list.Count, true);
                    }
                    catch (InvalidOperationException) when (attempt < 2) { }
                }
                return (0, false);
            }).GetAwaiter().GetResult();
        }
        catch { return (0, false); }
    }

    private static PluginCandidateState Parse(byte[] content)
    {
        if (content.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("插件候选记录超过允许大小。");
        var state = JsonSerializer.Deserialize<PluginCandidateState>(content)
            ?? throw new InvalidDataException("插件候选记录格式无效。");
        if (state.SchemaVersion != 1 || state.Videos is null || state.Videos.Count > 1_000_000 ||
            state.Videos.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                pair.Value.Count > 8 || pair.Value.Any(item => item is null ||
                    item.Metadata is null || string.IsNullOrWhiteSpace(item.PluginId))))
            throw new InvalidDataException("插件候选记录内容无效。");
        return state;
    }

    private static string Key(PluginCandidateRecord item) =>
        $"{item.PluginId}|{item.Metadata.SourceUrl}|{item.Metadata.Code}|{item.Metadata.Title}";
}
