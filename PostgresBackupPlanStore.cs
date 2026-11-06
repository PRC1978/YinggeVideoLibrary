using System.IO;
using System.Text.Json;
using System.Security.Cryptography;

namespace YinggeVideoLibrary;

internal static class PostgresBackupPlanStore
{
    private const string DocumentName = "postgres-backup-plan.json";
    private const string Prefix = "yingge-db-";

    internal sealed class Plan
    {
        public int Version { get; set; } = 1;
        public bool Enabled { get; set; }
        public string Frequency { get; set; } = "daily";
        public int RetentionCount { get; set; } = 3;
        public string DestinationDirectory { get; set; } = "";
        public DateTimeOffset? NextDueUtc { get; set; }
        public bool Pending { get; set; }
        public DateTimeOffset? LastCompletedUtc { get; set; }
        public string LastBackupDirectory { get; set; } = "";
        public string LastError { get; set; } = "";
        public List<BackupRecord> Backups { get; set; } = [];
    }

    internal sealed record BackupRecord(string Directory, string Root,
        string ManifestSha256, DateTimeOffset CreatedUtc);

    private static Plan Parse(byte[]? bytes)
    {
        var plan = bytes is null ? new Plan() : JsonSerializer.Deserialize<Plan>(bytes)
            ?? throw new InvalidDataException("数据库备份设置为空。");
        Validate(plan);
        return plan;
    }

    private static void Validate(Plan plan)
    {
        if (plan.Version != 1 || plan.Frequency is not ("daily" or "weekly" or "monthly") ||
            plan.RetentionCount is < 1 or > 30 || plan.DestinationDirectory is null ||
            plan.DestinationDirectory.Length > 1024 || plan.LastBackupDirectory is null ||
            plan.LastError is null || plan.LastError.Length > 2048 ||
            plan.Backups is null || plan.Backups.Count > 1000 ||
            plan.Backups.Any(item => item is null || item.Directory is null ||
                item.Root is null || item.ManifestSha256?.Length != 64))
            throw new InvalidDataException("数据库备份设置格式无效；未执行备份。");
    }

