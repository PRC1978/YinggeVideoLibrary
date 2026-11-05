using System.Data;
using System.IO;
using System.Security.Cryptography;
using Npgsql;

namespace YinggeVideoLibrary;

// A backup taken while the database is the authoritative store.
//
// Consistency comes from two locks that every writer already respects:
//   * the exclusive .library-session.lock file lease, which stops the JSON app;
//   * pg_advisory_lock(178942601) on the snapshot connection, which stops every
//     database writer that takes the same key in its own transaction.
// The snapshot transaction stays open while images are copied, so the rows and
// the image bytes describe the same revision. The directory only appears under
// its final name after the manifest and every SHA-256 have been re-read.
internal static class PostgresLibraryBackup
{
    internal sealed record Info(string Directory, string ManifestSha256, int VideoCount,
        int MetadataCount, int DocumentCount, long Bytes, DateTimeOffset CreatedUtc);

    internal static async Task<Info> CreateAsync(string connectionString, string dataDirectory,
        string destination, CancellationToken cancellationToken = default)
    {
        dataDirectory = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(dataDirectory))
            throw new DirectoryNotFoundException("数据目录不存在，拒绝备份。 ");
        // An exclusive lease fails immediately when the app is running, which is
        // what "锁定库" must mean for a backup started from the UI thread.
        using var offlineLease = new FileStream(
            Path.Combine(dataDirectory, ".library-session.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        return await CreateLockedAsync(connectionString, dataDirectory, destination,
            cancellationToken);
    }

    // The caller already holds the exclusive session lease (restore does), so
    // this variant must not try to open the same lease file a second time.
    internal static async Task<Info> CreateLockedAsync(string connectionString,
        string dataDirectory, string destination, CancellationToken cancellationToken = default)
    {
        dataDirectory = Path.GetFullPath(dataDirectory);
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("备份目录已存在；拒绝覆盖已有备份。 ");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_lock(178942601)", connection))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await PostgresLibraryExport.EnsureImportedAsync(connection, transaction, cancellationToken);
        var estimated = await PostgresLibraryExport.EstimateSnapshotBytesAsync(connection,
            transaction, dataDirectory, cancellationToken);
        PostgresLibraryExport.EnsureAvailableSpace(destination, estimated);
        var result = await PostgresLibraryExport.ExportInTransactionAsync(connection, transaction,
            destination, dataDirectory, cancellationToken);

        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(destination,
            PostgresLibraryExport.ManifestName), cancellationToken);
        return new(destination, Convert.ToHexString(SHA256.HashData(manifestBytes)),
            result.VideoCount, result.MetadataCount, result.DocumentCount,
            DirectorySize(destination), DateTimeOffset.UtcNow);
    }

    internal static long DirectorySize(string directory)
    {
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            total += new FileInfo(path).Length;
        }
        return total;
    }
}
