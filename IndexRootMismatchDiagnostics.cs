using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class IndexRootMismatchDiagnostics
{
    internal static Dictionary<string, bool> Verify(string temporaryRoot)
    {
        var fullRoot = Path.GetFullPath(temporaryRoot);
        var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("索引安全诊断只允许写入系统临时目录。 ");

        var directory = Path.Combine(fullRoot, "index-safety-" + Guid.NewGuid().ToString("N"));
        var oldFolder = Path.Combine(directory, "old-media");
        var newEmptyFolder = Path.Combine(directory, "new-empty-media");
        Directory.CreateDirectory(oldFolder);
        Directory.CreateDirectory(newEmptyFolder);
        var oldVideo = Path.Combine(oldFolder, "old.mp4");
        File.WriteAllText(oldVideo, "fixture");
        var path = Path.Combine(directory, "library-index.json");
        var old = new LibraryIndex
        {
            Folders = [oldFolder],
            Entries = [new IndexEntry { Path = oldVideo, Size = 7, ModifiedTicks = DateTime.UtcNow.Ticks }]
        };
        File.WriteAllText(path, JsonSerializer.Serialize(old));
        var originalHash = Hash(path);

        var newEmpty = new LibraryIndex { Folders = [newEmptyFolder], Entries = [] };
        var preserved = MainWindow.WriteIndexSnapshotAtPath(path, newEmpty);
        var oldStillPresent = JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(path));
        var protectedDifferentEmpty = preserved.PreservedMismatchedIndex && preserved.PreviousEntryCount == 1 &&
            Hash(path) == originalHash && File.Exists(path + ".last-good") &&
            Hash(path + ".last-good") == originalHash &&
            oldStillPresent?.Entries.Count == 1 &&
            newEmpty.Entries.Count == 0 && !Directory.EnumerateFiles(newEmptyFolder).Any();

        var sameRootEmpty = new LibraryIndex { Folders = [oldFolder], Entries = [] };
        var sameRootOutcome = MainWindow.WriteIndexSnapshotAtPath(path, sameRootEmpty);
        var sameRootClears = !sameRootOutcome.PreservedMismatchedIndex &&
            JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(path))?.Entries.Count == 0 &&
            JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(path + ".bak"))?.Entries.Count == 1;

        var newVideo = Path.Combine(newEmptyFolder, "new.mp4");
        File.WriteAllText(newVideo, "fixture");
        var differentPopulated = new LibraryIndex
        {
            Folders = [newEmptyFolder],
            Entries = [new IndexEntry { Path = newVideo, Size = 7, ModifiedTicks = DateTime.UtcNow.Ticks }]
        };
        var populatedOutcome = MainWindow.WriteIndexSnapshotAtPath(path, differentPopulated);
        var differentPopulatedSaved = !populatedOutcome.PreservedMismatchedIndex &&
            JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(path)) is { Entries.Count: 1 } loaded &&
            loaded.Entries[0].Path == newVideo;

        // An empty current index is never held back merely because roots differ.
        File.WriteAllText(path, JsonSerializer.Serialize(sameRootEmpty));
        var emptyOldOutcome = MainWindow.WriteIndexSnapshotAtPath(path, newEmpty);
        var emptyOldAllowsEmpty = !emptyOldOutcome.PreservedMismatchedIndex &&
            JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(path)) is { Entries.Count: 0 } empty &&
            empty.Folders.Single() == newEmptyFolder;

        // Clearing the folder list in settings is an explicit user action.
        // It must still be able to replace a nonempty index with an empty one.
        var clearPath = Path.Combine(directory, "explicit-clear-index.json");
        File.WriteAllText(clearPath, JsonSerializer.Serialize(old));
        var explicitClearOutcome = MainWindow.WriteIndexSnapshotAtPath(clearPath,
            new LibraryIndex { Folders = [], Entries = [] }, explicitlyClearedByUser: true);
        var explicitClearWorks = !explicitClearOutcome.PreservedMismatchedIndex &&
            JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(clearPath)) is
                { Folders.Length: 0, Entries.Count: 0 } &&
            JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(clearPath + ".bak")) is { Entries.Count: 1 };

        // A corrupt primary is not permission to erase a valid recovery copy.
        // Test both recovery locations independently because the older .bak
        // file may itself be absent or unusable.
        var damagedBackupPath = Path.Combine(directory, "damaged-with-backup.json");
        File.WriteAllText(damagedBackupPath, "{broken");
        File.WriteAllText(damagedBackupPath + ".bak", JsonSerializer.Serialize(old));
        var backupHash = Hash(damagedBackupPath + ".bak");
        var corruptPrimaryHash = Hash(damagedBackupPath);
        var backupOutcome = MainWindow.WriteIndexSnapshotAtPath(damagedBackupPath, newEmpty);
        var backupPreserved = backupOutcome.PreservedMismatchedIndex && backupOutcome.PreviousEntryCount == 1 &&
            Hash(damagedBackupPath) == corruptPrimaryHash && Hash(damagedBackupPath + ".bak") == backupHash;

        var damagedLastGoodPath = Path.Combine(directory, "damaged-with-last-good.json");
        File.WriteAllText(damagedLastGoodPath, "{broken");
        File.WriteAllText(damagedLastGoodPath + ".last-good", JsonSerializer.Serialize(old));
        var lastGoodHash = Hash(damagedLastGoodPath + ".last-good");
        corruptPrimaryHash = Hash(damagedLastGoodPath);
        var lastGoodOutcome = MainWindow.WriteIndexSnapshotAtPath(damagedLastGoodPath, newEmpty);
        var lastGoodPreserved = lastGoodOutcome.PreservedMismatchedIndex && lastGoodOutcome.PreviousEntryCount == 1 &&
            Hash(damagedLastGoodPath) == corruptPrimaryHash &&
            Hash(damagedLastGoodPath + ".last-good") == lastGoodHash;

        return new Dictionary<string, bool>
        {
            ["differentEmptyRootPreservesOldIndexAndSnapshot"] = protectedDifferentEmpty,
            ["sameRootCanClearAllVideos"] = sameRootClears,
            ["differentPopulatedRootCanReplaceIndex"] = differentPopulatedSaved,
            ["emptyOldIndexDoesNotBlockEmptyRoot"] = emptyOldAllowsEmpty,
            ["explicitFolderClearStillSavesEmptyIndex"] = explicitClearWorks,
            ["corruptPrimaryPreservesBackup"] = backupPreserved,
            ["corruptPrimaryPreservesLastGood"] = lastGoodPreserved
        };
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
