using System.IO;

namespace YinggeVideoLibrary;

internal static class DmhyAnimeReleaseDiagnostics
{
    const string SampleRss = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0"><channel>
          <item>
            <title><![CDATA[【喵萌奶茶屋】★01月新番★[葬送的芙莉蓮 / Sousou no Frieren][29-38][BDRip][1080p][繁日雙語]]]></title>
            <link>http://share.dmhy.org/topics/view/729092_01_Sousou_no_Frieren_29-38_BDRip_1080p.html</link>
            <pubDate>Sat, 17 Oct 2026 23:42:42 +0800</pubDate>
            <category><![CDATA[季度全集]]></category>
            <description><![CDATA[<a href="https://untrusted.example.test">ignored</a>]]></description>
            <enclosure url="magnet:?xt=urn:btih:UNTRUSTED" type="application/x-bittorrent" />
          </item>
          <item>
            <title>[Group][Other Anime][01][720p]</title>
            <link>https://share.dmhy.org.evil.test/topics/view/729093_Other_Anime.html</link>
            <category>動畫</category>
          </item>
          <item>
            <title>[Manga Group][Manga Title][01]</title>
            <link>https://share.dmhy.org/topics/view/729094_Manga_Title.html</link>
            <category>漫畫</category>
          </item>
        </channel></rss>
        """;

    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        var items = DmhyAnimeReleaseScraper.ParseRss(SampleRss);
        var release = items.Single();
        var noResults = DmhyAnimeReleaseScraper.ParseRss("<rss><channel /></rss>").Count == 0;
        // Items that lost their title or link mean the feed structure moved; an
        // empty channel above is the only legitimate "no releases" answer.
        var brokenItemsFail = ThrowsInvalidData("""
            <rss><channel><item><category>動畫</category></item>
            <item><title>Anime</title><category>動畫</category></item></channel></rss>
            """);
        var dtdFails = ThrowsInvalidData("""
            <!DOCTYPE rss [<!ENTITY external SYSTEM "file:///C:/Windows/win.ini">]>
            <rss><channel><item><title>&external;</title></item></channel></rss>
            """);
        var htmlFails = ThrowsInvalidData("<html><body>Checking your browser</body></html>");
        var badPathFails = ThrowsInvalidData("""
            <rss><channel><item><title>Anime</title><link>https://share.dmhy.org/topics/view/123.html</link>
            <category>動畫</category></item></channel></rss><trailing />
            """);
        var fileRejected = false;
        try { _ = DmhyAnimeReleaseScraper.NormalizeQuery("C:\\videos\\anime.mkv"); }
        catch (ArgumentException) { fileRejected = true; }
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["dmhyRssIsReleaseEvidenceOnly"] = items.Count == 1 &&
                release.ReleaseTitle.Contains("29-38", StringComparison.Ordinal) &&
                release.WorkTitle == "葬送的芙莉蓮" && release.Season is null &&
                release.Episode == "29-38" && release.SubtitleGroup == "喵萌奶茶屋" &&
                release.Resolution == "1080p" && release.PublishedAt?.Year == 2026 &&
                release.SourceUrl == "https://share.dmhy.org/topics/view/729092_01_Sousou_no_Frieren_29-38_BDRip_1080p.html" &&
                !release.ToString().Contains("magnet:", StringComparison.OrdinalIgnoreCase) &&
                !release.ToString().Contains("untrusted.example.test", StringComparison.OrdinalIgnoreCase),
            ["dmhyRssRejectsUnsafeXmlAndHostilePages"] = dtdFails && htmlFails && badPathFails &&
                noResults && brokenItemsFail &&
                !DmhyAnimeReleaseScraper.TryPageUrl("https://share.dmhy.org.evil.test/topics/view/1_a.html", out _) &&
                !DmhyAnimeReleaseScraper.TryPageUrl("https://user@share.dmhy.org/topics/view/1_a.html", out _) &&
                !DmhyAnimeReleaseScraper.TryPageUrl("https://share.dmhy.org/topics/view/1_a.html?x=1", out _) &&
                !DmhyAnimeReleaseScraper.TryPageUrl("magnet:?xt=urn:btih:123", out _) &&
                fileRejected,
            // The same feed mixes half-width and full-width ASCII in RAW; all
            // three spellings must reach the allowlist.
            ["dmhyCategoryNormalizesFullWidthRaw"] = FullWidthCategoryVariants(),
            ["animeReleaseTitleCleansGroupSeasonEpisodeAndQuality"] =
                AnimeReleaseTitleParser.CleanWorkTitle("[Shiniori-Raws]葬送的芙莉莲 第二季/Sousou no Frieren S2 4k 2160p x265 10bit") == "葬送的芙莉莲" &&
                AnimeReleaseTitleParser.Analyze("[Shiniori-Raws]葬送的芙莉莲 第二季/Sousou no Frieren S2 4k 2160p x265 10bit").Season == 2 &&
                AnimeReleaseTitleParser.CleanWorkTitle("[ANi] 勇者处刑 惩罚勇者9004队服刑记录 - 01 [1080P].mkv") == "勇者处刑 惩罚勇者9004队服刑记录" &&
                AnimeReleaseTitleParser.CleanWorkTitle("[Fate/stay night][01][1080p]") == "Fate/stay night" &&
                AnimeReleaseTitleParser.Analyze("[Group] My Anime S01E08 [720p]").Episode == "08" &&
                AnimeReleaseTitleParser.CleanWorkTitle("[jibaketa合成][代理商粵語]葬送的芙莉蓮 第二季 / Sousou no Frieren 2nd Season - 10 END") == "葬送的芙莉蓮" &&
                AnimeReleaseTitleParser.Analyze("[Xspitfire911] 葬送的芙莉莲 S01 + S02 BDRIP 1080p").Season is null
        };
        AddCapturedFeedRegression(checks);
        return checks;
    }

    // Half-width RAW, full-width ＲＡＷ and the mixed RＡＷ all have to be
    // accepted, and a non-anime category still has to be rejected.
    static bool FullWidthCategoryVariants()
    {
        static string Item(int id, string category, string title) => $"""
            <item><title>{title}</title>
            <link>https://share.dmhy.org/topics/view/{id}_raw.html</link>
            <category>{category}</category></item>
            """;
        var xml = "<rss><channel>" +
            Item(1, "RAW", "[Group] Anime A [01][1080p]") +
            Item(2, "ＲＡＷ", "[Group] Anime B [01][1080p]") +
            Item(3, "RＡＷ", "[Group] Anime C [01][1080p]") +
            Item(4, "漫畫", "[Group] Manga D [01]") +
            "</channel></rss>";
        var parsed = DmhyAnimeReleaseScraper.ParseRss(xml);
        return parsed.Count == 3 && parsed.Select(item => item.Category).SequenceEqual(["RAW", "ＲＡＷ", "RＡＷ"]);
    }

    // Optional offline regression against a captured 500-item feed. Enabled only
    // when YINGGE_DMHY_RSS_FIXTURE points at the snapshot, so the default self
    // test stays hermetic. The captured channel holds 457 動畫, 32 季度全集 and
    // 11 full-width ＲＡＷ items; before the normalization the 11 ＲＡＷ items were
    // dropped by the category allowlist. The remaining 21 of 500 never yield a
    // series title from the release name (pre-existing title-parser behaviour,
    // unrelated to categories), hence 479 kept with all 11 ＲＡＷ recovered.
    static void AddCapturedFeedRegression(IDictionary<string, bool> checks)
    {
        var path = Environment.GetEnvironmentVariable("YINGGE_DMHY_RSS_FIXTURE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var xml = File.ReadAllText(path);
        var kept = DmhyAnimeReleaseScraper.ParseRss(xml, 5_000);
        checks["dmhyCapturedFeedKeepsAllFullWidthRawItems"] =
            kept.Count(item => item.Category == "ＲＡＷ") == 11 && kept.Count == 479;
    }

    static bool ThrowsInvalidData(string xml)
    {
        try { _ = DmhyAnimeReleaseScraper.ParseRss(xml); return false; }
        catch (InvalidDataException) { return true; }
    }
}
