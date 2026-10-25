using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using StackPanel = System.Windows.Controls.StackPanel;
using WrapPanel = System.Windows.Controls.WrapPanel;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    ComboBox? reviewStatusFilter;
    ComboBox? reviewSourceFilter;
    ComboBox? reviewConfidenceFilter;
    ComboBox? reviewSavedViewPicker;
    Button? reviewDeleteViewButton;
    TextBlock? reviewSavedViewNotice;
    IReadOnlyList<ReviewSavedView> reviewSavedViews = [];
    Button? reviewUndoButton;
    Button? reviewPreviousPageButton;
    Button? reviewNextPageButton;
    TextBlock? reviewPageLabel;
    TextBlock? reviewHistoryWarning;
    const int ReviewPageSize = 12;
    int reviewPageIndex;
    bool updatingReviewSources;
    bool applyingReviewSavedView;
    bool reviewHistoryReady;
    int reviewHistoryReloadRequests;
    int reviewHistoryInvalidLines;
    int reviewHistoryMutationVersion;
    readonly SemaphoreSlim reviewHistoryReadGate = new(1, 1);
    readonly List<ReviewTransaction> reviewHistory = [];
    PreparedReviewApproval? reviewPreparedPreview;

    void InitializeReviewWorkflowUi()
    {
        if (ScrapePanel.Child is not StackPanel panel) return;
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        reviewStatusFilter = MakeReviewFilter(["状态：全部", "待审核", "已否决", "已通过", "未找到"]);
        reviewSourceFilter = MakeReviewFilter(["来源：全部"]);
        reviewConfidenceFilter = MakeReviewFilter(["可信度：全部", "低于 40%", "40–79%", "80% 以上"]);
        foreach (var filter in new[] { reviewStatusFilter, reviewSourceFilter, reviewConfidenceFilter })
        {
            filter.SelectionChanged += (_, _) =>
            {
                if (updatingReviewSources || applyingReviewSavedView) return;
                if (reviewSavedViewPicker is not null) reviewSavedViewPicker.SelectedIndex = 0;
                if (reviewDeleteViewButton is not null) reviewDeleteViewButton.IsEnabled = false;
                reviewPageIndex = 0;
                RefreshReviewQueue();
            };
            bar.Children.Add(filter);
        }
        panel.Children.Insert(Math.Max(0, panel.Children.Count - 2), bar);
        var savedViewBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        reviewSavedViewPicker = new ComboBox { Width = 190, Height = 32, Margin = new Thickness(0, 0, 7, 0),
            ToolTip = "选择已保存的状态、来源和可信度组合" };
        reviewSavedViewPicker.SelectionChanged += (_, _) =>
        {
            if (applyingReviewSavedView) return;
            var name = (reviewSavedViewPicker.SelectedItem as ComboBoxItem)?.Tag as string;
            if (reviewDeleteViewButton is not null) reviewDeleteViewButton.IsEnabled = name is not null;
            var saved = reviewSavedViews.FirstOrDefault(view => string.Equals(view.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (saved is not null) ApplyReviewSavedView(saved);
        };
        var saveView = new Button { Content = "保存当前组合", Style = (Style)FindResource("Action") };
        saveView.Click += (_, _) => SaveReviewView();
        reviewDeleteViewButton = new Button { Content = "删除此视图", Style = (Style)FindResource("Action"), IsEnabled = false };
        reviewDeleteViewButton.Click += (_, _) => DeleteReviewView();
        var reloadViews = new Button { Content = "刷新视图", Style = (Style)FindResource("Action") };
        reloadViews.Click += (_, _) =>
        {
            var selectedName = (reviewSavedViewPicker.SelectedItem as ComboBoxItem)?.Tag as string;
            ReloadSavedReviewViews(selectedName);
            var saved = reviewSavedViews.FirstOrDefault(view => string.Equals(view.Name, selectedName, StringComparison.CurrentCultureIgnoreCase));
            if (saved is not null) ApplyReviewSavedView(saved);
        };
        savedViewBar.Children.Add(reviewSavedViewPicker);
        savedViewBar.Children.Add(saveView);
        savedViewBar.Children.Add(reviewDeleteViewButton);
        savedViewBar.Children.Add(reloadViews);
        panel.Children.Insert(Math.Max(0, panel.Children.Count - 2), savedViewBar);
        reviewSavedViewNotice = new TextBlock
        {
            Text = "组合状态、来源与可信度后可保存视图；批量选择只作用于当前筛选结果。",
            Foreground = System.Windows.Media.Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 9)
        };
        panel.Children.Insert(Math.Max(0, panel.Children.Count - 2), reviewSavedViewNotice);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var preview = new Button { Content = "预览批量审批", Style = (Style)FindResource("Action") };
        preview.Click += (_, _) => PreviewReviewBatch();
        reviewUndoButton = new Button { Content = "撤销上次审核", Style = (Style)FindResource("Action") };
        reviewUndoButton.Click += (_, _) => UndoLastReview();
        var history = new Button { Content = "审核历史", Style = (Style)FindResource("Action") };
        history.Click += (_, _) => ShowReviewHistory();
        var export = new Button { Content = "导出审核结果", Style = (Style)FindResource("Action") };
        export.Click += (_, _) => ExportReviews();
        var exportHistory = new Button { Content = "导出完整历史", Style = (Style)FindResource("Action") };
        exportHistory.Click += (_, _) => ExportReviewHistory();
        foreach (var button in new[] { preview, reviewUndoButton, history, export, exportHistory }) actions.Children.Add(button);
        panel.Children.Insert(Math.Max(0, panel.Children.Count - 2), actions);
        reviewHistoryWarning = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 9),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Insert(Math.Max(0, panel.Children.Count - 2), reviewHistoryWarning);
        var pager = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 7, 0, 0) };
        reviewPreviousPageButton = new Button { Content = "上一页", Style = (Style)FindResource("Action") };
        reviewPreviousPageButton.Click += (_, _) => { reviewPageIndex--; RefreshReviewQueue(); ScrollReviewQueueToTop(); };
        reviewPageLabel = new TextBlock { VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(7, 0, 14, 0) };
        reviewNextPageButton = new Button { Content = "下一页", Style = (Style)FindResource("Action") };
        reviewNextPageButton.Click += (_, _) => { reviewPageIndex++; RefreshReviewQueue(); ScrollReviewQueueToTop(); };
        pager.Children.Add(reviewPreviousPageButton);
        pager.Children.Add(reviewPageLabel);
        pager.Children.Add(reviewNextPageButton);
        panel.Children.Add(pager);
        UpdateReviewSourceFilter();
        ReloadSavedReviewViews();
        UpdateReviewUndoButton();
        // Large JSONL history is parsed after first paint, away from the UI
        // thread. Undo stays unavailable until a verified snapshot is ready.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => _ = ReloadReviewHistoryAsync()));
    }

    async Task<bool> ReloadReviewHistoryAsync()
    {
        reviewHistoryReloadRequests++;
        reviewHistoryReady = false;
        UpdateReviewUndoButton();
        await reviewHistoryReadGate.WaitAsync();
        try
        {
            while (!isClosing)
            {
                var mutationVersion = reviewHistoryMutationVersion;
                var (loaded, invalid) = await Task.Run(() =>
                {
                    var rows = ReviewTransactionStore.Load(DataDir, out var invalidRows);
                    return (rows, invalidRows);
                });
                if (mutationVersion != reviewHistoryMutationVersion) continue;
                reviewHistory.Clear();
                reviewHistory.AddRange(loaded);
                if (invalid != reviewHistoryInvalidLines && invalid > 0)
                    Log($"审核历史中有 {invalid} 条损坏或不支持的记录，已跳过；有效记录仍可查看和安全撤销。");
                reviewHistoryInvalidLines = invalid;
                if (reviewHistoryWarning is not null)
                {
                    reviewHistoryWarning.Text = invalid > 0
                        ? $"审核历史已跳过 {invalid} 条损坏或不支持的记录。有效记录可查看和撤销；撤销前会核对当前磁盘状态。"
                        : "";
                    reviewHistoryWarning.Visibility = invalid > 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                reviewHistoryReady = true;
                UpdateReviewUndoButton();
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log("审核历史读取失败：" + ex.Message);
            if (reviewHistoryWarning is not null)
            {
                reviewHistoryWarning.Text = "审核历史暂时无法读取；撤销已停用以保护当前数据。";
                reviewHistoryWarning.Visibility = Visibility.Visible;
            }
            return false;
        }
        finally
        {
            reviewHistoryReadGate.Release();
            reviewHistoryReloadRequests--;
            UpdateReviewUndoButton();
        }
    }

    static ComboBox MakeReviewFilter(IEnumerable<string> options)
    {
        var box = new ComboBox { Width = 135, Height = 32, Margin = new Thickness(0, 0, 7, 0) };
        foreach (var option in options) box.Items.Add(option);
        box.SelectedIndex = 0;
        return box;
    }

    void ReloadSavedReviewViews(string? selectName = null)
    {
        if (reviewSavedViewPicker is null) return;
        try
        {
            reviewSavedViews = ReviewSavedViewStore.Load(DataDir, out var recovered);
            reviewSavedViewNotice!.Text = recovered
                ? "审核视图主文件损坏，已从恢复副本读取。保存新视图会重建主文件。"
                : "组合状态、来源与可信度后可保存视图；批量选择只作用于当前筛选结果。";
        }
        catch (Exception ex)
        {
            reviewSavedViews = [];
            reviewSavedViewNotice!.Text = "保存的审核视图无法读取，原文件已保留。请先检查数据目录：" + ex.Message;
            Log("审核视图读取失败：" + ex.Message);
        }
        applyingReviewSavedView = true;
        try
        {
            reviewSavedViewPicker.Items.Clear();
            reviewSavedViewPicker.Items.Add(new ComboBoxItem { Content = "当前条件（未保存）" });
            foreach (var view in reviewSavedViews)
                reviewSavedViewPicker.Items.Add(new ComboBoxItem { Content = view.Name, Tag = view.Name,
                    ToolTip = $"状态：{ReviewStatusLabel(view.Status)} · 来源：{(view.Source.Length == 0 ? "全部" : view.Source)} · 可信度：{ReviewConfidenceLabel(view.Confidence)}" });
            reviewSavedViewPicker.SelectedItem = reviewSavedViewPicker.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, selectName, StringComparison.CurrentCultureIgnoreCase))
                ?? reviewSavedViewPicker.Items[0];
            if (reviewDeleteViewButton is not null) reviewDeleteViewButton.IsEnabled = selectName is not null && reviewSavedViewPicker.SelectedIndex > 0;
        }
        finally { applyingReviewSavedView = false; }
    }

    static string ReviewStatusLabel(string status) => status switch
    {
        "pending" => "待审核", "rejected" => "已否决", "approved" => "已通过", "unmatched" => "未找到", _ => "全部"
    };

    static string ReviewConfidenceLabel(string confidence) => confidence switch
    {
        "low" => "低于 40%", "medium" => "40–79%", "high" => "80% 以上", _ => "全部"
    };

    ReviewSavedView CurrentReviewView(string name) => new()
    {
        Name = name,
        Status = (reviewStatusFilter?.SelectedIndex ?? 0) switch
        { 1 => "pending", 2 => "rejected", 3 => "approved", 4 => "unmatched", _ => "all" },
        Source = (reviewSourceFilter?.SelectedIndex ?? 0) == 0 ? "" : reviewSourceFilter?.SelectedItem as string ?? "",
        Confidence = (reviewConfidenceFilter?.SelectedIndex ?? 0) switch
        { 1 => "low", 2 => "medium", 3 => "high", _ => "all" }
    };

    void ApplyReviewSavedView(ReviewSavedView saved)
    {
        if (reviewStatusFilter is null || reviewSourceFilter is null || reviewConfidenceFilter is null) return;
        applyingReviewSavedView = true;
        try
        {
            reviewStatusFilter.SelectedIndex = saved.Status switch
            { "pending" => 1, "rejected" => 2, "approved" => 3, "unmatched" => 4, _ => 0 };
            reviewConfidenceFilter.SelectedIndex = saved.Confidence switch
            { "low" => 1, "medium" => 2, "high" => 3, _ => 0 };
            UpdateReviewSourceFilter();
            if (saved.Source.Length == 0) reviewSourceFilter.SelectedIndex = 0;
            else
            {
                var source = reviewSourceFilter.Items.Cast<string>()
                    .FirstOrDefault(item => string.Equals(item, saved.Source, StringComparison.CurrentCultureIgnoreCase));
                if (source is null) { reviewSourceFilter.Items.Add(saved.Source); source = saved.Source; }
                reviewSourceFilter.SelectedItem = source;
            }
        }
        finally { applyingReviewSavedView = false; }
        reviewPageIndex = 0;
        RefreshReviewQueue();
        ScrollReviewQueueToTop();
    }

    void SaveReviewView()
    {
        var currentName = (reviewSavedViewPicker?.SelectedItem as ComboBoxItem)?.Tag as string;
        var name = PromptText("保存审核视图", "视图名称（最多 40 字）：", currentName ?? "")?.Trim();
        if (name is null) return;
        if (reviewSavedViews.Any(view => string.Equals(view.Name, name, StringComparison.CurrentCultureIgnoreCase)) &&
            MessageBox.Show(this, $"已存在“{name}”。要用当前筛选条件覆盖此视图吗？", "保存审核视图",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            reviewSavedViews = ReviewSavedViewStore.Upsert(DataDir, CurrentReviewView(name));
            ReloadSavedReviewViews(name);
            Log("已保存审核视图：" + name);
        }
        catch (Exception ex) { MessageBox.Show(this, "审核视图未保存：" + ex.Message, "保存审核视图", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void DeleteReviewView()
    {
        var name = (reviewSavedViewPicker?.SelectedItem as ComboBoxItem)?.Tag as string;
        if (name is null || MessageBox.Show(this, $"删除审核视图“{name}”？审核记录不会改变。", "删除审核视图",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            reviewSavedViews = ReviewSavedViewStore.Delete(DataDir, name);
            ReloadSavedReviewViews();
            Log("已删除审核视图：" + name);
        }
        catch (Exception ex) { MessageBox.Show(this, "审核视图未删除：" + ex.Message, "删除审核视图", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void ScrollReviewQueueToTop()
    {
        if (ReviewQueuePanel.Parent is ScrollViewer queueScroll) queueScroll.ScrollToTop();
    }

    void UpdateReviewSourceFilter()
    {
        if (reviewSourceFilter is null) return;
        updatingReviewSources = true;
        try
        {
        var selected = reviewSourceFilter.SelectedItem as string;
        var sources = config.AiReviews.Values.Select(item => item.CandidateSource).Where(source => !string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        // A saved source can temporarily have no reviews. Keep it selected so
        // the view continues to mean the same thing after a restart or rescan.
        if (!string.IsNullOrWhiteSpace(selected) && selected != "来源：全部" && !sources.Contains(selected, StringComparer.CurrentCultureIgnoreCase)) sources.Add(selected);
        sources = sources.OrderBy(source => source, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (reviewSourceFilter.Items.Count == sources.Count + 1 && sources.SequenceEqual(reviewSourceFilter.Items.Cast<string>().Skip(1))) return;
        reviewSourceFilter.Items.Clear();
        reviewSourceFilter.Items.Add("来源：全部");
        foreach (var source in sources) reviewSourceFilter.Items.Add(source);
        reviewSourceFilter.SelectedItem = selected is not null && reviewSourceFilter.Items.Contains(selected) ? selected : "来源：全部";
        }
        finally { updatingReviewSources = false; }
    }

    bool ReviewMatchesFilter(AiReviewRecord review)
    {
        var status = (reviewStatusFilter?.SelectedIndex ?? 0) switch
        { 1 => "pending", 2 => "rejected", 3 => "approved", 4 => "unmatched", _ => "all" };
        var source = (reviewSourceFilter?.SelectedIndex ?? 0) == 0 ? "" : reviewSourceFilter?.SelectedItem as string ?? "";
        var confidence = (reviewConfidenceFilter?.SelectedIndex ?? 0) switch
        { 1 => "low", 2 => "medium", 3 => "high", _ => "all" };
        return ReviewMatchesFilter(review, status, source, confidence);
    }

    static bool ReviewMatchesFilter(AiReviewRecord review, string status, string source, string confidence)
    {
        if (status != "all" && review.Status != status) return false;
        if (source.Length > 0 && !string.Equals(source, review.CandidateSource, StringComparison.CurrentCultureIgnoreCase)) return false;
        return confidence switch
        {
            "low" => review.Confidence < 40,
            "medium" => review.Confidence >= 40 && review.Confidence < 80,
            "high" => review.Confidence >= 80,
            _ => true
        };
    }

    static List<string> CollectReviewIdsForSelection(IEnumerable<AiReviewRecord> reviews,
        IEnumerable<string> videoIds, Func<AiReviewRecord, bool> matchesFilter,
        bool lowConfidenceOnly, int manualThreshold)
    {
        var available = videoIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return reviews.Where(matchesFilter)
            .Where(review => review.Status is "pending" or "rejected")
            .Where(review => !lowConfidenceOnly || review.Confidence < manualThreshold)
            .Select(review => review.VideoId)
            .Where(available.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static Dictionary<string, VideoItem> BuildReviewVideoMap(IEnumerable<VideoItem> source,
        IReadOnlySet<string> targetIds)
    {
        var map = new Dictionary<string, VideoItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in source)
            if (targetIds.Contains(video.Id)) map.TryAdd(video.Id, video);
        return map;
    }

    internal static bool VerifyReviewSelectionForDiagnostics()
    {
        var videoIds = Enumerable.Range(0, 32_000).Select(index => $"video-{index}").ToArray();
        var reviews = Enumerable.Range(0, 10_000).Select(index => new AiReviewRecord
        {
            VideoId = $"video-{index}", Status = "pending", Confidence = index % 100
        }).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var all = CollectReviewIdsForSelection(reviews, videoIds,
            review => review.Confidence >= 20, lowConfidenceOnly: false, manualThreshold: 40);
        var low = CollectReviewIdsForSelection(reviews, videoIds,
            review => review.Confidence >= 20, lowConfidenceOnly: true, manualThreshold: 40);
        var targets = reviews.Select(review => review.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sampleVideos = videoIds.Select(id => new VideoItem
        {
            Id = id, Path = id, Folder = "", Title = id, Size = 1, Modified = default
        });
        var mapped = BuildReviewVideoMap(sampleVideos, targets);
        var selectionValid = all.Count == 8_000 && low.Count == 2_000 && mapped.Count == 10_000 &&
            all.All(id => id.StartsWith("video-", StringComparison.Ordinal)) &&
            clock.Elapsed < TimeSpan.FromSeconds(3);
        var combined = new[]
        {
            new AiReviewRecord { Status = "pending", CandidateSource = "JavDB", Confidence = 32 },
            new AiReviewRecord { Status = "pending", CandidateSource = "JavDB", Confidence = 82 },
            new AiReviewRecord { Status = "rejected", CandidateSource = "JavDB", Confidence = 24 },
            new AiReviewRecord { Status = "pending", CandidateSource = "TMDB", Confidence = 24 }
        };
        return selectionValid && combined.Count(item => ReviewMatchesFilter(item, "pending", "JavDB", "low")) == 1 &&
            ReviewSavedViewStore.VerifyForDiagnostics() && ReviewFieldApproval.VerifyForDiagnostics() &&
            ReviewApprovalPlanner.VerifyForDiagnostics();
    }

    void PreviewReviewBatch()
    {
        reviewPreparedPreview = null;
        var available = videos.Select(video => video.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = reviewSelection.Where(id => available.Contains(id) &&
            config.AiReviews.TryGetValue(id, out var review) && review.Status is ("pending" or "rejected"))
            .OrderBy(id => config.AiReviews[id].Confidence).ToList();
        if (ids.Count == 0) { MessageBox.Show(this, "先选择要审核的候选。", "审批预览"); return; }
        try
        {
            var rows = BuildReviewApprovalRows(ids);
            if (!ShowReviewFieldChoices(rows, out var prepared) || prepared is null) return;
            reviewPreparedPreview = prepared;
            MessageBox.Show(this, prepared.Describe() + "\n\n此处尚未提交。点“通过选中”会使用这份逐字段方案；若资料变化会拒绝提交。",
                "批量审批预览", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法预览审核：" + ex.Message, "批量审批预览", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    ReviewItemSnapshot CaptureReviewState(string id, IReadOnlySet<string>? trackedAutoTags = null) => new()
    {
        Review = config.AiReviews.TryGetValue(id, out var review) ? CloneReview(review) : null,
        Metadata = metadata.TryGetValue(id, out var record) ? record.Clone() : null,
        Tags = config.Tags.TryGetValue(id, out var tags) ? tags.ToList() : null,
        AutoAmateur = trackedAutoTags?.Contains(id) ?? config.AutoAmateurTags.Contains(id, StringComparer.OrdinalIgnoreCase)
    };

    Dictionary<string, ReviewItemSnapshot> CaptureReviewStates(IEnumerable<string> ids)
    {
        var tracked = config.AutoAmateurTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ids.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, id => CaptureReviewState(id, tracked), StringComparer.OrdinalIgnoreCase);
    }

    ReviewItemSnapshot WithAutomaticAmateurTag(VideoItem video, ReviewItemSnapshot state, bool shouldTag)
    {
        var result = state.Clone();
        if (shouldTag)
        {
            // A manually assigned 素人 tag is never claimed as an automatic
            // tag and must survive an unrelated approval or undo.
            if (video.Tags.Contains("素人", StringComparer.CurrentCultureIgnoreCase)) return result;
            var tags = video.Tags.ToList();
            tags.Add("素人");
            result.Tags = ParseTags(string.Join(",", tags));
            result.AutoAmateur = true;
        }
        else if (result.AutoAmateur)
        {
            result.Tags = ParseTags(string.Join(",", video.Tags.Where(tag =>
                !tag.Equals("素人", StringComparison.CurrentCultureIgnoreCase))));
            result.AutoAmateur = false;
        }
        return result;
    }

    void ApplyReviewStates(IReadOnlyDictionary<string, ReviewItemSnapshot> states)
    {
        var affected = states.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        config.AutoAmateurTags.RemoveAll(id => affected.Contains(id));
        foreach (var (id, state) in states)
        {
            if (state.Review is null) config.AiReviews.Remove(id);
            else config.AiReviews[id] = CloneReview(state.Review);
            if (state.Metadata is null) metadata.Remove(id);
            else metadata[id] = state.Metadata.Clone();
            if (state.Tags is null || state.Tags.Count == 0) config.Tags.Remove(id);
            else config.Tags[id] = state.Tags.ToList();
            if (state.AutoAmateur) config.AutoAmateurTags.Add(id);
        }
    }

    void RefreshReviewVideos(IEnumerable<string> ids)
    {
        var affected = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = videos.Where(video => affected.Contains(video.Id)).ToList();
        foreach (var video in items) { video.DisplayTitle = video.Title; video.Code = null; }
        ApplySavedFields(items);
        foreach (var video in items) video.RefreshTags();
    }

    void AppendReviewHistory(ReviewTransaction entry)
    {
        ReviewTransactionStore.Append(DataDir, entry);
        reviewHistoryMutationVersion++;
        reviewHistory.Add(entry);
        UpdateReviewUndoButton();
    }

    // Caller holds ConfigWriteMutex. The durable intent is recorded before
    // either data file changes; recovery can finish a process interrupted at
    // any of the config, metadata or history writes.
    bool TryCommitJournaledReview(ReviewTransaction transaction, out string error)
    {
        error = "";
        var begun = false;
        IDisposable? metadataLease = null;
        try
        {
            metadataLease = MetadataStore.AcquireReviewWriteLease();
            ReviewTransactionStore.Begin(DataDir, transaction);
            begun = true;
            ApplyReviewStates(transaction.After);
            SaveConfig();
            if (transaction.Before.Any(pair =>
                System.Text.Json.JsonSerializer.Serialize(pair.Value.Metadata) !=
                System.Text.Json.JsonSerializer.Serialize(transaction.After[pair.Key].Metadata)))
                MetadataStore.SaveReviewUndo(DataDir, metadata,
                    transaction.Before.ToDictionary(pair => pair.Key, pair => pair.Value.Metadata, StringComparer.OrdinalIgnoreCase),
                    transaction.After.ToDictionary(pair => pair.Key, pair => pair.Value.Metadata, StringComparer.OrdinalIgnoreCase));
            if (!PersistedReviewStatesMatch(transaction.After))
                throw new InvalidDataException("审核数据写入后与预览不一致，已停止提交。");
            AppendReviewHistory(transaction);
            ReviewTransactionStore.Complete(DataDir, transaction);
            RefreshReviewVideos(transaction.After.Keys);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (!begun) return false;
            try
            {
                if (ReviewTransactionStore.IsRecorded(DataDir, transaction))
                {
                    // History is the commit marker. Never roll back an action
                    // already present in the audit trail.
                    ReviewTransactionStore.Complete(DataDir, transaction);
                    RefreshReviewVideos(transaction.After.Keys);
                    return true;
                }
                ReviewTransactionStore.RollbackUncommitted(DataDir, transaction);
                ApplyReviewStates(transaction.Before);
                configBaseline = CloneLibraryConfig(config);
                metadata = MetadataStore.Load(DataDir);
                RefreshReviewVideos(transaction.Before.Keys);
            }
            catch (Exception recoveryEx)
            {
                error += "；恢复未完成，请停止操作并重启软件：" + recoveryEx.Message;
                Log("审核事务保留待恢复：" + recoveryEx.Message);
            }
            return false;
        }
        finally { metadataLease?.Dispose(); }
    }

    void UpdateReviewUndoButton()
    {
        if (reviewUndoButton is not null)
        {
            reviewUndoButton.IsEnabled = reviewHistoryReady && reviewHistoryReloadRequests == 0 &&
                ReviewTransactionStore.LastUndoable(reviewHistory) is not null;
            reviewUndoButton.ToolTip = reviewHistoryReady && reviewHistoryReloadRequests == 0
                ? "撤销最近一条状态仍匹配的有效审核记录" : "正在后台读取审核历史，请稍候";
        }
    }

    async void UndoLastReview()
    {
        if (!await ReloadReviewHistoryAsync())
        {
            MessageBox.Show(this, "审核历史暂时无法读取，已停止撤销以保护当前数据。", "撤销审核");
            return;
        }
        var entry = ReviewTransactionStore.LastUndoable(reviewHistory);
        if (entry is null) return;
        var live = CaptureReviewStates(entry.After.Keys);
        if (entry.After.Any(pair => !ReviewTransactionStore.Equal(live[pair.Key], pair.Value)) ||
            !PersistedReviewStatesMatch(entry.After))
        {
            MessageBox.Show(this, "这些审核项的状态、资料或标签已在此窗口或其他窗口更改。为避免覆盖新内容，未执行撤销。", "撤销审核");
            return;
        }
        var locked = false;
        try
        {
            try { ConfigWriteMutex.WaitOne(); locked = true; }
            catch (AbandonedMutexException) { locked = true; }
            if (!PersistedReviewStatesMatch(entry.After) ||
                entry.After.Any(pair => !ReviewTransactionStore.Equal(CaptureReviewState(pair.Key), pair.Value)))
                throw new InvalidOperationException("审核项在撤销前已改变，请刷新后重试。");
            var transaction = ReviewTransactionStore.Create("undo", live, entry.Before,
                undoesTransactionId: entry.Id);
            if (!TryCommitJournaledReview(transaction, out var error))
                throw new IOException(error);
            RefreshReviewQueue(); RenderCards();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "撤销未完成：" + ex.Message, "审核历史", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { if (locked) ConfigWriteMutex.ReleaseMutex(); }
    }

    bool PersistedReviewStatesMatch(IReadOnlyDictionary<string, ReviewItemSnapshot> expected)
    {
        try
        {
            if (!TryReadJson(ConfigPath, out LibraryConfig? diskConfig) || diskConfig?.AiReviews is null ||
                diskConfig.Tags is null || diskConfig.AutoAmateurTags is null) return false;
            var metadataPath = Path.Combine(DataDir, "metadata.json");
            Dictionary<string, MovieMetadata> diskMetadata;
            if (File.Exists(metadataPath))
            {
                if (!TryReadJson(metadataPath, out Dictionary<string, MovieMetadata>? loaded) || loaded is null) return false;
                diskMetadata = new Dictionary<string, MovieMetadata>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            else diskMetadata = [];
            var diskReviews = new Dictionary<string, AiReviewRecord>(diskConfig.AiReviews, StringComparer.OrdinalIgnoreCase);
            var diskTags = new Dictionary<string, List<string>>(diskConfig.Tags, StringComparer.OrdinalIgnoreCase);
            var diskAutoTags = diskConfig.AutoAmateurTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, state) in expected)
            {
                var disk = new ReviewItemSnapshot
                {
                    Review = diskReviews.TryGetValue(id, out var review) ? review : null,
                    Metadata = diskMetadata.TryGetValue(id, out var record) ? record : null,
                    Tags = diskTags.TryGetValue(id, out var tags) ? tags : null,
                    AutoAmateur = diskAutoTags.Contains(id)
                };
                if (!ReviewTransactionStore.Equal(disk, state)) return false;
            }
            return true;
        }
        catch (Exception ex) { Log("审核撤销时读取磁盘状态失败：" + ex.Message); return false; }
    }

    async void ShowReviewHistory()
    {
        if (!await ReloadReviewHistoryAsync())
        { MessageBox.Show(this, "审核历史暂时无法读取。", "审核历史"); return; }
        var transactions = reviewHistory.ToArray();
        var rows = await Task.Run(() => ReviewHistoryRow.FromTransactions(transactions));
        if (isClosing) return;
        var window = new Window { Title = reviewHistoryInvalidLines > 0
            ? $"完整审核历史（已跳过 {reviewHistoryInvalidLines} 条损坏记录）" : "完整审核历史",
            Owner = this, Width = 1120, Height = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, ItemsSource = rows };
        foreach (var (header, binding, width) in new[] { ("时间", "Time", 150d), ("操作", "Action", 110d), ("视频 ID", "VideoId", 150d), ("候选", "Title", 240d), ("来源", "Source", 100d), ("可信度", "Confidence", 75d), ("原状态", "BeforeStatus", 75d), ("结果", "AfterStatus", 75d),
            ("原资料", "BeforeMetadata", 220d), ("结果资料", "AfterMetadata", 220d), ("原标签", "BeforeTags", 180d), ("结果标签", "AfterTags", 180d), ("自动素人", "AutoAmateur", 90d), ("撤销事务", "UndoId", 240d) })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(binding), Width = width });
        static ComboBox HistoryFilter(IEnumerable<(string Label, string Value)> choices, double width)
        {
            var box = new ComboBox { Width = width, Height = 32, Margin = new Thickness(0, 0, 7, 7) };
            foreach (var (label, value) in choices) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            box.SelectedIndex = 0;
            return box;
        }
        static string SelectedFilter(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        var status = HistoryFilter([("结果状态：全部", ""), ("待审核", "pending"), ("已否决", "rejected"),
            ("已通过", "approved"), ("未找到", "unmatched"), ("无审核记录", "—")], 145);
        var action = HistoryFilter(new[] { ("操作：全部", "") }.Concat(rows.Select(row => row.Action)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).Select(value => (value, value))), 135);
        var source = HistoryFilter(new[] { ("来源：全部", "") }.Concat(rows.Select(row => row.Source)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).Select(value => (value, value))), 150);
        var confidence = HistoryFilter([("可信度：全部", ""), ("低于 40%", "low"),
            ("40–79%", "medium"), ("80% 以上", "high")], 135);
        var fromDate = new DatePicker { Width = 135, Height = 32, Margin = new Thickness(0, 0, 7, 7),
            ToolTip = "开始日期（含当天）" };
        var toDate = new DatePicker { Width = 135, Height = 32, Margin = new Thickness(0, 0, 7, 7),
            ToolTip = "结束日期（含当天）" };
        var search = new TextBox { Width = 220, Height = 32, Margin = new Thickness(0, 0, 7, 7),
            ToolTip = "搜索视频 ID、候选标题、资料、标签或事务 ID" };
        var reset = new Button { Content = "清除筛选", Height = 32, Margin = new Thickness(0, 0, 0, 7),
            Padding = new Thickness(9, 0, 9, 0) };
        var filters = new WrapPanel { Margin = new Thickness(10, 10, 10, 2) };
        foreach (var control in new UIElement[] { status, action, source, confidence,
            new TextBlock { Text = "日期", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 7) }, fromDate,
            new TextBlock { Text = "至", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 7) }, toDate, search, reset }) filters.Children.Add(control);
        var count = new TextBlock { Text = $"显示 {rows.Count:N0} / {rows.Count:N0} 条记录 · 导出完整历史不受筛选影响",
            Margin = new Thickness(10, 3, 10, 9), Foreground = System.Windows.Media.Brushes.SlateGray };
        var layout = new DockPanel();
        DockPanel.SetDock(filters, Dock.Top);
        DockPanel.SetDock(count, Dock.Bottom);
        layout.Children.Add(filters);
        layout.Children.Add(count);
        layout.Children.Add(grid);
        window.Content = layout;

        bool resetting = false;
        int revision = 0;
        CancellationTokenSource? filterCancellation = null;
        var searchDelay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        async void ApplyFilters()
        {
            if (resetting) return;
            searchDelay.Stop();
            filterCancellation?.Cancel();
            filterCancellation = null;
            var ownRevision = ++revision;
            var query = new ReviewHistoryQuery(SelectedFilter(status), SelectedFilter(action),
                SelectedFilter(source), SelectedFilter(confidence), fromDate.SelectedDate, toDate.SelectedDate,
                search.Text);
            if (query.FromLocalDate > query.ToLocalDate)
            {
                grid.ItemsSource = Array.Empty<ReviewHistoryRow>();
                count.Text = "开始日期不能晚于结束日期。";
                return;
            }
            var current = new CancellationTokenSource();
            filterCancellation = current;
            count.Text = "正在筛选审核历史…";
            try
            {
                var matched = await Task.Run(() => rows.Where((row, index) =>
                {
                    if ((index & 255) == 0) current.Token.ThrowIfCancellationRequested();
                    return query.Matches(row);
                }).ToArray(), current.Token);
                if (current.IsCancellationRequested || ownRevision != revision || !window.IsVisible) return;
                grid.ItemsSource = matched;
                count.Text = $"显示 {matched.Length:N0} / {rows.Count:N0} 条记录 · 导出完整历史不受筛选影响";
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(filterCancellation, current)) filterCancellation = null;
                current.Dispose();
            }
        }
        foreach (var box in new[] { status, action, source, confidence })
            box.SelectionChanged += (_, _) => ApplyFilters();
        fromDate.SelectedDateChanged += (_, _) => ApplyFilters();
        toDate.SelectedDateChanged += (_, _) => ApplyFilters();
        searchDelay.Tick += (_, _) => ApplyFilters();
        search.TextChanged += (_, _) =>
        {
            if (resetting) return;
            filterCancellation?.Cancel();
            searchDelay.Stop();
            searchDelay.Start();
        };
        reset.Click += (_, _) =>
        {
            resetting = true;
            try
            {
                status.SelectedIndex = action.SelectedIndex = source.SelectedIndex = confidence.SelectedIndex = 0;
                fromDate.SelectedDate = toDate.SelectedDate = null;
                search.Clear();
            }
            finally { resetting = false; }
            ApplyFilters();
        };
        window.Closed += (_, _) => { searchDelay.Stop(); filterCancellation?.Cancel(); };
        window.ShowDialog();
    }

    async void ExportReviewHistory()
    {
        if (!await ReloadReviewHistoryAsync())
        { MessageBox.Show(this, "审核历史暂时无法读取。", "导出审核历史"); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出完整审核历史", Filter = "CSV 文件|*.csv|JSON 文件|*.json", FileName = "映格审核历史.csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var records = reviewHistory.ToArray();
        try
        {
            await Task.Run(() => ReviewHistoryExport.Write(dialog.FileName, records,
                reviewHistoryInvalidLines, string.Equals(Path.GetExtension(dialog.FileName), ".json", StringComparison.OrdinalIgnoreCase)));
            MessageBox.Show(this, $"已导出 {records.Length} 笔审核事务。", "导出审核历史");
        }
        catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message, "导出审核历史", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void ExportReviews()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出审核结果", Filter = "CSV 文件|*.csv", FileName = "映格审核结果.csv" };
        if (dialog.ShowDialog(this) != true) return;
        var lines = new List<string> { "视频ID,候选标题,来源,可信度,状态,决策来源,依据,创建时间,审核时间" };
        lines.AddRange(config.AiReviews.Values.OrderByDescending(item => item.CreatedAt).Select(item => string.Join(",", new object?[] { item.VideoId, item.CandidateTitle, item.CandidateSource, item.Confidence, item.Status, item.DecisionSource, item.Reason, item.CreatedAt.ToString("O"), item.ReviewedAt?.ToString("O") }.Select(ReviewCsv.Cell))));
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
        MessageBox.Show(this, $"已导出 {lines.Count - 1} 条审核结果。", "审核结果");
    }
}
