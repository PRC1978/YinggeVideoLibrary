using System.IO;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // The UI stays responsive while disk checks and the atomic config replace
    // run on a worker. All related config writers share ConfigWriteMutex.
    async Task<SiteListLocalAction> CommitSiteListLocalActionAsync(
        SiteListLocalActionStore.Preview preview)
    {
        var local = SnapshotSiteListLibrary();
        var oldMemory = CloneLibraryConfig(config);
        var result = await Task.Run(() => SaveSiteListLocalAction(preview, local,
            ConfigPath, DataDir, credentialStoreReady, javinfoCredentialReady));
        ReconcileSiteListLocalAction(result.Config, oldMemory, result.Action);
        return result.Action;
    }

    async Task<SiteListLocalAction> UndoSiteListLocalActionAsync(string actionId)
    {
        var local = SnapshotSiteListLibrary();
        var oldMemory = CloneLibraryConfig(config);
        var result = await Task.Run(() => UndoSavedSiteListLocalAction(actionId, local,
            ConfigPath, credentialStoreReady, javinfoCredentialReady));
        ReconcileSiteListLocalAction(result.Config, oldMemory, result.Action);
        return result.Action;
    }

    void ReconcileSiteListLocalAction(LibraryConfig persisted, LibraryConfig oldMemory,
        SiteListLocalAction action)
    {
        // Preserve unrelated settings changed by another UI callback while the
        // worker was writing. The action's targeted fields were checked under
        // the writer mutex before the persisted snapshot was replaced.
        var reconciled = MergeLibraryConfig(oldMemory, config, persisted);
        reconciled.TmdbToken = config.TmdbToken;
        reconciled.AiKey = config.AiKey;
        reconciled.JavinfoKey = config.JavinfoKey;
        config = reconciled;
        configBaseline = CloneLibraryConfig(persisted);
        configBaseline.TmdbToken = config.TmdbToken;
        configBaseline.AiKey = config.AiKey;
        configBaseline.JavinfoKey = config.JavinfoKey;
        configSource = "config.json";
        configSaveVersion++;
        if (action.Changes.Any(change => change.BeforeTags is not null)) categoryDataVersion++;
        var affected = action.Changes.Select(change => change.VideoId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos.Where(video => affected.Contains(video.Id)))
        {
            video.Favorite = config.Favorites.Contains(video.Id, StringComparer.OrdinalIgnoreCase);
            video.Tags.Clear();
            video.Tags.AddRange(AiSuggestionAudit.TagsOf(config, video.Id));
            video.RefreshTags();
        }
        if (view is "all" or "recent" or "favorites" or "categories") RenderCards();
    }

    internal static (LibraryConfig Config, SiteListLocalAction Action) SaveSiteListLocalAction(
        SiteListLocalActionStore.Preview preview,
        IReadOnlyList<SiteListImportStore.LocalVideo> library, string configPath,
        string dataDir, bool scrubCredentials, bool scrubJavinfo)
    {
        var ownsMutex = false;
        try
        {
            try { ConfigWriteMutex.WaitOne(); ownsMutex = true; }
            catch (AbandonedMutexException) { ownsMutex = true; }
            var latest = ReadSiteListActionConfig(configPath);
            var list = SiteListImportStore.Load(dataDir);
            if (list.Revision != preview.ListRevision)
                throw new InvalidOperationException("导入清单已更新，请重新预览。");
            ValidateSiteListLocalIdentity(preview.Changes, list, library, requireEntry: true);
            var current = CloneLibraryConfig(latest);
            var action = SiteListLocalActionStore.Apply(current, preview);
            var persisted = WriteConfigSnapshot(latest, current, configPath, scrubCredentials, scrubJavinfo);
            return (persisted, action);
        }
        finally { if (ownsMutex) ConfigWriteMutex.ReleaseMutex(); }
    }

    internal static (LibraryConfig Config, SiteListLocalAction Action) UndoSavedSiteListLocalAction(
        string actionId, IReadOnlyList<SiteListImportStore.LocalVideo> library,
        string configPath, bool scrubCredentials, bool scrubJavinfo)
    {
        var ownsMutex = false;
        try
        {
            try { ConfigWriteMutex.WaitOne(); ownsMutex = true; }
            catch (AbandonedMutexException) { ownsMutex = true; }
            var latest = ReadSiteListActionConfig(configPath);
            if (!latest.SiteListLocalActions.TryGetValue(actionId, out var original) || original.Kind != "apply")
                throw new InvalidOperationException("片单操作历史已改变，请刷新后重试。");
            ValidateSiteListLocalIdentity(original.Changes, null, library, requireEntry: false);
            var current = CloneLibraryConfig(latest);
            var action = SiteListLocalActionStore.Undo(current, actionId);
            var persisted = WriteConfigSnapshot(latest, current, configPath, scrubCredentials, scrubJavinfo);
            return (persisted, action);
        }
        finally { if (ownsMutex) ConfigWriteMutex.ReleaseMutex(); }
    }

    static LibraryConfig ReadSiteListActionConfig(string configPath)
    {
        if (TryReadJson(configPath, out LibraryConfig? latest) && latest is not null &&
            SiteListLocalActionStore.IsHistoryWellFormed(latest)) return latest;
        if (!File.Exists(configPath) && !File.Exists(configPath + ".bak")) return new LibraryConfig();
        throw new InvalidDataException("配置或片单操作历史无法读取，已停止修改以保护现有数据。");
    }

    static void ValidateSiteListLocalIdentity(IEnumerable<SiteListLocalChange> changes,
        SiteListImportStore.State? list,
        IReadOnlyList<SiteListImportStore.LocalVideo> library, bool requireEntry)
    {
        var current = library.GroupBy(video => video.VideoId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var entries = list?.Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (change.LocalFingerprint.Length != 64 || !current.TryGetValue(change.VideoId, out var matches) ||
                matches.Length != 1)
                throw new InvalidOperationException("本地视频索引已变化，请重新扫描和核对片单。");
            var fresh = SiteListImportStore.RefreshCurrentFileStamp(matches[0]);
            if (fresh is null || SiteListImportStore.ComputeLocalFingerprint(fresh) != change.LocalFingerprint)
                throw new InvalidOperationException("本地视频文件或资料已变化，已拒绝修改另一个作品。");
            if (change.ContentSha256.Length != 64 ||
                !SiteListLocalActionStore.ComputeContentSha256(fresh)
                    .Equals(change.ContentSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("本地视频内容已变化，已拒绝修改另一个作品。");
            if (requireEntry && (entries is null || !entries.TryGetValue(change.EntryKey, out var entry) ||
                entry.Status != "已匹配" || entry.VideoId != change.VideoId ||
                entry.LocalFingerprint != change.LocalFingerprint ||
                entry.ContentSha256 != change.ContentSha256))
                throw new InvalidOperationException("导入清单中的匹配关系已变化，请重新预览。");
        }
    }
}
