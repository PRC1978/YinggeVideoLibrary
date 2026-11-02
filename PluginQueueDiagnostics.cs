using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Queue-level acceptance for plugin participation: enqueue idempotence,
// cancellation, restart, and the scheduling/retry rules that keep a failing
// plugin from marking a video as unmatched or blocking other sources.
internal static class PluginQueueDiagnostics
{
    internal static int RunToFile(string workDirectory, string outPath)
    {
        var checks = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        var details = new List<string>();
        var observed = new Dictionary<string, object?>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();

        void Check(string name, bool ok, string detail = "")
        {
            checks[name] = ok;
            if (!ok) details.Add(string.IsNullOrEmpty(detail) ? name : name + "：" + detail);
        }

        string work;
        try
        {
            work = Path.GetFullPath(workDirectory);
            var real = Path.GetFullPath(AppPaths.DefaultDataDirectory);
            var forbidden = work.Equals(real, StringComparison.OrdinalIgnoreCase) ||
                work.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            Check("workDirectoryIsolated", !forbidden);
            if (forbidden) return Finish(outPath, checks, details, observed, clock, 1);
            Directory.CreateDirectory(work);
        }
        catch (Exception ex)
        {
            Check("workDirectoryIsolated", false, ex.Message);
            return Finish(outPath, checks, details, observed, clock, 1);
        }

        // --- source key -------------------------------------------------------
        var plain = MainWindow.AppendPluginQueueSource(["TMDB"], null);
        var withPlugin = MainWindow.AppendPluginQueueSource(["TMDB"], "com.example");
        var pluginOnly = MainWindow.AppendPluginQueueSource([], "com.example");
        observed["pluginSourceKey"] = pluginOnly.LastOrDefault();
        Check("pluginSourceOnlyAddedWhenActive",
            plain.Length == 1 && plain[0] == "TMDB" && withPlugin.Length == 2 &&
            withPlugin[1] == "plugin:com.example" && pluginOnly.SequenceEqual(["plugin:com.example"]));

        // --- enqueue, duplicate, restart -------------------------------------
        var data = Path.Combine(work, "queue");
        Directory.CreateDirectory(data);
        var item = new ScrapeQueueItem
        {
            VideoId = "video-1", Kind = "title", SearchTitle = "示例作品 1",
            NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        };
        var first = ScrapeQueueStore.Add(data, [item]);
        var duplicate = ScrapeQueueStore.Add(data, [new ScrapeQueueItem
        {
            VideoId = "video-1", Kind = "title", SearchTitle = "示例作品 1",
            NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        }]);
        observed["addedFirst"] = first;
        observed["addedDuplicate"] = duplicate;
        Check("duplicateEnqueueIsIgnored", first == 1 && duplicate == 0);

        var reloaded = ScrapeQueueStore.Load(data);
        Check("queueSurvivesRestart", reloaded.Count == 1 && reloaded[0].VideoId == "video-1" &&
            reloaded[0].SearchTitle == "示例作品 1");

        // --- scheduling requires a source ------------------------------------
        var blocked = ScrapeQueueStore.PeekNextFair(data, [], []);
        var scheduled = ScrapeQueueStore.PeekNextFair(data, [], ["plugin:com.example"]);
        Check("taskBlockedWithoutAnySource", blocked.Item is null && blocked.HasBlocked);
        Check("pluginSourceMakesTaskSchedulable", scheduled.Item is not null &&
            scheduled.Item.VideoId == "video-1" && !scheduled.HasBlocked);

        // --- reschedule keeps the task (plugin timeout path) ------------------
        var rescheduled = scheduled.Item!;
        rescheduled.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(2);
        rescheduled.LastError = "插件超时";
        ScrapeQueueStore.Reschedule(data, rescheduled);
        var afterReschedule = ScrapeQueueStore.Load(data);
        Check("rescheduleKeepsTask", afterReschedule.Count == 1 && !afterReschedule[0].Failed &&
            afterReschedule[0].LastError == "插件超时");

        // A source-level failure keeps the video's retry budget, which is what
        // stops a broken plugin from exhausting the task and becoming "failed".
        var unavailable = new ScrapeSourceUnavailableException("插件暂时不可用", DateTimeOffset.UtcNow.AddMinutes(1));
        Check("pluginFailureIsRetryableNotUnmatched",
            MainWindow.ShouldRetryScrapeItem(afterReschedule[0], unavailable) &&
            MainWindow.AttemptsAfterRetry(afterReschedule[0].Attempts, unavailable) == afterReschedule[0].Attempts);

        // --- cancel ------------------------------------------------------------
        var cancelled = ScrapeQueueStore.ClearPending(data);
        var afterCancel = ScrapeQueueStore.Load(data);
        observed["cleared"] = cancelled;
        Check("cancelRemovesPendingTasks", cancelled == 1 && afterCancel.Count == 0);

        // A failed record is kept by cancel so the user can retry it later.
        ScrapeQueueStore.Add(data, [new ScrapeQueueItem { VideoId = "video-2", Kind = "title", SearchTitle = "x" }]);
        ScrapeQueueStore.MarkFailed(data, "video-2", "内置来源未找到");
        var clearedWithFailed = ScrapeQueueStore.ClearPending(data);
        var afterFailed = ScrapeQueueStore.Load(data);
        Check("cancelKeepsFailedRecords", clearedWithFailed == 0 && afterFailed.Count == 1 && afterFailed[0].Failed);

        return Finish(outPath, checks, details, observed, clock,
            checks.Values.All(value => value) ? 0 : 1);
    }

    static int Finish(string outPath, SortedDictionary<string, bool> checks, List<string> details,
        Dictionary<string, object?> observed, Stopwatch clock, int exitCode)
    {
        var failed = checks.Where(pair => !pair.Value).Select(pair => pair.Key).ToList();
        var payload = new Dictionary<string, object?>
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("O"),
            ["passed"] = failed.Count == 0,
            ["checkCount"] = checks.Count,
            ["elapsedMs"] = clock.ElapsedMilliseconds,
            ["failedChecks"] = failed,
            ["failureDetails"] = details,
            ["observed"] = observed
        };
        foreach (var pair in checks) payload[pair.Key] = pair.Value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("无法写入自检结果：" + ex.Message);
            exitCode = 2;
        }
        Console.Out.WriteLine(failed.Count == 0
            ? $"插件队列自检通过：{checks.Count} 项"
            : $"插件队列自检失败：{failed.Count}/{checks.Count} 项 · {string.Join("、", failed)}");
        return exitCode;
    }
}
