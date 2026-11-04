using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Client;

public sealed record LogoutResult(bool RevokedOnServer, string? Error);

public sealed class CloudAccountClient : IDisposable
{
    static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    readonly HttpClient http;
    readonly bool ownsHttpClient;
    readonly CloudClientOptions options;
    readonly ISessionStore store;
    string? token;

    public CloudAccountClient(CloudClientOptions options, ISessionStore? sessionStore = null,
        HttpClient? httpClient = null)
    {
        if (options.Validate() is { } error) throw new ArgumentException(error, nameof(options));

        this.options = options;
        store = sessionStore ?? new NullSessionStore();
        ownsHttpClient = httpClient is null;
        http = httpClient ?? CreateHttpClient(options);
        if (http.BaseAddress is null) http.BaseAddress = options.BaseAddress;
    }

    public Uri BaseAddress => options.BaseAddress;

    public bool HasSession => !string.IsNullOrEmpty(token);

    public async Task<RegistrationResponse> RegisterAsync(string inviteCode, string username,
        string password, CancellationToken ct = default)
    {
        var body = await SendAsync(JsonRequest(HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(inviteCode, username, password)), ct);
        return Read<RegistrationResponse>(body)
               ?? throw new CloudAccountException(ProblemCodes.InternalError, null, "服务器未返回注册结果。");
    }

    public async Task<AuthResponse> LoginAsync(string username, string password,
        CancellationToken ct = default)
    {
        var body = await SendAsync(JsonRequest(HttpMethod.Post, "/api/v1/auth/login",
            new LoginRequest(username, password)), ct);
        var auth = Read<AuthResponse>(body)
                   ?? throw new CloudAccountException(ProblemCodes.InternalError, null, "服务器未返回登录结果。");
        token = auth.Token;
        await store.WriteAsync(auth.Token, ct);
        return auth;
    }

    public async Task<LogoutResult> LogoutAsync(CancellationToken ct = default)
    {
        var active = await EnsureTokenAsync(ct);
        await store.ClearAsync(ct);
        token = null;
        if (active is null) return new LogoutResult(true, null);

        try
        {
            using var request = Authorized(HttpMethod.Post, "/api/v1/auth/logout");
            await SendAsync(request, ct);
            return new LogoutResult(true, null);
        }
        catch (CloudAccountException exception) when (exception.IsNetworkProblem)
        {
            // The local token is already gone; the server copy expires on its own.
            return new LogoutResult(false, exception.Message);
        }
        catch (CloudAccountException exception) when (exception.Code == ProblemCodes.Unauthorized)
        {
            return new LogoutResult(true, null);
        }
    }

    public async Task<bool> RestoreSessionAsync(CancellationToken ct = default)
    {
        var stored = await store.ReadAsync(ct);
        if (string.IsNullOrEmpty(stored)) return false;
        token = stored;
        var status = await GetSessionAsync(ct);
        return status is not null;
    }

    public async Task<SessionStatus?> GetSessionAsync(CancellationToken ct = default)
    {
        if (await EnsureTokenAsync(ct) is null) return null;
        try
        {
            using var request = Authorized(HttpMethod.Get, "/api/v1/auth/session");
            var body = await SendAsync(request, ct);
            return Read<SessionStatus>(body);
        }
        catch (CloudAccountException exception) when (exception.Code == ProblemCodes.Unauthorized)
        {
            token = null;
            await store.ClearAsync(ct);
            return null;
        }
    }

