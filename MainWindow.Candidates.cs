using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Panel = System.Windows.Controls.Panel;
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
    CheckBox? aniListEnabledBox;
    CheckBox? javDbTitleEnabledBox;
    CheckBox? javDatabaseEnabledBox;
    CheckBox? tvMazeEnabledBox;
    CheckBox? wikidataEnabledBox;
    CheckBox? kitsuEnabledBox;
    CheckBox? tenraiEnabledBox;

    void InitializeCandidateSettings()
    {
        if (ScrapePanel.Child is not StackPanel panel) return;
        ScrapeSourceBox.Items.Add(new ComboBoxItem { Content = "全部已启用来源" });
        ScrapeSourceBox.Items.Add(new ComboBoxItem { Content = "Bangumi 动画 / 真人" });
        ScrapeSourceBox.Items.Add(new ComboBoxItem { Content = "JavDB 标题" });
        ScrapeSourceBox.Items.Add(new ComboBoxItem { Content = "AniList 动画" });
        ScrapeSourceBox.Items.Add(new ComboBoxItem { Content = "JAVDatabase 标题" });
        ScrapeSourceBox.SelectedIndex = 3;
        JavDbEnabledBox.IsChecked = config.EnableJavDb;
        PornhubEnabledBox.IsChecked = config.EnablePornhub;
        TmdbEnabledBox.IsChecked = config.EnableTmdb;
        TmdbEnabledBox.ToolTip = "TMDB 只查询明确归入普通电影/剧集、且没有成人或番号标识的视频；类型未知时后台自动跳过，其他已启用来源仍会尝试。";
        var toggleGroup = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        toggleGroup.Children.Add(new TextBlock
        {
            Text = "TMDB 范围：仅普通电影/剧集。未分类或成人/番号视频会跳过 TMDB；其余已启用来源照常搜刮。单独手动查询 TMDB 时会再次确认。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        javDatabaseEnabledBox = new CheckBox { Content = "启用 JAVDatabase 番号及标题候选", IsChecked = config.EnableJavDatabase, Margin = new Thickness(0, 0, 0, 10) };
        javDbTitleEnabledBox = new CheckBox { Content = "启用 JavDB 标题候选", IsChecked = config.EnableJavDbTitle, Margin = new Thickness(0, 0, 0, 10) };
        bangumiEnabledBox = new CheckBox { Content = "启用 Bangumi 动画 / 真人作品", IsChecked = config.EnableBangumi, Margin = new Thickness(0, 0, 0, 10) };
        aniListEnabledBox = new CheckBox { Content = "启用 AniList 动画（手动搜索；不用于全量队列）", IsChecked = config.EnableAniList,
            Margin = new Thickness(0, 0, 0, 10), ToolTip = "AniList 官方 API 禁止大量采集；手动搜刮会按官方限速请求。" };
        tvMazeEnabledBox = new CheckBox { Content = "启用 TVmaze 剧集", IsChecked = config.EnableTvMaze, Margin = new Thickness(0, 0, 0, 10) };
        wikidataEnabledBox = new CheckBox { Content = "启用 Wikidata 电影 / 剧集 / 动画", IsChecked = config.EnableWikidata,
            Margin = new Thickness(0, 0, 0, 10), ToolTip = "无需密钥；默认关闭。启用后会参与标题队列，所有候选仍须核对。" };
        kitsuEnabledBox = new CheckBox { Content = "启用 Kitsu 动画候选", IsChecked = config.EnableKitsu,
            Margin = new Thickness(0, 0, 0, 10), ToolTip = "无需密钥；默认关闭。启用后参与标题队列，请在动画库使用。详情核对动画 ID、标题和年份后只进入人工审核。" };
        tenraiEnabledBox = new CheckBox { Content = "启用 Tenrai 动画候选（MyAnimeList）", IsChecked = config.EnableTenrai,
            Margin = new Thickness(0, 0, 0, 10), ToolTip = "无需密钥；默认关闭。Tenrai 提供缓存的 MyAnimeList 动画资料；后台每次请求至少间隔 5 秒，核对编号和标题后只进入人工审核。" };
        foreach (var box in new[] { javDatabaseEnabledBox, javDbTitleEnabledBox, bangumiEnabledBox, aniListEnabledBox, tvMazeEnabledBox, wikidataEnabledBox, kitsuEnabledBox, tenraiEnabledBox }) toggleGroup.Children.Add(box);
        var ordinaryPreset = new Button
        {
            Content = "启用普通影视与动画公开来源",
            Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 5),
            ToolTip = "开启 TVmaze、Bangumi、Wikidata 和 Kitsu；不会启动搜刮。Tenrai 动画来源须单独勾选；需要密钥的 TMDB 与 OMDb 仍按各自设置。"
        };
        toggleGroup.Children.Add(ordinaryPreset);
        toggleGroup.Children.Add(new TextBlock
        {
            Text = "开启后可用于后续标题搜刮；详情仍核对作品身份并进入人工审核。AniList 与 OMDb 保持单部手动查询。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var folderHintBox = new CheckBox
        {
            Content = "标题不明确时，参考目录中的人物名字",
            IsChecked = config.UseFolderPersonHint,
            Margin = new Thickness(0, 3, 0, 5),
            ToolTip = "从视频目录向上检查到已添加的库文件夹；只使用判定为人物名的目录名称，不发送完整路径。"
        };
        toggleGroup.Children.Add(folderHintBox);
        panel.Children.Insert(panel.Children.IndexOf(TmdbEnabledBox) + 1, toggleGroup);
        var applyingSourcePreset = false;
        void SaveSourceToggles(object? _, RoutedEventArgs __)
        {
            if (!IsLoaded || applyingSourcePreset) return;
            config.EnableJavDb = JavDbEnabledBox.IsChecked == true;
            config.EnableJavDatabase = javDatabaseEnabledBox.IsChecked == true;
            config.EnableJavDbTitle = javDbTitleEnabledBox.IsChecked == true;
            config.EnableBangumi = bangumiEnabledBox.IsChecked == true;
            config.EnableAniList = aniListEnabledBox.IsChecked == true;
            config.EnablePornhub = PornhubEnabledBox.IsChecked == true;
            config.EnableTmdb = TmdbEnabledBox.IsChecked == true;
            config.EnableTvMaze = tvMazeEnabledBox.IsChecked == true;
            config.EnableWikidata = wikidataEnabledBox.IsChecked == true;
            config.EnableKitsu = kitsuEnabledBox.IsChecked == true;
            config.EnableTenrai = tenraiEnabledBox.IsChecked == true;
            config.UseFolderPersonHint = folderHintBox.IsChecked == true;
            SaveConfig();
            RefreshSourceDiagnostics();
        }
        foreach (var box in new[] { JavDbEnabledBox, PornhubEnabledBox, TmdbEnabledBox, javDatabaseEnabledBox, javDbTitleEnabledBox, bangumiEnabledBox, aniListEnabledBox, tvMazeEnabledBox, wikidataEnabledBox, kitsuEnabledBox, tenraiEnabledBox, folderHintBox })
        {
            box.Checked += SaveSourceToggles;
            box.Unchecked += SaveSourceToggles;
        }
        ordinaryPreset.Click += (_, _) =>
        {
            applyingSourcePreset = true;
            try
            {
                tvMazeEnabledBox.IsChecked = true;
                bangumiEnabledBox.IsChecked = true;
                wikidataEnabledBox.IsChecked = true;
                kitsuEnabledBox.IsChecked = true;
            }
            finally { applyingSourcePreset = false; }
            SaveSourceToggles(null, new RoutedEventArgs());
        };
        var section = new StackPanel { Margin = new Thickness(0, 5, 0, 14) };
        section.Children.Add(new TextBlock { Text = "来源可信度加权（0–30）", FontWeight = FontWeights.SemiBold });
        sourceWeightBox = new TextBox { Text = SourceWeightEditorText(config.SourceTrustWeights), Margin = new Thickness(0, 6, 0, 6), ToolTip = "例如 TMDB=12, JavDB=10, OMDb=7" };
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
        if (ReviewSelectAllButton.Parent is Panel reviewActions)
        {
            var compare = new Button { Content = "比较已存标题候选", Style = (Style)FindResource("Action"),
                ToolTip = "从审核选择中打开，或查找视频；离线查看最多 12 个候选及原始字段出处。" };
            compare.Click += async (_, _) =>
            {
                var selectedId = reviewSelection.Count == 1 ? reviewSelection.First() : null;
                var selected = selectedId is null ? null : videos.FirstOrDefault(video => video.Id == selectedId);
                selected ??= ChooseVideoForTitleScrape("选择要比较候选的视频", "查看候选");
                if (selected is null) return;
                var snapshot = await Task.Run(() => ScrapeCandidateCache.LoadTitleCandidates(DataDir, selected.Id));
                if (snapshot is null)
                {
                    MessageBox.Show(this, "这个视频尚无已保存的标题候选。先在搜刮页按标题搜索，或等待标题队列处理。", "候选比较");
                    return;
                }
                ShowTitleCandidateComparison(selected, snapshot);
            };
            reviewActions.Children.Add(compare);
        }
        InitializeJavDbCatalog(panel, toggleGroup);
        InitializeSourceDiagnostics(panel, toggleGroup);
    }

    internal static string SourceWeightEditorText(IReadOnlyDictionary<string, int> weights)
        => string.Join(", ", weights.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));

    int SourceTrust(string source) => config.SourceTrustWeights
        .Where(pair => source.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
        .Select(pair => pair.Value).DefaultIfEmpty(0).Max();

    ScrapeCandidateCache.TitleCandidateSnapshot BuildTitleCandidateSnapshot(VideoItem video, string query,
        IEnumerable<MovieMetadata> candidates)
    {
        var now = DateTime.UtcNow;
        var ranked = candidates.Select(candidate =>
        {
            var local = LocalScrapeConfidence(video, candidate, out var reason);
            var weight = SourceTrust(candidate.Source);
            var search = candidate.Clone();
            if (!search.DetailFetched && search.FieldProvenance?.Count is not > 0)
                search.StampAvailableFields("搜索结果");
            return new ScrapeCandidateCache.RankedTitleCandidate
            {
                LocalScore = local,
                SourceWeight = weight,
                Score = Math.Clamp(local + weight, 0, 100),
                Evidence = reason + $"；来源权重 +{weight}；标题候选须人工核对",
                SearchMetadata = search,
                DetailMetadata = candidate.DetailFetched ? candidate.Clone() : null,
                DetailStatus = candidate.DetailFetched
                    ? candidate.DetailIdentityVerified ? "verified" : "identity-unverified"
                    : "search-only",
                DetailFetchedUtc = candidate.DetailFetched ? now : null
            };
        });
        return new ScrapeCandidateCache.TitleCandidateSnapshot
        {
            VideoId = video.Id, Query = query.Trim(), SearchedUtc = now,
            ExpiresUtc = now.AddDays(3),
            Candidates = ScrapeCandidateCache.OrderTitleCandidates(ranked)
        };
    }

    void ShowTitleCandidateComparison(VideoItem video, ScrapeCandidateCache.TitleCandidateSnapshot snapshot)
    {
        var dialog = new Window { Title = "标题候选比较", Owner = this, Width = 980, Height = 660,
            MinWidth = 720, MinHeight = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(16) };
        var summary = new TextBlock
        {
            Text = $"{video.DisplayTitle} · {snapshot.Candidates.Count} 个候选 · {snapshot.SearchedUtc.ToLocalTime():yyyy-MM-dd HH:mm} 搜索" +
                (snapshot.NeedsRefresh ? " · 搜索结果已过期，待联网刷新" : " · 可离线查看"),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(summary, Dock.Top); root.Children.Add(summary);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = new ListBox();
        foreach (var candidate in snapshot.Candidates)
        {
            var label = new StackPanel { Margin = new Thickness(6, 5, 6, 5) };
            label.Children.Add(new TextBlock { Text = $"{candidate.Rank}. {candidate.SearchMetadata.Title}",
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 54 });
            label.Children.Add(new TextBlock { Text = $"估分 {candidate.Score}% · {candidate.SearchMetadata.Source} · {candidate.SearchMetadata.Code}",
                Foreground = Brushes.SlateGray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            list.Items.Add(new ListBoxItem { Content = label, Tag = candidate });
        }
        Grid.SetColumn(list, 0); body.Children.Add(list);
        var detail = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        var scroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2); body.Children.Add(scroll);
        void RenderComparison()
        {
            detail.Children.Clear();
            if (list.SelectedItem is not ListBoxItem { Tag: ScrapeCandidateCache.RankedTitleCandidate row }) return;
            var source = row.SearchMetadata;
            var full = row.DetailMetadata;
            var stage = row.DetailStatus switch
            {
                "verified" => "详情已核对身份",
                "identity-unverified" => "详情身份未确认，不能自动采用",
                "source-unavailable" => "详情来源暂不可用，仍可比较搜索摘要",
                "detail-error" => "详情读取失败，仍可比较搜索摘要",
                _ => "仅有搜索摘要"
            };
            detail.Children.Add(new TextBlock { Text = $"第 {row.Rank} 位 · 初步匹配 {row.Score}% · {stage}",
                FontWeight = FontWeights.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap });
            detail.Children.Add(new TextBlock { Text = $"本地视频：{video.DisplayTitle}\n查询线索：{snapshot.Query}\n匹配依据：{row.Evidence}",
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 7, 0, 10) });
            if (source.Source.Equals("Trakt", StringComparison.OrdinalIgnoreCase))
            {
                static string Ids(MovieMetadata item) => string.Join(" · ",
                    new[] { "trakt", "imdb", "tmdb" }.Where(key => item.ExternalIds?.ContainsKey(key) == true)
                        .Select(key => key.ToUpperInvariant() + " " + item.ExternalIds[key]));
                detail.Children.Add(new TextBlock
                {
                    Text = "搜索编号：" + Ids(source) + (full is null ? "" : "\n详情编号：" + Ids(full)),
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkBlue,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }
            static string JoinValues(IEnumerable<string>? values) => string.Join("、", values ?? []);
            var fields = new (string Label, string Key, Func<MovieMetadata, string?> Value)[]
            {
                ("标题", nameof(MovieMetadata.Title), item => item.Title),
                ("编号", nameof(MovieMetadata.Code), item => item.Code),
                ("原名", nameof(MovieMetadata.OriginalTitle), item => item.OriginalTitle),
                ("人物", nameof(MovieMetadata.Actors), item => JoinValues(item.Actors)),
                ("发布日期", nameof(MovieMetadata.ReleaseDate), item => item.ReleaseDate),
                ("工作室 / 发行", nameof(MovieMetadata.Studio), item => item.Studio),
                ("片商 / 制作方", nameof(MovieMetadata.Maker), item => item.Maker),
                ("厂牌", nameof(MovieMetadata.Label), item => item.Label),
                ("系列", nameof(MovieMetadata.Series), item => item.Series),
                ("导演", nameof(MovieMetadata.Director), item => item.Director),
                ("类别", nameof(MovieMetadata.Genres), item => JoinValues(item.Genres)),
                ("封面", nameof(MovieMetadata.PosterUrl), item => item.PosterUrl),
                ("来源评分", nameof(MovieMetadata.Rating), item => item.Rating),
                ("简介", nameof(MovieMetadata.Summary), item => item.Summary)
            };
            foreach (var field in fields)
            {
                var before = field.Value(source)?.Trim() ?? "";
                var after = full is null ? "" : field.Value(full)?.Trim() ?? "";
                if (before.Length == 0 && after.Length == 0) continue;
                var provenance = full?.FieldProvenance?.GetValueOrDefault(field.Key)
                    ?? source.FieldProvenance?.GetValueOrDefault(field.Key);
                var sourceText = provenance is null ? "旧记录未标注字段出处" :
                    $"{provenance.Source} · {provenance.Stage} · {provenance.SourceUrl}";
                detail.Children.Add(new TextBlock { Text = field.Label, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 7, 0, 2) });
                detail.Children.Add(new TextBlock { Text = before.Length == 0 ? "搜索摘要：未提供" : "搜索摘要：" + before,
                    TextWrapping = TextWrapping.Wrap });
                if (full is not null)
                    detail.Children.Add(new TextBlock { Text = after.Length == 0 ? "详情：未提供" : "详情：" + after,
                        TextWrapping = TextWrapping.Wrap, Foreground = before == after ? Brushes.SlateGray : Brushes.DarkBlue });
                detail.Children.Add(new TextBlock { Text = "出处：" + sourceText, TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.SlateGray, FontSize = 11 });
            }
        }
        list.SelectionChanged += (_, _) => RenderComparison();
        list.SelectedIndex = snapshot.Candidates.Count > 0 ? 0 : -1;
        root.Children.Add(body); dialog.Content = root;
        dialog.ShowDialog();
    }

    MovieMetadata? ChooseScrapeCandidate(VideoItem video, IReadOnlyList<(MovieMetadata candidate, int score)> candidates)
    {
        var snapshot = BuildTitleCandidateSnapshot(video, "手动标题查询（原输入未记录）",
            candidates.Select(item => item.candidate));
        var previous = ScrapeCandidateCache.LoadTitleCandidates(DataDir, video.Id);
        if (previous is not null)
            foreach (var row in snapshot.Candidates)
            {
                var prior = previous.Candidates.FirstOrDefault(old =>
                    string.Equals(old.SearchMetadata.Source, row.SearchMetadata.Source, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(old.SearchMetadata.SourceUrl, row.SearchMetadata.SourceUrl, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(old.SearchMetadata.Code, row.SearchMetadata.Code, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(old.SearchMetadata.Title, row.SearchMetadata.Title, StringComparison.OrdinalIgnoreCase));
                if (row.DetailMetadata is null && prior?.DetailMetadata is not null)
                {
                    row.DetailMetadata = prior.DetailMetadata.Clone();
                    row.DetailStatus = prior.DetailStatus;
                    row.DetailFetchedUtc = prior.DetailFetchedUtc;
                }
            }
        ScrapeCandidateCache.SaveTitleCandidates(DataDir, snapshot);
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
            if (candidate.Source.Equals("Trakt", StringComparison.OrdinalIgnoreCase))
                row.Children.Add(new TextBlock { Text = candidate.DetailNote,
                    Foreground = System.Windows.Media.Brushes.SlateGray, FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
            list.Items.Add(row);
        }
        list.MouseDoubleClick += (_, _) => { if (list.SelectedIndex >= 0) dialog.DialogResult = true; };
        layout.Children.Add(list); dialog.Content = layout;
        return dialog.ShowDialog() == true && list.SelectedIndex >= 0 ? candidates[list.SelectedIndex].candidate : null;
    }

    VideoItem? ChooseVideoForTitleScrape(string dialogTitle = "选择要搜刮的视频", string actionTitle = "读取完整资料")
    {
        if (videos.Count == 0) { MessageBox.Show(this, "视频库为空。", "完整资料搜刮"); return null; }
        var selected = videos.Where(video => selectedVideoIds.Contains(video.Id)).Take(2).ToList();
        if (selected.Count == 1) return selected[0];
        var dialog = new Window { Title = dialogTitle, Owner = this, Width = 660, Height = 490,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var search = new TextBox { Height = 34, Margin = new Thickness(0, 0, 0, 10), ToolTip = "输入文件名或标题关键词" };
        DockPanel.SetDock(search, Dock.Top); layout.Children.Add(search);
        var list = new ListBox { DisplayMemberPath = "DisplayTitle" };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "取消", Width = 80, Style = (Style)FindResource("Action") };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var accept = new Button { Content = actionTitle, Width = 130, Style = (Style)FindResource("Primary") };
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

    async Task<List<MovieMetadata>> SearchCandidatesAsync(string title, int selectedSource, bool bulk = false,
        CancellationToken cancellationToken = default, JavinfoSearchEvidence? javinfoEvidence = null,
        VideoItem? video = null)
    {
        var tmdbScope = TmdbUsagePolicy.Evaluate(video,
            video is null ? null : metadata.GetValueOrDefault(video.Id), title);
        if (selectedSource == 0 && !tmdbScope.Allowed)
            throw new InvalidOperationException("TMDB 只用于明确的普通电影或剧集：" + tmdbScope.Reason + "。可继续使用其他来源。");
        if (selectedSource == 8)
            RequireManualJavinfoEvidence(bulk, config.EnableJavinfo, javinfoEvidence, config.JavinfoKey);
        if (selectedSource == 10)
            RequireManualOmdb(bulk, config.EnableOmdb, OmdbScraper.CredentialReady());
        if (selectedSource == 13)
            RequireManualTrakt(bulk, config.EnableTrakt, TraktScraper.CredentialReady());
        if (selectedSource != 3 && selectedSource is not (8 or 10 or 13))
            RequireManualSourceEnabled(selectedSource, selectedSource switch
            {
                0 => TmdbEnabledBox.IsChecked == true,
                1 => PornhubEnabledBox.IsChecked == true,
                2 => tvMazeEnabledBox?.IsChecked == true,
                4 => bangumiEnabledBox?.IsChecked == true,
                5 => javDbTitleEnabledBox?.IsChecked == true,
                6 => aniListEnabledBox?.IsChecked == true,
                7 => javDatabaseEnabledBox?.IsChecked == true,
                9 => wikidataEnabledBox?.IsChecked == true,
                11 => kitsuEnabledBox?.IsChecked == true,
                12 => tenraiEnabledBox?.IsChecked == true,
                _ => false
            });
        if (selectedSource == 0 && string.IsNullOrWhiteSpace(config.TmdbToken))
            throw new InvalidOperationException("请先在搜刮页保存 TMDB API Read Access Token。");
        var sources = PlanCandidateSources(selectedSource, bulk);
        var results = new List<MovieMetadata>();
        var completedSources = 0;
        var failedSources = 0;
        async Task<(string name, IReadOnlyList<MovieMetadata>? found, Exception? error)> QueryAsync(int source)
        {
            var name = source switch { 1 => "Pornhub", 2 => "TVmaze", 4 => "Bangumi", 5 => "JavDB 标题", 6 => "AniList", 7 => "JAVDatabase 标题", 8 => "Javinfo 手动", 9 => "Wikidata", 10 => "OMDb", 11 => "Kitsu", 12 => "Tenrai", 13 => "Trakt", _ => "TMDB" };
            var sourceKey = source switch { 5 => "JavDB", 7 => "JAVDatabase", _ => name };
            try
            {
                Task<List<MovieMetadata>> Fetch() => source switch
                {
                    1 => AdditionalScrapers.SearchPornhubAsync(title),
                    2 => AdditionalScrapers.SearchTvMazeAsync(title),
                    4 => BangumiScraper.SearchAsync(title),
                    5 => JavDbScraper.SearchTitleAsync(title),
                    6 => AniListScraper.SearchAsync(title),
                    7 => JavDatabaseScraper.SearchTitleAsync(title),
                    8 => JavinfoScraper.SearchAsync(javinfoEvidence!, config.JavinfoKey, DataDir, config.JavinfoDailyRequestLimit),
                    9 => WikidataScraper.SearchAsync(title, cancellationToken),
                    10 => OmdbScraper.SearchAsync(title, cancellationToken),
                    11 => KitsuScraper.SearchAsync(title, cancellationToken),
                    12 => TenraiScraper.SearchAsync(title, cancellationToken),
                    13 => TraktScraper.SearchAsync(title, cancellationToken),
                    _ => AdditionalScrapers.SearchTmdbAsync(title, config.TmdbToken)
                };
                // The cache itself decides whether a network request is needed.
                // A valid local hit must remain usable while this source cools.
                var cacheQuery = source == 8 ? javinfoEvidence!.Code : title;
                var found = await ScrapeCandidateCache.GetOrFetchAsync(DataDir, name, cacheQuery,
                    () => bulk ? RunQueueSourceAsync(sourceKey, Fetch, cancellationToken) : Fetch())
                    .WaitAsync(cancellationToken);
                // Old snapshots can contain several loosely matched numbers.
                // Never offer or enrich a paid candidate with a different code.
                if (source == 8)
                    found = JavinfoScraper.ExactNumberCandidates(found, javinfoEvidence!);
                return (name, found, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ScrapeNotFoundException) when (bulk) { return (name, [], null); }
            catch (Exception ex) { return (name, null, ex); }
        }
        var pending = new List<Task<(string name, IReadOnlyList<MovieMetadata>? found, Exception? error)>>();
        foreach (var source in sources)
        {
            if (selectedSource == 3 && (source == 0 && TmdbEnabledBox.IsChecked != true ||
                                        source == 1 && PornhubEnabledBox.IsChecked != true ||
                                        source == 2 && tvMazeEnabledBox?.IsChecked != true ||
                                        source == 4 && bangumiEnabledBox?.IsChecked != true ||
                                        source == 5 && javDbTitleEnabledBox?.IsChecked != true ||
                                        source == 6 && aniListEnabledBox?.IsChecked != true ||
                                        source == 7 && javDatabaseEnabledBox?.IsChecked != true ||
                                        source == 9 && wikidataEnabledBox?.IsChecked != true ||
                                        source == 11 && kitsuEnabledBox?.IsChecked != true ||
                                        source == 12 && tenraiEnabledBox?.IsChecked != true)) continue;
            if (bulk && source == 6) continue; // AniList terms prohibit mass collection.
            if (source == 0 && (!tmdbScope.Allowed || string.IsNullOrWhiteSpace(config.TmdbToken))) continue;
            pending.Add(QueryAsync(source));
        }
        var outcomes = await Task.WhenAll(pending).WaitAsync(cancellationToken);
        foreach (var outcome in outcomes)
        {
            if (outcome.error is null) { completedSources++; results.AddRange(outcome.found ?? []); }
            else
            {
                failedSources++;
                if (selectedSource is 8 or 10 or 11 or 12 or 13) throw outcome.error;
                Log($"{outcome.name} 候选查询失败：{outcome.error.Message}");
            }
        }
        if (bulk && results.Count == 0 && failedSources > 0)
            throw BuildBulkSourceFailure(outcomes.Where(outcome => outcome.error is not null)
                .Select(outcome => (outcome.name, outcome.error!)), "候选查询");
        if (completedSources == 0) throw new InvalidOperationException("没有可用的搜刮来源，或所有来源均请求失败；此次不会标记为无匹配。请检查 TMDB 凭据、网络和来源设置。");
        if (results.Count == 0 && failedSources > 0) throw new InvalidOperationException("部分来源请求失败，暂不能确定是否无匹配；此次不会标记为素人。");
        return results.Where(item => !item.Source.StartsWith("TMDB", StringComparison.OrdinalIgnoreCase) ||
                tmdbScope.Allowed && !item.IsAdultContent &&
                !TmdbUsagePolicy.HasAdultCue(item.Title) && !TmdbUsagePolicy.HasAdultCue(item.Summary))
            .GroupBy(item => item.Source + "|" + item.SourceUrl + "|" + item.Title,
                StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
    }

    internal static int[] PlanCandidateSources(int selectedSource, bool bulk)
    {
        if (selectedSource is < 0 or > 13) throw new ArgumentOutOfRangeException(nameof(selectedSource));
        if (bulk && selectedSource == 8)
            throw new InvalidOperationException("Javinfo 只能手动选择，不能进入批量搜刮。");
        if (bulk && selectedSource == 6)
            throw new InvalidOperationException("AniList 只供单部手动搜索，不能进入批量搜刮。");
        if (bulk && selectedSource == 10)
            throw new InvalidOperationException("OMDb 只能手动选择，不能进入批量搜刮。");
        if (bulk && selectedSource == 13)
            throw new InvalidOperationException("Trakt 只能手动选择，不能进入批量搜刮。");
        return selectedSource == 3 ? [0, 1, 2, 4, 5, 6, 7, 9, 11, 12] : [selectedSource];
    }

    internal static void RequireManualTrakt(bool bulk, bool enabled, bool credentialReady)
    {
        if (bulk || !enabled)
            throw new InvalidOperationException("Trakt 只供单部手动查询，请先在搜刮页启用并单独选择它。");
        if (!credentialReady)
            throw new InvalidOperationException("请先在搜刮页保存当前 Windows 用户的 Trakt Client ID。");
    }

    internal static void RequireManualOmdb(bool bulk, bool enabled, bool credentialReady)
    {
        if (bulk || !enabled)
            throw new InvalidOperationException("OMDb 只供单部手动查询，请先在搜刮页启用并单独选择它。");
        if (!credentialReady)
            throw new InvalidOperationException("请先在搜刮页保存 OMDb API Key。");
    }

    internal static void RequireManualSourceEnabled(int selectedSource, bool enabled)
    {
        var name = selectedSource switch
        {
            0 => "TMDB", 1 => "Pornhub", 2 => "TVmaze", 4 => "Bangumi", 5 => "JavDB 标题",
            6 => "AniList", 7 => "JAVDatabase", 9 => "Wikidata", 11 => "Kitsu", 12 => "Tenrai",
            _ => throw new ArgumentOutOfRangeException(nameof(selectedSource))
        };
        if (!enabled) throw new InvalidOperationException($"请先在搜刮设置中启用 {name} 来源。");
    }

    internal static void RequireManualJavinfoEvidence(bool bulk, bool enabled,
        JavinfoSearchEvidence? evidence, string key)
    {
        if (bulk || !enabled)
            throw new InvalidOperationException("Javinfo 为付费手动来源，请先在搜刮页启用，再单独选择它搜索。");
        if (evidence is null)
            throw new InvalidOperationException("Javinfo 需要明确番号和已核对的日文作品原名；本次不会发出付费请求。");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("请先在搜刮页保存 Javinfo API Key。");
    }

    static Exception BuildBulkSourceFailure(IEnumerable<(string Name, Exception Error)> failures, string operation)
    {
        var list = failures.ToArray();
        var permanent = list.FirstOrDefault(item => !IsTemporaryScrapeFailure(item.Error));
        var temporary = list.Where(item => IsTemporaryScrapeFailure(item.Error)).ToArray();
        static string DescribePermanent((string Name, Exception Error) failure)
        {
            HttpStatusCode? status = failure.Error switch
            {
                ScrapeHttpException http => http.StatusCode,
                HttpRequestException http => http.StatusCode,
                _ => null
            };
            var reason = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? $"HTTP {(int)status.Value}，请检查该来源的 API 凭据或访问权限"
                : failure.Error.Message;
            if (reason.Length > 180) reason = reason[..180];
            return $"{failure.Name} · {reason}";
        }
        // A permanent failure from one source does not rule out another source
        // that is only cooling. Keep the item until that source gets its turn.
        if (temporary.Length > 0)
        {
            var next = temporary.Select(item => (item.Error as ScrapeSourceUnavailableException)?.RetryAt)
                .Where(at => at.HasValue).Select(at => at!.Value)
                .DefaultIfEmpty(DateTimeOffset.UtcNow.AddSeconds(30)).Min();
            var unavailable = string.Join("、", temporary.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            var permanentNote = permanent.Error is null ? "" : $"；{DescribePermanent(permanent)}";
            return new ScrapeSourceUnavailableException(
                $"{operation}：{unavailable} 暂时不可用，任务已保留并等待该来源恢复{permanentNote}。", next);
        }
        if (permanent.Error is not null)
        {
            return new InvalidDataException($"{operation}失败：{DescribePermanent(permanent)}。已停止此视频自动重试；修正来源后可点击“重试失败”。");
        }
        return new InvalidDataException($"{operation}没有可用来源；请检查搜刮设置后手动重试。");
    }

    internal static bool VerifyBulkSourceFailureForDiagnostics()
    {
        var unauthorized = (Exception)new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
        var limited = (Exception)new ScrapeSourceUnavailableException("rate limited", DateTimeOffset.UtcNow.AddMinutes(2));
        var credentialError = BuildBulkSourceFailure([("TMDB", unauthorized)], "候选查询");
        var mixedError = BuildBulkSourceFailure([("TMDB", unauthorized), ("Bangumi", limited)], "候选查询");
        var reversedMixedError = BuildBulkSourceFailure([("Bangumi", limited), ("TMDB", unauthorized)], "候选查询");
        var cooldown = BuildBulkSourceFailure([("Bangumi", limited)], "候选查询");
        return credentialError is InvalidDataException && credentialError.Message.Contains("HTTP 401", StringComparison.Ordinal)
            && mixedError is ScrapeSourceUnavailableException { RetryAt: { } mixedAt }
            && mixedAt > DateTimeOffset.UtcNow && mixedError.Message.Contains("HTTP 401", StringComparison.Ordinal)
            && reversedMixedError is ScrapeSourceUnavailableException
            && cooldown is ScrapeSourceUnavailableException { RetryAt: { } retryAt }
            && retryAt > DateTimeOffset.UtcNow;
    }
}
