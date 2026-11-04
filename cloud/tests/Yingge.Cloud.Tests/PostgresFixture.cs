using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;
using Yingge.Cloud.Accounts;
using Yingge.Cloud.Accounts.Data;

namespace Yingge.Cloud.Tests;

// Starts a throwaway PostgreSQL cluster in a temp directory. The cluster listens
// on loopback only, uses trust auth (it exists for the duration of the test run)
// and is deleted afterwards. Production guidance never mirrors this.
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string OwnerPassword = "owner-test-password";
    public const string AppPassword = "app-test-password";
    const string Database = "yingge_cloud_test";

    readonly List<string> log = [];
    Process? postmaster;
    string dataDirectory = "";

    public string Root { get; private set; } = "";

    public string BinDirectory { get; private set; } = "";

    public int Port { get; private set; }

    public string OwnerConnectionString =>
        $"Host=127.0.0.1;Port={Port};Database={Database};Username=yingge_owner;Password={OwnerPassword};Pooling=true;Maximum Pool Size=10";

    public string AppConnectionString =>
        $"Host=127.0.0.1;Port={Port};Database={Database};Username=yingge_app;Password={AppPassword};Pooling=true;Maximum Pool Size=20";

    public NpgsqlDataSource OwnerDataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        BinDirectory = LocateBinaries();
        Root = Path.Combine(Path.GetTempPath(), "yingge-cloud-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        dataDirectory = Path.Combine(Root, "data");
        Port = FreePort();

        await RunAsync("initdb", $"-D \"{dataDirectory}\" -U postgres -A trust -E UTF8 --locale=C");
        postmaster = StartPostmaster();
        await WaitUntilReadyAsync();

        await ExecuteAdminAsync("postgres", $"""
            create role yingge_owner login password '{OwnerPassword}' nosuperuser nocreatedb nocreaterole;
            create role yingge_app login password '{AppPassword}' nosuperuser nocreatedb nocreaterole;
            create database {Database} owner yingge_owner;
            """, viaPsql: true);
        await ExecuteAdminAsync(Database, "revoke all on schema public from public;", viaPsql: true);

        OwnerDataSource = Db.Create(OwnerConnectionString, 6, "tests-migrate");
        await Migrator.ApplyAsync(OwnerDataSource, null, CancellationToken.None);

        // The application role gets DML only. DDL stays with the owner, so a
        // compromised service account cannot reshape the schema.
        await ExecuteAdminAsync(Database, """
            grant usage on schema public to yingge_app;
            grant select, insert, update, delete on all tables in schema public to yingge_app;
            grant usage, select on all sequences in schema public to yingge_app;
            """, viaPsql: true);
    }

    public async Task DisposeAsync()
    {
        await OwnerDataSource.DisposeAsync();
        if (postmaster is { HasExited: false })
        {
            try
            {
                await RunAsync("pg_ctl", $"-D \"{dataDirectory}\" stop -m immediate", throwOnError: false);
            }
            catch (Exception)
            {
                // Fall through to the kill below.
            }
        }

        if (postmaster is { HasExited: false })
        {
            postmaster.Kill(entireProcessTree: true);
            await postmaster.WaitForExitAsync();
        }
        postmaster?.Dispose();

        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Temp data only; the OS cleans it up eventually.
        }
    }

    public async Task ResetAsync()
    {
        await ExecuteAdminAsync(Database,
            "truncate table error_reports, sessions, invites, audit_events, accounts restart identity cascade;",
            viaPsql: true);
    }

    public async Task ExecuteOwnerAsync(string sql)
    {
        await using var connection = await OwnerDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public Task RunToolAsync(string executable, string arguments) => RunAsync(executable, arguments);

    public string ConnectionStringFor(string database) =>
        $"Host=127.0.0.1;Port={Port};Database={database};Username=yingge_owner;Password={OwnerPassword};Pooling=false";

    public async Task<string> CreateDatabaseAsync(string database)
    {
        await ExecuteAdminAsync("postgres", $"drop database if exists {database};", viaPsql: true);
        await ExecuteAdminAsync("postgres", $"create database {database} owner yingge_owner;", viaPsql: true);
        return ConnectionStringFor(database);
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = await OwnerDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull) return default;
        return (T)Convert.ChangeType(value, typeof(T));
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    async Task WaitUntilReadyAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (postmaster is { HasExited: true })
                throw new InvalidOperationException("PostgreSQL 未能启动：" + string.Join('\n', LogTail()));

            try
            {
                await using var connection = new NpgsqlConnection(
                    $"Host=127.0.0.1;Port={Port};Database=postgres;Username=postgres;Timeout=3;Pooling=false");
                await connection.OpenAsync();
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException)
            {
                last = exception;
                await Task.Delay(250);
            }
        }

        throw new TimeoutException("等待 PostgreSQL 就绪超时。", last);
    }

    Process StartPostmaster()
    {
        var info = new ProcessStartInfo(Executable("postgres"),
            $"-D \"{dataDirectory}\" -p {Port} -c listen_addresses=127.0.0.1 " +
            "-c fsync=off -c full_page_writes=off -c synchronous_commit=off -c max_connections=100")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, args) => AppendLog(args.Data);
        process.ErrorDataReceived += (_, args) => AppendLog(args.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    void AppendLog(string? line)
    {
        if (line is null) return;
        lock (log) log.Add(line);
    }

    string[] LogTail()
    {
        lock (log) return log.TakeLast(20).ToArray();
    }

    async Task ExecuteAdminAsync(string database, string sql, bool viaPsql)
    {
        if (!viaPsql)
        {
            await using var connection = new NpgsqlConnection(
                $"Host=127.0.0.1;Port={Port};Database={database};Username=postgres;Pooling=false");
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            return;
        }

        var script = Path.Combine(Root, "script-" + Guid.NewGuid().ToString("N")[..6] + ".sql");
        await File.WriteAllTextAsync(script, sql);
        await RunAsync("psql",
            $"-h 127.0.0.1 -p {Port} -U postgres -d {database} -v ON_ERROR_STOP=1 -f \"{script}\"");
    }

    async Task RunAsync(string executable, string arguments, bool throwOnError = true)
    {
        var info = new ProcessStartInfo(Executable(executable), arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.Environment["PGCLIENTENCODING"] = "UTF8";

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("无法启动 " + executable);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{executable} 退出码 {process.ExitCode}。{Environment.NewLine}{stderr}{stdout}");
    }

    string Executable(string name) => Path.Combine(BinDirectory,
        OperatingSystem.IsWindows() ? name + ".exe" : name);

    static string LocateBinaries()
    {
        var configured = Environment.GetEnvironmentVariable("YINGGE_TEST_PG_BIN");
        if (!string.IsNullOrWhiteSpace(configured) && HasServer(configured))
            return configured;

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".yingge-cloud", "pg", "bin");
        if (HasServer(local)) return local;

        var candidates = new List<string>();
        var programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        if (Directory.Exists(programFiles))
            candidates.AddRange(Directory.EnumerateDirectories(programFiles)
                .Select(path => Path.Combine(path, "bin")));

        // Ubuntu 22.04 keeps PostgreSQL under /usr/lib/postgresql/<version>/bin.
        const string systemRoot = "/usr/lib/postgresql";
        if (Directory.Exists(systemRoot))
            candidates.AddRange(Directory.EnumerateDirectories(systemRoot)
                .Select(path => Path.Combine(path, "bin")));

        var match = candidates
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(HasServer);
        if (match is not null) return match;

        throw new InvalidOperationException(
            "找不到 PostgreSQL 二进制。设置环境变量 YINGGE_TEST_PG_BIN 指向包含 postgres 的 bin 目录" +
            "（Windows 也可用 scripts/Get-PostgreSql.ps1 下载）。");
    }

    static bool HasServer(string directory) =>
        File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "postgres.exe" : "postgres"));
}

[CollectionDefinition(CloudCollection.Name)]
public sealed class CloudCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "cloud";
}
