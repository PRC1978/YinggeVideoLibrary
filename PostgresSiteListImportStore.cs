namespace YinggeVideoLibrary;

// The import matcher and audit rules are shared with file mode. PostgreSQL
// remains the only durable store; no site-list JSON is written beside the DB.
internal static class PostgresSiteListImportStore
{
    private const string Name = "site-list-import.json";

    private static async Task<(SiteListImportStore.State State, string? Revision)> ReadAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var document = await PostgresLibraryDocumentStore.ReadAsync(connectionString, Name,
            cancellationToken);
        return (SiteListImportStore.ParseDatabaseDocument(document?.Content), document?.Revision);
    }

    internal static async Task<SiteListImportStore.State> LoadAsync(string connectionString,
        CancellationToken cancellationToken = default) =>
        (await ReadAsync(connectionString, cancellationToken)).State;

    internal static async Task<SiteListImportStore.Preview> CreatePreviewAsync(
        string connectionString, string exportPath,
        IReadOnlyList<SiteListImportStore.LocalVideo> library,
        CancellationToken cancellationToken = default,
        IProgress<SiteListImportStore.HashProgress>? progress = null)
    {
        var baseline = await ReadAsync(connectionString, cancellationToken);
        return await Task.Run(() => SiteListImportStore.CreatePreviewFromState(
            baseline.State, exportPath, library, cancellationToken, progress), cancellationToken);
    }

    internal static async Task<SiteListImportStore.AuditAction> CommitAsync(
        string connectionString, string dataDirectory, SiteListImportStore.Preview preview,
        IReadOnlyList<SiteListImportStore.LocalVideo> library,
        CancellationToken cancellationToken = default,
        IProgress<SiteListImportStore.HashProgress>? progress = null)
    {
        var baseline = await ReadAsync(connectionString, cancellationToken);
        return await Task.Run(() => SiteListImportStore.Commit(dataDirectory, preview,
            library, cancellationToken, progress, baseline.State,
            state => PostgresLibraryDocumentStore.SaveAsync(connectionString, Name,
                SiteListImportStore.SerializeDatabaseDocument(state), baseline.Revision,
                cancellationToken).GetAwaiter().GetResult()), cancellationToken);
    }

    internal static async Task<SiteListImportStore.AuditAction> UndoAsync(
        string connectionString, string dataDirectory, string batchId,
        CancellationToken cancellationToken = default)
    {
        var baseline = await ReadAsync(connectionString, cancellationToken);
        return await Task.Run(() => SiteListImportStore.Undo(dataDirectory, batchId,
            baseline.State,
            state => PostgresLibraryDocumentStore.SaveAsync(connectionString, Name,
                SiteListImportStore.SerializeDatabaseDocument(state), baseline.Revision,
                cancellationToken).GetAwaiter().GetResult()), cancellationToken);
    }
}
