using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ListBox = System.Windows.Controls.ListBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    List<ReviewApprovalRow> BuildReviewApprovalRows(IReadOnlyList<string> ids)
    {
        var distinct = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var snapshots = CaptureReviewStates(distinct);
        var targets = distinct.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videosById = BuildReviewVideoMap(videos, targets);
        var rows = new List<ReviewApprovalRow>(distinct.Count);
        foreach (var id in distinct)
        {
            if (!videosById.TryGetValue(id, out var video) || !snapshots.TryGetValue(id, out var snapshot) ||
                snapshot.Review?.Status is not ("pending" or "rejected"))
                throw new InvalidDataException($"视频 {id} 的审核项已变化或不在当前视频库中，请刷新队列。");
            rows.Add(new ReviewApprovalRow
            {
                VideoId = id,
                VideoTitle = video.DisplayTitle,
                Before = snapshot,
                Candidate = snapshot.Review.ToMetadata()
            });
        }
        return rows;
    }

    // Called from both the single-row and batch approve/reject actions. A
    // prepared transaction is frozen after the preview and checked again
    // against memory and disk before any write.
    bool TryCommitReviewDecisionsWithPreview(IReadOnlyList<string> ids, bool approved, out string error)
    {
        error = "";
        try
        {
            PreparedReviewApproval prepared;
            if (approved)
            {
                var saved = reviewPreparedPreview;
                reviewPreparedPreview = null;
                if (saved is not null && saved.Before.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(ids) && DateTime.Now - saved.PreparedAt <= TimeSpan.FromMinutes(10))
                    prepared = saved;
                else
                {
                    if (saved is not null && saved.Before.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(ids))
                    {
                        error = "逐字段预览已超过 10 分钟，请重新预览后提交。";
                        return false;
                    }
                    var rows = BuildReviewApprovalRows(ids);
                    if (rows.Count == 0) { error = "没有可审核的候选。"; return false; }
                    if (!ShowReviewFieldChoices(rows, out var accepted) || accepted is null) return false;
                    prepared = accepted;
                }
            }
            else
            {
                var rows = BuildReviewApprovalRows(ids);
                if (rows.Count == 0) { error = "没有可审核的候选。"; return false; }
                prepared = ReviewApprovalPlanner.Prepare(rows, false);
            }
            if (MessageBox.Show(this, prepared.Describe(), approved ? "逐字段审批预览" : "否决预览",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            return CommitPreparedReviewApproval(prepared, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    bool CommitPreparedReviewApproval(PreparedReviewApproval prepared, out string error)
    {
        error = "";
        var locked = false;
        try
        {
            try { ConfigWriteMutex.WaitOne(); locked = true; }
            catch (AbandonedMutexException) { locked = true; }
            var ids = prepared.Before.Keys.ToArray();
            var live = CaptureReviewStates(ids);
            if (!ReviewApprovalPlanner.Matches(prepared, live) || !PersistedReviewStatesMatch(prepared.Before))
                throw new InvalidOperationException("预览已过期：候选、已保存资料、标签或审核状态在预览期间发生变化。请刷新队列后重新预览。");
            var transaction = ReviewTransactionStore.Create(
                prepared.Approved ? "field-approve" : "reject", prepared.Before, prepared.After);
            return TryCommitJournaledReview(transaction, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally { if (locked) ConfigWriteMutex.ReleaseMutex(); }
    }

    bool ShowReviewFieldChoices(IReadOnlyList<ReviewApprovalRow> rows, out PreparedReviewApproval? prepared)
    {
        prepared = null;
        var result = (PreparedReviewApproval?)null;
        var dialog = new Window
        {
            Owner = this, Title = rows.Count == 1 ? "逐字段审核候选" : $"逐字段批量审核 · {rows.Count} 条",
            Width = 1050, Height = 720, MinWidth = 780, MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new TextBlock
        {
            Text = $"已选 {rows.Count} 条 · 逐字段对照原值和候选值。原值已有内容时默认保留；来源冲突默认保留，必须由你明确选择。‘不采用’会清空该字段。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(header, 0); layout.Children.Add(header);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var list = new ListBox { ItemsSource = rows, DisplayMemberPath = nameof(ReviewApprovalRow.Display) };
        VirtualizingStackPanel.SetIsVirtualizing(list, true);
        VirtualizingStackPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        Grid.SetColumn(list, 0); body.Children.Add(list);
        var fieldsPanel = new StackPanel();
        var fieldScroll = new ScrollViewer { Content = fieldsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(fieldScroll, 2); body.Children.Add(fieldScroll);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ReviewApprovalRow row)
            {
                PopulateReviewFieldChoices(fieldsPanel, row);
                fieldScroll.ScrollToTop();
            }
        };
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", MinWidth = 80, Height = 35, Margin = new Thickness(6, 0, 0, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var confirm = new Button { Content = "预览实际写入", MinWidth = 128, Height = 35, Margin = new Thickness(6, 0, 0, 0) };
        confirm.Click += (_, _) =>
        {
            try
            {
                result = ReviewApprovalPlanner.Prepare(rows, true);
                dialog.DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, ex.Message, "字段选择需要调整", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel);
        DockPanel.SetDock(confirm, Dock.Right); footer.Children.Add(confirm);
        footer.Children.Add(new TextBlock
        {
            Text = "逐条检查后点“预览实际写入”；确认时会校验全部选中项。",
            VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        dialog.Content = layout;
        list.SelectedIndex = 0;
        var accepted = dialog.ShowDialog() == true;
        prepared = accepted ? result : null;
        return accepted;
    }

    void PopulateReviewFieldChoices(StackPanel panel, ReviewApprovalRow row)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = $"{row.VideoTitle}\n候选：{row.Candidate.Source} · {row.Before.Review?.Confidence ?? 0}% · {row.Candidate.Code}",
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        foreach (var field in ReviewFieldApproval.Fields)
        {
            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(225, 231, 240)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10)
            };
            var content = new StackPanel();
            border.Child = content;
            var conflicted = row.Candidate.SourceConflictFields?.Contains(field.Key, StringComparer.Ordinal) == true;
            content.Children.Add(new TextBlock { Text = field.Label + (conflicted ? " · 来源冲突，请明确选择" : ""),
                FontWeight = FontWeights.SemiBold, Foreground = conflicted ? Brushes.IndianRed : Brushes.DarkSlateGray });
            AddReviewFieldValue(content, "原值", ReviewFieldApproval.Value(row.Before.Metadata, field.Key));
            AddReviewFieldValue(content, "候选", ReviewFieldApproval.Value(row.Candidate, field.Key));
            var selector = new ComboBox { MinWidth = 190, Height = 30, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 4) };
            void AddOption(string label, ReviewFieldSelection choice) => selector.Items.Add(new ComboBoxItem { Content = label, Tag = choice });
            AddOption("保留原值", new(ReviewFieldAction.Keep));
            if (!string.IsNullOrWhiteSpace(ReviewFieldApproval.Value(row.Candidate, field.Key)))
                AddOption("采用候选", new(ReviewFieldAction.Adopt));
            foreach (var observation in ReviewFieldApproval.Options(row.Candidate, field.Key))
                AddOption($"采用 {observation.Source} 的值：{TruncateReviewValue(observation.Value, 32)}",
                    new(ReviewFieldAction.Source, observation));
            AddOption("不采用（清空）", new(ReviewFieldAction.Clear));
            if (conflicted && !row.Choices.ContainsKey(field.Key))
            {
                var placeholder = new ComboBoxItem { Content = "请选择处理方式" };
                selector.Items.Insert(0, placeholder);
                selector.SelectedItem = placeholder;
            }
            else
            {
                var current = row.Choice(field.Key);
                selector.SelectedItem = selector.Items.Cast<ComboBoxItem>().First(item =>
                {
                    if (item.Tag is not ReviewFieldSelection option) return false;
                    return option.Action == current.Action && (option.Action != ReviewFieldAction.Source ||
                        (option.Observation?.Source == current.Observation?.Source && option.Observation?.Value == current.Observation?.Value));
                });
            }
            var resulting = new TextBlock { Foreground = Brushes.SteelBlue, TextWrapping = TextWrapping.Wrap };
            void UpdateResult()
            {
                try
                {
                    var after = ReviewFieldApproval.Build(row.Before.Metadata, row.Candidate, row.Choices);
                    resulting.Text = "写入后：" + TruncateReviewValue(ReviewFieldApproval.Value(after, field.Key), 180);
                }
                catch (InvalidDataException ex) { resulting.Text = "待调整：" + ex.Message; }
            }
            selector.SelectionChanged += (_, _) =>
            {
                if (selector.SelectedItem is ComboBoxItem item && item.Tag is ReviewFieldSelection choice)
                {
                    row.Choices[field.Key] = choice;
                    UpdateResult();
                }
            };
            content.Children.Add(selector);
            content.Children.Add(resulting);
            UpdateResult();
            panel.Children.Add(border);
        }
    }

    static void AddReviewFieldValue(StackPanel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label + "：" + TruncateReviewValue(value, 160), ToolTip = value,
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
    }

    static string TruncateReviewValue(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? "（空）" : value.Length <= length ? value : value[..length] + "…";
}
