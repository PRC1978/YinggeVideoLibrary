using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace YinggeVideoLibrary;

/// <summary>
/// Result of one fixture generation run.
/// </summary>
internal sealed class UiFixtureResult
{
    public string DataDirectory { get; set; } = "";
    public int VideoCount { get; set; }
    public int CategoryLeafCount { get; set; }
    public int CategoryNodeCount { get; set; }
    public int FolderCount { get; set; }
    public int TagCount { get; set; }
    public int MetadataEntryCount { get; set; }
    public long ConfigBytes { get; set; }
    public long IndexBytes { get; set; }
    public long MetadataBytes { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Builds a deterministic synthetic library so the unmodified UI can be measured
/// at the "large library" scale the performance plan talks about.
///
/// The generator writes the same three files the application itself writes
/// (config.json, library-index.json, metadata.json) using the application's own
/// model types, so loading exercises the production parsing and layout code and
/// nothing is special-cased for diagnostics.
///
/// Entry paths deliberately point at files that do not exist. The scan treats a
/// missing root as "incomplete" and preserves the indexed entries, so the UI
/// keeps the synthetic library for the whole run. This line measures UI
/// computation and rendering, not disk I/O or poster decoding, and the report
/// says so explicitly.
/// </summary>
internal static class UiFixtureGenerator
{
    // Four scan roots keep the folder page meaningful; 500 collections per root
    // give a 60-row page a real pagination job.
    const int RootCount = 4;
    const int CollectionsPerRoot = 500;

    static readonly string[] CategoryRoots = ["电影", "剧集", "动漫", "其他"];
    static readonly string[] CategoryGroups = ["动作", "剧情", "纪录", "科幻", "悬疑", "喜剧", "恐怖", "音乐", "体育", "自然"];
    static readonly string[] CategorySections = ["段一", "段二", "段三", "段四", "段五"];
    static readonly string[] TagPool =
        ["旅行", "科幻", "悬疑", "纪录", "剧情", "动作", "喜剧", "恐怖", "音乐", "体育", "美食", "自然"];

    internal static int Run(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        // --ui-fixture <dataDir> <videoCount> [categoryCount]
        if (args.Length < 3)
        {
            Console.Out.WriteLine("用法：映格视频库.exe --ui-fixture <隔离数据目录> <视频数量> [分类叶子数]");
            return 2;
        }
        if (!int.TryParse(args[2], out var videoCount) || videoCount <= 0)
        {
            Console.Out.WriteLine("视频数量必须是正整数。");
            return 2;
        }
        var categoryCount = 2000;
        if (args.Length > 3 && int.TryParse(args[3], out var requestedCategories) && requestedCategories > 0)
            categoryCount = requestedCategories;
        string dataDir;
        try { dataDir = System.IO.Path.GetFullPath(args[1]); }
        catch (Exception ex)
        {
            Console.Out.WriteLine("数据目录无效：" + ex.Message);
            return 2;
        }

        var result = Seed(dataDir, videoCount, categoryCount);
        Console.Out.WriteLine($"隔离数据目录：{result.DataDirectory}");
        Console.Out.WriteLine($"视频条目：{result.VideoCount}");
        Console.Out.WriteLine($"分类叶子：{result.CategoryLeafCount}，分类节点（含祖先）：{result.CategoryNodeCount}");
        Console.Out.WriteLine($"扫描根：{result.FolderCount}，库内目录：{CollectionsPerRoot * RootCount}，标签：{result.TagCount}");
        Console.Out.WriteLine($"元数据条目：{result.MetadataEntryCount}");
        Console.Out.WriteLine($"config.json {result.ConfigBytes:N0} 字节；library-index.json {result.IndexBytes:N0} 字节；metadata.json {result.MetadataBytes:N0} 字节");
        Console.Out.WriteLine($"生成耗时：{result.ElapsedMs} ms");
        return 0;
    }

    /// <summary>
    /// Deterministic, idempotent (overwrite) fixture write. Same arguments always
    /// produce byte-identical content so repeated measurements are comparable.
    /// </summary>
    internal static UiFixtureResult Seed(string dataDir, int videoCount, int categoryCount)
    {
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(dataDir);
        // Never create this directory: the scan must report the roots as
        // unavailable so the indexed entries are preserved instead of replaced
        // by an empty scan.
        var libraryRoot = System.IO.Path.Combine(dataDir, "library");
        var roots = Enumerable.Range(0, RootCount)
            .Select(index => System.IO.Path.Combine(libraryRoot, $"位置{index + 1}"))
            .ToArray();

        var leaves = BuildCategoryLeaves(categoryCount);
        var config = new LibraryConfig
        {
            SchemaVersion = 2,
            Folders = roots.ToList(),
            ShowAll = false,
            StartPage = "all",
            AutoCategorize = true,
            CardPageSize = 60,
            PosterCacheLimitMb = 128,
            UpdateAutoCheck = false,
            UpdateCheckIntervalHours = 168,
            CategoryNames = CategoryRoots.ToList()
        };
        var index = new LibraryIndex { Folders = roots, Entries = new List<IndexEntry>(videoCount) };
        var metadata = new Dictionary<string, MovieMetadata>(videoCount, StringComparer.OrdinalIgnoreCase);
        var baseTime = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Local);

        for (var position = 0; position < videoCount; position++)
        {
            var root = roots[position % RootCount];
            var collection = (position / RootCount) % CollectionsPerRoot;
            var folder = System.IO.Path.Combine(root, $"作品集-{collection:000}");
            var title = BuildTitle(position);
            var path = System.IO.Path.Combine(folder, title + ".mp4");
            var id = MainWindow.FixtureIdFor(path);
            index.Entries.Add(new IndexEntry
            {
                Path = path,
                // Deterministic pseudo size so "按大小排序" has real work to do.
                Size = 700_000_000L + position % 977 * 1_048_576L + position % 1024 * 4096L,
                ModifiedTicks = baseTime.AddMinutes(position).Ticks
            });
            config.Categories[id] = leaves[position % leaves.Count];
            config.Tags[id] = [TagPool[position % TagPool.Length], TagPool[(position / TagPool.Length) % TagPool.Length]];
            config.WatchStates[id] = position % 3 == 0 ? "已看" : position % 3 == 1 ? "观看中" : "未看";
            if (position % 997 == 0) config.Favorites.Add(id);
            metadata[id] = new MovieMetadata
            {
                SchemaVersion = 2,
                Code = $"ABC-{10000 + position}",
                Title = title,
                Source = "fixture",
                // Fixed timestamp keeps the file byte-identical across runs.
                ScrapedAt = baseTime
            };
        }

        var categoryNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in leaves)
            foreach (var ancestor in CategoryAncestors(leaf)) categoryNodes.Add(ancestor);

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        // Metadata is written without null members: the store reads the same
        // shape back and the file stays small enough to parse at startup.
        var metadataJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var configBytes = WriteJson(System.IO.Path.Combine(dataDir, "config.json"), config, jsonOptions);
        var indexBytes = WriteJson(System.IO.Path.Combine(dataDir, "library-index.json"), index, jsonOptions);
        var metadataBytes = WriteJson(System.IO.Path.Combine(dataDir, "metadata.json"), metadata, metadataJsonOptions);

        clock.Stop();
        return new UiFixtureResult
        {
            DataDirectory = dataDir,
            VideoCount = index.Entries.Count,
            CategoryLeafCount = leaves.Count,
            CategoryNodeCount = categoryNodes.Count,
            FolderCount = roots.Length,
            TagCount = TagPool.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            MetadataEntryCount = metadata.Count,
            ConfigBytes = configBytes,
            IndexBytes = indexBytes,
            MetadataBytes = metadataBytes,
            ElapsedMs = clock.ElapsedMilliseconds
        };
    }

