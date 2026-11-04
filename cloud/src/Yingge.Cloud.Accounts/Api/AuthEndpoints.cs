using Yingge.Cloud.Accounts.Data;
using Yingge.Cloud.Accounts.Security;
using Yingge.Cloud.Accounts.Services;
using Yingge.Cloud.Protocol;
using Microsoft.AspNetCore.Identity;

namespace Yingge.Cloud.Accounts.Api;

internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth");
        group.MapPost("/register", Register).RequireRateLimiting(RateLimitSetup.Register);
        group.MapPost("/login", Login).RequireRateLimiting(RateLimitSetup.Login);
        group.MapPost("/logout", Logout).RequireRateLimiting(RateLimitSetup.Session);
        group.MapGet("/session", Session).RequireRateLimiting(RateLimitSetup.Session);
    }

    static async Task<IResult> Register(RegisterRequest? request, HttpContext http,
        InviteRepository invites, AccountRepository accounts, AuditRepository audit,
        PasswordService passwords, CancellationToken ct)
    {
        if (request is null)
            return Problems.Create(400, ProblemCodes.InvalidRequest, "请求内容为空。");

        var inviteCode = AccountRules.NormalizeInviteCode(request.InviteCode);
        if (!AccountRules.IsWellFormedInviteCode(inviteCode))
        {
            await audit.LogAsync("register.rejected", null, ClientIp(http),
                new { reason = "invite_malformed" }, ct);
            return Problems.Create(403, ProblemCodes.InviteInvalid, "邀请码无效或已使用。");
        }

        var username = AccountRules.NormalizeUsername(request.Username);
        if (AccountRules.ValidateUsername(username) is { } usernameError)
            return Problems.Create(400, ProblemCodes.InvalidRequest, usernameError);
        if (AccountRules.ValidatePassword(request.Password, username) is { } passwordError)
            return Problems.Create(400, ProblemCodes.InvalidRequest, passwordError);

        // Cheap existence check before the deliberately expensive password hash.
        var inviteHash = TokenCodec.Hash(inviteCode);
        if (await invites.FindUsableAsync(inviteHash, ct) is null)
        {
            await audit.LogAsync("register.rejected", null, ClientIp(http),
                new { reason = "invite_unusable" }, ct);
            return Problems.Create(403, ProblemCodes.InviteInvalid, "邀请码无效或已使用。");
        }

        var passwordHash = await Task.Run(() => passwords.Hash(request.Password!), ct);
        try
        {
            var accountId = await accounts.RegisterAsync(inviteHash, username, passwordHash,
                ClientIp(http), audit, ct);
            return Results.Json(new RegistrationResponse(accountId, username),
                statusCode: StatusCodes.Status201Created);
        }
        catch (InviteNotUsableException)
        {
            await audit.LogAsync("register.rejected", null, ClientIp(http),
                new { reason = "invite_unusable_race" }, ct);
            return Problems.Create(403, ProblemCodes.InviteInvalid, "邀请码无效或已使用。");
        }
        catch (UsernameTakenException)
        {
            await audit.LogAsync("register.rejected", null, ClientIp(http),
                new { reason = "username_taken" }, ct);
            return Problems.Create(409, ProblemCodes.UsernameTaken, "该账号名已被占用。");
        }
    }

    static async Task<IResult> Login(LoginRequest? request, HttpContext http,
        AccountRepository accounts, SessionRepository sessions, AuditRepository audit,
        PasswordService passwords, CloudOptions options, TimeProvider clock, CancellationToken ct)
    {
        if (request is null || string.IsNullOrEmpty(request.Password))
            return Problems.Create(401, ProblemCodes.InvalidCredentials, "账号名或密码不正确。");

        var username = AccountRules.NormalizeUsername(request.Username);
        if (AccountRules.ValidateUsername(username) is not null)
            return Problems.Create(401, ProblemCodes.InvalidCredentials, "账号名或密码不正确。");

        var account = await accounts.FindByUsernameAsync(username, ct);
        if (account is null)
        {
            // Same cost as a real verification so the response time does not
            // reveal whether the account exists.
            await Task.Run(() => passwords.Verify(passwords.DummyHash, request.Password!), ct);
            await audit.LogAsync("login.failed", null, ClientIp(http),
                new { reason = "unknown_account", username }, ct);
            return Problems.Create(401, ProblemCodes.InvalidCredentials, "账号名或密码不正确。");
        }

        if (!string.Equals(account.Status, "active", StringComparison.Ordinal))
        {
            await audit.LogAsync("login.rejected", account.Id, ClientIp(http),
                new { reason = "disabled" }, ct);
            return Problems.Create(403, ProblemCodes.AccountDisabled, "该账号已被停用，请联系维护者。");
        }

        var now = clock.GetUtcNow();
        if (account.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalSeconds));
            await audit.LogAsync("login.locked", account.Id, ClientIp(http),
                new { reason = "lockout_active" }, ct);
            return new ProblemResult(429, ProblemCodes.AccountLocked,
                "登录失败次数过多，请稍后再试。", retryAfterSeconds: retryAfter);
        }

        var verification = await Task.Run(() => passwords.Verify(account.PasswordHash,
            request.Password!), ct);
        if (verification == PasswordVerificationResult.Failed)
        {
            var (failures, locked) = await accounts.RecordFailedLoginAsync(account.Id,
                options.Security.MaxFailedLogins, TimeSpan.FromMinutes(options.Security.LockoutMinutes), ct);
            await audit.LogAsync(locked ? "login.locked" : "login.failed", account.Id, ClientIp(http),
                new { reason = "bad_password", failures }, ct);
            return Problems.Create(401, ProblemCodes.InvalidCredentials, "账号名或密码不正确。");
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var rehashed = await Task.Run(() => passwords.Hash(request.Password!), ct);
            await accounts.UpdatePasswordAsync(account.Id, rehashed, ct);
        }

        await accounts.ResetFailedLoginAsync(account.Id, ct);
        var token = TokenCodec.CreateSessionToken();
        var expiresAt = await sessions.CreateAsync(account.Id, TokenCodec.Hash(token),
            TimeSpan.FromHours(options.Security.SessionHours), ClientVersion(http), ClientIp(http), ct);
        await audit.LogAsync("login.succeeded", account.Id, ClientIp(http),
            new { session_expires_at = expiresAt }, ct);
        return Results.Json(new AuthResponse(token, expiresAt, account.Username, account.Id));
    }

    static async Task<IResult> Logout(HttpContext http, SessionAuthenticator authenticator,
        SessionRepository sessions, AuditRepository audit, CancellationToken ct)
    {
        var (session, problemCode) = await authenticator.AuthenticateAsync(http, ct);
        if (session is null) return Problems.Auth(problemCode);

        await sessions.RevokeByHashAsync(session.TokenHash, ct);
        await audit.LogAsync("session.revoked", session.AccountId, ClientIp(http),
            new { reason = "logout" }, ct);
        return Results.NoContent();
    }

    static async Task<IResult> Session(HttpContext http, SessionAuthenticator authenticator,
        ReportRepository reports, CloudOptions options, TimeProvider clock, CancellationToken ct)
    {
        var (session, problemCode) = await authenticator.AuthenticateAsync(http, ct);
        if (session is null) return Problems.Auth(problemCode);

        var used = await reports.CountSinceAsync(session.AccountId, clock.GetUtcNow().AddDays(-1), ct);
        return Results.Json(new SessionStatus(true, session.Username, session.ExpiresAt,
            used, options.Reports.DailyLimit));
    }

    internal static string? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    internal static string? ClientVersion(HttpContext http) =>
        http.Request.Headers.TryGetValue("X-Client-Version", out var value) &&
        value.ToString() is { Length: > 0 and <= 32 } version
            ? version
            : null;
}
