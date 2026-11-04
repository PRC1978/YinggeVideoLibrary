using System.Text;

namespace Yingge.Cloud.Protocol;

public readonly record struct ReportValidationError(string Code, string Detail, int? Index);

// Shared schema validation. The server always runs it; the client runs the
// same code before upload so a user sees a local error instead of a round trip.
public static class ReportValidator
{
    public static ReportValidationError? Validate(ErrorReportRequest? request, DateTimeOffset now)
    {
        if (request is null) return Invalid("缺少报告内容。");

        var version = request.ClientVersion;
        if (string.IsNullOrWhiteSpace(version) ||
            version.Length > ReportLimits.MaxClientVersionChars ||
            !IsVersionToken(version))
            return Invalid("客户端版本号格式不正确。");

        if (string.IsNullOrEmpty(request.Os) || !ReportLimits.IsAllowedOs(request.Os))
            return Invalid("操作系统字段不在允许范围内。");

        if (string.IsNullOrEmpty(request.Arch) || !ReportLimits.IsAllowedArch(request.Arch))
            return Invalid("架构字段不在允许范围内。");

        if (request.OccurredAtUtc is { } occurred)
        {
            if (occurred > now.AddMinutes(10) || occurred < now.AddDays(-30))
                return Invalid("报告时间超出允许范围。");
        }

        if (request.Messages is null || request.Messages.Length == 0)
            return Invalid("报告至少需要一条错误消息。");

        if (request.Messages.Length > ReportLimits.MaxMessages)
            return new ReportValidationError(ProblemCodes.ReportInvalid,
                $"错误消息超过 {ReportLimits.MaxMessages} 条上限。", null);

        var total = 0;
        for (var index = 0; index < request.Messages.Length; index++)
        {
            var message = request.Messages[index];
            if (string.IsNullOrWhiteSpace(message))
                return new ReportValidationError(ProblemCodes.ReportInvalid, "错误消息不能为空。", index);
            if (message.Length > ReportLimits.MaxMessageChars)
                return new ReportValidationError(ProblemCodes.ReportInvalid,
                    $"单条错误消息超过 {ReportLimits.MaxMessageChars} 字符上限。", index);
            if (message.Contains('\0') || HasForbiddenControlCharacter(message))
                return new ReportValidationError(ProblemCodes.ReportInvalid,
                    "错误消息包含不允许的控制字符。", index);
            total += Encoding.UTF8.GetByteCount(message);
        }

        if (total > ReportLimits.MaxBodyBytes / 2)
            return new ReportValidationError(ProblemCodes.ReportInvalid, "错误消息总长度超出上限。", null);

        return null;
    }

    static ReportValidationError Invalid(string detail) =>
        new(ProblemCodes.ReportInvalid, detail, null);

    static bool IsVersionToken(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '+' or '-'))
                return false;
        }
        return char.IsAsciiLetterOrDigit(value[0]);
    }

    static bool HasForbiddenControlCharacter(string value)
    {
        foreach (var character in value)
            if (char.IsControl(character) && character is not ('\t' or '\n' or '\r'))
                return true;
        return false;
    }
}

public static class AccountRules
{
    static readonly string[] WeakPasswords =
    [
        "123456789012", "password1234", "qwertyuiop12", "aaaaaaaaaaaa",
        "111111111111", "passwordpassword", "letmein123456", "admin12345678"
    ];

    public static string NormalizeInviteCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return string.Empty;
        var builder = new StringBuilder(code.Length);
        foreach (var character in code)
        {
            if (character is '-' or ' ') continue;
            if (!char.IsAsciiLetterOrDigit(character)) return string.Empty;
            builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }

    public static bool IsWellFormedInviteCode(string normalized) =>
        normalized.Length is >= 16 and <= 32;

    public static string NormalizeUsername(string? username) =>
        (username ?? string.Empty).Trim().ToLowerInvariant();

    public static string? ValidateUsername(string normalized)
    {
        if (normalized.Length is < ReportLimits.MinUsernameChars or > ReportLimits.MaxUsernameChars)
            return $"账号名需要 {ReportLimits.MinUsernameChars} 到 {ReportLimits.MaxUsernameChars} 个字符。";
        if (!char.IsAsciiLetterOrDigit(normalized[0])) return "账号名必须以字母或数字开头。";
        foreach (var character in normalized)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
                return "账号名只允许字母、数字、点、下划线和短横线。";
        return null;
    }

    public static string? ValidatePassword(string? password, string normalizedUsername)
    {
        if (password is null) return "密码不能为空。";
        if (password.Length < ReportLimits.MinPasswordChars)
            return $"密码至少需要 {ReportLimits.MinPasswordChars} 个字符。";
        if (password.Length > ReportLimits.MaxPasswordChars)
            return $"密码不能超过 {ReportLimits.MaxPasswordChars} 个字符。";
        if (string.Equals(password, normalizedUsername, StringComparison.OrdinalIgnoreCase))
            return "密码不能与账号名相同。";
        if (WeakPasswords.Contains(password, StringComparer.OrdinalIgnoreCase))
            return "密码过于常见，请换一个。";
        var hasLetter = password.Any(char.IsLetter);
        var hasDigit = password.Any(char.IsDigit);
        if (!hasLetter || !hasDigit) return "密码需要同时包含字母和数字。";
        return null;
    }
}
