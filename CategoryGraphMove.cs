using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal enum CategoryGraphMoveKind { Video, Folder, Branch }
internal enum CategoryBranchMoveMode { Nest, Merge }

internal sealed class CategoryGraphState
{
    public required string Primary { get; init; }
    public required string[] Paths { get; init; }
    public string? SavedPrimary { get; init; }
    public string[]? SavedPaths { get; init; }

    public static CategoryGraphState Capture(VideoItem video, LibraryConfig config) => new()
    {
        Primary = video.Category,
        Paths = video.CategoryList.ToArray(),
        SavedPrimary = config.Categories.GetValueOrDefault(video.Id),
        SavedPaths = config.MultiCategories.TryGetValue(video.Id, out var paths) ? paths.ToArray() : null
    };

    public static CategoryGraphState ForPaths(string[] paths) => new()
    {
        Primary = paths.FirstOrDefault() ?? "未分类",
        Paths = paths,
        SavedPrimary = paths.FirstOrDefault(),
        SavedPaths = paths.Length == 0 ? null : paths.ToArray()
    };

    public void Apply(VideoItem video, LibraryConfig config)
    {
        video.CategoryList.Clear();
        video.CategoryList.AddRange(Paths);
        video.Category = Primary;
        if (SavedPrimary is null) config.Categories.Remove(video.Id);
        else config.Categories[video.Id] = SavedPrimary;
        if (SavedPaths is null) config.MultiCategories.Remove(video.Id);
        else config.MultiCategories[video.Id] = SavedPaths.ToList();
    }

    public bool Matches(VideoItem video, LibraryConfig config) =>
        string.Equals(video.Category, Primary, StringComparison.Ordinal) &&
        video.CategoryList.Count == Paths.Length &&
        video.CategoryList.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Paths) &&
        MatchesSaved(video.Id, config);

    public bool MatchesSaved(string id, LibraryConfig config) =>
        string.Equals(config.Categories.GetValueOrDefault(id), SavedPrimary, StringComparison.Ordinal) &&
        ((SavedPaths is null && !config.MultiCategories.ContainsKey(id)) ||
         (SavedPaths is not null && config.MultiCategories.TryGetValue(id, out var paths) &&
          paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(SavedPaths)));

    public bool SameAs(CategoryGraphState other) =>
        string.Equals(Primary, other.Primary, StringComparison.Ordinal) &&
        Paths.SequenceEqual(other.Paths, StringComparer.Ordinal) &&
        string.Equals(SavedPrimary, other.SavedPrimary, StringComparison.Ordinal) &&
        ((SavedPaths is null && other.SavedPaths is null) ||
         (SavedPaths is not null && other.SavedPaths is not null &&
          SavedPaths.SequenceEqual(other.SavedPaths, StringComparer.Ordinal)));
}

internal sealed record CategoryGraphChange(VideoItem Video, CategoryGraphState Before, CategoryGraphState After);
internal sealed record CategoryGraphVideoSnapshot(VideoItem Video, string Folder, CategoryGraphState Before)
{
    public string[] Categories => Before.Paths.Length == 0 ? [Before.Primary] : Before.Paths;
}
internal sealed record CategoryGraphVideoReference(string Id, string Path, VideoItem Video);

internal sealed record CategoryGraphHistoryChange(string VideoId, string VideoPath,
    CategoryGraphState Before, CategoryGraphState After);

