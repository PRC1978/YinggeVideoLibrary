using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using StackPanel = System.Windows.Controls.StackPanel;
using WrapPanel = System.Windows.Controls.WrapPanel;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public sealed class ReviewHistoryEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Action { get; set; } = "";
    public Dictionary<string, AiReviewRecord> Before { get; set; } = [];
    public Dictionary<string, AiReviewRecord> After { get; set; } = [];
    public Dictionary<string, MovieMetadata?> MetadataBefore { get; set; } = [];
    public Dictionary<string, List<string>> TagsBefore { get; set; } = [];
    public Dictionary<string, bool> AutoAmateurBefore { get; set; } = [];
    public bool Undone { get; set; }
}

public partial class MainWindow
{
    ComboBox? reviewStatusFilter;
    ComboBox? reviewSourceFilter;
    ComboBox? reviewConfidenceFilter;
    Button? reviewUndoButton;
    Button? reviewPreviousPageButton;
    Button? reviewNextPageButton;
    TextBlock? reviewPageLabel;
    const int ReviewPageSize = 12;
    int reviewPageIndex;
    bool updatingReviewSources;
    readonly List<ReviewHistoryEntry> reviewHistory = [];
    static string ReviewHistoryPath => Path.Combine(DataDir, "review-history.jsonl");

    void InitializeReviewWorkflowUi()
    {
        if (ScrapePanel.Child is not StackPanel panel) return;
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        reviewStatusFilter = MakeReviewFilter(["状态：全部", "待审核", "已否决", "已通过", "未找到"]);
        reviewSourceFilter = MakeReviewFilter(["来源：全部"]);
        reviewConfidenceFilter = MakeReviewFilter(["可信度：全部", "低于 40%", "40–79%", "80% 以上"]);
        foreach (var filter in new[] { reviewStatusFilter, reviewSourceFilter, reviewConfidenceFilter })
        {
            filter.SelectionChanged += (_, _) => { if (!updatingReviewSources) { reviewPageIndex = 0; RefreshReviewQueue(); } };
            bar.Children.Add(filter);
        }
        panel.Children.Insert(Math.Max(0, panel.Children.Count - 2), bar);
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
        try
        {
            if (File.Exists(ReviewHistoryPath))
                foreach (var line in File.ReadLines(ReviewHistoryPath))
                    try { if (JsonSerializer.Deserialize<ReviewHistoryEntry>(line) is { } entry) reviewHistory.Add(entry); } catch { }
        }
        catch (Exception ex) { Log("审核历史读取失败：" + ex.Message); }
        UpdateReviewSourceFilter();
        UpdateReviewUndoButton();
    }

