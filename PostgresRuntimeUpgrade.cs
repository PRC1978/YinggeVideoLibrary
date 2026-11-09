using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Logical, side-by-side major upgrade. The source directory stays the rollback
// target: never point a different major's binaries at its physical PG files.
internal static class PostgresRuntimeUpgrade
{
    internal sealed record Result(int SourceMajor, int TargetMajor, int VideoCount,
        int MetadataCount, string SnapshotDirectory, string NewLibraryDirectory);

    internal static async Task<Result> UpgradeAsync(string oldRuntime, string newRuntime,
        string sourceLibrary, string destination, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, Action? afterBackup = null,
        Action? afterImport = null)
    {
        oldRuntime = Canonical(oldRuntime); newRuntime = Canonical(newRuntime);
        sourceLibrary = Canonical(sourceLibrary); destination = Canonical(destination);
        if (!Directory.Exists(sourceLibrary) || !PostgresStorageModeGuard.PostgresMayOpen(sourceLibrary))
            throw new InvalidDataException("原资料库未启用数据库模式，或仍有待恢复的操作。");
        if (Directory.Exists(destination) || File.Exists(destination) ||
            Overlap(sourceLibrary, destination) || Overlap(destination, oldRuntime) ||
            Overlap(destination, newRuntime))
            throw new InvalidDataException("升级目标必须是原资料库和运行时目录之外的新目录。");
        NoLinks(sourceLibrary); NoLinks(destination); NoLinks(oldRuntime); NoLinks(newRuntime);
        var sourceData = PostgresClusterLocation.DataPath(sourceLibrary);
        if (Overlap(sourceData, destination))
            throw new InvalidDataException("升级目标不能与原数据库目录重叠。");
        NoLinks(sourceData);
        progress?.Report("检查数据库版本");
        var oldMajor = await RuntimeMajorAsync(oldRuntime, cancellationToken);
        var newMajor = await RuntimeMajorAsync(newRuntime, cancellationToken);
        var versionPath = Path.Combine(sourceData, "PG_VERSION");
        var originalVersion = await File.ReadAllBytesAsync(versionPath, cancellationToken);
        if (!int.TryParse(System.Text.Encoding.UTF8.GetString(originalVersion).Trim(), out var dataMajor)
            || dataMajor != oldMajor)
            throw new InvalidDataException("原运行时与数据库文件主版本不一致；原库没有改动。");
        if (newMajor <= oldMajor)
            throw new InvalidDataException("请选择更高主版本的 PostgreSQL；同主版本更新不使用此迁移流程。");
        var snapshot = destination + ".pre-upgrade-" + Guid.NewGuid().ToString("N");
        var published = false;
        // An active application must make this operation fail before starting
        // either cluster. Do not stop or steal its owner lease.
        using var session = new FileStream(Path.Combine(sourceLibrary, ".library-session.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            progress?.Report("导出并校验原库备份");
            await using (var oldCluster = await PostgresPrivateCluster.OpenAsync(oldRuntime,
                sourceLibrary, cancellationToken))
                await PostgresLibraryBackup.CreateLockedAsync(oldCluster.ConnectionString,
                    sourceLibrary, snapshot, cancellationToken);
            afterBackup?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("导入独立的新版本数据库");
            var imported = await PostgresLibraryBackupCoordinator.RestoreToFreshPrivateLibraryAsync(
                newRuntime, snapshot, destination, cancellationToken);
            published = true;
            afterImport?.Invoke();
            progress?.Report("核对全部资料并重开验证");
            await using (var target = await PostgresPrivateCluster.OpenAsync(newRuntime,
                destination, cancellationToken))
            {
                if (!await PostgresLibraryMigration.VerifyContentAsync(snapshot,
                        target.ConnectionString, cancellationToken))
                    throw new InvalidDataException("升级后的资料与备份不一致；保留原库供回退。");
                await PostgresStorageModeActivation.RewriteAfterRestoreAsync(target.ConnectionString,
                    destination, snapshot, cancellationToken);
            }
            // Both physical versions remain separate and the old library is
            // still selected. Opening the verified new directory is explicit.
            if (!(await File.ReadAllBytesAsync(versionPath, cancellationToken)).SequenceEqual(originalVersion))
                throw new InvalidDataException("原库版本标记发生变化，升级没有完成。");
            progress?.Report("升级副本已就绪，原资料库已保留");
            return new(oldMajor, newMajor, imported.VideoCount, imported.MetadataCount, snapshot, destination);
        }
        catch
        {
            // Source data and its selection never changed. A target published
            // before a later failure is blocked, not silently treated as ready.
            if (published && Directory.Exists(destination))
                PostgresStorageModeGuard.BeginRestore(destination,
                    "yingge_restore_runtime_upgrade", "yingge_prev_runtime_upgrade");
            throw;
        }
    }

    internal static async Task<int> RuntimeMajorAsync(string directory, CancellationToken cancellationToken = default)
    {
        foreach (var name in new[] { "postgres.exe", "pg_ctl.exe", "initdb.exe" })
            if (!File.Exists(Path.Combine(directory, "bin", name)))
                throw new InvalidDataException("所选 PostgreSQL 运行时不完整。");
        if (!File.Exists(Path.Combine(directory, "share", "postgres.bki")))
            throw new InvalidDataException("所选 PostgreSQL 运行时缺少初始化文件。");
        using var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(directory, "bin", "postgres.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--version");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try { await process.WaitForExitAsync(limit.Token); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        var text = await output; await error;
        var version = Regex.Match(text, @"^postgres\s+\(PostgreSQL\)\s+(?<major>\d{1,2})(?:\.|\s|$)");
        if (process.ExitCode != 0 || !version.Success || !int.TryParse(version.Groups["major"].Value, out var major) || major < 14)
            throw new InvalidDataException("无法识别受支持的 PostgreSQL 主版本。");
        return major;
    }

    static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    static bool Inside(string parent, string path) => path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static bool Overlap(string first, string second) => Inside(first, second) || Inside(second, first);
    static void NoLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("升级目录不能经过符号链接或目录联接。");
    }
}
