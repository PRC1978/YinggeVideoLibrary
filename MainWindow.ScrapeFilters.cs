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
    HashSet<string> scrapeSkippedTags = new(StringComparer.CurrentCultureIgnoreCase);

    void InitializeScrapeFiltersUi()
    {
        if (ScrapePanel.Child is not StackPanel panel) return;
        scrapeSkippedTags = ParseTags(config.ScrapeSkipTags).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        Toolbar.Children.Remove(ScrapeBatchButton);
        var titleAction = Toolbar.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Content, "标题搜刮"));
        if (titleAction is not null) Toolbar.Children.Remove(titleAction);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        ScrapeBatchButton.Style = (Style)FindResource("Primary");
        actions.Children.Add(ScrapeBatchButton);
        if (titleAction is not null) actions.Children.Add(titleAction);
        var preview = new Button { Content = "预览入队范围", Style = (Style)FindResource("Action") };
        preview.Click += (_, _) => PreviewScrapeRange();
        actions.Children.Add(preview);
        panel.Children.Insert(3, actions);

        var section = new StackPanel { Margin = new Thickness(0, 2, 0, 18) };
        section.Children.Add(new TextBlock { Text = "批量搜刮条件", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        section.Children.Add(new TextBlock { Text = "以下条件同时用于新入队视频和已有队列；跳过的视频可在调整条件后重新入队。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 8) });
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

    void PreviewScrapeRange()
    {
        if (!ReadScrapeFilterSettings()) return;
        var candidate = videos.Where(video => !metadata.ContainsKey(video.Id) && !config.AiReviews.ContainsKey(video.Id))
            .Where(video => !string.IsNullOrWhiteSpace(JavDbScraper.GuessCode(video.Title)))
            .Where(video => scrapeOnlySelectedBox?.IsChecked != true || selectedVideoIds.Contains(video.Id)).ToList();
        var evaluated = candidate.Select(video => (video, reason: ScrapeSkipReason(video))).ToList();
        var groups = evaluated.Where(item => item.reason is not null).GroupBy(item => item.reason).Select(group => $"{group.Key}：{group.Count()} 部");
        var eligible = evaluated.Where(item => item.reason is null).Select(item => item.video).ToList();
        MessageBox.Show(this, $"有番号的未匹配视频：{candidate.Count} 部\n符合条件：{eligible.Count} 部\n每次最多入队 {MaxScrapeRequestsPerClick} 部。\n\n" + string.Join("\n", groups) + "\n\n前几项：\n" + string.Join("\n", eligible.Take(8).Select(video => video.Title)), "搜刮范围预览");
    }
}
