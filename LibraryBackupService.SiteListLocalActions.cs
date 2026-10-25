using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

internal static partial class LibraryBackupService
{
    // Full relocation keeps the user's local-action history. Its old video
    // fingerprints contain the old path, so the generic ID/path JSON mapper
    // alone cannot make those records safe to undo on the target library.
    static void RewriteSiteListLocalActionFingerprints(string targetConfigPath,
        string sourceDirectory, string targetDirectory,
        IReadOnlyDictionary<string, string> idMap)
    {
        if (!File.Exists(targetConfigPath)) return;
        var sourcePath = Path.Combine(sourceDirectory, "config.json");
        if (!File.Exists(sourcePath)) return;
        var source = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(sourcePath))
            ?? throw new InvalidDataException("来源片单操作历史无法读取。");
        var target = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(targetConfigPath))
            ?? throw new InvalidDataException("目标片单操作历史无法读取。");
        if (source.SiteListLocalActions.Count == 0) return;
        if (!SiteListLocalActionStore.IsHistoryWellFormed(source) ||
            !SiteListLocalActionStore.IsHistoryWellFormed(target))
            throw new InvalidDataException("片单本地操作历史不完整，已停止迁移。");
        RemapSiteListLocalActions(source, target, idMap,
            id => TryTargetSiteListSnapshot(sourceDirectory, id, requireLiveFile: false),
            id => TryTargetSiteListSnapshot(targetDirectory, id));
        File.WriteAllText(targetConfigPath, JsonSerializer.Serialize(target, JsonOptions));
    }

    internal static void RemapSiteListLocalActions(LibraryConfig source, LibraryConfig target,
        IReadOnlyDictionary<string, string> idMap,
        Func<string, SiteListImportStore.LocalVideo?> sourceSnapshot,
        Func<string, SiteListImportStore.LocalVideo?> targetSnapshot)
    {
        if (!SiteListLocalActionStore.IsHistoryWellFormed(source) ||
            !SiteListLocalActionStore.IsHistoryWellFormed(target) ||
            source.SiteListLocalActions.Count != target.SiteListLocalActions.Count)
            throw new InvalidDataException("片单本地操作历史不完整，已停止迁移。");
        var sourceFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targetFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targetContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, oldAction) in source.SiteListLocalActions)
        {
            if (!target.SiteListLocalActions.TryGetValue(id, out var newAction) ||
                oldAction.Kind != newAction.Kind || oldAction.Changes.Count != newAction.Changes.Count)
                throw new InvalidDataException("片单操作记录的前后快照不一致，已停止迁移。");
            for (var index = 0; index < oldAction.Changes.Count; index++)
            {
                var oldChange = oldAction.Changes[index];
                var newChange = newAction.Changes[index];
                if (!idMap.TryGetValue(oldChange.VideoId, out var mapped) ||
                    oldChange.EntryKey != newChange.EntryKey ||
                    !mapped.Equals(newChange.VideoId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("片单操作仍引用无法映射的视频，已停止迁移。");
                if (!sourceFingerprints.TryGetValue(oldChange.VideoId, out var oldFingerprint))
                {
                    var snapshot = sourceSnapshot(oldChange.VideoId)
                        ?? throw new InvalidDataException("来源视频快照缺失，已停止迁移片单操作历史。");
                    oldFingerprint = SiteListImportStore.ComputeLocalFingerprint(snapshot);
                    sourceFingerprints[oldChange.VideoId] = oldFingerprint;
                }
                if (!oldFingerprint.Equals(oldChange.LocalFingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("来源视频在片单操作后已变化，历史不能安全迁移。");
                if (!targetFingerprints.TryGetValue(mapped, out var newFingerprint))
                {
                    var snapshot = targetSnapshot(mapped)
                        ?? throw new InvalidDataException("目标视频快照缺失，历史不能安全迁移。");
                    newFingerprint = SiteListImportStore.ComputeLocalFingerprint(snapshot);
                    targetFingerprints[mapped] = newFingerprint;
                    targetContents[mapped] = SiteListLocalActionStore.ComputeContentSha256(snapshot);
                }
                if (!targetContents[mapped].Equals(oldChange.ContentSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("目标文件内容与片单操作历史不一致，已停止迁移。");
                newChange.VideoId = mapped;
                newChange.LocalFingerprint = newFingerprint;
            }
        }
        if (!SiteListLocalActionStore.IsHistoryWellFormed(target))
            throw new InvalidDataException("映射后的片单操作历史无效，已停止迁移。");
    }

    // Selective import merges categories/tags/reviews field by field and does
    // not import Favorites or action history. Remove source history from the
    // temporary mapped config so generic remapping never leaves stale IDs or
    // fingerprints in an intermediate package. Full backups remain intact.
    static void StripUnimportedSiteListLocalActions(string path)
    {
        if (!File.Exists(path)) return;
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("选择性导入配置格式无效。");
        root.Remove("siteListLocalActions");
        File.WriteAllText(path, root.ToJsonString(JsonOptions));
    }
}
