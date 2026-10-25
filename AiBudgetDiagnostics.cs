using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class AiBudgetDiagnostics
{
    internal static async Task<IReadOnlyDictionary<string, bool>> VerifyAsync(string root)
    {
        Directory.CreateDirectory(root);
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var english = AiRequestQueue.EstimateInputTokens("hello world, this is an English request");
        var chinese = AiRequestQueue.EstimateInputTokens("你好世界，这是中文请求，包含演员和作品标题");
        checks["aiBudgetInputEstimateIsConservativeForEnglishAndChinese"] =
            english >= Encoding.UTF8.GetByteCount("hello world, this is an English request") &&
            chinese >= Encoding.UTF8.GetByteCount("你好世界，这是中文请求，包含演员和作品标题") &&
            chinese > "你好世界，这是中文请求，包含演员和作品标题".Length;
        checks["aiBudgetStoredRequestOutputCannotUnderreserve"] =
            AiRequestQueue.EstimateOutputLimitTokens("{\"max_tokens\":2048}",
                new LibraryConfig { AiMaxOutputTokens = 128 }) == 2048 &&
            AiRequestQueue.EstimateOutputLimitTokens("{\"max_output_tokens\":1024}",
                new LibraryConfig { AiMaxOutputTokens = 128 }) == 1024;

        using (var chat = JsonDocument.Parse("{\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":4}}"))
            checks["aiBudgetChatUsageParsed"] =
                AiRequestQueue.TryReadReportedTokens(chat, out var input, out var output) &&
                input == 12 && output == 4;
        using (var responses = JsonDocument.Parse("{\"usage\":{\"input_tokens\":20,\"output_tokens\":7}}"))
            checks["aiBudgetResponsesUsageParsed"] =
                AiRequestQueue.TryReadReportedTokens(responses, out var input, out var output) &&
                input == 20 && output == 7;
        using (var zero = JsonDocument.Parse("{\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0}}"))
            checks["aiBudgetImplausibleZeroUsageStaysEstimated"] =
                !AiRequestQueue.TryReadReportedTokens(zero, out _, out _);
        using (var missing = JsonDocument.Parse("{\"choices\":[]}"))
            checks["aiBudgetMissingUsageStaysEstimated"] =
                !AiRequestQueue.TryReadReportedTokens(missing, out _, out _);
        using (var partial = JsonDocument.Parse("{\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":4,\"total_tokens\":30}}"))
            checks["aiBudgetPartialUsageStaysEstimated"] =
                !AiRequestQueue.TryReadReportedTokens(partial, out _, out _);

        var legacyDir = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyDir);
        var ledgerPath = Path.Combine(legacyDir, "ai-budget.json");
        File.WriteAllText(ledgerPath, JsonSerializer.Serialize(new
        {
            Day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), EstimatedUsd = 0.25
        }));
        var legacy = AiRequestQueue.LoadBudgetLedger(legacyDir);
        checks["aiBudgetLegacyLedgerWithoutNewCountersLoads"] =
            legacy.EstimatedUsd == 0.25 && legacy.UnverifiedInputEstimateTokens == 0 &&
            legacy.UnverifiedOutputLimitTokens == 0;
        File.WriteAllText(ledgerPath, JsonSerializer.Serialize(new AiRequestQueue.BudgetLedger
        {
            Day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            EstimatedUsd = 0.1, UnverifiedUsd = 0.2
        }));
        try { _ = AiRequestQueue.LoadBudgetLedger(legacyDir); checks["aiBudgetInconsistentBreakdownFailsClosed"] = false; }
        catch (InvalidDataException) { checks["aiBudgetInconsistentBreakdownFailsClosed"] = true; }
        File.WriteAllText(ledgerPath, JsonSerializer.Serialize(new AiRequestQueue.BudgetLedger
        {
            Day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            EstimatedUsd = 0.1, UnverifiedInputEstimateTokens = -1
        }));
        try { _ = AiRequestQueue.LoadBudgetLedger(legacyDir); checks["aiBudgetNegativeEstimatedTokensFailClosed"] = false; }
        catch (InvalidDataException) { checks["aiBudgetNegativeEstimatedTokensFailClosed"] = true; }

        await using var api = new ScriptedApi();
        var config = new LibraryConfig
        {
            AiKey = "private-ai-key-sentinel", AiInputUsdPerMillion = 1,
            AiOutputUsdPerMillion = 4, AiMaxOutputTokens = 50,
            AiDailyBudgetUsd = 1, AiRequestIntervalMs = 500
        };
        var payload = new { model = "offline", messages = new[] { new { role = "user", content = "中文 English" } }, max_tokens = 50 };
        var requestJson = JsonSerializer.Serialize(payload);
        var firstDir = Path.Combine(root, "retry-and-reconcile");
        var firstJob = AiRequestQueue.Enqueue(firstDir, "review", api.Endpoint, payload);
        using (var first = await AiRequestQueue.SendJobAsync(config, api.Endpoint, payload, firstDir, firstJob))
            checks["aiBudget429ThenReportedResponseReturned"] =
                first.RootElement.GetProperty("choices")[0].GetProperty("message")
                    .GetProperty("content").GetString() == "ok";
        var firstLedger = AiRequestQueue.LoadBudgetLedger(firstDir);
        var reserve = (AiRequestQueue.EstimateInputTokens(requestJson) + 50 * 4d) / 1_000_000d;
        var reported = (9 + 3 * 4d) / 1_000_000d;
        checks["aiBudget429AttemptReservedAndSuccessReconciled"] =
            api.RequestCount == 2 && firstLedger.AttemptCount == 2 &&
            firstLedger.ReportedUsageCount == 1 &&
            firstLedger.InputTokens == 9 && firstLedger.OutputTokens == 3 &&
            firstLedger.UnverifiedInputEstimateTokens == AiRequestQueue.EstimateInputTokens(requestJson) &&
            firstLedger.UnverifiedOutputLimitTokens == 50 &&
            Math.Abs(firstLedger.UnverifiedUsd - reserve) < 0.000000001d &&
            Math.Abs(firstLedger.ReportedUsageUsd - reported) < 0.000000001d &&
            Math.Abs(firstLedger.EstimatedUsd - reserve - reported) < 0.000000001d;
        using (var duplicate = await AiRequestQueue.SendJobAsync(config, api.Endpoint, payload, firstDir, firstJob))
            checks["aiBudgetCompletedDuplicateUsesSavedResponseWithoutCharge"] =
                duplicate.RootElement.GetProperty("choices")[0].GetProperty("message")
                    .GetProperty("content").GetString() == "ok" &&
                api.RequestCount == 2 &&
                AiRequestQueue.LoadBudgetLedger(firstDir).AttemptCount == 2;
        config.AiDailyBudgetUsd = firstLedger.EstimatedUsd;
        try
        {
            using var unexpected = await AiRequestQueue.SendAsync(config, api.Endpoint,
                payload, firstDir, new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);
            checks["aiBudgetExhaustionBlocksBeforeNetwork"] = false;
        }
        catch (InvalidOperationException ex)
        {
            checks["aiBudgetExhaustionBlocksBeforeNetwork"] =
                ex.Message.Contains("预算", StringComparison.Ordinal) && api.RequestCount == 2 &&
                AiRequestQueue.LoadBudgetLedger(firstDir).AttemptCount == 2;
        }
        config.AiDailyBudgetUsd = 1;

        var noUsageDir = Path.Combine(root, "missing-usage");
        using (var noUsage = await AiRequestQueue.SendAsync(config, api.Endpoint, payload, noUsageDir))
            checks["aiBudgetNoUsageResponseAccepted"] = noUsage.RootElement.TryGetProperty("choices", out _);
        var unverified = AiRequestQueue.LoadBudgetLedger(noUsageDir);
        checks["aiBudgetNoUsageRetainsLabeledUpperBound"] =
            api.RequestCount == 3 && unverified.AttemptCount == 1 &&
            unverified.ReportedUsageCount == 0 && unverified.InputTokens == 0 &&
            unverified.UnverifiedInputEstimateTokens == AiRequestQueue.EstimateInputTokens(requestJson) &&
            unverified.UnverifiedOutputLimitTokens == 50 &&
            Math.Abs(unverified.UnverifiedUsd - reserve) < 0.000000001d;

        var canceledDir = Path.Combine(root, "canceled-after-send");
        var canceledJob = AiRequestQueue.Enqueue(canceledDir, "tags", api.Endpoint, payload);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
        {
            var send = AiRequestQueue.SendJobAsync(config, api.Endpoint, payload,
                canceledDir, canceledJob, cancellation.Token);
            await api.WaitForRequestAsync(4);
            cancellation.Cancel();
            try { using var unexpected = await send; checks["aiBudgetCanceledSentAttemptRemainsUnverified"] = false; }
            catch (OperationCanceledException)
            {
                var charged = AiRequestQueue.LoadBudgetLedger(canceledDir);
                checks["aiBudgetCanceledSentAttemptRemainsUnverified"] =
                    AiJobStore.Get(canceledDir, canceledJob).Status == AiJobStore.Canceled &&
                    charged.AttemptCount == 1 && charged.ReportedUsageCount == 0 &&
                    Math.Abs(charged.UnverifiedUsd - reserve) < 0.000000001d;
            }
        }

        var echoDir = Path.Combine(root, "credential-echo");
        var echoJob = AiRequestQueue.Enqueue(echoDir, "tags", api.Endpoint, payload);
        try
        {
            using var unexpected = await AiRequestQueue.SendJobAsync(config, api.Endpoint,
                payload, echoDir, echoJob);
            checks["aiBudgetResponseEchoCannotPersistCredential"] = false;
        }
        catch (InvalidDataException)
        {
            checks["aiBudgetResponseEchoCannotPersistCredential"] =
                AiJobStore.Get(echoDir, echoJob).Status == AiJobStore.Failed &&
                !Directory.EnumerateFiles(echoDir, "*.json", SearchOption.AllDirectories)
                    .Select(File.ReadAllText).Any(text => text.Contains(config.AiKey, StringComparison.Ordinal));
        }
        await api.CompleteAsync();
        checks["aiLoopbackNeverReceivesCloudBearer"] = api.DidNotReceiveBearer;

        var queryDir = Path.Combine(root, "credential-query");
        try
        {
            using var unexpected = await AiRequestQueue.SendAsync(config,
                api.Endpoint + "?api%5Fkey=plaintext-query-secret", payload, queryDir);
            checks["aiBudgetCredentialQueryBlockedBeforeNetwork"] = false;
        }
        catch (InvalidOperationException)
        {
            checks["aiBudgetCredentialQueryBlockedBeforeNetwork"] =
                !File.Exists(Path.Combine(queryDir, "ai-budget.json")) &&
                !Directory.EnumerateFiles(queryDir, "*.json", SearchOption.AllDirectories)
                    .Select(File.ReadAllText).Any(text => text.Contains("plaintext-query-secret", StringComparison.Ordinal));
        }
        return checks;
    }

    sealed class ScriptedApi : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly Task server;
        readonly TaskCompletionSource<bool>[] accepted = Enumerable.Range(0, 5)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        int requestCount;
        bool didNotReceiveBearer = true;

        internal ScriptedApi()
        {
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/chat/completions";
            server = ServeAsync();
        }

        internal string Endpoint { get; }
        internal int RequestCount => Volatile.Read(ref requestCount);
        internal bool DidNotReceiveBearer => didNotReceiveBearer;

        internal async Task WaitForRequestAsync(int number) =>
            await accepted[number - 1].Task.WaitAsync(TimeSpan.FromSeconds(8));

        internal async Task CompleteAsync() => await server.WaitAsync(TimeSpan.FromSeconds(8));

        async Task ServeAsync()
        {
            for (var i = 0; i < 5; i++)
            {
                using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
                using var stream = client.GetStream();
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 16384)
                {
                    if (await stream.ReadAsync(one) == 0) break;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 &&
                        header[^2] == 13 && header[^1] == 10) break;
                }
                var headers = Encoding.ASCII.GetString(header.ToArray());
                if (headers.Contains("Authorization: Bearer", StringComparison.OrdinalIgnoreCase))
                    didNotReceiveBearer = false;
                var lengthLine = headers.Split("\r\n").FirstOrDefault(line =>
                    line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var remaining = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim(), CultureInfo.InvariantCulture);
                while (remaining > 0)
                {
                    var count = await stream.ReadAsync(new byte[Math.Min(remaining, 4096)]);
                    if (count == 0) break;
                    remaining -= count;
                }
                Interlocked.Increment(ref requestCount);
                accepted[i].TrySetResult(true);
                if (i == 3) await Task.Delay(500);
                var body = i switch
                {
                    1 => "{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":3}}",
                    4 => "{\"choices\":[{\"message\":{\"content\":\"private\\u002dai\\u002dkey\\u002dsentinel\"}}]}",
                    _ => "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"
                };
                var response = i == 0
                    ? "HTTP/1.1 429 Too Many Requests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                try { await stream.WriteAsync(Encoding.UTF8.GetBytes(response)); }
                catch (Exception) when (i == 3) { /* Client canceled after reservation. */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { /* Diagnostic failure is reflected in checks. */ }
        }
    }
}
