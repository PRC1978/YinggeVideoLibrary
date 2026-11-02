using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class LargeBackupDiagnostics
{
    internal static int RunToFile(string root, string resultPath)
    {
        var finding = new Dictionary<string, object?>();
        try
        {
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
                throw new InvalidOperationException("仅可使用系统临时目录下的全新空目录。");
            for (var part = new DirectoryInfo(full); part is not null; part = part.Parent)
                if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("隔离目录不能位于文件夹链接下。");
            var clock = Stopwatch.StartNew();
            var fixture = UiFixtureGenerator.Seed(full, 32_909, 15_730);
            finding["fixtureVideos"] = fixture.VideoCount;
            finding["fixtureCategoryLeaves"] = fixture.CategoryLeafCount;
            finding["fixtureMs"] = clock.ElapsedMilliseconds;

            LibraryBackupService.StageOfflineBackup(full);
            var backupStarted = clock.ElapsedMilliseconds;
            var backupApplied = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(full, out var backupError, false);
            var package = LibraryBackupService.LatestBackup(full);
            var verified = package is not null && LibraryBackupService.Verify(package).IsValid;
            finding["backupApplied"] = backupApplied;
            finding["backupError"] = backupError;
            finding["backupVerified"] = verified;
            finding["backupMs"] = clock.ElapsedMilliseconds - backupStarted;
            if (!backupApplied || !verified || package is null) throw new InvalidOperationException("大库一致性备份未通过校验。");

            var names = new[] { "config.json", "library-index.json", "metadata.json" };
            var backed = names.ToDictionary(name => name, name => Hash(Path.Combine(package, name)));
            foreach (var name in names) File.AppendAllText(Path.Combine(full, name), "\n ");
            var restoreStarted = clock.ElapsedMilliseconds;
            var preview = LibraryBackupService.CreateRestorePreview(full, package);
            LibraryBackupService.StageRestore(full, preview);
            var restored = LibraryBackupService.ApplyPendingAtStartup(full, out var restoreError, false);
            finding["restored"] = restored;
            finding["restoreError"] = restoreError;
            finding["restoreMs"] = clock.ElapsedMilliseconds - restoreStarted;
            finding["filesMatchBackup"] = names.All(name => Hash(Path.Combine(full, name)) == backed[name]);
            using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(full, "library-index.json")));
            finding["restoredVideos"] = index.RootElement.GetProperty("Entries").GetArrayLength();
            finding["passed"] = restored && (bool)finding["filesMatchBackup"]! &&
                (int)finding["restoredVideos"]! == 32_909;
        }
        catch (Exception ex)
        {
            finding["passed"] = false;
            finding["error"] = ex.ToString();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(finding, new JsonSerializerOptions { WriteIndented = true }));
        return finding.TryGetValue("passed", out var passed) && passed is true ? 0 : 1;
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
