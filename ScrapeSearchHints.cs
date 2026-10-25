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
        "collection", "collections", "private", "unsorted", "new", "old", "subtitles", "documentary", "anime", "music", "games",
        "cosplay", "clips", "photos", "pictures", "images", "travel", "photography", "japan", "korea", "asian", "western",
        "amateur", "models", "model", "tags", "favorites", "archive", "archives", "genres", "genre", "studio", "studios",
        "子目录", "预览", "花絮", "正片", "原片", "封面", "样片", "素材", "片段", "系列", "章节",
        "subfolder", "folder", "extras", "extra", "samples", "sample", "covers", "cover", "thumbnails", "thumbs", "screenshots", "preview", "previews"
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
    static readonly Regex LatinSingleToken = new(@"^[A-Za-z]{5,24}$", RegexOptions.Compiled);
    static readonly Regex InternalCapital = new(@"[a-z][A-Z]", RegexOptions.Compiled);
    static readonly Regex EnglishCategorySuffix = new(@"^[A-Za-z]+(?:Category|Collection|Genre|Videos|Library|Media|Clips|Archive|Studio)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NumberedFileTitle = new(@"^(?:(?:video|vid|clip|movie|file|part|ep|episode|scene)[\s._-]*)?\d+(?:[\s._-]\d+)*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CjkLetters = new(@"[\u3400-\u9FFF\u3040-\u30FF\uAC00-\uD7AF]", RegexOptions.Compiled);

    // Consider the whole rooted chain: the closest folder can be a work title
    // or an incidental subfolder. A person container or creator-like spelling
    // wins first; ancestry only breaks close ties after generic folders are
    // filtered. Never return the library root or a complete path.
    internal static string? FindPersonHint(string? videoPath, IEnumerable<string>? libraryRoots)
    {
        var directories = RootedDirectories(videoPath, libraryRoots);
        if (directories is null) return null;
        var title = AdditionalScrapers.CleanTitle(Path.GetFileNameWithoutExtension(videoPath!));
        return ChoosePerson(directories, title)?.Name;
    }

    // When the filename is only a counter, use a work folder below the
    // selected person. A category outside that person's subtree is never a
    // substitute title, even if it happens to contain words.
    internal static string? FindWorkTitleHint(string? videoPath, IEnumerable<string>? libraryRoots,
        bool assumeWeakFilename = false)
    {
        var directories = RootedDirectories(videoPath, libraryRoots);
        if (directories is null) return null;
        var fileTitle = AdditionalScrapers.CleanTitle(Path.GetFileNameWithoutExtension(videoPath!));
        if (!assumeWeakFilename && !IsWeakTitle(fileTitle)) return null;
        var person = ChoosePerson(directories, fileTitle);
        if (person is null) return null;
        for (var index = person.Value.Index + 1; index < directories.Count; index++)
        {
            var leaf = Path.GetFileName(directories[index]);
            if (leaf.Length is < 2 or > 120 || GenericFolders.Contains(leaf) || EnglishCategorySuffix.IsMatch(leaf)
                || NumberedFileTitle.IsMatch(leaf) || Regex.IsMatch(leaf, @"^(?:19|20)\d{2}$")
                || !Regex.IsMatch(leaf, @"[\p{L}]{2,}")) continue;
            var cleaned = AdditionalScrapers.CleanTitle(leaf);
            if (!IsWeakTitle(cleaned)) return cleaned;
        }
        return null;
    }

    internal static bool IsWeakTitle(string? title) => string.IsNullOrWhiteSpace(title)
        || NumberedFileTitle.IsMatch(AdditionalScrapers.CleanTitle(title).Trim());

    static (int Index, string Name)? ChoosePerson(IReadOnlyList<string> directories, string filenameTitle)
    {
        (int Index, string Name, int Score)? best = null;
        for (var index = 0; index < directories.Count; index++)
        {
            var leaf = Path.GetFileName(directories[index]);
            if (LooksLikeWorkFolder(leaf, filenameTitle)) continue;
            var parent = Path.GetDirectoryName(directories[index]);
            var parentName = parent is null ? "" : Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
            var labelled = PersonContainerFolders.Contains(parentName);
            var name = PersonFromFolder(directories[index], labelled);
            if (name is null) continue;
            var score = labelled ? 100 : 0;
            if (leaf.StartsWith('@') || Regex.IsMatch(leaf, @"^[A-Za-z]-[\u3400-\u9FFF]")) score += 25;
            if (CjkLetters.IsMatch(name)) score += name.Length <= 6 ? 18 : 4;
            else if (LatinSingleToken.IsMatch(name)) score += name.All(ch => ch is >= 'A' and <= 'Z') ? 20 : 15;
            else score += 10;
            score += Math.Min(24, (directories.Count - index - 1) * 8);
            if (best is null || score > best.Value.Score) best = (index, name, score);
        }
        return best is null ? null : (best.Value.Index, best.Value.Name);
    }

    static List<string>? RootedDirectories(string? videoPath, IEnumerable<string>? libraryRoots)
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
        var directories = new List<string>();
        var current = directory;
        for (var depth = 0; depth < 64 && !current.Equals(boundary, StringComparison.OrdinalIgnoreCase); depth++)
        {
            directories.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || !IsInside(parent, boundary)) break;
            current = Path.TrimEndingDirectorySeparator(parent);
        }
        directories.Reverse();
        return directories;
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
        if (leaf.Length is < 2 or > 32 || GenericFolders.Contains(leaf) || EnglishCategorySuffix.IsMatch(leaf) || QualityOrCollection.IsMatch(leaf)
            || CodeOrDate.IsMatch(leaf) || !LettersOnly.IsMatch(leaf)) return null;

        // East Asian names can be one compact token. A distinctive all-caps or
        // camel-cased Latin creator name can also stand alone; plain English
        // category words remain excluded unless under a person container.
        if (CjkLetters.IsMatch(leaf)) return leaf.Length <= 16 ? leaf : null;
        var words = Regex.Split(leaf, @"[\s._·・-]+", RegexOptions.None).Where(word => word.Length > 0).ToArray();
        if (words.Length is >= 2 and <= 4 && words.All(word => word.Length is >= 2 and <= 20)) return leaf;
        if (words.Length == 1 && LatinSingleToken.IsMatch(leaf) &&
            (leaf.All(ch => ch is >= 'A' and <= 'Z') || InternalCapital.IsMatch(leaf))) return leaf;
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
