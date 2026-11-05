using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

// Shares the file-mode three-way merge rules while PostgreSQL remains an
// isolated storage path. The normal window must not call this alone: index,
// metadata, review, queue and backup must switch together.
internal static class PostgresLibraryConfigStore
{
    internal static async Task<LibraryConfig> SaveAsync(string connectionString,
        LibraryConfig baseline, LibraryConfig current,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = await PostgresLibraryDocumentStore.ReadAsync(
                connectionString, "config.json", cancellationToken)
                ?? throw new InvalidDataException("数据库缺少配置，未保存。 ");
            var latest = JsonSerializer.Deserialize<LibraryConfig>(snapshot.Content)
                ?? throw new InvalidDataException("数据库配置无效，未保存。 ");
            if (latest.SchemaVersion is < 1 or > 2 || current.SchemaVersion is < 1 or > 2)
                throw new InvalidDataException("配置格式版本不受当前程序支持，未保存。 ");
            if (!AiSuggestionAudit.IsHistoryWellFormed(latest) ||
                !AiSuggestionAudit.IsHistoryWellFormed(current) ||
                !AiSuggestionAudit.IsHistoryWellFormed(baseline) ||
                !SiteListLocalActionStore.IsHistoryWellFormed(latest) ||
                !SiteListLocalActionStore.IsHistoryWellFormed(current) ||
                !SiteListLocalActionStore.IsHistoryWellFormed(baseline))
                throw new InvalidDataException("审核或片单历史无效，数据库配置未保存。 ");
            var merged = MainWindow.MergeLibraryConfig(baseline, current, latest);
            if (!SiteListLocalActionStore.IsHistoryWellFormed(merged))
                throw new InvalidDataException("合并后的片单历史无效，数据库配置未保存。 ");
            merged.SchemaVersion = 2;
            var persisted = MainWindow.CloneLibraryConfig(merged);
            persisted.TmdbToken = "";
            persisted.AiKey = "";
            persisted.JavinfoKey = "";
            // Preserve settings added by a newer client, as the review writer
            // does. Known fields come from the validated three-way merge.
            var document = JsonNode.Parse(snapshot.Content) as JsonObject
                ?? throw new InvalidDataException("数据库配置根对象无效，未保存。 ");
            var known = JsonSerializer.SerializeToNode(persisted) as JsonObject
                ?? throw new InvalidDataException("数据库配置无法序列化，未保存。 ");
            foreach (var (name, value) in known)
                document[name] = value?.DeepClone();
            try
            {
                await PostgresLibraryDocumentStore.SaveAsync(connectionString,
                    "config.json", JsonSerializer.SerializeToUtf8Bytes(document),
                    snapshot.Revision, cancellationToken);
                return merged;
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
                // Another writer won the compare-and-swap. Re-read and merge
                // against its committed state rather than losing either edit.
            }
        }
        throw new InvalidOperationException("配置在保存期间持续变化，请重试。 ");
    }
}
