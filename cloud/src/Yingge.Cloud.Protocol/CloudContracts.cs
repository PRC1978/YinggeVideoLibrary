using System.Text.Json.Serialization;

namespace Yingge.Cloud.Protocol;

// Wire contract for the invitation-only account service. The client and the
// server share these records, but the server never trusts a field because it
// came from the client: every value is validated again server side.

public sealed record RegisterRequest(string InviteCode, string Username, string Password);

public sealed record LoginRequest(string Username, string Password);

public sealed record RegistrationResponse(Guid AccountId, string Username);

public sealed record AuthResponse(
    string Token,
    DateTimeOffset ExpiresAt,
    string Username,
    Guid AccountId);

public sealed record SessionStatus(
    bool Authenticated,
    string Username,
    DateTimeOffset ExpiresAt,
    int UploadsLast24Hours,
    int DailyUploadLimit);

public sealed record ErrorReportRequest(
    string ClientVersion,
    string Os,
    string Arch,
    DateTimeOffset? OccurredAtUtc,
    string[] Messages);

public sealed record ErrorReportReceipt(
    Guid Id,
    DateTimeOffset ReceivedAt,
    DateTimeOffset ExpiresAt,
    int MessageCount,
    int ByteSize);

public sealed record ErrorReportSummary(
    Guid Id,
    DateTimeOffset ReceivedAt,
    DateTimeOffset ExpiresAt,
    int MessageCount,
    int ByteSize);

public sealed record ApiProblem(
    string? Type,
    string? Title,
    int Status,
    string Code,
    string? Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Index = null);

public sealed record HealthResponse(string Status, string Database);

// Stable machine-readable codes. Clients branch on these, never on the message
// text, so the operator can reword details without breaking a client build.
public static class ProblemCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidJson = "invalid_json";
    public const string InvalidCredentials = "invalid_credentials";
    public const string InviteInvalid = "invite_invalid";
    public const string UsernameTaken = "username_taken";
    public const string AccountDisabled = "account_disabled";
    public const string AccountLocked = "account_locked";
    public const string RateLimited = "rate_limited";
    public const string Unauthorized = "unauthorized";
    public const string HttpsRequired = "https_required";
    public const string PayloadTooLarge = "payload_too_large";
    public const string ReportInvalid = "report_invalid";
    public const string ReportContainsPrivateData = "report_contains_private_data";
    public const string DuplicateReport = "duplicate_report";
    public const string UploadQuotaExceeded = "upload_quota_exceeded";
    public const string ReportNotFound = "report_not_found";
    public const string NotFound = "not_found";
    public const string InternalError = "internal_error";
    public const string NetworkError = "network_error";
    public const string Timeout = "timeout";
    public const string InvalidConfiguration = "invalid_configuration";
}

public static class ReportLimits
{
    public const int MaxMessages = 100;
    public const int MaxMessageChars = 2000;
    public const int MaxBodyBytes = 128 * 1024;
    public const int MaxClientVersionChars = 32;
    public const int MaxUsernameChars = 32;
    public const int MinUsernameChars = 3;
    public const int MinPasswordChars = 12;
    public const int MaxPasswordChars = 128;

    public static bool IsAllowedOs(string value) => value is "windows" or "linux" or "macos";

    public static bool IsAllowedArch(string value) => value is "x64" or "arm64" or "x86";
}
