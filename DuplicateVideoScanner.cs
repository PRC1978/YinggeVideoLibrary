using System.IO;
using System.Security.Cryptography;

namespace YinggeVideoLibrary;

internal sealed record DuplicateVideoInput(
    string Id, string Path, string Title, long IndexedSize, string[] Categories, string[] Tags,
    string PlatformRating = "");

internal sealed record DuplicateVideoRecord(
    string Id, string Path, string Title, long Size, string[] Categories, string[] Tags,
    double? PersonalScore, string PlatformRating)
{
    public string CategoryText => Categories.Length == 0 ? "未分类" : string.Join(" / ", Categories);
    public string TagsText => Tags.Length == 0 ? "—" : string.Join("、", Tags);
    public string ScoreText => PersonalScore is { } score ? score.ToString("0.#") + " / 10" : "未评分";
    public string PlatformScoreText => string.IsNullOrWhiteSpace(PlatformRating) ? "未记录" : PlatformRating;
}

internal sealed record DuplicateVideoGroup(string Sha256, long Size, IReadOnlyList<DuplicateVideoRecord> Videos)
{
    public string Summary => $"{Videos.Count} 部 · {Size / 1024d / 1024d:0.##} MB · {Videos[0].Title}";
}

internal sealed record DuplicateVideoScanProgress(int Completed, int Total, int Skipped);
internal sealed record DuplicateVideoScanResult(IReadOnlyList<DuplicateVideoGroup> Groups, int Candidates, int Skipped);
internal sealed record DuplicateLocalMergeProposal(
    string KeepId, string KeepPath, string Sha256, long Size,
    string[] Categories, string[] Tags, double? PersonalScore, bool RatingNeedsDecision)
{
    public string Describe() => string.Join(Environment.NewLine,
        "", "计划合并（仅预览，尚未写入）", "SHA-256：" + Sha256,
        "保留路径：" + KeepPath,
        "分类并集：" + (Categories.Length == 0 ? "未分类" : string.Join(" / ", Categories)),
        "标签并集：" + (Tags.Length == 0 ? "—" : string.Join("、", Tags)),
        "个人评分：" + (RatingNeedsDecision ? "存在不同评分，需人工选择" :
            PersonalScore is { } score ? score.ToString("0.#") + " / 10" : "未评分"),
        "当前仅供核对；没有文件删除或资料写入。");
}

internal static class DuplicateVideoScanner
{
    internal const int PageSize = 30;

