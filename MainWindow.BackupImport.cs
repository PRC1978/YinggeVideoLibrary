using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void OpenSelectiveBackupImport(object sender, RoutedEventArgs e)
    {
        var dialog = BuildSelectiveBackupImportDialog();
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    Window BuildSelectiveBackupImportDialog()
    {
        var dialog = new Window
        {
            Title = "选择性导入备份",
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 760,
            Height = 710,
            MinWidth = 640,
            MinHeight = 560,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = root;

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(new TextBlock
        {
            Text = "从备份导入所选资料", FontSize = 21, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 49, 80))
        });
        header.Children.Add(ImportHint("先选择范围并预览差异。确认后排定于下次启动前导入；视频原文件不会移动。"));
        root.Children.Add(header);

        var body = new StackPanel();
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        body.Children.Add(new TextBlock { Text = "备份来源", FontWeight = FontWeights.SemiBold });
        body.Children.Add(ImportHint("选择一个含有备份清单的文件夹。也可粘贴其他位置的备份路径。"));
        var sourceRow = new Grid { Margin = new Thickness(0, 0, 0, 17) };
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string latestPath;
        try { latestPath = LibraryBackupService.LatestBackup(DataDir) ?? string.Empty; }
        catch { latestPath = string.Empty; }
        var source = new TextBox
        {
            Text = latestPath, Height = 38, MinWidth = 160, Padding = new Thickness(9, 6, 9, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromRgb(210, 222, 240))
        };
        sourceRow.Children.Add(source);
        var recent = ImportButton("最近备份");
        Grid.SetColumn(recent, 1);
        sourceRow.Children.Add(recent);
        var browse = ImportButton("选择文件夹…");
        Grid.SetColumn(browse, 2);
        sourceRow.Children.Add(browse);
        var export = ImportButton("生成跨设备包");
        export.ToolTip = "主动读取当前索引中的每部视频，计算完整内容指纹。大库可能需要数小时，可随时取消；普通定期备份不会执行这一步。";
        Grid.SetColumn(export, 3);
        sourceRow.Children.Add(export);
        body.Children.Add(sourceRow);

        body.Children.Add(new TextBlock { Text = "导入范围", FontWeight = FontWeights.SemiBold });
        body.Children.Add(ImportHint("可同时选择多项；未勾选的资料完全不处理。"));
        var scopes = new WrapPanel { Margin = new Thickness(0, 0, 0, 17) };
        var scopeChecks = new List<CheckBox>();
        foreach (var (scope, label) in new[]
        {
            (LibraryImportScope.Metadata, "影片资料"),
            (LibraryImportScope.Categories, "分类"),
            (LibraryImportScope.Tags, "标签"),
            (LibraryImportScope.Ratings, "个人评分"),
            (LibraryImportScope.Reviews, "审核记录"),
            (LibraryImportScope.Queue, "搜刮队列"),
            (LibraryImportScope.SiteLists, "站点片单及审计")
            , (LibraryImportScope.RoleProfiles, "角色档案与作品关联")
        })
        {
            var option = new CheckBox
            {
                Content = label, Tag = scope, IsChecked = true,
                Width = 205, Margin = new Thickness(0, 8, 0, 8)
            };
            scopeChecks.Add(option);
            scopes.Children.Add(option);
        }
        body.Children.Add(scopes);
        var skipUnindexed = new CheckBox
        {
            Content = "仅导入索引内分类，跳过来源库的历史孤项",
            IsChecked = false, Margin = new Thickness(0, 0, 0, 3)
        };
        body.Children.Add(skipUnindexed);
        body.Children.Add(ImportHint("仅跨设备迁移时生效。预览会列出跳过数量和旧编号示例；原迁移包不会修改。"));

        body.Children.Add(new TextBlock { Text = "当前资料与备份冲突时", FontWeight = FontWeights.SemiBold });
        var keep = new RadioButton
        {
            Content = "保留当前资料（推荐）", IsChecked = true,
            GroupName = "BackupImportConflict", Margin = new Thickness(0, 10, 0, 5)
        };
        var replace = new RadioButton
        {
            Content = "优先使用备份资料", GroupName = "BackupImportConflict",
            Margin = new Thickness(0, 0, 0, 5)
        };
        body.Children.Add(keep);
        body.Children.Add(replace);
        body.Children.Add(ImportHint("审核历史始终只追加缺失记录。选择备份优先时，预览会列出将替换的数量。"));

        var previewBox = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(217, 226, 241)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14), Margin = new Thickness(0, 12, 5, 0),
            MinHeight = 170
        };
        var previewText = new TextBlock
        {
            Text = "点击“预览差异”查看备份时间、来源、目标和各项改动数量。",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(75, 91, 116))
        };
        previewBox.Child = previewText;
        body.Children.Add(previewBox);
        var mappingChoices = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(mappingChoices);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        var close = ImportButton("取消");
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        var stage = ImportButton("确认并排定", primary: true);
        stage.IsEnabled = false;
        DockPanel.SetDock(stage, Dock.Right);
        footer.Children.Add(stage);
        var preview = ImportButton("预览差异");
        DockPanel.SetDock(preview, Dock.Right);
        footer.Children.Add(preview);

        LibraryBackupService.ImportPreview? currentPreview = null;
        var busy = false;
        CancellationTokenSource? exportCancellation = null;
        var manualMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        LibraryImportScope SelectedScopes()
        {
            var selected = LibraryImportScope.None;
            foreach (var option in scopeChecks)
                if (option.IsChecked == true && option.Tag is LibraryImportScope scope) selected |= scope;
            return selected;
        }
        void InvalidatePreview()
        {
            currentPreview = null;
            stage.IsEnabled = false;
            previewText.Text = "条件已更改，请重新预览差异。";
            mappingChoices.Children.Clear();
            manualMapping.Clear();
        }
        void SetBusy(bool value)
        {
            busy = value;
            source.IsEnabled = !value;
            recent.IsEnabled = !value;
            browse.IsEnabled = !value;
            export.IsEnabled = !value;
            foreach (var option in scopeChecks) option.IsEnabled = !value;
            skipUnindexed.IsEnabled = !value;
            keep.IsEnabled = !value;
            replace.IsEnabled = !value;
            preview.IsEnabled = !value;
            stage.IsEnabled = !value && currentPreview?.HasChanges == true;
            close.Content = value && exportCancellation is not null ? "取消生成" : "取消";
            close.IsEnabled = !value || exportCancellation is not null;
        }

        source.TextChanged += (_, _) => InvalidatePreview();
        foreach (var option in scopeChecks)
        {
            option.Checked += (_, _) => InvalidatePreview();
            option.Unchecked += (_, _) => InvalidatePreview();
        }
        keep.Checked += (_, _) => InvalidatePreview();
        replace.Checked += (_, _) => InvalidatePreview();
        skipUnindexed.Checked += (_, _) => InvalidatePreview();
        skipUnindexed.Unchecked += (_, _) => InvalidatePreview();
        recent.Click += (_, _) =>
        {
            try
            {
                source.Text = LibraryBackupService.LatestBackup(DataDir) ?? string.Empty;
                if (source.Text.Length == 0) previewText.Text = "没有找到本机备份，请选择其他备份文件夹。";
            }
            catch (Exception ex) { previewText.Text = "查找最近备份失败：" + ex.Message; }
        };
        browse.Click += (_, _) =>
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择含有映格备份清单的文件夹",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (System.IO.Directory.Exists(source.Text)) picker.SelectedPath = source.Text;
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) source.Text = picker.SelectedPath;
        };
        export.Click += async (_, _) =>
        {
            InvalidatePreview();
            exportCancellation = new CancellationTokenSource();
            SetBusy(true);
            previewText.Text = "正在创建备份并读取全部视频内容指纹；大库可能需要数小时，可点击取消生成。";
            try
            {
                var operation = exportCancellation;
                var token = operation.Token;
                var package = await Task.Run(() => LibraryBackupService.CreateCrossDevicePackage(DataDir, token,
                    (done, total) => dialog.Dispatcher.BeginInvoke(() =>
                    {
                        if (exportCancellation == operation)
                            previewText.Text = $"正在计算跨设备指纹：{done:N0}/{total:N0} 部…";
                    })));
                source.Text = package;
                previewText.Text = "跨设备迁移包已生成：" + package
                    + "\n复制整个文件夹到另一设备，选择后预览导入。视频原文件不会装入迁移包。";
            }
            catch (OperationCanceledException) { previewText.Text = "已取消生成，未留下不完整迁移包。"; }
            catch (Exception ex) { previewText.Text = "生成跨设备包失败：" + ex.Message; }
            finally
            {
                exportCancellation.Dispose();
                exportCancellation = null;
                SetBusy(false);
            }
        };
        preview.Click += async (_, _) =>
        {
            var selected = SelectedScopes();
            var path = source.Text.Trim();
            if (selected == LibraryImportScope.None)
            {
                previewText.Text = "请至少选择一种资料。";
                return;
            }
            if (path.Length == 0)
            {
                previewText.Text = "请先选择备份文件夹。";
                return;
            }
            currentPreview = null;
            stage.IsEnabled = false;
            SetBusy(true);
            previewText.Text = "正在校验备份并比较当前资料…";
            try
            {
                var policy = replace.IsChecked == true
                    ? LibraryImportConflictPolicy.PreferBackup : LibraryImportConflictPolicy.KeepCurrent;
                var choices = new Dictionary<string, string>(manualMapping, StringComparer.OrdinalIgnoreCase);
                currentPreview = await Task.Run(() => LibraryBackupService.CreateImportPreview(
                    DataDir, path, selected, policy, choices, skipUnindexed.IsChecked == true));
                previewText.Text = currentPreview.ConfirmationText
                    + (!currentPreview.MappingComplete ? "\n\n请先解决上述视频匹配问题。"
                        : currentPreview.HasChanges ? "" : "\n\n所选范围没有需要导入的变化。");
                mappingChoices.Children.Clear();
                foreach (var row in currentPreview.MappingRows.Where(row => row.ExactCandidates.Count > 1
                             && !manualMapping.ContainsKey(row.OldVideoId)).Take(100))
                {
                    mappingChoices.Children.Add(new TextBlock
                    {
                        Text = row.RelativePath + "：选择目标视频", TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 3)
                    });
                    var selector = new System.Windows.Controls.ComboBox { Height = 36, Tag = row.OldVideoId,
                        ItemsSource = row.ExactCandidates, Margin = new Thickness(0, 0, 0, 5) };
                    selector.SelectionChanged += (_, _) =>
                    {
                        if (selector.SelectedItem is string chosen)
                            manualMapping[row.OldVideoId] = chosen;
                        currentPreview = null;
                        stage.IsEnabled = false;
                        previewText.Text = "人工选择已更新，请重新预览确认全部匹配和资料差异。";
                    };
                    mappingChoices.Children.Add(selector);
                }
                if (currentPreview.MappingRows.Any(row => row.Status.Contains("禁止导入", StringComparison.Ordinal)))
                    mappingChoices.Children.Add(ImportHint("有视频找不到相同内容，或同名文件内容不同；请核对目标视频后刷新索引。当前不会导入这些旧编号。"));
            }
            catch (Exception ex) { previewText.Text = "预览失败：" + ex.Message; }
            finally { SetBusy(false); }
        };
        stage.Click += async (_, _) =>
        {
            var selectedPreview = currentPreview;
            if (selectedPreview is null || !selectedPreview.HasChanges) return;
            if (MessageBox.Show(dialog, selectedPreview.ConfirmationText + "\n\n确认排定这次导入吗？",
                    "确认选择性导入", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SetBusy(true);
            previewText.Text = "正在再次校验并排定离线导入…";
            try
            {
                await Task.Run(() => LibraryBackupService.StageImport(DataDir, selectedPreview));
                BackupInfo.Text = "选择性导入已排定。关闭所有映格窗口并重新打开，即会在载入资料前执行；此前可点击“取消待导入”。";
                SetBusy(false);
                dialog.DialogResult = true;
            }
            catch (Exception ex)
            {
                currentPreview = null;
                previewText.Text = "排定失败：" + ex.Message + "\n请重新预览后再试。";
            }
            finally { if (dialog.IsVisible) SetBusy(false); }
        };
        close.Click += (_, _) =>
        {
            if (exportCancellation is not null) exportCancellation.Cancel();
            else dialog.DialogResult = false;
        };
        dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
        return dialog;
    }

    internal bool VerifyBackupImportUiForDiagnostics()
    {
        if (SelectiveBackupImportButton.Content as string != "选择性导入备份"
            || CancelScheduledImportButton.Content as string != "取消待导入") return false;
        var dialog = BuildSelectiveBackupImportDialog();
        var controls = ImportLogicalChildren(dialog.Content as DependencyObject).ToArray();
        var offeredScopes = controls.OfType<CheckBox>()
            .Where(option => option.Tag is LibraryImportScope)
            .Select(option => (LibraryImportScope)option.Tag)
            .ToHashSet();
        var expectedScopes = new[]
        {
            LibraryImportScope.Metadata, LibraryImportScope.Categories, LibraryImportScope.Tags,
            LibraryImportScope.Ratings, LibraryImportScope.Reviews, LibraryImportScope.Queue,
            LibraryImportScope.SiteLists, LibraryImportScope.RoleProfiles
        };
        var policies = controls.OfType<RadioButton>()
            .Where(option => option.GroupName == "BackupImportConflict").ToArray();
        var commands = controls.OfType<Button>()
            .Select(button => button.Content as string).ToHashSet();
        return expectedScopes.All(offeredScopes.Contains) && offeredScopes.Count == expectedScopes.Length
            && policies.Length == 2 && policies[0].IsChecked == true
            && commands.Contains("预览差异") && commands.Contains("确认并排定")
            && commands.Contains("取消") && commands.Contains("选择文件夹…");
    }

    static IEnumerable<DependencyObject> ImportLogicalChildren(DependencyObject? parent)
    {
        if (parent is null) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in ImportLogicalChildren(child)) yield return descendant;
        }
    }

    void CancelScheduledBackupImport(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!LibraryBackupService.HasPendingImport(DataDir))
            {
                BackupInfo.Text = "当前没有待执行的选择性导入。";
                return;
            }
            if (MessageBox.Show(this, "取消待执行的选择性导入？当前资料不会改变。",
                    "取消待导入", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            LibraryBackupService.CancelPendingImport(DataDir);
            BackupInfo.Text = "已取消待导入任务；当前资料未修改。";
        }
        catch (Exception ex) { BackupInfo.Text = "取消待导入失败：" + ex.Message; }
    }

    static TextBlock ImportHint(string text) => new()
    {
        Text = text, Foreground = new SolidColorBrush(Color.FromRgb(109, 126, 153)),
        FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5)
    };

    Button ImportButton(string label, bool primary = false) => new()
    {
        Content = label,
        Style = (Style)FindResource(primary ? "Primary" : "Action"),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        Margin = new Thickness(8, 0, 0, 0)
    };
}
