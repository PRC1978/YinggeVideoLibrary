using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

internal static partial class LibraryBackupService
{
    static ImportPlan BuildImportPlan(string dataDir, string backupDirectory,
        LibraryImportScope scopes, LibraryImportConflictPolicy policy)
    {
        var plan = new ImportPlan();
        JsonObject? currentConfig = null, backupConfig = null, mergedConfig = null;
        if ((scopes & (LibraryImportScope.Categories | LibraryImportScope.Tags | LibraryImportScope.Reviews)) != 0)
        {
            currentConfig = ReadImportObject(Path.Combine(dataDir, "config.json"));
            backupConfig = ReadImportObject(Path.Combine(backupDirectory, "config.json"));
            ValidateImportConfig(currentConfig, "当前配置");
            ValidateImportConfig(backupConfig, "备份配置");
            mergedConfig = (JsonObject)currentConfig.DeepClone();
        }
        if (scopes.HasFlag(LibraryImportScope.Metadata))
        {
            var current = ReadImportObject(Path.Combine(dataDir, "metadata.json"));
            var backed = ReadImportObject(Path.Combine(backupDirectory, "metadata.json"));
            ValidateMetadataMap(current, "当前资料");
            ValidateMetadataMap(backed, "备份资料");
            var merged = (JsonObject)current.DeepClone();
            var summary = MergeMap(merged, backed, LibraryImportScope.Metadata, "资料", policy);
            plan.Sections.Add(summary);
            if (!JsonNode.DeepEquals(current, merged))
                plan.FileContents["metadata.json"] = merged.ToJsonString(JsonOptions);
        }
        if (scopes.HasFlag(LibraryImportScope.Categories))
        {
            var summary = new ImportScopeSummary { Scope = LibraryImportScope.Categories, Label = "分类" };
            MergeConfigMap(mergedConfig!, backupConfig!, "categories", policy, summary);
            MergeConfigMap(mergedConfig!, backupConfig!, "multiCategories", policy, summary);
            MergeConfigMap(mergedConfig!, backupConfig!, "categoryRenames", policy, summary);
            MergeConfigList(mergedConfig!, backupConfig!, "categoryNames", summary);
            plan.Sections.Add(summary);
        }
        if (scopes.HasFlag(LibraryImportScope.Tags))
        {
            var summary = new ImportScopeSummary { Scope = LibraryImportScope.Tags, Label = "标签" };
            MergeConfigMap(mergedConfig!, backupConfig!, "tags", policy, summary, plan.ConflictDetails);
            MergeConfigList(mergedConfig!, backupConfig!, "autoAmateurTags", summary);
            plan.Sections.Add(summary);
        }
        if (scopes.HasFlag(LibraryImportScope.Ratings))
        {
            var current = ReadImportObject(Path.Combine(dataDir, "personal-ratings.json"));
            var backed = ReadImportObject(Path.Combine(backupDirectory, "personal-ratings.json"));
            ValidateRatingsMap(current, "当前个人评分");
            ValidateRatingsMap(backed, "备份个人评分");
            var merged = (JsonObject)current.DeepClone();
            var summary = MergeMap(merged, backed, LibraryImportScope.Ratings, "个人评分", policy,
                plan.ConflictDetails);
            plan.Sections.Add(summary);
            if (!JsonNode.DeepEquals(current, merged))
                plan.FileContents["personal-ratings.json"] = merged.ToJsonString(JsonOptions);
        }
        if (scopes.HasFlag(LibraryImportScope.Reviews))
        {
            var summary = new ImportScopeSummary { Scope = LibraryImportScope.Reviews, Label = "审核" };
            MergeConfigMap(mergedConfig!, backupConfig!, "aiReviews", policy, summary);
            MergeReviewHistory(dataDir, backupDirectory, policy, summary, plan);
            MergeReviewViews(dataDir, backupDirectory, policy, summary, plan);
            plan.Sections.Add(summary);
        }
        if (mergedConfig is not null && !JsonNode.DeepEquals(currentConfig, mergedConfig))
            plan.FileContents["config.json"] = mergedConfig.ToJsonString(JsonOptions);
        if (scopes.HasFlag(LibraryImportScope.Queue))
            MergeQueue(dataDir, backupDirectory, policy, plan);
        if (scopes.HasFlag(LibraryImportScope.SiteLists))
            MergeSiteLists(dataDir, backupDirectory, plan);
        if (scopes.HasFlag(LibraryImportScope.RoleProfiles))
            MergeRoleProfiles(dataDir, backupDirectory, policy, plan);
        return plan;
    }

