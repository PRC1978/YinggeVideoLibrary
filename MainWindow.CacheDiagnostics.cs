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

    public async Task<int> WriteHiddenStartupDiagnosticAsync(string destination, bool verifyGraph = false, bool verifyScrape = false, bool verifySettings = false)
    {
        for (var attempt = 0; attempt < 120 && cacheStartupDiagnostic.Stage is not ("refresh finished" or "index error" or "ui startup exception"); attempt++)
            await Task.Delay(250);
        try
        {
            if (verifyGraph)
            {
                var graphStarted = System.Diagnostics.Stopwatch.StartNew();
                ShowView("categories");
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                var graphBuildMs = graphStarted.ElapsedMilliseconds;
                var viewport = sphereGraphViewport ?? throw new InvalidOperationException("分类图视口未创建");
                var sphere = sphereMapSurface ?? throw new InvalidOperationException("分类图绘制层未创建");
                var graphIndex = GetSphereGraphIndex();
                if (sphere.NodeCount == 0 || viewport.ActualWidth < 100 || viewport.ActualHeight < 100)
                    throw new InvalidOperationException("分类图节点或可视区域未完成布局");
                if (sphere.NodeCount != graphIndex.AllCategoryCounts.Count + graphIndex.TagCounts.Count ||
                    !sphere.EveryNodeLinked || sphereGraphTimer.IsEnabled || sphereGraphCanvas?.Children.Count != 1)
                    throw new InvalidOperationException("分类图节点关系不完整，或未使用单层合并绘制");
                var firstTag = graphIndex.RelatedTags.Where(pair => pair.Value.Count > 0)
                    .OrderByDescending(pair => pair.Value.Count).ThenByDescending(pair => graphIndex.TagCounts[pair.Key])
                    .Select(pair => pair.Key).FirstOrDefault()
                    ?? graphIndex.TagCounts.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
                var tagNodes = 0;
                var tagSwitchMs = 0L;
                if (firstTag is not null)
                {
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    NavigateSphereNode("\u0001" + firstTag);
                    await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                    tagSwitchMs = started.ElapsedMilliseconds;
                    sphere = sphereMapSurface ?? throw new InvalidOperationException("标签切换后绘制层未创建");
                    var expected = graphIndex.TagCounts[firstTag];
                    var expectedNodes = graphIndex.CategoriesByTag[firstTag].Count + graphIndex.RelatedTags[firstTag].Count;
                    if (sphere.RootCount != expected || sphere.NodeCount != expectedNodes ||
                        !sphere.EveryNodeLinked || sphereGraphCanvas?.Children.Count != 1)
                        throw new InvalidOperationException("按标签筛选后关联节点或连线不完整");
                    tagNodes = sphere.NodeCount;
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
                var seenPaths = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
                var navigationPages = 0;
                while (true)
                {
                    navigationPages++;
                    foreach (var item in navList.ItemsSource.OfType<SphereNavItem>()) seenPaths.Add(item.Path);
                    if (!nextPage.IsEnabled) break;
                    if (navigationPages > sphere.NodeCount) throw new InvalidOperationException("分类节点分页未能结束");
                    nextPage.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }
                if (seenPaths.Count != sphere.NodeCount)
                    throw new InvalidOperationException($"分类图有 {sphere.NodeCount} 个节点，但分页导航仅能访问 {seenPaths.Count} 个");
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
                var previewStarted = System.Diagnostics.Stopwatch.StartNew();
                SaveSpherePreview(sphere, Path.ChangeExtension(destination, ".png"));
                var previewMs = previewStarted.ElapsedMilliseconds;
                RecordCacheDiagnostic("graph verified", $"全部相关节点 {sphere.NodeCount}，选中标签关联节点 {tagNodes}，全部节点经 {navigationPages} 页可访问且连线完整；视口 {viewport.ActualWidth:F0}×{viewport.ActualHeight:F0}；首次构建 {graphBuildMs} ms，标签切换 {tagSwitchMs} ms，离屏绘制与 PNG 保存 {previewMs} ms；缩放、平移、调高正常");
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
                var librarySection = settingsSections["library"];
                librarySection.IsExpanded = true;
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                if (!GpuBox.IsVisible) throw new InvalidOperationException("点击卡片后未显示视频库设置");
                librarySection.IsExpanded = false;
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
                if (GpuBox.IsVisible) throw new InvalidOperationException("收起卡片后仍显示视频库设置");
                if (SettingsPanel.ActualWidth < 500 || PlayerPathBox.Parent is null || TmdbTokenBox.Parent is null)
                    throw new InvalidOperationException("设置页尺寸或已有设置控件异常");
                var optionProbe = new LibraryConfig
                {
                    UpdateAutoCheck = false, UpdateCheckIntervalHours = 24,
                    ScrapeRequestIntervalSeconds = 5, ScrapeRefreshIncompleteMetadata = true,
                    CardPageSize = 30
                };
                var optionClone = CloneLibraryConfig(optionProbe);
                if (optionClone.UpdateAutoCheck || optionClone.UpdateCheckIntervalHours != 24
                    || optionClone.ScrapeRequestIntervalSeconds != 5 || !optionClone.ScrapeRefreshIncompleteMetadata
                    || optionClone.CardPageSize != 30)
                    throw new InvalidOperationException("设置选项在配置快照中丢失");
                RecordCacheDiagnostic("settings ui verified", $"分组 {sections} 个，宽度 {SettingsPanel.ActualWidth:F0}");
            }
            RecordCacheDiagnostic("hidden startup probe", $"完成后的阶段：{cacheStartupDiagnostic.Stage}");
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
}
