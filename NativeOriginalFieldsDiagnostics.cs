using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class NativeOriginalFieldsDiagnostics
{
    static readonly MovieMetadata JavDbCandidate = new()
    {
        Code = "ABC-123", Title = "搜索摘要", Source = "JavDB", SourceUrl = "https://javdb.com/v/example"
    };

    static readonly MovieMetadata JavDatabaseCandidate = new()
    {
        Code = "ABC-123", Title = "Search summary", Source = "JAVDatabase",
        SourceUrl = "https://www.javdatabase.com/movies/ABC-123/"
    };

    static string JavDbHtml(string headingCode = "ABC-123", string panelCode = "ABC-123",
        string headingTitle = "朝の作品名", bool includeLabel = true) =>
        $"<h2 class='title is-4'><strong>{headingCode}</strong><strong class='current-title'>{headingTitle}</strong></h2>" +
        $"<div class='panel-block'><strong>番號:</strong><span class='value'>{(panelCode == "ABC-123" ? "<a href='/video_codes/ABC'>ABC</a>-123" : panelCode)}</span></div>" +
        "<div class='panel-block'><strong>日期:</strong><span class='value'>2025-04-03</span></div>" +
        "<div class='panel-block'><strong>片商:</strong><span class='value'><a>制作方甲</a></span></div>" +
        (includeLabel ? "<div class='panel-block'><strong>廠牌:</strong><span class='value'><a>厂牌乙</a></span></div>" : "") +
        "<div class='panel-block'><strong>發行商:</strong><span class='value'><a>发行方丙</a></span></div>";

    static string JavDatabaseHtml(bool explicitJapaneseTitle = true, bool explicitMakerLabel = true) =>
        "<html><h1>ABC-123 English display title</h1>" +
        "<p class='mb-1'><b>DVD ID: </b>ABC-123</p>" +
        "<p class='mb-1'><b>Title: </b>English display title</p>" +
        (explicitJapaneseTitle ? "<p class='mb-1'><b>Japanese Title: </b>朝の作品名</p>" : "") +
        "<p class='mb-1'><b>Studio: </b>发行方丙</p>" +
        (explicitMakerLabel ? "<p class='mb-1'><b>Maker: </b>制作方甲</p>" +
             "<p class='mb-1'><b>Label: </b>厂牌乙</p>" : "") +
        new string('x', 550) + "</html>";

    internal static Dictionary<string, bool> Verify()
    {
        var javDb = JavDbScraper.ParseJavDbDetails(JavDbCandidate, JavDbHtml());
        var noLabel = JavDbScraper.ParseJavDbDetails(JavDbCandidate, JavDbHtml(includeLabel: false));
        var translated = JavDbScraper.ParseJavDbDetails(JavDbCandidate, JavDbHtml(headingTitle: "English title"));
        var wrongHeading = JavDbScraper.ParseJavDbDetails(JavDbCandidate, JavDbHtml(headingCode: "ABC-124"));
        var wrongNumber = JavDbScraper.ParseJavDbDetails(JavDbCandidate, JavDbHtml(panelCode: "ABC-124"));
        var javDatabase = JavDatabaseScraper.ParseDetail(JavDatabaseCandidate, JavDatabaseHtml());
        var ordinaryDatabase = JavDatabaseScraper.ParseDetail(JavDatabaseCandidate,
            JavDatabaseHtml(explicitJapaneseTitle: false, explicitMakerLabel: false));
        var aggregate = NativeMetadataAggregation.AggregateExactCode("ABC-123", [javDb, javDatabase]);
        var approved = ReviewFieldApproval.Build(null, javDb, new Dictionary<string, ReviewFieldSelection>());
        var roundTrip = JsonSerializer.Deserialize<MovieMetadata>(JsonSerializer.Serialize(aggregate));
        return new Dictionary<string, bool>
        {
            ["nativeJapaneseTitleRequiresTwoCodeChecks"] = javDb.DetailFetched && javDb.DetailIdentityVerified
                && javDb.OriginalTitle == "朝の作品名" && wrongHeading.OriginalTitle is null
                && wrongNumber.OriginalTitle is null && !wrongNumber.DetailFetched,
            ["nativeLatinHeadingNotInventedJapanese"] = translated.OriginalTitle is null,
            ["nativeMakerLabelPublisherIndependent"] = javDb.Maker == "制作方甲" && javDb.Label == "厂牌乙"
                && javDb.Studio == "发行方丙" && javDb.ReleaseDate == "2025-04-03" && noLabel.Label is null,
            ["nativeExplicitFieldsProvenance"] = javDb.FieldProvenance[nameof(MovieMetadata.OriginalTitle)].Source == "JavDB"
                && javDb.FieldProvenance[nameof(MovieMetadata.Maker)].Stage == "详情页"
                && javDb.FieldProvenance[nameof(MovieMetadata.Label)].SourceUrl == JavDbCandidate.SourceUrl,
            ["nativeDatabaseOnlyExplicitOriginalMakerLabel"] = javDatabase.OriginalTitle == "朝の作品名"
                && javDatabase.Maker == "制作方甲" && javDatabase.Label == "厂牌乙"
                && ordinaryDatabase.OriginalTitle is null && ordinaryDatabase.Maker is null
                && ordinaryDatabase.Label is null && ordinaryDatabase.Studio == "发行方丙",
            ["nativeFieldReviewAndAggregation"] = ReviewFieldApproval.Fields.Any(field => field.Key == nameof(MovieMetadata.Maker))
                && ReviewFieldApproval.Fields.Any(field => field.Key == nameof(MovieMetadata.Label))
                && approved.Maker == javDb.Maker && approved.Label == javDb.Label
                && aggregate.Maker == javDb.Maker && aggregate.Label == javDb.Label
                && aggregate.SourceObservations.Any(item => item.Field == nameof(MovieMetadata.Maker) && item.Source == "JAVDatabase"),
            ["nativeFieldsSurviveSerialization"] = roundTrip?.OriginalTitle == "朝の作品名"
                && roundTrip.Maker == "制作方甲" && roundTrip.Label == "厂牌乙"
                && roundTrip.FieldProvenance.ContainsKey(nameof(MovieMetadata.Label))
        };
    }
}
