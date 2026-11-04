using System.Net;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Tests;

public sealed class RegistrationTests(PostgresFixture fixture) : CloudTestBase(fixture)
{
    [Fact]
    public async Task Valid_invite_creates_account_and_consumes_the_invite()
    {
        var invite = await TestSupport.CreateInviteAsync(Fixture);
        var username = "alice" + Guid.NewGuid().ToString("N")[..6];

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(invite.Code, username.ToUpperInvariant(), TestSupport.Password));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await TestSupport.BodyAsync<RegistrationResponse>(response);
        Assert.NotNull(created);
        Assert.Equal(username, created!.Username);

        Assert.Equal(1, await Fixture.ScalarAsync<int>("select count(*)::int from accounts"));
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            "select count(*)::int from invites where used_at is not null and used_by_account is not null"));
    }

    [Fact]
    public async Task Registration_without_a_valid_invite_is_refused()
    {
        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest("", "bob" + Guid.NewGuid().ToString("N")[..6], TestSupport.Password));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ProblemCodes.InviteInvalid, (await TestSupport.ProblemAsync(response))!.Code);
        Assert.Equal(0, await Fixture.ScalarAsync<int>("select count(*)::int from accounts"));
    }

    [Fact]
    public async Task Expired_invite_is_refused()
    {
        var invite = await TestSupport.CreateInviteAsync(Fixture);
        await Fixture.ExecuteOwnerAsync($"""
            update invites set created_at = now() - interval '2 hours',
                expires_at = now() - interval '1 hour' where id = '{invite.Id}';
            """);

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(invite.Code, "carol" + Guid.NewGuid().ToString("N")[..6],
                TestSupport.Password));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ProblemCodes.InviteInvalid, (await TestSupport.ProblemAsync(response))!.Code);
    }

    [Fact]
    public async Task Revoked_invite_is_refused()
    {
        var invite = await TestSupport.CreateInviteAsync(Fixture);
        await Fixture.ExecuteOwnerAsync($"update invites set revoked_at = now() where id = '{invite.Id}';");

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(invite.Code, "dave" + Guid.NewGuid().ToString("N")[..6],
                TestSupport.Password));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Invite_works_only_once()
    {
        var invite = await TestSupport.CreateInviteAsync(Fixture);
        var first = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(invite.Code, "erin" + Guid.NewGuid().ToString("N")[..6],
                TestSupport.Password));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(invite.Code, "frank" + Guid.NewGuid().ToString("N")[..6],
                TestSupport.Password));

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal(1, await Fixture.ScalarAsync<int>("select count(*)::int from accounts"));
    }

    [Fact]
    public async Task Duplicate_username_is_rejected_without_burning_the_second_invite()
    {
        var account = await CreateAccountAsync();
        var spare = await TestSupport.CreateInviteAsync(Fixture);

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(spare.Code, account.Username, TestSupport.Password));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ProblemCodes.UsernameTaken, (await TestSupport.ProblemAsync(response))!.Code);
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            $"select count(*)::int from invites where id = '{spare.Id}' and used_at is null"));
    }

    [Fact]
    public async Task Weak_password_is_rejected_before_anything_is_written()
    {
        var invite = await TestSupport.CreateInviteAsync(Fixture);
        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(invite.Code, "grace" + Guid.NewGuid().ToString("N")[..6], "short"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Fixture.ScalarAsync<int>("select count(*)::int from accounts"));
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            "select count(*)::int from invites where used_at is null"));
    }

    [Fact]
    public async Task Unknown_fields_in_the_request_are_rejected()
    {
        var invite = await TestSupport.CreateInviteAsync(Fixture);
        var username = "henry" + Guid.NewGuid().ToString("N")[..6];
        var json = $$"""
            {"inviteCode":"{{invite.Code}}","username":"{{username}}","password":"{{TestSupport.Password}}","role":"admin"}
            """;

        var response = await TestSupport.SendRawAsync(Client, HttpMethod.Post, "/api/v1/auth/register", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemCodes.InvalidJson, (await TestSupport.ProblemAsync(response))!.Code);
        Assert.Equal(0, await Fixture.ScalarAsync<int>("select count(*)::int from accounts"));
    }

    [Fact]
    public async Task Invite_code_is_accepted_with_any_dash_or_case_formatting()
    {
        var invite = await TestSupport.CreateInviteAsync(Fixture);
        var mangled = "  " + invite.Code.Replace("-", "").ToLowerInvariant() + " ";

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(mangled, "ivan" + Guid.NewGuid().ToString("N")[..6],
                TestSupport.Password));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
