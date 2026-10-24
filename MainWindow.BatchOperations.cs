using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    enum BatchChangeKind { Category, Tags, Status, Favorite }

    sealed class BatchUndoEntry
    {
        public required VideoItem Video { get; init; }
        public string? Category { get; init; }
        public string[]? CategoryList { get; init; }
        public string? SavedCategory { get; init; }
        public string[]? SavedMultiCategories { get; init; }
        public string[]? Tags { get; init; }
        public string[]? SavedTags { get; init; }
        public string? WatchStatus { get; init; }
        public string? SavedStatus { get; init; }
        public bool Favorite { get; init; }
        public bool SavedFavorite { get; init; }
        public bool SavedAutoAmateur { get; init; }
    }

    sealed class BatchUndoRecord
    {
        public required BatchChangeKind Kind { get; init; }
        public required string Label { get; init; }
        public required List<BatchUndoEntry> Entries { get; init; }
        public string? AddedCategoryName { get; set; }
    }

    const int BatchChunkSize = 256;
    bool batchOperationInProgress;
    string batchProgressText = "";
    BatchUndoRecord? lastBatchUndo;

    static BatchUndoEntry CaptureBatchState(VideoItem video, LibraryConfig state, BatchChangeKind kind,
        HashSet<string>? favorites = null, HashSet<string>? autoAmateur = null)
    {
        return kind switch
        {
            BatchChangeKind.Category => new BatchUndoEntry
            {
                Video = video,
                Category = video.Category,
                CategoryList = video.CategoryList.ToArray(),
                SavedCategory = state.Categories.GetValueOrDefault(video.Id),
                SavedMultiCategories = state.MultiCategories.TryGetValue(video.Id, out var values) ? values.ToArray() : null
            },
            BatchChangeKind.Tags => new BatchUndoEntry
            {
                Video = video,
                Tags = video.Tags.ToArray(),
                SavedTags = state.Tags.TryGetValue(video.Id, out var values) ? values.ToArray() : null,
                SavedAutoAmateur = autoAmateur?.Contains(video.Id) ?? false
            },
            BatchChangeKind.Status => new BatchUndoEntry
            {
                Video = video,
                WatchStatus = video.WatchStatus,
                SavedStatus = state.WatchStates.GetValueOrDefault(video.Id)
            },
            _ => new BatchUndoEntry
            {
                Video = video,
                Favorite = video.Favorite,
                SavedFavorite = favorites?.Contains(video.Id) ?? false
            }
        };
    }

    static void RestoreBatchState(BatchUndoEntry entry, LibraryConfig state, BatchChangeKind kind,
        HashSet<string>? favorites = null, HashSet<string>? autoAmateur = null)
    {
        var video = entry.Video;
        switch (kind)
        {
            case BatchChangeKind.Category:
                video.CategoryList.Clear();
                video.CategoryList.AddRange(entry.CategoryList!);
                video.Category = entry.Category!;
                if (entry.SavedCategory is null) state.Categories.Remove(video.Id);
                else state.Categories[video.Id] = entry.SavedCategory;
                if (entry.SavedMultiCategories is null) state.MultiCategories.Remove(video.Id);
                else state.MultiCategories[video.Id] = entry.SavedMultiCategories.ToList();
                break;
            case BatchChangeKind.Tags:
                video.Tags.Clear();
                video.Tags.AddRange(entry.Tags!);
                if (entry.SavedTags is null) state.Tags.Remove(video.Id);
                else state.Tags[video.Id] = entry.SavedTags.ToList();
                if (entry.SavedAutoAmateur) autoAmateur!.Add(video.Id);
                else autoAmateur!.Remove(video.Id);
                break;
            case BatchChangeKind.Status:
                video.WatchStatus = entry.WatchStatus!;
                if (entry.SavedStatus is null) state.WatchStates.Remove(video.Id);
                else state.WatchStates[video.Id] = entry.SavedStatus;
                break;
            case BatchChangeKind.Favorite:
                video.Favorite = entry.Favorite;
                if (entry.SavedFavorite) favorites!.Add(video.Id);
                else favorites!.Remove(video.Id);
                break;
        }
    }

    static void ApplyBatchCategoryValue(VideoItem video, LibraryConfig state, string target, bool replace)
    {
        var categories = new List<string>(video.CategoryList.Count + 1);
        if (!replace)
            foreach (var category in CategoriesOf(video))
                if (!string.IsNullOrWhiteSpace(category) && category is not "未分类" and not "全部" &&
                    !categories.Contains(category, StringComparer.CurrentCultureIgnoreCase)) categories.Add(category);
        if (target is not "未分类" and not "全部" && !categories.Contains(target, StringComparer.CurrentCultureIgnoreCase)) categories.Add(target);
        video.CategoryList.Clear();
        video.CategoryList.AddRange(categories);
        video.Category = categories.FirstOrDefault() ?? "未分类";
        if (video.Category == "未分类") state.Categories.Remove(video.Id); else state.Categories[video.Id] = video.Category;
        if (categories.Count == 0) state.MultiCategories.Remove(video.Id); else state.MultiCategories[video.Id] = categories.ToList();
    }

    static void ApplyBatchTagValue(VideoItem video, LibraryConfig state, IReadOnlyList<string> tags,
        HashSet<string> lookup, HashSet<string> autoAmateur, bool remove, bool replace, bool clear)
    {
        if (clear || replace) video.Tags.Clear();
        if (!clear)
        {
            if (remove) video.Tags.RemoveAll(tag => lookup.Contains(tag));
            else foreach (var tag in tags)
                if (!video.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase)) video.Tags.Add(tag);
        }
        if (!video.Tags.Contains("素人", StringComparer.CurrentCultureIgnoreCase)) autoAmateur.Remove(video.Id);
        if (video.Tags.Count == 0) state.Tags.Remove(video.Id); else state.Tags[video.Id] = video.Tags.ToList();
    }

    static void ApplyBatchStatusValue(VideoItem video, LibraryConfig state, string status)
    {
        video.WatchStatus = status;
        if (status == "未看") state.WatchStates.Remove(video.Id); else state.WatchStates[video.Id] = status;
    }

    static void ApplyBatchFavoriteValue(VideoItem video, HashSet<string> favorites, bool? value)
    {
        video.Favorite = value ?? !video.Favorite;
        if (video.Favorite) favorites.Add(video.Id); else favorites.Remove(video.Id);
    }

    // Rebuild a membership list once, preserving old order where possible.
    // Removing one ID at a time from a 30,000-item list was quadratic.
    static List<string> RebuildMembershipList(IReadOnlyList<string> oldValues, HashSet<string> members,
        IEnumerable<string> touchedIds)
    {
        var ordered = new List<string>(members.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in oldValues)
            if (members.Contains(id) && seen.Add(id)) ordered.Add(id);
        foreach (var id in touchedIds)
            if (members.Contains(id) && seen.Add(id)) ordered.Add(id);
        return ordered;
    }

    static void KeepConcurrentMembershipChanges(HashSet<string> original, IReadOnlyList<string> current,
        HashSet<string> updated, IReadOnlyList<VideoItem> touchedVideos)
    {
        var touched = new HashSet<string>(touchedVideos.Select(video => video.Id), StringComparer.OrdinalIgnoreCase);
        var latest = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var id in original)
            if (!touched.Contains(id) && !latest.Contains(id)) updated.Remove(id);
        foreach (var id in latest)
            if (!touched.Contains(id) && !original.Contains(id)) updated.Add(id);
    }

    static async Task<long> ProcessBatchChunksAsync<T>(IReadOnlyList<T> items, Action<T> apply, Action<int>? progress = null)
    {
        long longestChunkMs = 0;
        for (var start = 0; start < items.Count; start += BatchChunkSize)
        {
            var watch = Stopwatch.StartNew();
            var end = Math.Min(start + BatchChunkSize, items.Count);
            for (var index = start; index < end; index++) apply(items[index]);
            longestChunkMs = Math.Max(longestChunkMs, watch.ElapsedMilliseconds);
            progress?.Invoke(end);
            if (end < items.Count) await Dispatcher.Yield(DispatcherPriority.Background);
        }
        return longestChunkMs;
    }

    async Task SaveBatchConfigAsync()
    {
        categoryDataVersion++;
        var version = categoryDataVersion;
        var savedCurrent = CloneLibraryConfig(config);
        var savedBaseline = CloneLibraryConfig(configBaseline);
        var scrubCredentials = credentialStoreReady;
        var scrubJavinfo = javinfoCredentialReady;
        var result = await Task.Run(() =>
        {
            var merged = WriteConfigSnapshot(savedBaseline, savedCurrent, ConfigPath, scrubCredentials, scrubJavinfo);
            return (Merged: merged, Baseline: CloneLibraryConfig(merged));
        });
        // Normal case: no other setting changed during the disk write. The
        // expensive merge and deep copy have already completed off the UI thread.
        if (version == categoryDataVersion)
        {
            result.Merged.TmdbToken = config.TmdbToken;
            result.Merged.AiKey = config.AiKey;
            result.Merged.JavinfoKey = config.JavinfoKey;
            config = result.Merged;
            configBaseline = result.Baseline;
        }
        else
        {
            // Another save ran while this one was in flight. Reconcile its
            // changes against the actual newest file on a worker. This matters
            // when the other writer finished *after* our background write.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var observedVersion = categoryDataVersion;
                var current = CloneLibraryConfig(config);
                var updated = await Task.Run(() =>
                {
                    if (!TryReadJson(ConfigPath, out LibraryConfig? latest) || latest is null)
                        throw new InvalidDataException("批量保存后无法读取共享配置");
                    var reconciled = MergeLibraryConfig(savedCurrent, current, latest);
                    reconciled.TmdbToken = current.TmdbToken;
                    reconciled.AiKey = current.AiKey;
                    reconciled.JavinfoKey = current.JavinfoKey;
                    latest.TmdbToken = current.TmdbToken;
                    latest.AiKey = current.AiKey;
                    latest.JavinfoKey = current.JavinfoKey;
                    return (Reconciled: reconciled, Baseline: CloneLibraryConfig(latest));
                });
                if (observedVersion != categoryDataVersion) continue;
                config = updated.Reconciled;
                configBaseline = updated.Baseline;
                break;
            }
        }
        configSource = "config.json";
    }

    async Task ExecuteBatchOperationAsync(IReadOnlyList<VideoItem> selected, BatchChangeKind kind, string label,
        Action<VideoItem> apply, Action<BatchUndoRecord>? finish = null,
        HashSet<string>? favorites = null, HashSet<string>? autoAmateur = null)
    {
        if (selected.Count == 0 || batchOperationInProgress) return;
        batchOperationInProgress = true;
        var undo = new BatchUndoRecord { Kind = kind, Label = label, Entries = new List<BatchUndoEntry>(selected.Count) };
        lastBatchUndo = undo;
        batchProgressText = $"正在{label} 0 / {selected.Count:N0}…";
        UpdateSelectionToolbar();
        try
        {
            var longest = await ProcessBatchChunksAsync(selected, video =>
            {
                undo.Entries.Add(CaptureBatchState(video, config, kind, favorites, autoAmateur));
                apply(video);
            }, done => SelectionSummary.Text = $"正在{label} {done:N0} / {selected.Count:N0}…");
            finish?.Invoke(undo);
            batchProgressText = $"正在保存 {selected.Count:N0} 部视频…";
            SelectionSummary.Text = batchProgressText;
            await Dispatcher.Yield(DispatcherPriority.Render);
            await SaveBatchConfigAsync();
            if (view is "all" or "recent" or "favorites" or "categories") RenderCards();
            Log($"{label}完成：{selected.Count:N0} 部；最长处理片段 {longest} ms");
        }
        catch (Exception error)
        {
            Log($"{label}失败，当前更改可撤销：{error.Message}");
            System.Windows.MessageBox.Show(this, $"{label}未能完成或保存。当前修改仍显示在视频库中，可点击“撤销批量操作”。\n{error.Message}", "映格视频库");
        }
        finally
        {
            batchOperationInProgress = false;
            batchProgressText = "";
            UpdateSelectionToolbar();
        }
    }

    async void UndoLastBatchAction(object sender, System.Windows.RoutedEventArgs e)
    {
        if (batchOperationInProgress || lastBatchUndo is null) return;
        var undo = lastBatchUndo;
        batchOperationInProgress = true;
        batchProgressText = $"正在撤销 {undo.Entries.Count:N0} 部视频…";
        UpdateSelectionToolbar();
        var favorites = undo.Kind == BatchChangeKind.Favorite
            ? new HashSet<string>(config.Favorites, StringComparer.OrdinalIgnoreCase) : null;
        var autoAmateur = undo.Kind == BatchChangeKind.Tags
            ? new HashSet<string>(config.AutoAmateurTags, StringComparer.OrdinalIgnoreCase) : null;
        try
        {
            await ProcessBatchChunksAsync(undo.Entries, item => RestoreBatchState(item, config, undo.Kind, favorites, autoAmateur),
                done => SelectionSummary.Text = $"正在撤销 {done:N0} / {undo.Entries.Count:N0}…");
            if (favorites is not null)
                config.Favorites = RebuildMembershipList(config.Favorites, favorites, undo.Entries.Select(entry => entry.Video.Id));
            if (autoAmateur is not null)
                config.AutoAmateurTags = RebuildMembershipList(config.AutoAmateurTags, autoAmateur, undo.Entries.Select(entry => entry.Video.Id));
            if (undo.AddedCategoryName is { } name && !videos.Any(video => video.CategoryList.Contains(name, StringComparer.CurrentCultureIgnoreCase)))
                config.CategoryNames.RemoveAll(value => value.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            batchProgressText = "正在保存撤销结果…";
            SelectionSummary.Text = batchProgressText;
            await Dispatcher.Yield(DispatcherPriority.Render);
            await SaveBatchConfigAsync();
            lastBatchUndo = null;
            if (view is "all" or "recent" or "favorites" or "categories") RenderCards();
            Log($"已撤销“{undo.Label}”：{undo.Entries.Count:N0} 部视频");
        }
        catch (Exception error)
        {
            Log($"撤销保存失败：{error.Message}");
            System.Windows.MessageBox.Show(this, $"撤销结果未能保存。请重试。\n{error.Message}", "映格视频库");
        }
        finally
        {
            batchOperationInProgress = false;
            batchProgressText = "";
            UpdateSelectionToolbar();
        }
    }

    // The diagnostic runs against synthetic items and a unique temporary
    // config. It never changes the real library, selection, or credentials.
    static async Task<string> VerifyLargeBatchOffscreenAsync()
    {
        const int count = 32_909;
        var items = Enumerable.Range(0, count).Select(index => new VideoItem
        {
            Id = $"batch-probe-{index:D5}",
            Path = $@"C:\batch-probe\{index:D5}.mp4",
            Title = $"probe-{index:D5}",
            Folder = @"C:\batch-probe",
            Size = 1024,
            Modified = DateTime.UnixEpoch
        }).ToList();
        var scratch = new LibraryConfig();
        var renderPulses = 0;
        var pulse = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1) };
        pulse.Tick += (_, _) => renderPulses++;
        pulse.Start();
        var timer = Stopwatch.StartNew();
        var longest = 0L;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"yingge-batch-probe-{Guid.NewGuid():N}");
        try
        {
            var categoryUndo = new List<BatchUndoEntry>(count);
            longest = Math.Max(longest, await ProcessBatchChunksAsync(items, video =>
            {
                categoryUndo.Add(CaptureBatchState(video, scratch, BatchChangeKind.Category));
                ApplyBatchCategoryValue(video, scratch, "Probe", false);
            }));
            if (scratch.Categories.Count != count) throw new InvalidOperationException("批量分类没有覆盖完整视频库");
            longest = Math.Max(longest, await ProcessBatchChunksAsync(categoryUndo,
                entry => RestoreBatchState(entry, scratch, BatchChangeKind.Category)));
            if (scratch.Categories.Count != 0 || scratch.MultiCategories.Count != 0)
                throw new InvalidOperationException("批量分类撤销未恢复原状态");

            var autoAmateur = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tags = new[] { "probe-tag" };
            var tagLookup = new HashSet<string>(tags, StringComparer.CurrentCultureIgnoreCase);
            longest = Math.Max(longest, await ProcessBatchChunksAsync(items,
                video => ApplyBatchTagValue(video, scratch, tags, tagLookup, autoAmateur, false, false, false)));
            longest = Math.Max(longest, await ProcessBatchChunksAsync(items,
                video => ApplyBatchStatusValue(video, scratch, "已看")));
            if (scratch.Tags.Count != count || scratch.WatchStates.Count != count)
                throw new InvalidOperationException("批量标签或状态漏掉分页外视频");

            scratch.Favorites = items.Select(video => video.Id).ToList();
            foreach (var video in items) video.Favorite = true;
            var favorites = new HashSet<string>(scratch.Favorites, StringComparer.OrdinalIgnoreCase);
            longest = Math.Max(longest, await ProcessBatchChunksAsync(items,
                video => ApplyBatchFavoriteValue(video, favorites, false)));
            scratch.Favorites = RebuildMembershipList(scratch.Favorites, favorites, items.Select(video => video.Id));
            if (scratch.Favorites.Count != 0 || items.Any(video => video.Favorite))
                throw new InvalidOperationException("批量取消收藏未覆盖完整视频库");

            var sampleVideo = items[0];
            var tagUndo = CaptureBatchState(sampleVideo, scratch, BatchChangeKind.Tags, autoAmateur: autoAmateur);
            ApplyBatchTagValue(sampleVideo, scratch, [], new HashSet<string>(), autoAmateur, false, true, true);
            RestoreBatchState(tagUndo, scratch, BatchChangeKind.Tags, autoAmateur: autoAmateur);
            var statusUndo = CaptureBatchState(sampleVideo, scratch, BatchChangeKind.Status);
            ApplyBatchStatusValue(sampleVideo, scratch, "未看");
            RestoreBatchState(statusUndo, scratch, BatchChangeKind.Status);
            var favoriteUndo = CaptureBatchState(sampleVideo, scratch, BatchChangeKind.Favorite, favorites: favorites);
            ApplyBatchFavoriteValue(sampleVideo, favorites, true);
            RestoreBatchState(favoriteUndo, scratch, BatchChangeKind.Favorite, favorites: favorites);
            if (sampleVideo.Tags.Count != 1 || sampleVideo.Tags[0] != "probe-tag" ||
                sampleVideo.WatchStatus != "已看" || sampleVideo.Favorite || favorites.Contains(sampleVideo.Id))
                throw new InvalidOperationException("标签、状态或收藏撤销未恢复原状态");

            Directory.CreateDirectory(temporaryDirectory);
            var path = Path.Combine(temporaryDirectory, "config.json");
            var cloneTimer = Stopwatch.StartNew();
            var savedCurrent = CloneLibraryConfig(scratch);
            var savedBaseline = CloneLibraryConfig(new LibraryConfig());
            var cloneMs = cloneTimer.ElapsedMilliseconds;
            var persisted = await Task.Run(() => WriteConfigSnapshot(savedBaseline, savedCurrent, path, true, true));
            var savedCount = await Task.Run(() => TryReadJson(path, out LibraryConfig? saved) ? saved?.Tags.Count ?? -1 : -1);
            if (persisted.Tags.Count != count || persisted.WatchStates.Count != count || savedCount != count)
                throw new InvalidOperationException("后台保存的批量修改不完整");
            if (renderPulses == 0 || longest > 1000)
                throw new InvalidOperationException($"批量处理阻塞了界面：最长片段 {longest} ms，绘制机会 {renderPulses}");
            var batchMs = timer.ElapsedMilliseconds;

            // Compare the old per-video List.RemoveAll path and the new set
            // update on the same 4,000 IDs without touching product data.
            var sample = items.Take(4000).Select(video => video.Id).ToArray();
            var oldFavorites = sample.ToList();
            var oldTimer = Stopwatch.StartNew();
            foreach (var id in sample) oldFavorites.RemoveAll(value => value.Equals(id, StringComparison.OrdinalIgnoreCase));
            var oldMs = oldTimer.ElapsedMilliseconds;
            var newFavorites = new HashSet<string>(sample, StringComparer.OrdinalIgnoreCase);
            var newTimer = Stopwatch.StartNew();
            foreach (var id in sample) newFavorites.Remove(id);
            var rebuilt = RebuildMembershipList(sample, newFavorites, sample);
            var newMs = newTimer.ElapsedMilliseconds;
            if (oldFavorites.Count != 0 || rebuilt.Count != 0) throw new InvalidOperationException("收藏对照结果不一致");
            return $"{count:N0} 部分类/分类撤销/标签/状态/收藏及后台保存 {batchMs} ms；UI 配置快照 {cloneMs} ms，最长 UI 片段 {longest} ms，绘制机会 {renderPulses}；4,000 部收藏旧循环 {oldMs} ms、集合写回 {newMs} ms；临时配置已校验";
        }
        finally
        {
            pulse.Stop();
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true); } catch { }
        }
    }

    public static async Task<int> WriteHeadlessBatchDiagnosticAsync(string destination)
    {
        string result;
        var success = true;
        try { result = await VerifyLargeBatchOffscreenAsync(); }
        catch (Exception error) { result = error.ToString(); success = false; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new
            {
                Success = success,
                Result = result,
                GeneratedAt = DateTimeOffset.Now,
                UsedRealLibraryData = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { return 1; }
        return success ? 0 : 1;
    }
}
