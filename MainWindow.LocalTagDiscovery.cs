using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using MessageBox = YinggeVideoLibrary.ThemeMessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    bool localTagDiscoveryRunning;
    sealed class LocalTagPreviewRow
    {
        public bool Selected { get; set; } = true;
        public required string VideoId { get; init; }
        public required string VideoTitle { get; init; }
        public required string Tags { get; init; }
        public required string Evidence { get; init; }
        public required string[] TagValues { get; init; }
    }

    async void SuggestLocalTags(object sender, RoutedEventArgs e)
    {
        if (batchOperationInProgress || localTagDiscoveryRunning) return;
        var selected = videos.Where(video => selectedVideoIds.Contains(video.Id)).ToArray();
        if (selected.Length == 0) return;
        localTagDiscoveryRunning = true;
        LocalTagAction.IsEnabled = false;
        LocalTagAction.Content = "正在查找本地标签…";
        try
        {
            var vocabulary = videos.SelectMany(video => video.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var inputs = selected.Select(video =>
            {
                metadata.TryGetValue(video.Id, out var record);
                return new LocalTagDiscovery.Input(video.Id, video.DisplayTitle, video.Folder,
                    video.Tags.ToArray(), record?.Genres?.ToArray() ?? [],
                    record?.Keywords?.ToArray() ?? [],
                    record is { DetailFetched: true, DetailIdentityVerified: true });
            }).ToArray();
            var suggestions = await Task.Run(() => LocalTagDiscovery.Build(inputs, vocabulary));
            if (suggestions.Count == 0)
            {
                MessageBox.Show(this, "没有找到能用现有标签和明确文字依据复用的标签；没有修改视频。",
                    "本地标签建议", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var rows = suggestions.Select(item => new LocalTagPreviewRow
            {
                VideoId = item.VideoId, VideoTitle = item.Title,
                Tags = string.Join("、", item.Tags), Evidence = item.Evidence,
                TagValues = item.Tags
            }).ToArray();
            if (!ConfirmLocalTagSuggestions(rows)) return;
            var chosen = rows.Where(row => row.Selected)
                .ToDictionary(row => row.VideoId, row => row.TagValues, StringComparer.OrdinalIgnoreCase);
            var targets = selected.Where(video => chosen.ContainsKey(video.Id)).ToArray();
            if (targets.Length == 0) return;
            var autoAmateur = config.AutoAmateurTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            await ExecuteBatchOperationAsync(targets, BatchChangeKind.Tags, "添加本地标签建议", video =>
            {
                foreach (var tag in chosen[video.Id])
                    if (!video.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase)) video.Tags.Add(tag);
                video.RefreshTags();
                config.Tags[video.Id] = video.Tags.ToList();
            }, autoAmateur: autoAmateur);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "本地标签查找失败：" + ex.Message, "本地标签建议",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            localTagDiscoveryRunning = false;
            LocalTagAction.Content = "本地标签建议";
            UpdateSelectionToolbar();
        }
    }

    bool ConfirmLocalTagSuggestions(LocalTagPreviewRow[] rows)
    {
        var dialog = new Window { Title = "预览本地标签建议", Owner = this,
            Width = 950, Height = 620, MinWidth = 620, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = UiTheme.SurfaceBrush, Foreground = UiTheme.TextPrimaryBrush };
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = $"找到 {rows.Length:N0} 部有本地标签建议的视频。仅勾选并确认后添加；不会覆盖原标签，也不会联网。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Foreground = UiTheme.TextPrimaryBrush };
        layout.Children.Add(heading);
        var table = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false,
            IsReadOnly = false, CanUserAddRows = false, EnableRowVirtualization = true,
            EnableColumnVirtualization = true, Background = UiTheme.SurfaceBrush,
            RowBackground = UiTheme.SurfaceBrush, AlternatingRowBackground = UiTheme.SurfaceBrush,
            Foreground = UiTheme.TextPrimaryBrush };
        table.Columns.Add(new DataGridCheckBoxColumn { Header = "添加", Width = 55,
            Binding = new Binding(nameof(LocalTagPreviewRow.Selected)) { Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        table.Columns.Add(new DataGridTextColumn { Header = "视频", Width = new DataGridLength(2, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(LocalTagPreviewRow.VideoTitle)), IsReadOnly = true });
        table.Columns.Add(new DataGridTextColumn { Header = "建议标签", Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(LocalTagPreviewRow.Tags)), IsReadOnly = true });
        table.Columns.Add(new DataGridTextColumn { Header = "依据", Width = new DataGridLength(2, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(LocalTagPreviewRow.Evidence)), IsReadOnly = true });
        Grid.SetRow(table, 1); layout.Children.Add(table);
        var footer = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var confirm = new Button { Content = "添加勾选标签", MinWidth = 120,
            Background = UiTheme.AccentBrush, Foreground = UiTheme.AccentForegroundBrush };
        confirm.Click += (_, _) => { table.CommitEdit(); table.CommitEdit(); dialog.DialogResult = true; };
        footer.Children.Add(cancel); footer.Children.Add(confirm);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        dialog.Content = layout;
        return dialog.ShowDialog() == true;
    }
}
