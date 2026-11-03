using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
                foreach (var (width, height) in new[] { (800, 550), (1240, 780), (1600, 1000) })
                {
                    Width = width;
                    Height = height;
                    // The review page only exists outside classic mode; the
                    // status page only exists in classic mode.
                    var pages = UiTheme.IsClassicMode
                        ? new[] { "all", "categories", "scrape", "status", "settings" }
                        : new[] { "all", "categories", "scrape", "review", "roles", "settings" };
                    foreach (var page in pages)
                    {
                        ShowView(page);
                        if (page == "all" && categoryCardPopulationCompletion is { } population)
                            await population.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
                            rolesVisible = RolesPanel.Visibility == Visibility.Visible,
                            statusVisible = StatusPanel.Visibility == Visibility.Visible,
                            libraryNavVisible = LibraryNav.Visibility == Visibility.Visible,
                            statusNavVisible = StatusNav.Visibility == Visibility.Visible,
                            reviewNavVisible = ReviewNav.Visibility == Visibility.Visible,
                            pluginsNavVisible = PluginsNav.Visibility == Visibility.Visible,
                            cardWidthExpected = UiTheme.Num(UiTheme.CardWidth),
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
            UiTheme.Apply(UiTheme.Tech);
            ApplyInterfaceMode();
            if (ReviewNav.Visibility != Visibility.Visible || PluginsNav.Visibility != Visibility.Visible
                || LibraryNav.Visibility != Visibility.Visible || StatusNav.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("新版界面的导航集合不正确");
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
}
