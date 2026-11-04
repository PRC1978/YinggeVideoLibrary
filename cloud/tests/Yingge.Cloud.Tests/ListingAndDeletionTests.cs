using System.Net;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Tests;

public sealed class ListingAndDeletionTests(PostgresFixture fixture) : CloudTestBase(fixture)
{
    [Fact]
    public async Task Listing_returns_only_the_callers_reports()
    {
        var alice = await CreateAccountAsync();
        var bob = await CreateAccountAsync();

        var aliceUpload = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("失败：Alice 的日志"), alice.Token);
        Assert.Equal(HttpStatusCode.Created, aliceUpload.StatusCode);
        var bobUpload = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("失败：Bob 的日志"), bob.Token);
        Assert.Equal(HttpStatusCode.Created, bobUpload.StatusCode);

        var response = await TestSupport.SendAsync(Client, HttpMethod.Get, "/api/v1/reports",
            token: alice.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await TestSupport.BodyAsync<ErrorReportSummary[]>(response);
        Assert.NotNull(items);
        Assert.Single(items!);
        Assert.Equal(1, items![0].MessageCount);
    }

    [Fact]
    public async Task Deleting_own_report_removes_the_file_and_hides_it_from_the_list()
    {
        var account = await CreateAccountAsync();
        var upload = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("失败：准备删除"), account.Token);
        var receipt = await TestSupport.BodyAsync<ErrorReportReceipt>(upload);
        Assert.NotNull(receipt);

        var delete = await TestSupport.SendAsync(Client, HttpMethod.Delete,
            "/api/v1/reports/" + receipt!.Id, token: account.Token);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Equal(0, await CountFilesAsync());
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            "select count(*)::int from error_reports where deleted_at is not null and deleted_reason = 'user_request'"));

        var list = await TestSupport.SendAsync(Client, HttpMethod.Get, "/api/v1/reports",
            token: account.Token);
        var items = await TestSupport.BodyAsync<ErrorReportSummary[]>(list);
        Assert.Empty(items!);

        var again = await TestSupport.SendAsync(Client, HttpMethod.Delete,
            "/api/v1/reports/" + receipt.Id, token: account.Token);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Deleting_someone_elses_report_is_a_404_and_keeps_the_file()
    {
        var owner = await CreateAccountAsync();
        var stranger = await CreateAccountAsync();
        var upload = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("失败：归属测试"), owner.Token);
        var receipt = await TestSupport.BodyAsync<ErrorReportReceipt>(upload);

        var delete = await TestSupport.SendAsync(Client, HttpMethod.Delete,
            "/api/v1/reports/" + receipt!.Id, token: stranger.Token);

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(1, await CountFilesAsync());
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            "select count(*)::int from error_reports where deleted_at is null"));
    }

    [Fact]
    public async Task Deleted_report_can_be_uploaded_again()
    {
        var account = await CreateAccountAsync();
        var report = Report("失败：可以重新上传");
        var upload = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            report, account.Token);
        var receipt = await TestSupport.BodyAsync<ErrorReportReceipt>(upload);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        await TestSupport.SendAsync(Client, HttpMethod.Delete, "/api/v1/reports/" + receipt!.Id,
            token: account.Token);

        var again = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            report, account.Token);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }
}

public sealed class HttpsEnforcementTests(PostgresFixture fixture) : CloudTestBase(fixture)
{
    protected override IReadOnlyDictionary<string, string?>? Settings { get; } =
        new Dictionary<string, string?> { ["Security:RequireHttps"] = "true" };

    [Fact]
    public async Task Plain_http_requests_are_refused_with_426()
    {
        var response = await TestSupport.SendAsync(Client, HttpMethod.Get, "/api/v1/health");

        Assert.Equal((HttpStatusCode)426, response.StatusCode);
        Assert.Equal(ProblemCodes.HttpsRequired, (await TestSupport.ProblemAsync(response))!.Code);
    }
}
