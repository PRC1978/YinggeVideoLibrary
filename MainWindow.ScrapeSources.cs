using System.IO;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    bool HasEnabledCodeSource => JavDbEnabledBox.IsChecked == true || javDatabaseEnabledBox?.IsChecked == true ||
        (r18EnabledBox?.IsChecked == true && r18CatalogInstalled);

    async Task<(MovieMetadata Record, bool ForcePending)> ScrapeCodeFromEnabledSourcesAsync(string code, string videoId,
        CancellationToken cancellationToken, bool queue = false)
    {
        var failures = new List<(string Name, Exception Error)>();
        var notFound = new List<string>();
        var verified = new List<MovieMetadata>(2);
        var r18CodeAmbiguous = false;
        var attempted = 0;
        if (JavDbEnabledBox.IsChecked == true)
        {
            attempted++;
            try
            {
                var record = queue
                    ? await RunQueueSourceAsync("JavDB", () => JavDbScraper.ScrapeAsync(code, videoId, DataDir, cancellationToken), cancellationToken)
                    : await JavDbScraper.ScrapeAsync(code, videoId, DataDir, cancellationToken);
                if (record.DetailFetched && record.DetailIdentityVerified &&
                    NativeMetadataAggregation.HasExactCode(record, code)) verified.Add(record);
                else failures.Add(("JavDB", new InvalidDataException("JavDB 详情未核验或番号与请求不一致：" + record.DetailNote)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ScrapeNotFoundException) { notFound.Add("JavDB"); }
            catch (Exception ex) { failures.Add(("JavDB", ex)); Log("JavDB 本次不可用：" + ex.Message); }
        }
        if (javDatabaseEnabledBox?.IsChecked == true)
        {
            attempted++;
            try
            {
                var record = queue
                    ? await RunQueueSourceAsync("JAVDatabase", () => JavDatabaseScraper.ScrapeByCodeAsync(code, cancellationToken), cancellationToken)
                    : await JavDatabaseScraper.ScrapeByCodeAsync(code, cancellationToken);
                if (!record.DetailFetched || !record.DetailIdentityVerified)
                    throw new InvalidDataException("JAVDatabase 未返回已核验的完整详情：" + record.DetailNote);
                if (!NativeMetadataAggregation.HasExactCode(record, code))
                    throw new InvalidDataException("JAVDatabase 详情番号与请求不一致。");
                verified.Add(record);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ScrapeNotFoundException) { notFound.Add("JAVDatabase"); }
            catch (Exception ex) { failures.Add(("JAVDatabase", ex)); Log("JAVDatabase 本次不可用：" + ex.Message); }
        }
        if (r18EnabledBox?.IsChecked == true && postgresSession is not null && r18CatalogInstalled)
        {
            attempted++;
            try
            {
                var local = await Task.Run(() => R18OfflineCatalog.SearchCode(postgresSession.ConnectionString, code), cancellationToken);
                if (local.Count == 0) notFound.Add(R18OfflineCatalog.Source);
                else if (R18OfflineCatalog.CodeResultsRequireSelection(local))
                {
                    r18CodeAmbiguous = true;
                    var video = videos.FirstOrDefault(item => item.Id == videoId);
                    if (video is not null)
                    {
                        // Keep every edition visible even when the background
                        // queue cannot ask the user to choose one right now.
                        if (queue) SaveTitleCandidates(BuildTitleCandidateSnapshot(video, code, local));
                        else
                        {
                            var choices = local.Select(record =>
                                (candidate: record, score: LocalScrapeConfidence(video, record, out _)))
                                .OrderByDescending(item => item.score).ToList();
                            var chosen = ChooseScrapeCandidate(video, choices, sameCodeEditions: true);
                            if (chosen is null) throw new OperationCanceledException("已取消选择离线作品。", cancellationToken);
                            verified.Add(chosen);
                        }
                    }
                    if (queue || video is null)
                        throw new InvalidDataException($"r18.dev 离线目录中番号 {code} 对应 {local.Count} 部不同作品；候选已保留，请手动核对并选定作品。");
                }
                else foreach (var record in local)
                    if (NativeMetadataAggregation.HasExactCode(record, code)) verified.Add(record);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (!queue) { throw; }
            catch (Exception ex) { failures.Add((R18OfflineCatalog.Source, ex)); Log("R18 离线目录本次不可用：" + ex.Message); }
        }
        if (attempted == 0) throw new InvalidOperationException("请先在搜刮页启用至少一个番号来源。");
        if (verified.Count > 0)
        {
            // Each source checks its own detail identity; the aggregator also
            // verifies that every result agrees with the requested code.
            var ordered = NativeMetadataAggregation.OrderByTrust(verified, SourceTrust);
            var preferred = ordered[0];
            if (string.IsNullOrWhiteSpace(preferred.Poster))
                await AdditionalScrapers.DownloadPosterAsync(preferred, videoId, DataDir);
            if (string.IsNullOrWhiteSpace(preferred.PosterUrl))
            {
                var alternate = ordered.Skip(1).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PosterUrl));
                if (alternate is not null && string.IsNullOrWhiteSpace(alternate.Poster))
                    await AdditionalScrapers.DownloadPosterAsync(alternate, videoId, DataDir);
            }
            var merged = NativeMetadataAggregation.AggregateExactCode(code, ordered);
            if (r18CodeAmbiguous)
                merged.DetailNote += "；r18.dev 离线目录中此番号对应多部作品，必须人工核对版本";
            if (failures.Count > 0)
                merged.DetailNote += $"；本次仅采用已成功的来源，{string.Join("、", failures.Select(item => item.Name))} 暂不可用，可稍后重试补全";
            if (notFound.Count > 0)
                merged.DetailNote += $"；{string.Join("、", notFound)} 未找到相同番号";
            return (merged, r18CodeAmbiguous);
        }
        // A blocked or temporarily unavailable source cannot prove that the
        // title is absent. Leave the item as failed/retryable instead of
        // marking the video as unmatched or amateur.
        if (failures.Count > 0)
        {
            if (queue) throw BuildBulkSourceFailure(failures, "番号详情");
            throw failures.Select(item => item.Error).OfType<ScrapeSourceUnavailableException>()
                .OrderBy(failure => failure.RetryAt ?? DateTimeOffset.UtcNow)
                .FirstOrDefault() ?? failures.Select(item => item.Error).FirstOrDefault(IsTemporaryScrapeFailure)
                ?? failures[0].Error;
        }
        throw new ScrapeNotFoundException("已查询启用的番号来源，没有找到完全匹配的番号。");
    }
}
