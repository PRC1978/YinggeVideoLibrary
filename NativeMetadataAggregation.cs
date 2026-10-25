using System.Globalization;
using System.Text;

namespace YinggeVideoLibrary;

// A source's verified detail remains evidence, not permission to silently
// replace a value reported by a different source or a human reviewer.
public sealed class MetadataSourceObservation
{
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Stage { get; set; } = "";

    public MetadataSourceObservation Clone() => new()
    {
        Field = Field, Value = Value, Source = Source,
        SourceUrl = SourceUrl, Stage = Stage
    };
}

internal static class NativeMetadataAggregation
{
    internal static List<MovieMetadata> OrderByTrust(IEnumerable<MovieMetadata> details, Func<string, int> sourceTrust)
        => details.OrderByDescending(item => sourceTrust(item.Source))
            .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SourceUrl, StringComparer.OrdinalIgnoreCase).ToList();

    // Scores, images, summaries and taxonomy are source-specific by nature.
    // Preserve all of their observations, but do not present normal variation
    // as an identity conflict that blocks every multi-source match.
    static readonly HashSet<string> IdentityFields =
    [
        nameof(MovieMetadata.ReleaseDate), nameof(MovieMetadata.RuntimeMinutes),
        nameof(MovieMetadata.Director), nameof(MovieMetadata.Studio),
        nameof(MovieMetadata.Maker), nameof(MovieMetadata.Label),
        nameof(MovieMetadata.Series), nameof(MovieMetadata.Country),
        nameof(MovieMetadata.Language), nameof(MovieMetadata.Actors)
    ];

    static readonly string[] ObservedFields =
    [
        nameof(MovieMetadata.Code), nameof(MovieMetadata.Title), nameof(MovieMetadata.OriginalTitle),
        nameof(MovieMetadata.ReleaseDate), nameof(MovieMetadata.RuntimeMinutes),
        nameof(MovieMetadata.Director), nameof(MovieMetadata.Studio),
        nameof(MovieMetadata.Maker), nameof(MovieMetadata.Label), nameof(MovieMetadata.Series),
        nameof(MovieMetadata.Country), nameof(MovieMetadata.Language), nameof(MovieMetadata.Actors),
        nameof(MovieMetadata.Genres), nameof(MovieMetadata.Keywords), nameof(MovieMetadata.Rating),
        nameof(MovieMetadata.Summary), nameof(MovieMetadata.PosterUrl),
        nameof(MovieMetadata.PreviewImageUrls), nameof(MovieMetadata.TrailerUrl)
    ];

    internal static MovieMetadata AggregateExactCode(string requestedCode, IEnumerable<MovieMetadata> details)
    {
        var requestedKey = ScrapeCodeIdentity.Key(requestedCode);
        if (requestedKey.Length < 4) throw new ArgumentException("番号无效。", nameof(requestedCode));
        var verified = details.Where(detail => detail is not null && IsNativeSource(detail.Source)
                && detail.DetailFetched && detail.DetailIdentityVerified
                && ScrapeCodeIdentity.Key(detail.Code).Equals(requestedKey, StringComparison.Ordinal))
            .GroupBy(detail => RatingAggregation.CanonicalPlatform(detail.Source), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        if (verified.Count == 0) throw new InvalidOperationException("没有身份和番号均已核对的内置来源详情。");

        // The first verified source remains primary. Secondary values only fill
        // blanks. Every supplied value is retained for offline comparison.
        var merged = verified[0].Clone();
        if (!JavDatabaseScraper.HasVerifiedMediaEvidence(merged, nameof(MovieMetadata.PreviewImageUrls)))
        {
            merged.PreviewImageUrls = [];
            merged.FieldProvenance.Remove(nameof(MovieMetadata.PreviewImageUrls));
        }
        if (!JavDatabaseScraper.HasVerifiedMediaEvidence(merged, nameof(MovieMetadata.TrailerUrl)))
        {
            merged.TrailerUrl = null;
            merged.FieldProvenance.Remove(nameof(MovieMetadata.TrailerUrl));
        }
        merged.SourceObservations = [];
        merged.SourceConflictFields = [];
        foreach (var detail in verified)
        {
            if (merged.CatalogLinks.Count == 0 && detail.CatalogLinks is { Count: > 0 })
                merged.CatalogLinks = detail.CatalogLinks.Select(link => link.Clone()).ToList();
            foreach (var field in ObservedFields)
            {
                var value = ReadField(detail, field);
                if (string.IsNullOrWhiteSpace(value)) continue;
                MetadataFieldProvenance? provenance = null;
                detail.FieldProvenance?.TryGetValue(field, out provenance);
                merged.SourceObservations.Add(new MetadataSourceObservation
                {
                    Field = field, Value = value, Source = detail.Source,
                    SourceUrl = detail.SourceUrl,
                    Stage = provenance?.Stage ?? "详情页"
                });
                if (ReferenceEquals(detail, verified[0]) || !string.IsNullOrWhiteSpace(ReadField(merged, field))) continue;
                CopyField(detail, merged, field);
                merged.FieldProvenance[field] = provenance?.Clone() ?? new MetadataFieldProvenance
                {
                    Source = detail.Source, SourceUrl = detail.SourceUrl, Stage = "详情页"
                };
            }
            foreach (var rating in RatingAggregation.DistinctValidRatings(detail.PlatformRatings))
                RatingAggregation.AddOrUpdate(merged, rating);
            if (RatingAggregation.FromMetadata(detail, "已核对相同番号") is { } rawRating)
                RatingAggregation.AddOrUpdate(merged, rawRating);
        }
        merged.Poster = verified.FirstOrDefault(detail =>
            string.Equals(detail.PosterUrl, merged.PosterUrl, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(detail.Poster))?.Poster;
        merged.SourceConflictFields = merged.SourceObservations
            .Where(item => IdentityFields.Contains(item.Field))
            .GroupBy(item => item.Field, StringComparer.Ordinal)
            .Where(group => group.Select(item => ComparisonKey(item.Field, item.Value))
                .Distinct(StringComparer.Ordinal).Take(2).Count() > 1)
            .Select(group => group.Key).OrderBy(field => field, StringComparer.Ordinal).ToList();
        // Titles commonly differ only because one catalog translates them.
        // Compare Japanese names only when two sources actually supply one;
        // English/Chinese display titles still remain visible as observations.
        var japaneseNames = verified.Select(item =>
            ContainsKana(item.OriginalTitle) ? item.OriginalTitle :
            ContainsKana(item.Title) ? item.Title : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Normalize(value!)).Distinct(StringComparer.Ordinal).ToList();
        if (japaneseNames.Count > 1)
            merged.SourceConflictFields.Add(verified.Any(item => ContainsKana(item.OriginalTitle))
                ? nameof(MovieMetadata.OriginalTitle) : nameof(MovieMetadata.Title));
        merged.DetailNote = verified.Count == 1
            ? verified[0].DetailNote
            : $"已核对相同番号，独立读取 {string.Join("、", verified.Select(item => item.Source))} 详情；" +
              (merged.SourceConflictFields.Count == 0
                  ? "来源字段无身份冲突，可在多来源对照中查看各站原值"
                  : $"{merged.SourceConflictFields.Count} 项字段存在差异，须人工核对后采用");
        return merged;
    }

    internal static bool RequiresManualReview(MovieMetadata candidate, AiReviewRecord? previousReview)
        => candidate.SourceConflictFields?.Count > 0 ||
           (previousReview is { DecisionSource: "manual" } && previousReview.Status is "approved" or "rejected");

    internal static IReadOnlyList<MetadataSourceObservation> FieldObservations(MovieMetadata metadata, string field)
        => (metadata.SourceObservations ?? []).Where(item => item.Field == field)
            .Select(item => item.Clone()).ToList();

    internal static bool IsNativeSource(string source) => source is "JavDB" or "JAVDatabase";

    internal static bool HasExactCode(MovieMetadata metadata, string requestedCode)
        => ScrapeCodeIdentity.Same(metadata.Code, requestedCode);

    static string ComparisonKey(string field, string value)
    {
        if (field == nameof(MovieMetadata.Actors))
            return string.Join("|", value.Split('、', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize).Order(StringComparer.Ordinal));
        if (field == nameof(MovieMetadata.RuntimeMinutes) && int.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var minutes)) return minutes.ToString(CultureInfo.InvariantCulture);
        if (field == nameof(MovieMetadata.ReleaseDate) && DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Normalize(value);
    }

    static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) result.Append(char.ToUpperInvariant(c));
        return result.ToString();
    }

    static bool ContainsKana(string? value) => value?.Any(c =>
        c is >= '\u3040' and <= '\u30ff' or >= '\uff66' and <= '\uff9f') == true;

    static string? ReadField(MovieMetadata metadata, string field) => field switch
    {
        nameof(MovieMetadata.Code) => metadata.Code,
        nameof(MovieMetadata.Title) => metadata.Title,
        nameof(MovieMetadata.OriginalTitle) => metadata.OriginalTitle,
        nameof(MovieMetadata.ReleaseDate) => metadata.ReleaseDate,
        nameof(MovieMetadata.RuntimeMinutes) => metadata.RuntimeMinutes?.ToString(CultureInfo.InvariantCulture),
        nameof(MovieMetadata.Director) => metadata.Director,
        nameof(MovieMetadata.Studio) => metadata.Studio,
        nameof(MovieMetadata.Maker) => metadata.Maker,
        nameof(MovieMetadata.Label) => metadata.Label,
        nameof(MovieMetadata.Series) => metadata.Series,
        nameof(MovieMetadata.Country) => metadata.Country,
        nameof(MovieMetadata.Language) => metadata.Language,
        nameof(MovieMetadata.Actors) => string.Join("、", metadata.Actors ?? []),
        nameof(MovieMetadata.Genres) => string.Join("、", metadata.Genres ?? []),
        nameof(MovieMetadata.Keywords) => string.Join("、", metadata.Keywords ?? []),
        nameof(MovieMetadata.Rating) => metadata.Rating,
        nameof(MovieMetadata.Summary) => metadata.Summary,
        nameof(MovieMetadata.PosterUrl) => metadata.PosterUrl,
        nameof(MovieMetadata.PreviewImageUrls) => JavDatabaseScraper.HasVerifiedMediaEvidence(metadata, field)
            ? string.Join("\n", metadata.PreviewImageUrls) : null,
        nameof(MovieMetadata.TrailerUrl) => JavDatabaseScraper.HasVerifiedMediaEvidence(metadata, field)
            ? metadata.TrailerUrl : null,
        _ => null
    };

    static void CopyField(MovieMetadata source, MovieMetadata target, string field)
    {
        switch (field)
        {
            case nameof(MovieMetadata.Title): target.Title = source.Title; break;
            case nameof(MovieMetadata.OriginalTitle): target.OriginalTitle = source.OriginalTitle; break;
            case nameof(MovieMetadata.ReleaseDate): target.ReleaseDate = source.ReleaseDate; break;
            case nameof(MovieMetadata.RuntimeMinutes): target.RuntimeMinutes = source.RuntimeMinutes; break;
            case nameof(MovieMetadata.Director): target.Director = source.Director; break;
            case nameof(MovieMetadata.Studio): target.Studio = source.Studio; break;
            case nameof(MovieMetadata.Maker): target.Maker = source.Maker; break;
            case nameof(MovieMetadata.Label): target.Label = source.Label; break;
            case nameof(MovieMetadata.Series): target.Series = source.Series; break;
            case nameof(MovieMetadata.Country): target.Country = source.Country; break;
            case nameof(MovieMetadata.Language): target.Language = source.Language; break;
            case nameof(MovieMetadata.Actors): target.Actors = source.Actors?.ToList() ?? []; break;
            case nameof(MovieMetadata.Genres): target.Genres = source.Genres?.ToList() ?? []; break;
            case nameof(MovieMetadata.Keywords): target.Keywords = source.Keywords?.ToList() ?? []; break;
            case nameof(MovieMetadata.Rating): target.Rating = source.Rating; break;
            case nameof(MovieMetadata.Summary): target.Summary = source.Summary; break;
            case nameof(MovieMetadata.PosterUrl): target.PosterUrl = source.PosterUrl; break;
            case nameof(MovieMetadata.PreviewImageUrls):
                if (JavDatabaseScraper.HasVerifiedMediaEvidence(source, field))
                    target.PreviewImageUrls = source.PreviewImageUrls.ToList();
                break;
            case nameof(MovieMetadata.TrailerUrl):
                if (JavDatabaseScraper.HasVerifiedMediaEvidence(source, field))
                    target.TrailerUrl = source.TrailerUrl;
                break;
        }
    }
}
