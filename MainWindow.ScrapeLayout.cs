using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // Keep the existing controls and handlers. Only group their presentation after
    // all source, queue and review initializers have attached their controls.
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
        panel.Children.Add(new TextBlock
        {
            Text = "搜刮任务", FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 51, 71)),
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(actions);
        panel.Children.Add(queue);
        panel.Children.Add(sourceLabel);
        ScrapeSourceBox.Margin = new Thickness(0, 8, 0, 6);
        panel.Children.Add(ScrapeSourceBox);
        ScrapeSourceResultText.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(ScrapeSourceResultText);
        panel.Children.Add(new Expander
        {
            Header = "筛选与跳过条件", Content = filters, IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(0, 8, 0, 4)
        });
        panel.Children.Add(new Expander
        {
            Header = "来源与高级设置", Content = advanced, IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 18),
            Padding = new Thickness(0, 8, 0, 4)
        });
        if (items[reviewStart] is TextBlock reviewHeading) reviewHeading.Text = "审核队列";
        foreach (var item in items.Skip(reviewStart)) panel.Children.Add(item);
    }
}
