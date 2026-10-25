using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Binding = System.Windows.Data.Binding;
using TextBox = System.Windows.Controls.TextBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    Button? aiSuggestionReviewButton;
    TextBlock? aiSuggestionReviewSummary;

    sealed class AiSuggestionRow
    {
        public required AiSuggestionRecord Record { get; init; }
        public string State => Record.Status switch
        {
            "pending" => "待人工", "ai-rejected" => "AI 否决 · 待人工", "approved" => "已通过",
            "rejected" => "人工否决", "undone" => "已撤销", _ => Record.Status
        };
        public string Kind => Record.Kind == "category" ? "分类" : "标签";
        public string Title => Record.InputTitle;
        public string Suggestion => Record.Kind == "category" ? Record.SuggestedCategory : string.Join("、", Record.SuggestedTags);
        public string Confidence => $"{Record.Confidence}%";
        public string Evidence => Record.Reason;
        public string Model => Record.Model;
        public string Time => Record.RequestedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string Cost => $"约 ${Record.EstimatedUsd:F5}";
    }

    sealed class AiActionRow
    {
        public required AiSuggestionAction Record { get; init; }
        public string TimeUtc => Record.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string Action => Record.Action switch { "approve" => "人工通过", "reject" => "人工否决", _ => "撤销" };
        public int Count => Record.Changes.Count;
        public string Id => Record.Id;
        public string? UndoesActionId => Record.UndoesActionId;
    }

    static void ShowAiAuditText(Window owner, string title, string content)
    {
        var detail = new Window { Title = title, Owner = owner, Width = 650, Height = 500,
            MinWidth = 450, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        detail.Content = new TextBox { Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(18), Padding = new Thickness(14), FontSize = 13 };
        detail.ShowDialog();
    }

    bool ConfirmAiApprovalPreview(Window owner, IReadOnlyList<AiSuggestionRow> selected)
    {
        var dialog = new Window { Title = "人工审批预览", Owner = owner, Width = 760, Height = 580,
            MinWidth = 550, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White };
        var layout = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock { Text = $"准备通过 {selected.Count} 条 AI 建议",
            FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var note = new TextBlock { Text = "下方预览最多 20 条。确认时还会核对磁盘记录与请求时的字段版本；已变化的项目不会被覆盖。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(note, Dock.Top); layout.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "返回审核", Style = (Style)FindResource("Action") };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var approve = new Button { Content = "确认通过", Style = (Style)FindResource("Primary") };
        approve.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(approve);
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var lines = selected.Take(20).Select((row, index) =>
        {
            var item = row.Record;
            var title = item.InputTitle.Length > 48 ? item.InputTitle[..48] + "…" : item.InputTitle;
            var before = item.Kind == "category" ? AiSuggestionAudit.CategoriesOf(config, item.VideoId) :
                AiSuggestionAudit.TagsOf(config, item.VideoId);
            List<string> after = item.Kind == "category" ? [item.SuggestedCategory] :
                before.Concat(item.SuggestedTags).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(50).ToList();
            return $"{index + 1}. {title}  [{row.Kind} · {row.Confidence}]\n" +
                $"    当前：{(before.Count == 0 ? "（空）" : string.Join("、", before))}\n" +
                $"    通过后：{(after.Count == 0 ? "（空）" : string.Join("、", after))}\n" +
                $"    依据：{item.Reason}\n";
        });
        var preview = string.Join("\n", lines) +
            (selected.Count > 20 ? $"\n其余 {selected.Count - 20} 条未在此显示，仍会逐项校验后作为同一批审批。" : "");
        layout.Children.Add(new TextBox { Text = preview, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), FontSize = 13 });
        dialog.Content = layout;
        return dialog.ShowDialog() == true;
    }

    void InitializeAiSuggestionReviewUi()
    {
        if (ScrapePanel.Child is not StackPanel panel) return;
        var section = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(246, 247, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(216, 221, 244)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16), Margin = new Thickness(0, 18, 0, 0)
        };
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "AI 分类与标签建议", FontSize = 17,
            FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(56, 66, 128)) });
        body.Children.Add(new TextBlock { Text = "建议先保存到待审记录。人工通过后才会改变分类或标签；可批量否决、查看原始依据与撤销。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
        aiSuggestionReviewSummary = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(88, 104, 147)),
            Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(aiSuggestionReviewSummary);
        aiSuggestionReviewButton = new Button { Content = "打开 AI 建议审核", Style = (Style)FindResource("Primary"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        aiSuggestionReviewButton.Click += (_, _) => ShowAiSuggestionReview();
        body.Children.Add(aiSuggestionReviewButton);
        section.Child = body;
        panel.Children.Add(section);
        RefreshAiSuggestionReviewSummary();
    }

    void RefreshAiSuggestionReviewSummary()
    {
        if (aiSuggestionReviewSummary is null) return;
        var records = config.AiSuggestions.Values;
        aiSuggestionReviewSummary.Text = $"待审核 {records.Count(AiSuggestionAudit.IsActionable)} 条 · 历史建议 {records.Count} 条 · 审批操作 {config.AiSuggestionActions.Count} 次";
    }

    static DataGridTextColumn AiColumn(string title, string property, double width) => new()
    {
        Header = title, Binding = new Binding(property), Width = width, IsReadOnly = true
    };

    void ShowAiSuggestionReview()
    {
        try { ReloadAiSuggestionAuditFromDisk(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "AI 审核记录", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var dialog = new Window
        {
            Title = "映格 · AI 建议审核", Owner = this, Width = 1120, Height = 670,
            MinWidth = 850, MinHeight = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253)), FontFamily = FontFamily,
            FontSize = 12
        };
        var layout = new DockPanel { Margin = new Thickness(20) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = "AI 建议审核", FontSize = 24, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(39, 52, 87)) });
        heading.Children.Add(new TextBlock { Text = "可单选或多选。低可信度与 AI 否决均不会自动写入；审批前再次核对最新分类、标签和建议状态。",
            Foreground = Brushes.SlateGray, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var footer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 12, 0, 0) };
        var summary = new TextBlock { Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 8) };
        footer.Children.Add(summary);
        var buttons = new WrapPanel();
        Button MakeButton(string label, RoutedEventHandler onClick, bool primary = false)
        {
            var button = new Button { Content = label, Style = (Style)FindResource(primary ? "Primary" : "Action") };
            button.Click += onClick; buttons.Children.Add(button); return button;
        }
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow, CanUserAddRows = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeight = 52, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(223, 230, 243))
        };
        grid.Columns.Add(AiColumn("状态", nameof(AiSuggestionRow.State), 110));
        grid.Columns.Add(AiColumn("类型", nameof(AiSuggestionRow.Kind), 52));
        grid.Columns.Add(AiColumn("视频", nameof(AiSuggestionRow.Title), 170));
        grid.Columns.Add(AiColumn("AI 建议", nameof(AiSuggestionRow.Suggestion), 150));
        grid.Columns.Add(AiColumn("可信度", nameof(AiSuggestionRow.Confidence), 70));
        grid.Columns.Add(AiColumn("依据", nameof(AiSuggestionRow.Evidence), 180));
        grid.Columns.Add(AiColumn("模型", nameof(AiSuggestionRow.Model), 115));
        grid.Columns.Add(AiColumn("请求时间", nameof(AiSuggestionRow.Time), 115));
        grid.Columns.Add(AiColumn("费用估计", nameof(AiSuggestionRow.Cost), 95));

        void Refresh()
        {
            try { ReloadAiSuggestionAuditFromDisk(); }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "审核记录读取失败", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var records = config.AiSuggestions.Values.OrderBy(record => AiSuggestionAudit.IsActionable(record) ? 0 : 1)
                .ThenBy(record => record.Confidence).ThenByDescending(record => record.RequestedAtUtc)
                .Select(record => new AiSuggestionRow { Record = record }).ToList();
            grid.ItemsSource = records;
            summary.Text = $"{records.Count} 条建议 · 待人工 {records.Count(row => AiSuggestionAudit.IsActionable(row.Record))} 条 · 完整审批历史 {config.AiSuggestionActions.Count} 次";
            RefreshAiSuggestionReviewSummary();
        }

        List<string> SelectedIds() => grid.SelectedItems.Cast<AiSuggestionRow>()
            .Where(row => AiSuggestionAudit.IsActionable(row.Record)).Select(row => row.Record.Id).ToList();

        void ShowDetails()
        {
            if (grid.SelectedItem is not AiSuggestionRow row) return;
            var item = row.Record;
            ShowAiAuditText(dialog, "AI 建议详情", $"建议编号：{item.Id}\n视频编号：{item.VideoId}\n状态：{row.State}\n" +
                $"类型：{row.Kind}\n标题：{item.InputTitle}\n请求时上下文：{item.InputContext}\n目标字段版本：{item.TargetFingerprint}\n" +
                $"模型建议：{row.Suggestion}\n可信度：{row.Confidence}\n模型判断：{item.Verdict}\n依据：{item.Reason}\n" +
                $"模型：{item.Model}\n请求时间：{row.Time}\n输入 Token 估计：{item.InputTokenEstimate}\n" +
                $"输出 Token 上限分摊：{item.OutputTokenLimitShare}\n费用上限估计：{row.Cost}");
        }
        grid.MouseDoubleClick += (_, _) => ShowDetails();

        MakeButton("通过选中", (_, _) =>
        {
            var selected = grid.SelectedItems.Cast<AiSuggestionRow>()
                .Where(row => AiSuggestionAudit.IsActionable(row.Record)).ToList();
            if (selected.Count == 0 || !ConfirmAiApprovalPreview(dialog, selected)) return;
            var ids = selected.Select(row => row.Record.Id).ToList();
            try { DecideAiSuggestions(ids, true); Refresh(); }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "审批未完成", MessageBoxButton.OK, MessageBoxImage.Warning); Refresh(); }
        }, primary: true);
        MakeButton("否决选中", (_, _) =>
        {
            var ids = SelectedIds(); if (ids.Count == 0) return;
            try { DecideAiSuggestions(ids, false); Refresh(); }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "否决未完成", MessageBoxButton.OK, MessageBoxImage.Warning); Refresh(); }
        });
        MakeButton("撤销上次审批", (_, _) =>
        {
            try { UndoLastAiSuggestionDecision(); Refresh(); }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "撤销未完成", MessageBoxButton.OK, MessageBoxImage.Warning); Refresh(); }
        });
        MakeButton("完整历史", (_, _) => ShowAiSuggestionHistory(dialog));
        MakeButton("查看详情", (_, _) => ShowDetails());
        MakeButton("刷新", (_, _) => Refresh());
        MakeButton("关闭", (_, _) => dialog.Close());
        footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        layout.Children.Add(grid); dialog.Content = layout;
        Refresh(); dialog.ShowDialog();
    }

    void ShowAiSuggestionHistory(Window owner)
    {
        var history = new Window { Title = "AI 建议完整历史", Owner = owner, Width = 900, Height = 570,
            MinWidth = 650, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, EnableRowVirtualization = true,
            SelectionMode = DataGridSelectionMode.Single, Background = Brushes.White };
        grid.Columns.Add(AiColumn("时间", "TimeUtc", 180));
        grid.Columns.Add(AiColumn("操作", "Action", 90));
        grid.Columns.Add(AiColumn("数量", "Count", 65));
        grid.Columns.Add(AiColumn("事务 ID", "Id", 250));
        grid.Columns.Add(AiColumn("撤销事务", "UndoesActionId", 250));
        grid.ItemsSource = config.AiSuggestionActions.Values.OrderByDescending(action => action.TimeUtc)
            .Select(action => new AiActionRow { Record = action }).ToList();
        grid.MouseDoubleClick += (_, _) =>
        {
            if (grid.SelectedItem is not AiActionRow row) return;
            var lines = row.Record.Changes.Select(change =>
                $"建议 {change.SuggestionId} · 视频 {change.VideoId}\n" +
                $"状态：{change.BeforeStatus} → {change.AfterStatus}\n" +
                (change.BeforeCategories is null ? "" :
                    $"分类：{string.Join("、", change.BeforeCategories)} → {string.Join("、", change.AfterCategories ?? [])}\n") +
                (change.BeforeTags is null ? "" :
                    $"标签：{string.Join("、", change.BeforeTags)} → {string.Join("、", change.AfterTags ?? [])}\n"));
            ShowAiAuditText(history, "AI 审核事务详情", $"事务：{row.Id}\n操作：{row.Action}\n时间：{row.TimeUtc}\n" +
                $"撤销事务：{row.UndoesActionId ?? "无"}\n\n{string.Join("\n", lines)}");
        };
        history.Content = grid; history.ShowDialog();
    }

    void ReloadAiSuggestionAuditFromDisk()
    {
        if (!TryReadJson(ConfigPath, out LibraryConfig? latest) || latest is null)
            throw new InvalidDataException("共享配置无法读取，已停止 AI 审核以保护现有资料。");
        if (!AiSuggestionAudit.IsHistoryWellFormed(latest))
            throw new InvalidDataException("AI 建议审核记录损坏，已停止修改。请先恢复配置备份。");
        config.AiSuggestions = latest.AiSuggestions.ToDictionary(pair => pair.Key,
            pair => AiSuggestionAudit.Clone(pair.Value), StringComparer.Ordinal);
        config.AiSuggestionActions = latest.AiSuggestionActions.ToDictionary(pair => pair.Key,
            pair => AiSuggestionAudit.Clone(pair.Value), StringComparer.Ordinal);
        configBaseline.AiSuggestions = latest.AiSuggestions.ToDictionary(pair => pair.Key,
            pair => AiSuggestionAudit.Clone(pair.Value), StringComparer.Ordinal);
        configBaseline.AiSuggestionActions = latest.AiSuggestionActions.ToDictionary(pair => pair.Key,
            pair => AiSuggestionAudit.Clone(pair.Value), StringComparer.Ordinal);
    }

    void SaveAiSuggestions(IEnumerable<AiSuggestionRecord> suggestions)
    {
        var saved = suggestions.ToList();
        if (saved.Count == 0) return;
        var before = CloneLibraryConfig(config);
        var baselineBefore = CloneLibraryConfig(configBaseline);
        var locked = false;
        var committed = false;
        try
        {
            try { ConfigWriteMutex.WaitOne(); locked = true; }
            catch (AbandonedMutexException) { locked = true; }
            ReloadAiSuggestionAuditFromDisk();
            foreach (var suggestion in saved)
            {
                if (!AiSuggestionAudit.IsValid(suggestion) || config.AiSuggestions.ContainsKey(suggestion.Id))
                    throw new InvalidDataException("AI 建议不完整或编号重复，未保存任何候选。");
                config.AiSuggestions.Add(suggestion.Id, suggestion);
            }
            SaveConfig();
            committed = true;
        }
        catch { if (!committed) { config = before; configBaseline = baselineBefore; } throw; }
        finally { if (locked) ConfigWriteMutex.ReleaseMutex(); }
        RefreshAiSuggestionReviewSummary();
    }

    void DecideAiSuggestions(IReadOnlyList<string> ids, bool approve)
    {
        if (ids.Count == 0) return;
        var before = CloneLibraryConfig(config);
        var baselineBefore = CloneLibraryConfig(configBaseline);
        var locked = false;
        var committed = false;
        AiSuggestionAction? action = null;
        try
        {
            try { ConfigWriteMutex.WaitOne(); locked = true; }
            catch (AbandonedMutexException) { locked = true; }
            if (!TryReadJson(ConfigPath, out LibraryConfig? latest) || latest is null ||
                !AiSuggestionAudit.IsHistoryWellFormed(latest) ||
                !AiSuggestionAudit.TargetsMatch(latest, config, ids))
                throw new InvalidOperationException("审核项或视频分类/标签已被另一份程序修改，请刷新后重试。");
            action = AiSuggestionAudit.Decide(config, ids, approve);
            SaveConfig();
            committed = true;
        }
        catch { if (!committed) { config = before; configBaseline = baselineBefore; } throw; }
        finally { if (locked) ConfigWriteMutex.ReleaseMutex(); }
        SyncAiSuggestionVideoFields(action!.Changes.Select(change => change.VideoId));
        if (view == "categories") RenderCategories();
        RenderCards();
        RefreshAiSuggestionReviewSummary();
        Log($"AI 建议已人工{(approve ? "通过" : "否决")} {ids.Count} 条");
    }

    void UndoLastAiSuggestionDecision()
    {
        var before = CloneLibraryConfig(config);
        var baselineBefore = CloneLibraryConfig(configBaseline);
        var locked = false;
        var committed = false;
        AiSuggestionAction? undo = null;
        string? actionId = null;
        try
        {
            try { ConfigWriteMutex.WaitOne(); locked = true; }
            catch (AbandonedMutexException) { locked = true; }
            if (!TryReadJson(ConfigPath, out LibraryConfig? latest) || latest is null ||
                !AiSuggestionAudit.IsHistoryWellFormed(latest))
                throw new InvalidOperationException("配置无法读取，撤销已停止。");
            var action = AiSuggestionAudit.LastUndoable(config)
                ?? throw new InvalidOperationException("没有可撤销的 AI 审核操作。");
            actionId = action.Id;
            var diskAction = AiSuggestionAudit.LastUndoable(latest);
            if (diskAction?.Id != action.Id ||
                !AiSuggestionAudit.TargetsMatch(latest, config, action.Changes.Select(change => change.SuggestionId)))
                throw new InvalidOperationException("AI 审核历史或视频资料已变化，撤销已停止。请刷新后重试。");
            undo = AiSuggestionAudit.UndoLast(config);
            SaveConfig();
            committed = true;
        }
        catch { if (!committed) { config = before; configBaseline = baselineBefore; } throw; }
        finally { if (locked) ConfigWriteMutex.ReleaseMutex(); }
        SyncAiSuggestionVideoFields(undo!.Changes.Select(change => change.VideoId));
        if (view == "categories") RenderCategories();
        RenderCards();
        RefreshAiSuggestionReviewSummary();
        Log($"已撤销 AI 审核事务：{actionId}");
    }

    void SyncAiSuggestionVideoFields(IEnumerable<string> ids)
    {
        var touched = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos.Where(video => touched.Contains(video.Id)))
        {
            var categories = AiSuggestionAudit.CategoriesOf(config, video.Id);
            video.CategoryList.Clear(); video.CategoryList.AddRange(categories);
            video.Category = categories.FirstOrDefault() ?? "未分类";
            video.Tags.Clear(); video.Tags.AddRange(AiSuggestionAudit.TagsOf(config, video.Id));
            video.RefreshTags();
        }
    }

    static string SafeAiAuditText(string? source, int limit)
    {
        var value = source ?? "";
        value = System.Text.RegularExpressions.Regex.Replace(value,
            @"(?i)(?:\b[A-Z]:[\\/]|\\\\)[^\s，。；;]+", "[本地路径]");
        value = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return value[..Math.Min(limit, value.Length)];
    }

    internal static AiSuggestionRecord ReadAiSuggestion(JsonElement value, string kind, VideoItem video,
        string inputTitle, string inputContext, string fingerprint, string model,
        double cost, long inputTokens, long outputTokens)
    {
        static string Field(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object &&
            node.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
        var confidence = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("confidence", out var confidenceValue) &&
            confidenceValue.ValueKind == JsonValueKind.Number && confidenceValue.TryGetInt32(out var parsed)
            ? Math.Clamp(parsed, 0, 100) : 0;
        var verdict = Field(value, "verdict").Trim().ToLowerInvariant();
        if (verdict is not "reject" and not "suggest") verdict = "suggest";
        var reason = Field(value, "reason").Trim();
        if (reason.Length == 0) reason = confidence == 0 ? "模型未给出可信度或依据，必须人工判断" : "模型未给出依据，必须人工判断";
        var category = kind == "category"
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : Field(value, "category")
            : "";
        var tags = new List<string>();
        if (kind == "tags")
        {
            var tagNode = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("tags", out var nested) ? nested : value;
            if (tagNode.ValueKind == JsonValueKind.Array)
                tags = tagNode.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? "").ToList();
            else if (tagNode.ValueKind == JsonValueKind.String)
                tags = tagNode.GetString()!.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        tags = tags.Select(tag => tag.Trim().TrimStart('#').Replace("\r", "").Replace("\n", ""))
            .Where(tag => tag.Length is > 0 and <= 24 && !tag.Any(char.IsControl) &&
                !tag.Contains(":\\", StringComparison.Ordinal) && !tag.Contains(":/", StringComparison.Ordinal) &&
                !tag.StartsWith("\\\\", StringComparison.Ordinal))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(5).ToList();
        return new AiSuggestionRecord
        {
            VideoId = video.Id, Kind = kind,
            InputTitle = SafeAiAuditText(inputTitle, 180),
            InputContext = SafeAiAuditText(inputContext, 160),
            TargetFingerprint = fingerprint,
            SuggestedCategory = SafeAiAuditText(category, 100), SuggestedTags = tags,
            Confidence = confidence, Verdict = verdict,
            Reason = SafeAiAuditText(reason, 240),
            Model = SafeAiAuditText(model, 120),
            RequestedAtUtc = DateTimeOffset.UtcNow,
            InputTokenEstimate = inputTokens, OutputTokenLimitShare = outputTokens,
            EstimatedUsd = Math.Max(0, cost),
            Status = verdict == "reject" ? "ai-rejected" : "pending"
        };
    }
}
