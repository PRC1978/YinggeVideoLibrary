using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace YinggeVideoLibrary;

// A single process owns the private PostgreSQL cluster for one library.
// Normal app startup does not call this until every writer has been cut over.
internal sealed class PostgresPrivateCluster : IAsyncDisposable
{
    internal static string CredentialNameFor(string libraryDirectory) =>
        "PostgresLibrary-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(libraryDirectory).ToLowerInvariant())))[..24];

    private readonly FileStream ownerLease;
    private readonly string controlBinary;
    private readonly string runtimeDirectory;
    private readonly string dataDirectory;
    private bool stopped;

    internal string ConnectionString { get; }

    private PostgresPrivateCluster(FileStream lease, string control, string runtime,
        string data, string connectionString)
    {
        ownerLease = lease;
        controlBinary = control;
        runtimeDirectory = runtime;
        dataDirectory = data;
        ConnectionString = connectionString;
    }

    internal static async Task<PostgresPrivateCluster> OpenAsync(string runtimeDirectory,
        string libraryDirectory, CancellationToken cancellationToken = default)
    {
        runtimeDirectory = Path.GetFullPath(runtimeDirectory);
        libraryDirectory = Path.GetFullPath(libraryDirectory);
        var initBinary = Path.Combine(runtimeDirectory, "bin", "initdb.exe");
        var controlBinary = Path.Combine(runtimeDirectory, "bin", "pg_ctl.exe");
        var postgresBinary = Path.Combine(runtimeDirectory, "bin", "postgres.exe");
        var requiredFiles = new[] { initBinary, controlBinary, postgresBinary,
            Path.Combine(runtimeDirectory, "share", "postgres.bki") };
        var missingFiles = requiredFiles.Where(path => !File.Exists(path))
            .Select(path => Path.GetRelativePath(runtimeDirectory, path)).ToArray();
        if (missingFiles.Length != 0)
            throw new InvalidDataException(
                "当前程序目录缺少 PostgreSQL 组件（" + string.Join("、", missingFiles) +
                "）。请使用完整安装包，或将便携包完整解压后从包内启动；单独复制映格视频库.exe 无法使用数据库。程序目录：" +
                Path.GetDirectoryName(runtimeDirectory));
        Directory.CreateDirectory(libraryDirectory);
        FileStream lease;
        try
        {
            lease = new FileStream(Path.Combine(libraryDirectory, ".postgres-cluster-owner.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
        {
            throw new InvalidOperationException(
                "这个视频库已被运行中的映格占用。它可能缩到了系统托盘；请从托盘打开映格，或在任务管理器中检查后台进程。请勿删除锁文件。",
                error);
        }
        try
        {
            var data = PostgresClusterLocation.DataPath(libraryDirectory);
            var credentialName = CredentialNameFor(libraryDirectory);
            var credential = WindowsCredentialStore.Read(credentialName);
            var versionFile = Path.Combine(data, "PG_VERSION");
            if (!File.Exists(versionFile))
            {
                if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any())
                    throw new InvalidDataException("数据库目录不完整，拒绝重新初始化以免覆盖资料。 ");
                if (credential is not null)
                    throw new InvalidDataException("发现数据库凭据但没有有效数据库目录，拒绝重新初始化。 ");
                credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var passwordFile = Path.Combine(libraryDirectory,
                    ".postgres-init-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await File.WriteAllTextAsync(passwordFile, credential + Environment.NewLine,
                        new UTF8Encoding(false), cancellationToken);
                    var init = await RunAsync(initBinary, ["-D", data, "-U", "yingge_owner",
                        "--auth-host=scram-sha-256", "--auth-local=scram-sha-256",
                        "--encoding=UTF8", "--no-locale", "--pwfile=" + passwordFile,
                        "--no-instructions"], runtimeDirectory, cancellationToken);
                    if (init != 0) throw new InvalidOperationException("私有数据库初始化失败。 ");
                    WindowsCredentialStore.Write(credentialName, credential);
                }
                finally { if (File.Exists(passwordFile)) File.Delete(passwordFile); }
            }
            if (string.IsNullOrWhiteSpace(credential))
                throw new InvalidDataException("私有数据库凭据缺失，拒绝用空口令启动。 ");
            var clusterVersion = (await File.ReadAllTextAsync(versionFile, cancellationToken)).Trim();
            using (var process = new Process { StartInfo = new ProcessStartInfo(postgresBinary)
                { Arguments = "--version", UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true } })
            {
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0 || !output.Contains(" " + clusterVersion + ".", StringComparison.Ordinal))
                    throw new InvalidDataException($"数据库文件版本 {clusterVersion} 与随程序提供的 PostgreSQL 不匹配。现有资料未被改动；请先用原版本导出并校验备份，再按升级说明迁移，勿删除数据库目录。");
            }
            if (File.Exists(Path.Combine(data, "postmaster.pid")) &&
                await RunAsync(controlBinary, ["-D", data, "status"], runtimeDirectory,
                    cancellationToken) == 0)
            {
                // An earlier desktop process may have died while its child
                // PostgreSQL stayed alive. pg_ctl start can report success for
                // that old server even though it is not on our new port.
                // The exclusive owner lease makes it safe to stop this exact
                // data directory before starting our own listener.
                if (await RunAsync(controlBinary,
                        ["-D", data, "-m", "fast", "-w", "stop"], runtimeDirectory,
                        cancellationToken, TimeSpan.FromSeconds(15)) != 0)
                    throw new IOException("发现遗留数据库进程，但未能安全停止；现有资料未改动。 ");
            }
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            async Task<int> StartClusterAsync() => await RunAsync(controlBinary,
                ["-D", data, "-l", Path.Combine(libraryDirectory, "postgres.log"),
                 "-o", $"-h 127.0.0.1 -p {port}", "-w", "start"],
                runtimeDirectory, cancellationToken);
            var started = await StartClusterAsync();
            if (started != 0 && File.Exists(Path.Combine(data, "postmaster.pid")))
            {
                // The desktop process may have crashed while its PostgreSQL
                // child remained alive. We hold the exclusive cluster-owner
                // lease, so stop only this data directory, then retry once.
                var stoppedOrphan = await RunAsync(controlBinary,
                    ["-D", data, "-m", "fast", "-w", "stop"],
                    runtimeDirectory, cancellationToken);
                if (stoppedOrphan == 0) started = await StartClusterAsync();
            }
            if (started != 0)
            {
                // pg_ctl may time out after postgres has actually launched.
                // Do not leave that process owning the library after we release
                // the owner lease.
                try { await RunAsync(controlBinary, ["-D", data, "-m", "fast", "-w", "stop"],
                    runtimeDirectory, CancellationToken.None); } catch { }
                throw new InvalidOperationException("私有数据库启动失败。 ");
            }
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = port, Database = "postgres", Username = "yingge_owner",
                Password = credential, Pooling = false, Timeout = 5, SslMode = SslMode.Disable
            };
            try
            {
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand(
                    "SELECT host(inet_server_addr()), current_setting('listen_addresses')", connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "127.0.0.1" ||
                    reader.GetString(1) != "127.0.0.1")
                    throw new InvalidDataException("私有数据库没有仅监听本机，拒绝使用。 ");
                return new PostgresPrivateCluster(lease, controlBinary, runtimeDirectory,
                    data, builder.ConnectionString);
            }
            catch
            {
                await RunAsync(controlBinary, ["-D", data, "-m", "fast", "-w", "stop"],
                    runtimeDirectory, CancellationToken.None);
                throw;
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (stopped) return;
        stopped = true;
        try
        {
            var exitCode = await RunAsync(controlBinary,
                ["-D", dataDirectory, "-m", "fast", "-w", "stop"],
                runtimeDirectory, CancellationToken.None, TimeSpan.FromSeconds(15));
            if (exitCode != 0) throw new IOException("私有数据库未能正常停止。 ");
        }
        finally { ownerLease.Dispose(); }
    }

    private static async Task<int> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, CancellationToken cancellationToken, TimeSpan? timeoutLimit = null)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
            { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutLimit ?? TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return process.ExitCode;
    }
}
