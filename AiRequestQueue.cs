using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;

namespace YinggeVideoLibrary;

// One queue for classification, tag suggestions and scrape review.
internal static class AiRequestQueue
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };
    static DateTime nextRequestAt = DateTime.MinValue;
    static readonly Mutex BudgetMutex = new(false, "Local\\YinggeVideoLibrary.AiBudget");
    public sealed class BudgetLedger { public string Day { get; set; } = ""; public double EstimatedUsd { get; set; } }

    public static bool CanUse(LibraryConfig config)
    {
        if (!Uri.TryCreate(config.AiEndpoint, UriKind.Absolute, out var uri)) return false;
        return !string.IsNullOrWhiteSpace(config.AiKey) || uri.IsLoopback;
    }

    public static async Task<JsonDocument> SendAsync(LibraryConfig config, string endpoint, object payload, string dataDir)
    {
        await Gate.WaitAsync();
        try
        {
            var uri = new Uri(endpoint);
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
                throw new InvalidOperationException("远程 AI 接口必须使用 HTTPS；本机接口可使用 HTTP。");
            var body = JsonSerializer.Serialize(payload);
            var estimatedInputTokens = Math.Max(1, body.Length);
            // User supplied rates are estimates; the provider's final bill is authoritative.
            var reserve = (estimatedInputTokens * config.AiInputUsdPerMillion + config.AiMaxOutputTokens * config.AiOutputUsdPerMillion) / 1_000_000d;
            var ownsBudgetMutex = false;
            try
            {
                try { BudgetMutex.WaitOne(); ownsBudgetMutex = true; }
                catch (AbandonedMutexException) { ownsBudgetMutex = true; }
                var ledgerPath = Path.Combine(dataDir, "ai-budget.json");
                BudgetLedger ledger;
                try { ledger = File.Exists(ledgerPath) ? JsonSerializer.Deserialize<BudgetLedger>(File.ReadAllText(ledgerPath)) ?? new() : new(); }
                catch { ledger = new(); }
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                if (ledger.Day != today) ledger = new BudgetLedger { Day = today };
                if (config.AiDailyBudgetUsd > 0 && ledger.EstimatedUsd + reserve > config.AiDailyBudgetUsd)
                    throw new InvalidOperationException("今日 AI 费用估算已达上限。可在设置中调整每日上限或费率。");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var delay = nextRequestAt - DateTime.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay);
                nextRequestAt = DateTime.UtcNow.AddMilliseconds(Math.Clamp(config.AiRequestIntervalMs, 500, 60000));
                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (!string.IsNullOrWhiteSpace(config.AiKey))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AiKey.Trim());
                using var response = await Client.SendAsync(request);
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                    await Task.Delay(retry > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : retry);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                ledger.EstimatedUsd += reserve;
                var temporary = ledgerPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(ledger));
                File.Move(temporary, ledgerPath, true);
                return document;
            }
            throw new HttpRequestException("AI 请求重试后仍失败。");
            }
            finally { if (ownsBudgetMutex) BudgetMutex.ReleaseMutex(); }
        }
        finally { Gate.Release(); }
    }
}
