using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class TmdbUsagePolicyDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        static VideoItem Video(string title, string folder = @"D:\Library\普通电影") => new()
        {
            Id = "tmdb-scope-test", Title = title, Path = Path.Combine(folder, title + ".mp4"),
            Folder = folder, Size = 1024, Modified = DateTime.UtcNow
        };
        var findings = new Dictionary<string, bool>();
        var unknown = Video("Ordinary-looking name", @"D:\Library\Unsorted");
        findings["tmdbUnknownVideoNeverAutomaticallyQueried"] =
            !TmdbUsagePolicy.Evaluate(unknown, null, unknown.Title).Allowed;

        var ordinary = Video("Example Film");
        ordinary.Category = "电影 / 剧情";
        findings["tmdbExplicitOrdinaryCategoryAllowed"] =
            TmdbUsagePolicy.Evaluate(ordinary, null, ordinary.Title).Allowed;
        findings["tmdbAdultFilenameBlockedDespiteOrdinaryCategory"] =
            !TmdbUsagePolicy.Evaluate(Video("FC2-PPV-4739552"), null, "Example Film").Allowed &&
            !TmdbUsagePolicy.Evaluate(Video("ABP-123"), null, "Example Film").Allowed &&
            !TmdbUsagePolicy.Evaluate(Video("HIPANGEL142"), null, "Example Film").Allowed;
        ordinary.Code = "SSIS-111";
        findings["tmdbAdultCodeFieldBlocked"] =
            !TmdbUsagePolicy.Evaluate(ordinary, null, ordinary.Title).Allowed;
        ordinary.Code = null;
        var adultFolder = Video("Example Film", @"D:\Library\成人\电影");
        adultFolder.Category = "电影";
        findings["tmdbAdultDirectoryBlockedDespiteOrdinaryCategory"] =
            !TmdbUsagePolicy.Evaluate(adultFolder, null, "Example Film").Allowed;
        ordinary.Tags.Add("素人");
        findings["tmdbAdultTagBlocked"] = !TmdbUsagePolicy.Evaluate(ordinary, null, ordinary.Title).Allowed;
        ordinary.Tags.Clear();
        findings["tmdbAdultQueryBlocked"] = !TmdbUsagePolicy.Evaluate(ordinary, null, "porn title").Allowed;

        var adultSource = new MovieMetadata
        {
            Source = "JavDB", Title = "Example Film", DetailFetched = true, DetailIdentityVerified = true
        };
        findings["tmdbExistingAdultSourceVetoesOrdinaryCategory"] =
            !TmdbUsagePolicy.Evaluate(ordinary, adultSource, ordinary.Title).Allowed;
        var verifiedSeries = new MovieMetadata
        {
            Source = "TVmaze", SourceUrl = "https://www.tvmaze.com/shows/7",
            Title = "Example Series", DetailFetched = true, DetailIdentityVerified = true
        };
        findings["tmdbVerifiedOrdinarySourceAllowsUnclassifiedVideo"] =
            TmdbUsagePolicy.Evaluate(unknown, verifiedSeries, "Example Series").Allowed;
        verifiedSeries.DetailIdentityVerified = false;
        findings["tmdbUnverifiedOrdinarySourceDoesNotAllowUnknownVideo"] =
            !TmdbUsagePolicy.Evaluate(unknown, verifiedSeries, "Example Series").Allowed;

        using var search = JsonDocument.Parse("""
            {"results":[{"id":1,"title":"Safe Film","adult":false},
                        {"id":2,"title":"Other Film","adult":true}]}
            """);
        findings["tmdbAdultSearchCandidateExcluded"] =
            AdditionalScrapers.ParseTmdbSearchResults(search.RootElement, "movie") is { Count: 1 } cards &&
            cards[0].SourceUrl == "https://www.themoviedb.org/movie/1";
        using var adultDetails = JsonDocument.Parse("""{"id":1,"title":"Safe Film","adult":true}""");
        try
        {
            _ = AdditionalScrapers.ParseTmdbDetails(new MovieMetadata
            {
                Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/1",
                Title = "Safe Film"
            }, adultDetails.RootElement, "movie");
            findings["tmdbAdultDetailRejectedBeforeSaving"] = false;
        }
        catch (InvalidDataException) { findings["tmdbAdultDetailRejectedBeforeSaving"] = true; }
        return findings;
    }
}
