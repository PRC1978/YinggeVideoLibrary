using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

internal static partial class ReviewTransactionStore
{
    // Rewriting IDs in a sealed transaction without resealing it would make
    // the history invisible to the normal loader and break undo. Only a
    // verified offline snapshot is passed here; invalid lines fail closed.
    internal static void RelocateHistoryFile(string path, Func<JsonNode, JsonNode> mapper)
    {
        if (!File.Exists(path)) return;
        var dataDir = Path.GetDirectoryName(path)!;
        _ = Load(dataDir, out var invalid);
        if (invalid > 0) throw new InvalidDataException("审核历史含无效记录，迁移已停止以保护原始历史。");
        var output = new StringBuilder();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var root = JsonNode.Parse(line)
                ?? throw new InvalidDataException("审核历史含空记录。");
            var mapped = mapper(root);
            if (mapped is not JsonObject document)
                throw new InvalidDataException("审核历史结构无效。");
            if (document["SchemaVersion"] is null)
            {
                _ = document.Deserialize<LegacyReviewHistoryEntry>(Options)
                    ?? throw new InvalidDataException("旧审核记录无效。");
            }
            else
            {
                var transaction = document.Deserialize<ReviewTransaction>(Options)
                    ?? throw new InvalidDataException("审核事务无效。");
                if (transaction.SchemaVersion != SchemaVersion || transaction.Before.Count == 0
                    || !transaction.Before.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(transaction.After.Keys))
                    throw new InvalidDataException("审核事务迁移后前后状态不一致。");
                transaction.Sha256 = Digest(transaction);
                document = JsonSerializer.SerializeToNode(transaction, Options) as JsonObject
                    ?? throw new InvalidDataException("审核事务无法重新校验。");
            }
            output.AppendLine(document.ToJsonString(Options));
        }
        File.WriteAllText(path, output.ToString(), Encoding.UTF8);
        _ = Load(dataDir, out invalid);
        if (invalid != 0) throw new InvalidDataException("迁移后的审核历史校验失败。");
    }
}
