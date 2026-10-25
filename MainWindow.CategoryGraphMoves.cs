using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using TextBlock = System.Windows.Controls.TextBlock;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    sealed record GraphMoveUndo(CategoryGraphHistoryEntry Entry, CategoryGraphMovePlan Plan);

    // A property avoids cross-file static initializer order in this partial class.
    static string GraphMoveHistoryPath => Path.Combine(DataDir, "graph-move-history.json");
    List<CategoryGraphHistoryEntry> graphMoveHistory = [];
    long graphMoveHistoryLoadRequest;
    bool graphMoveHistoryRebindPending;
    string graphMoveHistoryIssue = "";

    GraphMoveUndo? lastGraphMoveUndo;
    Button? graphMoveUndoButton;
    TextBlock? graphMoveStatus;
    bool graphMoveInProgress;
    string graphMoveProgress = "";

    FrameworkElement BuildGraphMoveActions()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 11) };
        graphMoveUndoButton = new Button { Content = "↶ 撤销上次图形分类", Style = (Style)FindResource("Action"),
            ToolTip = "可跨重启恢复最近的图上分类操作；如果相关分类已被其他操作修改，将拒绝撤销。" };
        graphMoveUndoButton.Click += UndoGraphMove;
        graphMoveStatus = new TextBlock { Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap };
        bar.Children.Add(graphMoveUndoButton);
        bar.Children.Add(graphMoveStatus);
        UpdateGraphMoveActions();
        return bar;
    }

    void UpdateGraphMoveActions()
    {
        if (graphMoveUndoButton is not null)
            graphMoveUndoButton.IsEnabled = !graphMoveInProgress && !batchOperationInProgress && lastGraphMoveUndo is not null;
        if (graphMoveStatus is not null)
            graphMoveStatus.Text = graphMoveProgress.Length > 0 ? graphMoveProgress :
                lastGraphMoveUndo is null ? "拖动分类或视频时会先显示影响预览。" :
                $"可撤销最近操作：{lastGraphMoveUndo.Plan.Changes.Count:N0} 部视频 · 已保留最近 {graphMoveHistory.Count} 次";
        if (graphMoveStatus is not null && graphMoveProgress.Length == 0 && lastGraphMoveUndo is null && graphMoveHistoryIssue.Length > 0)
            graphMoveStatus.Text = graphMoveHistoryIssue;
    }

    void ScheduleRestoreGraphMoveHistory()
    {
        var request = ++graphMoveHistoryLoadRequest;
        var source = videos;
        // The scan may have replaced every VideoItem instance. Disable the
        // old binding immediately; the durable entries themselves stay intact.
        if (lastGraphMoveUndo is not null)
        {
            lastGraphMoveUndo = null;
            graphMoveHistoryIssue = "正在重新核对撤销记录…";
            UpdateGraphMoveActions();
        }
        if (graphMoveInProgress) { graphMoveHistoryRebindPending = true; return; }
        _ = RestoreGraphMoveHistoryAsync(request, source);
    }

    void ResumePendingGraphHistoryRebind()
    {
        if (!graphMoveHistoryRebindPending) return;
        graphMoveHistoryRebindPending = false;
        ScheduleRestoreGraphMoveHistory();
    }

    async Task RestoreGraphMoveHistoryAsync(long request, List<VideoItem> source)
    {
        try
        {
            var saved = await Task.Run(() => CategoryGraphMoveHistoryStore.Read(GraphMoveHistoryPath));
            if (request != graphMoveHistoryLoadRequest || !ReferenceEquals(source, videos) || isClosing || graphMoveInProgress) return;
            graphMoveHistory = saved.ToList();
            await RebindGraphMoveHistoryAsync(request, source);
        }
        catch (Exception error)
        {
            if (request != graphMoveHistoryLoadRequest || isClosing || graphMoveInProgress) return;
            lastGraphMoveUndo = null;
            graphMoveHistoryIssue = "撤销历史暂时无法读取；原记录已保留。";
            Log("图形分类撤销历史读取失败：" + error.Message);
            UpdateGraphMoveActions();
        }
    }

    async Task RebindGraphMoveHistoryAsync(long request, List<VideoItem> source)
    {
        GraphMoveUndo? candidate = null;
        var entries = graphMoveHistory.ToArray();
        var references = new CategoryGraphVideoReference[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            var video = source[index];
            references[index] = new(video.Id, video.Path, video);
            if ((index & 511) == 511)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (request != graphMoveHistoryLoadRequest || !ReferenceEquals(source, videos) || isClosing) return;
            }
        }
        var plans = await Task.Run(() =>
        {
            var byId = new Dictionary<string, CategoryGraphVideoReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var video in references)
                if (!byId.TryAdd(video.Id, video)) return new CategoryGraphMovePlan?[entries.Length];
            return entries.Select(entry => entry.Rebind(byId)).ToArray();
        });
        for (var index = 0; index < entries.Length; index++)
        {
            if (request != graphMoveHistoryLoadRequest || !ReferenceEquals(source, videos) || isClosing) return;
            var plan = plans[index];
            if (plan is null || !await GraphMoveMatchesCurrentAsync(plan, after: true)) continue;
            if (!await GraphMoveDiskMatchesAsync(plan, after: true)) continue;
            candidate = new(entries[index], plan);
            break;
        }
        if (request != graphMoveHistoryLoadRequest || !ReferenceEquals(source, videos) || isClosing) return;
        lastGraphMoveUndo = candidate;
        graphMoveHistoryIssue = "";
        if (candidate is null && graphMoveHistory.Count > 0)
            graphMoveHistoryIssue = "最近的撤销记录已保留；等待完整扫描，或相关分类已改变，暂不能安全撤销。";
        UpdateGraphMoveActions();
    }

    async Task<bool> GraphMoveMatchesCurrentAsync(CategoryGraphMovePlan plan, bool after)
    {
        // A branch can include the entire library. Check a bounded number of
        // objects per dispatcher turn rather than freezing the graph page.
        if (!plan.MatchesNames(config, after)) return false;
        for (var index = 0; index < plan.Changes.Count; index++)
        {
            var change = plan.Changes[index];
            if (!(after ? change.After : change.Before).Matches(change.Video, config)) return false;
            if ((index & 255) == 255) await Dispatcher.Yield(DispatcherPriority.Background);
        }
        return plan.MatchesNames(config, after);
    }

    async Task RememberGraphMoveAsync(CategoryGraphHistoryEntry entry, CategoryGraphMovePlan plan)
    {
        ++graphMoveHistoryLoadRequest;
        lastGraphMoveUndo = new(entry, plan);
        try
        {
            graphMoveHistory = (await Task.Run(() => CategoryGraphMoveHistoryStore.Add(GraphMoveHistoryPath, entry))).ToList();
            graphMoveHistoryIssue = "";
        }
        catch (Exception error)
        {
            graphMoveHistoryIssue = "本次撤销记录未能写入磁盘；本次运行仍可撤销。";
            Log("图形分类撤销历史保存失败：" + error.Message);
            MessageBox.Show(this, "分类已保存，但跨重启撤销记录暂时无法保存：" + error.Message,
                "图形分类", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateGraphMoveActions();
    }

    void BeginGraphVideoMove(VideoItem video, string target)
    {
        if (graphMoveInProgress || batchOperationInProgress) return;
        try
        {
            var plan = CategoryGraphMovePlan.ForVideo(video, config, target);
            BeginGraphMovePreview([plan]);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "分类预览", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    async void BeginGraphSourceMove(string source, string target)
    {
        if (graphMoveInProgress || batchOperationInProgress) return;
        IReadOnlyList<CategoryGraphMovePlan>? options = null;
        graphMoveInProgress = true;
        graphMoveProgress = "正在收集分类预览…";
        UpdateGraphMoveActions();
        try
        {
            var sourceVideos = videos;
            var version = categoryDataVersion;
            var scan = scanVersion;
            var names = config.CategoryNames.ToArray();
            var snapshots = new CategoryGraphVideoSnapshot[sourceVideos.Count];
            for (var index = 0; index < sourceVideos.Count; index++)
            {
                var video = sourceVideos[index];
                snapshots[index] = new(video, video.Folder, CategoryGraphState.Capture(video, config));
                if ((index & 255) == 255)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (isClosing || batchOperationInProgress || scan != scanVersion ||
                        version != categoryDataVersion || !ReferenceEquals(sourceVideos, videos))
                        throw new OperationCanceledException("视频库已变化，请重新拖动以预览。 ");
                }
            }
            // A disconnected folder must not block the UI on Directory.Exists.
            // Folder drags carry absolute paths; category drags carry names.
            if (Path.IsPathRooted(source))
            {
                var folderPlan = await Task.Run(() => CategoryGraphMovePlan.ForFolderSnapshots(
                    snapshots, names, source, target));
                if (folderPlan.Changes.Count == 0)
                {
                    MessageBox.Show(this, "此文件夹及其子文件夹没有可移动的视频。", "分类预览",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                options = [folderPlan];
            }
            else options = await Task.Run(() => new[]
            {
                CategoryGraphMovePlan.ForBranchSnapshots(snapshots, names, source, target, CategoryBranchMoveMode.Nest),
                CategoryGraphMovePlan.ForBranchSnapshots(snapshots, names, source, target, CategoryBranchMoveMode.Merge)
            }.Where(plan => plan.HasChanges).ToArray());
            if (isClosing || batchOperationInProgress || scan != scanVersion ||
                version != categoryDataVersion || !ReferenceEquals(sourceVideos, videos))
                throw new OperationCanceledException("视频库已变化，请重新拖动以预览。");
        }
        catch (Exception ex) { if (!isClosing) MessageBox.Show(this, ex.Message, "分类预览", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            graphMoveInProgress = false;
            graphMoveProgress = "";
            UpdateGraphMoveActions();
            ResumePendingGraphHistoryRebind();
        }
        if (options is not null && !isClosing) BeginGraphMovePreview(options);
    }

    void BeginGraphMovePreview(IReadOnlyList<CategoryGraphMovePlan> options)
    {
        if (options.Count == 0)
        {
            MessageBox.Show(this, "分类已经是目标位置，没有需要保存的更改。", "分类预览",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var plan = ShowGraphMovePreview(options);
        if (plan is not null) _ = ApplyGraphMoveAsync(plan);
    }

    CategoryGraphMovePlan? ShowGraphMovePreview(IReadOnlyList<CategoryGraphMovePlan> options)
    {
        CategoryGraphMovePlan? selected = null;
        var dialog = new Window { Title = "预览分类变更", Owner = this, Width = 690, Height = 440,
            MinWidth = 540, MinHeight = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var layout = new DockPanel { Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock { Text = "确认前查看会改动的分类和视频", FontSize = 18,
            FontWeight = FontWeights.SemiBold, Foreground = Brushes.MidnightBlue,
            Margin = new Thickness(0, 0, 0, 9) });
        DockPanel.SetDock(layout.Children[^1], Dock.Top);
        ComboBox? modePicker = null;
        if (options.Count > 1)
        {
            modePicker = new ComboBox { Height = 34, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var option in options)
                modePicker.Items.Add(new ComboBoxItem { Content = option.BranchMode == CategoryBranchMoveMode.Nest
                    ? $"移为子分类：{option.DestinationRoot}" : $"合并到现有分类：{option.Target}", Tag = option });
            modePicker.SelectedIndex = 0;
            DockPanel.SetDock(modePicker, Dock.Top);
            layout.Children.Add(modePicker);
        }
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Foreground = Brushes.DarkSlateGray, Margin = new Thickness(1, 1, 10, 5) };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = preview };
        void UpdatePreview() => preview.Text = ((modePicker?.SelectedItem as ComboBoxItem)?.Tag as CategoryGraphMovePlan
            ?? options[0]).PreviewText() + "\n\n确认后会保存最近 3 次图形分类撤销记录，重启后仍可安全撤销。";
        if (modePicker is not null) modePicker.SelectionChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", Width = 90, Height = 34, IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "确认修改", Width = 110, Height = 34, IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(77, 106, 219)), Foreground = Brushes.White };
        confirm.Click += (_, _) =>
        {
            selected = (modePicker?.SelectedItem as ComboBoxItem)?.Tag as CategoryGraphMovePlan ?? options[0];
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(scroll);
        dialog.Content = layout;
        return dialog.ShowDialog() == true ? selected : null;
    }

    async Task<bool> GraphMoveDiskMatchesAsync(CategoryGraphMovePlan plan, bool after) =>
        await Task.Run(() =>
        {
            if (!File.Exists(ConfigPath)) return !after;
            return TryReadJson(ConfigPath, out LibraryConfig? latest) && latest is not null &&
                   plan.MatchesSaved(latest, after);
        });

    async Task ApplyGraphMoveAsync(CategoryGraphMovePlan plan)
    {
        if (graphMoveInProgress || batchOperationInProgress) return;
        graphMoveInProgress = true;
        graphMoveProgress = "正在核对分类…";
        UpdateGraphMoveActions();
        var beforeSelection = selectedCategory;
        var afterSelection = plan.MapSelection(beforeSelection, after: true);
        // The plan holds immutable before/after arrays; VideoItem IDs and paths
        // are init-only. Serializing a large branch history can run off UI.
        var undoEntryTask = Task.Run(() => CategoryGraphHistoryEntry.FromPlan(plan,
            beforeSelection, afterSelection));
        var remembered = false;
        try
        {
            if (!await GraphMoveMatchesCurrentAsync(plan, after: false) || !await GraphMoveDiskMatchesAsync(plan, after: false))
                throw new InvalidOperationException("分类已在另一处改变。请刷新分类后重新预览，避免覆盖新更改。");
            await ProcessBatchChunksAsync(plan.Changes, change => plan.ApplyChange(change, config, after: true),
                done => { graphMoveProgress = $"正在调整分类 {done:N0} / {plan.Changes.Count:N0}…"; UpdateGraphMoveActions(); });
            plan.ApplyNames(config, after: true);
            graphMoveProgress = "正在保存分类…";
            UpdateGraphMoveActions();
            await SaveBatchConfigAsync(BatchChangeKind.Category);
            if (!await GraphMoveDiskMatchesAsync(plan, after: true))
                throw new InvalidOperationException("保存后检测到其他窗口的分类更改，请刷新视频库核对结果。");
            await RememberGraphMoveAsync(await undoEntryTask, plan);
            remembered = true;
            selectedCategory = afterSelection;
            RenderCategories();
            RenderCards();
            Log($"图形分类已保存：{plan.Changes.Count:N0} 部视频，{plan.Source} → {plan.DestinationRoot}");
        }
        catch (Exception ex)
        {
            // WriteConfigSnapshot replaces config atomically. If it did not
            // persist our after state, restore the in-memory before state.
            var diskBefore = await GraphMoveDiskMatchesAsync(plan, after: false);
            var diskAfter = !diskBefore && await GraphMoveDiskMatchesAsync(plan, after: true);
            if (diskBefore && await GraphMoveMatchesCurrentAsync(plan, after: true))
            {
                await ProcessBatchChunksAsync(plan.Changes, change => plan.ApplyChange(change, config, after: false));
                plan.ApplyNames(config, after: false);
                selectedCategory = beforeSelection;
                RenderCategories();
                RenderCards();
            }
            else if (diskAfter && await GraphMoveMatchesCurrentAsync(plan, after: true))
            {
                if (!remembered) await RememberGraphMoveAsync(await undoEntryTask, plan);
                selectedCategory = afterSelection;
                RenderCategories();
                RenderCards();
            }
            Log("图形分类未通过最终校验：" + ex.Message);
            MessageBox.Show(this, diskAfter ? "分类已写入，但最终校验未完成。请检查分类结果；上次操作仍可撤销。\n" + ex.Message :
                "分类操作未完成，已检查磁盘状态。请刷新后核对，再重试。\n" + ex.Message,
                "图形分类", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            graphMoveInProgress = false;
            graphMoveProgress = "";
            UpdateGraphMoveActions();
            ResumePendingGraphHistoryRebind();
        }
    }

    async void UndoGraphMove(object sender, RoutedEventArgs e)
    {
        if (graphMoveInProgress || batchOperationInProgress || lastGraphMoveUndo is not { } undo) return;
        var plan = undo.Plan;
        if (MessageBox.Show(this, $"恢复上次图形分类操作？\n将恢复 {plan.Changes.Count:N0} 部视频的原分类和相关分类名称。",
            "撤销图形分类", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        graphMoveInProgress = true;
        graphMoveProgress = "正在核对撤销条件…";
        UpdateGraphMoveActions();
        try
        {
            if (!await GraphMoveMatchesCurrentAsync(plan, after: true) || !await GraphMoveDiskMatchesAsync(plan, after: true))
                throw new InvalidOperationException("相关分类已再次改变，不能安全撤销。请刷新后检查当前分类。");
            await ProcessBatchChunksAsync(plan.Changes, change => plan.ApplyChange(change, config, after: false),
                done => { graphMoveProgress = $"正在恢复分类 {done:N0} / {plan.Changes.Count:N0}…"; UpdateGraphMoveActions(); });
            plan.ApplyNames(config, after: false);
            graphMoveProgress = "正在保存撤销结果…";
            UpdateGraphMoveActions();
            await SaveBatchConfigAsync(BatchChangeKind.Category);
            if (!await GraphMoveDiskMatchesAsync(plan, after: false))
                throw new InvalidOperationException("撤销结果与磁盘分类不一致，请刷新后核对。");
            if (selectedCategory == undo.Entry.SelectionAfter) selectedCategory = undo.Entry.SelectionBefore;
            lastGraphMoveUndo = null;
            ++graphMoveHistoryLoadRequest;
            try
            {
                graphMoveHistory = (await Task.Run(() => CategoryGraphMoveHistoryStore.Remove(
                    GraphMoveHistoryPath, undo.Entry.Id))).ToList();
                await RebindGraphMoveHistoryAsync(graphMoveHistoryLoadRequest, videos);
            }
            catch (Exception error)
            {
                graphMoveHistory.RemoveAll(entry => entry.Id == undo.Entry.Id);
                graphMoveHistoryIssue = "分类已撤销，但历史记录清理失败；记录会在下次读取时核对。";
                Log("撤销历史清理失败：" + error.Message);
            }
            RenderCategories();
            RenderCards();
            Log($"已撤销图形分类：{plan.Changes.Count:N0} 部视频");
        }
        catch (Exception ex)
        {
            // Keep the undo record if the before state was not persisted.
            if (await GraphMoveDiskMatchesAsync(plan, after: true) && await GraphMoveMatchesCurrentAsync(plan, after: false))
            {
                await ProcessBatchChunksAsync(plan.Changes, change => plan.ApplyChange(change, config, after: true));
                plan.ApplyNames(config, after: true);
                RenderCategories();
                RenderCards();
            }
            Log("图形分类撤销失败：" + ex.Message);
            MessageBox.Show(this, "撤销未完成：" + ex.Message, "撤销图形分类",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            graphMoveInProgress = false;
            graphMoveProgress = "";
            UpdateGraphMoveActions();
            ResumePendingGraphHistoryRebind();
        }
    }
}
