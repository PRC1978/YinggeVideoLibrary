using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Yingge.Cloud.Accounts;
using Yingge.Cloud.Accounts.Data;

namespace Yingge.Cloud.Tests;

public sealed class CloudAppFactory : WebApplicationFactory<Program>
{
    readonly Dictionary<string, string?> settings;

    public CloudAppFactory(PostgresFixture fixture, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        StorageRoot = Path.Combine(Path.GetTempPath(), "yingge-cloud-storage",
            Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(StorageRoot);

        settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Database:AppConnectionString"] = fixture.AppConnectionString,
            ["Storage:RootPath"] = StorageRoot,
            ["Security:RequireHttps"] = "false",
            // Fast hashing keeps the suite usable; production pins 210000.
            ["Security:PasswordIterations"] = "1000",
            ["Security:LoginPerMinute"] = "10000",
            ["Security:RegisterPerHour"] = "10000",
            ["Security:GlobalPerMinute"] = "100000",
            ["Reports:DailyLimit"] = "100",
            ["Reports:BurstLimit"] = "100",
            ["Reports:RetentionDays"] = "30",
            ["Reports:SweepMinutes"] = "600"
        };

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            settings[key] = value;
    }

    public string StorageRoot { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(settings));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try
        {
            if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true);
        }
        catch (IOException)
        {
            // Test temp directory only.
        }
    }
}

internal sealed record InviteTicket(string Code, Guid Id);

internal static class TestSupport
{
    public const string Password = "StrongPass-1234";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method,
        string path, object? body = null, string? token = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method,
        string path, string json, string? token = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    public static async Task<T?> BodyAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, Json);
    }

    public static async Task<Yingge.Cloud.Protocol.ApiProblem?> ProblemAsync(HttpResponseMessage response) =>
        await BodyAsync<Yingge.Cloud.Protocol.ApiProblem>(response);

    public static async Task<Yingge.Cloud.Protocol.AuthResponse> LoginAsync(HttpClient client,
        string? username = null, string? password = null)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/login",
            new { username, password });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var auth = await BodyAsync<Yingge.Cloud.Protocol.AuthResponse>(response);
        Assert.NotNull(auth);
        return auth!;
    }

    public static async Task<InviteTicket> CreateInviteAsync(PostgresFixture fixture,
        TimeSpan? lifetime = null, string? note = null)
    {
        var code = Yingge.Cloud.Accounts.Security.TokenCodec.CreateInviteCode();
        var normalized = Yingge.Cloud.Protocol.AccountRules.NormalizeInviteCode(code);
        var repository = new InviteRepository(fixture.OwnerDataSource);
        var id = await repository.CreateAsync(
            Yingge.Cloud.Accounts.Security.TokenCodec.Hash(normalized), note,
            DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)), CancellationToken.None);
        return new InviteTicket(code, id);
    }

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}

[Collection(CloudCollection.Name)]
public abstract class CloudTestBase : IAsyncLifetime
{
    protected CloudTestBase(PostgresFixture fixture) => Fixture = fixture;

    protected PostgresFixture Fixture { get; }

    protected CloudAppFactory Factory { get; private set; } = null!;

    protected HttpClient Client { get; private set; } = null!;

    protected virtual IReadOnlyDictionary<string, string?>? Settings => null;

    public async Task InitializeAsync()
    {
        await Fixture.ResetAsync();
        Factory = new CloudAppFactory(Fixture, Settings);
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }

    protected async Task<TestAccount> CreateAccountAsync()
    {
        var invite = await TestSupport.CreateInviteAsync(Fixture);
        var username = "user" + Guid.NewGuid().ToString("N")[..10];
        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/auth/register",
            new Yingge.Cloud.Protocol.RegisterRequest(invite.Code, username, TestSupport.Password));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var auth = await TestSupport.LoginAsync(Client, username, TestSupport.Password);
        return new TestAccount(username, TestSupport.Password, auth.Token, auth.AccountId);
    }

    protected static Yingge.Cloud.Protocol.ErrorReportRequest Report(params string[] messages) =>
        new("1.6.6", "windows", "x64", DateTimeOffset.UtcNow, messages);

    protected Task<int> CountReportsAsync() =>
        Fixture.ScalarAsync<int>("select count(*)::int from error_reports");

    protected Task<int> CountFilesAsync() =>
        Task.FromResult(Directory.Exists(Factory.StorageRoot)
            ? Directory.EnumerateFiles(Factory.StorageRoot, "*.json", SearchOption.AllDirectories).Count()
            : 0);
}

public sealed record TestAccount(string Username, string Password, string Token, Guid AccountId);
