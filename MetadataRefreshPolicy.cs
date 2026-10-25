namespace YinggeVideoLibrary;

// A second scrape is a proposal. A sparse response must never silently erase
// values the library already accepted, even if an earlier acceptance was automatic.
internal static class MetadataRefreshPolicy
{
    internal static bool RequiresReview(MovieMetadata? current, MovieMetadata candidate)
    {
        if (current is null) return false;
        foreach (var field in ReviewFieldApproval.Fields)
            if (!string.Equals(ReviewFieldApproval.Value(current, field.Key),
                ReviewFieldApproval.Value(candidate, field.Key), StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(current.Poster) &&
            !string.Equals(current.Poster, candidate.Poster, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static MovieMetadata AcceptedValue(MovieMetadata? current, MovieMetadata candidate)
        => current?.Clone() ?? candidate.Clone();

    internal static bool VerifyForDiagnostics()
    {
        var old = new MovieMetadata { Code = "ABC-123", Title = "旧作品", Director = "甲", Summary = "旧简介",
            DetailFetched = true, DetailIdentityVerified = true, Poster = "old-cover.jpg" };
        var sparse = new MovieMetadata { Code = "ABC-123", Title = "旧作品", Director = "甲",
            DetailFetched = true, DetailIdentityVerified = true, Poster = "new-cover.jpg" };
        if (!RequiresReview(old, sparse) || AcceptedValue(old, sparse).Summary != "旧简介") return false;
        var exact = old.Clone();
        exact.ScrapedAt = old.ScrapedAt.AddDays(1);
        if (RequiresReview(old, exact)) return false;
        exact.Director = "乙";
        if (!RequiresReview(old, exact)) return false;
        return !RequiresReview(null, sparse);
    }
}
