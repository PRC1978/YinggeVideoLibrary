using System.IO;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // Title matches are less reliable than an exact catalog code. The queue
    // obtains the best candidate's source details, then leaves the result in
    // review rather than accepting a plausible but unverified title match.
    async Task<string> ProcessTitleScrapeQueueItemAsync(
        ScrapeQueueItem item, VideoItem video, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var title = AdditionalScrapers.CleanTitle(item.SearchTitle);
        if (!Regex.IsMatch(title, @"[\u3400-\u9FFF]|\p{L}{2,}"))
            throw new InvalidDataException("文件名缺少可搜索的文字标题；请先手动补充标题后再搜刮。");

        var candidates = await SearchCandidatesAsync(title, 3).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count == 0)
            throw new ScrapeNotFoundException("已查询启用的公开来源，没有找到标题候选。");

        var chosen = candidates
            .Select(candidate => (candidate,
                score: Math.Clamp(LocalScrapeConfidence(video, candidate, out _) +
                                  SourceTrust(candidate.Source), 0, 100)))
            .OrderByDescending(result => result.score)
            .ThenBy(result => result.candidate.Title, StringComparer.OrdinalIgnoreCase)
            .First().candidate;

        // A single source detail request per video keeps large queues bounded;
        // repeat titles share the existing three-day candidate/detail cache.
        var details = await ScrapeCandidateCache.GetOrFetchAsync(
            DataDir, chosen.Source + " 详情", chosen.SourceUrl,
            async () => [await AdditionalScrapers.FetchFullDetailsAsync(chosen, config.TmdbToken)])
            .WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var record = details.FirstOrDefault()
            ?? throw new InvalidOperationException("来源未返回标题候选详情，请稍后重试。");

        await AdditionalScrapers.DownloadPosterAsync(record, video.Id, DataDir).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Task.Run(() => ScrapeQueueStore.HasPending(DataDir, item.VideoId), cancellationToken))
            throw new OperationCanceledException("标题搜刮任务已从队列取消。", cancellationToken);

        var status = await StoreScrapeReviewAsync(video, record, forcePending: true, cancellationToken: cancellationToken);
        Log($"标题搜刮完成：{video.Title} · {record.Source} · {status}");
        return status;
    }
}
