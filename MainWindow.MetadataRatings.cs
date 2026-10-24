using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    sealed record RatingProbe(string Source, MovieMetadata? Details, PlatformRating? Rating, string? Error);

    void AddMetadataRatings(StackPanel parent, Window dialog, MovieMetadata record, VideoItem? video, bool saved)
    {
        var section = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(221, 230, 245)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12)
        };
        var content = new StackPanel();
        section.Child = content;
        parent.Children.Add(section);
        content.Children.Add(new TextBlock { Text = "作品评分", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(35, 49, 74)) });
        content.Children.Add(new TextBlock { Text = "个人评分与站点评分分别保存；匹配可信度只说明搜刮结果是否对应这部视频。",
            Margin = new Thickness(0, 3, 0, 9), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(115, 130, 156)) });
        content.Children.Add(new TextBlock { Text = "综合分将各站原分制换算到 10 分，并限制票数权重；原始分数始终单独显示。",
            Margin = new Thickness(0, 0, 0, 9), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(115, 130, 156)) });

        if (video is not null)
        {
            var ownRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            ownRow.Children.Add(new TextBlock { Text = "我的评分  ", VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(43, 63, 100)) });
            var scoreBox = new System.Windows.Controls.ComboBox { Width = 108, Height = 32, VerticalAlignment = VerticalAlignment.Center };
            var existing = PersonalRatingStore.Get(DataDir, video.Id);
            foreach (var halfStep in Enumerable.Range(0, 21).Select(index => index / 2.0))
                scoreBox.Items.Add(new ComboBoxItem { Content = halfStep.ToString("0.#", CultureInfo.InvariantCulture) + " / 10", Tag = halfStep });
            if (existing is double ownScore)
            {
                var match = scoreBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (double)item.Tag == ownScore);
                if (match is null)
                {
                    match = new ComboBoxItem { Content = ownScore.ToString("0.#", CultureInfo.InvariantCulture) + " / 10", Tag = ownScore };
                    scoreBox.Items.Add(match);
                }
                scoreBox.SelectedItem = match;
            }
            else scoreBox.SelectedIndex = 0;
            ownRow.Children.Add(scoreBox);
            var save = new Button { Content = "保存个人评分", Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("Action") };
            var clear = new Button { Content = "清除", Margin = new Thickness(7, 0, 0, 0),
                Style = (Style)FindResource("Action"), IsEnabled = existing is not null };
            save.Click += async (_, _) =>
            {
                if (scoreBox.SelectedItem is not ComboBoxItem selected || selected.Tag is not double score) return;
                save.IsEnabled = false; clear.IsEnabled = false;
                try
                {
                    await Task.Run(() => PersonalRatingStore.Set(DataDir, video.Id, score));
                    clear.IsEnabled = true;
                }
                catch (Exception ex) { MessageBox.Show(dialog, "个人评分保存失败：" + ex.Message, "作品评分", MessageBoxButton.OK, MessageBoxImage.Warning); }
                finally { save.IsEnabled = true; }
            };
            clear.Click += async (_, _) =>
            {
                save.IsEnabled = false; clear.IsEnabled = false;
                try { await Task.Run(() => PersonalRatingStore.Set(DataDir, video.Id, null)); scoreBox.SelectedIndex = 0; }
                catch (Exception ex)
                {
                    clear.IsEnabled = true;
                    MessageBox.Show(dialog, "个人评分清除失败：" + ex.Message, "作品评分", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally { save.IsEnabled = true; }
            };
            ownRow.Children.Add(save); ownRow.Children.Add(clear);
            content.Children.Add(ownRow);
        }

        var summary = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(60, 84, 148)) };
        var sourceRows = new StackPanel { Margin = new Thickness(0, 5, 0, 2) };
        content.Children.Add(summary);
        content.Children.Add(sourceRows);
        void RefreshSources()
        {
            sourceRows.Children.Clear();
            var display = record.Clone();
            var original = RatingAggregation.FromMetadata(display, "原始来源资料");
            if (original is not null && !RatingAggregation.DistinctValidRatings(display.PlatformRatings)
                .Any(item => RatingAggregation.CanonicalPlatform(item.Platform).Equals(
                    RatingAggregation.CanonicalPlatform(original.Platform), StringComparison.OrdinalIgnoreCase)))
                RatingAggregation.AddOrUpdate(display, original);
            var ratings = RatingAggregation.DistinctValidRatings(display.PlatformRatings);
            var combined = RatingAggregation.Summarize(ratings);
            summary.Text = combined is null ? "多平台综合评分：需要至少两个有明确分制的独立来源"
                : $"多平台综合 {combined.ScoreOutOfTen:0.#} / 10 · {combined.SourceCount} 个独立平台";
            foreach (var rating in ratings)
            {
                var line = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new TextBlock
                {
                    Text = $"{rating.Platform}  {rating.Value:0.##} / {rating.Maximum:0.##}" +
                        (rating.VoteCount is int votes and > 0 ? $"  ·  {votes:N0} 票" : "") +
                        (string.IsNullOrWhiteSpace(rating.MatchEvidence) ? "" : $"  ·  {rating.MatchEvidence}"),
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(53, 72, 105))
                });
                if (TryRatingSourceUrl(rating.SourceUrl, out var uri))
                {
                    var source = new Button { Content = "来源", Margin = new Thickness(8, 0, 0, 0),
                        Style = (Style)FindResource("Action") };
                    source.Click += (_, _) =>
                    {
                        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch (Exception ex) { MessageBox.Show(dialog, "无法打开来源页面：" + ex.Message, "作品评分"); }
                    };
                    Grid.SetColumn(source, 1);
                    line.Children.Add(source);
                }
                sourceRows.Children.Add(line);
            }
            if (ratings.Count == 0)
                sourceRows.Children.Add(new TextBlock { Text = "暂无可核对分制的站点评分。",
                    Foreground = new SolidColorBrush(Color.FromRgb(128, 142, 164)) });
            if (!string.IsNullOrWhiteSpace(record.Rating) && original is null)
                sourceRows.Children.Add(new TextBlock { Text = $"{record.Source} 原始评分：{record.Rating}（分制未明确，不计入综合分）",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 105, 75)) });
        }
        RefreshSources();
        async Task SaveConfirmedRatingsAsync(IEnumerable<PlatformRating> confirmed)
        {
            var updated = record.Clone();
            foreach (var item in confirmed) RatingAggregation.AddOrUpdate(updated, item);
            var snapshot = new Dictionary<string, MovieMetadata>(metadata, StringComparer.OrdinalIgnoreCase)
            {
                [video!.Id] = updated
            };
            await Task.Run(() => MetadataStore.Save(DataDir, snapshot));
            metadata[video!.Id] = updated;
            record.PlatformRatings = updated.PlatformRatings;
            RefreshSources();
        }

        if (saved && video is not null)
        {
            var otherSources = new Button { Content = "从其他平台手动选择作品评分", HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0), Style = (Style)FindResource("Action") };
            otherSources.Click += async (_, _) =>
            {
                var confirmed = ChooseOtherPlatformRating(dialog, record, video);
                if (confirmed is null) return;
                otherSources.IsEnabled = false;
                try { await SaveConfirmedRatingsAsync([confirmed]); }
                catch (Exception ex) { MessageBox.Show(dialog, "评分保存失败，已有资料未改变：" + ex.Message,
                    "作品评分", MessageBoxButton.OK, MessageBoxImage.Warning); }
                finally { otherSources.IsEnabled = true; }
            };
            content.Children.Add(otherSources);
        }

        var canQuery = saved && video is not null && Regex.IsMatch(record.Code?.Trim() ?? "", @"^[A-Za-z0-9]{2,15}(?:-[A-Za-z0-9]{2,20})+$");
        if (!canQuery)
        {
            content.Children.Add(new TextBlock
            {
                Text = saved ? "按番号自动查询 JavDB / JAVDatabase 评分需要已核对的番号；上方仍可手动选择其他平台作品。"
                    : record.Source == "本地视频" ? "补充站点评分前，请先搜刮并核对作品番号。"
                    : "候选资料通过审核后，才能把其他站点评分加入视频。",
                Margin = new Thickness(0, 9, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(128, 142, 164)),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        var lookup = new Button { Content = "按番号查询 JavDB、JAVDatabase 评分", HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0), Style = (Style)FindResource("Action") };
        content.Children.Add(lookup);
        lookup.Click += async (_, _) =>
        {
            lookup.IsEnabled = false;
            var previousCaption = lookup.Content;
            lookup.Content = "正在查询两个公开来源…";
            try
            {
                var code = (record.Code ?? "").Trim().ToUpperInvariant();
                var results = await Task.WhenAll(
                    Task.Run(() => ProbeJavDbRatingAsync(code)),
                    Task.Run(() => ProbeJavDatabaseRatingAsync(code)));
                if (!dialog.IsVisible) return;
                var valid = results.Where(item => item.Rating is not null).ToArray();
                var report = string.Join("\n", results.Select(item => item.Rating is PlatformRating rating
                    ? $"{item.Source}：{rating.Value:0.##}/{rating.Maximum:0.##} · {item.Details?.Title}"
                    : $"{item.Source}：{item.Error ?? "没有明确分制的评分"}"));
                if (valid.Length == 0)
                {
                    MessageBox.Show(dialog, "本次没有找到可加入的站点评分，已有资料未改变。\n\n" + report,
                        "站点评分", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (MessageBox.Show(dialog,
                    $"请核对视频与下列站点作品是否相同：\n本地：{video!.Title}\n番号：{code}\n\n{report}\n\n确认后仅保存评分及来源，不覆盖标题、封面等搜刮资料。",
                    "确认评分来源", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var confirmedRatings = new List<PlatformRating>();
                foreach (var item in valid)
                {
                    var rating = item.Rating!.Clone();
                    rating.MatchEvidence = "番号精确匹配 · 人工确认";
                    confirmedRatings.Add(rating);
                }
                await SaveConfirmedRatingsAsync(confirmedRatings);
            }
            catch (Exception ex) { MessageBox.Show(dialog, "站点评分保存失败，已有评分保持不变：" + ex.Message,
                "站点评分", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { lookup.Content = previousCaption; lookup.IsEnabled = true; }
        };
    }

    static async Task<RatingProbe> ProbeJavDbRatingAsync(string code)
    {
        try
        {
            var candidates = await JavDbScraper.SearchTitleAsync(code);
            var exact = candidates.FirstOrDefault(item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (exact is null) return new("JavDB", null, null, "没有完全匹配的番号");
            var details = await JavDbScraper.FetchCandidateDetailsAsync(exact);
            if (!details.DetailFetched || !details.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
                return new("JavDB", null, null, "详情未能核对番号");
            var rating = RatingAggregation.DistinctValidRatings(details.PlatformRatings)
                .FirstOrDefault(item => RatingAggregation.CanonicalPlatform(item.Platform) == "JavDB")
                ?? RatingAggregation.FromMetadata(details, "番号精确匹配");
            return new("JavDB", details, rating, rating is null
                ? string.IsNullOrWhiteSpace(details.Rating) ? "未提供评分" : $"原始评分 {details.Rating}，分制未确认"
                : null);
        }
        catch (Exception ex) { return new("JavDB", null, null, ex.Message); }
    }

    PlatformRating? ChooseOtherPlatformRating(Window owner, MovieMetadata savedRecord, VideoItem video)
    {
        var picker = new Window
        {
            Title = "选择同一作品的站点评分", Owner = owner, Width = 650, Height = 520,
            MinWidth = 510, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "从公开作品库查找同一作品", FontSize = 18,
            FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(35, 49, 74)) });
        top.Children.Add(new TextBlock { Text = "选择结果并读取详情后，还需你确认作品身份。这里只加入评分，不更改原有搜刮资料。",
            Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 119, 151)) });
        var controls = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sourceBox = new System.Windows.Controls.ComboBox { Height = 34 };
        foreach (var name in new[] { "Bangumi", "AniList" }) sourceBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        var tmdbToken = config.TmdbToken;
        if (!string.IsNullOrWhiteSpace(tmdbToken)) sourceBox.Items.Add(new ComboBoxItem { Content = "TMDB", Tag = "TMDB" });
        sourceBox.SelectedIndex = 0;
        controls.Children.Add(sourceBox);
        var query = new System.Windows.Controls.TextBox { Height = 34, Text = savedRecord.OriginalTitle ?? savedRecord.Title,
            Margin = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetColumn(query, 1); controls.Children.Add(query);
        var search = new Button { Content = "搜索", Width = 78, Style = (Style)FindResource("Action") };
        Grid.SetColumn(search, 2); controls.Children.Add(search);
        top.Children.Add(controls);
        var guidance = new TextBlock { Text = "请选择站点并搜索标题；列表结果不会自动认作同一作品。",
            Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(105, 124, 157)) };
        top.Children.Add(guidance);
        DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        var footer = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "取消", Width = 78, Style = (Style)FindResource("Action") };
        cancel.Click += (_, _) => picker.DialogResult = false;
        var choose = new Button { Content = "读取并确认选中作品", Width = 176, IsEnabled = false,
            Style = (Style)FindResource("Primary"), Margin = new Thickness(8, 0, 0, 0) };
        footer.Children.Add(cancel); footer.Children.Add(choose);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var candidates = new System.Windows.Controls.ListBox();
        candidates.SelectionChanged += (_, _) => choose.IsEnabled = candidates.SelectedItem is ListBoxItem;
        layout.Children.Add(candidates);
        picker.Content = layout;
        search.Click += async (_, _) =>
        {
            var title = query.Text.Trim();
            if (title.Length < 2)
            {
                guidance.Text = "请填写至少两个字符的标题关键词。";
                return;
            }
            if (title.Length > 160) title = title[..160];
            var source = (sourceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Bangumi";
            search.IsEnabled = false; choose.IsEnabled = false; candidates.Items.Clear();
            guidance.Text = $"正在查询 {source}…";
            try
            {
                var found = await Task.Run(() => source switch
                {
                    "AniList" => AniListScraper.SearchAsync(title),
                    "TMDB" => AdditionalScrapers.SearchTmdbAsync(title, tmdbToken),
                    _ => BangumiScraper.SearchAsync(title)
                });
                if (!picker.IsVisible) return;
                foreach (var item in found.Take(15))
                {
                    var row = new StackPanel { Margin = new Thickness(6, 4, 6, 5) };
                    row.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap });
                    row.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { item.Source, item.ReleaseDate, item.SourceUrl }
                            .Where(value => !string.IsNullOrWhiteSpace(value))), FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = new SolidColorBrush(Color.FromRgb(110, 126, 151)) });
                    candidates.Items.Add(new ListBoxItem { Content = row, Tag = item });
                }
                guidance.Text = found.Count == 0 ? $"{source} 没有返回候选，尝试换一个关键词或来源。"
                    : $"{source} 返回 {found.Count} 个候选；请选择确实对应本地视频的作品。";
            }
            catch (Exception ex) { if (picker.IsVisible) guidance.Text = $"{source} 查询失败：{ex.Message}"; }
            finally { search.IsEnabled = true; }
        };
        PlatformRating? confirmedRating = null;
        choose.Click += async (_, _) =>
        {
            if ((candidates.SelectedItem as ListBoxItem)?.Tag is not MovieMetadata selected) return;
            choose.IsEnabled = false; search.IsEnabled = false;
            guidance.Text = "正在读取选中作品的来源详情…";
            try
            {
                var details = await Task.Run(() => AdditionalScrapers.FetchFullDetailsAsync(selected, tmdbToken));
                if (!picker.IsVisible) return;
                if (!details.DetailFetched) throw new InvalidDataException("来源没有返回可核对的完整详情。");
                var rating = RatingAggregation.DistinctValidRatings(details.PlatformRatings).FirstOrDefault(item =>
                    RatingAggregation.CanonicalPlatform(item.Platform).Equals(
                        RatingAggregation.CanonicalPlatform(details.Source), StringComparison.OrdinalIgnoreCase))
                    ?? RatingAggregation.FromMetadata(details, "人工选择同一作品");
                if (rating is null) throw new InvalidDataException("来源没有提供明确分制的评分，未保存。原始评分：" + (details.Rating ?? "无"));
                var confirmation = $"请核对是不是同一部作品：\n\n本地视频：{video.Title}\n已保存作品：{savedRecord.Title}" +
                    (string.IsNullOrWhiteSpace(savedRecord.Code) ? "" : $"\n番号：{savedRecord.Code}") +
                    $"\n\n新来源：{details.Source}\n作品：{details.Title}\n日期：{details.ReleaseDate ?? "未知"}" +
                    $"\n评分：{rating.Value:0.##}/{rating.Maximum:0.##}\n链接：{details.SourceUrl}\n\n确认是同一作品并加入评分吗？";
                if (MessageBox.Show(picker, confirmation, "确认作品身份", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                rating.MatchEvidence = "人工选择候选并确认同一作品";
                confirmedRating = rating;
                picker.DialogResult = true;
            }
            catch (Exception ex) { if (picker.IsVisible) guidance.Text = "详情读取失败：" + ex.Message; }
            finally { choose.IsEnabled = candidates.SelectedItem is ListBoxItem; search.IsEnabled = true; }
        };
        picker.ShowDialog();
        return confirmedRating;
    }

    static async Task<RatingProbe> ProbeJavDatabaseRatingAsync(string code)
    {
        try
        {
            var details = await JavDatabaseScraper.ScrapeByCodeAsync(code);
            if (!details.DetailFetched || !details.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
                return new("JAVDatabase", null, null, "详情未能核对番号");
            var rating = RatingAggregation.DistinctValidRatings(details.PlatformRatings)
                .FirstOrDefault(item => RatingAggregation.CanonicalPlatform(item.Platform) == "JAVDatabase")
                ?? RatingAggregation.FromMetadata(details, "番号精确匹配");
            return new("JAVDatabase", details, rating, rating is null
                ? string.IsNullOrWhiteSpace(details.Rating) ? "未提供评分" : $"原始评分 {details.Rating}，分制未确认"
                : null);
        }
        catch (Exception ex) { return new("JAVDatabase", null, null, ex.Message); }
    }

    static bool TryRatingSourceUrl(string sourceUrl, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var value) || value.Scheme != Uri.UriSchemeHttps ||
            value.UserInfo.Length != 0 || value.Port != 443) return false;
        var host = value.Host.ToLowerInvariant();
        if (host is not ("javdb.com" or "www.javdb.com" or "javdatabase.com" or "www.javdatabase.com" or
            "www.themoviedb.org" or "themoviedb.org" or "bgm.tv" or "anilist.co" or "www.tvmaze.com" or
            "tvmaze.com" or "www.pornhub.com" or "pornhub.com")) return false;
        uri = value;
        return true;
    }
}
