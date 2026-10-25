using System.Globalization;
using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal enum ReviewFieldAction { Keep, Adopt, Clear, Source }

internal sealed record ReviewFieldSelection(ReviewFieldAction Action, MetadataSourceObservation? Observation = null);

internal sealed record ReviewFieldDefinition(string Key, string Label);

// A review owns the exact choices displayed in its preview. This class has no
// UI or file I/O, so the same builder is used for preview, commit and tests.
internal static class ReviewFieldApproval
{
    internal static readonly IReadOnlyList<ReviewFieldDefinition> Fields =
    [
        new(nameof(MovieMetadata.Code), "番号 / 编号"),
        new(nameof(MovieMetadata.Title), "标题"),
        new(nameof(MovieMetadata.OriginalTitle), "原始标题"),
        new(nameof(MovieMetadata.ReleaseDate), "发行日期"),
        new(nameof(MovieMetadata.RuntimeMinutes), "时长"),
        new(nameof(MovieMetadata.Director), "导演"),
        new(nameof(MovieMetadata.Studio), "工作室 / 发行"),
        new(nameof(MovieMetadata.Maker), "片商 / 制作方"),
        new(nameof(MovieMetadata.Label), "厂牌"),
        new(nameof(MovieMetadata.Series), "系列"),
        new(nameof(MovieMetadata.Actors), "演员"),
        new(nameof(MovieMetadata.Genres), "类型 / 类别"),
        new(nameof(MovieMetadata.Keywords), "关键词"),
        new(nameof(MovieMetadata.Country), "地区"),
        new(nameof(MovieMetadata.Language), "语言"),
        new(nameof(MovieMetadata.Rating), "来源评分"),
        new(nameof(MovieMetadata.PlatformRatings), "各平台评分"),
        new(nameof(MovieMetadata.Summary), "简介"),
        new(nameof(MovieMetadata.PosterUrl), "封面"),
        new(nameof(MovieMetadata.PreviewImageUrls), "样张图片"),
        new(nameof(MovieMetadata.TrailerUrl), "预告链接"),
        new(nameof(MovieMetadata.CatalogLinks), "作品关系链接")
    ];

    internal static string Value(MovieMetadata? metadata, string key) => metadata is null ? "" : key switch
    {
        nameof(MovieMetadata.Code) => metadata.Code,
        nameof(MovieMetadata.Title) => metadata.Title,
        nameof(MovieMetadata.OriginalTitle) => metadata.OriginalTitle ?? "",
        nameof(MovieMetadata.ReleaseDate) => metadata.ReleaseDate ?? "",
        nameof(MovieMetadata.RuntimeMinutes) => metadata.RuntimeMinutes is > 0 ? metadata.RuntimeMinutes.Value.ToString(CultureInfo.InvariantCulture) : "",
        nameof(MovieMetadata.Director) => metadata.Director ?? "",
        nameof(MovieMetadata.Studio) => metadata.Studio ?? "",
        nameof(MovieMetadata.Maker) => metadata.Maker ?? "",
        nameof(MovieMetadata.Label) => metadata.Label ?? "",
        nameof(MovieMetadata.Series) => metadata.Series ?? "",
        nameof(MovieMetadata.Actors) => Join(metadata.Actors),
        nameof(MovieMetadata.Genres) => Join(metadata.Genres),
        nameof(MovieMetadata.Keywords) => Join(metadata.Keywords),
        nameof(MovieMetadata.Country) => metadata.Country ?? "",
        nameof(MovieMetadata.Language) => metadata.Language ?? "",
        nameof(MovieMetadata.Rating) => metadata.Rating ?? "",
        nameof(MovieMetadata.PlatformRatings) => string.Join("、", (metadata.PlatformRatings ?? []).Select(value =>
            $"{value.Platform} {value.Value.ToString(CultureInfo.InvariantCulture)}/{value.Maximum.ToString(CultureInfo.InvariantCulture)}")),
        nameof(MovieMetadata.Summary) => metadata.Summary ?? "",
        nameof(MovieMetadata.PosterUrl) => metadata.PosterUrl ?? "",
        nameof(MovieMetadata.PreviewImageUrls) => JavDatabaseScraper.HasVerifiedMediaEvidence(metadata, key)
            ? string.Join("\n", metadata.PreviewImageUrls) : "",
        nameof(MovieMetadata.TrailerUrl) => JavDatabaseScraper.HasVerifiedMediaEvidence(metadata, key)
            ? metadata.TrailerUrl ?? "" : "",
        nameof(MovieMetadata.CatalogLinks) => string.Join("、", (metadata.CatalogLinks ?? []).Select(value => value.DisplayName)),
        _ => ""
    };

