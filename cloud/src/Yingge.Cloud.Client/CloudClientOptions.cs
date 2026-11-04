namespace Yingge.Cloud.Client;

public sealed class CloudAccountException : Exception
{
    public CloudAccountException(string code, int? statusCode, string message, Exception? inner = null,
        int? retryAfterSeconds = null)
        : base(message, inner)
    {
        Code = code;
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public string Code { get; }

    public int? StatusCode { get; }

    public int? RetryAfterSeconds { get; }

    public bool IsNetworkProblem => Code is Protocol.ProblemCodes.NetworkError or Protocol.ProblemCodes.Timeout;
}

public sealed class CloudClientOptions
{
    public required Uri BaseAddress { get; init; }

    // Only for an isolated loopback test server. The desktop host never sets
    // this, and Validate() refuses plain HTTP for every other host.
    public bool AllowInsecureLoopback { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public string UserAgent { get; init; } = "YinggeVideoLibrary-CloudClient";

    public string? ClientVersion { get; init; }

    public string? Validate()
    {
        if (BaseAddress is null) return "缺少服务器地址。";
        if (!BaseAddress.IsAbsoluteUri) return "服务器地址必须是完整 URL。";
        if (BaseAddress.Scheme == Uri.UriSchemeHttps) return null;
        if (BaseAddress.Scheme == Uri.UriSchemeHttp && AllowInsecureLoopback && IsLoopback(BaseAddress.Host))
            return null;
        return "服务器地址必须使用 HTTPS；只有本机回环测试允许明文 HTTP。";
    }

    static bool IsLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
}
