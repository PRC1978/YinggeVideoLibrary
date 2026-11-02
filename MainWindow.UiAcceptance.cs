using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Threading;

namespace YinggeVideoLibrary;

/// <summary>
/// Offscreen UI measurements for the large-library performance line.
///
/// Everything here runs on the real window and the real production code paths;
/// only the data directory is substituted (AppPaths --data-dir) and the window
/// stays off screen. Nothing is written back to the library: the probe never
/// calls SaveConfig/SaveIndex and restores the in-memory view state before it
/// exits.
///
/// The numbers describe UI computation and rendering only. The synthetic library
/// has no decodable images and its files do not exist, so poster decoding and
/// disk I/O are excluded by construction and must not be read as a real-library
/// acceptance result.
/// </summary>
public partial class MainWindow
{
    // Same id derivation the library itself uses. The fixture keys config entries
    // by this id so the generated files match what ItemsFromIndex computes.
    internal static string FixtureIdFor(string path) => IdFor(path);

    // One warm-up call (excluded) plus five measured samples per query.
    const int UiDiagSearchSamples = 5;

    public async Task<int> WriteHiddenSearchDiagnosticAsync(string destination)
    {
        var report = new Dictionary<string, object?>(StringComparer.Ordinal);
        var totalClock = Stopwatch.StartNew();
        try
        {
            // Same settle rule as the hidden startup probe: wait for the cached
            // load and the background scan before timing anything.
            for (var attempt = 0; attempt < 240 &&
                cacheStartupDiagnostic.Stage is not ("refresh finished" or "index error" or "hidden startup probe error"); attempt++)
                await Task.Delay(250);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);

            report["dataDirectory"] = DataDir;
            report["libraryStage"] = cacheStartupDiagnostic.Stage;
            report["libraryVideoCount"] = videos.Count;
            report["configuredFolderCount"] = config.Folders.Count;
            report["cardPageSize"] = config.CardPageSize;
            report["startupListMode"] = config.ShowAll ? "完整列表" : "分页";
            report["note"] = "合成库：条目路径不存在、无缩略图；本组数字不含海报解码与磁盘 I/O。";

            report["search"] = await MeasureSearchAsync();
            report["cardRebind"] = await VerifyCardRebindAsync();
            report["pagination"] = await MeasurePaginationAsync();
            report["selection"] = await MeasureSelectionAsync();
            report["folderPage"] = await MeasureFolderPageAsync();

            // Restore the ordinary list state before the graph probe.
            config.ShowAll = false;
            pageIndex = 0;
            SearchBox.Text = "";
            searchRefreshTimer.Stop();
            ClearVideoSelection();
            ShowView("all");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);

            // Existing graph-index probe (32909 videos / 2068 categories). It is a
            // pure in-memory build, so it is run on a worker to keep the UI idle.
            report["graphIndexProbe"] = await Task.Run(() => VerifySphereIncrementalIndexForDiagnostics());

