using System.Runtime.InteropServices;
using System.Text;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Client;

// A report is built and redacted on the client, previewed by the user, and only
// then uploaded. Messages never include library index data because the host
// only passes the error event text it already stores locally.
public sealed record ErrorReportDraft
{
    public required string ClientVersion { get; init; }

    public required string Os { get; init; }

    public required string Arch { get; init; }

    public DateTimeOffset? OccurredAtUtc { get; init; }

    public required IReadOnlyList<string> Messages { get; init; }

    public int RedactionCount { get; init; }

    public static ErrorReportDraft Create(string clientVersion, IEnumerable<string?> messages,
        DateTimeOffset? occurredAtUtc = null, string? os = null, string? arch = null)
    {
        var redacted = ErrorReportRedactor.RedactMessages(messages);
        return new ErrorReportDraft
        {
            ClientVersion = NormalizeVersion(clientVersion),
            Os = os ?? DefaultOs(),
            Arch = arch ?? DefaultArch(),
            OccurredAtUtc = occurredAtUtc ?? DateTimeOffset.UtcNow,
            Messages = redacted.Messages,
            RedactionCount = redacted.Replacements
        };
    }

    public int MessageCount => Messages.Count;

    public int ByteSize => Messages.Sum(message => Encoding.UTF8.GetByteCount(message));

    public ErrorReportRequest ToRequest() =>
        new(ClientVersion, Os, Arch, OccurredAtUtc, Messages.ToArray());

    // Runs the same validation the server will run, so the user sees a problem
    // before anything leaves the machine.
    public string? ValidateLocally(DateTimeOffset now) =>
        ReportValidator.Validate(ToRequest(), now)?.Detail;

    public string PreviewText(int maxMessages = 5)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"错误条目：{MessageCount} 条，约 {ByteSize / 1024.0:0.0} KB");
        builder.AppendLine($"已在本机脱敏：{RedactionCount} 处（路径、文件名、分享链接、凭据）");
        builder.AppendLine("不会上传视频文件、封面、索引、配置或 API Key。");
        if (MessageCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine("前几条内容：");
            foreach (var message in Messages.Take(maxMessages))
                builder.AppendLine("· " + (message.Length > 160 ? message[..160] + "…" : message));
            if (MessageCount > maxMessages) builder.AppendLine($"…其余 {MessageCount - maxMessages} 条");
        }
        return builder.ToString();
    }

    static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "0.0.0";
        var builder = new StringBuilder(version.Length);
        foreach (var character in version.Trim())
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '-')
                builder.Append(character);
        var normalized = builder.ToString();
        if (normalized.Length == 0) return "0.0.0";
        return normalized.Length > ReportLimits.MaxClientVersionChars
            ? normalized[..ReportLimits.MaxClientVersionChars]
            : normalized;
    }

    static string DefaultOs()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        return "linux";
    }

    static string DefaultArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64"
    };
}
