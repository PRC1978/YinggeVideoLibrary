using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    internal async Task<int> WriteHiddenMotionDiagnosticAsync(string outputPath)
    {
        try
        {
            var results = new List<object>();
            foreach (var (mode, expectedMilliseconds) in new[]
                { (UiMotion.Smooth, 0), (UiMotion.Balanced, 130), (UiMotion.Animated, 280) })
            {
                config.UiMotion = mode;
                ShowView("settings");
                ShowView("all");
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                var initialOpacity = LibraryScroll.Opacity;
                var initialY = (LibraryScroll.RenderTransform as TranslateTransform)?.Y ?? 0;
                await Task.Delay(expectedMilliseconds + 100);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                var finalOpacity = LibraryScroll.Opacity;
                var finalY = (LibraryScroll.RenderTransform as TranslateTransform)?.Y ?? 0;
                var passed = mode == UiMotion.Smooth
                    ? Math.Abs(initialOpacity - 1) < .001 && Math.Abs(initialY) < .001
                    : initialOpacity < 1 && initialY > 0 &&
                      Math.Abs(finalOpacity - 1) < .01 && Math.Abs(finalY) < .1;
                results.Add(new { mode, expectedMilliseconds, initialOpacity, initialY,
                    finalOpacity, finalY, passed });
                if (!passed) throw new InvalidOperationException($"{mode} 的页面动效未按设置完成");
            }
            config.UiMotion = UiMotion.Animated;
            ShowView("settings");
            ShowView("all");
            config.UiMotion = UiMotion.Smooth;
            ShowView("settings");
            var interruptedOpacity = LibraryScroll.Opacity;
            var interruptedY = (LibraryScroll.RenderTransform as TranslateTransform)?.Y ?? 0;
            var interruptionPassed = Math.Abs(interruptedOpacity - 1) < .001 && Math.Abs(interruptedY) < .001;
            results.Add(new { mode = "animated-to-smooth", expectedMilliseconds = 0,
                initialOpacity = interruptedOpacity, initialY = interruptedY,
                finalOpacity = interruptedOpacity, finalY = interruptedY, passed = interruptionPassed });
            if (!interruptionPassed) throw new InvalidOperationException("切换到流畅优先后残留了旧页面动画");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new { passed = true, results },
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new { passed = false, error = ex.ToString() },
                new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    /// <summary>Screenshot every interface mode at the three acceptance window
    /// sizes, covering the pages that differ per mode (library, scrape, review,
    /// settings). Runs fully offscreen against an isolated --data-dir library.
    /// Also asserts a few theme invariants so regressions fail loudly instead
    /// of shipping an unreadable mode.</summary>
    internal async Task<int> WriteHiddenThemeDiagnosticAsync(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var report = new List<object>();
            var measuredCardWidths = new List<(string Mode, double Expected, double Actual)>();
            foreach (var (mode, label, _) in UiTheme.Modes)
            {
                UiTheme.Apply(mode);
                ApplyInterfaceMode();
                // Classic is a pixel-faithful compatibility option. New modes
                // require even their subdued text to remain legible on every
                // normal content surface.
                if (mode != UiTheme.Classic)
                {
                    foreach (var background in new[] { UiTheme.PageBackground, UiTheme.Surface, UiTheme.SurfaceAlt })
                    {
                        var surface = UiTheme.Brush(background).Color;
                        if (surface.A < 255)
                        {
                            var page = UiTheme.Brush(UiTheme.PageBackground).Color;
                            var alpha = surface.A / 255d;
                            surface = Color.FromRgb(
                                (byte)Math.Round(surface.R * alpha + page.R * (1 - alpha)),
                                (byte)Math.Round(surface.G * alpha + page.G * (1 - alpha)),
                                (byte)Math.Round(surface.B * alpha + page.B * (1 - alpha)));
                        }
                        var contrast = ThemeContrast(UiTheme.Brush(UiTheme.TextTertiary).Color, surface);
                        if (contrast < 4.5)
                            throw new InvalidOperationException($"{mode} 的辅助文字与 {background} 对比度不足：{contrast:0.00}:1");
                        if (background != UiTheme.PageBackground &&
                            ThemeContrast(UiTheme.Brush(UiTheme.Accent).Color, surface) < 4.5)
                            throw new InvalidOperationException($"{mode} 的强调文字与 {background} 对比度不足");
                    }
                    if (ThemeContrast(UiTheme.Brush(UiTheme.AccentForeground).Color,
                            UiTheme.Brush(UiTheme.Accent).Color) < 4.5)
                        throw new InvalidOperationException($"{mode} 的强调按钮文字对比度不足");
                }
                foreach (var (width, height) in new[] { (800, 550), (1240, 780), (1600, 1000) })
                {
                    Width = width;
                    Height = height;
                    // The review page only exists outside classic mode; the
                    // status page only exists in classic mode.
                    var pages = UiTheme.IsClassicMode
                        ? new[] { "all", "categories", "folders", "recommendations", "scrape", "status", "settings" }
                        : new[] { "all", "categories", "folders", "recommendations", "scrape", "review", "roles", "plugins", "settings" };
                    foreach (var page in pages)
                    {
                        // Startup populates videos asynchronously. Seed the
                        // in-memory review sample only when the review page is
                        // reached, after the library page has populated.
                        if (page == "review" && AppPaths.IsRedirected &&
                            config.AiReviews.Count == 0)
                        {
                            foreach (var (video, index) in videos.Take(4)
                                .Select((video, index) => (video, index)))
                                config.AiReviews[video.Id] = new AiReviewRecord
                                {
                                    VideoId = video.Id,
                                    CandidateCode = $"UI-{index + 1:000}",
                                    CandidateTitle = "隔离验收候选 · " + video.Title,
                                    CandidateSource = "fixture",
                                    Confidence = 35 + index * 12,
                                    Status = "pending",
                                    DecisionSource = "rules",
                                    Reason = "合成候选用于界面排版与对比度检查；不代表真实搜刮结果。"
                                };
                        }
                        ShowView(page);
                        if (page is not ("all" or "categories") &&
                            (MissingFilterButton.Visibility == Visibility.Visible ||
                             UnavailableFilterButton.Visibility == Visibility.Visible))
                            throw new InvalidOperationException($"{page} 页仍显示视频缺失筛选按钮");
                        if (page == "review" && config.AiReviews.Values.Any(review =>
                            videos.Any(video => video.Id == review.VideoId) && ReviewMatchesFilter(review)) &&
                            ReviewQueuePanel.Children.Count == 1 &&
                            ReviewQueuePanel.Children[0] is System.Windows.Controls.TextBlock)
                            throw new InvalidOperationException("有可见审核记录，但审核列表显示空状态");
                        if (page == "recommendations")
                        {
                            for (var attempt = 0; attempt < 30 &&
                                !RecommendationPanel.Children.OfType<System.Windows.Controls.Border>().Any(); attempt++)
                                await Task.Delay(50);
                        }
                        var cardPopulationTimedOut = false;
                        if (page == "all" && categoryCardPopulationCompletion is { } population)
                            cardPopulationTimedOut = await Task.WhenAny(population.Task,
                                Task.Delay(TimeSpan.FromSeconds(3))) != population.Task;
                        LibraryScroll.ScrollToTop();
                        UpdateLayout();
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        UpdateLayout();
                        if (page == "categories" && width == 1240 && height == 780 &&
                            sphereGraphCard is { IsVisible: true } card &&
                            card.TranslatePoint(new System.Windows.Point(0, card.ActualHeight), this).Y > height)
                            throw new InvalidOperationException($"{mode} 分类关系图卡片超出窗口底部");
                        if (page == "all" && Cards.VisibleCardCount > 0)
                            measuredCardWidths.Add((mode, UiTheme.Num(UiTheme.CardWidth),
                                ((FrameworkElement)Cards.Children[0]).ActualWidth));
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(this);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(outputDirectory, $"{mode}-{width}x{height}-{page}.png"));
                        encoder.Save(output);
                        if (mode == UiTheme.Glass && width == 1240 && page == "categories" && sphereMapSurface is not null)
                        {
                            SetSphereRotation(.55, .38);
                            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                            var tilted = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                            tilted.Render(this);
                            var tiltedEncoder = new PngBitmapEncoder();
                            tiltedEncoder.Frames.Add(BitmapFrame.Create(tilted));
                            using var tiltedOutput = File.Create(Path.Combine(outputDirectory, "glass-1240x780-categories-tilted.png"));
                            tiltedEncoder.Save(tiltedOutput);
                            SetSphereRotation(0, 0);
                        }
                        report.Add(new
                        {
                            mode, width, height, page,
                            pageBackground = UiTheme.Brush(UiTheme.PageBackground).Color.ToString(),
                            textPrimary = UiTheme.Brush(UiTheme.TextPrimary).Color.ToString(),
                            reviewVisible = ReviewPanel.Visibility == Visibility.Visible,
                            reviewStoredCount = page == "review" ? config.AiReviews.Count : (int?)null,
                            reviewMatchingVideoCount = page == "review" ? config.AiReviews.Keys.Count(id => videos.Any(video => video.Id == id)) : (int?)null,
                            reviewValueMatchingVideoCount = page == "review" ? config.AiReviews.Values.Count(review => videos.Any(video => video.Id == review.VideoId)) : (int?)null,
                            reviewRecognizedStatusCount = page == "review" ? config.AiReviews.Values.Count(review => review.Status is "pending" or "rejected" or "approved" or "unmatched") : (int?)null,
                            reviewFilteredCount = page == "review" ? config.AiReviews.Values.Count(ReviewMatchesFilter) : (int?)null,
                            reviewRenderedRows = page == "review" ? ReviewQueuePanel.Children.Count : (int?)null,
                            reviewUsesPageScroll = page == "review" && ReviewQueuePanel.Parent is System.Windows.Controls.ScrollViewer reviewScroll
                                ? double.IsNaN(reviewScroll.Height) && reviewScroll.VerticalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Disabled
                                : (bool?)null,
                            rolesVisible = RolesPanel.Visibility == Visibility.Visible,
                            pluginsVisible = PluginsPanel.Visibility == Visibility.Visible,
                            statusVisible = StatusPanel.Visibility == Visibility.Visible,
                            libraryNavVisible = LibraryNav.Visibility == Visibility.Visible,
                            statusNavVisible = StatusNav.Visibility == Visibility.Visible,
                            reviewNavVisible = ReviewNav.Visibility == Visibility.Visible,
                            pluginsNavVisible = PluginsNav.Visibility == Visibility.Visible,
                            cardWidthExpected = UiTheme.Num(UiTheme.CardWidth),
                            cardPopulationTimedOut,
                            cardWidthActual = page == "all" && Cards.VisibleCardCount > 0
                                ? ((FrameworkElement)Cards.Children[0]).ActualWidth : (double?)null,
                            sphereNodeCount = page == "categories" ? sphereMapSurface?.NodeCount : null,
                            sphereEveryNodeLinked = page == "categories" ? sphereMapSurface?.EveryNodeLinked : null,
                            graphViewportTop = page == "categories" && sphereGraphViewport is not null
                                ? sphereGraphViewport.TranslatePoint(new System.Windows.Point(0, 0), this).Y : (double?)null,
                            graphViewportBottom = page == "categories" && sphereGraphViewport is not null
                                ? sphereGraphViewport.TranslatePoint(new System.Windows.Point(0, sphereGraphViewport.ActualHeight), this).Y : (double?)null,
                            graphCardBottom = page == "categories" && sphereGraphCard is not null
                                ? sphereGraphCard.TranslatePoint(new System.Windows.Point(0, sphereGraphCard.ActualHeight), this).Y : (double?)null,
                        });
                    }
                }
            }
            // Exercise the live inspector and the same node-selection method
            // used by graph hits, chips and the navigator.
            UiTheme.Apply(UiTheme.Tech);
            ApplyInterfaceMode();
            Width = 1240;
            Height = 780;
            ShowView("categories");
            UpdateLayout();
            var inspectedPath = GetSphereGraphIndexForUi()?.VideosByPath.Values
                .SelectMany(item => item.CategoryValues)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (inspectedPath is not null)
            {
                SelectSphereCategory(inspectedPath);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                if (Math.Abs(sphereZoom - 1.2) > .001 ||
                    sphereSelectionSources?.Text.Contains("fixture", StringComparison.OrdinalIgnoreCase) != true)
                    throw new InvalidOperationException("节点单次选择未到 120% 或详情未读取隔离库来源");
                ShowView("all");
                ShowView("categories");
                if (Math.Abs(sphereZoom - 1) > .001)
                    throw new InvalidOperationException("重进分类页未恢复 100% 适应视图");
            }
            // Invariants: classic mode must keep the original nav set and the
            // status page; redesigned modes must expose review + plugins nav.
            UiTheme.Apply(UiTheme.Classic);
            ApplyInterfaceMode();
            if (ReviewNav.Visibility != Visibility.Collapsed || PluginsNav.Visibility != Visibility.Collapsed
                || StatusNav.Visibility != Visibility.Visible || AllNav.Visibility != Visibility.Visible)
                throw new InvalidOperationException("原版界面的导航集合被改变");
            if (ReviewQueuePanel.Parent is not System.Windows.Controls.ScrollViewer classicQueue ||
                classicQueue.Parent is not System.Windows.Controls.StackPanel classicReview ||
                reviewSavedSection is null || classicReview.Children.IndexOf(reviewSavedSection) > classicReview.Children.IndexOf(classicQueue))
                throw new InvalidOperationException("原版审核操作顺序未恢复");
            UiTheme.Apply(UiTheme.Tech);
            ApplyInterfaceMode();
            if (ReviewNav.Visibility != Visibility.Visible || PluginsNav.Visibility != Visibility.Visible
                || LibraryNav.Visibility != Visibility.Visible || StatusNav.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("新版界面的导航集合不正确");
            if (ReviewQueuePanel.Parent is not System.Windows.Controls.ScrollViewer newQueue ||
                newQueue.Parent is not System.Windows.Controls.StackPanel newReview ||
                reviewSavedSection is null || newReview.Children.IndexOf(reviewSavedSection) < newReview.Children.IndexOf(newQueue))
                throw new InvalidOperationException("新版审核记录未提前显示");
            ShowView("review");
            ShowView("roles");
            if (!ReferenceEquals(RolesNav.Background, UiTheme.SelectionBackgroundBrush)
                || !ReferenceEquals(ReviewNav.Background, System.Windows.Media.Brushes.Transparent))
                throw new InvalidOperationException("人物与审核导航同时保持选中状态");
            if (measuredCardWidths.Any(item => Math.Abs(item.Expected - item.Actual) > 1))
                throw new InvalidOperationException("界面模式切换后卡片宽度没有跟随密度设置");
            // A single screenshot per theme misses the old frozen-brush bug:
            // repeated switches could leave a control painted in the previous
            // palette. Exercise live bindings and navigation through 20 cycles.
            var switchChecks = new List<object>();
            for (var cycle = 0; cycle < 20; cycle++)
            {
                var mode = UiTheme.Modes[cycle % UiTheme.Modes.Length].Id;
                UiTheme.Apply(mode);
                ApplyInterfaceMode();
                var titleColor = (PageTitle.Foreground as SolidColorBrush)?.Color;
                if (titleColor != UiTheme.TextPrimaryBrush.Color)
                    throw new InvalidOperationException($"第 {cycle + 1} 次切换后标题仍使用旧主题颜色");
                if ((ReviewNav.Visibility == Visibility.Visible) == UiTheme.IsClassicMode)
                    throw new InvalidOperationException($"第 {cycle + 1} 次切换后审核入口状态不正确");
                switchChecks.Add(new { cycle = cycle + 1, mode, titleColor = titleColor?.ToString() });
            }
            File.WriteAllText(Path.Combine(outputDirectory, "theme-switch.json"),
                JsonSerializer.Serialize(switchChecks, new JsonSerializerOptions { WriteIndented = true }));
            // The complete list must reflow after a live theme change. A
            // hidden ListBox used to report width 0 and stayed at one column.
            config.ShowAll = true;
            ShowView("all");
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            ReflowVirtualRows();
            if (currentFilteredVideos.Count >= 5 && VirtualCards.ActualWidth > 900 && virtualColumns < 4)
                throw new InvalidOperationException("主题切换后完整列表仍只显示一列");
            ShowView("review");
            RefreshCardsAfterReview();
            if (ReviewPanel.Visibility != Visibility.Visible || LibraryScroll.Visibility != Visibility.Visible ||
                AllListPanel.Visibility == Visibility.Visible)
                throw new InvalidOperationException("审核刷新错误地切回视频完整列表");
            if (reviewTopNextPageButton is null || reviewTopPageJumpBox is null ||
                ReviewQueuePanel.Parent is not System.Windows.Controls.ScrollViewer queue ||
                (UiTheme.IsClassicMode ? queue.Height < 500 : !double.IsNaN(queue.Height)))
                throw new InvalidOperationException("审核队列缺少上方翻页、跳转或连续页面滚动");
            if (reviewPageCount > 1)
            {
                reviewTopPageJumpBox.Text = reviewPageCount.ToString();
                JumpToReviewPage(reviewTopPageJumpBox);
                if (reviewPageIndex != reviewPageCount - 1 || ReviewQueuePanel.Children.Count == 0)
                    throw new InvalidOperationException("审核页跳转没有到达末页");
                reviewTopPageJumpBox.Text = "1";
                JumpToReviewPage(reviewTopPageJumpBox);
            }
            var navPanel = BuildSphereNavigator([]) as System.Windows.Controls.StackPanel
                ?? throw new InvalidOperationException("节点导航未创建");
            var navBar = (System.Windows.Controls.DockPanel)navPanel.Children[0];
            var expand = (System.Windows.Controls.Button)navBar.Children[1];
            var navList = (System.Windows.Controls.ListBox)navPanel.Children[1];
            expand.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (navList.Height < 240) throw new InvalidOperationException("节点导航未能展开");
            // Probe why implicit stock-control styles do or do not land.
            var combo = ScrapeSourceBox;
            var probe = new
            {
                comboBackground = combo.Background?.ToString(),
                comboBackgroundSource = DependencyPropertyHelper.GetValueSource(combo, System.Windows.Controls.ComboBox.BackgroundProperty).BaseValueSource.ToString(),
                comboTemplate = combo.Template?.GetType().FullName,
                comboStyleIsNull = combo.Style is null,
                appResourceComboStyle = TryFindResource(typeof(System.Windows.Controls.ComboBox))?.ToString(),
            };
            File.WriteAllText(Path.Combine(outputDirectory, "theme-layout.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(outputDirectory, "style-probe.json"),
                JsonSerializer.Serialize(probe, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "error.txt"), ex.ToString());
            return 1;
        }
    }

    static double ThemeContrast(Color foreground, Color background)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte component)
            {
                var value = component / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
