namespace Yingge.Cloud.Protocol;

public enum PrivateDataKind
{
    WindowsPath,
    UncPath,
    PosixPath,
    FileUrl,
    MediaFileName,
    ShareLink,
    Credential,
    Email
}

public readonly record struct PrivateDataFinding(PrivateDataKind Kind, int Index, int Length)
{
    // A contact address can legitimately appear inside a third-party error
    // message and carries far less risk than a library path, so the server
    // redacts it in the client but does not refuse the upload for it.
    public bool IsBlocking => Kind != PrivateDataKind.Email;
}

// Server-side detector. The client performs the same scan before it shows the
// preview; the server runs it again because a modified client is assumed.
public static class PrivateDataScanner
{
    public static IReadOnlyList<PrivateDataFinding> Scan(string? text, bool includeEmail = true)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var findings = new List<PrivateDataFinding>();
        Collect(findings, text, PrivateDataKind.WindowsPath, PrivatePatterns.DrivePath);
        Collect(findings, text, PrivateDataKind.UncPath, PrivatePatterns.UncPath);
        Collect(findings, text, PrivateDataKind.PosixPath, PrivatePatterns.PosixPath);
        Collect(findings, text, PrivateDataKind.FileUrl, PrivatePatterns.FileUrl);
        Collect(findings, text, PrivateDataKind.MediaFileName, PrivatePatterns.MediaFileName);
        Collect(findings, text, PrivateDataKind.ShareLink, PrivatePatterns.ShareLink);
        Collect(findings, text, PrivateDataKind.Credential, PrivatePatterns.CredentialValue);
        Collect(findings, text, PrivateDataKind.Credential, PrivatePatterns.CredentialAssignment);
        Collect(findings, text, PrivateDataKind.Credential, PrivatePatterns.SignedUrl);
        if (includeEmail) Collect(findings, text, PrivateDataKind.Email, PrivatePatterns.Email);
        return findings;
    }

    public static PrivateDataFinding? FindBlocking(string? text)
    {
        var findings = Scan(text, includeEmail: false);
        return findings.Count > 0 ? findings[0] : null;
    }

    // Returns the index of the first message that still contains private data,
    // or null when every message is clean.
    public static int? FirstBlockingMessage(IReadOnlyList<string>? messages)
    {
        if (messages is null) return null;
        for (var index = 0; index < messages.Count; index++)
            if (Scan(messages[index], includeEmail: false).Any(finding => finding.IsBlocking))
                return index;
        return null;
    }

    static void Collect(List<PrivateDataFinding> findings, string text, PrivateDataKind kind,
        System.Text.RegularExpressions.Regex pattern)
    {
        foreach (System.Text.RegularExpressions.Match match in pattern.Matches(text))
            findings.Add(new PrivateDataFinding(kind, match.Index, match.Length));
    }
}