    static JsonObject ReadImportObject(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("数据文件不是 JSON 对象：" + Path.GetFileName(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("数据文件无法读取：" + Path.GetFileName(path), ex);
        }
    }

    static void ValidateImportConfig(JsonObject config, string label)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<LibraryConfig>(config.ToJsonString());
            if (parsed?.Categories is null || parsed.MultiCategories is null || parsed.Tags is null
                || parsed.CategoryNames is null || parsed.CategoryRenames is null
                || parsed.AutoAmateurTags is null || parsed.AiReviews is null)
                throw new InvalidDataException(label + "缺少有效分类、标签或审核字段。");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException(label + "格式无法安全导入。", ex);
        }
    }

    static void ValidateMetadataMap(JsonObject map, string label)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, MovieMetadata>>(map.ToJsonString());
            if (parsed is null || parsed.Count != map.Count || parsed.Any(item =>
                    string.IsNullOrWhiteSpace(item.Key) || item.Value is null))
                throw new InvalidDataException(label + "含无效视频 ID 或资料。");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException(label + "格式无法安全导入。", ex);
        }
    }

    static void ValidateRatingsMap(JsonObject map, string label)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, PersonalRatingEntry>>(map.ToJsonString());
            if (parsed is null || parsed.Count != map.Count || parsed.Any(item =>
                    string.IsNullOrWhiteSpace(item.Key) || item.Value is null
                    || !double.IsFinite(item.Value.Score) || item.Value.Score is < 0 or > 10))
                throw new InvalidDataException(label + "含无效评分。");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException(label + "格式无法安全导入。", ex);
        }
    }

    static ImportScopeSummary MergeMap(JsonObject current, JsonObject backed,
        LibraryImportScope scope, string label, LibraryImportConflictPolicy policy,
        List<ImportConflictDetail>? conflictDetails = null)
    {
        var summary = new ImportScopeSummary
        {
            Scope = scope, Label = label, BackupItems = backed.Count, CurrentItems = current.Count
        };
        var currentKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in current.Select(item => item.Key))
            if (!currentKeys.TryAdd(key, key))
                throw new InvalidDataException(label + "包含只差大小写的重复视频 ID：" + key);
        var backupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in backed)
        {
            if (!backupKeys.Add(key))
                throw new InvalidDataException(label + "备份包含只差大小写的重复视频 ID：" + key);
            if (!currentKeys.TryGetValue(key, out var existingKey))
            {
                current[key] = value?.DeepClone();
                currentKeys.Add(key, key);
                summary.Added++;
                continue;
            }
            if (JsonNode.DeepEquals(current[existingKey], value)) continue;
            if (conflictDetails is { Count: < 20 } &&
                scope is LibraryImportScope.Tags or LibraryImportScope.Ratings)
                conflictDetails.Add(new ImportConflictDetail(scope, existingKey,
                    ImportConflictValue(scope, current[existingKey]), ImportConflictValue(scope, value)));
            if (policy == LibraryImportConflictPolicy.PreferBackup)
            {
                current[existingKey] = value?.DeepClone();
                summary.Replaced++;
            }
            else summary.KeptConflicts++;
        }
        return summary;
    }

    static string ImportConflictValue(LibraryImportScope scope, JsonNode? value)
    {
        if (scope == LibraryImportScope.Tags)
        {
            if (value is not JsonArray tags) throw new InvalidDataException("标签冲突值无效。");
            if (tags.Count == 0) return "（无标签）";
            var shown = tags.Take(8).Select(tag =>
            {
                var text = tag?.GetValue<string>() ?? "（空）";
                text = text.Replace('\r', ' ').Replace('\n', ' ');
                return text.Length <= 72 ? text : text[..72] + "…";
            });
            return string.Join("、", shown)
                + (tags.Count > 8 ? $"…（另 {tags.Count - 8} 个标签）" : "");
        }
        if (scope == LibraryImportScope.Ratings)
        {
            if (value is not JsonObject rating || !rating.TryGetPropertyValue("Score", out var score)
                || score is null) throw new InvalidDataException("评分冲突值无效。");
            var number = JsonSerializer.Deserialize<double>(score.ToJsonString());
            if (!double.IsFinite(number) || number is < 0 or > 10)
                throw new InvalidDataException("评分冲突值无效。");
            return score.ToJsonString() + " 分";
        }
        throw new InvalidOperationException("只支持预览标签和个人评分冲突。");
    }

    static void MergeConfigMap(JsonObject currentConfig, JsonObject backupConfig, string property,
        LibraryImportConflictPolicy policy, ImportScopeSummary total,
        List<ImportConflictDetail>? conflictDetails = null)
    {
        var backed = GetImportMap(backupConfig, property, createIfMissing: false);
        var current = GetImportMap(currentConfig, property, createIfMissing: backed.Count > 0);
        var result = MergeMap(current, backed, total.Scope, total.Label, policy, conflictDetails);
        total.BackupItems += result.BackupItems;
        total.CurrentItems += result.CurrentItems;
        total.Added += result.Added;
        total.Replaced += result.Replaced;
        total.KeptConflicts += result.KeptConflicts;
    }

    static JsonObject GetImportMap(JsonObject config, string property, bool createIfMissing)
    {
        if (!config.TryGetPropertyValue(property, out var node) || node is null)
        {
            var created = new JsonObject();
            if (createIfMissing) config[property] = created;
            return created;
        }
        return node as JsonObject
            ?? throw new InvalidDataException("配置字段格式无效：" + property);
    }

    static void MergeConfigList(JsonObject currentConfig, JsonObject backupConfig, string property,
        ImportScopeSummary summary)
    {
        var backed = GetImportList(backupConfig, property, createIfMissing: false);
        var current = GetImportList(currentConfig, property, createIfMissing: backed.Count > 0);
        summary.CurrentItems += current.Count;
        summary.BackupItems += backed.Count;
        var known = current.Select(item => item?.GetValue<string>() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in backed)
        {
            var value = item?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("列表含空名称：" + property);
            if (!known.Add(value)) continue;
            current.Add(value);
            summary.Added++;
        }
    }

    static JsonArray GetImportList(JsonObject config, string property, bool createIfMissing)
    {
        if (!config.TryGetPropertyValue(property, out var node) || node is null)
        {
            var created = new JsonArray();
            if (createIfMissing) config[property] = created;
            return created;
        }
        return node as JsonArray
            ?? throw new InvalidDataException("配置列表格式无效：" + property);
    }

    static void MergeReviewHistory(string dataDir, string backupDirectory,
        LibraryImportConflictPolicy policy, ImportScopeSummary summary, ImportPlan plan)
    {
        var currentPath = Path.Combine(dataDir, "review-history.jsonl");
        var backupPath = Path.Combine(backupDirectory, "review-history.jsonl");
        var currentLines = ReadValidReviewLines(dataDir, currentPath);
        var backupLines = ReadValidReviewLines(backupDirectory, backupPath);
        summary.CurrentItems += currentLines.Count;
        summary.BackupItems += backupLines.Count;
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in currentLines)
        {
            var id = ReviewHistoryKey(line);
            if (current.TryGetValue(id, out var previous) && previous != line)
                throw new InvalidDataException("当前审核历史包含重复且冲突的事务 ID。");
            current[id] = line;
        }
        var sourceIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var line in backupLines)
        {
            var id = ReviewHistoryKey(line);
            if (sourceIds.TryGetValue(id, out var previous) && previous != line)
                throw new InvalidDataException("备份审核历史包含重复且冲突的事务 ID。");
            sourceIds[id] = line;
            if (current.TryGetValue(id, out var existing))
            {
                if (existing != line) summary.KeptConflicts++;
                continue;
            }
            current[id] = line;
            added.Add(line);
            summary.Added++;
        }
        if (added.Count == 0) return;
        var original = File.Exists(currentPath) ? File.ReadAllText(currentPath) : "";
        var separator = original.Length == 0 || original.EndsWith('\n') ? "" : "\n";
        plan.FileContents["review-history.jsonl"] = original + separator + string.Join("\n", added) + "\n";
    }

    static List<string> ReadValidReviewLines(string directory, string path)
    {
        if (!File.Exists(path)) return [];
        ReviewTransactionStore.Load(directory, out var invalidLines);
        if (invalidLines != 0) throw new InvalidDataException("审核历史含无效记录：" + path);
        return File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
    }

    static string ReviewHistoryKey(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.TryGetProperty("SchemaVersion", out var version) && version.GetInt32() == 2)
        {
            if (!root.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString()))
                throw new InvalidDataException("审核事务缺少 ID。");
            return "id:" + id.GetString();
        }
        return "legacy:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
    }

    static void MergeReviewViews(string dataDir, string backupDirectory,
        LibraryImportConflictPolicy policy, ImportScopeSummary summary, ImportPlan plan)
    {
        var sourcePath = Path.Combine(backupDirectory, "review-views.json");
        var currentPath = Path.Combine(dataDir, "review-views.json");
        if (!File.Exists(sourcePath) && !File.Exists(currentPath)) return;
        var current = ReviewSavedViewStore.Load(dataDir, out var recovered).Select(view => view.Clone()).ToList();
        if (recovered) throw new InvalidDataException("当前审核视图主文件损坏，请先修复后导入。");
        var backed = ReviewSavedViewStore.Load(backupDirectory, out var sourceRecovered);
        if (sourceRecovered) throw new InvalidDataException("备份审核视图主文件损坏。");
        summary.CurrentItems += current.Count;
        summary.BackupItems += backed.Count;
        var viewsChanged = false;
        foreach (var view in backed)
        {
            var index = current.FindIndex(item => item.Name.Equals(view.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                current.Add(view.Clone());
                summary.Added++;
                viewsChanged = true;
                continue;
            }
            if (JsonSerializer.Serialize(current[index]) == JsonSerializer.Serialize(view)) continue;
            if (policy == LibraryImportConflictPolicy.PreferBackup)
            {
                current[index] = view.Clone();
                summary.Replaced++;
                viewsChanged = true;
            }
            else summary.KeptConflicts++;
        }
        if (!viewsChanged) return;
        if (current.Count > 32) throw new InvalidDataException("合并后的审核视图超过 32 个，请缩小导入范围。");
        current.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        plan.FileContents["review-views.json"] = JsonSerializer.Serialize(new { Version = 1, Views = current }, JsonOptions);
    }

    static void MergeQueue(string dataDir, string backupDirectory,
        LibraryImportConflictPolicy policy, ImportPlan plan)
    {
        var current = ScrapeQueueStore.Load(dataDir);
        var backed = ReadImportQueue(Path.Combine(backupDirectory, "scrape-queue.json"));
        var summary = new ImportScopeSummary
        {
            Scope = LibraryImportScope.Queue, Label = "搜刮队列",
            BackupItems = backed.Count, CurrentItems = current.Count
        };
        var merged = current.Select(CopyQueueItem).ToList();
        var positions = merged.Select((item, index) => (item, index))
            .ToDictionary(item => item.item.VideoId, item => item.index, StringComparer.OrdinalIgnoreCase);
        foreach (var incoming in backed)
        {
            if (!positions.TryGetValue(incoming.VideoId, out var index))
            {
                positions[incoming.VideoId] = merged.Count;
                merged.Add(CopyQueueItem(incoming));
                summary.Added++;
                continue;
            }
            if (JsonSerializer.Serialize(merged[index]) == JsonSerializer.Serialize(incoming)) continue;
            if (policy == LibraryImportConflictPolicy.PreferBackup)
            {
                merged[index] = CopyQueueItem(incoming);
                summary.Replaced++;
            }
            else summary.KeptConflicts++;
        }
        plan.Sections.Add(summary);
        if (summary.Added + summary.Replaced > 0)
            plan.FileContents["scrape-queue.json"] = JsonSerializer.Serialize(merged, JsonOptions);
    }

    static List<ScrapeQueueItem> ReadImportQueue(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("备份队列格式无效。");
        var result = new List<ScrapeQueueItem>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var item = element.Deserialize<ScrapeQueueItem>()
                ?? throw new InvalidDataException("备份队列含无效任务。");
            if (!element.TryGetProperty("NextAttemptAt", out _)) item.NextAttemptAt = DateTimeOffset.MinValue;
            result.Add(item);
        }
        return result;
    }

    static ScrapeQueueItem CopyQueueItem(ScrapeQueueItem item) => new()
    {
        VideoId = item.VideoId, Kind = item.Kind, Code = item.Code, SearchTitle = item.SearchTitle,
        RefreshExisting = item.RefreshExisting,
        Attempts = item.Attempts, Failed = item.Failed, NextAttemptAt = item.NextAttemptAt,
        LastError = item.LastError
    };
}
