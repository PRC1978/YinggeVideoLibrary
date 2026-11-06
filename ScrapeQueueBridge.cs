namespace YinggeVideoLibrary;

// Transitional UI boundary. Every normal scrape-queue call must pass through
// here so a database-mode window cannot touch the legacy queue journal.
internal sealed class ScrapeQueueBridge
{
    private readonly string dataDir;
    private readonly PostgresLibraryRuntimeSession? postgres;

    internal ScrapeQueueBridge(string dataDir, PostgresLibraryRuntimeSession? postgres)
    {
        this.dataDir = dataDir;
        this.postgres = postgres;
    }

    internal List<ScrapeQueueItem> Load() => postgres is null
        ? ScrapeQueueStore.Load(dataDir)
        : PostgresScrapeQueueStore.LoadAsync(postgres.ConnectionString).GetAwaiter().GetResult();

    internal int Add(IEnumerable<ScrapeQueueItem> items) => postgres is null
        ? ScrapeQueueStore.Add(dataDir, items)
        : postgres.AddQueueAsync(items).GetAwaiter().GetResult();

    internal void Remove(string videoId)
    {
        if (postgres is null) ScrapeQueueStore.Remove(dataDir, videoId);
        else postgres.RemoveQueueAsync(videoId).GetAwaiter().GetResult();
    }

    internal int ClearPending() => postgres is null
        ? ScrapeQueueStore.ClearPending(dataDir)
        : postgres.ClearPendingQueueAsync().GetAwaiter().GetResult();

    internal int RetryFailures() => postgres is null
        ? ScrapeQueueStore.RetryFailures(dataDir)
        : PostgresScrapeQueueStore.RetryFailuresAsync(postgres.ConnectionString)
            .GetAwaiter().GetResult();

    internal bool HasPending(string videoId) => postgres is null
        ? ScrapeQueueStore.HasPending(dataDir, videoId)
        : PostgresScrapeQueueStore.HasPendingAsync(postgres.ConnectionString, videoId)
            .GetAwaiter().GetResult();

    internal void Reschedule(ScrapeQueueItem item)
    {
        if (postgres is null) ScrapeQueueStore.Reschedule(dataDir, item);
        else postgres.RescheduleQueueAsync(item).GetAwaiter().GetResult();
    }

    internal void MarkFailed(string videoId, string error)
    {
        if (postgres is null) ScrapeQueueStore.MarkFailed(dataDir, videoId, error);
        else postgres.MarkQueueFailedAsync(videoId, error).GetAwaiter().GetResult();
    }

    internal DateTimeOffset ReserveSourceRequest(string source, TimeSpan spacing) =>
        postgres is null ? ScrapeQueueStore.ReserveSourceRequest(dataDir, source, spacing)
            : postgres.ReserveSourceAsync(source, spacing).GetAwaiter().GetResult();

    internal DateTimeOffset GetSourceCooldownAt(string source) => postgres is null
        ? ScrapeQueueStore.GetSourceCooldownAt(dataDir, source)
        : postgres.SourceCooldownAsync(source).GetAwaiter().GetResult();

    internal DateTimeOffset RecordSourceFailure(string source, string error,
        bool rateLimited, TimeSpan? retryAfter, int consecutiveFailureLimit) =>
        postgres is null
            ? ScrapeQueueStore.RecordSourceFailure(dataDir, source, error, rateLimited,
                retryAfter, consecutiveFailureLimit)
            : postgres.RecordSourceFailureAsync(source, error, rateLimited,
                retryAfter, consecutiveFailureLimit).GetAwaiter().GetResult();

    internal void ReportSourceSuccess(string source)
    {
        if (postgres is null) ScrapeQueueStore.ReportSourceSuccess(dataDir, source);
        else postgres.ReportSourceSuccessAsync(source).GetAwaiter().GetResult();
    }

    internal (ScrapeQueueItem? Item, int PendingCount, bool HasBlocked) PeekNextFair(
        IReadOnlyCollection<string> codeSources, IReadOnlyCollection<string> titleSources) =>
        postgres is null ? ScrapeQueueStore.PeekNextFair(dataDir, codeSources, titleSources)
            : postgres.PeekQueueAsync(dataDir, codeSources, titleSources).GetAwaiter().GetResult();

    internal (int PendingCount, int FailedCount, DateTimeOffset? NextAttemptAt,
        List<(string Query, string Error)> FailedExamples) GetStatus(
        IReadOnlyCollection<string>? codeSources = null,
        IReadOnlyCollection<string>? titleSources = null)
    {
        if (postgres is null) return codeSources is null || titleSources is null
            ? ScrapeQueueStore.GetStatus(dataDir)
            : ScrapeQueueStore.GetStatus(dataDir, codeSources, titleSources);
        var queue = Load();
        var pending = queue.Where(item => !item.Failed).ToArray();
        var failed = queue.Where(item => item.Failed).ToArray();
        DateTimeOffset? next;
        if (codeSources is null || titleSources is null)
            next = pending.Length == 0 ? null : pending.Min(item => item.NextAttemptAt);
        else
            next = PeekNextFair(codeSources, titleSources).Item?.NextAttemptAt;
        return (pending.Length, failed.Length, next,
            failed.Take(5).Select(item => (item.DisplayQuery, item.LastError)).ToList());
    }

    internal async Task<IAsyncDisposable?> TryAcquireWorkerAsync(
        CancellationToken cancellationToken = default)
    {
        if (postgres is not null)
            return await postgres.TryAcquireScrapeWorkerAsync(cancellationToken);
        var lease = await Task.Run(() => ScrapeQueueStore.TryAcquireWorker(dataDir),
            cancellationToken);
        return lease is null ? null : new FileLease(lease);
    }

    private sealed class FileLease(IDisposable lease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lease.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
