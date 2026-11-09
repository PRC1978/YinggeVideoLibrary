using System.Diagnostics;
using System.Windows.Threading;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    sealed record PreparedSearch(string Query, string View, string Category, string? Tag,
        int SortIndex, List<VideoItem> Source, List<VideoItem> Items, int HitCount, string[] Fields);

    int searchQueryVersion;
    int searchRenderVersion;
    PreparedSearch? preparedSearch;
    double lastSearchEligibilityMs;
    double lastSearchCaptureMaxSliceMs;
    double lastSearchApplyMs;
    double lastSearchRenderFilterMs;
    double lastSearchRenderBindingMs;
    double lastSearchRenderFinalMs;
    double lastSearchRetireMaxChunkMs;
    double lastSearchPopulateMaxChunkMs;
    List<VideoItem>? searchCopySource;
    int searchCopySourceCount;
    readonly Dictionary<VideoItem, VideoItem> searchCopies = new(ReferenceEqualityComparer.Instance);

    static bool SearchCopyIsCurrent(VideoItem original, VideoItem copy)
    {
        if (original.Id != copy.Id || original.Path != copy.Path ||
            original.Title != copy.Title || original.Folder != copy.Folder ||
            original.Size != copy.Size || original.Modified != copy.Modified ||
            original.Category != copy.Category || original.Code != copy.Code ||
            original.WatchStatus != copy.WatchStatus || original.Favorite != copy.Favorite ||
            original.DisplayTitle != copy.DisplayTitle ||
            original.Tags.Count != copy.Tags.Count ||
            original.CategoryList.Count != copy.CategoryList.Count) return false;
        for (var i = 0; i < original.Tags.Count; i++)
            if (original.Tags[i] != copy.Tags[i]) return false;
        for (var i = 0; i < original.CategoryList.Count; i++)
            if (original.CategoryList[i] != copy.CategoryList[i]) return false;
        return true;
    }

    static VideoItem MakeSearchCopy(VideoItem original)
    {
        var copy = new VideoItem
        {
            Id = original.Id, Path = original.Path, Title = original.Title,
            Folder = original.Folder, Size = original.Size, Modified = original.Modified,
            Category = original.Category, Code = original.Code,
            WatchStatus = original.WatchStatus, Favorite = original.Favorite,
            DisplayTitle = original.DisplayTitle
        };
        copy.Tags.AddRange(original.Tags);
        copy.CategoryList.AddRange(original.CategoryList);
        return copy;
    }

    // Capture mutable card data on the UI thread in bounded slices. The worker
    // then reads detached copies, so edits made while searching cannot race
    // with List<T> enumeration or WPF property notifications.
    async Task RenderSearchAsync()
    {
        if (isClosing || SearchBox is null || SortBox is null) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) { RenderCards(); return; }
        var request = searchQueryVersion;
        var render = searchRenderVersion;
        var source = videos;
        var targetView = view;
        var category = selectedCategory;
        var tag = activeTagFilter;
        var sort = SortBox.SelectedIndex;
        var eligibilityClock = Stopwatch.StartNew();
        var eligible = source.Where(v =>
            (tag is null || v.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase)) &&
            (targetView != "favorites" || v.Favorite) &&
            (targetView != "categories" || category == "全部" ||
             v.CategoryList.Any(c => c.Equals(category, StringComparison.OrdinalIgnoreCase) ||
                                     c.StartsWith(category + " / ", StringComparison.OrdinalIgnoreCase)) ||
             v.Category.Equals(category, StringComparison.OrdinalIgnoreCase))).ToArray();
        lastSearchEligibilityMs = eligibilityClock.Elapsed.TotalMilliseconds;
        // Snapshots are immutable once handed to the worker. Validate every
        // mutable search field before reuse so edits are visible on the next
        // query without racing the worker or relying on UI notifications.
        if (!ReferenceEquals(searchCopySource, source) || searchCopySourceCount != source.Count ||
            searchCopies.Count > source.Count * 2)
        {
            searchCopies.Clear();
            searchCopySource = source;
            searchCopySourceCount = source.Count;
        }
        var copies = new (VideoItem Original, VideoItem Search)[eligible.Length];
        var sliceClock = Stopwatch.StartNew();
        lastSearchCaptureMaxSliceMs = 0;
        for (var i = 0; i < eligible.Length; i++)
        {
            var v = eligible[i];
            if (!searchCopies.TryGetValue(v, out var copy) || !SearchCopyIsCurrent(v, copy))
            {
                copy = MakeSearchCopy(v);
                searchCopies[v] = copy;
            }
            copies[i] = (v, copy);
            if ((i & 127) == 127)
            {
                lastSearchCaptureMaxSliceMs = Math.Max(lastSearchCaptureMaxSliceMs, sliceClock.Elapsed.TotalMilliseconds);
                await Dispatcher.Yield(DispatcherPriority.Background);
                sliceClock.Restart();
                if (isClosing || request != searchQueryVersion || render != searchRenderVersion ||
                    !ReferenceEquals(source, videos) || targetView != view) return;
            }
        }
        // Parse once per request. In a large library parsing the same query for
        // every video creates tens of thousands of short-lived arrays.
        var parts = query.Split([' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await Task.Run(() =>
        {
            var hits = new List<(VideoItem Original, VideoItem Search, double Score, string Reason)>();
            foreach (var pair in copies)
            {
                if (TryGetSearchMatch(pair.Search, parts, out var score, out var reason) && score >= 0)
                    hits.Add((pair.Original, pair.Search, score, reason));
            }
            var ranked = hits.OrderByDescending(hit => hit.Score);
            IOrderedEnumerable<(VideoItem Original, VideoItem Search, double Score, string Reason)> sorted = sort switch
            {
                1 => ranked.ThenBy(hit => hit.Search.Title, StringComparer.CurrentCultureIgnoreCase),
                2 => ranked.ThenBy(hit => hit.Search.Modified),
                3 => ranked.ThenByDescending(hit => hit.Search.Size),
                _ => ranked.ThenByDescending(hit => hit.Search.Modified)
            };
            return (Items: sorted.Select(hit => hit.Original).ToList(),
                HitCount: hits.Count,
                Fields: hits.Select(hit => hit.Reason).Where(reason => reason.Length > 0)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(3).ToArray());
        });
        if (isClosing || request != searchQueryVersion || render != searchRenderVersion ||
            !ReferenceEquals(source, videos) || targetView != view ||
            category != selectedCategory || tag != activeTagFilter ||
            sort != SortBox.SelectedIndex || query != SearchBox.Text.Trim()) return;
        // RenderCards keeps the existing page collection and rebinds its cards
        // in small chunks. Retiring all visuals first causes needless layout
        // passes and a blank page while the new search result arrives.
        lastSearchRetireMaxChunkMs = 0;
        lastSearchPopulateMaxChunkMs = 0;
        preparedSearch = new PreparedSearch(query, targetView, category, tag, sort, source,
            result.Items, result.HitCount, result.Fields);
        var applyClock = Stopwatch.StartNew();
        RenderCards();
        lastSearchApplyMs = applyClock.Elapsed.TotalMilliseconds;
    }
}
