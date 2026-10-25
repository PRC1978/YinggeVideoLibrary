using System.IO;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // Title matches are less reliable than an exact catalog code. Preserve
    // multiple search cards before detail requests, then enrich the first
    // three for offline comparison while one verified match enters review.
    async Task<string> ProcessTitleScrapeQueueItemAsync(
        ScrapeQueueItem item, VideoItem video, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var title = AdditionalScrapers.CleanTitle(item.SearchTitle);
        if (config.UseFolderPersonHint && ScrapeSearchHints.IsWeakTitle(title))
            title = ScrapeSearchHints.FindWorkTitleHint(video.Path, config.Folders) ?? title;
        if (!Regex.IsMatch(title, @"[\u3400-\u9FFF]|\p{L}{2,}"))
            throw new InvalidDataException("文件名和人物目录下的作品目录均缺少可搜索标题；请先手动补充标题后再搜刮。");

        var firstCandidates = await SearchCandidatesAsync(title, 3, bulk: true,
            cancellationToken: cancellationToken, video: video);
        var candidates = await SearchWithFolderPersonHintAsync(video, title, 3, firstCandidates, cancellationToken, bulk: true);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count == 0)
            throw new ScrapeNotFoundException("已查询启用的公开来源，没有找到标题候选。");

        // Search cache and source parsers are independent inputs. Check again
        // before ranking or fetching details; the queue must never dispatch a
        // paid/manual candidate even if an old cache contains one.
        var tmdbScope = TmdbUsagePolicy.Evaluate(video, metadata.GetValueOrDefault(video.Id), title);
        var queueCandidates = FilterTitleQueueCandidates(candidates)
            .Where(candidate => QueueSourceKey(candidate.Source) != "TMDB" ||
                tmdbScope.Allowed && !candidate.IsAdultContent &&
                !TmdbUsagePolicy.HasAdultCue(candidate.Title) &&
                !TmdbUsagePolicy.HasAdultCue(candidate.Summary)).ToList();
        if (queueCandidates.Count != candidates.Count)
            Log($"标题候选中有 {candidates.Count - queueCandidates.Count} 项不属于后台队列或不满足 TMDB 普通影视范围，已忽略并核对其余候选。");
        if (queueCandidates.Count == 0)
            throw new InvalidDataException("标题候选的来源均不属于后台搜刮队列；未请求付费来源，请重新查询公开来源。");

        var snapshot = BuildTitleCandidateSnapshot(video, title, queueCandidates);
        // This write precedes network detail calls. A cancellation, timeout or
        // source error must not turn several real candidates into "no result".
        ScrapeCandidateCache.SaveTitleCandidates(DataDir, snapshot);

        var attemptedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var detailFailures = new List<(string Name, Exception Error)>();
        MovieMetadata? record = null;
        async Task TryFetchDetailAsync(ScrapeCandidateCache.RankedTitleCandidate row)
        {
            var candidate = row.SearchMetadata;
            var source = QueueSourceKey(candidate.Source);
            if (source.Length == 0)
            {
                row.DetailStatus = "source-rejected";
                return;
            }
            if (source == "TMDB" && (!TmdbUsagePolicy.Evaluate(video,
                    metadata.GetValueOrDefault(video.Id), title).Allowed ||
                    candidate.IsAdultContent || TmdbUsagePolicy.HasAdultCue(candidate.Title)))
            {
                row.DetailStatus = "source-rejected";
                return;
            }
            attemptedSources.Add(source);
            if (string.IsNullOrWhiteSpace(candidate.SourceUrl))
            {
                row.DetailStatus = "detail-error";
                return;
            }
            try
            {
                var details = await ScrapeCandidateCache.GetOrFetchAsync(DataDir, candidate.Source + " 详情",
                    candidate.SourceUrl,
                    async () => [await RunQueueSourceAsync(source,
                        () => AdditionalScrapers.FetchFullDetailsAsync(candidate, config.TmdbToken),
                        cancellationToken)])
                    .WaitAsync(cancellationToken);
                var detail = details.FirstOrDefault(item => ScrapeCandidateCache.DetailMatchesCandidate(candidate, item))
                    ?? details.FirstOrDefault(item => item.DetailFetched && ScrapeCandidateCache.SameCandidateIdentity(candidate, item));
                if (detail is null)
                {
                    row.DetailStatus = "detail-error";
                    return;
                }
                if (source == "TMDB" && (detail.IsAdultContent ||
                    TmdbUsagePolicy.HasAdultCue(detail.Title) ||
                    TmdbUsagePolicy.HasAdultCue(detail.Summary)))
                {
                    row.DetailStatus = "source-rejected";
                    return;
                }
                row.DetailMetadata = detail.Clone();
                row.DetailFetchedUtc = DateTime.UtcNow;
                row.DetailStatus = detail.DetailIdentityVerified ? "verified" : "identity-unverified";
                if (record is null && detail.DetailIdentityVerified) record = detail;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ScrapeSourceUnavailableException ex)
            {
                row.DetailStatus = "source-unavailable";
                detailFailures.Add((source, ex));
                Log($"{source} 详情暂时不可用，尝试其他来源：{ex.Message}");
            }
            catch (Exception ex)
            {
                row.DetailStatus = "detail-error";
                detailFailures.Add((source, ex));
                Log($"{source} 详情读取失败，尝试其他来源：{ex.Message}");
            }
        }

        // At least three distinct candidates receive a detail attempt when
        // available, including three candidates from the same source.
        foreach (var row in snapshot.Candidates.Take(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryFetchDetailAsync(row);
        }
        // If none of those could establish identity, retain the old fallback
        // to other sources instead of letting one cooling source block all.
        if (record is null)
            foreach (var row in snapshot.Candidates.Skip(3))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attemptedSources.Contains(QueueSourceKey(row.SearchMetadata.Source))) continue;
                await TryFetchDetailAsync(row);
                if (record is not null) break;
            }
        if (record is not null && NativeMetadataAggregation.IsNativeSource(record.Source) &&
            NativeMetadataAggregation.HasExactCode(record, record.Code))
        {
            // Title queries may discover the same numbered work at both native
            // catalogs. Fetch the other card only when its explicit code agrees;
            // a merely similar title must never be merged across sources.
            var related = snapshot.Candidates.Where(row =>
                NativeMetadataAggregation.IsNativeSource(row.SearchMetadata.Source) &&
                !row.SearchMetadata.Source.Equals(record.Source, StringComparison.OrdinalIgnoreCase) &&
                NativeMetadataAggregation.HasExactCode(row.SearchMetadata, record.Code)).ToList();
            foreach (var row in related.Where(row => row.DetailMetadata is null && row.DetailStatus == "search-only").Take(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TryFetchDetailAsync(row);
            }
            var sameWork = NativeMetadataAggregation.OrderByTrust(snapshot.Candidates.Select(row => row.DetailMetadata)
                .OfType<MovieMetadata>()
                .Where(detail => NativeMetadataAggregation.IsNativeSource(detail.Source) &&
                    detail.DetailFetched && detail.DetailIdentityVerified &&
                    NativeMetadataAggregation.HasExactCode(detail, record.Code)), SourceTrust);
            if (sameWork.Select(detail => detail.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                record = NativeMetadataAggregation.AggregateExactCode(record.Code, sameWork);
            else if (related.Any(row => row.DetailStatus is "source-unavailable" or "detail-error" or "identity-unverified"))
                record.DetailNote += "；另一个同番号来源的详情本次未能核验，可稍后重试补全";
        }
        ScrapeCandidateCache.SaveTitleCandidates(DataDir, snapshot);
        if (record is null && detailFailures.Count > 0)
            throw BuildBulkSourceFailure(detailFailures, "候选详情");
        if (record is null)
            throw new InvalidDataException("候选来源未返回可核验的完整详情；任务已保留供人工重试。");

        var posterSource = record.SourceObservations is { Count: > 0 }
            ? snapshot.Candidates.Select(row => row.DetailMetadata).OfType<MovieMetadata>()
                .FirstOrDefault(detail => detail.Source != record.Source &&
                    detail.Source.Equals(record.FieldProvenance?.GetValueOrDefault(nameof(MovieMetadata.PosterUrl))?.Source,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(detail.PosterUrl, record.PosterUrl, StringComparison.OrdinalIgnoreCase))
            : null;
        if (posterSource is not null)
        {
            await AdditionalScrapers.DownloadPosterAsync(posterSource, video.Id, DataDir).WaitAsync(cancellationToken);
            record.Poster = posterSource.Poster;
        }
        else await AdditionalScrapers.DownloadPosterAsync(record, video.Id, DataDir).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Task.Run(() => ScrapeQueueStore.HasPending(DataDir, item.VideoId), cancellationToken))
            throw new OperationCanceledException("标题搜刮任务已从队列取消。", cancellationToken);

        var status = await StoreScrapeReviewAsync(video, record, forcePending: true, cancellationToken: cancellationToken);
        Log($"标题搜刮完成：{video.Title} · {record.Source} · {status}");
        return status;
    }

    internal static List<MovieMetadata> FilterTitleQueueCandidates(IEnumerable<MovieMetadata> candidates)
        => candidates.Where(candidate => candidate is not null && QueueSourceKey(candidate.Source).Length > 0).ToList();

    static string QueueSourceKey(string? source)
    {
        return source switch
        {
            "TMDB 电影" or "TMDB 剧集" => "TMDB",
            "JAVDatabase" => "JAVDatabase",
            "JavDB" => "JavDB",
            "TVmaze" => "TVmaze",
            "Wikidata" => "Wikidata",
            "Kitsu" => "Kitsu",
            "Tenrai" => "Tenrai",
            "Mikan" => "Mikan",
            "FankuHub" => "FankuHub",
            "Pornhub" => "Pornhub",
            "Bangumi" => "Bangumi",
            _ => ""
        };
    }
}
