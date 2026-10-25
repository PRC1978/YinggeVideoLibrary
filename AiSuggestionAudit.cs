using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace YinggeVideoLibrary;

// Classification/tag suggestions have their own audit fields. Scrape review
// snapshots intentionally do not contain Categories or MultiCategories.
public sealed class AiSuggestionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string VideoId { get; set; } = "";
    public string Kind { get; set; } = ""; // category | tags
    public string InputTitle { get; set; } = "";
    public string InputContext { get; set; } = "";
    // A digest is enough for a version check and does not duplicate a user's
    // category/tag values (which could themselves contain a local path).
    public string TargetFingerprint { get; set; } = "";
    public string SuggestedCategory { get; set; } = "";
    public List<string> SuggestedTags { get; set; } = [];
    public int Confidence { get; set; }
    public string Verdict { get; set; } = "suggest"; // suggest | reject
    public string Reason { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long InputTokenEstimate { get; set; }
    public long OutputTokenLimitShare { get; set; }
    public double EstimatedUsd { get; set; }
    public string Status { get; set; } = "pending"; // pending | ai-rejected | approved | rejected | undone
}

public sealed class AiSuggestionChange
{
    public string SuggestionId { get; set; } = "";
    public string VideoId { get; set; } = "";
    public string BeforeStatus { get; set; } = "";
    public string AfterStatus { get; set; } = "";
    // Null indicates an untouched field. Empty list means an absent mapping.
    public List<string>? BeforeCategories { get; set; }
    public List<string>? AfterCategories { get; set; }
    public List<string>? BeforeTags { get; set; }
    public List<string>? AfterTags { get; set; }
}

public sealed class AiSuggestionAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset TimeUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Action { get; set; } = ""; // approve | reject | undo
    public string? UndoesActionId { get; set; }
    public List<AiSuggestionChange> Changes { get; set; } = [];
}

internal static class AiSuggestionAudit
{
    public static AiSuggestionRecord Clone(AiSuggestionRecord source) =>
        JsonSerializer.Deserialize<AiSuggestionRecord>(JsonSerializer.Serialize(source))!;

    public static AiSuggestionAction Clone(AiSuggestionAction source) =>
        JsonSerializer.Deserialize<AiSuggestionAction>(JsonSerializer.Serialize(source))!;

    public static bool IsActionable(AiSuggestionRecord? suggestion) =>
        suggestion?.Status is "pending" or "ai-rejected";

