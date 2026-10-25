using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    sealed record TagBrowserEntry(string Name, int Count)
    {
        public string Display => $"#{Name}    {Count:N0} 部视频";
    }

    string? activeTagFilter;
    Window? tagBrowserWindow;

    void UpdateTagFilterControls()
    {
        if (BrowseTagsAction is null || ClearTagFilterAction is null) return;
        BrowseTagsAction.Content = activeTagFilter is null
            ? "🏷 浏览标签"
            : "🏷 标签：" + (activeTagFilter.Length <= 12 ? activeTagFilter : activeTagFilter[..12] + "…");
        BrowseTagsAction.ToolTip = activeTagFilter is null
            ? "查看全库标签和每个标签的视频数量；选择后筛选视频"
            : $"当前按 #{activeTagFilter} 筛选 · 点击查看其他标签";
        ClearTagFilterAction.Visibility = activeTagFilter is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void ApplyTagFilter(string tag)
    {
        activeTagFilter = tag;
        // A tag selected from the whole-library browser should show its full
        // result set, rather than inheriting an unrelated title search.
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        if (view != "all") ShowView("all"); else RenderCards();
        if (LibraryScroll.Visibility == Visibility.Visible) LibraryScroll.ScrollToTop();
        Log($"正在查看标签 #{tag} 的视频");
    }

    void ClearTagFilter(object sender, RoutedEventArgs e)
    {
        if (activeTagFilter is null) return;
        activeTagFilter = null;
        RenderCards();
        if (LibraryScroll.Visibility == Visibility.Visible) LibraryScroll.ScrollToTop();
    }

    static List<TagBrowserEntry> CountLibraryTags(string[][] videoTags)
    {
        var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var tags in videoTags)
            foreach (var tag in tags.Distinct(StringComparer.CurrentCultureIgnoreCase))
                if (!string.IsNullOrWhiteSpace(tag)) counts[tag] = counts.GetValueOrDefault(tag) + 1;
        return counts.Select(pair => new TagBrowserEntry(pair.Key, pair.Value))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    async void BrowseAllTags(object sender, RoutedEventArgs e)
    {
        if (tagBrowserWindow is { IsVisible: true }) { tagBrowserWindow.Activate(); return; }

        var browser = new Window
        {
            Title = "浏览标签 · 映格视频库",
            Owner = this,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 560,
            Height = 620,
            MinWidth = 420,
            MinHeight = 380,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        tagBrowserWindow = browser;
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock { Text = "全库标签", FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(38, 51, 71)) };
        Grid.SetRow(heading, 0); layout.Children.Add(heading);
        var summary = new TextBlock { Text = "正在整理标签…", Margin = new Thickness(0, 6, 0, 18), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(111, 128, 157)) };
        Grid.SetRow(summary, 1); layout.Children.Add(summary);
        var search = new TextBox { Height = 40, Padding = new Thickness(12, 8, 12, 7), Margin = new Thickness(0, 0, 0, 12), FontSize = 13, BorderBrush = new SolidColorBrush(Color.FromRgb(211, 222, 240)), ToolTip = "输入标签名称筛选" };
        Grid.SetRow(search, 2); layout.Children.Add(search);
        var list = new ListBox { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(221, 229, 242)), BorderThickness = new Thickness(1), Padding = new Thickness(3), DisplayMemberPath = nameof(TagBrowserEntry.Display), FontSize = 14 };
        list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        list.SetValue(ScrollViewer.CanContentScrollProperty, true);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 9, 12, 9)));
        itemStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 1, 0, 1)));
        list.ItemContainerStyle = itemStyle;
        Grid.SetRow(list, 3); layout.Children.Add(list);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var clear = TagDialogButton("清除标签筛选", false);
        clear.Click += (_, _) => ClearTagFilter(clear, new RoutedEventArgs());
        var choose = TagDialogButton("查看匹配视频", true);
        choose.IsEnabled = false;
        var close = TagDialogButton("关闭", false);
        close.Click += (_, _) => browser.Close();
        actions.Children.Add(clear); actions.Children.Add(choose); actions.Children.Add(close);
        Grid.SetRow(actions, 4); layout.Children.Add(actions);
        browser.Content = layout;

        var allTags = new List<TagBrowserEntry>();
        var isLoading = true;
        var searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        void RefreshResults()
        {
            if (isLoading) return;
            var term = search.Text.Trim();
            var filtered = term.Length == 0 ? allTags : allTags.Where(entry => entry.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToList();
            list.ItemsSource = filtered;
            summary.Text = allTags.Count == 0
                ? "尚无标签。可以在视频卡片上点击“查看标签”添加。"
                : $"{allTags.Count:N0} 个标签 · {filtered.Count:N0} 个匹配 · 双击标签直接查看视频";
        }
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshResults(); };
        search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        list.SelectionChanged += (_, _) => choose.IsEnabled = list.SelectedItem is TagBrowserEntry;
        void ChooseTag()
        {
            if (list.SelectedItem is not TagBrowserEntry selected) return;
            browser.Close();
            ApplyTagFilter(selected.Name);
        }
        choose.Click += (_, _) => ChooseTag();
        list.MouseDoubleClick += (_, _) => ChooseTag();
        list.KeyDown += (_, key) => { if (key.Key == Key.Enter) { ChooseTag(); key.Handled = true; } };
        browser.Closed += (_, _) => { searchTimer.Stop(); if (ReferenceEquals(tagBrowserWindow, browser)) tagBrowserWindow = null; };
        browser.Show();

        // Only the explicit browser action takes this snapshot. Aggregation
        // runs off the UI thread and the list virtualizes its thousands of rows.
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (!browser.IsVisible) return;
        try
        {
            var snapshot = videos.Where(video => video.Tags.Count > 0).Select(video => video.Tags.ToArray()).ToArray();
            allTags = await Task.Run(() => CountLibraryTags(snapshot));
            isLoading = false;
            if (browser.IsVisible) RefreshResults();
        }
        catch (Exception ex)
        {
            isLoading = false;
            if (browser.IsVisible) summary.Text = "读取标签失败，请关闭后重试。";
            Log("标签浏览失败：" + ex.Message);
        }
    }

    void ShowVideoTags(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VideoItem video) return;
        string? chosenTag = null;
        var editRequested = false;
        var dialog = new Window
        {
            Title = "查看视频标签 · 映格视频库",
            Owner = this,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 480,
            Height = 340,
            MinWidth = 380,
            MinHeight = 280,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        var layout = new Grid { Margin = new Thickness(23) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock { Text = video.DisplayTitle, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(38, 51, 71)), ToolTip = video.DisplayTitle };
        Grid.SetRow(title, 0); layout.Children.Add(title);
        var hint = new TextBlock { Text = $"{video.Tags.Count} 个标签 · 点击标签查看所有匹配视频", Margin = new Thickness(0, 5, 0, 15), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(111, 128, 157)) };
        Grid.SetRow(hint, 1); layout.Children.Add(hint);
        var surface = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(221, 229, 242)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14) };
        var flow = new WrapPanel();
        if (video.Tags.Count == 0)
            flow.Children.Add(new TextBlock { Text = "这部视频还没有标签。可以点击下方“编辑标签”添加。", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(111, 128, 157)), Margin = new Thickness(3) });
        else foreach (var tag in video.Tags.Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            var chip = new Button { Content = "#" + tag, Padding = new Thickness(11, 7, 11, 7), Margin = new Thickness(0, 0, 8, 8), Background = new SolidColorBrush(Color.FromRgb(236, 231, 251)), Foreground = new SolidColorBrush(Color.FromRgb(91, 70, 161)), BorderBrush = new SolidColorBrush(Color.FromRgb(220, 209, 248)), Cursor = Cursors.Hand, ToolTip = "查看全库带有这个标签的视频" };
            chip.Click += (_, _) => { chosenTag = tag; dialog.Close(); };
            flow.Children.Add(chip);
        }
        surface.Child = new ScrollViewer { Content = flow, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(surface, 2); layout.Children.Add(surface);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var edit = TagDialogButton("编辑标签", true);
        edit.Click += (_, _) => { editRequested = true; dialog.Close(); };
        var close = TagDialogButton("关闭", false);
        close.Click += (_, _) => dialog.Close();
        actions.Children.Add(edit); actions.Children.Add(close);
        Grid.SetRow(actions, 3); layout.Children.Add(actions);
        dialog.Content = layout;
        dialog.ShowDialog();
        if (chosenTag is not null) ApplyTagFilter(chosenTag);
        else if (editRequested) EditTagsForVideo(video);
    }

    static Button TagDialogButton(string label, bool primary)
    {
        var accent = new SolidColorBrush(Color.FromRgb(76, 109, 215));
        return new Button
        {
            Content = label,
            MinWidth = label.Length > 4 ? 104 : 76,
            Height = 38,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Background = primary ? accent : Brushes.White,
            Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(70, 83, 102)),
            BorderBrush = primary ? accent : new SolidColorBrush(Color.FromRgb(220, 229, 242)),
            Cursor = Cursors.Hand
        };
    }
}
