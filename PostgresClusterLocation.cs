using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// The pointer lives with the library; the database may live on another local disk.
// Relocation is staged while the app is running and applied before PostgreSQL starts.
internal static class PostgresClusterLocation
{
    internal const string MarkerName = "postgres-location.json";
    internal const string PendingName = "postgres-location-pending.json";
    private sealed record Location(string DataPath, string? PreviousPath);
    private sealed record Pending(string SourcePath, string TargetPath);

    internal static string DefaultPath(string library) => Path.Combine(Path.GetFullPath(library), "postgres-data");

    internal static string DataPath(string library)
    {
        library = Path.GetFullPath(library);
        var marker = Path.Combine(library, MarkerName);
        if (!File.Exists(marker)) return DefaultPath(library);
        var location = Read<Location>(marker);
        ValidateLocation(library, location.DataPath);
        return Path.GetFullPath(location.DataPath);
    }

    internal static string? PreviousPath(string library)
    {
        var marker = Path.Combine(library, MarkerName);
        if (!File.Exists(marker)) return null;
        var location = Read<Location>(marker);
        if (string.IsNullOrWhiteSpace(location.PreviousPath)) return null;
        ValidateLocation(library, location.PreviousPath);
        return Path.GetFullPath(location.PreviousPath);
    }

    internal static string? PendingTarget(string library)
    {
        var path = Path.Combine(library, PendingName);
        if (!File.Exists(path)) return null;
        var pending = Read<Pending>(path);
        ValidateLocation(library, pending.TargetPath);
        return pending.TargetPath;
    }

    internal static void CancelPending(string library)
    {
        var pending = Path.Combine(Path.GetFullPath(library), PendingName);
        if (File.Exists(pending)) File.Delete(pending);
    }

