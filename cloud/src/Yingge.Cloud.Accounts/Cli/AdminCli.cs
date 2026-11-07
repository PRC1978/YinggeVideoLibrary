using Microsoft.Extensions.Configuration;
using Yingge.Cloud.Accounts.Data;
using Yingge.Cloud.Accounts.Security;
using Yingge.Cloud.Accounts.Services;
using Yingge.Cloud.Accounts.Storage;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Accounts.Cli;

// Operator-only commands. They run with the owner connection string; the API
// process never sees that credential.
internal static class AdminCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0];
        var rest = args[1..];
        var adminConnection = FirstNonEmpty(Option(rest, "--admin-db"),
            Environment.GetEnvironmentVariable("YINGGE_CLOUD_ADMIN_DB"));
        if (string.IsNullOrWhiteSpace(adminConnection))
        {
            Console.Error.WriteLine("缺少管理连接串：使用 --admin-db 或环境变量 YINGGE_CLOUD_ADMIN_DB。");
            return 2;
        }

        await using var dataSource = Db.Create(adminConnection, 4, "yingge-cloud-admin");
        var audit = new AuditRepository(dataSource);
        var invites = new InviteRepository(dataSource);
        var accounts = new AccountRepository(dataSource);
        var sessions = new SessionRepository(dataSource);
        var reports = new ReportRepository(dataSource);

        switch (command)
        {
            case "migrate":
            {
                var applied = await Migrator.ApplyAsync(dataSource, Console.WriteLine, ct);
                Console.WriteLine(applied.Count == 0 ? "数据库已是最新。" : $"已应用 {applied.Count} 个迁移。");
                return 0;
            }
            case "migrate-down":
            {
                var target = Option(rest, "--to");
                Console.WriteLine("回滚前请确认已做数据库备份。");
                var rolledBack = await Migrator.RollbackAsync(dataSource, target, Console.WriteLine, ct);
                Console.WriteLine(rolledBack.Count == 0
                    ? "没有需要回滚的迁移。"
                    : $"已回滚 {rolledBack.Count} 个迁移。");
                return 0;
            }
            case "invite-create":
            {
                var note = Option(rest, "--note");
                var ttlHours = IntOption(rest, "--ttl-hours", 72);
                var count = Math.Clamp(IntOption(rest, "--count", 1), 1, 50);
                var expiresAt = DateTimeOffset.UtcNow.AddHours(ttlHours);
                for (var index = 0; index < count; index++)
                {
                    var code = TokenCodec.CreateInviteCode();
                    var normalized = AccountRules.NormalizeInviteCode(code);
                    var inviteId = await invites.CreateAsync(TokenCodec.Hash(normalized), note, expiresAt, ct);
                    await audit.LogAsync("invite.created", null, null,
                        new { invite_id = inviteId, expires_at = expiresAt, note_present = note is not null }, ct);
                    Console.WriteLine($"invite_code={code}");
                }
                Console.WriteLine($"expires_at={expiresAt:O}");
                return 0;
            }
            case "admin-create":
            {
                var username = AccountRules.NormalizeUsername(Option(rest, "--username"));
                if (AccountRules.ValidateUsername(username) is not null)
                {
                    Console.Error.WriteLine("管理员账号名无效。");
                    return 2;
                }
                Console.Error.WriteLine("从标准输入读取一次性初始密码（不会显示或写入日志）。");
                var initialPassword = await Console.In.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(initialPassword))
                {
                    Console.Error.WriteLine("初始密码为空。");
                    return 2;
                }
                var hash = new PasswordService(210_000).Hash(initialPassword);
                if (!await accounts.CreateAdminAsync(username, hash, ct))
                {
                    Console.Error.WriteLine("账号已存在，不会修改其角色或密码。");
                    return 1;
                }
                await audit.LogAsync("admin.created", null, null,
                    new { username, must_change_password = true }, ct);
                Console.WriteLine("管理员账号已创建，首次登录后必须修改密码。");
                return 0;
            }
            case "invite-revoke":
            {
                var code = Option(rest, "--code");
                if (string.IsNullOrWhiteSpace(code))
                {
                    Console.Error.WriteLine("用法：admin invite-revoke --code <邀请码>");
                    return 2;
                }
                var normalized = AccountRules.NormalizeInviteCode(code);
                var revoked = await invites.RevokeAsync(TokenCodec.Hash(normalized), ct);
                await audit.LogAsync("invite.revoked", null, null, new { revoked }, ct);
                Console.WriteLine(revoked ? "邀请码已撤销。" : "没有找到未使用的邀请码。");
                return revoked ? 0 : 1;
            }
            case "invites-revoke-all":
            {
                var revoked = await invites.RevokeAllPendingAsync(ct);
                await audit.LogAsync("invite.revoked_all", null, null, new { revoked }, ct);
                Console.WriteLine($"已撤销 {revoked} 个未使用邀请码。");
                return 0;
            }
            case "account-disable":
            case "account-enable":
            {
                var username = AccountRules.NormalizeUsername(Option(rest, "--username"));
                if (string.IsNullOrWhiteSpace(username))
                {
                    Console.Error.WriteLine("用法：admin account-disable --username <账号名>");
                    return 2;
                }
                var active = command == "account-enable";
                if (!await accounts.SetStatusAsync(username, active, ct))
                {
                    Console.Error.WriteLine("找不到该账号。");
                    return 1;
                }
                var revoked = active ? 0 : await sessions.RevokeAllForAccountAsync(
                    (await accounts.FindByUsernameAsync(username, ct))!.Id, ct);
                await audit.LogAsync(active ? "account.enabled" : "account.disabled", null, null,
                    new { username, revoked_sessions = revoked }, ct);
                Console.WriteLine(active
                    ? $"账号 {username} 已启用。"
                    : $"账号 {username} 已停用，撤销 {revoked} 个会话。");
                return 0;
            }
            case "session-revoke":
            {
                var username = AccountRules.NormalizeUsername(Option(rest, "--username"));
                var account = await accounts.FindByUsernameAsync(username, ct);
                if (account is null)
                {
                    Console.Error.WriteLine("找不到该账号。");
                    return 1;
                }
                var revoked = await sessions.RevokeAllForAccountAsync(account.Id, ct);
                await audit.LogAsync("session.revoked", account.Id, null,
                    new { reason = "operator", revoked }, ct);
                Console.WriteLine($"已撤销 {revoked} 个会话。");
                return 0;
            }
            case "purge-expired":
            {
                var root = ResolveStorageRoot(rest);
                if (root is null)
                {
                    Console.Error.WriteLine("缺少日志目录：使用 --storage-root 或环境变量 YINGGE_CLOUD_STORAGE_ROOT。");
                    return 2;
                }
                var files = new ReportFileStore(new StorageOptions { RootPath = root });
                using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
                var retention = new RetentionService(reports, files, audit,
                    loggerFactory.CreateLogger<RetentionService>());
                var purged = await retention.PurgeExpiredAsync(500, ct);
                Console.WriteLine($"已清理 {purged} 份过期日志。");
                return 0;
            }
            case "status":
            {
                Console.WriteLine($"accounts={await accounts.CountAsync(ct)}");
                Console.WriteLine($"active_sessions={await sessions.CountActiveAsync(ct)}");
                Console.WriteLine($"pending_invites={await invites.CountPendingAsync(ct)}");
                var (live, deleted) = await reports.CountAsync(ct);
                Console.WriteLine($"reports_live={live}");
                Console.WriteLine($"reports_deleted={deleted}");
                var applied = await Migrator.ListAppliedAsync(dataSource, ct);
                Console.WriteLine($"migrations={string.Join(',', applied.Select(item => item.Version))}");
                return 0;
            }
            default:
                Console.Error.WriteLine($"未知命令：{command}");
                PrintHelp();
                return 2;
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
            映格云端账号服务 · 运维命令
              admin migrate [--admin-db <连接串>]
              admin migrate-down [--to <版本号>]
              admin invite-create [--note <备注>] [--ttl-hours 72] [--count 1]
              admin admin-create --username <账号名>  # 初始密码从 stdin 读取
              admin invite-revoke --code <邀请码>
              admin invites-revoke-all
              admin account-disable --username <账号名>
              admin account-enable --username <账号名>
              admin session-revoke --username <账号名>
              admin purge-expired [--storage-root <目录>]
              admin status
            """);
    }

    static string? Option(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        return null;
    }

    static int IntOption(string[] args, string name, int fallback) =>
        int.TryParse(Option(args, name), out var value) && value > 0 ? value : fallback;

    static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    static string? ResolveStorageRoot(string[] args)
    {
        var explicitRoot = FirstNonEmpty(Option(args, "--storage-root"),
            Environment.GetEnvironmentVariable("YINGGE_CLOUD_STORAGE_ROOT"));
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return Path.GetFullPath(explicitRoot);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var configured = configuration["Storage:RootPath"];
        return string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
    }
}
