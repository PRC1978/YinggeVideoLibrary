using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class LibraryRelocationDiagnostics
{
    internal static IReadOnlyDictionary<string, object> Verify(string parentDirectory)
    {
        var findings = new Dictionary<string, object>(StringComparer.Ordinal);
        var fixture = Path.Combine(parentDirectory, "relocation-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(fixture, "data");
        var oldRoot = Path.Combine(fixture, "old");
        var newRoot = Path.Combine(fixture, "new");
        try
        {
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(Path.Combine(oldRoot, "Actor"));
            Directory.CreateDirectory(Path.Combine(newRoot, "Actor"));
            Directory.CreateDirectory(Path.Combine(newRoot, "Duplicate"));
            var oldA = Path.Combine(oldRoot, "Actor", "same.mp4");
            var oldB = Path.Combine(oldRoot, "different.mkv");
            var oldC = Path.Combine(oldRoot, "unique.avi");
            var newA = Path.Combine(newRoot, "Actor", "same.mp4");
            var duplicateA = Path.Combine(newRoot, "Duplicate", "same.mp4");
            var newB = Path.Combine(newRoot, "different.mkv");
            var newC = Path.Combine(newRoot, "unique.avi");
            File.WriteAllBytes(oldA, [1, 2, 3, 4]);
            File.WriteAllBytes(oldB, [4, 5, 6, 7]);
            File.WriteAllBytes(oldC, [8, 9, 10, 11]);
            File.Copy(oldA, newA);
            File.Copy(oldA, duplicateA);
            File.WriteAllBytes(newB, [7, 6, 5, 4]);
            File.Copy(oldC, newC);
            var oldIds = new[] { oldA, oldB, oldC }.Select(LibraryRelocationService.IdForPath).ToArray();
            var newIds = new[] { newA, newB, newC }.Select(LibraryRelocationService.IdForPath).ToArray();
            var config = new LibraryConfig
            {
                Folders = [oldRoot], Favorites = [oldIds[0]],
                Categories = new() { [oldIds[0]] = "演员 / 作品" },
                MultiCategories = new() { [oldIds[0]] = ["演员 / 作品"] },
                Tags = new() { [oldIds[1]] = ["手工标签"] },
                AiReviews = new() { [oldIds[2]] = new AiReviewRecord
                    { VideoId = oldIds[2], Status = "approved", CandidateTitle = "搜刮标题", CandidateSource = "JavDB" } }
            };
            File.WriteAllText(Path.Combine(data, "config.json"), JsonSerializer.Serialize(config));
            var index = new LibraryIndex
            {
                Folders = [oldRoot], Entries = new[] { oldA, oldB, oldC }.Select(path => new IndexEntry
                {
                    Path = path, Size = new FileInfo(path).Length,
                    ModifiedTicks = new FileInfo(path).LastWriteTime.Ticks
                }).ToList()
            };
            File.WriteAllText(Path.Combine(data, "library-index.json"), JsonSerializer.Serialize(index));
            MetadataStore.Save(data, new Dictionary<string, MovieMetadata>
                { [oldIds[0]] = new() { Title = "已审核作品", Source = "JavDB" } });
            PersonalRatingStore.Set(data, oldIds[1], 8.5);
            ScrapeQueueStore.Add(data, [new ScrapeQueueItem
                { VideoId = oldIds[2], Kind = "title", SearchTitle = "待搜刮标题" }]);
            var before = new Dictionary<string, ReviewItemSnapshot>
                { [oldIds[0]] = new() { Tags = ["旧"] } };
            var after = new Dictionary<string, ReviewItemSnapshot>
                { [oldIds[0]] = new() { Tags = ["新"] } };
            ReviewTransactionStore.Append(data, ReviewTransactionStore.Create("approve", before, after));
            Directory.CreateDirectory(Path.Combine(data, "posters"));
            File.WriteAllBytes(Path.Combine(data, "posters", oldIds[0] + ".jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
            var aiEndpoint = "http://localhost:11117/v1/chat/completions";
            var aiRequest = "{\"messages\":[{\"role\":\"user\",\"content\":\"old Actor folder\"}]}";
            var pendingAiId = AiJobStore.Create(data, "classification", aiEndpoint, aiRequest).Id;
            var completedAiId = AiJobStore.Create(data, "tags", aiEndpoint, aiRequest).Id;
            var completedAiAttempt = AiJobStore.Begin(data, completedAiId, aiEndpoint, aiRequest);
            AiJobStore.Finish(data, completedAiId, completedAiAttempt.AttemptId,
                AiJobStore.Completed, resultJson: "{\"suggestion\":\"old Actor folder\"}");

            var prepared = LibraryRelocationService.PrepareSourceManifest(data, oldRoot);
            findings["relocationSourceFingerprintPrepared"] = prepared.Entries.Count == 3
                && prepared.Entries.All(entry => entry.Sha256.Length == 64);
            Directory.Move(oldRoot, oldRoot + "-offline");
            var snapshot = Directory.EnumerateFiles(data, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith("scrape-queue.", StringComparison.Ordinal))
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            var preview = LibraryRelocationService.CreatePreview(data, oldRoot, newRoot);
            findings["relocationPreviewReadOnlyAndOffline"] = snapshot.All(pair =>
                    File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
                && preview.Rows.Count == 3 && preview.SourceManifestSha256.Length == 64;
            findings["relocationSameNameDifferentContentManual"] = preview.Rows.Single(row =>
                row.Source.OldVideoId == oldIds[1]).Kind == LibraryRelocationService.MatchKind.DifferentContent;
            findings["relocationDuplicateContentAmbiguous"] = preview.Rows.Single(row =>
                row.Source.OldVideoId == oldIds[0]).Kind == LibraryRelocationService.MatchKind.Ambiguous;
            findings["relocationUniqueContentAutomatic"] = preview.Rows.Single(row =>
                row.Source.OldVideoId == oldIds[2]).Kind == LibraryRelocationService.MatchKind.Exact;
            try
            {
                LibraryRelocationService.StageConfirmedRelocation(preview, []);
                findings["relocationRequiresManualDecisions"] = false;
            }
            catch (InvalidOperationException)
            {
                findings["relocationRequiresManualDecisions"] = !File.Exists(
                    Path.Combine(data, "restore-pending.json"));
            }
            var decisions = new[]
            {
                new LibraryRelocationService.Decision(oldIds[0], newA),
                new LibraryRelocationService.Decision(oldIds[1], newB, true)
            };
            var confirmation = LibraryRelocationService.StageConfirmedRelocation(preview, decisions);
            findings["relocationStageLeavesLiveStoresUnchanged"] = confirmation.VideoCount == 3
                && File.Exists(Path.Combine(data, "restore-pending.json"))
                && File.Exists(Path.Combine(data, "library-relocation-pending.json"))
                && AiJobStore.Get(data, pendingAiId).Status == AiJobStore.Created
                && AiJobStore.Get(data, pendingAiId).ProtectedRequest is not null
                && snapshot.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value));
            File.WriteAllBytes(newC, [0, 0, 0, 0]);
            var changedBlocked = !LibraryRelocationService.ApplyPendingAtStartup(data,
                out var changedError, requireSingleInstance: false)
                && changedError.Contains("发生变化", StringComparison.Ordinal)
                && File.Exists(Path.Combine(data, "restore-pending.json"))
                && snapshot.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value));
            File.WriteAllBytes(newC, [8, 9, 10, 11]);
            findings["relocationTargetChangedBeforeStartupBlocked"] = changedBlocked;
            var applied = LibraryRelocationService.ApplyPendingAtStartup(data, out var error,
                requireSingleInstance: false);
            var moved = JsonSerializer.Deserialize<LibraryConfig>(
                File.ReadAllText(Path.Combine(data, "config.json")))!;
            var movedIndex = JsonSerializer.Deserialize<LibraryIndex>(
                File.ReadAllText(Path.Combine(data, "library-index.json")))!;
            var reviews = ReviewTransactionStore.Load(data, out var invalid);
            findings["relocationPreservesLinkedData"] = applied && error.Length == 0
                && moved.Folders.Contains(newRoot, StringComparer.OrdinalIgnoreCase)
                && !moved.Folders.Contains(oldRoot, StringComparer.OrdinalIgnoreCase)
                && movedIndex.Entries.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(new[] { newA, newB, newC })
                && moved.Categories.GetValueOrDefault(newIds[0]) == "演员 / 作品"
                && moved.Tags.GetValueOrDefault(newIds[1])?.Contains("手工标签") == true
                && moved.AiReviews.GetValueOrDefault(newIds[2])?.VideoId == newIds[2]
                && MetadataStore.Load(data).GetValueOrDefault(newIds[0])?.Title == "已审核作品"
                && PersonalRatingStore.Get(data, newIds[1]) == 8.5
                && ScrapeQueueStore.Load(data).Single().VideoId == newIds[2]
                && invalid == 0 && reviews.Count == 1
                && reviews[0].Before.ContainsKey(newIds[0])
                && File.Exists(Path.Combine(data, "posters", newIds[0] + ".jpg"))
                && !File.Exists(Path.Combine(data, "posters", oldIds[0] + ".jpg"));
            var relocatedPendingAi = AiJobStore.Get(data, pendingAiId);
            var relocatedCompletedAi = AiJobStore.Get(data, completedAiId);
            var pendingAiCannotRetry = false;
            try { AiJobStore.ReadProtectedRequest(relocatedPendingAi); }
            catch (InvalidOperationException) { pendingAiCannotRetry = true; }
            findings["relocationQuarantinesOldAiRequests"] =
                relocatedPendingAi.Status == AiJobStore.Canceled
                && relocatedPendingAi.LastErrorCode == "relocated-request"
                && relocatedPendingAi.ProtectedRequest is null
                && pendingAiCannotRetry
                && relocatedCompletedAi.Status == AiJobStore.Completed
                && relocatedCompletedAi.ProtectedRequest is null
                && relocatedCompletedAi.ResultJson == "{\"suggestion\":\"old Actor folder\"}";
            findings["relocationStartupIdempotent"] = LibraryRelocationService.ApplyPendingAtStartup(data,
                out var secondError, requireSingleInstance: false) && secondError.Length == 0;

            var onlineData = Path.Combine(fixture, "online-data");
            var onlineOld = Path.Combine(fixture, "online-old");
            var onlineNew = Path.Combine(fixture, "online-new");
            Directory.CreateDirectory(onlineData);
            Directory.CreateDirectory(onlineOld);
            Directory.CreateDirectory(onlineNew);
            var onlineOldFile = Path.Combine(onlineOld, "video.mp4");
            var onlineNewFile = Path.Combine(onlineNew, "video.mp4");
            File.WriteAllBytes(onlineOldFile, [20, 30, 40]);
            File.Copy(onlineOldFile, onlineNewFile);
            File.WriteAllText(Path.Combine(onlineData, "config.json"), JsonSerializer.Serialize(
                new LibraryConfig { Folders = [onlineOld] }));
            File.WriteAllText(Path.Combine(onlineData, "library-index.json"), JsonSerializer.Serialize(
                new LibraryIndex { Folders = [onlineOld], Entries = [new IndexEntry
                {
                    Path = onlineOldFile, Size = 3,
                    ModifiedTicks = new FileInfo(onlineOldFile).LastWriteTime.Ticks
                }] }));
            var online = LibraryRelocationService.CreatePreview(onlineData, onlineOld, onlineNew);
            var onlineConfirmed = LibraryRelocationService.StageConfirmedRelocation(online, []);
            findings["relocationOnlinePreviewCanConfirm"] = online.AutomaticCount == 1
                && onlineConfirmed.VideoCount == 1
                && LibraryRelocationService.ApplyPendingAtStartup(onlineData,
                    out var onlineError, requireSingleInstance: false)
                && onlineError.Length == 0;
        }
        catch (Exception ex)
        {
            findings["relocationDiagnosticException"] = ex.GetType().Name + ": " + ex.Message;
        }
        finally { try { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); } catch { } }
        return findings;
    }
}
