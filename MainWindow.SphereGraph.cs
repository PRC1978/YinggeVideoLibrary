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
        public long Version { get; set; }
        public required Dictionary<string, int> AllCategoryCounts { get; init; }
        public required Dictionary<string, int> TagCounts { get; init; }
        public required Dictionary<string, Dictionary<string, int>> CategoriesByTag { get; init; }
        public required Dictionary<string, Dictionary<string, int>> RelatedTags { get; init; }
        public required Dictionary<string, List<VideoItem>> VideosByTag { get; init; }
        public int VideoCount { get; set; }
        public required Dictionary<string, GraphVideoSnapshot> VideosByPath { get; init; }
        public required HashSet<string> ConfiguredAncestors { get; init; }
    }

    // A small category or tag edit should not recount every relationship in a
    // large library. Keep the exact contribution of each video, then subtract
    // and add only changed contributions when categoryDataVersion advances.
    sealed class GraphVideoSnapshot
    {
        public required VideoItem Video { get; init; }
        public required string[] CategoryValues { get; init; }
        public required string[] RawTags { get; init; }
        public required string[] Ancestors { get; init; }
        public required string[] Tags { get; init; }

        public bool Matches(VideoItem item)
        {
            if (!ReferenceEquals(Video, item)) return false;
            if (item.CategoryList.Count == 0)
            {
                if (CategoryValues.Length != 1 || !string.Equals(CategoryValues[0], item.Category, StringComparison.Ordinal)) return false;
            }
            else
            {
                if (CategoryValues.Length != item.CategoryList.Count) return false;
                for (var index = 0; index < CategoryValues.Length; index++)
                    if (!string.Equals(CategoryValues[index], item.CategoryList[index], StringComparison.Ordinal)) return false;
            }
            if (RawTags.Length != item.Tags.Count) return false;
            for (var index = 0; index < RawTags.Length; index++)
                if (!string.Equals(RawTags[index], item.Tags[index], StringComparison.Ordinal)) return false;
            return true;
        }
    }

    // Capture mutable WPF-side video fields before dispatching the expensive
    // relationship build. The worker only reads these immutable arrays.
    sealed record GraphVideoInput(VideoItem Video, string Path, string[] CategoryValues, string[] RawTags);

    sealed record SphereTagOption(string Name, int Count)
    {
        public override string ToString() => Name.Length == 0 ? $"全部标签 · {Count:N0} 部" : $"#{Name} · {Count:N0} 部";
    }

    sealed record SphereNavItem(string Path, string Label, int Count)
    {
        public string Display => $"{Label}   ·   {Count:N0} 部";
        public override string ToString() => Display;
    }

    // One WPF visual draws every category and edge. A large library still has
    // only one graph element, and rotation invalidates paint without rebuilding
    // a visual tree of cards, drop shadows and Line controls on every frame.
    sealed class SphereMapSurface : FrameworkElement
    {
        sealed record Association(string TagPath, string CategoryPath, int Count);

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
        static readonly Pen AssociationPen = FrozenPen(Color.FromArgb(195, 36, 151, 150), 1.45);
        static readonly Pen NodePen = FrozenPen(Color.FromRgb(234, 241, 255), 1);
        static readonly Pen GlobePen = FrozenPen(Color.FromRgb(181, 205, 245), 1.5);
        static readonly Pen RootPen = FrozenPen(Color.FromRgb(245, 248, 255), 2);
        static readonly Pen OrbitPen = FrozenPen(Color.FromArgb(110, 121, 154, 211), 1);
        static readonly Brush GlobeBrush = MakeGlobeBrush();
        static readonly Typeface LabelTypeface = new("Microsoft YaHei UI");
        readonly List<SphereMapNode> nodes;
        readonly (SphereMapNode Node, double Cos, double Sin)[] polarNodes;
        readonly SphereMapNode[] rankedLabels;
        readonly ImageSource baseLayer;
        readonly Dictionary<string, System.Windows.Point> basePoints = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<(int X, int Y), List<SphereMapNode>> hitCells = [];
        readonly Dictionary<string, SphereMapNode> byPath;
        readonly Dictionary<string, List<SphereMapNode>> childrenByParent = new(StringComparer.OrdinalIgnoreCase);
        readonly IReadOnlyDictionary<string, Dictionary<string, int>> tagCategories;
        readonly IReadOnlyDictionary<string, int>? rootTagCategories;
        readonly Dictionary<string, List<Association>> associationsByCategory = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, FormattedText> normalLabels = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, FormattedText> selectedLabels = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> focusedBranches = new(StringComparer.OrdinalIgnoreCase);
        string cachedBranchSelection = "\u0000";
        string cachedBranchHover = "\u0000";
        string cachedBranchDrop = "\u0000";
        string cachedAssociationFocus = "\u0000";
        List<Association> cachedAssociations = [];
        Geometry? focusedLinkGeometry;
        Geometry? associationGeometry;
        public int FocusGeometryBuilds { get; private set; }
        public int AssociationGeometryBuilds { get; private set; }
        public string HoverPath { get; private set; } = "\u0000";
        public string SelectedPath { get; set; } = "";
        public string DropPath { get; set; } = "\u0000";
        public string RootLabel { get; set; } = "全部分类";
        public int RootCount { get; set; }
        public double Rotation { get; set; }
        public double ZoomLevel { get; set; } = 1;
        public int NodeCount => nodes.Count;
        public double AssociationBuildMs { get; private set; }
        public double BaseLayerBuildMs { get; private set; }
        public bool EveryNodeLinked => nodes.All(node => node.Parent.Length == 0 || byPath.ContainsKey(node.Parent));
        public int FocusAssociationCount => FocusAssociations().Count;
        public bool IsBranchHighlighted(string path)
        {
            RefreshFocusedBranches();
            return focusedBranches.Contains(path);
        }

        public SphereMapSurface(List<SphereMapNode> nodes, IReadOnlyDictionary<string, Dictionary<string, int>> tagCategories,
            IReadOnlyDictionary<string, int>? rootTagCategories = null)
        {
            var constructionClock = System.Diagnostics.Stopwatch.StartNew();
            this.nodes = nodes;
            this.tagCategories = tagCategories;
            this.rootTagCategories = rootTagCategories;
            Width = 1120;
            Height = 620;
            polarNodes = nodes.Select(node => (node, Math.Cos(node.Angle), Math.Sin(node.Angle))).ToArray();
            byPath = nodes.ToDictionary(node => node.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                if (!childrenByParent.TryGetValue(node.Parent, out var siblings))
                    childrenByParent[node.Parent] = siblings = [];
                siblings.Add(node);
            }
            var center = new System.Windows.Point(Width / 2, Height / 2 + 7);
            basePoints[""] = center;
            foreach (var (node, cosine, sine) in polarNodes)
                basePoints[node.Path] = new System.Windows.Point(
                    center.X + cosine * node.Radius * 246,
                    center.Y + sine * node.Radius * 232);
            BuildHitCells();
            foreach (var (tag, categories) in tagCategories)
            {
                var tagPath = "\u0001" + tag;
                if (!byPath.ContainsKey(tagPath)) continue;
                foreach (var (category, count) in categories)
                {
                    if (!byPath.ContainsKey(category)) continue;
                    if (!associationsByCategory.TryGetValue(category, out var entries))
                        associationsByCategory[category] = entries = [];
                    entries.Add(new Association(tagPath, category, count));
                }
            }
            AssociationBuildMs = constructionClock.Elapsed.TotalMilliseconds;
            rankedLabels = nodes.OrderByDescending(node => node.Count)
                .ThenBy(node => node.Depth).ThenBy(node => node.Label, StringComparer.CurrentCultureIgnoreCase)
                .Take(120).ToArray();
            AllowDrop = true;
            Cursor = Cursors.Hand;
            ToolTip = "点击节点筛选 · 拖动分类节点到目标节点 · 可把视频拖到节点";
            var layerStarted = constructionClock.Elapsed.TotalMilliseconds;
            baseLayer = BuildBaseLayer();
            BaseLayerBuildMs = constructionClock.Elapsed.TotalMilliseconds - layerStarted;
        }

        ImageSource BuildBaseLayer()
        {
            var center = basePoints[""];
            var layer = new DrawingVisual();
            using (var dc = layer.RenderOpen())
            {
                foreach (var node in nodes)
                {
                    if (!basePoints.TryGetValue(node.Parent, out var parent)) parent = center;
                    dc.DrawLine(LinkPen, parent, basePoints[node.Path]);
                }
                foreach (var node in nodes)
                {
                    var point = basePoints[node.Path];
                    var radius = NodeRadius(node);
                    dc.DrawEllipse(node.IsTag ? TagBrush : node.Count == 0 ? NodeLightBrush : NodeBrush,
                        NodePen, point, radius, radius);
                }
            }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(layer);
            bitmap.Freeze();
            return bitmap;
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

        public static Matrix RotationMatrix(System.Windows.Point center, double angle)
        {
            var cosine = Math.Cos(angle);
            var sine = Math.Sin(angle);
            return new Matrix(
                cosine, (232.0 / 246.0) * sine,
                -(246.0 / 232.0) * sine, cosine,
                center.X - center.X * cosine + center.Y * (246.0 / 232.0) * sine,
                center.Y - center.X * (232.0 / 246.0) * sine - center.Y * cosine);
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
            if (string.Equals(HoverPath, path, StringComparison.OrdinalIgnoreCase)) return;
            HoverPath = path;
            var associations = FocusAssociations();
            ToolTip = path == "\u0000" ? "点击节点筛选 · 拖动视频或分类到节点" : path.Length == 0
                ? $"{RootLabel} · {RootCount:N0} 部视频 · {associations.Count:N0} 条分类关联" :
                $"{(byPath[path].IsTag ? "#" + byPath[path].TagName : path)} · {byPath[path].Count:N0} 部视频 · {associations.Count:N0} 条真实标签关联";
            InvalidateVisual();
        }

        // In the complete graph the index already contains exact tag/category
        // intersections. In a selected-tag graph the caller supplies only
        // intersections occurring on videos that carry the selected tag.
        // Resolve them once per hover/selection, not on each rotation frame.
        List<Association> FocusAssociations()
        {
            var focus = HoverPath == "\u0000" ? SelectedPath : HoverPath;
            if (string.Equals(focus, cachedAssociationFocus, StringComparison.OrdinalIgnoreCase))
                return cachedAssociations;
            cachedAssociationFocus = focus;
            cachedAssociations = [];
            associationGeometry = null;
            if (focus.Length == 0 && rootTagCategories is not null)
            {
                foreach (var (category, count) in rootTagCategories)
                    if (byPath.ContainsKey(category)) cachedAssociations.Add(new Association("", category, count));
            }
            else if (focus.StartsWith("\u0001", StringComparison.Ordinal))
            {
                if (tagCategories.TryGetValue(focus[1..], out var categories))
                    foreach (var (category, count) in categories)
                        if (byPath.ContainsKey(category)) cachedAssociations.Add(new Association(focus, category, count));
            }
            else if (focus.Length > 0 && byPath.ContainsKey(focus))
            {
                if (rootTagCategories is not null && rootTagCategories.TryGetValue(focus, out var rootCount))
                    cachedAssociations.Add(new Association("", focus, rootCount));
                if (associationsByCategory.TryGetValue(focus, out var entries)) cachedAssociations.AddRange(entries);
            }
            return cachedAssociations;
        }

        public string? HitPath(System.Windows.Point point)
        {
            var root = basePoints[""];
            if ((root - point).LengthSquared <= 44 * 44) return "";
            // The full hit grid is built once in the unrotated coordinate space.
            // Transform the pointer backwards instead of reprojecting every
            // category on each mouse move or animation frame.
            var rotation = RotationMatrix(root, Rotation);
            var inverse = rotation;
            inverse.Invert();
            var unrotatedPoint = inverse.Transform(point);
            string? nearest = null;
            var nearestDistanceSquared = double.MaxValue;
            var cellX = (int)Math.Floor(unrotatedPoint.X / 32);
            var cellY = (int)Math.Floor(unrotatedPoint.Y / 32);
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            {
                if (!hitCells.TryGetValue((cellX + offsetX, cellY + offsetY), out var candidates)) continue;
                foreach (var node in candidates)
                {
                    var distanceSquared = (rotation.Transform(basePoints[node.Path]) - point).LengthSquared;
                    var hitRadius = Math.Max(11, NodeRadius(node) + 6);
                    if (distanceSquared <= hitRadius * hitRadius && distanceSquared < nearestDistanceSquared)
                    {
                        nearest = node.Path;
                        nearestDistanceSquared = distanceSquared;
                    }
                }
            }
            return nearest;
        }

        void BuildHitCells()
        {
            hitCells.Clear();
            foreach (var node in nodes)
            {
                var point = basePoints[node.Path];
                var cell = ((int)Math.Floor(point.X / 32), (int)Math.Floor(point.Y / 32));
                if (!hitCells.TryGetValue(cell, out var candidates)) hitCells[cell] = candidates = [];
                candidates.Add(node);
            }
        }

        double NodeRadius(SphereMapNode node) => Math.Clamp(2.4 + Math.Log10(node.Count + 1) * 1.7, 2.5, 9);

        static bool InBranch(string path, string focus) => focus.Length > 0 &&
            (path.Equals(focus, StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(focus + " / ", StringComparison.OrdinalIgnoreCase) ||
             focus.StartsWith(path + " / ", StringComparison.OrdinalIgnoreCase));

        void RefreshFocusedBranches()
        {
            if (cachedBranchSelection == SelectedPath && cachedBranchHover == HoverPath && cachedBranchDrop == DropPath) return;
            cachedBranchSelection = SelectedPath;
            cachedBranchHover = HoverPath;
            cachedBranchDrop = DropPath;
            focusedLinkGeometry = null;
            focusedBranches.Clear();
            void Include(string focus)
            {
                if (focus.Length == 0 || focus == "\u0000") return;
                if (!byPath.TryGetValue(focus, out var start))
                {
                    // A stale selected category can remain after a rescan.
                    // Preserve the old prefix behavior in that rare case.
                    foreach (var node in nodes)
                        if (InBranch(node.Path, focus)) focusedBranches.Add(node.Path);
                    return;
                }
                var ancestor = start;
                while (true)
                {
                    focusedBranches.Add(ancestor.Path);
                    if (ancestor.Parent.Length == 0 || !byPath.TryGetValue(ancestor.Parent, out var parent)) break;
                    ancestor = parent;
                }
                var pending = new Stack<SphereMapNode>();
                pending.Push(start);
                while (pending.Count > 0)
                {
                    var node = pending.Pop();
                    if (!childrenByParent.TryGetValue(node.Path, out var children)) continue;
                    foreach (var child in children)
                    {
                        focusedBranches.Add(child.Path);
                        pending.Push(child);
                    }
                }
            }
            Include(SelectedPath);
            Include(HoverPath);
            Include(DropPath);
        }

        FormattedText LabelText(SphereMapNode node, bool selected)
        {
            var cache = selected ? selectedLabels : normalLabels;
            if (cache.TryGetValue(node.Path, out var text)) return text;
            text = new FormattedText(node.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelTypeface, 11, selected ? HoverBrush : TextBrush, 1)
            { MaxTextWidth = 122, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            cache[node.Path] = text;
            return text;
        }

        Geometry FocusedLinkGeometry()
        {
            if (focusedLinkGeometry is not null) return focusedLinkGeometry;
            var lines = new StreamGeometry();
            using (var context = lines.Open())
                foreach (var path in focusedBranches)
                {
                    var node = byPath[path];
                    context.BeginFigure(basePoints[node.Parent], false, false);
                    context.LineTo(basePoints[node.Path], true, false);
                }
            lines.Freeze();
            FocusGeometryBuilds++;
            return focusedLinkGeometry = lines;
        }

        Geometry AssociationLinkGeometry(IReadOnlyList<Association> associations)
        {
            if (associationGeometry is not null) return associationGeometry;
            var lines = new StreamGeometry();
            using (var context = lines.Open())
                foreach (var association in associations)
                {
                    context.BeginFigure(basePoints[association.TagPath], false, false);
                    context.LineTo(basePoints[association.CategoryPath], true, false);
                }
            lines.Freeze();
            AssociationGeometryBuilds++;
            return associationGeometry = lines;
        }

        public (int Focus, int Associations) SnapshotGeometryCacheForDiagnostics()
        {
            RefreshFocusedBranches();
            if (focusedBranches.Count > 0) _ = FocusedLinkGeometry();
            var associations = FocusAssociations();
            if (associations.Count > 0) _ = AssociationLinkGeometry(associations);
            return (FocusGeometryBuilds, AssociationGeometryBuilds);
        }

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

            RefreshFocusedBranches();
            var rotation = RotationMatrix(center, Rotation);
            System.Windows.Point Position(string path) => rotation.Transform(basePoints[path]);
            // The full topology is rasterized once on graph construction. A
            // matrix rotates the cached layer around the globe while labels,
            // hit testing and focused relationships remain crisp and live.
            dc.PushTransform(new MatrixTransform(rotation));
            dc.DrawImage(baseLayer, bounds);
            if (focusedBranches.Count > 0)
                dc.DrawGeometry(null, FocusLinkPen, FocusedLinkGeometry());

            // Cross-links are shown when a category or tag is focused. Drawing
            // all such lines permanently would bury the hierarchy in large
            // libraries; every relationship remains available on hover.
            var associations = FocusAssociations();
            if (associations.Count > 0)
                dc.DrawGeometry(null, AssociationPen, AssociationLinkGeometry(associations));
            dc.Pop();
            void DrawFocusedNode(string path, bool active)
            {
                if (!byPath.TryGetValue(path, out var node)) return;
                var point = Position(path);
                var radius = NodeRadius(node);
                dc.DrawEllipse(HaloBrush, null, point, radius + 7, radius + 7);
                dc.DrawEllipse(active ? SelectedBrush : HoverBrush,
                    NodePen, point, radius + 2, radius + 2);
            }
            DrawFocusedNode(SelectedPath, true);
            if (!string.Equals(DropPath, SelectedPath, StringComparison.OrdinalIgnoreCase)) DrawFocusedNode(DropPath, true);
            if (!string.Equals(HoverPath, SelectedPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(HoverPath, DropPath, StringComparison.OrdinalIgnoreCase)) DrawFocusedNode(HoverPath, false);
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
                var point = Position(node.Path);
                var text = LabelText(node, node.Path.Equals(SelectedPath, StringComparison.OrdinalIgnoreCase));
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
                !string.Equals(SelectedPath, HoverPath, StringComparison.OrdinalIgnoreCase)) DrawLabel(selectedNode);
            foreach (var node in rankedLabels)
            {
                if (drawn >= labelLimit) break;
                if (node.Path.Equals(HoverPath, StringComparison.OrdinalIgnoreCase) ||
                    node.Path.Equals(SelectedPath, StringComparison.OrdinalIgnoreCase)) continue;
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
    string sphereGraphBuildProfile = "";
    string categoryRenderProfile = "";
    string sphereIndexWarmProfile = "未完成";
    SphereGraphIndex? sphereGraphIndex;
    CancellationTokenSource? sphereIndexBuildCancellation;
    Task? sphereIndexBuildTask;
    long sphereIndexBuildRequest;
    long sphereIndexBuildVersion = -1;
    int sphereIndexBuildScanVersion = -1;
    List<VideoItem>? sphereIndexBuildVideos;
    sealed record SphereTagProjection(SphereGraphIndex Index, long Version, string Tag,
        Dictionary<string, Dictionary<string, int>> Links);
    SphereTagProjection? sphereTagProjection;
    CancellationTokenSource? sphereTagBuildCancellation;
    Task? sphereTagBuildTask;
    long sphereTagBuildRequest;
    SphereGraphIndex? sphereTagBuildIndex;
    long sphereTagBuildVersion = -1;
    string sphereTagBuildName = "";
    readonly Dictionary<string, Button> sphereCategoryChips = new(StringComparer.OrdinalIgnoreCase);
    ListBox? sphereCategoryQuickList;
    bool sphereSyncingCategoryList;
    string sphereTagFilter = "";
    string sphereNavSearch = "";
    int sphereNavPage;
    string? sphereMouseDownPath;
    System.Windows.Point sphereMouseDownPoint;

    // Config writes are much more frequent than changes to the graph's input.
    // Compare the persisted relationship fields after the merge so a setting
    // save leaves both the index and the rendered category workbench intact.
    // The scan and incremental index paths advance categoryDataVersion when
    // the set of VideoItem objects itself changes.
    static bool GraphRelevantConfigEquals(LibraryConfig left, LibraryConfig right) =>
        left.AutoCategorize == right.AutoCategorize &&
        left.FoldersClearedByUser == right.FoldersClearedByUser &&
        left.Folders.SequenceEqual(right.Folders, StringComparer.OrdinalIgnoreCase) &&
        left.CategoryNames.SequenceEqual(right.CategoryNames, StringComparer.Ordinal) &&
        SameGraphStringMap(left.Categories, right.Categories) &&
        SameGraphStringMap(left.CategoryRenames, right.CategoryRenames) &&
        SameGraphListMap(left.MultiCategories, right.MultiCategories) &&
        SameGraphListMap(left.Tags, right.Tags);

    static bool SameGraphStringMap(Dictionary<string, string> left, Dictionary<string, string> right)
    {
        if (left.Count != right.Count) return false;
        // MergeLibraryConfig builds right with the library's ordinal path
        // comparer, so lookup remains linear overall even for large libraries.
        foreach (var (key, value) in left)
            if (!right.TryGetValue(key, out var other) ||
                !string.Equals(value, other, StringComparison.Ordinal)) return false;
        return true;
    }

    static bool SameGraphListMap(Dictionary<string, List<string>> left, Dictionary<string, List<string>> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (key, values) in left)
        {
            if (!right.TryGetValue(key, out var other)) return false;
            if (values.Count != other.Count) return false;
            if (!values.SequenceEqual(other, StringComparer.Ordinal) &&
                !new HashSet<string>(values, StringComparer.Ordinal).SetEquals(other)) return false;
        }
        return true;
    }

    SphereGraphIndex GetSphereGraphIndex()
    {
        if (sphereGraphIndex is { } cached && cached.Version == categoryDataVersion && cached.VideoCount == videos.Count)
            return cached;
        if (sphereGraphIndex is { } previous && TryUpdateSphereGraphIndex(previous, videos, config.CategoryNames, categoryDataVersion))
            return previous;
        return sphereGraphIndex = BuildSphereGraphIndex(videos, config.CategoryNames, categoryDataVersion);
    }

    SphereGraphIndex? GetSphereGraphIndexForUi()
    {
        if (sphereGraphIndex is { } cached && cached.Version == categoryDataVersion && cached.VideoCount == videos.Count)
            return cached;
        if (sphereIndexBuildTask is { IsCompleted: false } && sphereIndexBuildVersion == categoryDataVersion &&
            sphereIndexBuildScanVersion == scanVersion && ReferenceEquals(sphereIndexBuildVideos, videos))
            return null;
        if (sphereGraphIndex is { } previous && TryUpdateSphereGraphIndex(previous, videos, config.CategoryNames, categoryDataVersion))
            return previous;
        ScheduleSphereGraphIndexBuild();
        return null;
    }

    void CancelSphereGraphIndexBuild()
    {
        CancelSphereTagProjection();
        sphereIndexBuildRequest++;
        try { sphereIndexBuildCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        sphereIndexBuildCancellation = null;
        sphereIndexBuildTask = null;
        sphereIndexBuildVideos = null;
        sphereIndexBuildVersion = -1;
        sphereIndexBuildScanVersion = -1;
    }

    void ScheduleSphereGraphIndexBuild()
    {
        if (sphereIndexBuildTask is { IsCompleted: false } &&
            sphereIndexBuildVersion == categoryDataVersion &&
            sphereIndexBuildScanVersion == scanVersion &&
            ReferenceEquals(sphereIndexBuildVideos, videos)) return;
        CancelSphereGraphIndexBuild();
        var request = sphereIndexBuildRequest;
        var version = categoryDataVersion;
        var scan = scanVersion;
        var source = videos;
        var captureClock = System.Diagnostics.Stopwatch.StartNew();
        var warmStartWorkingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        var input = CaptureGraphInputs(source);
        var names = config.CategoryNames.ToArray();
        var captureMs = captureClock.Elapsed.TotalMilliseconds;
        var cancellation = new CancellationTokenSource();
        sphereIndexBuildCancellation = cancellation;
        sphereIndexBuildVersion = version;
        sphereIndexBuildScanVersion = scan;
        sphereIndexBuildVideos = source;
        sphereIndexBuildTask = BuildSphereGraphIndexAsync(input, names, source, version, scan,
            request, captureMs, warmStartWorkingSet, cancellation);
    }

    static bool AcceptSphereBuild(long request, long activeRequest, long version, long currentVersion,
        int scan, int currentScan, bool sameVideos, bool closing) =>
        !closing && request == activeRequest &&
        version == currentVersion && scan == currentScan && sameVideos;

    async Task BuildSphereGraphIndexAsync(GraphVideoInput[] input, string[] names,
        List<VideoItem> source, long version, int scan, long request,
        double captureMs, long warmStartWorkingSet, CancellationTokenSource cancellation)
    {
        // Let the scheduler store this task before a tiny library can finish.
        await Task.Yield();
        try
        {
            var buildClock = System.Diagnostics.Stopwatch.StartNew();
            var built = await Task.Run(() => BuildSphereGraphIndex(input, names, version, cancellation.Token),
                cancellation.Token);
            if (!AcceptSphereBuild(request, sphereIndexBuildRequest, version, categoryDataVersion,
                    scan, scanVersion, ReferenceEquals(source, videos), isClosing) ||
                cancellation.IsCancellationRequested) return;
            sphereGraphIndex = built;
            var process = System.Diagnostics.Process.GetCurrentProcess();
            sphereIndexWarmProfile = $"快照 {captureMs:F1} ms，后台构建 {buildClock.Elapsed.TotalMilliseconds:F1} ms，工作集 {warmStartWorkingSet / 1048576d:F1}→{process.WorkingSet64 / 1048576d:F1} MiB，进程峰值 {process.PeakWorkingSet64 / 1048576d:F1} MiB";
            Log($"分类关系索引已在后台完成：{input.Length:N0} 部，快照 {captureMs:F1} ms，构建 {buildClock.Elapsed.TotalMilliseconds:F1} ms");
            if (view == "categories") RenderCategories();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (request == sphereIndexBuildRequest && view == "categories" && !isClosing)
            {
                Log("分类关系索引构建失败：" + error.Message);
                CategoryPanel.Children.Clear();
                CategoryPanel.Children.Add(new TextBlock { Text = "分类关系暂时无法生成。请刷新视频库后重试：" + error.Message,
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed });
                // The videos remain browsable even if graph construction fails.
                StartCategoryCardPopulation(allowWithoutGraph: true);
            }
        }
        finally
        {
            if (request == sphereIndexBuildRequest)
            {
                sphereIndexBuildCancellation = null;
                sphereIndexBuildTask = null;
                sphereIndexBuildVideos = null;
                sphereIndexBuildVersion = -1;
                sphereIndexBuildScanVersion = -1;
            }
            cancellation.Dispose();
        }
    }

    internal async Task WaitForSphereGraphBuildAsync()
    {
        if (sphereIndexBuildTask is { } indexTask) await indexTask;
        if (sphereTagBuildTask is { } tagTask) await tagTask;
    }

    void CancelSphereTagProjection()
    {
        sphereTagBuildRequest++;
        try { sphereTagBuildCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        sphereTagBuildCancellation = null;
        sphereTagBuildTask = null;
        sphereTagBuildIndex = null;
        sphereTagBuildVersion = -1;
        sphereTagBuildName = "";
        sphereTagProjection = null;
    }

    Dictionary<string, Dictionary<string, int>>? GetSelectedTagLinksForUi(SphereGraphIndex index,
        string tag, IReadOnlyDictionary<string, int> related)
    {
        if (sphereTagProjection is { } cached && ReferenceEquals(cached.Index, index) &&
            cached.Version == index.Version && string.Equals(cached.Tag, tag, StringComparison.CurrentCultureIgnoreCase))
            return cached.Links;
        if (sphereTagBuildTask is { IsCompleted: false } && ReferenceEquals(sphereTagBuildIndex, index) &&
            sphereTagBuildVersion == index.Version && string.Equals(sphereTagBuildName, tag, StringComparison.CurrentCultureIgnoreCase))
            return null;
        CancelSphereTagProjection();
        var request = sphereTagBuildRequest;
        var version = index.Version;
        var scan = scanVersion;
        var captureClock = System.Diagnostics.Stopwatch.StartNew();
        var input = CaptureGraphInputs(index.VideosByTag[tag]);
        var visible = related.Keys.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var captureMs = captureClock.Elapsed.TotalMilliseconds;
        var cancellation = new CancellationTokenSource();
        sphereTagBuildCancellation = cancellation;
        sphereTagBuildIndex = index;
        sphereTagBuildVersion = version;
        sphereTagBuildName = tag;
        sphereTagBuildTask = BuildSphereTagProjectionAsync(input, visible, index, tag, version,
            scan, request, captureMs, cancellation);
        return null;
    }

    async Task BuildSphereTagProjectionAsync(GraphVideoInput[] input, HashSet<string> visible,
        SphereGraphIndex index, string tag, long version, int scan, long request,
        double captureMs, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var links = await Task.Run(() => BuildSelectedTagCategoryLinks(input, visible, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested || request != sphereTagBuildRequest ||
                !ReferenceEquals(index, sphereGraphIndex) || version != index.Version ||
                scan != scanVersion || view != "categories" || isClosing ||
                !string.Equals(tag, sphereTagFilter, StringComparison.CurrentCultureIgnoreCase)) return;
            sphereTagProjection = new(index, version, tag, links);
            Log($"标签关系已在后台完成：{input.Length:N0} 部，快照 {captureMs:F1} ms，投影 {clock.Elapsed.TotalMilliseconds:F1} ms");
            lastCategoryRenderVersion = -1;
            RenderCategories();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (request == sphereTagBuildRequest && view == "categories" && !isClosing)
                Log("标签关系构建失败：" + error.Message);
        }
        finally
        {
            if (request == sphereTagBuildRequest)
            {
                sphereTagBuildCancellation = null;
                sphereTagBuildTask = null;
                sphereTagBuildIndex = null;
                sphereTagBuildVersion = -1;
                sphereTagBuildName = "";
            }
            cancellation.Dispose();
        }
    }

    static HashSet<string> ConfiguredGraphAncestors(IEnumerable<string> categoryNames)
    {
        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in categoryNames)
            foreach (var ancestor in CategoryAncestors(name)) configured.Add(ancestor);
        return configured;
    }

    static GraphVideoInput CaptureGraphInput(VideoItem video) => new(video, video.Path,
        CategoriesOf(video).ToArray(), video.Tags.ToArray());

    static GraphVideoInput[] CaptureGraphInputs(IReadOnlyList<VideoItem> videos)
    {
        var result = new GraphVideoInput[videos.Count];
        for (var index = 0; index < videos.Count; index++) result[index] = CaptureGraphInput(videos[index]);
        return result;
    }

    static GraphVideoSnapshot SnapshotGraphVideo(VideoItem video, Dictionary<string, string[]> ancestorCache) =>
        SnapshotGraphVideo(CaptureGraphInput(video), ancestorCache);

    static GraphVideoSnapshot SnapshotGraphVideo(GraphVideoInput input, Dictionary<string, string[]> ancestorCache)
    {
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in input.CategoryValues)
            foreach (var ancestor in CachedCategoryAncestors(category, ancestorCache)) ancestors.Add(ancestor);
        return new GraphVideoSnapshot
        {
            Video = input.Video,
            CategoryValues = input.CategoryValues,
            RawTags = input.RawTags,
            Ancestors = ancestors.ToArray(),
            Tags = input.RawTags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray()
        };
    }

    static void ApplyGraphVideo(SphereGraphIndex index, GraphVideoSnapshot snapshot, int change)
    {
        foreach (var ancestor in snapshot.Ancestors)
        {
            var count = index.AllCategoryCounts.GetValueOrDefault(ancestor) + change;
            // Zero-count configured nodes are retained after the entire delta.
            index.AllCategoryCounts[ancestor] = count;
        }
        foreach (var tag in snapshot.Tags)
        {
            var count = index.TagCounts.GetValueOrDefault(tag) + change;
            if (count == 0)
            {
                index.TagCounts.Remove(tag);
                index.CategoriesByTag.Remove(tag);
                index.RelatedTags.Remove(tag);
            }
            else index.TagCounts[tag] = count;
            if (change > 0)
            {
                if (!index.CategoriesByTag.TryGetValue(tag, out var categoryMap))
                    index.CategoriesByTag[tag] = categoryMap = new(StringComparer.OrdinalIgnoreCase);
                foreach (var ancestor in snapshot.Ancestors)
                    categoryMap[ancestor] = categoryMap.GetValueOrDefault(ancestor) + 1;
                if (!index.RelatedTags.ContainsKey(tag))
                    index.RelatedTags[tag] = new(StringComparer.CurrentCultureIgnoreCase);
                if (!index.VideosByTag.TryGetValue(tag, out var taggedVideos))
                    index.VideosByTag[tag] = taggedVideos = [];
                taggedVideos.Add(snapshot.Video);
            }
            else
            {
                // Remove the relationships before dropping an emptied tag.
                if (index.CategoriesByTag.TryGetValue(tag, out var categoryMap))
                    foreach (var ancestor in snapshot.Ancestors)
                    {
                        var value = categoryMap.GetValueOrDefault(ancestor) - 1;
                        if (value == 0) categoryMap.Remove(ancestor);
                        else categoryMap[ancestor] = value;
                    }
                if (index.VideosByTag.TryGetValue(tag, out var taggedVideos))
                {
                    taggedVideos.Remove(snapshot.Video);
                    if (taggedVideos.Count == 0) index.VideosByTag.Remove(tag);
                }
            }
        }
        for (var leftIndex = 0; leftIndex < snapshot.Tags.Length; leftIndex++)
            for (var rightIndex = leftIndex + 1; rightIndex < snapshot.Tags.Length; rightIndex++)
            {
                var leftTag = snapshot.Tags[leftIndex];
                var rightTag = snapshot.Tags[rightIndex];
                if (change > 0)
                {
                    var left = index.RelatedTags[leftTag];
                    var right = index.RelatedTags[rightTag];
                    left[rightTag] = left.GetValueOrDefault(rightTag) + 1;
                    right[leftTag] = right.GetValueOrDefault(leftTag) + 1;
                }
                else
                {
                    // The last contribution can already have removed a tag's
                    // map; all reciprocal references disappear with it.
                    if (index.RelatedTags.TryGetValue(leftTag, out var left))
                    {
                        var value = left.GetValueOrDefault(rightTag) - 1;
                        if (value <= 0) left.Remove(rightTag); else left[rightTag] = value;
                    }
                    if (index.RelatedTags.TryGetValue(rightTag, out var right))
                    {
                        var value = right.GetValueOrDefault(leftTag) - 1;
                        if (value <= 0) right.Remove(leftTag); else right[leftTag] = value;
                    }
                }
            }
    }

    static SphereGraphIndex BuildSphereGraphIndex(IReadOnlyList<VideoItem> source,
        IEnumerable<string> categoryNames, long version) =>
        BuildSphereGraphIndex(CaptureGraphInputs(source), categoryNames, version, CancellationToken.None);

    static SphereGraphIndex BuildSphereGraphIndex(IReadOnlyList<GraphVideoInput> source,
        IEnumerable<string> categoryNames, long version, CancellationToken cancellation)
    {
        var configured = ConfiguredGraphAncestors(categoryNames);
        var all = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tagCounts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var categoriesByTag = new Dictionary<string, Dictionary<string, int>>(StringComparer.CurrentCultureIgnoreCase);
        var relatedTags = new Dictionary<string, Dictionary<string, int>>(StringComparer.CurrentCultureIgnoreCase);
        var videosByTag = new Dictionary<string, List<VideoItem>>(StringComparer.CurrentCultureIgnoreCase);
        var byPath = new Dictionary<string, GraphVideoSnapshot>(source.Count, StringComparer.OrdinalIgnoreCase);
        var index = new SphereGraphIndex
        {
            Version = version, VideoCount = source.Count, AllCategoryCounts = all,
            TagCounts = tagCounts, CategoriesByTag = categoriesByTag, RelatedTags = relatedTags,
            VideosByTag = videosByTag, VideosByPath = byPath, ConfiguredAncestors = configured
        };
        foreach (var ancestor in configured) all[ancestor] = 0;
        var ancestorCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        for (var position = 0; position < source.Count; position++)
        {
            if ((position & 255) == 0) cancellation.ThrowIfCancellationRequested();
            var input = source[position];
            var snapshot = SnapshotGraphVideo(input, ancestorCache);
            // An unusual duplicate path still contributes like the previous
            // full builder; such a snapshot simply cannot take the delta path.
            byPath.TryAdd(input.Path, snapshot);
            ApplyGraphVideo(index, snapshot, 1);
        }
        return index;
    }

    static bool TryUpdateSphereGraphIndex(SphereGraphIndex index, IReadOnlyList<VideoItem> source,
        IEnumerable<string> categoryNames, long version)
    {
        if (index.VideosByPath.Count != index.VideoCount) return false;
        var changed = new List<(string Id, GraphVideoSnapshot? Old, VideoItem? Current)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in source)
        {
            if (!seen.Add(video.Path)) return false;
            index.VideosByPath.TryGetValue(video.Path, out var previous);
            if (previous is null || !previous.Matches(video)) changed.Add((video.Path, previous, video));
        }
        foreach (var (id, previous) in index.VideosByPath)
            if (!seen.Contains(id)) changed.Add((id, previous, null));
        // Sparse edits are where a delta is beneficial. Bulk rescans remain a
        // single full pass and avoid thousands of List<VideoItem> removals.
        if (changed.Count > Math.Clamp(source.Count / 100, 64, 256)) return false;
        var configured = ConfiguredGraphAncestors(categoryNames);
        var ancestorCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var additions = changed.Where(entry => entry.Current is not null)
            .Select(entry => (entry.Id, Snapshot: SnapshotGraphVideo(entry.Current!, ancestorCache))).ToArray();
        foreach (var (_, previous, _) in changed)
            if (previous is not null) ApplyGraphVideo(index, previous, -1);
        foreach (var (id, _, _) in changed) index.VideosByPath.Remove(id);
        foreach (var (id, snapshot) in additions)
        {
            ApplyGraphVideo(index, snapshot, 1);
            index.VideosByPath[id] = snapshot;
        }
        foreach (var ancestor in index.AllCategoryCounts.Keys.ToArray())
            if (index.AllCategoryCounts[ancestor] == 0 && !configured.Contains(ancestor))
                index.AllCategoryCounts.Remove(ancestor);
        foreach (var ancestor in configured) index.AllCategoryCounts.TryAdd(ancestor, 0);
        index.ConfiguredAncestors.Clear();
        index.ConfiguredAncestors.UnionWith(configured);
        index.Version = version;
        index.VideoCount = source.Count;
        return true;
    }

    // The selected-tag view must count intersections on the SAME videos.
    // Combining separate global tag/category totals would invent links when
    // the selected tag and another tag occur in that category on different videos.
    static Dictionary<string, Dictionary<string, int>> BuildSelectedTagCategoryLinks(
        IEnumerable<VideoItem> selectedVideos, IReadOnlyDictionary<string, int> visibleRelatedTags)
    {
        var links = new Dictionary<string, Dictionary<string, int>>(StringComparer.CurrentCultureIgnoreCase);
        var ancestorCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in selectedVideos)
        {
            var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in CategoriesOf(video))
                foreach (var ancestor in CachedCategoryAncestors(category, ancestorCache)) ancestors.Add(ancestor);
            foreach (var tag in video.Tags.Where(tag => visibleRelatedTags.ContainsKey(tag))
                         .Distinct(StringComparer.CurrentCultureIgnoreCase))
            {
                if (!links.TryGetValue(tag, out var categories)) links[tag] = categories = new(StringComparer.OrdinalIgnoreCase);
                foreach (var category in ancestors)
                    categories[category] = categories.GetValueOrDefault(category) + 1;
            }
        }
        return links;
    }

    static Dictionary<string, Dictionary<string, int>> BuildSelectedTagCategoryLinks(
        IReadOnlyList<GraphVideoInput> selectedVideos, HashSet<string> visibleRelatedTags,
        CancellationToken cancellation)
    {
        var links = new Dictionary<string, Dictionary<string, int>>(StringComparer.CurrentCultureIgnoreCase);
        var ancestorCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        for (var position = 0; position < selectedVideos.Count; position++)
        {
            if ((position & 255) == 0) cancellation.ThrowIfCancellationRequested();
            var input = selectedVideos[position];
            var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in input.CategoryValues)
                foreach (var ancestor in CachedCategoryAncestors(category, ancestorCache)) ancestors.Add(ancestor);
            foreach (var tag in input.RawTags.Where(visibleRelatedTags.Contains)
                         .Distinct(StringComparer.CurrentCultureIgnoreCase))
            {
                if (!links.TryGetValue(tag, out var categories)) links[tag] = categories = new(StringComparer.OrdinalIgnoreCase);
                foreach (var category in ancestors)
                    categories[category] = categories.GetValueOrDefault(category) + 1;
            }
        }
        return links;
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
        var children = nodes.GroupBy(node => node.Parent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(node => node.Count).ThenBy(node => node.Path, StringComparer.CurrentCultureIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
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

    Border BuildSphereCategoryGraphV2(SphereGraphIndex index)
    {
        var graphClock = System.Diagnostics.Stopwatch.StartNew();
        sphereGraphTimer.Stop();
        sphereGraphCanvas = null;
        sphereGraphViewport = null;
        sphereMapSurface = null;
        sphereGraphScale = null;
        sphereGraphTranslation = null;
        sphereZoomLabel = null;
        sphereZoom = 1;
        spherePanOffset = default;
        spherePanning = false;
        sphereRotation = 0;
        sphereMouseDownPath = null;

        if (sphereTagFilter.Length > 0 && !index.TagCounts.ContainsKey(sphereTagFilter)) sphereTagFilter = "";
        var counts = sphereTagFilter.Length == 0 ? index.AllCategoryCounts : index.CategoriesByTag[sphereTagFilter];
        var connectedTags = sphereTagFilter.Length == 0 ? index.TagCounts : index.RelatedTags[sphereTagFilter];
        var relevantCount = sphereTagFilter.Length == 0 ? index.VideoCount : index.TagCounts[sphereTagFilter];
        Dictionary<string, Dictionary<string, int>> tagCategoryLinks;
        if (sphereTagFilter.Length == 0)
        {
            if (sphereTagBuildTask is { IsCompleted: false }) CancelSphereTagProjection();
            tagCategoryLinks = index.CategoriesByTag;
        }
        else
        {
            var selectedLinks = GetSelectedTagLinksForUi(index, sphereTagFilter, connectedTags);
            if (selectedLinks is null)
            {
                lastCategoryRenderVersion = -1;
                return new Border
                {
                    Margin = new Thickness(0, 8, 0, 10), Padding = new Thickness(18),
                    Background = new SolidColorBrush(Color.FromRgb(247, 250, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(216, 228, 248)),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                    Child = new TextBlock { Text = $"正在后台计算 #{sphereTagFilter} 与全部分类的真实共现关系…",
                        Foreground = new SolidColorBrush(Color.FromRgb(67, 91, 141)), FontSize = 13 }
                };
            }
            tagCategoryLinks = selectedLinks;
        }
        var nodes = LayoutSphereNodes(counts, connectedTags);
        var layoutMs = graphClock.Elapsed.TotalMilliseconds;
        sphereMapSurface = new SphereMapSurface(nodes, tagCategoryLinks,
            sphereTagFilter.Length == 0 ? null : counts)
        {
            RootCount = relevantCount,
            RootLabel = sphereTagFilter.Length == 0 ? "全部分类" :
                "#" + (sphereTagFilter.Length > 6 ? sphereTagFilter[..6] + "…" : sphereTagFilter),
            SelectedPath = selectedCategory is "全部" or "未分类" ? (selectedCategory == "未分类" ? "未分类" : "") : selectedCategory
        };
        var surfaceMs = graphClock.Elapsed.TotalMilliseconds - layoutMs;
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
            if (sphereMouseDownPath is not null && string.Equals(hit, sphereMouseDownPath, StringComparison.OrdinalIgnoreCase))
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
        title.Children.Add(new TextBlock { Text = $"{counts.Count:N0} 个分类 + {connectedTags.Count:N0} 个相关标签全部绘出 · {relevantCount:N0} 部视频 · 悬停节点查看真实共现连线", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(130, 145, 176)), Margin = new Thickness(0, 3, 0, 0) });
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
        var navigatorMs = graphClock.Elapsed.TotalMilliseconds - layoutMs - surfaceMs;
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
        if (nodes.Count > 500)
        {
            sphereGraphBuildProfile = $"节点布局 {layoutMs:F1} ms，绘制层 {surfaceMs:F1} ms（关系准备 {sphereMapSurface.AssociationBuildMs:F1} ms、底图 {sphereMapSurface.BaseLayerBuildMs:F1} ms），导航 {navigatorMs:F1} ms，总计 {graphClock.Elapsed.TotalMilliseconds:F1} ms";
        }
        return new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(216, 228, 248)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Padding = new Thickness(10), Margin = new Thickness(0, 5, 0, 16), Child = panel };
    }

    static void AddAncestors(string? path, Dictionary<string, int> counts, bool increment,
        HashSet<string>? seen = null, Dictionary<string, string[]>? cache = null)
    {
        foreach (var ancestor in cache is null ? CategoryAncestors(path) : CachedCategoryAncestors(path, cache))
        {
            if (!counts.ContainsKey(ancestor)) counts[ancestor] = 0;
            if (increment && (seen is null || seen.Add(ancestor))) counts[ancestor]++;
        }
    }

    static string[] CachedCategoryAncestors(string? path, Dictionary<string, string[]> cache)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "全部") return [];
        if (cache.TryGetValue(path, out var ancestors)) return ancestors;
        ancestors = CategoryAncestors(path).ToArray();
        cache[path] = ancestors;
        return ancestors;
    }

    static IEnumerable<string> CategoryAncestors(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "全部") yield break;
        var parts = path.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 1; i <= parts.Length; i++)
            yield return string.Join(" / ", parts.Take(i));
    }

    static bool VerifyGraphDataVersionForDiagnostics()
    {
        var baseline = new LibraryConfig
        {
            Folders = [@"C:\Videos"],
            CategoryNames = ["人物"],
            Categories = new(StringComparer.OrdinalIgnoreCase) { ["one"] = "人物" },
            MultiCategories = new(StringComparer.OrdinalIgnoreCase) { ["one"] = ["人物 / 作品"] },
            Tags = new(StringComparer.OrdinalIgnoreCase) { ["one"] = ["演员"] },
            CategoryRenames = new(StringComparer.OrdinalIgnoreCase) { ["原名"] = "人物" }
        };
        var settings = CloneLibraryConfig(baseline);
        settings.Player = @"C:\Player.exe";
        settings.CardPageSize = 120;
        settings.SearchHistory.Add("检索词");
        settings.WatchStates["one"] = "已看";
        settings.Favorites.Add("one");
        var mergedSettings = MergeLibraryConfig(baseline, settings, CloneLibraryConfig(baseline));
        if (!GraphRelevantConfigEquals(baseline, mergedSettings)) return false;

        static bool ChangesGraph(LibraryConfig baseline, Action<LibraryConfig> change)
        {
            var edited = CloneLibraryConfig(baseline);
            change(edited);
            var merged = MergeLibraryConfig(baseline, edited, CloneLibraryConfig(baseline));
            return !GraphRelevantConfigEquals(baseline, merged);
        }
        return ChangesGraph(baseline, value => value.CategoryNames.Add("新分类")) &&
            ChangesGraph(baseline, value => value.Categories["one"] = "新分类") &&
            ChangesGraph(baseline, value => value.MultiCategories["one"].Add("人物 / 第二部")) &&
            ChangesGraph(baseline, value => value.Tags["one"].Add("新标签")) &&
            ChangesGraph(baseline, value => value.CategoryRenames["原名"] = "新分类") &&
            ChangesGraph(baseline, value => value.Folders.Add(@"D:\Videos")) &&
            ChangesGraph(baseline, value => value.AutoCategorize = !value.AutoCategorize);
    }

    internal static bool VerifySphereRelationsForDiagnostics()
    {
        static VideoItem Sample(string id, string category, params string[] tags)
        {
            var item = new VideoItem
            {
                Id = id, Path = id + ".mp4", Title = id, Folder = "diagnostic",
                Size = 1, Modified = DateTime.UtcNow, Category = category
            };
            item.Tags.AddRange(tags);
            return item;
        }
        var selectedVideos = new[]
        {
            Sample("one", "人物 / 作品", "所选标签", "同现", "同现"),
            Sample("two", "其他", "所选标签")
        };
        var unrelated = Sample("three", "其他", "同现");
        var links = BuildSelectedTagCategoryLinks(selectedVideos, new Dictionary<string, int> { ["同现"] = 1 });
        var snapshotLinks = Task.Run(() => BuildSelectedTagCategoryLinks(CaptureGraphInputs(selectedVideos),
            new HashSet<string>(StringComparer.CurrentCultureIgnoreCase) { "同现" },
            CancellationToken.None)).GetAwaiter().GetResult();
        var snapshotProjectionExact = snapshotLinks.Count == links.Count &&
            snapshotLinks.All(pair => links.TryGetValue(pair.Key, out var expected) &&
                expected.Count == pair.Value.Count && pair.Value.All(edge =>
                    expected.TryGetValue(edge.Key, out var count) && count == edge.Value));
        var counts = new Dictionary<string, int> { ["人物"] = 1, ["人物 / 作品"] = 1, ["其他"] = 1 };
        var nodes = LayoutSphereNodes(counts, new Dictionary<string, int> { ["同现"] = 1 });
        var surface = new SphereMapSurface(nodes, links, counts);
        var selectedTagReachesAllCategories = surface.FocusAssociationCount == counts.Count;
        surface.SetHover("\u0001同现");
        var matchingTagLinks = surface.FocusAssociationCount == 2;
        surface.SetHover("其他");
        // The selected tag itself still connects to this category, while the
        // unrelated secondary tag must not acquire a false cross-link.
        var unrelatedCategoryExcluded = surface.FocusAssociationCount == 1;
        surface.SetHover("人物 / 作品");
        var exactCategoryLinked = surface.FocusAssociationCount == 2;
        surface.SelectedPath = "人物 / 作品";
        surface.DropPath = "\u0000";
        surface.SetHover("\u0001同现");
        var branchHighlightsFollowRealParents = surface.IsBranchHighlighted("人物") &&
            surface.IsBranchHighlighted("人物 / 作品") && surface.IsBranchHighlighted("\u0001同现") &&
            !surface.IsBranchHighlighted("其他");
        var mapCenter = new System.Windows.Point(surface.Width / 2, surface.Height / 2 + 7);
        var firstNode = nodes[0];
        surface.Rotation = .31;
        var rotatedNodeCenter = new System.Windows.Point(
            mapCenter.X + Math.Cos(firstNode.Angle + surface.Rotation) * firstNode.Radius * 246,
            mapCenter.Y + Math.Sin(firstNode.Angle + surface.Rotation) * firstNode.Radius * 232);
        var baseNodeCenter = new System.Windows.Point(
            mapCenter.X + Math.Cos(firstNode.Angle) * firstNode.Radius * 246,
            mapCenter.Y + Math.Sin(firstNode.Angle) * firstNode.Radius * 232);
        var bitmapNodeCenter = SphereMapSurface.RotationMatrix(mapCenter, surface.Rotation).Transform(baseNodeCenter);
        var hitGridTracksRotation = surface.HitPath(mapCenter) == "" &&
            surface.HitPath(rotatedNodeCenter) == firstNode.Path &&
            (bitmapNodeCenter - rotatedNodeCenter).LengthSquared < .0001;
        var firstGeometryBuild = surface.SnapshotGeometryCacheForDiagnostics();
        surface.Rotation += .1;
        var nextGeometryBuild = surface.SnapshotGeometryCacheForDiagnostics();
        var rotationReusesRelationshipGeometry = firstGeometryBuild == (1, 1) &&
            nextGeometryBuild == firstGeometryBuild;
        // Canonically equivalent-looking names can still be distinct library
        // paths. Category identity follows the same ordinal comparer used by
        // the cards, navigator and graph hit map, not linguistic collation.
        var distinctUnicodePaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        AddAncestors("人物 / Café", distinctUnicodePaths, false);
        AddAncestors("人物 / Cafe\u0301", distinctUnicodePaths, false);
        var unicodeNodes = LayoutSphereNodes(distinctUnicodePaths, new Dictionary<string, int>());
        var unicodePathsRemainReachable = distinctUnicodePaths.Count == 3 &&
            unicodeNodes.Count == 3 &&
            new SphereMapSurface(unicodeNodes,
                new Dictionary<string, Dictionary<string, int>>()).EveryNodeLinked;
        return snapshotProjectionExact && selectedTagReachesAllCategories && matchingTagLinks && unrelatedCategoryExcluded
            && exactCategoryLinked && branchHighlightsFollowRealParents && hitGridTracksRotation &&
            rotationReusesRelationshipGeometry && surface.EveryNodeLinked
            && unicodePathsRemainReachable && VerifyGraphDataVersionForDiagnostics()
            && links["同现"]["人物"] == 1 && links["同现"]["人物 / 作品"] == 1
            && !links["同现"].ContainsKey("其他") && unrelated.Tags.Contains("同现");
    }

    internal static IReadOnlyDictionary<string, object> VerifySphereIncrementalIndexForDiagnostics()
    {
        const int videoCount = 32909;
        const int leafCategoryCount = 2067;
        var source = new List<VideoItem>(videoCount);
        for (var index = 0; index < videoCount; index++)
        {
            var video = new VideoItem
            {
                Id = "graph-diagnostic-" + index,
                Path = "graph-diagnostic-" + index + ".mp4",
                Title = "graph-diagnostic-" + index,
                Folder = "graph-diagnostic",
                Size = 1,
                Modified = DateTime.UnixEpoch,
                Category = "人物 / 作品" + (index % leafCategoryCount)
            };
            video.Tags.Add("总标签");
            video.Tags.Add("同现" + (index % 16));
            source.Add(video);
        }
        var configured = new[] { "人物" };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var incremental = BuildSphereGraphIndex(source, configured, 1);
        var coldMilliseconds = clock.Elapsed.TotalMilliseconds;
        var initialNodesComplete = incremental.AllCategoryCounts.Count == leafCategoryCount + 1 &&
            incremental.TagCounts.Count == 17 && incremental.VideosByPath.Count == videoCount;
        clock.Restart();
        var captured = CaptureGraphInputs(source);
        var captureMilliseconds = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var background = Task.Run(() => BuildSphereGraphIndex(captured, configured, 1,
            CancellationToken.None)).GetAwaiter().GetResult();
        var backgroundMilliseconds = clock.Elapsed.TotalMilliseconds;
        var backgroundExact = background.VideoCount == videoCount &&
            background.AllCategoryCounts.Count == incremental.AllCategoryCounts.Count &&
            background.TagCounts.Count == incremental.TagCounts.Count &&
            background.CategoriesByTag.Count == incremental.CategoriesByTag.Count;
        source[0].Category = "人物 / 新作品";
        source[0].Tags.Remove("同现0");
        source[0].Tags.Add("新增标签");
        var snapshotStable = background.AllCategoryCounts.GetValueOrDefault("人物 / 作品0") > 0 &&
            !background.AllCategoryCounts.ContainsKey("人物 / 新作品") &&
            background.TagCounts.ContainsKey("同现0") && !background.TagCounts.ContainsKey("新增标签");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellationObserved = false;
        try { _ = BuildSphereGraphIndex(captured, configured, 1, cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        var staleBuildRejected = !AcceptSphereBuild(4, 5, 1, 1, 7, 7, true, false) &&
            !AcceptSphereBuild(5, 5, 1, 2, 7, 7, true, false) &&
            !AcceptSphereBuild(5, 5, 1, 1, 7, 8, true, false) &&
            !AcceptSphereBuild(5, 5, 1, 1, 7, 7, true, true) &&
            AcceptSphereBuild(5, 5, 1, 1, 7, 7, true, false);
        clock.Restart();
        var usedDelta = TryUpdateSphereGraphIndex(incremental, source, ["人物", "空分类"], 2);
        var deltaMilliseconds = clock.Elapsed.TotalMilliseconds;
        var full = BuildSphereGraphIndex(source, ["人物", "空分类"], 2);
        static bool SameCounts(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) =>
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var count) && count == pair.Value);
        static bool SameNested(IReadOnlyDictionary<string, Dictionary<string, int>> left,
            IReadOnlyDictionary<string, Dictionary<string, int>> right) =>
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var values) &&
                SameCounts(pair.Value, values));
        var exact = usedDelta && SameCounts(incremental.AllCategoryCounts, full.AllCategoryCounts) &&
            SameCounts(incremental.TagCounts, full.TagCounts) &&
            SameNested(incremental.CategoriesByTag, full.CategoriesByTag) &&
            SameNested(incremental.RelatedTags, full.RelatedTags) &&
            incremental.VideosByTag.Count == full.VideosByTag.Count &&
            incremental.VideosByTag.All(pair => full.VideosByTag.TryGetValue(pair.Key, out var matches) &&
                pair.Value.Count == matches.Count &&
                pair.Value.Select(video => video.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(matches.Select(video => video.Path)));
        return new Dictionary<string, object>
        {
            ["graphIncremental32909Videos2068Categories"] = initialNodesComplete && exact &&
                incremental.AllCategoryCounts.GetValueOrDefault("空分类") == 0 &&
                incremental.AllCategoryCounts.GetValueOrDefault("人物 / 新作品") == 1,
            ["graphIndexColdBuildMs"] = Math.Round(coldMilliseconds, 2),
            ["graphBackgroundSnapshotAndStaleBuildSafe"] = backgroundExact && snapshotStable &&
                cancellationObserved && staleBuildRejected,
            ["graphSnapshotCaptureMs"] = Math.Round(captureMilliseconds, 2),
            ["graphBackgroundFullBuildMs"] = Math.Round(backgroundMilliseconds, 2),
            ["graphIndexSingleEditMs"] = Math.Round(deltaMilliseconds, 2),
            ["graphIndexSourceVideoCount"] = videoCount,
            ["graphIndexInitialCategoryCount"] = leafCategoryCount + 1
        };
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
            var active = name.Equals(selectedCategory, StringComparison.OrdinalIgnoreCase);
            chip.Background = new SolidColorBrush(active ? Color.FromRgb(234, 240, 255) : Colors.White);
            chip.Foreground = new SolidColorBrush(active ? Color.FromRgb(62, 99, 213) : Color.FromRgb(70, 83, 102));
        }
        if (sphereCategoryQuickList is { } quickList)
        {
            var selectedItem = quickList.ItemsSource?.OfType<SphereNavItem>()
                .FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(quickList.SelectedItem, selectedItem))
            {
                sphereSyncingCategoryList = true;
                try { quickList.SelectedItem = selectedItem; }
                finally { sphereSyncingCategoryList = false; }
            }
        }
        RenderCards();
        LibraryScroll.ScrollToTop();
    }
}
