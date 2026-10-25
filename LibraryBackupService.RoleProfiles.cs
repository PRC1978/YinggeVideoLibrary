using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static partial class LibraryBackupService
{
    // Import uses profile IDs and explicit source IDs. A matching display name
    // never merges two people. A source identity already attached to another
    // profile is kept with the current data even under PreferBackup.
    static void MergeRoleProfiles(string dataDir, string backupDirectory,
        LibraryImportConflictPolicy policy, ImportPlan plan)
    {
        var current = RoleProfileStore.Load(dataDir);
        var incoming = RoleProfileStore.Load(backupDirectory);
        var merged = RoleProfileStore.Parse(JsonSerializer.Serialize(current));
        var summary = new ImportScopeSummary
        {
            Scope = LibraryImportScope.RoleProfiles, Label = "角色档案与作品关联",
            BackupItems = incoming.Profiles.Count, CurrentItems = current.Profiles.Count
        };
        var currentIds = merged.Profiles.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var profile in incoming.Profiles)
        {
            var old = currentIds.GetValueOrDefault(profile.Id);
            if (old is not null && JsonSerializer.Serialize(old) == JsonSerializer.Serialize(profile)) continue;
            var conflictingIdentity = profile.Sources.Any(source => merged.Profiles.Any(existing =>
                !existing.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase) &&
                existing.Sources.Any(value => value.Provider.Equals(source.Provider, StringComparison.OrdinalIgnoreCase)
                    && value.PersonId.Equals(source.PersonId, StringComparison.OrdinalIgnoreCase))));
            if (conflictingIdentity)
            {
                summary.KeptConflicts++;
                continue;
            }
            var copy = JsonSerializer.Deserialize<RoleProfileStore.Profile>(JsonSerializer.Serialize(profile))!;
            if (old is null)
            {
                merged.Profiles.Add(copy);
                currentIds.Add(copy.Id, copy);
                summary.Added++;
            }
            else if (policy == LibraryImportConflictPolicy.PreferBackup)
            {
                merged.Profiles[merged.Profiles.IndexOf(old)] = copy;
                currentIds[copy.Id] = copy;
                summary.Replaced++;
            }
            else summary.KeptConflicts++;
        }
        plan.Sections.Add(summary);
        if (summary.Added == 0 && summary.Replaced == 0) return;
        merged.Revision = Guid.NewGuid().ToString("N");
        RoleProfileStore.ValidateState(merged);
        plan.FileContents[RoleProfileStore.FileName] = JsonSerializer.Serialize(merged, JsonOptions);
    }
}
