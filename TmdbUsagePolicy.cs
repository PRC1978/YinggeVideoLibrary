using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// TMDB's API terms prohibit using its data or images in connection with
// pornographic material. A title match is not evidence of an ordinary work.
// Unknown library items are deliberately routed to the other enabled sources.
internal static class TmdbUsagePolicy
{
    internal readonly record struct Decision(bool Allowed, string Reason);

    static readonly Regex AdultCue = new(
        @"(?:成人|色情|成人视频|番号|无码|無碼|有码|有碼|女优|女優|素人|东京热|東京熱|麻豆|91大神|里番|裏番|18禁|性爱|性愛|性交)" +
        @"|(?<![A-Za-z0-9])(?:av|porn(?:hub)?|jav(?:db)?|xvideos|xhamster|onlyfans|hentai|xxx|sex|nude|naked|hardcore|amateur|r[-_]?18|fc2(?:[-_ ]?ppv)?|1pondo|caribbeancom|heyzo)(?![A-Za-z0-9])" +
        @"|(?<![A-Za-z0-9])(?:abp|ipx|ssni|ssis|mide|midv|pred|juq|dvaj|fsdss|miaa|hmn|ntr)[-_ ]?\d{2,8}(?![A-Za-z0-9])" +
        @"|(?<![A-Za-z0-9])[A-Za-z]{2,10}[-_]\d{2,8}(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // Compact catalogue numbers are intentionally case-sensitive here: a
    // normal title such as Movie2020 is not enough to classify as a number.
    static readonly Regex CompactCode = new(@"(?<![A-Za-z0-9])[A-Z]{2,10}\d{2,8}(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly HashSet<string> OrdinaryCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "电影", "劇集", "剧集", "电视剧", "普通电影", "普通剧集", "普通电视剧",
        "movies", "films", "tv shows", "tv series"
    };

    internal static Decision Evaluate(VideoItem? video, MovieMetadata? saved, string? query)
    {
        if (video is null) return new(false, "缺少本地视频上下文，不能向 TMDB 发送标题");
        if (saved?.IsAdultContent == true || IsAdultSource(saved?.Source) ||
            (saved?.SourceObservations?.Any(item => item is null || IsAdultSource(item.Source)) ?? false))
            return new(false, "已有资料表明这是成人来源或成人作品");
        if (HasAdultCue(video.Title) || HasAdultCue(video.Code) || HasAdultCue(video.Path) || HasAdultCue(video.Category) ||
            video.CategoryList.Any(HasAdultCue) || video.Tags.Any(HasAdultCue) ||
            HasAdultCue(query) || HasAdultCue(saved?.Code) ||
            (saved?.Genres?.Any(HasAdultCue) ?? false) ||
            (saved?.Keywords?.Any(HasAdultCue) ?? false))
            return new(false, "标题、目录、标签、分类或已有资料包含成人/番号标识");
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return new(false, "缺少可核对的普通影视标题");
        if (HasOrdinaryCategory(video) || IsVerifiedOrdinarySource(saved))
            return new(true, "已明确分类为普通电影/剧集，或已有核实的普通影视来源");
        return new(false, "尚未明确分类为普通电影/剧集；类型不明的视频不自动请求 TMDB");
    }

    internal static bool IsAdultSource(string? source) => source is not null &&
        (source.Equals("JavDB", StringComparison.OrdinalIgnoreCase) ||
         source.Equals("JAVDatabase", StringComparison.OrdinalIgnoreCase) ||
         source.StartsWith("Javinfo", StringComparison.OrdinalIgnoreCase) ||
         source.Equals("Pornhub", StringComparison.OrdinalIgnoreCase));

    internal static bool HasAdultCue(string? value) => !string.IsNullOrWhiteSpace(value) &&
        (AdultCue.IsMatch(value) || CompactCode.IsMatch(value));

    static bool HasOrdinaryCategory(VideoItem video) =>
        new[] { video.Category }.Concat(video.CategoryList).Any(category =>
            category is not null && category.Split([" / ", "\\", "/"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() is { } root && OrdinaryCategories.Contains(root));

    static bool IsVerifiedOrdinarySource(MovieMetadata? saved)
    {
        if (saved is not { DetailFetched: true, DetailIdentityVerified: true } ||
            !Uri.TryCreate(saved.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        return saved.Source switch
        {
            "TMDB 电影" or "TMDB 剧集" => uri.Host.Equals("www.themoviedb.org", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath, @"^/(?:movie|tv)/[0-9]+/?$", RegexOptions.IgnoreCase),
            "TVmaze" => uri.Host.Equals("www.tvmaze.com", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath, @"^/shows/[0-9]+/?$", RegexOptions.IgnoreCase),
            "OMDb" => uri.Host.Equals("www.imdb.com", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath, @"^/title/tt[0-9]+/?$", RegexOptions.IgnoreCase),
            "Trakt" => uri.Host.Equals("trakt.tv", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath, @"^/(?:movies|shows)/[A-Za-z0-9-]+/?$", RegexOptions.IgnoreCase),
            _ => false
        };
    }
}
