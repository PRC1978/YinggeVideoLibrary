using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Yingge.Cloud.Accounts.Data;
using Yingge.Cloud.Accounts.Security;
using Yingge.Cloud.Accounts.Services;
using Yingge.Cloud.Accounts.Storage;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Accounts.Api;

internal static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/reports");
        group.MapPost("", Upload).RequireRateLimiting(RateLimitSetup.Upload);
        group.MapGet("", List).RequireRateLimiting(RateLimitSetup.Upload);
        group.MapDelete("/{id:guid}", Delete).RequireRateLimiting(RateLimitSetup.Upload);
    }

    static async Task<IResult> Upload(HttpContext http, SessionAuthenticator authenticator,
        ReportService reports, AuditRepository audit, TimeProvider clock, CancellationToken ct)
    {
        var (session, problemCode) = await authenticator.AuthenticateAsync(http, ct);
        if (session is null) return Problems.Auth(problemCode);

        var body = await RequestReader.ReadBodyAsync(http.Request, ReportLimits.MaxBodyBytes, ct);
        if (body is null)
        {
            await audit.LogAsync("report.rejected", session.AccountId, AuthEndpoints.ClientIp(http),
                new { reason = "payload_too_large" }, ct);
            return Problems.Create(413, ProblemCodes.PayloadTooLarge,
                $"日志包超过 {ReportLimits.MaxBodyBytes / 1024} KB 上限。");
        }

        ErrorReportRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ErrorReportRequest>(body, StrictJson.Options);
        }
        catch (JsonException)
        {
            await audit.LogAsync("report.rejected", session.AccountId, AuthEndpoints.ClientIp(http),
                new { reason = "invalid_json" }, ct);
            return Problems.Create(400, ProblemCodes.InvalidJson,
                "请求内容不是有效的 JSON，或包含未定义的字段。");
        }

        if (ReportValidator.Validate(request, clock.GetUtcNow()) is { } violation)
        {
            await audit.LogAsync("report.rejected", session.AccountId, AuthEndpoints.ClientIp(http),
                new { reason = violation.Code, index = violation.Index }, ct);
            return Problems.Create(422, violation.Code, violation.Detail, violation.Index);
        }

        // Defence in depth: the client masks private data before upload, the
        // server refuses anything that still looks like a path, share link or
        // credential. A modified client gains nothing here.
        var privateIndex = PrivateDataScanner.FirstBlockingMessage(request!.Messages);
        if (privateIndex is { } index)
        {
            await audit.LogAsync("report.rejected", session.AccountId, AuthEndpoints.ClientIp(http),
                new { reason = "private_data", index }, ct);
            return Problems.Create(422, ProblemCodes.ReportContainsPrivateData,
                "日志第 " + (index + 1) + " 条仍包含路径、文件名、分享链接或凭据，已拒绝入库。", index);
        }

        try
        {
            var receipt = await reports.AcceptAsync(session.AccountId, request,
                body, AuthEndpoints.ClientIp(http), ct);
            return Results.Json(receipt, statusCode: StatusCodes.Status201Created);
        }
        catch (UploadQuotaExceededException exception)
        {
            await audit.LogAsync("report.rejected", session.AccountId, AuthEndpoints.ClientIp(http),
                new { reason = "quota" }, ct);
            var retryAfter = Math.Max(1, (int)Math.Ceiling(exception.RetryAfter.TotalSeconds));
            return new ProblemResult(429, ProblemCodes.UploadQuotaExceeded,
                "上传次数超出配额，请稍后再试。", retryAfterSeconds: retryAfter);
        }
        catch (DuplicateReportException)
        {
            await audit.LogAsync("report.rejected", session.AccountId, AuthEndpoints.ClientIp(http),
                new { reason = "duplicate" }, ct);
            return Problems.Create(409, ProblemCodes.DuplicateReport,
                "这份日志已经上传过，无需重复提交。");
        }
    }

    static async Task<IResult> List(HttpContext http, SessionAuthenticator authenticator,
        ReportRepository reports, CancellationToken ct)
    {
        var (session, problemCode) = await authenticator.AuthenticateAsync(http, ct);
        if (session is null) return Problems.Auth(problemCode);

        var rows = await reports.ListAsync(session.AccountId, 50, ct);
        return Results.Json(rows.Select(row => new ErrorReportSummary(row.Id, row.ReceivedAt,
            row.ExpiresAt, row.MessageCount, row.ByteSize)).ToArray());
    }

    static async Task<IResult> Delete(Guid id, HttpContext http, SessionAuthenticator authenticator,
        ReportService reports, CancellationToken ct)
    {
        var (session, problemCode) = await authenticator.AuthenticateAsync(http, ct);
        if (session is null) return Problems.Auth(problemCode);

        var deleted = await reports.DeleteAsync(session.AccountId, id,
            AuthEndpoints.ClientIp(http), ct);
        return deleted
            ? Results.NoContent()
            : Problems.Create(404, ProblemCodes.ReportNotFound, "找不到这份日志，或它已被删除。");
    }
}

internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/health", async (Npgsql.NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
                await using var command = new Npgsql.NpgsqlCommand("select 1", connection);
                await command.ExecuteScalarAsync(timeout.Token);
                return Results.Json(new HealthResponse("ok", "ok"));
            }
            catch (Exception exception) when (exception is Npgsql.NpgsqlException or OperationCanceledException)
            {
                return Results.Json(new HealthResponse("degraded", "unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted) return false;

        switch (exception)
        {
            case JsonException:
            case BadHttpRequestException { InnerException: JsonException }:
                await Problems.Create(400, ProblemCodes.InvalidJson,
                    "请求内容不是有效的 JSON，或包含未定义的字段。").ExecuteAsync(httpContext);
                return true;
            case BadHttpRequestException:
                await Problems.Create(400, ProblemCodes.InvalidRequest, "请求格式不正确。")
                    .ExecuteAsync(httpContext);
                return true;
            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                return true;
            default:
                logger.LogError(exception, "未处理的异常：{Path}", httpContext.Request.Path);
                await Problems.Create(500, ProblemCodes.InternalError, "服务器内部错误，请稍后重试。")
                    .ExecuteAsync(httpContext);
                return true;
        }
    }
}
