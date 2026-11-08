using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Offline evaluation of the shipping identity gate against human-labelled
// pairs. It never calls a model, scraper, or the user's library.
internal static class AiScrapeEvaluation
{
    internal const string Schema = "yingge.ai-scrape-evaluation.v1";
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal sealed class Fixture
    {
        public string Schema { get; set; } = "";
        public string Provenance { get; set; } = "";
        public double InputUsdPerMillion { get; set; }
        public double OutputUsdPerMillion { get; set; }
        public List<Case> Cases { get; set; } = [];
    }

    internal sealed class Case
    {
        public string Id { get; set; } = "";
        public string LocalTitle { get; set; } = "";
        public MovieMetadata Candidate { get; set; } = new();
        public bool? GoldSameWork { get; set; }
        public int ProposedConfidence { get; set; }
        public string ProposedVerdict { get; set; } = "review";
        public bool ModelCalled { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }

    internal sealed record CaseResult(string Id, string SourceGroup, bool GoldSameWork, string Decision,
        bool AutoApproved, bool ModelCallEligible, double EstimatedUsd);

    internal sealed record RateInterval(double Lower, double Upper);
    internal sealed record SourceMetrics(string SourceGroup, int Cases, int GoldNonMatches,
        int AutoApproved, int FalseApprovals, double? AutoApprovalPrecision,
        RateInterval? Precision95, double? FalseApprovalRate, RateInterval? FalseApprovalRate95);

    internal sealed record Report(string Schema, string Evidence, int CaseCount,
        int GoldMatches, int GoldNonMatches, int AutoApproved, int FalseApprovals,
        int CorrectApprovals, int PendingOrRejected, double? AutoApprovalPrecision,
        double? FalseApprovalRate, double AutoApprovalCoverage,
        int ModelCalls, double EstimatedUsd, string CalibrationStatus,
        int? SuggestedThreshold, List<CaseResult> Cases,
        RateInterval? Precision95, RateInterval? FalseApprovalRate95,
        List<SourceMetrics> Sources, int IdentityConflictCases, int IdentityConflictAutoApproved)
    {
        // These measure automatic approval only. A match held for manual
        // review is not evidence that the search failed to retrieve it.
        public int MatchesNotAutoApproved => GoldMatches - CorrectApprovals;
        public double? AutoApprovalRecall => GoldMatches == 0 ? null : (double)CorrectApprovals / GoldMatches;
        public RateInterval? AutoApprovalRecall95 => Wilson(CorrectApprovals, GoldMatches);
        public double? MatchNotAutoApprovedRate => GoldMatches == 0 ? null : (double)MatchesNotAutoApproved / GoldMatches;
        public string RetrievalRecallStatus => "未测量：当前输入为已取得的候选对，不包含完整检索结果；不得将待审率称为搜刮漏配率";
    }

