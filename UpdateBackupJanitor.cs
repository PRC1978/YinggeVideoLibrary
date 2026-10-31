using System.IO;

namespace YinggeVideoLibrary;

/// <summary>Result of one pruning pass. Every entry is an absolute path.</summary>
internal sealed class UpdateBackupJanitorResult
{
    internal List<string> Deleted { get; } = new();
    internal List<string> Kept { get; } = new();
    internal List<string> Skipped { get; } = new();
    internal List<string> Errors { get; } = new();
    internal string Context { get; init; } = "";

    internal bool Failed => Errors.Count > 0;

    internal string Describe() =>
        $"删除 {Deleted.Count}、保留 {Kept.Count}、跳过 {Skipped.Count}、失败 {Errors.Count}"
        + (Context.Length == 0 ? "" : "（" + Context + "）");
}

/// <summary>
/// Removes stale <c>*.update-previous</c> rollback copies from the install
/// directory. The in-app updater writes one backup next to the executable on
/// every replacement; without a janitor those copies stay forever and an
/// installed build never reclaims the space.
///
/// Rules (deliberately conservative):
/// * only direct children of the install directory are considered;
/// * only regular files whose name ends with <c>.update-previous</c>;
/// * the newest backup is kept while it is younger than <c>graceDays</c>, so a
///   freshly written rollback copy is never removed by the same update;
/// * a backup that has not been written for <c>graceDays</c> is removed even
///   when it is the only copy left, which is what reclaims the space taken by
///   a rollback copy of a version nobody can return to;
/// * nothing is ever recursed into and no exception escapes.
/// </summary>
internal static partial class UpdateBackupJanitor
{
    internal const string BackupSuffix = ".update-previous";
    internal const int DefaultGraceDays = 7;

    /// <summary>
    /// Startup pass. Call once from <c>App.OnStartup</c> with the install
    /// directory (the folder of the running executable).
    /// </summary>
    internal static UpdateBackupJanitorResult TryRun(string? installDirectory, int graceDays = DefaultGraceDays) =>
        Run(installDirectory, graceDays, enforceGrace: true, "startup");

    /// <summary>
    /// Called by the update worker right after the verified replacement. The
    /// newest backup is the version that was just replaced, so any other backup
    /// is stale and can go immediately.
    /// </summary>
    internal static UpdateBackupJanitorResult PruneAfterReplace(
        string? installDirectory, string currentVersion, int graceDays = DefaultGraceDays) =>
        Run(installDirectory, graceDays, enforceGrace: false, "replaced " + currentVersion);

    static UpdateBackupJanitorResult Run(string? installDirectory, int graceDays, bool enforceGrace, string context)
    {
        var result = new UpdateBackupJanitorResult { Context = context };
        try
        {
            if (string.IsNullOrWhiteSpace(installDirectory) || !Path.IsPathFullyQualified(installDirectory))
            {
                result.Skipped.Add("(安装目录无效)");
                return result;
            }
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
            if (string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root) ?? ""),
                    StringComparison.OrdinalIgnoreCase))
            {
                // Never sweep a drive root, whatever the caller passes in.
                result.Skipped.Add("(拒绝处理磁盘根目录)");
                return result;
            }
            if (!Directory.Exists(root))
            {
                result.Skipped.Add(root);
                return result;
            }

            var candidates = new List<(string Path, DateTime WrittenUtc)>();
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (!IsSafeBackupFile(root, path, out var reason))
                    {
                        result.Skipped.Add($"{path}：{reason}");
                        continue;
                    }
                    candidates.Add((path, File.GetLastWriteTimeUtc(path)));
                }
                catch (Exception ex) { result.Errors.Add($"{path}：{ex.Message}"); }
            }
            if (candidates.Count == 0) return result;

            candidates.Sort((left, right) => right.WrittenUtc.CompareTo(left.WrittenUtc));
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, graceDays));
            for (var index = 0; index < candidates.Count; index++)
            {
                var (path, writtenUtc) = candidates[index];
                // Startup pass: anything untouched for the grace period goes,
                // including a lone rollback copy that has outlived its value.
                // Post-replacement pass: the newest copy is the version that was
                // just replaced, so every other copy is stale immediately.
                if (enforceGrace ? writtenUtc >= cutoff : index == 0)
                {
                    result.Kept.Add(path);
                    continue;
                }
                try
                {
                    File.Delete(path);
                    result.Deleted.Add(path);
                }
                catch (Exception ex) { result.Errors.Add($"{path}：{ex.Message}"); }
            }
        }
        catch (Exception ex) { result.Errors.Add(ex.Message); }
        return result;
    }

    // Re-validates every candidate instead of trusting the enumeration filter:
    // a directory or reparse point must never be removed even if it is named
    // like a backup.
    static bool IsSafeBackupFile(string root, string path, out string reason)
    {
        reason = "";
        var name = Path.GetFileName(path);
        if (name.Length <= BackupSuffix.Length
            || !name.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
        {
            reason = "名称不是更新备份";
            return false;
        }
        var full = Path.GetFullPath(path);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(full) ?? "");
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            reason = "不在安装目录第一层";
            return false;
        }
        FileAttributes attributes;
        try { attributes = File.GetAttributes(full); }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            reason = "是目录";
            return false;
        }
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            reason = "是重解析点";
            return false;
        }
        return true;
    }
}
