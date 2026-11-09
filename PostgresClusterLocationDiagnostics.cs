using System.IO;
using System.Text.Json;
using Npgsql;

namespace YinggeVideoLibrary;

internal static class PostgresClusterLocationDiagnostics
{
    internal static async Task<int> RunToFileAsync(string runtime, string report)
    {
        var findings = new Dictionary<string, bool>();
        var root = Path.Combine(Path.GetTempPath(), "YinggePgMove-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "library");
        var parent = Path.Combine(root, "new-disk");
        var secondParent = Path.Combine(root, "another-disk");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(secondParent);
        var credentialName = PostgresPrivateCluster.CredentialNameFor(library);
        var passed = false;
        try
        {
            await using (var cluster = await PostgresPrivateCluster.OpenAsync(runtime, library))
            await using (var connection = new NpgsqlConnection(cluster.ConnectionString))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand(
                    "CREATE TABLE yingge_location_probe(value integer NOT NULL); INSERT INTO yingge_location_probe VALUES(73)",
                    connection);
                await create.ExecuteNonQueryAsync();
            }
            File.WriteAllText(Path.Combine(library, PostgresStorageModeGuard.MarkerName), "postgresql-v1");
            var original = PostgresClusterLocation.DataPath(library);
            var target = PostgresClusterLocation.Stage(library, parent);
            findings["stageKeepsOriginal"] = Directory.Exists(original) &&
                !Directory.Exists(target) && PostgresClusterLocation.PendingTarget(library) == target;
            findings["movedAndMarkerCommitted"] = await PostgresClusterLocation.ApplyPendingAsync(library) &&
                PostgresClusterLocation.DataPath(library) == target &&
                Directory.Exists(original) && Directory.Exists(target) &&
                PostgresClusterLocation.PendingTarget(library) is null;
            await using (var moved = await PostgresPrivateCluster.OpenAsync(runtime, library))
            await using (var connection = new NpgsqlConnection(moved.ConnectionString))
            {
                await connection.OpenAsync();
                await using var count = new NpgsqlCommand("SELECT value FROM yingge_location_probe", connection);
                findings["reopensRealPostgresWithData"] = Convert.ToInt32(await count.ExecuteScalarAsync()) == 73;
            }
            var occupied = PostgresClusterLocation.Stage(library, secondParent);
            Directory.CreateDirectory(occupied);
            try
            {
                await PostgresClusterLocation.ApplyPendingAsync(library);
                findings["occupiedDestinationKeepsCurrent"] = false;
            }
            catch (IOException)
            {
                findings["occupiedDestinationKeepsCurrent"] =
                    PostgresClusterLocation.DataPath(library) == target && Directory.Exists(target);
            }
            Directory.Delete(occupied);
            PostgresClusterLocation.CancelPending(library);
            findings["pendingCanBeCanceledAfterFailure"] =
                PostgresClusterLocation.PendingTarget(library) is null &&
                PostgresClusterLocation.DataPath(library) == target;
            PostgresClusterLocation.RollbackRecent(library);
            findings["rollbackReturnsToOriginal"] = PostgresClusterLocation.DataPath(library) == original;
            await using (var restored = await PostgresPrivateCluster.OpenAsync(runtime, library))
            await using (var connection = new NpgsqlConnection(restored.ConnectionString))
            {
                await connection.OpenAsync();
                await using var count = new NpgsqlCommand("SELECT value FROM yingge_location_probe", connection);
                findings["rollbackReopensRealPostgres"] = Convert.ToInt32(await count.ExecuteScalarAsync()) == 73;
            }
        }
        catch (Exception error)
        {
            findings["unexpectedError"] = false;
            findings["errorType_" + error.GetType().Name] = false;
            findings["errorMessage_" + error.Message] = false;
        }
        finally
        {
            WindowsCredentialStore.Write(credentialName, "");
            passed = findings.Count >= 5 && findings.Values.All(value => value);
            if (passed)
                try { Directory.Delete(root, recursive: true); } catch { }
        }
        var result = new { passed, realLibraryRead = false, findings,
            failedFixture = passed ? null : root,
            postgresLog = passed ? null : (File.Exists(Path.Combine(library, "postgres.log"))
                ? File.ReadAllText(Path.Combine(library, "postgres.log")) : null) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.passed ? 0 : 1;
    }
}
