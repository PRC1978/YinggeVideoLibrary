using System.Text;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// A paid Javinfo request needs two independent pieces of manually reviewable
// evidence. The constructor is private so callers cannot bypass validation by
// supplying an arbitrary title or an ambiguous filename fragment.
public sealed class JavinfoSearchEvidence
{
    static readonly Regex ExactCode = new(@"^(?:FC2-PPV-\d{5,8}|[A-Z]{2,10}-\d{2,5})$", RegexOptions.Compiled);
    static readonly HashSet<string> GenericPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "VID", "VIDEO", "IMG", "IMAGE", "MOV", "MOVIE", "CLIP", "FILE", "PART", "EP", "DSC", "MVI"
    };

    public string Code { get; }
    public string JapaneseTitle { get; }

    private JavinfoSearchEvidence(string code, string japaneseTitle)
    {
        Code = code;
        JapaneseTitle = japaneseTitle;
    }

    public static JavinfoSearchEvidence Create(string? codeInput, string? japaneseTitleInput,
        bool confirmedHanOnly = false, string? localCode = null)
    {
        var code = CanonicalCode(codeInput);
        if (code is null)
            throw new InvalidOperationException("Javinfo 是付费来源：请先填写明确的作品番号（例如 ABC-123），不能用模糊标题或文件序号查询。");
        if (!string.IsNullOrWhiteSpace(localCode))
        {
            var expected = CanonicalCode(localCode);
            if (expected is null || !code.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Javinfo 番号与当前视频已有番号不一致；请先核对视频和番号，本次不会发出付费请求。");
        }

        var title = (japaneseTitleInput ?? "").Trim().Normalize(NormalizationForm.FormKC);
        if (title.Length is < 2 or > 160 || title.Contains('\\') || title.Contains('/') ||
            title.Contains('\r') || title.Contains('\n') || title.Contains("://", StringComparison.Ordinal))
            throw new InvalidOperationException("Javinfo 需要明确的日文作品原名（不是路径、链接或文件序号）；请在手动搜刮窗口补充。");
        var cjkCount = title.Count(IsCjk);
        var hasKana = title.Any(IsKana);
        if (cjkCount < 2 || (!hasKana && !confirmedHanOnly))
            throw new InvalidOperationException("Javinfo 需要日文作品原名。纯汉字无法自动区分中文和日文，请核对原名后勾选确认；本次不会发出付费请求。");
        if (CanonicalCode(title) is not null || title.Equals(code, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("日文作品原名不能只填写番号；本次不会发出付费请求。");
        return new JavinfoSearchEvidence(code, title);
    }

    public static string? CanonicalCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var code = JavDbScraper.GuessCode(input.Trim());
        if (code is null || !ExactCode.IsMatch(code)) return null;
        // The input must be the code itself. A guessed substring from a long
        // filename is not sufficient evidence for a paid API call.
        var condensedInput = Regex.Replace(input.Trim(), @"[-_\s]", "").ToUpperInvariant();
        var condensedCode = code.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (!condensedInput.Equals(condensedCode, StringComparison.Ordinal)) return null;
        var prefix = code.Split('-')[0];
        return GenericPrefixes.Contains(prefix) ? null : code;
    }

    public static string? SuggestedJapaneseTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var title = text.Trim().Normalize(NormalizationForm.FormKC);
        var code = JavDbScraper.GuessCode(title);
        if (code is not null)
        {
            var components = code.Split('-');
            var fragment = components.Length == 3
                ? @"FC2[-_ ]?PPV[-_ ]?" + Regex.Escape(components[2])
                : Regex.Escape(components[0]) + @"[-_ ]?" + Regex.Escape(components[1]);
            title = Regex.Replace(title, fragment, " ", RegexOptions.IgnoreCase);
        }
        title = Regex.Replace(title, @"\.(mp4|mkv|avi|wmv|mov|m4v)$", "", RegexOptions.IgnoreCase).Trim(' ', '_', '-', '.');
        return title.Count(IsCjk) >= 2 ? title : null;
    }

    public bool MatchesTitleJa(string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        static string Normalize(string text)
        {
            var normalized = text.Normalize(NormalizationForm.FormKC);
            return new string(normalized.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }
        var expected = Normalize(JapaneseTitle);
        var returned = Normalize(actual);
        var code = Normalize(Code);
        if (returned.StartsWith(code, StringComparison.Ordinal)) returned = returned[code.Length..];
        return expected.Equals(returned, StringComparison.Ordinal);
    }

    static bool IsKana(char value) => value is >= '\u3041' and <= '\u3096' or >= '\u30A1' and <= '\u30FA';
    static bool IsCjk(char value) => IsKana(value) || value is >= '\u3400' and <= '\u9FFF';
}
