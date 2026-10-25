using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// A role/person profile belongs to the user. Names are display text, never
// identity keys: two people with the same name stay separate until the user
// explicitly links a verified catalogue identity or a local video.
internal static class RoleProfileStore
{
    internal const int CurrentVersion = 1;
    internal const string FileName = "role-profiles.json";
    const int MaxPortraitBytes = 1024 * 1024;
    static readonly Mutex WriteMutex = new(false, @"Local\YinggeVideoLibrary.RoleProfilesWrite");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal sealed class State
    {
        public int Version { get; set; } = CurrentVersion;
        public string Revision { get; set; } = "0";
        public List<Profile> Profiles { get; set; } = [];
    }

    internal sealed class Profile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public List<string> Aliases { get; set; } = [];
        // Every field below is optional and entered by the user. A scrape
        // never overwrites it, even when the source has a different value.
        public string Role { get; set; } = "";
        public string Birthday { get; set; } = "";
        public string Gender { get; set; } = "";
        public string Height { get; set; } = "";
        public string Weight { get; set; } = "";
        public string Measurements { get; set; } = "";
        public string BloodType { get; set; } = "";
        public string Biography { get; set; } = "";
        // A bounded image selected by the user is embedded so ordinary backup,
        // restore and cross-device moves retain the portrait without paths.
        public string PortraitMime { get; set; } = "";
        public string PortraitBase64 { get; set; } = "";
        public List<SourceIdentity> Sources { get; set; } = [];
        public List<SourceDetails> SourcedDetails { get; set; } = [];
        public List<WorkLink> Works { get; set; } = [];
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    internal sealed class SourceIdentity
    {
        public string Provider { get; set; } = "";
        public string PersonId { get; set; } = "";
        public string PersonUrl { get; set; } = "";
        public string NameAtSource { get; set; } = "";
        public string VerifiedFromWorkUrl { get; set; } = "";
        public DateTimeOffset LinkedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    internal sealed class WorkLink
    {
        public string LocalVideoId { get; set; } = "";
        // The local ID alone can survive a replacement at the same path.
        // A full digest is captured when the user makes the link and checked
        // again before the app follows it to a local work.
        public string LocalPath { get; set; } = "";
        public long LocalSize { get; set; }
        public long LocalModifiedTicks { get; set; }
        public string ContentSha256 { get; set; } = "";
        public string WorkTitle { get; set; } = "";
        public string Role { get; set; } = "";
        public string SourceProvider { get; set; } = "";
        public string SourcePersonId { get; set; } = "";
        public string SourceWorkId { get; set; } = "";
        public string EvidenceUrl { get; set; } = "";
        public string LinkOrigin { get; set; } = "manual";
        public DateTimeOffset LinkedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    internal sealed class SourceDetails
    {
        public string Provider { get; set; } = "";
        public string PersonId { get; set; } = "";
        public string SourceUrl { get; set; } = "";
        public string Name { get; set; } = "";
        public string Biography { get; set; } = "";
        public string Birthday { get; set; } = "";
        public string Deathday { get; set; } = "";
        public string Gender { get; set; } = "";
        public string PlaceOfBirth { get; set; } = "";
        public string Country { get; set; } = "";
        public string PortraitUrl { get; set; } = "";
        public string PortraitMime { get; set; } = "";
        public string PortraitBase64 { get; set; } = "";
        public DateTimeOffset FetchedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    internal static State Load(string dataDir)
    {
        var path = PathFor(dataDir);
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new State();
        if (TryRead(path, out var state) || TryRead(path + ".bak", out state)) return state;
        throw new InvalidDataException("角色档案无法读取；原文件已保留，请检查数据目录或恢复备份。");
    }

    internal static void VerifyFile(string path)
    {
        if (!TryRead(path, out _)) throw new InvalidDataException("角色档案格式或版本无效。");
    }

    internal static State Parse(string contents)
    {
        var state = JsonSerializer.Deserialize<State>(contents)
            ?? throw new InvalidDataException("角色档案格式无效。");
        Validate(state);
        return state;
    }

    static bool TryRead(string path, out State state)
    {
        state = new State();
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 96L * 1024 * 1024) return false;
            var loaded = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            if (loaded is null) return false;
            Validate(loaded);
            state = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or InvalidDataException or NotSupportedException or FormatException) { return false; }
    }

    static void Validate(State state)
    {
        if (state.Version != CurrentVersion || state.Revision is null ||
            state.Revision.Length is < 1 or > 40 || state.Profiles is null || state.Profiles.Count > 10000)
            throw new InvalidDataException("角色档案版本或数量无效。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in state.Profiles)
        {
            if (p is null || !Guid.TryParseExact(p.Id, "N", out _) || !ids.Add(p.Id)
                || !TextOk(p.Name, 180) || string.IsNullOrWhiteSpace(p.Name)
                || p.Aliases is null || p.Aliases.Count > 50 || p.Aliases.Any(x => !TextOk(x, 180))
                || !TextOk(p.Role, 180) || !TextOk(p.Birthday, 80)
                || !TextOk(p.Gender, 80) || !TextOk(p.Height, 80)
                || !TextOk(p.Weight, 80) || !TextOk(p.Measurements, 120)
                || !TextOk(p.BloodType, 80) || !TextOk(p.Biography, 20000)
                || p.Sources is null || p.Sources.Count > 30
                || p.SourcedDetails is null || p.SourcedDetails.Count > 30
                || p.Works is null || p.Works.Count > 20000)
                throw new InvalidDataException("角色档案包含无效字段。");
            if (p.PortraitBase64 is null || p.PortraitMime is null ||
                p.PortraitBase64.Length > (MaxPortraitBytes + 2) / 3 * 4 + 4 ||
                p.PortraitMime is not ("" or "image/jpeg" or "image/png" or "image/webp") ||
                (p.PortraitBase64.Length == 0) != (p.PortraitMime.Length == 0))
                throw new InvalidDataException("角色头像格式无效。");
            if (p.PortraitBase64.Length > 0)
            {
                var bytes = Convert.FromBase64String(p.PortraitBase64);
                if (bytes.Length > MaxPortraitBytes || !ImageSignatureMatches(bytes, p.PortraitMime))
                    throw new InvalidDataException("角色头像格式无效。");
            }
            foreach (var source in p.Sources)
            {
                if (source is null || !TextOk(source.Provider, 40) || string.IsNullOrWhiteSpace(source.Provider)
                    || !TextOk(source.PersonId, 300) || string.IsNullOrWhiteSpace(source.PersonId)
                    || !TextOk(source.NameAtSource, 180) || !HttpsUrl(source.PersonUrl)
                    || !HttpsUrl(source.VerifiedFromWorkUrl)
                    || !VerifiedSourceIdentity(source))
                    throw new InvalidDataException("角色来源格式无效。");
                if (!identities.Add(source.Provider + "\n" + source.PersonId))
                    throw new InvalidDataException("同一来源人物被关联到多个角色档案。");
            }
            var sourcedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var details in p.SourcedDetails)
            {
                if (details is null || !TextOk(details.Provider, 40) || !TextOk(details.PersonId, 300)
                    || !HttpsUrl(details.SourceUrl) || !TextOk(details.Name, 180)
                    || !TextOk(details.Biography, 20000) || !TextOk(details.Birthday, 80)
                    || !TextOk(details.Deathday, 80) || !TextOk(details.Gender, 80)
                    || !TextOk(details.PlaceOfBirth, 180) || !TextOk(details.Country, 180)
                    || !TextOk(details.PortraitUrl, 2048)
                    || !TextOk(details.PortraitMime, 40) || !TextOk(details.PortraitBase64,
                        (MaxPortraitBytes + 2) / 3 * 4 + 4)
                    || !sourcedIds.Add(details.Provider + "\n" + details.PersonId)
                    || !p.Sources.Any(source => source.Provider.Equals(details.Provider, StringComparison.OrdinalIgnoreCase)
                        && source.PersonId.Equals(details.PersonId, StringComparison.OrdinalIgnoreCase)
                        && source.PersonUrl.Equals(details.SourceUrl, StringComparison.OrdinalIgnoreCase))
                    || (details.PortraitBase64.Length == 0) != (details.PortraitMime.Length == 0)
                    || details.PortraitMime is not ("" or "image/jpeg" or "image/png"))
                    throw new InvalidDataException("来源人物详情格式无效。");
                if (details.PortraitUrl.Length > 0 && !RolePersonDetailsFetcher.TrustedPortraitUrl(
                        details.Provider, details.PortraitUrl))
                    throw new InvalidDataException("来源肖像地址无效。");
                if (details.PortraitBase64.Length > 0)
                {
                    var image = Convert.FromBase64String(details.PortraitBase64);
                    if (image.Length > MaxPortraitBytes || !ImageSignatureMatches(image, details.PortraitMime))
                        throw new InvalidDataException("来源肖像内容无效。");
                }
            }
            var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usesTmdbDetails = p.Sources.Any(source => source.Provider == "TMDB") ||
                p.SourcedDetails.Any(details => details.Provider == "TMDB");
            if (usesTmdbDetails && p.Sources.Any(source => source.Provider != "TMDB"))
                throw new InvalidDataException("TMDB 人物资料不能与其他来源身份合并在同一档案。");
            foreach (var work in p.Works)
            {
                if (work is null || !TextOk(work.LocalVideoId, 160) || !TextOk(work.WorkTitle, 500)
                    || !TextOk(work.LocalPath, 4096) || work.LocalSize < 0
                    || work.LocalModifiedTicks < 0 || !TextOk(work.ContentSha256, 64)
                    || (work.ContentSha256.Length != 0 && !HexDigest(work.ContentSha256))
                    || (work.ContentSha256.Length > 0 && (work.LocalPath.Length == 0 ||
                        work.LocalVideoId.Length == 0 || work.LocalSize <= 0 || work.LocalModifiedTicks <= 0))
                    || !TextOk(work.Role, 180) || !TextOk(work.SourceProvider, 40)
                    || !TextOk(work.SourcePersonId, 300)
                    || !TextOk(work.SourceWorkId, 300) || !TextOk(work.LinkOrigin, 40)
                    || !TextOk(work.EvidenceUrl, 2048)
                    || (work.EvidenceUrl.Length > 0 && !HttpsUrl(work.EvidenceUrl))
                    || work.LocalVideoId.Length == 0 && work.SourceWorkId.Length == 0
                    || work.LinkOrigin is not ("manual" or "verified-source")
                    || (usesTmdbDetails && (work.LinkOrigin != "verified-source" ||
                        work.SourceProvider != "TMDB"))
                    || (work.LinkOrigin == "verified-source" &&
                        (!TrustedWorkUrl(work.SourceProvider, work.EvidenceUrl)
                         || !string.Equals(work.SourceWorkId, work.EvidenceUrl, StringComparison.Ordinal)
                         || !p.Sources.Any(source => source.Provider.Equals(work.SourceProvider,
                                StringComparison.OrdinalIgnoreCase) &&
                             source.PersonId.Equals(work.SourcePersonId, StringComparison.OrdinalIgnoreCase)))))
                    throw new InvalidDataException("角色作品关联格式无效。");
                if (!links.Add(work.LocalVideoId + "\n" + work.SourceProvider + "\n" + work.SourceWorkId))
                    throw new InvalidDataException("角色作品包含重复关联。");
            }
        }
    }

    internal static void ValidateState(State state) => Validate(state);

    static bool TextOk(string? value, int maxLength) => value is not null && value.Length <= maxLength;
    static bool HttpsUrl(string? value) => value is { Length: > 0 and <= 2048 }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0;

    static bool HexDigest(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    static bool CanonicalUri(string value, string host, string pathPattern)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        return System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, pathPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            value.Equals(uri.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase);
    }

    static bool TrustedWorkUrl(string provider, string url) => provider switch
    {
        "JavDB" => CanonicalUri(url, "javdb.com", @"^/v/[A-Za-z0-9_-]{2,64}/?$"),
        "TMDB" => CanonicalUri(url, "www.themoviedb.org", @"^/(?:movie|tv)/[0-9]+/?$"),
        "TVmaze" => CanonicalUri(url, "www.tvmaze.com", @"^/shows/[0-9]+/?$"),
        _ => false
    };

    static bool VerifiedSourceIdentity(SourceIdentity source)
    {
        if (!TrustedWorkUrl(source.Provider, source.VerifiedFromWorkUrl)) return false;
        if (source.Provider == "JavDB")
            return source.PersonId.Equals(source.PersonUrl, StringComparison.OrdinalIgnoreCase) &&
                JavDbScraper.TryCatalogUrl(source.PersonUrl, out var canonical, out var kind) &&
                kind == "演员" && canonical.Equals(source.PersonUrl, StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(source.PersonId, out var id) || id <= 0 || source.PersonId != id.ToString())
            return false;
        return source.Provider switch
        {
            "TMDB" => source.PersonUrl == $"https://www.themoviedb.org/person/{id}",
            "TVmaze" => source.PersonUrl == $"https://www.tvmaze.com/people/{id}",
            _ => false
        };
    }

    internal static bool IsTrustedIdentity(SourceIdentity source) => VerifiedSourceIdentity(source);

    static bool ImageSignatureMatches(byte[] bytes, string mime) => mime switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
        "image/png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };

    internal static void Save(string dataDir, State state)
    {
        Validate(state);
        Directory.CreateDirectory(dataDir);
        var path = PathFor(dataDir);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var owns = false;
        try
        {
            try { WriteMutex.WaitOne(); owns = true; }
            catch (AbandonedMutexException) { owns = true; }
            // Callers may edit a stale UI snapshot. Refuse to erase another
            // process's changes rather than silently last-writer-wins.
            var fresh = Load(dataDir);
            if (state.Revision != fresh.Revision) throw new InvalidDataException("角色档案已由另一窗口修改，请重新打开。 ");
            state.Revision = Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            VerifyFile(temporary);
            if (File.Exists(path) && TryRead(path, out _)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            if (owns) try { WriteMutex.ReleaseMutex(); } catch { }
        }
    }

    internal static Profile? FindBySource(State state, string provider, string personId) =>
        state.Profiles.FirstOrDefault(profile => profile.Sources.Any(source =>
            source.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
            source.PersonId.Equals(personId, StringComparison.OrdinalIgnoreCase)));

    internal static bool TryVerifiedJavDbActor(MovieMetadata record, JavDbCatalogLink actor,
        out SourceIdentity identity)
    {
        identity = new SourceIdentity();
        if (!JavDbScraper.IsVerifiedCatalogOrigin(record, actor) || actor.Kind != "演员" ||
            !JavDbScraper.TryCatalogUrl(actor.Url, out var canonical, out var kind) || kind != "演员") return false;
        identity = new SourceIdentity
        {
            Provider = "JavDB", PersonId = canonical, PersonUrl = canonical,
            NameAtSource = actor.Name.Trim(), VerifiedFromWorkUrl = record.SourceUrl
        };
        return true;
    }

    internal static bool TryVerifiedPersonCredit(MovieMetadata record, VerifiedPersonCredit credit,
        out SourceIdentity identity)
    {
        identity = new SourceIdentity();
        if (!record.DetailFetched || !record.DetailIdentityVerified || record.IsAdultContent ||
            !string.Equals(credit.VerifiedFromWorkUrl, record.SourceUrl, StringComparison.Ordinal) ||
            !(record.PersonCredits?.Any(saved => saved is not null && saved.Provider == credit.Provider &&
                saved.PersonId == credit.PersonId && saved.PersonUrl == credit.PersonUrl &&
                saved.Name == credit.Name && saved.VerifiedFromWorkUrl == credit.VerifiedFromWorkUrl) == true) ||
            !int.TryParse(credit.PersonId, out var personId) || personId <= 0 ||
            string.IsNullOrWhiteSpace(credit.Name) || credit.Name.Length > 180) return false;
        var trusted = credit.Provider switch
        {
            "TMDB" => record.Source.StartsWith("TMDB", StringComparison.Ordinal) &&
                Uri.TryCreate(record.SourceUrl, UriKind.Absolute, out var tmdbWork) &&
                tmdbWork.Scheme == Uri.UriSchemeHttps && tmdbWork.Host == "www.themoviedb.org" &&
                System.Text.RegularExpressions.Regex.IsMatch(tmdbWork.AbsolutePath, @"^/(?:movie|tv)/[0-9]+/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                credit.PersonUrl == $"https://www.themoviedb.org/person/{personId}",
            "TVmaze" => record.Source == "TVmaze" &&
                Uri.TryCreate(record.SourceUrl, UriKind.Absolute, out var tvmazeWork) &&
                tvmazeWork.Scheme == Uri.UriSchemeHttps && tvmazeWork.Host == "www.tvmaze.com" &&
                System.Text.RegularExpressions.Regex.IsMatch(tvmazeWork.AbsolutePath, @"^/shows/[0-9]+/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                credit.PersonUrl == $"https://www.tvmaze.com/people/{personId}",
            _ => false
        };
        if (!trusted) return false;
        identity = new SourceIdentity
        {
            Provider = credit.Provider, PersonId = credit.PersonId,
            PersonUrl = credit.PersonUrl, NameAtSource = credit.Name,
            VerifiedFromWorkUrl = credit.VerifiedFromWorkUrl
        };
        return true;
    }

    internal static void SetPortrait(Profile profile, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxPortraitBytes)
            throw new InvalidDataException("头像须为不超过 1 MB 的本地 JPEG 或 PNG 图片。");
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", _ => ""
        };
        var bytes = File.ReadAllBytes(path);
        if (!ImageSignatureMatches(bytes, mime)) throw new InvalidDataException("头像文件格式无效。");
        profile.PortraitMime = mime;
        profile.PortraitBase64 = Convert.ToBase64String(bytes);
    }

    internal static void ClearPortrait(Profile profile)
    {
        profile.PortraitMime = "";
        profile.PortraitBase64 = "";
    }
}
