using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Owns the private database process while a database-mode library is open.
// The normal window does not use this until every authoritative writer has
// moved to PostgreSQL; opening a partially migrated window would split data.
internal sealed class PostgresLibraryRuntimeSession : IAsyncDisposable
{
    private readonly PostgresPrivateCluster cluster;

    internal string ConnectionString => cluster.ConnectionString;
    internal LibraryConfig Config { get; private set; }
    internal PostgresLibraryIndexStore.Snapshot Index { get; private set; }
    internal PostgresLibraryMetadataStore.Snapshot Metadata { get; private set; }
    internal IReadOnlyList<ScrapeQueueItem> Queue { get; private set; }
    internal PostgresReviewTransactionStore.HistorySnapshot ReviewHistory { get; private set; }
    internal PostgresRoleProfileStore.Snapshot Roles { get; private set; }
    private readonly SemaphoreSlim writeGate = new(1, 1);

    private PostgresLibraryRuntimeSession(PostgresPrivateCluster cluster, LibraryConfig config,
        PostgresLibraryIndexStore.Snapshot index,
        PostgresLibraryMetadataStore.Snapshot metadata, List<ScrapeQueueItem> queue,
        PostgresReviewTransactionStore.HistorySnapshot reviewHistory,
        PostgresRoleProfileStore.Snapshot roles)
    {
        this.cluster = cluster;
        Config = config;
        Index = index;
        Metadata = metadata;
        Queue = queue;
        ReviewHistory = reviewHistory;
        Roles = roles;
    }