internal sealed record CategoryGraphHistoryEntry(Guid Id, DateTimeOffset SavedAt,
    CategoryGraphMoveKind Kind, string Source, string Target, string DestinationRoot,
    CategoryBranchMoveMode BranchMode, string[] NamesBefore, string[] NamesAfter,
    string SelectionBefore, string SelectionAfter, CategoryGraphHistoryChange[] Changes)
{
    public static CategoryGraphHistoryEntry FromPlan(CategoryGraphMovePlan plan,
        string selectionBefore, string selectionAfter) => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            plan.Kind, plan.Source, plan.Target, plan.DestinationRoot, plan.BranchMode,
            plan.NamesBefore.ToArray(), plan.NamesAfter.ToArray(), selectionBefore, selectionAfter,
            plan.Changes.Select(change => new CategoryGraphHistoryChange(change.Video.Id,
                change.Video.Path, change.Before, change.After)).ToArray());

    public CategoryGraphMovePlan? Rebind(IReadOnlyList<VideoItem> videos) =>
        Rebind(videos.Select(video => new CategoryGraphVideoReference(video.Id, video.Path, video)).ToArray());

    public CategoryGraphMovePlan? Rebind(IReadOnlyList<CategoryGraphVideoReference> videos)
    {
        var byId = new Dictionary<string, CategoryGraphVideoReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
            if (!byId.TryAdd(video.Id, video)) return null;
        return Rebind(byId);
    }

    public CategoryGraphMovePlan? Rebind(IReadOnlyDictionary<string, CategoryGraphVideoReference> byId)
    {
        if (Id == Guid.Empty || NamesBefore is null || NamesAfter is null || Changes is null ||
            Source is null || Target is null || DestinationRoot is null ||
            Changes.Any(change => string.IsNullOrWhiteSpace(change.VideoId) ||
                string.IsNullOrWhiteSpace(change.VideoPath) || change.Before is null || change.After is null ||
                change.Before.Paths is null || change.After.Paths is null)) return null;
        var resolved = new List<CategoryGraphChange>(Changes.Length);
        foreach (var change in Changes)
        {
            if (!byId.TryGetValue(change.VideoId, out var video) ||
                !string.Equals(video.Path, change.VideoPath, StringComparison.OrdinalIgnoreCase)) return null;
            resolved.Add(new(video.Video, change.Before, change.After));
        }
        return new CategoryGraphMovePlan
        {
            Kind = Kind, Source = Source, Target = Target, DestinationRoot = DestinationRoot,
            BranchMode = BranchMode, NamesBefore = NamesBefore, NamesAfter = NamesAfter,
            Changes = resolved
        };
    }
}

internal static class CategoryGraphMoveHistoryStore
{
    const int FormatVersion = 1;
    const int RetainedMoves = 3;
    sealed record HistoryFile(int Version, CategoryGraphHistoryEntry[] Entries);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static IReadOnlyList<CategoryGraphHistoryEntry> Read(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = File.OpenRead(path);
        var saved = JsonSerializer.Deserialize<HistoryFile>(stream, JsonOptions);
        if (saved is null || saved.Version != FormatVersion || saved.Entries is null ||
            saved.Entries.Any(entry => entry is null || entry.Id == Guid.Empty || entry.Changes is null))
            throw new InvalidDataException("图形分类撤销历史格式不完整。原文件已保留。");
        return saved.Entries.Take(RetainedMoves).ToArray();
    }

    static T WithLock<T>(string path, Func<T> action)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var mutex = new Mutex(false, @"Local\YinggeVideoLibrary.GraphMoveHistory.v1");
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new TimeoutException("撤销历史正在由另一个程序保存，请稍后重试。");
            return action();
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    static IReadOnlyList<CategoryGraphHistoryEntry> Write(string path,
        IReadOnlyList<CategoryGraphHistoryEntry> entries)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 65536, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new HistoryFile(FormatVersion, entries.ToArray()), JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return entries;
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    public static IReadOnlyList<CategoryGraphHistoryEntry> Add(string path, CategoryGraphHistoryEntry entry) =>
        WithLock(path, () =>
        {
            var entries = Read(path).Where(saved => saved.Id != entry.Id).Prepend(entry)
                .Take(RetainedMoves).ToArray();
            return Write(path, entries);
        });

    public static IReadOnlyList<CategoryGraphHistoryEntry> Remove(string path, Guid id) =>
        WithLock(path, () => Write(path, Read(path).Where(entry => entry.Id != id)
            .Take(RetainedMoves).ToArray()));

