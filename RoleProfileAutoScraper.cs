using System.IO;
using System.Net;
using System.Net.Http;
using System.Diagnostics;

namespace YinggeVideoLibrary;

// Bulk discovery starts with a verified work already saved for a local video.
// A person's display name never proves identity and is never a merge key.
internal static class RoleProfileAutoScraper
{
    const int MaxProfiles = 10000;
    const int MaxWorksPerProfile = 20000;
    const int SaveBatchSize = 500;
    static readonly TimeSpan SourceSpacing = TimeSpan.FromMilliseconds(600);
    static readonly TimeSpan MaxSaveInterval = TimeSpan.FromMinutes(2);
    static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(30);

    internal sealed class Candidate
    {
        internal Candidate(RoleProfileStore.SourceIdentity identity) => Identity = identity;
        internal RoleProfileStore.SourceIdentity Identity { get; }
        internal List<WorkEvidence> Evidence { get; } = [];
        internal HashSet<string> EvidenceKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Name => Identity.NameAtSource;
        public string Provider => Identity.Provider;
        public string PersonId => Identity.PersonId;
        public string PersonUrl => Identity.PersonUrl;
        public string VerifiedFromWorkUrl => Identity.VerifiedFromWorkUrl;
        public int WorkCount => Evidence.Count;
        public bool CanFetchDetails => Provider is "TMDB" or "TVmaze";
    }

    internal sealed record WorkEvidence(VideoItem Video, MovieMetadata Record,
        RoleProfileStore.SourceIdentity Identity, string CharacterName);

    internal sealed class PreviewResult
    {
        internal PreviewResult(IReadOnlyList<Candidate> candidates, int existingCount,
            int newCount, int workCount, int skippedUnverifiedCount,
            int workCapacityExceededCount, bool capacityExceeded)
        {
            Candidates = candidates;
            ExistingCount = existingCount;
            NewCount = newCount;
            WorkCount = workCount;
            SkippedUnverifiedCount = skippedUnverifiedCount;
            WorkCapacityExceededCount = workCapacityExceededCount;
            CapacityExceeded = capacityExceeded;
        }
        public IReadOnlyList<Candidate> Candidates { get; }
        public int ExistingCount { get; }
        public int NewCount { get; }
        public int WorkCount { get; }
        public int SkippedUnverifiedCount { get; }
        public int WorkCapacityExceededCount { get; }
        public bool CapacityExceeded { get; }
        public IReadOnlyDictionary<string, int> ProviderCounts => Candidates
            .GroupBy(candidate => candidate.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(),
                StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record Progress(int Processed, int Total, int Created, int Updated,
        int Fetched, int Failed, string CurrentName);
    internal sealed record Result(int Processed, int Created, int Updated, int Fetched,
        int Failed, int SkippedCredentials, int SkippedChanged, int SkippedRateLimited,
        IReadOnlyList<string> Failures);

    internal static PreviewResult Preview(IReadOnlyList<VideoItem> videos,
        IReadOnlyDictionary<string, MovieMetadata> savedMetadata, RoleProfileStore.State state,
        string searchText = "", CancellationToken cancellationToken = default)
    {
        RoleProfileStore.ValidateState(state);
        var discovered = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        var seenVideoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var video in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seenVideoIds.Add(video.Id) || !savedMetadata.TryGetValue(video.Id, out var record)
                || record is null) continue;
            var foundVerified = false;
            if (record.DetailFetched && record.DetailIdentityVerified)
            {
                if (record.Source == "JavDB")
                {
                    foreach (var actor in record.CatalogLinks ?? [])
                    {
                        if (actor is null || !RoleProfileStore.TryVerifiedJavDbActor(record, actor,
                                out var identity)) continue;
                        foundVerified |= Add(discovered, video, record, identity, "");
                    }
                }
                foreach (var credit in record.PersonCredits ?? [])
                {
                    if (credit is null || credit.Provider == "TMDB" &&
                            !TmdbUsagePolicy.Evaluate(video, record, record.Title).Allowed ||
                        !RoleProfileStore.TryVerifiedPersonCredit(record, credit, out var identity)) continue;
                    foundVerified |= Add(discovered, video, record, identity, credit.CharacterName);
                }
            }
            if (!foundVerified && (record.Actors?.Count > 0 ||
                record.PersonCredits?.Count > 0 || record.CatalogLinks?.Any(link => link?.Kind == "演员") == true))
                skipped++;
        }