    internal static async Task<Plan> LoadAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        var saved = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            DocumentName, cancellationToken);
        return Parse(saved?.Content);
    }

    internal static async Task<Plan> ChangeAsync(string connectionString, Func<Plan, Plan> change,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var saved = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
                DocumentName, cancellationToken);
            var next = change(Parse(saved?.Content));
            Validate(next);
            try
            {
                await PostgresLibraryDocumentStore.SaveAsync(connectionString, DocumentName,
                    JsonSerializer.SerializeToUtf8Bytes(next), saved?.Revision, cancellationToken);
                return next;
            }
            catch (InvalidOperationException) when (attempt < 2) { }
        }
        throw new InvalidOperationException("数据库备份设置被另一窗口持续修改，请重试。");
    }

    internal static string BackupRoot(string dataDirectory, Plan plan)
    {
        var data = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(plan.DestinationDirectory.Length == 0
            ? data + ".postgres-backups" : plan.DestinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (IsInside(data, root))
            throw new InvalidDataException("数据库备份位置不能位于视频库数据目录内。");
        return root;
    }

    internal static void ValidateDestination(string dataDirectory, string destination,
        IEnumerable<string> videoRoots)
    {
        var plan = new Plan { DestinationDirectory = destination };
        var root = BackupRoot(dataDirectory, plan);
        foreach (var folder in videoRoots.Where(Directory.Exists))
            if (IsInside(Path.GetFullPath(folder), root) || IsInside(root, Path.GetFullPath(folder)))
                throw new InvalidDataException("数据库备份位置不能与视频扫描目录重叠。");
    }

    private static bool IsInside(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative));
    }

    internal static DateTimeOffset NextDueUtc(string frequency) => frequency switch
    {
        "daily" => DateTimeOffset.UtcNow.AddDays(1),
        "weekly" => DateTimeOffset.UtcNow.AddDays(7),
        _ => DateTimeOffset.UtcNow.AddMonths(1)
    };

    internal static async Task<PostgresLibraryBackup.Info?> RunAtStartupAsync(
        string connectionString, string dataDirectory, IEnumerable<string> videoRoots,
        CancellationToken cancellationToken = default)
    {
        var plan = await LoadAsync(connectionString, cancellationToken);
        if (!plan.Pending && !(plan.Enabled && plan.NextDueUtc <= DateTimeOffset.UtcNow))
            return null;
        // Reserve the intention before any backup bytes are copied. A failed
        // snapshot remains pending and is retried on a later launch.
        plan = await ChangeAsync(connectionString, current =>
        {
            current.Pending = true;
            return current;
        }, cancellationToken);
        try
        {
            ValidateDestination(dataDirectory, BackupRoot(dataDirectory, plan), videoRoots);
            var root = BackupRoot(dataDirectory, plan);
            Directory.CreateDirectory(root);
            var destination = Path.Combine(root, Prefix + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
                + "-" + Guid.NewGuid().ToString("N"));
            var backup = await PostgresLibraryBackup.CreateLockedAsync(connectionString,
                dataDirectory, destination, cancellationToken);
            await ChangeAsync(connectionString, current =>
            {
                current.Pending = false;
                current.LastCompletedUtc = backup.CreatedUtc;
                current.LastBackupDirectory = backup.Directory;
                current.LastError = "";
                current.Backups.Add(new BackupRecord(backup.Directory, root,
                    backup.ManifestSha256, backup.CreatedUtc));
                current.NextDueUtc = current.Enabled ? NextDueUtc(current.Frequency) : null;
                return current;
            }, cancellationToken);
            try { await PruneTrackedBackupsAsync(connectionString, dataDirectory, cancellationToken); }
            catch (Exception error)
            {
                await ChangeAsync(connectionString, current =>
                {
                    current.LastError = "备份已完成；旧备份清理失败：" + error.Message;
                    if (current.LastError.Length > 2048) current.LastError = current.LastError[..2048];
                    return current;
                }, CancellationToken.None);
            }
            return backup;
        }
        catch (Exception error)
        {
            await ChangeAsync(connectionString, current =>
            {
                current.Pending = true;
                current.LastError = error.Message.Length > 2048 ? error.Message[..2048] : error.Message;
                return current;
            }, CancellationToken.None);
            throw;
        }
    }

    internal static async Task PruneTrackedBackupsAsync(string connectionString,
        string dataDirectory, CancellationToken cancellationToken = default)
    {
        var plan = await LoadAsync(connectionString, cancellationToken);
        var old = plan.Backups.OrderByDescending(item => item.CreatedUtc)
            .Skip(plan.RetentionCount).ToArray();
        foreach (var item in old)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(item.Root).TrimEnd(Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(item.Directory).TrimEnd(Path.DirectorySeparatorChar);
            if (IsInside(Path.GetFullPath(dataDirectory), root) ||
                !string.Equals(Path.GetDirectoryName(target), root,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith(Prefix, StringComparison.Ordinal) ||
                !Directory.Exists(root) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                !Directory.Exists(target) || HasReparsePoint(target))
                continue;
            var manifest = Path.Combine(target, PostgresLibraryExport.ManifestName);
            if (!File.Exists(manifest) ||
                !Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifest,
                    cancellationToken))).Equals(item.ManifestSha256, StringComparison.Ordinal))
                continue;
            Directory.Delete(target, recursive: true);
            await ChangeAsync(connectionString, current =>
            {
                current.Backups.RemoveAll(saved => saved.Directory == item.Directory &&
                    saved.ManifestSha256 == item.ManifestSha256);
                return current;
            }, cancellationToken);
        }
    }

    private static bool HasReparsePoint(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var path = pending.Pop();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        return false;
    }
}
