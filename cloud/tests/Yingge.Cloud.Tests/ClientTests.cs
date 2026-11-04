using System.Net;
using Yingge.Cloud.Client;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Tests;

public sealed class ClientTests(PostgresFixture fixture) : CloudTestBase(fixture)
{
    [Fact]
    public async Task Client_registers_logs_in_and_uploads_an_redacted_report()
    {
        var invite = await TestSupport.CreateInviteAsync(Fixture);
        var username = "client" + Guid.NewGuid().ToString("N")[..8];
        var store = new InMemorySessionStore();
        using var client = CreateClient(store);

        var registration = await client.RegisterAsync(invite.Code, username, TestSupport.Password);
        Assert.Equal(username, registration.Username);

        var auth = await client.LoginAsync(username, TestSupport.Password);
        Assert.True(client.HasSession);
        Assert.Equal(auth.Token, await store.ReadAsync());

        var draft = ErrorReportDraft.Create("1.6.6",
            ["刮削失败：D:\\Videos\\movie.mkv 无法读取", "重试失败：无法连接 TMDB。"]);
        Assert.Equal(1, draft.RedactionCount);
        Assert.DoesNotContain("D:\\", draft.Messages[0], StringComparison.Ordinal);

        var receipt = await client.UploadReportAsync(draft);
        Assert.Equal(2, receipt.MessageCount);
        Assert.Equal(1, await CountReportsAsync());

        var duplicate = await Assert.ThrowsAsync<CloudAccountException>(
            () => client.UploadReportAsync(draft));
        Assert.Equal(ProblemCodes.DuplicateReport, duplicate.Code);
        Assert.Equal(409, duplicate.StatusCode);
    }