    internal static async Task<DuplicateVideoScanResult> ScanAsync(
        IReadOnlyList<DuplicateVideoInput> videos, IReadOnlyDictionary<string, PersonalRatingEntry> ratings,
        IProgress<DuplicateVideoScanProgress>? progress, CancellationToken cancellationToken)
    {
        // The index size only narrows the expensive work. A digest is accepted
        // only if the file still has that size and timestamp after the read.
        var candidates = videos.Where(video => video.IndexedSize > 0 && !string.IsNullOrWhiteSpace(video.Path))
            .GroupBy(video => video.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .GroupBy(video => video.IndexedSize)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        var buckets = new Dictionary<(long Size, string Hash), List<DuplicateVideoRecord>>();
        var skipped = 0;
        for (var index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var video = candidates[index];
            try
            {
                var before = new FileInfo(video.Path);
                if (!before.Exists || before.Length != video.IndexedSize) { skipped++; continue; }
                var changedAt = before.LastWriteTimeUtc;
                await using var stream = new FileStream(video.Path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length != video.IndexedSize) { skipped++; continue; }
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                before.Refresh();
                if (!before.Exists || before.Length != video.IndexedSize || before.LastWriteTimeUtc != changedAt)
                {
                    skipped++;
                    continue;
                }
                var key = (video.IndexedSize, hash);
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = [];
                var score = ratings.TryGetValue(video.Id, out var rating) &&
                    double.IsFinite(rating.Score) && rating.Score is >= 0 and <= 10 ? rating.Score : (double?)null;
                bucket.Add(new DuplicateVideoRecord(video.Id, video.Path, video.Title,
                    video.IndexedSize, video.Categories, video.Tags, score, video.PlatformRating));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
            finally
            {
                if ((index + 1) % 8 == 0 || index + 1 == candidates.Length)
                    progress?.Report(new DuplicateVideoScanProgress(index + 1, candidates.Length, skipped));
            }
        }
        var groups = buckets.Where(pair => pair.Value.Count > 1)
            .Select(pair => new DuplicateVideoGroup(pair.Key.Hash, pair.Key.Size,
                pair.Value.OrderBy(video => video.Path, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderByDescending(group => group.Videos.Count)
            .ThenByDescending(group => group.Size)
            .ThenBy(group => group.Videos[0].Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DuplicateVideoScanResult(groups, candidates.Length, skipped);
    }

    internal static DuplicateVideoGroup[] Filter(IReadOnlyList<DuplicateVideoGroup> groups, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return groups.ToArray();
        var term = query.Trim();
        return groups.Where(group => group.Sha256.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            group.Videos.Any(video => video.Path.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                video.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                video.Categories.Any(category => category.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                video.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
    }

    internal static DuplicateVideoGroup[] Page(IReadOnlyList<DuplicateVideoGroup> groups, int page) =>
        groups.Skip(Math.Max(0, page) * PageSize).Take(PageSize).ToArray();

    internal static string Compare(DuplicateVideoGroup group, DuplicateVideoRecord keep)
    {
        if (!group.Videos.Any(video => video.Id == keep.Id && video.Path == keep.Path))
            throw new ArgumentException("保留项不属于这一重复组。", nameof(keep));
        var lines = new List<string>
        {
            "差异预览只读；确认资料合并后仅修改保留项的本地分类、标签与评分，绝不删除或修改视频文件。",
            "保留：" + keep.Path
        };
        foreach (var other in group.Videos.Where(video => video.Id != keep.Id || video.Path != keep.Path))
        {
            lines.Add("");
            lines.Add("对比：" + other.Path);
            if (!string.Equals(other.Title, keep.Title, StringComparison.Ordinal))
                lines.Add($"标题：{keep.Title} ↔ {other.Title}");
            if (!keep.Categories.SequenceEqual(other.Categories, StringComparer.OrdinalIgnoreCase))
                lines.Add($"分类：{keep.CategoryText} ↔ {other.CategoryText}");
            if (!keep.Tags.SequenceEqual(other.Tags, StringComparer.OrdinalIgnoreCase))
                lines.Add($"标签：{keep.TagsText} ↔ {other.TagsText}");
            if (keep.PersonalScore != other.PersonalScore)
                lines.Add($"我的评分：{keep.ScoreText} ↔ {other.ScoreText}");
            if (!string.Equals(keep.PlatformRating, other.PlatformRating, StringComparison.Ordinal))
                lines.Add($"平台评分：{keep.PlatformScoreText} ↔ {other.PlatformScoreText}");
            if (lines[^1] == "对比：" + other.Path) lines.Add("资料相同，仅文件路径不同。");
        }
        lines.Add(ProposeLocalMerge(group, keep).Describe());
        return string.Join(Environment.NewLine, lines);
    }

    internal static DuplicateLocalMergeProposal ProposeLocalMerge(DuplicateVideoGroup group, DuplicateVideoRecord keep)
    {
        if (!group.Videos.Any(video => video.Id == keep.Id && video.Path == keep.Path))
            throw new ArgumentException("保留项不属于这一重复组。", nameof(keep));
        var ordered = new[] { keep }.Concat(group.Videos.Where(video => video.Id != keep.Id || video.Path != keep.Path));
        var categories = ordered.SelectMany(video => video.Categories)
            .Where(value => !string.IsNullOrWhiteSpace(value) && value != "未分类")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tags = ordered.SelectMany(video => video.Tags)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var scores = group.Videos.Where(video => video.PersonalScore is not null)
            .Select(video => video.PersonalScore!.Value).Distinct().ToArray();
        return new DuplicateLocalMergeProposal(keep.Id, keep.Path, group.Sha256, group.Size,
            categories, tags, scores.Length == 1 ? scores[0] : keep.PersonalScore, scores.Length > 1);
    }
}
