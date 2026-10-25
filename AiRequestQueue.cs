using System.Collections.Concurrent;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Classification, tag suggestions and scrape review share one durable budget.
// Budget writes are serialized briefly; network requests use separate local
// and cloud slots so a slow provider cannot block an unrelated endpoint.
internal static class AiRequestQueue
{
    static readonly ConcurrentDictionary<string, CancellationTokenSource> ActiveJobs = new(StringComparer.Ordinal);
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };
    // Named Mutex slots recover automatically if a process exits mid-request.
    // Unlike a named Semaphore, they cannot stay consumed after a crash.
    static readonly Mutex[] LocalSlots = CreateSlots("Local");
    static readonly Mutex[] CloudSlots = CreateSlots("Cloud");
    static readonly Mutex BudgetGate = new(false, "Local\\YinggeVideoLibrary.AiBudgetV3");
    static Mutex[] CreateSlots(string channel) => Enumerable.Range(0, 4)
        .Select(index => new Mutex(false, $"Local\\YinggeVideoLibrary.Ai{channel}Slot{index}"))
        .ToArray();
    public sealed class BudgetLedger
    {
        public string Day { get; set; } = "";
        // Backward-compatible amount used for the daily ceiling and backup merge.
        public double EstimatedUsd { get; set; }
        public double ReportedUsageUsd { get; set; }
        public double UnverifiedUsd { get; set; }
        public int AttemptCount { get; set; }
        public int ReportedUsageCount { get; set; }
        // These are conservative budget estimates for attempts without provider usage.
        // InputTokens/OutputTokens below contain only provider-reported values.
        public long UnverifiedInputEstimateTokens { get; set; }
        public long UnverifiedOutputLimitTokens { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        // NextAllowedUtc is retained for profiles written before per-channel pacing.
        public DateTimeOffset NextAllowedUtc { get; set; }
        public DateTimeOffset NextLocalAllowedUtc { get; set; }
        public DateTimeOffset NextCloudAllowedUtc { get; set; }
    }

    public static bool CanUse(LibraryConfig config)
    {
        if (!Uri.TryCreate(config.AiEndpoint, UriKind.Absolute, out var uri)) return false;
        return !string.IsNullOrWhiteSpace(config.AiKey) || uri.IsLoopback;
    }

    internal static BudgetLedger LoadBudgetLedger(string dataDir)
    {
        var ledgerPath = Path.Combine(dataDir, "ai-budget.json");
        if (!File.Exists(ledgerPath)) return new BudgetLedger();
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(ledgerPath));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty(nameof(BudgetLedger.Day), out var dayValue) ||
                dayValue.ValueKind != JsonValueKind.String ||
                !DateOnly.TryParseExact(dayValue.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _) ||
                !json.RootElement.TryGetProperty(nameof(BudgetLedger.EstimatedUsd), out var costValue) ||
                costValue.ValueKind != JsonValueKind.Number || !costValue.TryGetDouble(out var cost) ||
                !double.IsFinite(cost) || cost < 0)
                throw new InvalidDataException("AI 用量记录格式不正确。");
            var ledger = json.RootElement.Deserialize<BudgetLedger>()
                ?? throw new InvalidDataException("AI 用量记录内容为空。");
            if (!double.IsFinite(ledger.ReportedUsageUsd) || ledger.ReportedUsageUsd < 0 ||
                !double.IsFinite(ledger.UnverifiedUsd) || ledger.UnverifiedUsd < 0 ||
                ledger.AttemptCount < 0 || ledger.ReportedUsageCount < 0 ||
                ledger.ReportedUsageCount > ledger.AttemptCount ||
                ledger.UnverifiedInputEstimateTokens < 0 || ledger.UnverifiedOutputLimitTokens < 0 ||
                ledger.InputTokens < 0 || ledger.OutputTokens < 0 ||
                ledger.EstimatedUsd + 0.000000001d < ledger.ReportedUsageUsd + ledger.UnverifiedUsd)
                throw new InvalidDataException("AI 用量记录包含无效金额或次数。");
            return ledger;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("AI 用量记录无法读取；为保护费用上限，已暂停 AI 请求。", ex);
        }
    }

    static void SaveBudgetLedger(string dataDir, BudgetLedger ledger)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "ai-budget.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(ledger));
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    // This is deliberately a conservative *budget estimate*, not a tokenizer.
    // One UTF-8 byte per input token plus framing margin is conservative for
    // common English and Chinese requests, but provider-side additions can vary.
    // Provider usage, when present and plausible, replaces this reservation.
    internal static long EstimateInputTokens(string body) =>
        Math.Max(1L, (long)Encoding.UTF8.GetByteCount(body) + 64L);

    internal static long EstimateOutputLimitTokens(string body, LibraryConfig config)
    {
        if (config.AiMaxOutputTokens < 1)
            throw new InvalidOperationException("AI 最大输出 Token 数必须大于零。");
        using var request = JsonDocument.Parse(body);
        if (request.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("AI 请求正文不是有效对象。");
        long limit = config.AiMaxOutputTokens;
        foreach (var name in new[] { "max_tokens", "max_completion_tokens", "max_output_tokens" })
        {
            if (!request.RootElement.TryGetProperty(name, out var item)) continue;
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var requested) || requested < 1)
                throw new InvalidDataException("AI 请求的最大输出 Token 数无效。");
            limit = Math.Max(limit, requested);
        }
        return limit;
    }

    internal static double EstimateReservedUsd(LibraryConfig config, string body) =>
        Cost(EstimateInputTokens(body), EstimateOutputLimitTokens(body, config), config);

    static double Cost(long inputTokens, long outputTokens, LibraryConfig config) =>
        (inputTokens * config.AiInputUsdPerMillion +
            outputTokens * config.AiOutputUsdPerMillion) / 1_000_000d;

    internal static bool TryReadReportedTokens(JsonDocument document, out long input, out long output)
    {
        input = 0; output = 0;
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object) return false;
        static bool Read(JsonElement node, string first, string alternate, out long value)
        {
            value = 0;
            var found = node.TryGetProperty(first, out var item) ||
                node.TryGetProperty(alternate, out item);
            return found && item.ValueKind == JsonValueKind.Number &&
                item.TryGetInt64(out value) && value >= 0;
        }
        if (!Read(usage, "prompt_tokens", "input_tokens", out input) ||
            !Read(usage, "completion_tokens", "output_tokens", out output) ||
            input == 0 || input > long.MaxValue - output) return false;
        // If a provider supplies only a partial breakdown, keep the larger
        // conservative reservation instead of calling the partial sum exact.
        if (usage.TryGetProperty("total_tokens", out var total) &&
            (total.ValueKind != JsonValueKind.Number ||
                !total.TryGetInt64(out var count) || count != input + output)) return false;
        return true;
    }

    static void AcquireBudgetGate(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { if (BudgetGate.WaitOne(250)) return; }
            catch (AbandonedMutexException) { return; }
        }
    }

    static bool IsRetryable(HttpRequestException error) =>
        error.StatusCode is null or HttpStatusCode.TooManyRequests ||
        (int)error.StatusCode >= 500;

    static bool HasCredentialInQuery(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => Uri.UnescapeDataString(part.Split('=', 2)[0]))
        .Any(name => name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("password", StringComparison.OrdinalIgnoreCase));

    static bool ContainsSecret(JsonElement element, string secret) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()?.Contains(secret, StringComparison.Ordinal) == true,
        JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsSecret(item, secret)),
        JsonValueKind.Object => element.EnumerateObject().Any(item =>
            item.Name.Contains(secret, StringComparison.Ordinal) || ContainsSecret(item.Value, secret)),
        _ => false
    };

    // Existing callers also receive durable evidence; callers that need stable IDs can
    // enqueue first, keep the returned ID, then call SendJobAsync explicitly.
    public static async Task<JsonDocument> SendAsync(LibraryConfig config, string endpoint,
        object payload, string dataDir, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var jobId = Enqueue(dataDir, "request", endpoint, payload);
        return await SendJobAsync(config, endpoint, payload, dataDir, jobId, cancellationToken);
    }

    internal static string Enqueue(string dataDir, string kind, string endpoint, object payload) =>
        AiJobStore.Create(dataDir, kind, endpoint, JsonSerializer.Serialize(payload)).Id;

    internal static AiJobStore.Job PrepareRetry(string dataDir, string jobId,
        string endpoint, object payload) =>
        AiJobStore.PrepareRetry(dataDir, jobId, endpoint, JsonSerializer.Serialize(payload));

    internal static AiJobStore.Job PrepareStoredRetry(string dataDir, string jobId,
        string endpoint, string requestJson) =>
        AiJobStore.PrepareRetry(dataDir, jobId, endpoint, requestJson);

    internal static bool TryCancelPending(string dataDir, string jobId) =>
        AiJobStore.TryCancelPending(dataDir, jobId);

    internal static bool TryCancelActive(string jobId)
    {
        if (!ActiveJobs.TryGetValue(jobId, out var cancellation)) return false;
        try { cancellation.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    internal static int RecoverInterrupted(string dataDir) => AiJobStore.RecoverInterrupted(dataDir);

    internal static async Task<JsonDocument> SendJobAsync(LibraryConfig config, string endpoint,
        object payload, string dataDir, string jobId, CancellationToken cancellationToken = default)
        => await SendStoredJobAsync(config, endpoint, JsonSerializer.Serialize(payload),
            dataDir, jobId, cancellationToken);

    internal static async Task<JsonDocument> SendStoredJobAsync(LibraryConfig config, string endpoint,
        string requestJson, string dataDir, string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = AiJobStore.Begin(dataDir, jobId, endpoint, requestJson);
        if (job.Status == AiJobStore.Completed)
            return JsonDocument.Parse(job.ResultJson!);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ActiveJobs[jobId] = linkedCancellation;
        JsonDocument document;
        try { document = await SendCoreAsync(config, endpoint, requestJson, dataDir, linkedCancellation.Token); }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            AiJobStore.Finish(dataDir, jobId, job.AttemptId, AiJobStore.Canceled, "request-canceled");
            throw;
        }
        catch (OperationCanceledException)
        {
            AiJobStore.Finish(dataDir, jobId, job.AttemptId, AiJobStore.Failed, "request-timeout");
            throw;
        }
        catch (Exception ex)
        {
            var errorCode = ex switch
            {
                HttpRequestException => "transport-error",
                InvalidOperationException => "policy-blocked",
                InvalidDataException => "invalid-data",
                JsonException => "invalid-response",
                _ => "request-failed"
            };
            AiJobStore.Finish(dataDir, jobId, job.AttemptId, AiJobStore.Failed, errorCode);
            throw;
        }
        finally { ActiveJobs.TryRemove(jobId, out _); }
        try
        {
            var responseJson = document.RootElement.GetRawText();
            if (Encoding.UTF8.GetByteCount(responseJson) > AiJobStore.MaxSavedResponseBytes)
            {
                AiJobStore.Finish(dataDir, jobId, job.AttemptId, AiJobStore.Failed,
                    "response-too-large");
                throw new InvalidDataException("AI 响应过大，不能保存为作业结果。");
            }
            var sentKey = config.AiKey?.Trim();
            if (!string.IsNullOrEmpty(sentKey) &&
                (responseJson.Contains(sentKey, StringComparison.Ordinal) ||
                    ContainsSecret(document.RootElement, sentKey)))
            {
                AiJobStore.Finish(dataDir, jobId, job.AttemptId, AiJobStore.Failed,
                    "response-contained-secret");
                throw new InvalidDataException("AI 响应包含接口凭据，已拒绝保存该响应。");
            }
            AiJobStore.Finish(dataDir, jobId, job.AttemptId, AiJobStore.Completed,
                resultJson: responseJson);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    readonly record struct ChannelPolicy(bool IsLocal, TimeSpan Interval, int Concurrency, int RetryCount);
    readonly record struct Reservation(string Day, double Cost, long InputEstimate, long OutputLimit);
    readonly record struct ReservationDecision(Reservation? Reservation, TimeSpan Delay);

    static ChannelPolicy PolicyFor(LibraryConfig config, Uri endpoint)
    {
        var local = endpoint.IsLoopback;
        var intervalMs = local ? config.AiLocalRequestIntervalMs : config.AiCloudRequestIntervalMs;
        if (intervalMs <= 0) intervalMs = config.AiRequestIntervalMs;
        return new ChannelPolicy(local, TimeSpan.FromMilliseconds(Math.Clamp(intervalMs, 500, 60000)),
            Math.Clamp(local ? config.AiLocalConcurrency : config.AiCloudConcurrency, 1, 4),
            Math.Clamp(local ? config.AiLocalRetryCount : config.AiCloudRetryCount, 0, 5));
    }

    sealed class SlotLease(Mutex slot) : IDisposable
    {
        public void Dispose() => slot.ReleaseMutex();
    }

    static SlotLease AcquireChannelSlot(ChannelPolicy policy, CancellationToken cancellationToken)
    {
        var slots = policy.IsLocal ? LocalSlots : CloudSlots;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < policy.Concurrency; index++)
            {
                try { if (slots[index].WaitOne(0)) return new SlotLease(slots[index]); }
                catch (AbandonedMutexException) { return new SlotLease(slots[index]); }
            }
            Thread.Sleep(50);
        }
    }

    static Task<T> WithBudgetGateAsync<T>(Func<T> action, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var owns = false;
        try
        {
            AcquireBudgetGate(cancellationToken);
            owns = true;
            return action();
        }
        finally { if (owns) BudgetGate.ReleaseMutex(); }
    }, cancellationToken);

    static async Task<JsonDocument> SendCoreAsync(LibraryConfig config, string endpoint,
        string body, string dataDir, CancellationToken cancellationToken)
    {
        var uri = new Uri(endpoint);
        var policy = PolicyFor(config, uri);
        // Mutex ownership is thread-affine. A dedicated worker keeps the slot
        // until the HTTP request finishes; short budget transactions run on
        // their own worker and never hold BudgetGate across an await.
        return await Task.Run(() =>
        {
            using var slot = AcquireChannelSlot(policy, cancellationToken);
            return SendReservedAsync(config, uri, policy, body, dataDir,
                cancellationToken).GetAwaiter().GetResult();
        }, cancellationToken);
    }

    static async Task<JsonDocument> SendReservedAsync(LibraryConfig config, Uri uri,
        ChannelPolicy policy,
        string body, string dataDir, CancellationToken cancellationToken)
    {
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
                throw new InvalidOperationException("远程 AI 接口必须使用 HTTPS；本机接口可使用 HTTP。");
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                HasCredentialInQuery(uri))
                throw new InvalidOperationException("AI 接口地址不得包含登录信息或凭据参数；请使用单独保存的 API Key。");
            if (!uri.IsLoopback && string.IsNullOrWhiteSpace(config.AiKey))
                throw new InvalidOperationException("云端 AI 接口缺少 API Key，未发送请求。");
            if (!double.IsFinite(config.AiInputUsdPerMillion) || config.AiInputUsdPerMillion < 0 ||
                !double.IsFinite(config.AiOutputUsdPerMillion) || config.AiOutputUsdPerMillion < 0 ||
                !double.IsFinite(config.AiDailyBudgetUsd) || config.AiDailyBudgetUsd < 0)
                throw new InvalidOperationException("AI 费率无效，请检查设置。");
            if (!uri.IsLoopback && config.AiDailyBudgetUsd > 0 &&
                (config.AiInputUsdPerMillion <= 0 || config.AiOutputUsdPerMillion <= 0))
                throw new InvalidOperationException("云端 AI 费率未完整设置，无法执行每日费用上限。");
            var inputEstimate = EstimateInputTokens(body);
            var outputLimit = EstimateOutputLimitTokens(body, config);
            var reservedCost = Cost(inputEstimate, outputLimit, config);
            if (!double.IsFinite(reservedCost) || reservedCost < 0)
                throw new InvalidOperationException("AI 请求费用估算无效。");
            for (var attempt = 0; attempt <= policy.RetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reservation = await ReserveAttemptAsync(config, policy, dataDir,
                    reservedCost, inputEstimate, outputLimit, cancellationToken);

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    // A saved cloud credential must never be handed to a
                    // loopback model just because the endpoint was switched.
                    if (!policy.IsLocal && !string.IsNullOrWhiteSpace(config.AiKey))
                        request.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AiKey.Trim());
                    using var response = await Client.SendAsync(request, cancellationToken);
                    if ((response.StatusCode == HttpStatusCode.TooManyRequests ||
                         (int)response.StatusCode >= 500) && attempt < policy.RetryCount)
                    {
                        var retry = response.Headers.RetryAfter?.Delta ??
                            TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(
                            retry.TotalMilliseconds, 500, 30000)), cancellationToken);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    try
                    {
                        if (TryReadReportedTokens(document, out var input, out var output))
                        {
                            var reportedCost = Cost(input, output, config);
                            if (!double.IsFinite(reportedCost) || reportedCost < 0)
                                throw new InvalidDataException("AI 服务返回的 Token 用量超出可核算范围。");
                            await ReconcileAttemptAsync(dataDir, reservation,
                                reportedCost, input, output);
                        }
                        return document;
                    }
                    catch { document.Dispose(); throw; }
                }
                catch (HttpRequestException error) when (attempt < policy.RetryCount && IsRetryable(error))
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < policy.RetryCount)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
                }
            }
            throw new HttpRequestException("AI 请求重试后仍失败。");
    }

    static async Task<Reservation> ReserveAttemptAsync(LibraryConfig config, ChannelPolicy policy,
        string dataDir, double cost, long inputEstimate, long outputLimit,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await WithBudgetGateAsync(() =>
            {
                var ledger = LoadBudgetLedger(dataDir);
                var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (ledger.Day != today) ledger = new BudgetLedger { Day = today };
                var now = DateTimeOffset.UtcNow;
                var channelNext = policy.IsLocal ? ledger.NextLocalAllowedUtc : ledger.NextCloudAllowedUtc;
                var readyAt = channelNext > ledger.NextAllowedUtc ? channelNext : ledger.NextAllowedUtc;
                if (readyAt > now) return new ReservationDecision(null, readyAt - now);
                if (config.AiDailyBudgetUsd > 0 && ledger.EstimatedUsd + cost > config.AiDailyBudgetUsd)
                    throw new InvalidOperationException("今日 AI 费用预算已达上限。可在设置中调整每日上限或费率。");
                // Reserve before dispatch. A crash, timeout or missing usage
                // must not silently refund a request that may have been sent.
                ledger.EstimatedUsd += cost;
                ledger.UnverifiedUsd += cost;
                ledger.UnverifiedInputEstimateTokens = checked(ledger.UnverifiedInputEstimateTokens + inputEstimate);
                ledger.UnverifiedOutputLimitTokens = checked(ledger.UnverifiedOutputLimitTokens + outputLimit);
                ledger.AttemptCount = checked(ledger.AttemptCount + 1);
                if (policy.IsLocal) ledger.NextLocalAllowedUtc = now + policy.Interval;
                else ledger.NextCloudAllowedUtc = now + policy.Interval;
                SaveBudgetLedger(dataDir, ledger);
                return new ReservationDecision(new Reservation(today, cost, inputEstimate, outputLimit), TimeSpan.Zero);
            }, cancellationToken);
            if (decision.Reservation is { } reserved) return reserved;
            await Task.Delay(decision.Delay, cancellationToken);
        }
    }

    static async Task ReconcileAttemptAsync(string dataDir, Reservation reservation,
        double reportedCost, long inputTokens, long outputTokens)
    {
        // Cancellation after an HTTP response must not interrupt accounting.
        await WithBudgetGateAsync(() =>
        {
            var ledger = LoadBudgetLedger(dataDir);
            if (ledger.Day != reservation.Day) return 0;
            ledger.EstimatedUsd = Math.Max(0, ledger.EstimatedUsd - reservation.Cost + reportedCost);
            ledger.UnverifiedUsd = Math.Max(0, ledger.UnverifiedUsd - reservation.Cost);
            ledger.UnverifiedInputEstimateTokens -= reservation.InputEstimate;
            ledger.UnverifiedOutputLimitTokens -= reservation.OutputLimit;
            ledger.ReportedUsageUsd += reportedCost;
            ledger.ReportedUsageCount = checked(ledger.ReportedUsageCount + 1);
            ledger.InputTokens = checked(ledger.InputTokens + inputTokens);
            ledger.OutputTokens = checked(ledger.OutputTokens + outputTokens);
            SaveBudgetLedger(dataDir, ledger);
            return 0;
        }, CancellationToken.None);
    }

    internal static async Task<bool> VerifyOldDayReconcileForDiagnosticsAsync(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var current = new BudgetLedger
        {
            Day = today, EstimatedUsd = 0.1, UnverifiedUsd = 0.1,
            AttemptCount = 1, UnverifiedInputEstimateTokens = 50,
            UnverifiedOutputLimitTokens = 30
        };
        SaveBudgetLedger(dataDir, current);
        var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await ReconcileAttemptAsync(dataDir, new Reservation(yesterday, 0.02, 10, 10), 0.01, 5, 5);
        var after = LoadBudgetLedger(dataDir);
        return after.Day == today && after.AttemptCount == 1 && after.ReportedUsageCount == 0 &&
            Math.Abs(after.EstimatedUsd - 0.1) < 0.000000001d &&
            Math.Abs(after.UnverifiedUsd - 0.1) < 0.000000001d;
    }

    internal static Task HoldMutexForDiagnosticsAsync(bool budget, bool local, string marker)
        => Task.Run(() =>
        {
            if (budget)
            {
                AcquireBudgetGate(CancellationToken.None);
                File.WriteAllText(marker, "locked");
                Thread.Sleep(Timeout.Infinite);
            }
            else
            {
                using var slot = AcquireChannelSlot(new ChannelPolicy(local, TimeSpan.FromMilliseconds(500), 1, 0),
                    CancellationToken.None);
                File.WriteAllText(marker, "locked");
                Thread.Sleep(Timeout.Infinite);
            }
        });

    internal static async Task<bool> TryMutexForDiagnosticsAsync(bool budget, bool local, string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if (budget)
                await WithBudgetGateAsync(() => { File.WriteAllText(marker, "acquired"); return 0; }, timeout.Token);
            else
                await Task.Run(() =>
                {
                    using var slot = AcquireChannelSlot(new ChannelPolicy(local, TimeSpan.FromMilliseconds(500), 1, 0),
                        timeout.Token);
                    File.WriteAllText(marker, "acquired");
                }, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return false; }
    }
}
