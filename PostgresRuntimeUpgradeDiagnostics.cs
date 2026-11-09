using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class PostgresRuntimeUpgradeDiagnostics
{
    internal static async Task<int> RunCommandAsync(string oldRuntime, string newRuntime,
        string source, string target, string report)
    {
        if (!Path.IsPathFullyQualified(report) || File.Exists(report)) return 2;
        report = Path.GetFullPath(report);
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        if (report.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var phases = new List<string>();
        try
        {
            var progress = new SynchronousProgress(phases);
            var upgraded = await PostgresRuntimeUpgrade.UpgradeAsync(oldRuntime, newRuntime,
                source, target, progress);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                passed = true, originalLibraryStillSelected = true, phases,
                upgraded.SourceMajor, upgraded.TargetMajor, upgraded.VideoCount,
                upgraded.MetadataCount, upgraded.SnapshotDirectory, upgraded.NewLibraryDirectory
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                passed = false, originalLibraryStillSelected = true, phases,
                errorType = error.GetType().Name,
                message = "升级未完成，原资料库没有被替换。请核对运行时主版本、新目标目录及原库是否已退出。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    internal static async Task<int> RunSelfTestAsync(string oldRuntime, string newRuntime, string report)
    {
        if (!Path.IsPathFullyQualified(report) || File.Exists(report)) return 2;
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!,
            "postgres-major-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = new Dictionary<string, bool>();
        var stage = "setup";
        string? failure = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            var fixture = Path.Combine(root, "fixture");
            PostgresBackupAcceptance.BuildFixtureForSelectiveImport(fixture, 12);
            var source = Path.Combine(root, "source");
            await using (var initial = await PostgresPrivateCluster.OpenAsync(oldRuntime, source))
            {
                await PostgresLibraryMigration.ImportAsync(fixture, initial.ConnectionString,
                    sourceLeasePath: fixture + ".import.lock");
                await File.WriteAllTextAsync(Path.Combine(source, PostgresStorageModeGuard.MarkerName), "postgresql-v1");
            }
            var versionPath = Path.Combine(source, "postgres-data", "PG_VERSION");
            var originalVersion = await File.ReadAllBytesAsync(versionPath);
            stage = "major-upgrade";
            var result = await PostgresRuntimeUpgrade.UpgradeAsync(oldRuntime, newRuntime,
                source, Path.Combine(root, "target"));
            checks["newMajorIsHigher"] = result.TargetMajor > result.SourceMajor;
            checks["allVideosImported"] = result.VideoCount == 12 && result.MetadataCount == 12;
            checks["snapshotValid"] = await PostgresLibraryExport.VerifyOfflineAsync(result.SnapshotDirectory);
            checks["sourceVersionRetained"] = originalVersion.SequenceEqual(await File.ReadAllBytesAsync(versionPath));
            await using (var reopened = await PostgresPrivateCluster.OpenAsync(oldRuntime, source))
                checks["sourceReopensAndMatches"] = await PostgresLibraryMigration.VerifyContentAsync(
                    result.SnapshotDirectory, reopened.ConnectionString);
            await using (var reopened = await PostgresPrivateCluster.OpenAsync(newRuntime, result.NewLibraryDirectory))
                checks["targetReopensAndMatches"] = await PostgresLibraryMigration.VerifyContentAsync(
                    result.SnapshotDirectory, reopened.ConnectionString);
            foreach (var afterImport in new[] { false, true })
            {
                stage = afterImport ? "failureAfterImport" : "failureAfterBackup";
                var target = Path.Combine(root, stage);
                var observed = false;
                Action injected = () => throw new IOException("Injected major upgrade interruption");
                try
                {
                    await PostgresRuntimeUpgrade.UpgradeAsync(oldRuntime, newRuntime, source, target,
                        afterBackup: afterImport ? null : injected, afterImport: afterImport ? injected : null);
                }
                catch (IOException) { observed = true; }
                checks[stage + "Observed"] = observed;
                checks[stage + "TargetSafe"] = afterImport
                    ? PostgresStorageModeGuard.Current(target) == PostgresStorageModeGuard.StorageMode.Blocked
                    : !Directory.Exists(target);
                await using var reopened = await PostgresPrivateCluster.OpenAsync(oldRuntime, source);
                checks[stage + "SourceIntact"] = await PostgresLibraryMigration.VerifyContentAsync(
                    result.SnapshotDirectory, reopened.ConnectionString);
            }
            async Task<bool> Refused(string oldPath, string newPath, string sourcePath, string targetPath)
            {
                try { await PostgresRuntimeUpgrade.UpgradeAsync(oldPath, newPath, sourcePath, targetPath); return false; }
                catch (InvalidDataException) { return true; }
            }
            stage = "guards";
            checks["sameMajorRefused"] = await Refused(oldRuntime, oldRuntime, source, Path.Combine(root, "same"));
            checks["downgradeRefused"] = await Refused(newRuntime, oldRuntime, result.NewLibraryDirectory, Path.Combine(root, "lower"));
            checks["wrongRuntimeRefused"] = await Refused(newRuntime, oldRuntime, source, Path.Combine(root, "wrong"));
            checks["existingTargetRefused"] = await Refused(oldRuntime, newRuntime, source, result.NewLibraryDirectory);
            await using (var active = await PostgresPrivateCluster.OpenAsync(oldRuntime, source))
            {
                var rejected = false;
                try { await PostgresRuntimeUpgrade.UpgradeAsync(oldRuntime, newRuntime, source, Path.Combine(root, "busy")); }
                catch (InvalidOperationException) { rejected = true; }
                checks["activeOwnerNotStopped"] = rejected && await PostgresLibraryMigration.VerifyContentAsync(
                    result.SnapshotDirectory, active.ConnectionString);
            }
            stage = "complete";
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
        var passed = stage == "complete" && checks.Values.All(value => value);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            passed, stage, failure, checks, elapsedMs = elapsed.ElapsedMilliseconds,
            realLibraryRead = false, realLibraryWritten = false, probeRoot = root
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    sealed class SynchronousProgress(List<string> phases) : IProgress<string>
    {
        public void Report(string value) => phases.Add(value);
    }
}