    internal async Task SaveConfigAsync(LibraryConfig edited,
        CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            Config = await PostgresLibraryConfigStore.SaveAsync(ConnectionString,
                Config, edited, cancellationToken);
        }
        finally { writeGate.Release(); }
    }

    internal async Task SaveIndexAsync(LibraryIndex edited,
        CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await PostgresLibraryIndexStore.SaveAsync(ConnectionString, edited,
                Index.Revision, cancellationToken);
            Index = await PostgresLibraryIndexStore.ReadAsync(ConnectionString,
                cancellationToken);
        }
        finally { writeGate.Release(); }
    }

    internal async Task<MainWindow.IndexSaveResult> SaveScannedIndexAsync(LibraryIndex scanned,
        bool explicitlyClearedByUser, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            if (scanned.Folders.Length == 0 && scanned.Entries.Count == 0 &&
                !explicitlyClearedByUser)
                return new(false, 0, false);
            var previous = Index.Index;
            if (MainWindow.ShouldPreserveMismatchedIndex(previous, scanned,
                explicitlyClearedByUser))
                return new(true, previous.Entries.Count, false);
            await PostgresLibraryIndexStore.SaveAsync(ConnectionString, scanned,
                Index.Revision, cancellationToken);
            Index = await PostgresLibraryIndexStore.ReadAsync(ConnectionString,
                cancellationToken);
            return new(false, 0, true);
        }
        finally { writeGate.Release(); }
    }

    internal async Task SaveMetadataAsync(IReadOnlyDictionary<string, MovieMetadata> edited,
        CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await PostgresLibraryMetadataStore.SaveAsync(ConnectionString, edited,
                Metadata.Revision, cancellationToken);
            Metadata = await PostgresLibraryMetadataStore.ReadAsync(ConnectionString,
                cancellationToken);
        }
        finally { writeGate.Release(); }
    }

    internal async Task CommitReviewAsync(ReviewTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await PostgresReviewTransactionStore.CommitAsync(ConnectionString, transaction,
                cancellationToken);
            var document = await PostgresLibraryDocumentStore.ReadAsync(ConnectionString,
                "config.json", cancellationToken)
                ?? throw new InvalidDataException("审核提交后数据库配置丢失。 ");
            Config = JsonSerializer.Deserialize<LibraryConfig>(document.Content)
                ?? throw new InvalidDataException("审核提交后数据库配置无效。 ");
            Metadata = await PostgresLibraryMetadataStore.ReadAsync(ConnectionString,
                cancellationToken);
            ReviewHistory = await PostgresReviewTransactionStore.LoadAsync(ConnectionString,
                cancellationToken);
        }
        finally { writeGate.Release(); }
    }

    internal async Task<int> AddQueueAsync(IEnumerable<ScrapeQueueItem> candidates,
        CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var added = await PostgresScrapeQueueStore.AddAsync(ConnectionString, candidates,
                cancellationToken);
            Queue = await PostgresScrapeQueueStore.LoadAsync(ConnectionString, cancellationToken);
            return added;
        }
        finally { writeGate.Release(); }
    }

    internal async Task SaveRolesAsync(RoleProfileStore.State edited,
        CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            Roles = await PostgresRoleProfileStore.SaveAsync(ConnectionString, Roles,
                edited, cancellationToken);
        }
        finally { writeGate.Release(); }
    }

    // Used for the remaining allowlisted user-state documents until their
    // individual UI writers are moved to typed database operations. A caller
    // must carry the revision it read; the session never overwrites blindly.
    internal Task<PostgresLibraryDocumentStore.Snapshot?> ReadDocumentAsync(string name,
        CancellationToken cancellationToken = default) =>
        PostgresLibraryDocumentStore.ReadAsync(ConnectionString, name, cancellationToken);

    internal async Task<string> SaveDocumentAsync(string name, byte[] content,
        string? expectedRevision, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            if (name == "config.json" || PostgresLibraryMigration.IsQueueDocumentName(name) ||
                name is "role-profiles.json" or "review-history.jsonl")
                throw new InvalidDataException("该资料必须使用专用数据库事务保存：" + name);
            return await PostgresLibraryDocumentStore.SaveAsync(ConnectionString, name,
                content, expectedRevision, cancellationToken);
        }
        finally { writeGate.Release(); }
    }

    internal async Task DeleteDocumentAsync(string name, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            if (name == "config.json" || PostgresLibraryMigration.IsQueueDocumentName(name) ||
                name is "role-profiles.json" or "review-history.jsonl")
                throw new InvalidDataException("该资料必须使用专用数据库事务删除：" + name);
            await PostgresLibraryDocumentStore.DeleteAsync(ConnectionString, name,
                expectedRevision, cancellationToken);
        }
        finally { writeGate.Release(); }
    }

    internal static async Task<PostgresLibraryRuntimeSession> OpenAsync(string runtimeDirectory,
        string dataDirectory, CancellationToken cancellationToken = default)
    {
        dataDirectory = Path.GetFullPath(dataDirectory);
        if (!PostgresStorageModeGuard.PostgresMayOpen(dataDirectory))
            throw new InvalidDataException("视频库未授权数据库模式，或恢复尚未完成。 ");
        var cluster = await PostgresPrivateCluster.OpenAsync(runtimeDirectory,
            dataDirectory, cancellationToken);
        try
        {
            if (!PostgresStorageModeGuard.PostgresMayOpen(dataDirectory))
                throw new InvalidDataException("数据库启动期间存储状态发生变化。 ");
            var connection = cluster.ConnectionString;
            var schema = await PostgresLibraryMigration.InspectSchemaAsync(connection,
                cancellationToken);
            if (schema.State != PostgresLibraryMigration.SchemaState.Current)
                throw new InvalidDataException("数据库结构版本不受当前程序支持。 ");
            var configDocument = await PostgresLibraryDocumentStore.ReadAsync(connection,
                "config.json", cancellationToken)
                ?? throw new InvalidDataException("数据库缺少配置。 ");
            var config = JsonSerializer.Deserialize<LibraryConfig>(configDocument.Content)
                ?? throw new InvalidDataException("数据库配置无效。 ");
            if (config.SchemaVersion is < 1 or > 2)
                throw new InvalidDataException("数据库配置版本不受当前程序支持。 ");
            var index = await PostgresLibraryIndexStore.ReadAsync(connection, cancellationToken);
            var metadata = await PostgresLibraryMetadataStore.ReadAsync(connection,
                cancellationToken);
            var queue = await PostgresScrapeQueueStore.LoadAsync(connection, cancellationToken);
            var reviews = await PostgresReviewTransactionStore.LoadAsync(connection,
                cancellationToken);
            if (reviews.InvalidLines != 0)
                throw new InvalidDataException("数据库审核历史包含无效记录。 ");
            var roles = await PostgresRoleProfileStore.LoadAsync(connection, cancellationToken);
            var counts = await PostgresLibraryMigration.VerifyAsync(connection,
                cancellationToken);
            if (counts.VideoCount != index.Index.Entries.Count ||
                counts.MetadataCount != metadata.Records.Count)
                throw new InvalidDataException("数据库内容计数不一致。 ");
            return new(cluster, config, index, metadata, queue, reviews, roles);
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        writeGate.Dispose();
        await cluster.DisposeAsync();
    }
}