        var query = searchText.Trim();
        var candidates = discovered.Values.Where(candidate => query.Length == 0 ||
                candidate.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                candidate.PersonId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.WorkCount)
            .ThenBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var existingProfiles = state.Profiles.SelectMany(profile => profile.Sources.Select(source =>
                (Key: source.Provider + "\n" + source.PersonId, Profile: profile)))
            .ToDictionary(pair => pair.Key, pair => pair.Profile, StringComparer.OrdinalIgnoreCase);
        var existing = candidates.Count(candidate => existingProfiles.ContainsKey(
            candidate.Provider + "\n" + candidate.PersonId));
        var newly = candidates.Length - existing;
        var workOverCapacity = 0;
        foreach (var candidate in candidates)
        {
            existingProfiles.TryGetValue(candidate.Provider + "\n" + candidate.PersonId, out var profile);
            var linkedVideoIds = profile?.Works.Where(work => work.LocalVideoId.Length > 0)
                .Select(work => work.LocalVideoId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var needed = candidate.Evidence.Count(evidence => linkedVideoIds.Add(evidence.Video.Id));
            workOverCapacity += Math.Max(0, (profile?.Works.Count ?? 0) + needed - MaxWorksPerProfile);
        }
        return new PreviewResult(candidates, existing, newly,
            candidates.Sum(candidate => candidate.WorkCount), skipped, workOverCapacity,
            state.Profiles.Count + newly > MaxProfiles || workOverCapacity > 0);
    }

    static bool Add(Dictionary<string, Candidate> discovered, VideoItem video,
        MovieMetadata record, RoleProfileStore.SourceIdentity identity, string character)
    {
        if (string.IsNullOrWhiteSpace(identity.NameAtSource) || identity.NameAtSource.Length > 180 ||
            !RoleProfileStore.IsTrustedIdentity(identity) || identity.VerifiedFromWorkUrl != record.SourceUrl)
            return false;
        var key = identity.Provider + "\n" + identity.PersonId;
        if (!discovered.TryGetValue(key, out var candidate))
            discovered.Add(key, candidate = new Candidate(identity));
        if (!candidate.EvidenceKeys.Add(video.Id + "\n" + record.SourceUrl)) return true;
        candidate.Evidence.Add(new WorkEvidence(video, record, identity, character));
        return true;
    }

    // MetadataStore replaces metadata.json atomically. Checking both the
    // primary and fallback file stamps makes a long import reload only when a
    // different accepted-metadata snapshot appears on disk. This avoids
    // parsing a large library file once for every person/network request.
    readonly record struct FileStamp(long Length, long CreatedTicks, long ModifiedTicks);
    readonly record struct MetadataStamp(FileStamp Primary, FileStamp Backup);

    static FileStamp Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists
            ? new FileStamp(file.Length, file.CreationTimeUtc.Ticks, file.LastWriteTimeUtc.Ticks)
            : new FileStamp(-1, 0, 0);
    }

    static MetadataStamp StampMetadata(string dataDir)
    {
        var path = Path.Combine(dataDir, "metadata.json");
        return new MetadataStamp(Stamp(path), Stamp(path + ".bak"));
    }