    public static bool VerifyForDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yingge-graph-history-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "graph-move-history.json");
        try
        {
            var config = new LibraryConfig { CategoryNames = ["原分类", "目标分类"] };
            var video = new VideoItem { Id = "isolated-one", Path = Path.Combine(directory, "one.mp4"),
                Folder = directory, Title = "one", Size = 1, Modified = DateTime.UnixEpoch,
                Category = "原分类" };
            video.CategoryList.Add("原分类");
            config.Categories[video.Id] = "原分类";
            config.MultiCategories[video.Id] = ["原分类"];
            var plan = CategoryGraphMovePlan.ForVideo(video, config, "目标分类");
            var entry = CategoryGraphHistoryEntry.FromPlan(plan, "原分类", "目标分类");
            var saved = Add(path, entry);
            for (var index = 0; index < 3; index++)
                saved = Add(path, entry with { Id = Guid.NewGuid(), SavedAt = DateTimeOffset.UtcNow.AddSeconds(index) });
            if (saved.Count != 3 || Read(path).Count != 3 || saved[0].Id == entry.Id) return false;
            // A partial index cannot bind the video yet; it must not delete
            // the durable record before a later complete scan can bind it.
            if (saved[0].Rebind(Array.Empty<VideoItem>()) is not null || Read(path).Count != 3) return false;
            var restored = saved[0].Rebind([video]);
            if (restored is null || restored.Changes.Count != 1) return false;
            restored.ApplyChange(restored.Changes[0], config, after: true);
            restored.ApplyNames(config, after: true);
            if (!restored.MatchesCurrent(config, after: true)) return false;
            video.Category = "人工修改";
            if (restored.MatchesCurrent(config, after: true)) return false;
            restored.ApplyChange(restored.Changes[0], config, after: true);
            var wrongPath = new VideoItem { Id = video.Id, Path = Path.Combine(directory, "other.mp4"),
                Folder = directory, Title = video.Title, Size = 1, Modified = DateTime.UnixEpoch };
            if (saved[0].Rebind([wrongPath]) is not null) return false;
            var remaining = Remove(path, saved[0].Id);
            return remaining.Count == 2 && remaining.All(other => other.Id != saved[0].Id) &&
                Read(path).Count == 2;
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}

// A plan does not mutate anything until the user accepts its preview. It
// retains exact before and after states so undo can refuse stale changes.
internal sealed class CategoryGraphMovePlan
{
    public required CategoryGraphMoveKind Kind { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required string DestinationRoot { get; init; }
    public required CategoryBranchMoveMode BranchMode { get; init; }
    public required List<CategoryGraphChange> Changes { get; init; }
    public required string[] NamesBefore { get; init; }
    public required string[] NamesAfter { get; init; }
    static bool SameNames(IEnumerable<string> left, IEnumerable<string> right) =>
        left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);
    public bool MatchesNames(LibraryConfig config, bool after) =>
        SameNames(config.CategoryNames, after ? NamesAfter : NamesBefore);
    public bool HasChanges => Changes.Count > 0 || !SameNames(NamesBefore, NamesAfter);

