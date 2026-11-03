namespace YinggeVideoLibrary;

// An actor name on a saved work is only a search hint. The actor directory
// must independently list the exact work URL and number before we attach its
// source identity to a local profile.
internal static class RoleNamedActorDiscovery
{
    internal static async Task<JavDbCatalogLink?> FindAsync(VideoItem video, MovieMetadata work,
        string actorName, CancellationToken token = default,
        Func<string, CancellationToken, Task<List<JavDbCatalogLink>>>? search = null,
        Func<string, int, CancellationToken, Task<JavDbScraper.CatalogPageResult>>? fetchPage = null)
    {
        if (work.Source != "JavDB" || !work.DetailFetched ||
            !JavDbScraper.IsCatalogCandidate(work) ||
            !ScrapeCodeIdentity.Same(JavDbScraper.GuessCode(video.Title), work.Code) ||
            !JavDbScraper.TryActorSearchName(actorName, out var normalized) ||
            !(work.Actors ?? []).Any(name => JavDbScraper.TryActorSearchName(name, out var saved)
                && saved == normalized)) return null;

        search ??= JavDbScraper.FindActorLinksByNameAsync;
        fetchPage ??= JavDbScraper.FetchCatalogPageWithNavigationAsync;
        var confirmed = new List<JavDbCatalogLink>();
        foreach (var link in (await search(actorName, token)).Take(3))
        {
            token.ThrowIfCancellationRequested();
            if (link.Kind != "演员" || !JavDbScraper.TryCatalogUrl(link.Url, out var actorUrl, out var kind) ||
                kind != "演员" || !JavDbScraper.TryActorSearchName(link.Name, out var foundName) ||
                foundName != normalized) continue;
            for (var page = 1; page <= 3; page++)
            {
                var result = await fetchPage(actorUrl, page, token);
                if (result.Movies.Any(candidate => JavDbScraper.IsCatalogCandidate(candidate) &&
                    ScrapeCodeIdentity.Same(candidate.Code, work.Code) &&
                    string.Equals(candidate.SourceUrl, work.SourceUrl, StringComparison.OrdinalIgnoreCase)))
                {
                    confirmed.Add(new JavDbCatalogLink { Kind = "演员", Name = link.Name, Url = actorUrl });
                    break;
                }
                if (!result.HasNextPage) break;
            }
        }
        // More than one same-name source identity claiming the same work is
        // ambiguous; let the user inspect the candidates instead of merging.
        return confirmed.DistinctBy(link => link.Url, StringComparer.OrdinalIgnoreCase).Count() == 1
            ? confirmed[0] : null;
    }
}
