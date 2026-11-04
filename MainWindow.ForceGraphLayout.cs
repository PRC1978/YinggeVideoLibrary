namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // Deterministic 3D force layout: hierarchy and strongest observed tag links
    // are springs in X/Y/Z; nearby nodes repel one another. Far-away repulsion uses
    // projected cell centers of mass, so a large library does not do O(n²) work per
    // iteration. The result is calculated once per graph rebuild, never per
    // animation frame or mouse movement.
    static void LayoutForceNodes(List<SphereMapNode> nodes,
        IReadOnlyDictionary<string, Dictionary<string, int>> tagCategoryLinks)
    {
        if (nodes.Count == 0) return;
        var byPath = nodes.Select((node, index) => (node.Path, index))
            .ToDictionary(entry => entry.Path, entry => entry.index, StringComparer.OrdinalIgnoreCase);
        var count = nodes.Count;
        var x = new double[count];
        var y = new double[count];
        var z = new double[count];
        var fx = new double[count];
        var fy = new double[count];
        var fz = new double[count];
        var parent = new int[count];
        for (var i = 0; i < count; i++)
        {
            var node = nodes[i];
            var jitter = i * 2.399963229728653;
            x[i] = Math.Cos(node.Angle) * node.Radius * .88 + Math.Cos(jitter) * .015;
            y[i] = Math.Sin(node.Angle) * node.Radius * .88 + Math.Sin(jitter) * .015;
            z[i] = Math.Sin(jitter * .73) * .16;
            parent[i] = byPath.TryGetValue(node.Parent, out var index) ? index : -1;
        }

        var crossLinks = new List<(int A, int B)>();
        foreach (var (tag, categories) in tagCategoryLinks)
        {
            if (!byPath.TryGetValue("\u0001" + tag, out var tagIndex)) continue;
            // Layout needs a bounded set of springs. Every real relationship
            // is still retained in the graph and displayed when focused.
            foreach (var category in categories.OrderByDescending(pair => pair.Value).Take(3))
                if (byPath.TryGetValue(category.Key, out var categoryIndex))
                    crossLinks.Add((tagIndex, categoryIndex));
        }

        const int gridWidth = 18;
        const int gridHeight = 12;
        const int gridCount = gridWidth * gridHeight;
        var mass = new int[gridCount];
        var centerX = new double[gridCount];
        var centerY = new double[gridCount];
        var centerZ = new double[gridCount];
        var occupants = new List<int>[gridCount];
        var steps = count > 1500 ? 18 : count > 600 ? 30 : 48;
        for (var step = 0; step < steps; step++)
        {
            Array.Clear(mass);
            Array.Clear(centerX);
            Array.Clear(centerY);
            Array.Clear(centerZ);
            Array.Clear(fx);
            Array.Clear(fy);
            Array.Clear(fz);
            for (var cell = 0; cell < gridCount; cell++)
                (occupants[cell] ??= []).Clear();
            for (var i = 0; i < count; i++)
            {
                var column = Math.Clamp((int)((x[i] + 1) * gridWidth / 2), 0, gridWidth - 1);
                var row = Math.Clamp((int)((y[i] + 1) * gridHeight / 2), 0, gridHeight - 1);
                var cell = row * gridWidth + column;
                occupants[cell].Add(i);
                mass[cell]++;
                centerX[cell] += x[i];
                centerY[cell] += y[i];
                centerZ[cell] += z[i];
            }
            for (var cell = 0; cell < gridCount; cell++)
                if (mass[cell] > 0)
                {
                    centerX[cell] /= mass[cell];
                    centerY[cell] /= mass[cell];
                    centerZ[cell] /= mass[cell];
                }

            for (var i = 0; i < count; i++)
            {
                var ownColumn = Math.Clamp((int)((x[i] + 1) * gridWidth / 2), 0, gridWidth - 1);
                var ownRow = Math.Clamp((int)((y[i] + 1) * gridHeight / 2), 0, gridHeight - 1);
                for (var cell = 0; cell < gridCount; cell++)
                {
                    if (mass[cell] == 0) continue;
                    var column = cell % gridWidth;
                    var row = cell / gridWidth;
                    if (Math.Abs(column - ownColumn) <= 1 && Math.Abs(row - ownRow) <= 1)
                    {
                        foreach (var other in occupants[cell])
                        {
                            if (other == i) continue;
                            var dx = x[i] - x[other];
                            var dy = y[i] - y[other];
                            var dz = z[i] - z[other];
                            var force = .00008 / (dx * dx + dy * dy + dz * dz + .0025);
                            fx[i] += dx * force;
                            fy[i] += dy * force;
                            fz[i] += dz * force;
                        }
                    }
                    else
                    {
                        var dx = x[i] - centerX[cell];
                        var dy = y[i] - centerY[cell];
                        var dz = z[i] - centerZ[cell];
                        var force = .00020 * Math.Sqrt(mass[cell]) / (dx * dx + dy * dy + dz * dz + .0025);
                        fx[i] += dx * force;
                        fy[i] += dy * force;
                        fz[i] += dz * force;
                    }
                }
                var p = parent[i];
                var px = p < 0 ? 0 : x[p];
                var py = p < 0 ? 0 : y[p];
                var pz = p < 0 ? 0 : z[p];
                var dxParent = px - x[i];
                var dyParent = py - y[i];
                var dzParent = pz - z[i];
                var distance = Math.Sqrt(dxParent * dxParent + dyParent * dyParent + dzParent * dzParent + .000001);
                var desired = p < 0 ? .34 : .13;
                var spring = .07 * (distance - desired) / distance;
                fx[i] += dxParent * spring;
                fy[i] += dyParent * spring;
                fz[i] += dzParent * spring;
                if (p >= 0)
                {
                    fx[p] -= dxParent * spring;
                    fy[p] -= dyParent * spring;
                    fz[p] -= dzParent * spring;
                }
            }
            foreach (var (a, b) in crossLinks)
            {
                var dx = x[b] - x[a];
                var dy = y[b] - y[a];
                var dz = z[b] - z[a];
                fx[a] += dx * .002;
                fy[a] += dy * .002;
                fz[a] += dz * .002;
                fx[b] -= dx * .002;
                fy[b] -= dy * .002;
                fz[b] -= dz * .002;
            }
            for (var i = 0; i < count; i++)
            {
                x[i] += Math.Clamp(fx[i] - x[i] * .009, -.045, .045);
                y[i] += Math.Clamp(fy[i] - y[i] * .009, -.045, .045);
                z[i] += Math.Clamp(fz[i] - z[i] * .009, -.045, .045);
            }
        }
        // Fit the solved positions to the graph viewport. A fixed .92 lower
        // bound left small libraries contracted into a tiny central picture.
        var boundX = Math.Max(.28, x.Max(value => Math.Abs(value)));
        var boundY = Math.Max(.28, y.Max(value => Math.Abs(value)));
        var boundZ = Math.Max(.28, z.Max(value => Math.Abs(value)));
        for (var i = 0; i < count; i++)
        {
            // The drawing surface is wide. Normalize each axis separately so
            // a tall solved graph does not waste most of that width.
            var px = x[i] * .90 / boundX;
            var py = y[i] * .90 / boundY;
            nodes[i].Angle = Math.Atan2(py, px);
            nodes[i].Radius = Math.Sqrt(px * px + py * py);
            nodes[i].Z = z[i] * .65 / boundZ;
        }
    }

    internal static bool VerifyForceLayoutForDiagnostics()
    {
        static List<SphereMapNode> Sample() =>
        [
            new() { Path = "电影", Parent = "", Label = "电影", Count = 12, Depth = 1, Angle = 0, Radius = .5 },
            new() { Path = "电影 / 剧情", Parent = "电影", Label = "剧情", Count = 7, Depth = 2, Angle = .3, Radius = .9 },
            new() { Path = "动画", Parent = "", Label = "动画", Count = 8, Depth = 1, Angle = 2, Radius = .5 },
            new() { Path = "\u0001收藏", Parent = "", Label = "#收藏", Count = 4, Depth = 1, Angle = 4, Radius = .6, IsTag = true, TagName = "收藏" }
        ];
        var links = new Dictionary<string, Dictionary<string, int>>
        { ["收藏"] = new() { ["电影 / 剧情"] = 3 } };
        var first = Sample();
        var second = Sample();
        LayoutForceNodes(first, links);
        LayoutForceNodes(second, links);
        return first.Zip(second).All(pair =>
                Math.Abs(pair.First.Angle - pair.Second.Angle) < 1e-12 &&
                Math.Abs(pair.First.Radius - pair.Second.Radius) < 1e-12 &&
                Math.Abs(pair.First.Z - pair.Second.Z) < 1e-12 &&
                double.IsFinite(pair.First.Angle) && double.IsFinite(pair.First.Radius) &&
                double.IsFinite(pair.First.Z)) &&
            first.Any(node => Math.Abs(node.Angle - Sample().First(original => original.Path == node.Path).Angle) > .01) &&
            first.Any(node => Math.Abs(node.Z) > .05) &&
            first.All(node => node.Radius <= 1.3 && Math.Abs(node.Z) <= .65);
    }
}
