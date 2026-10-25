using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    internal static IReadOnlyDictionary<string, bool> VerifyCurrentLibraryCopyForDiagnostics(string root)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var originalConfig = Path.Combine(DataDir, "config.json");
        var originalIndex = Path.Combine(DataDir, "library-index.json");
        var originalMetadata = Path.Combine(DataDir, "metadata.json");
        if (!File.Exists(originalConfig) || !File.Exists(originalIndex) || !File.Exists(originalMetadata))
            return checks;
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "config.json", "library-index.json", "metadata.json" })
            File.Copy(Path.Combine(DataDir, name), Path.Combine(root, name), true);
        foreach (var name in new[] { "personal-ratings.json", "review-history.jsonl" })
            if (File.Exists(Path.Combine(DataDir, name)))
                File.Copy(Path.Combine(DataDir, name), Path.Combine(root, name), true);
        foreach (var path in Directory.EnumerateFiles(DataDir, "scrape-queue.*"))
            if (Path.GetExtension(path) is ".json" or ".bin")
                File.Copy(path, Path.Combine(root, Path.GetFileName(path)), true);
        var copiedConfigPath = Path.Combine(root, "config.json");
        var original = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(copiedConfigPath))!;
        var index = JsonSerializer.Deserialize<LibraryIndex>(
            File.ReadAllText(Path.Combine(root, "library-index.json")))!;
        var records = MetadataStore.Load(root);
        var ratingCount = PersonalRatingStore.Load(root).Count;
        var queueCount = ScrapeQueueStore.Load(root).Count;
        var reviewHistoryPath = Path.Combine(root, "review-history.jsonl");
        var historyCount = File.Exists(reviewHistoryPath) ? File.ReadLines(reviewHistoryPath).Count() : 0;
        var saved = WriteConfigSnapshot(CloneLibraryConfig(original), original,
            copiedConfigPath, true, true);
        MetadataStore.Save(root, records);
        var after = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(copiedConfigPath))!;
        var afterRecords = MetadataStore.Load(root);
        checks["realLibraryCopyUpgradePreservesCounts"] = index.Entries.Count > 0
            && original.Folders.Count == after.Folders.Count
            && original.Favorites.Count == after.Favorites.Count
            && original.Categories.Count == after.Categories.Count
            && original.MultiCategories.Count == after.MultiCategories.Count
            && original.Tags.Count == after.Tags.Count
            && original.AiReviews.Count == after.AiReviews.Count
            && records.Count == afterRecords.Count
            && PersonalRatingStore.Load(root).Count == ratingCount
            && ScrapeQueueStore.Load(root).Count == queueCount
            && (File.Exists(reviewHistoryPath) ? File.ReadLines(reviewHistoryPath).Count() : 0) == historyCount;
        checks["realLibraryCopyUpgradePreservesRelations"] =
            original.Categories.All(pair => after.Categories.TryGetValue(pair.Key, out var value) && value == pair.Value)
            && original.MultiCategories.All(pair => after.MultiCategories.TryGetValue(pair.Key, out var value)
                && value.SequenceEqual(pair.Value))
            && original.Tags.All(pair => after.Tags.TryGetValue(pair.Key, out var value)
                && value.SequenceEqual(pair.Value))
            && records.All(pair => afterRecords.TryGetValue(pair.Key, out var value)
                && value.Code == pair.Value.Code && value.Title == pair.Value.Title
                && value.Source == pair.Value.Source
                && (value.PlatformRatings?.Count ?? 0) == (pair.Value.PlatformRatings?.Count ?? 0))
            && saved.SchemaVersion == 2 && afterRecords.Values.All(value => value.SchemaVersion == 2);
        var configBytes = File.ReadAllBytes(copiedConfigPath);
        var metadataBytes = File.ReadAllBytes(Path.Combine(root, "metadata.json"));
        WriteConfigSnapshot(CloneLibraryConfig(saved), saved, copiedConfigPath, true, true);
        MetadataStore.Save(root, afterRecords);
        checks["realLibraryCopyRepeatedUpgradeIsStable"] =
            File.ReadAllBytes(copiedConfigPath).AsSpan().SequenceEqual(configBytes)
            && File.ReadAllBytes(Path.Combine(root, "metadata.json")).AsSpan().SequenceEqual(metadataBytes);
        return checks;
    }

    internal static IReadOnlyDictionary<string, bool> VerifyLibraryFormatMigrationForDiagnostics(string root)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "config.json");
        const string legacyConfig = """
            {"folders":["D:\\Videos"],"favorites":["video-a"],
             "categories":{"video-a":"电影/日本"},
             "multiCategories":{"video-a":["电影/日本","收藏/经典"]},
             "tags":{"video-a":["动画","经典"]},
             "watchStates":{"video-a":"已看"},
             "aiReviews":{"video-a":{"VideoId":"video-a","Status":"approved","CandidateTitle":"旧片"}}}
            """;
        File.WriteAllText(configPath, legacyConfig);
        var old = JsonSerializer.Deserialize<LibraryConfig>(legacyConfig)!;
        var before = CloneLibraryConfig(old);
        checks["legacyConfigUsesVersionOne"] = old.SchemaVersion == 1;
        var saved = WriteConfigSnapshot(before, old, configPath, true, true);
        var migrated = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(configPath))!;
        checks["legacyConfigPreservesVideoRelations"] = saved.SchemaVersion == 2
            && migrated.SchemaVersion == 2 && migrated.Folders.SequenceEqual(old.Folders)
            && migrated.Favorites.SequenceEqual(old.Favorites)
            && migrated.Categories.Count == old.Categories.Count
            && migrated.MultiCategories["video-a"].SequenceEqual(old.MultiCategories["video-a"])
            && migrated.Tags["video-a"].SequenceEqual(old.Tags["video-a"])
            && migrated.WatchStates["video-a"] == "已看"
            && migrated.AiReviews.Count == old.AiReviews.Count;
        var firstSnapshot = File.ReadAllText(configPath);
        WriteConfigSnapshot(CloneLibraryConfig(saved), saved, configPath, true, true);
        checks["configMigrationRepeatedSaveIsStable"] = File.ReadAllText(configPath) == firstSnapshot;
        File.WriteAllText(configPath, "{\"schemaVersion\":3,\"folders\":[\"D:\\\\Newer\"]}");
        var unsupportedConfig = File.ReadAllText(configPath);
        try { WriteConfigSnapshot(saved, saved, configPath, true, true); }
        catch (InvalidDataException) { }
        checks["futureConfigRefusesOverwrite"] = File.ReadAllText(configPath) == unsupportedConfig;

        var metadataPath = Path.Combine(root, "metadata.json");
        const string legacyMetadata = """
            {"video-a":{"Code":"ABC-123","Title":"旧片","Source":"TMDB","Rating":"8.1/10","Genres":["剧情"]},
             "video-b":{"Code":"","Title":"第二部","Source":"TVmaze","Actors":["演员"]}}
            """;
        File.WriteAllText(metadataPath, legacyMetadata);
        var records = MetadataStore.Load(root);
        checks["legacyMetadataUsesVersionOne"] = records.Count == 2
            && records.Values.All(value => value.SchemaVersion == 1);
        MetadataStore.Save(root, records);
        var upgraded = MetadataStore.Load(root);
        checks["legacyMetadataPreservesFields"] = upgraded.Count == 2
            && upgraded.Values.All(value => value.SchemaVersion == 2)
            && upgraded["video-a"].Code == "ABC-123"
            && upgraded["video-a"].Genres.SequenceEqual(["剧情"])
            && upgraded["video-b"].Actors.SequenceEqual(["演员"]);
        var metadataSnapshot = File.ReadAllText(metadataPath);
        MetadataStore.Save(root, upgraded);
        checks["metadataMigrationRepeatedSaveIsStable"] = File.ReadAllText(metadataPath) == metadataSnapshot;
        File.WriteAllText(metadataPath, "{\"video-a\":{\"SchemaVersion\":3,\"Title\":\"新版\"}}");
        var unsupportedMetadata = File.ReadAllText(metadataPath);
        try { MetadataStore.Save(root, upgraded); }
        catch (InvalidDataException) { }
        checks["futureMetadataRefusesOverwrite"] = File.ReadAllText(metadataPath) == unsupportedMetadata;
        return checks;
    }
}
