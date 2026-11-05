using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Typed document adapters for isolated database-mode tests. Callers that also
// change config or work metadata must use the shared atomic-state transaction
// instead of saving one document at a time.
internal static class PostgresUserStateStore
{
    internal sealed record Snapshot<T>(T Value, string? Revision);

    internal static async Task<Snapshot<Dictionary<string, PersonalRatingEntry>>> LoadRatingsAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var saved = await PostgresLibraryDocumentStore.ReadAsync(
            connectionString, "personal-ratings.json", cancellationToken);
        if (saved is null) return new(new(StringComparer.OrdinalIgnoreCase), null);
        if (saved.Content.Length > 32 * 1024 * 1024) throw new InvalidDataException("个人评分超过允许大小。");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, PersonalRatingEntry>>(saved.Content)
            ?? throw new InvalidDataException("个人评分格式无效。");
        if (parsed.Count > 1_000_000 || parsed.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
            pair.Value is null || !double.IsFinite(pair.Value.Score) ||
            pair.Value.Score is < 0 or > 10))
            throw new InvalidDataException("个人评分内容无效。");
        return new(new(parsed, StringComparer.OrdinalIgnoreCase), saved.Revision);
    }

    internal static async Task<Snapshot<Dictionary<string, PersonalRatingEntry>>> SetRatingAsync(
        string connectionString, string videoId, double? score, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId) || videoId.Length > 160)
            throw new ArgumentException("视频 ID 无效。", nameof(videoId));
        if (score is double value && (!double.IsFinite(value) || value is < 0 or > 10))
            throw new ArgumentOutOfRangeException(nameof(score));
        var current = await LoadRatingsAsync(connectionString, cancellationToken);
        var next = new Dictionary<string, PersonalRatingEntry>(current.Value, StringComparer.OrdinalIgnoreCase);
        if (score is double selected)
            next[videoId] = new PersonalRatingEntry { Score = selected, UpdatedAtUtc = DateTime.UtcNow };
        else next.Remove(videoId);
        var revision = await PostgresLibraryDocumentStore.SaveAsync(connectionString,
            "personal-ratings.json", JsonSerializer.SerializeToUtf8Bytes(next),
            current.Revision, cancellationToken);
        return new(next, revision);
    }

    internal static async Task<Snapshot<IReadOnlyList<ReviewSavedView>>> LoadReviewViewsAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var saved = await PostgresLibraryDocumentStore.ReadAsync(
            connectionString, "review-views.json", cancellationToken);
        return saved is null ? new([], null) :
            new(ReviewSavedViewStore.ParseDatabaseDocument(saved.Content), saved.Revision);
    }

    internal static async Task<Snapshot<IReadOnlyList<ReviewSavedView>>> SaveReviewViewsAsync(
        string connectionString, Snapshot<IReadOnlyList<ReviewSavedView>> baseline,
        IEnumerable<ReviewSavedView> views, CancellationToken cancellationToken = default)
    {
        var bytes = ReviewSavedViewStore.SerializeDatabaseDocument(views);
        var revision = await PostgresLibraryDocumentStore.SaveAsync(connectionString,
            "review-views.json", bytes, baseline.Revision, cancellationToken);
        return new(ReviewSavedViewStore.ParseDatabaseDocument(bytes), revision);
    }

    internal static async Task<Snapshot<IReadOnlyList<CategoryGraphHistoryEntry>>> LoadGraphMovesAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var saved = await PostgresLibraryDocumentStore.ReadAsync(
            connectionString, "graph-move-history.json", cancellationToken);
        return saved is null ? new([], null) :
            new(CategoryGraphMoveHistoryStore.ParseDatabaseDocument(saved.Content), saved.Revision);
    }

    internal static async Task<Snapshot<IReadOnlyList<CategoryGraphHistoryEntry>>> SaveGraphMovesAsync(
        string connectionString, Snapshot<IReadOnlyList<CategoryGraphHistoryEntry>> baseline,
        IEnumerable<CategoryGraphHistoryEntry> history, CancellationToken cancellationToken = default)
    {
        var bytes = CategoryGraphMoveHistoryStore.SerializeDatabaseDocument(history);
        var revision = await PostgresLibraryDocumentStore.SaveAsync(connectionString,
            "graph-move-history.json", bytes, baseline.Revision, cancellationToken);
        return new(CategoryGraphMoveHistoryStore.ParseDatabaseDocument(bytes), revision);
    }

    internal static async Task<Snapshot<PluginState>> LoadPluginStateAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var saved = await PostgresLibraryDocumentStore.ReadAsync(
            connectionString, PluginStateStore.FileName, cancellationToken);
        if (saved is null) return new(new PluginState(), null);
        if (saved.Content.Length > 4 * 1024 * 1024) throw new InvalidDataException("插件状态超过允许大小。");
        var state = JsonSerializer.Deserialize<PluginState>(saved.Content)
            ?? throw new InvalidDataException("插件状态格式无效。");
        ValidatePluginState(state);
        return new(state, saved.Revision);
    }

    internal static async Task<Snapshot<PluginState>> SavePluginStateAsync(
        string connectionString, Snapshot<PluginState> baseline,
        PluginState edited, CancellationToken cancellationToken = default)
    {
        ValidatePluginState(edited);
        var clone = PluginStateStore.Clone(edited);
        clone.SchemaVersion = 1;
        var revision = await PostgresLibraryDocumentStore.SaveAsync(connectionString,
            PluginStateStore.FileName, JsonSerializer.SerializeToUtf8Bytes(clone),
            baseline.Revision, cancellationToken);
        return new(clone, revision);
    }

    private static void ValidatePluginState(PluginState state)
    {
        if (state.SchemaVersion != 1 || state.Enabled is null || state.Consents is null ||
            state.LastErrors is null || state.Enabled.Count > 1000 ||
            state.Consents.Count > 1000 || state.LastErrors.Count > 1000 ||
            state.Enabled.Any(string.IsNullOrWhiteSpace) ||
            state.Consents.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) ||
            state.LastErrors.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Value is null || pair.Value.Message is null || pair.Value.Message.Length > 600))
            throw new InvalidDataException("插件状态无效，数据库未保存。");
    }
}
