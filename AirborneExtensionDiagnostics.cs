using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

/// <summary>
/// Headless assertions for <see cref="AirborneExtensionStore"/>.
///
/// Everything runs inside a random <c>%TEMP%</c> folder and is removed in
/// <c>finally</c>. No real Edge profile and no real data directory is ever
/// read or written; the only Edge-related call permitted here is a
/// <see cref="Directory.Exists(string)"/> probe used for a report note.
/// </summary>
internal static class AirborneExtensionDiagnostics
{
    const string ExtensionId = "khkeolgobhdoloioehjgfpobjnmagfha";

    internal static int RunToFile(string resultPath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var notes = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "YinggeAirborneTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "源 目录 Edge 扩展");
            var dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(Path.Combine(source, "_metadata"));
            Directory.CreateDirectory(Path.Combine(source, "inject"));
            WriteManifest(source, "1.0.0");
            File.WriteAllText(Path.Combine(source, "content.js"), "console.log('airborne');");
            File.WriteAllText(Path.Combine(source, "inject", "helper.js"), "window.__airborne = 1;");
            File.WriteAllText(Path.Combine(source, "_metadata", "verified_contents.json"), "{\"description\":\"edge\"}");
            File.WriteAllText(Path.Combine(source, "_metadata", "computed_hashes.json"), "{\"files\":{}}");
            var sourceFiles = CountFiles(source);
            notes.Add($"sourceFiles={sourceFiles}");

            var beforeFirst = AirborneExtensionStore.SnapshotFingerprint(source);
            var first = AirborneExtensionStore.EnsureManagedCopy(source, dataDir, out var firstError);
            var afterFirst = AirborneExtensionStore.SnapshotFingerprint(source);
            checks["sourceFolderUnchangedAfterLoad"] = first is not null &&
                SameFingerprint(beforeFirst, afterFirst) && beforeFirst.Count == sourceFiles;
            notes.Add("firstCopy=" + (first ?? "<null>") + (firstError is null ? "" : " error=" + firstError));
            checks["managedCopyLivesUnderDataDirectory"] = first is not null &&
                AirborneExtensionStore.IsManagedPath(first, dataDir) &&
                Path.GetFullPath(first).StartsWith(Path.GetFullPath(Path.Combine(dataDir, "extensions", "airborne")) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            checks["metadataNotCopiedIntoManagedCopy"] = first is not null &&
                !Directory.Exists(Path.Combine(first, "_metadata")) &&
                !AirborneExtensionStore.HashPayload(first).Keys.Any(key =>
                    key.Contains("_metadata", StringComparison.OrdinalIgnoreCase));

            var beforeSecond = AirborneExtensionStore.SnapshotFingerprint(source);
            var second = AirborneExtensionStore.EnsureManagedCopy(source, dataDir, out _);
            var afterSecond = AirborneExtensionStore.SnapshotFingerprint(source);
            checks["managedCopyIsIdempotent"] = first is not null && second is not null &&
                string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase) &&
                CountFiles(second) == CountFiles(first) &&
                SameFingerprint(beforeSecond, afterSecond);
            notes.Add("payloadFiles=" + (first is null ? "<null>" : CountFiles(first).ToString()));

            // A loadable manifest alone must not authorise loading the folder itself:
            // the raw source is accepted as a copy source yet refused as a load path.
            checks["rawSourcePathRejectedBeforeLoad"] =
                AirborneExtensionStore.ValidateFolderForLoad(source, dataDir) == false &&
                File.Exists(Path.Combine(source, "manifest.json")) && first is not null &&
                AirborneExtensionStore.ValidateFolderForLoad(first, dataDir) == true;

            // Edge-style folder, plus a sibling that only shares the store name prefix.
            var edgeFolder = Path.Combine(root, "edge-user-data", "Default", "Extensions", ExtensionId, "1.0.0");
            Directory.CreateDirectory(Path.Combine(edgeFolder, "_metadata"));
            WriteManifest(edgeFolder, "1.0.0");
            File.WriteAllText(Path.Combine(edgeFolder, "_metadata", "computed_hashes.json"), "{}");
            var lookalike = Path.Combine(dataDir, "extensions", "airborne-fake", "1.0.0");
            Directory.CreateDirectory(lookalike);
            WriteManifest(lookalike, "1.0.0");
            checks["edgeFolderIsNeverPassedToAddBrowserExtension"] =
                AirborneExtensionStore.ValidateFolderForLoad(edgeFolder, dataDir) == false &&
                AirborneExtensionStore.ValidateFolderForLoad(lookalike, dataDir) == false &&
                AirborneExtensionStore.IsManagedPath(edgeFolder, dataDir) == false &&
                AirborneExtensionStore.IsManagedPath(lookalike, dataDir) == false &&
                AirborneExtensionStore.ValidateFolderForLoad(first, dataDir) == true;

            // Upgrade: each manifest version change must produce a new generation.
            WriteManifest(source, "1.1.0");
            var beforeUpgradeOne = AirborneExtensionStore.SnapshotFingerprint(source);
            var upgraded = AirborneExtensionStore.EnsureManagedCopy(source, dataDir, out var upgradeError);
            var afterUpgradeOne = AirborneExtensionStore.SnapshotFingerprint(source);
            WriteManifest(source, "1.2.0");
            var beforeUpgradeTwo = AirborneExtensionStore.SnapshotFingerprint(source);
            var upgradedAgain = AirborneExtensionStore.EnsureManagedCopy(source, dataDir, out var upgradeError2);
            var afterUpgradeTwo = AirborneExtensionStore.SnapshotFingerprint(source);
            var upgradeMessage = upgradeError ?? upgradeError2;
            notes.Add("upgrade=" + (upgraded ?? "<null>") + " / " + (upgradedAgain ?? "<null>") +
                (upgradeMessage is null ? "" : " error=" + upgradeMessage));
            checks["sourceUpgradeCreatesNewManagedVersion"] = first is not null && upgraded is not null && upgradedAgain is not null &&
                !string.Equals(Path.GetFullPath(upgraded), Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFullPath(upgradedAgain), Path.GetFullPath(upgraded), StringComparison.OrdinalIgnoreCase) &&
                AirborneExtensionStore.IsManagedPath(upgradedAgain, dataDir) &&
                SameFingerprint(beforeUpgradeOne, afterUpgradeOne) &&
                SameFingerprint(beforeUpgradeTwo, afterUpgradeTwo);
            var generations = AirborneExtensionStore.ManagedGenerations(source, dataDir);
            notes.Add("generations=" + string.Join(",", generations.Select(Path.GetFileName)));
            var firstFullPath = first is null ? null : Path.GetFullPath(first);
            checks["metadataPrunedToTwoGenerations"] = generations.Count == AirborneExtensionStore.RetainedGenerations &&
                generations.All(path => !string.Equals(Path.GetFullPath(path), firstFullPath, StringComparison.OrdinalIgnoreCase));

            // Corrupted managed copy: repair by re-copying, or reject cleanly.
            var corrupted = Path.Combine(upgradedAgain!, "content.js");
            File.Delete(corrupted);
            var repaired = AirborneExtensionStore.EnsureManagedCopy(source, dataDir, out var repairError);
            var repairedIntact = repaired is not null && File.Exists(Path.Combine(repaired, "content.js")) &&
                SameFingerprint(AirborneExtensionStore.HashPayload(source), AirborneExtensionStore.HashPayload(repaired));
            var stagingLeftovers = Directory.EnumerateDirectories(AirborneExtensionStore.ManagedRoot(dataDir),
                "*.staging-*", SearchOption.AllDirectories).ToList();
            checks["corruptedManagedCopyIsRepairedOrRejected"] =
                (repairedIntact || (repaired is null && !string.IsNullOrWhiteSpace(repairError))) &&
                stagingLeftovers.Count == 0;
            notes.Add("repaired=" + (repaired ?? "<null>") + (repairError is null ? "" : " error=" + repairError));

            Write(resultPath, checks, notes);
            return checks.Values.All(value => value) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Write(resultPath, checks, notes, ex);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* Best effort: the folder lives under %TEMP%. */ }
        }
    }

    static void WriteManifest(string folder, string version) =>
        File.WriteAllText(Path.Combine(folder, "manifest.json"), $$"""
            {
              "manifest_version": 3,
              "name": "小电视空降助手",
              "version": "{{version}}",
              "content_scripts": [
                { "matches": ["*://www.bilibili.com/*"], "js": ["content.js"] }
              ],
              "options_ui": { "page": "options/options.html" }
            }
            """);

    static int CountFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();

    static bool SameFingerprint(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var hash) && string.Equals(hash, pair.Value, StringComparison.OrdinalIgnoreCase));

    static void Write(string resultPath, Dictionary<string, bool> checks, List<string> notes, Exception? failure = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(resultPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new
        {
            passed = failure is null && checks.Count > 0 && checks.Values.All(value => value),
            checks,
            notes,
            error = failure?.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
