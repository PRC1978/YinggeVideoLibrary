using System.Collections.Concurrent;

namespace YinggeVideoLibrary;

// Scheduling is advisory; ReserveAsync is the authoritative cross-process
// source slot. This adapter never reads the legacy queue journal or sidecar.
internal static class PostgresScrapeQueueScheduler
{
    private sealed class State
    {
        internal string LastSelectedKind = "title";
        internal int TitleScanOffset;
        internal long CacheRevision = -1;
        internal int TitleCount;
    }

    private static readonly ConcurrentDictionary<string, State> States = new(StringComparer.Ordinal);

    internal static async Task<(ScrapeQueueItem? Item, int PendingCount, bool HasBlocked)> PeekNextFairAsync(
        string connectionString, string cacheDirectory,
        IReadOnlyCollection<string> codeSources, IReadOnlyCollection<string> titleSources,
        bool includeCachedTitle = true, CancellationToken cancellationToken = default)
    {
        var queue = await PostgresScrapeQueueStore.LoadAsync(connectionString, cancellationToken);
        var pending = queue.Where(item => !item.Failed).ToArray();
        var count = pending.Length;
        if (count == 0) return (null, 0, false);
        var backoffs = await PostgresScrapeSourceBackoffStore.LoadAsync(connectionString, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? SourceAt(IReadOnlyCollection<string> sources)
        {
            if (sources.Count == 0) return null;
            var earliest = DateTimeOffset.MaxValue;
            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                var ready = now;
                if (backoffs.TryGetValue(source, out var state))
                    ready = Max(now, state.Until, state.NextRequestAt);
                if (ready < earliest) earliest = ready;
            }
            return earliest == DateTimeOffset.MaxValue ? null : earliest;
        }

        // LINQ OrderBy is stable: LoadAsync already returns durable ordinal order.
        var code = pending.Where(item => item.Kind == "code")
            .OrderBy(item => item.NextAttemptAt).FirstOrDefault();
        var title = pending.Where(item => item.Kind == "title")
            .OrderBy(item => item.NextAttemptAt).FirstOrDefault();
        var codeSourceAt = SourceAt(codeSources);
        var titleSourceAt = SourceAt(titleSources);
        DateTimeOffset? codeAt = code is null || codeSourceAt is null ? null : Max(code.NextAttemptAt, codeSourceAt.Value);
        DateTimeOffset? titleAt = title is null || titleSourceAt is null ? null : Max(title.NextAttemptAt, titleSourceAt.Value);
        var state = States.GetOrAdd(connectionString, _ => new State());
        if (includeCachedTitle && titleAt > now && titleSourceAt > now)
        {
            var readyTitles = pending.Where(item => item.Kind == "title" && item.NextAttemptAt <= now)
                .OrderBy(item => item.NextAttemptAt).ToArray();
            var cacheRevision = ScrapeCandidateCache.CacheRevision;
            var start = 0;
            lock (state)
            {
                if (state.TitleCount == readyTitles.Length && state.CacheRevision == cacheRevision)
                    start = state.TitleScanOffset;
                else { state.TitleScanOffset = 0; state.TitleCount = readyTitles.Length; state.CacheRevision = cacheRevision; }
            }
            if (readyTitles.Length > 0)
            {
                var batch = readyTitles.Skip(start).Take(1024).ToArray();
                var files = ScrapeCandidateCache.SnapshotCacheFiles(cacheDirectory);
                var readyId = ScrapeCandidateCache.FirstReadyQueuedTitle(cacheDirectory,
                    batch, titleSources, files);
                if (readyId is not null)
                {
                    title = batch.First(item => item.VideoId.Equals(readyId, StringComparison.OrdinalIgnoreCase));
                    titleAt = title.NextAttemptAt;
                }
                lock (state) state.TitleScanOffset = readyId is null && start + batch.Length < readyTitles.Length
                    ? start + batch.Length : 0;
            }
        }
        bool chooseCode;
        lock (state)
        {
            chooseCode = codeAt is not null && (titleAt is null ||
                (codeAt <= now && titleAt <= now ? state.LastSelectedKind != "code" :
                    codeAt < titleAt || codeAt == titleAt && state.LastSelectedKind != "code"));
            var selected = chooseCode ? code : titleAt is not null ? title : null;
            var selectedAt = chooseCode ? codeAt : titleAt;
            if (selected is null || selectedAt is null) return (null, count, true);
            if (selectedAt <= now) state.LastSelectedKind = selected.Kind;
            return (new ScrapeQueueItem
            {
                VideoId = selected.VideoId, Kind = selected.Kind, Code = selected.Code,
                SearchTitle = selected.SearchTitle, RefreshExisting = selected.RefreshExisting,
                Attempts = selected.Attempts, Failed = selected.Failed,
                NextAttemptAt = selectedAt.Value, LastError = selected.LastError
            }, count, false);
        }
    }

    private static DateTimeOffset Max(params DateTimeOffset[] values) => values.Max();
}