    static string Join(IEnumerable<string>? values) => string.Join("、", values ?? []);

    internal static ReviewFieldSelection Default(string key, MovieMetadata? current, MovieMetadata candidate)
    {
        var proposed = Value(candidate, key);
        if (string.IsNullOrWhiteSpace(proposed)) return new(ReviewFieldAction.Keep);
        // A disagreement between independent sources needs an explicit human
        // choice even when there is no accepted value yet.
        if (candidate.SourceConflictFields?.Contains(key, StringComparer.Ordinal) == true)
            return new(ReviewFieldAction.Keep);
        return string.IsNullOrWhiteSpace(Value(current, key))
            ? new(ReviewFieldAction.Adopt)
            : new(ReviewFieldAction.Keep);
    }

    internal static IReadOnlyList<MetadataSourceObservation> Options(MovieMetadata candidate, string key) =>
        (candidate.SourceObservations ?? [])
            .Where(item => item.Field == key && !string.IsNullOrWhiteSpace(item.Value) &&
                (!IsMediaField(key) || ValidMediaObservation(candidate, item)))
            .GroupBy(item => (item.Source, item.Value))
            .Select(group => group.First().Clone()).ToList();

    static bool IsMediaField(string key) => key is nameof(MovieMetadata.PreviewImageUrls) or nameof(MovieMetadata.TrailerUrl);

    internal static bool ValidMediaObservation(MovieMetadata candidate, MetadataSourceObservation item)
    {
        if (!candidate.DetailFetched || !candidate.DetailIdentityVerified || item.Source != "JAVDatabase" ||
            !JavDatabaseScraper.IsVerifiedDetailSource(candidate.Code, item.SourceUrl)) return false;
        if (item.Field == nameof(MovieMetadata.TrailerUrl)) return JavDatabaseScraper.IsTrustedTrailerUrl(item.Value);
        var urls = item.Value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return urls.Length is > 0 and <= 48 && urls.All(JavDatabaseScraper.IsTrustedPreviewUrl);
    }

