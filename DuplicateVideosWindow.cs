using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfBinding = System.Windows.Data.Binding;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

internal sealed class DuplicateVideosWindow : Window
{
    readonly IReadOnlyList<DuplicateVideoInput> inputs;
    readonly string dataDir;
    readonly CancellationTokenSource scanCancellation = new();
    readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Dictionary<string, string> keepByGroup = new(StringComparer.Ordinal);
    readonly TextBlock status = new() { Foreground = WpfBrushes.SlateGray };
    readonly TextBlock pageStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock keepStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = WpfBrushes.DarkSlateBlue };
    readonly WpfListBox groupList = new() { DisplayMemberPath = nameof(DuplicateVideoGroup.Summary) };
    readonly DataGrid videoTable = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single
    };
    readonly WpfTextBox search = new() { MinWidth = 240, Margin = new Thickness(8, 0, 8, 0) };
    readonly WpfTextBox differences = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    readonly WpfButton previous = new() { Content = "上一页", MinWidth = 74 };
    readonly WpfButton next = new() { Content = "下一页", MinWidth = 74 };
    readonly WpfButton cancel = new() { Content = "取消检测", MinWidth = 90 };
    readonly WpfButton keepSelected = new() { Content = "将选中视频设为保留项", Margin = new Thickness(0, 6, 0, 6) };
    readonly WpfButton mergeMetadata = new() { Content = "预览并确认资料合并", Margin = new Thickness(8, 6, 0, 6) };
    readonly WpfButton undoMerge = new() { Content = "撤销最近资料合并", Margin = new Thickness(8, 6, 0, 6) };
    DuplicateVideoGroup[] allGroups = [];
    DuplicateVideoGroup[] filteredGroups = [];
    DuplicateVideoRecord? keeper;
    int page;
    bool operationBusy;
    bool requiresRescan;
    CancellationTokenSource? operationCancellation;

    internal DuplicateVideoScanResult? ScanResult { get; private set; }
    internal List<DuplicateMetadataTransaction> CompletedTransactions { get; } = [];

    internal DuplicateVideosWindow(IReadOnlyList<DuplicateVideoInput> inputs, string dataDir)
    {
        this.inputs = inputs;
        this.dataDir = dataDir;
        Title = "重复视频 · 核对资料";
        Width = 1120;
        Height = 730;
        MinWidth = 860;
        MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = WpfBrushes.White;
        Content = BuildContent();
        Loaded += async (_, _) => await ScanAsync();
        Closing += (_, args) => { if (operationBusy) args.Cancel = true; };
        Closed += (_, _) => { scanCancellation.Cancel(); searchDelay.Stop(); };
        searchDelay.Tick += async (_, _) =>
        {
            searchDelay.Stop();
            await FilterAsync();
        };
        search.TextChanged += (_, _) => { searchDelay.Stop(); searchDelay.Start(); };
        groupList.SelectionChanged += (_, _) => ShowGroup();
        keepSelected.Click += (_, _) =>
        {
            if (groupList.SelectedItem is not DuplicateVideoGroup group ||
                videoTable.SelectedItem is not DuplicateVideoRecord selected) return;
            keeper = selected;
            keepByGroup[GroupKey(group)] = selected.Path;
            ShowDifferences(group);
        };
        previous.Click += (_, _) => { page--; ShowPage(); };
        next.Click += (_, _) => { page++; ShowPage(); };
        cancel.Click += (_, _) =>
        {
            if (operationCancellation is { } current) current.Cancel();
            else scanCancellation.Cancel();
        };
        mergeMetadata.Click += async (_, _) => await ConfirmMergeAsync();
        undoMerge.Click += async (_, _) => await UndoMergeAsync();
        ShowPage();
    }

    UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "重复视频", FontSize = 21, FontWeight = FontWeights.SemiBold,
            Foreground = WpfBrushes.MidnightBlue
        });
        heading.Children.Add(new TextBlock
        {
            Text = "按大小筛选，再用完整 SHA-256 确认。扫描与差异预览只读；确认后仅合并所选保留项的本地资料，绝不删除或修改视频文件。",
            Margin = new Thickness(0, 5, 0, 5), Foreground = WpfBrushes.DimGray,
            TextWrapping = TextWrapping.Wrap
        });
        heading.Children.Add(status);
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var searchRow = new DockPanel { Margin = new Thickness(0, 12, 0, 10) };
        searchRow.Children.Add(new TextBlock { Text = "搜索路径、标题、分类或标签：", VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(cancel, Dock.Right);
        searchRow.Children.Add(cancel);
        searchRow.Children.Add(search);
        Grid.SetRow(searchRow, 1);
        root.Children.Add(searchRow);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        groupList.Margin = new Thickness(0, 0, 12, 0);
        groupList.BorderBrush = WpfBrushes.LightSteelBlue;
        body.Children.Add(groupList);
        var detail = new Grid();
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        detail.Children.Add(new TextBlock
        {
            Text = "组内文件与现有资料", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        AddColumn("标题", nameof(DuplicateVideoRecord.Title), 180);
        AddColumn("分类", nameof(DuplicateVideoRecord.CategoryText), 150);
        AddColumn("标签", nameof(DuplicateVideoRecord.TagsText), 160);
        AddColumn("我的评分", nameof(DuplicateVideoRecord.ScoreText), 90);
        AddColumn("平台评分", nameof(DuplicateVideoRecord.PlatformScoreText), 160);
        AddColumn("完整路径", nameof(DuplicateVideoRecord.Path), 430);
        Grid.SetRow(videoTable, 1);
        detail.Children.Add(videoTable);
        var keepRow = new StackPanel { Orientation = WpfOrientation.Vertical };
        var actions = new StackPanel { Orientation = WpfOrientation.Horizontal };
        actions.Children.Add(keepSelected);
        actions.Children.Add(mergeMetadata);
        actions.Children.Add(undoMerge);
        keepRow.Children.Add(actions);
        keepRow.Children.Add(keepStatus);
        Grid.SetRow(keepRow, 2);
        detail.Children.Add(keepRow);
        differences.Margin = new Thickness(0, 8, 0, 0);
        differences.FontFamily = new WpfFontFamily("Consolas, Microsoft YaHei UI");
        Grid.SetRow(differences, 3);
        detail.Children.Add(differences);
        Grid.SetColumn(detail, 1);
        body.Children.Add(detail);
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        var footer = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        previous.Margin = new Thickness(0, 0, 8, 0);
        pageStatus.Margin = new Thickness(0, 0, 8, 0);
        footer.Children.Add(previous);
        footer.Children.Add(pageStatus);
        footer.Children.Add(next);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    void AddColumn(string header, string path, double width) => videoTable.Columns.Add(new DataGridTextColumn
    {
        Header = header, Binding = new WpfBinding(path), Width = new DataGridLength(width)
    });

    async Task ScanAsync()
    {
        status.Text = "正在后台读取文件并计算校验值…";
        var progress = new Progress<DuplicateVideoScanProgress>(value =>
            status.Text = $"后台检测 {value.Completed:N0} / {value.Total:N0} 部候选 · 无法读取或已变更 {value.Skipped:N0} 部");
        try
        {
            ScanResult = await Task.Run(async () =>
            {
                var ratings = PersonalRatingStore.Load(dataDir);
                return await DuplicateVideoScanner.ScanAsync(inputs, ratings, progress, scanCancellation.Token);
            }, scanCancellation.Token);
            if (scanCancellation.IsCancellationRequested) return;
            allGroups = ScanResult.Groups.ToArray();
            status.Text = $"已确认 {allGroups.Length:N0} 组 · 比对 {ScanResult.Candidates:N0} 部候选 · 跳过 {ScanResult.Skipped:N0} 部不可读或已变化文件";
            cancel.IsEnabled = false;
            await FilterAsync();
        }
        catch (OperationCanceledException)
        {
            status.Text = "检测已取消，未修改任何文件。";
            cancel.IsEnabled = false;
        }
        catch (Exception ex)
        {
            status.Text = "检测失败：" + ex.Message;
            cancel.IsEnabled = false;
        }
    }

    async Task FilterAsync()
    {
        var query = search.Text;
        var source = allGroups;
        var matches = await Task.Run(() => DuplicateVideoScanner.Filter(source, query));
        if (scanCancellation.IsCancellationRequested || query != search.Text) return;
        filteredGroups = matches;
        page = 0;
        ShowPage();
    }

    void ShowPage()
    {
        var pages = Math.Max(1, (filteredGroups.Length + DuplicateVideoScanner.PageSize - 1) / DuplicateVideoScanner.PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        groupList.ItemsSource = DuplicateVideoScanner.Page(filteredGroups, page);
        groupList.SelectedIndex = groupList.Items.Count > 0 ? 0 : -1;
        pageStatus.Text = $"第 {page + 1} / {pages} 页 · {filteredGroups.Length:N0} 组";
        previous.IsEnabled = page > 0;
        next.IsEnabled = page + 1 < pages;
    }

    void ShowGroup()
    {
        if (groupList.SelectedItem is not DuplicateVideoGroup group)
        {
            videoTable.ItemsSource = null;
            keepStatus.Text = "";
            differences.Text = "";
            keeper = null;
            keepSelected.IsEnabled = false;
            mergeMetadata.IsEnabled = false;
            return;
        }
        videoTable.ItemsSource = group.Videos;
        keeper = group.Videos.FirstOrDefault(video =>
            keepByGroup.TryGetValue(GroupKey(group), out var path) && path == video.Path) ?? group.Videos[0];
        videoTable.SelectedItem = keeper;
        keepSelected.IsEnabled = true;
        mergeMetadata.IsEnabled = !operationBusy && !requiresRescan;
        ShowDifferences(group);
    }

    void ShowDifferences(DuplicateVideoGroup group)
    {
        if (keeper is null) return;
        keepStatus.Text = "计划保留：" + keeper.Path;
        differences.Text = DuplicateVideoScanner.Compare(group, keeper);
    }

    async Task ConfirmMergeAsync()
    {
        if (operationBusy || requiresRescan || groupList.SelectedItem is not DuplicateVideoGroup group ||
            keeper is not { } keep) return;
        var proposal = DuplicateVideoScanner.ProposeLocalMerge(group, keep);
        var selectedScore = proposal.PersonalScore;
        if (proposal.RatingNeedsDecision)
        {
            var selection = ChooseRating(group);
            if (!selection.Confirmed) { status.Text = "已取消，未写入任何资料。"; return; }
            selectedScore = selection.Score;
        }
        SetBusy(true);
        try
        {
            status.Text = "正在核对最新分类、标签和评分…";
            var plan = await Task.Run(() => DuplicateMetadataTransactionStore.Prepare(
                dataDir, group, keep, selectedScore, proposal.RatingNeedsDecision));
            var answer = WpfMessageBox.Show(this, DuplicateMetadataTransactionStore.Preview(plan) +
                "\n\n确认后将重新核对每个文件的完整内容。若资料或文件变化，会停止写入。",
                "确认重复资料合并", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) { status.Text = "已取消，未写入任何资料。"; return; }
            operationCancellation = new CancellationTokenSource();
            cancel.Content = "取消校验";
            cancel.IsEnabled = true;
            status.Text = "正在后台重新校验重复文件；此时可取消，尚未写入…";
            await DuplicateMetadataTransactionStore.CommitAsync(dataDir, plan, operationCancellation.Token,
                () => { cancel.IsEnabled = false; status.Text = "校验通过，正在保存资料与撤销记录…"; });
            CompletedTransactions.Add(plan);
            requiresRescan = true;
            status.Text = "资料合并已保存。要继续处理，请关闭此页后重新检测。";
        }
        catch (OperationCanceledException) { status.Text = "校验已取消，未写入任何资料。"; }
        catch (Exception ex) { ShowOperationFailure(ex); }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            SetBusy(false);
        }
    }

    async Task UndoMergeAsync()
    {
        if (operationBusy) return;
        SetBusy(true);
        try
        {
            var last = await Task.Run(() => DuplicateMetadataTransactionStore.LastUndoable(dataDir));
            if (last is null) { status.Text = "没有可撤销的重复资料合并。"; return; }
            var answer = WpfMessageBox.Show(this,
                $"将撤销最近一次重复资料合并，恢复保留文件的分类、标签和个人评分。\n\n保留路径：{last.KeepPath}\n校验值：{last.Evidence[0].Sha256}\n\n不会删除或移动视频文件。",
                "确认撤销重复资料合并", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) { status.Text = "已取消撤销，未写入任何资料。"; return; }
            operationCancellation = new CancellationTokenSource();
            cancel.Content = "取消校验";
            cancel.IsEnabled = true;
            status.Text = "正在重新校验文件；此时可取消，尚未写入…";
            var undone = await DuplicateMetadataTransactionStore.UndoLastAsync(dataDir,
                operationCancellation.Token,
                () => { cancel.IsEnabled = false; status.Text = "校验通过，正在恢复资料与撤销记录…"; });
            CompletedTransactions.Add(undone);
            requiresRescan = true;
            status.Text = "撤销已保存。要继续处理，请关闭此页后重新检测。";
        }
        catch (OperationCanceledException) { status.Text = "撤销校验已取消，未写入任何资料。"; }
        catch (Exception ex) { ShowOperationFailure(ex); }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        operationBusy = busy;
        if (busy) searchDelay.Stop();
        groupList.IsEnabled = !busy;
        videoTable.IsEnabled = !busy;
        search.IsEnabled = !busy;
        keepSelected.IsEnabled = !busy && groupList.SelectedItem is DuplicateVideoGroup;
        mergeMetadata.IsEnabled = !busy && !requiresRescan && groupList.SelectedItem is DuplicateVideoGroup;
        undoMerge.IsEnabled = !busy;
        previous.IsEnabled = !busy && page > 0;
        next.IsEnabled = !busy && page + 1 < Math.Max(1,
            (filteredGroups.Length + DuplicateVideoScanner.PageSize - 1) / DuplicateVideoScanner.PageSize);
        if (!busy)
        {
            cancel.Content = "取消检测";
            cancel.IsEnabled = ScanResult is null && !scanCancellation.IsCancellationRequested;
        }
    }

    void ShowOperationFailure(Exception error)
    {
        if (DuplicateMetadataTransactionStore.HasPending(dataDir))
        {
            WpfMessageBox.Show(this,
                "资料写入已中断，恢复记录已保留。软件将关闭；重新启动时会先恢复或提示冲突。\n" + error.Message,
                "重复资料合并待恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
            System.Windows.Application.Current.Shutdown(1);
            return;
        }
        status.Text = "未写入资料：" + error.Message;
        WpfMessageBox.Show(this, status.Text, "重复资料合并", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    (bool Confirmed, double? Score) ChooseRating(DuplicateVideoGroup group)
    {
        var dialog = new Window
        {
            Owner = this, Title = "选择要保留的个人评分", Width = 440, Height = 230,
            MinWidth = 380, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "这组视频有不同的个人评分。请选择保留项最终使用的评分：",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        var choices = group.Videos.Where(video => video.PersonalScore is not null)
            .Select(video => video.PersonalScore!.Value).Distinct().OrderByDescending(score => score)
            .Select(score => new RatingChoice(score, score.ToString("0.#") + " / 10"))
            .Append(new RatingChoice(null, "不保留个人评分"))
            .ToArray();
        var picker = new WpfComboBox { ItemsSource = choices, DisplayMemberPath = nameof(RatingChoice.Label) };
        panel.Children.Add(picker);
        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var accept = new WpfButton { Content = "继续预览", IsDefault = true, IsEnabled = false,
            MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var dismiss = new WpfButton { Content = "取消", IsCancel = true, MinWidth = 70 };
        picker.SelectionChanged += (_, _) => accept.IsEnabled = picker.SelectedItem is RatingChoice;
        accept.Click += (_, _) => dialog.DialogResult = true;
        dismiss.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(accept);
        buttons.Children.Add(dismiss);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog.ShowDialog() == true && picker.SelectedItem is RatingChoice chosen
            ? (true, chosen.Score) : (false, null);
    }

    sealed record RatingChoice(double? Score, string Label);

    static string GroupKey(DuplicateVideoGroup group) => group.Size + ":" + group.Sha256;
}
