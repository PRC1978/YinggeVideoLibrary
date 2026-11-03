using System.Globalization;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// A comparison view, not an automatic metadata merge. Only IDs verified by
// their own catalogue can link two independently fetched detail records.
internal static class VerifiedWorkClusters
{
    static readonly HashSet<string> IdentityFields =
    [
        nameof(MovieMetadata.ReleaseDate), nameof(MovieMetadata.RuntimeMinutes),
        nameof(MovieMetadata.Director), nameof(MovieMetadata.Studio),
        nameof(MovieMetadata.Actors)
    ];
    internal sealed record FieldValue(string Field, string Value, string Source, string SourceUrl);
    internal sealed record Cluster(IReadOnlyList<int> Members, string SharedId,
        IReadOnlyList<FieldValue> Fields, IReadOnlyList<string> Conflicts);

    sealed record Identity(string? Imdb, string? Tmdb, string? MediaType);

    internal static IReadOnlyList<Cluster> Find(IReadOnlyList<MovieMetadata> details)
    {
        var identities = details.Select(VerifiedIdentity).ToArray();
        var used = new HashSet<int>();
        var result = new List<Cluster>();
        for (var first = 0; first < details.Count; first++)
        {
            if (used.Contains(first) || identities[first] is null) continue;
            var members = new List<int> { first };
            bool added;
            do
            {
                added = false;
                for (var next = 0; next < details.Count; next++)
                {
                    if (next == first || used.Contains(next) || members.Contains(next) || identities[next] is null)
                        continue;
                    if (!members.Any(member => CanLink(identities[member]!, identities[next]!)) ||
                        !members.All(member => !Contradictory(identities[member]!, identities[next]!) &&
                            !details[member].Source.Equals(details[next].Source, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    members.Add(next);
                    added = true;
                }
            }
            while (added);
            if (members.Count < 2) continue;
            members.Sort();
            foreach (var member in members) used.Add(member);
            var fields = members.SelectMany(index => Observations(details[index])).ToList();
            var conflicts = fields.Where(item => IdentityFields.Contains(item.Field))
                .GroupBy(item => item.Field, StringComparer.Ordinal)
                .Where(group => group.Select(item => NormalizeValue(item.Field, item.Value))
                    .Distinct(StringComparer.Ordinal).Take(2).Count() > 1)
                .Select(group => group.Key).Order(StringComparer.Ordinal).ToList();
            var firstEdge = members.SelectMany(left => members.Where(right => right > left &&
                    CanLink(identities[left]!, identities[right]!))
                .Select(right => (Left: left, Right: right))).First();
            var sharedId = SharedId(identities[firstEdge.Left]!, identities[firstEdge.Right]!);
            result.Add(new Cluster(members, sharedId, fields, conflicts));
        }
        return result;
    }

    static Identity? VerifiedIdentity(MovieMetadata item)
    {
        if (!item.DetailFetched || !item.DetailIdentityVerified ||
            !Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length != 0 || url.Port != 443 ||
            url.Query.Length != 0 || url.Fragment.Length != 0)
            return null;
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (item.Source == "OMDb" && url.Host.Equals("www.imdb.com", StringComparison.OrdinalIgnoreCase) &&
            parts.Length == 2 && parts[0] == "title" && ValidImdb(parts[1]))
            return new Identity(parts[1].ToLowerInvariant(), null, null);
        if (item.Source.StartsWith("TMDB ", StringComparison.Ordinal) &&
            url.Host.Equals("www.themoviedb.org", StringComparison.OrdinalIgnoreCase) &&
            parts.Length == 2 && parts[0] is "movie" or "tv" &&
            item.Source == (parts[0] == "movie" ? "TMDB 电影" : "TMDB 剧集") &&
            ValidNumeric(parts[1]))
            return new Identity(null, parts[1], parts[0]);
        if (item.Source == "Trakt" && url.Host.Equals("trakt.tv", StringComparison.OrdinalIgnoreCase) &&
            parts.Length >= 2 && parts[0] is "movies" or "shows" &&
            TraktScraper.TryCandidateIdentity(item, out var type, out _, out _) &&
            type == (parts[0] == "movies" ? "movie" : "show"))
        {
            var imdb = item.ExternalIds is not null && item.ExternalIds.TryGetValue("imdb", out var imdbValue)
                ? imdbValue : null;
            var tmdb = item.ExternalIds is not null && item.ExternalIds.TryGetValue("tmdb", out var tmdbValue)
                ? tmdbValue : null;
            return new Identity(ValidImdb(imdb) ? imdb!.ToLowerInvariant() : null,
                ValidNumeric(tmdb) ? tmdb : null, type == "movie" ? "movie" : "tv");
        }
        return null;
    }

    static bool CanLink(Identity left, Identity right)
    {
        if (Contradictory(left, right)) return false;
        return left.Imdb is not null && left.Imdb == right.Imdb ||
            left.Tmdb is not null && left.Tmdb == right.Tmdb && left.MediaType == right.MediaType;
    }

    static bool Contradictory(Identity left, Identity right) =>
        left.Imdb is not null && right.Imdb is not null && left.Imdb != right.Imdb ||
        left.Tmdb is not null && right.Tmdb is not null &&
        (left.Tmdb != right.Tmdb || left.MediaType != right.MediaType);

    static string SharedId(Identity left, Identity right) =>
        left.Imdb is not null && left.Imdb == right.Imdb ? "IMDb " + left.Imdb :
        "TMDB " + left.MediaType + "/" + left.Tmdb;

    static IEnumerable<FieldValue> Observations(MovieMetadata item)
    {
        var fields = new (string Field, string? Value)[]
        {
            (nameof(MovieMetadata.Title), item.Title),
            (nameof(MovieMetadata.OriginalTitle), item.OriginalTitle),
            (nameof(MovieMetadata.ReleaseDate), item.ReleaseDate),
            (nameof(MovieMetadata.RuntimeMinutes), item.RuntimeMinutes?.ToString(CultureInfo.InvariantCulture)),
            (nameof(MovieMetadata.Director), item.Director),
            (nameof(MovieMetadata.Studio), item.Studio),
            (nameof(MovieMetadata.Actors), string.Join("、", item.Actors ?? [])),
            (nameof(MovieMetadata.Summary), item.Summary),
            (nameof(MovieMetadata.Rating), item.Rating)
        };
        return fields.Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .Select(field => new FieldValue(field.Field, field.Value!, item.Source, item.SourceUrl));
    }

    static string NormalizeValue(string field, string value)
    {
        static string Normalize(string text) => new(text.Normalize(System.Text.NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return field == nameof(MovieMetadata.Actors)
            ? string.Join("|", value.Split('、', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize).Order(StringComparer.Ordinal))
            : Normalize(value);
    }
    static bool ValidImdb(string? value) => value is not null &&
        Regex.IsMatch(value, @"^tt\d{7,10}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static bool ValidNumeric(string? value) => value is not null &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 &&
        value == id.ToString(CultureInfo.InvariantCulture);
}
