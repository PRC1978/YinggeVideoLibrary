using System.IO;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void StoreUnmatchedScrape(VideoItem video, string source, string reason)
    {
        var locked = false;
        try
        {
            try { ConfigWriteMutex.WaitOne(); locked = true; }
            catch (AbandonedMutexException) { locked = true; }
            var before = CaptureReviewState(video.Id);
            var after = before.Clone();
            after.Review = new AiReviewRecord
            {
                VideoId = video.Id, CandidateTitle = "未找到匹配", CandidateSource = source,
                Confidence = 0, Status = "unmatched", DecisionSource = "rules",
                Reason = reason, Model = "local-rules"
            };
            after = WithAutomaticAmateurTag(video, after, true);
            var transaction = ReviewTransactionStore.Create("unmatched",
                new Dictionary<string, ReviewItemSnapshot> { [video.Id] = before },
                new Dictionary<string, ReviewItemSnapshot> { [video.Id] = after });
            if (!TryCommitJournaledReview(transaction, out var error))
                throw new IOException("无匹配审核记录无法完整保存：" + error);
            ApplyReviewBadge(video);
        }
        finally { if (locked) ConfigWriteMutex.ReleaseMutex(); }
        if (view == "scrape") RefreshReviewQueue();
        Log($"搜刮无匹配：{video.DisplayTitle} · {source}；已标记素人");
    }
}
