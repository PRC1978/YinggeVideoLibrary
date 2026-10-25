namespace YinggeVideoLibrary;

// Both sites independently printed the same structured work code in a bounded
// relation directory. This is only a candidate preview, never approval data.
internal sealed record CrossSourceRelationCandidate(MovieMetadata JavDb, MovieMetadata JavDatabase)
{
    public string Code => ScrapeCodeIdentity.Key(JavDb.Code);
    public string DisplayName => $"{Code} · {JavDb.Title} · 两站目录均出现";
}

internal sealed record CrossSourceRelationPreview(
    JavDbCatalogLink JavDbRelation, JavDatabaseRelationLink JavDatabaseRelation,
    string JavDbOriginUrl, int JavDbPagesRead, int JavDatabasePagesRead,
    bool JavDbMayHaveMore, bool JavDatabaseMayHaveMore,
    IReadOnlyList<CrossSourceRelationCandidate> Candidates);

internal static class CrossSourceRelationBrowser
{
    internal const int MaximumPagesPerSource = 3;
    internal const int MaximumCandidates = 48;

    internal static void ValidateRelations(MovieMetadata javDbOrigin, JavDbCatalogLink javDbLink,
        JavDatabaseRelationLink databaseLink)
    {
        if (!JavDbScraper.IsVerifiedCatalogOrigin(javDbOrigin, javDbLink) ||
            !JavDatabaseCatalog.TryCatalogPageUrl(databaseLink.Url, databaseLink, out _, out var page) ||
            page != 1 || databaseLink.Kind is not ("演员" or "系列") ||
            ScrapeCodeIdentity.Key(databaseLink.OriginCode).Length == 0)
            throw new InvalidOperationException("请先从两站已核对的作品详情中选择真实关系链接。 ");
    }

    // Duplicate cards for one code and different detail URLs are ambiguous;
    // silently choosing one would weaken the identity check.
    internal static IReadOnlyList<CrossSourceRelationCandidate> IntersectObservedCandidates(
        IReadOnlyList<MovieMetadata> javDbCandidates, IReadOnlyList<MovieMetadata> databaseCandidates)
    {
        static Dictionary<string, MovieMetadata> UniqueByCode(IEnumerable<MovieMetadata> candidates,
            string source, Func<MovieMetadata, bool> validUrl)
        {
            var groups = candidates.Where(item => string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase))
                .Select(item => (Key: ScrapeCodeIdentity.Key(item.Code), Item: item))
                .Where(pair => pair.Key.Length >= 4 && validUrl(pair.Item))
                .GroupBy(pair => pair.Key, StringComparer.Ordinal);
            var unique = new Dictionary<string, MovieMetadata>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var urls = group.Select(pair => pair.Item.SourceUrl)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (urls.Count == 1) unique[group.Key] = group.First().Item;
            }
            return unique;
        }

        var left = UniqueByCode(javDbCandidates, "JavDB", JavDbScraper.IsCatalogCandidate);
        var right = UniqueByCode(databaseCandidates, "JAVDatabase", item =>
            JavDatabaseScraper.IsVerifiedDetailSource(item.Code, item.SourceUrl));
        return left.Where(pair => right.ContainsKey(pair.Key)).Take(MaximumCandidates)
            .Select(pair => new CrossSourceRelationCandidate(pair.Value.Clone(), right[pair.Key].Clone()))
            .ToList();
    }

    internal static async Task<CrossSourceRelationPreview> FetchPreviewAsync(MovieMetadata javDbOrigin,
        JavDbCatalogLink javDbLink, JavDatabaseRelationLink databaseLink,
        CancellationToken cancellationToken = default)
        => await FetchPreviewCoreAsync(javDbOrigin, javDbLink, databaseLink,
            (link, page, token) => JavDbScraper.FetchCatalogPageWithNavigationAsync(link.Url, page, token),
            (link, url, token) => JavDatabaseCatalog.FetchCatalogPageAsync(link, url, token),
            cancellationToken);

    // The delegates make the page budget and observed-next-link behavior
    // independently testable without making a network request.
    internal static async Task<CrossSourceRelationPreview> FetchPreviewCoreAsync(MovieMetadata javDbOrigin,
        JavDbCatalogLink javDbLink, JavDatabaseRelationLink databaseLink,
        Func<JavDbCatalogLink, int, CancellationToken, Task<JavDbScraper.CatalogPageResult>> fetchJavDb,
        Func<JavDatabaseRelationLink, string, CancellationToken, Task<JavDatabaseRelationPage>> fetchDatabase,
        CancellationToken cancellationToken = default)
    {
        ValidateRelations(javDbOrigin, javDbLink, databaseLink);
        var javDbCandidates = new List<MovieMetadata>();
        var databaseCandidates = new List<MovieMetadata>();
        var javDbPages = 0;
        var databasePages = 0;
        var javDbHasMore = false;
        var databaseHasMore = false;
        for (var page = 1; page <= MaximumPagesPerSource; page++)
        {
            var result = await fetchJavDb(javDbLink, page, cancellationToken);
            javDbPages++;
            javDbCandidates.AddRange(result.Movies);
            javDbHasMore = result.HasNextPage;
            if (!javDbHasMore) break;
        }
        string? databasePageUrl = databaseLink.Url;
        while (databasePageUrl is not null && databasePages < MaximumPagesPerSource)
        {
            var result = await fetchDatabase(databaseLink, databasePageUrl, cancellationToken);
            databasePages++;
            databaseCandidates.AddRange(result.Candidates);
            databasePageUrl = result.NextPageUrl;
            if (databasePageUrl is not null &&
                (!JavDatabaseCatalog.TryCatalogPageUrl(databasePageUrl, databaseLink, out var verifiedNext, out var nextNumber) ||
                 nextNumber != databasePages + 1 || !verifiedNext.Equals(databasePageUrl, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("JAVDatabase 返回了无法核对的下一页地址。 ");
        }
        databaseHasMore = databasePageUrl is not null;
        return new CrossSourceRelationPreview(javDbLink.Clone(), databaseLink,
            javDbOrigin.SourceUrl, javDbPages, databasePages, javDbHasMore, databaseHasMore,
            IntersectObservedCandidates(javDbCandidates, databaseCandidates));
    }
}
