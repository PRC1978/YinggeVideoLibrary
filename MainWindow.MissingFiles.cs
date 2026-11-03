using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

// Missing local files are visible but never removed automatically: the user
// decides when an index record is dropped, and the record can be restored.
public partial class MainWindow
{
    // 0 = all, 1 = only missing, 2 = only roots that could not be read.
    int availabilityFilter;
    bool cleanupRecoveryDone;

    int MissingFileCount => videos.Count(item => item.IsMissing);
    int UnavailableRootCount => videos.Count(item => item.IsUnavailable);

    int lastMissingChip = -1;
    int lastUnavailableChip = -1;
    int lastAvailabilityFilter = -1;
    bool? lastChipPageVisible;

    // Called from every RenderCards pass, so it must be free when nothing
    // changed: building brushes and re-laying out the header cost ~16 ms even
    // for a zero-hit search.
    void UpdateAvailabilityChips()
    {
        if (MissingFilterButton is null) return;
        var missing = MissingFileCount;
        var unavailable = UnavailableRootCount;
        var onVideoPage = view is "all" or "recent" or "favorites" or "categories";
        if (missing == lastMissingChip && unavailable == lastUnavailableChip &&
            availabilityFilter == lastAvailabilityFilter && onVideoPage == lastChipPageVisible)
            return;
        lastMissingChip = missing;
        lastUnavailableChip = unavailable;
        lastAvailabilityFilter = availabilityFilter;
        lastChipPageVisible = onVideoPage;
        MissingFilterButton.Content = $"不在本地 {missing}";
        MissingFilterButton.Visibility = onVideoPage && (missing > 0 || availabilityFilter == 1) ? Visibility.Visible : Visibility.Collapsed;
        UnavailableFilterButton.Content = $"位置暂不可用 {unavailable}";
        UnavailableFilterButton.Visibility = onVideoPage && (unavailable > 0 || availabilityFilter == 2) ? Visibility.Visible : Visibility.Collapsed;
        HighlightChip(MissingFilterButton, availabilityFilter == 1, Color.FromRgb(180, 35, 24));
        HighlightChip(UnavailableFilterButton, availabilityFilter == 2, Color.FromRgb(180, 83, 9));
        if (MissingFilesSummary is not null)
        {
            MissingFilesSummary.Text = missing == 0 && unavailable == 0
                ? "所有索引记录都能在磁盘上找到对应文件。"
                : $"不在本地 {missing} 条（文件已不存在）· 位置暂不可用 {unavailable} 条（扫描根未读取，未计入清理）";
        }
        if (MissingCleanupButton is not null) MissingCleanupButton.IsEnabled = missing > 0;
        if (MissingUndoButton is not null) MissingUndoButton.IsEnabled = MissingCleanupStore.HasUndoable(DataDir);
    }

    static readonly Brush ChipIdleBackground = UiTheme.SurfaceBrush;
    static readonly Brush ChipIdleBorder = UiTheme.AccentSoftBackgroundBrush;
    static readonly Brush ChipIdleForeground = UiTheme.TextTertiaryBrush;
    static readonly Brush ChipMissingBackground = new SolidColorBrush(Color.FromArgb(30, 180, 35, 24));
    static readonly Brush ChipMissingBorder = UiTheme.DangerBrush;
    static readonly Brush ChipMissingForeground = UiTheme.DangerBrush;
    static readonly Brush ChipUnavailableBackground = new SolidColorBrush(Color.FromArgb(30, 180, 83, 9));
    static readonly Brush ChipUnavailableBorder = UiTheme.DangerBrush;
    static readonly Brush ChipUnavailableForeground = UiTheme.DangerBrush;

    static void HighlightChip(Button button, bool active, Color accent)
    {
        var missingAccent = accent == Color.FromRgb(180, 35, 24);
        button.Background = active ? (missingAccent ? ChipMissingBackground : ChipUnavailableBackground) : ChipIdleBackground;
        button.BorderBrush = active ? (missingAccent ? ChipMissingBorder : ChipUnavailableBorder) : ChipIdleBorder;
        button.Foreground = active ? (missingAccent ? ChipMissingForeground : ChipUnavailableForeground) : ChipIdleForeground;
    }

    void ToggleMissingFilter(object sender, RoutedEventArgs e)
    {
        availabilityFilter = availabilityFilter == 1 ? 0 : 1;
        ShowView(view);
        UpdateAvailabilityChips();
    }