    internal static MovieMetadata Build(MovieMetadata? current, MovieMetadata candidate,
        IReadOnlyDictionary<string, ReviewFieldSelection> choices)
    {
        var result = current?.Clone() ?? new MovieMetadata
        {
            Source = candidate.Source, SourceUrl = candidate.SourceUrl,
            DetailFetched = candidate.DetailFetched,
            DetailIdentityVerified = candidate.DetailIdentityVerified,
            DetailNote = candidate.DetailNote,
            ScrapedAt = candidate.ScrapedAt
        };
        result.FieldProvenance ??= [];
        var adopted = 0;
        var actorsAction = ReviewFieldAction.Keep;
        var mixedSourceChosen = false;
        foreach (var field in Fields)
        {
            if (candidate.SourceConflictFields?.Contains(field.Key, StringComparer.Ordinal) == true &&
                !choices.ContainsKey(field.Key))
                throw new InvalidDataException($"字段 {field.Label} 存在来源冲突，请明确选择采用、保留或不采用。");
            var choice = choices.TryGetValue(field.Key, out var selected)
                ? selected : Default(field.Key, current, candidate);
            if (field.Key == nameof(MovieMetadata.Actors)) actorsAction = choice.Action;
            if (choice.Action == ReviewFieldAction.Keep) continue;
            if (choice.Action == ReviewFieldAction.Source &&
                (choice.Observation is null || !Options(candidate, field.Key).Any(item =>
                    item.Source == choice.Observation.Source && item.Value == choice.Observation.Value &&
                    item.SourceUrl == choice.Observation.SourceUrl)))
                throw new InvalidDataException($"字段 {field.Label} 的来源值不属于当前候选。请重新预览。");
            if (choice.Action == ReviewFieldAction.Source &&
                (!string.Equals(choice.Observation!.Source, candidate.Source, StringComparison.Ordinal) ||
                 !string.Equals(choice.Observation.SourceUrl, candidate.SourceUrl, StringComparison.Ordinal)))
                mixedSourceChosen = true;
            if (choice.Action == ReviewFieldAction.Adopt && string.IsNullOrWhiteSpace(Value(candidate, field.Key)))
                throw new InvalidDataException($"字段 {field.Label} 没有可采用的候选值。");
            if (field.Key == nameof(MovieMetadata.RuntimeMinutes) && choice.Action == ReviewFieldAction.Source &&
                (!int.TryParse(choice.Observation!.Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var observedMinutes) || observedMinutes <= 0))
                throw new InvalidDataException("候选来源的时长不是有效分钟数，请保留原值或选择其他来源。");
            if (IsMediaField(field.Key) && choice.Action != ReviewFieldAction.Clear &&
                (!candidate.DetailFetched || !candidate.DetailIdentityVerified ||
                 !ScrapeCodeIdentity.Same(result.Code, candidate.Code) ||
                 (choice.Action == ReviewFieldAction.Adopt &&
                  !JavDatabaseScraper.HasVerifiedMediaEvidence(candidate, field.Key))))
                throw new InvalidDataException($"字段 {field.Label} 没有核对过作品身份或可信来源，不能采用。");
            Assign(result, field.Key, choice.Action == ReviewFieldAction.Clear ? null :
                choice.Action == ReviewFieldAction.Source ? choice.Observation!.Value : Value(candidate, field.Key),
                choice.Action == ReviewFieldAction.Adopt ? candidate : null);
            if (choice.Action == ReviewFieldAction.Clear) result.FieldProvenance.Remove(field.Key);
            else
            {
                var provenance = choice.Action == ReviewFieldAction.Source
                    ? new MetadataFieldProvenance { Source = choice.Observation!.Source,
                        SourceUrl = choice.Observation.SourceUrl, Stage = choice.Observation.Stage }
                    : candidate.FieldProvenance?.GetValueOrDefault(field.Key)?.Clone() ??
                      new MetadataFieldProvenance { Source = candidate.Source, SourceUrl = candidate.SourceUrl, Stage = "人工审核候选" };
                result.FieldProvenance[field.Key] = provenance;
                adopted++;
            }
        }
        if (string.IsNullOrWhiteSpace(result.Title))
            throw new InvalidDataException("审批后的标题为空。请选择标题来源或保留原有标题。");
        // An existing record from the exact same verified work is still that
        // work after field review. Only a genuinely mixed source loses the
        // single-source identity needed to retain person credits.
        var sameCandidateWork = current is null ||
            string.Equals(current.Source, candidate.Source, StringComparison.Ordinal) &&
            string.Equals(current.SourceUrl, candidate.SourceUrl, StringComparison.Ordinal);
        if (adopted > 0)
        {
            var oneSourceWork = sameCandidateWork && !mixedSourceChosen;
            result.Source = oneSourceWork ? candidate.Source : "人工审核组合";
            result.SourceUrl = oneSourceWork ? candidate.SourceUrl : "";
            result.ScrapedAt = candidate.ScrapedAt;
            result.DetailFetched |= candidate.DetailFetched;
            result.DetailIdentityVerified = oneSourceWork && candidate.DetailFetched &&
                candidate.DetailIdentityVerified &&
                string.Equals(result.Code, candidate.Code, StringComparison.OrdinalIgnoreCase);
            result.DetailNote = "已逐字段审核；各字段出处见资料详情";
            result.SourceObservations = (current?.SourceObservations ?? [])
                .Concat(candidate.SourceObservations ?? [])
                .GroupBy(item => (item.Field, item.Value, item.Source, item.SourceUrl))
                .Select(group => group.First().Clone()).ToList();
        }
        // A name list alone is not an identity. Preserve cast IDs only when
        // the selected actor field belongs to the same independently checked
        // work and the final record still points to that exact work.
        var creditSource = actorsAction switch
        {
            ReviewFieldAction.Adopt => candidate,
            ReviewFieldAction.Keep => current,
            _ => null
        };
        result.PersonCredits = creditSource is not null &&
            creditSource.DetailFetched && creditSource.DetailIdentityVerified &&
            result.DetailFetched && result.DetailIdentityVerified &&
            string.Equals(result.Source, creditSource.Source, StringComparison.Ordinal) &&
            string.Equals(result.SourceUrl, creditSource.SourceUrl, StringComparison.Ordinal) &&
            string.Equals(result.Code, creditSource.Code, StringComparison.OrdinalIgnoreCase)
                ? (creditSource.PersonCredits ?? []).Where(credit => credit is not null &&
                    RoleProfileStore.TryVerifiedPersonCredit(creditSource, credit, out _))
                    .Select(credit => credit.Clone()).ToList()
                : [];
        // Every contested field now has an explicit disposition. The raw
        // observations remain available, including values that were declined.
        result.SourceConflictFields = [];
        return result;
    }

