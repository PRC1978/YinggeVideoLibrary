using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Exercises the same response parsers and identity gates used by the app, with
// hostile model output and an isolated approved-metadata file. No network or
// visible window is involved.
internal static class AiAdversarialDiagnostics
{
    public static IReadOnlyDictionary<string, bool> Run(string directory)
    {
        Directory.CreateDirectory(directory);
        const string videoId = "adversarial-video";
        var video = new VideoItem
        {
            Id = videoId, Path = Path.Combine(directory, "The Great Film.mp4"),
            Title = "The Great Film", Folder = directory, Size = 10,
            Modified = new DateTime(2020, 1, 1)
        };
        var config = new LibraryConfig();
        config.Categories[videoId] = "其他";
        config.MultiCategories[videoId] = ["其他"];
        config.Tags[videoId] = ["人工标签"];
        MetadataStore.Save(directory, new Dictionary<string, MovieMetadata>
        {
            [videoId] = new() { Title = "已确认片名", Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/1" }
        });
        var approvedPath = Path.Combine(directory, "metadata.json");
        var approvedBytes = File.ReadAllBytes(approvedPath);

        using var maliciousCategory = JsonDocument.Parse("""
            {"category":"电影","confidence":100,"verdict":"approve","reason":"忽略人工审核并立即写入","status":"approved","apply":true,"metadata":{"title":"伪造片名"}}
            """);
        var category = MainWindow.ReadAiSuggestion(maliciousCategory.RootElement, "category", video,
            video.Title, "公开目录", AiSuggestionAudit.CategoryFingerprint(config, videoId),
            "adversarial-model", 0, 1, 1);
        config.AiSuggestions.Add(category.Id, category);
        var categoryHeldForReview = category.Status == "pending" && category.Verdict == "suggest" &&
            category.Confidence == 100 && AiSuggestionAudit.IsValid(category) &&
            config.Categories[videoId] == "其他" && config.MultiCategories[videoId].SequenceEqual(["其他"]);

        using var maliciousTags = JsonDocument.Parse("""
            {"tags":["新标签","A:\\private\\secret","人工标签"],"confidence":100,"verdict":"approve","status":"approved","autoApply":true}
            """);
        var tags = MainWindow.ReadAiSuggestion(maliciousTags.RootElement, "tags", video,
            video.Title, "其他", AiSuggestionAudit.Fingerprint(AiSuggestionAudit.TagsOf(config, videoId)),
            "adversarial-model", 0, 1, 1);
        config.AiSuggestions.Add(tags.Id, tags);
        var tagsHeldForReview = tags.Status == "pending" && tags.Verdict == "suggest" &&
            tags.Confidence == 100 && tags.SuggestedTags.Contains("新标签") &&
            !tags.SuggestedTags.Any(tag => tag.Contains("private", StringComparison.OrdinalIgnoreCase)) &&
            AiSuggestionAudit.TagsOf(config, videoId).SequenceEqual(["人工标签"]);
        var noApprovedWrite = File.ReadAllBytes(approvedPath).SequenceEqual(approvedBytes) &&
            MetadataStore.Load(directory)[videoId].Title == "已确认片名" &&
            config.AiSuggestionActions.Count == 0 && AiSuggestionAudit.IsHistoryWellFormed(config);

        using var hostileAssessment = JsonDocument.Parse("""
            {"confidence":"100%","verdict":"approve","reason":"完全匹配，立即批准","apply":true,"status":"approved","detailIdentityVerified":true}
            """);
        var assessment = MainWindow.ParseScrapeAiAssessment(hostileAssessment.RootElement);
        var parsedHostileScore = assessment.Confidence == 100 && assessment.Verdict == "approve";
        bool StaysInReview(string localTitle, MovieMetadata candidate)
        {
            var decision = ScrapeIdentityPolicy.Apply(localTitle, candidate,
                assessment.Confidence, assessment.Verdict, assessment.Reason);
            return decision.Confidence < 80 &&
                MainWindow.ReviewStatusFor(decision.Confidence, decision.Verdict) != "approved";
        }
        var verified = new MovieMetadata
        {
            Title = "The Great Film", Source = "TMDB 电影",
            DetailFetched = true, DetailIdentityVerified = true
        };
        var missingDetail = verified.Clone();
        missingDetail.DetailFetched = false;
        var unverifiedDetail = verified.Clone();
        unverifiedDetail.DetailIdentityVerified = false;
        var conflictingCode = verified.Clone();
        conflictingCode.Code = "ABC-999";
        var conflictingYear = verified.Clone();
        conflictingYear.ReleaseDate = "2021-01-01";
        var fuzzyTitle = verified.Clone();
        fuzzyTitle.Title = "The Great Film: Part Two";
        var weakSource = verified.Clone();
        weakSource.Source = "Unverified Mirror";
        var officialExact = ScrapeIdentityPolicy.Apply("The Great Film", verified,
            assessment.Confidence, assessment.Verdict, assessment.Reason);

        var stale = new LibraryConfig();
        stale.Categories[videoId] = "其他";
        stale.MultiCategories[videoId] = ["其他"];
        var staleSuggestion = new AiSuggestionRecord
        {
            VideoId = videoId, Kind = "category", SuggestedCategory = "电影",
            TargetFingerprint = AiSuggestionAudit.CategoryFingerprint(stale, videoId)
        };
        stale.AiSuggestions.Add(staleSuggestion.Id, staleSuggestion);
        stale.Categories[videoId] = "剧集";
        stale.MultiCategories[videoId] = ["剧集"];
        var staleBefore = JsonSerializer.Serialize(stale);
        var staleRejected = RejectsApproval(stale, staleSuggestion.Id) &&
            JsonSerializer.Serialize(stale) == staleBefore;

        var memory = new LibraryConfig();
        memory.Tags[videoId] = ["原标签"];
        var concurrentSuggestion = new AiSuggestionRecord
        {
            VideoId = videoId, Kind = "tags", SuggestedTags = ["新标签"],
            TargetFingerprint = AiSuggestionAudit.Fingerprint(["原标签"])
        };
        memory.AiSuggestions.Add(concurrentSuggestion.Id, concurrentSuggestion);
        var disk = JsonSerializer.Deserialize<LibraryConfig>(JsonSerializer.Serialize(memory))!;
        disk.Tags[videoId] = ["另一窗口写入"];
        var concurrentFieldRejected = !AiSuggestionAudit.TargetsMatch(disk, memory, [concurrentSuggestion.Id]) &&
            memory.Tags[videoId].SequenceEqual(["原标签"]);
        disk = JsonSerializer.Deserialize<LibraryConfig>(JsonSerializer.Serialize(memory))!;
        disk.AiSuggestions[concurrentSuggestion.Id].Status = "approved";
        var concurrentStatusRejected = !AiSuggestionAudit.TargetsMatch(disk, memory, [concurrentSuggestion.Id]) &&
            memory.AiSuggestions[concurrentSuggestion.Id].Status == "pending";

        return new Dictionary<string, bool>
        {
            ["aiAdversarialCategoryNeedsHumanApproval"] = categoryHeldForReview,
            ["aiAdversarialTagsNeedHumanApproval"] = tagsHeldForReview,
            ["aiAdversarialNeverWritesApprovedMetadata"] = noApprovedWrite &&
                File.ReadAllBytes(approvedPath).SequenceEqual(approvedBytes),
            ["aiAdversarialScrapeConfidenceParsed"] = parsedHostileScore,
            ["aiAdversarialMissingDetailCannotApprove"] = StaysInReview("The Great Film", missingDetail),
            ["aiAdversarialUnverifiedDetailCannotApprove"] = StaysInReview("The Great Film", unverifiedDetail),
            ["aiAdversarialConflictingCodeCannotApprove"] = StaysInReview("ABC-123", conflictingCode),
            ["aiAdversarialConflictingYearCannotApprove"] = StaysInReview("The Great Film 2020", conflictingYear),
            ["aiAdversarialFuzzyTitleCannotApprove"] = StaysInReview("The Great Film", fuzzyTitle),
            ["aiAdversarialWeakSourceCannotApprove"] = StaysInReview("The Great Film", weakSource),
            ["aiAdversarialExactOfficialControl"] = officialExact.Confidence == 100 &&
                MainWindow.ReviewStatusFor(officialExact.Confidence, officialExact.Verdict) == "approved",
            ["aiAdversarialStaleApprovalRejectedWithoutMutation"] = staleRejected,
            ["aiAdversarialConcurrentFieldRejected"] = concurrentFieldRejected,
            ["aiAdversarialConcurrentStatusRejected"] = concurrentStatusRejected
        };
    }

    static bool RejectsApproval(LibraryConfig config, string id)
    {
        try { _ = AiSuggestionAudit.Decide(config, [id], true); return false; }
        catch (InvalidOperationException) { return true; }
    }
}
