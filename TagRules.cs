using System.Text;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Tag text is normalised in more than one place: the config loader canonicalises
// the in-memory map, while review transactions compare against the file on disk.
// Both must use the same rules or a config that still carries a duplicate or an
// untrimmed tag looks "changed" on every commit and the review can never be
// queued.
internal static class TagRules
{
    static readonly char[] Separators = [',', '，', ';', '；', '\n', '\r', '\t'];

    internal const int MaxTags = 50;

    internal static List<string> Parse(string text) =>
        Normalize(text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    internal static List<string> Normalize(IEnumerable<string?>? tags)
    {
        if (tags is null) return [];
        return tags
            .Where(tag => tag is not null)
            .Select(tag => Regex.Replace(tag!.Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim().TrimStart('#', '＃'))
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxTags)
            .ToList();
    }
}