            var posterCache = VideoItem.PosterCacheStats();
            report["posterCache"] = new Dictionary<string, object?>
            {
                ["decodedBytes"] = posterCache.Bytes,
                ["entries"] = posterCache.Entries,
                ["hits"] = posterCache.Hits,
                ["misses"] = posterCache.Misses,
                ["evictions"] = posterCache.Evictions
            };
            report["processWorkingSetMiB"] = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576d, 1);
            report["gen2Collections"] = GC.CollectionCount(2);
            totalClock.Stop();
            report["diagnosticTotalMs"] = totalClock.ElapsedMilliseconds;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
            File.WriteAllText(destination, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            totalClock.Stop();
            report["diagnosticTotalMs"] = totalClock.ElapsedMilliseconds;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
                File.WriteAllText(destination, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch { }
            return 1;
        }
    }

    async Task<object> VerifyCardRebindAsync()
    {
        SearchBox.Text = "夜行城市";
        searchRefreshTimer.Stop();
        await RenderSearchAsync();
        if (categoryCardPopulationCompletion is { } firstPopulation)
            await firstPopulation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (pageItems.Count == 0) throw new InvalidOperationException("卡片复用诊断缺少第一组视频");

        var first = pageItems[0];
        var priorPoster = first.Poster;
        var priorSelection = first.IsSelected;
        var testPoster = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "YinggeCardRebind-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            // A tiny synthetic PNG proves that an old image and selection do
            // not remain on a reused card when the search changes its item.
            File.WriteAllBytes(testPoster, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/RkUAAAAASUVORK5CYII="));
            await Task.Run(() => VideoItem.PreloadPoster(testPoster));
            first.Poster = testPoster;
            first.IsSelected = true;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var firstCard = (System.Windows.FrameworkElement)Cards.Children[0];
            var firstImage = FindCardChild<System.Windows.Controls.Image>(firstCard);
            var firstCheck = FindCardChild<System.Windows.Controls.CheckBox>(firstCard);
            var oldVisualsSet = firstImage?.Source is not null && firstCheck?.IsChecked == true;

            SearchBox.Text = "Morning Light";
            searchRefreshTimer.Stop();
            await RenderSearchAsync();
            if (categoryCardPopulationCompletion is { } secondPopulation)
                await secondPopulation.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            if (pageItems.Count == 0) throw new InvalidOperationException("卡片复用诊断缺少第二组视频");
            var second = pageItems[0];
            var secondCard = (System.Windows.FrameworkElement)Cards.Children[0];
            var secondImage = FindCardChild<System.Windows.Controls.Image>(secondCard);
            var secondCheck = FindCardChild<System.Windows.Controls.CheckBox>(secondCard);
            return new Dictionary<string, object?>
            {
                ["oldVisualsSet"] = oldVisualsSet,
                ["sameCardReused"] = ReferenceEquals(firstCard, secondCard),
                ["differentVideo"] = first.Id != second.Id,
                ["oldPosterCleared"] = second.Poster is null && secondImage?.Source is null,
                ["oldSelectionCleared"] = !second.IsSelected && secondCheck?.IsChecked == false
            };
        }
        finally
        {
            first.Poster = priorPoster;
            first.IsSelected = priorSelection;
            try { File.Delete(testPoster); } catch { }
            SearchBox.Text = "";
            searchRefreshTimer.Stop();
            RenderCards();
        }
    }

    static T? FindCardChild<T>(System.Windows.DependencyObject root) where T : System.Windows.FrameworkElement
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindCardChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>
    /// Per-query synchronous cost of the production search path: RenderCards()
    /// performs the view filter, the field scan with bounded edit distance, the
    /// ranking sort and the page/card rebind in one UI-thread call. The layout
    /// pass that follows is timed separately because it is deferred work.
    /// </summary>
    async Task<object> MeasureSearchAsync()
    {
        var queries = new (string Label, string Query)[]
        {
            ("中文标题词", "夜行城市"),
            ("英文标题词", "Morning Light"),
            ("编号", "ABC-10042"),
            ("标签与状态组合", "标签:旅行 状态:未看"),
            ("分类与状态组合", "分类:电影 状态:未看"),
            ("三词模糊含拼写错误", "nigt city 2024"),
            ("零命中", "qzxv无匹配词")
        };
        var savedShowAll = config.ShowAll;
        var savedPageIndex = pageIndex;
        config.ShowAll = false;
        pageIndex = 0;
        var rows = new List<object>();
        try
        {
            foreach (var (label, query) in queries)
            {
                SearchBox.Text = query;
                searchRefreshTimer.Stop();
                RenderCards();                       // warm-up call for this query
                searchRefreshTimer.Stop();
                if (categoryCardPopulationCompletion is { } warmCards)
                    await warmCards.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);

                var renders = new List<double>();
                var layouts = new List<double>();
                var totals = new List<double>();
                var hits = new List<int>();
                for (var sample = 0; sample < UiDiagSearchSamples; sample++)
                {
                    SearchBox.Text = query;
                    searchRefreshTimer.Stop();
                    var renderClock = Stopwatch.StartNew();
                    RenderCards();
                    var renderMs = renderClock.Elapsed.TotalMilliseconds;
                    var hitCount = LastSearchHitCount;
                    var layoutClock = Stopwatch.StartNew();
                    if (categoryCardPopulationCompletion is { } cards)
                        await cards.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                    var layoutMs = layoutClock.Elapsed.TotalMilliseconds;
                    renders.Add(renderMs);
                    layouts.Add(layoutMs);
                    totals.Add(renderMs + layoutMs);
                    hits.Add(hitCount);
                    searchRefreshTimer.Stop();
                    await Task.Delay(20);
                }
                var renderSorted = renders.OrderBy(value => value).ToList();
                var layoutSorted = layouts.OrderBy(value => value).ToList();
                var totalSorted = totals.OrderBy(value => value).ToList();
                var expectedIds = currentFilteredVideos.Select(video => video.Id).ToArray();
                var lastPulse = Stopwatch.GetTimestamp();
                var maxUiGapMs = 0d;
                var pulse = new DispatcherTimer(DispatcherPriority.Send)
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                pulse.Tick += (_, _) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    maxUiGapMs = Math.Max(maxUiGapMs, Stopwatch.GetElapsedTime(lastPulse, now).TotalMilliseconds);
                    lastPulse = now;
                };
                pulse.Start();
                var asyncClock = Stopwatch.StartNew();
                try
                {
                    await RenderSearchAsync();
                    if (categoryCardPopulationCompletion is { } asyncCards)
                        await asyncCards.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    pulse.Stop();
                    await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                }
                finally { pulse.Stop(); }
                var asyncTotalMs = asyncClock.Elapsed.TotalMilliseconds;
                var sameResults = expectedIds.SequenceEqual(currentFilteredVideos.Select(video => video.Id));
                var visibleCount = Math.Min(currentFilteredVideos.Count, config.CardPageSize is 30 or 60 or 90 ? config.CardPageSize : 60);
                var sameVisualCards = Cards.VisibleCardCount == visibleCount &&
                    Cards.Children.Count >= visibleCount &&
                    Enumerable.Range(0, visibleCount).All(index =>
                        ReferenceEquals((Cards.Children[index] as System.Windows.FrameworkElement)?.DataContext, pageItems[index]));
                rows.Add(new Dictionary<string, object?>
                {
                    ["label"] = label,
                    ["query"] = query,
                    ["hits"] = hits[0],
                    ["filterRenderMs"] = renders.Select(value => Math.Round(value, 2)).ToList(),
                    ["filterRenderP50Ms"] = UiDiagPercentile(renderSorted, .5),
                    ["filterRenderP95Ms"] = UiDiagPercentile(renderSorted, .95),
                    ["filterRenderMaxMs"] = Math.Round(renderSorted[^1], 2),
                    ["filterRenderMeanMs"] = Math.Round(renders.Average(), 2),
                    ["layoutMs"] = layouts.Select(value => Math.Round(value, 2)).ToList(),
                    ["layoutP50Ms"] = UiDiagPercentile(layoutSorted, .5),
                    ["layoutP95Ms"] = UiDiagPercentile(layoutSorted, .95),
                    ["layoutMaxMs"] = Math.Round(layoutSorted[^1], 2),
                    ["syncTotalP50Ms"] = UiDiagPercentile(totalSorted, .5),
                    ["syncTotalP95Ms"] = UiDiagPercentile(totalSorted, .95),
                    ["syncTotalMaxMs"] = Math.Round(totalSorted[^1], 2),
                    ["asyncResultsEqual"] = sameResults,
                    ["asyncVisualCardsEqual"] = sameVisualCards,
                    ["asyncTotalMs"] = Math.Round(asyncTotalMs, 2),
                    ["asyncMaxUiGapMs"] = Math.Round(maxUiGapMs, 2),
                    ["asyncEligibilityMs"] = Math.Round(lastSearchEligibilityMs, 2),
                    ["asyncCaptureMaxSliceMs"] = Math.Round(lastSearchCaptureMaxSliceMs, 2),
                    ["asyncApplyMs"] = Math.Round(lastSearchApplyMs, 2)
                    , ["asyncRenderFilterMs"] = Math.Round(lastSearchRenderFilterMs, 2)
                    , ["asyncRenderBindingMs"] = Math.Round(lastSearchRenderBindingMs, 2)
                    , ["asyncRenderFinalMs"] = Math.Round(lastSearchRenderFinalMs, 2)
                    , ["asyncRetireMaxChunkMs"] = Math.Round(lastSearchRetireMaxChunkMs, 2)
                    , ["asyncPopulateMaxChunkMs"] = Math.Round(lastSearchPopulateMaxChunkMs, 2)
                });
            }
        }
        finally
        {
            config.ShowAll = savedShowAll;
            pageIndex = savedPageIndex;
            SearchBox.Text = "";
            searchRefreshTimer.Stop();
            RenderCards();
        }
        return new Dictionary<string, object?>
        {
            ["samplesPerQuery"] = UiDiagSearchSamples,
            ["warmUpExcluded"] = true,
            ["frameBudgetMs"] = 16,
            ["rows"] = rows
        };
    }

    /// <summary>
    /// Page-mode card rebind at the configured page size plus the complete-list
    /// (virtual row) rebuild, which is what a 3 万部 library pays on every filter
    /// change in the non-paged mode.
    /// </summary>
    async Task<object> MeasurePaginationAsync()
    {
        var savedShowAll = config.ShowAll;
        var savedPageIndex = pageIndex;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            SearchBox.Text = "";
            searchRefreshTimer.Stop();
            config.ShowAll = false;
            pageIndex = 0;
            RenderCards();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            result["filteredCount"] = currentFilteredVideos.Count;
            result["pageSize"] = config.CardPageSize;
            result["pageCardCount"] = pageItems.Count;

            var pageRenders = new List<double>();
            var pageLayouts = new List<double>();
            for (var page = 0; page < 3; page++)
            {
                pageIndex = page;
                var renderClock = Stopwatch.StartNew();
                RenderCards();
                pageRenders.Add(renderClock.Elapsed.TotalMilliseconds);
                var layoutClock = Stopwatch.StartNew();
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                pageLayouts.Add(layoutClock.Elapsed.TotalMilliseconds);
            }
            var pageSorted = pageRenders.OrderBy(value => value).ToList();
            result["pageSwitchMs"] = pageRenders.Select(value => Math.Round(value, 2)).ToList();
            result["pageSwitchP50Ms"] = UiDiagPercentile(pageSorted, .5);
            result["pageSwitchMaxMs"] = Math.Round(pageSorted[^1], 2);
            result["pageLayoutMs"] = pageLayouts.Select(value => Math.Round(value, 2)).ToList();
            result["pageItemsOnThirdPage"] = pageItems.Count;

            // The first full-list pass only establishes a real viewport width, so
            // the measured pass chunks rows exactly like a laid-out window does.
            config.ShowAll = true;
            pageIndex = 0;
            RenderCards();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var warmColumns = virtualColumns;
            var fullClock = Stopwatch.StartNew();
            RenderCards();
            var fullMs = fullClock.Elapsed.TotalMilliseconds;
            var fullLayoutClock = Stopwatch.StartNew();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            result["fullListRebuildMs"] = Math.Round(fullMs, 2);
            result["fullListLayoutMs"] = Math.Round(fullLayoutClock.Elapsed.TotalMilliseconds, 2);
            result["fullListColumnsAfterWarmUp"] = warmColumns;
            result["virtualRowCount"] = virtualRows.Count;
            result["virtualColumnCount"] = virtualColumns;
        }
        finally
        {
            config.ShowAll = savedShowAll;
            pageIndex = savedPageIndex;
            RenderCards();
        }
        return result;
    }

    /// <summary>
    /// Shift range selection across 1000 filtered items (17 pages at 60 per
    /// page) plus the 3 万部 select-all path, on the UI thread.
    /// </summary>
    async Task<object> MeasureSelectionAsync()
    {
        var savedShowAll = config.ShowAll;
        var savedPageIndex = pageIndex;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            SearchBox.Text = "";
            searchRefreshTimer.Stop();
            config.ShowAll = false;
            pageIndex = 0;
            RenderCards();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            ClearVideoSelection();
            var ordered = currentFilteredVideos;
            result["filteredCount"] = ordered.Count;
            result["requestedRange"] = 1000;
            if (ordered.Count >= 1000)
            {
                ToggleCardSelection(ordered[0]);
                var rangeClock = Stopwatch.StartNew();
                SelectVideoRange(ordered[999]);
                var rangeMs = rangeClock.Elapsed.TotalMilliseconds;
                var rangeLayoutClock = Stopwatch.StartNew();
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                result["rangeMs"] = Math.Round(rangeMs, 2);
                result["rangeLayoutMs"] = Math.Round(rangeLayoutClock.Elapsed.TotalMilliseconds, 2);
                result["selectedAfterRange"] = selectedVideoIds.Count;
                result["rangeCoversExpected"] = selectedVideoIds.Count == 1000;
                ClearVideoSelection();
            }
            var allClock = Stopwatch.StartNew();
            SelectAllLibraryVideos(this, new System.Windows.RoutedEventArgs());
            result["selectAllMs"] = Math.Round(allClock.Elapsed.TotalMilliseconds, 2);
            result["selectedAfterSelectAll"] = selectedVideoIds.Count;
            ClearVideoSelection();
            result["selectedAfterClear"] = selectedVideoIds.Count;
        }
        finally
        {
            ClearVideoSelection();
            config.ShowAll = savedShowAll;
            pageIndex = savedPageIndex;
            ShowView("all");
        }
        return result;
    }

    /// <summary>
    /// Folder page: tree build over the whole index, the directory listing with
    /// its own search/sort/paging, and the per-folder video listing.
    /// </summary>
    async Task<object> MeasureFolderPageAsync()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            var showClock = Stopwatch.StartNew();
            ShowView("folders");
            result["showViewFoldersMs"] = Math.Round(showClock.Elapsed.TotalMilliseconds, 2);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var workspaceClock = Stopwatch.StartNew();
            await RenderFolderWorkspaceAsync();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            result["treeBuildAndRenderMs"] = Math.Round(workspaceClock.Elapsed.TotalMilliseconds, 2);

            var tree = folderDisplayedTree;
            result["rootCount"] = tree?.RootNodes.Length ?? 0;
            result["indexedDirectoryCount"] = tree?.ByPath.Count ?? 0;
            result["indexedVideoCount"] = tree?.Source.Count ?? 0;

            var listingClock = Stopwatch.StartNew();
            UpdateFolderDirectoryListing();
            result["directoryListingMs"] = Math.Round(listingClock.Elapsed.TotalMilliseconds, 2);
            result["directoryRowsOnPage"] = folderResultsHost?.Children.Count ?? 0;
            var videoClock = Stopwatch.StartNew();
            UpdateFolderVideoListing();
            result["videoListingMs"] = Math.Round(videoClock.Elapsed.TotalMilliseconds, 2);

            folderSortMode = 1;
            var sortByCountClock = Stopwatch.StartNew();
            UpdateFolderDirectoryListing();
            result["sortByVideoCountMs"] = Math.Round(sortByCountClock.Elapsed.TotalMilliseconds, 2);
            folderSortMode = 2;
            var sortByRecentClock = Stopwatch.StartNew();
            UpdateFolderDirectoryListing();
            result["sortByNewestMs"] = Math.Round(sortByRecentClock.Elapsed.TotalMilliseconds, 2);
            folderSortMode = 0;

            folderSearch = "作品集-1";
            var searchClock = Stopwatch.StartNew();
            UpdateFolderDirectoryListing();
            result["directorySearchMs"] = Math.Round(searchClock.Elapsed.TotalMilliseconds, 2);
            result["directorySearchRowsOnPage"] = folderResultsHost?.Children.Count ?? 0;
            folderSearch = "";

            folderDirectoryPage = 1;
            var pageClock = Stopwatch.StartNew();
            UpdateFolderDirectoryListing();
            result["secondDirectoryPageMs"] = Math.Round(pageClock.Elapsed.TotalMilliseconds, 2);
            folderDirectoryPage = 0;

            if (tree is { RootNodes.Length: > 0 })
            {
                folderCurrentRoot = tree.RootNodes[0].Path;
                folderCurrentPath = tree.RootNodes[0].Path;
                var drillClock = Stopwatch.StartNew();
                BuildFolderWorkspace(tree);
                result["openRootPageMs"] = Math.Round(drillClock.Elapsed.TotalMilliseconds, 2);
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                result["rootChildCount"] = tree.RootNodes[0].Children.Count;
                result["rootDirectoryRowsOnPage"] = folderResultsHost?.Children.Count ?? 0;
            }
        }
        finally
        {
            folderCurrentRoot = null;
            folderCurrentPath = null;
            folderSearch = "";
            folderDirectoryPage = 0;
            folderVideoPage = 0;
            ShowView("all");
        }
        return result;
    }

    static double UiDiagPercentile(List<double> sortedValues, double fraction)
    {
        if (sortedValues.Count == 0) return 0;
        var index = (int)Math.Ceiling(sortedValues.Count * fraction) - 1;
        return Math.Round(sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)], 2);
    }
}