    public async Task<ErrorReportReceipt> UploadReportAsync(ErrorReportDraft draft,
        CancellationToken ct = default)
    {
        if (draft.ValidateLocally(DateTimeOffset.UtcNow) is { } localError)
            throw new CloudAccountException(ProblemCodes.ReportInvalid, 422, localError);

        if (await EnsureTokenAsync(ct) is null)
            throw new CloudAccountException(ProblemCodes.Unauthorized, 401, "需要先登录再上传。");

        try
        {
            using var request = Authorized(HttpMethod.Post, "/api/v1/reports", draft.ToRequest());
            var body = await SendAsync(request, ct);
            return Read<ErrorReportReceipt>(body)
                   ?? throw new CloudAccountException(ProblemCodes.InternalError, null, "服务器未返回上传结果。");
        }
        catch (CloudAccountException exception) when (exception.Code == ProblemCodes.Unauthorized)
        {
            token = null;
            await store.ClearAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<ErrorReportSummary>> ListReportsAsync(CancellationToken ct = default)
    {
        if (await EnsureTokenAsync(ct) is null)
            throw new CloudAccountException(ProblemCodes.Unauthorized, 401, "需要先登录。");
        using var request = Authorized(HttpMethod.Get, "/api/v1/reports");
        var body = await SendAsync(request, ct);
        return Read<ErrorReportSummary[]>(body) ?? [];
    }

    public async Task DeleteReportAsync(Guid id, CancellationToken ct = default)
    {
        if (await EnsureTokenAsync(ct) is null)
            throw new CloudAccountException(ProblemCodes.Unauthorized, 401, "需要先登录。");
        using var request = Authorized(HttpMethod.Delete, "/api/v1/reports/" + id.ToString("D"));
        await SendAsync(request, ct);
    }

    public void Dispose()
    {
        if (ownsHttpClient) http.Dispose();
    }

    static HttpClient CreateHttpClient(CloudClientOptions options) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            BaseAddress = options.BaseAddress,
            Timeout = options.Timeout
        };

    async Task<string?> EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(token)) return token;
        token = await store.ReadAsync(ct);
        return string.IsNullOrEmpty(token) ? null : token;
    }

    HttpRequestMessage JsonRequest(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Client-Version", options.ClientVersion ?? "0.0.0");
        request.Headers.UserAgent.ParseAdd(options.UserAgent);
        if (body is not null) request.Content = JsonContent.Create(body, options: SerializerOptions);
        return request;
    }

    HttpRequestMessage Authorized(HttpMethod method, string path) =>
        Authorized(method, path, null);

    HttpRequestMessage Authorized(HttpMethod method, string path, object? body)
    {
        var request = JsonRequest(method, path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    async Task<byte[]> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            if (!response.IsSuccessStatusCode) throw Problem(response, body);
            return body;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudAccountException(ProblemCodes.Timeout, null, "连接服务器超时。");
        }
        catch (HttpRequestException exception)
        {
            throw new CloudAccountException(ProblemCodes.NetworkError, null,
                "无法连接服务器：" + exception.Message, exception);
        }
    }

    static CloudAccountException Problem(HttpResponseMessage response, byte[] body)
    {
        ApiProblem? problem = null;
        try
        {
            problem = JsonSerializer.Deserialize<ApiProblem>(body, SerializerOptions);
        }
        catch (JsonException)
        {
            // Fall back to a generic message; the status code still drives the UI.
        }

        var status = (int)response.StatusCode;
        var code = problem?.Code ?? (status switch
        {
            401 => ProblemCodes.Unauthorized,
            413 => ProblemCodes.PayloadTooLarge,
            _ => ProblemCodes.InternalError
        });

        int? retryAfter = null;
        if (response.Headers.RetryAfter?.Delta is { } delta)
            retryAfter = (int)Math.Ceiling(delta.TotalSeconds);
        else if (response.Headers.RetryAfter?.Date is { } date)
            retryAfter = (int)Math.Max(1, Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));

        return new CloudAccountException(code, status,
            problem?.Detail ?? $"服务器返回 {(int)response.StatusCode}。", null, retryAfter);
    }

    static T? Read<T>(byte[] body)
    {
        if (body.Length == 0) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(body, SerializerOptions);
        }
        catch (JsonException)
        {
            throw new CloudAccountException(ProblemCodes.InternalError, null, "服务器返回了无法解析的内容。");
        }
    }
}
