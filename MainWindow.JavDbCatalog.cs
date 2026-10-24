using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBox = System.Windows.Controls.TextBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    sealed class JavDbCatalogRow(MovieMetadata metadata)
    {
        public MovieMetadata Metadata { get; } = metadata;
        public string DisplayName => $"{Metadata.Code} · {Metadata.Title}";
    }

    TextBox? javDbCatalogQuery;
    ComboBox? javDbCatalogLinks;
    ListBox? javDbCatalogResults;
    TextBlock? javDbCatalogStatus;
    Button? javDbCatalogPrevious;
    Button? javDbCatalogNext;
    bool javDbCatalogBusy;
    bool javDbCatalogBrowsing;
    int javDbCatalogPage = 1;

    void InitializeJavDbCatalog(StackPanel parent, UIElement after)
    {
        var shell = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 227, 245)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13),
            Padding = new Thickness(15), Margin = new Thickness(0, 16, 0, 14)
        };
        var body = new StackPanel();
        shell.Child = body;
        body.Children.Add(new TextBlock { Text = "JavDB 作品关系浏览", FontSize = 16, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = "手动查找一个作品，再沿其公开详情中的演员、系列和标签浏览相关作品。选中作品后可匹配到本地视频，并交由人工审核。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 11)
        });

        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        javDbCatalogQuery = new TextBox { Height = 35, Padding = new Thickness(9, 5, 9, 5), ToolTip = "输入番号或作品标题，不发送本地文件路径" };
        var search = CatalogButton("搜索作品", true);
        search.Click += JavDbCatalogSearch;
        DockPanel.SetDock(search, Dock.Right); searchRow.Children.Add(search);
        searchRow.Children.Add(javDbCatalogQuery);
        body.Children.Add(searchRow);

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 9) };
        var fromSelected = CatalogButton("读取选中视频的关系");
        fromSelected.Click += JavDbCatalogUseSelectedVideo;
        var readLinks = CatalogButton("读取所选作品的关系");
        readLinks.Click += JavDbCatalogReadLinks;
        var match = CatalogButton("匹配到本地视频…");
        match.Click += JavDbCatalogMatch;
        actions.Children.Add(fromSelected); actions.Children.Add(readLinks); actions.Children.Add(match);
        body.Children.Add(actions);

        var relationshipRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var browse = CatalogButton("浏览相关作品", true);
        browse.Click += JavDbCatalogBrowse;
        DockPanel.SetDock(browse, Dock.Right); relationshipRow.Children.Add(browse);
        javDbCatalogLinks = new ComboBox { Height = 35, DisplayMemberPath = nameof(JavDbCatalogLink.DisplayName),
            ToolTip = "只展示当前作品的 JavDB 详情页实际提供的关系链接" };
        javDbCatalogLinks.SelectionChanged += (_, _) =>
        { javDbCatalogBrowsing = false; javDbCatalogPage = 1; SetJavDbCatalogPageButtons(); };
        relationshipRow.Children.Add(javDbCatalogLinks);
        body.Children.Add(relationshipRow);

        javDbCatalogStatus = new TextBlock { Text = "先搜索作品，或选择一个已搜刮的 JavDB 视频读取关系。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(javDbCatalogStatus);
        javDbCatalogResults = new ListBox { Height = 190, DisplayMemberPath = nameof(JavDbCatalogRow.DisplayName),
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 227, 245)) };
        body.Children.Add(javDbCatalogResults);
        var pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        javDbCatalogPrevious = CatalogButton("上一页");
        javDbCatalogPrevious.Click += async (_, _) => await JavDbCatalogChangePageAsync(-1);
        javDbCatalogNext = CatalogButton("下一页");
        javDbCatalogNext.Click += async (_, _) => await JavDbCatalogChangePageAsync(1);
        pager.Children.Add(javDbCatalogPrevious); pager.Children.Add(javDbCatalogNext);
        body.Children.Add(pager);
        SetJavDbCatalogPageButtons();
        parent.Children.Insert(parent.Children.IndexOf(after) + 1, shell);
    }

    Button CatalogButton(string text, bool primary = false) => new()
    {
        Content = text, Style = (Style)FindResource(primary ? "Primary" : "Action"),
        Margin = new Thickness(0, 0, 7, 0), MinHeight = 34
    };

    void SetJavDbCatalogPageButtons()
    {
        if (javDbCatalogPrevious is not null) javDbCatalogPrevious.IsEnabled = !javDbCatalogBusy && javDbCatalogBrowsing && javDbCatalogPage > 1;
        if (javDbCatalogNext is not null) javDbCatalogNext.IsEnabled = !javDbCatalogBusy && javDbCatalogBrowsing && javDbCatalogLinks?.SelectedItem is JavDbCatalogLink
            && javDbCatalogResults?.Items.Count > 0 && javDbCatalogPage < 20;
    }

    void ShowJavDbCatalogResults(IEnumerable<MovieMetadata> records, string caption)
    {
        var rows = records.Take(48).Select(record => new JavDbCatalogRow(record)).ToList();
        javDbCatalogResults!.ItemsSource = rows;
        javDbCatalogResults.SelectedIndex = rows.Count > 0 ? 0 : -1;
        javDbCatalogStatus!.Text = $"{caption} · {rows.Count} 部";
        SetJavDbCatalogPageButtons();
    }

    async void JavDbCatalogSearch(object sender, RoutedEventArgs e)
    {
        if (javDbCatalogBusy || javDbCatalogQuery is null) return;
        var query = javDbCatalogQuery.Text.Trim();
        if (query.Length == 0) { javDbCatalogStatus!.Text = "请先输入番号或作品标题。"; return; }
        await RunJavDbCatalogAsync(async () =>
        {
            var candidates = await ScrapeCandidateCache.GetOrFetchAsync(DataDir, "JavDB 手动作品搜索", query,
                () => JavDbScraper.SearchTitleAsync(query));
            javDbCatalogLinks!.ItemsSource = null;
            javDbCatalogPage = 1;
            ShowJavDbCatalogResults(candidates, candidates.Count == 0 ? "未找到可核对的公开候选；视频库未改变" : "作品候选；选择一个并读取关系");
        });
    }

    void JavDbCatalogUseSelectedVideo(object sender, RoutedEventArgs e)
    {
        var ids = selectedVideoIds.Take(2).ToList();
        if (ids.Count != 1 || !metadata.TryGetValue(ids[0], out var saved) ||
            !saved.Source.Equals("JavDB", StringComparison.OrdinalIgnoreCase))
        { javDbCatalogStatus!.Text = "请在视频库中只选中一部已从 JavDB 搜刮的视频；也可以在这里搜索作品。"; return; }
        javDbCatalogQuery!.Text = saved.Code;
        if (saved.CatalogLinks?.Count > 0)
        {
            javDbCatalogLinks!.ItemsSource = saved.CatalogLinks.OrderBy(link => link.Kind).ThenBy(link => link.Name).ToList();
            javDbCatalogLinks.SelectedIndex = 0;
            javDbCatalogStatus!.Text = $"已读取该视频保存的 {saved.CatalogLinks.Count} 条关系。";
        }
        else javDbCatalogStatus!.Text = "这部视频的旧资料没有保存关系链接；点击“搜索作品”，然后“读取所选作品的关系”。";
    }

    async void JavDbCatalogReadLinks(object sender, RoutedEventArgs e)
    {
        if (javDbCatalogBusy || javDbCatalogResults?.SelectedItem is not JavDbCatalogRow row) return;
        await RunJavDbCatalogAsync(async () =>
        {
            var details = (await ScrapeCandidateCache.GetOrFetchAsync(DataDir, "JavDB 详情", row.Metadata.SourceUrl,
                async () => [await JavDbScraper.FetchCandidateDetailsAsync(row.Metadata)])).First();
            var links = details.CatalogLinks?.OrderBy(link => link.Kind).ThenBy(link => link.Name).ToList() ?? [];
            javDbCatalogLinks!.ItemsSource = links;
            javDbCatalogLinks.SelectedIndex = links.Count > 0 ? 0 : -1;
            javDbCatalogStatus!.Text = links.Count > 0 ? $"已读取 {row.Metadata.Code} 的 {links.Count} 条公开关系；选择关系后浏览作品。"
                : "这条详情未提供可识别的演员、系列或标签链接；不会猜测目录地址。";
        });
    }

    async void JavDbCatalogBrowse(object sender, RoutedEventArgs e)
    {
        if (javDbCatalogBusy || javDbCatalogLinks?.SelectedItem is not JavDbCatalogLink) return;
        javDbCatalogPage = 1;
        javDbCatalogBrowsing = await LoadJavDbCatalogPageAsync();
        SetJavDbCatalogPageButtons();
    }

    async Task JavDbCatalogChangePageAsync(int offset)
    {
        if (javDbCatalogBusy || javDbCatalogLinks?.SelectedItem is not JavDbCatalogLink) return;
        var target = javDbCatalogPage + offset;
        if (target is < 1 or > 20) return;
        var previous = javDbCatalogPage;
        javDbCatalogPage = target;
        if (!await LoadJavDbCatalogPageAsync()) javDbCatalogPage = previous;
        SetJavDbCatalogPageButtons();
    }

    async Task<bool> LoadJavDbCatalogPageAsync()
    {
        if (javDbCatalogLinks?.SelectedItem is not JavDbCatalogLink link) return false;
        return await RunJavDbCatalogAsync(async () =>
        {
            var rows = await ScrapeCandidateCache.GetOrFetchAsync(DataDir, "JavDB 关系目录", link.Url + "|" + javDbCatalogPage,
                () => JavDbScraper.FetchCatalogPageAsync(link.Url, javDbCatalogPage));
            ShowJavDbCatalogResults(rows, $"{link.DisplayName} · 第 {javDbCatalogPage} 页");
        });
    }

    async void JavDbCatalogMatch(object sender, RoutedEventArgs e)
    {
        if (javDbCatalogBusy || javDbCatalogResults?.SelectedItem is not JavDbCatalogRow row) return;
        var video = ChooseVideoForTitleScrape();
        if (video is null) return;
        await RunJavDbCatalogAsync(async () =>
        {
            var details = (await ScrapeCandidateCache.GetOrFetchAsync(DataDir, "JavDB 详情", row.Metadata.SourceUrl,
                async () => [await JavDbScraper.FetchCandidateDetailsAsync(row.Metadata)])).First();
            await AdditionalScrapers.DownloadPosterAsync(details, video.Id, DataDir);
            await StoreScrapeReviewAsync(video, details, forcePending: true);
            RefreshReviewQueue(); RenderCards();
            javDbCatalogStatus!.Text = $"{details.Code} 已加入人工审核；通过前不会覆盖视频信息。";
        });
    }

    async Task<bool> RunJavDbCatalogAsync(Func<Task> operation)
    {
        javDbCatalogBusy = true; SetJavDbCatalogPageButtons();
        javDbCatalogStatus!.Text = "正在读取公开资料…";
        try { await operation(); return true; }
        catch (Exception ex)
        {
            javDbCatalogStatus.Text = "操作失败：" + ex.Message + " 请检查审核队列后再重试；不会据此标记视频为未找到。";
            Log("JavDB 关系浏览失败：" + ex.Message);
            return false;
        }
        finally { javDbCatalogBusy = false; SetJavDbCatalogPageButtons(); }
    }
}
