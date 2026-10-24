using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using StackPanel = System.Windows.Controls.StackPanel;
using Orientation = System.Windows.Controls.Orientation;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using FlowDirection = System.Windows.FlowDirection;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    sealed class SphereMapNode
    {
        public required string Path { get; init; }
        public required string Parent { get; init; }
        public required string Label { get; init; }
        public required int Count { get; init; }
        public required int Depth { get; init; }
        public bool IsTag { get; init; }
        public string TagName { get; init; } = "";
        public double Angle { get; set; }
        public double Radius { get; set; }
    }

    sealed class SphereGraphIndex
    {
        public long Version { get; init; }
        public required Dictionary<string, int> AllCategoryCounts { get; init; }
        public required Dictionary<string, int> TagCounts { get; init; }
        public required Dictionary<string, Dictionary<string, int>> CategoriesByTag { get; init; }
        public required Dictionary<string, Dictionary<string, int>> RelatedTags { get; init; }
        public int VideoCount { get; init; }
    }

    sealed record SphereTagOption(string Name, int Count)
    {
        public override string ToString() => Name.Length == 0 ? $"全部标签 · {Count:N0} 部" : $"#{Name} · {Count:N0} 部";
    }

    sealed record SphereNavItem(string Path, string Label, int Count)
    {
        public override string ToString() => $"{Label}   ·   {Count:N0} 部";
    }

    // One WPF visual draws every category and edge. A large library still has
    // only one graph element, and rotation invalidates paint without rebuilding
    // a visual tree of cards, drop shadows and Line controls on every frame.
    sealed class SphereMapSurface : FrameworkElement
    {
        static readonly Brush Backdrop = FrozenBrush(Color.FromRgb(247, 250, 255));
        static readonly Brush NodeBrush = FrozenBrush(Color.FromRgb(78, 124, 222));
        static readonly Brush NodeLightBrush = FrozenBrush(Color.FromRgb(151, 181, 242));
        static readonly Brush TagBrush = FrozenBrush(Color.FromRgb(74, 165, 164));
        static readonly Brush SelectedBrush = FrozenBrush(Color.FromRgb(246, 168, 74));
        static readonly Brush HoverBrush = FrozenBrush(Color.FromRgb(91, 71, 193));
        static readonly Brush HaloBrush = FrozenBrush(Color.FromArgb(60, 72, 101, 207));
        static readonly Brush RootBrush = FrozenBrush(Color.FromRgb(83, 100, 193));
        static readonly Brush TextBrush = FrozenBrush(Color.FromRgb(47, 69, 121));
        static readonly Brush LightTextBrush = FrozenBrush(Color.FromRgb(124, 143, 177));
        static readonly Brush LabelBackdrop = FrozenBrush(Color.FromArgb(225, 250, 252, 255));
        static readonly Pen LinkPen = FrozenPen(Color.FromArgb(184, 89, 126, 205), 1.15);
        static readonly Pen FocusLinkPen = FrozenPen(Color.FromArgb(242, 232, 147, 54), 2.0);
        static readonly Pen NodePen = FrozenPen(Color.FromRgb(234, 241, 255), 1);
        static readonly Pen GlobePen = FrozenPen(Color.FromRgb(181, 205, 245), 1.5);
        static readonly Pen RootPen = FrozenPen(Color.FromRgb(245, 248, 255), 2);
        static readonly Pen OrbitPen = FrozenPen(Color.FromArgb(110, 121, 154, 211), 1);
        static readonly Brush GlobeBrush = MakeGlobeBrush();
        static readonly Typeface LabelTypeface = new("Microsoft YaHei UI");
        readonly List<SphereMapNode> nodes;
        readonly SphereMapNode[] rankedLabels;
        readonly Dictionary<string, System.Windows.Point> projected = new(StringComparer.CurrentCultureIgnoreCase);
        readonly Dictionary<string, SphereMapNode> byPath;
        public string HoverPath { get; private set; } = "\u0000";
        public string SelectedPath { get; set; } = "";
        public string DropPath { get; set; } = "\u0000";
        public string RootLabel { get; set; } = "全部分类";
        public int RootCount { get; set; }
        public double Rotation { get; set; }
        public double ZoomLevel { get; set; } = 1;
        public int NodeCount => nodes.Count;
        public bool EveryNodeLinked => nodes.All(node => node.Parent.Length == 0 || byPath.ContainsKey(node.Parent));

        public SphereMapSurface(List<SphereMapNode> nodes)
        {
            this.nodes = nodes;
            byPath = nodes.ToDictionary(node => node.Path, StringComparer.CurrentCultureIgnoreCase);
            rankedLabels = nodes.OrderByDescending(node => node.Count)
                .ThenBy(node => node.Depth).ThenBy(node => node.Label, StringComparer.CurrentCultureIgnoreCase)
                .Take(120).ToArray();
            Width = 1120;
            Height = 620;
            AllowDrop = true;
            Cursor = Cursors.Hand;
            ToolTip = "点击节点筛选 · 拖动分类节点到目标节点 · 可把视频拖到节点";
        }

        static Brush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        static Pen FrozenPen(Color color, double thickness)
        {
            var pen = new Pen(FrozenBrush(color), thickness);
            pen.Freeze();
            return pen;
        }

        static Brush MakeGlobeBrush()
        {
            var brush = new RadialGradientBrush
            {
                Center = new System.Windows.Point(.35, .27),
                GradientOrigin = new System.Windows.Point(.29, .21),
                RadiusX = .86,
                RadiusY = .86
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(252, 246, 250, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(241, 219, 231, 255), .62));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(220, 172, 196, 245), 1));
            brush.Freeze();
            return brush;
        }

        public void SetHover(string? path)
        {
            path ??= "\u0000";
            if (string.Equals(HoverPath, path, StringComparison.CurrentCultureIgnoreCase)) return;
            HoverPath = path;
            ToolTip = path == "\u0000" ? "点击节点筛选 · 拖动视频或分类到节点" : path.Length == 0
                ? $"{RootLabel} · {RootCount:N0} 部视频" :
                $"{(byPath[path].IsTag ? "#" + byPath[path].TagName : path)} · {byPath[path].Count:N0} 部视频";
            InvalidateVisual();
        }

        public string? HitPath(System.Windows.Point point)
        {
            if (projected.Count == 0) Project();
            var root = projected[""];
            if ((root - point).Length <= 32) return "";
            string? nearest = null;
            var nearestDistance = double.MaxValue;
            foreach (var node in nodes)
            {
                var distance = (projected[node.Path] - point).Length;
                var hitRadius = Math.Max(11, NodeRadius(node) + 6);
                if (distance <= hitRadius && distance < nearestDistance)
                {
                    nearest = node.Path;
                    nearestDistance = distance;
                }
            }
            return nearest;
        }

        double NodeRadius(SphereMapNode node) => Math.Clamp(2.4 + Math.Log10(node.Count + 1) * 1.7, 2.5, 9);

        void Project()
        {
            projected.Clear();
            var center = new System.Windows.Point(Width / 2, Height / 2 + 7);
            projected[""] = center;
            foreach (var node in nodes)
            {
                var angle = node.Angle + Rotation;
                projected[node.Path] = new System.Windows.Point(
                    center.X + Math.Cos(angle) * node.Radius * 246,
                    center.Y + Math.Sin(angle) * node.Radius * 232);
            }
        }

        static bool InBranch(string path, string focus) => focus.Length > 0 &&
            (path.Equals(focus, StringComparison.CurrentCultureIgnoreCase) ||
             path.StartsWith(focus + " / ", StringComparison.CurrentCultureIgnoreCase) ||
             focus.StartsWith(path + " / ", StringComparison.CurrentCultureIgnoreCase));

        protected override void OnRender(DrawingContext dc)
        {
            var bounds = new Rect(0, 0, Width, Height);
            dc.DrawRectangle(Backdrop, null, bounds);
            var center = new System.Windows.Point(Width / 2, Height / 2 + 7);
            dc.DrawEllipse(GlobeBrush, GlobePen, center, 265, 265);
            dc.PushTransform(new RotateTransform(25, center.X, center.Y));
            dc.DrawEllipse(null, OrbitPen, center, 248, 74);
            dc.Pop();
            dc.PushTransform(new RotateTransform(-37, center.X, center.Y));
            dc.DrawEllipse(null, OrbitPen, center, 247, 117);
            dc.Pop();

            Project();
            // Every relationship is retained. DrawingContext batches them into
            // the same visual instead of constructing one WPF Line per edge.
            foreach (var node in nodes)
            {
                if (!projected.TryGetValue(node.Parent, out var parent)) parent = center;
                var focus = InBranch(node.Path, SelectedPath) || InBranch(node.Path, HoverPath) ||
                            InBranch(node.Path, DropPath);
                dc.DrawLine(focus ? FocusLinkPen : LinkPen, parent, projected[node.Path]);
            }
            foreach (var node in nodes)
            {
                var point = projected[node.Path];
                var radius = NodeRadius(node);
                var active = node.Path.Equals(SelectedPath, StringComparison.CurrentCultureIgnoreCase) ||
                             node.Path.Equals(DropPath, StringComparison.CurrentCultureIgnoreCase);
                var hovered = node.Path.Equals(HoverPath, StringComparison.CurrentCultureIgnoreCase);
                if (active || hovered) dc.DrawEllipse(HaloBrush, null, point, radius + 7, radius + 7);
                dc.DrawEllipse(active ? SelectedBrush : hovered ? HoverBrush : node.IsTag ? TagBrush : node.Count == 0 ? NodeLightBrush : NodeBrush,
                    NodePen, point, active || hovered ? radius + 2 : radius, active || hovered ? radius + 2 : radius);
            }
            dc.DrawEllipse(RootBrush, RootPen, center, 43, 43);
            DrawCentered(dc, "✦", center.X, center.Y - 14, 16, Brushes.White, FontWeights.Bold);
            DrawCentered(dc, RootLabel, center.X, center.Y + 8, 11, Brushes.White, FontWeights.SemiBold);

            // All nodes remain visible as dots. Labels use collision avoidance;
            // the navigator below offers exact access to every dense node.
            var occupied = new List<Rect> { new(center.X - 52, center.Y - 48, 104, 96) };
            var drawn = 0;
            var labelLimit = ZoomLevel >= 1.6 ? 28 : 16;
            void DrawLabel(SphereMapNode node)
            {
                var point = projected[node.Path];
                var text = new FormattedText(node.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    LabelTypeface, 11,
                    node.Path.Equals(SelectedPath, StringComparison.CurrentCultureIgnoreCase) ? HoverBrush : TextBrush, 1)
                { MaxTextWidth = 122, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                var left = point.X + NodeRadius(node) + 6;
                if (left + text.Width > Width - 15) left = point.X - NodeRadius(node) - text.Width - 6;
                var box = new Rect(left - 4, point.Y - 10, text.Width + 8, 20);
                if (occupied.Any(existing => existing.IntersectsWith(box))) return;
                occupied.Add(box);
                dc.DrawRoundedRectangle(LabelBackdrop, null, box, 4, 4);
                dc.DrawText(text, new System.Windows.Point(left, point.Y - text.Height / 2));
                drawn++;
            }
            if (byPath.TryGetValue(HoverPath, out var hoveredNode)) DrawLabel(hoveredNode);
            if (byPath.TryGetValue(SelectedPath, out var selectedNode) &&
                !string.Equals(SelectedPath, HoverPath, StringComparison.CurrentCultureIgnoreCase)) DrawLabel(selectedNode);
            foreach (var node in rankedLabels)
            {
                if (drawn >= labelLimit) break;
                if (node.Path.Equals(HoverPath, StringComparison.CurrentCultureIgnoreCase) ||
                    node.Path.Equals(SelectedPath, StringComparison.CurrentCultureIgnoreCase)) continue;
                DrawLabel(node);
            }
            if (nodes.Count == 0)
                DrawCentered(dc, "这个标签还没有关联分类节点", center.X, center.Y + 72, 13, LightTextBrush, FontWeights.Normal);
        }

        static void DrawCentered(DrawingContext dc, string value, double x, double y, double size, Brush brush, FontWeight weight)
        {
            var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
                size, brush, 1);
            dc.DrawText(text, new System.Windows.Point(x - text.Width / 2, y - text.Height / 2));
        }
    }

    SphereMapSurface? sphereMapSurface;
    SphereGraphIndex? sphereGraphIndex;
    readonly Dictionary<string, Button> sphereCategoryChips = new(StringComparer.CurrentCultureIgnoreCase);
    string sphereTagFilter = "";
    string sphereNavSearch = "";
    int sphereNavPage;
    string? sphereMouseDownPath;
    System.Windows.Point sphereMouseDownPoint;

    SphereGraphIndex GetSphereGraphIndex()
    {
        if (sphereGraphIndex is { } cached && cached.Version == categoryDataVersion && cached.VideoCount == videos.Count)
            return cached;
        var all = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var tagCounts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var categoriesByTag = new Dictionary<string, Dictionary<string, int>>(StringComparer.CurrentCultureIgnoreCase);
        var relatedTags = new Dictionary<string, Dictionary<string, int>>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var configured in config.CategoryNames) AddAncestors(configured, all, false);
        foreach (var video in videos)
        {
            var ancestors = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var path in CategoriesOf(video)) AddAncestors(path, all, true, ancestors);
            var tags = video.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var tag in tags)
            {
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
                if (!categoriesByTag.TryGetValue(tag, out var map)) categoriesByTag[tag] = map = new(StringComparer.CurrentCultureIgnoreCase);
                foreach (var ancestor in ancestors) map[ancestor] = map.GetValueOrDefault(ancestor) + 1;
                if (!relatedTags.ContainsKey(tag)) relatedTags[tag] = new(StringComparer.CurrentCultureIgnoreCase);
            }
            for (var i = 0; i < tags.Count; i++)
                for (var j = i + 1; j < tags.Count; j++)
                {
                    var left = relatedTags[tags[i]];
                    var right = relatedTags[tags[j]];
                    left[tags[j]] = left.GetValueOrDefault(tags[j]) + 1;
                    right[tags[i]] = right.GetValueOrDefault(tags[i]) + 1;
                }
        }
        return sphereGraphIndex = new SphereGraphIndex
        {
            Version = categoryDataVersion,
            VideoCount = videos.Count,
            AllCategoryCounts = all,
            TagCounts = tagCounts,
            CategoriesByTag = categoriesByTag,
            RelatedTags = relatedTags
        };
    }

    static List<SphereMapNode> LayoutSphereNodes(IReadOnlyDictionary<string, int> counts, IReadOnlyDictionary<string, int> relatedTags)
    {
        var nodes = counts.Keys.Select(path => new SphereMapNode
        {
            Path = path,
            Parent = path.LastIndexOf(" / ", StringComparison.Ordinal) is var separator && separator >= 0 ? path[..separator] : "",
            Label = path.Split(" / ").Last(),
            Count = counts[path],
            Depth = path.Split(" / ", StringSplitOptions.RemoveEmptyEntries).Length
        }).ToList();
        nodes.AddRange(relatedTags.Select(pair => new SphereMapNode
        {
            Path = "\u0001" + pair.Key,
            Parent = "",
            Label = "#" + pair.Key,
            Count = pair.Value,
            Depth = 1,
            IsTag = true,
            TagName = pair.Key
        }));
        var children = nodes.GroupBy(node => node.Parent, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(node => node.Count).ThenBy(node => node.Path, StringComparer.CurrentCultureIgnoreCase).ToList(),
                StringComparer.CurrentCultureIgnoreCase);
        // A leaf-only angular layout makes a short folder chain collapse onto
        // one radial spoke. In a sparse tag view, place every actual node on
        // its own sector, keeping related descendants next to their parent.
        // Edges still connect to the real parent; this changes layout only.
        if (nodes.Count is > 0 and <= 40)
        {
            var ordered = new List<SphereMapNode>(nodes.Count);
            void Visit(SphereMapNode node)
            {
                ordered.Add(node);
                if (children.TryGetValue(node.Path, out var descendants))
                    foreach (var child in descendants) Visit(child);
            }
            if (children.TryGetValue("", out var sparseRoots))
                foreach (var root in sparseRoots) Visit(root);
            var maxSparseDepth = Math.Max(1, nodes.Max(node => node.Depth));
            for (var i = 0; i < ordered.Count; i++)
            {
                var node = ordered[i];
                node.Angle = -Math.PI / 2 + 2 * Math.PI * (i + .5) / ordered.Count;
                node.Radius = .71 + .13 * (node.Depth - 1) / Math.Max(1, maxSparseDepth - 1);
            }
            return nodes;
        }
        var leafCount = nodes.Count(node => !children.ContainsKey(node.Path));
        var cursor = 0;
        double Position(SphereMapNode node)
        {
            if (!children.TryGetValue(node.Path, out var descendants) || descendants.Count == 0)
            {
                var angle = leafCount == 1 ? -Math.PI / 2 : -Math.PI / 2 + 2 * Math.PI * (cursor + .5) / leafCount;
                cursor++;
                node.Angle = angle;
                return angle;
            }
            var first = Position(descendants[0]);
            var last = first;
            for (var i = 1; i < descendants.Count; i++) last = Position(descendants[i]);
            node.Angle = (first + last) / 2;
            return node.Angle;
        }
        if (children.TryGetValue("", out var roots)) foreach (var root in roots) Position(root);
        var maxDepth = Math.Max(1, nodes.Select(node => node.Depth).DefaultIfEmpty(1).Max());
        foreach (var node in nodes)
            node.Radius = maxDepth == 1 ? .78 : .48 + .43 * (node.Depth - 1) / (maxDepth - 1);
        return nodes;
    }

    Border BuildSphereCategoryGraphV2()
    {
        sphereGraphTimer.Stop();
        sphereGraphCanvas = null;
        sphereGraphViewport = null;
        sphereGraphScale = null;
        sphereGraphTranslation = null;
        sphereZoomLabel = null;
        sphereZoom = 1;
        spherePanOffset = default;
        spherePanning = false;
        sphereRotation = 0;
        sphereMouseDownPath = null;

        var index = GetSphereGraphIndex();
        if (sphereTagFilter.Length > 0 && !index.TagCounts.ContainsKey(sphereTagFilter)) sphereTagFilter = "";
        var counts = sphereTagFilter.Length == 0 ? index.AllCategoryCounts : index.CategoriesByTag[sphereTagFilter];
        var connectedTags = sphereTagFilter.Length == 0 ? index.TagCounts : index.RelatedTags[sphereTagFilter];
        var relevantCount = sphereTagFilter.Length == 0 ? index.VideoCount : index.TagCounts[sphereTagFilter];
        var nodes = LayoutSphereNodes(counts, connectedTags);
        sphereMapSurface = new SphereMapSurface(nodes)
        {
            RootCount = relevantCount,
            RootLabel = sphereTagFilter.Length == 0 ? "全部分类" :
                "#" + (sphereTagFilter.Length > 6 ? sphereTagFilter[..6] + "…" : sphereTagFilter),
            SelectedPath = selectedCategory is "全部" or "未分类" ? (selectedCategory == "未分类" ? "未分类" : "") : selectedCategory
        };
        var surface = sphereMapSurface;
        surface.MouseLeftButtonDown += (_, e) =>
        {
            sphereMouseDownPath = surface.HitPath(e.GetPosition(surface));
            sphereMouseDownPoint = e.GetPosition(surface);
            if (sphereMouseDownPath is not null) e.Handled = true;
        };
        surface.MouseLeftButtonUp += (_, e) =>
        {
            var hit = surface.HitPath(e.GetPosition(surface));
            if (sphereMouseDownPath is not null && string.Equals(hit, sphereMouseDownPath, StringComparison.CurrentCultureIgnoreCase))
            {
                NavigateSphereNode(hit!);
                e.Handled = true;
            }
            sphereMouseDownPath = null;
        };
        surface.MouseMove += (_, e) =>
        {
            var hit = surface.HitPath(e.GetPosition(surface));
            surface.SetHover(hit);
            if (sphereMouseDownPath is null || sphereMouseDownPath.Length == 0 ||
                sphereMouseDownPath.StartsWith("\u0001", StringComparison.Ordinal) ||
                e.LeftButton != MouseButtonState.Pressed) return;
            var distance = e.GetPosition(surface) - sphereMouseDownPoint;
            if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var dragged = sphereMouseDownPath;
            sphereMouseDownPath = null;
            ShowDragHint("正在拖动分类节点 · 放到其他节点即可移动");
            try { System.Windows.DragDrop.DoDragDrop(surface, dragged, System.Windows.DragDropEffects.Move); }
            finally { HideDragHint(); }
        };
        surface.DragOver += (_, e) =>
        {
            var hit = surface.HitPath(e.GetPosition(surface));
            var allowed = !string.IsNullOrEmpty(hit) && !hit.StartsWith("\u0001", StringComparison.Ordinal) && hit != "未分类" &&
                          (e.Data.GetDataPresent(typeof(VideoItem)) || e.Data.GetDataPresent(typeof(string)));
            surface.DropPath = allowed ? hit! : "\u0000";
            surface.InvalidateVisual();
            e.Effects = allowed ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
            e.Handled = true;
        };
        surface.DragLeave += (_, _) => { surface.DropPath = "\u0000"; surface.InvalidateVisual(); };
        surface.Drop += (_, e) =>
        {
            var target = surface.HitPath(e.GetPosition(surface));
            surface.DropPath = "\u0000";
            surface.InvalidateVisual();
            if (string.IsNullOrEmpty(target) || target.StartsWith("\u0001", StringComparison.Ordinal) || target == "未分类") return;
            if (e.Data.GetData(typeof(VideoItem)) is VideoItem video) { MoveVideoToCategory(video, target); e.Handled = true; }
            else if (e.Data.GetData(typeof(string)) is string source) { MoveSourceToCategory(source, target); e.Handled = true; }
        };

        var canvas = new Canvas { Width = 1120, Height = 620, Background = Brushes.Transparent };
        canvas.Children.Add(surface);
        sphereGraphCanvas = canvas;
        var scroll = new ScrollViewer
        {
            Height = sphereManualViewportHeight ?? Math.Clamp(ActualHeight * .62, 320, 690),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false, PanningMode = PanningMode.None,
            Background = Brushes.White, Content = canvas, ClipToBounds = true
        };
        var camera = new TransformGroup();
        sphereGraphScale = new ScaleTransform(1, 1);
        sphereGraphTranslation = new TranslateTransform();
        camera.Children.Add(sphereGraphScale);
        camera.Children.Add(sphereGraphTranslation);
        canvas.RenderTransform = camera;
        sphereGraphViewport = scroll;
        scroll.SizeChanged += (_, _) => UpdateSphereViewport();
        scroll.PreviewMouseWheel += SphereGraphMouseWheel;
        scroll.PreviewMouseDown += SphereGraphMouseDown;
        scroll.PreviewMouseMove += SphereGraphMouseMove;
        scroll.PreviewMouseUp += SphereGraphMouseUp;
        scroll.LostMouseCapture += (_, _) => EndSpherePan();
        SizeChangedEventHandler resizeWindow = (_, _) => scroll.Height = sphereManualViewportHeight ?? Math.Clamp(ActualHeight * .62, 320, 690);
        scroll.Loaded += (_, _) => { SizeChanged += resizeWindow; scroll.Height = sphereManualViewportHeight ?? Math.Clamp(ActualHeight * .62, 320, 690); UpdateSphereViewport(); };
        scroll.Unloaded += (_, _) => SizeChanged -= resizeWindow;

        var header = new DockPanel { Margin = new Thickness(10, 0, 10, 10), LastChildFill = true };
        sphereRotationButton = new Button { Content = sphereRotationEnabled ? "Ⅱ  暂停旋转" : "▶  开始旋转", Style = (Style)FindResource("Action"), Padding = new Thickness(11, 0, 11, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = "开始或暂停球形图旋转" };
        sphereRotationButton.Click += ToggleSphereRotation;
        DockPanel.SetDock(sphereRotationButton, Dock.Right); header.Children.Add(sphereRotationButton);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        Button Control(string text, string hint, Action action)
        {
            var button = new Button { Content = text, Style = (Style)FindResource("Action"), Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = hint };
            button.Click += (_, _) => action();
            tools.Children.Add(button);
            return button;
        }
        Control("−", "缩小球形图", () => ZoomSphereGraph(1 / 1.2));
        sphereZoomLabel = new TextBlock { Text = "100%", Width = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(74, 96, 145)) };
        tools.Children.Add(sphereZoomLabel);
        Control("+", "放大球形图", () => ZoomSphereGraph(1.2));
        Control("适应", "重置缩放和位置", ResetSphereGraphView);
        if (selectedCategory is not "全部" and not "未分类") Control("返回上级", "查看上一级分类", () =>
        {
            var separator = selectedCategory.LastIndexOf(" / ", StringComparison.Ordinal);
            SelectSphereCategory(separator < 0 ? "" : selectedCategory[..separator]);
        });
        DockPanel.SetDock(tools, Dock.Right); header.Children.Add(tools);
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "球形智能分类图", FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(57, 79, 133)) });
        title.Children.Add(new TextBlock { Text = $"{counts.Count:N0} 个分类 + {connectedTags.Count:N0} 个相关标签全部绘出 · {relevantCount:N0} 部视频 · 连线表示层级与共现", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(130, 145, 176)), Margin = new Thickness(0, 3, 0, 0) });
        header.Children.Add(title);

        var tagOptions = new List<SphereTagOption> { new("", index.VideoCount) };
        tagOptions.AddRange(index.TagCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new SphereTagOption(pair.Key, pair.Value)));
        var tagBar = new DockPanel { Margin = new Thickness(10, 0, 10, 8) };
        tagBar.Children.Add(new TextBlock { Text = "标签关系", Foreground = new SolidColorBrush(Color.FromRgb(47, 69, 121)), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) });
        var tagPicker = new ComboBox { Width = 280, Height = 31, ItemsSource = tagOptions, IsTextSearchEnabled = true, MaxDropDownHeight = 360 };
        VirtualizingStackPanel.SetIsVirtualizing(tagPicker, true);
        VirtualizingStackPanel.SetVirtualizationMode(tagPicker, VirtualizationMode.Recycling);
        tagPicker.SelectedItem = tagOptions.First(option => string.Equals(option.Name, sphereTagFilter, StringComparison.CurrentCultureIgnoreCase));
        tagPicker.SelectionChanged += (_, _) =>
        {
            if (tagPicker.SelectedItem is not SphereTagOption option || string.Equals(option.Name, sphereTagFilter, StringComparison.CurrentCultureIgnoreCase)) return;
            sphereTagFilter = option.Name;
            sphereNavSearch = "";
            sphereNavPage = 0;
            lastCategoryRenderVersion = -1;
            RenderCategories();
        };
        tagBar.Children.Add(tagPicker);
        tagBar.Children.Add(new TextBlock { Text = "选择标签后，球面展示与它相关的全部分类及层级。密集节点可在下方搜索并定位。", Foreground = new SolidColorBrush(Color.FromRgb(124, 143, 177)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });

        var navigator = BuildSphereNavigator(nodes);
        var resizeHandle = new Grid { Height = 20, Background = Brushes.Transparent, ToolTip = "上下拖动以调整球形图高度" };
        resizeHandle.Children.Add(new Border { Width = 44, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(177, 193, 222)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        var thumb = new System.Windows.Controls.Primitives.Thumb { Background = Brushes.Transparent, Cursor = Cursors.SizeNS };
        thumb.DragDelta += (_, e) =>
        {
            sphereManualViewportHeight = Math.Clamp((sphereManualViewportHeight ?? scroll.ActualHeight) + e.VerticalChange, 260, 1000);
            scroll.Height = sphereManualViewportHeight.Value;
        };
        resizeHandle.Children.Add(thumb);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(tagBar);
        panel.Children.Add(scroll);
        panel.Children.Add(resizeHandle);
        panel.Children.Add(navigator);
        if (view == "categories" && sphereRotationEnabled) sphereGraphTimer.Start();
        return new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(216, 228, 248)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Padding = new Thickness(10), Margin = new Thickness(0, 5, 0, 16), Child = panel };
    }

    static void AddAncestors(string? path, Dictionary<string, int> counts, bool increment, HashSet<string>? seen = null)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "全部") return;
        var parts = path.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            var ancestor = string.Join(" / ", parts.Take(i));
            if (!counts.ContainsKey(ancestor)) counts[ancestor] = 0;
            if (increment && (seen is null || seen.Add(ancestor))) counts[ancestor]++;
        }
    }

    FrameworkElement BuildSphereNavigator(List<SphereMapNode> nodes)
    {
        var all = nodes.OrderByDescending(node => node.Count).ThenBy(node => node.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        var panel = new StackPanel { Margin = new Thickness(10, 1, 10, 8) };
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var pagination = new StackPanel { Orientation = Orientation.Horizontal };
        var previous = new Button { Content = "‹ 上一页", Style = (Style)FindResource("Action"), Padding = new Thickness(8, 0, 8, 0) };
        var next = new Button { Content = "下一页 ›", Style = (Style)FindResource("Action"), Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(5, 0, 0, 0) };
        var pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 7, 0), Foreground = new SolidColorBrush(Color.FromRgb(111, 130, 166)) };
        pagination.Children.Add(previous); pagination.Children.Add(pageLabel); pagination.Children.Add(next);
        DockPanel.SetDock(pagination, Dock.Right); bar.Children.Add(pagination);
        bar.Children.Add(new TextBlock { Text = "节点导航", Foreground = new SolidColorBrush(Color.FromRgb(57, 79, 133)), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var search = new TextBox { Text = sphereNavSearch, Width = 240, Height = 29, Margin = new Thickness(12, 0, 0, 0), ToolTip = "输入分类名，定位球面上拥挤的节点" };
        bar.Children.Add(search);
        panel.Children.Add(bar);
        var list = new ListBox { Height = 110, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingStackPanel.SetIsVirtualizing(list, true);
        VirtualizingStackPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        panel.Children.Add(list);
        void Update()
        {
            var filtered = sphereNavSearch.Length == 0 ? all : all.Where(node => node.Label.Contains(sphereNavSearch, StringComparison.CurrentCultureIgnoreCase) || node.Path.Contains(sphereNavSearch, StringComparison.CurrentCultureIgnoreCase)).ToList();
            const int pageSize = 24;
            var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
            sphereNavPage = Math.Clamp(sphereNavPage, 0, pages - 1);
            list.ItemsSource = filtered.Skip(sphereNavPage * pageSize).Take(pageSize).Select(node => new SphereNavItem(node.Path, node.IsTag ? "标签  " + node.Label : "分类  " + node.Path, node.Count)).ToList();
            pageLabel.Text = $"{sphereNavPage + 1}/{pages} · {filtered.Count:N0} 个";
            previous.IsEnabled = sphereNavPage > 0;
            next.IsEnabled = sphereNavPage < pages - 1;
        }
        previous.Click += (_, _) => { sphereNavPage--; Update(); };
        next.Click += (_, _) => { sphereNavPage++; Update(); };
        search.TextChanged += (_, _) => { sphereNavSearch = search.Text.Trim(); sphereNavPage = 0; Update(); };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not SphereNavItem item) return;
            NavigateSphereNode(item.Path);
        };
        Update();
        return panel;
    }

    void NavigateSphereNode(string path)
    {
        if (path.StartsWith("\u0001", StringComparison.Ordinal))
        {
            sphereTagFilter = path[1..];
            sphereNavSearch = "";
            sphereNavPage = 0;
            lastCategoryRenderVersion = -1;
            RenderCategories();
            return;
        }
        SelectSphereCategory(path);
    }

    void SelectSphereCategory(string path)
    {
        selectedCategory = path.Length == 0 ? "全部" : path;
        pageIndex = 0;
        lastRenderedSelectedCategory = selectedCategory;
        if (sphereMapSurface is not null)
        {
            sphereMapSurface.SelectedPath = path;
            sphereMapSurface.InvalidateVisual();
        }
        foreach (var (name, chip) in sphereCategoryChips)
        {
            var active = name.Equals(selectedCategory, StringComparison.CurrentCultureIgnoreCase);
            chip.Background = new SolidColorBrush(active ? Color.FromRgb(234, 240, 255) : Colors.White);
            chip.Foreground = new SolidColorBrush(active ? Color.FromRgb(62, 99, 213) : Color.FromRgb(70, 83, 102));
        }
        RenderCards();
        LibraryScroll.ScrollToTop();
    }
}
