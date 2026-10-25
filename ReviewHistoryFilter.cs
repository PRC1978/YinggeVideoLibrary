namespace YinggeVideoLibrary;

// One transaction can affect several videos. Keep one searchable row per
// video without changing the persisted audit journal.
internal sealed class ReviewHistoryRow
{
    public DateTime TimeUtc { get; init; }
    public string Time => TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Action { get; init; } = "";
    public string VideoId { get; init; } = "";
    public string TransactionId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public int? Confidence { get; init; }
    public string BeforeStatus { get; init; } = "—";
    public string AfterStatus { get; init; } = "—";
    public string BeforeMetadata { get; init; } = "—";
    public string AfterMetadata { get; init; } = "—";
    public string BeforeTags { get; init; } = "—";
    public string AfterTags { get; init; } = "—";
    public string AutoAmateur { get; init; } = "";
    public string UndoId { get; init; } = "";

    public static List<ReviewHistoryRow> FromTransactions(IEnumerable<ReviewTransaction> transactions)
    {
        var rows = new List<ReviewHistoryRow>();
        foreach (var entry in transactions.Reverse())
        {
            foreach (var id in entry.Before.Keys.Concat(entry.After.Keys)
                         .Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty(""))
            {
                entry.Before.TryGetValue(id, out var before);
                entry.After.TryGetValue(id, out var after);
                rows.Add(new ReviewHistoryRow
                {
                    TimeUtc = entry.TimeUtc,
                    Action = entry.Action,
                    VideoId = id,
                    TransactionId = entry.Id,
                    Title = after?.Review?.CandidateTitle ?? before?.Review?.CandidateTitle ?? "",
                    Source = after?.Review?.CandidateSource ?? before?.Review?.CandidateSource ?? "",
                    Confidence = after?.Review?.Confidence ?? before?.Review?.Confidence,
                    BeforeStatus = before?.Review?.Status ?? "—",
                    AfterStatus = after?.Review?.Status ?? "—",
                    BeforeMetadata = before?.Metadata?.Title ?? "—",
                    AfterMetadata = after?.Metadata?.Title ?? "—",
                    BeforeTags = before?.Tags is { Count: > 0 } oldTags ? string.Join("、", oldTags) : "—",
                    AfterTags = after?.Tags is { Count: > 0 } newTags ? string.Join("、", newTags) : "—",
                    AutoAmateur = entry.Complete
                        ? $"{(before?.AutoAmateur == true ? "有" : "无")} → {(after?.AutoAmateur == true ? "有" : "无")}"
                        : "旧记录",
                    UndoId = entry.UndoesTransactionId ?? ""
                });
            }
        }
        return rows;
    }
}

internal sealed record ReviewHistoryQuery(
    string Status = "",
    string Action = "",
    string Source = "",
    string Confidence = "",
    DateTime? FromLocalDate = null,
    DateTime? ToLocalDate = null,
    string Search = "")
{
    public bool Matches(ReviewHistoryRow row)
    {
        if (Status.Length > 0 && !string.Equals(row.AfterStatus, Status, StringComparison.OrdinalIgnoreCase)) return false;
        if (Action.Length > 0 && !string.Equals(row.Action, Action, StringComparison.OrdinalIgnoreCase)) return false;
        if (Source.Length > 0 && !string.Equals(row.Source, Source, StringComparison.OrdinalIgnoreCase)) return false;
        if (Confidence.Length > 0)
        {
            if (row.Confidence is not int value) return false;
            if (Confidence == "low" && value >= 40 || Confidence == "medium" && (value < 40 || value >= 80) ||
                Confidence == "high" && value < 80) return false;
        }
        if (FromLocalDate.HasValue || ToLocalDate.HasValue)
        {
            var date = row.TimeUtc.ToLocalTime().Date;
            if (FromLocalDate.HasValue && date < FromLocalDate.Value.Date ||
                ToLocalDate.HasValue && date > ToLocalDate.Value.Date) return false;
        }
        var term = Search.Trim();
        if (term.Length == 0) return true;
        bool Contains(string text) => text.Contains(term, StringComparison.CurrentCultureIgnoreCase);
        return Contains(row.VideoId) || Contains(row.Title) || Contains(row.Source) ||
            Contains(row.Action) || Contains(row.TransactionId) || Contains(row.UndoId) ||
            Contains(row.BeforeMetadata) || Contains(row.AfterMetadata) ||
            Contains(row.BeforeTags) || Contains(row.AfterTags);
    }

    public static bool VerifyForDiagnostics()
    {
        var before = new ReviewItemSnapshot { Review = new AiReviewRecord
        {
            VideoId = "v1", CandidateTitle = "日本电影", CandidateSource = "TMDB", Confidence = 73, Status = "pending"
        }, Tags = ["剧情"] };
        var after = before.Clone();
        after.Review!.Status = "approved";
        after.Metadata = new MovieMetadata { Title = "正式片名" };
        var first = ReviewTransactionStore.Create("approve", new Dictionary<string, ReviewItemSnapshot> { ["v1"] = before },
            new Dictionary<string, ReviewItemSnapshot> { ["v1"] = after });
        first.TimeUtc = new DateTime(2026, 10, 25, 4, 0, 0, DateTimeKind.Utc);
        var undone = ReviewTransactionStore.Create("undo", first.After, first.Before, undoesTransactionId: first.Id);
        undone.TimeUtc = first.TimeUtc.AddDays(1);
        var rows = ReviewHistoryRow.FromTransactions([first, undone]);
        return rows.Count == 2 && rows[0].Action == "undo" && rows[1].AfterStatus == "approved" &&
            rows[1].VideoId == "v1" && rows[1].Confidence == 73 &&
            new ReviewHistoryQuery(Status: "approved", Source: "tmdb", Confidence: "medium").Matches(rows[1]) &&
            !new ReviewHistoryQuery(Status: "approved").Matches(rows[0]) &&
            new ReviewHistoryQuery(Action: "undo", Search: "剧情").Matches(rows[0]) &&
            new ReviewHistoryQuery(Search: "正式片名").Matches(rows[1]) &&
            new ReviewHistoryQuery(FromLocalDate: rows[1].TimeUtc.ToLocalTime().Date,
                ToLocalDate: rows[1].TimeUtc.ToLocalTime().Date).Matches(rows[1]) &&
            !new ReviewHistoryQuery(FromLocalDate: rows[1].TimeUtc.ToLocalTime().Date.AddDays(1)).Matches(rows[1]) &&
            !new ReviewHistoryQuery(Confidence: "high").Matches(rows[1]);
    }
}
