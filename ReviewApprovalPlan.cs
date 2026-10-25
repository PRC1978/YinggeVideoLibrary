using System.IO;

namespace YinggeVideoLibrary;

internal sealed class ReviewApprovalRow
{
    internal required string VideoId { get; init; }
    internal required string VideoTitle { get; init; }
    internal required ReviewItemSnapshot Before { get; init; }
    internal required MovieMetadata Candidate { get; init; }
    internal Dictionary<string, ReviewFieldSelection> Choices { get; } = new(StringComparer.Ordinal);

    public string Display => $"{Before.Review?.Confidence ?? 0}% · {VideoTitle} · {Before.Review?.CandidateSource}";

    internal ReviewFieldSelection Choice(string key) => Choices.TryGetValue(key, out var selection)
        ? selection : ReviewFieldApproval.Default(key, Before.Metadata, Candidate);

    internal ReviewItemSnapshot After(bool approved, DateTime reviewedAt)
    {
        var result = Before.Clone();
        if (result.Review is null) throw new InvalidDataException("审核候选已经失效。");
        result.Review.Status = approved ? "approved" : "rejected";
        result.Review.DecisionSource = "manual";
        result.Review.ReviewedAt = reviewedAt;
        if (approved)
        {
            result.Metadata = ReviewFieldApproval.Build(Before.Metadata, Candidate, Choices);
            if (result.AutoAmateur)
            {
                result.Tags?.RemoveAll(tag => tag.Equals("素人", StringComparison.CurrentCultureIgnoreCase));
                result.AutoAmateur = false;
            }
        }
        return result;
    }
}

internal sealed class PreparedReviewApproval
{
    internal required bool Approved { get; init; }
    internal required DateTime PreparedAt { get; init; }
    internal required Dictionary<string, ReviewItemSnapshot> Before { get; init; }
    internal required Dictionary<string, ReviewItemSnapshot> After { get; init; }
    internal required IReadOnlyList<ReviewApprovalRow> Rows { get; init; }

    internal string Describe(int limit = 18)
    {
        var changed = 0;
        var kept = 0;
        var cleared = 0;
        var sources = 0;
        foreach (var row in Rows)
        {
            if (!Approved) continue;
            foreach (var field in ReviewFieldApproval.Fields)
            {
                var before = ReviewFieldApproval.Value(row.Before.Metadata, field.Key);
                var after = ReviewFieldApproval.Value(After[row.VideoId].Metadata, field.Key);
                if (before == after) { kept++; continue; }
                changed++;
                if (after.Length == 0) cleared++;
                if (row.Choice(field.Key).Action == ReviewFieldAction.Source) sources++;
            }
        }
        var lines = Rows.Take(limit).Select(row =>
        {
            var changedFields = Approved ? ReviewFieldApproval.Fields
                .Where(field => ReviewFieldApproval.Value(row.Before.Metadata, field.Key) !=
                    ReviewFieldApproval.Value(After[row.VideoId].Metadata, field.Key))
                .Select(field => field.Label).ToList() : [];
            return $"{row.Before.Review?.Confidence ?? 0}% · {row.Before.Review?.CandidateSource} · {row.VideoTitle}" +
                (Approved ? $" → 更改 {changedFields.Count} 项：{string.Join("、", changedFields.Take(6))}" : " → 原资料保持不变");
        });
        return $"将{(Approved ? "通过" : "否决")} {Rows.Count} 条候选。" +
            (Approved ? $"实际改变 {changed} 个字段，保留 {kept} 个字段，清空 {cleared} 个字段，采用来源原值 {sources} 个字段。" :
                "不会修改任何已保存的视频资料或标签。") +
            "\n\n" + string.Join("\n", lines) + (Rows.Count > limit ? $"\n…另有 {Rows.Count - limit} 条" : "") +
            "\n\n确认后会再次核对当前内存和磁盘状态；若预览期间候选或原资料改变，操作会被拒绝。";
    }
}

internal static class ReviewApprovalPlanner
{
    internal static PreparedReviewApproval Prepare(IReadOnlyList<ReviewApprovalRow> rows, bool approved,
        DateTime? reviewedAt = null)
    {
        if (rows.Count == 0) throw new InvalidDataException("没有可审核的候选。");
        var time = reviewedAt ?? DateTime.Now;
        var before = new Dictionary<string, ReviewItemSnapshot>(StringComparer.OrdinalIgnoreCase);
        var after = new Dictionary<string, ReviewItemSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Before.Review?.Status is not ("pending" or "rejected"))
                throw new InvalidDataException($"视频 {row.VideoId} 的审核状态已变化，请刷新队列。");
            if (!before.TryAdd(row.VideoId, row.Before.Clone()))
                throw new InvalidDataException("审核选择包含重复的视频。");
            after[row.VideoId] = row.After(approved, time);
        }
        return new PreparedReviewApproval
        {
            Approved = approved, PreparedAt = time, Before = before, After = after, Rows = rows
        };
    }

    internal static bool Matches(PreparedReviewApproval prepared,
        IReadOnlyDictionary<string, ReviewItemSnapshot> current) =>
        prepared.Before.Count == current.Count && prepared.Before.All(pair =>
            current.TryGetValue(pair.Key, out var live) && ReviewTransactionStore.Equal(pair.Value, live));

    internal static bool VerifyForDiagnostics()
    {
        var old = new MovieMetadata { Title = "手工标题", Director = "手工导演", Code = "AB-123" };
        var candidate = new MovieMetadata { Title = "候选标题", Director = "候选导演", Code = "AB-123", Source = "JavDB" };
        var row = new ReviewApprovalRow
        {
            VideoId = "one", VideoTitle = "本地视频", Candidate = candidate,
            Before = new ReviewItemSnapshot
            {
                Metadata = old, Review = new AiReviewRecord { VideoId = "one", Status = "pending", Confidence = 30,
                    CandidateTitle = candidate.Title, CandidateCode = candidate.Code, CandidateSource = candidate.Source },
                Tags = ["素人", "自定义"], AutoAmateur = true
            }
        };
        var preview = Prepare([row], true, new DateTime(2026, 1, 1));
        if (preview.After["one"].Metadata?.Title != "手工标题" ||
            preview.After["one"].Metadata?.Director != "手工导演" ||
            preview.After["one"].Tags is not { Count: 1 } tags || tags[0] != "自定义" ||
            preview.After["one"].AutoAmateur || !Matches(preview, new Dictionary<string, ReviewItemSnapshot> { ["one"] = row.Before.Clone() })) return false;
        row.Choices[nameof(MovieMetadata.Director)] = new(ReviewFieldAction.Adopt);
        var changed = Prepare([row], true, new DateTime(2026, 1, 1));
        if (changed.After["one"].Metadata?.Director != "候选导演" ||
            changed.After["one"].Metadata?.Title != "手工标题" ||
            !changed.Describe().Contains("导演", StringComparison.Ordinal)) return false;
        var stale = row.Before.Clone();
        stale.Metadata!.Title = "另一窗口的新标题";
        if (Matches(changed, new Dictionary<string, ReviewItemSnapshot> { ["one"] = stale })) return false;
        var rejected = Prepare([row], false);
        return rejected.After["one"].Review?.Status == "rejected" &&
            rejected.After["one"].Metadata?.Title == "手工标题" &&
            rejected.After["one"].Tags?.Count == 2;
    }
}
