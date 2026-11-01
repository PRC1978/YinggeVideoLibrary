using System.IO;

namespace YinggeVideoLibrary;

// A plugin is an untrusted source of *claims*. This bridge turns a claim into a
// normal candidate and then a normal pending review; it never writes metadata
// and never marks a candidate as identity-verified on the plugin's word.
internal static class PluginScrapeBridge
{
    // Same value as MainWindow.ReviewManualThreshold. Plugin candidates are
    // additionally forced to pending regardless of this number.
    internal const int ManualThreshold = 80;

    // A stored candidate survives the plugin being disabled or removed, so the
    // review row can still rebuild its descriptor from the record.
    internal static PluginDescriptor DescriptorFor(string pluginId, string name, string version) =>
        new()
        {
            FolderPath = "", FolderName = pluginId,
            Manifest = new PluginManifest { Id = pluginId, Name = name, Version = version },
            Status = PluginStatus.Ready
        };

    internal static string SourceLabel(PluginDescriptor plugin) =>
        "插件：" + (string.IsNullOrWhiteSpace(plugin.DisplayName) ? plugin.Id : plugin.DisplayName);

    internal static MovieMetadata ToMetadata(PluginCandidate candidate, PluginDescriptor plugin)
    {
        var metadata = new MovieMetadata
        {
            Code = candidate.Code ?? "",
            Title = candidate.Title,
            OriginalTitle = candidate.OriginalTitle,
            ReleaseDate = candidate.Year is { } year ? year.ToString("0000") : null,
            Source = SourceLabel(plugin),
            SourceUrl = candidate.SourceUrl,
            PosterUrl = candidate.PosterUrl,
            Summary = candidate.Summary,
            ScrapedAt = DateTime.Now,
            // The plugin says it fetched a detail page. The host cannot verify
            // that, so identity stays unverified and the review gate decides.
            DetailFetched = true,
            DetailIdentityVerified = false,
            DetailNote = $"插件来源：{plugin.Id} v{plugin.Manifest?.Version}；" +
                (string.IsNullOrWhiteSpace(candidate.Evidence) ? "插件未提供身份依据" : "依据：" + candidate.Evidence)
        };
        return metadata;
    }

    internal static AiReviewRecord BuildPendingReview(string videoId, string localTitle,
        MovieMetadata candidate, PluginDescriptor plugin, int confidenceHint)
    {
        var reason = $"插件 {plugin.Id} 返回的候选；" +
            (string.IsNullOrWhiteSpace(candidate.DetailNote) ? "" : candidate.DetailNote + "；") +
            "插件候选一律进入人工审核";
        var identity = ScrapeIdentityPolicy.Apply(localTitle, candidate,
            Math.Min(Math.Clamp(confidenceHint, 0, 100), ManualThreshold - 1), "review", reason);
        return new AiReviewRecord
        {
            VideoId = videoId,
            CandidateCode = candidate.Code,
            CandidateTitle = candidate.Title,
            CandidateSource = candidate.Source,
            CandidateSourceUrl = candidate.SourceUrl,
            CandidatePoster = candidate.Poster,
            CandidatePosterUrl = candidate.PosterUrl,
            CandidateDetails = candidate.Clone(),
            Confidence = Math.Min(identity.Confidence, ManualThreshold - 1),
            // Never "approved": a plugin cannot write video metadata.
            Status = "pending",
            DecisionSource = "plugin",
            Reason = identity.Reason,
            Model = $"{plugin.Id} v{plugin.Manifest?.Version}",
            CreatedAt = DateTime.Now
        };
    }

    // Redacted one-line summary for the management page and the run log.
    internal static string DescribeOutcome(PluginSearchOutcome outcome)
    {
        if (!outcome.Ok) return $"失败：{outcome.ErrorCode} · {PluginLog.Redact(outcome.ErrorMessage)}";
        return $"返回 {outcome.Candidates.Count} 条候选 · {outcome.ElapsedMs} ms";
    }

    internal static string DescribeFolder(PluginDescriptor plugin)
    {
        try { return Path.GetFileName(plugin.FolderPath.TrimEnd(Path.DirectorySeparatorChar)); }
        catch { return plugin.FolderPath; }
    }
}
