namespace YinggeVideoLibrary;

internal static class AvScanEligibility
{
    // An explicit user category or tag is required. Script, folder names and
    // Japanese-looking titles are not evidence of the video's origin.
    internal static bool CanOffer(VideoItem video, MovieMetadata? accepted)
    {
        if (video.Availability != VideoAvailability.Present ||
            !string.IsNullOrWhiteSpace(video.Code) ||
            JavDbScraper.GuessCodes(video.Title).Count != 0 ||
            !string.IsNullOrWhiteSpace(accepted?.Code)) return false;
        return video.Tags.Any(IsJapanLabel) || video.CategoryList.Any(IsJapanPath) ||
            IsJapanPath(video.Category) ||
            (accepted?.DetailFetched == true && accepted.DetailIdentityVerified &&
                IsJapanLabel(accepted.Country ?? ""));
    }

    static bool IsJapanPath(string value) => value.Split(['/', '\\', '>', '／'],
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(IsJapanLabel);

    static bool IsJapanLabel(string value) => value.Trim() is "日本" or "Japan" or "JAPAN";

    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        var video = new VideoItem { Id = "avscan-test", Path = "X:\\test.mp4", Title = "unknown",
            Folder = "X:\\", Size = 1, Modified = DateTime.UtcNow };
        video.Tags.Add("日本");
        var results = new Dictionary<string, bool>();
        results["avScanRequiresExplicitJapanAndNoCode"] = CanOffer(video, null);
        video.Tags.Clear();
        results["avScanRejectsLanguageGuess"] = !CanOffer(video, null);
        video.CategoryList.Add("影片/日本/未整理");
        results["avScanAcceptsExplicitCategoryPath"] = CanOffer(video, null);
        video.Code = "ABC-123";
        results["avScanRejectsExistingCode"] = !CanOffer(video, null);
        video.Code = null;
        video.Availability = VideoAvailability.RootUnavailable;
        results["avScanRejectsUnavailableFile"] = !CanOffer(video, null);
        video.Availability = VideoAvailability.Present;
        results["avScanRejectsAcceptedCode"] = !CanOffer(video,
            new MovieMetadata { Code = "ABC-123" });
        return results;
    }
}
