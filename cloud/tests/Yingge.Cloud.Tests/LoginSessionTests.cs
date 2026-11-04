using System.Net;
using Yingge.Cloud.Accounts.Security;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Tests;

public sealed class LoginSessionTests(PostgresFixture fixture) : CloudTestBase(fixture)
{
    [Fact]
    public async Task Login_returns_a_token_and_records_the_session()
    {
        var account = await CreateAccountAsync();

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/login",
            new LoginRequest(account.Username.ToUpperInvariant(), account.Password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = await TestSupport.BodyAsync<AuthResponse>(response);
        Assert.NotNull(auth);
        Assert.Equal(43, auth!.Token.Length);
        Assert.True(auth.ExpiresAt > DateTimeOffset.UtcNow);

        var hash = TestSupport.Hex(TokenCodec.Hash(auth.Token));
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            $"select count(*)::int from sessions where token_hash = decode('{hash}','hex') and revoked_at is null"));

        // Every stored value is a 32-byte SHA-256 digest, never the 43-char token.
        Assert.Equal(0, await Fixture.ScalarAsync<int>(
            "select count(*)::int from sessions where octet_length(token_hash) <> 32"));
    }

    [Fact]
    public async Task Wrong_password_is_rejected_without_creating_a_session()
    {
        var account = await CreateAccountAsync();
        var sessionsBefore = await Fixture.ScalarAsync<int>("select count(*)::int from sessions");

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/login",
            new LoginRequest(account.Username, "WrongPassword-9999"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ProblemCodes.InvalidCredentials, (await TestSupport.ProblemAsync(response))!.Code);
        Assert.Equal(sessionsBefore, await Fixture.ScalarAsync<int>(
            "select count(*)::int from sessions"));
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            $"select failed_login_count from accounts where username = '{account.Username}'"));
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            "select count(*)::int from audit_events where event = 'login.failed'"));
    }

    [Fact]
    public async Task Unknown_account_is_indistinguishable_from_a_wrong_password()
    {
        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/login",
            new LoginRequest("nobody" + Guid.NewGuid().ToString("N")[..6], "SomePassword-1234"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ProblemCodes.InvalidCredentials, (await TestSupport.ProblemAsync(response))!.Code);
    }

    [Fact]
    public async Task Five_failures_lock_the_account_even_with_the_right_password()
    {
        var account = await CreateAccountAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failure = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/login",
                new LoginRequest(account.Username, "WrongPassword-" + attempt));
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        var locked = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/login",
            new LoginRequest(account.Username, account.Password));

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal(ProblemCodes.AccountLocked, (await TestSupport.ProblemAsync(locked))!.Code);
        Assert.True(locked.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task Disabled_account_cannot_log_in()
    {
        var account = await CreateAccountAsync();
        await Fixture.ExecuteOwnerAsync(
            $"update accounts set status = 'disabled', disabled_at = now() where username = '{account.Username}';");

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/login",
            new LoginRequest(account.Username, account.Password));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ProblemCodes.AccountDisabled, (await TestSupport.ProblemAsync(response))!.Code);
    }

    [Fact]
    public async Task Logout_revokes_the_session_immediately()
    {
        var account = await CreateAccountAsync();

        var logout = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/logout",
            token: account.Token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var status = await TestSupport.SendAsync(Client, HttpMethod.Get, "/api/v1/auth/session",
            token: account.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);

        var upload = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("上传失败：测试"), account.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        Assert.Equal(0, await CountReportsAsync());
    }

    [Fact]
    public async Task Expired_session_is_rejected()
    {
        var account = await CreateAccountAsync();
        var hash = TestSupport.Hex(TokenCodec.Hash(account.Token));
        // Move both timestamps: the schema keeps expires_at after created_at.
        await Fixture.ExecuteOwnerAsync($"""
            update sessions set created_at = now() - interval '10 hours',
                expires_at = now() - interval '1 minute'
            where token_hash = decode('{hash}','hex');
            """);

        var status = await TestSupport.SendAsync(Client, HttpMethod.Get, "/api/v1/auth/session",
            token: account.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task Disabling_an_account_invalidates_its_existing_session()
    {
        var account = await CreateAccountAsync();
        await Fixture.ExecuteOwnerAsync(
            $"update accounts set status = 'disabled', disabled_at = now() where username = '{account.Username}';");

        var upload = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("上传失败：禁用后"), account.Token);

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(ProblemCodes.AccountDisabled, (await TestSupport.ProblemAsync(upload))!.Code);
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            "select count(*)::int from sessions where revoked_at is not null"));
        Assert.Equal(0, await CountReportsAsync());
    }

    [Fact]
    public async Task Session_status_reports_the_daily_quota()
    {
        var account = await CreateAccountAsync();

        var response = await TestSupport.SendAsync(Client, HttpMethod.Get, "/api/v1/auth/session",
            token: account.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await TestSupport.BodyAsync<SessionStatus>(response);
        Assert.NotNull(status);
        Assert.True(status!.Authenticated);
        Assert.Equal(account.Username, status.Username);
        Assert.Equal(100, status.DailyUploadLimit);
        Assert.Equal(0, status.UploadsLast24Hours);
    }

    [Fact]
    public async Task Login_rate_limit_returns_429_with_a_retry_after_header()
    {
        var account = await CreateAccountAsync();
        var limited = new CloudAppFactory(Fixture, new Dictionary<string, string?>
        {
            ["Security:LoginPerMinute"] = "2"
        });
        await using (limited)
        {
            using var limitedClient = limited.CreateClient();
            HttpStatusCode last = HttpStatusCode.OK;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var response = await TestSupport.SendAsync(limitedClient, HttpMethod.Post,
                    "/api/v1/auth/login", new LoginRequest(account.Username, "WrongPassword-123"));
                last = response.StatusCode;
                if (last == HttpStatusCode.TooManyRequests)
                {
                    Assert.Equal(ProblemCodes.RateLimited, (await TestSupport.ProblemAsync(response))!.Code);
                    Assert.NotNull(response.Headers.RetryAfter);
                    break;
                }
            }
            Assert.Equal(HttpStatusCode.TooManyRequests, last);
        }
    }
}
