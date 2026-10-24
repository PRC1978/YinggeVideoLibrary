using System.Globalization;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

public sealed class PlatformRating
{
    public string Platform { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public double Value { get; set; }
    public double Maximum { get; set; } = 10;
    public int? VoteCount { get; set; }
    public string MatchEvidence { get; set; } = "";
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;

    public PlatformRating Clone() => new()
    {
        Platform = Platform, SourceUrl = SourceUrl, Value = Value, Maximum = Maximum,
        VoteCount = VoteCount, MatchEvidence = MatchEvidence, CollectedAt = CollectedAt
    };
}

public sealed record RatingSummary(double ScoreOutOfTen, int SourceCount, int KnownVotes);

public static class RatingAggregation
{
    static readonly Regex ExplicitScale = new(@"(?<score>\d{1,3}(?:\.\d{1,2})?)\s*/\s*(?<scale>5|10|100)(?!\d)", RegexOptions.Compiled);
    static readonly Regex Percent = new(@"(?<score>\d{1,3}(?:\.\d{1,2})?)\s*%", RegexOptions.Compiled);
    static readonly Regex Votes = new(@"(?:\(|（)?\s*(?<count>[\d,，]+)\s*(?:票|votes?|ratings?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PlatformRating? FromMetadata(MovieMetadata metadata, string matchEvidence = "来源详情")
    {
        var raw = metadata.Rating;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var explicitMatch = ExplicitScale.Match(raw);
        var percentMatch = explicitMatch.Success ? Match.Empty : Percent.Match(raw);
        var scoreText = explicitMatch.Success ? explicitMatch.Groups["score"].Value : percentMatch.Groups["score"].Value;
        var scaleText = explicitMatch.Success ? explicitMatch.Groups["scale"].Value : "100";
        if (scoreText.Length == 0 || !double.TryParse(scoreText, NumberStyles.Number, CultureInfo.InvariantCulture, out var score) ||
            !double.TryParse(scaleText, NumberStyles.Number, CultureInfo.InvariantCulture, out var scale) ||
            score < 0 || score > scale) return null;
        var voteMatch = Votes.Match(raw);
        int? votes = voteMatch.Success && int.TryParse(voteMatch.Groups["count"].Value.Replace(",", "").Replace("，", ""), out var count) && count > 0
            ? count : null;
        return new PlatformRating
        {
            Platform = CanonicalPlatform(metadata.Source), SourceUrl = metadata.SourceUrl,
            Value = score, Maximum = scale, VoteCount = votes,
            MatchEvidence = matchEvidence, CollectedAt = DateTime.UtcNow
        };
    }

    public static bool IsValid(PlatformRating? rating) => rating is not null &&
        !string.IsNullOrWhiteSpace(rating.Platform) && double.IsFinite(rating.Value) && double.IsFinite(rating.Maximum) &&
        rating.Maximum > 0 && rating.Value >= 0 && rating.Value <= rating.Maximum && rating.VoteCount is null or >= 0;

    public static string CanonicalPlatform(string source)
    {
        source = source.Trim();
        if (source.StartsWith("Javinfo · ", StringComparison.OrdinalIgnoreCase))
            source = source["Javinfo · ".Length..].Trim();
        if (source.StartsWith("TMDB", StringComparison.OrdinalIgnoreCase)) return "TMDB";
        if (source.StartsWith("JAVDatabase", StringComparison.OrdinalIgnoreCase)) return "JAVDatabase";
        if (source.StartsWith("JavDB", StringComparison.OrdinalIgnoreCase)) return "JavDB";
        if (source.Equals("AniList", StringComparison.OrdinalIgnoreCase)) return "AniList";
        if (source.Equals("Bangumi", StringComparison.OrdinalIgnoreCase)) return "Bangumi";
        if (source.Equals("TVmaze", StringComparison.OrdinalIgnoreCase)) return "TVmaze";
        if (source.Equals("Pornhub", StringComparison.OrdinalIgnoreCase)) return "Pornhub";
        return source;
    }

    public static List<PlatformRating> DistinctValidRatings(IEnumerable<PlatformRating>? ratings)
        => (ratings ?? []).Where(IsValid).GroupBy(rating => CanonicalPlatform(rating.Platform), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(rating => rating.CollectedAt).First().Clone())
            .OrderBy(rating => rating.Platform, StringComparer.OrdinalIgnoreCase).ToList();

    // Preserve the platform values; a bounded vote weight keeps one large site
    // from erasing the others. A single source is displayed alone, not called
    // a multi-platform aggregate.
    public static RatingSummary? Summarize(IEnumerable<PlatformRating>? ratings)
    {
        var unique = DistinctValidRatings(ratings);
        if (unique.Count < 2) return null;
        double weighted = 0, totalWeight = 0;
        var knownVotes = 0;
        foreach (var rating in unique)
        {
            var weight = rating.VoteCount is int votes and > 0
                ? Math.Clamp(Math.Log10(votes + 1d) / 2d, 1d, 3d) : 1d;
            weighted += rating.Value / rating.Maximum * 10d * weight;
            totalWeight += weight;
            knownVotes += rating.VoteCount ?? 0;
        }
        return new RatingSummary(Math.Round(weighted / totalWeight, 1, MidpointRounding.AwayFromZero), unique.Count, knownVotes);
    }

    public static void AddOrUpdate(MovieMetadata metadata, PlatformRating rating)
    {
        if (!IsValid(rating)) return;
        metadata.PlatformRatings ??= [];
        var canonical = CanonicalPlatform(rating.Platform);
        metadata.PlatformRatings.RemoveAll(item => CanonicalPlatform(item.Platform).Equals(canonical, StringComparison.OrdinalIgnoreCase));
        metadata.PlatformRatings.Add(rating.Clone());
    }
}
