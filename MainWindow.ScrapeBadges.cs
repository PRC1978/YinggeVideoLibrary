namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void SetAutomaticAmateurTag(VideoItem video, bool shouldTag)
    {
        var tracked = config.AutoAmateurTags.Contains(video.Id, StringComparer.OrdinalIgnoreCase);
        if (shouldTag)
        {
            if (video.Tags.Contains("素人", StringComparer.CurrentCultureIgnoreCase)) return;
            video.Tags.Add("素人");
            video.RefreshTags();
            config.Tags[video.Id] = ParseTags(string.Join(",", video.Tags));
            if (!tracked) config.AutoAmateurTags.Add(video.Id);
        }
        else if (tracked)
        {
            video.Tags.RemoveAll(tag => tag.Equals("素人", StringComparison.CurrentCultureIgnoreCase));
            video.RefreshTags();
            if (video.Tags.Count == 0) config.Tags.Remove(video.Id);
            else config.Tags[video.Id] = ParseTags(string.Join(",", video.Tags));
            config.AutoAmateurTags.RemoveAll(id => id.Equals(video.Id, StringComparison.OrdinalIgnoreCase));
        }
    }

    void StoreUnmatchedScrape(VideoItem video, string source, string reason)
    {
        var previous = config.AiReviews.TryGetValue(video.Id, out var old)
            ? new Dictionary<string, AiReviewRecord> { [video.Id] = CloneReview(old) }
            : new Dictionary<string, AiReviewRecord>();
        var metadataBefore = new Dictionary<string, MovieMetadata?> { [video.Id] = metadata.TryGetValue(video.Id, out var saved) ? saved : null };
        config.AiReviews[video.Id] = new AiReviewRecord
        {
            VideoId = video.Id, CandidateTitle = "未找到匹配", CandidateSource = source,
            Confidence = 0, Status = "unmatched", DecisionSource = "rules",
            Reason = reason, Model = "local-rules"
        };
        SetAutomaticAmateurTag(video, true);
        ApplyReviewBadge(video);
        SaveConfig();
        RecordReviewAction("unmatched", previous, metadataBefore);
        if (view == "scrape") RefreshReviewQueue();
        Log($"搜刮无匹配：{video.DisplayTitle} · {source}；已标记素人");
    }
}
