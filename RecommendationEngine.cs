namespace YinggeVideoLibrary;

// A small immutable snapshot keeps ranking off the WPF thread. In particular,
// paths and filenames never enter the online-topic generator.
internal sealed record RecommendationSnapshot(
    string Id, bool Favorite, string WatchStatus, DateTime Modified,
    string[] Categories, string[] Tags)
{
    public static RecommendationSnapshot Capture(VideoItem video) => new(
        video.Id, video.Favorite, video.WatchStatus, video.Modified,
        video.CategoryList.Count > 0 ? video.CategoryList.ToArray() : [video.Category],
        video.Tags.ToArray());
}

internal sealed record LocalRecommendation(string VideoId, double Score, string Reason);

// Keyword is selected from a fixed, generic list. No title, filename, path,
// person name, custom tag or custom category is sent to Bilibili.
internal sealed record BilibiliRecommendationTopic(string Label, string Keyword, string Reason);

internal static class RecommendationEngine
{
    private static readonly (string Label, string Keyword, string[] Aliases)[] PublicTopics =
    [
        ("电影", "电影解说", ["电影", "影片", "movie", "cinema"]),
        ("剧集", "电视剧解析", ["剧集", "电视剧", "美剧", "韩剧", "tv series"]),
        ("动画", "动画推荐", ["动画", "动漫", "番剧", "anime"]),
        ("纪录片", "纪录片", ["纪录片", "documentary"]),
        ("科幻", "科幻影视", ["科幻", "sci-fi", "science fiction"]),
        ("音乐", "音乐现场", ["音乐", "演唱会", "music"]),
        ("旅行", "旅行纪录片", ["旅行", "旅游", "travel"]),
        ("美食", "美食纪录片", ["美食", "烹饪", "料理", "cooking"]),
        ("摄影", "摄影教程", ["摄影", "拍摄", "photography"]),
        ("游戏", "游戏攻略", ["游戏", "game", "gaming"]),
        ("科技", "科技科普", ["科技", "数码", "technology"]),
        ("历史", "历史纪录片", ["历史", "history"]),
        ("汽车", "汽车评测", ["汽车", "car", "automotive"]),
        ("运动", "运动训练", ["运动", "健身", "sport", "fitness"]),
        ("编程", "编程教程", ["编程", "代码", "programming"])
    ];

    public static List<LocalRecommendation> Rank(IReadOnlyList<RecommendationSnapshot> videos, int limit = 24)
    {
        if (videos.Count == 0 || limit <= 0) return [];
        var categoryInterest = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var tagInterest = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
        {
            var weight = video.Favorite ? 5d : video.WatchStatus switch
            {
                "观看中" => 2d,
                "已看" => 1d,
                _ => 0d
            };
            if (weight == 0) continue;
            foreach (var category in CleanTokens(video.Categories, 8, true))
                categoryInterest[category] = categoryInterest.GetValueOrDefault(category) + weight;
            foreach (var tag in CleanTokens(video.Tags, 12, false))
                tagInterest[tag] = tagInterest.GetValueOrDefault(tag) + weight;
        }

        var newest = videos.Max(video => video.Modified);
        var top = new PriorityQueue<LocalRecommendation, double>();
        foreach (var video in videos)
        {
            var categories = CleanTokens(video.Categories, 8, true);
            var tags = CleanTokens(video.Tags, 12, false);
            var bestTag = tags.Select(tag => (tag, affinity: tagInterest.GetValueOrDefault(tag)))
                .OrderByDescending(hit => hit.affinity).FirstOrDefault();
            var bestCategory = categories.Select(category => (category, affinity: categoryInterest.GetValueOrDefault(category)))
                .OrderByDescending(hit => hit.affinity).FirstOrDefault();
            var baseScore = video.WatchStatus switch
            {
                "未看" => 36d,
                "观看中" => 28d,
                _ => 8d
            };
            if (video.Favorite) baseScore += 8;
            var daysFromNewest = Math.Max(0, (newest - video.Modified).TotalDays);
            var freshness = 14d / (1d + daysFromNewest / 45d);
            var affinity = Math.Min(24d, 6d * Math.Log2(1d + bestTag.affinity))
                + Math.Min(18d, 5d * Math.Log2(1d + bestCategory.affinity));
            var score = baseScore + freshness + affinity;
            var reason = bestTag.affinity >= bestCategory.affinity && bestTag.affinity > 0
                ? $"与你常看的标签「{bestTag.tag}」相关"
                : bestCategory.affinity > 0
                    ? $"与你常看的分类「{bestCategory.category}」相关"
                    : video.WatchStatus == "观看中" ? "继续观看"
                    : video.Favorite ? "你收藏的视频" : "最近加入 · 尚未观看";
            var result = new LocalRecommendation(video.Id, score, reason);
            if (top.Count < limit) top.Enqueue(result, score);
            else if (top.TryPeek(out _, out var lowest) && score > lowest)
            {
                top.Dequeue();
                top.Enqueue(result, score);
            }
        }
        return top.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(item => item.Score).ThenBy(item => item.VideoId, StringComparer.Ordinal).ToList();
    }

    public static List<BilibiliRecommendationTopic> BuildBilibiliTopics(IReadOnlyList<RecommendationSnapshot> videos, int limit = 6)
    {
        if (limit <= 0) return [];
        var scores = new double[PublicTopics.Length];
        foreach (var video in videos)
        {
            var weight = video.Favorite ? 4d : video.WatchStatus switch
            {
                "观看中" => 2d,
                "已看" => 1d,
                _ => 0.25d
            };
            var interests = CleanTokens(video.Categories, 8, true)
                .Concat(CleanTokens(video.Tags, 12, false)).ToArray();
            for (var index = 0; index < PublicTopics.Length; index++)
                if (interests.Any(value => PublicTopics[index].Aliases.Any(alias =>
                    alias.All(char.IsAsciiLetter) ? value.Equals(alias, StringComparison.OrdinalIgnoreCase)
                        : value.Contains(alias, StringComparison.OrdinalIgnoreCase))))
                    scores[index] += weight;
        }
        var selected = Enumerable.Range(0, PublicTopics.Length)
            .Where(index => scores[index] > 0)
            .OrderByDescending(index => scores[index]).ThenBy(index => index)
            .Take(limit).Select(index => new BilibiliRecommendationTopic(
                PublicTopics[index].Label, PublicTopics[index].Keyword,
                "根据本机收藏、观看状态、分类和标签生成"))
            .ToList();
        if (selected.Count == 0)
            selected.AddRange(new[] { PublicTopics[0], PublicTopics[3], PublicTopics[10] }.Take(limit).Select(topic => new BilibiliRecommendationTopic(
                topic.Label, topic.Keyword, "通用主题；不包含本地视频信息")));
        return selected;
    }

    private static string[] CleanTokens(IEnumerable<string> values, int limit, bool categories)
    {
        return values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => value.Length <= 48 && (!categories || value != "未分类"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
    }
}
