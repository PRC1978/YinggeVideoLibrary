using System.Globalization;
using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// This report deliberately uses an allowlist. Never serialize LibraryConfig,
// queue jobs, raw source errors, URLs, or provider responses into an export.
internal static class SourceHealthDiagnostics
{
    internal sealed record SourceState(string Name, bool Enabled, string Mode, bool CredentialReady,
        string Status, DateTimeOffset? CooldownUntilUtc, string LastFailureCategory, string ParserRevision);

    internal sealed record PaidBudgetState(string Status, int? ReservedToday, int DailyLimit, string UtcDay);

    internal sealed record Snapshot(string ApplicationVersion, DateTimeOffset CapturedAtUtc,
        string LocalRecordStatus, IReadOnlyList<SourceState> Sources, PaidBudgetState JavinfoBudget);

    internal sealed record BackoffState(DateTimeOffset Until, string Error);

    static string AppVersion => typeof(SourceHealthDiagnostics).Assembly.GetName().Version?.ToString(3) ?? "未知";

    internal static Snapshot Capture(string dataDir, LibraryConfig config, DateTimeOffset? capturedAt = null)
    {
        var now = capturedAt ?? DateTimeOffset.UtcNow;
        var (backoffs, recordStatus) = ReadBackoffs(dataDir);
        var budget = ReadPaidBudget(dataDir, config.JavinfoDailyRequestLimit, now);
        return Build(config, backoffs, budget, recordStatus, now,
            OmdbScraper.CredentialReady(), TraktScraper.CredentialReady());
    }

    internal static Snapshot Build(LibraryConfig config, IReadOnlyDictionary<string, BackoffState> backoffs,
        PaidBudgetState paidBudget, string recordStatus, DateTimeOffset now,
        bool omdbCredentialReady = false, bool traktCredentialReady = false)
    {
        var parser = AppVersion + " · 规则 r1";
        var sources = new List<SourceState>(16);
        var backoffUnknown = recordStatus == "冷却记录不可读，状态可能不完整";
        void Add(string name, bool enabled, string mode, bool credentialReady = true)
        {
            backoffs.TryGetValue(name, out var backoff);
            var cooldown = backoff?.Until > now ? backoff.Until : (DateTimeOffset?)null;
            var status = !enabled ? "已关闭"
                : !credentialReady ? "缺少凭据"
                : name == "Javinfo" && paidBudget.Status == "用量记录异常" ? "付费请求已阻止"
                : backoffUnknown && name != "Javinfo" ? "冷却状态未知"
                : cooldown is not null ? "冷却中"
                : "可尝试（未联网验证）";
            var recentFailure = backoff is not null ? ClassifyFailure(backoff.Error)
                : name is "AniList" or "Javinfo" or "OMDb" or "Trakt"
                    ? "手动请求未记录" : "无未恢复队列错误";
            sources.Add(new SourceState(name, enabled, mode, credentialReady, status,
                cooldown, recentFailure, parser));
        }

        var javDbMode = config.EnableJavDb && config.EnableJavDbTitle ? "番号及标题队列"
            : config.EnableJavDb ? "番号队列" : config.EnableJavDbTitle ? "标题队列" : "番号及标题队列";
        Add("JavDB", config.EnableJavDb || config.EnableJavDbTitle,
            javDbMode + "；已核对详情、演员/系列关系及标签交集（手动预览）");
        Add("JAVDatabase", config.EnableJavDatabase, "番号及标题队列；已核对详情、评分及手动演员/系列关系浏览");
        Add("TMDB", config.EnableTmdb, "标题队列", !string.IsNullOrWhiteSpace(config.TmdbToken));
        Add("Pornhub", config.EnablePornhub, "标题队列");
        Add("TVmaze", config.EnableTvMaze, "标题队列");
        Add("Wikidata", config.EnableWikidata, "电影 / 剧集 / 动画标题队列；无密钥，需核验条目");
        Add("Kitsu", config.EnableKitsu, "动画标题队列；无密钥，核对条目和年份后人工审核");
        Add("Tenrai", config.EnableTenrai, "动画标题队列；缓存的 MyAnimeList 条目，核对编号与标题后人工审核");
        Add("Mikan", config.EnableMikan, config.AnimeSourcesInQueue
            ? "番剧标题队列（仅动漫分类）；公开番组详情，人工审核" : "番剧作品；仅手动与动漫分类的全部来源查询，人工审核");
        Add("FankuHub", config.EnableFankuHub, config.AnimeSourcesInQueue
            ? "番剧标题队列（仅动漫分类）；公开作品详情，人工审核" : "番剧作品；仅手动与动漫分类的全部来源查询，人工审核");
        Add("DMHY", config.EnableDmhy, "仅手动查询发布线索；不覆盖作品资料");
        Add("OMDb", config.EnableOmdb, "电影 / 剧集标题；仅手动按需查询并核对 IMDb ID", omdbCredentialReady);
        Add("Trakt", config.EnableTrakt,
            "电影 / 剧集；仅手动按需查询并核对 Trakt、IMDb/TMDB 编号", traktCredentialReady);
        Add("Bangumi", config.EnableBangumi, "标题队列");
        Add("AniList", config.EnableAniList, "仅手动搜索");
        Add("Javinfo", config.EnableJavinfo, "可选付费 · 仅手动精确查询",
            !string.IsNullOrWhiteSpace(config.JavinfoKey) && config.JavinfoKey.StartsWith("jvi_", StringComparison.Ordinal));
        return new Snapshot(AppVersion, now.ToUniversalTime(), recordStatus, sources, paidBudget);
    }

