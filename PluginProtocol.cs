using System.Text.Json;
using System.Text.Json.Serialization;

namespace YinggeVideoLibrary;

// Versioned JSON-lines protocol. One request per line, one response per line,
// matched by id. The host never trusts a field it did not validate.
internal sealed class PluginRequest
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public PluginSearchParameters? Parameters { get; set; }
}

internal sealed class PluginSearchParameters
{
    // Opaque per-call token. The plugin never learns the video id or the file
    // path, so it cannot build a library map from repeated calls.
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("query")] public string Query { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "title";
    [JsonPropertyName("limit")] public int Limit { get; set; } = 8;
    [JsonPropertyName("year")] public int? Year { get; set; }
    [JsonPropertyName("settings")] public Dictionary<string, string>? Settings { get; set; }
}

internal sealed class PluginResponse
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] public JsonElement Result { get; set; }
    [JsonPropertyName("error")] public PluginErrorPayload? Error { get; set; }
}

internal sealed class PluginErrorPayload
{
    [JsonPropertyName("code")] public string Code { get; set; } = "error";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("retryAfterSeconds")] public int? RetryAfterSeconds { get; set; }
}

internal sealed record PluginHello(string Name, string Version, IReadOnlyList<string> Capabilities);

internal sealed record PluginCandidate(
    string Title, string? OriginalTitle, string? Code, int? Year,
    string Source, string SourceUrl, string? PosterUrl, string? Summary,
    string Evidence, int ConfidenceHint);

internal sealed record PluginSearchOutcome(
    bool Ok, string ErrorCode, string ErrorMessage, TimeSpan? RetryAfter,
    IReadOnlyList<PluginCandidate> Candidates, long ElapsedMs, int DroppedCandidates = 0)
{
    internal static PluginSearchOutcome Failure(string code, string message, long elapsedMs, TimeSpan? retryAfter = null) =>
        new(false, code, message, retryAfter, [], elapsedMs);
}

internal static class PluginProtocol
{
    internal const string MethodDescribe = "describe";
    internal const string MethodSearch = "search";
    internal const int MaxCandidates = 20;
    internal const int MaxTitleLength = 300;
    internal const int MaxUrlLength = 2048;

    internal static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string Encode(PluginRequest request) => JsonSerializer.Serialize(request, Json);
}