    static bool Within(string path, string branch) =>
        path.Equals(branch, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(branch + " / ", StringComparison.OrdinalIgnoreCase);

    static CategoryGraphMovePlan BuildReplacement(IEnumerable<VideoItem> candidates,
        LibraryConfig config, CategoryGraphMoveKind kind, string source, string target)
    {
        var changes = new List<CategoryGraphChange>();
        foreach (var video in candidates)
        {
            var before = CategoryGraphState.Capture(video, config);
            var after = CategoryGraphState.ForPaths([target]);
            if (!before.SameAs(after)) changes.Add(new CategoryGraphChange(video, before, after));
        }
        var beforeNames = config.CategoryNames.ToArray();
        var afterNames = beforeNames.ToList();
        if (!afterNames.Contains(target, StringComparer.OrdinalIgnoreCase)) afterNames.Add(target);
        return new CategoryGraphMovePlan
        {
            Kind = kind, Source = source, Target = target, DestinationRoot = target,
            BranchMode = CategoryBranchMoveMode.Merge, Changes = changes,
            NamesBefore = beforeNames, NamesAfter = afterNames.ToArray()
        };
    }

    public static CategoryGraphMovePlan ForVideo(VideoItem video, LibraryConfig config, string target) =>
        BuildReplacement([video], config, CategoryGraphMoveKind.Video, video.DisplayTitle, target);

    public static CategoryGraphMovePlan ForFolder(IEnumerable<VideoItem> videos, LibraryConfig config,
        string source, string target) => ForFolderSnapshots(videos.Select(video =>
            new CategoryGraphVideoSnapshot(video, video.Folder, CategoryGraphState.Capture(video, config))).ToArray(),
            config.CategoryNames.ToArray(), source, target);

    public static CategoryGraphMovePlan ForFolderSnapshots(IReadOnlyList<CategoryGraphVideoSnapshot> videos,
        string[] categoryNames, string source, string target)
    {
        var root = Path.GetPathRoot(source);
        var folder = string.Equals(source, root, StringComparison.OrdinalIgnoreCase)
            ? source : source.TrimEnd('\\', '/');
        var separator = Path.DirectorySeparatorChar;
        var prefix = folder.EndsWith(separator) ? folder : folder + separator;
        var affected = videos.Where(video => video.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
            video.Folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var changes = new List<CategoryGraphChange>();
        foreach (var video in affected)
        {
            var after = CategoryGraphState.ForPaths([target]);
            if (!video.Before.SameAs(after)) changes.Add(new(video.Video, video.Before, after));
        }
        var afterNames = categoryNames.ToList();
        if (!afterNames.Contains(target, StringComparer.OrdinalIgnoreCase)) afterNames.Add(target);
        return new CategoryGraphMovePlan
        {
            Kind = CategoryGraphMoveKind.Folder, Source = source, Target = target,
            DestinationRoot = target, BranchMode = CategoryBranchMoveMode.Merge,
            Changes = changes, NamesBefore = categoryNames, NamesAfter = afterNames.ToArray()
        };
    }

    public static CategoryGraphMovePlan ForBranch(IEnumerable<VideoItem> videos, LibraryConfig config,
        string source, string target, CategoryBranchMoveMode mode) =>
        ForBranchSnapshots(videos.Select(video => new CategoryGraphVideoSnapshot(video, video.Folder,
            CategoryGraphState.Capture(video, config))).ToArray(), config.CategoryNames.ToArray(), source, target, mode);

    public static CategoryGraphMovePlan ForBranchSnapshots(IReadOnlyList<CategoryGraphVideoSnapshot> videos,
        string[] categoryNames, string source, string target, CategoryBranchMoveMode mode)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) ||
            source is "全部" or "未分类" || target is "全部" or "未分类" ||
            Within(target, source)) throw new ArgumentException("不能把分类移动到自身或下级分类。", nameof(target));
        var leaf = source.Split(" / ", StringSplitOptions.RemoveEmptyEntries).Last();
        var destination = mode == CategoryBranchMoveMode.Nest ? target + " / " + leaf : target;
        var changes = new List<CategoryGraphChange>();
        foreach (var video in videos)
        {
            var categories = video.Categories;
            if (!categories.Any(path => Within(path, source))) continue;
            var moved = categories.Select(path => Within(path, source)
                ? destination + path[source.Length..] : path)
                .Where(path => !string.IsNullOrWhiteSpace(path) && path is not "未分类" and not "全部")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var before = video.Before;
            var after = CategoryGraphState.ForPaths(moved);
            if (!before.SameAs(after)) changes.Add(new CategoryGraphChange(video.Video, before, after));
        }
        var beforeNames = categoryNames;
        var afterNames = beforeNames.Select(path => Within(path, source)
                ? destination + path[source.Length..] : path)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!afterNames.Contains(destination, StringComparer.OrdinalIgnoreCase)) afterNames.Add(destination);
        return new CategoryGraphMovePlan
        {
            Kind = CategoryGraphMoveKind.Branch, Source = source, Target = target,
            DestinationRoot = destination, BranchMode = mode, Changes = changes,
            NamesBefore = beforeNames, NamesAfter = afterNames.ToArray()
        };
    }

    public bool MatchesCurrent(LibraryConfig config, bool after) =>
        SameNames(config.CategoryNames, after ? NamesAfter : NamesBefore) &&
        Changes.All(change => (after ? change.After : change.Before).Matches(change.Video, config));

    public bool MatchesSaved(LibraryConfig config, bool after) =>
        SameNames(config.CategoryNames, after ? NamesAfter : NamesBefore) &&
        Changes.All(change => (after ? change.After : change.Before).MatchesSaved(change.Video.Id, config));

    public void ApplyChange(CategoryGraphChange change, LibraryConfig config, bool after) =>
        (after ? change.After : change.Before).Apply(change.Video, config);

    public void ApplyNames(LibraryConfig config, bool after)
    {
        config.CategoryNames.Clear();
        config.CategoryNames.AddRange(after ? NamesAfter : NamesBefore);
    }

    public string MapSelection(string selected, bool after)
    {
        if (Kind != CategoryGraphMoveKind.Branch) return selected;
        var oldRoot = after ? Source : DestinationRoot;
        var newRoot = after ? DestinationRoot : Source;
        return Within(selected, oldRoot) ? newRoot + selected[oldRoot.Length..] : selected;
    }

    public string PreviewText()
    {
        var action = Kind switch
        {
            CategoryGraphMoveKind.Video => $"把视频“{Source}”移动到“{Target}”",
            CategoryGraphMoveKind.Folder => $"把文件夹“{Path.GetFileName(Source.TrimEnd('\\', '/'))}”及子文件夹中的视频移动到“{Target}”",
            _ => BranchMode == CategoryBranchMoveMode.Nest
                ? $"把分类“{Source}”及其下级移到“{Target}”下面，成为“{DestinationRoot}”"
                : $"把分类“{Source}”及其下级合并到“{Target}”"
        };
        var summary = $"{action}\n\n受影响视频：{Changes.Count:N0} 部\n分类名称：{NamesBefore.Length:N0} → {NamesAfter.Length:N0} 个";
        if (Kind is CategoryGraphMoveKind.Video or CategoryGraphMoveKind.Folder)
            summary += "\n原有视频分类将被目标分类替换。";
        else summary += "\n同一视频的其他分类会保留；下级分类按相同层级一起移动。";
        if (Changes.Count > 0)
            summary += "\n\n示例：\n" + string.Join("\n", Changes.Take(5).Select(change =>
                $"• {change.Video.DisplayTitle}: {string.Join("、", change.Before.Paths)} → {string.Join("、", change.After.Paths)}"));
        return summary;
    }

    public static bool VerifyForDiagnostics()
    {
        static VideoItem Video(string id, string folder, params string[] paths)
        {
            var video = new VideoItem { Id = id, Path = Path.Combine(folder, id + ".mp4"),
                Folder = folder, Title = id, Size = 1, Modified = DateTime.UnixEpoch };
            video.CategoryList.AddRange(paths);
            video.Category = paths.FirstOrDefault() ?? "未分类";
            return video;
        }
        var config = new LibraryConfig { CategoryNames = ["人物", "人物 / 作品", "人物 / 作品 / 下级", "其他"] };
        var one = Video("one", @"C:\media\actor", "人物 / 作品 / 下级", "其他");
        var two = Video("two", @"C:\media\actor-old", "人物 / 作品");
        foreach (var video in new[] { one, two })
        {
            config.Categories[video.Id] = video.Category;
            config.MultiCategories[video.Id] = video.CategoryList.ToList();
        }
        var nested = ForBranch([one, two], config, "人物 / 作品", "其他", CategoryBranchMoveMode.Nest);
        var captured = new[] { one, two }.Select(video => new CategoryGraphVideoSnapshot(video, video.Folder,
            CategoryGraphState.Capture(video, config))).ToArray();
        var backgroundPreview = Task.Run(() => ForBranchSnapshots(captured, config.CategoryNames.ToArray(),
            "人物 / 作品", "其他", CategoryBranchMoveMode.Nest)).GetAwaiter().GetResult();
        if (nested.DestinationRoot != "其他 / 作品" || nested.Changes.Count != 2 ||
            !one.CategoryList.Contains("人物 / 作品 / 下级") || !nested.PreviewText().Contains("2 部") ||
            !nested.NamesAfter.Contains("其他 / 作品 / 下级") ||
            !backgroundPreview.NamesAfter.SequenceEqual(nested.NamesAfter) ||
            backgroundPreview.Changes.Count != nested.Changes.Count) return false;
        foreach (var change in nested.Changes) nested.ApplyChange(change, config, after: true);
        nested.ApplyNames(config, after: true);
        if (!nested.MatchesCurrent(config, after: true) ||
            !one.CategoryList.SequenceEqual(["其他 / 作品 / 下级", "其他"]) ||
            nested.MapSelection("人物 / 作品 / 下级", after: true) != "其他 / 作品 / 下级") return false;
        config.MultiCategories[one.Id].Reverse(); // Shared config merging may reorder surviving classifications.
        if (!nested.MatchesSaved(config, after: true)) return false;
        config.MultiCategories[one.Id].Reverse();
        one.CategoryList.Add("人工修改");
        if (nested.MatchesCurrent(config, after: true)) return false;
        one.CategoryList.Remove("人工修改");
        foreach (var change in nested.Changes) nested.ApplyChange(change, config, after: false);
        nested.ApplyNames(config, after: false);
        if (!nested.MatchesCurrent(config, after: false)) return false;
        var merged = ForBranch([one, two], config, "人物 / 作品", "其他", CategoryBranchMoveMode.Merge);
        if (merged.Changes[0].After.Paths[0] != "其他 / 下级" ||
            merged.Changes[0].After.Paths.Length != 2) return false;
        try { ForBranch([one], config, "人物", "人物 / 作品", CategoryBranchMoveMode.Nest); return false; }
        catch (ArgumentException) { }
        var folder = ForFolder([one, two], config, @"C:\media\actor", "其他");
        if (folder.Changes.Count != 1 || ForFolder([one, two], config, @"C:\", "其他").Changes.Count != 2) return false;
        var replacement = ForVideo(one, config, "其他");
        if (!replacement.HasChanges || replacement.Changes.Count != 1 ||
            !one.CategoryList.Contains("人物 / 作品 / 下级")) return false;
        return true;
    }
}
