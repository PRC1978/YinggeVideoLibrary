namespace YinggeVideoLibrary;

internal static class VerifiedWorkClustersDiagnostics
{
    internal static Dictionary<string, bool> Verify()
    {
        var trakt = new MovieMetadata
        {
            Source = "Trakt", SourceUrl = "https://trakt.tv/movies/example-2024", Title = "Example",
            ExternalIds = new() { ["trakt"] = "900", ["imdb"] = "tt1234567", ["tmdb"] = "42" },
            ReleaseDate = "2024-01-01", Director = "甲", Actors = ["A", "B"],
            DetailFetched = true, DetailIdentityVerified = true
        };
        var tmdb = new MovieMetadata
        {
            Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/42",
            Title = "译名", ReleaseDate = "2025-01-01", Director = "乙", Actors = ["B", "A"],
            DetailFetched = true, DetailIdentityVerified = true
        };
        var omdb = new MovieMetadata
        {
            Source = "OMDb", SourceUrl = "https://www.imdb.com/title/tt1234567/", Title = "Other",
            ReleaseDate = "2024-01-01", DetailFetched = true, DetailIdentityVerified = true
        };
        var linkedTmdb = VerifiedWorkClusters.Find([trakt, tmdb]);
        var linkedImdb = VerifiedWorkClusters.Find([trakt, omdb]);
        var unverified = tmdb.Clone(); unverified.DetailIdentityVerified = false;
        var wrongId = tmdb.Clone(); wrongId.SourceUrl = "https://www.themoviedb.org/movie/43";
        var tv = tmdb.Clone(); tv.Source = "TMDB 剧集";
        tv.SourceUrl = "https://www.themoviedb.org/tv/42";
        var conflictingCrossId = trakt.Clone(); conflictingCrossId.ExternalIds["tmdb"] = "43";
        var titleOnly = omdb.Clone(); titleOnly.DetailFetched = false;
        var forgedHost = omdb.Clone(); forgedHost.SourceUrl = "https://www.imdb.com.evil.invalid/title/tt1234567/";
        var before = tmdb.Clone();
        var sameSource = tmdb.Clone(); sameSource.Title = "同站重复";
        return new Dictionary<string, bool>
        {
            ["workClusterMatchesVerifiedTmdbMovieId"] = linkedTmdb.Count == 1 &&
                linkedTmdb[0].SharedId == "TMDB movie/42" && linkedTmdb[0].Members.SequenceEqual([0, 1]),
            ["workClusterMatchesVerifiedImdbId"] = linkedImdb.Count == 1 &&
                linkedImdb[0].SharedId == "IMDb tt1234567",
            ["workClusterBridgesVerifiedImdbAndTmdbIds"] =
                VerifiedWorkClusters.Find([omdb, tmdb, trakt]) is { Count: 1 } bridged &&
                bridged[0].Members.SequenceEqual([0, 1, 2]),
            ["workClusterShowsOriginalFieldsAndConflict"] = linkedTmdb.Count == 1 &&
                linkedTmdb[0].Conflicts.Contains(nameof(MovieMetadata.ReleaseDate)) &&
                linkedTmdb[0].Conflicts.Contains(nameof(MovieMetadata.Director)) &&
                !linkedTmdb[0].Conflicts.Contains(nameof(MovieMetadata.Actors)) &&
                linkedTmdb[0].Fields.Count(item => item.Field == nameof(MovieMetadata.Title)) == 2 &&
                linkedTmdb[0].Fields.Any(item => item.SourceUrl == tmdb.SourceUrl),
            ["workClusterRejectsSearchOnlyAndUnverified"] =
                VerifiedWorkClusters.Find([trakt, unverified]).Count == 0 &&
                VerifiedWorkClusters.Find([trakt, titleOnly]).Count == 0,
            ["workClusterRejectsWrongAndContradictoryIds"] =
                VerifiedWorkClusters.Find([trakt, wrongId]).Count == 0 &&
                VerifiedWorkClusters.Find([conflictingCrossId, tmdb]).Count == 0,
            ["workClusterRejectsMovieTvCollision"] = VerifiedWorkClusters.Find([trakt, tv]).Count == 0,
            ["workClusterRejectsForgedHostAndSameSource"] =
                VerifiedWorkClusters.Find([trakt, forgedHost]).Count == 0 &&
                VerifiedWorkClusters.Find([tmdb, sameSource]).Count == 0,
            ["workClusterDoesNotChangeCandidate"] = tmdb.Title == before.Title &&
                tmdb.ReleaseDate == before.ReleaseDate && tmdb.SourceObservations.Count == 0
        };
    }
}