    void ToggleUnavailableFilter(object sender, RoutedEventArgs e)
    {
        availabilityFilter = availabilityFilter == 2 ? 0 : 2;
        ShowView(view);
        UpdateAvailabilityChips();
    }

    void PreviewMissingCleanup(object sender, RoutedEventArgs e)
    {
        var targets = videos.Where(item => item.IsMissing).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "没有确认丢失的记录可以清理。", "清理不在本地记录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var byRoot = targets
            .GroupBy(item => NormalizeRoots(config.Folders).FirstOrDefault(root => IsDescendantPath(root, item.Path)) ?? item.Folder)
            .OrderByDescending(group => group.Count())
            .ToList();
        var scope = string.Join(Environment.NewLine, byRoot.Take(8).Select(group => $"  · {group.Key}：{group.Count()} 条"));
        if (byRoot.Count > 8) scope += Environment.NewLine + $"  · 其余 {byRoot.Count - 8} 个位置省略";
        var samples = string.Join(Environment.NewLine, targets.Take(3).Select(item => "  · " + item.Path));
        var unavailable = UnavailableRootCount;
        var message =
            $"将清除 {targets.Count} 条索引记录，涉及 {byRoot.Count} 个位置：{Environment.NewLine}{scope}{Environment.NewLine}{Environment.NewLine}" +
            $"示例：{Environment.NewLine}{samples}{Environment.NewLine}{Environment.NewLine}" +
            (unavailable > 0
                ? $"另有 {unavailable} 条记录所在位置当前不可访问，**不会**进入清理范围。{Environment.NewLine}{Environment.NewLine}"
                : "") +
            "只删除视频库里的索引记录，不会删除、移动或修改任何视频文件。清理后可在“状态”页撤销。";
        if (MessageBox.Show(this, message, "确认清理不在本地的记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _ = PerformMissingCleanupAsync(targets);
    }

    async Task PerformMissingCleanupAsync(List<VideoItem> targets)
    {
        var entries = targets.Select(item => new MissingCleanupEntry
        {
            Path = item.Path, Size = item.Size, ModifiedTicks = item.Modified.Ticks
        }).ToList();
        var removed = new List<VideoItem>();
        var indexPreserved = false;
        var outcome = await MissingCleanupCoordinator.CleanupAsync(DataDir, entries, async () =>
        {
            foreach (var item in targets)
            {
                if (videos.Remove(item)) removed.Add(item);
                selectedVideoIds.Remove(item.Id);
            }
            try
            {
                var saved = await SaveIndexAsync(videos, NormalizeRoots(config.Folders));
                indexPreserved = !saved.Written && saved.PreservedMismatchedIndex;
                return saved.Written;
            }
            catch (Exception ex)
            {
                // The index still holds the rows, so the list must show them too.
                Log("清理不在本地记录失败：" + ex.Message);
                foreach (var item in removed) if (videos.All(existing => existing.Id != item.Id)) videos.Add(item);
                removed.Clear();
                return false;
            }
        });
        switch (outcome)
        {
            case CleanupWriteOutcome.Done:
                Log($"已清除 {removed.Count} 条不在本地的索引记录（未删除任何文件），可在状态页撤销。");
                TaskStatusText.Text = $"已清除 {removed.Count} 条不在本地的记录";
                break;
            case CleanupWriteOutcome.DoneButJournalUnmarked:
                Log($"已清除 {removed.Count} 条记录，但清理日志未能标记完成；重启后这些记录可能被恢复。");
                TaskStatusText.Text = "已清除记录，但清理日志未能标记";
                MessageBox.Show(this,
                    "索引记录已清除，但清理日志无法写入完成标记。重启后这些记录可能重新出现，届时可以再次清理；本次没有删除任何文件。",
                    "清理不在本地记录", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case CleanupWriteOutcome.JournalFailed:
                Log("清理日志无法写入，已取消清理。");
                MessageBox.Show(this, "清理日志无法写入（数据目录或插件状态目录可能只读），索引没有任何变化。",
                    "清理不在本地记录", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case CleanupWriteOutcome.IndexFailed:
                Log(indexPreserved
                    ? "清理未生效：旧索引与当前文件夹不一致，已保护旧索引。"
                    : "索引写入失败，清理未生效。");
                MessageBox.Show(this, indexPreserved
                        ? "当前文件夹设置与磁盘上的旧索引不一致，程序保护了旧索引，本次清理没有生效。请先在“视频文件夹”里重新扫描，再执行清理。"
                        : "索引写入失败，清理没有生效，清理日志已回滚，可以稍后重试。",
                    "清理不在本地记录", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
        RenderCards();
        UpdateAvailabilityChips();
    }

    async void UndoMissingCleanup(object sender, RoutedEventArgs e)
    {
        var added = new List<VideoItem>();
        var undoIndexPreserved = false;
        var outcome = await MissingCleanupCoordinator.UndoAsync(DataDir, async entries =>
        {
            foreach (var entry in entries)
            {
                if (videos.Any(item => string.Equals(item.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) continue;
                var item = RestoredVideo(entry);
                videos.Add(item);
                added.Add(item);
            }
            ApplySavedFields(added);
            try
            {
                var saved = await SaveIndexAsync(videos, NormalizeRoots(config.Folders));
                undoIndexPreserved = !saved.Written && saved.PreservedMismatchedIndex;
                return saved.Written;
            }
            catch (Exception ex)
            {
                Log("撤销清理写入索引失败：" + ex.Message);
                return false;
            }
        });
        switch (outcome)
        {
            case CleanupWriteOutcome.Done:
                Log($"已撤销清理，恢复 {added.Count} 条索引记录。");
                TaskStatusText.Text = $"已恢复 {added.Count} 条记录";
                break;
            case CleanupWriteOutcome.DoneButJournalUnmarked:
                Log($"已恢复 {added.Count} 条记录，但撤销标记未能写入；可再次点击撤销（重复恢复会被跳过）。");
                TaskStatusText.Text = "已恢复记录，但撤销标记未写入";
                break;
            case CleanupWriteOutcome.NothingToDo:
                MessageBox.Show(this, "没有可撤销的清理记录。", "撤销清理", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case CleanupWriteOutcome.JournalFailed:
                MessageBox.Show(this, "清理日志无法写入，未做任何更改。", "撤销清理", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case CleanupWriteOutcome.IndexFailed:
                // The rows only existed in memory; put the list back in sync with disk.
                foreach (var item in added) videos.Remove(item);
                MessageBox.Show(this, undoIndexPreserved
                        ? "当前文件夹设置与磁盘上的旧索引不一致，程序保护了旧索引，撤销没有生效；清理记录仍然可以撤销。请先重新扫描后再试。"
                        : "索引写入失败，撤销没有生效；清理记录仍然可以撤销，请稍后重试。",
                    "撤销清理", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
        RenderCards();
        UpdateAvailabilityChips();
    }

    VideoItem RestoredVideo(MissingCleanupEntry entry) => new()
    {
        Id = IdFor(entry.Path), Path = entry.Path, Title = Path.GetFileNameWithoutExtension(entry.Path),
        Folder = Path.GetDirectoryName(entry.Path) ?? "", Size = Math.Max(0, entry.Size),
        Modified = entry.ModifiedTicks is > 0 and var ticks ? new DateTime(ticks, DateTimeKind.Local) : DateTime.MinValue,
        Availability = VideoAvailability.Missing
    };

    // A cleanup interrupted between "journal written" and "index saved" must not
    // leave the library short of rows. Restore them once per session; a failed
    // write keeps the pending marker so the next start retries.
    async Task RecoverInterruptedCleanupAsync()
    {
        var outcome = await MissingCleanupCoordinator.RecoverAsync(DataDir, async pending =>
        {
            var restored = 0;
            foreach (var entry in pending.Entries)
            {
                if (videos.Any(item => string.Equals(item.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) continue;
                videos.Add(RestoredVideo(entry));
                restored++;
            }
            if (restored == 0) return true;
            try
            {
                var saved = await SaveIndexAsync(videos, NormalizeRoots(config.Folders));
                if (!saved.Written)
                {
                    Log("恢复中断清理时索引未写入（旧索引受保护或没有配置文件夹），已保留待恢复标记。");
                    return false;
                }
                Log($"上次清理未完成，已回滚并恢复 {restored} 条记录。");
                return true;
            }
            catch (Exception ex)
            {
                Log("恢复中断清理的索引失败，已保留待恢复标记：" + ex.Message);
                return false;
            }
        });
        if (outcome == CleanupWriteOutcome.IndexFailed)
            Log("上次清理未完成，但索引写入失败；下次启动会再次尝试恢复。");
    }
}
