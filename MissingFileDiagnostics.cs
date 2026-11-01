using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Offline acceptance for "not on disk" detection and the cleanup journal.
// Everything happens inside the caller-supplied work directory: no real library
// is opened and no video file is ever deleted by the product code exercised here.
internal static class MissingFileDiagnostics
{
    internal static int RunToFile(string workDirectory, string outPath)
    {
        var checks = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        var details = new List<string>();
        var observed = new Dictionary<string, object?>(StringComparer.Ordinal);
        var notExercised = new List<string>();
        var clock = Stopwatch.StartNew();

        void Check(string name, bool ok, string detail = "")
        {
            checks[name] = ok;
            if (!ok) details.Add(string.IsNullOrEmpty(detail) ? name : name + "：" + detail);
        }

        string work;
        try
        {
            work = Path.GetFullPath(workDirectory);
            var real = Path.GetFullPath(AppPaths.DefaultDataDirectory);
            var forbidden = work.Equals(real, StringComparison.OrdinalIgnoreCase) ||
                work.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            Check("workDirectoryIsolated", !forbidden, forbidden ? "工作目录指向真实库，已拒绝" : "");
            if (forbidden) return Finish(outPath, checks, details, observed, notExercised, clock, 1);
            Directory.CreateDirectory(work);
        }
        catch (Exception ex)
        {
            Check("workDirectoryIsolated", false, "工作目录无效：" + ex.Message);
            return Finish(outPath, checks, details, observed, notExercised, clock, 1);
        }

        var root = Path.Combine(work, "library");
        var otherRoot = Path.Combine(work, "other");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(otherRoot);
        var files = Enumerable.Range(1, 3).Select(index => Path.Combine(root, $"video-{index}.mp4")).ToList();
        foreach (var file in files) File.WriteAllText(file, "x");
        File.WriteAllText(Path.Combine(otherRoot, "outside.mp4"), "x");

        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scan = MainWindow.Scan([root], favorites);
        Check("realScanFindsFiles", scan.Items.Count == 3 && scan.IncompleteRoots.Count == 0,
            $"items={scan.Items.Count} incomplete={scan.IncompleteRoots.Count}");

        var previous = scan.Items.ToList();

        // --- one file deleted ------------------------------------------------
        File.Delete(files[0]);
        var afterDelete = MainWindow.Scan([root], favorites);
        var deletedList = afterDelete.Items.ToList();
        var (missing, unknown) = MainWindow.MergeAvailability(deletedList, previous, [root], afterDelete.IncompleteRoots);
        observed["missingAfterDelete"] = missing;
        Check("deletedFileBecomesMissing", afterDelete.Items.Count == 2 && missing == 1 && unknown == 0);
        Check("deletedEntryKeepsAvailability", deletedList.Single(item => item.IsMissing).AvailabilityText == "不在本地");
        Check("presentEntriesStayPresent", deletedList.Count(item => item.Availability == VideoAvailability.Present) == 2);

        // --- root unavailable (external drive unplugged) ---------------------
        var moved = root + "-offline";
        Directory.Move(root, moved);
        var offline = MainWindow.Scan([root], favorites);
        var offlineList = offline.Items.ToList();
        var (offlineMissing, offlineUnknown) = MainWindow.MergeAvailability(
            offlineList, previous, [root], offline.IncompleteRoots);
        observed["offlineMissing"] = offlineMissing;
        observed["offlineUnknown"] = offlineUnknown;
        Check("unavailableRootIsNotMissing", offline.IncompleteRoots.Contains(root) && offlineMissing == 0);
        Check("unavailableRootKeepsRows", offlineUnknown == 3 && offlineList.All(item => item.IsUnavailable));
        Check("unavailableIsNotCleanupCandidate", offlineList.All(item => !item.IsMissing));

        // --- reconnected -----------------------------------------------------
        Directory.Move(moved, root);
        var reconnected = MainWindow.Scan([root], favorites);
        var reconnectedList = reconnected.Items.ToList();
        var (backMissing, backUnknown) = MainWindow.MergeAvailability(
            reconnectedList, offlineList, [root], reconnected.IncompleteRoots);
        // After reconnecting, the two files that still exist are present again and
        // the one deleted earlier is reported as missing, never as unavailable.
        Check("reconnectedRootClearsUnavailable", backUnknown == 0 &&
            reconnectedList.All(item => item.Availability != VideoAvailability.RootUnavailable));
        Check("reconnectedSeesFiles", reconnected.Items.Count == 2 && backMissing == 1);

        // --- a folder that is no longer configured ---------------------------
        var outside = new List<VideoItem>
        {
            new()
            {
                Id = "outside", Path = Path.Combine(otherRoot, "outside.mp4"), Title = "outside",
                Folder = otherRoot, Size = 1, Modified = DateTime.Now
            }
        };
        var keep = MainWindow.Scan([root], favorites).Items.ToList();
        MainWindow.MergeAvailability(keep, outside, [root], []);
        Check("unconfiguredRootDropped", keep.All(item => !string.Equals(item.Path, outside[0].Path, StringComparison.OrdinalIgnoreCase)));

        // --- index round-trip ------------------------------------------------
        var indexPath = Path.Combine(work, "library-index.json");
        var index = new LibraryIndex
        {
            Folders = [root],
            Entries =
            [
                new IndexEntry { Path = files[1], Size = 1, ModifiedTicks = DateTime.Now.Ticks, Missing = true },
                new IndexEntry { Path = files[2], Size = 1, ModifiedTicks = DateTime.Now.Ticks, Missing = false }
            ]
        };
        MainWindow.WriteIndexSnapshotAtPath(indexPath, index);
        var reloaded = JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(indexPath));
        Check("missingFlagRoundTripsIndex", reloaded is not null && reloaded.Entries.Count == 2 &&
            reloaded.Entries[0].Missing && !reloaded.Entries[1].Missing);

