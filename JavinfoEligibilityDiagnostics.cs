using System.IO;

namespace YinggeVideoLibrary;

internal static class JavinfoEligibilityDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify(string directory)
    {
        var results = new Dictionary<string, bool>();
        static bool Rejected(string? code, string? title, bool confirmedHanOnly = false)
        {
            try { JavinfoSearchEvidence.Create(code, title, confirmedHanOnly); return false; }
            catch (InvalidOperationException) { return true; }
        }

        results["javinfoNoNumberBlocksPaidSource"] = Rejected(null, "作品の題名");
        results["javinfoVagueOrGenericNumberBlocksPaidSource"] =
            Rejected("ABC", "作品の題名") && Rejected("VID_2024", "作品の題名") &&
            Rejected("ABC-123 其他字", "作品の題名");
        results["javinfoNonJapaneseTitleBlocksPaidSource"] =
            Rejected("ABC-123", "Chinese translated title") &&
            Rejected("ABC-123", "中文作品") && Rejected("ABC-123", "作品の題名"[..1]);
        var allowed = JavinfoSearchEvidence.Create("ABC-123", "作品の題名");
        var confirmedKanji = JavinfoSearchEvidence.Create("ABC-123", "作品原名", confirmedHanOnly: true);
        results["javinfoExactNumberAndJapaneseTitleAllowed"] =
            allowed.Code == "ABC-123" && allowed.JapaneseTitle == "作品の題名" &&
            allowed.MatchesTitleJa("作品 の 題名") && allowed.MatchesTitleJa("ABC-123 作品の題名") &&
            !allowed.MatchesTitleJa("別の作品") &&
            confirmedKanji.JapaneseTitle == "作品原名";
        var exactCandidates = JavinfoScraper.ExactNumberCandidates([
            new MovieMetadata { Source = "Javinfo", Code = "ABC-124", Title = "別の作品" },
            new MovieMetadata { Source = "Javinfo", Code = "ABC-123", Title = "作品の題名" }
        ], allowed);
        results["javinfoPaidCandidatesRequireExactNumber"] = exactCandidates.Count == 1 &&
            exactCandidates[0].Code == "ABC-123";
        results["javinfoConflictingLocalNumberBlocked"] = RejectConflictingLocalCode();

        var usage = Path.Combine(directory, "javinfo-usage.json");
        var candidate = new MovieMetadata { Source = "Javinfo · fanza", Code = "ABC-124", Title = "Example" };
        var searchRejected = false;
        var detailRejected = false;
        try { JavinfoScraper.SearchAsync(null!, "jvi_diagnostic", directory).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { searchRejected = true; }
        try { JavinfoScraper.FetchCandidateDetailsAsync(candidate, "jvi_diagnostic", directory, allowed).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { detailRejected = true; }
        var absentContextRejected = false;
        try { AdditionalScrapers.FetchFullDetailsAsync(candidate, "", "jvi_diagnostic", directory).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { absentContextRejected = true; }
        results["javinfoRejectedRequestsDoNotReservePaidQuota"] = searchRejected && detailRejected &&
            absentContextRejected && !File.Exists(usage);
        return results;

        static bool RejectConflictingLocalCode()
        {
            try { JavinfoSearchEvidence.Create("ABC-124", "作品の題名", localCode: "ABC-123"); return false; }
            catch (InvalidOperationException) { return true; }
        }
    }
}
