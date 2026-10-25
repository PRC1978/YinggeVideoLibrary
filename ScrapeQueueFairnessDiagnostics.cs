using System.Diagnostics;
using System.IO;

namespace YinggeVideoLibrary;

internal sealed record ScrapeQueueFairnessResult(
    bool Passed, int JobCount, long AddMilliseconds, long PeekMilliseconds,
    bool IndependentBackoff, bool AlternatingRoutes, bool RestartRecovered,
    bool CancelPreservedFailures, bool CachedHitBypassesCooldown,
    bool SnapshotSchemaValidated, bool SourceCooldownPreservesRetryBudget,
    bool PermanentSourceErrorStopsRetry, bool QueuedCachedDetailBypassesCooldown,
    long CacheBypassPeekMilliseconds, int CacheBypassScanBatches,
    long InitialCacheBypassMilliseconds, bool LargeQueue32kBounded,
    int LargeQueueScanBatches, long LargeQueueScanMilliseconds);

internal static class ScrapeQueueFairnessDiagnostics
{
    // No network or window. Four simulated sources cover the two routes used
    // by the real worker; the durable snapshot is reloaded after checkpoint.
    internal static ScrapeQueueFairnessResult Run(string testRoot)
    {
        var directory = Path.Combine(testRoot, "fair-scrape-queue");
        var jobs = Enumerable.Range(0, 10_000).Select(index => index < 5_000
            ? new ScrapeQueueItem { VideoId = "code-" + index, Kind = "code", Code = "ABC-" + index }
            : new ScrapeQueueItem { VideoId = "title-" + index, Kind = "title", SearchTitle = "作品 " + index })
            .ToArray();
        string[] codeSources = ["JavDB", "JAVDatabase"];
        string[] titleSources = ["TMDB", "Bangumi"];
        var timer = Stopwatch.StartNew();
        var added = ScrapeQueueStore.Add(directory, jobs);
        timer.Stop();
        var addMs = timer.ElapsedMilliseconds;

        // One 429 must not delay a second source in the same route.
        var javDbUntil = ScrapeQueueStore.RecordSourceFailure(directory, "JavDB", "429", true, TimeSpan.FromMinutes(10));
        var first = ScrapeQueueStore.PeekNextFair(directory, codeSources, titleSources);
        var oneSourceStillReady = first.Item?.Kind == "code"
            && first.Item.NextAttemptAt <= DateTimeOffset.UtcNow;

        // Cooling both code sources must not block title jobs.
        var javDatabaseUntil = ScrapeQueueStore.RecordSourceFailure(directory, "JAVDatabase", "429", true, TimeSpan.FromMinutes(9));
        var title = ScrapeQueueStore.PeekNextFair(directory, codeSources, titleSources);
        var otherRouteReady = title.Item?.Kind == "title"
            && title.Item.NextAttemptAt <= DateTimeOffset.UtcNow;
        ScrapeQueueStore.RecordSourceFailure(directory, "TMDB", "429", true, TimeSpan.FromMinutes(11));
        ScrapeQueueStore.RecordSourceFailure(directory, "Bangumi", "429", true, TimeSpan.FromMinutes(12));
        var scheduled = ScrapeQueueStore.PeekNextFair(directory, codeSources, titleSources);
        var earliestCooldownWins = scheduled.Item?.Kind == "code"
            && scheduled.Item.NextAttemptAt >= javDatabaseUntil;

        ScrapeQueueStore.ReportSourceSuccess(directory, "JAVDatabase");
        ScrapeQueueStore.ReportSourceSuccess(directory, "TMDB");
        var nextCode = ScrapeQueueStore.PeekNextFair(directory, codeSources, titleSources);
        var nextTitle = ScrapeQueueStore.PeekNextFair(directory, codeSources, titleSources);
        var alternating = nextCode.Item?.Kind == "code" && nextTitle.Item?.Kind == "title";
        var independent = oneSourceStillReady && otherRouteReady && earliestCooldownWins
            && ScrapeQueueStore.GetSourceReadyAt(directory, "JavDB") >= javDbUntil;

        timer.Restart();
        for (var index = 0; index < 1_000; index++)
            _ = ScrapeQueueStore.PeekNextFair(directory, codeSources, titleSources);
        timer.Stop();
        var peekMs = timer.ElapsedMilliseconds;

        // The first queued title has no cache. A later title has a complete,
        // verified search/detail pair and must run during source cooldown.
        // An unverified detail immediately before it must remain gated.
        static async Task SeedTitleCacheAsync(string dataDir, int number, bool verified)
        {
            var title = "作品 " + number;
            var url = $"https://www.themoviedb.org/movie/{number}";
            var search = new MovieMetadata { Title = title, Source = "TMDB 电影", SourceUrl = url };
            var detail = new MovieMetadata
            {
                Title = title, Source = "TMDB 电影", SourceUrl = url,
                DetailFetched = true, DetailIdentityVerified = verified
            };
            _ = await ScrapeCandidateCache.GetOrFetchAsync(dataDir, "TMDB", title,
                () => Task.FromResult(new List<MovieMetadata> { search }));
            _ = await ScrapeCandidateCache.GetOrFetchAsync(dataDir, "TMDB 电影 详情", url,
                () => Task.FromResult(new List<MovieMetadata> { detail }));
        }
        Task.Run(async () =>
        {
            await SeedTitleCacheAsync(directory, 9998, false);
            await SeedTitleCacheAsync(directory, 9999, true);
        }).GetAwaiter().GetResult();
        ScrapeQueueStore.RecordSourceFailure(directory, "TMDB", "429", true, TimeSpan.FromMinutes(10));
        var gated = ScrapeQueueStore.PeekNextFair(directory, [], titleSources, includeCachedTitle: false);
        timer.Restart();
        (ScrapeQueueItem? Item, int PendingCount, bool HasBlocked) cached = default;
        var scanBatches = 0;
        while (scanBatches++ < 8)
        {
            cached = ScrapeQueueStore.PeekNextFair(directory, [], titleSources);
            if (cached.Item?.VideoId == "title-9999") break;
        }
        timer.Stop();
        var initialCacheBypassMs = timer.ElapsedMilliseconds;
        var cachedStatus = ScrapeQueueStore.GetStatus(directory, [], titleSources);
        var queuedCachedDetailBypassesCooldown = gated.Item?.VideoId == "title-5000"
            && gated.Item.NextAttemptAt > DateTimeOffset.UtcNow
            && cached.Item?.VideoId == "title-9999"
            && cached.Item.NextAttemptAt <= DateTimeOffset.UtcNow
            && cachedStatus.NextAttemptAt <= DateTimeOffset.UtcNow;
        timer.Restart();
        for (var index = 0; index < 1_000; index++)
            _ = ScrapeQueueStore.PeekNextFair(directory, [], titleSources);
        timer.Stop();
        var cacheBypassPeekMs = timer.ElapsedMilliseconds;

        ScrapeQueueStore.CheckpointForDiagnostics(directory);
        ScrapeQueueStore.ForgetCachedStateAfterOfflineRestore(directory);
        var restored = ScrapeQueueStore.Load(directory);
        var restartRecovered = restored.Count == jobs.Length
            && ScrapeQueueStore.GetSourceReadyAt(directory, "JavDB") >= javDbUntil
            && ScrapeQueueStore.GetSourceReadyAt(directory, "JAVDatabase") == DateTimeOffset.MinValue
            && ScrapeQueueStore.PeekNextFair(directory, codeSources, titleSources).Item is not null;

        ScrapeQueueStore.MarkFailed(directory, "code-0", "diagnostic failure");
        var canceled = ScrapeQueueStore.ClearPending(directory);
        var remaining = ScrapeQueueStore.Load(directory);
        var cancelPreserved = canceled == jobs.Length - 1 && remaining.Count == 1
            && remaining[0].Failed && remaining[0].VideoId == "code-0"
            && ScrapeQueueStore.RetryFailures(directory) == 1
            && ScrapeQueueStore.HasPending(directory, "code-0");

        var cachedHitBypassesCooldown = Task.Run(() =>
            ScrapeCandidateCache.VerifyCooldownGateAsync(directory)).GetAwaiter().GetResult();
        var malformedSidecar = Path.Combine(directory, "malformed-source-backoff.json");
        File.WriteAllText(malformedSidecar, "{}");
        var snapshotSchemaValidated = ScrapeQueueStore.ValidateSourceBackoffSnapshot(
            Path.Combine(directory, "scrape-source-backoff.json"))
            && !ScrapeQueueStore.ValidateSourceBackoffSnapshot(malformedSidecar);
        var sourceCooldownPreservesRetryBudget = MainWindow.VerifySourceRetryPolicyForDiagnostics();
        var permanentSourceErrorStopsRetry = MainWindow.VerifyBulkSourceFailureForDiagnostics();
        var largeQueue = VerifyBoundedLargeQueue(Path.Combine(testRoot, "large-cache-queue"));

        return new ScrapeQueueFairnessResult(
            added == jobs.Length && independent && alternating && restartRecovered && cancelPreserved &&
            cachedHitBypassesCooldown && snapshotSchemaValidated && sourceCooldownPreservesRetryBudget &&
            permanentSourceErrorStopsRetry && queuedCachedDetailBypassesCooldown && largeQueue.Passed,
            jobs.Length, addMs, peekMs, independent, alternating, restartRecovered, cancelPreserved,
            cachedHitBypassesCooldown, snapshotSchemaValidated, sourceCooldownPreservesRetryBudget,
            permanentSourceErrorStopsRetry, queuedCachedDetailBypassesCooldown, cacheBypassPeekMs,
            scanBatches, initialCacheBypassMs, largeQueue.Passed, largeQueue.Batches,
            largeQueue.Milliseconds);
    }