    internal static Report Evaluate(Fixture fixture)
    {
        Validate(fixture);
        var results = new List<CaseResult>(fixture.Cases.Count);
        foreach (var row in fixture.Cases)
        {
            var video = new VideoItem
            {
                Id = row.Id, Path = row.Id + ".mp4", Title = row.LocalTitle,
                Folder = "fixture", Size = 0, Modified = DateTime.UnixEpoch
            };
            var eligible = AiScrapePolicy.ShouldAutoAssess(video, row.Candidate, false);
            var decision = ScrapeIdentityPolicy.Apply(row.LocalTitle, row.Candidate,
                row.ProposedConfidence, row.ProposedVerdict, "离线评测");
            var status = MainWindow.ReviewStatusFor(decision.Confidence, decision.Verdict);
            var cost = row.ModelCalled
                ? (row.InputTokens * fixture.InputUsdPerMillion +
                   row.OutputTokens * fixture.OutputUsdPerMillion) / 1_000_000d
                : 0d;
            results.Add(new(row.Id, SourceGroup(row.Candidate.Source), row.GoldSameWork!.Value, status,
                status == "approved", eligible, cost));
        }

        var matches = results.Count(x => x.GoldSameWork);
        var nonMatches = results.Count - matches;
        var approved = results.Count(x => x.AutoApproved);
        var falseApproved = results.Count(x => x.AutoApproved && !x.GoldSameWork);
        var suggestion = SuggestThreshold(fixture, matches, nonMatches);
        return new(Schema, "离线标注样本；未调用模型或真实来源，不能视作真实世界准确率或账单",
            results.Count, matches, nonMatches, approved, falseApproved,
            approved - falseApproved, results.Count - approved,
            approved == 0 ? null : (double)(approved - falseApproved) / approved,
            nonMatches == 0 ? null : (double)falseApproved / nonMatches,
            (double)approved / results.Count,
            fixture.Cases.Count(x => x.ModelCalled), results.Sum(x => x.EstimatedUsd),
            fixture.Provenance == "synthetic" ? "synthetic-cannot-calibrate" :
                suggestion is null ? "insufficient-or-no-safe-threshold" : "advisory-only-not-trained",
            suggestion, results,
            Wilson(approved - falseApproved, approved), Wilson(falseApproved, nonMatches),
            results.GroupBy(x => x.SourceGroup, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(group => SummarizeSource(group.Key, group.ToList())).ToList(),
            fixture.Cases.Count(x => x.Candidate.SourceConflictFields?.Count > 0),
            fixture.Cases.Zip(results).Count(pair => pair.First.Candidate.SourceConflictFields?.Count > 0 && pair.Second.AutoApproved));
    }

    static SourceMetrics SummarizeSource(string source, List<CaseResult> cases)
    {
        var approved = cases.Count(x => x.AutoApproved);
        var falseApproved = cases.Count(x => x.AutoApproved && !x.GoldSameWork);
        var negatives = cases.Count(x => !x.GoldSameWork);
        return new(source, cases.Count, negatives, approved, falseApproved,
            approved == 0 ? null : (double)(approved - falseApproved) / approved,
            Wilson(approved - falseApproved, approved),
            negatives == 0 ? null : (double)falseApproved / negatives,
            Wilson(falseApproved, negatives));
    }

    // Fixed public catalog names only. Plugin-supplied names may contain a
    // private path, title, or other user data and must never enter the report.
    static string SourceGroup(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "javdb" => "JavDB", "javdatabase" => "JAVDatabase", "javinfo" => "Javinfo",
        "bangumi" => "Bangumi", "anilist" => "AniList", "mikan" => "Mikan",
        "fankuhub" => "FankuHub", "tvmaze" => "TVmaze", "kitsu" => "Kitsu",
        "trakt" => "Trakt", "omdb" => "OMDb", "pornhub" => "Pornhub",
        "tmdb" or "tmdb 电影" or "tmdb 剧集" => "TMDB",
        _ => "Other / plugin"
    };

    // Wilson score interval, 95% two-sided. Undefined denominators stay null.
    internal static RateInterval? Wilson(int successes, int total)
    {
        if (total <= 0) return null;
        const double z = 1.959963984540054;
        var p = (double)successes / total;
        var denominator = 1 + z * z / total;
        var center = (p + z * z / (2 * total)) / denominator;
        var spread = z / denominator * Math.Sqrt(p * (1 - p) / total + z * z / (4d * total * total));
        return new(Math.Max(0, center - spread), Math.Min(1, center + spread));
    }

    static int? SuggestThreshold(Fixture fixture, int matches, int nonMatches)
    {
        if (fixture.Provenance != "human-reviewed" || fixture.Cases.Count < 100 ||
            matches < 30 || nonMatches < 30) return null;
        for (var threshold = 80; threshold <= 99; threshold++)
        {
            var approved = 0;
            var falseApproved = 0;
            foreach (var row in fixture.Cases)
            {
                var decision = ScrapeIdentityPolicy.Apply(row.LocalTitle, row.Candidate,
                    row.ProposedConfidence, row.ProposedVerdict, "离线校准");
                if (!decision.Verdict.Equals("approve", StringComparison.OrdinalIgnoreCase) ||
                    decision.Confidence < threshold) continue;
                approved++;
                if (row.GoldSameWork == false) falseApproved++;
            }
            var approvalError95 = Wilson(falseApproved, approved);
            var negativeError95 = Wilson(falseApproved, nonMatches);
            if (approved >= 10 && approvalError95?.Upper <= 0.01 &&
                negativeError95?.Upper <= 0.01) return threshold;
        }
        return null;
    }

    internal static int RunToFile(string fixturePath, string outputPath)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("评测样本为空");
        var report = Evaluate(fixture);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (directory is not null) Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    static void Validate(Fixture fixture)
    {
        if (fixture.Schema != Schema) throw new InvalidDataException("评测样本版本不匹配");
        if (fixture.Provenance is not ("synthetic" or "human-reviewed"))
            throw new InvalidDataException("评测样本须注明 synthetic 或 human-reviewed 来源");
        if (fixture.Cases is null || fixture.Cases.Count is < 1 or > 10_000)
            throw new InvalidDataException("评测样本数应为 1 到 10000");
        if (!double.IsFinite(fixture.InputUsdPerMillion) || fixture.InputUsdPerMillion is < 0 or > 1_000_000 ||
            !double.IsFinite(fixture.OutputUsdPerMillion) || fixture.OutputUsdPerMillion is < 0 or > 1_000_000)
            throw new InvalidDataException("费率无效");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in fixture.Cases)
        {
            if (string.IsNullOrWhiteSpace(row.Id) || row.Id.Length > 64 ||
                !Regex.IsMatch(row.Id, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant) || !ids.Add(row.Id) ||
                string.IsNullOrWhiteSpace(row.LocalTitle) || row.Candidate is null ||
                row.GoldSameWork is null ||
                row.ProposedConfidence is < 0 or > 100 ||
                row.InputTokens is < 0 or > 1_000_000_000 ||
                row.OutputTokens is < 0 or > 1_000_000_000 ||
                (!row.ModelCalled && (row.InputTokens != 0 || row.OutputTokens != 0)))
                throw new InvalidDataException("评测样本字段无效或 ID 重复");
        }
    }
}
