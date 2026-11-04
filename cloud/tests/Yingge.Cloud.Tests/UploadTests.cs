using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Tests;

public sealed class UploadTests(PostgresFixture fixture) : CloudTestBase(fixture)
{
    [Fact]
    public async Task Valid_report_is_stored_with_metadata_and_a_file_outside_the_web_root()
    {
        var account = await CreateAccountAsync();

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("生成封面失败：无法连接 TMDB。", "索引写入失败：磁盘空间不足。"), account.Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await TestSupport.BodyAsync<ErrorReportReceipt>(response);
        Assert.NotNull(receipt);
        Assert.Equal(2, receipt!.MessageCount);
        Assert.True(receipt.ByteSize > 0);
        Assert.True(receipt.ExpiresAt > receipt.ReceivedAt);

        Assert.Equal(1, await CountReportsAsync());
        Assert.Equal(1, await CountFilesAsync());

        var files = Directory.EnumerateFiles(Factory.StorageRoot, "*.json", SearchOption.AllDirectories)
            .ToArray();
        var content = await File.ReadAllTextAsync(files[0]);
        Assert.Contains("生成封面失败", content, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\", content, StringComparison.Ordinal);

        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            "select count(*)::int from audit_events where event = 'report.accepted'"));
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            "select count(*)::int from error_reports where message_count = 2 and deleted_at is null"));
    }

    [Fact]
    public async Task Upload_without_a_token_is_rejected_and_writes_nothing()
    {
        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("下载失败：连接被拒绝。"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await CountReportsAsync());
        Assert.Equal(0, await CountFilesAsync());
    }

    [Theory]
    [InlineData("读取失败：D:\\Videos\\movie.mkv 无法访问。")]
    [InlineData("读取失败：\\\\nas\\share\\movie.mkv 无法访问。")]
    [InlineData("读取失败：/home/user/movies/movie.mkv 无法访问。")]
    [InlineData("读取失败：movie.mkv 无法访问。")]
    [InlineData("请求失败：api_key=sk-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("请求失败：magnet:?xt=urn:btih:0123456789abcdef")]
    public async Task Report_that_still_contains_private_data_is_refused(string message)
    {
        var account = await CreateAccountAsync();

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("正常错误", message), account.Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await TestSupport.ProblemAsync(response);
        Assert.Equal(ProblemCodes.ReportContainsPrivateData, problem!.Code);
        Assert.Equal(1, problem.Index);
        Assert.Equal(0, await CountReportsAsync());
        Assert.Equal(0, await CountFilesAsync());
    }

    [Fact]
    public async Task Too_many_messages_are_refused()
    {
        var account = await CreateAccountAsync();
        var messages = Enumerable.Range(0, 101).Select(index => "失败 " + index).ToArray();

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report(messages), account.Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ProblemCodes.ReportInvalid, (await TestSupport.ProblemAsync(response))!.Code);
        Assert.Equal(0, await CountReportsAsync());
    }

    [Fact]
    public async Task Oversized_body_is_refused_before_parsing()
    {
        var account = await CreateAccountAsync();
        var messages = Enumerable.Range(0, 60).Select(index => new string('x', 2500) + index).ToArray();

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report(messages), account.Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ProblemCodes.PayloadTooLarge, (await TestSupport.ProblemAsync(response))!.Code);
        Assert.Equal(0, await CountReportsAsync());
    }

    [Fact]
    public async Task Unknown_fields_are_refused()
    {
        var account = await CreateAccountAsync();
        var json = """
            {"clientVersion":"1.6.6","os":"windows","arch":"x64","messages":["失败"],"libraryPath":"D:\\Videos"}
            """;

        var response = await TestSupport.SendRawAsync(Client, HttpMethod.Post, "/api/v1/reports",
            json, account.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemCodes.InvalidJson, (await TestSupport.ProblemAsync(response))!.Code);
        Assert.Equal(0, await CountReportsAsync());
    }

    [Fact]
    public async Task Duplicate_submission_is_rejected_and_keeps_one_row()
    {
        var account = await CreateAccountAsync();
        var report = Report("刮削失败：超时。", "重试失败：超时。");

        var first = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            report, account.Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            report, account.Token);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ProblemCodes.DuplicateReport, (await TestSupport.ProblemAsync(second))!.Code);
        Assert.Equal(1, await CountReportsAsync());
        Assert.Equal(1, await CountFilesAsync());
    }

    [Fact]
    public async Task Empty_message_list_is_refused()
    {
        var account = await CreateAccountAsync();

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            new ErrorReportRequest("1.6.6", "windows", "x64", DateTimeOffset.UtcNow, []), account.Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await CountReportsAsync());
    }

    [Fact]
    public async Task Bad_os_or_arch_is_refused()
    {
        var account = await CreateAccountAsync();

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            new ErrorReportRequest("1.6.6", "android", "mips", DateTimeOffset.UtcNow, ["失败"]),
            account.Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Report_from_the_future_is_refused()
    {
        var account = await CreateAccountAsync();

        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            new ErrorReportRequest("1.6.6", "windows", "x64", DateTimeOffset.UtcNow.AddDays(2), ["失败"]),
            account.Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}

public sealed class UploadQuotaTests(PostgresFixture fixture) : CloudTestBase(fixture)
{
    protected override IReadOnlyDictionary<string, string?>? Settings { get; } =
        new Dictionary<string, string?> { ["Reports:DailyLimit"] = "1" };

    [Fact]
    public async Task Second_report_within_the_daily_quota_is_refused()
    {
        var account = await CreateAccountAsync();

        var first = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("失败一"), account.Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("失败二"), account.Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(ProblemCodes.UploadQuotaExceeded, (await TestSupport.ProblemAsync(second))!.Code);
        Assert.Equal(1, await CountReportsAsync());
    }
}

public sealed class RetentionTests(PostgresFixture fixture) : CloudTestBase(fixture)
{
    [Fact]
    public async Task Purge_removes_expired_reports_and_their_files()
    {
        var account = await CreateAccountAsync();
        var response = await TestSupport.SendAsync(Client, HttpMethod.Post, "/api/v1/reports",
            Report("失败：保留期测试"), account.Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await Fixture.ExecuteOwnerAsync(
            "update error_reports set retention_expires_at = now() - interval '1 day';");

        var retention = Factory.Services.GetRequiredService<
            Yingge.Cloud.Accounts.Services.RetentionService>();
        var purged = await retention.PurgeExpiredAsync(50, CancellationToken.None);

        Assert.Equal(1, purged);
        Assert.Equal(0, await CountFilesAsync());
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            "select count(*)::int from error_reports where deleted_at is not null and deleted_reason = 'retention'"));
        Assert.Equal(1, await Fixture.ScalarAsync<int>(
            "select count(*)::int from audit_events where event = 'report.retention_purged'"));
    }
}
