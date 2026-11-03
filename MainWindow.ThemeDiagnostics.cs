using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
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
                        var contrast = ThemeContrast(UiTheme.Brush(UiTheme.TextTertiary).Color,
                            UiTheme.Brush(background).Color);
                        if (contrast < 4.5)
                            throw new InvalidOperationException($"{mode} 的辅助文字与 {background} 对比度不足：{contrast:0.00}:1");
                    }
                }
                foreach (var (width, height) in new[] { (800, 550), (1240, 780), (1600, 1000) })
                {
                    Width = width;
                    Height = height;
                    // The review page only exists outside classic mode; the
                    // status page only exists in classic mode.
                    var pages = UiTheme.IsClassicMode
                        ? new[] { "all", "categories", "recommendations", "scrape", "status", "settings" }
                        : new[] { "all", "categories", "recommendations", "scrape", "review", "roles", "plugins", "settings" };
                    foreach (var page in pages)
                    {
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
                        if (page == "all" && Cards.VisibleCardCount > 0)
                            measuredCardWidths.Add((mode, UiTheme.Num(UiTheme.CardWidth),
                                ((FrameworkElement)Cards.Children[0]).ActualWidth));
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(this);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(outputDirectory, $"{mode}-{width}x{height}-{page}.png"));
                        encoder.Save(output);
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
                        });
                    }
                }
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
            if (navList.Height < 400) throw new InvalidOperationException("节点导航未能展开");
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