    static ComboBox MakeReviewFilter(IEnumerable<string> options)
    {
        var box = new ComboBox { Width = 135, Height = 32, Margin = new Thickness(0, 0, 7, 0) };
        foreach (var option in options) box.Items.Add(option);
        box.SelectedIndex = 0;
        return box;
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
        var sources = config.AiReviews.Values.Select(item => item.CandidateSource).Where(source => !string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(source => source).ToList();
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
        var status = reviewStatusFilter?.SelectedIndex ?? 0;
        if (status == 1 && review.Status != "pending" || status == 2 && review.Status != "rejected" || status == 3 && review.Status != "approved" || status == 4 && review.Status != "unmatched") return false;
        var source = reviewSourceFilter?.SelectedItem as string;
        if (!string.IsNullOrEmpty(source) && source != "来源：全部" && !string.Equals(source, review.CandidateSource, StringComparison.CurrentCultureIgnoreCase)) return false;
        return (reviewConfidenceFilter?.SelectedIndex ?? 0) switch
        {
            1 => review.Confidence < 40,
            2 => review.Confidence >= 40 && review.Confidence < 80,
            3 => review.Confidence >= 80,
            _ => true
        };
    }

    void PreviewReviewBatch()
    {
        var selected = config.AiReviews.Values.Where(review => reviewSelection.Contains(review.VideoId)).OrderBy(review => review.Confidence).ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "先选择要审核的候选。", "审批预览"); return; }
        var details = string.Join("\n", selected.Take(25).Select(review => $"{review.Confidence,3}%  {review.Status,-8}  {review.CandidateSource}  {review.CandidateTitle}  ←  {videos.FirstOrDefault(v => v.Id == review.VideoId)?.DisplayTitle}"));
        MessageBox.Show(this, $"共 {selected.Count} 条；低可信度 {selected.Count(item => item.Confidence < 40)} 条；已否决 {selected.Count(item => item.Status == "rejected")} 条。\n\n{details}" + (selected.Count > 25 ? $"\n…另有 {selected.Count - 25} 条" : ""), "批量审批预览");
    }

    void RecordReviewAction(string action, IReadOnlyDictionary<string, AiReviewRecord> before, IReadOnlyDictionary<string, MovieMetadata?> metadataBefore,
        IReadOnlyDictionary<string, List<string>>? tagsBefore = null, IReadOnlyDictionary<string, bool>? autoAmateurBefore = null)
    {
        var entry = new ReviewHistoryEntry
        {
            Action = action,
            Before = before.ToDictionary(pair => pair.Key, pair => CloneReview(pair.Value), StringComparer.OrdinalIgnoreCase),
            After = before.Keys.Concat(metadataBefore.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(config.AiReviews.ContainsKey).ToDictionary(id => id, id => CloneReview(config.AiReviews[id]), StringComparer.OrdinalIgnoreCase),
            MetadataBefore = metadataBefore.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            TagsBefore = tagsBefore?.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase) ?? [],
            AutoAmateurBefore = autoAmateurBefore?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) ?? []
        };
        try { AppendReviewHistory(entry); }
        catch (Exception ex) { Log("审核已保存，但历史记录写入失败：" + ex.Message); }
    }

    void AppendReviewHistory(ReviewHistoryEntry entry)
    {
        reviewHistory.Add(entry);
        File.AppendAllText(ReviewHistoryPath, JsonSerializer.Serialize(entry) + Environment.NewLine, Encoding.UTF8);
        UpdateReviewUndoButton();
    }

    void UpdateReviewUndoButton()
    {
        if (reviewUndoButton is not null) reviewUndoButton.IsEnabled = reviewHistory.LastOrDefault()?.Action is "approve" or "reject";
    }

    void UndoLastReview()
    {
        var entry = reviewHistory.LastOrDefault();
        if (entry is null || entry.Action is not ("approve" or "reject")) return;
        foreach (var (id, after) in entry.After)
            if (!config.AiReviews.TryGetValue(id, out var current) || !JsonEquals(current, after))
            { MessageBox.Show(this, "这些审核项在别处被修改，无法安全撤销。", "撤销审核"); return; }
        try
        {
            foreach (var (id, before) in entry.Before)
            {
                var restored = CloneReview(before);
                // A manual undo must win over a concurrent automatic evaluator.
                restored.DecisionSource = "manual";
                config.AiReviews[id] = restored;
            }
            foreach (var (id, old) in entry.MetadataBefore)
            {
                if (old is null) metadata.Remove(id); else metadata[id] = old;
            }
            foreach (var (id, old) in entry.TagsBefore)
            {
                if (old.Count == 0) config.Tags.Remove(id); else config.Tags[id] = old.ToList();
            }
            foreach (var (id, old) in entry.AutoAmateurBefore)
            {
                config.AutoAmateurTags.RemoveAll(item => item.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (old) config.AutoAmateurTags.Add(id);
            }
            SaveConfig(); MetadataStore.Save(DataDir, metadata);
            foreach (var video in videos.Where(video => entry.Before.ContainsKey(video.Id))) { video.DisplayTitle = video.Title; video.Code = null; }
            ApplySavedFields(videos.Where(video => entry.Before.ContainsKey(video.Id)).ToList());
            AppendReviewHistory(new ReviewHistoryEntry { Action = "undo", Before = entry.After, After = entry.Before, MetadataBefore = entry.MetadataBefore, Undone = true });
            RefreshReviewQueue(); RenderCards();
        }
        catch (Exception ex) { MessageBox.Show(this, "撤销失败：" + ex.Message, "审核历史", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    static bool JsonEquals<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    void ShowReviewHistory()
    {
        var window = new Window { Title = "完整审核历史", Owner = this, Width = 850, Height = 530, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var rows = reviewHistory.AsEnumerable().Reverse().SelectMany(entry =>
            entry.Before.Keys.Concat(entry.After.Keys).Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty("")
                .Select(id =>
                {
                    entry.Before.TryGetValue(id, out var before);
                    entry.After.TryGetValue(id, out var after);
                    return new { Time = entry.Time.ToString("yyyy-MM-dd HH:mm:ss"), entry.Action, Title = after?.CandidateTitle ?? before?.CandidateTitle ?? "",
                        Source = after?.CandidateSource ?? before?.CandidateSource ?? "", Confidence = after?.Confidence,
                        BeforeStatus = before?.Status ?? "—", AfterStatus = after?.Status ?? "—" };
                })).ToList();
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, ItemsSource = rows };
        foreach (var (header, binding, width) in new[] { ("时间", "Time", 150d), ("操作", "Action", 110d), ("候选", "Title", 240d), ("来源", "Source", 100d), ("可信度", "Confidence", 75d), ("原状态", "BeforeStatus", 75d), ("结果", "AfterStatus", 75d) })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(binding), Width = width });
        window.Content = grid;
        window.ShowDialog();
    }

    void ExportReviewHistory()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出完整审核历史", Filter = "CSV 文件|*.csv", FileName = "映格审核历史.csv" };
        if (dialog.ShowDialog(this) != true) return;
        static string Csv(object? value) => "\"" + (value?.ToString() ?? "").Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "时间,操作,视频ID,是否撤销动作,操作前状态,操作后状态,操作前可信度,操作后可信度,操作前来源,操作后来源,操作前候选,操作后候选,操作前审核记录JSON,操作后审核记录JSON,操作前视频信息JSON,操作前标签JSON,操作前自动素人标记" };
        foreach (var entry in reviewHistory)
        {
            var ids = entry.Before.Keys.Concat(entry.After.Keys).Concat(entry.MetadataBefore.Keys)
                .Concat(entry.TagsBefore.Keys).Concat(entry.AutoAmateurBefore.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty("");
            foreach (var id in ids)
            {
                entry.Before.TryGetValue(id, out var before);
                entry.After.TryGetValue(id, out var after);
                entry.MetadataBefore.TryGetValue(id, out var metadataBefore);
                entry.TagsBefore.TryGetValue(id, out var tagsBefore);
                entry.AutoAmateurBefore.TryGetValue(id, out var autoAmateurBefore);
                lines.Add(string.Join(",", new object?[] {
                    entry.Time.ToString("O"), entry.Action, id, entry.Undone,
                    before?.Status, after?.Status, before?.Confidence, after?.Confidence,
                    before?.CandidateSource, after?.CandidateSource, before?.CandidateTitle, after?.CandidateTitle,
                    before is null ? "" : JsonSerializer.Serialize(before), after is null ? "" : JsonSerializer.Serialize(after),
                    metadataBefore is null ? "" : JsonSerializer.Serialize(metadataBefore),
                    tagsBefore is null ? "" : JsonSerializer.Serialize(tagsBefore),
                    entry.AutoAmateurBefore.ContainsKey(id) ? autoAmateurBefore : ""
                }.Select(Csv)));
            }
        }
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
    }

    void ExportReviews()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出审核结果", Filter = "CSV 文件|*.csv", FileName = "映格审核结果.csv" };
        if (dialog.ShowDialog(this) != true) return;
        static string Csv(object? value) => "\"" + (value?.ToString() ?? "").Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "视频ID,候选标题,来源,可信度,状态,决策来源,依据,创建时间,审核时间" };
        lines.AddRange(config.AiReviews.Values.OrderByDescending(item => item.CreatedAt).Select(item => string.Join(",", new object?[] { item.VideoId, item.CandidateTitle, item.CandidateSource, item.Confidence, item.Status, item.DecisionSource, item.Reason, item.CreatedAt.ToString("O"), item.ReviewedAt?.ToString("O") }.Select(Csv))));
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
        MessageBox.Show(this, $"已导出 {lines.Count - 1} 条审核结果。", "审核结果");
    }
}
