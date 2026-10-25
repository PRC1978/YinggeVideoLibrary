using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using DragDropEffects = System.Windows.DragDropEffects;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfPath = System.Windows.Shapes.Path;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    const int FolderDirectoryPageSize = 60;
    const int FolderVideoPageSize = 40;

    sealed class IndexedFolder(string path, string name)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public IndexedFolder? Parent { get; set; }
        public List<IndexedFolder> Children { get; } = [];
        public List<VideoItem> DirectVideos { get; } = [];
        public int TotalVideos { get; set; }
        public long NewestTicks { get; set; }
        public IndexedFolder[] ByName { get; set; } = [];
        public IndexedFolder[] ByCount { get; set; } = [];
        public IndexedFolder[] ByRecent { get; set; } = [];
    }

    sealed class FolderTreeIndex(List<VideoItem> source, string[] roots)
    {
        public List<VideoItem> Source { get; } = source;
        public string[] Roots { get; } = roots;
        public Dictionary<string, IndexedFolder> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IndexedFolder[] RootNodes { get; set; } = [];
    }

    Task<FolderTreeIndex>? folderTreeBuild;
    List<VideoItem>? folderTreeSource;
    string[] folderTreeRoots = [];
    FolderTreeIndex? folderDisplayedTree;
    string? folderCurrentRoot;
    string? folderCurrentPath;
    string folderSearch = "";
    int folderSortMode;
    int folderDirectoryPage;
    int folderVideoPage;
    int folderRenderRequest;
    StackPanel? folderResultsHost;
    StackPanel? folderVideosHost;
    DispatcherTimer? folderSearchTimer;

    static FolderTreeIndex BuildFolderTreeIndex(List<VideoItem> source, string[] roots)
    {
        var tree = new FolderTreeIndex(source, roots);
        IndexedFolder GetOrCreate(string path, string name)
        {
            if (tree.ByPath.TryGetValue(path, out var existing)) return existing;
            var node = new IndexedFolder(path, name);
            tree.ByPath[path] = node;
            return node;
        }
        IndexedFolder Child(IndexedFolder parent, string name)
        {
            var path = NormalizeRoot(System.IO.Path.Combine(parent.Path, name));
            var node = GetOrCreate(path, name);
            if (node.Parent is null && !ReferenceEquals(node, parent))
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
            return node;
        }
        IndexedFolder Descend(IndexedFolder start, string destination)
        {
            if (destination.Equals(start.Path, StringComparison.OrdinalIgnoreCase)) return start;
            var relative = System.IO.Path.GetRelativePath(start.Path, destination);
            if (relative is "." or "") return start;
            var current = start;
            foreach (var segment in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
                current = Child(current, segment);
            return current;
        }

        var orderedRoots = roots.OrderBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in orderedRoots)
            GetOrCreate(root, System.IO.Path.GetFileName(root.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : root);
        // A separately configured child root remains navigable from its
        // parent root, even when it has not yet yielded an indexed video.
        foreach (var root in orderedRoots)
        {
            var parentRoot = orderedRoots.Where(candidate => IsDescendantPath(candidate, root))
                .OrderByDescending(candidate => candidate.Length).FirstOrDefault();
            if (parentRoot is not null) Descend(tree.ByPath[parentRoot], root);
        }
        tree.RootNodes = roots.Select(root => tree.ByPath[root]).ToArray();

        foreach (var video in source)
        {
            try
            {
                var directory = NormalizeRoot(video.Folder);
                var root = orderedRoots.FirstOrDefault(candidate => directory.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                    || IsDescendantPath(candidate, directory));
                if (root is null) continue;
                var leaf = Descend(tree.ByPath[root], directory);
                leaf.DirectVideos.Add(video);
                for (var current = leaf; current is not null; current = current.Parent)
                {
                    current.TotalVideos++;
                    current.NewestTicks = Math.Max(current.NewestTicks, video.Modified.Ticks);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { /* A malformed old index entry is ignored. */ }
        }
        foreach (var node in tree.ByPath.Values)
        {
            node.ByName = node.Children.OrderBy(child => child.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            node.ByCount = node.Children.OrderByDescending(child => child.TotalVideos)
                .ThenBy(child => child.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            node.ByRecent = node.Children.OrderByDescending(child => child.NewestTicks)
                .ThenBy(child => child.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            node.DirectVideos.Sort((left, right) => right.Modified.CompareTo(left.Modified));
        }
        return tree;
    }

    async Task RenderFolderWorkspaceAsync()
    {
        var request = ++folderRenderRequest;
        FoldersPanel.Children.Clear();
        FoldersPanel.Children.Add(new TextBlock
        {
            Text = "正在整理文件夹视图…", Margin = new Thickness(0, 15, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(127, 143, 168))
        });
        var roots = NormalizeRoots(config.Folders);
        var source = videos;
        if (folderTreeBuild is null || folderTreeBuild.IsFaulted || folderTreeBuild.IsCanceled ||
            !ReferenceEquals(folderTreeSource, source) || !roots.SequenceEqual(folderTreeRoots, StringComparer.OrdinalIgnoreCase))
        {
            folderTreeSource = source;
            folderTreeRoots = roots;
            folderTreeBuild = Task.Run(() => BuildFolderTreeIndex(source, roots));
        }
        try
        {
            var tree = await folderTreeBuild;
            if (isClosing || view != "folders" || request != folderRenderRequest) return;
            folderDisplayedTree = tree;
            if (folderCurrentRoot is not null && (!tree.Roots.Contains(folderCurrentRoot, StringComparer.OrdinalIgnoreCase)
                || folderCurrentPath is null || !tree.ByPath.ContainsKey(folderCurrentPath)))
            {
                folderCurrentRoot = null;
                folderCurrentPath = null;
            }
            BuildFolderWorkspace(tree);
        }
        catch (Exception ex)
        {
            if (request != folderRenderRequest) return;
            FoldersPanel.Children.Clear();
            FoldersPanel.Children.Add(new TextBlock { Text = "文件夹视图暂时无法建立：" + ex.Message,
                Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap });
            Log("文件夹树构建失败：" + ex.Message);
        }
    }

    void BuildFolderWorkspace(FolderTreeIndex tree)
    {
        FoldersPanel.Children.Clear();
        var current = folderCurrentPath is not null && tree.ByPath.TryGetValue(folderCurrentPath, out var node) ? node : null;
        var summary = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
        summary.Children.Add(new TextBlock
        {
            Text = current is null ? "我的文件夹" : current.Name,
            FontSize = 21, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(38, 51, 71))
        });
        summary.Children.Add(new TextBlock
        {
            Text = current is null
                ? $"{tree.Roots.Length} 个扫描位置 · {tree.ByPath.Count:N0} 个库内目录 · {tree.Source.Count:N0} 部视频"
                : $"{current.TotalVideos:N0} 部视频（含子目录） · 本层 {current.DirectVideos.Count:N0} 部 · {current.Children.Count:N0} 个库内子目录",
            FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(123, 141, 169)),
            Margin = new Thickness(0, 5, 0, 0)
        });
        summary.Children.Add(new TextBlock
        {
            Text = "按视频索引浏览库内目录；没有索引视频的空目录不会显示。扫描位置始终显示。",
            FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(139, 153, 177)),
            Margin = new Thickness(0, 4, 0, 0)
        });
        FoldersPanel.Children.Add(summary);
        FoldersPanel.Children.Add(BuildFolderBreadcrumb(tree, current));

        var controls = new WrapPanel { Margin = new Thickness(0, 7, 0, 13) };
        if (current is not null)
        {
            var back = FolderAction("← 返回上级");
            back.Click += (_, _) => FolderNavigateBack();
            controls.Children.Add(back);
        }
        var add = FolderAction("＋ 添加扫描位置");
        add.Click += AddFolder;
        controls.Children.Add(add);
        var rescan = FolderAction("↻ 扫描当前库");
        rescan.Click += (_, _) => _ = RefreshAsync();
        controls.Children.Add(rescan);
        var search = new TextBox
        {
            Width = 230, Height = 34, Text = folderSearch, VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 9, 8),
            ToolTip = "搜索当前层的库内目录"
        };
        controls.Children.Add(search);
        var sort = new ComboBox { Width = 125, Height = 34, Margin = new Thickness(0, 0, 9, 8) };
        foreach (var label in new[] { "名称排序", "视频最多", "最近更新" }) sort.Items.Add(label);
        sort.SelectedIndex = folderSortMode;
        sort.SelectionChanged += (_, _) => { folderSortMode = Math.Max(0, sort.SelectedIndex); folderDirectoryPage = 0; UpdateFolderDirectoryListing(); };
        controls.Children.Add(sort);
        FoldersPanel.Children.Add(controls);
        folderResultsHost = new StackPanel();
        FoldersPanel.Children.Add(folderResultsHost);
        folderVideosHost = new StackPanel { Margin = new Thickness(0, 16, 0, 10) };
        FoldersPanel.Children.Add(folderVideosHost);

        if (folderSearchTimer is null)
        {
            folderSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
            folderSearchTimer.Tick += (_, _) => { folderSearchTimer.Stop(); folderDirectoryPage = 0; UpdateFolderDirectoryListing(); };
        }
        search.TextChanged += (_, _) =>
        {
            folderSearch = search.Text.Trim();
            folderSearchTimer.Stop();
            folderSearchTimer.Start();
        };
        UpdateFolderDirectoryListing();
        UpdateFolderVideoListing();
    }

    static Button FolderAction(string label) => new()
    {
        Content = label, Height = 34, Padding = new Thickness(11, 5, 11, 5),
        Margin = new Thickness(0, 0, 9, 8), Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(217, 226, 241)),
        Foreground = new SolidColorBrush(Color.FromRgb(69, 98, 161)), Cursor = Cursors.Hand
    };

    WrapPanel BuildFolderBreadcrumb(FolderTreeIndex tree, IndexedFolder? current)
    {
        var breadcrumb = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var all = FolderAction("全部位置");
        all.Click += (_, _) => FolderNavigate(null, null);
        breadcrumb.Children.Add(all);
        if (current is null || folderCurrentRoot is null) return breadcrumb;
        var path = new Stack<IndexedFolder>();
        for (var node = current; node is not null; node = node.Parent)
        {
            path.Push(node);
            if (node.Path.Equals(folderCurrentRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
        while (path.Count > 0)
        {
            var node = path.Pop();
            breadcrumb.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 8), Foreground = new SolidColorBrush(Color.FromRgb(149, 163, 187)) });
            var step = FolderAction(node.Name);
            step.ToolTip = node.Path;
            var destination = node.Path;
            step.Click += (_, _) => FolderNavigate(folderCurrentRoot, destination);
            breadcrumb.Children.Add(step);
        }
        return breadcrumb;
    }

    void FolderNavigate(string? root, string? path)
    {
        folderCurrentRoot = root;
        folderCurrentPath = path;
        folderSearch = "";
        folderDirectoryPage = 0;
        folderVideoPage = 0;
        if (view == "folders") { RenderFolders(); LibraryScroll.ScrollToTop(); }
        else ShowView("folders");
    }

    void FolderNavigateBack()
    {
        if (folderCurrentPath is null || folderCurrentRoot is null) return;
        if (folderCurrentPath.Equals(folderCurrentRoot, StringComparison.OrdinalIgnoreCase))
            FolderNavigate(null, null);
        else
        {
            var parent = System.IO.Path.GetDirectoryName(folderCurrentPath);
            FolderNavigate(folderCurrentRoot, parent is null ||
                (!parent.Equals(folderCurrentRoot, StringComparison.OrdinalIgnoreCase) && !IsDescendantPath(folderCurrentRoot, parent))
                ? folderCurrentRoot : parent);
        }
    }

    void UpdateFolderDirectoryListing()
    {
        if (view != "folders" || folderResultsHost is null || folderDisplayedTree is null) return;
        var tree = folderDisplayedTree;
        var current = folderCurrentPath is not null && tree.ByPath.TryGetValue(folderCurrentPath, out var node) ? node : null;
        IEnumerable<IndexedFolder> children = current is null
            ? folderSortMode switch
            {
                1 => tree.RootNodes.OrderByDescending(item => item.TotalVideos),
                2 => tree.RootNodes.OrderByDescending(item => item.NewestTicks),
                _ => tree.RootNodes.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            }
            : folderSortMode switch
            {
                1 => current.ByCount, 2 => current.ByRecent, _ => current.ByName
            };
        if (folderSearch.Length > 0)
            children = children.Where(item => item.Name.Contains(folderSearch, StringComparison.CurrentCultureIgnoreCase));
        var matches = children.ToArray();
        var pageCount = Math.Max(1, (matches.Length + FolderDirectoryPageSize - 1) / FolderDirectoryPageSize);
        folderDirectoryPage = Math.Clamp(folderDirectoryPage, 0, pageCount - 1);
        folderResultsHost.Children.Clear();
        folderResultsHost.Children.Add(new TextBlock
        {
            Text = current is null ? $"扫描位置 · {matches.Length:N0}" : $"库内子目录 · {matches.Length:N0}",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 10), Foreground = new SolidColorBrush(Color.FromRgb(45, 65, 104))
        });
        var cards = new WrapPanel();
        foreach (var child in matches.Skip(folderDirectoryPage * FolderDirectoryPageSize).Take(FolderDirectoryPageSize))
            cards.Children.Add(BuildFolderCard(child, current is null));
        folderResultsHost.Children.Add(cards);
        if (matches.Length == 0)
            folderResultsHost.Children.Add(new TextBlock
            {
                Text = tree.Roots.Length == 0 ? "还没有扫描位置。点击上方“添加扫描位置”。" :
                    folderSearch.Length > 0 ? "当前层没有匹配的库内目录。" : "这个目录下没有已索引的子目录。",
                Foreground = new SolidColorBrush(Color.FromRgb(127, 143, 168)), Margin = new Thickness(0, 9, 0, 6)
            });
        if (pageCount > 1) folderResultsHost.Children.Add(BuildFolderPager(folderDirectoryPage, pageCount,
            page => { folderDirectoryPage = page; UpdateFolderDirectoryListing(); }));
    }

    Border BuildFolderCard(IndexedFolder node, bool isRoot)
    {
        var card = new Border
        {
            Width = 194, MinHeight = 164, CornerRadius = new CornerRadius(14),
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(216, 226, 244)),
            BorderThickness = new Thickness(1), Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 11, 11), Cursor = Cursors.Hand,
            Focusable = true, ToolTip = node.Path
        };
        var body = new StackPanel();
        var iconArea = new Grid { Height = 62 };
        var folderIcon = new WpfPath
        {
            Data = Geometry.Parse("M4,8 C4,6.9 4.9,6 6,6 L13,6 L16,9 L28,9 C29.1,9 30,9.9 30,11 L30,25 C30,26.1 29.1,27 28,27 L6,27 C4.9,27 4,26.1 4,25 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(91, 126, 219)),
            Stroke = new SolidColorBrush(Color.FromRgb(66, 103, 199)),
            StrokeThickness = 0.7, Width = 54, Height = 47,
            Stretch = Stretch.Uniform, HorizontalAlignment = WpfHorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconArea.Children.Add(folderIcon);
        if (isRoot)
        {
            var remove = new Button
            {
                Content = "移除", FontSize = 11, Foreground = Brushes.IndianRed,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(5, 2, 5, 2), Cursor = Cursors.Hand
            };
            remove.Click += (_, _) => RemoveFolderRoot(node.Path);
            remove.HorizontalAlignment = WpfHorizontalAlignment.Right;
            remove.VerticalAlignment = VerticalAlignment.Top;
            iconArea.Children.Add(remove);
        }
        body.Children.Add(iconArea);
        body.Children.Add(new TextBlock
        {
            Text = node.Name, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(59, 93, 178)),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 163
        });
        body.Children.Add(new TextBlock
        {
            Text = $"{node.TotalVideos:N0} 部视频 · {node.Children.Count:N0} 个子目录",
            FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(115, 132, 161)),
            Margin = new Thickness(0, 10, 0, 0)
        });
        body.Children.Add(new TextBlock
        {
            Text = isRoot ? node.Path : $"本层 {node.DirectVideos.Count:N0} 部 · 点击进入",
            FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(145, 159, 182)),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 7, 0, 0)
        });
        card.Child = body;
        card.MouseEnter += (_, _) => card.Background = new SolidColorBrush(Color.FromRgb(239, 245, 255));
        card.MouseLeave += (_, _) => card.Background = Brushes.White;
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindParent<WpfButtonBase>(source) is not null)
            { folderDragElement = null; return; }
            folderDragElement = card;
            folderDragStart = e.GetPosition(card);
        };
        card.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var shouldOpen = ReferenceEquals(folderDragElement, card) &&
                (e.OriginalSource is not DependencyObject source || FindParent<WpfButtonBase>(source) is null);
            if (ReferenceEquals(folderDragElement, card)) folderDragElement = null;
            HideDragHint();
            if (shouldOpen) FolderNavigate(isRoot ? node.Path : folderCurrentRoot, node.Path);
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || !ReferenceEquals(folderDragElement, card)) return;
            if (e.OriginalSource is DependencyObject source && FindParent<WpfButtonBase>(source) is not null) return;
            var point = e.GetPosition(card);
            if (Math.Abs(point.X - folderDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - folderDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            folderDragElement = null;
            ShowDragHint("正在拖动文件夹 · 放到分类卡片即可归类");
            try { DragDrop.DoDragDrop(card, node.Path, DragDropEffects.Move); }
            catch (Exception ex) { Log("文件夹拖动失败：" + ex.Message); }
            finally { HideDragHint(); }
        };
        card.PreviewMouseRightButtonDown += (_, e) =>
        {
            card.ContextMenu = BuildFolderContextMenu(node.Path);
            card.ContextMenu.IsOpen = true;
            e.Handled = true;
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            { FolderNavigate(isRoot ? node.Path : folderCurrentRoot, node.Path); e.Handled = true; }
        };
        return card;
    }

    void RemoveFolderRoot(string path)
    {
        config.Folders.RemoveAll(folder => NormalizeRoot(folder).Equals(path, StringComparison.OrdinalIgnoreCase));
        config.FoldersClearedByUser = config.Folders.Count == 0;
        SaveConfig();
        if (folderCurrentRoot?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
        { folderCurrentRoot = null; folderCurrentPath = null; }
        ConfigureFolderWatchers();
        CountText.Text = $"{config.Folders.Count} 个位置";
        RenderFolders();
        _ = RefreshAsync();
    }

    static StackPanel BuildFolderPager(int page, int count, Action<int> navigate)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 12) };
        var previous = FolderAction("上一页");
        previous.IsEnabled = page > 0;
        previous.Click += (_, _) => navigate(page - 1);
        bar.Children.Add(previous);
        bar.Children.Add(new TextBlock
        {
            Text = $"第 {page + 1:N0} / {count:N0} 页", VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(111, 129, 160)), Margin = new Thickness(2, 0, 11, 8)
        });
        var next = FolderAction("下一页");
        next.IsEnabled = page < count - 1;
        next.Click += (_, _) => navigate(page + 1);
        bar.Children.Add(next);
        return bar;
    }

    void UpdateFolderVideoListing()
    {
        if (view != "folders" || folderVideosHost is null || folderDisplayedTree is null) return;
        folderVideosHost.Children.Clear();
        if (folderCurrentPath is null || !folderDisplayedTree.ByPath.TryGetValue(folderCurrentPath, out var node)) return;
        var videosHere = node.DirectVideos;
        folderVideosHost.Children.Add(new TextBlock
        {
            Text = $"本层视频 · {videosHere.Count:N0}", FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(45, 65, 104)), Margin = new Thickness(0, 0, 0, 10)
        });
        var pageCount = Math.Max(1, (videosHere.Count + FolderVideoPageSize - 1) / FolderVideoPageSize);
        folderVideoPage = Math.Clamp(folderVideoPage, 0, pageCount - 1);
        foreach (var video in videosHere.Skip(folderVideoPage * FolderVideoPageSize).Take(FolderVideoPageSize))
        {
            var row = new Border
            {
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(225, 232, 244)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11, 8, 11, 8), Margin = new Thickness(0, 0, 0, 6)
            };
            var contents = new DockPanel { LastChildFill = true };
            var play = FolderAction("▶ 播放");
            play.Margin = new Thickness(10, 0, 0, 0);
            play.Click += (_, _) => LaunchVideo(video);
            DockPanel.SetDock(play, Dock.Right);
            contents.Children.Add(play);
            contents.Children.Add(new TextBlock
            {
                Text = video.DisplayTitle, VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(42, 59, 88)),
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = video.Path
            });
            row.Child = contents;
            row.PreviewMouseRightButtonDown += (_, e) =>
            { row.ContextMenu = BuildCardContextMenu(video); row.ContextMenu.IsOpen = true; e.Handled = true; };
            folderVideosHost.Children.Add(row);
        }
        if (videosHere.Count == 0)
            folderVideosHost.Children.Add(new TextBlock { Text = "本层没有视频；可以进入子文件夹继续浏览。",
                Foreground = new SolidColorBrush(Color.FromRgb(127, 143, 168)) });
        if (pageCount > 1) folderVideosHost.Children.Add(BuildFolderPager(folderVideoPage, pageCount,
            page => { folderVideoPage = page; UpdateFolderVideoListing(); }));
    }

    internal static bool VerifyFolderTreeForDiagnostics()
    {
        var root = NormalizeRoot(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yingge-folder-tree-check"));
        var child = System.IO.Path.Combine(root, "人物", "作品", "子目录");
        var items = new List<VideoItem>
        {
            new() { Id = "a", Path = System.IO.Path.Combine(root, "one.mp4"), Folder = root,
                Title = "one", Size = 1, Modified = new DateTime(2025, 1, 1) },
            new() { Id = "b", Path = System.IO.Path.Combine(child, "two.mp4"), Folder = child,
                Title = "two", Size = 1, Modified = new DateTime(2026, 1, 1) },
            new() { Id = "empty-folder", Path = "", Folder = null!, Title = "bad", Size = 0, Modified = default },
            new() { Id = "invalid-folder", Path = "", Folder = root + "\\bad\0name", Title = "bad", Size = 0, Modified = default }
        };
        var tree = BuildFolderTreeIndex(items, [root]);
        return tree.RootNodes.Length == 1 && tree.RootNodes[0].TotalVideos == 2 &&
            tree.RootNodes[0].DirectVideos.Count == 1 && tree.ByPath[NormalizeRoot(child)].DirectVideos.Count == 1 &&
            tree.ByPath[NormalizeRoot(System.IO.Path.Combine(root, "人物"))].TotalVideos == 1 &&
            tree.RootNodes[0].ByName.Length == 1;
    }

    internal async Task<string> VerifyFolderWorkspaceOffscreenAsync()
    {
        if (videos.Count == 0 || config.Folders.Count == 0)
            throw new InvalidOperationException("真实共享视频索引或扫描位置为空，无法验证文件夹页");
        var started = System.Diagnostics.Stopwatch.StartNew();
        folderSearch = "";
        folderSortMode = 0;
        folderCurrentPath = null;
        folderCurrentRoot = null;
        ShowView("folders");
        var building = folderTreeBuild ?? throw new InvalidOperationException("文件夹索引未开始建立");
        var tree = await building.WaitAsync(TimeSpan.FromSeconds(20));
        await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
        if (!ReferenceEquals(folderDisplayedTree, tree) || FoldersPanel.Visibility != Visibility.Visible
            || Toolbar.Visibility != Visibility.Collapsed || tree.RootNodes.Length == 0)
            throw new InvalidOperationException("文件夹根页未从共享索引显示");
        int CardCount() => folderResultsHost?.Children.OfType<WrapPanel>().FirstOrDefault()?.Children.Count
            ?? throw new InvalidOperationException("文件夹卡片容器未建立");
        if (CardCount() != Math.Min(tree.RootNodes.Length, FolderDirectoryPageSize))
            throw new InvalidOperationException("根目录卡片数量或分页上限不正确");
        if (tree.ByPath.Count == 0 || tree.RootNodes.Any(root => !tree.ByPath.ContainsKey(root.Path)))
            throw new InvalidOperationException("扫描根未写入文件夹内存索引");
        var rootMs = started.ElapsedMilliseconds;

        var root = tree.RootNodes.FirstOrDefault(item => item.Children.Count > 0) ?? tree.RootNodes[0];
        FolderNavigate(root.Path, root.Path);
        await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
        if (folderCurrentPath != root.Path || CardCount() > FolderDirectoryPageSize
            || root.TotalVideos != root.DirectVideos.Count + root.Children.Sum(child => child.TotalVideos))
            throw new InvalidOperationException("进入扫描根后子目录计数或分页异常");
        var maxCards = CardCount();
        var maxVideos = folderVideosHost?.Children.OfType<Border>().Count() ?? 0;
        if (maxVideos > FolderVideoPageSize)
            throw new InvalidOperationException("本层视频超出分页上限");

        var drillDepth = 0;
        if (root.Children.Count > 0)
        {
            var child = root.Children[0];
            FolderNavigate(root.Path, child.Path);
            await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
            if (folderCurrentPath != child.Path || child.Parent != root
                || child.TotalVideos != child.DirectVideos.Count + child.Children.Sum(item => item.TotalVideos)
                || CardCount() > FolderDirectoryPageSize)
                throw new InvalidOperationException("逐级进入库内目录后层级、计数或分页异常");
            drillDepth = 2;
            maxCards = Math.Max(maxCards, CardCount());
            maxVideos = Math.Max(maxVideos, folderVideosHost?.Children.OfType<Border>().Count() ?? 0);
            FolderNavigateBack();
            await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
            if (folderCurrentPath != root.Path)
                throw new InvalidOperationException("返回上级没有回到扫描根");
        }
        FolderNavigateBack();
        await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
        if (folderCurrentPath is not null || folderCurrentRoot is not null)
            throw new InvalidOperationException("从扫描根返回未回到全部位置");
        if (maxCards > FolderDirectoryPageSize || maxVideos > FolderVideoPageSize)
            throw new InvalidOperationException("文件夹或视频控件数超过分页上限");
        return $"共享索引 {videos.Count:N0} 部；扫描位置 {tree.RootNodes.Length:N0} 个；库内目录 {tree.ByPath.Count:N0} 个；" +
            $"下钻 {drillDepth} 层并返回；最多 {maxCards} 张目录卡 / {maxVideos} 条视频；根页 {rootMs} ms，完整导航 {started.ElapsedMilliseconds} ms";
    }
}
