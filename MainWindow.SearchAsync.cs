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
        var copies = new (VideoItem Original, VideoItem Search)[eligible.Length];
        var sliceClock = Stopwatch.StartNew();
        lastSearchCaptureMaxSliceMs = 0;
        for (var i = 0; i < eligible.Length; i++)
        {
            var v = eligible[i];
            var copy = new VideoItem
            {
                Id = v.Id, Path = v.Path, Title = v.Title, Folder = v.Folder,
                Size = v.Size, Modified = v.Modified, Category = v.Category,
                Code = v.Code, WatchStatus = v.WatchStatus, Favorite = v.Favorite,
                DisplayTitle = v.DisplayTitle
            };
            copy.Tags.AddRange(v.Tags);
            copy.CategoryList.AddRange(v.CategoryList);
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
        var result = await Task.Run(() =>
        {
            var hits = new List<(VideoItem Original, VideoItem Search, double Score, string Reason)>();
            foreach (var pair in copies)
            {
                if (TryGetSearchMatch(pair.Search, query, out var score, out var reason) && score >= 0)
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
        // Replacing a populated ItemsSource tears down every card in one UI
        // turn. Retire the old page in small batches before binding the new one.
        var oldCards = categoryCardItems;
        lastSearchRetireMaxChunkMs = 0;
        lastSearchPopulateMaxChunkMs = 0;
        if (oldCards is not null && ReferenceEquals(Cards.ItemsSource, oldCards))
        {
            categoryCardRenderRequest++;
            categoryCardPopulationCompletion?.TrySetResult(false);
            while (oldCards.Count > 0)
            {
                var retireClock = Stopwatch.StartNew();
                for (var n = 0; n < 3 && oldCards.Count > 0; n++)
                    oldCards.RemoveAt(oldCards.Count - 1);
                lastSearchRetireMaxChunkMs = Math.Max(lastSearchRetireMaxChunkMs, retireClock.Elapsed.TotalMilliseconds);
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (isClosing || request != searchQueryVersion || render != searchRenderVersion ||
                    !ReferenceEquals(source, videos) || targetView != view ||
                    category != selectedCategory || tag != activeTagFilter ||
                    sort != SortBox.SelectedIndex || query != SearchBox.Text.Trim()) return;
            }
        }
        preparedSearch = new PreparedSearch(query, targetView, category, tag, sort, source,
            result.Items, result.HitCount, result.Fields);
        var applyClock = Stopwatch.StartNew();
        RenderCards();
        lastSearchApplyMs = applyClock.Elapsed.TotalMilliseconds;
    }
}
