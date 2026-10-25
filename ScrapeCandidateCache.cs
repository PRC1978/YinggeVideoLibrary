using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace YinggeVideoLibrary;

internal static class ScrapeCandidateCache
{
    const int CurrentSchemaVersion = 2;
    const int TitleSnapshotSchemaVersion = 1;
    const string DefaultParserVersion = "1";
    const long MaxProbeEntryBytes = 4L * 1024 * 1024;
    static long cacheRevision;
    internal static long CacheRevision => Interlocked.Read(ref cacheRevision);

    public sealed class Entry
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = "";
        public string Query { get; set; } = "";
        public string ParserVersion { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public List<MovieMetadata> Candidates { get; set; } = [];
    }

    // Search cards and independently checked details are stored separately.
    // A review can inspect the original values even after a detail page changes.
    public sealed class RankedTitleCandidate
    {
        public int Rank { get; set; }
        public int Score { get; set; }
        public int LocalScore { get; set; }
        public int SourceWeight { get; set; }
        public string Evidence { get; set; } = "";
        public MovieMetadata SearchMetadata { get; set; } = new();
        public MovieMetadata? DetailMetadata { get; set; }
        public string DetailStatus { get; set; } = "search-only";
        public DateTime? DetailFetchedUtc { get; set; }

        [JsonIgnore]
        public MovieMetadata DisplayMetadata => DetailMetadata ?? SearchMetadata;
    }

    public sealed class TitleCandidateSnapshot
    {
        public int SchemaVersion { get; set; } = TitleSnapshotSchemaVersion;
        public string VideoId { get; set; } = "";
        public string Query { get; set; } = "";
        public DateTime SearchedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public List<RankedTitleCandidate> Candidates { get; set; } = [];
        [JsonIgnore]
        public bool NeedsRefresh => ExpiresUtc <= DateTime.UtcNow;
    }

