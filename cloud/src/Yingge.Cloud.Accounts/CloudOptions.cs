using System.Net;

namespace Yingge.Cloud.Accounts;

internal sealed class DatabaseOptions
{
    public string AppConnectionString { get; init; } = "";
    public int PoolMaxSize { get; init; } = 10;
}

internal sealed class SecurityOptions
{
    public bool RequireHttps { get; init; } = true;
    public int SessionHours { get; init; } = 8;
    public int MaxFailedLogins { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 15;
    public int PasswordIterations { get; init; } = 210_000;
    public int LoginPerMinute { get; init; } = 10;
    public int RegisterPerHour { get; init; } = 5;
    public int GlobalPerMinute { get; init; } = 300;
    public IReadOnlyList<IPAddress> KnownProxies { get; init; } = [];
}

internal sealed class ReportOptions
{
    public int RetentionDays { get; init; } = 30;
    public int DailyLimit { get; init; } = 10;
    public int BurstLimit { get; init; } = 3;
    public int BurstWindowMinutes { get; init; } = 5;
    public int SweepMinutes { get; init; } = 15;
}

internal sealed class StorageOptions
{
    public string RootPath { get; init; } = "";
}

// Configuration is read once at startup. Anything invalid stops the process
// instead of degrading into an insecure default.
internal sealed class CloudOptions
{
    public required DatabaseOptions Database { get; init; }
    public required SecurityOptions Security { get; init; }
    public required ReportOptions Reports { get; init; }
    public required StorageOptions Storage { get; init; }

    public static CloudOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var appConnection = FirstNonEmpty(
            configuration["Database:AppConnectionString"],
            System.Environment.GetEnvironmentVariable("YINGGE_CLOUD_APP_DB"));
        if (string.IsNullOrWhiteSpace(appConnection))
            throw new InvalidOperationException(
                "缺少数据库连接串：设置 Database:AppConnectionString 或环境变量 YINGGE_CLOUD_APP_DB。");

        var storageRoot = FirstNonEmpty(
            configuration["Storage:RootPath"],
            System.Environment.GetEnvironmentVariable("YINGGE_CLOUD_STORAGE_ROOT"));
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new InvalidOperationException(
                "缺少日志存放目录：设置 Storage:RootPath 或环境变量 YINGGE_CLOUD_STORAGE_ROOT。");

        var resolvedRoot = Path.GetFullPath(Path.IsPathFullyQualified(storageRoot)
            ? storageRoot
            : Path.Combine(environment.ContentRootPath, storageRoot));
        var contentRoot = Path.GetFullPath(environment.ContentRootPath);
        if (IsUnder(resolvedRoot, contentRoot))
            throw new InvalidOperationException("日志存放目录必须位于 Web 根目录之外，避免被静态文件中间件暴露。");

        var security = configuration.GetSection("Security");
        var reports = configuration.GetSection("Reports");

        return new CloudOptions
        {
            Database = new DatabaseOptions
            {
                AppConnectionString = appConnection,
                PoolMaxSize = Positive(configuration.GetValue("Database:PoolMaxSize", 10), 10)
            },
            Security = new SecurityOptions
            {
                RequireHttps = configuration.GetValue("Security:RequireHttps", true),
                SessionHours = Positive(security.GetValue("SessionHours", 8), 8),
                MaxFailedLogins = Positive(security.GetValue("MaxFailedLogins", 5), 5),
                LockoutMinutes = Positive(security.GetValue("LockoutMinutes", 15), 15),
                PasswordIterations = Math.Max(1_000, security.GetValue("PasswordIterations", 210_000)),
                LoginPerMinute = Positive(security.GetValue("LoginPerMinute", 10), 10),
                RegisterPerHour = Positive(security.GetValue("RegisterPerHour", 5), 5),
                GlobalPerMinute = Positive(security.GetValue("GlobalPerMinute", 300), 300),
                KnownProxies = ParseProxies(security.GetSection("KnownProxies"))
            },
            Reports = new ReportOptions
            {
                RetentionDays = Positive(reports.GetValue("RetentionDays", 30), 30),
                DailyLimit = Positive(reports.GetValue("DailyLimit", 10), 10),
                BurstLimit = Positive(reports.GetValue("BurstLimit", 3), 3),
                BurstWindowMinutes = Positive(reports.GetValue("BurstWindowMinutes", 5), 5),
                SweepMinutes = Positive(reports.GetValue("SweepMinutes", 15), 15)
            },
            Storage = new StorageOptions { RootPath = resolvedRoot }
        };
    }

    static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    static int Positive(int value, int fallback) => value > 0 ? value : fallback;

    static IReadOnlyList<IPAddress> ParseProxies(IConfigurationSection section)
    {
        var proxies = new List<IPAddress>();
        foreach (var child in section.GetChildren())
            if (IPAddress.TryParse(child.Value, out var address)) proxies.Add(address);
        return proxies;
    }

    static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, comparison) ||
               string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), comparison);
    }
}