    [Fact]
    public void Client_refuses_plain_http_to_a_remote_host()
    {
        var exception = Assert.Throws<ArgumentException>(() => new CloudAccountClient(
            new CloudClientOptions { BaseAddress = new Uri("http://reports.yingevideo.top") }));
        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_upload_keeps_the_report_in_the_outbox()
    {
        var account = await CreateAccountAsync();
        var outbox = new LocalUploadOutbox(Path.Combine(Path.GetTempPath(),
            "yingge-cloud-outbox", Guid.NewGuid().ToString("N")[..8]));
        var draft = ErrorReportDraft.Create("1.6.6", ["上传失败：服务器不可达。"]);
        Assert.NotNull(await outbox.SaveAsync(draft));

        var offlineStore = new InMemorySessionStore();
        await offlineStore.WriteAsync(account.Token);
        using var offline = new CloudAccountClient(new CloudClientOptions
        {
            BaseAddress = new Uri($"http://127.0.0.1:{PostgresFixture.FreePort()}"),
            AllowInsecureLoopback = true,
            Timeout = TimeSpan.FromSeconds(2)
        }, offlineStore);

        var result = await outbox.FlushAsync(offline);

        Assert.Equal(0, result.Uploaded);
        Assert.Equal(1, result.Remaining);
        Assert.NotNull(result.LastError);
        Assert.Single(outbox.Pending());
        Assert.Equal(0, await CountReportsAsync());

        // The same pending file uploads once the server is reachable again.
        using var online = CreateClient(new InMemorySessionStore(account.Token));
        var retried = await outbox.FlushAsync(online);

        Assert.Equal(1, retried.Uploaded);
        Assert.Empty(outbox.Pending());
        Assert.Equal(1, await CountReportsAsync());
    }

    [Fact]
    public async Task Invalid_session_is_cleared_locally()
    {
        var store = new InMemorySessionStore(new string('b', 43));
        using var client = CreateClient(store);

        var status = await client.GetSessionAsync();

        Assert.Null(status);
        Assert.Null(await store.ReadAsync());
    }

    [Fact]
    public async Task Logout_revokes_server_session_and_clears_local_token()
    {
        var account = await CreateAccountAsync();
        var store = new InMemorySessionStore(account.Token);
        using var client = CreateClient(store);

        var result = await client.LogoutAsync();

        Assert.True(result.RevokedOnServer);
        Assert.Null(await store.ReadAsync());
        Assert.False(client.HasSession);
        Assert.Null(await client.GetSessionAsync());
        var serverStatus = await TestSupport.SendAsync(Client, HttpMethod.Get,
            "/api/v1/auth/session", token: account.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, serverStatus.StatusCode);
    }

    [Fact]
    public async Task Outbox_refuses_to_grow_without_limit()
    {
        var outbox = new LocalUploadOutbox(Path.Combine(Path.GetTempPath(),
            "yingge-cloud-outbox", Guid.NewGuid().ToString("N")[..8]));
        for (var index = 0; index < 50; index++)
            Assert.NotNull(await outbox.SaveAsync(ErrorReportDraft.Create("1.6.6", ["失败 " + index])));

        Assert.Null(await outbox.SaveAsync(ErrorReportDraft.Create("1.6.6", ["失败 超限"])));
        Assert.Equal(50, outbox.Pending().Count);
    }

    CloudAccountClient CreateClient(ISessionStore store) => new(new CloudClientOptions
    {
        BaseAddress = Client.BaseAddress!,
        AllowInsecureLoopback = true,
        ClientVersion = "1.6.6",
        Timeout = TimeSpan.FromSeconds(10)
    }, store, Client);
}

public sealed class RedactionTests
{
    [Theory]
    [InlineData(@"打开 D:\Videos\movie.mkv 失败", "D:\\")]
    [InlineData(@"打开 \\nas\share\movie.mkv 失败", "\\\\nas")]
    [InlineData("打开 /home/user/movies/movie.mkv 失败", "/home/user")]
    [InlineData("打开 file:///C:/Videos/movie.mkv 失败", "file://")]
    [InlineData("下载 magnet:?xt=urn:btih:abc 失败", "magnet:")]
    [InlineData("请求失败 api_key=sk-abcdefghijklmnopqrstuvwxyz", "sk-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("联系 someone@example.com 失败", "someone@example.com")]
    public void Private_data_is_masked(string message, string forbidden)
    {
        var redacted = ErrorReportRedactor.Redact(message);

        Assert.True(redacted.Replacements > 0);
        Assert.DoesNotContain(forbidden, redacted.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<", redacted.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_message_is_left_alone()
    {
        var redacted = ErrorReportRedactor.Redact("刮削失败：连接超时。");

        Assert.Equal(0, redacted.Replacements);
        Assert.Equal("刮削失败：连接超时。", redacted.Text);
    }

    [Fact]
    public void Server_scan_blocks_paths_but_tolerates_email()
    {
        Assert.NotNull(PrivateDataScanner.FindBlocking(@"D:\Videos\a.mkv"));
        Assert.Null(PrivateDataScanner.FindBlocking("联系 someone@example.com 失败"));
        Assert.Equal(1, PrivateDataScanner.FirstBlockingMessage(["正常", "D:\\Videos\\a.mkv"]));
        Assert.Null(PrivateDataScanner.FirstBlockingMessage(["正常", "失败"]));
    }

    [Fact]
    public void Draft_reports_counts_and_preview()
    {
        var draft = ErrorReportDraft.Create("1.6.6",
            ["失败：D:\\Videos\\a.mkv", "失败：无法连接", "失败：magnet:?xt=urn:btih:1"]);

        Assert.Equal(3, draft.MessageCount);
        Assert.Equal(2, draft.RedactionCount);
        var preview = draft.PreviewText();
        Assert.Contains("错误条目：3 条", preview, StringComparison.Ordinal);
        Assert.Contains("已在本机脱敏：2 处", preview, StringComparison.Ordinal);
        Assert.Null(draft.ValidateLocally(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Draft_validation_rejects_too_many_messages()
    {
        var messages = Enumerable.Range(0, 101).Select(index => "失败 " + index).ToArray();
        var draft = ErrorReportDraft.Create("1.6.6", messages);

        Assert.NotNull(draft.ValidateLocally(DateTimeOffset.UtcNow));
    }
}