    static string TitleSnapshotPath(string dataDir, string videoId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoId.Trim().ToUpperInvariant())));
        return System.IO.Path.Combine(dataDir, "title-candidates", key + ".json");
    }

    // The snapshot is deliberately not an expiring cache entry: stale evidence
    // remains comparable offline, clearly marked as needing a refresh.
    internal static TitleCandidateSnapshot? LoadTitleCandidates(string dataDir, string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return null;
        try
        {
            var file = new FileInfo(TitleSnapshotPath(dataDir, videoId));
            if (!file.Exists || file.Length > MaxProbeEntryBytes) return null;
            var snapshot = JsonSerializer.Deserialize<TitleCandidateSnapshot>(File.ReadAllText(file.FullName));
            return snapshot is { SchemaVersion: TitleSnapshotSchemaVersion, Candidates: { Count: > 0 and <= 12 } }
                && string.Equals(snapshot.VideoId, videoId, StringComparison.OrdinalIgnoreCase)
                && snapshot.Candidates.All(candidate => candidate.SearchMetadata is not null)
                    ? snapshot : null;
        }
        catch { return null; }
    }

    internal static List<RankedTitleCandidate> OrderTitleCandidates(IEnumerable<RankedTitleCandidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.SearchMetadata.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SearchMetadata.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SearchMetadata.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SearchMetadata.SourceUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count <= 12)
        {
            for (var index = 0; index < ordered.Count; index++) ordered[index].Rank = index + 1;
            return ordered;
        }
        // Keep the three best close matches and a route from every other
        // source before filling the remaining slots. Otherwise twelve cards
        // from one failing source could hide the only usable alternative.
        var kept = ordered.Take(3).ToList();
        var sources = kept.Select(candidate => candidate.SearchMetadata.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ordered)
        {
            if (kept.Count >= 12) break;
            if (sources.Add(candidate.SearchMetadata.Source)) kept.Add(candidate);
        }
        foreach (var candidate in ordered)
        {
            if (kept.Count >= 12) break;
            if (!kept.Contains(candidate)) kept.Add(candidate);
        }
        kept = kept.OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.SearchMetadata.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SearchMetadata.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SearchMetadata.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SearchMetadata.SourceUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < kept.Count; index++) kept[index].Rank = index + 1;
        return kept;
    }

    internal static void SaveTitleCandidates(string dataDir, TitleCandidateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.VideoId) || snapshot.Candidates.Count is < 1 or > 12)
            throw new ArgumentException("标题候选快照必须包含视频标识与 1–12 个真实候选。", nameof(snapshot));
        var path = TitleSnapshotPath(dataDir, snapshot.VideoId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            snapshot.SchemaVersion = TitleSnapshotSchemaVersion;
            snapshot.Candidates = OrderTitleCandidates(snapshot.Candidates);
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
            if (new FileInfo(temporary).Length > MaxProbeEntryBytes)
                throw new InvalidDataException("候选快照超过 4 MB，未覆盖上次记录。");
            File.Move(temporary, path, true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    static string PathFor(string dataDir, string source, string title)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + title.Trim().ToUpperInvariant())));
        return System.IO.Path.Combine(dataDir, "candidate-cache", key + ".json");
    }

    // A cache envelope can outlive a parser or arrive in an imported backup.
    // Bind every card to the source that owns the cache key so a malformed
    // native search entry cannot route a later detail request to a paid source.
    internal static bool CandidateMatchesCacheSource(string cacheSource, MovieMetadata? candidate)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Source)) return false;
        var actual = candidate.Source.Trim();
        static bool Same(string left, string right) =>
            left.Equals(right, StringComparison.OrdinalIgnoreCase);
        return cacheSource switch
        {
            "TMDB" or "TMDB 详情" => actual is "TMDB" or "TMDB 电影" or "TMDB 剧集",
            "JavDB 标题" or "JavDB 手动作品搜索" or "JavDB 标签首页交集" or "JavDB 标签前三页交集" or "JavDB 关系目录"
                => Same(actual, "JavDB"),
            "JAVDatabase 标题" => Same(actual, "JAVDatabase"),
            "Javinfo 手动" => actual.StartsWith("Javinfo · ", StringComparison.OrdinalIgnoreCase)
                && actual.Length > "Javinfo · ".Length,
            "Trakt" or "Trakt 详情" => Same(actual, "Trakt") &&
                TraktScraper.TryCandidateIdentity(candidate, out _, out _, out _),
            _ when cacheSource.EndsWith(" 详情", StringComparison.Ordinal)
                => Same(actual, cacheSource[..^" 详情".Length]),
            _ => Same(actual, cacheSource)
        };
    }

    // A verified flag belongs to one specific work, not every card saved at
    // the same cache key. Compare the public detail identity before using it.
    internal static bool SameCandidateIdentity(MovieMetadata candidate, MovieMetadata? detail)
    {
        if (detail is null ||
            !string.Equals(candidate.Source, detail.Source, StringComparison.OrdinalIgnoreCase) ||
            !SamePublicDetailUrl(candidate.SourceUrl, detail.SourceUrl)) return false;
        var candidateHasCode = !string.IsNullOrWhiteSpace(candidate.Code);
        var detailHasCode = !string.IsNullOrWhiteSpace(detail.Code);
        if (candidateHasCode != detailHasCode ||
            candidateHasCode && !ScrapeCodeIdentity.Same(candidate.Code, detail.Code)) return false;
        if (candidate.Source.Equals("Trakt", StringComparison.OrdinalIgnoreCase))
        {
            if (!TraktScraper.TryCandidateIdentity(candidate, out var kind, out var id, out var slug) ||
                !TraktScraper.TryCandidateIdentity(detail, out var detailKind, out var detailId, out var detailSlug) ||
                kind != detailKind || id != detailId || slug != detailSlug) return false;
            foreach (var key in new[] { "imdb", "tmdb" })
                if (candidate.ExternalIds.TryGetValue(key, out var expected) &&
                    (!detail.ExternalIds.TryGetValue(key, out var actual) || actual != expected)) return false;
        }
        return true;
    }

    internal static bool DetailMatchesCandidate(MovieMetadata candidate, MovieMetadata? detail)
        => detail is { DetailFetched: true, DetailIdentityVerified: true } && SameCandidateIdentity(candidate, detail);

    static bool SamePublicDetailUrl(string? first, string? second)
    {
        static Uri? Parse(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Port == 443 &&
            uri.Query.Length < 1024 ? uri : null;
        var left = Parse(first);
        var right = Parse(second);
        return left is not null && right is not null &&
            left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
            left.AbsolutePath.TrimEnd('/').Equals(right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal) &&
            left.Query.Equals(right.Query, StringComparison.Ordinal);
    }

    static bool CacheAccepted(Entry cached, string source, string query, string parserVersion)
    {
        if (cached.ExpiresUtc <= DateTime.UtcNow || cached.Candidates is null) return false;
        if (!cached.Candidates.All(candidate => CandidateMatchesCacheSource(source, candidate))) return false;
        if (cached.SchemaVersion == CurrentSchemaVersion && cached.Source == source &&
            string.Equals(cached.Query, query, StringComparison.OrdinalIgnoreCase) && cached.ParserVersion == parserVersion)
        {
            // An old or partial detail cannot bypass the network gate merely
            // because it was saved under the current cache envelope.
            return !source.EndsWith(" 详情", StringComparison.Ordinal)
                || (cached.Candidates.Count > 0 &&
                    cached.Candidates.All(candidate => candidate.DetailFetched && candidate.DetailIdentityVerified &&
                        (!Uri.TryCreate(query, UriKind.Absolute, out var requested) ||
                         requested.Scheme != Uri.UriSchemeHttps || SamePublicDetailUrl(query, candidate.SourceUrl))));
        }
        // Still-valid legacy search cards are useful for candidate ranking.
        // Legacy details and empty search responses must be refreshed.
        return cached.SchemaVersion == 0 && parserVersion == DefaultParserVersion
            && !source.EndsWith(" 详情", StringComparison.Ordinal)
            && cached.Candidates.Count > 0 && cached.Candidates.All(candidate => !candidate.DetailFetched);
    }

    public static async Task<IReadOnlyList<MovieMetadata>> GetOrFetchAsync(string dataDir, string source, string title,
        Func<Task<List<MovieMetadata>>> fetch, string parserVersion = DefaultParserVersion)
    {
        var path = PathFor(dataDir, source, title);
        var query = title.Trim();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path)) is { } cached &&
                CacheAccepted(cached, source, query, parserVersion))
                return cached.Candidates;
        }
        catch { /* A corrupt cache is a miss. */ }
        var results = await fetch();
        if (!results.All(candidate => CandidateMatchesCacheSource(source, candidate)))
            throw new InvalidDataException("搜刮来源返回了不属于当前来源的候选；已拒绝使用和缓存该结果。");
        if (source.EndsWith(" 详情", StringComparison.Ordinal) &&
            Uri.TryCreate(query, UriKind.Absolute, out var requestedUrl) && requestedUrl.Scheme == Uri.UriSchemeHttps &&
            !results.All(candidate => SamePublicDetailUrl(query, candidate.SourceUrl)))
            throw new InvalidDataException("来源详情链接与请求的作品不一致；已拒绝使用和缓存该结果。");
        var fetchedAt = DateTime.UtcNow;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Entry
            {
                SchemaVersion = CurrentSchemaVersion,
                Source = source,
                Query = query,
                ParserVersion = parserVersion,
                FetchedUtc = fetchedAt,
                ExpiresUtc = fetchedAt.Add(results.Count == 0 ? TimeSpan.FromHours(6) : TimeSpan.FromDays(3)),
                Candidates = results
            }));
            File.Move(temporary, path, true);
            Interlocked.Increment(ref cacheRevision);
        }
        catch { /* Search results remain usable without disk caching. */ }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch { } }
        return results;
    }

    static Entry? ReadAcceptedEntry(string dataDir, string source, string query,
        IReadOnlySet<string>? knownFiles = null)
    {
        try
        {
            var path = PathFor(dataDir, source, query);
            if (knownFiles is not null && !knownFiles.Contains(System.IO.Path.GetFileName(path))) return null;
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxProbeEntryBytes) return null;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
            return entry is not null && CacheAccepted(entry, source, query.Trim(), DefaultParserVersion)
                ? entry : null;
        }
        catch { return null; }
    }

    static string? SearchCacheSource(string source) => source switch
    {
        "TMDB" => "TMDB",
        "Pornhub" => "Pornhub",
        "TVmaze" => "TVmaze",
        "Wikidata" => "Wikidata",
        "Kitsu" => "Kitsu",
        "Tenrai" => "Tenrai",
        "Bangumi" => "Bangumi",
        "Mikan" => "Mikan",
        "FankuHub" => "FankuHub",
        "JavDB" => "JavDB 标题",
        "JAVDatabase" => "JAVDatabase 标题",
        _ => null
    };

    static bool CandidateFromSource(MovieMetadata candidate, string source) => source == "TMDB"
        ? candidate.Source is "TMDB 电影" or "TMDB 剧集"
        : string.Equals(candidate.Source, source, StringComparison.OrdinalIgnoreCase);

    // The scheduler may bypass a source cooldown only if title search and a
    // verified full detail are both already on disk. This is a read-only probe:
    // it never invokes a fetch callback or weakens the network gate.
    internal static bool HasReadyVerifiedTitleDetail(string dataDir, string title,
        IReadOnlyCollection<string> enabledSources, IReadOnlySet<string>? knownFiles = null)
    {
        var query = AdditionalScrapers.CleanTitle(title).Trim();
        if (query.Length == 0) return false;
        foreach (var source in enabledSources)
        {
            var searchSource = SearchCacheSource(source);
            if (searchSource is null || ReadAcceptedEntry(dataDir, searchSource, query, knownFiles) is not { } search)
                continue;
            foreach (var candidate in search.Candidates.Take(64))
            {
                if (!CandidateFromSource(candidate, source) ||
                    string.IsNullOrWhiteSpace(candidate.SourceUrl) || candidate.SourceUrl.Length > 2048)
                    continue;
                if (ReadAcceptedEntry(dataDir, candidate.Source + " 详情", candidate.SourceUrl, knownFiles) is { } detail &&
                    detail.Candidates.Any(record => DetailMatchesCandidate(candidate, record)))
                    return true;
            }
        }
        return false;
    }

    internal static HashSet<string> SnapshotCacheFiles(string dataDir)
    {
        try
        {
            var cacheDir = System.IO.Path.Combine(dataDir, "candidate-cache");
            return Directory.Exists(cacheDir)
                ? Directory.EnumerateFiles(cacheDir, "*.json", SearchOption.TopDirectoryOnly)
                    .Select(System.IO.Path.GetFileName).OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static string? FirstReadyQueuedTitle(string dataDir,
        IEnumerable<ScrapeQueueItem> pendingTitles, IReadOnlyCollection<string> enabledSources,
        IReadOnlySet<string> knownFiles)
    {
        if (knownFiles.Count == 0) return null;
        foreach (var item in pendingTitles)
        {
            if (HasReadyVerifiedTitleDetail(dataDir, item.SearchTitle, enabledSources, knownFiles))
                return item.VideoId;
        }
        return null;
    }

    internal static async Task<bool> VerifyLegacyIdentityRefreshAsync(string dataDir)
    {
        var source = "JavDB 详情";
        var query = "ABC-123";
        var path = PathFor(dataDir, source, query);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Entry
        {
            ExpiresUtc = DateTime.UtcNow.AddDays(1),
            Candidates = [new MovieMetadata { Code = query, Source = "JavDB", DetailFetched = true }]
        }));
        var requests = 0;
        var refreshed = await GetOrFetchAsync(dataDir, source, query, () =>
        {
            requests++;
            return Task.FromResult(new List<MovieMetadata>
            {
                new() { Code = query, Source = "JavDB", DetailFetched = true, DetailIdentityVerified = true }
            });
        });
        var stored = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path));
        var kept = await GetOrFetchAsync(dataDir, source, query, () =>
        {
            requests++;
            return Task.FromResult(new List<MovieMetadata>());
        });
        return requests == 1 && refreshed.Count == 1 && refreshed[0].DetailIdentityVerified &&
            kept.Count == 1 && stored?.SchemaVersion == CurrentSchemaVersion &&
            stored.Source == source && stored.Query == query;
    }

    // Offline regression for queue cooldown integration. A verified local
    // cache entry is usable while a source is cooling; an incomplete, empty,
    // expired or legacy detail must still enter the gated fetch callback.
    internal static async Task<bool> VerifyCooldownGateAsync(string dataDir)
    {
        var directory = System.IO.Path.Combine(dataDir, "cooldown-cache-test");
        Directory.CreateDirectory(directory);
        const string queueSource = "TMDB";
        static MovieMetadata Detail(bool verified) => new()
        {
            Code = "ABC-123", Title = "作品", Source = queueSource,
            DetailFetched = true, DetailIdentityVerified = verified
        };
        async Task Seed(string source, string query, List<MovieMetadata> candidates,
            DateTime expiryUtc, int schemaVersion = CurrentSchemaVersion)
        {
            var path = PathFor(directory, source, query);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Entry
            {
                SchemaVersion = schemaVersion, Source = source, Query = query,
                ParserVersion = DefaultParserVersion, ExpiresUtc = expiryUtc,
                Candidates = candidates
            }));
        }

        var future = DateTime.UtcNow.AddHours(1);
        await Seed("TMDB", "valid-search", [new MovieMetadata { Title = "作品", Source = queueSource }], future);
        await Seed("TMDB 详情", "valid-detail", [Detail(true)], future);
        await Seed("TMDB 详情", "unverified-detail", [Detail(false)], future);
        await Seed("TMDB 详情", "expired-detail", [Detail(true)], DateTime.UtcNow.AddMinutes(-1));
        await Seed("TMDB 详情", "legacy-detail", [Detail(true)], future, 0);
        await Seed("TMDB 详情", "empty-detail", [], future);

        ScrapeQueueStore.RecordSourceFailure(directory, queueSource, "429", true, TimeSpan.FromMinutes(10));
        var fetchInvocations = 0;
        Task<List<MovieMetadata>> GatedFetch()
        {
            fetchInvocations++;
            return Task.FromException<List<MovieMetadata>>(
                new ScrapeSourceUnavailableException("simulated source cooldown"));
        }

        var search = await GetOrFetchAsync(directory, "TMDB", "valid-search", GatedFetch);
        var detail = await GetOrFetchAsync(directory, "TMDB 详情", "valid-detail", GatedFetch);
        if (fetchInvocations != 0 || search.Count != 1 || detail.Count != 1 ||
            !detail[0].DetailIdentityVerified ||
            ScrapeQueueStore.GetSourceReadyAt(directory, queueSource) <= DateTimeOffset.UtcNow)
            return false;
        foreach (var query in new[] { "unverified-detail", "expired-detail", "legacy-detail", "empty-detail" })
        {
            try { _ = await GetOrFetchAsync(directory, "TMDB 详情", query, GatedFetch); return false; }
            catch (ScrapeSourceUnavailableException) { }
        }
        return fetchInvocations == 4;
    }

    internal static async Task<bool> VerifyWikidataCacheReadyAsync(string dataDir)
    {
        var directory = System.IO.Path.Combine(dataDir, "wikidata-cache-ready");
        const string title = "Spirited Away";
        const string url = "https://www.wikidata.org/wiki/Q155653";
        var candidate = new MovieMetadata { Source = "Wikidata", SourceUrl = url, Title = title };
        var detail = candidate.Clone();
        detail.DetailFetched = true;
        detail.DetailIdentityVerified = true;
        await GetOrFetchAsync(directory, "Wikidata", title, () => Task.FromResult(new List<MovieMetadata> { candidate }));
        await GetOrFetchAsync(directory, "Wikidata 详情", url, () => Task.FromResult(new List<MovieMetadata> { detail }));
        return HasReadyVerifiedTitleDetail(directory, title, ["Wikidata"])
            && !HasReadyVerifiedTitleDetail(directory, "Other Title", ["Wikidata"])
            && CandidateMatchesCacheSource("Wikidata 详情", detail);
    }

    internal static bool VerifyAnimeSourceCacheReady(string dataDir)
    {
        var directory = System.IO.Path.Combine(dataDir, "anime-source-cache-ready");
        Directory.CreateDirectory(directory);
        static void Seed(string directory, string source, string query, List<MovieMetadata> candidates)
        {
            var path = PathFor(directory, source, query);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Entry
            {
                SchemaVersion = CurrentSchemaVersion, Source = source, Query = query,
                ParserVersion = DefaultParserVersion, ExpiresUtc = DateTime.UtcNow.AddHours(1),
                Candidates = candidates
            }));
        }

        foreach (var (source, url) in new[]
        {
            ("Mikan", "https://mikanani.me/Home/Bangumi/3141"),
            ("FankuHub", "https://fankuhub.com/anime/cmpaxwdmq0amnmhlhtwpagp5l")
        })
        {
            var title = "Anime " + source;
            var candidate = new MovieMetadata { Source = source, SourceUrl = url, Title = title };
            var detail = candidate.Clone();
            detail.DetailFetched = true;
            detail.DetailIdentityVerified = true;
            Seed(directory, source, title, [candidate]);
            Seed(directory, source + " 详情", url, [detail]);
            ScrapeQueueStore.RecordSourceFailure(directory, source, "429", true, TimeSpan.FromMinutes(10));
            var cacheFiles = SnapshotCacheFiles(directory);
            if (ScrapeQueueStore.GetSourceReadyAt(directory, source) <= DateTimeOffset.UtcNow ||
                !HasReadyVerifiedTitleDetail(directory, title, [source], cacheFiles) ||
                FirstReadyQueuedTitle(directory,
                    [new ScrapeQueueItem { VideoId = source, Kind = "title", SearchTitle = title }],
                    [source], cacheFiles) != source ||
                HasReadyVerifiedTitleDetail(directory, "Other " + title, [source], cacheFiles))
                return false;
        }
        return true;
    }

    internal static async Task<bool> VerifyDetailIdentityBindingAsync(string dataDir)
    {
        var directory = System.IO.Path.Combine(dataDir, "detail-identity-binding");
        const string title = "One Work";
        const string firstUrl = "https://www.themoviedb.org/movie/123/";
        const string otherUrl = "https://www.themoviedb.org/movie/456/";
        var candidate = new MovieMetadata { Source = "TMDB 电影", Title = title, SourceUrl = firstUrl, Code = "ABC-123" };
        var good = candidate.Clone(); good.DetailFetched = true; good.DetailIdentityVerified = true;
        var wrongUrl = good.Clone(); wrongUrl.SourceUrl = otherUrl;
        var wrongCode = good.Clone(); wrongCode.Code = "ABC-124";
        var missingCode = good.Clone(); missingCode.Code = "";
        var wrongSource = good.Clone(); wrongSource.Source = "Wikidata";
        var normalized = good.Clone(); normalized.SourceUrl = "https://WWW.THEMOVIEDB.ORG/movie/123";
        if (!DetailMatchesCandidate(candidate, good) || !DetailMatchesCandidate(candidate, normalized) ||
            DetailMatchesCandidate(candidate, wrongUrl) || DetailMatchesCandidate(candidate, wrongCode) ||
            DetailMatchesCandidate(candidate, missingCode) || DetailMatchesCandidate(candidate, wrongSource)) return false;

        await GetOrFetchAsync(directory, "TMDB", title, () => Task.FromResult(new List<MovieMetadata> { candidate }));
        await GetOrFetchAsync(directory, "TMDB 电影 详情", firstUrl, () => Task.FromResult(new List<MovieMetadata> { wrongCode }));
        if (HasReadyVerifiedTitleDetail(directory, title, ["TMDB"])) return false;
        var detailPath = PathFor(directory, "TMDB 电影 详情", firstUrl);
        await File.WriteAllTextAsync(detailPath, JsonSerializer.Serialize(new Entry
        {
            SchemaVersion = CurrentSchemaVersion, Source = "TMDB 电影 详情", Query = firstUrl,
            ParserVersion = DefaultParserVersion, ExpiresUtc = DateTime.UtcNow.AddHours(1),
            Candidates = [wrongUrl]
        }));
        if (HasReadyVerifiedTitleDetail(directory, title, ["TMDB"])) return false;
        var refreshed = await GetOrFetchAsync(directory, "TMDB 电影 详情", firstUrl,
            () => Task.FromResult(new List<MovieMetadata> { good }));
        return refreshed.Count == 1 && DetailMatchesCandidate(candidate, refreshed[0]) &&
            HasReadyVerifiedTitleDetail(directory, title, ["TMDB"]);
    }
}
