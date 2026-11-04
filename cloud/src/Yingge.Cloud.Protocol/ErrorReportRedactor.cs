using System.Text.RegularExpressions;

namespace Yingge.Cloud.Protocol;

public readonly record struct RedactionResult(string Text, int Replacements);

public readonly record struct MessagesRedactionResult(IReadOnlyList<string> Messages, int Replacements);

// Client-side masking. Runs before the preview dialog so the user sees exactly
// what will leave the machine, and before the upload so the server-side scan
// has nothing left to refuse.
public static class ErrorReportRedactor
{
    const string PathToken = "<路径>";
    const string FileToken = "<文件名>";
    const string LinkToken = "<链接>";
    const string CredentialToken = "<凭据>";
    const string EmailToken = "<邮箱>";

    public static RedactionResult Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new RedactionResult(text ?? string.Empty, 0);
        var current = text;
        var replacements = 0;
        current = Replace(current, PrivatePatterns.ShareLink, LinkToken, ref replacements);
        current = Replace(current, PrivatePatterns.SignedUrl, LinkToken, ref replacements);
        current = Replace(current, PrivatePatterns.CredentialValue, CredentialToken, ref replacements);
        current = Replace(current, PrivatePatterns.CredentialAssignment, CredentialToken, ref replacements);
        current = Replace(current, PrivatePatterns.FileUrl, PathToken, ref replacements);
        current = Replace(current, PrivatePatterns.DrivePath, PathToken, ref replacements);
        current = Replace(current, PrivatePatterns.UncPath, PathToken, ref replacements);
        current = Replace(current, PrivatePatterns.PosixPath, PathToken, ref replacements);
        current = Replace(current, PrivatePatterns.MediaFileName, FileToken, ref replacements);
        current = Replace(current, PrivatePatterns.Email, EmailToken, ref replacements);
        return new RedactionResult(current, replacements);
    }

    public static MessagesRedactionResult RedactMessages(IEnumerable<string?> messages)
    {
        var replacements = 0;
        var result = new List<string>();
        foreach (var message in messages)
        {
            var redacted = Redact(message);
            replacements += redacted.Replacements;
            result.Add(redacted.Text);
        }
        return new MessagesRedactionResult(result, replacements);
    }

    static string Replace(string text, Regex pattern, string token, ref int replacements)
    {
        var matches = pattern.Matches(text);
        if (matches.Count == 0) return text;
        replacements += matches.Count;
        return pattern.Replace(text, token);
    }
}
