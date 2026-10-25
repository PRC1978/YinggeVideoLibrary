using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// A snapshot includes every field that an approval or scrape can change.
// Null means that the corresponding dictionary had no entry, including for Tags.
internal sealed class ReviewItemSnapshot
{
    public AiReviewRecord? Review { get; set; }
    public MovieMetadata? Metadata { get; set; }
    public List<string>? Tags { get; set; }
    public bool AutoAmateur { get; set; }

    public ReviewItemSnapshot Clone() => new()
    {
        Review = Review is null ? null : JsonSerializer.Deserialize<AiReviewRecord>(JsonSerializer.Serialize(Review)),
        Metadata = Metadata?.Clone(),
        Tags = Tags?.ToList(),
        AutoAmateur = AutoAmateur
    };
}

internal sealed class ReviewTransaction
{
    public int SchemaVersion { get; set; } = ReviewTransactionStore.SchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = "";
    public string? UndoesTransactionId { get; set; }
    public bool Complete { get; set; } = true;
    public Dictionary<string, ReviewItemSnapshot> Before { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ReviewItemSnapshot> After { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Sha256 { get; set; } = "";
}

// The original JSONL is retained, so existing users' audit records remain
// visible/exportable. They lacked complete after snapshots and cannot be
// safely used for a multi-step undo.
internal sealed class LegacyReviewHistoryEntry
{
    public DateTime Time { get; set; }
    public string Action { get; set; } = "";
    public Dictionary<string, AiReviewRecord> Before { get; set; } = [];
    public Dictionary<string, AiReviewRecord> After { get; set; } = [];
    public Dictionary<string, MovieMetadata?> MetadataBefore { get; set; } = [];
    public Dictionary<string, List<string>> TagsBefore { get; set; } = [];
    public Dictionary<string, bool> AutoAmateurBefore { get; set; } = [];
    public bool Undone { get; set; }
}

internal static class ReviewCsv
{
    // Spreadsheet programs can evaluate a quoted CSV cell as a formula.
    // Keep numeric values numeric, but force every user/source supplied text
    // beginning with a formula marker to be interpreted as literal text.
    public static string Cell(object? value)
    {
        var text = value?.ToString() ?? "";
        if (value is string && LooksLikeFormula(text)) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    static bool LooksLikeFormula(string text)
    {
        // Treat each physical line as a possible spreadsheet cell. Leading
        // whitespace and invisible format marks must not bypass the guard.
        foreach (var line in text.Split(['\r', '\n']))
        {
            var index = 0;
            while (index < line.Length && (char.IsWhiteSpace(line[index]) ||
                char.GetUnicodeCategory(line[index]) == System.Globalization.UnicodeCategory.Format)) index++;
            if (index < line.Length && line[index] is '=' or '+' or '-' or '@') return true;
        }
        return text.Length > 0 && text[0] is '\t' or '\r' or '\n';
    }

    public static bool VerifyForDiagnostics() =>
        Cell("=HYPERLINK(\"https://example.invalid\")") == "\"'=HYPERLINK(\"\"https://example.invalid\"\")\"" &&
        Cell(" \t@SUM(1)") == "\"' \t@SUM(1)\"" &&
        Cell("\uFEFF-CMD") == "\"'\uFEFF-CMD\"" &&
        Cell("说明\n+1") == "\"'说明\n+1\"" &&
        Cell("普通\"标题") == "\"普通\"\"标题\"" &&
        Cell(42) == "\"42\"";
}

internal static partial class ReviewTransactionStore
{
    public const int SchemaVersion = 2;
    const string FileName = "review-history.jsonl";
    static readonly JsonSerializerOptions Options = new();

    public static ReviewTransaction Create(string action,
        IReadOnlyDictionary<string, ReviewItemSnapshot> before,
        IReadOnlyDictionary<string, ReviewItemSnapshot> after,
        bool complete = true,
        string? undoesTransactionId = null)
    {
        var record = new ReviewTransaction
        {
            Action = action,
            Complete = complete,
            UndoesTransactionId = undoesTransactionId,
            Before = before.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase),
            After = after.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase)
        };
        record.Sha256 = Digest(record);
        return record;
    }

    public static void Append(string dataDir, ReviewTransaction record)
    {
        if (record.SchemaVersion != SchemaVersion || record.Before.Count == 0 ||
            !record.Before.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(record.After.Keys))
            throw new InvalidDataException("审核事务缺少完整的前后状态。");
        record.Sha256 = Digest(record);
        Directory.CreateDirectory(dataDir);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Options) + "\n");
        var path = Path.Combine(dataDir, FileName);
        // One writer at a time. A torn final line is separated from the next
        // record, so a later valid transaction remains readable after a crash.
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.Read, 4096, FileOptions.WriteThrough);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n') stream.WriteByte((byte)'\n');
        }
        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public static List<ReviewTransaction> Load(string dataDir, out int invalidLines)
    {
        invalidLines = 0;
        var records = new List<ReviewTransaction>();
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) return records;
        // A review may finish while the background loader is reading a large
        // history. Sharing writes keeps the UI action responsive; a partial
        // line is skipped and the next reload observes the completed record.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("SchemaVersion", out var version) && version.GetInt32() == SchemaVersion)
                {
                    var record = JsonSerializer.Deserialize<ReviewTransaction>(line, Options);
                    if (record is null || record.Before is null || record.After is null ||
                        string.IsNullOrWhiteSpace(record.Id) || record.Before.Count == 0 ||
                        !record.Before.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(record.After.Keys) ||
                        !string.Equals(record.Sha256, Digest(record), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("审核事务的校验值或快照无效。");
                    records.Add(record);
                }
                else if (!root.TryGetProperty("SchemaVersion", out _))
                {
                    var legacy = JsonSerializer.Deserialize<LegacyReviewHistoryEntry>(line, Options)
                        ?? throw new InvalidDataException("旧审核历史无效。");
                    var before = new Dictionary<string, ReviewItemSnapshot>(StringComparer.OrdinalIgnoreCase);
                    var after = new Dictionary<string, ReviewItemSnapshot>(StringComparer.OrdinalIgnoreCase);
                    var ids = legacy.Before.Keys.Concat(legacy.After.Keys).Concat(legacy.MetadataBefore.Keys)
                        .Concat(legacy.TagsBefore.Keys).Concat(legacy.AutoAmateurBefore.Keys)
                        .Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var id in ids)
                    {
                        legacy.Before.TryGetValue(id, out var oldReview);
                        legacy.After.TryGetValue(id, out var newReview);
                        legacy.MetadataBefore.TryGetValue(id, out var oldMetadata);
                        legacy.TagsBefore.TryGetValue(id, out var oldTags);
                        before[id] = new ReviewItemSnapshot
                        {
                            Review = oldReview, Metadata = oldMetadata, Tags = oldTags,
                            AutoAmateur = legacy.AutoAmateurBefore.GetValueOrDefault(id)
                        };
                        after[id] = new ReviewItemSnapshot { Review = newReview };
                    }
                    records.Add(new ReviewTransaction
                    {
                        SchemaVersion = 1,
                        Id = "legacy-" + records.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        TimeUtc = legacy.Time.Kind == DateTimeKind.Unspecified
                            ? DateTime.SpecifyKind(legacy.Time, DateTimeKind.Local).ToUniversalTime()
                            : legacy.Time.ToUniversalTime(),
                        Action = legacy.Action,
                        Complete = false,
                        Before = before,
                        After = after
                    });
                }
                else invalidLines++;
            }
            catch { invalidLines++; }
        }
        return records;
    }

    public static ReviewTransaction? LastUndoable(IReadOnlyList<ReviewTransaction> records)
    {
        var undone = records.Where(record => record.Action == "undo" && record.UndoesTransactionId is not null)
            .Select(record => record.UndoesTransactionId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = records.Count - 1; index >= 0; index--)
        {
            var record = records[index];
            if (record.SchemaVersion == SchemaVersion && record.Complete && record.Action != "undo" &&
                record.Before.Count > 0 && !undone.Contains(record.Id)) return record;
        }
        return null;
    }

    public static bool Equal(ReviewItemSnapshot left, ReviewItemSnapshot right)
    {
        // An absent tag key and an empty tag list are the same user-visible
        // state; older call sites represented both as an empty list.
        var leftTags = left.Tags is { Count: > 0 } ? left.Tags : null;
        var rightTags = right.Tags is { Count: > 0 } ? right.Tags : null;
        return left.AutoAmateur == right.AutoAmateur &&
            JsonSerializer.Serialize(left.Review, Options) == JsonSerializer.Serialize(right.Review, Options) &&
            JsonSerializer.Serialize(left.Metadata, Options) == JsonSerializer.Serialize(right.Metadata, Options) &&
            JsonSerializer.Serialize(leftTags, Options) == JsonSerializer.Serialize(rightTags, Options);
    }

    static string Digest(ReviewTransaction record)
    {
        var original = record.Sha256;
        try
        {
            record.Sha256 = "";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Options))));
        }
        finally { record.Sha256 = original; }
    }

    public static bool VerifyForDiagnostics(string dataDir)
    {
        if (!ReviewCsv.VerifyForDiagnostics()) return false;
        var first = new ReviewItemSnapshot();
        var candidate = new ReviewItemSnapshot
        {
            Review = new AiReviewRecord { VideoId = "one", Status = "pending", CandidateTitle = "候选" },
            Tags = ["标签"], AutoAmateur = true
        };
        var approved = candidate.Clone();
        approved.Review!.Status = "approved";
        approved.Metadata = new MovieMetadata { Title = "候选", DetailFetched = true };
        var a = Create("candidate", new Dictionary<string, ReviewItemSnapshot> { ["one"] = first },
            new Dictionary<string, ReviewItemSnapshot> { ["one"] = candidate });
        var b = Create("approve", new Dictionary<string, ReviewItemSnapshot> { ["one"] = candidate },
            new Dictionary<string, ReviewItemSnapshot> { ["one"] = approved });
        Append(dataDir, a);
        Append(dataDir, b);
        var reloaded = Load(dataDir, out var invalid);
        if (invalid != 0 || reloaded.Count != 2 || LastUndoable(reloaded)?.Id != b.Id ||
            !Equal(reloaded[1].After["one"], approved)) return false;
        var conflict = approved.Clone();
        conflict.Tags!.Add("人工修改");
        if (Equal(conflict, reloaded[1].After["one"])) return false;
        var undo = Create("undo", new Dictionary<string, ReviewItemSnapshot> { ["one"] = approved },
            new Dictionary<string, ReviewItemSnapshot> { ["one"] = candidate }, undoesTransactionId: b.Id);
        Append(dataDir, undo);
        reloaded = Load(dataDir, out invalid);
        if (invalid != 0 || LastUndoable(reloaded)?.Id != a.Id) return false;
        File.AppendAllText(Path.Combine(dataDir, FileName), "{\"broken\":", Encoding.UTF8);
        reloaded = Load(dataDir, out invalid);
        if (invalid != 1 || reloaded.Count != 3 || LastUndoable(reloaded)?.Id != a.Id) return false;
        var later = Create("candidate", new Dictionary<string, ReviewItemSnapshot> { ["two"] = first },
            new Dictionary<string, ReviewItemSnapshot> { ["two"] = candidate });
        Append(dataDir, later);
        reloaded = Load(dataDir, out invalid);
        if (invalid != 1 || reloaded.Count != 4 || LastUndoable(reloaded)?.Id != later.Id) return false;
        File.AppendAllText(Path.Combine(dataDir, FileName), "{\"SchemaVersion\":2,\"Id\":\"invalid\"}" + Environment.NewLine, Encoding.UTF8);
        var afterMiddle = Create("candidate", new Dictionary<string, ReviewItemSnapshot> { ["three"] = first },
            new Dictionary<string, ReviewItemSnapshot> { ["three"] = candidate });
        Append(dataDir, afterMiddle);
        reloaded = Load(dataDir, out invalid);
        if (invalid != 2 || reloaded.Count != 5 || LastUndoable(reloaded)?.Id != afterMiddle.Id) return false;
        Append(dataDir, Create("undo", afterMiddle.After, afterMiddle.Before,
            undoesTransactionId: afterMiddle.Id));
        reloaded = Load(dataDir, out invalid);
        if (invalid != 2 || reloaded.Count != 6 || LastUndoable(reloaded)?.Id != later.Id) return false;
        File.AppendAllText(Path.Combine(dataDir, FileName), "{\"unfinished\":", Encoding.UTF8);
        reloaded = Load(dataDir, out invalid);
        if (invalid != 3 || reloaded.Count != 6 || LastUndoable(reloaded)?.Id != later.Id) return false;
        Append(dataDir, Create("undo", later.After, later.Before, undoesTransactionId: later.Id));
        reloaded = Load(dataDir, out invalid);
        if (invalid != 3 || reloaded.Count != 7 || LastUndoable(reloaded)?.Id != a.Id) return false;
        var accepted = new Dictionary<string, MovieMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = new MovieMetadata { Title = "accepted" },
            ["unrelated"] = new MovieMetadata { Title = "keep" }
        };
        MetadataStore.Save(dataDir, accepted);
        var desired = new Dictionary<string, MovieMetadata>(accepted, StringComparer.OrdinalIgnoreCase);
        desired.Remove("one");
        MetadataStore.SaveReviewUndo(dataDir, desired,
            new Dictionary<string, MovieMetadata?> { ["one"] = accepted["one"] },
            new Dictionary<string, MovieMetadata?> { ["one"] = null });
        var saved = MetadataStore.Load(dataDir);
        if (saved.ContainsKey("one") || saved.GetValueOrDefault("unrelated")?.Title != "keep") return false;
        try
        {
            MetadataStore.SaveReviewUndo(dataDir, desired,
                new Dictionary<string, MovieMetadata?> { ["one"] = accepted["one"] },
                new Dictionary<string, MovieMetadata?> { ["one"] = new MovieMetadata { Title = "stale" } });
            return false;
        }
        catch (InvalidOperationException) { return !MetadataStore.Load(dataDir).ContainsKey("one"); }
    }
}
