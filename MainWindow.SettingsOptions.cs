using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    TextBlock? settingsCacheStatus;

    void InitializeSettingsOptions()
    {
        if (SettingsPanel.Child is not StackPanel panel) return;
        panel.Children.Add(BuildAppearanceOptions());
        panel.Children.Add(BuildScrapeOptions());
        panel.Children.Add(BuildLibraryOptions());
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
        return section;
    }

    StackPanel BuildLibraryOptions()
    {
        var section = new StackPanel { Name = "SettingsLibraryOptions", Margin = new Thickness(0, 16, 0, 8) };
        section.Children.Add(new TextBlock { Text = "资料库和缓存", FontWeight = FontWeights.SemiBold });
        section.Children.Add(SettingsHint("配置、视频索引和缩略图保存在同一用户的共用数据目录。"));
        settingsCacheStatus = SettingsHint($"数据目录：{DataDir}");
        section.Children.Add(settingsCacheStatus);
        var buttons = new WrapPanel();
        var inspect = new Button { Content = "查看缓存占用", Style = (Style)FindResource("Action") };
        inspect.Click += async (_, _) => await RefreshCacheUsageAsync();
        buttons.Children.Add(inspect);
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
        return section;
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
                settingsCacheStatus.Text = $"候选结果：{usage.candidates.count} 项 / {usage.candidates.bytes / 1048576d:0.0} MB · 缩略图：{usage.thumbs.count} 张 / {usage.thumbs.bytes / 1048576d:0.0} MB\n{DataDir}";
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
