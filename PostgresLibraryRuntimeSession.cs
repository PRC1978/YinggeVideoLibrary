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
    internal LibraryConfig Config { get; }
    internal PostgresLibraryIndexStore.Snapshot Index { get; }
    internal PostgresLibraryMetadataStore.Snapshot Metadata { get; }
    internal IReadOnlyList<ScrapeQueueItem> Queue { get; }
    internal PostgresReviewTransactionStore.HistorySnapshot ReviewHistory { get; }
    internal PostgresRoleProfileStore.Snapshot Roles { get; }

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

    public ValueTask DisposeAsync() => cluster.DisposeAsync();
}
