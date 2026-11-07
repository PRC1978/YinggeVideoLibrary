using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Yingge.Cloud.Accounts.Data;
using Yingge.Cloud.Accounts.Security;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Accounts.Api;

internal static class AdminEndpoints
{
    sealed record InviteCreateRequest(string? Note, int Hours);
    sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin").RequireRateLimiting(RateLimitSetup.Session);
        group.MapGet("/overview", Overview);
        group.MapGet("/accounts", Accounts);
        group.MapPost("/accounts/{username}/status/{status}", AccountStatus);
        group.MapGet("/invites", Invites);
        group.MapPost("/invites", CreateInvite);
        group.MapPost("/invites/{id:guid}/revoke", RevokeInvite);
        group.MapGet("/reports", Reports);
        group.MapGet("/audit", Audit);
        group.MapPost("/change-password", ChangePassword);
    }

    static async Task<(AccountRow? Account, IResult? Error)> AuthorizeAsync(
        HttpContext http, SessionAuthenticator authenticator, AccountRepository accounts,
        CloudOptions options, CancellationToken ct, bool allowInitialPassword = false)
    {
        if (options.Admin.AccessKeyHash is { Length: 32 } expected)
        {
            var key = http.Request.Headers["X-Admin-Key"].ToString();
            if (key.Length > 0)
            {
                var actual = SHA256.HashData(Encoding.UTF8.GetBytes(key));
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                    return (null, Problems.Create(403, "admin_forbidden", "管理员身份未通过验证。"));
            }
        }
        var (session, problem) = await authenticator.AuthenticateAsync(http, ct);
        if (session is null) return (null, Problems.Auth(problem));
        var account = await accounts.FindByIdAsync(session.AccountId, ct);
        if (account is null || !account.IsAdmin)
            return (null, Problems.Create(403, "admin_forbidden", "此账号没有管理权限。"));
        if (account.MustChangePassword && !allowInitialPassword)
            return (null, Problems.Create(403, "password_change_required", "请先修改初始密码。"));
        http.Response.Headers.CacheControl = "no-store";
        return (account, null);
    }

    static async Task<IResult> Overview(HttpContext http, SessionAuthenticator auth,
        AccountRepository accounts, InviteRepository invites, ReportRepository reports,
        SessionRepository sessions, CloudOptions options, CancellationToken ct)
    {
        var (account, error) = await AuthorizeAsync(http, auth, accounts, options, ct, true);
        if (error is not null) return error;
        if (account!.MustChangePassword) return Results.Json(new { mustChangePassword = true });
        var (live, deleted) = await reports.CountAsync(ct);
        return Results.Json(new { mustChangePassword = false, accounts = await accounts.CountAsync(ct),
            pendingInvites = await invites.CountPendingAsync(ct), activeSessions = await sessions.CountActiveAsync(ct),
            reportsLive = live, reportsDeleted = deleted });
    }

    static async Task<IResult> Accounts(HttpContext http, SessionAuthenticator auth,
        AccountRepository accounts, CloudOptions options, CancellationToken ct)
    {
        var (_, error) = await AuthorizeAsync(http, auth, accounts, options, ct);
        return error ?? Results.Json(await accounts.ListForAdminAsync(100, ct));
    }

    static async Task<IResult> AccountStatus(string username, string status, HttpContext http,
        SessionAuthenticator auth, AccountRepository accounts, SessionRepository sessions,
        AuditRepository audit, CloudOptions options, CancellationToken ct)
    {
        var (actor, error) = await AuthorizeAsync(http, auth, accounts, options, ct);
        if (error is not null) return error;
        if (status is not ("active" or "disabled")) return Results.BadRequest();
        username = AccountRules.NormalizeUsername(username);
        var target = await accounts.FindByUsernameAsync(username, ct);
        if (target is null) return Results.NotFound();
        if (target.IsAdmin || target.Id == actor!.Id)
            return Problems.Create(403, "admin_protected", "不能从网页修改管理员账号状态。");
        await accounts.SetStatusAsync(username, status == "active", ct);
        if (status == "disabled") await sessions.RevokeAllForAccountAsync(target.Id, ct);
        await audit.LogAsync("admin.account_status", actor.Id, AuthEndpoints.ClientIp(http),
            new { target_id = target.Id, status }, ct);
        return Results.NoContent();
    }

    static async Task<IResult> Invites(HttpContext http, SessionAuthenticator auth,
        AccountRepository accounts, AdminRepository admin, CloudOptions options, CancellationToken ct)
    {
        var (_, error) = await AuthorizeAsync(http, auth, accounts, options, ct);
        return error ?? Results.Json(await admin.InvitesAsync(ct));
    }

    static async Task<IResult> CreateInvite(InviteCreateRequest? request, HttpContext http,
        SessionAuthenticator auth, AccountRepository accounts, InviteRepository invites,
        AuditRepository audit, CloudOptions options, CancellationToken ct)
    {
        var (actor, error) = await AuthorizeAsync(http, auth, accounts, options, ct);
        if (error is not null) return error;
        if (request is null || request.Hours is < 1 or > 168 || request.Note?.Length > 200)
            return Problems.Create(400, ProblemCodes.InvalidRequest, "有效期需为 1–168 小时，备注最多 200 字。");
        var code = TokenCodec.CreateInviteCode();
        var expires = DateTimeOffset.UtcNow.AddHours(request.Hours);
        var id = await invites.CreateAsync(TokenCodec.Hash(AccountRules.NormalizeInviteCode(code)),
            request.Note, expires, ct);
        await audit.LogAsync("admin.invite_created", actor!.Id, AuthEndpoints.ClientIp(http),
            new { invite_id = id, expires_at = expires }, ct);
        return Results.Json(new { id, code, expiresAt = expires }, statusCode: 201);
    }

    static async Task<IResult> RevokeInvite(Guid id, HttpContext http, SessionAuthenticator auth,
        AccountRepository accounts, AdminRepository admin, AuditRepository audit,
        CloudOptions options, CancellationToken ct)
    {
        var (actor, error) = await AuthorizeAsync(http, auth, accounts, options, ct);
        if (error is not null) return error;
        if (!await admin.RevokeInviteByIdAsync(id, ct)) return Results.NotFound();
        await audit.LogAsync("admin.invite_revoked", actor!.Id, AuthEndpoints.ClientIp(http),
            new { invite_id = id }, ct);
        return Results.NoContent();
    }

    static async Task<IResult> Reports(HttpContext http, SessionAuthenticator auth,
        AccountRepository accounts, AdminRepository admin, CloudOptions options, CancellationToken ct)
    {
        var (_, error) = await AuthorizeAsync(http, auth, accounts, options, ct);
        return error ?? Results.Json(await admin.ReportsAsync(ct));
    }

    static async Task<IResult> Audit(HttpContext http, SessionAuthenticator auth,
        AccountRepository accounts, AdminRepository admin, CloudOptions options, CancellationToken ct)
    {
        var (_, error) = await AuthorizeAsync(http, auth, accounts, options, ct);
        return error ?? Results.Json(await admin.AuditAsync(ct));
    }

    static async Task<IResult> ChangePassword(PasswordChangeRequest? request, HttpContext http,
        SessionAuthenticator auth, AccountRepository accounts, SessionRepository sessions,
        AuditRepository audit, PasswordService passwords, CloudOptions options, CancellationToken ct)
    {
        var (actor, error) = await AuthorizeAsync(http, auth, accounts, options, ct, true);
        if (error is not null) return error;
        if (request is null || passwords.Verify(actor!.PasswordHash, request.CurrentPassword) ==
            PasswordVerificationResult.Failed)
            return Problems.Create(403, "password_invalid", "当前密码不正确。");
        var validation = AccountRules.ValidatePassword(request.NewPassword, actor.Username);
        if (validation is not null || request.NewPassword.Length < 16)
            return Problems.Create(400, ProblemCodes.InvalidRequest,
                validation ?? "管理员密码至少需要 16 个字符。");
        if (request.NewPassword == request.CurrentPassword)
            return Problems.Create(400, ProblemCodes.InvalidRequest, "新密码不能与旧密码相同。");
        var hash = await Task.Run(() => passwords.Hash(request.NewPassword), ct);
        await accounts.ChangeAdminPasswordAsync(actor.Id, hash, ct);
        await sessions.RevokeAllForAccountAsync(actor.Id, ct);
        await audit.LogAsync("admin.password_changed", actor.Id, AuthEndpoints.ClientIp(http), new { }, ct);
        return Results.NoContent();
    }
}
