using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Runs one real queue cycle inside an offscreen window, so the plugin path is
// exercised end to end (dispatch, candidate store, review record, queue state)
// without depending on synthetic mouse input.
public partial class MainWindow
{
    public async Task<int> RunPluginQueueProbeAsync(string destination)
    {
        var clock = Stopwatch.StartNew();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            for (var attempt = 0; attempt < 160 && cacheStartupDiagnostic.Stage is not ("refresh finished" or "index error"); attempt++)
                await Task.Delay(250);
            EnsurePluginCatalog();
            var plugin = ActiveQueuePluginId();
            result["pluginQueueEnabled"] = config.PluginQueueEnabled;
            result["activePlugin"] = plugin;
            if (plugin is null)
            {
                result["error"] = "没有可用的队列插件";
                return Write(destination, result, 1);
            }
            var titleFilter = Environment.GetEnvironmentVariable("YINGGE_QUEUE_PROBE_TITLE");
            var video = string.IsNullOrWhiteSpace(titleFilter)
                ? videos.FirstOrDefault()
                : videos.FirstOrDefault(item => item.Title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase));
            if (video is null)
            {
                result["error"] = "没有视频";
                return Write(destination, result, 1);
            }
            result["video"] = video.Title;

            var seeded = ScrapeQueueStore.Add(DataDir, [new ScrapeQueueItem
            {
                VideoId = video.Id, Kind = "title", SearchTitle = video.Title,
                NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1)
            }]);
            result["seeded"] = seeded;

            var expectFailure = string.Equals(Environment.GetEnvironmentVariable("YINGGE_QUEUE_PROBE_EXPECT"),
                "fail", StringComparison.OrdinalIgnoreCase);
            var budget = int.TryParse(Environment.GetEnvironmentVariable("YINGGE_QUEUE_PROBE_SECONDS"), out var seconds)
                ? Math.Clamp(seconds, 10, 300) : 90;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(budget));
            scrapeQueuePaused = false;
            try { await PollScrapeQueueAsync(cancellation.Token); }
            catch (OperationCanceledException) { result["timedOut"] = true; }

            var queue = ScrapeQueueStore.Load(DataDir);
            var reviews = config.AiReviews.GetValueOrDefault(video.Id);
            result["pendingAfterRun"] = queue.Count;
            result["lastError"] = queue.FirstOrDefault()?.LastError ?? "";
            result["candidateCount"] = PluginCandidateStore.CountFor(DataDir, video.Id);
            result["reviewStatus"] = reviews?.Status ?? "";
            result["reviewDecisionSource"] = reviews?.DecisionSource ?? "";
            result["unmatchedRecords"] = config.AiReviews.Values.Count(item => item.Status == "unmatched");
            result["elapsedMs"] = clock.ElapsedMilliseconds;
            var candidates = PluginCandidateStore.CountFor(DataDir, video.Id);
            result["passed"] = expectFailure
                // A failing plugin must keep the task and never produce an
                // "unmatched" record or a review.
                ? queue.Count == 1 && !queue[0].Failed && candidates == 0 &&
                  reviews is null && (result["unmatchedRecords"] as int? ?? 1) == 0
                : queue.Count == 0 && candidates > 0 &&
                  reviews is { Status: "pending" } && reviews.DecisionSource == "plugin";
            return Write(destination, result, result["passed"] is true ? 0 : 1);
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
            result["elapsedMs"] = clock.ElapsedMilliseconds;
            return Write(destination, result, 1);
        }
    }

    static int Write(string destination, Dictionary<string, object?> payload, int exitCode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllText(destination, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("无法写入队列诊断结果：" + ex.Message);
            return 2;
        }
        return exitCode;
    }
}
