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

internal sealed record RecommendationOptions(
    bool UseFavorites = true,
    bool UseWatchStatus = true,
    bool UseCategories = true,
    bool UseTags = true,
    bool EnableOnlineTopics = true,
    IReadOnlySet<string>? LessLikeVideoIds = null);

// Keyword is selected from a fixed, generic list. No title, filename, path,
// person name, custom tag or custom category is sent to Bilibili.
internal sealed record BilibiliRecommendationTopic(string Label, string Keyword, string Reason);

internal static class RecommendationEngine
{
    private sealed record RankedCandidate(LocalRecommendation Recommendation, string Group);

    private sealed class GroupCursor(List<RankedCandidate> candidates)
    {
        public List<RankedCandidate> Candidates { get; } = candidates;
        public int Index { get; set; }
    }

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

    public static List<LocalRecommendation> Rank(IReadOnlyList<RecommendationSnapshot> videos, int limit = 24,
        RecommendationOptions? options = null)
    {
        if (videos.Count == 0 || limit <= 0) return [];
        options ??= new RecommendationOptions();
        var lessLike = options.LessLikeVideoIds;
        var categoryInterest = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var tagInterest = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var reducedCategories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reducedTags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
        {
            if (lessLike?.Contains(video.Id) == true)
            {
                if (options.UseCategories)
                    foreach (var category in CleanTokens(video.Categories, 8, true))
                        reducedCategories[category] = reducedCategories.GetValueOrDefault(category) + 1;
                if (options.UseTags)
                    foreach (var tag in CleanTokens(video.Tags, 12, false))
                        reducedTags[tag] = reducedTags.GetValueOrDefault(tag) + 1;
                continue;
            }
            var weight = InterestWeight(video, options);
            if (weight == 0) continue;
            if (options.UseCategories)
                foreach (var category in CleanTokens(video.Categories, 8, true))
                    categoryInterest[category] = categoryInterest.GetValueOrDefault(category) + weight;
            if (options.UseTags)
                foreach (var tag in CleanTokens(video.Tags, 12, false))
                    tagInterest[tag] = tagInterest.GetValueOrDefault(tag) + weight;
        }

        var newest = videos.Max(video => video.Modified);
        var ranked = new List<RankedCandidate>(videos.Count);
        foreach (var video in videos)
        {
            if (lessLike?.Contains(video.Id) == true) continue;
            var categories = options.UseCategories ? CleanTokens(video.Categories, 8, true) : [];
            var tags = options.UseTags ? CleanTokens(video.Tags, 12, false) : [];
            // The candidate must not count its own favorite/watch signals as evidence
            // that somebody with similar interests also liked it.
            var ownWeight = InterestWeight(video, options);
            var bestTag = tags.Select(tag => (tag, affinity: Math.Max(0, tagInterest.GetValueOrDefault(tag) - ownWeight)))
                .OrderByDescending(hit => hit.affinity).FirstOrDefault();
            var bestCategory = categories.Select(category => (category, affinity: Math.Max(0, categoryInterest.GetValueOrDefault(category) - ownWeight)))
                .OrderByDescending(hit => hit.affinity).FirstOrDefault();
            var baseScore = !options.UseWatchStatus ? 24d : video.WatchStatus switch
            {
                "未看" => 36d,
                "观看中" => 28d,
                _ => 8d
            };
            if (options.UseFavorites && video.Favorite) baseScore += 8;
            var daysFromNewest = Math.Max(0, (newest - video.Modified).TotalDays);
            var freshness = 14d / (1d + daysFromNewest / 45d);
            var tagBonus = Math.Min(24d, 6d * Math.Log2(1d + bestTag.affinity));
            var categoryBonus = Math.Min(18d, 5d * Math.Log2(1d + bestCategory.affinity));
            var reducedTagCount = tags.Length == 0 ? 0 : tags.Max(tag => reducedTags.GetValueOrDefault(tag));
            var reducedCategoryCount = categories.Length == 0 ? 0 : categories.Max(category => reducedCategories.GetValueOrDefault(category));
            var reduction = Math.Min(32d, 9d * Math.Log2(1d + reducedTagCount)
                + 7d * Math.Log2(1d + reducedCategoryCount));
            var score = baseScore + freshness + tagBonus + categoryBonus - reduction;
            var reason = tagBonus >= categoryBonus && tagBonus > 0
                ? $"与你常看的标签「{bestTag.tag}」相关"
                : categoryBonus > 0
                    ? $"与你常看的分类「{bestCategory.category}」相关"
                    : options.UseWatchStatus && video.WatchStatus == "观看中" ? "继续观看"
                    : options.UseFavorites && video.Favorite ? "你收藏的视频"
                    : options.UseWatchStatus && video.WatchStatus == "已看" ? "重温已看视频" : "最近加入的视频";
            var group = categories.Length > 0 ? categories[0].Split(" / ", 2, StringSplitOptions.TrimEntries)[0]
                : tags.Length > 0 ? tags[0] : "未分类";
            ranked.Add(new RankedCandidate(new LocalRecommendation(video.Id, score, reason), group));
        }

        // A category with thousands of matching videos should not fill every
        // recommendation page. Group heads stay score ordered; a small penalty
        // after each pick gives other categories room without hiding any video.
        var groups = ranked.GroupBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GroupCursor(group.OrderByDescending(item => item.Recommendation.Score)
                .ThenBy(item => item.Recommendation.VideoId, StringComparer.Ordinal).ToList()))
            .ToArray();
        var comparer = Comparer<(double Score, string Group)>.Create((left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : StringComparer.Ordinal.Compare(left.Group, right.Group);
        });
        var queue = new PriorityQueue<GroupCursor, (double Score, string Group)>(comparer);
        foreach (var group in groups)
        {
            var head = group.Candidates[0];
            queue.Enqueue(group, (head.Recommendation.Score, head.Group));
        }
        var result = new List<LocalRecommendation>(Math.Min(limit, videos.Count));
        while (result.Count < limit && queue.TryDequeue(out var group, out _))
        {
            result.Add(group.Candidates[group.Index].Recommendation);
            group.Index++;
            if (group.Index >= group.Candidates.Count) continue;
            var next = group.Candidates[group.Index];
            var fairnessPenalty = 9d * Math.Log2(1d + group.Index);
            queue.Enqueue(group, (next.Recommendation.Score - fairnessPenalty, next.Group));
        }
        return result;
    }

    private static double InterestWeight(RecommendationSnapshot video, RecommendationOptions options) =>
        options.UseFavorites && video.Favorite ? 5d : !options.UseWatchStatus ? 0d : video.WatchStatus switch
    {
        "观看中" => 2d,
        "已看" => 1d,
        _ => 0d
    };

    public static List<BilibiliRecommendationTopic> BuildBilibiliTopics(IReadOnlyList<RecommendationSnapshot> videos,
        int limit = 6, RecommendationOptions? options = null)
    {
        options ??= new RecommendationOptions();
        if (limit <= 0 || !options.EnableOnlineTopics) return [];
        var scores = new double[PublicTopics.Length];
        var reducedTopics = new int[PublicTopics.Length];
        var hasExplicitInterest = videos.Any(video =>
            options.LessLikeVideoIds?.Contains(video.Id) != true
            && ((options.UseFavorites && video.Favorite)
                || (options.UseWatchStatus && video.WatchStatus is "观看中" or "已看")));
        foreach (var video in videos)
        {
            var interests = (options.UseCategories ? CleanTokens(video.Categories, 8, true) : [])
                .Concat(options.UseTags ? CleanTokens(video.Tags, 12, false) : []).ToArray();
            if (options.LessLikeVideoIds?.Contains(video.Id) == true)
            {
                for (var index = 0; index < PublicTopics.Length; index++)
                    if (MatchesPublicTopic(interests, PublicTopics[index].Aliases)) reducedTopics[index]++;
                continue;
            }
            var weight = options.UseFavorites && video.Favorite ? 4d : !options.UseWatchStatus
                ? hasExplicitInterest ? 0d : 0.25d : video.WatchStatus switch
            {
                "观看中" => 2d,
                "已看" => 1d,
                // A very large unwatched library should not drown out actual
                // favorites and viewing history. Use its topics only for a cold start.
                _ => hasExplicitInterest ? 0d : 0.25d
            };
            if (weight == 0) continue;
            for (var index = 0; index < PublicTopics.Length; index++)
                if (MatchesPublicTopic(interests, PublicTopics[index].Aliases))
                    scores[index] += weight;
        }
        for (var index = 0; index < scores.Length; index++)
            scores[index] /= 1d + 2d * reducedTopics[index];
        var selected = Enumerable.Range(0, PublicTopics.Length)
            .Where(index => scores[index] > 0)
            .OrderByDescending(index => scores[index]).ThenBy(index => index)
            .Take(limit).Select(index => new BilibiliRecommendationTopic(
                PublicTopics[index].Label, PublicTopics[index].Keyword,
                "根据本机收藏、观看状态、分类和标签生成"))
            .ToList();
        foreach (var fallback in new[] { PublicTopics[0], PublicTopics[3], PublicTopics[10], PublicTopics[2], PublicTopics[5], PublicTopics[9] })
        {
            if (selected.Count >= limit) break;
            if (selected.Any(topic => topic.Keyword == fallback.Keyword)) continue;
            var topicIndex = Array.FindIndex(PublicTopics, topic => topic.Keyword == fallback.Keyword);
            if (topicIndex >= 0 && reducedTopics[topicIndex] > 0) continue;
            selected.Add(new BilibiliRecommendationTopic(fallback.Label, fallback.Keyword, "通用主题；不包含本地视频信息"));
        }
        return selected;
    }

    private static bool MatchesPublicTopic(IEnumerable<string> interests, IEnumerable<string> aliases) =>
        interests.Any(value => aliases.Any(alias =>
            alias.All(char.IsAsciiLetter) ? value.Equals(alias, StringComparison.OrdinalIgnoreCase)
                : value.Contains(alias, StringComparison.OrdinalIgnoreCase)));

    private static string[] CleanTokens(IEnumerable<string> values, int limit, bool categories)
    {
        return values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => value.Length <= 48 && (!categories || value != "未分类"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
    }
}
