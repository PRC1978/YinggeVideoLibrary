using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class ReviewTransactionRecoveryDiagnostics
{
    public static IReadOnlyDictionary<string, bool> Run(string root)
    {
        Directory.CreateDirectory(root);
        var results = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (step, key) in new[]
        {
            ("config", "reviewCrashAfterConfigRecovers"),
            ("metadata", "reviewCrashAfterMetadataRecovers"),
            ("history", "reviewCrashAfterHistoryNoDuplicate")
        })
        {
            var data = Path.Combine(root, step);
            var before = Pending("one", "待审核", "旧标题");
            var after = Approved("one", "新标题");
            Seed(data, before);
            var transaction = Transaction("field-approve", before, after);
            ReviewTransactionStore.Begin(data, transaction);
            var interrupted = !ReviewTransactionStore.RecoverPendingAtStartupForDiagnostics(data,
                out _, reached => { if (reached == step) throw new IOException("simulated process stop"); });
            var retained = ReviewTransactionStore.HasPending(data);
            var replayed = ReviewTransactionStore.RecoverPendingAtStartup(data, out _);
            var loaded = ReviewTransactionStore.Load(data, out var invalid);
            results[key] = interrupted && retained && replayed && invalid == 0 &&
                !ReviewTransactionStore.HasPending(data) && loaded.Count(item => item.Id == transaction.Id) == 1 &&
                SameSaved(data, after) && UnrelatedPreserved(data);
        }

        foreach (var (action, step, key) in new[]
        {
            ("auto-approved", "metadata", "reviewAutoScrapeCrashRecovers"),
            ("ai-evaluate", "config", "reviewAiEvaluationCrashRecovers"),
            ("candidate", "config", "reviewCandidateCrashRetainsAcceptedMetadata"),
            ("unmatched", "config", "reviewUnmatchedCrashRecoversTag")
        })
        {
            var data = Path.Combine(root, action);
            var before = Pending("one", "原候选", "已保存资料");
            var after = action switch
            {
                "candidate" => before.Clone(),
                "unmatched" => before.Clone(),
                _ => Approved("one", action == "ai-evaluate" ? "AI 通过" : "自动通过")
            };
            if (action == "candidate")
            {
                after.Review!.Status = MainWindow.ReviewStatusFor(45, "review");
                after.Review.Confidence = 45;
                after.Review.CandidateTitle = "待人工候选";
            }
            if (action == "unmatched")
            {
                after.Review!.Status = "unmatched";
                after.Review.Confidence = 0;
                after.Tags = ["原标签", "素人"];
                after.AutoAmateur = true;
            }
            Seed(data, before);
            var transaction = Transaction(action, before, after);
            ReviewTransactionStore.Begin(data, transaction);
            var interrupted = !ReviewTransactionStore.RecoverPendingAtStartupForDiagnostics(data,
                out _, reached => { if (reached == step) throw new IOException("simulated process stop"); });
            var replayed = ReviewTransactionStore.RecoverPendingAtStartup(data, out _);
            var history = ReviewTransactionStore.Load(data, out var invalid);
            results[key] = interrupted && replayed && invalid == 0 &&
                history.Count(item => item.Id == transaction.Id) == 1 && SameSaved(data, after) &&
                UnrelatedPreserved(data) && !ReviewTransactionStore.HasPending(data);
        }

        {
            var data = Path.Combine(root, "stale-preview");
            var before = Pending("one", "待审核", "旧标题");
            var after = Approved("one", "新标题");
            Seed(data, before);
            var changed = before.Clone();
            changed.Tags = ["人工修改"];
            Seed(data, changed);
            var rejected = false;
            try { ReviewTransactionStore.Begin(data, Transaction("field-approve", before, after)); }
            catch (InvalidOperationException) { rejected = true; }
            results["reviewStalePreviewAndConcurrentEditBlocked"] = rejected &&
                !ReviewTransactionStore.HasPending(data) && SameSaved(data, changed);
        }

        {
            var data = Path.Combine(root, "recovery-conflict");
            var before = Pending("one", "待审核", "旧标题");
            var after = Approved("one", "新标题");
            Seed(data, before);
            var transaction = Transaction("field-approve", before, after);
            ReviewTransactionStore.Begin(data, transaction);
            ReviewTransactionStore.RecoverPendingAtStartupForDiagnostics(data,
                out _, reached => { if (reached == "config") throw new IOException("simulated process stop"); });
            var concurrent = new MovieMetadata { Title = "另一个窗口的标题" };
            MetadataStore.Save(data, new Dictionary<string, MovieMetadata> { ["one"] = concurrent });
            var refused = !ReviewTransactionStore.RecoverPendingAtStartup(data, out var reason);
            results["reviewRecoveryConflictFailsClosed"] = refused && reason.Contains("修改", StringComparison.Ordinal) &&
                ReviewTransactionStore.HasPending(data) &&
                MetadataStore.Load(data).GetValueOrDefault("one")?.Title == concurrent.Title &&
                ReviewTransactionStore.Load(data, out _).All(item => item.Id != transaction.Id);
        }

        {
            var data = Path.Combine(root, "auto-scrape-concurrent-metadata");
            var before = Pending("one", "待审核", "原始资料");
            var after = Approved("one", "自动搜刮资料");
            Seed(data, before);
            MetadataStore.Save(data, new Dictionary<string, MovieMetadata>
            { ["one"] = new() { Title = "另一窗口已保存的新资料" } });
            var stopped = false;
            try { ReviewTransactionStore.Begin(data, Transaction("auto-approved", before, after)); }
            catch (InvalidOperationException) { stopped = true; }
            results["reviewAutoScrapeConcurrentMetadataBlocked"] = stopped &&
                !ReviewTransactionStore.HasPending(data) &&
                MetadataStore.Load(data)["one"].Title == "另一窗口已保存的新资料";
        }

        results["reviewLowConfidenceGatePreserved"] =
            MainWindow.ReviewStatusFor(35, "approve") == "rejected" &&
            MainWindow.ReviewStatusFor(65, "approve") == "pending" &&
            MainWindow.ReviewStatusFor(95, "review") == "pending";

        {
            var data = Path.Combine(root, "three-undos");
            var s0 = Pending("one", "待审核", "原始");
            var s1 = Approved("one", "第一版");
            var s2 = s1.Clone(); s2.Review!.Status = "rejected";
            var s3 = Approved("one", "第二版");
            Seed(data, s0);
            var a = Transaction("field-approve", s0, s1);
            var b = Transaction("reject", s1, s2);
            var c = Transaction("field-approve", s2, s3);
            var normal = Commit(data, a) && Commit(data, b) && Commit(data, c);
            var first = ReviewTransactionStore.LastUndoable(ReviewTransactionStore.Load(data, out _))?.Id == c.Id;
            var undoC = Transaction("undo", s3, s2, c.Id);
            var second = Commit(data, undoC) &&
                ReviewTransactionStore.LastUndoable(ReviewTransactionStore.Load(data, out _))?.Id == b.Id;
            var undoB = Transaction("undo", s2, s1, b.Id);
            var third = Commit(data, undoB) &&
                ReviewTransactionStore.LastUndoable(ReviewTransactionStore.Load(data, out _))?.Id == a.Id;
            var undoA = Transaction("undo", s1, s0, a.Id);
            var fourth = Commit(data, undoA);
            var history = ReviewTransactionStore.Load(data, out var invalid);
            results["reviewThreeSuccessiveUndoTransactions"] = normal && first && second && third && fourth &&
                invalid == 0 && history.Count == 6 && ReviewTransactionStore.LastUndoable(history) is null &&
                SameSaved(data, s0) && UnrelatedPreserved(data);
        }
        return results;
    }

    static bool Commit(string data, ReviewTransaction transaction)
    {
        ReviewTransactionStore.Begin(data, transaction);
        return ReviewTransactionStore.RecoverPendingAtStartup(data, out _);
    }

    static ReviewItemSnapshot Pending(string id, string title, string metadataTitle) => new()
    {
        Review = new AiReviewRecord { VideoId = id, CandidateTitle = title, Status = "pending", Confidence = 55 },
        Metadata = new MovieMetadata { Title = metadataTitle }, Tags = ["原标签"]
    };

    static ReviewItemSnapshot Approved(string id, string title) => new()
    {
        Review = new AiReviewRecord { VideoId = id, CandidateTitle = title, Status = "approved", Confidence = 95 },
        Metadata = new MovieMetadata { Title = title, DetailFetched = true }, Tags = ["新标签"]
    };

    static ReviewTransaction Transaction(string action, ReviewItemSnapshot before, ReviewItemSnapshot after,
        string? undoId = null)
        => ReviewTransactionStore.Create(action,
            new Dictionary<string, ReviewItemSnapshot> { ["one"] = before },
            new Dictionary<string, ReviewItemSnapshot> { ["one"] = after }, undoesTransactionId: undoId);

    static void Seed(string data, ReviewItemSnapshot state)
    {
        Directory.CreateDirectory(data);
        var config = new LibraryConfig();
        if (state.Review is not null) config.AiReviews["one"] = state.Review;
        if (state.Tags is not null) config.Tags["one"] = state.Tags;
        if (state.AutoAmateur) config.AutoAmateurTags.Add("one");
        File.WriteAllText(Path.Combine(data, "config.json"), JsonSerializer.Serialize(config));
        var metadata = new Dictionary<string, MovieMetadata>
        {
            ["unrelated"] = new() { Title = "保留的其它视频" }
        };
        if (state.Metadata is not null) metadata["one"] = state.Metadata;
        File.WriteAllText(Path.Combine(data, "metadata.json"), JsonSerializer.Serialize(metadata));
    }

    static bool SameSaved(string data, ReviewItemSnapshot expected)
    {
        var config = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(Path.Combine(data, "config.json")))!;
        var metadata = MetadataStore.Load(data);
        var actual = new ReviewItemSnapshot
        {
            Review = config.AiReviews.GetValueOrDefault("one"),
            Metadata = metadata.GetValueOrDefault("one"),
            Tags = config.Tags.GetValueOrDefault("one"),
            AutoAmateur = config.AutoAmateurTags.Contains("one", StringComparer.OrdinalIgnoreCase)
        };
        return ReviewTransactionStore.Equal(actual, expected);
    }

    static bool UnrelatedPreserved(string data)
        => MetadataStore.Load(data).GetValueOrDefault("unrelated")?.Title == "保留的其它视频";
}
