using System.IO;
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
    const int MaxScrapeAttempts = 4;
    TimeSpan ScrapeRequestSpacing => TimeSpan.FromSeconds(double.IsFinite(config.ScrapeRequestIntervalSeconds)
        ? Math.Clamp(config.ScrapeRequestIntervalSeconds, 2.5, 30) : 2.5);
    ScrapeSourcePolicy PolicyForSource(string source) => ScrapeSourcePolicy.Normalize(
        config.ScrapeSourcePolicies.GetValueOrDefault(source),
        ScrapeSourcePolicy.DefaultIntervalFor(source, config.ScrapeRequestIntervalSeconds));
    string[] EnabledQueueCodeSources()
    {
        return PlanQueueCodeSources(JavDbEnabledBox.IsChecked == true,
            javDatabaseEnabledBox?.IsChecked == true);
    }

    string[] EnabledQueueTitleSources()
    {
        return PlanQueueTitleSources(TmdbEnabledBox.IsChecked == true && !string.IsNullOrWhiteSpace(config.TmdbToken),
            PornhubEnabledBox.IsChecked == true, tvMazeEnabledBox?.IsChecked == true,
            bangumiEnabledBox?.IsChecked == true, javDbTitleEnabledBox?.IsChecked == true,
            javDatabaseEnabledBox?.IsChecked == true, wikidataEnabledBox?.IsChecked == true,
            kitsuEnabledBox?.IsChecked == true, tenraiEnabledBox?.IsChecked == true);
    }

    internal static string[] PlanQueueCodeSources(bool javDb, bool javDatabase)
    {
        var sources = new List<string>(2);
        if (javDb) sources.Add("JavDB");
        if (javDatabase) sources.Add("JAVDatabase");
        return [.. sources];
    }

    internal static string[] PlanQueueTitleSources(bool tmdb, bool pornHub, bool tvMaze,
        bool bangumi, bool javDb, bool javDatabase, bool wikidata = false, bool kitsu = false,
        bool tenrai = false)
    {
        var sources = new List<string>(7);
        if (tmdb) sources.Add("TMDB");
        if (pornHub) sources.Add("Pornhub");
        if (tvMaze) sources.Add("TVmaze");
        if (bangumi) sources.Add("Bangumi");
        if (javDb) sources.Add("JavDB");
        if (javDatabase) sources.Add("JAVDatabase");
        if (wikidata) sources.Add("Wikidata");
        if (kitsu) sources.Add("Kitsu");
        if (tenrai) sources.Add("Tenrai");
        // Javinfo is paid/manual; AniList disallows mass collection.
        return [.. sources];
    }

    async Task<T> RunQueueSourceAsync<T>(string source, Func<Task<T>> request, CancellationToken cancellationToken)
    {
        var policy = PolicyForSource(source);
        while (true)
        {
            var readyAt = await Task.Run(() => ScrapeQueueStore.ReserveSourceRequest(DataDir, source,
                TimeSpan.FromSeconds(policy.IntervalSeconds)), cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (readyAt <= now) break;
            // Search and detail may legitimately query the same source twice
            // within one item. Wait for the pacing slot, but leave a genuinely
            // failing/429 source for another source or the next polling pass.
            var cooldownAt = await Task.Run(() => ScrapeQueueStore.GetSourceCooldownAt(DataDir, source),
                cancellationToken);
            if (cooldownAt > now)
                throw new ScrapeSourceUnavailableException($"{source} 来源限速冷却中。", cooldownAt);
            await Task.Delay(readyAt - now, cancellationToken);
        }
        try
        {
            var result = await request().WaitAsync(cancellationToken);
            await Task.Run(() => ScrapeQueueStore.ReportSourceSuccess(DataDir, source), cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ScrapeNotFoundException)
        {
            await Task.Run(() => ScrapeQueueStore.ReportSourceSuccess(DataDir, source), cancellationToken);
            throw;
        }
        catch (Exception ex) when (IsTemporaryScrapeFailure(ex))
        {
            var limited = ex is ScrapeHttpException { StatusCode: HttpStatusCode.TooManyRequests }
                || ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
            var retryAfter = ex is ScrapeHttpException rateLimit ? rateLimit.RetryAfter : null;
            var retryAt = await Task.Run(() => ScrapeQueueStore.RecordSourceFailure(
                DataDir, source, ex.Message, limited, retryAfter,
                policy.ConsecutiveFailureLimit), cancellationToken);
            throw new ScrapeSourceUnavailableException($"{source} 暂时不可用：{ex.Message}", retryAt);
        }
    }
    CancellationTokenSource? scrapeQueueCancellation;
    Task? scrapeQueueTask;
    bool scrapeQueuePaused = true;
    TextBlock? scrapeQueueInfo;
    Button? scrapeQueuePageButton;
    Button? scrapeQueueRetryButton;
    Button? scrapeQueueClearButton;
    bool scrapeQueueChanging;
    long scrapeQueueStatusRevision;

    void InitializeScrapePollingUi()
    {
        ScrapeBatchButton.Content = "全部符合条件入队";
        ScrapeBatchButton.ToolTip = "有番号的视频优先使用启用的番号来源；其他视频按标题搜索公开来源。勾选“仅已选”时只处理选中视频";
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
                if (scrapeQueueChanging) return;
                scrapeQueueChanging = true;
                try
                {
                    var count = await Task.Run(() => ScrapeQueueStore.RetryFailures(DataDir));
                    Log($"已将 {count} 项失败搜刮重新排队");
                    await ResumeScrapePollingAsync();
                }
                catch (Exception ex) { Log("重试搜刮队列失败：" + ex.Message); }
                finally { scrapeQueueChanging = false; UpdateScrapeQueueStatus(); }
            };
            DockPanel.SetDock(scrapeQueueRetryButton, Dock.Right);
            row.Children.Add(scrapeQueueRetryButton);
            scrapeQueueClearButton = new Button
            {
                Content = "取消待处理", Style = (Style)FindResource("Action"),
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip = "停止当前轮询并移除尚未处理的任务；失败记录及已保存的资料保持不变"
            };
            scrapeQueueClearButton.Click += async (_, _) => await CancelPendingScrapeQueueAsync();
            DockPanel.SetDock(scrapeQueueClearButton, Dock.Right);
            row.Children.Add(scrapeQueueClearButton);
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

    async void UpdateScrapeQueueStatus()
    {
        var revision = Interlocked.Increment(ref scrapeQueueStatusRevision);
        (int PendingCount, int FailedCount, DateTimeOffset? NextAttemptAt, List<(string Query, string Error)> FailedExamples) status;
        var codeSources = EnabledQueueCodeSources();
        var titleSources = EnabledQueueTitleSources();
        try { status = await Task.Run(() => ScrapeQueueStore.GetStatus(DataDir, codeSources, titleSources)); }
        catch (Exception ex)
        {
            if (!isClosing) Log("读取搜刮队列状态失败：" + ex.Message);
            return;
        }
        if (isClosing || revision != Interlocked.Read(ref scrapeQueueStatusRevision)) return;
        var pendingCount = status.PendingCount;
        var failedCount = status.FailedCount;
        var active = scrapeQueueTask is { IsCompleted: false } && !scrapeQueuePaused;
        var next = status.NextAttemptAt is null ? "" : $" · 下次 {status.NextAttemptAt.Value.ToLocalTime():HH:mm:ss}";
        if (scrapeQueueInfo is not null)
            scrapeQueueInfo.Text = pendingCount == 0 && failedCount == 0
                ? "搜刮队列为空。点击“全部符合条件入队”后，有番号的走番号来源，其余按标题查找，后台逐项处理。"
                : $"待处理 {pendingCount} 项 · 失败 {failedCount} 项 · {(active ? "正在后台轮询" : "已暂停，点击继续才会联网")}{next}"
                  + (pendingCount > 0 ? $" · 最少约 {FormatScrapeMinimumDuration(pendingCount)}（不含网络耗时）" : "");
        var buttonText = active ? "暂停轮询" : "继续轮询";
        ScrapeQueuePauseButton.Content = buttonText;
        ScrapeQueuePauseButton.IsEnabled = pendingCount > 0 && !scrapeQueueChanging;
        if (scrapeQueuePageButton is not null)
        {
            scrapeQueuePageButton.Content = buttonText;
            scrapeQueuePageButton.IsEnabled = pendingCount > 0 && !scrapeQueueChanging;
        }
        if (scrapeQueueRetryButton is not null)
        {
            scrapeQueueRetryButton.IsEnabled = failedCount > 0 && !scrapeQueueChanging;
            scrapeQueueRetryButton.ToolTip = failedCount == 0 ? null : string.Join("\n", status.FailedExamples.Select(x => $"{x.Query}：{x.Error}"));
        }
        if (scrapeQueueClearButton is not null)
            scrapeQueueClearButton.IsEnabled = pendingCount > 0 && !scrapeQueueChanging;
    }

    string FormatScrapeMinimumDuration(int count)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, count - 1) * ScrapeRequestSpacing.TotalSeconds);
        if (span.TotalDays >= 1) return $"{span.TotalDays:0.#} 天";
        if (span.TotalHours >= 1) return $"{span.TotalHours:0.#} 小时";
        if (span.TotalMinutes >= 1) return $"{span.TotalMinutes:0.#} 分钟";
        return $"{span.TotalSeconds:0} 秒";
    }

    async Task CancelPendingScrapeQueueAsync()
    {
        if (scrapeQueueChanging) return;
        int pending;
        try { pending = (await Task.Run(() => ScrapeQueueStore.GetStatus(DataDir))).PendingCount; }
        catch (Exception ex)
        {
            MessageBox.Show(this, "读取搜刮队列失败：" + ex.Message, "取消待处理队列", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (pending == 0) { UpdateScrapeQueueStatus(); return; }
        if (MessageBox.Show(this,
                $"要停止轮询并取消 {pending} 项待处理搜刮吗？\n\n失败记录仍可单独重试；已经搜刮完成并保存的资料不会删除。",
                "取消待处理队列", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        scrapeQueueChanging = true;
        scrapeQueuePaused = true;
        scrapeQueueCancellation?.Cancel();
        ScrapeBatchButton.IsEnabled = false;
        ScrapeQueuePauseButton.IsEnabled = false;
        if (scrapeQueuePageButton is not null) scrapeQueuePageButton.IsEnabled = false;
        if (scrapeQueueRetryButton is not null) scrapeQueueRetryButton.IsEnabled = false;
        if (scrapeQueueClearButton is not null) scrapeQueueClearButton.IsEnabled = false;
        TaskStatusText.Text = "正在停止搜刮并取消待处理任务；当前网络请求可能需要片刻返回…";
        try
        {
            if (scrapeQueueTask is { IsCompleted: false } running)
                try { await running; } catch (OperationCanceledException) { }
            var removed = await Task.Run(() => ScrapeQueueStore.ClearPending(DataDir));
            TaskStatusText.Text = $"已取消 {removed} 项待处理搜刮；失败记录和已完成资料已保留";
            Log(TaskStatusText.Text);
        }
        catch (Exception ex)
        {
            TaskStatusText.Text = "取消搜刮队列失败";
            Log("取消搜刮队列失败：" + ex.Message);
            MessageBox.Show(this, ex.Message, "取消搜刮队列失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            scrapeQueueChanging = false;
            ScrapeBatchButton.IsEnabled = true;
            UpdateScrapeQueueStatus();
        }
    }

    internal readonly record struct PlannedScrapeItem(VideoItem Video, ScrapeQueueItem? Item,
        bool UsedFolderTitle, bool NeedsManualTitle);

    // Only the immutable identity, title and path of VideoItem are read here.
    // Capture UI settings first, then plan large batches away from the UI thread.
    internal static PlannedScrapeItem PlanScrapeQueueItem(VideoItem video, bool hasCodeSource, bool useFolderHint,
        string[] libraryFolders, bool refreshExisting = false, string? acceptedCode = null, string? acceptedTitle = null)
    {
        var fileCode = hasCodeSource ? JavDbScraper.GuessCode(video.Title) : null;
        var savedCode = refreshExisting && hasCodeSource ? JavDbScraper.GuessCode(acceptedCode ?? "") : null;
        var conflictingCodes = fileCode is not null && savedCode is not null &&
            !ScrapeCodeIdentity.Same(fileCode, savedCode);
        var code = conflictingCodes ? null : savedCode ?? fileCode;
        if (!string.IsNullOrWhiteSpace(code))
            return new(video, new ScrapeQueueItem { VideoId = video.Id, Kind = "code", Code = code,
                RefreshExisting = refreshExisting }, false, false);
        var title = refreshExisting && !string.IsNullOrWhiteSpace(acceptedTitle)
            ? AdditionalScrapers.CleanTitle(acceptedTitle).Trim()
            : AdditionalScrapers.CleanTitle(video.Title).Trim();
        var folderTitle = useFolderHint && ScrapeSearchHints.IsWeakTitle(title)
            ? ScrapeSearchHints.FindWorkTitleHint(video.Path, libraryFolders) : null;
        if (folderTitle is not null) title = folderTitle;
        if (title.Length == 0) title = video.Title.Trim();
        return new(video, title.Length == 0 ? null : new ScrapeQueueItem { VideoId = video.Id, Kind = "title",
            SearchTitle = title, RefreshExisting = refreshExisting },
            folderTitle is not null, conflictingCodes || ScrapeSearchHints.IsWeakTitle(title));
    }

    async Task EnqueueScrapeBatchAsync()
    {
        if (scrapeQueueChanging) return;
        scrapeQueueChanging = true;
        ScrapeBatchButton.IsEnabled = false;
        try
        {
            if (!ReadScrapeFilterSettings()) return;
            var queued = await Task.Run(() => ScrapeQueueStore.Load(DataDir));
            var known = queued.Select(x => x.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var refreshExisting = scrapeRefreshExistingBox?.IsChecked == true;
            var candidates = videos.Where(v => CanQueueForFullMetadata(v, refreshExisting) && !known.Contains(v.Id))
                .Where(v => scrapeOnlySelectedBox?.IsChecked != true || selectedVideoIds.Contains(v.Id))
                .Where(v => ScrapeSkipReason(v) is null).ToArray();
            var hasCodeSource = HasEnabledCodeSource;
            var useFolderHint = config.UseFolderPersonHint;
            var libraryFolders = config.Folders.ToArray();
            TaskStatusText.Text = $"正在准备 {candidates.Length} 项搜刮任务…";
            var inputs = candidates.Select(video =>
            {
                var accepted = metadata.GetValueOrDefault(video.Id);
                return (video, refresh: refreshExisting && accepted is not null,
                    code: accepted?.Code, title: accepted?.Title);
            }).ToArray();
            var plans = await Task.Run(() => inputs.Select(input =>
                PlanScrapeQueueItem(input.video, hasCodeSource, useFolderHint, libraryFolders,
                    input.refresh, input.code, input.title)).ToList());
            var pending = plans.Select(plan => plan.Item).OfType<ScrapeQueueItem>().ToList();
            if (pending.Count == 0 && queued.All(x => x.Failed))
            {
                var failed = queued.Count(x => x.Failed);
                var message = failed == 0
                    ? "没有符合条件、尚待搜刮的视频；已完成或待审核的视频不会重复入队。"
                    : $"没有新的可搜刮视频；已有 {failed} 项失败结果。需要重试时，请在“搜刮”页点击“重试失败”。";
                MessageBox.Show(this, message, "映格视频库", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (pending.Count > 0)
            {
                var refreshCount = pending.Count(item => item.RefreshExisting);
                if ((pending.Count > 100 || refreshCount > 0) && MessageBox.Show(this,
                        $"本次将加入全部 {pending.Count} 项符合条件的视频（番号 {pending.Count(item => item.Kind == "code")} 项，标题 {pending.Count(item => item.Kind == "title")} 项；其中已存资料补全 {refreshCount} 项）。\n后台会把番号或标题发送到已启用的公开资料来源，逐项限速联网。按当前间隔至少约 {FormatScrapeMinimumDuration(pending.Count)}，实际还包含网络耗时。\n\n补全结果进入人工审核；确认前不会覆盖已保存字段。可随时暂停或取消待处理队列。是否继续？",
                        "确认批量搜刮", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                TaskStatusText.Text = $"正在保存 {pending.Count} 项搜刮任务…";
                var added = await Task.Run(() => ScrapeQueueStore.Add(DataDir, pending));
                Log($"已向搜刮队列加入全部 {added} 项符合条件的视频（番号/标题），按间隔逐项联网。");
                if (added > 0 && scrapeRefreshExistingBox is not null) scrapeRefreshExistingBox.IsChecked = false;
            }
            await ResumeScrapePollingAsync();
        }
        catch (Exception ex)
        {
            Log("搜刮队列保存失败：" + ex.Message);
            TaskStatusText.Text = "搜刮队列保存失败";
        }
        finally
        {
            scrapeQueueChanging = false;
            ScrapeBatchButton.IsEnabled = true;
            UpdateScrapeQueueStatus();
        }
    }

    async Task ResumeScrapePollingAsync()
    {
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
            List<VideoItem>? indexedVideos = null;
            Dictionary<string, VideoItem> videoById = new(StringComparer.OrdinalIgnoreCase);
            while (!isClosing && !scrapeQueuePaused)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var codeSources = EnabledQueueCodeSources();
                var titleSources = EnabledQueueTitleSources();
                var (item, pendingCount, hasBlocked) = await Task.Run(
                    () => ScrapeQueueStore.PeekNextFair(DataDir, codeSources, titleSources), cancellationToken);
                if (item is null)
                {
                    if (hasBlocked)
                    {
                        scrapeQueuePaused = true;
                        TaskStatusText.Text = "剩余任务没有可用来源；请在搜刮页启用相应来源后继续，任务已保留";
                    }
                    break;
                }
                var delay = item.NextAttemptAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    TaskStatusText.Text = $"搜刮队列 {pendingCount} 项 · {item.NextAttemptAt.ToLocalTime():HH:mm:ss} 继续";
                    await Task.Delay(delay < TimeSpan.FromSeconds(3) ? delay : TimeSpan.FromSeconds(3), cancellationToken);
                    continue;
                }
                if (!ReferenceEquals(indexedVideos, videos))
                {
                    indexedVideos = videos;
                    videoById = new Dictionary<string, VideoItem>(StringComparer.OrdinalIgnoreCase);
                    foreach (var indexedVideo in indexedVideos)
                        if (!string.IsNullOrWhiteSpace(indexedVideo.Id)) videoById.TryAdd(indexedVideo.Id, indexedVideo);
                }
                videoById.TryGetValue(item.VideoId, out var video);
                if (video is not null && !CanQueueForFullMetadata(video, item.RefreshExisting))
                {
                    await Task.Run(() => ScrapeQueueStore.Remove(DataDir, item.VideoId));
                    UpdateScrapeQueueStatus();
                    continue;
                }
                if (video is null)
                {
                    // An unavailable source drive must not discard queued work.
                    item.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(2);
                    item.LastError = "视频当前不在索引中，等待文件夹重新可用";
                    await Task.Run(() => ScrapeQueueStore.Reschedule(DataDir, item));
                    Log("搜刮队列等待视频索引恢复：" + item.DisplayQuery);
                    UpdateScrapeQueueStatus();
                    continue;
                }
                if (ScrapeSkipReason(video) is { } skipReason)
                {
                    await Task.Run(() => ScrapeQueueStore.Remove(DataDir, item.VideoId));
                    Log($"已跳过搜刮：{video.Title} · {skipReason}");
                    UpdateScrapeQueueStatus();
                    continue;
                }

                TaskStatusText.Text = $"轮询搜刮 {item.DisplayQuery} · 队列余 {pendingCount} 项";
                try
                {
                    string status;
                    if (item.Kind == "title")
                    {
                        status = await ProcessTitleScrapeQueueItemAsync(item, video, cancellationToken);
                    }
                    else
                    {
                        var candidate = await ScrapeCodeFromEnabledSourcesAsync(item.Code, item.VideoId, cancellationToken, queue: true);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!await Task.Run(() => ScrapeQueueStore.HasPending(DataDir, item.VideoId)))
                        {
                            Log("搜刮任务已在另一份程序中取消：" + item.DisplayQuery);
                            continue;
                        }
                        if (metadata.ContainsKey(video.Id) && !candidate.DetailFetched)
                            throw new InvalidDataException("旧资料保留；新详情暂不可读：" + candidate.DetailNote);
                        status = await StoreScrapeReviewAsync(video, candidate,
                            forcePending: item.RefreshExisting, cancellationToken: cancellationToken);
                    }
                    await Task.Run(() => ScrapeQueueStore.Remove(DataDir, item.VideoId));
                    if (status == "approved") approved++;
                    else if (status == "pending") review++;
                    else rejected++;
                    Log($"轮询搜刮完成：{item.DisplayQuery} · 审核状态 {status}");
                    if (view == "scrape") RefreshReviewQueue();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException ex)
                {
                    if (!await Task.Run(() => ScrapeQueueStore.HasPending(DataDir, item.VideoId)))
                    {
                        Log("搜刮任务已在另一份程序中取消：" + item.DisplayQuery);
                        continue;
                    }
                    if (await HandleScrapeItemFailureAsync(item, ex)) failed++;
                }
                catch (ScrapeNotFoundException ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await Task.Run(() => ScrapeQueueStore.HasPending(DataDir, item.VideoId), cancellationToken))
                    {
                        Log("搜刮任务已在另一份程序中取消：" + item.DisplayQuery);
                        continue;
                    }
                    if (metadata.ContainsKey(video.Id))
                    {
                        // A failed attempt to enrich a legacy record must not
                        // replace its previously approved metadata or review.
                        await Task.Run(() => ScrapeQueueStore.MarkFailed(DataDir, item.VideoId, "旧资料保留；新详情未找到：" + ex.Message));
                        failed++;
                        Log("旧资料已保留，补全详情未找到：" + item.DisplayQuery);
                    }
                    else
                    {
                        StoreUnmatchedScrape(video, item.Kind == "title" ? "标题搜刮" : "番号来源", ex.Message);
                        await Task.Run(() => ScrapeQueueStore.Remove(DataDir, item.VideoId));
                        rejected++;
                    }
                }
                catch (Exception ex)
                {
                    if (await HandleScrapeItemFailureAsync(item, ex)) failed++;
                }
                UpdateScrapeQueueStatus();
                await Task.Delay(ScrapeRequestSpacing, cancellationToken);
            }
            if (!isClosing && !scrapeQueuePaused)
            {
                TaskStatusText.Text = $"搜刮队列完成 · 自动通过 {approved} · 待审核 {review} · 未匹配/否决 {rejected} · 失败 {failed}";
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

    async Task<bool> HandleScrapeItemFailureAsync(ScrapeQueueItem item, Exception ex)
    {
        var retry = ShouldRetryScrapeItem(item, ex);
        if (retry)
        {
            // A rate-limited or temporarily unavailable source owns its own
            // cooldown. It must not exhaust this video's finite retry budget.
            var sourceUnavailable = ex is ScrapeSourceUnavailableException;
            item.Attempts = AttemptsAfterRetry(item.Attempts, ex);
            var retryDelay = ScrapeRetryDelay(Math.Max(1, item.Attempts), ex);
            item.NextAttemptAt = DateTimeOffset.UtcNow.Add(retryDelay);
            item.LastError = ex.Message.Length > 240 ? ex.Message[..240] : ex.Message;
            await Task.Run(() => ScrapeQueueStore.Reschedule(DataDir, item));
            Log(sourceUnavailable
                ? $"搜刮来源冷却：{item.DisplayQuery} · {retryDelay.TotalSeconds:0} 秒后重试（未消耗视频重试次数）· {ex.Message}"
                : $"搜刮暂时失败：{item.DisplayQuery} · {retryDelay.TotalSeconds:0} 秒后重试（{item.Attempts}/{MaxScrapeAttempts - 1}）· {ex.Message}");
            return false;
        }
        await Task.Run(() => ScrapeQueueStore.MarkFailed(DataDir, item.VideoId, ex.Message));
        Log($"搜刮失败：{item.DisplayQuery} · {ex.Message}");
        return true;
    }

    static bool ShouldRetryScrapeItem(ScrapeQueueItem item, Exception ex) =>
        ex is ScrapeSourceUnavailableException ||
        IsTemporaryScrapeFailure(ex) && item.Attempts + 1 < MaxScrapeAttempts;

    static int AttemptsAfterRetry(int attempts, Exception ex) =>
        ex is ScrapeSourceUnavailableException ? attempts : attempts + 1;

    internal static bool VerifySourceRetryPolicyForDiagnostics()
    {
        var exhausted = new ScrapeQueueItem { VideoId = "test", Kind = "title", SearchTitle = "title", Attempts = MaxScrapeAttempts - 1 };
        return ShouldRetryScrapeItem(exhausted,
                new ScrapeSourceUnavailableException("source cooling", DateTimeOffset.UtcNow.AddMinutes(1)))
            && AttemptsAfterRetry(exhausted.Attempts, new ScrapeSourceUnavailableException("source cooling")) == exhausted.Attempts
            && !ShouldRetryScrapeItem(exhausted, new HttpRequestException("network unavailable"))
            && !ShouldRetryScrapeItem(new ScrapeQueueItem { VideoId = "test", Kind = "title", SearchTitle = "title" },
                new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized));
    }

    static bool IsTemporaryScrapeFailure(Exception ex) => ex switch
    {
        ScrapeHttpException http => http.StatusCode == HttpStatusCode.TooManyRequests || http.StatusCode == HttpStatusCode.RequestTimeout || (int?)http.StatusCode >= 500,
        ScrapeSourceUnavailableException => true,
        HttpRequestException http => http.StatusCode is null || http.StatusCode == HttpStatusCode.TooManyRequests
            || http.StatusCode == HttpStatusCode.RequestTimeout || (int?)http.StatusCode >= 500,
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
        if (ex is ScrapeSourceUnavailableException { RetryAt: { } retryAt })
        {
            var until = retryAt - DateTimeOffset.UtcNow;
            if (until > backoff) backoff = until;
        }
        return backoff;
    }

    void StopScrapePollingForClose() => scrapeQueueCancellation?.Cancel();
}