    static void Assign(MovieMetadata target, string key, string? value, MovieMetadata? source)
    {
        var empty = string.IsNullOrWhiteSpace(value);
        switch (key)
        {
            case nameof(MovieMetadata.Code): target.Code = value ?? ""; break;
            case nameof(MovieMetadata.Title): target.Title = value ?? ""; break;
            case nameof(MovieMetadata.OriginalTitle): target.OriginalTitle = value; break;
            case nameof(MovieMetadata.ReleaseDate): target.ReleaseDate = value; break;
            case nameof(MovieMetadata.RuntimeMinutes):
                target.RuntimeMinutes = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0 ? minutes : null; break;
            case nameof(MovieMetadata.Director): target.Director = value; break;
            case nameof(MovieMetadata.Studio): target.Studio = value; break;
            case nameof(MovieMetadata.Maker): target.Maker = value; break;
            case nameof(MovieMetadata.Label): target.Label = value; break;
            case nameof(MovieMetadata.Series): target.Series = value; break;
            case nameof(MovieMetadata.Country): target.Country = value; break;
            case nameof(MovieMetadata.Language): target.Language = value; break;
            case nameof(MovieMetadata.Summary): target.Summary = value; break;
            case nameof(MovieMetadata.Rating):
                target.Rating = value;
                break;
            case nameof(MovieMetadata.PosterUrl):
                target.PosterUrl = value;
                target.Poster = empty ? null : source?.Poster;
                break;
            case nameof(MovieMetadata.PreviewImageUrls):
                target.PreviewImageUrls = empty ? [] : value!.Split('\n',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
                break;
            case nameof(MovieMetadata.TrailerUrl): target.TrailerUrl = value; break;
            case nameof(MovieMetadata.Actors): target.Actors = source?.Actors?.ToList() ?? Split(value); break;
            case nameof(MovieMetadata.Genres): target.Genres = source?.Genres?.ToList() ?? Split(value); break;
            case nameof(MovieMetadata.Keywords): target.Keywords = source?.Keywords?.ToList() ?? Split(value); break;
            case nameof(MovieMetadata.PlatformRatings):
                target.PlatformRatings = source?.PlatformRatings?.Select(item => item.Clone()).ToList() ?? []; break;
            case nameof(MovieMetadata.CatalogLinks):
                target.CatalogLinks = source?.CatalogLinks?.Select(item => item.Clone()).ToList() ?? []; break;
        }
    }

    static List<string> Split(string? value) => (value ?? "").Split('、',
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    static bool VerifyPersonCreditRoundTrip()
    {
        var candidate = new MovieMetadata
        {
            Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/42",
            Code = "WORK-42", Title = "示例作品"
        };
        using var document = JsonDocument.Parse("""
            {"id":42,"title":"示例作品","overview":"作品简介","credits":{"cast":[
              {"id":101,"name":"同名演员","character":"角色甲"},
              {"id":102,"name":"同名演员","character":"角色乙"}]}}
            """);
        candidate = AdditionalScrapers.ParseTmdbDetails(candidate, document.RootElement, "movie");
        var approved = Build(null, candidate, new Dictionary<string, ReviewFieldSelection>());
        var reopened = JsonSerializer.Deserialize<MovieMetadata>(JsonSerializer.Serialize(approved));
        if (reopened is null || reopened.PersonCredits.Count != 2 ||
            reopened.PersonCredits[0].PersonId == reopened.PersonCredits[1].PersonId ||
            reopened.PersonCredits.Any(credit => !RoleProfileStore.TryVerifiedPersonCredit(reopened, credit, out _)))
            return false;

        var sameWork = Build(reopened, candidate, new Dictionary<string, ReviewFieldSelection>
        {
            [nameof(MovieMetadata.Summary)] = new(ReviewFieldAction.Adopt)
        });
        if (sameWork.Source != candidate.Source || sameWork.SourceUrl != candidate.SourceUrl ||
            sameWork.PersonCredits.Count != 2) return false;

        var clearActors = Build(null, candidate, new Dictionary<string, ReviewFieldSelection>
        {
            [nameof(MovieMetadata.Actors)] = new(ReviewFieldAction.Clear)
        });
        var clearExistingActors = Build(reopened, candidate, new Dictionary<string, ReviewFieldSelection>
        {
            [nameof(MovieMetadata.Actors)] = new(ReviewFieldAction.Clear)
        });
        var changeCode = Build(null, candidate, new Dictionary<string, ReviewFieldSelection>
        {
            [nameof(MovieMetadata.Code)] = new(ReviewFieldAction.Clear)
        });
        if (clearActors.PersonCredits.Count != 0 || clearExistingActors.PersonCredits.Count != 0 ||
            changeCode.PersonCredits.Count != 0) return false;

        candidate.SourceObservations.Add(new MetadataSourceObservation
        {
            Field = nameof(MovieMetadata.Director), Source = "其它来源",
            SourceUrl = "https://example.org/work/42", Value = "另一位导演"
        });
        var mixed = Build(reopened, candidate, new Dictionary<string, ReviewFieldSelection>
        {
            [nameof(MovieMetadata.Director)] = new(ReviewFieldAction.Source, candidate.SourceObservations[^1])
        });
        if (mixed.Source != "人工审核组合" || mixed.SourceUrl.Length != 0 ||
            mixed.PersonCredits.Count != 0) return false;

        var otherWork = reopened.Clone();
        otherWork.Source = "TVmaze";
        otherWork.SourceUrl = "https://www.tvmaze.com/shows/42";
        var replacedActors = Build(otherWork, candidate, new Dictionary<string, ReviewFieldSelection>
        {
            [nameof(MovieMetadata.Actors)] = new(ReviewFieldAction.Adopt)
        });
        if (replacedActors.PersonCredits.Count != 0) return false;
        var unverified = candidate.Clone();
        unverified.DetailIdentityVerified = false;
        return Build(null, unverified, new Dictionary<string, ReviewFieldSelection>()).PersonCredits.Count == 0;
    }

    internal static bool VerifyForDiagnostics()
    {
        var old = new MovieMetadata { Title = "人工标题", Code = "AB-123", Director = "人工导演", Summary = "旧简介",
            FieldProvenance = new Dictionary<string, MetadataFieldProvenance> { [nameof(MovieMetadata.Title)] = new() { Source = "manual" } } };
        var candidate = new MovieMetadata { Title = "爬取标题", Code = "AB-123", Director = "导演甲", Summary = "新简介",
            Source = "JavDB", SourceConflictFields = [nameof(MovieMetadata.Director)],
            SourceObservations = [new() { Field = nameof(MovieMetadata.Director), Value = "导演乙", Source = "JAVDatabase", SourceUrl = "https://example.invalid/work" }] };
        if (Default(nameof(MovieMetadata.Title), old, candidate).Action != ReviewFieldAction.Keep ||
            Default(nameof(MovieMetadata.Director), null, candidate).Action != ReviewFieldAction.Keep) return false;
        try { Build(old, candidate, new Dictionary<string, ReviewFieldSelection>()); return false; }
        catch (InvalidDataException) { }
        var choices = new Dictionary<string, ReviewFieldSelection>
        {
            [nameof(MovieMetadata.Summary)] = new(ReviewFieldAction.Adopt),
            [nameof(MovieMetadata.Director)] = new(ReviewFieldAction.Source, candidate.SourceObservations[0]),
            [nameof(MovieMetadata.Rating)] = new(ReviewFieldAction.Clear)
        };
        var result = Build(old, candidate, choices);
        if (result.Title != "人工标题" || result.Director != "导演乙" || result.Summary != "新简介" ||
            result.FieldProvenance[nameof(MovieMetadata.Title)].Source != "manual" ||
            result.FieldProvenance[nameof(MovieMetadata.Director)].Source != "JAVDatabase" ||
            result.SourceConflictFields.Count != 0 || result.SourceObservations.Count != 1) return false;
        var low = Build(old, candidate, new Dictionary<string, ReviewFieldSelection>
        {
            [nameof(MovieMetadata.Director)] = new(ReviewFieldAction.Keep)
        });
        if (low.Title != "人工标题" || low.Director != "人工导演" || low.Summary != "旧简介") return false;
        try
        {
            Build(old, candidate, new Dictionary<string, ReviewFieldSelection>
            {
                [nameof(MovieMetadata.Director)] = new(ReviewFieldAction.Source,
                    new MetadataSourceObservation { Field = nameof(MovieMetadata.Director), Source = "unknown", Value = "伪造" })
            });
            return false;
        }
        catch (InvalidDataException) { return VerifyPersonCreditRoundTrip(); }
    }
}
