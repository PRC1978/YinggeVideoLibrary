using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace YinggeVideoLibrary;

// An isolated runtime probe. Normal startup must not use this until all JSON
// writers and backup/restore have been switched together.
internal static class BundledPostgresRuntime
{
    internal static async Task<int> RunProbeToFileAsync(string runtimeDirectory, string resultPath)
    {
        var findings = new Dictionary<string, object?>();
        var probeRoot = Path.Combine(Path.GetTempPath(), "YinggePgProbe-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(probeRoot, "data");
        var passwordFile = Path.Combine(probeRoot, "init-password.txt");
        var credentialName = "PostgresProbe-" + Guid.NewGuid().ToString("N");
        var ctl = Path.Combine(runtimeDirectory, "bin", "pg_ctl.exe");
        var initdb = Path.Combine(runtimeDirectory, "bin", "initdb.exe");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var started = false;
        try
        {
            if (!File.Exists(initdb) || !File.Exists(ctl) ||
                !File.Exists(Path.Combine(runtimeDirectory, "share", "postgres.bki")))
                throw new InvalidDataException("PostgreSQL runtime is incomplete.");
            Directory.CreateDirectory(probeRoot);
            // Only a random test secret is temporarily written. The regular
            // user credential must live in Windows Credential Manager.
            await File.WriteAllTextAsync(passwordFile, password + Environment.NewLine,
                new UTF8Encoding(false));
            var initialized = await RunAsync(initdb, ["-D", data, "-U", "yingge_owner",
                "--auth-host=scram-sha-256", "--auth-local=scram-sha-256",
                "--encoding=UTF8", "--no-locale", "--pwfile=" + passwordFile,
                "--no-instructions"], runtimeDirectory);
            if (initialized.ExitCode != 0) throw new InvalidOperationException(
                "initdb failed: " + Tail(initialized.Error + initialized.Output));
            WindowsCredentialStore.Write(credentialName, password);
            File.Delete(passwordFile);

            // Hold a socket to choose a free loopback port, then release it
            // immediately before pg_ctl starts. Connection verification below
            // catches the unlikely race with another local process.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var startedResult = await RunAsync(ctl, ["-D", data, "-l", Path.Combine(probeRoot, "server.log"),
                "-o", $"-h 127.0.0.1 -p {port}", "-w", "start"], runtimeDirectory,
                captureOutput: false);
            if (startedResult.ExitCode != 0) throw new InvalidOperationException(
                "pg_ctl start failed: " + Tail(startedResult.Error + startedResult.Output));
            started = true;
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = port, Username = "yingge_owner",
                Password = WindowsCredentialStore.Read(credentialName), Database = "postgres",
                Pooling = false, Timeout = 5, SslMode = SslMode.Disable
            };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT current_setting('server_version'), host(inet_server_addr()), current_setting('listen_addresses')",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidDataException("PostgreSQL did not answer.");
            var version = reader.GetString(0);
            var address = reader.GetString(1);
            var listenAddresses = reader.GetString(2);
            findings["version"] = version;
            findings["serverAddress"] = address;
            findings["listenAddresses"] = listenAddresses;
            findings["loopbackOnly"] = address == "127.0.0.1" && listenAddresses == "127.0.0.1";
            findings["credentialRoundTrip"] = WindowsCredentialStore.Read(credentialName) == password;
            var sourceDirectory = Path.Combine(probeRoot, "source");
            Directory.CreateDirectory(sourceDirectory);
            var samplePath = Path.Combine(probeRoot, "media", "sample.mp4");
            var videoId = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(samplePath.ToLowerInvariant())))[..24].ToLowerInvariant();
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "library-index.json"),
                JsonSerializer.Serialize(new LibraryIndex
                {
                    Folders = [Path.GetDirectoryName(samplePath)!],
                    Entries = [new IndexEntry { Path = samplePath, Size = 42,
                        ModifiedTicks = DateTime.UtcNow.Ticks }]
                }));
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "metadata.json"),
                JsonSerializer.Serialize(new Dictionary<string, MovieMetadata>
                    { [videoId] = new MovieMetadata { Title = "isolated sample" } }));
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "config.json"),
                "{\"folders\":[],\"categories\":[\"isolated\"]}");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "scrape-queue.json"),
                "{\"items\":[]}");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "role-profiles.json"),
                "{\"profiles\":[]}");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "review-history.jsonl"),
                "{\"id\":\"isolated-review\"}\n");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory,
                "scrape-queue.snapshot.00000001.json"), "{\"items\":[]}");
            var activeSessionBlocked = false;
            using (LibraryBackupService.AcquireSessionLease(sourceDirectory))
            {
                try { await PostgresLibraryMigration.ImportAsync(sourceDirectory, builder.ConnectionString); }
                catch (IOException) { activeSessionBlocked = true; }
            }
            findings["activeSessionImportBlocked"] = activeSessionBlocked;
            var imported = await PostgresLibraryMigration.ImportAsync(sourceDirectory, builder.ConnectionString);
            var verified = await PostgresLibraryMigration.VerifyContentAsync(sourceDirectory, builder.ConnectionString);
            findings["sampleImportVerified"] = imported.VideoCount == 1 &&
                imported.MetadataCount == 1 && verified;
            var configDocument = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "config.json");
            var revisedConfig = Encoding.UTF8.GetBytes("{\"folders\":[],\"categories\":[\"verified\"]}");
            var revisedHash = await PostgresLibraryDocumentStore.SaveAsync(
                builder.ConnectionString, "config.json", revisedConfig,
                configDocument?.Revision);
            var reloadedConfig = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "config.json");
            var staleWriteRejected = false;
            try
            {
                await PostgresLibraryDocumentStore.SaveAsync(builder.ConnectionString,
                    "config.json", Encoding.UTF8.GetBytes("{}"), configDocument?.Revision);
            }
            catch (InvalidOperationException) { staleWriteRejected = true; }
            findings["stateDocumentCasVerified"] = configDocument is not null &&
                reloadedConfig?.Revision == revisedHash &&
                reloadedConfig.Content.SequenceEqual(revisedConfig) && staleWriteRejected;
            findings["passed"] = version.StartsWith("18.6", StringComparison.Ordinal) &&
                findings["loopbackOnly"] is true && findings["credentialRoundTrip"] is true &&
                findings["sampleImportVerified"] is true && findings["stateDocumentCasVerified"] is true &&
                findings["activeSessionImportBlocked"] is true;
        }
        catch (Exception ex)
        {
            findings["passed"] = false;
            findings["error"] = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            if (started)
            {
                try
                {
                    var stopped = await RunAsync(ctl, ["-D", data, "-m", "fast", "-w", "stop"], runtimeDirectory,
                        captureOutput: false);
                    findings["stopped"] = stopped.ExitCode == 0;
                }
                catch { findings["stopped"] = false; }
            }
            if (File.Exists(passwordFile)) File.Delete(passwordFile);
            WindowsCredentialStore.Write(credentialName, "");
        }
        findings["realLibraryRead"] = false;
        findings["probeDirectory"] = probeRoot;
        findings["passed"] = findings["passed"] is true && findings.GetValueOrDefault("stopped") is true;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(findings,
            new JsonSerializerOptions { WriteIndented = true }));
        return findings["passed"] is true ? 0 : 1;
    }

    static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string executable, IEnumerable<string> arguments, string workDirectory,
        bool captureOutput = true)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = captureOutput, RedirectStandardError = captureOutput
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = captureOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var stderr = captureOutput ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(Path.GetFileName(executable) + " exceeded two minutes.");
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    static string Tail(string text) => text.Length <= 1000 ? text : text[^1000..];
}