    static (IReadOnlyDictionary<string, BackoffState> Backoffs, string Status) ReadBackoffs(string dataDir)
    {
        var primary = Path.Combine(dataDir, "scrape-source-backoff.json");
        var backup = primary + ".bak";
        if (TryReadBackoffs(primary, out var result)) return (result, "本地冷却记录正常");
        if (TryReadBackoffs(backup, out result)) return (result, "使用上次有效的冷却记录");
        var exists = File.Exists(primary) || File.Exists(backup);
        return (new Dictionary<string, BackoffState>(StringComparer.OrdinalIgnoreCase),
            exists ? "冷却记录不可读，状态可能不完整" : "尚无冷却记录");
    }

    static bool TryReadBackoffs(string path, out IReadOnlyDictionary<string, BackoffState> result)
    {
        result = new Dictionary<string, BackoffState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!ScrapeQueueStore.ValidateSourceBackoffSnapshot(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var items = new Dictionary<string, BackoffState>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in document.RootElement.GetProperty("Sources").EnumerateObject())
            {
                var value = source.Value;
                var untilText = value.GetProperty("Until").GetString();
                if (!DateTimeOffset.TryParse(untilText, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var until)) return false;
                items[source.Name] = new BackoffState(until, value.GetProperty("LastError").GetString() ?? "");
            }
            result = items;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
            or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    static PaidBudgetState ReadPaidBudget(string dataDir, int dailyLimit, DateTimeOffset now)
    {
        var limit = Math.Clamp(dailyLimit, 1, 100);
        var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Path.Combine(dataDir, "javinfo-usage.json");
        if (!File.Exists(path)) return new PaidBudgetState("尚未使用", 0, limit, day);
        try
        {
            if (new FileInfo(path).Length > 64 * 1024)
                return new PaidBudgetState("用量记录异常", null, limit, day);
            var ledger = JavinfoUsageLedger.ReadForDiagnostics(dataDir);
            if (!DateTime.TryParseExact(ledger.utcDay, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var ledgerDay) || ledger.reservedCalls < 0
                || ledgerDay.Date > now.UtcDateTime.Date)
                return new PaidBudgetState("用量记录异常", null, limit, day);
            return new PaidBudgetState("本地预留次数", ledger.utcDay == day ? ledger.reservedCalls : 0, limit, day);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
            or InvalidOperationException or NullReferenceException)
        {
            return new PaidBudgetState("用量记录异常", null, limit, day);
        }
    }

    internal static string ClassifyFailure(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "无未恢复错误";
        var value = raw.ToLowerInvariant();
        if (value.Contains("429", StringComparison.Ordinal) || value.Contains("限速", StringComparison.Ordinal)
            || value.Contains("rate limit", StringComparison.Ordinal)) return "请求受限（429）";
        if (value.Contains("401", StringComparison.Ordinal) || value.Contains("403", StringComparison.Ordinal)
            || value.Contains("凭据", StringComparison.Ordinal) || value.Contains("unauthorized", StringComparison.Ordinal))
            return "凭据或访问权限错误";
        if (value.Contains("timeout", StringComparison.Ordinal) || value.Contains("超时", StringComparison.Ordinal))
            return "请求超时";
        if (value.Contains("502", StringComparison.Ordinal) || value.Contains("503", StringComparison.Ordinal)
            || value.Contains("504", StringComparison.Ordinal)) return "来源服务器暂不可用";
        if (value.Contains("network", StringComparison.Ordinal) || value.Contains("网络", StringComparison.Ordinal)
            || value.Contains("连接", StringComparison.Ordinal)) return "网络连接错误";
        return "其他来源错误";
    }

    internal static string ToRedactedJson(Snapshot snapshot)
    {
        // Serialize only constructed, bounded facts. Raw error text and all
        // configuration secrets never enter this object graph.
        var data = new
        {
            schemaVersion = 1,
            applicationVersion = snapshot.ApplicationVersion,
            capturedAtUtc = snapshot.CapturedAtUtc,
            localRecordStatus = snapshot.LocalRecordStatus,
            sources = snapshot.Sources.Select(item => new
            {
                source = item.Name,
                enabled = item.Enabled,
                mode = item.Mode,
                credentialReady = item.CredentialReady,
                status = item.Status,
                cooldownUntilUtc = item.CooldownUntilUtc,
                lastFailureCategory = item.LastFailureCategory,
                parserRevision = item.ParserRevision
            }).ToArray(),
            javinfoBudget = new
            {
                status = snapshot.JavinfoBudget.Status,
                reservedToday = snapshot.JavinfoBudget.ReservedToday,
                dailyLimit = snapshot.JavinfoBudget.DailyLimit,
                utcDay = snapshot.JavinfoBudget.UtcDay
            }
        };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static bool VerifyForDiagnostics()
    {
        var config = new LibraryConfig { TmdbToken = "tmdb-PRIVATE-KEY", JavinfoKey = "jvi_PRIVATE-KEY",
            EnableJavinfo = true, JavinfoDailyRequestLimit = 3 };
        var backoffs = new Dictionary<string, BackoffState>(StringComparer.OrdinalIgnoreCase)
        {
            ["JavDB"] = new(DateTimeOffset.Parse("2026-10-25T12:10:00Z", CultureInfo.InvariantCulture),
                "HTTP 429 C:\\private\\SECRET-TITLE.mp4 Cookie=PRIVATE-COOKIE"),
            ["TMDB"] = new(DateTimeOffset.Parse("2026-10-25T12:10:00Z", CultureInfo.InvariantCulture),
                "C:\\private\\SECRET-TITLE.mp4 Cookie=PRIVATE-COOKIE")
        };
        var now = DateTimeOffset.Parse("2026-10-25T12:00:00Z", CultureInfo.InvariantCulture);
        var snapshot = Build(config, backoffs, new PaidBudgetState("本地预留次数", 1, 3, "2026-10-25"),
            "本地冷却记录正常", now);
        var json = ToRedactedJson(snapshot);
        return snapshot.Sources.Count == 16
            && snapshot.Sources.Single(item => item.Name == "JavDB").Status == "冷却中"
            && snapshot.Sources.Single(item => item.Name == "JavDB").LastFailureCategory == "请求受限（429）"
            && snapshot.Sources.Single(item => item.Name == "TMDB").LastFailureCategory == "其他来源错误"
            && !json.Contains("PRIVATE", StringComparison.Ordinal)
            && !json.Contains("SECRET-TITLE", StringComparison.Ordinal)
            && !json.Contains("Cookie", StringComparison.Ordinal)
            && !json.Contains("\\private", StringComparison.Ordinal)
            && !json.Contains("tmdb-", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("jvi_", StringComparison.OrdinalIgnoreCase);
    }
}
