using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

// Database-mode review writer. The file-mode journal remains authoritative
// until the whole application switches storage modes.
internal static class PostgresReviewTransactionStore
{
    internal static async Task CommitAsync(string connectionString, ReviewTransaction review,
        CancellationToken cancellationToken = default)
    {
        if (review.SchemaVersion != ReviewTransactionStore.SchemaVersion ||
            review.Before.Count == 0 ||
            !review.Before.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(review.After.Keys))
            throw new InvalidDataException("审核事务缺少完整的前后状态。");

        var configDocument = await PostgresLibraryDocumentStore.ReadAsync(
            connectionString, "config.json", cancellationToken)
            ?? throw new InvalidDataException("数据库缺少配置，审核未提交。");
        var historyDocument = await PostgresLibraryDocumentStore.ReadAsync(
            connectionString, "review-history.jsonl", cancellationToken);
        var metadataSnapshot = await PostgresLibraryMetadataStore.ReadAsync(connectionString, cancellationToken);
        var config = JsonSerializer.Deserialize<LibraryConfig>(configDocument.Content)
            ?? throw new InvalidDataException("数据库配置无效，审核未提交。");
        if (config.AiReviews is null || config.Tags is null || config.AutoAmateurTags is null)
            throw new InvalidDataException("数据库审核配置不完整，审核未提交。");
        var metadata = metadataSnapshot.Records;
        var reviews = new Dictionary<string, AiReviewRecord>(config.AiReviews, StringComparer.OrdinalIgnoreCase);
        var tags = new Dictionary<string, List<string>>(config.Tags, StringComparer.OrdinalIgnoreCase);
        var autoTags = config.AutoAmateurTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, before) in review.Before)
        {
            var current = new ReviewItemSnapshot
            {
                Review = reviews.GetValueOrDefault(id),
                Metadata = metadata.GetValueOrDefault(id),
                Tags = tags.GetValueOrDefault(id),
                AutoAmateur = autoTags.Contains(id)
            };
            if (!ReviewTransactionStore.Equal(current, before))
                throw new InvalidOperationException("审核预览已过期，数据库未修改：" + id);
        }

        foreach (var (id, after) in review.After)
        {
            if (after.Review is null) reviews.Remove(id);
            else reviews[id] = after.Review;
            if (after.Metadata is null) metadata.Remove(id);
            else metadata[id] = after.Metadata;
            if (after.Tags is not { Count: > 0 }) tags.Remove(id);
            else tags[id] = after.Tags.ToList();
            if (after.AutoAmateur) autoTags.Add(id);
            else autoTags.Remove(id);
        }
        config.AiReviews = reviews;
        config.Tags = tags;
        config.AutoAmateurTags = autoTags.ToList();
        // Preserve settings introduced by newer versions of the client.
        var configJson = JsonNode.Parse(configDocument.Content) as JsonObject
            ?? throw new InvalidDataException("数据库配置根对象无效，审核未提交。");
        configJson["aiReviews"] = JsonSerializer.SerializeToNode(config.AiReviews);
        configJson["tags"] = JsonSerializer.SerializeToNode(config.Tags);
        configJson["autoAmateurTags"] = JsonSerializer.SerializeToNode(config.AutoAmateurTags);

        var history = historyDocument is null ? "" : Encoding.UTF8.GetString(historyDocument.Content);
        if (history.Length > 0 && history[^1] != '\n')
            throw new InvalidDataException("审核历史末尾不完整，数据库未修改。");
        var historyBytes = Encoding.UTF8.GetBytes(history + JsonSerializer.Serialize(review) + "\n");
        await PostgresLibraryAtomicStateStore.CommitAsync(connectionString,
            metadata, metadataSnapshot.Revision,
            [new("config.json", JsonSerializer.SerializeToUtf8Bytes(configJson), configDocument.Revision),
             new("review-history.jsonl", historyBytes, historyDocument?.Revision)],
            cancellationToken);
    }
}
