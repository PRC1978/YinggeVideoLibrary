using System.Text.RegularExpressions;

namespace Yingge.Cloud.Protocol;

// Patterns that identify data this service must never receive: local media
// paths, file names, share links and credentials. They are deliberately
// conservative. A false positive costs the user one rejected upload; a false
// negative stores private library metadata on a shared server.
internal static class PrivatePatterns
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    internal static readonly Regex DrivePath = new(
        @"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\r\n\t""'<>|?；，。]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);

    internal static readonly Regex UncPath = new(
        @"\\\\[A-Za-z0-9._$-]+\\[^\r\n\t""'<>|?；，。]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);

    internal static readonly Regex PosixPath = new(
        @"(?<![\w.])/(?:home|Users|mnt|media|Volumes|private|var/folders)/[^\r\n\t""'<>|；，。]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);

    internal static readonly Regex FileUrl = new(
        @"file://[^\r\n\t""'<>|]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    internal static readonly Regex MediaFileName = new(
        @"\.(?:mkv|mp4|avi|wmv|mov|m2ts|mts|ts|iso|flv|webm|rmvb|rm|mpg|mpeg|m4v|vob|divx|ogv|3gp|srt|ass|ssa|sub|idx|nfo)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    internal static readonly Regex ShareLink = new(
        @"(?:magnet:\?|ed2k://|thunder://|ftp://)[^\s""'<>]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    internal static readonly Regex CredentialValue = new(
        @"(?i)\b(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);

    internal static readonly Regex CredentialAssignment = new(
        @"(?i)\b(?:api[_-]?key|apikey|client[_-]?secret|secret|password|passwd|access[_-]?token|refresh[_-]?token|authorization)\b\s*[:=]\s*[""']?[A-Za-z0-9._~+/-]{12,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);

    internal static readonly Regex SignedUrl = new(
        @"(?i)https?://[^\s""'<>]*[?&](?:key|token|sig|signature|auth|secret|credential)=[^\s""'<>]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);

    internal static readonly Regex Email = new(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);
}
