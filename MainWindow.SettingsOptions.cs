using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    TextBlock? settingsCacheStatus;
    ComboBox? posterCacheLimitChoices;
    TextBlock? settingsOfflineBackupStatus;
    DispatcherTimer? settingsBackupTimer;
    bool periodicBackupEnabled;
    DateTimeOffset? periodicBackupNextDueUtc;

    void InitializeSettingsOptions()
    {
        if (SettingsPanel.Child is not StackPanel panel) return;
        panel.Children.Add(BuildAppearanceOptions());
        panel.Children.Add(BuildScrapeOptions());
        panel.Children.Add(BuildLibraryOptions());
        panel.Children.Add(BuildStartupOptions());
        settingsBackupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        settingsBackupTimer.Tick += (_, _) =>
        {
            if (isClosing) { settingsBackupTimer.Stop(); return; }
            if (!periodicBackupEnabled || periodicBackupNextDueUtc is not { } due
                || DateTimeOffset.UtcNow < due) return;
            try
            {
                LibraryBackupService.CheckAndStageDuePeriodicBackup(DataDir);
                RefreshOfflineBackupStatus();
            }
            catch (Exception ex) { if (settingsOfflineBackupStatus is not null)
                settingsOfflineBackupStatus.Text = "定期备份排定失败：" + ex.Message; }
        };
        settingsBackupTimer.Start();
        Closed += (_, _) => settingsBackupTimer?.Stop();
    }

    static TextBlock SettingsHint(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(105, 123, 150)),
        Margin = new Thickness(0, 5, 0, 9)
    };

    StackPanel BuildAppearanceOptions()
    {
        var section = new StackPanel { Name = "SettingsAppearanceOptions", Margin = new Thickness(0, 16, 0, 8) };
        section.Children.Add(new TextBlock { Text = "列表密度", FontWeight = FontWeights.SemiBold });
        section.Children.Add(SettingsHint("分页列表每页显示的卡片数。完整列表仍使用虚拟滚动。"));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "每页卡片", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var choices = new ComboBox { Width = 100, Height = 34, ItemsSource = new[] { 30, 60, 90 }, SelectedItem = config.CardPageSize };
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedItem is not int count || config.CardPageSize == count) return;
            config.CardPageSize = count;
            SaveConfig();
            pageIndex = 0;
            if (view is "all" or "recent" or "favorites" or "categories") RenderCards();
        };
        row.Children.Add(choices);
        section.Children.Add(row);
        return section;
    }

    StackPanel BuildScrapeOptions()
    {
        var section = new StackPanel { Name = "SettingsScrapeOptions", Margin = new Thickness(0, 16, 0, 8) };
        var retry = new CheckBox
        {
            Content = "允许重新搜刮资料不完整的 JavDB 视频",
            IsChecked = config.ScrapeRefreshIncompleteMetadata,
            Margin = new Thickness(0, 0, 0, 2)
        };
        retry.Checked += (_, _) => { config.ScrapeRefreshIncompleteMetadata = true; SaveConfig(); };
        retry.Unchecked += (_, _) => { config.ScrapeRefreshIncompleteMetadata = false; SaveConfig(); };
        section.Children.Add(retry);
        section.Children.Add(SettingsHint("只重试尚未读取详情的 JavDB 旧记录；已有完整资料仍会跳过。"));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "搜刮请求间隔", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var intervals = new[] { 2.5, 5d, 10d, 30d };
        var interval = new ComboBox { Width = 104, Height = 34, ItemsSource = intervals,
            SelectedItem = intervals.Contains(config.ScrapeRequestIntervalSeconds) ? config.ScrapeRequestIntervalSeconds : 2.5 };
        interval.SelectionChanged += (_, _) =>
        {
            if (interval.SelectedItem is not double seconds || config.ScrapeRequestIntervalSeconds == seconds) return;
            config.ScrapeRequestIntervalSeconds = seconds;
            SaveConfig();
        };
        row.Children.Add(interval);
        row.Children.Add(new TextBlock { Text = "秒 / 项", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        section.Children.Add(row);
        section.Children.Add(SettingsHint("用于后台番号与标题搜刮队列。调大间隔可减轻来源站点的请求压力。"));
        section.Children.Add(new TextBlock { Text = "按来源调整", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 5) });
        section.Children.Add(SettingsHint("这是同一来源两次搜刮任务之间的最短间隔。一次任务内的搜索、详情等多个请求由来源适配器另行限速。连续失败达到上限后冷却一小时，任务保留；后台队列每个来源并发为 1。"));
        foreach (var (source, label) in new (string Source, string Label)[]
        {
            ("TMDB", "TMDB"), ("TVmaze", "TVmaze"), ("Bangumi", "Bangumi"),
            ("Wikidata", "Wikidata"), ("Kitsu", "Kitsu 动画"), ("Tenrai", "Tenrai 动画"), ("JavDB", "JavDB"),
            ("JAVDatabase", "JAVDatabase"), ("Pornhub", "Pornhub")
        })
        {
            var saved = ScrapeSourcePolicy.Normalize(config.ScrapeSourcePolicies.GetValueOrDefault(source),
                ScrapeSourcePolicy.DefaultIntervalFor(source, config.ScrapeRequestIntervalSeconds));
            var sourceRow = new StackPanel { Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2) };
            sourceRow.Children.Add(new TextBlock { Text = label, Width = 132,
                VerticalAlignment = VerticalAlignment.Center });
            var sourceIntervals = new[] { 1d, 2.5, 5d, 10d, 30d, 60d };
            var sourceInterval = new ComboBox { Width = 90, Height = 32, ItemsSource = sourceIntervals,
                SelectedItem = sourceIntervals.Contains(saved.IntervalSeconds) ? saved.IntervalSeconds : 2.5 };
            sourceInterval.SelectionChanged += (_, _) =>
            {
                if (sourceInterval.SelectedItem is not double value) return;
                var current = ScrapeSourcePolicy.Normalize(config.ScrapeSourcePolicies.GetValueOrDefault(source),
                    ScrapeSourcePolicy.DefaultIntervalFor(source, config.ScrapeRequestIntervalSeconds));
                if (current.IntervalSeconds == value) return;
                current.IntervalSeconds = value;
                config.ScrapeSourcePolicies[source] = current;
                SaveConfig();
            };
            sourceRow.Children.Add(sourceInterval);
            sourceRow.Children.Add(new TextBlock { Text = "秒   失败后冷却", Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center });
            var retryChoices = new[] { 1, 2, 3, 4, 5, 10 };
            var sourceRetries = new ComboBox { Width = 70, Height = 32, ItemsSource = retryChoices,
                SelectedItem = retryChoices.Contains(saved.ConsecutiveFailureLimit) ? saved.ConsecutiveFailureLimit : 4 };
            sourceRetries.SelectionChanged += (_, _) =>
            {
                if (sourceRetries.SelectedItem is not int value) return;
                var current = ScrapeSourcePolicy.Normalize(config.ScrapeSourcePolicies.GetValueOrDefault(source),
                    ScrapeSourcePolicy.DefaultIntervalFor(source, config.ScrapeRequestIntervalSeconds));
                if (current.ConsecutiveFailureLimit == value) return;
                current.ConsecutiveFailureLimit = value;
                config.ScrapeSourcePolicies[source] = current;
                SaveConfig();
            };
            sourceRow.Children.Add(sourceRetries);
            section.Children.Add(sourceRow);
        }
        return section;
    }

    StackPanel BuildLibraryOptions()
    {
        var section = new StackPanel { Name = "SettingsLibraryOptions", Margin = new Thickness(0, 16, 0, 8) };
        section.Children.Add(new TextBlock { Text = "资料库和缓存", FontWeight = FontWeights.SemiBold });
        section.Children.Add(SettingsHint("配置、视频索引和缩略图保存在同一用户的共用数据目录。"));
        section.Children.Add(SettingsHint($"默认数据目录：{DataDir}\n磁盘缩略图：{ThumbDir}\n候选结果缓存：{Path.Combine(DataDir, "candidate-cache")}"));
        var cacheLimitRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        cacheLimitRow.Children.Add(new TextBlock { Text = "海报内存缓存", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0) });
        var cacheLimit = new ComboBox { Name = "PosterCacheLimitChoices", Width = 132, Height = 34 };
        posterCacheLimitChoices = cacheLimit;
        foreach (var (limit, label) in new (int Limit, string Label)[]
            { (64, "64 MB"), (128, "128 MB（默认）"), (256, "256 MB"), (512, "512 MB"),
              (1024, "1 GB"), (2048, "2 GB"), (0, "无限制") })
            cacheLimit.Items.Add(new ComboBoxItem { Content = label, Tag = limit });
        cacheLimit.SelectedItem = cacheLimit.Items.Cast<ComboBoxItem>()
            .First(item => item.Tag is int limit && limit == config.PosterCacheLimitMb);
        cacheLimit.SelectionChanged += (_, _) =>
        {
            if (cacheLimit.SelectedItem is not ComboBoxItem { Tag: int limit } ||
                config.PosterCacheLimitMb == limit) return;
            config.PosterCacheLimitMb = limit;
            VideoItem.ConfigurePosterCache(limit);
            SaveConfig();
            if (settingsCacheStatus is not null)
                settingsCacheStatus.Text = limit == 0 ? "海报内存缓存已设为无限制。"
                    : $"海报内存缓存上限已设为 {limit} MB。";
        };
        cacheLimitRow.Children.Add(cacheLimit);
        section.Children.Add(cacheLimitRow);
        section.Children.Add(SettingsHint("这里只限制已解码海报占用的内存，不限制磁盘缩略图。无限制仅取消容量上限，仍按浏览进度载入，不会一次读取全库；关闭软件后内存缓存会清空。"));
        settingsCacheStatus = SettingsHint("点击“查看缓存占用”读取磁盘使用量。");
        section.Children.Add(settingsCacheStatus);
        var buttons = new WrapPanel();
        var inspect = new Button { Content = "查看缓存占用", Style = (Style)FindResource("Action") };
        inspect.Click += async (_, _) => await RefreshCacheUsageAsync();
        buttons.Children.Add(inspect);
        var openThumbs = new Button { Content = "打开缩略图目录", Style = (Style)FindResource("Action") };
        openThumbs.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(ThumbDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ThumbDir)
                    { UseShellExecute = true });
            }
            catch (Exception ex) { settingsCacheStatus!.Text = "打开缩略图目录失败：" + ex.Message; }
        };
        buttons.Children.Add(openThumbs);
        var clear = new Button { Content = "清理候选结果缓存", Style = (Style)FindResource("Action") };
        clear.Click += async (_, _) => await ClearCandidateCacheAsync(clear);
        buttons.Children.Add(clear);
        var rescan = new Button { Content = "重新扫描视频文件夹", Style = (Style)FindResource("Action") };
        rescan.Click += async (_, _) =>
        {
            settingsCacheStatus!.Text = "正在后台重新扫描视频文件夹…";
            await RefreshAsync();
            if (!isClosing) settingsCacheStatus.Text = $"扫描完成 · {videos.Count} 部视频 · {DataDir}";
        };
        buttons.Children.Add(rescan);
        section.Children.Add(buttons);
        section.Children.Add(new TextBlock { Text = "定期一致性备份", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 17, 0, 0) });
        section.Children.Add(SettingsHint("到期后标记为待执行；关闭所有映格窗口并重新打开时，会在载入资料库前完成一致性备份和校验。"));
        LibraryBackupService.BackupSchedulePolicy policy;
        var scheduleDamaged = false;
        try { policy = LibraryBackupService.ReadSchedule(DataDir); }
        catch (Exception ex)
        {
            policy = new LibraryBackupService.BackupSchedulePolicy();
            scheduleDamaged = true;
            section.Children.Add(SettingsHint("备份设置读取失败：" + ex.Message));
        }
        periodicBackupEnabled = policy.Enabled;
        periodicBackupNextDueUtc = policy.NextDueUtc;
        var enabled = new CheckBox { Content = "启用定期备份", IsChecked = policy.Enabled,
            Margin = new Thickness(0, 8, 0, 10) };
        section.Children.Add(enabled);
        var scheduleRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        scheduleRow.Children.Add(new TextBlock { Text = "频率", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0) });
        var frequency = new ComboBox { Width = 110, Height = 34, Margin = new Thickness(0, 0, 18, 0) };
        foreach (var (key, label) in new[] { ("daily", "每天"), ("weekly", "每周"), ("monthly", "每月") })
        {
            var item = new ComboBoxItem { Content = label, Tag = key };
            frequency.Items.Add(item);
            if (policy.Frequency == key) frequency.SelectedItem = item;
        }
        scheduleRow.Children.Add(frequency);
        scheduleRow.Children.Add(new TextBlock { Text = "保留最近", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0) });
        var retention = new TextBox { Width = 55, Height = 34, Text = policy.RetentionCount.ToString(),
            VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        scheduleRow.Children.Add(retention);
        scheduleRow.Children.Add(new TextBlock { Text = "轮自动备份（默认 3，可设 1–30）",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        section.Children.Add(scheduleRow);
        section.Children.Add(new TextBlock { Text = "备份位置", FontWeight = FontWeights.SemiBold });
        var destination = new TextBox { Height = 35, IsReadOnly = true,
            Text = policy.DestinationDirectory.Length == 0 ? "默认：资料库数据目录\\backups" : policy.DestinationDirectory,
            ToolTip = "自定义目标下会建立仅用于本资料库的备份子目录。",
            VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 7) };
        var selectedDestination = policy.DestinationDirectory;
        section.Children.Add(destination);
        var destinationActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var choose = new Button { Content = "选择备份文件夹", Style = (Style)FindResource("Action") };
        choose.Click += (_, _) =>
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择保存映格备份的位置",
                SelectedPath = Directory.Exists(selectedDestination) ? selectedDestination : "",
                ShowNewFolderButton = true
            };
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            selectedDestination = picker.SelectedPath;
            destination.Text = selectedDestination;
        };
        destinationActions.Children.Add(choose);
        var useDefault = new Button { Content = "使用默认位置", Style = (Style)FindResource("Action") };
        useDefault.Click += (_, _) =>
        {
            selectedDestination = "";
            destination.Text = "默认：资料库数据目录\\backups";
        };
        destinationActions.Children.Add(useDefault);
        var saveSchedule = new Button { Content = "保存备份设置", Style = (Style)FindResource("Primary") };
        saveSchedule.Click += (_, _) =>
        {
            try
            {
                if (!int.TryParse(retention.Text.Trim(), out var keep) || keep is < 1 or > 30)
                    throw new InvalidDataException("保留轮数请输入 1 到 30。");
                var requested = new LibraryBackupService.BackupSchedulePolicy
                {
                    Enabled = enabled.IsChecked == true,
                    Frequency = (frequency.SelectedItem as ComboBoxItem)?.Tag as string ?? "daily",
                    RetentionCount = keep,
                    DestinationDirectory = selectedDestination
                };
                LibraryBackupService.SaveSchedule(DataDir, requested, config.Folders);
                var saved = LibraryBackupService.ReadSchedule(DataDir);
                selectedDestination = saved.DestinationDirectory;
                destination.Text = selectedDestination.Length == 0
                    ? "默认：资料库数据目录\\backups" : selectedDestination;
                periodicBackupEnabled = saved.Enabled;
                periodicBackupNextDueUtc = saved.NextDueUtc;
                RefreshOfflineBackupStatus();
            }
            catch (Exception ex) { settingsOfflineBackupStatus!.Text = "保存备份设置失败：" + ex.Message; }
        };
        destinationActions.Children.Add(saveSchedule);
        section.Children.Add(destinationActions);
        var backupActions = new WrapPanel();
        var scheduleBackup = new Button { Content = "下次启动创建一致性备份", Style = (Style)FindResource("Primary") };
        scheduleBackup.Click += (_, _) =>
        {
            try
            {
                LibraryBackupService.StageOfflineBackup(DataDir);
                RefreshOfflineBackupStatus();
            }
            catch (Exception ex) { settingsOfflineBackupStatus!.Text = "无法排定备份：" + ex.Message; }
        };
        backupActions.Children.Add(scheduleBackup);
        var cancelBackup = new Button { Content = "取消待执行备份", Style = (Style)FindResource("Action") };
        cancelBackup.Click += (_, _) =>
        {
            try
            {
                LibraryBackupService.CancelPendingOfflineBackup(DataDir);
                RefreshOfflineBackupStatus();
            }
            catch (Exception ex) { settingsOfflineBackupStatus!.Text = "取消失败：" + ex.Message; }
        };
        backupActions.Children.Add(cancelBackup);
        section.Children.Add(backupActions);
        settingsOfflineBackupStatus = SettingsHint(DescribeOfflineBackupStatus());
        section.Children.Add(settingsOfflineBackupStatus);
        if (scheduleDamaged)
        {
            var reset = new Button { Content = "重置损坏的备份设置", Style = (Style)FindResource("Action") };
            reset.Click += (_, _) =>
            {
                try
                {
                    LibraryBackupService.ResetDamagedSchedule(DataDir);
                    enabled.IsChecked = false;
                    frequency.SelectedIndex = 0;
                    retention.Text = "3";
                    selectedDestination = "";
                    destination.Text = "默认：资料库数据目录\\backups";
                    RefreshOfflineBackupStatus();
                    reset.IsEnabled = false;
                    reset.Content = "已重置，损坏文件已留存";
                }
                catch (Exception ex) { settingsOfflineBackupStatus!.Text = "重置失败：" + ex.Message; }
            };
            section.Children.Add(reset);
        }
        return section;
    }

    void RefreshOfflineBackupStatus()
    {
        if (settingsOfflineBackupStatus is not null)
            settingsOfflineBackupStatus.Text = DescribeOfflineBackupStatus();
    }

    string DescribeOfflineBackupStatus()
    {
        LibraryBackupService.BackupSchedulePolicy policy;
        try { policy = LibraryBackupService.ReadSchedule(DataDir); }
        catch (Exception ex)
        {
            periodicBackupEnabled = false;
            periodicBackupNextDueUtc = null;
            var pendingNotice = LibraryBackupService.HasPendingOfflineBackup(DataDir)
                ? "手动待办会在下次启动时改存默认位置；定期待办需修复设置后重试。" : "";
            return "定期备份设置损坏：" + ex.Message + Environment.NewLine
                + "原设置文件与历史备份未删除。请点击下方“重置损坏的备份设置”后重新选择备份位置。" + pendingNotice;
        }
        periodicBackupEnabled = policy.Enabled;
        periodicBackupNextDueUtc = policy.NextDueUtc;
        var last = LibraryBackupService.LastOfflineBackupResult(DataDir);
        var scheduled = policy.Enabled && policy.NextDueUtc is { } due
            ? $"下次到期：{due.ToLocalTime():yyyy-MM-dd HH:mm} · " : "定期备份：关闭 · ";
        var location = $"目标：{LibraryBackupService.CurrentBackupDirectory(DataDir)}";
        var pending = LibraryBackupService.HasPendingOfflineBackup(DataDir)
            ? last is { Succeeded: false }
                ? $"上次失败：{last.Message}；待办保留，关闭所有窗口后重启会重试。"
                : "已待执行：关闭所有窗口并重启，启动前创建一致性备份。"
            : last is null ? "尚无一致性备份。"
            : last.Succeeded ? $"最近完成：{last.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {last.Path} · {last.Message}"
            : "最近备份失败：" + last.Message;
        return scheduled + location + Environment.NewLine + pending;
    }

    async Task RefreshCacheUsageAsync()
    {
        if (settingsCacheStatus is null) return;
        settingsCacheStatus.Text = "正在后台统计缓存…";
        try
        {
            var usage = await Task.Run(() =>
            {
                static (int count, long bytes) CountFiles(string folder)
                {
                    if (!Directory.Exists(folder)) return (0, 0);
                    var count = 0; long bytes = 0;
                    foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                    {
                        try { bytes += new FileInfo(path).Length; count++; } catch (IOException) { }
                    }
                    return (count, bytes);
                }
                return (candidates: CountFiles(Path.Combine(DataDir, "candidate-cache")),
                    thumbs: CountFiles(ThumbDir));
            });
            if (!isClosing)
            {
                var memory = VideoItem.PosterCacheStats();
                settingsCacheStatus.Text = $"候选结果：{usage.candidates.count} 项 / {usage.candidates.bytes / 1048576d:0.0} MB · 缩略图：{usage.thumbs.count} 张 / {usage.thumbs.bytes / 1048576d:0.0} MB"
                    + $"\n海报内存：{memory.Entries} 张 / {memory.Bytes / 1048576d:0.0} MB（{(memory.LimitBytes == 0 ? "无限制" : $"上限 {memory.LimitBytes / 1048576d:0} MB")}）";
            }
        }
        catch (Exception ex) { if (!isClosing) settingsCacheStatus.Text = "读取缓存状态失败：" + ex.Message; }
    }

    async Task ClearCandidateCacheAsync(Button button)
    {
        if (System.Windows.MessageBox.Show(this, "删除本地候选搜索缓存？已保存的影片资料、审核记录和缩略图不会被删除。", "清理候选结果缓存",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        button.IsEnabled = false;
        try
        {
            var removed = await Task.Run(() =>
            {
                var folder = Path.Combine(DataDir, "candidate-cache");
                if (!Directory.Exists(folder)) return 0;
                var count = 0;
                foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(path); count++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                return count;
            });
            if (!isClosing && settingsCacheStatus is not null) settingsCacheStatus.Text = $"已清理 {removed} 项候选结果缓存。";
        }
        catch (Exception ex) { if (!isClosing && settingsCacheStatus is not null) settingsCacheStatus.Text = "清理失败：" + ex.Message; }
        finally { if (!isClosing) button.IsEnabled = true; }
    }
}
