using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Yingge.Cloud.Accounts.Data;
using Yingge.Cloud.Accounts.Security;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Tests;

public sealed class AdminTests(PostgresFixture fixture) : CloudTestBase(fixture)
{
    const string AccessKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const string InitialPassword = "InitialOnly-1234";
    const string NewPassword = "NewStrongAdminPassword-4567";

    protected override IReadOnlyDictionary<string, string?> Settings =>
        new Dictionary<string, string?> {
            ["Admin:AccessKeyHash"] = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(AccessKey)))
        };

    async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path,
        string? token = null, string key = AccessKey, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1/admin/" + path);
        request.Headers.Add("X-Admin-Key", key);
        if (token is not null) request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    [Fact]
    public async Task Web_pages_are_served_without_exposing_admin_data()
    {
        var register = await Client.GetAsync("/");
        var admin = await Client.GetAsync("/admin/");
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Contains("邀请注册", await register.Content.ReadAsStringAsync());
        Assert.Contains("管理入口", await admin.Content.ReadAsStringAsync());
        var login = await Client.GetAsync("/login/");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("login-form", await login.Content.ReadAsStringAsync());
        var loginScript = await Client.GetAsync("/login.js");
        Assert.Equal(HttpStatusCode.OK, loginScript.StatusCode);
        Assert.Contains("我的账号", await loginScript.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAdminAsync(HttpMethod.Get, "overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAdminAsync(HttpMethod.Get, "resources")).StatusCode);
    }

    [Fact]
    public void Resource_storage_scan_is_bounded_and_reports_partial_measurements()
    {
        File.WriteAllBytes(Path.Combine(Factory.StorageRoot, "one.bin"), new byte[8]);
        File.WriteAllBytes(Path.Combine(Factory.StorageRoot, "two.bin"), new byte[8]);
        var limited = Yingge.Cloud.Accounts.Api.AdminStorageUsage.Measure(Factory.StorageRoot,
            CancellationToken.None, maxEntries: 1);
        Assert.False(limited.Complete);
        Assert.Equal(8L, limited.Bytes);
        var full = Yingge.Cloud.Accounts.Api.AdminStorageUsage.Measure(Factory.StorageRoot, CancellationToken.None);
        Assert.True(full.Complete);
        Assert.Equal(16L, full.Bytes);
    }

    [Fact]
    public void Resource_storage_scan_obeys_cancellation()
    {
        File.WriteAllBytes(Path.Combine(Factory.StorageRoot, "one.bin"), new byte[8]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Yingge.Cloud.Accounts.Api.AdminStorageUsage.Measure(
            Factory.StorageRoot, cancellation.Token));
    }

    [Fact]
    public async Task Ordinary_account_and_wrong_key_cannot_access_admin_data()
    {
        var invite = await TestSupport.CreateInviteAsync(Fixture);
        var username = "user" + Guid.NewGuid().ToString("N")[..6];
        var registered = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(invite.Code, username, TestSupport.Password));
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var user = await TestSupport.LoginAsync(Client, username, TestSupport.Password);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAdminAsync(HttpMethod.Get, "overview", user.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAdminAsync(HttpMethod.Get, "resources", user.Token)).StatusCode);

        var accounts = new AccountRepository(Fixture.OwnerDataSource);
        Assert.True(await accounts.CreateAdminAsync("yun", new PasswordService(1000).Hash(InitialPassword),
            CancellationToken.None));
        var admin = await TestSupport.LoginAsync(Client, "yun", InitialPassword);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAdminAsync(HttpMethod.Get, "overview", admin.Token,
                new string('0', 64))).StatusCode);
    }

    [Fact]
    public async Task Initial_admin_must_change_password_then_can_manage_invites()
    {
        var accounts = new AccountRepository(Fixture.OwnerDataSource);
        Assert.True(await accounts.CreateAdminAsync("yun", new PasswordService(1000).Hash(InitialPassword),
            CancellationToken.None));
        var initial = await TestSupport.LoginAsync(Client, "yun", InitialPassword);
        var overview = await SendAdminAsync(HttpMethod.Get, "overview", initial.Token);
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Contains("mustChangePassword", await overview.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAdminAsync(HttpMethod.Get, "accounts", initial.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAdminAsync(HttpMethod.Get, "resources", initial.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAdminAsync(HttpMethod.Post, "change-password", initial.Token, body: new {
                currentPassword = InitialPassword, newPassword = "short"
            })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAdminAsync(HttpMethod.Post, "change-password", initial.Token, body: new {
                currentPassword = InitialPassword, newPassword = NewPassword
            })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAdminAsync(HttpMethod.Get, "overview", initial.Token)).StatusCode);

        var renewed = await TestSupport.LoginAsync(Client, "yun", NewPassword);
        var resources = await SendAdminAsync(HttpMethod.Get, "resources", renewed.Token);
        Assert.Equal(HttpStatusCode.OK, resources.StatusCode);
        Assert.Equal("no-store", resources.Headers.CacheControl?.ToString());
        var resourceJson = System.Text.Json.JsonDocument.Parse(await resources.Content.ReadAsStringAsync());
        Assert.True(resourceJson.RootElement.GetProperty("databaseBytes").GetInt64() > 0);
        Assert.True(resourceJson.RootElement.GetProperty("processWorkingSetBytes").GetInt64() > 0);
        Assert.True(resourceJson.RootElement.GetProperty("storageTotalBytes").GetInt64() > 0);
        Assert.DoesNotContain(Factory.StorageRoot, resourceJson.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAdminAsync(HttpMethod.Get, "accounts", renewed.Token)).StatusCode);
        var created = await SendAdminAsync(HttpMethod.Post, "invites", renewed.Token,
            body: new { note = "test", hours = 24 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(1, await Fixture.ScalarAsync<int>("select count(*)::int from invites"));
        Assert.DoesNotContain("password", await (await SendAdminAsync(HttpMethod.Get,
            "accounts", renewed.Token)).Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
