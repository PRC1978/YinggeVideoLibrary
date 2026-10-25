using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using System.Buffers;

namespace YinggeVideoLibrary;

// This history lives in config.json with Favorites and Tags. A config replace
// therefore commits a decision and its undo data in one atomic file update.
public sealed class SiteListLocalChange
{
    public string EntryKey { get; set; } = "";
    public string VideoId { get; set; } = "";
    public string LocalFingerprint { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public bool? BeforeFavorite { get; set; }
    public bool? AfterFavorite { get; set; }
    public List<string>? BeforeTags { get; set; }
    public List<string>? AfterTags { get; set; }
}

public sealed class SiteListLocalAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset TimeUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Kind { get; set; } = ""; // apply | undo
    public string? UndoesActionId { get; set; }
    public string Label { get; set; } = "";
    public List<SiteListLocalChange> Changes { get; set; } = [];
}

internal static class SiteListLocalActionStore
{
    internal sealed class Preview
    {
        public long ListRevision { get; set; }
        public List<SiteListLocalChange> Changes { get; set; } = [];
        public bool AddFavorite { get; set; }
        public string Tag { get; set; } = "";
        public int DuplicateLocalVideos { get; set; }
        public int AlreadySatisfied { get; set; }
    }

    internal static Preview Prepare(SiteListImportStore.State list,
        IEnumerable<SiteListImportStore.Entry> selected,
        IReadOnlyList<SiteListImportStore.LocalVideo> library, LibraryConfig config,
        bool addFavorite, string tag, CancellationToken cancellationToken = default)
    {
        tag = tag.Trim();
        if (!addFavorite && tag.Length == 0) throw new InvalidOperationException("请选收藏或填写标签。");
        if (tag.Length > 24 || tag.Any(char.IsControl))
            throw new InvalidOperationException("标签最多 24 个字符，不能包含控制字符。");
        var entries = selected.ToArray();
        if (entries.Length is 0 or > 25_000) throw new InvalidOperationException("请先选择要应用的已匹配条目。");
        var stored = list.Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var current = library.GroupBy(video => video.VideoId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var preview = new Preview { ListRevision = list.Revision, AddFavorite = addFavorite, Tag = tag };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verifiedContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var favoriteSet = config.Favorites.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Status != "已匹配" || entry.LocalFingerprint.Length != 64 ||
                entry.ContentSha256.Length != 64 ||
                !stored.TryGetValue(entry.Key, out var saved) || !SameEntry(entry, saved) ||
                !current.TryGetValue(entry.VideoId, out var candidates) || candidates.Length != 1)
                throw new InvalidOperationException("选中条目已变化或缺少导入时的内容校验，请重新导入核对。");
            var fresh = SiteListImportStore.RefreshCurrentFileStamp(candidates[0]);
            if (!SiteListImportStore.CanPlayMatchedEntry(entry, fresh, cancellationToken, verifiedContent))
                throw new InvalidOperationException("本地文件内容或作品资料与导入时不同，请重新导入核对后操作。");
            if (!seen.Add(entry.VideoId)) { preview.DuplicateLocalVideos++; continue; }
            var oldFavorite = favoriteSet.Contains(entry.VideoId);
            var oldTags = AiSuggestionAudit.TagsOf(config, entry.VideoId);
            var newTags = tag.Length == 0 || oldTags.Contains(tag, StringComparer.CurrentCultureIgnoreCase)
                ? oldTags : oldTags.Append(tag).ToList();
            if (newTags.Count > 50 && newTags.Count != oldTags.Count)
                throw new InvalidOperationException("视频标签已达 50 个，无法再添加。");
            var change = new SiteListLocalChange
            {
                EntryKey = entry.Key, VideoId = entry.VideoId, LocalFingerprint = entry.LocalFingerprint,
                BeforeFavorite = addFavorite && !oldFavorite ? oldFavorite : null,
                AfterFavorite = addFavorite && !oldFavorite ? true : null,
                BeforeTags = !oldTags.SequenceEqual(newTags, StringComparer.CurrentCultureIgnoreCase) ? oldTags : null,
                AfterTags = !oldTags.SequenceEqual(newTags, StringComparer.CurrentCultureIgnoreCase) ? newTags : null
            };
            if (change.BeforeFavorite is null && change.BeforeTags is null) preview.AlreadySatisfied++;
            else
            {
                change.ContentSha256 = entry.ContentSha256;
                preview.Changes.Add(change);
            }
        }
        return preview;
    }

    internal static bool SameEntry(SiteListImportStore.Entry left, SiteListImportStore.Entry right) =>
        left.Key == right.Key && left.BatchId == right.BatchId && left.Status == right.Status &&
        left.VideoId.Equals(right.VideoId, StringComparison.OrdinalIgnoreCase) &&
        left.LocalFingerprint == right.LocalFingerprint &&
        left.ContentSha256 == right.ContentSha256;

    internal static bool SameTargets(LibraryConfig config, IEnumerable<SiteListLocalChange> changes, bool after)
    {
        var favorites = config.Favorites.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            var favorite = after ? change.AfterFavorite : change.BeforeFavorite;
            if (favorite is not null && favorites.Contains(change.VideoId) != favorite.Value)
                return false;
            var tags = after ? change.AfterTags : change.BeforeTags;
            if (tags is not null && !AiSuggestionAudit.TagsOf(config, change.VideoId)
                    .SequenceEqual(tags, StringComparer.CurrentCultureIgnoreCase)) return false;
        }
        return true;
    }

    internal static SiteListLocalAction Apply(LibraryConfig config, Preview preview)
    {
        if (preview.Changes.Count == 0) throw new InvalidOperationException("所选作品的收藏和标签已符合目标，无需修改。");
        if (!SameTargets(config, preview.Changes, after: false))
            throw new InvalidOperationException("本地收藏或标签在预览后发生变化，请重新预览。");
        var action = new SiteListLocalAction
        {
            Kind = "apply", Label = (preview.AddFavorite ? "加入收藏" : "") +
                (preview.Tag.Length > 0 ? (preview.AddFavorite ? " + " : "") + "标签：" + preview.Tag : ""),
            Changes = CloneChanges(preview.Changes)
        };
        SetTargets(config, action.Changes, after: true);
        config.SiteListLocalActions.Add(action.Id, action);
        return action;
    }

    internal static SiteListLocalAction Undo(LibraryConfig config, string actionId)
    {
        if (!config.SiteListLocalActions.TryGetValue(actionId, out var target) || target.Kind != "apply" ||
            config.SiteListLocalActions.Values.Any(action => action.Kind == "undo" && action.UndoesActionId == actionId))
            throw new InvalidOperationException("找不到可撤销的片单操作，或这次操作已经撤销。");
        if (!SameTargets(config, target.Changes, after: true))
            throw new InvalidOperationException("收藏或标签随后被修改，撤销会覆盖新操作；请手动处理。");
        var undo = new SiteListLocalAction
        {
            Kind = "undo", UndoesActionId = target.Id, Label = "撤销" + target.Label,
            Changes = target.Changes.Select(change => new SiteListLocalChange
            {
                EntryKey = change.EntryKey, VideoId = change.VideoId, LocalFingerprint = change.LocalFingerprint,
                ContentSha256 = change.ContentSha256,
                BeforeFavorite = change.AfterFavorite, AfterFavorite = change.BeforeFavorite,
                BeforeTags = change.AfterTags?.ToList(), AfterTags = change.BeforeTags?.ToList()
            }).ToList()
        };
        SetTargets(config, target.Changes, after: false);
        config.SiteListLocalActions.Add(undo.Id, undo);
        return undo;
    }

    static void SetTargets(LibraryConfig config, IEnumerable<SiteListLocalChange> changes, bool after)
    {
        var favorites = config.Favorites.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        var touchedFavorite = false;
        foreach (var change in changes)
        {
            var favorite = after ? change.AfterFavorite : change.BeforeFavorite;
            if (favorite is not null)
            {
                touchedFavorite = true;
                if (favorite.Value && favorites.Add(change.VideoId)) added.Add(change.VideoId);
                else if (!favorite.Value) favorites.Remove(change.VideoId);
            }
            var tags = after ? change.AfterTags : change.BeforeTags;
            if (tags is not null)
            {
                if (tags.Count == 0) config.Tags.Remove(change.VideoId);
                else config.Tags[change.VideoId] = tags.ToList();
            }
        }
        if (touchedFavorite)
            config.Favorites = config.Favorites.Where(favorites.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).Concat(added).ToList();
    }

    static List<SiteListLocalChange> CloneChanges(IEnumerable<SiteListLocalChange> changes) =>
        changes.Select(change => new SiteListLocalChange
        {
            EntryKey = change.EntryKey, VideoId = change.VideoId, LocalFingerprint = change.LocalFingerprint,
            ContentSha256 = change.ContentSha256,
            BeforeFavorite = change.BeforeFavorite, AfterFavorite = change.AfterFavorite,
            BeforeTags = change.BeforeTags?.ToList(), AfterTags = change.AfterTags?.ToList()
        }).ToList();

    internal static bool IsHistoryWellFormed(LibraryConfig config)
    {
        if (config.SiteListLocalActions is null) return false;
        foreach (var (id, action) in config.SiteListLocalActions)
        {
            if (action is null || id.Length != 32 || id != action.Id ||
                action.Kind is not ("apply" or "undo") || action.Label is null or { Length: > 100 } ||
                action.Changes is not { Count: > 0 and <= 25_000 } ||
                action.Changes.Any(change => change is null) ||
                action.Changes.GroupBy(change => change.VideoId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1) ||
                action.Changes.Any(change => change.EntryKey?.Length != 64 ||
                    change.LocalFingerprint?.Length != 64 || change.ContentSha256?.Length != 64 ||
                    string.IsNullOrWhiteSpace(change.VideoId) ||
                    (change.BeforeFavorite is null && change.BeforeTags is null) ||
                    change.BeforeFavorite.HasValue != change.AfterFavorite.HasValue ||
                    (change.BeforeTags is null) != (change.AfterTags is null) ||
                    change.BeforeTags is { Count: > 50 } || change.AfterTags is { Count: > 50 } ||
                    change.BeforeTags?.Any(tag => tag is null) == true ||
                    change.AfterTags?.Any(tag => tag is null) == true)) return false;
            if (action.Kind == "apply")
            {
                if (action.UndoesActionId is not null) return false;
            }
            else
            {
                if (action.UndoesActionId is null ||
                    !config.SiteListLocalActions.TryGetValue(action.UndoesActionId, out var original) ||
                    original.Kind != "apply" || original.Changes.Count != action.Changes.Count) return false;
                for (var index = 0; index < action.Changes.Count; index++)
                {
                    var undone = action.Changes[index];
                    var applied = original.Changes[index];
                    if (undone.EntryKey != applied.EntryKey || undone.VideoId != applied.VideoId ||
                        undone.LocalFingerprint != applied.LocalFingerprint ||
                        undone.ContentSha256 != applied.ContentSha256 ||
                        undone.BeforeFavorite != applied.AfterFavorite || undone.AfterFavorite != applied.BeforeFavorite ||
                        !SameList(undone.BeforeTags, applied.AfterTags) ||
                        !SameList(undone.AfterTags, applied.BeforeTags)) return false;
                }
            }
        }
        return config.SiteListLocalActions.Values.Where(action => action.Kind == "undo")
            .GroupBy(action => action.UndoesActionId, StringComparer.Ordinal)
            .All(group => group.Count() == 1);
    }

    internal static SiteListLocalAction Clone(SiteListLocalAction action) =>
        JsonSerializer.Deserialize<SiteListLocalAction>(JsonSerializer.Serialize(action))!;

    static bool SameList(List<string>? left, List<string>? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    internal static string ComputeContentSha256(SiteListImportStore.LocalVideo snapshot,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Path.Length == 0) throw new InvalidOperationException("本地文件路径为空，无法确认作品内容。");
        // A read-only share prevents another Windows writer from replacing the
        // file during hashing. The stamp check catches ordinary concurrent
        // edits; the full digest catches equal-size/equal-time replacements.
        using var stream = new FileStream(snapshot.Path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        if (stream.Length != snapshot.Size)
            throw new InvalidOperationException("本地文件大小在核对期间改变，请重新预览。");
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        string digest;
        try
        {
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasher.AppendData(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            digest = Convert.ToHexString(hasher.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        var current = new FileInfo(snapshot.Path);
        if (!current.Exists || current.Length != snapshot.Size ||
            current.LastWriteTime.Ticks != snapshot.ModifiedTicks)
            throw new InvalidOperationException("本地文件在核对期间改变，请重新预览。");
        return digest;
    }
}
