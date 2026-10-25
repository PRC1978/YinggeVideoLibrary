using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    Button? relocationSettingsButton;

    sealed class RelocationCloseState
    {
        public bool Busy { get; set; }
        public bool Completed { get; set; }
        public bool CanClose => !Busy || Completed;
    }

    sealed class RelocationDisplayRow(LibraryRelocationService.PreviewRow source)
    {
        public LibraryRelocationService.PreviewRow Source { get; } = source;
        public string RelativePath => Source.Source.RelativePath;
        public string Status => Source.Kind switch
        {
            LibraryRelocationService.MatchKind.Exact => "内容一致",
            LibraryRelocationService.MatchKind.Ambiguous => "多个相同文件",
            LibraryRelocationService.MatchKind.DifferentContent => "同名但内容不同",
            _ => "未找到"
        };
        public string Proposed => Source.Kind switch
        {
            LibraryRelocationService.MatchKind.Exact => Source.ProposedTargetPath ?? "",
            LibraryRelocationService.MatchKind.Ambiguous => $"{Source.ExactContentPaths.Count} 个候选，需要选择",
            LibraryRelocationService.MatchKind.DifferentContent => Source.SameRelativePath ?? "",
            _ => "请手动指定新目录中的视频"
        };
    }

    FrameworkElement BuildLibraryRelocationSettingsEntry()
    {
        var container = new StackPanel { Margin = new Thickness(0, 12, 0, 3) };
        container.Children.Add(new TextBlock
        {
            Text = "视频库搬迁与路径映射", FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Tint("#263852")
        });
        container.Children.Add(new TextBlock
        {
            Text = "更换硬盘或移动文件夹时，先在旧目录可用时建立指纹，再对新目录预览匹配。分类、标签、评分与审核记录会随视频一起映射。",
            Foreground = Tint("#71829B"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 9)
        });
        relocationSettingsButton = new Button
        {
            Content = "迁移视频库…", Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        relocationSettingsButton.Click += (_, _) => OpenLibraryRelocationDialog();
        container.Children.Add(relocationSettingsButton);
        return container;
    }

    void OpenLibraryRelocationDialog()
    {
        var dialog = BuildLibraryRelocationDialog();
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    Window BuildLibraryRelocationDialog()
    {
        var dialog = new Window
        {
            Title = "视频库搬迁与路径映射",
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 1000, Height = 790, MinWidth = 750, MinHeight = 620,
            ShowInTaskbar = false, Background = Tint("#F7F9FD")
        };
        var closeState = new RelocationCloseState();
        dialog.Tag = closeState;
        var shell = new Grid { Margin = new Thickness(22) };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = shell;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock
        {
            Text = "将现有视频库映射到新目录", FontSize = 21,
            FontWeight = FontWeights.SemiBold, Foreground = Tint("#20314C")
        });
        heading.Children.Add(new TextBlock
        {
            Text = "只校验视频内容和调整索引、资料与审核记录，不移动或复制视频。迁移会在下次启动时执行。",
            FontSize = 12, Foreground = Tint("#70819A"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        shell.Children.Add(heading);

        var pathCard = new Border
        {
            Background = System.Windows.Media.Brushes.White, BorderBrush = Tint("#DFE7F3"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(pathCard, 1);
        shell.Children.Add(pathCard);
        var pathLayout = new StackPanel();
        pathCard.Child = pathLayout;
        pathLayout.Children.Add(new TextBlock { Text = "原视频库根目录", FontWeight = FontWeights.SemiBold });
        var oldRow = new Grid { Margin = new Thickness(0, 5, 0, 9) };
        oldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        oldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        oldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var oldRoot = new ComboBox
        {
            Name = "RelocationOldRoot", IsEditable = true, Height = 36,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        foreach (var folder in config.Folders.Where(folder => !string.IsNullOrWhiteSpace(folder))
                     .Distinct(StringComparer.OrdinalIgnoreCase)) oldRoot.Items.Add(folder);
        if (oldRoot.Items.Count > 0) oldRoot.SelectedIndex = 0;
        oldRow.Children.Add(oldRoot);
        var prepare = RelocationButton("建立旧盘指纹");
        prepare.ToolTip = "移动视频前执行。旧盘之后不可用时，预览可使用已保存的指纹。";
        Grid.SetColumn(prepare, 1); oldRow.Children.Add(prepare);
        var browseOld = RelocationButton("选择目录…");
        Grid.SetColumn(browseOld, 2); oldRow.Children.Add(browseOld);
        pathLayout.Children.Add(oldRow);
        pathLayout.Children.Add(new TextBlock { Text = "新视频库根目录", FontWeight = FontWeights.SemiBold });
        var newRow = new Grid { Margin = new Thickness(0, 5, 0, 7) };
        newRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        newRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var newRoot = new TextBox
        {
            Name = "RelocationNewRoot", Height = 36, Padding = new Thickness(9, 6, 9, 6),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        newRow.Children.Add(newRoot);
        var browseNew = RelocationButton("选择目录…");
        Grid.SetColumn(browseNew, 1); newRow.Children.Add(browseNew);
        pathLayout.Children.Add(newRow);
        pathLayout.Children.Add(new TextBlock
        {
            Text = "旧目录不在时，可使用事先建立的指纹。所有目标视频必须已经位于新目录中。",
            FontSize = 11, Foreground = Tint("#8291A7"), TextWrapping = TextWrapping.Wrap
        });
        pathLayout.Children.Add(new TextBlock
        {
            Text = "跨电脑迁移时，请把旧电脑数据目录中的来源指纹文件（library-relocation-source.json）另行复制到新电脑的数据目录；普通备份不包含这份文件。",
            FontSize = 11, Foreground = Tint("#8291A7"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(content, 2); shell.Children.Add(content);
        var summaryRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        content.Children.Add(summaryRow);
        var nextUnresolved = RelocationButton("下一待确认");
        nextUnresolved.IsEnabled = false;
        DockPanel.SetDock(nextUnresolved, Dock.Right);
        summaryRow.Children.Add(nextUnresolved);
        var statusFilter = new ComboBox
        {
            Name = "RelocationStatusFilter", Width = 145, Height = 30,
            Margin = new Thickness(7, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center,
            ItemsSource = new[] { "全部", "需人工", "内容一致", "多个相同文件", "同名但内容不同", "未找到" },
            SelectedIndex = 0
        };
        DockPanel.SetDock(statusFilter, Dock.Right);
        summaryRow.Children.Add(statusFilter);
        var summary = new TextBlock
        {
            Name = "RelocationSummary", Text = "请选择新目录并预览。扫描可能需要一些时间。",
            FontWeight = FontWeights.SemiBold, Foreground = Tint("#425B80"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        summaryRow.Children.Add(summary);

        var grid = new DataGrid
        {
            Name = "RelocationPreviewGrid", AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
            EnableRowVirtualization = true, EnableColumnVirtualization = true,
            BorderBrush = Tint("#DFE7F3"), Background = System.Windows.Media.Brushes.White
        };
        VirtualizingPanel.SetIsVirtualizing(grid, true);
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        grid.Columns.Add(new DataGridTextColumn { Header = "匹配", Binding = new System.Windows.Data.Binding(nameof(RelocationDisplayRow.Status)), Width = 135 });
        grid.Columns.Add(new DataGridTextColumn { Header = "原目录中的视频", Binding = new System.Windows.Data.Binding(nameof(RelocationDisplayRow.RelativePath)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "目标文件 / 提示", Binding = new System.Windows.Data.Binding(nameof(RelocationDisplayRow.Proposed)), Width = new DataGridLength(3, DataGridLengthUnitType.Star) });
        Grid.SetRow(grid, 1); content.Children.Add(grid);

        var decisionCard = new Border
        {
            Background = System.Windows.Media.Brushes.White, BorderBrush = Tint("#DFE7F3"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(13),
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetRow(decisionCard, 2); content.Children.Add(decisionCard);
        var decisionLayout = new StackPanel(); decisionCard.Child = decisionLayout;
        var selectedHint = new TextBlock
        {
            Text = "选择上方一部视频；内容一致的文件将自动映射。",
            Foreground = Tint("#526989"), TextWrapping = TextWrapping.Wrap
        };
        decisionLayout.Children.Add(selectedHint);
        var candidates = new ComboBox
        {
            Name = "RelocationExactCandidates", Height = 32, Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };
        decisionLayout.Children.Add(candidates);
        var choiceRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        choiceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        choiceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var target = new TextBox
        {
            Name = "RelocationDecisionTarget", Height = 32,
            Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "为这一部视频指定新目录中的目标文件"
        };
        choiceRow.Children.Add(target);
        var browseFile = RelocationButton("选择视频…");
        Grid.SetColumn(browseFile, 1); choiceRow.Children.Add(browseFile);
        decisionLayout.Children.Add(choiceRow);
        var acceptDifferent = new CheckBox
        {
            Name = "RelocationAcceptDifferent", Content = "我确认内容不同，允许这个目标视频替代原视频",
            Margin = new Thickness(0, 8, 0, 0), Foreground = Tint("#8A542D")
        };
        decisionLayout.Children.Add(acceptDifferent);
        var saveChoice = RelocationButton("保存本条选择", primary: true);
        saveChoice.Margin = new Thickness(0, 8, 0, 0);
        decisionLayout.Children.Add(saveChoice);
        choiceRow.Visibility = Visibility.Collapsed;
        acceptDifferent.Visibility = Visibility.Collapsed;
        saveChoice.Visibility = Visibility.Collapsed;

        var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 13, 0, 0) };
        Grid.SetRow(footer, 3); shell.Children.Add(footer);
        var close = RelocationButton("关闭"); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        var stage = RelocationButton("确认并排定迁移", primary: true);
        stage.Name = "RelocationStage";
        stage.IsEnabled = false; DockPanel.SetDock(stage, Dock.Right); footer.Children.Add(stage);
        var preview = RelocationButton("预览匹配"); DockPanel.SetDock(preview, Dock.Right); footer.Children.Add(preview);
        var cancelWork = RelocationButton("取消扫描");
        cancelWork.IsEnabled = false; DockPanel.SetDock(cancelWork, Dock.Right); footer.Children.Add(cancelWork);
        var progress = new System.Windows.Controls.ProgressBar
        {
            Name = "RelocationProgress", Height = 7, IsIndeterminate = true,
            Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 20, 0)
        };
        footer.Children.Add(progress);

        LibraryRelocationService.Preview? currentPreview = null;
        RelocationDisplayRow[] rows = [];
        var decisions = new Dictionary<string, LibraryRelocationService.Decision>(StringComparer.OrdinalIgnoreCase);
        CancellationTokenSource? operation = null;
        var loadingChoice = false;

        string OldPath() => oldRoot.Text.Trim();
        string NewPath() => newRoot.Text.Trim();
        int ManualCount() => rows.Count(row => row.Source.Kind != LibraryRelocationService.MatchKind.Exact);
        void RefreshGrid()
        {
            var filtered = statusFilter.SelectedIndex switch
            {
                1 => rows.Where(row => row.Source.Kind != LibraryRelocationService.MatchKind.Exact),
                2 => rows.Where(row => row.Source.Kind == LibraryRelocationService.MatchKind.Exact),
                3 => rows.Where(row => row.Source.Kind == LibraryRelocationService.MatchKind.Ambiguous),
                4 => rows.Where(row => row.Source.Kind == LibraryRelocationService.MatchKind.DifferentContent),
                5 => rows.Where(row => row.Source.Kind == LibraryRelocationService.MatchKind.Missing),
                _ => rows.AsEnumerable()
            };
            grid.ItemsSource = filtered.ToArray();
        }
        void RefreshSummary()
        {
            if (currentPreview is null) return;
            var exact = currentPreview.AutomaticCount;
            var ambiguous = rows.Count(row => row.Source.Kind == LibraryRelocationService.MatchKind.Ambiguous);
            var different = rows.Count(row => row.Source.Kind == LibraryRelocationService.MatchKind.DifferentContent);
            var missing = rows.Count(row => row.Source.Kind == LibraryRelocationService.MatchKind.Missing);
            summary.Text = $"共 {rows.Length:N0} 部：内容一致 {exact:N0}，多个相同文件 {ambiguous:N0}，内容不同 {different:N0}，未找到 {missing:N0}。人工确认 {decisions.Count:N0}/{ManualCount():N0}。";
            stage.IsEnabled = !closeState.Busy && rows.Length > 0 && decisions.Count == ManualCount();
        }
        void ClearPreview()
        {
            currentPreview = null;
            rows = [];
            decisions.Clear();
            grid.ItemsSource = null;
            nextUnresolved.IsEnabled = false;
            stage.IsEnabled = false;
            selectedHint.Text = "目录已更改，请重新预览匹配。";
            choiceRow.Visibility = Visibility.Collapsed;
            candidates.Visibility = Visibility.Collapsed;
            acceptDifferent.Visibility = Visibility.Collapsed;
            saveChoice.Visibility = Visibility.Collapsed;
        }
        void SetBusy(bool value)
        {
            closeState.Busy = value;
            oldRoot.IsEnabled = !value; newRoot.IsEnabled = !value;
            browseOld.IsEnabled = !value; browseNew.IsEnabled = !value;
            prepare.IsEnabled = !value; preview.IsEnabled = !value;
            grid.IsEnabled = !value; choiceRow.IsEnabled = !value;
            saveChoice.IsEnabled = !value; candidates.IsEnabled = !value;
            acceptDifferent.IsEnabled = !value; close.IsEnabled = !value;
            cancelWork.IsEnabled = value; progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            stage.IsEnabled = !value && currentPreview is not null && rows.Length > 0
                && decisions.Count == ManualCount();
            nextUnresolved.IsEnabled = !value && currentPreview is not null
                && decisions.Count < ManualCount();
        }
        void InvalidateChoice()
        {
            if (loadingChoice || grid.SelectedItem is not RelocationDisplayRow selected
                || selected.Source.Kind == LibraryRelocationService.MatchKind.Exact) return;
            decisions.Remove(selected.Source.Source.OldVideoId);
            RefreshSummary();
        }
        void LoadChoice()
        {
            loadingChoice = true;
            try
            {
                if (grid.SelectedItem is not RelocationDisplayRow row)
                {
                    selectedHint.Text = "选择上方一部视频；内容一致的文件将自动映射。";
                    choiceRow.Visibility = Visibility.Collapsed;
                    candidates.Visibility = Visibility.Collapsed;
                    acceptDifferent.Visibility = Visibility.Collapsed;
                    saveChoice.Visibility = Visibility.Collapsed;
                    return;
                }
                if (row.Source.Kind == LibraryRelocationService.MatchKind.Exact)
                {
                    selectedHint.Text = "内容指纹一致，自动映射到：" + row.Source.ProposedTargetPath;
                    choiceRow.Visibility = Visibility.Collapsed;
                    candidates.Visibility = Visibility.Collapsed;
                    acceptDifferent.Visibility = Visibility.Collapsed;
                    saveChoice.Visibility = Visibility.Collapsed;
                    return;
                }
                selectedHint.Text = row.Source.Kind switch
                {
                    LibraryRelocationService.MatchKind.Ambiguous => "多个目标文件内容相同，请明确选择一个。",
                    LibraryRelocationService.MatchKind.DifferentContent => "同一相对路径存在不同内容。确认替代前，请检查目标文件。",
                    _ => "未找到内容一致的文件。请在新目录中指定目标视频，内容不同则勾选允许替代。"
                };
                choiceRow.Visibility = Visibility.Visible;
                saveChoice.Visibility = Visibility.Visible;
                candidates.Visibility = row.Source.Kind == LibraryRelocationService.MatchKind.Ambiguous
                    ? Visibility.Visible : Visibility.Collapsed;
                candidates.ItemsSource = row.Source.ExactContentPaths;
                acceptDifferent.Visibility = row.Source.Kind == LibraryRelocationService.MatchKind.Ambiguous
                    ? Visibility.Collapsed : Visibility.Visible;
                decisions.TryGetValue(row.Source.Source.OldVideoId, out var saved);
                var suggested = row.Source.Kind == LibraryRelocationService.MatchKind.DifferentContent
                    ? row.Source.SameRelativePath : null;
                target.Text = saved?.TargetPath ?? suggested ?? "";
                target.IsReadOnly = row.Source.Kind == LibraryRelocationService.MatchKind.Ambiguous;
                browseFile.IsEnabled = row.Source.Kind != LibraryRelocationService.MatchKind.Ambiguous;
                candidates.SelectedItem = saved is null ? null : row.Source.ExactContentPaths.FirstOrDefault(path =>
                    path.Equals(saved.TargetPath, StringComparison.OrdinalIgnoreCase));
                acceptDifferent.IsChecked = saved?.AcceptDifferentContent == true;
            }
            finally { loadingChoice = false; }
        }

        oldRoot.SelectionChanged += (_, _) => ClearPreview();
        oldRoot.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ClearPreview()));
        newRoot.TextChanged += (_, _) => ClearPreview();
        target.TextChanged += (_, _) => InvalidateChoice();
        acceptDifferent.Checked += (_, _) => InvalidateChoice();
        acceptDifferent.Unchecked += (_, _) => InvalidateChoice();
        candidates.SelectionChanged += (_, _) =>
        {
            if (loadingChoice || candidates.SelectedItem is not string selected) return;
            target.Text = selected;
        };
        grid.SelectionChanged += (_, _) => LoadChoice();
        statusFilter.SelectionChanged += (_, _) => RefreshGrid();
        nextUnresolved.Click += (_, _) =>
        {
            var row = rows.FirstOrDefault(item => item.Source.Kind != LibraryRelocationService.MatchKind.Exact
                && !decisions.ContainsKey(item.Source.Source.OldVideoId));
            if (row is null) return;
            if (statusFilter.SelectedIndex is 2 or 3 or 4 or 5) statusFilter.SelectedIndex = 1;
            grid.SelectedItem = row;
            grid.ScrollIntoView(row);
        };
        browseOld.Click += (_, _) =>
        {
            var path = PickRelocationFolder("选择原视频库根目录", OldPath());
            if (path is not null) oldRoot.Text = path;
        };
        browseNew.Click += (_, _) =>
        {
            var path = PickRelocationFolder("选择新视频库根目录", NewPath());
            if (path is not null) newRoot.Text = path;
        };
        browseFile.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择新目录中的目标视频", CheckFileExists = true,
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.flv;*.ts;*.mts;*.m2ts;*.webm;*.mpg;*.mpeg;*.vob|所有文件|*.*"
            };
            if (Directory.Exists(NewPath())) picker.InitialDirectory = NewPath();
            if (picker.ShowDialog(dialog) == true) target.Text = picker.FileName;
        };
        saveChoice.Click += (_, _) =>
        {
            if (grid.SelectedItem is not RelocationDisplayRow row || currentPreview is null) return;
            var selectedPath = target.Text.Trim();
            try
            {
                var full = Path.GetFullPath(selectedPath);
                var root = Path.GetFullPath(currentPreview.TargetRoot);
                var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                    throw new InvalidOperationException("请选择新目录内存在的视频文件。");
                if (row.Source.Kind == LibraryRelocationService.MatchKind.Ambiguous
                    && !row.Source.ExactContentPaths.Contains(full, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请从内容一致的候选文件中选择一个。");
                if (row.Source.Kind == LibraryRelocationService.MatchKind.DifferentContent
                    && acceptDifferent.IsChecked != true)
                    throw new InvalidOperationException("该目标与原视频内容不同，请明确勾选允许替代。");
                decisions[row.Source.Source.OldVideoId] = new LibraryRelocationService.Decision(
                    row.Source.Source.OldVideoId, full, acceptDifferent.IsChecked == true);
                selectedHint.Text = "已保存本条选择。";
                RefreshSummary();
            }
            catch (Exception ex) { selectedHint.Text = "无法保存选择：" + ex.Message; }
        };
        cancelWork.Click += (_, _) =>
        {
            operation?.Cancel();
            cancelWork.IsEnabled = false;
            summary.Text = "正在停止扫描，请稍候…";
        };
        prepare.Click += async (_, _) =>
        {
            var source = OldPath();
            if (source.Length == 0) { summary.Text = "请先选择原视频库根目录。"; return; }
            SetBusy(true); operation = new CancellationTokenSource();
            summary.Text = "正在后台读取旧视频内容并建立指纹；旧目录必须仍可访问…";
            try
            {
                var result = await Task.Run(() => LibraryRelocationService.PrepareSourceManifest(
                    DataDir, source, operation.Token));
                summary.Text = $"已保存 {result.Entries.Count:N0} 部视频的来源指纹。现在可以移动硬盘或视频文件，再预览新目录。";
            }
            catch (OperationCanceledException) { summary.Text = "已取消建立指纹；当前视频库未改变。"; }
            catch (Exception ex) { summary.Text = "建立指纹失败：" + ex.Message; }
            finally { operation.Dispose(); operation = null; SetBusy(false); }
        };
        preview.Click += async (_, _) =>
        {
            var source = OldPath(); var destination = NewPath();
            if (source.Length == 0 || destination.Length == 0)
            {
                summary.Text = "请选择原目录和新目录。"; return;
            }
            ClearPreview(); SetBusy(true); operation = new CancellationTokenSource();
            summary.Text = "正在后台扫描并核对新目录的视频内容；不会写入当前资料…";
            try
            {
                var result = await Task.Run(() => LibraryRelocationService.CreatePreview(
                    DataDir, source, destination, operation.Token));
                rows = await Task.Run(() => result.Rows.Select(row => new RelocationDisplayRow(row)).ToArray());
                currentPreview = result;
                statusFilter.SelectedIndex = ManualCount() > 0 ? 1 : 0;
                RefreshGrid();
                var first = rows.FirstOrDefault(row =>
                    row.Source.Kind != LibraryRelocationService.MatchKind.Exact) ?? rows.FirstOrDefault();
                grid.SelectedItem = first;
                if (first is not null) grid.ScrollIntoView(first);
                RefreshSummary();
            }
            catch (OperationCanceledException) { summary.Text = "已取消预览；当前视频库未改变。"; }
            catch (Exception ex) { summary.Text = "预览失败：" + ex.Message; }
            finally { operation.Dispose(); operation = null; SetBusy(false); }
        };
        stage.Click += async (_, _) =>
        {
            var selectedPreview = currentPreview;
            if (selectedPreview is null || decisions.Count != ManualCount()) return;
            var manualDifferent = decisions.Values.Count(decision => decision.AcceptDifferentContent);
            var explanation = $"将 {selectedPreview.Rows.Count:N0} 部视频映射到新目录，其中 "
                + $"{selectedPreview.AutomaticCount:N0} 部自动确认、{decisions.Count:N0} 部人工指定、{manualDifferent:N0} 部允许不同内容替代。\n\n"
                + "此操作只排定下次启动时切换资料；不会移动或复制视频。确认继续？";
            if (MessageBox.Show(dialog, explanation, "确认视频库迁移", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true); operation = new CancellationTokenSource();
            summary.Text = "正在后台重新校验并准备安全恢复包；请保持软件开启…";
            // The final package-and-schedule step has a small non-cancellable
            // section. Do not offer a cancel button that could imply rollback.
            cancelWork.IsEnabled = false;
            try
            {
                var chosen = decisions.Values.ToArray();
                var result = await Task.Run(() => LibraryRelocationService.StageConfirmedRelocation(
                    selectedPreview, chosen, operation.Token));
                summary.Text = result.Message;
                MessageBox.Show(dialog, result.Message + "\n视频原文件未移动或复制。", "迁移已排定",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                closeState.Completed = true;
                SetBusy(false);
                dialog.DialogResult = true;
            }
            catch (OperationCanceledException) { summary.Text = "已取消排定迁移；当前视频库未改变。"; }
            catch (Exception ex)
            {
                summary.Text = "排定失败：" + ex.Message
                    + "\n请核对本条选择；若目录或视频库已变化，请重新预览。";
            }
            finally
            {
                operation.Dispose(); operation = null;
                if (dialog.IsVisible) SetBusy(false);
            }
        };
        close.Click += (_, _) => dialog.DialogResult = false;
        dialog.Closing += (_, e) => { if (!closeState.CanClose) e.Cancel = true; };
        return dialog;
    }

    static string? PickRelocationFolder(string title, string current)
    {
        using var picker = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = false
        };
        if (Directory.Exists(current)) picker.SelectedPath = current;
        return picker.ShowDialog() == System.Windows.Forms.DialogResult.OK ? picker.SelectedPath : null;
    }

    Button RelocationButton(string label, bool primary = false) => new()
    {
        Content = label, Style = (Style)FindResource(primary ? "Primary" : "Action"),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(7, 0, 0, 0)
    };

    internal bool VerifyLibraryRelocationUiForDiagnostics()
    {
        if (relocationSettingsButton?.Content as string != "迁移视频库…"
            || !settingsSections.TryGetValue("data", out var section)
            || section.Content is not StackPanel dataBody
            || !dataBody.Children.Contains(relocationSettingsButton.Parent as UIElement)) return false;
        var dialog = BuildLibraryRelocationDialog();
        var children = ImportLogicalChildren(dialog.Content as DependencyObject).ToArray();
        var names = children.OfType<FrameworkElement>().Select(element => element.Name).ToHashSet();
        var commands = children.OfType<Button>().Select(button => button.Content as string).ToHashSet();
        var controlsPresent = names.Contains("RelocationOldRoot") && names.Contains("RelocationNewRoot")
            && names.Contains("RelocationPreviewGrid") && names.Contains("RelocationExactCandidates")
            && names.Contains("RelocationAcceptDifferent") && names.Contains("RelocationStage")
            && names.Contains("RelocationProgress") && commands.Contains("建立旧盘指纹")
            && names.Contains("RelocationStatusFilter")
            && commands.Contains("预览匹配") && commands.Contains("取消扫描")
            && commands.Contains("保存本条选择") && commands.Contains("确认并排定迁移")
            && commands.Contains("下一待确认")
            && children.OfType<DataGrid>().Single().EnableRowVirtualization;
        if (!controlsPresent || dialog.Tag is not RelocationCloseState closeState) return false;
        closeState.Busy = true;
        if (closeState.CanClose) return false;
        closeState.Completed = true;
        if (!closeState.CanClose) return false;
        // Exercise the actual Closing handler without putting the app in front.
        dialog.Owner = this;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -30000; dialog.Top = -30000;
        dialog.Opacity = 0; dialog.ShowActivated = false;
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() => dialog.DialogResult = true));
        return dialog.ShowDialog() == true;
    }
}
