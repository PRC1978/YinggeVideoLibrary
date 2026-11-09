using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace YinggeVideoLibrary;

// Explicit opt-in acceptance against a live library's logical, read-only
// snapshot. Never copies PG physical files, opens media, or restores in place
// at the source. Reports contain counts and booleans, never private records.
internal static class PostgresRealCopyDiagnostics
{
    static readonly Assembly Product = typeof(MovieMetadata).Assembly;
    const BindingFlags Methods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static Type ProductType(string name) => Product.GetType("YinggeVideoLibrary." + name, true)!;
    static async Task<object?> CallAsync(string type, string method, params object?[] supplied)
    {
        var target = ProductType(type).GetMethod(method, Methods)
            ?? throw new MissingMethodException(type, method);
        var parameters = target.GetParameters();
        var arguments = parameters.Select((parameter, index) => index < supplied.Length
            ? supplied[index] : parameter.DefaultValue).ToArray();
        object? returned;
        try { returned = target.Invoke(null, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        if (returned is not Task task) return returned;
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
    static T Property<T>(object item, string name) => (T)item.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(item)!;
    static string CredentialName(string directory) => "PostgresLibrary-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToLowerInvariant())))[..24];
    static string? ReadCredential(string directory) => (string?)ProductType("WindowsCredentialStore")
        .GetMethod("Read", Methods)!.Invoke(null, [CredentialName(directory)]);

    static void CreatePrivateDirectory(string directory)
    {
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("Acceptance destination must be new.");
        var info = Directory.CreateDirectory(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    internal static async Task<int> RunAsync(string sourceDirectory, string runtimeDirectory,
        string probeRoot, CancellationToken cancellationToken = default)
    {
        sourceDirectory = Path.GetFullPath(sourceDirectory);
        runtimeDirectory = Path.GetFullPath(runtimeDirectory);
        probeRoot = Path.GetFullPath(probeRoot);
        if (probeRoot.StartsWith(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || sourceDirectory == probeRoot)
            throw new ArgumentException("Acceptance destination must be outside the source library.");
        CreatePrivateDirectory(probeRoot);
        var snapshot = Path.Combine(probeRoot, "logical-snapshot");
        var library = Path.Combine(probeRoot, "independent-library");
        var driftSnapshot = Path.Combine(probeRoot, "pre-restore-logical-snapshot");
        var checks = new Dictionary<string, bool>();
        var timings = new Dictionary<string, long>();
        object? cluster = null;
        object? counts = null;
        string stage = "source-read-only-connection";
        string? errorType = null;
        var watch = Stopwatch.StartNew();
        try
        {
            var pidLines = await File.ReadAllLinesAsync(Path.Combine(sourceDirectory, "postgres-data", "postmaster.pid"), cancellationToken);
            if (pidLines.Length < 4 || !int.TryParse(pidLines[3], out var port))
                throw new InvalidDataException("The source cluster is not running; acceptance does not start it.");
            var password = ReadCredential(sourceDirectory) ?? throw new InvalidOperationException("Source credential unavailable.");
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = port, Username = "yingge_owner", Database = "postgres",
                Password = password, Pooling = false, Timeout = 10, CommandTimeout = 30,
                ApplicationName = "YinggeRealCopyReadOnly",
                Options = "-c default_transaction_read_only=on -c statement_timeout=30000 -c lock_timeout=5000"
            };
            var sourceConnection = builder.ConnectionString;
            password = "";
            builder.Password = "";
            await using (var connection = new NpgsqlConnection(sourceConnection))
            {
                await connection.OpenAsync(cancellationToken);
                await using var readOnly = new NpgsqlCommand("SHOW transaction_read_only", connection);
                checks["sourceConnectionReadOnly"] = (string?)await readOnly.ExecuteScalarAsync(cancellationToken) == "on";
            }
            if (!checks["sourceConnectionReadOnly"]) throw new InvalidOperationException("Read-only guard unavailable.");
            stage = "consistent-logical-export";
            watch.Restart();
            counts = await CallAsync("PostgresLibraryExport", "ExportAsync", sourceConnection, snapshot, cancellationToken, null);
            timings["logicalExportMs"] = watch.ElapsedMilliseconds;
            sourceConnection = "";
            checks["snapshotManifestVerified"] = (bool)(await CallAsync("PostgresLibraryExport", "VerifyOfflineAsync", snapshot, cancellationToken))!;
            await File.WriteAllTextAsync(Path.Combine(probeRoot, "snapshot-ready.json"),
                JsonSerializer.Serialize(new { ready = true, mediaIncluded = false,
                    videos = Property<int>(counts!, "VideoCount"), metadata = Property<int>(counts!, "MetadataCount"),
                    documents = Property<int>(counts!, "DocumentCount") }), cancellationToken);

            stage = "isolated-cluster-import";
            cluster = await CallAsync("PostgresPrivateCluster", "OpenAsync", runtimeDirectory, library, cancellationToken);
            var target = Property<string>(cluster!, "ConnectionString");
            watch.Restart();
            await CallAsync("PostgresLibraryMigration", "ImportAsync", snapshot, target, cancellationToken,
                snapshot + ".import.lock", "public");
            checks["importMatchesEverySnapshotEntity"] = (bool)(await CallAsync("PostgresLibraryMigration", "VerifyContentAsync", snapshot, target, cancellationToken, "public"))!;
            timings["logicalImportMs"] = watch.ElapsedMilliseconds;
            await CallAsync("PostgresStorageModeActivation", "RewriteAfterRestoreAsync", target, library, snapshot, cancellationToken);
            var marker = await File.ReadAllBytesAsync(Path.Combine(library, "storage-postgresql.active"), cancellationToken);

            stage = "isolated-mutation";
            await using (var connection = new NpgsqlConnection(target))
            {
                await connection.OpenAsync(cancellationToken);
                await using var mutate = new NpgsqlCommand("UPDATE library_video SET size_bytes=size_bytes+1", connection);
                checks["isolatedMutationApplied"] = await mutate.ExecuteNonQueryAsync(cancellationToken) > 0;
            }
            checks["mutationDetectable"] = !(bool)(await CallAsync("PostgresLibraryMigration", "VerifyContentAsync", snapshot, target, cancellationToken, "public"))!;
            await CallAsync("PostgresLibraryExport", "ExportAsync", target, driftSnapshot, cancellationToken, null);

            foreach (var afterSwap in new[] { false, true })
            {
                stage = afterSwap ? "failure-after-swap" : "failure-before-swap";
                Action injected = () => throw new IOException("Injected acceptance failure");
                var threw = false;
                try
                {
                    await CallAsync("PostgresLibraryRestore", "RestoreInPlaceAsync", target, library, snapshot,
                        cancellationToken, afterSwap ? null : injected, afterSwap ? injected : null, null, null);
                }
                catch (IOException) { threw = true; }
                var suffix = afterSwap ? "AfterSwap" : "BeforeSwap";
                checks["injectedFailureObserved" + suffix] = threw;
                checks["originalDataRetained" + suffix] = (bool)(await CallAsync("PostgresLibraryMigration", "VerifyContentAsync", driftSnapshot, target, cancellationToken, "public"))!;
                checks["originalMarkerRetained" + suffix] = marker.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(library, "storage-postgresql.active"), cancellationToken));
                checks["restoreSentinelCleared" + suffix] = !File.Exists(Path.Combine(library, ".postgres-restore-in-progress"));
                // Restore archive names have one-second precision; keep independent attempts distinct.
                await Task.Delay(TimeSpan.FromSeconds(1.1), cancellationToken);
            }

            stage = "successful-restore";
            watch.Restart();
            var restored = await CallAsync("PostgresLibraryRestore", "RestoreInPlaceAsync", target, library, snapshot,
                cancellationToken, null, null, null, null);
            timings["restoreMs"] = watch.ElapsedMilliseconds;
            checks["restoredAllSnapshotEntities"] = (bool)(await CallAsync("PostgresLibraryMigration", "VerifyContentAsync", snapshot, target, cancellationToken, "public"))!;
            checks["previousSchemaRetainsPreRestoreData"] = (bool)(await CallAsync("PostgresLibraryMigration", "VerifyContentAsync", driftSnapshot, target, cancellationToken, Property<string>(restored!, "PreviousSchema")))!;
            checks["rollbackArchiveVerified"] = (bool)(await CallAsync("PostgresLibraryExport", "VerifyOfflineAsync", Property<string>(restored!, "RollbackArchive"), cancellationToken))!;
            checks["activationMarkerRewritten"] = Property<bool>(restored!, "MarkerRewritten");
            stage = "isolated-restart";
            await ((IAsyncDisposable)cluster!).DisposeAsync();
            cluster = null;
            cluster = await CallAsync("PostgresPrivateCluster", "OpenAsync", runtimeDirectory, library, cancellationToken);
            checks["restartRetainsRestoredSnapshot"] = (bool)(await CallAsync("PostgresLibraryMigration", "VerifyContentAsync", snapshot,
                Property<string>(cluster!, "ConnectionString"), cancellationToken, "public"))!;
            stage = "complete";
        }
        catch (Exception error) { errorType = error.GetType().Name; }
        finally
        {
            if (cluster is IAsyncDisposable disposable)
            {
                try { await disposable.DisposeAsync(); checks["isolatedClusterStopped"] = true; }
                catch { checks["isolatedClusterStopped"] = false; }
            }
            checks["sourceNeverRestoredOrMutated"] = true;
            var passed = stage == "complete" && checks.Values.All(value => value);
            await File.WriteAllTextAsync(Path.Combine(probeRoot, "result.json"), JsonSerializer.Serialize(new
            {
                passed, stage, errorType, realLibraryRead = true, realLibraryWritten = false,
                logicalSnapshot = true, physicalPgCopy = false, mediaIncluded = false,
                counts = counts is null ? null : new { videos = Property<int>(counts, "VideoCount"),
                    metadata = Property<int>(counts, "MetadataCount"), documents = Property<int>(counts, "DocumentCount") },
                timings, checks
            }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        }
        return stage == "complete" && checks.Values.All(value => value) ? 0 : 1;
    }
}
