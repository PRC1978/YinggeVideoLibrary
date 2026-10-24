using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using StackPanel = System.Windows.Controls.StackPanel;
using MessageBox = System.Windows.MessageBox;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    TextBox? sourceWeightBox;
    CheckBox? bangumiEnabledBox;
    CheckBox? javDbTitleEnabledBox;
    CheckBox? javDatabaseEnabledBox;
    CheckBox? tvMazeEnabledBox;

    void InitializeCandidateSettings()
    {
        if (ScrapePanel.Child is not StackPanel panel) return;
        ScrapeSourceBox.Items.Add(new ComboBoxItem { Content = "全部已启用来源" });
        ScrapeSourceBox.Items.Add(new ComboBoxItem { Content = "Bangumi 动画 / 真人" });
        ScrapeSourceBox.Items.Add(new ComboBoxItem { Content = "JavDB 标题" });
        ScrapeSourceBox.SelectedIndex = 3;
        JavDbEnabledBox.IsChecked = config.EnableJavDb;
        PornhubEnabledBox.IsChecked = config.EnablePornhub;
        TmdbEnabledBox.IsChecked = config.EnableTmdb;
        var toggleGroup = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        javDatabaseEnabledBox = new CheckBox { Content = "启用 JAVDatabase 番号补充来源", IsChecked = config.EnableJavDatabase, Margin = new Thickness(0, 0, 0, 10) };
        javDbTitleEnabledBox = new CheckBox { Content = "启用 JavDB 标题候选", IsChecked = config.EnableJavDbTitle, Margin = new Thickness(0, 0, 0, 10) };
        bangumiEnabledBox = new CheckBox { Content = "启用 Bangumi 动画 / 真人作品", IsChecked = config.EnableBangumi, Margin = new Thickness(0, 0, 0, 10) };
        tvMazeEnabledBox = new CheckBox { Content = "启用 TVmaze 剧集", IsChecked = config.EnableTvMaze, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var box in new[] { javDatabaseEnabledBox, javDbTitleEnabledBox, bangumiEnabledBox, tvMazeEnabledBox }) toggleGroup.Children.Add(box);
        var folderHintBox = new CheckBox
        {
            Content = "标题不明确时，参考目录中的人物名字",
            IsChecked = config.UseFolderPersonHint,
            Margin = new Thickness(0, 3, 0, 5),
            ToolTip = "从视频目录向上检查到已添加的库文件夹；只使用判定为人物名的目录名称，不发送完整路径。"
        };
        toggleGroup.Children.Add(folderHintBox);
        panel.Children.Insert(panel.Children.IndexOf(TmdbEnabledBox) + 1, toggleGroup);
        void SaveSourceToggles(object? _, RoutedEventArgs __)
        {
            if (!IsLoaded) return;
            config.EnableJavDb = JavDbEnabledBox.IsChecked == true;
            config.EnableJavDatabase = javDatabaseEnabledBox.IsChecked == true;
            config.EnableJavDbTitle = javDbTitleEnabledBox.IsChecked == true;
            config.EnableBangumi = bangumiEnabledBox.IsChecked == true;
            config.EnablePornhub = PornhubEnabledBox.IsChecked == true;
            config.EnableTmdb = TmdbEnabledBox.IsChecked == true;
            config.EnableTvMaze = tvMazeEnabledBox.IsChecked == true;
            config.UseFolderPersonHint = folderHintBox.IsChecked == true;
            SaveConfig();
        }
        foreach (var box in new[] { JavDbEnabledBox, PornhubEnabledBox, TmdbEnabledBox, javDatabaseEnabledBox, javDbTitleEnabledBox, bangumiEnabledBox, tvMazeEnabledBox, folderHintBox })
        {
            box.Checked += SaveSourceToggles;
            box.Unchecked += SaveSourceToggles;
        }
        var section = new StackPanel { Margin = new Thickness(0, 5, 0, 14) };
        section.Children.Add(new TextBlock { Text = "来源可信度加权（0–30）", FontWeight = FontWeights.SemiBold });
        sourceWeightBox = new TextBox { Text = string.Join(", ", config.SourceTrustWeights.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")), Margin = new Thickness(0, 6, 0, 6), ToolTip = "例如 TMDB=12, JavDB=10, Bangumi=8" };
        section.Children.Add(sourceWeightBox);
        var button = new Button { Content = "保存来源权重", Style = (Style)FindResource("Action"), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        button.Click += (_, _) =>
        {
            try
            {
                var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in sourceWeightBox.Text.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var split = part.Split('=', 2);
                    if (split.Length != 2 || string.IsNullOrWhiteSpace(split[0]) || !int.TryParse(split[1], out var value) || value < 0 || value > 30)
                        throw new FormatException("请按 TMDB=12, TVmaze=8 格式填写 0–30 的权重。");
                    weights[split[0].Trim()] = value;
                }
                config.SourceTrustWeights = weights; SaveConfig();
                MessageBox.Show(this, "来源权重已保存。", "搜刮设置");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "来源权重", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        section.Children.Add(button);
        panel.Children.Insert(panel.Children.IndexOf(ScrapeSourceBox) + 1, section);
    }

    int SourceTrust(string source) => config.SourceTrustWeights
        .Where(pair => source.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
        .Select(pair => pair.Value).DefaultIfEmpty(0).Max();

    MovieMetadata? ChooseScrapeCandidate(VideoItem video, IReadOnlyList<(MovieMetadata candidate, int score)> candidates)
    {
        var dialog = new Window { Title = "选择搜刮候选", Owner = this, Width = 700, Height = 480, MinWidth = 530,
            MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var person = config.UseFolderPersonHint ? ScrapeSearchHints.FindPersonHint(video.Path, config.Folders) : null;
        var caption = new TextBlock { Text = person is null
            ? "按标题匹配和来源可信度排序。选定候选后会读取来源详情，并在审核资料中展示。"
            : $"按标题和来源排序；已参考目录人物线索「{person}」。选定后会读取详情并核对演员，完整路径不会提交给来源。",
            Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(caption, Dock.Top); layout.Children.Add(caption);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", Width = 82, Height = 32, Style = (Style)FindResource("Action") };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var accept = new Button { Content = "使用选中候选", Width = 132, Height = 32, Style = (Style)FindResource("Primary"), Margin = new Thickness(8, 0, 0, 0) };
        accept.Click += (_, _) => dialog.DialogResult = true;
        footer.Children.Add(cancel); footer.Children.Add(accept);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var list = new ListBox { SelectedIndex = 0 };
        foreach (var (candidate, score) in candidates)
        {
            var row = new StackPanel { Margin = new Thickness(8, 5, 8, 6) };
            row.Children.Add(new TextBlock { Text = candidate.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = $"匹配估分 {score}% · {candidate.Source} · {candidate.Code}", Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(0, 3, 0, 0) });
            var glimpse = string.Join(" · ", new[] { candidate.ReleaseDate, candidate.Rating, candidate.Summary }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (glimpse.Length > 0) row.Children.Add(new TextBlock { Text = glimpse, Foreground = System.Windows.Media.Brushes.SlateGray,
                FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
            list.Items.Add(row);
        }
        list.MouseDoubleClick += (_, _) => { if (list.SelectedIndex >= 0) dialog.DialogResult = true; };
        layout.Children.Add(list); dialog.Content = layout;
        return dialog.ShowDialog() == true && list.SelectedIndex >= 0 ? candidates[list.SelectedIndex].candidate : null;
    }

    VideoItem? ChooseVideoForTitleScrape()
    {
        if (videos.Count == 0) { MessageBox.Show(this, "视频库为空。", "完整资料搜刮"); return null; }
        var selected = videos.Where(video => selectedVideoIds.Contains(video.Id)).Take(2).ToList();
        if (selected.Count == 1) return selected[0];
        var dialog = new Window { Title = "选择要搜刮的视频", Owner = this, Width = 660, Height = 490,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var search = new TextBox { Height = 34, Margin = new Thickness(0, 0, 0, 10), ToolTip = "输入文件名或标题关键词" };
        DockPanel.SetDock(search, Dock.Top); layout.Children.Add(search);
        var list = new ListBox { DisplayMemberPath = "DisplayTitle" };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "取消", Width = 80, Style = (Style)FindResource("Action") };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var accept = new Button { Content = "读取完整资料", Width = 130, Style = (Style)FindResource("Primary") };
        accept.Click += (_, _) => { if (list.SelectedItem is VideoItem) dialog.DialogResult = true; };
        footer.Children.Add(cancel); footer.Children.Add(accept);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        void Filter()
        {
            var query = search.Text.Trim();
            list.ItemsSource = videos.Where(video => query.Length == 0 || video.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase) || video.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .Take(100).ToList();
            list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
        }
        search.TextChanged += (_, _) => Filter();
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is VideoItem) dialog.DialogResult = true; };
        Filter(); layout.Children.Add(list); dialog.Content = layout;
        dialog.Loaded += (_, _) => search.Focus();
        return dialog.ShowDialog() == true ? list.SelectedItem as VideoItem : null;
    }

    async Task<List<MovieMetadata>> SearchCandidatesAsync(string title, int selectedSource)
    {
        var sources = selectedSource == 3 ? new[] { 0, 1, 2, 4, 5 } : new[] { selectedSource };
        var results = new List<MovieMetadata>();
        var completedSources = 0;
        var failedSources = 0;
        async Task<(string name, IReadOnlyList<MovieMetadata>? found, Exception? error)> QueryAsync(int source)
        {
            var name = source switch { 1 => "Pornhub", 2 => "TVmaze", 4 => "Bangumi", 5 => "JavDB 标题", _ => "TMDB" };
            try
            {
                var found = await ScrapeCandidateCache.GetOrFetchAsync(DataDir, name, title, () => source switch
                {
                    1 => AdditionalScrapers.SearchPornhubAsync(title),
                    2 => AdditionalScrapers.SearchTvMazeAsync(title),
                    4 => BangumiScraper.SearchAsync(title),
                    5 => JavDbScraper.SearchTitleAsync(title),
                    _ => AdditionalScrapers.SearchTmdbAsync(title, config.TmdbToken)
                });
                return (name, found, null);
            }
            catch (Exception ex) { return (name, null, ex); }
        }
        var pending = new List<Task<(string name, IReadOnlyList<MovieMetadata>? found, Exception? error)>>();
        foreach (var source in sources)
        {
            if (selectedSource == 3 && (source == 0 && TmdbEnabledBox.IsChecked != true ||
                                        source == 1 && PornhubEnabledBox.IsChecked != true ||
                                        source == 2 && tvMazeEnabledBox?.IsChecked != true ||
                                        source == 4 && bangumiEnabledBox?.IsChecked != true ||
                                        source == 5 && javDbTitleEnabledBox?.IsChecked != true)) continue;
            if (source == 0 && string.IsNullOrWhiteSpace(config.TmdbToken)) continue;
            pending.Add(QueryAsync(source));
        }
        foreach (var outcome in await Task.WhenAll(pending))
        {
            if (outcome.error is null) { completedSources++; results.AddRange(outcome.found ?? []); }
            else { failedSources++; Log($"{outcome.name} 候选查询失败：{outcome.error.Message}"); }
        }
        if (completedSources == 0) throw new InvalidOperationException("没有可用的搜刮来源，或所有来源均请求失败；此次不会标记为无匹配。请检查 TMDB 凭据、网络和来源设置。");
        if (results.Count == 0 && failedSources > 0) throw new InvalidOperationException("部分来源请求失败，暂不能确定是否无匹配；此次不会标记为素人。");
        return results.GroupBy(item => item.Source + "|" + item.SourceUrl + "|" + item.Title, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
    }
}