    internal static string Stage(string library, string parent)
    {
        library = Path.GetFullPath(library);
        parent = Path.GetFullPath(parent);
        if (!PostgresStorageModeGuard.PostgresMayOpen(library))
            throw new InvalidOperationException("只有已启用 PostgreSQL 的视频库可以移动数据库。");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("目标文件夹不存在。");
        if (new DriveInfo(Path.GetPathRoot(parent)!).DriveType is not
            (DriveType.Fixed or DriveType.Removable))
            throw new InvalidDataException("请选择本机磁盘，不能将数据库放在网络共享中。");
        RejectLinks(parent);
        var current = DataPath(library);
        if (!File.Exists(Path.Combine(current, "PG_VERSION")))
            throw new InvalidDataException("当前数据库目录不完整，不能排定迁移。");
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(library.ToUpperInvariant())))[..12];
        var target = Path.Combine(parent, "Yingge-Postgres-" + suffix);
        ValidateTarget(library, current, target);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("目标数据库目录已存在，请选择另一位置。");
        WriteAtomic(Path.Combine(library, PendingName), new Pending(current, target));
        return target;
    }

    internal static async Task<bool> ApplyPendingAsync(string library, CancellationToken cancellationToken = default)
    {
        library = Path.GetFullPath(library);
        var pendingPath = Path.Combine(library, PendingName);
        if (!File.Exists(pendingPath)) return false;
        var pending = Read<Pending>(pendingPath);
        var current = DataPath(library);
        if (Same(current, pending.TargetPath) &&
            PreviousPath(library) is { } previous && Same(previous, pending.SourcePath))
        {
            // A crash after publishing the pointer but before deleting the
            // pending file is already a committed move.
            File.Delete(pendingPath);
            return false;
        }
        if (!Same(current, pending.SourcePath))
            throw new InvalidDataException("待迁移数据库与当前库位置不一致，已停止以保护资料。");
        ValidateTarget(library, current, pending.TargetPath);
        if (!Directory.Exists(current) || !File.Exists(Path.Combine(current, "PG_VERSION")))
            throw new InvalidDataException("原数据库目录不完整，迁移未开始。");
        if (!Directory.Exists(Path.GetDirectoryName(pending.TargetPath)!))
            throw new DirectoryNotFoundException("目标磁盘或文件夹当前不可用，原数据库未改动。");
        RejectLinks(current);
        RejectLinks(Path.GetDirectoryName(pending.TargetPath)!);
        if (File.Exists(Path.Combine(current, "postmaster.pid")))
            throw new IOException("数据库尚未完全停止，请先正常退出映格后重试；原数据库未改动。");
        // The application also holds its per-library mutex and session lease here.
        using var owner = new FileStream(Path.Combine(library, ".postgres-cluster-owner.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(pending.TargetPath))
        {
            // A crash between the atomic directory rename and pointer publish
            // leaves a complete target. Adopt it only when every file matches.
            if (!TreesEqual(current, pending.TargetPath))
                throw new IOException("目标目录已存在且与原数据库不同，原数据库未改动。");
            WriteAtomic(Path.Combine(library, MarkerName), new Location(pending.TargetPath, current));
            File.Delete(pendingPath);
            return true;
        }
        if (File.Exists(pending.TargetPath))
            throw new IOException("目标路径已被文件占用，原数据库未改动。");
        var staging = pending.TargetPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            var files = CollectFiles(current).ToArray();
            var bytes = files.Sum(file => new FileInfo(file).Length);
            var drive = new DriveInfo(Path.GetPathRoot(pending.TargetPath)!);
            if (drive.AvailableFreeSpace < bytes + Math.Max(64L * 1024 * 1024, bytes / 20))
                throw new IOException("目标磁盘空间不足，原数据库未改动。");
            Directory.CreateDirectory(staging);
            // PostgreSQL relies on several intentionally empty directories
            // (for example pg_tblspc and pg_twophase) at startup.
            foreach (var directory in CollectDirectories(current))
                Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(current, directory)));
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("数据库内包含链接，拒绝复制。");
                var destination = Path.Combine(staging, Path.GetRelativePath(current, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var input = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = File.Create(destination))
                    await input.CopyToAsync(output, cancellationToken);
                await using var originalForHash = File.OpenRead(file);
                await using var copyForHash = File.OpenRead(destination);
                var left = await SHA256.HashDataAsync(originalForHash, cancellationToken);
                var right = await SHA256.HashDataAsync(copyForHash, cancellationToken);
                if (!left.SequenceEqual(right)) throw new IOException("数据库文件校验失败，原数据库未改动。");
            }
            if (!File.Exists(Path.Combine(staging, "PG_VERSION")))
                throw new InvalidDataException("复制后的数据库缺少版本文件。");
            Directory.Move(staging, pending.TargetPath);
            WriteAtomic(Path.Combine(library, MarkerName), new Location(pending.TargetPath, current));
            File.Delete(pendingPath);
            return true;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    internal static void RollbackRecent(string library)
    {
        var marker = Path.Combine(library, MarkerName);
        var location = Read<Location>(marker);
        if (string.IsNullOrWhiteSpace(location.PreviousPath) ||
            !File.Exists(Path.Combine(location.PreviousPath, "PG_VERSION")))
            throw new InvalidDataException("原数据库副本不可用，无法自动回退。");
        ValidateLocation(library, location.PreviousPath);
        if (Same(location.PreviousPath, DefaultPath(library))) File.Delete(marker);
        else WriteAtomic(marker, new Location(location.PreviousPath, null));
    }

    private static void ValidateTarget(string library, string source, string target)
    {
        ValidateLocation(library, target);
        if (Same(source, target) || IsInside(source, target) || IsInside(target, source))
            throw new InvalidDataException("新数据库位置不能与当前数据库目录重叠。");
        if (IsInside(library, target) || IsInside(target, library))
            throw new InvalidDataException("请选择视频库数据目录之外的数据库位置。");
    }

    private static void ValidateLocation(string library, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            Path.GetFullPath(path) != path || Same(path, Path.GetPathRoot(path)!))
            throw new InvalidDataException("数据库位置标记无效，已停止以保护资料。");
        // The default path is valid; other paths must be outside the library.
        if (!Same(path, DefaultPath(library)) && (IsInside(library, path) || IsInside(path, library)))
            throw new InvalidDataException("数据库位置与视频库目录重叠。");
    }

    private static void RejectLinks(string path)
    {
        for (var part = new DirectoryInfo(path); part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("数据库路径包含链接，已拒绝迁移。");
    }

    private static IEnumerable<string> CollectFiles(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("数据库内包含链接，拒绝复制。");
            if (Directory.Exists(entry))
            {
                foreach (var file in CollectFiles(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    private static IEnumerable<string> CollectDirectories(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("数据库内包含链接，拒绝复制。");
            if (!Directory.Exists(entry)) continue;
            yield return entry;
            foreach (var child in CollectDirectories(entry)) yield return child;
        }
    }

    private static bool TreesEqual(string source, string destination)
    {
        RejectLinks(destination);
        var originalDirectories = CollectDirectories(source)
            .Select(path => Path.GetRelativePath(source, path))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var copiedDirectories = CollectDirectories(destination)
            .Select(path => Path.GetRelativePath(destination, path))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!originalDirectories.SequenceEqual(copiedDirectories, StringComparer.Ordinal)) return false;
        var original = CollectFiles(source).Select(path => Path.GetRelativePath(source, path))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var copy = CollectFiles(destination).Select(path => Path.GetRelativePath(destination, path))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!original.SequenceEqual(copy, StringComparer.Ordinal)) return false;
        foreach (var relative in original)
        {
            using var a = File.OpenRead(Path.Combine(source, relative));
            using var b = File.OpenRead(Path.Combine(destination, relative));
            if (a.Length != b.Length || !SHA256.HashData(a).SequenceEqual(SHA256.HashData(b)))
                return false;
        }
        return true;
    }

    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
        Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static bool IsInside(string parent, string child) => Path.GetFullPath(child).StartsWith(
        Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    private static T Read<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("数据库位置标记无法读取。");
    private static void WriteAtomic<T>(string path, T value)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
