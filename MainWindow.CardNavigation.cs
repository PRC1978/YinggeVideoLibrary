using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    internal static string? BuildChildCategoryPath(string? parent, string? childName)
    {
        var name = childName?.Trim();
        if (string.IsNullOrWhiteSpace(parent) || parent is "全部" or "未分类" || string.IsNullOrWhiteSpace(name)
            || name.Length > 40 || name is "全部" or "未分类"
            || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl)) return null;
        return parent.Trim() + " / " + name;
    }

    internal static bool VerifyChildCategoriesForDiagnostics()
    {
        var path = BuildChildCategoryPath("人物", "作品");
        var grandchild = BuildChildCategoryPath(path, "片段");
        if (path != "人物 / 作品" || grandchild != "人物 / 作品 / 片段"
            || BuildChildCategoryPath("未分类", "作品") is not null
            || BuildChildCategoryPath("人物", "错误/路径") is not null) return false;
        var video = new VideoItem
        {
            Id = "child-category-probe", Path = "child-category-probe.mp4", Folder = ".",
            Title = "分类层级测试", Category = "电影", Size = 0, Modified = DateTime.UtcNow
        };
        video.CategoryList.Add("电影");
        var state = new LibraryConfig();
        ApplyBatchCategoryValue(video, state, path, false);
        if (video.CategoryList.Count != 2 || !video.CategoryList.Contains(path)
            || !state.MultiCategories[video.Id].Contains(path)) return false;
        ApplyBatchCategoryValue(video, state, grandchild, true);
        return video.CategoryList.Count == 1 && video.CategoryList[0] == grandchild
            && state.Categories[video.Id] == grandchild;
    }

    string? CreateChildCategory(string parent, Window owner)
    {
        if (string.IsNullOrWhiteSpace(parent) || parent is "全部" or "未分类") return null;
        var prompt = new Window
        {
            Title = "新建子分类", Owner = owner, Width = 560, Height = 260,
            MinWidth = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Background = Brushes.White
        };
        var body = new StackPanel { Margin = new Thickness(22) };
        body.Children.Add(new TextBlock { Text = "在此分类下新建子分类", FontSize = 17, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = parent, TextWrapping = TextWrapping.Wrap, MaxHeight = 55,
            Foreground = new SolidColorBrush(Color.FromRgb(92, 111, 150)),
            Margin = new Thickness(0, 8, 0, 12)
        });
        var input = new System.Windows.Controls.TextBox { Height = 36, MaxLength = 40, Padding = new Thickness(9, 5, 9, 5) };
        body.Children.Add(input);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var accept = new Button { Content = "创建", Style = (Style)FindResource("Primary") };
        accept.Click += (_, _) => prompt.DialogResult = true;
        var cancel = new Button { Content = "取消", Style = (Style)FindResource("Action") };
        cancel.Click += (_, _) => prompt.DialogResult = false;
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { prompt.DialogResult = true; e.Handled = true; }
            if (e.Key == Key.Escape) { prompt.DialogResult = false; e.Handled = true; }
        };
        prompt.Content = body;
        prompt.Loaded += (_, _) => input.Focus();
        if (prompt.ShowDialog() != true) return null;
        var path = BuildChildCategoryPath(parent, input.Text);
        if (path is null)
        {
            System.Windows.MessageBox.Show(owner, "子分类名称须在 40 个字符以内，不能包含斜杠、控制字符或保留名称。", "名称无效",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        return path;
    }

    void CreateChildCategoryFromWorkbench(object sender, RoutedEventArgs e)
    {
        if (selectedCategory is "全部" or "未分类")
        {
            System.Windows.MessageBox.Show(this, "先在分类列表或球形图中选择父分类，再新建子分类。",
                "请选择父分类", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var path = CreateChildCategory(selectedCategory, this);
        if (path is null) return;
        if (!config.CategoryNames.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            config.CategoryNames.Add(path);
            SaveConfig();
        }
        selectedCategory = path;
        pageIndex = 0;
        RenderCategories();
        RenderCards();
    }

    void ShowVideoCategories(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VideoItem video) return;
        var categories = CategoriesOf(video)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (categories.Length == 0) categories = ["未分类"];

        var dialog = new Window
        {
            Title = "当前分类",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 620,
            Height = Math.Min(620, 190 + categories.Length * 67),
            MinWidth = 440,
            MinHeight = 260,
            Background = Brushes.White,
            FontFamily = FontFamily,
            FontSize = 13
        };
        var layout = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
        heading.Children.Add(new TextBlock { Text = "当前分类", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(38, 51, 71)) });
        heading.Children.Add(new TextBlock { Text = video.DisplayTitle, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(115, 130, 153)), Margin = new Thickness(0, 6, 0, 0), MaxHeight = 45 });
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var manage = new Button { Content = "管理分类", Style = (Style)FindResource("Action") };
        manage.Click += (_, _) => { dialog.Close(); Dispatcher.BeginInvoke(() => ShowVideoCategoryPicker(video)); };
        actions.Children.Add(manage);
        var close = new Button { Content = "关闭", Style = (Style)FindResource("Action") };
        close.Click += (_, _) => dialog.Close();
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);

        var rows = new StackPanel();
        foreach (var category in categories)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock
            {
                Text = category,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(54, 74, 123)),
                Margin = new Thickness(12, 10, 12, 10)
            };
            row.Children.Add(name);
            var open = new Button
            {
                Content = "打开分类",
                Style = (Style)FindResource("Action"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 6, 4),
                Height = 32
            };
            open.Click += (_, _) =>
            {
                dialog.Close();
                selectedCategory = category;
                pageIndex = 0;
                ShowView("categories");
                LibraryScroll.ScrollToTop();
            };
            Grid.SetColumn(open, 1);
            row.Children.Add(open);
            var surface = new Border
            {
                Child = row,
                Background = new SolidColorBrush(Color.FromRgb(245, 248, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(219, 228, 247)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9)
            };
            rows.Children.Add(surface);
        }
        layout.Children.Add(new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        dialog.Content = layout;
        dialog.ShowDialog();
    }

    void PageJumpClick(object sender, RoutedEventArgs e) => JumpToPage();

    void PageJumpKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        JumpToPage();
    }

    void JumpToPage()
    {
        var pageSize = config.CardPageSize is 30 or 60 or 90 ? config.CardPageSize : 60;
        var pages = Math.Max(1, (currentFilteredVideos.Count + pageSize - 1) / pageSize);
        if (!int.TryParse(PageJumpBox.Text.Trim(), out var requested) || requested < 1)
        {
            PageJumpBox.ToolTip = $"请输入 1 到 {pages} 的页码";
            PageJumpBox.Focus();
            PageJumpBox.SelectAll();
            return;
        }
        pageIndex = Math.Clamp(requested, 1, pages) - 1;
        RenderCards();
        LibraryScroll.ScrollToTop();
    }
}