    static (bool Passed, int Batches, long Milliseconds) VerifyBoundedLargeQueue(string directory)
    {
        const int count = 32_000;
        var added = ScrapeQueueStore.Add(directory, Enumerable.Range(0, count)
            .Select(index => new ScrapeQueueItem
            {
                VideoId = "large-" + index, Kind = "title", SearchTitle = "冷却作品 " + index
            }));
        const string title = "冷却作品 31999";
        const string url = "https://www.themoviedb.org/movie/31999";
        Task.Run(async () =>
        {
            _ = await ScrapeCandidateCache.GetOrFetchAsync(directory, "TMDB", title,
                () => Task.FromResult(new List<MovieMetadata>
                {
                    new() { Title = title, Source = "TMDB 电影", SourceUrl = url }
                }));
            _ = await ScrapeCandidateCache.GetOrFetchAsync(directory, "TMDB 电影 详情", url,
                () => Task.FromResult(new List<MovieMetadata>
                {
                    new() { Title = title, Source = "TMDB 电影", SourceUrl = url,
                        DetailFetched = true, DetailIdentityVerified = true }
                }));
        }).GetAwaiter().GetResult();
        ScrapeQueueStore.RecordSourceFailure(directory, "TMDB", "429", true, TimeSpan.FromMinutes(10));
        var timer = Stopwatch.StartNew();
        var batches = 0;
        ScrapeQueueItem? selected = null;
        while (batches++ < 40)
        {
            selected = ScrapeQueueStore.PeekNextFair(directory, [], ["TMDB"]).Item;
            if (selected?.VideoId == "large-31999") break;
        }
        timer.Stop();
        var scan = ScrapeQueueStore.CachedTitleScanDiagnostics(directory);
        var passed = added == count && selected?.VideoId == "large-31999"
            && selected.NextAttemptAt <= DateTimeOffset.UtcNow
            && batches is >= 31 and <= 33 && scan.Snapshots == 1
            && scan.MaxBatchSize is > 0 and <= 1024;
        return (passed, batches, timer.ElapsedMilliseconds);
    }
}
