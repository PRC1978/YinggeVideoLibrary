using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

sealed class CacheStartupDiagnostic
{
    public DateTimeOffset UpdatedAt { get; set; }
    public int ProcessId { get; set; } = Environment.ProcessId;
    public string Executable { get; set; } = Environment.ProcessPath ?? "";
    public DateTime ExecutableModifiedAtUtc { get; set; } = Environment.ProcessPath is { } executable && File.Exists(executable)
        ? File.GetLastWriteTimeUtc(executable) : DateTime.MinValue;
    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;
    public string WindowsUser { get; set; } = $"{Environment.UserDomainName}\\{Environment.UserName}";
    public string DataDirectory { get; set; } = "";
    public string ConfigSource { get; set; } = "";
    public string IndexSource { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public int ConfiguredFolderCount { get; set; }
    public int AvailableFolderCount { get; set; }
    public string[] UnavailableFolders { get; set; } = [];
    public int IndexedVideoCount { get; set; }
    public int DisplayedVideoCount { get; set; }
    public List<string> History { get; set; } = [];
}

public partial class MainWindow
{
    // Read the same paths and index parser as normal startup without creating
    // a window. This lets the published single-file EXE be checked from any
    // working folder while it stays entirely in the background.
    public static int WriteHeadlessCacheDiagnostic(string? destination)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var migration = TryMigrateLegacyCaches(DataDir);
            var config = LoadConfig(out var configSource);
            var roots = NormalizeRoots(config.Folders);
            var report = new CacheStartupDiagnostic
            {
                UpdatedAt = DateTimeOffset.Now,
                DataDirectory = DataDir,
                ConfigSource = configSource,
                ConfiguredFolderCount = roots.Length,
                AvailableFolderCount = roots.Count(Directory.Exists),
                UnavailableFolders = roots.Where(root => !Directory.Exists(root)).ToArray(),
                Stage = "headless cache probe",
                Message = migration
            };
            var (index, indexSource) = ReadBestLibraryIndex(config);
            report.IndexSource = indexSource;
            if (index is null) report.Message = string.IsNullOrWhiteSpace(migration)
                ? "共享索引不存在或无法读取" : migration + "；共享索引不存在或无法读取";
            else
            {
                report.IndexedVideoCount = index.Entries.Count;
                if (roots.Length == 0 && !config.FoldersClearedByUser)
                    roots = NormalizeRoots(index.Folders);
                report.ConfiguredFolderCount = roots.Length;
                report.AvailableFolderCount = roots.Count(Directory.Exists);
                report.UnavailableFolders = roots.Where(root => !Directory.Exists(root)).ToArray();
                if (!NormalizeRoots(index.Folders).SequenceEqual(roots, StringComparer.OrdinalIgnoreCase))
                {
                    report.DisplayedVideoCount = ItemsFromIndex(index, out var ignored)
                        .Count(item => roots.Any(root => IsDescendantPath(root, item.Path)));
                    report.Message = report.DisplayedVideoCount > 0
                        ? $"文件夹设置变化，可先载入 {report.DisplayedVideoCount} 部视频；跳过 {ignored} 条无效记录"
                        : "索引与配置的文件夹不一致";
                }
                else
                {
                    report.DisplayedVideoCount = ItemsFromIndex(index, out var invalid).Count;
                    report.Message = $"可载入 {report.DisplayedVideoCount} 部视频；跳过 {invalid} 条无效记录";
                }
                if (!string.IsNullOrWhiteSpace(migration)) report.Message = migration + "；" + report.Message;
            }
            destination ??= Path.Combine(DataDir, "diagnostics", $"probe-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllText(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch { return 1; }
    }

    readonly CacheStartupDiagnostic cacheStartupDiagnostic = new();
    readonly string cacheDiagnosticPath = Path.Combine(
        DataDir, "diagnostics", $"startup-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.json");