    static (Dictionary<string, MovieMetadata> Records, MetadataStamp Stamp) LoadStableMetadata(
        string dataDir)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = StampMetadata(dataDir);
            var records = MetadataStore.Load(dataDir);
            var after = StampMetadata(dataDir);
            if (before == after) return (records, after);
        }
        throw new IOException("作品元数据正被另一份程序修改，请稍后重新预览人物候选。 ");
    }

    static void RefreshMetadata(string dataDir, ref Dictionary<string, MovieMetadata> records,
        ref MetadataStamp stamp)
    {
        if (StampMetadata(dataDir) == stamp) return;
        (records, stamp) = LoadStableMetadata(dataDir);
    }

    static WorkEvidence[] VerifiedEvidence(Candidate candidate,
        IReadOnlyDictionary<string, MovieMetadata> currentMetadata) =>
        candidate.Evidence.Where(item => currentMetadata.TryGetValue(item.Video.Id, out var saved) &&
                saved is not null && saved.SourceUrl == item.Record.SourceUrl &&
                StillVerified(item with { Record = saved }, candidate))
            .Select(item => item with { Record = currentMetadata[item.Video.Id] }).ToArray();

    sealed class SourcePacing
    {
        internal DateTimeOffset NextRequestUtc;
    }

    static async Task<RoleProfileStore.SourceDetails> FetchDetailsWithBackoffAsync(
        RoleProfileStore.SourceIdentity identity, string tmdbToken,
        Func<RoleProfileStore.SourceIdentity, CancellationToken,
            Task<RoleProfileStore.SourceDetails>>? fetchDetails,
        SourcePacing pacing, CancellationToken token)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (fetchDetails is null)
            {
                var wait = pacing.NextRequestUtc - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                pacing.NextRequestUtc = DateTimeOffset.UtcNow + SourceSpacing;
            }
            try
            {
                return fetchDetails is null
                    ? await RolePersonDetailsFetcher.FetchAsync(identity, tmdbToken, token,
                        downloadPortrait: false)
                    : await fetchDetails(identity, token);
            }
            catch (RolePersonDetailsFetcher.RateLimitException ex)
            {
                if (attempt > 0 || ex.RetryAfter > MaxRateLimitWait) throw;
                await Task.Delay(ex.RetryAfter, token);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt > 0) throw new RolePersonDetailsFetcher.RateLimitException(
                    identity.Provider, TimeSpan.FromSeconds(5));
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
        throw new InvalidOperationException("人物来源重试状态无效。 ");
    }

    internal static async Task<Result> RunAsync(string dataDir, PreviewResult preview,
        string tmdbToken, IProgress<Progress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<RoleProfileStore.SourceIdentity, CancellationToken,
            Task<RoleProfileStore.SourceDetails>>? fetchDetails = null)
    {
        if (preview.CapacityExceeded)
            throw new InvalidOperationException(preview.WorkCapacityExceededCount > 0
                ? $"有 {preview.WorkCapacityExceededCount:N0} 条作品关联会超过单个人物 20,000 部的上限；请缩小导入范围。"
                : "候选人物数量超过单个角色档案库 10,000 人的上限，请先缩小导入范围。 ");
        var state = RoleProfileStore.Load(dataDir);
        var (currentMetadata, metadataStamp) = LoadStableMetadata(dataDir);
        if (preview.Candidates.Count > 0 && currentMetadata.Count == 0)
            throw new InvalidDataException("已保存的作品元数据已移除或无法读取，请重新预览人物候选。 ");
        var profilesBySource = state.Profiles.SelectMany(profile => profile.Sources.Select(source =>
                (Key: source.Provider + "\n" + source.PersonId, Profile: profile)))
            .ToDictionary(pair => pair.Key, pair => pair.Profile, StringComparer.OrdinalIgnoreCase);
        var newly = preview.Candidates.Count(candidate => !profilesBySource.ContainsKey(
            candidate.Provider + "\n" + candidate.PersonId));
        if (state.Profiles.Count + newly > MaxProfiles)
            throw new InvalidOperationException("角色档案已发生变化，导入后会超过档案上限，请重新预览。 ");

        // Refuse a changed profile's work-cap overflow before the first write.
        // Existing manual links to the same local video count as linked.
        foreach (var candidate in preview.Candidates)
        {
            profilesBySource.TryGetValue(candidate.Provider + "\n" + candidate.PersonId, out var existing);
            var linked = existing?.Works.Where(work => work.LocalVideoId.Length > 0)
                .Select(work => work.LocalVideoId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var additional = VerifiedEvidence(candidate, currentMetadata)
                .Count(evidence => linked.Add(evidence.Video.Id));
            if ((existing?.Works.Count ?? 0) + additional > MaxWorksPerProfile)
                throw new InvalidOperationException($"人物“{candidate.Name}”的作品关联会超过 20,000 部上限；请重新预览并缩小范围。 ");
        }

        var processed = 0;
        var created = 0;
        var updated = 0;
        var fetched = 0;
        var failed = 0;
        var skippedCredentials = 0;
        var skippedChanged = 0;
        var skippedRateLimited = 0;
        var pending = 0;
        var pendingEvidence = new List<(Candidate Candidate, WorkEvidence[] Evidence)>();
        var failures = new List<string>();
        var stoppedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pacing = new SourcePacing();
        var progressClock = Stopwatch.StartNew();
        var lastProgressAt = TimeSpan.Zero;
        var lastSaveUtc = DateTimeOffset.UtcNow;

        void Report(string name)
        {
            if (progress is null) return;
            var now = progressClock.Elapsed;
            if (processed != preview.Candidates.Count && processed % 100 != 0 &&
                now - lastProgressAt < TimeSpan.FromSeconds(10)) return;
            lastProgressAt = now;
            progress.Report(new Progress(processed, preview.Candidates.Count,
                created, updated, fetched, failed, name));
        }

        void SavePending()
        {
            if (pending == 0) return;
            pending = 0;
            // Hold the same cross-process lease used by metadata writers from
            // the final provenance check through this role-profile commit.
            // If a work changed while a prior person was being fetched, the
            // whole unsaved batch is refused instead of persisting stale links.
            using var lease = MetadataStore.AcquireReviewWriteLease();
            (currentMetadata, metadataStamp) = LoadStableMetadata(dataDir);
            foreach (var (candidate, evidenceAtApply) in pendingEvidence)
            {
                var currentKeys = VerifiedEvidence(candidate, currentMetadata)
                    .Select(item => item.Video.Id + "\n" + item.Record.SourceUrl)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (evidenceAtApply.Any(item => !currentKeys.Contains(
                    item.Video.Id + "\n" + item.Record.SourceUrl)))
                    throw new InvalidDataException("导入期间作品来源已改变，当前未保存批次已停止；请重新预览人物候选。 ");
            }
            RoleProfileStore.Save(dataDir, state);
            pendingEvidence.Clear();
            lastSaveUtc = DateTimeOffset.UtcNow;
        }

        try
        {
            foreach (var candidate in preview.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RefreshMetadata(dataDir, ref currentMetadata, ref metadataStamp);
                var validEvidence = VerifiedEvidence(candidate, currentMetadata);
                if (validEvidence.Length == 0)
                {
                    skippedChanged++;
                    processed++;
                    Report(candidate.Name);
                    continue;
                }
                var sourceKey = candidate.Provider + "\n" + candidate.PersonId;
                profilesBySource.TryGetValue(sourceKey, out var profile);
                var isNew = profile is null;
                RoleProfileStore.SourceDetails? fetchedDetails = null;
                if (candidate.CanFetchDetails && !(profile?.SourcedDetails.Any(item =>
                    item.Provider.Equals(candidate.Provider, StringComparison.OrdinalIgnoreCase) &&
                    item.PersonId.Equals(candidate.PersonId, StringComparison.OrdinalIgnoreCase)) ?? false))
                {
                    if (candidate.Provider == "TMDB" && string.IsNullOrWhiteSpace(tmdbToken))
                        skippedCredentials++;
                    else if (stoppedProviders.Contains(candidate.Provider))
                        skippedRateLimited++;
                    else
                    {
                        try
                        {
                            fetchedDetails = await FetchDetailsWithBackoffAsync(validEvidence[0].Identity,
                                tmdbToken, fetchDetails, pacing, cancellationToken);
                            if (fetchedDetails.Provider != candidate.Provider ||
                                fetchedDetails.PersonId != candidate.PersonId ||
                                fetchedDetails.SourceUrl != validEvidence[0].Identity.PersonUrl)
                                throw new InvalidDataException("来源返回的人物身份与已核实作品不一致。 ");
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (RolePersonDetailsFetcher.RateLimitException ex)
                        {
                            stoppedProviders.Add(candidate.Provider);
                            skippedRateLimited++;
                            if (failures.Count < 20)
                                failures.Add(candidate.Provider + " 人物来源限速，已停止本轮该来源请求；请稍后重试。" +
                                    (ex.RetryAfter > TimeSpan.Zero ? $"建议等待至少 {Math.Ceiling(ex.RetryAfter.TotalSeconds):N0} 秒。" : ""));
                        }
                        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or
                                   TaskCanceledException or System.Text.Json.JsonException)
                        {
                            failed++;
                            if (failures.Count < 20)
                                failures.Add(candidate.Provider + " · " + candidate.Name + "：" + ex.Message);
                        }
                    }
                }

                // The network request may have taken seconds. Recheck disk
                // provenance before applying either the details or the works.
                RefreshMetadata(dataDir, ref currentMetadata, ref metadataStamp);
                validEvidence = VerifiedEvidence(candidate, currentMetadata);
                if (validEvidence.Length == 0)
                {
                    skippedChanged++;
                    processed++;
                    Report(candidate.Name);
                    continue;
                }
                var linkedVideoIds = profile?.Works.Where(work => work.LocalVideoId.Length > 0)
                    .Select(work => work.LocalVideoId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var newWorks = validEvidence.Where(evidence => linkedVideoIds.Add(evidence.Video.Id))
                    .ToArray();
                if ((profile?.Works.Count ?? 0) + newWorks.Length > MaxWorksPerProfile)
                    throw new InvalidOperationException($"人物“{candidate.Name}”的作品关联会超过 20,000 部上限；已停止并保存此前处理的档案。 ");
                if (isNew)
                {
                    profile = new RoleProfileStore.Profile { Name = candidate.Name };
                    profile.Sources.Add(validEvidence[0].Identity);
                    state.Profiles.Add(profile);
                    profilesBySource.Add(sourceKey, profile);
                    created++;
                }
                var activeProfile = profile ?? throw new InvalidOperationException("人物档案创建失败。 ");
                var changed = false;
                foreach (var evidence in newWorks)
                {
                    // No full-file SHA during bulk import. The local-open action
                    // must still pass MatchesAsync, which rejects unverified IDs.
                    activeProfile.Works.Add(new RoleProfileStore.WorkLink
                    {
                        LocalVideoId = evidence.Video.Id,
                        WorkTitle = Limit(evidence.Record.Title, 500),
                        Role = Limit(evidence.CharacterName, 180),
                        SourceProvider = candidate.Provider,
                        SourcePersonId = candidate.PersonId,
                        SourceWorkId = evidence.Record.SourceUrl,
                        EvidenceUrl = evidence.Record.SourceUrl,
                        LinkOrigin = "verified-source"
                    });
                    changed = true;
                }
                if (fetchedDetails is not null && !activeProfile.SourcedDetails.Any(item =>
                    item.Provider.Equals(candidate.Provider, StringComparison.OrdinalIgnoreCase) &&
                    item.PersonId.Equals(candidate.PersonId, StringComparison.OrdinalIgnoreCase)))
                {
                    activeProfile.SourcedDetails.Add(fetchedDetails);
                    fetched++;
                    changed = true;
                }
                if (isNew || changed)
                {
                    if (!isNew) updated++;
                    activeProfile.UpdatedUtc = DateTimeOffset.UtcNow;
                    pending++;
                    pendingEvidence.Add((candidate, validEvidence));
                }
                processed++;
                if (pending >= SaveBatchSize || pending > 0 &&
                    DateTimeOffset.UtcNow - lastSaveUtc >= MaxSaveInterval) SavePending();
                Report(candidate.Name);
            }
        }
        catch (Exception error)
        {
            try { SavePending(); }
            catch (Exception saveError) { throw new AggregateException(error, saveError); }
            throw;
        }
        SavePending();
        return new Result(processed, created, updated, fetched, failed,
            skippedCredentials, skippedChanged, skippedRateLimited, failures);
    }

    static bool StillVerified(WorkEvidence item, Candidate candidate)
    {
        if (item.Identity.Provider != candidate.Provider ||
            item.Identity.PersonId != candidate.PersonId ||
            item.Identity.VerifiedFromWorkUrl != item.Record.SourceUrl ||
            !RoleProfileStore.IsTrustedIdentity(item.Identity)) return false;
        if (candidate.Provider == "JavDB")
            return (item.Record.CatalogLinks ?? []).Any(actor => actor is not null &&
                RoleProfileStore.TryVerifiedJavDbActor(item.Record, actor, out var rechecked) &&
                rechecked.PersonId == candidate.PersonId &&
                rechecked.VerifiedFromWorkUrl == item.Record.SourceUrl);
        if (candidate.Provider == "TMDB" &&
            !TmdbUsagePolicy.Evaluate(item.Video, item.Record, item.Record.Title).Allowed) return false;
        return (item.Record.PersonCredits ?? []).Any(credit => credit is not null &&
            RoleProfileStore.TryVerifiedPersonCredit(item.Record, credit, out var rechecked) &&
            rechecked.Provider == candidate.Provider && rechecked.PersonId == candidate.PersonId &&
            rechecked.VerifiedFromWorkUrl == item.Record.SourceUrl);
    }

    static string Limit(string? text, int length) => text is null ? ""
        : text.Length <= length ? text : text[..length];
}
