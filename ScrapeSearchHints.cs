using System.IO;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

internal static class ScrapeSearchHints
{
    static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "视频", "影片", "电影", "电视剧", "剧集", "动漫", "动画", "番剧", "其他", "未分类", "分类", "作品", "合集", "下载", "新建文件夹",
        "日本", "欧美", "国产", "亚洲", "韩国", "中国", "台湾", "香港", "高清", "中字", "中文字幕", "字幕", "已看", "未看", "观看中",
        "成人", "女优", "女優", "演员", "演員", "人物", "艺人", "模特", "素人", "资源", "整理", "备份", "收藏", "最新", "旧版",
        "纪录片", "记录片", "科幻", "动作", "喜剧", "剧情", "恐怖", "悬疑", "音乐", "游戏", "摄影", "旅行", "美食", "运动", "科技",
        "videos", "video", "movies", "movie", "series", "downloads", "download", "adult", "misc", "other", "actors", "performers",
        "collection", "collections", "private", "unsorted", "new", "old", "subtitles", "documentary", "anime", "music", "games"
    };

    static readonly HashSet<string> PersonContainerFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "女优", "女優", "演员", "演員", "人物", "艺人", "模特", "actors", "performers"
    };

    static readonly Regex QualityOrCollection = new(
        @"\b(?:4k|8k|2160p|1080p|720p|480p|h\.?26[45]|x26[45]|web[-_. ]?dl|webrip|bluray|bdrip|dvdrip|hdr|hevc|av1|mp4|mkv|avi)\b|" +
        @"(?:作品|合集|全集|完整版|字幕|资源|資源|下载|下載|整理|备份|備份|第\s*\d+\s*[季集部卷])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CodeOrDate = new(@"(?:\b(?:19|20)\d{2}\b)|(?:\b[A-Za-z]{2,10}[-_ ]?\d{2,5}\b)|\d", RegexOptions.Compiled);
    static readonly Regex SizeSuffix = new(@"[-_ ]+\d+(?:\.\d+)?\s*(?:GB?|MB?|TB?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LettersOnly = new(@"^[\p{L}\p{M}][\p{L}\p{M} ·・._-]*$", RegexOptions.Compiled);
    static readonly Regex CjkLetters = new(@"[\u3400-\u9FFF\u3040-\u30FF\uAC00-\uD7AF]", RegexOptions.Compiled);

    // Walk from the video's own directory up to (but not including) the
    // longest configured library root. A root path and its parents are never
    // interpreted as names. The return value is a single leaf name, not a path.
    internal static string? FindPersonHint(string? videoPath, IEnumerable<string>? libraryRoots)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || libraryRoots is null) return null;
        string? directory;
        try { directory = Path.GetDirectoryName(Path.GetFullPath(videoPath)); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(directory)) return null;
        directory = Path.TrimEndingDirectorySeparator(directory);
        var boundary = libraryRoots.Select(NormalizeRoot).Where(root => root is not null && IsInside(directory, root))
            .OrderByDescending(root => root!.Length).FirstOrDefault();
        if (boundary is null) return null;

        var title = AdditionalScrapers.CleanTitle(Path.GetFileNameWithoutExtension(videoPath));
        var current = directory;
        for (var depth = 0; depth < 64 && !current.Equals(boundary, StringComparison.OrdinalIgnoreCase); depth++)
        {
            var parent = Path.GetDirectoryName(current);
            var parentName = parent is null ? "" : Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
            var leaf = Path.GetFileName(current);
            if (!LooksLikeWorkFolder(leaf, title))
            {
                var person = PersonFromFolder(current, PersonContainerFolders.Contains(parentName));
                if (person is not null) return person;
            }
            if (string.IsNullOrWhiteSpace(parent) || !IsInside(parent, boundary)) break;
            current = Path.TrimEndingDirectorySeparator(parent);
        }
        return null;
    }

    internal static string? PersonFromFolder(string? folder, bool labelledParent = false)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string leaf;
        try { leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder.Trim())).Trim(); }
        catch { return null; }
        if (leaf.Length == 0) return null;
        leaf = SizeSuffix.Replace(leaf, "").Trim();
        if (Regex.IsMatch(leaf, @"^[A-Za-z]-[\u3400-\u9FFF]")) leaf = leaf[2..];
        if (leaf.StartsWith('@')) leaf = leaf[1..].Trim();
        if (leaf.Length is < 2 or > 32 || GenericFolders.Contains(leaf) || QualityOrCollection.IsMatch(leaf)
            || CodeOrDate.IsMatch(leaf) || !LettersOnly.IsMatch(leaf)) return null;

        // East Asian names can be one compact token; Latin names require two
        // words unless a labelled person container supplies stronger context.
        if (CjkLetters.IsMatch(leaf)) return leaf.Length <= 16 ? leaf : null;
        var words = Regex.Split(leaf, @"[\s._·・-]+", RegexOptions.None).Where(word => word.Length > 0).ToArray();
        if (words.Length is >= 2 and <= 4 && words.All(word => word.Length is >= 2 and <= 20)) return leaf;
        return labelledParent && words.Length == 1 && words[0].Length is >= 3 and <= 24 ? leaf : null;
    }

    internal static bool MatchesPerson(string person, string value)
    {
        var normalizedPerson = Normalize(person);
        var normalizedValue = Normalize(value);
        return normalizedPerson.Length >= 2 && normalizedValue.Contains(normalizedPerson, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool LooksLikeWorkFolder(string leaf, string filenameTitle)
    {
        var directory = Normalize(leaf);
        var title = Normalize(filenameTitle);
        return directory.Length >= 2 && title.Length >= 2 &&
            (title.Contains(directory, StringComparison.OrdinalIgnoreCase) || directory.Contains(title, StringComparison.OrdinalIgnoreCase));
    }

    static string Normalize(string value) => Regex.Replace(value, @"[\s._\-\[\]()（）·・]+", "").ToLowerInvariant();

    static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try { return Path.IsPathFullyQualified(root) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) : null; }
        catch { return null; }
    }

    static bool IsInside(string folder, string root)
    {
        if (folder.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
