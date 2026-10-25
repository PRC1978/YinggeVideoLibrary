using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBox = System.Windows.Controls.TextBox;
using WrapPanel = System.Windows.Controls.WrapPanel;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    ListBox? scrapeExcludedList;
    TextBox? scrapeKeywordBox;
    TextBox? scrapeSkipTagsBox;
    TextBox? scrapeMinSizeBox;
    CheckBox? scrapeSkipWatchedBox;
    CheckBox? scrapeOnlySelectedBox;
    CheckBox? scrapeRefreshExistingBox;
    HashSet<string> scrapeSkippedTags = new(StringComparer.CurrentCultureIgnoreCase);

    void InitializeScrapeFiltersUi()
    {
        if (ScrapePanel.Child is not StackPanel panel) return;
        scrapeSkippedTags = ParseTags(config.ScrapeSkipTags).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        Toolbar.Children.Remove(ScrapeBatchButton);
        var titleAction = Toolbar.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Content, "完整资料搜刮"));
        if (titleAction is not null)
        {
            Toolbar.Children.Remove(titleAction);
            titleAction.Content = "完整资料搜刮";
            titleAction.ToolTip = "先选择候选，再读取来源详情、演职员、日期、类别和简介；来源未提供的字段保持空白";
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        ScrapeBatchButton.Style = (Style)FindResource("Primary");
        actions.Children.Add(ScrapeBatchButton);
        if (titleAction is not null) actions.Children.Add(titleAction);
        var preview = new Button { Content = "预览入队范围", Style = (Style)FindResource("Action") };
        preview.Click += async (_, _) =>
        {
            preview.IsEnabled = false;
            ScrapeBatchButton.IsEnabled = false;
            try { await PreviewScrapeRangeAsync(); }
            finally
            {
                preview.IsEnabled = true;
                ScrapeBatchButton.IsEnabled = !scrapeQueueChanging;
            }
        };
        actions.Children.Add(preview);
        panel.Children.Insert(3, actions);
        panel.Children.Insert(4, new TextBlock { Text = "搜刮会读取所选来源的详情信息，低可信度先进入审核。来源未公开或暂不可访问的字段会标明缺失。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 12) });

        var section = new StackPanel { Margin = new Thickness(0, 2, 0, 18) };
        section.Children.Add(new TextBlock { Text = "批量搜刮条件", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        section.Children.Add(new TextBlock { Text = "默认将排除条件之外的全部尚待搜刮视频入队：有番号且启用番号来源的优先按番号查找，其余按标题查找。队列逐项限速执行，可随时暂停或取消待处理。以下排除条件也适用于已有队列。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 8) });
        scrapeRefreshExistingBox = new CheckBox
        {
            Content = "本次补全已搜刮但字段缺失的资料",
            IsChecked = false,
            Margin = new Thickness(0, 2, 0, 4),
            ToolTip = "仅本次入队时生效。先预览范围；已保存的字段不会自动被新来源覆盖，补全结果进入人工审核。队列任务会保留此模式直到处理完成。"
        };
        scrapeRefreshExistingBox.Checked += (_, _) => ScrapeBatchButton.Content = "未搜刮＋需补全入队";
        scrapeRefreshExistingBox.Unchecked += (_, _) => ScrapeBatchButton.Content = "全部符合条件入队";
        section.Children.Add(scrapeRefreshExistingBox);
        section.Children.Add(new TextBlock { Text = "补全模式只会增加详情未核对或常用字段缺失的已保存作品；审核中、已否决的候选不会重新入队。补全结果需人工确认，原资料在确认前保持不变。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 10) });
        section.Children.Add(new TextBlock { Text = "跳过这些文件夹（含子文件夹）", FontWeight = FontWeights.SemiBold });
        scrapeExcludedList = new ListBox { Height = 88, Margin = new Thickness(0, 6, 0, 6) };
        RefreshExcludedFoldersList();
        section.Children.Add(scrapeExcludedList);
        var folderButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var add = new Button { Content = "＋ 选择跳过文件夹", Style = (Style)FindResource("Action") };
        add.Click += (_, _) =>
        {
            using var chooser = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要跳过的文件夹", ShowNewFolderButton = false };
            if (chooser.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            if (!config.ScrapeExcludedFolders.Contains(chooser.SelectedPath, StringComparer.OrdinalIgnoreCase)) config.ScrapeExcludedFolders.Add(chooser.SelectedPath);
            SaveConfig(); RefreshExcludedFoldersList();
        };
        var remove = new Button { Content = "移除选中", Style = (Style)FindResource("Action") };
        remove.Click += (_, _) =>
        {
            if (scrapeExcludedList?.SelectedItem is not string selected) return;
            config.ScrapeExcludedFolders.RemoveAll(path => path.Equals(selected, StringComparison.OrdinalIgnoreCase));
            SaveConfig(); RefreshExcludedFoldersList();
        };
        folderButtons.Children.Add(add); folderButtons.Children.Add(remove); section.Children.Add(folderButtons);
        section.Children.Add(new TextBlock { Text = "文件名必须包含（留空不限）", FontWeight = FontWeights.SemiBold });
        scrapeKeywordBox = new TextBox { Text = config.ScrapeFilenameContains, Height = 32, Margin = new Thickness(0, 5, 0, 9) }; section.Children.Add(scrapeKeywordBox);
        section.Children.Add(new TextBlock { Text = "跳过包含这些标签的视频（逗号分隔）", FontWeight = FontWeights.SemiBold });
        scrapeSkipTagsBox = new TextBox { Text = config.ScrapeSkipTags, Height = 32, Margin = new Thickness(0, 5, 0, 9) }; section.Children.Add(scrapeSkipTagsBox);
        var lower = new WrapPanel();
        scrapeSkipWatchedBox = new CheckBox { Content = "跳过已看", IsChecked = config.ScrapeSkipWatched, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        lower.Children.Add(scrapeSkipWatchedBox);
        lower.Children.Add(new TextBlock { Text = "最小文件大小 MB", VerticalAlignment = System.Windows.VerticalAlignment.Center });
        scrapeMinSizeBox = new TextBox { Text = config.ScrapeMinSizeMb.ToString(), Width = 75, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
        lower.Children.Add(scrapeMinSizeBox); section.Children.Add(lower);
        scrapeOnlySelectedBox = new CheckBox { Content = "本次仅将已选视频加入队列", Margin = new Thickness(0, 10, 0, 8) };
        section.Children.Add(scrapeOnlySelectedBox);
        var save = new Button { Content = "保存筛选条件", Style = (Style)FindResource("Action"), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        save.Click += (_, _) => { if (ReadScrapeFilterSettings()) MessageBox.Show(this, "搜刮条件已保存。", "搜刮设置"); };
        section.Children.Add(save);
        panel.Children.Insert(4, section);
    }

    void RefreshExcludedFoldersList()
    {
        if (scrapeExcludedList is null) return;
        scrapeExcludedList.Items.Clear();
        foreach (var path in config.ScrapeExcludedFolders.OrderBy(path => path)) scrapeExcludedList.Items.Add(path);
    }

    bool ReadScrapeFilterSettings()
    {
        if (!int.TryParse(scrapeMinSizeBox?.Text, out var minimum) || minimum is < 0 or > 1000000)
        { MessageBox.Show(this, "最小文件大小应为 0–1000000 MB 的整数。", "搜刮条件", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        config.ScrapeFilenameContains = scrapeKeywordBox?.Text.Trim() ?? "";
        config.ScrapeSkipTags = scrapeSkipTagsBox?.Text.Trim() ?? "";
        scrapeSkippedTags = ParseTags(config.ScrapeSkipTags).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        config.ScrapeSkipWatched = scrapeSkipWatchedBox?.IsChecked == true;
        config.ScrapeMinSizeMb = minimum;
        SaveConfig();
        return true;
    }

    string? ScrapeSkipReason(VideoItem video)
    {
        if (config.ScrapeExcludedFolders.Any(folder => video.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) || IsDescendantPath(folder, video.Path))) return "跳过文件夹";
        if (config.ScrapeSkipWatched && video.WatchStatus == "已看") return "已看";
        if (config.ScrapeMinSizeMb > 0 && video.Size < config.ScrapeMinSizeMb * 1048576L) return "文件过小";
        if (config.ScrapeFilenameContains.Length > 0 && !video.Title.Contains(config.ScrapeFilenameContains, StringComparison.CurrentCultureIgnoreCase)) return "文件名不匹配";
        if (video.Tags.Any(scrapeSkippedTags.Contains)) return "跳过标签";
        return null;
    }

    internal static bool HasMissingScrapeFields(MovieMetadata existing)
    {
        if (!existing.DetailFetched || !existing.DetailIdentityVerified) return true;
        return string.IsNullOrWhiteSpace(existing.ReleaseDate)
            || existing.RuntimeMinutes is null or <= 0
            || string.IsNullOrWhiteSpace(existing.Studio) && string.IsNullOrWhiteSpace(existing.Maker)
            || existing.Actors is not { Count: > 0 }
            || existing.Genres is not { Count: > 0 }
            || string.IsNullOrWhiteSpace(existing.PosterUrl);
    }

    internal static bool CanRefreshSavedScrape(MovieMetadata existing, AiReviewRecord? review,
        bool explicitRefresh, bool legacyJavDbRefresh)
    {
        if (review is not null && review.Status != "approved") return false;
        if (explicitRefresh) return HasMissingScrapeFields(existing);
        return legacyJavDbRefresh && !existing.DetailFetched
            && string.Equals(existing.Source, "JavDB", StringComparison.OrdinalIgnoreCase);
    }

    bool CanQueueForFullMetadata(VideoItem video, bool refreshExisting = false)
    {
        if (!metadata.TryGetValue(video.Id, out var existing))
            return !config.AiReviews.ContainsKey(video.Id);
        config.AiReviews.TryGetValue(video.Id, out var review);
        return CanRefreshSavedScrape(existing, review, refreshExisting, config.ScrapeRefreshIncompleteMetadata);
    }

    async Task PreviewScrapeRangeAsync()
    {
        if (!ReadScrapeFilterSettings()) return;
        var refreshExisting = scrapeRefreshExistingBox?.IsChecked == true;
        var candidate = videos.Where(video => CanQueueForFullMetadata(video, refreshExisting))
            .Where(video => scrapeOnlySelectedBox?.IsChecked != true || selectedVideoIds.Contains(video.Id)).ToList();
        var evaluated = candidate.Select(video => (video, reason: ScrapeSkipReason(video))).ToList();
        var groups = evaluated.Where(item => item.reason is not null).GroupBy(item => item.reason).Select(group => $"{group.Key}：{group.Count()} 部");
        var accepted = evaluated.Where(item => item.reason is null).Select(item => item.video).ToArray();
        var hasCodeSource = HasEnabledCodeSource;
        var useFolderHint = config.UseFolderPersonHint;
        var libraryFolders = config.Folders.ToArray();
        var inputs = candidate.Select(video =>
        {
            var accepted = metadata.GetValueOrDefault(video.Id);
            return (video, refresh: refreshExisting && accepted is not null,
                code: accepted?.Code, title: accepted?.Title);
        }).ToArray();
        List<PlannedScrapeItem> plans;
        List<ScrapeQueueItem> queued;
        try
        {
            var acceptedIds = accepted.Select(video => video.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var planningTask = Task.Run(() => inputs.Where(input => acceptedIds.Contains(input.video.Id))
                .Select(input => PlanScrapeQueueItem(input.video, hasCodeSource, useFolderHint, libraryFolders,
                    input.refresh, input.code, input.title)).ToList());
            var queueTask = Task.Run(() => ScrapeQueueStore.Load(DataDir));
            await Task.WhenAll(planningTask, queueTask);
            plans = planningTask.Result;
            queued = queueTask.Result;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "准备搜刮范围失败：" + ex.Message, "搜刮范围预览", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var eligible = plans.Where(plan => plan.Item is not null).ToList();
        var known = queued.Select(item => item.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newlyQueued = eligible.Where(plan => !known.Contains(plan.Video.Id)).ToList();
        var pendingCount = queued.Count(item => !item.Failed);
        var skipped = string.Join("\n", groups);
        MessageBox.Show(this,
            $"模式：{(refreshExisting ? "未搜刮＋已存资料补全（需人工审核）" : "默认未搜刮入队")}\n候选视频：{candidate.Count} 部\n排除条件后：{eligible.Count} 部（番号 {eligible.Count(plan => plan.Item!.Kind == "code")}，标题 {eligible.Count(plan => plan.Item!.Kind == "title")}；已存资料补全 {eligible.Count(plan => plan.Item!.RefreshExisting)}）\n目录补出作品标题：{eligible.Count(plan => plan.UsedFolderTitle)} 部；标题较弱或番号冲突、需人工核对：{eligible.Count(plan => plan.NeedsManualTitle)} 部。\n已在队列：{eligible.Count - newlyQueued.Count} 部；本次可新增：{newlyQueued.Count} 部。\n当前待处理：{pendingCount} 项；全部加入后最少约 {FormatScrapeMinimumDuration(pendingCount + newlyQueued.Count)}，不含网络耗时和重试。\n\n"
            + (skipped.Length > 0 ? "排除明细：\n" + skipped + "\n\n" : "")
            + "本次前几项：\n" + string.Join("\n", newlyQueued.Take(8).Select(plan => plan.Video.Title)),
            "搜刮范围预览");
    }
}
