using System.IO;
using System.Security.Cryptography;

namespace YinggeVideoLibrary;

internal static partial class UpdateBackupJanitor
{
    /// <summary>
    /// Runs every backup-retention assertion inside a throw-away directory and
    /// returns a diagnostic map. Used by <c>--self-test-updater</c> and by the
    /// update section of <c>--self-test-features</c>; the real install
    /// directory is never touched.
    /// </summary>
    internal static IReadOnlyDictionary<string, bool> SelfTestInto()
    {
        var findings = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["updateBackupKeepsNewestOnly"] = false,
            ["updateBackupReclaimsStaleOnlyCopy"] = false,
            ["updateBackupKeepsRecentBackupWithinGrace"] = false,
            ["updateBackupNeverDeletesNonBackupFiles"] = false,
            ["updateBackupIgnoresReparsePointOrDirectory"] = false,
            ["updateBackupPruneFailureDoesNotFailUpdate"] = false,
            ["uninstallerRemovesBackupFiles"] = false
        };
        var sandbox = Path.Combine(Path.GetTempPath(), "YinggeUpdateBackupTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);
            Check(findings, "updateBackupKeepsNewestOnly", () => KeepsNewestOnly(sandbox));
            Check(findings, "updateBackupReclaimsStaleOnlyCopy", () => ReclaimsStaleOnlyCopy(sandbox));
            Check(findings, "updateBackupKeepsRecentBackupWithinGrace", () => KeepsRecentWithinGrace(sandbox));
            Check(findings, "updateBackupNeverDeletesNonBackupFiles", () => NeverDeletesOtherFiles(sandbox));
            Check(findings, "updateBackupIgnoresReparsePointOrDirectory", () => IgnoresDirectoryAndLinks(sandbox));
            Check(findings, "updateBackupPruneFailureDoesNotFailUpdate", () => PruneFailureKeepsUpdate(sandbox));
            Check(findings, "uninstallerRemovesBackupFiles", InstallerDeletesBackups);
        }
        catch { /* Every check already defaults to false. */ }
        finally { TryDeleteTree(sandbox); }
        return findings;
    }

    static void Check(Dictionary<string, bool> findings, string name, Func<bool> body)
    {
        try { findings[name] = body(); }
        catch { findings[name] = false; }
    }

    // Three backups, 30/10 days old plus one written an hour ago: the recent
    // one is the rollback copy and the two stale ones go.
    static bool KeepsNewestOnly(string sandbox)
    {
        var directory = Path.Combine(sandbox, "keeps-newest");
        Directory.CreateDirectory(directory);
        var oldest = WriteBackup(directory, "映格视频库-1.5.1.exe" + BackupSuffix, "old", DateTime.UtcNow.AddDays(-30));
        var middle = WriteBackup(directory, "映格视频库-1.5.2.exe" + BackupSuffix, "middle", DateTime.UtcNow.AddDays(-10));
        var newest = WriteBackup(directory, "映格视频库.exe" + BackupSuffix, "newest", DateTime.UtcNow.AddHours(-1));
        var result = TryRun(directory);
        return !result.Failed
            && result.Deleted.Count == 2
            && File.Exists(newest) && File.ReadAllText(newest) == "newest"
            && !File.Exists(oldest) && !File.Exists(middle);
    }

    // The reported leak: one rollback copy of a version nobody returns to keeps
    // ~165 MB forever. Once it is older than the grace period it must go.
    static bool ReclaimsStaleOnlyCopy(string sandbox)
    {
        var directory = Path.Combine(sandbox, "stale-single");
        Directory.CreateDirectory(directory);
        var stale = WriteBackup(directory, "映格视频库.exe" + BackupSuffix, "1.5.3", DateTime.UtcNow.AddDays(-30));
        var executable = Path.Combine(directory, "映格视频库.exe");
        File.WriteAllText(executable, "1.5.4");
        var result = TryRun(directory);
        return !result.Failed && result.Deleted.Count == 1 && !File.Exists(stale)
            && File.ReadAllText(executable) == "1.5.4";
    }

    // A fresh backup is the only rollback copy; it must survive even though it
    // is not "the newest of many".
    static bool KeepsRecentWithinGrace(string sandbox)
    {
        var single = Path.Combine(sandbox, "recent-single");
        Directory.CreateDirectory(single);
        var only = WriteBackup(single, "映格视频库.exe" + BackupSuffix, "only", DateTime.UtcNow.AddMinutes(-2));
        var singleResult = TryRun(single);
        var pair = Path.Combine(sandbox, "recent-pair");
        Directory.CreateDirectory(pair);
        var fresh = WriteBackup(pair, "映格视频库.exe" + BackupSuffix, "fresh", DateTime.UtcNow.AddHours(-1));
        var recent = WriteBackup(pair, "映格视频库-1.5.2.exe" + BackupSuffix, "recent", DateTime.UtcNow.AddHours(-48));
        var pairResult = TryRun(pair);
        return singleResult.Deleted.Count == 0 && File.Exists(only)
            && pairResult.Deleted.Count == 0 && File.Exists(fresh) && File.Exists(recent)
            && pairResult.Kept.Count == 2;
    }

    // Same directory holds the executable, unrelated files, a sibling
    // ".update-next" leftover and a data folder: none of them may be removed.
    static bool NeverDeletesOtherFiles(string sandbox)
    {
        var directory = Path.Combine(sandbox, "other-files");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "映格视频库.exe");
        File.WriteAllText(executable, "app");
        var notes = Path.Combine(directory, "notes.txt");
        File.WriteAllText(notes, "keep me");
        var next = Path.Combine(directory, "映格视频库.exe.update-next");
        File.WriteAllText(next, "leftover");
        var dataDirectory = Path.Combine(directory, "data");
        Directory.CreateDirectory(dataDirectory);
        var payload = Path.Combine(dataDirectory, "library.json");
        File.WriteAllText(payload, "{}");
        WriteBackup(directory, "映格视频库-1.5.1.exe" + BackupSuffix, "old", DateTime.UtcNow.AddDays(-40));
        var newest = WriteBackup(directory, "映格视频库.exe" + BackupSuffix, "newest", DateTime.UtcNow.AddDays(-2));
        var result = TryRun(directory);
        return !result.Failed
            && result.Deleted.Count == 1
            && File.ReadAllText(executable) == "app"
            && File.ReadAllText(notes) == "keep me"
            && File.ReadAllText(next) == "leftover"
            && File.ReadAllText(payload) == "{}"
            && File.Exists(newest);
    }

    // A directory or a link that happens to carry the backup suffix is not a
    // backup file and must be left alone.
    static bool IgnoresDirectoryAndLinks(string sandbox)
    {
        var directory = Path.Combine(sandbox, "links");
        Directory.CreateDirectory(directory);
        var trap = Path.Combine(directory, "映格视频库.exe" + BackupSuffix);
        Directory.CreateDirectory(trap);
        var inner = Path.Combine(trap, "inner.txt");
        File.WriteAllText(inner, "inner");
        Directory.SetLastWriteTimeUtc(trap, DateTime.UtcNow.AddDays(-90));
        var executable = Path.Combine(directory, "映格视频库.exe");
        File.WriteAllText(executable, "app");
        var newest = WriteBackup(directory, "映格视频库-1.5.3.exe" + BackupSuffix, "newest", DateTime.UtcNow.AddMinutes(-3));
        string? link = null;
        try
        {
            link = Path.Combine(directory, "link" + BackupSuffix);
            File.CreateSymbolicLink(link, executable);
            File.SetLastWriteTimeUtc(link, DateTime.UtcNow.AddDays(-90));
        }
        catch { link = null; /* Creating links needs developer mode; skip it. */ }
        var result = TryRun(directory);
        return !result.Failed
            && Directory.Exists(trap) && File.ReadAllText(inner) == "inner"
            && File.Exists(newest)
            && (link is null || File.Exists(link))
            && File.ReadAllText(executable) == "app";
    }

    // The exact sequence Apply() uses: replace, then prune. A backup that cannot
    // be deleted must only be reported, never turned into an update failure.
    static bool PruneFailureKeepsUpdate(string sandbox)
    {
        var directory = Path.Combine(sandbox, "prune-failure");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "映格视频库.exe");
        var source = Path.Combine(directory, "downloaded.exe");
        File.WriteAllText(target, "old-version");
        File.WriteAllText(source, "new-version");
        var locked = WriteBackup(directory, "映格视频库-1.5.0.exe" + BackupSuffix, "stale", DateTime.UtcNow.AddDays(-45));
        File.SetAttributes(locked, File.GetAttributes(locked) | FileAttributes.ReadOnly);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        PortableUpdateWorker.ReplaceVerifiedFile(source, target, hash);
        var result = PortableUpdateWorker.PruneBackupsNeverThrows(target, "1.5.0");
        return File.ReadAllText(target) == "new-version"
            && File.Exists(target + BackupSuffix)
            && File.ReadAllText(target + BackupSuffix) == "old-version"
            && result.Failed
            && result.Errors.Any(error => error.StartsWith(locked, StringComparison.Ordinal))
            && File.Exists(locked);
    }

    static bool InstallerDeletesBackups()
    {
        var script = FindInstallerScript();
        if (script is null)
        {
            // An installed copy does not ship the Inno Setup script; fall back to
            // checking the detector itself against a good and a bad sample.
            return InstallerDeletesBackupFiles("[UninstallDelete]\nType: files; Name: \"{app}\\*.update-previous\"\n")
                && !InstallerDeletesBackupFiles("[UninstallDelete]\nType: filesandordirs; Name: \"{app}\"\n")
                && !InstallerDeletesBackupFiles("[Files]\nSource: \"a\"; DestDir: \"{app}\"\n");
        }
        return InstallerDeletesBackupFiles(File.ReadAllText(script));
    }

    /// <summary>True when the script has an [UninstallDelete] entry for the
    /// per-install update backups and no blanket {app} delete.</summary>
    internal static bool InstallerDeletesBackupFiles(string installerScript)
    {
        if (string.IsNullOrWhiteSpace(installerScript)) return false;
        var inSection = false;
        var foundBackup = false;
        foreach (var raw in installerScript.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.Equals("[UninstallDelete]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            // Inno Setup comments must start the line; ';' inside an entry is
            // part of the entry (e.g. "Type: files; Name: ...").
            if (line.StartsWith(';')) continue;
            var entry = line;
            if (entry.Contains("Name: \"{app}\"", StringComparison.OrdinalIgnoreCase)) return false;
            if (entry.StartsWith("Type:", StringComparison.OrdinalIgnoreCase)
                && entry.Contains("{app}\\*" + BackupSuffix, StringComparison.OrdinalIgnoreCase))
                foundBackup = true;
        }
        return foundBackup;
    }

    static string? FindInstallerScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "YinggeVideoLibrary.iss");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static string WriteBackup(string directory, string name, string content, DateTime writtenUtc)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    static void TryDeleteTree(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("YinggeUpdateBackupTest-", StringComparison.Ordinal)
                || !Directory.Exists(full)) return;
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            Directory.Delete(full, recursive: true);
        }
        catch { }
    }
}
