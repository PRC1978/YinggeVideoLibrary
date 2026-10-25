using System.IO;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class ReviewHistoryExport
{
    public static void Write(string path, IReadOnlyList<ReviewTransaction> transactions,
        int invalidLinesSkipped, bool json)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("导出目录无效。");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                if (json)
                {
                    JsonSerializer.Serialize(stream, new
                    {
                        Format = "YinggeReviewHistory",
                        ExportVersion = 1,
                        ExportedAtUtc = DateTime.UtcNow,
                        InvalidLinesSkipped = invalidLinesSkipped,
                        TransactionCount = transactions.Count,
                        Transactions = transactions
                    }, new JsonSerializerOptions { WriteIndented = true });
                }
                else
                {
                    using var writer = new StreamWriter(stream, new UTF8Encoding(true), 64 * 1024, leaveOpen: true);
                    writer.WriteLine("时间,操作,视频ID,是否撤销动作,操作前状态,操作后状态,操作前可信度,操作后可信度,操作前来源,操作后来源,操作前候选,操作后候选,操作前审核记录JSON,操作后审核记录JSON,操作前视频信息JSON,操作前标签JSON,操作前自动素人标记,事务ID,记录版本,撤销事务ID,操作后视频信息JSON,操作后标签JSON,操作后自动素人标记,前后快照完整");
                    foreach (var entry in transactions)
                    {
                        var ids = entry.Before.Keys.Concat(entry.After.Keys)
                            .Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty("");
                        foreach (var id in ids)
                        {
                            entry.Before.TryGetValue(id, out var before);
                            entry.After.TryGetValue(id, out var after);
                            writer.WriteLine(string.Join(",", new object?[] {
                                entry.TimeUtc.ToLocalTime().ToString("O"), entry.Action, id, entry.Action == "undo",
                                before?.Review?.Status, after?.Review?.Status,
                                before?.Review?.Confidence, after?.Review?.Confidence,
                                before?.Review?.CandidateSource, after?.Review?.CandidateSource,
                                before?.Review?.CandidateTitle, after?.Review?.CandidateTitle,
                                before?.Review is null ? "" : JsonSerializer.Serialize(before.Review),
                                after?.Review is null ? "" : JsonSerializer.Serialize(after.Review),
                                before?.Metadata is null ? "" : JsonSerializer.Serialize(before.Metadata),
                                before?.Tags is null ? "" : JsonSerializer.Serialize(before.Tags),
                                entry.Complete ? before?.AutoAmateur : "",
                                entry.Id, entry.SchemaVersion, entry.UndoesTransactionId,
                                after?.Metadata is null ? "" : JsonSerializer.Serialize(after.Metadata),
                                after?.Tags is null ? "" : JsonSerializer.Serialize(after.Tags),
                                entry.Complete ? after?.AutoAmateur : "", entry.Complete
                            }.Select(ReviewCsv.Cell)));
                        }
                    }
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool VerifyForDiagnostics()
    {
        var json = Path.GetTempFileName();
        var csv = Path.GetTempFileName();
        try
        {
            var old = new ReviewItemSnapshot { Review = new AiReviewRecord
                { VideoId = "v1", Status = "pending", CandidateSource = "JavDB", CandidateTitle = "=中文\n标题" } };
            var next = old.Clone();
            next.Review!.Status = "approved";
            var record = ReviewTransactionStore.Create("field-approve",
                new Dictionary<string, ReviewItemSnapshot> { ["v1"] = old },
                new Dictionary<string, ReviewItemSnapshot> { ["v1"] = next });
            Write(json, [record], 1, json: true);
            Write(csv, [record], 1, json: false);
            using var parsed = JsonDocument.Parse(File.ReadAllText(json));
            var root = parsed.RootElement;
            var transactions = root.GetProperty("Transactions");
            var csvText = File.ReadAllText(csv);
            return root.GetProperty("Format").GetString() == "YinggeReviewHistory" &&
                root.GetProperty("TransactionCount").GetInt32() == 1 &&
                root.GetProperty("InvalidLinesSkipped").GetInt32() == 1 &&
                transactions.GetArrayLength() == 1 &&
                transactions[0].GetProperty("Id").GetString() == record.Id &&
                transactions[0].GetProperty("Before").GetProperty("v1")
                    .GetProperty("Review").GetProperty("CandidateTitle").GetString() == "=中文\n标题" &&
                csvText.Contains("'=中文", StringComparison.Ordinal) &&
                csvText.Contains(record.Id, StringComparison.Ordinal);
        }
        finally { File.Delete(json); File.Delete(csv); }
    }
}
