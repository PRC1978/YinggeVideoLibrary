using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.IO;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // Keep the existing controls and handlers. Only group their presentation after
    // all source, queue and review initializers have attached their controls.
    System.Windows.Controls.TabControl? scrapePages;
    // The three scrape groups are kept as fields so ApplyInterfaceMode can
    // re-parent them without rebuilding: classic mode hosts all three in the
    // scrape TabControl; the redesigned modes lift the review group into the
    // first-class 审核 page.
    StackPanel? scrapeTasksGroup;
    StackPanel? scrapeReviewGroup;
    StackPanel? scrapeAdvancedGroup;
    TabItem? scrapeReviewTab;

    void RegroupScrapePanel()
    {
        if (ScrapePanel.Child is not StackPanel panel) return;
        var items = panel.Children.Cast<UIElement>().ToArray();
        var reviewStart = Array.FindIndex(items, item => item is TextBlock text &&
            text.Text == "AI 搜刮可信度审核");
        var queue = items.OfType<StackPanel>().FirstOrDefault(item =>
            scrapeQueueInfo is not null && item.Children.Contains(scrapeQueueInfo));
        var actions = items.OfType<WrapPanel>().FirstOrDefault(item => item.Children.Contains(ScrapeBatchButton));
        var filters = items.OfType<StackPanel>().FirstOrDefault(item =>
            scrapeExcludedList is not null && item.Children.Contains(scrapeExcludedList));
        var sourceLabel = items.OfType<TextBlock>().FirstOrDefault(item => item.Text == "默认完整资料搜刮来源");
        if (reviewStart < 0 || queue is null || actions is null || filters is null || sourceLabel is null) return;

        var advancedItems = items.Take(reviewStart).Where(item =>
            !ReferenceEquals(item, queue) && !ReferenceEquals(item, actions) &&
            !ReferenceEquals(item, filters) && !ReferenceEquals(item, sourceLabel) &&
            !ReferenceEquals(item, ScrapeSourceBox) && !ReferenceEquals(item, ScrapeSourceResultText)).ToArray();
        panel.Children.Clear();
        var advanced = new StackPanel();
        foreach (var item in advancedItems) advanced.Children.Add(item);
        ScrapePanel.MaxWidth = double.PositiveInfinity;
        panel.MaxWidth = double.PositiveInfinity;
        var tasks = new StackPanel();
        tasks.Children.Add(new TextBlock
        {
            Text = "搜刮任务", FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = UiTheme.TextPrimaryBrush,
            Margin = new Thickness(0, 0, 0, 6)
        });
        tasks.Children.Add(actions);
        tasks.Children.Add(queue);
        tasks.Children.Add(sourceLabel);
        ScrapeSourceBox.Margin = new Thickness(0, 8, 0, 6);
        tasks.Children.Add(ScrapeSourceBox);
        ScrapeSourceResultText.Margin = new Thickness(0, 0, 0, 6);
        tasks.Children.Add(ScrapeSourceResultText);
        tasks.Children.Add(new Expander
        {
            Header = "筛选与跳过条件", Content = filters, IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(0, 8, 0, 4)
        });
        var review = new StackPanel();
        if (items[reviewStart] is TextBlock reviewHeading) reviewHeading.Text = "审核队列";
        foreach (var item in items.Skip(reviewStart)) review.Children.Add(item);
        scrapeTasksGroup = tasks;
        scrapeReviewGroup = review;
        scrapeAdvancedGroup = advanced;
        ArrangeScrapeGroups();
        ScrapePanel.SizeChanged += (_, _) => UpdateScrapePagesWidth();
    }

    /// <summary>Parent the scrape groups according to the interface mode:
    /// classic keeps the three-tab layout inside the scrape page; the
    /// redesigned modes keep 任务与队列 / 来源与凭据 tabs and lift the review
    /// group into the standalone 审核 page host. Safe to call repeatedly.</summary>
    void ArrangeScrapeGroups()
    {
        if (scrapeTasksGroup is null || scrapeReviewGroup is null || scrapeAdvancedGroup is null) return;
        if (ScrapePanel.Child is not StackPanel panel) return;
        // Detach the review group from wherever it currently lives.
        if (scrapeReviewTab is not null && ReferenceEquals(scrapeReviewTab.Content, scrapeReviewGroup))
            scrapeReviewTab.Content = null;
        if (ReferenceEquals(scrapeReviewGroup.Parent, ReviewPanelBody))
            ReviewPanelBody.Children.Remove(scrapeReviewGroup);
        panel.Children.Clear();
        ReviewPanelBody.Children.Clear();
        var pages = new System.Windows.Controls.TabControl
        {
            BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        pages.Items.Add(new TabItem { Header = "任务与队列", Content = scrapeTasksGroup, Padding = new Thickness(14, 8, 14, 8) });
        if (UiTheme.IsClassicMode)
        {
            scrapeReviewTab = new TabItem { Header = "人工审核", Content = scrapeReviewGroup, Padding = new Thickness(14, 8, 14, 8) };
            pages.Items.Add(scrapeReviewTab);
        }
        else ReviewPanelBody.Children.Add(scrapeReviewGroup);
        pages.Items.Add(new TabItem { Header = "来源与凭据", Content = scrapeAdvancedGroup, Padding = new Thickness(14, 8, 14, 8) });
        pages.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, pages) || pages.SelectedItem is not TabItem selected ||
                selected.Content is not FrameworkElement content) return;
            var duration = UiMotion.PageFadeMilliseconds(config.UiMotion);
            content.BeginAnimation(OpacityProperty, null);
            content.Opacity = 1;
            if (content.RenderTransform is System.Windows.Media.TranslateTransform oldMovement)
            {
                oldMovement.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                oldMovement.Y = 0;
            }
            if (duration == 0) return;
            if (UiTheme.Current is UiTheme.Cinema or UiTheme.Glass) duration = Math.Max(duration, 320);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(pages.SelectedItem, selected)) return;
                var easing = new System.Windows.Media.Animation.QuadraticEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                content.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.65, 1,
                    TimeSpan.FromMilliseconds(duration)) { EasingFunction = easing });
                var movement = new System.Windows.Media.TranslateTransform();
                content.RenderTransform = movement;
                movement.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(duration))
                    { EasingFunction = easing });
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        panel.Children.Add(pages);
        scrapePages = pages;
        UpdateScrapePagesWidth();
    }

    // A vertical StackPanel measures its children with infinite width, so a
    // long TextBox or an unwrapped button row used to widen the tab content and
    // get clipped by the page ScrollViewer at 800x550. Constrain the TabControl
    // to the real viewport (minus page margin and card padding) so WrapPanels
    // wrap and long inputs scroll inside their own box.
    void UpdateScrapePagesWidth()
    {
        if (scrapePages is null) return;
        var panelWidth = ScrapePanel.ActualWidth;
        if (panelWidth <= 0) return;
        // ScrapePanel already excludes the page margin; its own Border padding
        // is 24 px on each side.
        scrapePages.Width = Math.Max(320, panelWidth - 48);
    }

    internal async Task<int> WriteHiddenScrapeDiagnosticAsync(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            ShowView("scrape");
            var tab = (ScrapePanel.Child as StackPanel)?.Children.OfType<System.Windows.Controls.TabControl>().Single();
            if (tab is null) throw new InvalidOperationException("搜刮二级页面不存在");
            var measurements = new List<object>();
            foreach (var (width, height) in new[] { (800, 550), (1240, 780), (1600, 1000) })
            {
                Width = width;
                Height = height;
                for (var page = 0; page < tab.Items.Count; page++)
                {
                    tab.SelectedIndex = page;
                    LibraryScroll.ScrollToTop();
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    UpdateLayout();
                    var content = (tab.Items[page] as TabItem)?.Content as FrameworkElement;
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                    measurements.Add(new
                    {
                        width, height, page, panelWidth = ScrapePanel.ActualWidth,
                        tabWidth = tab.ActualWidth, contentWidth = content?.ActualWidth ?? 0,
                        dpiScaleX = dpi.DpiScaleX, dpiScaleY = dpi.DpiScaleY,
                        viewportWidth = LibraryScroll.ViewportWidth,
                        clippedHorizontally = content is not null && content.ActualWidth > LibraryScroll.ViewportWidth + 1
                    });
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(this);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(outputDirectory, $"scrape-{width}x{height}-page{page}.png"));
                    encoder.Save(output);
                }
            }
            File.WriteAllText(Path.Combine(outputDirectory, "layout.json"),
                JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
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
