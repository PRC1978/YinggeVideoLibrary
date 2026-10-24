using System.IO;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    bool HasEnabledCodeSource => JavDbEnabledBox.IsChecked == true || javDatabaseEnabledBox?.IsChecked == true;

    async Task<MovieMetadata> ScrapeCodeFromEnabledSourcesAsync(string code, string videoId, CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        var attempted = 0;
        if (JavDbEnabledBox.IsChecked == true)
        {
            attempted++;
            try
            {
                var record = await JavDbScraper.ScrapeAsync(code, videoId, DataDir, cancellationToken);
                if (record.DetailFetched) return record;
                failures.Add(new InvalidDataException("JavDB 未返回完整详情：" + record.DetailNote));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ScrapeNotFoundException) { }
            catch (Exception ex) { failures.Add(ex); Log("JavDB 本次不可用：" + ex.Message); }
        }
        if (javDatabaseEnabledBox?.IsChecked == true)
        {
            attempted++;
            try
            {
                var record = await JavDatabaseScraper.ScrapeByCodeAsync(code, cancellationToken);
                if (!record.DetailFetched) throw new InvalidDataException("JAVDatabase 未返回完整详情：" + record.DetailNote);
                await AdditionalScrapers.DownloadPosterAsync(record, videoId, DataDir);
                return record;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ScrapeNotFoundException) { }
            catch (Exception ex) { failures.Add(ex); Log("JAVDatabase 本次不可用：" + ex.Message); }
        }
        if (attempted == 0) throw new InvalidOperationException("请先在搜刮页启用至少一个番号来源。");
        // A blocked or temporarily unavailable source cannot prove that the
        // title is absent. Leave the item as failed/retryable instead of
        // marking the video as unmatched or amateur.
        if (failures.Count > 0)
            throw failures.FirstOrDefault(IsTemporaryScrapeFailure) ?? failures[0];
        throw new ScrapeNotFoundException("已查询启用的番号来源，没有找到完全匹配的番号。");
    }
}