        // --- cleanup journal -------------------------------------------------
        var dataDir = Path.Combine(work, "data");
        Directory.CreateDirectory(dataDir);
        var entries = new[]
        {
            new MissingCleanupEntry { Path = files[0], Size = 1, ModifiedTicks = DateTime.Now.Ticks },
            new MissingCleanupEntry { Path = files[1], Size = 1, ModifiedTicks = DateTime.Now.Ticks }
        };
        var record = MissingCleanupStore.Begin(dataDir, entries);
        Check("cleanupBeginLeavesPending", MissingCleanupStore.Pending(dataDir)?.Id == record.Id);
        Check("cleanupJournalKeepsPaths", MissingCleanupStore.Pending(dataDir)?.Entries.Count == 2);

        MissingCleanupStore.Complete(dataDir, record);
        Check("cleanupCompleteClearsPending", MissingCleanupStore.Pending(dataDir) is null);
        Check("cleanupBecomesUndoable", MissingCleanupStore.HasUndoable(dataDir));
        var undone = MissingCleanupStore.UndoLast(dataDir);
        Check("cleanupUndoReturnsRecord", undone is not null && undone.Id == record.Id && undone.Entries.Count == 2);
        Check("cleanupUndoIsSingleUse", !MissingCleanupStore.HasUndoable(dataDir));

        var interrupted = MissingCleanupStore.Begin(dataDir, entries);
        MissingCleanupStore.MarkRolledBack(dataDir, interrupted.Id);
        Check("interruptedCleanupRollsBack", MissingCleanupStore.Pending(dataDir) is null &&
            !MissingCleanupStore.HasUndoable(dataDir));

        File.WriteAllText(Path.Combine(dataDir, MissingCleanupStore.FileName), "{ broken");
        var recovered = MissingCleanupStore.Load(dataDir);
        Check("damagedJournalDoesNotThrow", recovered.History.Count == 0 && recovered.PendingId is null);

        // The product code under test must never remove a video file.
        Check("cleanupNeverDeletesVideoFiles",
            File.Exists(files[1]) && File.Exists(files[2]) && File.Exists(Path.Combine(otherRoot, "outside.mp4")));

        var exitCode = checks.Values.All(value => value) ? 0 : 1;
        return Finish(outPath, checks, details, observed, notExercised, clock, exitCode);
    }

    static int Finish(string outPath, SortedDictionary<string, bool> checks, List<string> details,
        Dictionary<string, object?> observed, List<string> notExercised, Stopwatch clock, int exitCode)
    {
        var failed = checks.Where(pair => !pair.Value).Select(pair => pair.Key).ToList();
        var payload = new Dictionary<string, object?>
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("O"),
            ["passed"] = failed.Count == 0,
            ["checkCount"] = checks.Count,
            ["elapsedMs"] = clock.ElapsedMilliseconds,
            ["failedChecks"] = failed,
            ["failureDetails"] = details,
            ["notExercised"] = notExercised,
            ["observed"] = observed
        };
        foreach (var pair in checks) payload[pair.Key] = pair.Value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("无法写入自检结果：" + ex.Message);
            exitCode = 2;
        }
        Console.Out.WriteLine(failed.Count == 0
            ? $"缺失文件自检通过：{checks.Count} 项"
            : $"缺失文件自检失败：{failed.Count}/{checks.Count} 项 · {string.Join("、", failed)}");
        return exitCode;
    }
}
