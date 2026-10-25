using System.Text;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Only the optional separator between a letter prefix and its numeric suffix
// is interchangeable. Removing every dash merges distinct code structures.
internal static class ScrapeCodeIdentity
{
    static readonly Regex Standard = new(@"^(?<prefix>[A-Z]{2,12})-?(?<number>[0-9]{2,8})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Fc2 = new(@"^FC2-?PPV-?(?<number>[0-9]{5,8})(?:-(?<part>[0-9]{2,3}))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Structured = new(@"^[A-Z0-9]+(?:-[A-Z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string Key(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var normalized = code.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        normalized = Regex.Replace(normalized, @"[\s_]+", "-");
        if (normalized.Length is < 4 or > 40 || !Structured.IsMatch(normalized)) return "";
        var fc2 = Fc2.Match(normalized);
        if (fc2.Success) return "FC2-PPV-" + fc2.Groups["number"].Value +
            (fc2.Groups["part"].Success ? "-" + fc2.Groups["part"].Value : "");
        var standard = Standard.Match(normalized);
        return standard.Success
            ? standard.Groups["prefix"].Value + "-" + standard.Groups["number"].Value
            : normalized;
    }

    internal static bool Same(string? left, string? right)
    {
        var key = Key(left);
        return key.Length >= 4 && key.Equals(Key(right), StringComparison.Ordinal);
    }

    internal static bool VerifyForDiagnostics() =>
        Same("ABC-123", "abc123") && Same("ABP_001", "ABP-001") &&
        Same("FC2-PPV-4739552", "fc2ppv4739552") &&
        !Same("ABC1-23", "ABC-123") && !Same("FC2-PPV-4739552-01", "FC2-PPV-4739552") &&
        !Same("ABC-123", "ABC-124") && !Same("???", "???");
}