    void RecordCacheDiagnostic(string stage, string message, int? indexedCount = null, IReadOnlyCollection<string>? unavailable = null)
    {
        try
        {
            cacheStartupDiagnostic.UpdatedAt = DateTimeOffset.Now;
            cacheStartupDiagnostic.DataDirectory = DataDir;
            cacheStartupDiagnostic.ConfigSource = configSource;
            cacheStartupDiagnostic.IndexSource = indexSource;
            cacheStartupDiagnostic.Stage = stage;
            cacheStartupDiagnostic.Message = message;
            var roots = NormalizeRoots(config.Folders);
            cacheStartupDiagnostic.ConfiguredFolderCount = roots.Length;
            cacheStartupDiagnostic.AvailableFolderCount = roots.Count(Directory.Exists);
            cacheStartupDiagnostic.UnavailableFolders = unavailable?.ToArray() ?? roots.Where(root => !Directory.Exists(root)).ToArray();
            if (indexedCount.HasValue) cacheStartupDiagnostic.IndexedVideoCount = indexedCount.Value;
            cacheStartupDiagnostic.DisplayedVideoCount = videos.Count;
            cacheStartupDiagnostic.History.Add($"{DateTimeOffset.Now:HH:mm:ss.fff} {stage}: config={roots.Length}, cleared={config.FoldersClearedByUser}, videos={videos.Count}, index={cacheStartupDiagnostic.IndexedVideoCount}, source={configSource}/{indexSource}; {message}");
            if (cacheStartupDiagnostic.History.Count > 24) cacheStartupDiagnostic.History.RemoveAt(0);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheDiagnosticPath)!);
            var temporary = cacheDiagnosticPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(cacheStartupDiagnostic, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, cacheDiagnosticPath, true);
        }
        catch { /* Diagnostics must never prevent a library from opening. */ }
    }

    string CacheDiagnosticSummary => $"数据目录：{DataDir}\n启动诊断：{cacheDiagnosticPath}\n本次读取：{cacheStartupDiagnostic.Message}";

    public async Task<int> WriteHeadlessUiDiagnosticAsync(string destination)
    {
        try
        {
            RecordCacheDiagnostic("ui probe before cache", "未显示窗口的界面启动检查");
            var loaded = await LoadCachedAsync();
            RecordCacheDiagnostic("ui probe after cache", $"缓存命中：{loaded}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllText(destination, JsonSerializer.Serialize(cacheStartupDiagnostic, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            RecordCacheDiagnostic("ui probe exception", ex.ToString());
            return 1;
        }
    }

    public async Task<int> WriteHiddenStartupDiagnosticAsync(string destination, bool verifyGraph = false, bool verifyScrape = false, bool verifySettings = false, bool verifySelection = false, bool verifyFolders = false, bool verifyRecommendations = false, bool verifyRoles = false, int scrollSeconds = 0)
    {
        for (var attempt = 0; attempt < 120 && cacheStartupDiagnostic.Stage is not ("refresh finished" or "index error" or "ui startup exception"); attempt++)
            await Task.Delay(250);
        try
        {
            var expectedStartupView = NormalizeStartupPage(config.StartPage);
            if (view != expectedStartupView)
                throw new InvalidOperationException($"启动页没有按设置显示：预期 {expectedStartupView}，实际 {view}");
            RecordCacheDiagnostic("startup page verified", expectedStartupView);
            if (verifyFolders)
                RecordCacheDiagnostic("folder ui verified", await VerifyFolderWorkspaceOffscreenAsync());
            if (verifyRecommendations)
                RecordCacheDiagnostic("recommendations verified", await VerifyRecommendationsOffscreenAsync());
            if (verifyRoles)
                RecordCacheDiagnostic("role profiles ui verified", VerifyRoleProfilesOffscreen());
            if (verifySelection)
            {
                ShowView("all");
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                if (videos.Count == 0 || !SelectionBar.IsVisible || SelectionBar.Parent is not System.Windows.Controls.Grid
                    || !BrowseTagsAction.IsVisible)
                    throw new InvalidOperationException("选择栏未显示在完整列表上方，或视频索引为空");
                activeTagFilter = "diagnostic-filter";
                UpdateTagFilterControls();
                if (!ClearTagFilterAction.IsVisible)
                    throw new InvalidOperationException("完整列表下缺少清除标签筛选入口");
                activeTagFilter = null;
                UpdateTagFilterControls();
                var allCount = videos.Count;
                var allStarted = System.Diagnostics.Stopwatch.StartNew();
                SelectAllLibraryVideos(this, new System.Windows.RoutedEventArgs());
                var allMs = allStarted.ElapsedMilliseconds;
                if (selectedVideoIds.Count != allCount || videos.Any(item => !item.IsSelected))
                    throw new InvalidOperationException("全选全部视频未涵盖分页外的视频");
                ClearVideoSelection();
                var savedFiltered = currentFilteredVideos;
                try
                {
                    currentFilteredVideos = videos.Take(Math.Min(17, videos.Count)).ToList();
                    SelectFilteredVideos(this, new System.Windows.RoutedEventArgs());
                    if (selectedVideoIds.Count != currentFilteredVideos.Count)
                        throw new InvalidOperationException("全选筛选结果错误地选择了其他视频");
                }
                finally
                {
                    ClearVideoSelection();
                    currentFilteredVideos = savedFiltered;
                }
                var savedShowAll = config.ShowAll;
                var savedPageIndex = pageIndex;
                try
                {
                    config.ShowAll = true;
                    pageIndex = 0;
                    RenderCards();
                    var ordered = currentFilteredVideos;
                    if (ordered.Count >= 6)
                    {
                        if (VirtualCards.ItemsSource is null || AllListPanel.Visibility != System.Windows.Visibility.Visible)
                            throw new InvalidOperationException("完整列表没有使用虚拟化视频卡片");
                        ToggleCardSelection(ordered[1]);
                        SelectVideoRange(ordered[5]);
                        if (selectedVideoIds.Count != 5 || ordered.Skip(1).Take(5).Any(item => !item.IsSelected)
                            || selectionAnchorId != ordered[1].Id)
                            throw new InvalidOperationException("完整列表 Shift 连续选择或锚点错误");
                        SelectVideoRange(ordered[0]);
                        if (selectedVideoIds.Count != 6 || !ordered[0].IsSelected)
                            throw new InvalidOperationException("反向 Shift 连续选择错误");
                        ClearVideoSelection();
                    }

                    config.ShowAll = false;
                    pageIndex = 0;
                    RenderCards();
                    ordered = currentFilteredVideos;
                    if (ordered.Count >= 6)
                    {
                        if (Cards.ItemsSource is null || pageItems.Count == 0)
                            throw new InvalidOperationException("分页列表没有视频卡片");
                        var diagnosticPageSize = config.CardPageSize is 30 or 60 or 90 ? config.CardPageSize : 60;
                        if (ordered.Count > diagnosticPageSize * 2)
                        {
                            PageJumpBox.Text = "3";
                            JumpToPage();
                            if (pageIndex != 2 || pageItems.Count == 0 || pageItems[0].Id != ordered[diagnosticPageSize * 2].Id)
                                throw new InvalidOperationException("页码跳转没有定位到正确的视频");
                            PageJumpBox.Text = "1";
                            JumpToPage();
                            if (pageIndex != 0) throw new InvalidOperationException("页码跳转无法返回第一页");
                        }
                        var endIndex = Math.Min(ordered.Count - 1, Math.Max(5, config.CardPageSize + 2));
                        ToggleCardSelection(ordered[1]);
                        if (endIndex >= pageItems.Count)
                        {
                            pageIndex = 1;
                            RenderCards();
                            if (!pageItems.Any(item => item.Id == ordered[endIndex].Id))
                                throw new InvalidOperationException("跨页目标视频没有显示在下一页");
                        }
                        SelectVideoRange(ordered[endIndex]);
                        if (selectedVideoIds.Count != endIndex || ordered.Skip(1).Take(endIndex).Any(item => !item.IsSelected))
                            throw new InvalidOperationException("分页列表跨页 Shift 连续选择错误");
                        ClearVideoSelection();
                        ToggleCardSelection(ordered[0]);
                        currentFilteredVideos = ordered.Skip(2).Take(3).ToList();
                        SelectVideoRange(ordered[3]);
                        if (selectedVideoIds.Count != 2 || selectionAnchorId != ordered[3].Id || !ordered[3].IsSelected)
                            throw new InvalidOperationException("筛选变化后 Shift 选择没有重建锚点");
                        ClearVideoSelection();
                        RecordCacheDiagnostic("shift range verified", $"完整列表和分页结果均按筛选排序连续选择；跨页终点 {endIndex}；筛选变化时重建锚点；未写入用户数据");
                    }
                }
                finally
                {
                    ClearVideoSelection();
                    config.ShowAll = savedShowAll;
                    pageIndex = savedPageIndex;
                    RenderCards();
                }
                var one = videos[0];
                var toggles = System.Diagnostics.Stopwatch.StartNew();
                for (var index = 0; index < 200; index++)
                {
                    one.IsSelected = true;
                    selectedVideoIds.Add(one.Id);
                    UpdateSelectionToolbar();
                    one.IsSelected = false;
                    selectedVideoIds.Remove(one.Id);
                    UpdateSelectionToolbar();
                }
                var toggleMs = toggles.ElapsedMilliseconds;
                if (selectedVideoIds.Count != 0 || videos.Any(item => item.IsSelected))
                    throw new InvalidOperationException("清空选择后仍有残留选择");
                RecordCacheDiagnostic("selection ui verified", $"全库 {allCount:N0} 部均可选择；筛选结果可单独全选；全库耗时 {allMs} ms，400 次单卡切换 {toggleMs} ms；未写入用户数据");
                RecordCacheDiagnostic("batch picker verified", await VerifyBatchCategoryPickerOffscreenAsync());
                RecordCacheDiagnostic("batch operation verified", await VerifyLargeBatchOffscreenAsync());
            }
            if (scrollSeconds > 0)
            {
                var originalShowAll = config.ShowAll;
                RecycledCardRow.BeginScrollDiagnostic();
                try
                {
                    config.ShowAll = true;
                    ShowView("all");
                    await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                    var scroller = FindVisualDescendant<System.Windows.Controls.ScrollViewer>(VirtualCards)
                        ?? throw new InvalidOperationException("完整列表的虚拟滚动器未创建");
                    if (virtualRows.Count < 10 || scroller.ScrollableHeight < 5)
                        throw new InvalidOperationException("无法在真实列表上执行连续滚动检查");
                    var times = new List<double>();
                    var offsetTimes = new List<double>();
                    var layoutTimes = new List<double>();
                    var overall = System.Diagnostics.Stopwatch.StartNew();
                    long peakWorkingSet = 0;
                    var startingWorkingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
                    var step = 0;
                    while (overall.Elapsed < TimeSpan.FromSeconds(scrollSeconds))
                    {
                        var measured = System.Diagnostics.Stopwatch.StartNew();
                        var maxOffset = Math.Max(1d, scroller.ScrollableHeight);
                        scroller.ScrollToVerticalOffset((step * 3d) % maxOffset);
                        offsetTimes.Add(measured.Elapsed.TotalMilliseconds);
                        var layoutStarted = System.Diagnostics.Stopwatch.StartNew();
                        await Dispatcher.InvokeAsync(scroller.UpdateLayout,
                            System.Windows.Threading.DispatcherPriority.Render);
                        layoutTimes.Add(layoutStarted.Elapsed.TotalMilliseconds);
                        measured.Stop();
                        times.Add(measured.Elapsed.TotalMilliseconds);
                        peakWorkingSet = Math.Max(peakWorkingSet,
                            System.Diagnostics.Process.GetCurrentProcess().WorkingSet64);
                        step++;
                        await Task.Delay(80);
                    }
                    var over100Ms = times.Count(value => value > 100);
                    var longestOver100MsRun = 0;
                    var currentOver100MsRun = 0;
                    foreach (var value in times)
                    {
                        currentOver100MsRun = value > 100 ? currentOver100MsRun + 1 : 0;
                        longestOver100MsRun = Math.Max(longestOver100MsRun, currentOver100MsRun);
                    }
                    var endingWorkingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
                    times.Sort();
                    offsetTimes.Sort();
                    layoutTimes.Sort();
                    var p95 = times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * .95) - 1)];
                    var p99 = times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * .99) - 1)];
                    var offsetP95 = offsetTimes[Math.Min(offsetTimes.Count - 1, (int)Math.Ceiling(offsetTimes.Count * .95) - 1)];
                    var layoutP95 = layoutTimes[Math.Min(layoutTimes.Count - 1, (int)Math.Ceiling(layoutTimes.Count * .95) - 1)];
                    var cache = VideoItem.PosterCacheStats();
                    var rowStats = RecycledCardRow.EndScrollDiagnostic();
                    RecordCacheDiagnostic("scroll performance verified",
                        $"离屏连续滚动 {scrollSeconds} 秒，{times.Count} 次；UI 片段 P95 {p95:F1} ms、P99 {p99:F1} ms、最慢 {times[^1]:F1} ms；滚动调用 P95 {offsetP95:F1} ms、布局绘制等待 P95 {layoutP95:F1} ms；"
                        + $"超过 100 ms {over100Ms} 次、最长连续 {longestOver100MsRun} 次；工作集起点/终点/峰值 {startingWorkingSet / 1048576d:F1}/{endingWorkingSet / 1048576d:F1}/{peakWorkingSet / 1048576d:F1} MiB，海报解码缓存 {cache.Bytes / 1048576d:F1} MiB / {(cache.LimitBytes == 0 ? "无限制" : $"{cache.LimitBytes / 1048576d:F0} MiB")}，淘汰 {cache.Evictions}；{rowStats}");
                }
                finally
                {
                    RecycledCardRow.EndScrollDiagnostic();
                    config.ShowAll = originalShowAll;
                    ShowView("all");
                }
            }
            if (verifyGraph)
            {
                var graphStarted = System.Diagnostics.Stopwatch.StartNew();
                ShowView("categories");
                var graphShowMs = graphStarted.Elapsed.TotalMilliseconds;
                await WaitForSphereGraphBuildAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var graphWaitMs = graphStarted.Elapsed.TotalMilliseconds - graphShowMs;
                var graphLayoutQueuedAt = graphStarted.Elapsed.TotalMilliseconds;
                var graphLayoutStartedAt = 0d;
                var graphLayoutExecutedMs = 0d;
                await Dispatcher.InvokeAsync(() =>
                {
                    graphLayoutStartedAt = graphStarted.Elapsed.TotalMilliseconds;
                    var layoutClock = System.Diagnostics.Stopwatch.StartNew();
                    UpdateLayout();
                    graphLayoutExecutedMs = layoutClock.Elapsed.TotalMilliseconds;
                }, System.Windows.Threading.DispatcherPriority.Render);
                var graphLayoutMs = graphStarted.Elapsed.TotalMilliseconds - graphShowMs - graphWaitMs;
                var graphBuildMs = graphStarted.ElapsedMilliseconds;
                var viewport = sphereGraphViewport ?? throw new InvalidOperationException("分类图视口未创建");
                var sphere = sphereMapSurface ?? throw new InvalidOperationException("分类图绘制层未创建");
                var cardInputLatencies = new List<double>();
                while (categoryCardPopulationCompletion is { Task.IsCompleted: false } &&
                       graphStarted.Elapsed < TimeSpan.FromSeconds(15))
                {
                    var inputClock = System.Diagnostics.Stopwatch.StartNew();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        sphere.Rotation += .015;
                        sphere.InvalidateVisual();
                        _ = sphere.HitPath(new System.Windows.Point(660, 280));
                    }, System.Windows.Threading.DispatcherPriority.Input);
                    cardInputLatencies.Add(inputClock.Elapsed.TotalMilliseconds);
                    await Task.Delay(32);
                }
                sphere.Rotation = 0;
                sphere.InvalidateVisual();
                if (categoryCardPopulationCompletion is { } cardCompletion &&
                    !await cardCompletion.Task.WaitAsync(TimeSpan.FromSeconds(15)))
                    throw new InvalidOperationException("分类页卡片分批加载被意外取消");
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                var cardReadyMs = graphStarted.ElapsedMilliseconds;
                var generatedCardContainers = Enumerable.Range(0, Cards.Items.Count)
                    .Count(position => Cards.ItemContainerGenerator.ContainerFromIndex(position) is not null);
                if (Cards.Items.Count != pageItems.Count || generatedCardContainers != pageItems.Count)
                    throw new InvalidOperationException($"分类页卡片未全部实现：数据 {Cards.Items.Count}、视觉容器 {generatedCardContainers}/{pageItems.Count}");
                var graphIndex = GetSphereGraphIndex();
                // Derive the expected paths from the source library, not from
                // the graph's dictionaries: a linguistic key comparer can
                // otherwise merge two Unicode paths and still pass all the
                // graph-versus-navigator count checks below.
                var expectedCategoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in config.CategoryNames)
                    foreach (var ancestor in CategoryAncestors(path)) expectedCategoryPaths.Add(ancestor);
                foreach (var video in videos)
                    foreach (var path in CategoriesOf(video))
                        foreach (var ancestor in CategoryAncestors(path)) expectedCategoryPaths.Add(ancestor);
                if (!expectedCategoryPaths.SetEquals(graphIndex.AllCategoryCounts.Keys))
                {
                    var missing = expectedCategoryPaths.Except(graphIndex.AllCategoryCounts.Keys, StringComparer.OrdinalIgnoreCase).Count();
                    var unexpected = graphIndex.AllCategoryCounts.Keys.Except(expectedCategoryPaths, StringComparer.OrdinalIgnoreCase).Count();
                    throw new InvalidOperationException($"分类源数据与图索引不一致：源数据 {expectedCategoryPaths.Count} 个、图索引 {graphIndex.AllCategoryCounts.Count} 个，缺失 {missing}、多出 {unexpected}");
                }
                if (sphere.NodeCount == 0 || viewport.ActualWidth < 100 || viewport.ActualHeight < 100)
                    throw new InvalidOperationException("分类图节点或可视区域未完成布局");
                if (sphere.NodeCount != graphIndex.AllCategoryCounts.Count + graphIndex.TagCounts.Count ||
                    !sphere.EveryNodeLinked || sphereGraphTimer.IsEnabled || sphereGraphCanvas?.Children.Count != 1)
                    throw new InvalidOperationException("分类图节点关系不完整，或未使用单层合并绘制");
                var mostConnectedTag = graphIndex.CategoriesByTag.OrderByDescending(pair => pair.Value.Count)
                    .FirstOrDefault(pair => pair.Value.Count > 0);
                if (mostConnectedTag.Key is not null)
                {
                    sphere.SetHover("\u0001" + mostConnectedTag.Key);
                    if (sphere.FocusAssociationCount != mostConnectedTag.Value.Count)
                        throw new InvalidOperationException("标签与分类的真实关联未全部绘出");
                    sphere.SetHover(null);
                }
                var firstTag = graphIndex.RelatedTags.Where(pair => pair.Value.Count > 0)
                    .OrderByDescending(pair => pair.Value.Count).ThenByDescending(pair => graphIndex.TagCounts[pair.Key])
                    .Select(pair => pair.Key).FirstOrDefault()
                    ?? graphIndex.TagCounts.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
                var tagNodes = 0;
                var tagSwitchMs = 0L;
                var selectedTagRelations = 0;
                if (firstTag is not null)
                {
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    NavigateSphereNode("\u0001" + firstTag);
                    await WaitForSphereGraphBuildAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                    tagSwitchMs = started.ElapsedMilliseconds;
                    sphere = sphereMapSurface ?? throw new InvalidOperationException("标签切换后绘制层未创建");
                    var expected = graphIndex.TagCounts[firstTag];
                    var expectedNodes = graphIndex.CategoriesByTag[firstTag].Count + graphIndex.RelatedTags[firstTag].Count;
                    if (sphere.RootCount != expected || sphere.NodeCount != expectedNodes ||
                        !sphere.EveryNodeLinked || sphereGraphCanvas?.Children.Count != 1)
                        throw new InvalidOperationException("按标签筛选后关联节点或连线不完整");
                    tagNodes = sphere.NodeCount;
                    selectedTagRelations = sphere.FocusAssociationCount;
                    if (selectedTagRelations != graphIndex.CategoriesByTag[firstTag].Count)
                        throw new InvalidOperationException($"所选标签与全部相关分类的直接连接不完整：{selectedTagRelations}/{graphIndex.CategoriesByTag[firstTag].Count}，悬停={sphere.HoverPath}，选择={sphere.SelectedPath}");
                    var firstRelated = graphIndex.RelatedTags[firstTag].Keys.FirstOrDefault();
                    if (firstRelated is not null)
                    {
                        sphere.SetHover("\u0001" + firstRelated);
                        if (sphere.FocusAssociationCount == 0)
                            throw new InvalidOperationException("所选标签下的真实共现关系未画出");
                        sphere.SetHover(null);
                    }
                    if (started.Elapsed > TimeSpan.FromSeconds(8))
                        throw new InvalidOperationException($"按标签重建图耗时过长：{started.Elapsed.TotalSeconds:F1} 秒");
                    SaveSpherePreview(sphere, Path.ChangeExtension(destination, ".tag.png"));
                    sphereTagFilter = "";
                    lastCategoryRenderVersion = -1;
                    RenderCategories();
                    await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                    sphere = sphereMapSurface ?? throw new InvalidOperationException("恢复全部标签后绘制层未创建");
                    viewport = sphereGraphViewport ?? throw new InvalidOperationException("恢复全部标签后视口未创建");
                }
                // Exercise every navigator page. The 24-row page limit must
                // never truncate the nodes or relationships on the sphere.
                var graphPanel = ((System.Windows.Controls.Border)CategoryPanel.Children[^1]).Child as System.Windows.Controls.StackPanel
                    ?? throw new InvalidOperationException("分类图布局未创建");
                var navPanel = graphPanel.Children[^1] as System.Windows.Controls.StackPanel
                    ?? throw new InvalidOperationException("分类节点导航未创建");
                var navBar = navPanel.Children[0] as System.Windows.Controls.DockPanel
                    ?? throw new InvalidOperationException("分类节点导航栏未创建");
                var pager = navBar.Children[0] as System.Windows.Controls.StackPanel
                    ?? throw new InvalidOperationException("分类节点分页未创建");
                var nextPage = pager.Children[2] as System.Windows.Controls.Button
                    ?? throw new InvalidOperationException("分类节点下一页按钮未创建");
                var navList = navPanel.Children[1] as System.Windows.Controls.ListBox
                    ?? throw new InvalidOperationException("分类节点列表未创建");
                // Category identity is ordinal, matching the graph's node
                // dictionary. Linguistic comparison can merge distinct paths.
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var navigationPages = 0;
                while (true)
                {
                    navigationPages++;
                    foreach (var item in navList.ItemsSource.OfType<SphereNavItem>()) seenPaths.Add(item.Path);
                    if (!nextPage.IsEnabled) break;
                    if (navigationPages > sphere.NodeCount) throw new InvalidOperationException("分类节点分页未能结束");
                    nextPage.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }
                var expectedNavigationPaths = new HashSet<string>(graphIndex.AllCategoryCounts.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var tag in graphIndex.TagCounts.Keys) expectedNavigationPaths.Add("\u0001" + tag);
                if (seenPaths.Count != sphere.NodeCount || !seenPaths.SetEquals(expectedNavigationPaths))
                    throw new InvalidOperationException($"分类图有 {sphere.NodeCount} 个节点，但分页导航仅能访问 {seenPaths.Count} 个；图索引期望 {expectedNavigationPaths.Count} 个不同路径");
                ZoomSphereGraph(1.2);
                if (sphereZoom <= 1) throw new InvalidOperationException("分类图放大操作失败");
                ResetSphereGraphView();
                var oldX = sphereGraphTranslation?.X ?? throw new InvalidOperationException("分类图平移变换未创建");
                spherePanOffset = new System.Windows.Vector(40, 25);
                UpdateSphereViewport();
                if (Math.Abs(sphereGraphTranslation.X - oldX - 40) > .5)
                    throw new InvalidOperationException("分类图平移操作失败");
                ResetSphereGraphView();
                var oldHeight = viewport.ActualHeight;
                sphereManualViewportHeight = oldHeight + 80;
                viewport.Height = sphereManualViewportHeight.Value;
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                if (viewport.ActualHeight <= oldHeight)
                    throw new InvalidOperationException("分类图高度调整失败");
                // Render several actual rotation frames off screen. This
                // measures the painting work that a dense library performs
                // while spinning without showing a window to the user.
                var frameBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)sphere.Width, (int)sphere.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var frameTimes = new List<double>();
                for (var frame = 0; frame < 12; frame++)
                {
                    var frameClock = System.Diagnostics.Stopwatch.StartNew();
                    sphere.Rotation = frame * .025;
                    sphere.InvalidateVisual();
                    frameBitmap.Render(sphere);
                    frameTimes.Add(frameClock.Elapsed.TotalMilliseconds);
                }
                var focusedFrameTimes = new List<double>();
                if (mostConnectedTag.Key is not null)
                {
                    sphere.SetHover("\u0001" + mostConnectedTag.Key);
                    for (var frame = 0; frame < 8; frame++)
                    {
                        var frameClock = System.Diagnostics.Stopwatch.StartNew();
                        sphere.Rotation = .31 + frame * .025;
                        sphere.InvalidateVisual();
                        frameBitmap.Render(sphere);
                        focusedFrameTimes.Add(frameClock.Elapsed.TotalMilliseconds);
                    }
                    sphere.SetHover(null);
                }
                sphere.Rotation = .48;
                sphere.InvalidateVisual();
                SaveSpherePreview(sphere, Path.ChangeExtension(destination, ".rotated.png"));
                sphere.Rotation = 0;
                sphere.InvalidateVisual();
                var previewStarted = System.Diagnostics.Stopwatch.StartNew();
                SaveSpherePreview(sphere, Path.ChangeExtension(destination, ".png"));
                var previewMs = previewStarted.ElapsedMilliseconds;
                var frameP95 = frameTimes.OrderBy(value => value).ElementAt(Math.Min(frameTimes.Count - 1, (int)Math.Ceiling(frameTimes.Count * .95) - 1));
                var cardInputP95 = cardInputLatencies.Count == 0 ? 0 : cardInputLatencies.OrderBy(value => value).ElementAt(Math.Min(cardInputLatencies.Count - 1, (int)Math.Ceiling(cardInputLatencies.Count * .95) - 1));
                RecordCacheDiagnostic("graph verified", $"全部相关节点 {sphere.NodeCount}，选中标签关联节点 {tagNodes}，该标签实际共现连线 {selectedTagRelations}，全部节点经 {navigationPages} 页可访问；视口 {viewport.ActualWidth:F0}×{viewport.ActualHeight:F0}；首次构建 {graphBuildMs} ms（切页 {graphShowMs:F1}、等待后台 {graphWaitMs:F1}、布局 {graphLayoutMs:F1} ms [调度 {graphLayoutStartedAt - graphLayoutQueuedAt:F1}、执行 {graphLayoutExecutedMs:F1}]），{pageItems.Count} 张本页卡片及 {generatedCardContainers} 个视觉容器全部到位 {cardReadyMs} ms，加载期间 {cardInputLatencies.Count} 次输入/旋转反馈平均 {(cardInputLatencies.Count == 0 ? 0 : cardInputLatencies.Average()):F1} ms、P95 {cardInputP95:F1} ms，标签切换 {tagSwitchMs} ms，12 帧离屏旋转平均 {frameTimes.Average():F1} ms、P95 {frameP95:F1} ms、最慢 {frameTimes.Max():F1} ms，最密标签 {mostConnectedTag.Value?.Count ?? 0} 条关联聚焦平均 {(focusedFrameTimes.Count == 0 ? 0 : focusedFrameTimes.Average()):F1} ms，离屏 PNG 保存 {previewMs} ms；索引预热 {sphereIndexWarmProfile}；分段：{categoryRenderProfile}；{sphereGraphBuildProfile}；缩放、平移、调高正常");
            }
            if (verifyScrape)
            {
                ShowView("scrape");
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                if (ScrapeBatchButton.Parent is not System.Windows.Controls.WrapPanel)
                    throw new InvalidOperationException("批量搜刮按钮未移动到搜刮页");
                if (ScrapePanel.Child is not System.Windows.Controls.StackPanel scrapePanel
                    || !scrapePanel.Children.OfType<System.Windows.Controls.WrapPanel>()
                        .SelectMany(panel => panel.Children.OfType<System.Windows.Controls.Button>())
                        .Any(button => Equals(button.Content, "完整资料搜刮")))
                    throw new InvalidOperationException("完整资料搜刮入口未显示");
                if (scrapeExcludedList is null || scrapeKeywordBox is null || scrapeSkipWatchedBox is null)
                    throw new InvalidOperationException("搜刮筛选控件未创建");
                if (ReviewQueuePanel.Children.Count > ReviewPageSize)
                    throw new InvalidOperationException("审核队列一次创建了过多记录控件");
                var cloneProbe = new LibraryConfig();
                cloneProbe.AiReviews["metadata-probe"] = new AiReviewRecord
                {
                    VideoId = "metadata-probe",
                    CandidateDetails = new MovieMetadata { Title = "测试", ReleaseDate = "2026-01-01", Actors = ["甲"] }
                };
                if (CloneLibraryConfig(cloneProbe).AiReviews["metadata-probe"].CandidateDetails?.Actors.SingleOrDefault() != "甲")
                    throw new InvalidOperationException("配置快照丢失搜刮详情");
                var approvedRecord = config.AiReviews.Values.FirstOrDefault(item => item.Status == "approved");
                if (approvedRecord is not null && !ReviewMatchesFilter(approvedRecord))
                    throw new InvalidOperationException("状态：全部未包含已通过记录");
                var navTop = OnlineNav.TranslatePoint(new System.Windows.Point(0, 0), Sidebar).Y;
                var settingsTop = SettingsNav.TranslatePoint(new System.Windows.Point(0, 0), Sidebar).Y;
                if (navTop + OnlineNav.ActualHeight > settingsTop)
                    throw new InvalidOperationException("在线按钮被底部设置按钮遮挡");
                RecordCacheDiagnostic("scrape ui verified", $"批量入口与跳过条件已显示；审核队列当前页 {ReviewQueuePanel.Children.Count} 条；在线按钮底部 {navTop + OnlineNav.ActualHeight:F0}，设置按钮顶部 {settingsTop:F0}");
            }
            if (verifySettings)
            {
                ShowView("settings");
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                var sections = CountVisualDescendants<System.Windows.Controls.Expander>(SettingsPanel);
                if (SettingsPanel.Visibility != System.Windows.Visibility.Visible || sections < 5)
                    throw new InvalidOperationException($"设置页分组未正确显示：{sections} 组");
                var grouping = VerifySettingsLayout();
                if (!grouping.Valid) throw new InvalidOperationException("设置项分组异常：" + grouping.Detail);
                if (!VerifyAiJobsUiForDiagnostics())
                    throw new InvalidOperationException("AI 作业状态与人工重试入口未显示");
                if (!VerifyBackupImportUiForDiagnostics())
                    throw new InvalidOperationException("选择性备份导入、范围及取消入口未显示");
                if (!VerifyLibraryRelocationUiForDiagnostics())
                    throw new InvalidOperationException("视频库迁移预览、人工选择或取消入口未显示");
                if (posterCacheLimitChoices is null ||
                    !posterCacheLimitChoices.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                        .Any(item => item.Tag is int limit && limit == 0))
                    throw new InvalidOperationException("海报缓存的无限制选项未显示");
                var currentPosterLimit = config.PosterCacheLimitMb;
                try
                {
                    VideoItem.ConfigurePosterCache(0);
                    if (VideoItem.PosterCacheStats().LimitBytes != 0)
                        throw new InvalidOperationException("海报无限制设置未生效");
                }
                finally { VideoItem.ConfigurePosterCache(currentPosterLimit); }
                var librarySection = settingsSections["library"];
                librarySection.IsExpanded = true;
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                if (!GpuBox.IsVisible) throw new InvalidOperationException("点击卡片后未显示视频库设置");
                librarySection.IsExpanded = false;
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                if (GpuBox.IsVisible) throw new InvalidOperationException("收起卡片后仍显示视频库设置");
                var startupSection = settingsSections["startup"];
                startupSection.IsExpanded = true;
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                if (!AutoStartBox.IsVisible || startupUseCurrentButton is null)
                    throw new InvalidOperationException("启动选项或开机自启入口未显示");
                const string quotedStartupPath = @"C:\Program Files\映格视频库\映格视频库.exe";
                if (StartupRegistration.ParseExecutable('"' + quotedStartupPath + '"' + " --auto-start") != quotedStartupPath
                    || StartupRegistration.ParseExecutable(quotedStartupPath) != quotedStartupPath
                    || StartupRegistration.ParseExecutable("invalid command") is not null)
                    throw new InvalidOperationException("开机自启路径解析未兼容旧值或新命令");
                if (!PublishedExecutable.IsThisApplication(Environment.ProcessPath)
                    || PublishedExecutable.IsThisApplication(Path.Combine(Environment.SystemDirectory, "cmd.exe")))
                    throw new InvalidOperationException("启动项或更新程序未验证当前发布版身份");
                if (!ShouldStartMinimized(true, true, true)
                    || ShouldStartMinimized(false, true, true)
                    || ShouldStartMinimized(true, false, true)
                    || ShouldStartMinimized(true, true, false))
                    throw new InvalidOperationException("手动启动或关闭自启时仍被错误地最小化");
                startupSection.IsExpanded = false;
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                if (SettingsPanel.ActualWidth < 500 || PlayerPathBox.Parent is null || TmdbTokenBox.Parent is null)
                    throw new InvalidOperationException("设置页尺寸或已有设置控件异常");
                var optionProbe = new LibraryConfig
                {
                    UpdateAutoCheck = false, UpdateCheckIntervalHours = 24,
                    ScrapeRequestIntervalSeconds = 5, ScrapeRefreshIncompleteMetadata = true,
                    CardPageSize = 30, PosterCacheLimitMb = 0, MinimizeToTray = true, StartMinimized = true,
                    StartPage = "categories"
                };
                var optionClone = CloneLibraryConfig(optionProbe);
                if (optionClone.UpdateAutoCheck || optionClone.UpdateCheckIntervalHours != 24
                    || optionClone.ScrapeRequestIntervalSeconds != 5 || !optionClone.ScrapeRefreshIncompleteMetadata
                    || optionClone.CardPageSize != 30 || optionClone.PosterCacheLimitMb != 0 || !optionClone.MinimizeToTray
                    || !optionClone.StartMinimized || optionClone.StartPage != "categories")
                    throw new InvalidOperationException("设置选项在配置快照中丢失");
                RecordCacheDiagnostic("settings ui verified", $"分组 {sections} 个，宽度 {SettingsPanel.ActualWidth:F0}");
            }
            var completedStage = cacheStartupDiagnostic.Stage;
            var posterCache = VideoItem.PosterCacheStats();
            if (posterCache.LimitBytes > 0 && posterCache.Bytes > posterCache.LimitBytes)
                throw new InvalidOperationException("海报缓存超出用户设置的解码图像预算");
            var workingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            RecordCacheDiagnostic("hidden startup probe",
                $"完成后的阶段：{completedStage}；海报缓存 {posterCache.Bytes / 1048576d:F1} MiB / {(posterCache.LimitBytes == 0 ? "无限制" : $"{posterCache.LimitBytes / 1048576d:F0} MiB")}，瞬时峰值 {posterCache.PeakBytes / 1048576d:F1} MiB，"
                + $"命中 {posterCache.Hits}、未命中 {posterCache.Misses}、淘汰 {posterCache.Evictions}；进程工作集 {workingSet / 1048576d:F1} MiB");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllText(destination, JsonSerializer.Serialize(cacheStartupDiagnostic, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            RecordCacheDiagnostic("hidden startup probe error", ex.ToString());
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
                File.WriteAllText(destination, JsonSerializer.Serialize(cacheStartupDiagnostic, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
            return 1;
        }
    }

    static void SaveSpherePreview(System.Windows.FrameworkElement surface, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        surface.Measure(new System.Windows.Size(1120, 620));
        surface.Arrange(new System.Windows.Rect(0, 0, 1120, 620));
        surface.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1120, 620, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(destination);
        encoder.Save(file);
    }

    static int CountVisualDescendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        var count = 0;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T) count++;
            count += CountVisualDescendants<T>(child);
        }
        return count;
    }
    static T? FindVisualDescendant<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindVisualDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