    static long WriteJson<T>(string path, T value, JsonSerializerOptions options)
    {
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, options));
        File.Move(temporary, path, true);
        return new FileInfo(path).Length;
    }

    /// <summary>
    /// Exactly <paramref name="categoryCount"/> distinct leaf paths, composed from
    /// four roots, ten groups, five sections and the remaining size. Index i maps
    /// bijectively to (root, group, section, suffix) for i below the product, so
    /// the requested leaf count is reproduced exactly.
    /// </summary>
    static List<string> BuildCategoryLeaves(int categoryCount)
    {
        var leaves = new List<string>(categoryCount);
        for (var index = 0; index < categoryCount; index++)
        {
            var root = CategoryRoots[index % CategoryRoots.Length];
            var group = CategoryGroups[(index / CategoryRoots.Length) % CategoryGroups.Length];
            var section = CategorySections[(index / (CategoryRoots.Length * CategoryGroups.Length)) % CategorySections.Length];
            var suffix = index / (CategoryRoots.Length * CategoryGroups.Length * CategorySections.Length);
            leaves.Add($"{root} / {group} / {section} / 组{suffix:00}");
        }
        return leaves;
    }

    static IEnumerable<string> CategoryAncestors(string path)
    {
        var current = "";
        foreach (var part in path.Split(" / ", StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? part : current + " / " + part;
            yield return current;
        }
    }

    static string BuildTitle(int position) => (position % 6) switch
    {
        0 => $"夜行城市 Night City 2024 第{position:00000}号",
        1 => $"深海回声 Deep Echo 第{position:00000}号",
        2 => $"测试影片 Test Sample 第{position:00000}号",
        3 => $"晨光计划 Morning Light 第{position:00000}号",
        4 => $"静默边境 Silent Border 第{position:00000}号",
        _ => $"档案馆 Archive 第{position:00000}号"
    };
}
