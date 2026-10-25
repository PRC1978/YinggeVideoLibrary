using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    internal sealed record BatchCategoryPickerUi(Window Dialog, TextBox Search, ListBox Categories, TextBlock ResultCount,
        Button Add, Button Replace, Button Clear);

    // A ListBox with a fixed viewport realizes only visible rows. The former
    // context menu created two MenuItems for every category before opening.
    internal BatchCategoryPickerUi BuildBatchCategoryPicker(IReadOnlyList<string> targets, int selectedCount, Action<string, bool> choose,
        string? description = null, bool moveOnly = false)
    {
        var dialog = new Window
        {
            Title = moveOnly ? "移动到分类" : selectedCount == 1 ? "视频分类" : "批量分类",
            Owner = this,
            Width = 640,
            Height = 620,
            MinWidth = 480,
            MinHeight = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        var layout = new Grid { Margin = new Thickness(22) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = "选择目标分类", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(38, 51, 71)) });
        heading.Children.Add(new TextBlock { Text = description ?? $"已选 {selectedCount:N0} 部视频 · 可添加到多个分类，也可替换原分类", Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(117, 131, 153)), TextTrimming = TextTrimming.CharacterEllipsis });
        layout.Children.Add(heading);

        var search = new TextBox { Height = 40, Padding = new Thickness(12, 8, 12, 8), BorderBrush = new SolidColorBrush(Color.FromRgb(213, 223, 239)), Background = Brushes.White, ToolTip = moveOnly ? "输入分类名称或路径筛选；按 Enter 移动到选中分类" : "输入分类名称或路径筛选；按 Enter 添加到选中分类" };
        Grid.SetRow(search, 1);
        layout.Children.Add(search);

        var count = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(117, 131, 153)), FontSize = 12, Margin = new Thickness(2, 8, 0, 8) };
        Grid.SetRow(count, 2);
        layout.Children.Add(count);

        var categories = new ListBox { BorderBrush = new SolidColorBrush(Color.FromRgb(213, 223, 239)), BorderThickness = new Thickness(1), Background = Brushes.White };
        VirtualizingPanel.SetIsVirtualizing(categories, true);
        VirtualizingPanel.SetVirtualizationMode(categories, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(categories, true);
        ScrollViewer.SetVerticalScrollBarVisibility(categories, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(categories, ScrollBarVisibility.Disabled);
        var rowStyle = new Style(typeof(ListBoxItem));
        rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
        rowStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 36.0));
        rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Stretch));
        categories.ItemContainerStyle = rowStyle;
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("."));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        categories.ItemTemplate = new DataTemplate { VisualTree = text };
        Grid.SetRow(categories, 3);
        layout.Children.Add(categories);

        void RefreshResults()
        {
            var terms = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var results = terms.Length == 0 ? targets.ToArray() : targets.Where(path => terms.All(term => path.Contains(term, StringComparison.CurrentCultureIgnoreCase))).ToArray();
            categories.ItemsSource = results;
            count.Text = $"显示 {results.Length:N0} / {targets.Count:N0} 个分类";
            if (results.Length > 0) categories.SelectedIndex = 0;
        }

        Button Action(string label, bool primary = false)
            => new() { Content = label, Style = (Style)FindResource(primary ? "Primary" : "Action"), Margin = new Thickness(0, 0, 9, 7) };
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var add = Action(moveOnly ? "移动到分类" : "添加到分类", true);
        var replace = Action("替换原分类");
        var create = Action(moveOnly ? "＋ 新建并移动" : "＋ 新建并添加");
        var createReplace = Action("＋ 新建并替换");
        var clear = Action("清除分类");
        var cancel = Action("取消");
        add.Click += (_, _) => { if (categories.SelectedItem is string target) choose(target, false); };
        replace.Click += (_, _) => { if (categories.SelectedItem is string target) choose(target, true); };
        create.Click += (_, _) => { var target = CreateCategory(dialog); if (!string.IsNullOrWhiteSpace(target)) choose(target, false); };
        createReplace.Click += (_, _) => { var target = CreateCategory(dialog); if (!string.IsNullOrWhiteSpace(target)) choose(target, true); };
        clear.Click += (_, _) => choose("未分类", true);
        cancel.Click += (_, _) => dialog.DialogResult = false;
        categories.SelectionChanged += (_, _) => add.IsEnabled = replace.IsEnabled = categories.SelectedItem is string;
        categories.MouseDoubleClick += (_, _) => { if (categories.SelectedItem is string target) choose(target, false); };
        search.TextChanged += (_, _) => RefreshResults();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && categories.SelectedItem is string target) { choose(target, false); e.Handled = true; }
            else if (e.Key == Key.Escape) { dialog.DialogResult = false; e.Handled = true; }
        };
        foreach (var button in moveOnly ? new[] { add, create, cancel } : new[] { add, replace, create, createReplace, clear, cancel }) actions.Children.Add(button);
        Grid.SetRow(actions, 4);
        layout.Children.Add(actions);
        dialog.Content = layout;
        dialog.Loaded += (_, _) => search.Focus();
        RefreshResults();
        return new BatchCategoryPickerUi(dialog, search, categories, count, add, replace, clear);
    }

    internal async Task<string> VerifyBatchCategoryPickerOffscreenAsync()
    {
        var targets = Enumerable.Range(0, 3500).Select(index => $"人物 {index:0000} / 作品分类").ToArray();
        // The category picker may hold thousands of paths, but a right click
        // must build a fixed-size menu before the user asks to choose one.
        var categoryCount = CategoryTargets().Count();
        var probe = new VideoItem
        {
            Id = "context-menu-offscreen-probe", Path = "context-menu-offscreen-probe.mp4",
            Title = "右键菜单诊断", Folder = ".", Size = 0, Modified = DateTime.UtcNow
        };
        var timer = Stopwatch.StartNew();
        var card = BuildCardContextMenu(probe);
        var folder = BuildFolderContextMenu("diagnostic-folder");
        timer.Stop();
        if (card.Items.Count > 20 || folder.Items.Count != 1 || timer.ElapsedMilliseconds > 500)
            throw new InvalidOperationException($"右键菜单仍同步构建了过多分类：卡片 {card.Items.Count} 项，文件夹 {folder.Items.Count} 项，{timer.ElapsedMilliseconds} ms");
        string? chosen = null;
        var shouldReplace = false;
        var picker = BuildBatchCategoryPicker(targets, 32909, (path, replace) => { chosen = path; shouldReplace = replace; });
        picker.Dialog.Left = -30000;
        picker.Dialog.Top = -30000;
        picker.Dialog.ShowActivated = false;
        picker.Dialog.Opacity = 0;
        picker.Dialog.Show();
        try
        {
            await Dispatcher.InvokeAsync(picker.Dialog.UpdateLayout, DispatcherPriority.Render);
            var realized = CountVisualDescendants<ListBoxItem>(picker.Categories);
            if (picker.Categories.Items.Count != targets.Length || realized == 0 || realized > 80)
                throw new InvalidOperationException($"批量分类列表未虚拟化：总计 {picker.Categories.Items.Count}，已创建 {realized} 个卡片");
            picker.Search.Text = "人物 257";
            if (picker.Categories.Items.Count == 0 || picker.Categories.Items.Count >= targets.Length)
                throw new InvalidOperationException("批量分类搜索没有缩小结果范围");
            picker.Search.Text = "";
            picker.Categories.SelectedIndex = 0;
            if (picker.Categories.SelectedItem is not string first) throw new InvalidOperationException("批量分类无法选择节点");
            picker.Add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (chosen != first || shouldReplace) throw new InvalidOperationException("批量分类添加动作不正确");
            picker.Replace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (chosen != first || !shouldReplace) throw new InvalidOperationException("批量分类替换动作不正确");
            picker.Clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (chosen != "未分类" || !shouldReplace) throw new InvalidOperationException("批量分类清除动作不正确");
            return $"实际 {categoryCount:N0} 分类下卡片菜单 {card.Items.Count} 项、文件夹菜单 {folder.Items.Count} 项，构建 {timer.ElapsedMilliseconds} ms；3,500 分类的选择器仅创建 {realized} 个可见条目，搜索、添加、替换、清除正常";
        }
        finally { picker.Dialog.Close(); }
    }
}
