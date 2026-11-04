using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Accounts.Api;

// Every error response uses the same shape and a stable machine code. Details
// are Chinese because the client shows them directly; codes never change.
internal sealed class ProblemResult : IResult
{
    static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    readonly int status;
    readonly string code;
    readonly string detail;
    readonly int? index;
    readonly int? retryAfterSeconds;

    public ProblemResult(int status, string code, string detail, int? index = null, int? retryAfterSeconds = null)
    {
        this.status = status;
        this.code = code;
        this.detail = detail;
        this.index = index;
        this.retryAfterSeconds = retryAfterSeconds;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        if (httpContext.Response.HasStarted) return;
        httpContext.Response.Clear();
        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";
        if (retryAfterSeconds is { } seconds)
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        var problem = new ApiProblem(
            "https://yingge.top/problems/" + code,
            Title(code),
            status,
            code,
            detail,
            index);
        await httpContext.Response.Body.WriteAsync(
            JsonSerializer.SerializeToUtf8Bytes(problem, SerializerOptions),
            httpContext.RequestAborted);
    }

    static string Title(string code) => code switch
    {
        ProblemCodes.InvalidRequest => "请求不合法",
        ProblemCodes.InvalidJson => "请求内容不合法",
        ProblemCodes.InvalidCredentials => "登录失败",
        ProblemCodes.InviteInvalid => "邀请码不可用",
        ProblemCodes.UsernameTaken => "账号名已被占用",
        ProblemCodes.AccountDisabled => "账号已停用",
        ProblemCodes.AccountLocked => "账号暂时锁定",
        ProblemCodes.RateLimited => "请求过于频繁",
        ProblemCodes.Unauthorized => "需要登录",
        ProblemCodes.HttpsRequired => "需要 HTTPS",
        ProblemCodes.PayloadTooLarge => "内容过大",
        ProblemCodes.ReportInvalid => "日志格式不符合要求",
        ProblemCodes.ReportContainsPrivateData => "日志含私人信息",
        ProblemCodes.DuplicateReport => "重复提交",
        ProblemCodes.UploadQuotaExceeded => "超出上传配额",
        ProblemCodes.ReportNotFound => "日志不存在",
        ProblemCodes.NotFound => "找不到资源",
        _ => "服务器错误"
    };
}

internal static class Problems
{
    public static ProblemResult Create(int status, string code, string detail, int? index = null) =>
        new(status, code, detail, index);

    public static ProblemResult RateLimited(string detail, int retryAfterSeconds) =>
        new(StatusCodes.Status429TooManyRequests, ProblemCodes.RateLimited, detail,
            retryAfterSeconds: retryAfterSeconds);

    public static ProblemResult Auth(string? problemCode) => problemCode == ProblemCodes.AccountDisabled
        ? Create(StatusCodes.Status403Forbidden, ProblemCodes.AccountDisabled, "该账号已被停用，请联系维护者。")
        : Create(StatusCodes.Status401Unauthorized, ProblemCodes.Unauthorized, "需要先登录。");
}
