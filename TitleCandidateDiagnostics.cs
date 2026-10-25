using System.IO;
using System.Net.Http;

namespace YinggeVideoLibrary;

internal sealed record TitleCandidateDiagnosticResult(
    bool Passed, bool ThreeCandidatesSurviveRestart, bool StableTieOrder,
    bool OriginalAndDetailProvenancePreserved, bool ExpiredSnapshotStillReadable,
    bool NetworkFailureNotCachedAsEmpty, bool OtherSourceSurvivesOverflow);

internal static class TitleCandidateDiagnostics
{
    // No window or network. Call from FeatureDiagnostics.RunAsync(testRoot).
    internal static async Task<TitleCandidateDiagnosticResult> RunAsync(string testRoot)
    {
        var directory = Path.Combine(testRoot, "title-candidate-diagnostics");
        Directory.CreateDirectory(directory);
        var now = DateTime.UtcNow;
        var searchCards = new[]
        {
            new MovieMetadata { Title = "相近作品 C", Code = "ABC-003", Source = "JavDB",
                SourceUrl = "https://example.invalid/c", Actors = ["甲"] },
            new MovieMetadata { Title = "相近作品 A", Code = "ABC-001", Source = "JavDB",
                SourceUrl = "https://example.invalid/a", Actors = ["乙"] },
            new MovieMetadata { Title = "相近作品 B", Code = "ABC-002", Source = "JavDB",
                SourceUrl = "https://example.invalid/b", Actors = ["丙"] }
        };
        foreach (var card in searchCards) card.StampAvailableFields("搜索结果");
        var snapshot = new ScrapeCandidateCache.TitleCandidateSnapshot
        {
            VideoId = "offline-video", Query = "相近作品", SearchedUtc = now,
            ExpiresUtc = now.AddDays(3),
            Candidates = searchCards.Select(card => new ScrapeCandidateCache.RankedTitleCandidate
            {
                Score = 72, LocalScore = 68, SourceWeight = 4,
                Evidence = "标题词重合；来源权重 +4；须人工核对",
                SearchMetadata = card.Clone()
            }).ToList()
        };
        ScrapeCandidateCache.SaveTitleCandidates(directory, snapshot);
        var firstLoad = ScrapeCandidateCache.LoadTitleCandidates(directory, "offline-video");
        var threeSurvive = firstLoad?.Candidates.Count == 3;
        var stable = firstLoad?.Candidates.Select(candidate => candidate.SearchMetadata.Code)
            .SequenceEqual(["ABC-001", "ABC-002", "ABC-003"]) == true
            && firstLoad.Candidates.Select(candidate => candidate.Rank).SequenceEqual([1, 2, 3]);
        if (firstLoad is not null)
        {
            foreach (var row in firstLoad.Candidates)
            {
                var detail = row.SearchMetadata.Clone();
                detail.Summary = "经详情页核对的简介 " + detail.Code;
                detail.RefreshChangedFieldProvenance(row.SearchMetadata, "详情页");
                detail.DetailFetched = true;
                detail.DetailIdentityVerified = true;
                row.DetailMetadata = detail;
                row.DetailStatus = "verified";
                row.DetailFetchedUtc = now;
            }
            ScrapeCandidateCache.SaveTitleCandidates(directory, firstLoad);
        }
        var secondLoad = ScrapeCandidateCache.LoadTitleCandidates(directory, "offline-video");
        var provenance = secondLoad?.Candidates.Count == 3 && secondLoad.Candidates.All(row =>
            row.SearchMetadata.FieldProvenance[nameof(MovieMetadata.Title)].Stage == "搜索结果"
            && row.DetailMetadata?.FieldProvenance[nameof(MovieMetadata.Title)].Stage == "搜索结果"
            && row.DetailMetadata?.FieldProvenance[nameof(MovieMetadata.Summary)].Stage == "详情页"
            && row.DetailMetadata.DetailIdentityVerified
            && row.Evidence.Contains("来源权重 +4", StringComparison.Ordinal)) == true;
        if (secondLoad is not null)
        {
            secondLoad.ExpiresUtc = now.AddMinutes(-1);
            ScrapeCandidateCache.SaveTitleCandidates(directory, secondLoad);
        }
        var expired = ScrapeCandidateCache.LoadTitleCandidates(directory, "offline-video");
        var staleReadable = expired?.NeedsRefresh == true && expired.Candidates.Count == 3;

        var attempts = 0;
        try
        {
            await ScrapeCandidateCache.GetOrFetchAsync(directory, "JavDB 标题", "相近作品", () =>
            {
                attempts++;
                return Task.FromException<List<MovieMetadata>>(new HttpRequestException("simulated 503"));
            });
        }
        catch (HttpRequestException) { }
        var afterFailure = await ScrapeCandidateCache.GetOrFetchAsync(directory, "JavDB 标题", "相近作品", () =>
        {
            attempts++;
            return Task.FromResult(new List<MovieMetadata> { searchCards[0].Clone() });
        });
        var afterRestart = await ScrapeCandidateCache.GetOrFetchAsync(directory, "JavDB 标题", "相近作品", () =>
        {
            attempts++;
            return Task.FromResult(new List<MovieMetadata>());
        });
        var failureNotEmpty = attempts == 2 && afterFailure.Count == 1 && afterRestart.Count == 1;
        var crowded = Enumerable.Range(0, 13).Select(index => new ScrapeCandidateCache.RankedTitleCandidate
        {
            Score = 90 - index,
            SearchMetadata = new MovieMetadata { Source = "JavDB", Code = $"ABC-{index:000}", Title = "相近作品" }
        }).ToList();
        crowded.Add(new ScrapeCandidateCache.RankedTitleCandidate
        {
            Score = 10,
            SearchMetadata = new MovieMetadata { Source = "TMDB 电影", Code = "MOV-1", Title = "另一来源" }
        });
        var bounded = ScrapeCandidateCache.OrderTitleCandidates(crowded);
        var sourceFallback = bounded.Count == 12 && bounded.Take(3).All(row => row.SearchMetadata.Source == "JavDB")
            && bounded.Any(row => row.SearchMetadata.Source == "TMDB 电影")
            && bounded.Select(row => row.Rank).SequenceEqual(Enumerable.Range(1, 12));
        return new(threeSurvive && stable && provenance && staleReadable && failureNotEmpty && sourceFallback,
            threeSurvive, stable, provenance, staleReadable, failureNotEmpty, sourceFallback);
    }
}
