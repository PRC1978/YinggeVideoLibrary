using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static partial class LibraryBackupService
{
    // An import batch is kept whole so its audit action, entry keys and undo
    // action remain a valid unit. If the same export key already exists, the
    // current batch wins; changing one entry would corrupt undo history.
    static void MergeSiteLists(string dataDir, string source, ImportPlan plan)
    {
        var current = SiteListImportStore.Load(dataDir);
        var incoming = SiteListImportStore.Load(source);
        var merged = JsonSerializer.Deserialize<SiteListImportStore.State>(JsonSerializer.Serialize(current))
            ?? throw new InvalidDataException("当前站点片单无法复制。");
        var summary = new ImportScopeSummary
        {
            Scope = LibraryImportScope.SiteLists, Label = "站点片单及审计",
            BackupItems = incoming.Entries.Count + incoming.Actions.Count,
            CurrentItems = current.Entries.Count + current.Actions.Count
        };
        var knownKeys = merged.Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var knownActions = merged.Actions.Select(action => action.Id).ToHashSet(StringComparer.Ordinal);
        var knownBatches = merged.Actions.Where(action => action.Kind == "import")
            .ToDictionary(action => action.BatchId, StringComparer.Ordinal);
        foreach (var imported in incoming.Actions.Where(action => action.Kind == "import"))
        {
            var sourceActions = incoming.Actions.Where(action => action.BatchId == imported.BatchId).ToArray();
            var sourceEntries = incoming.Entries.Where(entry => entry.BatchId == imported.BatchId).ToArray();
            if (knownBatches.TryGetValue(imported.BatchId, out var existing))
            {
                var existingActions = merged.Actions.Where(action => action.BatchId == imported.BatchId).ToArray();
                var existingEntries = merged.Entries.Where(entry => entry.BatchId == imported.BatchId).ToArray();
                if (!SameActions(sourceActions, existingActions) || !SameEntries(sourceEntries, existingEntries))
                    summary.KeptConflicts += sourceActions.Length + sourceEntries.Length;
                continue;
            }
            if (sourceEntries.Any(entry => knownKeys.Contains(entry.Key))
                || sourceActions.Any(action => knownActions.Contains(action.Id)))
            {
                summary.KeptConflicts += sourceActions.Length + sourceEntries.Length;
                continue;
            }
            foreach (var action in sourceActions)
            {
                merged.Actions.Add(action);
                knownActions.Add(action.Id);
            }
            foreach (var entry in sourceEntries)
            {
                merged.Entries.Add(entry);
                knownKeys.Add(entry.Key);
            }
            knownBatches[imported.BatchId] = imported;
            summary.Added += sourceActions.Length + sourceEntries.Length;
        }
        plan.Sections.Add(summary);
        if (summary.Added == 0) return;
        merged.Revision = checked(Math.Max(current.Revision, incoming.Revision) + 1);
        var text = JsonSerializer.Serialize(merged, JsonOptions);
        var validationDir = Path.Combine(Path.GetTempPath(), "yingge-site-list-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validationDir);
        try
        {
            File.WriteAllText(Path.Combine(validationDir, "site-list-import.json"), text);
            _ = SiteListImportStore.Load(validationDir);
        }
        finally
        {
            try { Directory.Delete(validationDir, true); } catch { }
        }
        plan.FileContents["site-list-import.json"] = text;
    }

    static bool SameActions(IReadOnlyList<SiteListImportStore.AuditAction> left,
        IReadOnlyList<SiteListImportStore.AuditAction> right) =>
        left.Count == right.Count && left.OrderBy(action => action.Id, StringComparer.Ordinal)
            .Select(action => JsonSerializer.Serialize(action))
            .SequenceEqual(right.OrderBy(action => action.Id, StringComparer.Ordinal)
                .Select(action => JsonSerializer.Serialize(action)), StringComparer.Ordinal);

    static bool SameEntries(IReadOnlyList<SiteListImportStore.Entry> left,
        IReadOnlyList<SiteListImportStore.Entry> right) =>
        left.Count == right.Count && left.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => JsonSerializer.Serialize(entry))
            .SequenceEqual(right.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => JsonSerializer.Serialize(entry)), StringComparer.Ordinal);
}
