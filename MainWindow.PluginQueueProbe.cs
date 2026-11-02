using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Real queue cycles against a real plugin process, driven from an offscreen
// window. Every scenario writes its own JSON so the raw evidence (request
// times, source keys, queue state, retry counts, leftover processes) can be
// inspected without a screenshot.
public partial class MainWindow
{
    public async Task<int> RunPluginQueueProbeAsync(string destination)
    {
        var clock = Stopwatch.StartNew();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var scenario = (Environment.GetEnvironmentVariable("YINGGE_QUEUE_PROBE_SCENARIO") ?? "single")
            .Trim().ToLowerInvariant();
        result["scenario"] = scenario;
        try
        {
            for (var attempt = 0; attempt < 160 &&
                cacheStartupDiagnostic.Stage is not ("refresh finished" or "index error"); attempt++)
                await Task.Delay(250);
            EnsurePluginCatalog();
            var plugins = QueuePlugins();
            result["pluginQueueEnabled"] = config.PluginQueueEnabled;
            result["plugins"] = plugins.Select(plugin => new
            {
                id = plugin.Id,
                name = plugin.DisplayName,
                key = PluginQueueSourceKey(plugin.Id),
                intervalSeconds = PolicyForSource(PluginQueueSourceKey(plugin.Id)).IntervalSeconds
            }).ToList();
            if (plugins.Count == 0)
            {
                result["error"] = "没有可用的队列插件";
                return Write(destination, result, 1);
            }

            var targets = SelectProbeVideos();
            result["videos"] = targets.Select(item => item.Title).ToList();
            if (targets.Count == 0)
            {
                result["error"] = "没有匹配的视频";
                return Write(destination, result, 1);
            }

            var before = ReadInvocations();
            result["invocationsBefore"] = before.Count;
            var seeded = 0;
            foreach (var video in targets)
            {
                seeded += ScrapeQueueStore.Add(DataDir, [new ScrapeQueueItem
                {
                    VideoId = video.Id, Kind = "title", SearchTitle = video.Title,
                    NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                }]);
            }
            // Duplicate scenarios enqueue the first video a second time; the store
            // must keep one entry, not two.
            if (scenario == "duplicate")
                seeded += ScrapeQueueStore.Add(DataDir, [new ScrapeQueueItem
                {
                    VideoId = targets[0].Id, Kind = "title", SearchTitle = targets[0].Title,
                    NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                }]);
            result["seeded"] = seeded;
            result["queueAfterSeed"] = ScrapeQueueStore.Load(DataDir).Count;

            var budget = Math.Clamp(ReadInt("YINGGE_QUEUE_PROBE_SECONDS", 90), 5, 300);
            var rounds = scenario == "duplicate" ? 2 : 1;
            for (var round = 0; round < rounds; round++)
            {
                if (round > 0)
                {
                    ScrapeQueueStore.Add(DataDir, [new ScrapeQueueItem
                    {
                        VideoId = targets[0].Id, Kind = "title", SearchTitle = targets[0].Title,
                        NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                    }]);
                }
                var cancelAfter = ReadInt("YINGGE_QUEUE_PROBE_CANCEL_MS", 0);
                using var cancellation = cancelAfter > 0
                    ? new CancellationTokenSource(TimeSpan.FromMilliseconds(cancelAfter))
                    : new CancellationTokenSource(TimeSpan.FromSeconds(budget));
                scrapeQueuePaused = false;
                var roundClock = Stopwatch.StartNew();
                try { await PollScrapeQueueAsync(cancellation.Token); }
                catch (OperationCanceledException) { result["cancelledMidRun"] = true; }
                result[$"round{round + 1}ElapsedMs"] = roundClock.ElapsedMilliseconds;
            }

            // A killed child can take a moment to disappear from the process list.
            await Task.Delay(2500);
            var invocations = ReadInvocations();
            result["requestLog"] = invocations
                .Select(item => new { at = item.At.ToString("yyyy-MM-dd HH:mm:ss.fff"), pluginId = item.PluginId })
                .ToList();
            result["invocations"] = invocations.Count - before.Count;
            result["intervalsMs"] = InvocationIntervals(invocations);
            result["residualPluginProcesses"] = ResidualPluginProcesses(plugins);
            result["residualCount"] = ((List<object>)result["residualPluginProcesses"]!).Count;

            var queue = ScrapeQueueStore.Load(DataDir);
            result["queueAfterRun"] = queue.Select(item => new
            {
                videoId = item.VideoId, kind = item.Kind, searchTitle = item.SearchTitle,
                attempts = item.Attempts, failed = item.Failed,
                nextAttemptAt = item.NextAttemptAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                lastError = item.LastError
            }).ToList();
            result["pendingAfterRun"] = queue.Count;
            result["failedAfterRun"] = queue.Count(item => item.Failed);

            var reviews = new Dictionary<string, object?>(StringComparer.Ordinal);
            var candidates = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var video in targets)
            {
                var review = config.AiReviews.GetValueOrDefault(video.Id);
                reviews[video.Title] = review is null ? null : new
                {
                    status = review.Status, decisionSource = review.DecisionSource,
                    candidateCode = review.CandidateCode, reason = review.Reason,
                    reviewedAt = review.ReviewedAt
                };
                candidates[video.Title] = PluginCandidateStore.CountFor(DataDir, video.Id);
            }
            result["reviews"] = reviews;
            result["candidateCounts"] = candidates;
            result["unmatchedRecords"] = config.AiReviews.Values.Count(item => item.Status == "unmatched");

            var now = DateTimeOffset.UtcNow;
            var cooldowns = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var plugin in plugins)
            {
                var key = PluginQueueSourceKey(plugin.Id);
                var until = ScrapeQueueStore.GetSourceCooldownAt(DataDir, key);
                cooldowns[key] = new
                {
                    coolingDown = until > now,
                    untilLocal = until == DateTimeOffset.MinValue ? "" : until.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    secondsLeft = until > now ? Math.Round((until - now).TotalSeconds, 1) : 0,
                    readyAt = ScrapeQueueStore.GetSourceReadyAt(DataDir, key).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")
                };
            }
            result["sourceCooldowns"] = cooldowns;
            result["elapsedMs"] = clock.ElapsedMilliseconds;
            return Evaluate(destination, result, scenario, targets, plugins, invocations, queue, candidates, cooldowns);
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
            result["elapsedMs"] = clock.ElapsedMilliseconds;
            return Write(destination, result, 1);
        }
    }

    // Runs exactly what the review row's candidate selector runs on
    // SelectionChanged. Used when UI Automation cannot drive a combo box in a
    // window that is not allowed to take focus (for example while another
    // application owns the foreground).
    public async Task<int> RunSelectCandidateProbeAsync(string destination, string titleFilter, int index)
    {
        for (var attempt = 0; attempt < 160 &&
            cacheStartupDiagnostic.Stage is not ("refresh finished" or "index error"); attempt++)
            await Task.Delay(250);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var video = videos.FirstOrDefault(item => item.Title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase));
        if (video is null)
        {
            result["error"] = "没有匹配的视频";
            return Write(destination, result, 1);
        }
        var before = config.AiReviews.GetValueOrDefault(video.Id);
        result["video"] = video.Title;
        result["beforeCode"] = before?.CandidateCode ?? "";
        result["beforeUrl"] = before?.CandidateSourceUrl ?? "";
        result["candidateCount"] = PluginCandidateStore.CountFor(DataDir, video.Id);
        ApplyPluginCandidateSelection(video.Id, index);
        var after = config.AiReviews.GetValueOrDefault(video.Id);
        result["afterCode"] = after?.CandidateCode ?? "";
        result["afterUrl"] = after?.CandidateSourceUrl ?? "";
        result["status"] = after?.Status ?? "";
        result["index"] = index;
        result["passed"] = after?.CandidateCode == PluginCandidateStore.Get(DataDir, video.Id)[index].Metadata.Code;
        return Write(destination, result, result["passed"] is true ? 0 : 1);
    }

    // Runs exactly what the review row's 通过 / 否决 buttons run, and reports the
    // same message a failure would put in the dialog.
    public async Task<int> RunReviewDecisionProbeAsync(string destination, string titleFilter, bool approve)
    {
        for (var attempt = 0; attempt < 160 &&
            cacheStartupDiagnostic.Stage is not ("refresh finished" or "index error"); attempt++)
            await Task.Delay(250);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var video = videos.FirstOrDefault(item => item.Title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase));
        if (video is null) { result["error"] = "没有匹配的视频"; return Write(destination, result, 1); }
        var before = config.AiReviews.GetValueOrDefault(video.Id);
        result["video"] = video.Title;
        result["beforeStatus"] = before?.Status ?? "";
        result["beforeCode"] = before?.CandidateCode ?? "";
        var ok = TryCommitReviewDecisionsWithPreview([video.Id], approve, out var error);
        result["commitOk"] = ok;
        result["error"] = error;
        result["afterStatus"] = config.AiReviews.GetValueOrDefault(video.Id)?.Status ?? "";
        result["metadataHasCode"] = metadata.GetValueOrDefault(video.Id)?.Code ?? "";
        result["passed"] = ok && result["afterStatus"] as string == (approve ? "approved" : "rejected");
        return Write(destination, result, result["passed"] is true ? 0 : 1);
    }

    // The probe keeps the previously used title filter so a scenario can pick a
    // video whose name triggers a plugin failure mode; "count:N" seeds N videos.
    List<VideoItem> SelectProbeVideos()
    {
        var filter = Environment.GetEnvironmentVariable("YINGGE_QUEUE_PROBE_TITLE");
        var countText = Environment.GetEnvironmentVariable("YINGGE_QUEUE_PROBE_COUNT");
        var count = int.TryParse(countText, out var parsed) ? Math.Clamp(parsed, 1, 12) : 1;
        var pool = videos.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(filter))
            pool = pool.Where(item => item.Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
        return pool.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).Take(count).ToList();
    }

    static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    List<(DateTimeOffset At, string PluginId)> ReadInvocations()
    {
        var list = new List<(DateTimeOffset, string)>();
        var folder = Path.Combine(DataDir, "plugins", "logs");
        if (!Directory.Exists(folder)) return list;
        foreach (var file in Directory.GetFiles(folder, "*.log"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            foreach (var line in File.ReadAllLines(file))
            {
                if (!line.Contains("查询「", StringComparison.Ordinal) || line.Length < 24) continue;
                if (DateTimeOffset.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
                    list.Add((at, id));
            }
        }
        list.Sort((left, right) => left.Item1.CompareTo(right.Item1));
        return list;
    }

    static List<long> InvocationIntervals(List<(DateTimeOffset At, string PluginId)> invocations) =>
        invocations.Zip(invocations.Skip(1), (first, second) => (long)(second.At - first.At).TotalMilliseconds)
            .ToList();

    static List<object> ResidualPluginProcesses(IReadOnlyList<PluginDescriptor> plugins)
    {
        var names = plugins.Select(plugin => Path.GetFileNameWithoutExtension(plugin.Manifest?.Entry?.File ?? ""))
            .Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<object>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (names.Contains(process.ProcessName))
                    found.Add(new { name = process.ProcessName, pid = process.Id });
            }
            catch { /* a process can exit between enumeration and inspection */ }
            finally { process.Dispose(); }
        }
        return found;
    }

    int Evaluate(string destination, Dictionary<string, object?> result, string scenario,
        List<VideoItem> targets, IReadOnlyList<PluginDescriptor> plugins,
        List<(DateTimeOffset At, string PluginId)> invocations, List<ScrapeQueueItem> queue,
        Dictionary<string, int> candidates, Dictionary<string, object?> cooldowns)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var reviews = (Dictionary<string, object?>)result["reviews"]!;
        var pending = (int)result["pendingAfterRun"]!;
        var unmatched = (int)result["unmatchedRecords"]!;
        var intervals = (List<long>)result["intervalsMs"]!;
        var residual = (int)result["residualCount"]!;
        var firstKey = PluginQueueSourceKey(plugins[0].Id);
        var firstCooldown = (dynamic)cooldowns[firstKey]!;
        bool Cooling(string key) => (bool)((dynamic)cooldowns[key]!).coolingDown;
        var candidateTotal = candidates.Values.Sum();

        switch (scenario)
        {
            case "multi":
                checks["everyVideoHasReview"] = candidates.Count == targets.Count &&
                    candidates.Values.All(count => count > 0);
                checks["queueDrained"] = pending == 0;
                checks["spacingRespected"] = intervals.Count >= 1 && intervals.All(value => value >= 5500);
                break;
            case "timeout":
                checks["pluginCalledOnce"] = (int)result["invocations"]! == 1;
                checks["taskKept"] = pending == 1 && (int)result["failedAfterRun"]! == 0;
                checks["cooldownRecorded"] = Cooling(firstKey) && (double)firstCooldown.secondsLeft >= 5;
                checks["noResidualProcess"] = residual == 0;
                checks["errorSaysTimeout"] = queue.Count == 1 &&
                    queue[0].LastError.Contains("timeout", StringComparison.OrdinalIgnoreCase);
                break;
            case "crash":
                checks["pluginCalledOnce"] = (int)result["invocations"]! == 1;
                checks["taskKept"] = pending == 1 && (int)result["failedAfterRun"]! == 0;
                checks["attemptCounted"] = queue.Count == 1 && queue[0].Attempts == 1;
                checks["cooldownRecorded"] = Cooling(firstKey) && (double)firstCooldown.secondsLeft >= 5;
                break;
            case "restart":
                checks["noNewInvocation"] = (int)result["invocations"]! == 0;
                checks["cooldownPersisted"] = Cooling(firstKey);
                checks["taskStillPending"] = pending == 1 && queue.Count == 1;
                break;
            case "independence":
                checks["failingPluginCooled"] = Cooling(firstKey);
                checks["healthyPluginUsed"] = candidateTotal > 0;
                checks["queueDrained"] = pending == 0;
                break;
            case "builtin":
                // A crashing plugin must leave the built-in path free to run and
                // to make its own decision. Here TVmaze answers "no such title",
                // so the unmatched record is the built-in's, not the plugin's.
                var builtInReview = targets
                    .Select(video => config.AiReviews.GetValueOrDefault(video.Id))
                    .FirstOrDefault(review => review is not null);
                checks["pluginCalledOnce"] = (int)result["invocations"]! == 1;
                checks["pluginCooledDown"] = Cooling(firstKey);
                checks["builtInMadeTheDecision"] = queue.Count == 0 && unmatched == 1 &&
                    builtInReview is { Status: "unmatched", DecisionSource: "rules" } &&
                    builtInReview.Reason.Contains("没有找到标题候选", StringComparison.Ordinal);
                break;
            case "cancel":
                checks["noResidualProcess"] = residual == 0;
                checks["cancelledBeforeBudget"] = (long)result["round1ElapsedMs"]! < 30000;
                break;
            case "duplicate":
                checks["queueSingleEntry"] = (int)result["queueAfterSeed"]! == 1;
                checks["queueDrained"] = pending == 0;
                checks["oneCandidateSet"] = candidateTotal == 3;
                checks["noDuplicateReview"] = reviews.Count == targets.Count;
                break;
            default:
                checks["queueDrained"] = pending == 0;
                checks["candidatesStored"] = candidateTotal > 0;
                break;
        }
        // Only the built-in scenario expects an unmatched record: there the
        // built-in source itself answered "no such title", which is its own
        // honest decision, not the plugin's failure being misreported.
        if (scenario != "builtin") checks["noUnmatched"] = unmatched == 0;
        result["checks"] = checks;
        result["passed"] = checks.Values.All(value => value);
        return Write(destination, result, result["passed"] is true ? 0 : 1);
    }

    static int Write(string destination, Dictionary<string, object?> payload, int exitCode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllText(destination, JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("无法写入队列诊断结果：" + ex.Message);
            return 2;
        }
        return exitCode;
    }
}
