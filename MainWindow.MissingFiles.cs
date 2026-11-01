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

    // Called from every RenderCards pass, so it must be free when nothing
    // changed: building brushes and re-laying out the header cost ~16 ms even
    // for a zero-hit search.
    void UpdateAvailabilityChips()
    {
        if (MissingFilterButton is null) return;
        var missing = MissingFileCount;
        var unavailable = UnavailableRootCount;
        if (missing == lastMissingChip && unavailable == lastUnavailableChip && availabilityFilter == lastAvailabilityFilter)
            return;
        lastMissingChip = missing;
        lastUnavailableChip = unavailable;
        lastAvailabilityFilter = availabilityFilter;
        MissingFilterButton.Content = $"不在本地 {missing}";
        MissingFilterButton.Visibility = missing > 0 || availabilityFilter == 1 ? Visibility.Visible : Visibility.Collapsed;
        UnavailableFilterButton.Content = $"位置暂不可用 {unavailable}";
        UnavailableFilterButton.Visibility = unavailable > 0 || availabilityFilter == 2 ? Visibility.Visible : Visibility.Collapsed;
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

    static readonly Brush ChipIdleBackground = Brushes.White;
    static readonly Brush ChipIdleBorder = new SolidColorBrush(Color.FromRgb(227, 232, 239));
    static readonly Brush ChipIdleForeground = new SolidColorBrush(Color.FromRgb(69, 83, 103));
    static readonly Brush ChipMissingBackground = new SolidColorBrush(Color.FromArgb(30, 180, 35, 24));
    static readonly Brush ChipMissingBorder = new SolidColorBrush(Color.FromRgb(180, 35, 24));
    static readonly Brush ChipMissingForeground = new SolidColorBrush(Color.FromRgb(180, 35, 24));
    static readonly Brush ChipUnavailableBackground = new SolidColorBrush(Color.FromArgb(30, 180, 83, 9));
    static readonly Brush ChipUnavailableBorder = new SolidColorBrush(Color.FromRgb(180, 83, 9));
    static readonly Brush ChipUnavailableForeground = new SolidColorBrush(Color.FromRgb(180, 83, 9));

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
        var record = MissingCleanupStore.Begin(DataDir, targets.Select(item => new MissingCleanupEntry
        {
            Path = item.Path, Size = item.Size, ModifiedTicks = item.Modified.Ticks
        }));
        var removed = new List<VideoItem>();
        try
        {
            foreach (var item in targets)
            {
                if (videos.Remove(item)) removed.Add(item);
                selectedVideoIds.Remove(item.Id);
            }
            await SaveIndexAsync(videos, NormalizeRoots(config.Folders));
            MissingCleanupStore.Complete(DataDir, record);
            Log($"已清除 {removed.Count} 条不在本地的索引记录（未删除任何文件），可在状态页撤销。");
            TaskStatusText.Text = $"已清除 {removed.Count} 条不在本地的记录";
        }
        catch (Exception ex)
        {
            // Nothing was deleted on disk; put the rows back and roll the log back
            // so the state matches what the index actually contains.
            foreach (var item in removed) if (videos.All(existing => existing.Id != item.Id)) videos.Add(item);
            MissingCleanupStore.MarkRolledBack(DataDir, record.Id);
            Log("清理不在本地记录失败：" + ex.Message);
            MessageBox.Show(this, "清理失败，索引没有变化：" + ex.Message, "清理不在本地记录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RenderCards();
        UpdateAvailabilityChips();
    }

    async void UndoMissingCleanup(object sender, RoutedEventArgs e)
    {
        var record = MissingCleanupStore.UndoLast(DataDir);
        if (record is null)
        {
            MessageBox.Show(this, "没有可撤销的清理记录。", "撤销清理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var restored = new List<VideoItem>();
        foreach (var entry in record.Entries)
        {
            if (videos.Any(item => string.Equals(item.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new VideoItem
            {
                Id = IdFor(entry.Path), Path = entry.Path, Title = Path.GetFileNameWithoutExtension(entry.Path),
                Folder = Path.GetDirectoryName(entry.Path) ?? "", Size = Math.Max(0, entry.Size),
                Modified = entry.ModifiedTicks is > 0 and var ticks ? new DateTime(ticks, DateTimeKind.Local) : DateTime.MinValue,
                Availability = VideoAvailability.Missing
            };
            videos.Add(item);
            restored.Add(item);
        }
        ApplySavedFields(restored);
        try
        {
            await SaveIndexAsync(videos, NormalizeRoots(config.Folders));
            Log($"已撤销清理，恢复 {restored.Count} 条索引记录。");
            TaskStatusText.Text = $"已恢复 {restored.Count} 条记录";
        }
        catch (Exception ex)
        {
            Log("撤销清理写入索引失败：" + ex.Message);
            MessageBox.Show(this, "已恢复内存中的记录，但写回索引失败：" + ex.Message, "撤销清理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RenderCards();
        UpdateAvailabilityChips();
    }

    // A cleanup interrupted between "journal written" and "index saved" must not
    // leave the library short of rows. Restore them once per session.
    async Task RecoverInterruptedCleanupAsync()
    {
        var pending = MissingCleanupStore.Pending(DataDir);
        if (pending is null) return;
        var restored = 0;
        foreach (var entry in pending.Entries)
        {
            if (videos.Any(item => string.Equals(item.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) continue;
            videos.Add(new VideoItem
            {
                Id = IdFor(entry.Path), Path = entry.Path, Title = Path.GetFileNameWithoutExtension(entry.Path),
                Folder = Path.GetDirectoryName(entry.Path) ?? "", Size = Math.Max(0, entry.Size),
                Modified = entry.ModifiedTicks is > 0 and var ticks ? new DateTime(ticks, DateTimeKind.Local) : DateTime.MinValue,
                Availability = VideoAvailability.Missing
            });
            restored++;
        }
        if (restored > 0)
        {
            try { await SaveIndexAsync(videos, NormalizeRoots(config.Folders)); }
            catch (Exception ex) { Log("恢复中断清理的索引失败：" + ex.Message); }
        }
        MissingCleanupStore.MarkRolledBack(DataDir, pending.Id);
        Log($"上次清理未完成，已回滚并恢复 {restored} 条记录。");
    }
}
