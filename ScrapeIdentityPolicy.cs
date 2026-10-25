using System.Text;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

internal readonly record struct ScrapeIdentityDecision(int Confidence, string Verdict, string Reason);

// AI and source trust may rank candidates, but neither can establish identity.
// The same gate is applied on the first scrape and on later AI re-evaluation.
internal static class ScrapeIdentityPolicy
{
    const int ManualThreshold = 80;
    static readonly Regex ExplicitYear = new(@"(?<![A-Za-z0-9-])(?<year>(?:19|20)\d{2})(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex ReleaseYear = new(@"^(?<year>(?:19|20)\d{2})(?:$|[-/.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static ScrapeIdentityDecision Apply(string localTitle, MovieMetadata candidate,
        int confidence, string verdict, string reason)
    {
        if (!candidate.DetailFetched)
            return Pending(confidence, verdict, reason, "尚未取得详情，搜索摘要不能自动通过");
        if (!candidate.DetailIdentityVerified)
            return Pending(confidence, verdict, reason, "详情未独立核对作品编号或来源 ID");
        if (ConflictingYear(localTitle, candidate) is { } yearConflict)
            return Pending(confidence, verdict, reason, yearConflict);

        var localCode = JavDbScraper.GuessCode(localTitle);
        if (!string.IsNullOrWhiteSpace(candidate.Code))
        {
            if (string.IsNullOrWhiteSpace(localCode))
                return Pending(confidence, verdict, reason, "文件名没有可核对的番号，标题候选须人工确认");
            if (!ScrapeCodeIdentity.Same(localCode, candidate.Code))
                return Pending(confidence, verdict, reason, $"番号冲突：文件名 {localCode}，候选 {candidate.Code}");
            return new(Math.Clamp(confidence, 0, 100), verdict, reason);
        }

        // Official catalogs have stable IDs in their detail responses. Their
        // exact title (or original title) can identify an uncoded local item;
        // a partial/fuzzy title, even at AI confidence 100, still needs review.
        if (IsOfficialCatalog(candidate.Source) &&
            (ExactTitle(localTitle, candidate.Title) || ExactTitle(localTitle, candidate.OriginalTitle)))
            return new(Math.Clamp(confidence, 0, 100), verdict, reason);

        return Pending(confidence, verdict, reason, "仅有近似标题，须人工核对作品身份");
    }

    static ScrapeIdentityDecision Pending(int confidence, string verdict, string reason, string evidence)
    {
        var safeVerdict = IsReject(verdict) ? verdict : "review";
        var safeConfidence = Math.Min(Math.Clamp(confidence, 0, 100), ManualThreshold - 1);
        return new(safeConfidence, safeVerdict,
            string.IsNullOrWhiteSpace(reason) ? evidence : reason.Trim() + "；" + evidence);
    }

    static bool IsReject(string? verdict)
    {
        var normalized = verdict?.Trim().ToLowerInvariant() ?? "";
        return normalized is "reject" or "rejected" or "否决" or "拒绝" or "不匹配" ||
            normalized.Contains("否决") || normalized.Contains("拒绝");
    }

    static string? ConflictingYear(string localTitle, MovieMetadata candidate)
    {
        var localYears = Years(localTitle);
        if (localYears.Count == 0) return null;
        if (localYears.Count > 1) return "文件名包含多个不同年份，须人工核对";

        var candidateYears = Years(candidate.Title);
        candidateYears.UnionWith(Years(candidate.OriginalTitle ?? ""));
        var release = ReleaseYear.Match(candidate.ReleaseDate?.Trim() ?? "");
        if (release.Success && int.TryParse(release.Groups["year"].Value, out var releaseYear))
            candidateYears.Add(releaseYear);
        if (candidateYears.Count == 0) return "文件名含年份但候选未提供可核对年份，须人工确认";
        var localYear = localYears.First();
        return candidateYears.Count == 1 && candidateYears.Contains(localYear)
            ? null : $"年份冲突：文件名 {localYear}，候选 {string.Join("/", candidateYears.Order())}";
    }

    static HashSet<int> Years(string text)
    {
        var years = new HashSet<int>();
        foreach (Match match in ExplicitYear.Matches(text))
            if (int.TryParse(match.Groups["year"].Value, out var year)) years.Add(year);
        return years;
    }

    static bool ExactTitle(string local, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var left = TitleKey(local);
        var right = TitleKey(candidate);
        return left.Length >= 4 && left.Equals(right, StringComparison.Ordinal);
    }

    static string TitleKey(string value)
    {
        var cleaned = AdditionalScrapers.CleanTitle(value);
        var result = new StringBuilder(cleaned.Length);
        foreach (var c in cleaned)
            if (char.IsLetterOrDigit(c)) result.Append(char.ToUpperInvariant(c));
        return result.ToString();
    }

    static bool IsOfficialCatalog(string? source) =>
        source?.StartsWith("TMDB", StringComparison.OrdinalIgnoreCase) == true ||
        source?.Equals("TVmaze", StringComparison.OrdinalIgnoreCase) == true ||
        source?.Equals("Bangumi", StringComparison.OrdinalIgnoreCase) == true ||
        source?.Equals("AniList", StringComparison.OrdinalIgnoreCase) == true;
}
