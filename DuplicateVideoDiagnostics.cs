using System.IO;
using System.Text;

namespace YinggeVideoLibrary;

internal static class DuplicateVideoDiagnostics
{
    internal static async Task<IReadOnlyDictionary<string, bool>> VerifyAsync(string root)
    {
        Directory.CreateDirectory(root);
        var inputs = new List<DuplicateVideoInput>();
        var ratings = new Dictionary<string, PersonalRatingEntry>(StringComparer.OrdinalIgnoreCase);
        for (var group = 0; group < 31; group++)
        {
            var content = Encoding.UTF8.GetBytes($"verified-{group:D2}");
            for (var copy = 0; copy < 2; copy++)
            {
                var id = $"{group:D2}-{copy}";
                var path = Path.Combine(root, $"group-{group:D2}-{copy}.mp4");
                File.WriteAllBytes(path, content);
                inputs.Add(new DuplicateVideoInput(id, path, $"作品 {group:D2}", content.Length,
                    copy == 0 ? ["人物", "作品"] : ["作品"],
                    copy == 0 ? ["收藏"] : ["待整理"],
                    copy == 0 ? "TMDB 8/10" : "TVmaze 6/10"));
            }
        }
        ratings["00-0"] = new PersonalRatingEntry { Score = 8.5 };
        ratings["00-1"] = new PersonalRatingEntry { Score = 7 };
        // Same indexed length, different bytes: the size prefilter must not
        // report this file as a duplicate. A missing path is skipped cleanly.
        var unique = Path.Combine(root, "unique.mp4");
        File.WriteAllBytes(unique, Encoding.UTF8.GetBytes("verified-XX"));
        inputs.Add(new DuplicateVideoInput("unique", unique, "其他", 11, [], []));
        inputs.Add(new DuplicateVideoInput("missing", Path.Combine(root, "missing.mp4"), "缺失", 11, [], []));
        var result = await DuplicateVideoScanner.ScanAsync(inputs, ratings, null, CancellationToken.None);
        var allGroupsShown = result.Groups.Count == 31 && result.Skipped == 1 &&
            DuplicateVideoScanner.Page(result.Groups, 0).Length == 30 &&
            DuplicateVideoScanner.Page(result.Groups, 1).Length == 1 &&
            DuplicateVideoScanner.Page(result.Groups, 2).Length == 0;
        var searchable = DuplicateVideoScanner.Filter(result.Groups, "group-30-").Length == 1 &&
            DuplicateVideoScanner.Filter(result.Groups, "待整理").Length == 31;
        var first = result.Groups.Single(group => group.Videos.Any(video => video.Id == "00-0"));
        var keep = first.Videos.Single(video => video.Id == "00-0");
        var preview = DuplicateVideoScanner.Compare(first, keep);
        var proposal = DuplicateVideoScanner.ProposeLocalMerge(first, keep);
        var differencesShown = keep.PersonalScore == 8.5 &&
            preview.Contains("分类：", StringComparison.Ordinal) &&
            preview.Contains("标签：", StringComparison.Ordinal) &&
            preview.Contains("我的评分：", StringComparison.Ordinal) &&
            preview.Contains("平台评分：", StringComparison.Ordinal) &&
            preview.Contains(keep.Path, StringComparison.Ordinal) &&
            proposal.KeepPath == keep.Path && proposal.Sha256 == first.Sha256 &&
            proposal.Categories.SequenceEqual(["人物", "作品"]) &&
            proposal.Tags.SequenceEqual(["收藏", "待整理"]) &&
            proposal.RatingNeedsDecision && preview.Contains("需人工选择", StringComparison.Ordinal);
        var unchanged = File.ReadAllBytes(keep.Path).SequenceEqual(Encoding.UTF8.GetBytes("verified-00")) &&
            File.Exists(first.Videos.Single(video => video.Id == "00-1").Path);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        var cancelled = false;
        try { await DuplicateVideoScanner.ScanAsync(inputs, ratings, null, stopped.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        return new Dictionary<string, bool>
        {
            ["duplicateSha256BrowseAll31GroupsAndPage"] = allGroupsShown,
            ["duplicatePathTagSearchAndMetadataPreview"] = searchable && differencesShown,
            ["duplicateReadOnlyAndCancelable"] = unchanged && cancelled
        };
    }
}