    public static string Fingerprint(IEnumerable<string> values)
    {
        var normalized = values.Select(value => value.ToUpperInvariant()).ToArray();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized))));
    }

    public static bool IsValid(AiSuggestionRecord? suggestion) =>
        suggestion is not null && !string.IsNullOrWhiteSpace(suggestion.Id) && !string.IsNullOrWhiteSpace(suggestion.VideoId) &&
        suggestion.Kind is "category" or "tags" && suggestion.Confidence is >= 0 and <= 100 &&
        suggestion.Verdict is "suggest" or "reject" &&
        suggestion.Status is "pending" or "ai-rejected" or "approved" or "rejected" or "undone" &&
        suggestion.InputTitle is { Length: <= 180 } && suggestion.InputContext is { Length: <= 160 } &&
        suggestion.TargetFingerprint is { Length: 64 } &&
        suggestion.TargetFingerprint.All(Uri.IsHexDigit) &&
        suggestion.Reason is { Length: <= 240 } && suggestion.Model is { Length: <= 120 } &&
        suggestion.SuggestedCategory is { Length: <= 100 } &&
        suggestion.SuggestedTags is { Count: <= 8 } &&
        suggestion.SuggestedTags.All(tag => tag.Length is > 0 and <= 24 && !tag.Any(char.IsControl)) &&
        suggestion.EstimatedUsd is >= 0 and < 10000 && double.IsFinite(suggestion.EstimatedUsd) &&
        suggestion.InputTokenEstimate >= 0 && suggestion.OutputTokenLimitShare >= 0;

    static bool SameValues(List<string>? first, List<string>? second) =>
        first is null ? second is null : second is not null && first.SequenceEqual(second, StringComparer.Ordinal);

    public static bool IsHistoryWellFormed(LibraryConfig config)
    {
        if (config.AiSuggestions is null || config.AiSuggestionActions is null) return false;
        if (config.AiSuggestions.Any(pair => pair.Value is null ||
            !string.Equals(pair.Key, pair.Value.Id, StringComparison.Ordinal) || !IsValid(pair.Value))) return false;
        foreach (var (id, action) in config.AiSuggestionActions)
        {
            if (action is null || !string.Equals(id, action.Id, StringComparison.Ordinal) ||
                action.Action is not ("approve" or "reject" or "undo") ||
                action.Changes is not { Count: > 0 and <= 100 } ||
                action.Changes.Any(change => change is null) ||
                action.Changes.GroupBy(change => change.SuggestionId, StringComparer.Ordinal)
                    .Any(group => group.Count() > 1)) return false;
            foreach (var change in action.Changes)
            {
                if (change is null || string.IsNullOrWhiteSpace(change.SuggestionId) ||
                    !config.AiSuggestions.TryGetValue(change.SuggestionId, out var suggestion) || suggestion is null ||
                    !string.Equals(change.VideoId, suggestion.VideoId, StringComparison.Ordinal) ||
                    change.BeforeStatus is not ("pending" or "ai-rejected" or "approved" or "rejected" or "undone") ||
                    change.AfterStatus is not ("pending" or "ai-rejected" or "approved" or "rejected" or "undone") ||
                    change.BeforeCategories is { Count: > 500 } || change.AfterCategories is { Count: > 500 } ||
                    change.BeforeTags is { Count: > 500 } || change.AfterTags is { Count: > 500 }) return false;
            }
            if (action.Action != "undo")
            {
                if (!string.IsNullOrEmpty(action.UndoesActionId) ||
                    action.Changes.Any(change => change.AfterStatus != (action.Action == "approve" ? "approved" : "rejected"))) return false;
                continue;
            }
            if (string.IsNullOrWhiteSpace(action.UndoesActionId) ||
                !config.AiSuggestionActions.TryGetValue(action.UndoesActionId, out var target) ||
                target is null || target.Action is not ("approve" or "reject") ||
                target.Changes is null || target.Changes.Count != action.Changes.Count) return false;
            foreach (var change in action.Changes)
            {
                var original = target.Changes.FirstOrDefault(item => item is not null && item.SuggestionId == change.SuggestionId);
                if (original is null || original.VideoId != change.VideoId ||
                    change.BeforeStatus != original.AfterStatus || change.AfterStatus != original.BeforeStatus ||
                    !SameValues(change.BeforeCategories, original.AfterCategories) ||
                    !SameValues(change.AfterCategories, original.BeforeCategories) ||
                    !SameValues(change.BeforeTags, original.AfterTags) ||
                    !SameValues(change.AfterTags, original.BeforeTags)) return false;
            }
        }
        return config.AiSuggestionActions.Values.Where(action => action.Action == "undo")
            .GroupBy(action => action.UndoesActionId, StringComparer.Ordinal)
            .All(group => group.Count() == 1);
    }

    public static List<string> CategoriesOf(LibraryConfig config, string id)
    {
        if (config.MultiCategories.TryGetValue(id, out var multiple) && multiple is { Count: > 0 })
            return multiple.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return config.Categories.TryGetValue(id, out var single) && !string.IsNullOrWhiteSpace(single)
            ? [single] : [];
    }

    public static List<string> TagsOf(LibraryConfig config, string id) =>
        config.Tags.TryGetValue(id, out var tags) ? tags.ToList() : [];

    public static string CategoryFingerprint(LibraryConfig config, string id)
    {
        config.Categories.TryGetValue(id, out var single);
        // Treat legacy single-category storage and an equivalent normalized
        // MultiCategories entry as the same logical state after undo.
        return Fingerprint(new[] { single ?? "", "\u0000" }.Concat(CategoriesOf(config, id)));
    }

    public static bool TargetsMatch(LibraryConfig disk, LibraryConfig memory, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (!disk.AiSuggestions.TryGetValue(id, out var diskSuggestion) ||
                !memory.AiSuggestions.TryGetValue(id, out var memorySuggestion) ||
                JsonSerializer.Serialize(diskSuggestion) != JsonSerializer.Serialize(memorySuggestion) ||
                CategoryFingerprint(disk, diskSuggestion.VideoId) !=
                    CategoryFingerprint(memory, diskSuggestion.VideoId) ||
                !TagsOf(disk, diskSuggestion.VideoId).SequenceEqual(
                    TagsOf(memory, diskSuggestion.VideoId), StringComparer.CurrentCultureIgnoreCase)) return false;
        }
        return true;
    }

    public static AiSuggestionAction Decide(LibraryConfig config, IEnumerable<string> ids, bool approve)
    {
        var unique = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unique.Length == 0) throw new InvalidOperationException("请先选择 AI 建议。");
        var records = unique.Select(id => config.AiSuggestions.TryGetValue(id, out var record) ? record : null).ToArray();
        if (records.Any(record => record is null || !IsValid(record) || !IsActionable(record)))
            throw new InvalidOperationException("AI 建议已变化或不再待审，请刷新后重试。");
        if (approve && records.Any(record => record!.Kind == "category" &&
                record.SuggestedCategory is not ("电影" or "剧集" or "动漫" or "其他") ||
            record!.Kind == "tags" && record.SuggestedTags.Count == 0))
            throw new InvalidOperationException("部分 AI 否决没有可应用的建议，只能人工否决。");
        if (approve && records.Any(record => !string.Equals(record!.TargetFingerprint,
                record.Kind == "category" ? CategoryFingerprint(config, record.VideoId) :
                    Fingerprint(TagsOf(config, record.VideoId)),
                StringComparison.Ordinal)))
            throw new InvalidOperationException("建议生成后，目标分类或标签已改变。请重新生成建议，避免覆盖较新的编辑。");
        // Do not let one batch apply two competing suggestions to the same field.
        if (approve && records.GroupBy(record => (record!.VideoId, record.Kind)).Any(group => group.Count() > 1))
            throw new InvalidOperationException("同一视频有多个同类建议，请分开审核。");
        var action = new AiSuggestionAction { Action = approve ? "approve" : "reject" };
        foreach (var record in records.Select(record => record!))
        {
            var change = new AiSuggestionChange
            {
                SuggestionId = record.Id, VideoId = record.VideoId,
                BeforeStatus = record.Status, AfterStatus = approve ? "approved" : "rejected"
            };
            if (approve && record.Kind == "category")
            {
                change.BeforeCategories = CategoriesOf(config, record.VideoId);
                change.AfterCategories = [record.SuggestedCategory];
                SetCategories(config, record.VideoId, change.AfterCategories);
            }
            else if (approve && record.Kind == "tags")
            {
                change.BeforeTags = TagsOf(config, record.VideoId);
                change.AfterTags = change.BeforeTags.Concat(record.SuggestedTags)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(50).ToList();
                SetTags(config, record.VideoId, change.AfterTags);
            }
            record.Status = change.AfterStatus;
            action.Changes.Add(change);
        }
        config.AiSuggestionActions.Add(action.Id, action);
        return action;
    }

    public static AiSuggestionAction? LastUndoable(LibraryConfig config)
    {
        var undone = config.AiSuggestionActions.Values.Where(action => action.Action == "undo")
            .Select(action => action.UndoesActionId).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return config.AiSuggestionActions.Values.Where(action => (action.Action is "approve" or "reject") &&
                action.Changes.Count > 0 && !undone.Contains(action.Id))
            .OrderByDescending(action => action.TimeUtc).FirstOrDefault();
    }

    public static AiSuggestionAction UndoLast(LibraryConfig config)
    {
        var target = LastUndoable(config) ?? throw new InvalidOperationException("没有可撤销的 AI 审核操作。");
        foreach (var change in target.Changes)
        {
            if (!config.AiSuggestions.TryGetValue(change.SuggestionId, out var suggestion) ||
                suggestion.Status != change.AfterStatus ||
                (change.AfterCategories is not null &&
                    (!CategoriesOf(config, change.VideoId).SequenceEqual(change.AfterCategories, StringComparer.OrdinalIgnoreCase) ||
                     !string.Equals(config.Categories.GetValueOrDefault(change.VideoId),
                         change.AfterCategories.FirstOrDefault(), StringComparison.OrdinalIgnoreCase))) ||
                (change.AfterTags is not null && !TagsOf(config, change.VideoId).SequenceEqual(change.AfterTags, StringComparer.CurrentCultureIgnoreCase)))
                throw new InvalidOperationException("AI 审核后的分类或标签已被修改，不能覆盖较新的操作。");
        }
        var undo = new AiSuggestionAction { Action = "undo", UndoesActionId = target.Id };
        foreach (var change in target.Changes)
        {
            if (change.BeforeCategories is not null) SetCategories(config, change.VideoId, change.BeforeCategories);
            if (change.BeforeTags is not null) SetTags(config, change.VideoId, change.BeforeTags);
            config.AiSuggestions[change.SuggestionId].Status = change.BeforeStatus;
            undo.Changes.Add(new AiSuggestionChange
            {
                SuggestionId = change.SuggestionId, VideoId = change.VideoId,
                BeforeStatus = change.AfterStatus, AfterStatus = change.BeforeStatus,
                BeforeCategories = change.AfterCategories?.ToList(), AfterCategories = change.BeforeCategories?.ToList(),
                BeforeTags = change.AfterTags?.ToList(), AfterTags = change.BeforeTags?.ToList()
            });
        }
        config.AiSuggestionActions.Add(undo.Id, undo);
        return undo;
    }

    public static void SetCategories(LibraryConfig config, string id, IReadOnlyList<string> categories)
    {
        if (categories.Count == 0) { config.Categories.Remove(id); config.MultiCategories.Remove(id); return; }
        config.Categories[id] = categories[0];
        config.MultiCategories[id] = categories.ToList();
    }

    public static void SetTags(LibraryConfig config, string id, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0) config.Tags.Remove(id);
        else config.Tags[id] = tags.ToList();
    }

    public static IReadOnlyDictionary<string, bool> VerifyForDiagnostics()
    {
        var config = new LibraryConfig();
        config.Categories["one"] = "旧分类";
        config.MultiCategories["one"] = ["旧分类"];
        config.Tags["one"] = ["原标签"];
        var category = new AiSuggestionRecord { VideoId = "one", Kind = "category", SuggestedCategory = "电影",
            Confidence = 24, Verdict = "reject", Status = "ai-rejected", Reason = "证据不足", Model = "diagnostic",
            InputTitle = "片名", TargetFingerprint = CategoryFingerprint(config, "one") };
        var tags = new AiSuggestionRecord { VideoId = "one", Kind = "tags", SuggestedTags = ["新标签"],
            Confidence = 15, Status = "pending", Reason = "片名", Model = "diagnostic", InputTitle = "片名",
            TargetFingerprint = Fingerprint(["原标签"]) };
        config.AiSuggestions[category.Id] = category;
        config.AiSuggestions[tags.Id] = tags;
        var noAutoWrite = CategoriesOf(config, "one").Single() == "旧分类" && TagsOf(config, "one").Single() == "原标签";
        var denied = Decide(config, [tags.Id], false);
        var denyNoWrite = denied.Action == "reject" && TagsOf(config, "one").Single() == "原标签";
        _ = UndoLast(config);
        var rejectionUndone = config.AiSuggestions[tags.Id].Status == "pending";
        var approved = Decide(config, [category.Id, tags.Id], true);
        var applied = approved.Changes.Count == 2 && CategoriesOf(config, "one").Single() == "电影" &&
            TagsOf(config, "one").SequenceEqual(["原标签", "新标签"]);
        var serialized = JsonSerializer.Serialize(config);
        var restarted = JsonSerializer.Deserialize<LibraryConfig>(serialized)!;
        var persisted = restarted.AiSuggestions.Count == 2 && restarted.AiSuggestionActions.Count == 3 &&
            restarted.AiSuggestions[category.Id].Confidence == 24 && restarted.AiSuggestions[category.Id].Reason == "证据不足";
        _ = UndoLast(restarted);
        var undone = CategoriesOf(restarted, "one").Single() == "旧分类" && TagsOf(restarted, "one").Single() == "原标签";
        var conflict = false;
        var second = Decide(restarted, [category.Id], true);
        restarted.Categories["one"] = "人工改动";
        try { _ = UndoLast(restarted); } catch (InvalidOperationException) { conflict = true; }
        var stale = false;
        var staleConfig = new LibraryConfig();
        var staleSuggestion = new AiSuggestionRecord { VideoId = "stale", Kind = "category",
            SuggestedCategory = "电影", TargetFingerprint = CategoryFingerprint(staleConfig, "stale"), Status = "pending" };
        staleConfig.AiSuggestions[staleSuggestion.Id] = staleSuggestion;
        staleConfig.Categories["stale"] = "较新的人工分类";
        try { _ = Decide(staleConfig, [staleSuggestion.Id], true); }
        catch (InvalidOperationException) { stale = true; }
        var emptyRejected = new AiSuggestionRecord { VideoId = "empty", Kind = "category",
            TargetFingerprint = Fingerprint([]), Verdict = "reject", Status = "ai-rejected" };
        staleConfig.AiSuggestions[emptyRejected.Id] = emptyRejected;
        var rejectCannotApprove = false;
        try { _ = Decide(staleConfig, [emptyRejected.Id], true); }
        catch (InvalidOperationException) { rejectCannotApprove = true; }
        var chain = new LibraryConfig();
        var chainCategory = new AiSuggestionRecord { VideoId = "chain", Kind = "category",
            SuggestedCategory = "电影", TargetFingerprint = CategoryFingerprint(chain, "chain") };
        var chainTags = new AiSuggestionRecord { VideoId = "chain", Kind = "tags",
            SuggestedTags = ["试验"], TargetFingerprint = Fingerprint([]) };
        chain.AiSuggestions[chainCategory.Id] = chainCategory;
        chain.AiSuggestions[chainTags.Id] = chainTags;
        _ = Decide(chain, [chainCategory.Id], true);
        _ = Decide(chain, [chainTags.Id], true);
        _ = UndoLast(chain);
        var firstUndo = CategoriesOf(chain, "chain").SequenceEqual(["电影"]) && TagsOf(chain, "chain").Count == 0;
        _ = UndoLast(chain);
        var chainUndo = firstUndo && CategoriesOf(chain, "chain").Count == 0 && TagsOf(chain, "chain").Count == 0;
        return new Dictionary<string, bool>
        {
            ["aiSuggestionsNeverAutoWrite"] = noAutoWrite,
            ["aiSuggestionManualRejectionHistory"] = denyNoWrite && rejectionUndone,
            ["aiSuggestionBatchApprovalFieldIsolation"] = applied,
            ["aiSuggestionCrossRestartHistory"] = persisted,
            ["aiSuggestionCrossRestartUndo"] = undone,
            ["aiSuggestionUndoRejectsLaterEdit"] = conflict && second.Action == "approve",
            ["aiSuggestionApprovalRejectsStaleField"] = stale,
            ["aiSuggestionRejectedEmptyCandidateCannotApprove"] = rejectCannotApprove,
            ["aiSuggestionConsecutiveUndo"] = chainUndo,
            ["aiSuggestionHistoryWellFormed"] = IsHistoryWellFormed(chain)
        };
    }
}
