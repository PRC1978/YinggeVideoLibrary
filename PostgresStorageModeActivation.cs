using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Writes storage-postgresql.active only after the database has been proven to
// be the authoritative copy: the backup manifest must verify offline, the
// database must match that snapshot byte for byte, and the pre-cutover JSON
// files must still be present. A failed activation removes any marker written
// by this call, so a crash or exception cannot leave the library "activated".
internal static class PostgresStorageModeActivation
{
    internal sealed record Marker(int SchemaVersion, string LibraryId, string BackupDirectory,
        string BackupManifestSha256, int VideoCount, int MetadataCount, string PostgresVersion,
        DateTimeOffset ActivatedUtc);

    private const int CurrentMarkerVersion = 1;

    internal static async Task<Marker> ActivateAsync(string connectionString, string dataDirectory,
        string backupDirectory, CancellationToken cancellationToken = default) =>
        await WriteAsync(connectionString, dataDirectory, backupDirectory, requireLegacyFiles: true,
            cancellationToken);

    // Restore rewrites the marker for a library that is already in database
    // mode. The pre-cutover JSON files may not exist there, but the backup and
    // the live database are still verified before the marker is replaced.
    internal static async Task<Marker> RewriteAfterRestoreAsync(string connectionString,
        string dataDirectory, string backupDirectory, CancellationToken cancellationToken = default) =>
        await WriteAsync(connectionString, dataDirectory, backupDirectory,
            requireLegacyFiles: false, cancellationToken);

    private static async Task<Marker> WriteAsync(string connectionString, string dataDirectory,
        string backupDirectory, bool requireLegacyFiles, CancellationToken cancellationToken)
    {
        dataDirectory = Path.GetFullPath(dataDirectory);
        backupDirectory = Path.GetFullPath(backupDirectory);
        var markerPath = Path.Combine(dataDirectory, PostgresStorageModeGuard.MarkerName);
        var temp = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var written = false;
        try
        {
            if (requireLegacyFiles)
                foreach (var name in new[] { "config.json", "library-index.json", "metadata.json" })
                    if (!File.Exists(Path.Combine(dataDirectory, name)))
                        throw new InvalidDataException("切换前必须保留旧文件：" + name);

            if (!await PostgresLibraryExport.VerifyOfflineAsync(backupDirectory, cancellationToken))
                throw new InvalidDataException("备份清单或文件校验失败，拒绝切换存储模式。 ");
            if (!await PostgresLibraryMigration.VerifyContentAsync(backupDirectory, connectionString,
                    cancellationToken))
                throw new InvalidDataException("数据库内容与备份不一致，拒绝切换存储模式。 ");
            await PostgresMissingCleanupStore.EnsureReadyForActivationAsync(connectionString,
                cancellationToken);

            var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(backupDirectory,
                PostgresLibraryExport.ManifestName), cancellationToken);
            var counts = await PostgresLibraryMigration.VerifyAsync(connectionString, cancellationToken);
            var version = await PostgresLibraryMigration.ReadServerVersionAsync(connectionString,
                cancellationToken);
            var marker = new Marker(CurrentMarkerVersion, LibraryId(dataDirectory),
                backupDirectory, Convert.ToHexString(SHA256.HashData(manifestBytes)),
                counts.VideoCount, counts.MetadataCount, version, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(marker,
                new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(temp, markerPath, overwrite: true);
            written = true;
            return marker;
        }
        catch
        {
            // Only the marker written by this call is removed; a marker left by
            // an earlier verified cutover is not silently discarded.
            if (written) TryDelete(markerPath);
            TryDelete(temp);
            throw;
        }
    }

    internal static Marker? TryRead(string dataDirectory) => TryReadMarker(
        Path.Combine(dataDirectory, PostgresStorageModeGuard.MarkerName));

    // Compatibility with the authorization-only marker written by the isolated
    // whole-library restore. It says "this directory is a PostgreSQL library"
    // but carries no backup pointer, so it must not be treated as a verified
    // snapshot for rollback purposes.
    internal static bool IsAuthorizationOnly(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath)) return false;
            return File.ReadAllText(markerPath).Trim() is "postgresql" or "postgresql-v1";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException)
        {
            return false;
        }
    }

    private static Marker? TryReadMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllBytes(path));
            if (marker is null || marker.SchemaVersion != CurrentMarkerVersion ||
                string.IsNullOrWhiteSpace(marker.BackupDirectory) ||
                string.IsNullOrWhiteSpace(marker.BackupManifestSha256)) return null;
            return marker;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static bool BackupStillVerifies(Marker marker) =>
        File.Exists(Path.Combine(marker.BackupDirectory, PostgresLibraryExport.ManifestName)) &&
        File.Exists(Path.Combine(marker.BackupDirectory, "library-index.json")) &&
        File.Exists(Path.Combine(marker.BackupDirectory, "metadata.json"));

    internal static string LibraryId(string dataDirectory) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(dataDirectory).ToLowerInvariant())))[..24];

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
