using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    const int MaxScrapeRequestsPerClick = 100;
    const int MaxScrapeAttempts = 4;
    static readonly TimeSpan ScrapeRequestSpacing = TimeSpan.FromSeconds(2.5);
    CancellationTokenSource? scrapeQueueCancellation;
    Task? scrapeQueueTask;
    bool scrapeQueuePaused = true;
    TextBlock? scrapeQueueInfo;
    Button? scrapeQueuePageButton;
    Button? scrapeQueueRetryButton;

    void InitializeScrapePollingUi()
    {
        ScrapeBatchButton.Content = "加入搜刮队列";
        if (ScrapePanel.Child is StackPanel panel)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16), LastChildFill = true };
            scrapeQueuePageButton = new Button
            {
                Content = "继续轮询", Style = (Style)FindResource("Action"),
                Margin = new Thickness(12, 0, 0, 0)
            };
            scrapeQueuePageButton.Click += CancelBatch;
            DockPanel.SetDock(scrapeQueuePageButton, Dock.Right);
            row.Children.Add(scrapeQueuePageButton);
            scrapeQueueRetryButton = new Button
            {
                Content = "重试失败", Style = (Style)FindResource("Action"),
                Margin = new Thickness(12, 0, 0, 0)
            };
            scrapeQueueRetryButton.Click += async (_, _) =>
            {
                try
                {
                    var count = ScrapeQueueStore.RetryFailures(DataDir);
                    Log($"已将 {count} 项失败搜刮重新排队");
                    await ResumeScrapePollingAsync();
                }
                catch (Exception ex) { Log("重试搜刮队列失败：" + ex.Message); }
            };
            DockPanel.SetDock(scrapeQueueRetryButton, Dock.Right);
            row.Children.Add(scrapeQueueRetryButton);
            scrapeQueueInfo = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(82, 100, 122)),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            row.Children.Add(scrapeQueueInfo);
            panel.Children.Insert(2, row);
        }
        UpdateScrapeQueueStatus();
    }

    void UpdateScrapeQueueStatus()
    {
        var queued = ScrapeQueueStore.Load(DataDir);
        var pendingCount = queued.Count(x => !x.Failed);
        var failedCount = queued.Count - pendingCount;
        var active = scrapeQueueTask is { IsCompleted: false } && !scrapeQueuePaused;
        var next = pendingCount == 0 ? "" : $" · 下次 {queued.Where(x => !x.Failed).Min(x => x.NextAttemptAt).ToLocalTime():HH:mm:ss}";
        if (scrapeQueueInfo is not null)
            scrapeQueueInfo.Text = pendingCount == 0 && failedCount == 0
                ? "搜刮队列为空。点击上方“加入搜刮队列”后按间隔逐个处理。"
                : $"待处理 {pendingCount} 项 · 失败 {failedCount} 项 · {(active ? "正在后台轮询" : "已暂停，点击继续才会联网")}{next}";
        var buttonText = active ? "暂停轮询" : "继续轮询";
        ScrapeQueuePauseButton.Content = buttonText;
        ScrapeQueuePauseButton.IsEnabled = pendingCount > 0;
        if (scrapeQueuePageButton is not null)
        {
            scrapeQueuePageButton.Content = buttonText;
            scrapeQueuePageButton.IsEnabled = pendingCount > 0;
        }
        if (scrapeQueueRetryButton is not null)
        {
            scrapeQueueRetryButton.IsEnabled = failedCount > 0;
            scrapeQueueRetryButton.ToolTip = failedCount == 0 ? null : string.Join("\n", queued.Where(x => x.Failed).Take(5).Select(x => $"{x.Code}：{x.LastError}"));
        }
    }

    async Task EnqueueScrapeBatchAsync()
    {
        try
        {
            if (JavDbEnabledBox.IsChecked != true)
            {
                MessageBox.Show(this, "请先在“搜刮”页启用 JavDB 番号搜刮。", "映格视频库", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var queued = ScrapeQueueStore.Load(DataDir);
            var known = queued.Select(x => x.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pending = videos.Where(v => !metadata.ContainsKey(v.Id) && !config.AiReviews.ContainsKey(v.Id) && !known.Contains(v.Id))
                .Select(v => new ScrapeQueueItem { VideoId = v.Id, Code = JavDbScraper.GuessCode(v.Title) ?? "" })
                .Where(x => x.Code.Length > 0).Take(MaxScrapeRequestsPerClick).ToList();
            if (pending.Count == 0 && queued.All(x => x.Failed))
            {
                var failed = queued.Count(x => x.Failed);
                var message = failed == 0
                    ? "没有可搜刮的视频。文件名需要包含类似 ABC-123 的番号。"
                    : $"没有新的可搜刮视频；已有 {failed} 项失败结果。需要重试时，请在“搜刮”页点击“重试失败”。";
                MessageBox.Show(this, message, "映格视频库", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (pending.Count > 0)
            {
                var added = ScrapeQueueStore.Add(DataDir, pending);
                Log($"已向搜刮队列加入 {added} 项；每次最多加入 {MaxScrapeRequestsPerClick} 项。");
            }
            await ResumeScrapePollingAsync();
        }
        catch (Exception ex)
        {
            Log("搜刮队列保存失败：" + ex.Message);
            TaskStatusText.Text = "搜刮队列保存失败";
        }
    }

    async Task ResumeScrapePollingAsync()
    {
        if (JavDbEnabledBox.IsChecked != true)
        {
            scrapeQueuePaused = true;
            TaskStatusText.Text = "JavDB 来源已关闭，无法启动搜刮队列";
            UpdateScrapeQueueStatus();
            return;
        }
        scrapeQueuePaused = false;
        if (scrapeQueueTask is { IsCompleted: false } previous && scrapeQueueCancellation?.IsCancellationRequested == true)
            try { await previous; } catch { }
        if (!scrapeQueuePaused) StartScrapePolling();
        UpdateScrapeQueueStatus();
    }

    void StartScrapePolling()
    {
        if (isClosing || scrapeQueueTask is { IsCompleted: false }) return;
        scrapeQueueCancellation = new CancellationTokenSource();
        scrapeQueueTask = PollScrapeQueueAsync(scrapeQueueCancellation.Token);
    }

    async Task PollScrapeQueueAsync(CancellationToken cancellationToken)
    {
        var approved = 0;
        var review = 0;
        var rejected = 0;
        var failed = 0;
        try
        {
            using var lease = ScrapeQueueStore.TryAcquireWorker(DataDir);
            if (lease is null)
            {
                scrapeQueuePaused = true;
                TaskStatusText.Text = "另一份映格正在轮询共享搜刮队列";
                Log(TaskStatusText.Text);
                return;
            }
            Log("后台搜刮轮询已启动");
            while (!isClosing && !scrapeQueuePaused)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (JavDbEnabledBox.IsChecked != true)
                {
                    scrapeQueuePaused = true;
                    TaskStatusText.Text = "JavDB 来源已关闭，搜刮队列已暂停";
                    break;
                }
                var queued = ScrapeQueueStore.Load(DataDir);
                var item = queued.Where(x => !x.Failed).OrderBy(x => x.NextAttemptAt).FirstOrDefault();
                if (item is null) break;
                var delay = item.NextAttemptAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    TaskStatusText.Text = $"搜刮队列 {queued.Count(x => !x.Failed)} 项 · {item.NextAttemptAt.ToLocalTime():HH:mm:ss} 继续";
                    await Task.Delay(delay < TimeSpan.FromSeconds(3) ? delay : TimeSpan.FromSeconds(3), cancellationToken);
                    continue;
                }
                if (metadata.ContainsKey(item.VideoId) || config.AiReviews.ContainsKey(item.VideoId))
                {
                    ScrapeQueueStore.Remove(DataDir, item.VideoId);
                    UpdateScrapeQueueStatus();
                    continue;
                }
                var video = videos.FirstOrDefault(x => string.Equals(x.Id, item.VideoId, StringComparison.OrdinalIgnoreCase));
                if (video is null)
                {
                    // An unavailable source drive must not discard queued work.
                    item.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(2);
                    item.LastError = "视频当前不在索引中，等待文件夹重新可用";
                    ScrapeQueueStore.Reschedule(DataDir, item);
                    Log("搜刮队列等待视频索引恢复：" + item.Code);
                    UpdateScrapeQueueStatus();
                    continue;
                }

                TaskStatusText.Text = $"轮询搜刮 {item.Code} · 队列余 {queued.Count(x => !x.Failed)} 项";
                try
                {
                    var candidate = await JavDbScraper.ScrapeAsync(item.Code, item.VideoId, DataDir, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var status = await StoreScrapeReviewAsync(video, candidate);
                    ScrapeQueueStore.Remove(DataDir, item.VideoId);
                    if (status == "approved") approved++;
                    else if (status == "pending") review++;
                    else rejected++;
                    Log($"轮询搜刮完成：{item.Code} · 审核状态 {status}");
                    if (view == "scrape") RefreshReviewQueue();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var retry = IsTemporaryScrapeFailure(ex) && item.Attempts + 1 < MaxScrapeAttempts;
                    if (retry)
                    {
                        item.Attempts++;
                        var retryDelay = ScrapeRetryDelay(item.Attempts, ex);
                        item.NextAttemptAt = DateTimeOffset.UtcNow.Add(retryDelay);
                        item.LastError = ex.Message.Length > 240 ? ex.Message[..240] : ex.Message;
                        ScrapeQueueStore.Reschedule(DataDir, item);
                        if (ex is ScrapeHttpException { StatusCode: HttpStatusCode.TooManyRequests })
                            ScrapeQueueStore.DelayAllUntil(DataDir, item.NextAttemptAt);
                        Log($"搜刮暂时失败：{item.Code} · {retryDelay.TotalSeconds:0} 秒后重试（{item.Attempts}/{MaxScrapeAttempts - 1}）· {ex.Message}");
                    }
                    else
                    {
                        failed++;
                        ScrapeQueueStore.MarkFailed(DataDir, item.VideoId, ex.Message);
                        Log($"搜刮失败：{item.Code} · {ex.Message}");
                    }
                }
                UpdateScrapeQueueStatus();
                await Task.Delay(ScrapeRequestSpacing, cancellationToken);
            }
            if (!isClosing && !scrapeQueuePaused)
            {
                TaskStatusText.Text = $"搜刮队列完成 · 自动通过 {approved} · 待审核 {review} · AI 否决 {rejected} · 失败 {failed}";
                Log(TaskStatusText.Text);
                if (approved + review + rejected > 0 && view is ("all" or "recent" or "favorites" or "categories")) RenderCards();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!isClosing) TaskStatusText.Text = "搜刮队列已暂停";
        }
        catch (Exception ex)
        {
            scrapeQueuePaused = true;
            TaskStatusText.Text = "搜刮队列中断，请检查运行消息后继续";
            Log("搜刮队列中断：" + ex.Message);
        }
        finally
        {
            scrapeQueueCancellation?.Dispose();
            scrapeQueueCancellation = null;
            if (!isClosing) UpdateScrapeQueueStatus();
        }
    }

    static bool IsTemporaryScrapeFailure(Exception ex) => ex switch
    {
        ScrapeHttpException http => http.StatusCode == HttpStatusCode.TooManyRequests || http.StatusCode == HttpStatusCode.RequestTimeout || (int?)http.StatusCode >= 500,
        HttpRequestException http => http.StatusCode is null || http.StatusCode == HttpStatusCode.RequestTimeout || (int?)http.StatusCode >= 500,
        TaskCanceledException => true,
        TimeoutException => true,
        _ => false
    };

    static TimeSpan ScrapeRetryDelay(int attempts, Exception ex)
    {
        var backoff = TimeSpan.FromSeconds(Math.Min(300, 10 * Math.Pow(2, attempts - 1)));
        if (ex is ScrapeHttpException { StatusCode: HttpStatusCode.TooManyRequests } rateLimit)
        {
            var minimum = TimeSpan.FromSeconds(30);
            if (rateLimit.RetryAfter is { } header && header > minimum) minimum = header;
            if (backoff < minimum) backoff = minimum;
        }
        return backoff;
    }

    void StopScrapePollingForClose() => scrapeQueueCancellation?.Cancel();
}
