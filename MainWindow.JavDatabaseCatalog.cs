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
    sealed class JavDatabaseCatalogRow(MovieMetadata metadata)
    {
        public MovieMetadata Metadata { get; } = metadata;
        public string DisplayName => $"{Metadata.Code} · {Metadata.Title}";
    }

    TextBox? javDatabaseCatalogCode;
    ComboBox? javDatabaseCatalogIdols;
    ListBox? javDatabaseCatalogResults;
    TextBlock? javDatabaseCatalogStatus;
    Button? javDatabaseCatalogPrevious;
    Button? javDatabaseCatalogNext;
    JavDatabaseRelationPage? javDatabaseCurrentPage;
    readonly List<string> javDatabaseVisitedPages = [];
    int javDatabasePageIndex;
    bool javDatabaseCatalogBusy;

    void InitializeJavDatabaseCatalog(StackPanel parent, UIElement after)
    {
        var shell = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 227, 245)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13),
            Padding = new Thickness(15), Margin = new Thickness(0, 8, 0, 14)
        };
        var body = new StackPanel();
        shell.Child = body;
        body.Children.Add(new TextBlock { Text = "JAVDatabase 演员与系列作品浏览", FontSize = 16, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = "输入明确番号，先核对作品详情，再沿详情中的演员和系列链接手动浏览作品。这里只展示公开候选，匹配本地视频后仍须人工审核。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 11)
        });

        var codeRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        javDatabaseCatalogCode = new TextBox { Height = 35, Padding = new Thickness(9, 5, 9, 5),
            ToolTip = "只发送明确番号，不发送本地路径或标签" };
        var read = CatalogButton("核对作品并读取关系", true);
        read.Click += JavDatabaseCatalogReadActors;
        DockPanel.SetDock(read, Dock.Right); codeRow.Children.Add(read);
        codeRow.Children.Add(javDatabaseCatalogCode);
        body.Children.Add(codeRow);
        InitializeJavDatabaseActorSearch(body);

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var useSelected = CatalogButton("填入所选视频番号");
        useSelected.Click += (_, _) =>
        {
            var ids = selectedVideoIds.Take(2).ToList();
            if (ids.Count != 1 || !metadata.TryGetValue(ids[0], out var saved) || string.IsNullOrWhiteSpace(saved.Code))
            { javDatabaseCatalogStatus!.Text = "请只选中一部已有明确番号的视频。"; return; }
            javDatabaseCatalogCode!.Text = saved.Code;
            javDatabaseCatalogStatus!.Text = "已填入番号；点击“核对作品并读取关系”才会访问来源。";
        };
        var match = CatalogButton("匹配到本地视频…");
        match.Click += JavDatabaseCatalogMatch;
        actions.Children.Add(useSelected); actions.Children.Add(match);
        body.Children.Add(actions);

        var browseRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var browse = CatalogButton("浏览相关作品", true);
        browse.Click += JavDatabaseCatalogBrowse;
        DockPanel.SetDock(browse, Dock.Right); browseRow.Children.Add(browse);
        javDatabaseCatalogIdols = new ComboBox { Height = 35, DisplayMemberPath = nameof(JavDatabaseRelationLink.DisplayName),
            ToolTip = "只展示经番号详情核对后，该作品实际给出的演员和系列链接" };
        javDatabaseCatalogIdols.SelectionChanged += (_, _) => ClearJavDatabaseCatalogPages();
        browseRow.Children.Add(javDatabaseCatalogIdols);
        body.Children.Add(browseRow);

        InitializeCrossSourceRelationBrowser(body);

        javDatabaseCatalogStatus = new TextBlock
        {
            Text = "先输入明确番号并读取演员与系列关系。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        body.Children.Add(javDatabaseCatalogStatus);
        javDatabaseCatalogResults = new ListBox { Height = 190, DisplayMemberPath = nameof(JavDatabaseCatalogRow.DisplayName),
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 227, 245)) };
        body.Children.Add(javDatabaseCatalogResults);
        var pager = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        javDatabaseCatalogPrevious = CatalogButton("上一页");
        javDatabaseCatalogPrevious.Click += async (_, _) => await JavDatabaseCatalogChangePageAsync(-1);
        javDatabaseCatalogNext = CatalogButton("下一页");
        javDatabaseCatalogNext.Click += async (_, _) => await JavDatabaseCatalogChangePageAsync(1);
        pager.Children.Add(javDatabaseCatalogPrevious); pager.Children.Add(javDatabaseCatalogNext);
        body.Children.Add(pager);
        SetJavDatabaseCatalogPageButtons();
        parent.Children.Insert(parent.Children.IndexOf(after) + 1, shell);
    }

    void ClearJavDatabaseCatalogPages()
    {
        javDatabaseCurrentPage = null;
        javDatabaseVisitedPages.Clear();
        javDatabasePageIndex = 0;
        if (javDatabaseCatalogResults is not null) javDatabaseCatalogResults.ItemsSource = null;
        SetJavDatabaseCatalogPageButtons();
    }

    void SetJavDatabaseCatalogPageButtons()
    {
        if (javDatabaseCatalogPrevious is not null)
            javDatabaseCatalogPrevious.IsEnabled = !javDatabaseCatalogBusy && javDatabasePageIndex > 0;
        if (javDatabaseCatalogNext is not null)
            javDatabaseCatalogNext.IsEnabled = !javDatabaseCatalogBusy && javDatabaseCurrentPage?.NextPageUrl is not null;
    }

    async void JavDatabaseCatalogReadActors(object sender, RoutedEventArgs e)
    {
        if (javDatabaseCatalogBusy || javDatabaseCatalogCode is null) return;
        var code = javDatabaseCatalogCode.Text.Trim();
        if (code.Length is < 4 or > 40)
        { javDatabaseCatalogStatus!.Text = "请填写明确番号，例如 ABC-123。"; return; }
        await RunJavDatabaseCatalogAsync(async () =>
        {
            var detail = await JavDatabaseScraper.ScrapeByCodeAsync(code);
            var links = await JavDatabaseCatalog.FetchVerifiedCatalogLinksAsync(detail);
            ClearJavDatabaseCatalogPages();
            javDatabaseCatalogIdols!.ItemsSource = links;
            javDatabaseCatalogIdols.SelectedIndex = links.Count > 0 ? 0 : -1;
            javDatabaseCatalogStatus!.Text = links.Count > 0
                ? $"已核对 {detail.Code} 的公开详情，读取到 {links.Count} 条演员或系列关系；选择后浏览。"
                : "该作品详情没有可核对的演员或系列链接；不会猜测关系目录。";
        });
    }

    async void JavDatabaseCatalogBrowse(object sender, RoutedEventArgs e)
    {
        if (javDatabaseCatalogBusy || javDatabaseCatalogIdols?.SelectedItem is not JavDatabaseRelationLink link) return;
        ClearJavDatabaseCatalogPages();
        await LoadJavDatabaseCatalogPageAsync(link, link.Url, appendHistory: true);
    }

    async Task JavDatabaseCatalogChangePageAsync(int offset)
    {
        if (javDatabaseCatalogBusy || javDatabaseCatalogIdols?.SelectedItem is not JavDatabaseRelationLink link) return;
        if (offset < 0 && javDatabasePageIndex > 0)
            await LoadJavDatabaseCatalogPageAsync(link, javDatabaseVisitedPages[javDatabasePageIndex - 1], appendHistory: false);
        else if (offset > 0 && javDatabaseCurrentPage?.NextPageUrl is { } next)
            await LoadJavDatabaseCatalogPageAsync(link, next, appendHistory: true);
    }

    async Task LoadJavDatabaseCatalogPageAsync(JavDatabaseRelationLink link, string url, bool appendHistory)
    {
        await RunJavDatabaseCatalogAsync(async () =>
        {
            var page = await JavDatabaseCatalog.FetchCatalogPageAsync(link, url);
            if (appendHistory)
            {
                if (javDatabaseVisitedPages.Count > javDatabasePageIndex + 1)
                    javDatabaseVisitedPages.RemoveRange(javDatabasePageIndex + 1,
                        javDatabaseVisitedPages.Count - javDatabasePageIndex - 1);
                javDatabaseVisitedPages.Add(url);
                javDatabasePageIndex = javDatabaseVisitedPages.Count - 1;
            }
            else javDatabasePageIndex--;
            javDatabaseCurrentPage = page;
            var rows = page.Candidates.Select(item => new JavDatabaseCatalogRow(item)).ToList();
            javDatabaseCatalogResults!.ItemsSource = rows;
            javDatabaseCatalogResults.SelectedIndex = rows.Count > 0 ? 0 : -1;
            javDatabaseCatalogStatus!.Text = $"{link.Name} · 第 {page.Page} 页的前 {rows.Count} 部公开候选。选择作品后读取详情并提交人工审核。";
        });
    }

    async void JavDatabaseCatalogMatch(object sender, RoutedEventArgs e)
    {
        if (javDatabaseCatalogBusy || javDatabaseCatalogResults?.SelectedItem is not JavDatabaseCatalogRow row) return;
        var video = ChooseVideoForTitleScrape();
        if (video is null) return;
        await RunJavDatabaseCatalogAsync(async () =>
        {
            var detail = await JavDatabaseScraper.FetchCandidateDetailsAsync(row.Metadata);
            if (!detail.DetailFetched || !detail.DetailIdentityVerified ||
                !ScrapeCodeIdentity.Same(row.Metadata.Code, detail.Code))
                throw new InvalidOperationException("相关作品的详情未核对同一番号。 ");
            await AdditionalScrapers.DownloadPosterAsync(detail, video.Id, DataDir);
            await StoreScrapeReviewAsync(video, detail, forcePending: true);
            RefreshReviewQueue(); RenderCards();
            javDatabaseCatalogStatus!.Text = $"{detail.Code} 已加入人工审核；通过前不会修改该视频的资料。";
        });
    }

    async Task RunJavDatabaseCatalogAsync(Func<Task> operation)
    {
        javDatabaseCatalogBusy = true;
        SetJavDatabaseCatalogPageButtons();
        javDatabaseCatalogStatus!.Text = "正在读取公开资料…";
        try { await operation(); }
        catch (Exception ex)
        {
            javDatabaseCatalogStatus.Text = "读取失败：" + ex.Message + " 不会据此把视频标记为未找到。";
            Log("JAVDatabase 演员关系浏览失败：" + ex.Message);
        }
        finally { javDatabaseCatalogBusy = false; SetJavDatabaseCatalogPageButtons(); }
    }
}
