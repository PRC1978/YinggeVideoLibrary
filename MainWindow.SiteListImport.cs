using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Binding = System.Windows.Data.Binding;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void OpenSiteListImport(object sender, RoutedEventArgs e)
    {
        var dialog = BuildSiteListImportDialog();
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    Window BuildSiteListImportDialog()
    {
        var dialog = new Window
        {
            Title = "导入站点清单", Width = 900, Height = 680,
            MinWidth = 690, MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = root;

        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "导入你自己导出的收藏或片单", FontSize = 21,
            FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(31, 49, 80)) });
        header.Children.Add(SettingsHint("选择 CSV 或 JSON 文件，先查看每项的匹配状态。已匹配会关联本地视频；待核对和未匹配也保留在独立清单中。不会自动修改视频资料或本地收藏。"));
        root.Children.Add(header);

        var sourceRow = new Grid { Margin = new Thickness(0, 12, 0, 9) };
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var source = new TextBox { Height = 36, Padding = new Thickness(8, 5, 8, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "用户自己导出的 UTF-8 CSV 或 JSON 文件，最多 8 MB / 25,000 条" };
        sourceRow.Children.Add(source);
        var browse = SiteListButton("选择文件…");
        Grid.SetColumn(browse, 1);
        sourceRow.Children.Add(browse);
        Grid.SetRow(sourceRow, 1);
        root.Children.Add(sourceRow);

        var status = new TextBlock
        {
            Text = "支持标题、番号、链接、来源字段；不连接站点或读取登录信息。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(83, 102, 132)),
            Margin = new Thickness(0, 4, 0, 10)
        };
        Grid.SetRow(status, 2);
        root.Children.Add(status);

        var rows = new DataGrid
        {
            IsReadOnly = true, AutoGenerateColumns = false,
            CanUserAddRows = false, CanUserDeleteRows = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Background = Brushes.White
        };
        rows.Columns.Add(new DataGridTextColumn { Header = "序号", Binding = new Binding(nameof(SiteListImportStore.PreviewRow.Number)), Width = 55 });
        rows.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding(nameof(SiteListImportStore.PreviewRow.Status)), Width = 80 });
        rows.Columns.Add(new DataGridTextColumn { Header = "来源", Binding = new Binding(nameof(SiteListImportStore.PreviewRow.Source)), Width = 120 });
        rows.Columns.Add(new DataGridTextColumn { Header = "作品", Binding = new Binding(nameof(SiteListImportStore.PreviewRow.Title)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        rows.Columns.Add(new DataGridTextColumn { Header = "编号", Binding = new Binding(nameof(SiteListImportStore.PreviewRow.Code)), Width = 110 });
        rows.Columns.Add(new DataGridTextColumn { Header = "本地对应 / 原因", Binding = new Binding(nameof(SiteListImportStore.PreviewRow.MatchSummary)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        rows.LoadingRow += (_, args) =>
        {
            if (args.Row.Item is SiteListImportStore.PreviewRow item)
                args.Row.ToolTip = item.Reason + (item.CandidateCount > 1 ? $"（{item.CandidateCount} 个候选）" : "")
                    + (item.Url.Length > 0 ? "\n" + item.Url : "");
        };
        Grid.SetRow(rows, 3);
        root.Children.Add(rows);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var cancel = SiteListButton("取消");
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);
        var commit = SiteListButton("确认导入", primary: true);
        commit.IsEnabled = false;
        DockPanel.SetDock(commit, Dock.Right);
        footer.Children.Add(commit);
        var preview = SiteListButton("预览匹配");
        DockPanel.SetDock(preview, Dock.Right);
        footer.Children.Add(preview);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        SiteListImportStore.Preview? active = null;
        var busy = false;
        CancellationTokenSource? previewCancel = null;
        CancellationTokenSource? commitCancel = null;
        void SetBusy(bool value)
        {
            busy = value;
            source.IsEnabled = browse.IsEnabled = preview.IsEnabled = !value;
            cancel.IsEnabled = true;
            commit.IsEnabled = !value && active?.Importable > 0;
        }
        source.TextChanged += (_, _) => { active = null; commit.IsEnabled = false; rows.ItemsSource = null; };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog
            {
                Title = "选择你自己导出的站点清单",
                Filter = "CSV 或 JSON 清单|*.csv;*.json|CSV 清单|*.csv|JSON 清单|*.json",
                CheckFileExists = true, Multiselect = false
            };
            if (picker.ShowDialog(dialog) == true) source.Text = picker.FileName;
        };
        preview.Click += async (_, _) =>
        {
            if (source.Text.Trim().Length == 0) { status.Text = "先选择 CSV 或 JSON 文件。"; return; }
            active = null;
            rows.ItemsSource = null;
            using var cancellation = new CancellationTokenSource();
            previewCancel = cancellation;
            SetBusy(true);
            status.Text = "正在读取文件并与本地视频比较；可取消，预览不会写入。";
            try
            {
                // Copy only the matching fields on the UI thread, then process the
                // export and the large library index in a worker.
                var local = SnapshotSiteListLibrary();
                var filePath = source.Text.Trim();
                var progress = new Progress<SiteListImportStore.HashProgress>(item =>
                {
                    if (busy && !cancellation.IsCancellationRequested)
                        status.Text = $"正在校验已匹配本地视频内容 {item.Done:N0}/{item.Total:N0}；可取消，预览不会写入。";
                });
                active = await Task.Run(() => SiteListImportStore.CreatePreview(
                    DataDir, filePath, local, cancellation.Token, progress));
                rows.ItemsSource = active.Rows;
                status.Text = $"共 {active.Rows.Count:N0} 条 · 已匹配 {active.Matched:N0} · 待核对 {active.Pending:N0} · 未匹配 {active.Unmatched:N0} · 重复 {active.Duplicates:N0}。可导入 {active.Importable:N0} 条。";
                if (active.Importable == 0 && active.Duplicates > 0)
                    status.Text += " 如需给旧清单补充内容校验，请先在“查看已导入清单”中撤销所属导入批次，再重新导入原文件。";
            }
            catch (OperationCanceledException) { status.Text = "已取消预览；没有写入清单。"; }
            catch (Exception ex) { status.Text = "预览失败：" + ex.Message; }
            finally
            {
                previewCancel = null;
                SetBusy(false);
                if (cancellation.IsCancellationRequested && dialog.IsVisible) dialog.DialogResult = false;
            }
        };
        commit.Click += async (_, _) =>
        {
            var selected = active;
            if (selected is null || selected.Importable == 0) return;
            var question = $"导入 {selected.Importable:N0} 条到独立清单？\n已匹配 {selected.Matched:N0} · 待核对 {selected.Pending:N0} · 未匹配 {selected.Unmatched:N0}\n不会自动改动本地收藏和影片资料。";
            if (MessageBox.Show(dialog, question, "确认导入", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            using var cancellation = new CancellationTokenSource();
            commitCancel = cancellation;
            SetBusy(true);
            status.Text = "正在复核已匹配视频的内容；保存前可取消。";
            try
            {
                var local = SnapshotSiteListLibrary();
                var progress = new Progress<SiteListImportStore.HashProgress>(item =>
                {
                    if (busy && !cancellation.IsCancellationRequested)
                        status.Text = $"正在复核已匹配视频 {item.Done:N0}/{item.Total:N0}；保存前可取消。";
                });
                var action = await Task.Run(() => SiteListImportStore.Commit(
                    DataDir, selected, local, cancellation.Token, progress));
                Log($"导入站点清单 {action.EntryKeys.Count:N0} 条；可在设置中查看或撤销");
                SetBusy(false);
                dialog.DialogResult = true;
                OpenSavedSiteLists(this, new RoutedEventArgs());
            }
            catch (OperationCanceledException) { status.Text = "已取消导入；没有写入清单。"; }
            catch (Exception ex)
            {
                active = null;
                status.Text = "导入失败：" + ex.Message + " 请重新预览。";
            }
            finally { commitCancel = null; if (dialog.IsVisible) SetBusy(false); }
        };
        cancel.Click += (_, _) =>
        {
            if (busy) (previewCancel ?? commitCancel)?.Cancel();
            else dialog.DialogResult = false;
        };
        dialog.Closing += (_, args) =>
        {
            if (!busy) return;
            (previewCancel ?? commitCancel)?.Cancel();
            args.Cancel = true;
        };
        return dialog;
    }

    SiteListImportStore.LocalVideo[] SnapshotSiteListLibrary()
    {
        return videos.Select(SnapshotSiteListVideo).ToArray();
    }

    SiteListImportStore.LocalVideo SnapshotSiteListVideo(VideoItem video)
    {
        metadata.TryGetValue(video.Id, out var details);
        return new SiteListImportStore.LocalVideo(video.Id, video.Title, video.DisplayTitle,
            !string.IsNullOrWhiteSpace(video.Code) ? video.Code : details?.Code ?? "",
            details?.Title ?? "", details?.OriginalTitle ?? "",
            details?.DetailIdentityVerified == true ? details.SourceUrl : "",
            video.Path, video.Size, video.Modified.Ticks);
    }

    async void OpenSavedSiteLists(object sender, RoutedEventArgs e)
    {
        SiteListImportStore.State state;
        try { state = await Task.Run(() => SiteListImportStore.Load(DataDir)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "已导入清单", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (isClosing || !IsLoaded) return;
        var dialog = new Window
        {
            Title = "已导入站点清单", Width = 980, Height = 790,
            MinWidth = 690, MinHeight = 510,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = root;
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var summary = new TextBlock { Text = $"已保存 {state.Entries.Count:N0} 条 · {state.Actions.Count(action => action.Kind == "import"):N0} 次导入",
            FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(summary);
        var filters = new DockPanel { LastChildFill = false };
        var statusFilter = new ComboBox { Width = 105, Height = 34, ItemsSource = new[] { "全部", "已匹配", "待核对", "未匹配" },
            SelectedIndex = 0, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(statusFilter, Dock.Right);
        filters.Children.Add(statusFilter);
        var search = new TextBox { Height = 34, MinWidth = 250, Padding = new Thickness(8, 5, 8, 5),
            ToolTip = "按标题、编号、来源或本地片名筛选已导入清单" };
        DockPanel.SetDock(search, Dock.Left);
        filters.Children.Add(search);
        top.Children.Add(filters);
        root.Children.Add(top);
        var entries = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, ItemsSource = state.Entries,
            CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Extended };
        entries.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding(nameof(SiteListImportStore.Entry.Status)), Width = 80 });
        entries.Columns.Add(new DataGridTextColumn { Header = "来源", Binding = new Binding(nameof(SiteListImportStore.Entry.Source)), Width = 125 });
        entries.Columns.Add(new DataGridTextColumn { Header = "作品", Binding = new Binding(nameof(SiteListImportStore.Entry.Title)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        entries.Columns.Add(new DataGridTextColumn { Header = "编号", Binding = new Binding(nameof(SiteListImportStore.Entry.Code)), Width = 110 });
        entries.Columns.Add(new DataGridTextColumn { Header = "本地对应", Binding = new Binding(nameof(SiteListImportStore.Entry.LocalTitle)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        entries.LoadingRow += (_, args) =>
        {
            if (args.Row.Item is SiteListImportStore.Entry item) args.Row.ToolTip = item.Url;
        };
        Grid.SetRow(entries, 1);
        root.Children.Add(entries);
        var historyTitle = new TextBlock { Text = "导入与撤销记录", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 7) };
        Grid.SetRow(historyTitle, 2);
        root.Children.Add(historyTitle);
        var history = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false,
            EnableRowVirtualization = true, ItemsSource = state.Actions.AsEnumerable().Reverse().ToArray(),
            CanUserAddRows = false, CanUserDeleteRows = false };
        history.Columns.Add(new DataGridTextColumn { Header = "时间", Binding = new Binding(nameof(SiteListImportStore.AuditAction.AtUtc)), Width = 180 });
        history.Columns.Add(new DataGridTextColumn { Header = "操作", Binding = new Binding(nameof(SiteListImportStore.AuditAction.Kind)), Width = 80 });
        history.Columns.Add(new DataGridTextColumn { Header = "文件", Binding = new Binding(nameof(SiteListImportStore.AuditAction.SourceFileName)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        history.Columns.Add(new DataGridTextColumn { Header = "已匹配", Binding = new Binding(nameof(SiteListImportStore.AuditAction.Matched)), Width = 76 });
        history.Columns.Add(new DataGridTextColumn { Header = "待核对", Binding = new Binding(nameof(SiteListImportStore.AuditAction.Pending)), Width = 76 });
        history.Columns.Add(new DataGridTextColumn { Header = "未匹配", Binding = new Binding(nameof(SiteListImportStore.AuditAction.Unmatched)), Width = 76 });
        Grid.SetRow(history, 3);
        root.Children.Add(history);
        var localTitle = new TextBlock { Text = "本地收藏和标签操作（勾选上方已匹配条目后手动应用）",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 7) };
        Grid.SetRow(localTitle, 4);
        root.Children.Add(localTitle);
        var localHistory = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false,
            EnableRowVirtualization = true, CanUserAddRows = false, CanUserDeleteRows = false,
            ItemsSource = config.SiteListLocalActions.Values.OrderByDescending(action => action.TimeUtc).ToArray() };
        localHistory.Columns.Add(new DataGridTextColumn { Header = "时间", Binding = new Binding(nameof(SiteListLocalAction.TimeUtc)), Width = 180 });
        localHistory.Columns.Add(new DataGridTextColumn { Header = "操作", Binding = new Binding(nameof(SiteListLocalAction.Kind)), Width = 70 });
        localHistory.Columns.Add(new DataGridTextColumn { Header = "内容", Binding = new Binding(nameof(SiteListLocalAction.Label)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        localHistory.Columns.Add(new DataGridTextColumn { Header = "视频", Binding = new Binding("Changes.Count"), Width = 60 });
        Grid.SetRow(localHistory, 5);
        root.Children.Add(localHistory);
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var close = SiteListButton("关闭");
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        var play = SiteListButton("播放所选本地视频");
        play.IsEnabled = false;
        DockPanel.SetDock(play, Dock.Right);
        footer.Children.Add(play);
        var sourceLink = SiteListButton("打开所选原链接");
        sourceLink.IsEnabled = false;
        DockPanel.SetDock(sourceLink, Dock.Right);
        footer.Children.Add(sourceLink);
        var undo = SiteListButton("撤销选中的导入");
        DockPanel.SetDock(undo, Dock.Right);
        footer.Children.Add(undo);
        var applyLocal = SiteListButton("预览收藏/标签…");
        applyLocal.IsEnabled = false;
        DockPanel.SetDock(applyLocal, Dock.Right);
        footer.Children.Add(applyLocal);
        var undoLocal = SiteListButton("撤销所选本地操作");
        undoLocal.IsEnabled = false;
        DockPanel.SetDock(undoLocal, Dock.Right);
        footer.Children.Add(undoLocal);
        Grid.SetRow(footer, 6);
        root.Children.Add(footer);
        void ApplyFilter()
        {
            var query = search.Text.Trim();
            var status = statusFilter.SelectedItem as string ?? "全部";
            entries.ItemsSource = state.Entries.Where(item =>
                (status == "全部" || item.Status == status) &&
                (query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                 item.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 item.Source.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                 item.LocalTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                .ToArray();
        }
        var filterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); ApplyFilter(); };
        search.TextChanged += (_, _) => { filterTimer.Stop(); filterTimer.Start(); };
        statusFilter.SelectionChanged += (_, _) => ApplyFilter();
        var playing = false;
        entries.SelectionChanged += (_, _) =>
        {
            var selected = entries.SelectedItem as SiteListImportStore.Entry;
            play.IsEnabled = !playing && selected?.Status == "已匹配";
            sourceLink.IsEnabled = selected is not null && SafeSiteListLink(selected.Url) is not null;
            applyLocal.IsEnabled = entries.SelectedItems.Cast<SiteListImportStore.Entry>()
                .Any(item => item.Status == "已匹配");
        };
        applyLocal.Click += (_, _) =>
        {
            var selected = entries.SelectedItems.Cast<SiteListImportStore.Entry>().ToArray();
            if (selected.Any(item => item.Status != "已匹配"))
            {
                MessageBox.Show(dialog, "选中项含待核对或未匹配条目。请只选已匹配的作品。", "不能应用本地操作");
                return;
            }
            if (selected.FirstOrDefault(item => item.ContentSha256.Length != 64) is { } oldEntry)
            {
                var import = state.Actions.FirstOrDefault(action => action.Kind == "import" && action.BatchId == oldEntry.BatchId);
                if (import is not null) { history.SelectedItem = import; history.ScrollIntoView(import); }
                MessageBox.Show(dialog, "所选旧记录没有导入时的完整内容校验。已定位下方所属导入批次；请撤销该批次，再重新导入原导出文件。直接重导会被识别为重复。撤销清单不会改动本地收藏或分类。",
                    "旧清单需要重新导入", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenSiteListApplyPreview(dialog, selected, action =>
            {
                localHistory.ItemsSource = config.SiteListLocalActions.Values
                    .OrderByDescending(record => record.TimeUtc).ToArray();
                Log($"片单本地操作完成：{action.Changes.Count:N0} 部 · {action.Label}");
            });
        };
        play.Click += async (_, _) =>
        {
            if (playing) return;
            if (entries.SelectedItem is not SiteListImportStore.Entry selected) return;
            var video = videos.FirstOrDefault(video => video.Id == selected.VideoId);
            if (video is null) { MessageBox.Show(dialog, "本地对应的视频不在当前索引中，请重新扫描并重新导入核对。", "影片不可用"); return; }
            if (selected.LocalFingerprint.Length != 64 || selected.ContentSha256.Length != 64)
            {
                var import = state.Actions.FirstOrDefault(action => action.Kind == "import" && action.BatchId == selected.BatchId);
                if (import is not null) { history.SelectedItem = import; history.ScrollIntoView(import); }
                MessageBox.Show(dialog, "这条旧清单记录没有导入时的完整内容校验。已定位下方所属导入批次；请先撤销该批次，再重新导入原导出文件。直接重导会被识别为重复。",
                    "旧清单需要重新导入", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            playing = true;
            play.IsEnabled = false;
            summary.Text = "正在核对本地视频的完整内容，请稍候…";
            try
            {
                var snapshot = SnapshotSiteListVideo(video);
                var sameContent = await Task.Run(() =>
                {
                    var current = SiteListImportStore.RefreshCurrentFileStamp(snapshot);
                    return SiteListImportStore.CanPlayMatchedEntry(selected, current);
                });
                if (!dialog.IsVisible) return;
                if (!ReferenceEquals(entries.SelectedItem, selected)) return;
                if (!sameContent)
                {
                    MessageBox.Show(dialog, "本地文件或作品资料已变化。为避免播放错误作品，已停止播放；请重新导入核对。", "需要重新核对");
                    return;
                }
                dialog.Close();
                LaunchVideo(video);
            }
            catch (Exception ex)
            {
                if (dialog.IsVisible) MessageBox.Show(dialog, "无法核对本地文件：" + ex.Message,
                    "影片不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                playing = false;
                if (dialog.IsVisible)
                {
                    summary.Text = $"已保存 {state.Entries.Count:N0} 条 · {state.Actions.Count(action => action.Kind == "import"):N0} 次导入";
                    play.IsEnabled = entries.SelectedItem is SiteListImportStore.Entry item && item.Status == "已匹配";
                }
            }
        };
        sourceLink.Click += (_, _) =>
        {
            if (entries.SelectedItem is not SiteListImportStore.Entry selected ||
                SafeSiteListLink(selected.Url) is not { } uri) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "无法打开原链接", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        history.SelectionChanged += (_, _) =>
        {
            var selected = history.SelectedItem as SiteListImportStore.AuditAction;
            undo.IsEnabled = selected?.Kind == "import" && selected.EntryKeys.Count > 0 &&
                !state.Actions.Any(action => action.Kind == "undo" && action.BatchId == selected.BatchId);
        };
        undo.IsEnabled = false;
        localHistory.SelectionChanged += (_, _) =>
        {
            var selected = localHistory.SelectedItem as SiteListLocalAction;
            undoLocal.IsEnabled = selected?.Kind == "apply" &&
                !config.SiteListLocalActions.Values.Any(action =>
                    action.Kind == "undo" && action.UndoesActionId == selected.Id);
        };
        undoLocal.Click += async (_, _) =>
        {
            if (localHistory.SelectedItem is not SiteListLocalAction selected || selected.Kind != "apply") return;
            if (MessageBox.Show(dialog, $"撤销“{selected.Label}”对 {selected.Changes.Count:N0} 部本地视频的修改？\n如果随后改过收藏或标签，系统会拒绝覆盖。",
                    "撤销本地操作", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            undoLocal.IsEnabled = close.IsEnabled = false;
            try
            {
                await UndoSiteListLocalActionAsync(selected.Id);
                localHistory.ItemsSource = config.SiteListLocalActions.Values
                    .OrderByDescending(action => action.TimeUtc).ToArray();
                Log($"已撤销片单本地操作：{selected.Changes.Count:N0} 部");
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "撤销失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { close.IsEnabled = true; }
        };
        undo.Click += async (_, _) =>
        {
            if (history.SelectedItem is not SiteListImportStore.AuditAction selected || selected.Kind != "import") return;
            if (MessageBox.Show(dialog, $"撤销这次导入的 {selected.EntryKeys.Count:N0} 条清单记录？\n本地影片资料和收藏不会改变。",
                    "撤销清单导入", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            undo.IsEnabled = close.IsEnabled = false;
            try
            {
                await Task.Run(() => SiteListImportStore.Undo(DataDir, selected.BatchId));
                state = await Task.Run(() => SiteListImportStore.Load(DataDir));
                ApplyFilter();
                history.ItemsSource = state.Actions.AsEnumerable().Reverse().ToArray();
                summary.Text = $"已保存 {state.Entries.Count:N0} 条 · {state.Actions.Count(action => action.Kind == "import"):N0} 次导入";
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "撤销失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { close.IsEnabled = true; }
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => filterTimer.Stop();
        dialog.ShowDialog();
    }

    sealed class SiteListApplyDisplayRow
    {
        public string VideoTitle { get; init; } = "";
        public string Change { get; init; } = "";
    }

    void OpenSiteListApplyPreview(Window owner, SiteListImportStore.Entry[] selected,
        Action<SiteListLocalAction> onCommitted)
    {
        if (selected.Length == 0) return;
        var dialog = new Window
        {
            Title = "预览片单的本地操作", Width = 700, Height = 570,
            MinWidth = 580, MinHeight = 420, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = root;
        var intro = new TextBlock
        {
            Text = $"已选 {selected.Length:N0} 条匹配记录。选择要改动的本地字段，先计算预览，再确认写入。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(intro);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var favorite = new CheckBox { Content = "加入本地收藏", IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        options.Children.Add(favorite);
        options.Children.Add(new TextBlock { Text = "另加标签：", VerticalAlignment = VerticalAlignment.Center });
        var tag = new TextBox { Width = 180, Height = 30,
            ToolTip = "留空表示不添加标签；不会覆盖已有标签" };
        options.Children.Add(tag);
        Grid.SetRow(options, 1);
        root.Children.Add(options);
        var status = new TextBlock { Text = "计算预览不会写入任何数据。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Foreground = new SolidColorBrush(Color.FromRgb(83, 102, 132)) };
        Grid.SetRow(status, 2);
        root.Children.Add(status);
        var rows = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false,
            CanUserAddRows = false, CanUserDeleteRows = false, EnableRowVirtualization = true };
        rows.Columns.Add(new DataGridTextColumn { Header = "本地视频", Binding = new Binding(nameof(SiteListApplyDisplayRow.VideoTitle)),
            Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        rows.Columns.Add(new DataGridTextColumn { Header = "将修改", Binding = new Binding(nameof(SiteListApplyDisplayRow.Change)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(rows, 3);
        root.Children.Add(rows);
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var cancel = SiteListButton("取消");
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);
        var commit = SiteListButton("确认写入", primary: true);
        commit.IsEnabled = false;
        DockPanel.SetDock(commit, Dock.Right);
        footer.Children.Add(commit);
        var previewButton = SiteListButton("计算预览");
        DockPanel.SetDock(previewButton, Dock.Right);
        footer.Children.Add(previewButton);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        SiteListLocalActionStore.Preview? prepared = null;
        var busy = false;
        var committing = false;
        CancellationTokenSource? previewCancel = null;
        void Invalidate()
        {
            prepared = null;
            commit.IsEnabled = false;
            rows.ItemsSource = null;
        }
        favorite.Checked += (_, _) => Invalidate();
        favorite.Unchecked += (_, _) => Invalidate();
        tag.TextChanged += (_, _) => Invalidate();
        void SetBusy(bool value)
        {
            busy = value;
            favorite.IsEnabled = tag.IsEnabled = previewButton.IsEnabled = !value;
            cancel.IsEnabled = !committing;
            commit.IsEnabled = !value && prepared?.Changes.Count > 0;
        }
        previewButton.Click += async (_, _) =>
        {
            Invalidate();
            using var cancellation = new CancellationTokenSource();
            previewCancel = cancellation;
            SetBusy(true);
            status.Text = "正在核对清单、本地文件和现有收藏标签…";
            try
            {
                var local = SnapshotSiteListLibrary();
                var ids = selected.Select(entry => entry.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var favorites = config.Favorites.Where(ids.Contains).ToList();
                var tags = config.Tags.Where(pair => ids.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                var target = new LibraryConfig { Favorites = favorites, Tags = tags };
                var useFavorite = favorite.IsChecked == true;
                var newTag = tag.Text.Trim();
                prepared = await Task.Run(() => SiteListLocalActionStore.Prepare(
                    SiteListImportStore.Load(DataDir), selected, local, target, useFavorite, newTag,
                    cancellation.Token));
                var names = selected.GroupBy(entry => entry.VideoId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().LocalTitle, StringComparer.OrdinalIgnoreCase);
                rows.ItemsSource = prepared.Changes.Select(change => new SiteListApplyDisplayRow
                {
                    VideoTitle = names.GetValueOrDefault(change.VideoId, change.VideoId),
                    Change = (change.AfterFavorite == true ? "加入收藏" : "") +
                        (change.AfterTags is not null ? (change.AfterFavorite == true ? " · " : "") + "添加标签 #" + newTag : "")
                }).ToArray();
                status.Text = $"将修改 {prepared.Changes.Count:N0} 部 · 重复关联同一视频 {prepared.DuplicateLocalVideos:N0} 条 · 已满足 {prepared.AlreadySatisfied:N0} 部。预览阶段没有写入。";
            }
            catch (OperationCanceledException) { status.Text = "已取消预览；没有写入本地收藏或标签。"; }
            catch (Exception ex) { status.Text = "预览失败：" + ex.Message; }
            finally
            {
                var canceled = cancellation.IsCancellationRequested;
                previewCancel = null;
                SetBusy(false);
                if (canceled && dialog.IsVisible) dialog.DialogResult = false;
            }
        };
        commit.Click += async (_, _) =>
        {
            var current = prepared;
            if (current?.Changes.Count is not > 0) return;
            if (MessageBox.Show(dialog, $"确认把预览的收藏和标签变更写入 {current.Changes.Count:N0} 部本地视频？\n此操作会记录历史，可以在清单中撤销。",
                    "确认本地操作", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            committing = true;
            SetBusy(true);
            status.Text = "正在重新核对并保存操作…";
            try
            {
                var action = await CommitSiteListLocalActionAsync(current);
                onCommitted(action);
                committing = false;
                SetBusy(false);
                dialog.DialogResult = true;
            }
            catch (Exception ex)
            {
                Invalidate();
                status.Text = "写入失败：" + ex.Message + " 请重新计算预览。";
            }
            finally { committing = false; if (dialog.IsVisible) SetBusy(false); }
        };
        cancel.Click += (_, _) =>
        {
            if (busy)
            {
                previewCancel?.Cancel();
                status.Text = "正在取消预览…";
            }
            else dialog.DialogResult = false;
        };
        dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
        dialog.ShowDialog();
    }

    static Uri? SafeSiteListLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0) return null;
        return uri;
    }

    Button SiteListButton(string label, bool primary = false) => new()
    {
        Content = label,
        Style = (Style)FindResource(primary ? "Primary" : "Action"),
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(8, 0, 0, 0)
    };
}
