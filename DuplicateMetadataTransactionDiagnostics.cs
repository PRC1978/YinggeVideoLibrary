using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

internal static class DuplicateMetadataTransactionDiagnostics
{
    internal static async Task<IReadOnlyDictionary<string, bool>> VerifyAsync(string root)
    {
        Directory.CreateDirectory(root);
        var media = Path.Combine(root, "media");
        Directory.CreateDirectory(media);
        var a = Path.Combine(media, "a.mp4");
        var b = Path.Combine(media, "b.mp4");
        var bytes = Encoding.UTF8.GetBytes("exact same media bytes");
        File.WriteAllBytes(a, bytes);
        File.WriteAllBytes(b, bytes);
        var inputs = new[]
        {
            new DuplicateVideoInput("keeper", a, "作品", bytes.Length, ["人物"], ["保留"]),
            new DuplicateVideoInput("other", b, "作品", bytes.Length, ["系列"], ["待整理"])
        };
        var ratings = new Dictionary<string, PersonalRatingEntry>
        {
            ["keeper"] = new() { Score = 8, UpdatedAtUtc = DateTime.UnixEpoch },
            ["other"] = new() { Score = 7, UpdatedAtUtc = DateTime.UnixEpoch }
        };
        var scan = await DuplicateVideoScanner.ScanAsync(inputs, ratings, null, CancellationToken.None);
        var group = scan.Groups.Single();
        var keep = group.Videos.Single(video => video.Id == "keeper");

        var normal = Path.Combine(root, "normal");
        Seed(normal);
        var precondition = false;
        try { DuplicateMetadataTransactionStore.Prepare(normal, group, keep, 8, explicitScoreChoice: false); }
        catch (InvalidOperationException) { precondition = !DuplicateMetadataTransactionStore.HasPending(normal); }
        var plan = DuplicateMetadataTransactionStore.Prepare(normal, group, keep, 7, explicitScoreChoice: true);
        var preview = DuplicateMetadataTransactionStore.Preview(plan);
        await DuplicateMetadataTransactionStore.CommitAsync(normal, plan, CancellationToken.None);
        var committed = State(normal, "keeper", ["人物", "系列"], ["保留", "待整理"], 7)
            && State(normal, "other", ["系列"], ["待整理"], 7)
            && !DuplicateMetadataTransactionStore.HasPending(normal)
            && DuplicateMetadataTransactionStore.History(normal).Count == 1
            && preview.Contains(a, StringComparison.Ordinal) && preview.Contains(group.Sha256, StringComparison.Ordinal)
            && File.ReadAllBytes(a).SequenceEqual(bytes) && File.ReadAllBytes(b).SequenceEqual(bytes);
        var backed = LibraryBackupService.Create(normal, forceDefault: true);
        var backupIncludesHistory = File.Exists(Path.Combine(backed, "duplicate-metadata-history.json"))
            && LibraryBackupService.Verify(backed).IsValid;
        File.Delete(Path.Combine(normal, "duplicate-metadata-history.json"));
        LibraryBackupService.StageRestore(normal, backed);
        var backupRestored = LibraryBackupService.ApplyPendingAtStartup(normal, out var backupError,
                requireSingleInstance: false)
            && backupError.Length == 0
            && DuplicateMetadataTransactionStore.History(normal).Count == 1;
        await DuplicateMetadataTransactionStore.UndoLastAsync(normal, CancellationToken.None);
        var undone = State(normal, "keeper", ["人物"], ["保留"], 8)
            && DuplicateMetadataTransactionStore.LastUndoable(normal) is null
            && DuplicateMetadataTransactionStore.History(normal).Count == 2;

        var relocationDir = Path.Combine(root, "relocation-history");
        Directory.CreateDirectory(relocationDir);
        var relocationHistory = Path.Combine(relocationDir, "duplicate-metadata-history.json");
        var originalHistory = Path.Combine(normal, "duplicate-metadata-history.json");
        var originalHistoryBytes = File.ReadAllBytes(originalHistory);
        File.Copy(originalHistory, relocationHistory);
        var relocatedA = Path.Combine(media, "relocated-a.mp4");
        var relocatedB = Path.Combine(media, "relocated-b.mp4");
        DuplicateMetadataTransactionStore.RelocateHistoryFile(relocationHistory, value => value switch
        {
            "keeper" => "keeper-new", "other" => "other-new",
            _ when value == a => relocatedA,
            _ when value == b => relocatedB,
            _ => value
        });
        DuplicateMetadataTransactionStore.VerifyHistoryFile(relocationHistory);
        var relocatedEntries = DuplicateMetadataTransactionStore.History(relocationDir);
        var relocated = relocatedEntries.Count == 2 && relocatedEntries.All(entry =>
            entry.KeepId == "keeper-new" && entry.KeepPath == relocatedA &&
            entry.Evidence.Select(file => file.VideoId).Order().SequenceEqual(new[] { "keeper-new", "other-new" }) &&
            entry.Evidence.Select(file => file.Path).Order().SequenceEqual(new[] { relocatedA, relocatedB }.Order()) &&
            entry.Evidence.All(file => file.Sha256 == group.Sha256)) &&
            File.ReadAllBytes(originalHistory).SequenceEqual(originalHistoryBytes);
        var tamperedHistory = JsonNode.Parse(File.ReadAllText(relocationHistory))!.AsArray();
        tamperedHistory[0]!["Sha256"] = new string('0', 64);
        File.WriteAllText(relocationHistory, tamperedHistory.ToJsonString());
        var rejectedHistory = false;
        try { DuplicateMetadataTransactionStore.VerifyHistoryFile(relocationHistory); }
        catch (InvalidDataException) { rejectedHistory = true; }

        var cancelledDir = Path.Combine(root, "cancelled");
        Seed(cancelledDir);
        var cancelledPlan = DuplicateMetadataTransactionStore.Prepare(cancelledDir, group, keep, 7, true);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        var cancelled = false;
        try { await DuplicateMetadataTransactionStore.CommitAsync(cancelledDir, cancelledPlan, stopped.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        cancelled &= State(cancelledDir, "keeper", ["人物"], ["保留"], 8)
            && !DuplicateMetadataTransactionStore.HasPending(cancelledDir);

        var interruptedDir = Path.Combine(root, "interrupted");
        Seed(interruptedDir);
        var interrupted = DuplicateMetadataTransactionStore.Prepare(interruptedDir, group, keep, 7, true);
        DuplicateMetadataTransactionStore.SimulateCrashAfterConfigForDiagnostics(interruptedDir, interrupted);
        var partial = DuplicateMetadataTransactionStore.HasPending(interruptedDir)
            && State(interruptedDir, "keeper", ["人物", "系列"], ["保留", "待整理"], 8);
        var recovered = DuplicateMetadataTransactionStore.RecoverPendingAtStartup(interruptedDir, out var error)
            && error.Length == 0 && State(interruptedDir, "keeper", ["人物", "系列"], ["保留", "待整理"], 7)
            && !DuplicateMetadataTransactionStore.HasPending(interruptedDir)
            && DuplicateMetadataTransactionStore.History(interruptedDir).Count == 1;

        var changedMediaDir = Path.Combine(root, "changed-media");
        Seed(changedMediaDir);
        var changedMediaPlan = DuplicateMetadataTransactionStore.Prepare(changedMediaDir, group, keep, 7, true);
        DuplicateMetadataTransactionStore.SimulateCrashAfterConfigForDiagnostics(changedMediaDir, changedMediaPlan);
        File.WriteAllBytes(a, Encoding.UTF8.GetBytes("x" + Encoding.UTF8.GetString(bytes)[1..]));
        var changedMediaStops = !DuplicateMetadataTransactionStore.RecoverPendingAtStartup(changedMediaDir, out var mediaError)
            && mediaError.Contains("视频内容", StringComparison.Ordinal)
            && DuplicateMetadataTransactionStore.HasPending(changedMediaDir)
            && State(changedMediaDir, "keeper", ["人物", "系列"], ["保留", "待整理"], 8);
        File.WriteAllBytes(a, bytes);

        var conflictDir = Path.Combine(root, "external-conflict");
        Seed(conflictDir);
        var conflictPlan = DuplicateMetadataTransactionStore.Prepare(conflictDir, group, keep, 7, true);
        DuplicateMetadataTransactionStore.SimulateCrashAfterConfigForDiagnostics(conflictDir, conflictPlan);
        ChangeKeeperTags(conflictDir, ["外部修改"]);
        var conflictStops = !DuplicateMetadataTransactionStore.RecoverPendingAtStartup(conflictDir, out var conflictError)
            && conflictError.Contains("其他程序", StringComparison.Ordinal)
            && DuplicateMetadataTransactionStore.HasPending(conflictDir)
            && State(conflictDir, "keeper", ["人物", "系列"], ["外部修改"], 8);

        var staleDir = Path.Combine(root, "stale-plan");
        Seed(staleDir);
        var stale = DuplicateMetadataTransactionStore.Prepare(staleDir, group, keep, 7, true);
        ChangeKeeperTags(staleDir, ["后来修改"]);
        var staleStops = false;
        try { await DuplicateMetadataTransactionStore.CommitAsync(staleDir, stale, CancellationToken.None); }
        catch (InvalidOperationException)
        {
            staleStops = State(staleDir, "keeper", ["人物"], ["后来修改"], 8)
                && !DuplicateMetadataTransactionStore.HasPending(staleDir);
        }
        var offlineGuard = true;
        foreach (var markerName in new[]
        {
            "restore-pending.json", "restore-active.json", "import-pending.json", "import-active.json",
            "library-relocation-pending.json"
        })
        {
            var guardedDir = Path.Combine(root, "guard-" + markerName);
            Seed(guardedDir);
            var guardedPlan = DuplicateMetadataTransactionStore.Prepare(guardedDir, group, keep, 7, true);
            File.WriteAllText(Path.Combine(guardedDir, markerName), "{}");
            var blocked = false;
            try { await DuplicateMetadataTransactionStore.CommitAsync(guardedDir, guardedPlan, CancellationToken.None); }
            catch (InvalidOperationException) { blocked = true; }
            offlineGuard &= blocked && !DuplicateMetadataTransactionStore.HasPending(guardedDir)
                && State(guardedDir, "keeper", ["人物"], ["保留"], 8);
        }
        return new Dictionary<string, bool>
        {
            ["duplicateMergeRequiresExplicitRatingConflictChoice"] = precondition,
            ["duplicateMergeJournalKeepsOtherMediaAndMetadata"] = committed,
            ["duplicateMergeCrossRestartUndo"] = undone,
            ["duplicateMergeHistoryBackupRestoreUndo"] = backupIncludesHistory && backupRestored && undone,
            ["duplicateMergeRelocationResealsHistory"] = relocated,
            ["duplicateMergeRejectsTamperedHistory"] = rejectedHistory,
            ["duplicateMergeCancelledBeforeCommitWritesNothing"] = cancelled,
            ["duplicateMergeRecoversMixedConfigRatingCrash"] = partial && recovered,
            ["duplicateMergeChangedMediaBlocksRecovery"] = changedMediaStops,
            ["duplicateMergeExternalChangePreservesPendingEvidence"] = conflictStops,
            ["duplicateMergeStalePlanWritesNothing"] = staleStops,
            ["duplicateMergeBlocksPendingOfflineReplacement"] = offlineGuard
        };
    }

    static void Seed(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config.json"), JsonSerializer.Serialize(new
        {
            categories = new Dictionary<string, string> { ["keeper"] = "人物", ["other"] = "系列" },
            multiCategories = new Dictionary<string, string[]>
                { ["keeper"] = ["人物"], ["other"] = ["系列"] },
            tags = new Dictionary<string, string[]> { ["keeper"] = ["保留"], ["other"] = ["待整理"] }
        }));
        File.WriteAllText(Path.Combine(directory, "personal-ratings.json"), JsonSerializer.Serialize(
            new Dictionary<string, PersonalRatingEntry>
            {
                ["keeper"] = new() { Score = 8, UpdatedAtUtc = DateTime.UnixEpoch },
                ["other"] = new() { Score = 7, UpdatedAtUtc = DateTime.UnixEpoch }
            }));
    }

    static void ChangeKeeperTags(string directory, string[] tags)
    {
        var path = Path.Combine(directory, "config.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["tags"]!["keeper"] = JsonSerializer.SerializeToNode(tags);
        File.WriteAllText(path, root.ToJsonString());
    }

    static bool State(string directory, string id, string[] categories, string[] tags, double score)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "config.json")))!.AsObject();
        var paths = root["multiCategories"]![id]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        var currentTags = root["tags"]![id]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        return paths.SequenceEqual(categories) && currentTags.SequenceEqual(tags)
            && PersonalRatingStore.Get(directory, id) == score;
    }
}
