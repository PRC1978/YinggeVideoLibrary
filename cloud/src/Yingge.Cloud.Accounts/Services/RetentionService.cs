using Yingge.Cloud.Accounts.Data;
using Yingge.Cloud.Accounts.Storage;

namespace Yingge.Cloud.Accounts.Services;

internal sealed class RetentionService(
    ReportRepository reports,
    ReportFileStore files,
    AuditRepository audit,
    ILogger<RetentionService> logger)
{
    public async Task<int> PurgeExpiredAsync(int batchSize, CancellationToken ct)
    {
        var claimed = await reports.ClaimExpiredAsync(batchSize, ct);
        if (claimed.Count == 0) return 0;

        var removed = 0;
        foreach (var (_, storageKey) in claimed)
        {
            try
            {
                await files.DeleteAsync(storageKey, ct);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                logger.LogWarning(exception, "无法删除过期日志文件 {StorageKey}", storageKey);
            }
        }

        await audit.LogAsync("report.retention_purged", null, null,
            new { claimed = claimed.Count, files_removed = removed }, ct);
        logger.LogInformation("保留期清理：标记 {Claimed} 条，删除 {Removed} 个文件", claimed.Count, removed);
        return claimed.Count;
    }
}

internal sealed class RetentionWorker(RetentionService retention, ReportOptions options,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.SweepMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await retention.PurgeExpiredAsync(200, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "保留期清理任务失败，将在下一个周期重试。");
            }
        }
    }
}
