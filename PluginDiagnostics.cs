using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

// Offline acceptance for the plugin platform. Every scenario runs inside the
// caller-supplied work directory; the real library is never opened and no
// network request is made. The sample plugin drives the process scenarios.
internal static class PluginDiagnostics
{
    internal static int RunToFile(string workDirectory, string outPath, string? sampleDirectory)
    {
        var checks = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        var details = new List<string>();
        var observed = new Dictionary<string, object?>(StringComparer.Ordinal);
        var notExercised = new List<string>();
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
                work.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                work.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            Check("workDirectoryIsolated", !forbidden, forbidden ? "工作目录指向真实库或程序目录，已拒绝写入" : "");
            if (forbidden) return Finish(outPath, checks, details, observed, notExercised, clock, 1);
            Directory.CreateDirectory(work);
        }
        catch (Exception ex)
        {
            Check("workDirectoryIsolated", false, "工作目录无效：" + ex.Message);
            return Finish(outPath, checks, details, observed, notExercised, clock, 1);
        }

        var root = Path.Combine(work, "data", "plugins");
        var dataDir = Path.Combine(work, "data");
        Directory.CreateDirectory(root);

        // --- manifest and catalog ------------------------------------------------
        Check("emptyCatalogScans", PluginManifestReader.Scan(Path.Combine(work, "missing")).Count == 0);
        WriteManifest(root, "bad-json", "{ this is not json");
        WriteManifest(root, "no-id", """{"schemaVersion":1,"name":"x","version":"1.0.0","apiVersion":1,"capabilities":["scrape.source"],"entry":{"file":"a.exe"}}""");
        WriteManifest(root, "dup", ManifestJson("com.example.dup", version: "1.0.0"));
        WriteManifest(root, "dup-two", ManifestJson("com.example.dup", version: "1.0.1"));
        WriteManifest(root, "future-api", ManifestJson("com.example.future", apiVersion: 2));
        WriteManifest(root, "theme", ManifestJson("com.example.theme", capability: "ui.theme"));
        WriteManifest(root, "escape", ManifestJson("com.example.escape", entry: "../escape.exe"));
        WriteManifest(root, "no-entry", ManifestJson("com.example.noentry", entry: "missing.exe"));
        WriteManifest(root, "ready", ManifestJson("com.example.ready", entry: "ready.exe"));
        File.WriteAllText(Path.Combine(root, "ready", "ready.exe"), "stub");

        var scanClock = Stopwatch.StartNew();
        var catalog = PluginManifestReader.Scan(root);
        scanClock.Stop();
        observed["catalogScanMs"] = scanClock.Elapsed.TotalMilliseconds;
        PluginDescriptor Find(string folder) => catalog.First(item => item.FolderName == folder);
        observed["catalogCount"] = catalog.Count;
        Check("damagedManifestReported", Find("bad-json").Status == PluginStatus.DamagedManifest);
        Check("missingIdReported", Find("no-id").Status == PluginStatus.DamagedManifest);
        Check("duplicateIdReported", Find("dup").Status == PluginStatus.DuplicateId && Find("dup-two").Status == PluginStatus.DuplicateId);
        Check("incompatibleApiReported", Find("future-api").Status == PluginStatus.IncompatibleApi);
        Check("unsupportedCapabilityReported", Find("theme").Status == PluginStatus.UnsupportedCapability);
        Check("unsafeEntryReported", Find("escape").Status == PluginStatus.UnsafeEntry);
        Check("missingEntryReported", Find("no-entry").Status == PluginStatus.MissingEntry);
        Check("readyPluginAccepted", Find("ready").Status == PluginStatus.Ready);

        // --- enablement and consent ---------------------------------------------
        var state = new PluginState();
        state.Enabled.Add("com.example.ready");
        state.Consents["com.example.ready"] = PluginManifestReader.Signature(new PluginPermissions
        {
            Network = ["example.com"], Data = ["query.text"]
        });
        Check("enablePersists", PluginStateStore.Save(dataDir, state, out _) &&
            PluginStateStore.IsEnabled(PluginStateStore.Load(dataDir), "com.example.ready"));
        var reloaded = PluginStateStore.Load(dataDir);
        reloaded.Enabled.RemoveAll(id => id == "com.example.ready");
        PluginStateStore.Save(dataDir, reloaded, out _);
        Check("disablePersists", !PluginStateStore.IsEnabled(PluginStateStore.Load(dataDir), "com.example.ready"));

        var narrow = PluginManifestReader.Signature(new PluginPermissions { Network = ["example.com"], Data = ["query.text"] });
        var wide = PluginManifestReader.Signature(new PluginPermissions { Network = ["example.com", "evil.example"], Data = ["query.text"] });
        Check("consentSignatureChangesWithPermissions", narrow.Length == 32 && narrow != wide);
        var changed = PluginManifestReader.Read(Path.Combine(root, "ready"));
        var consentState = new PluginState();
        consentState.Consents[changed.Id] = wide;
        Check("consentMismatchDisables", !PluginStateStore.ConsentMatches(consentState, changed));

        // --- independent process -------------------------------------------------
        if (string.IsNullOrWhiteSpace(sampleDirectory) || !Directory.Exists(sampleDirectory))
        {
            notExercised.Add("processScenarios(samplePluginDirectoryMissing)");
        }
        else
        {
            var sample = CopySample(sampleDirectory!, Path.Combine(root, "sample"));
            var samplePlugin = PluginManifestReader.Read(sample);
            observed["sampleStatus"] = samplePlugin.Status.ToString();
            observed["sampleVersion"] = samplePlugin.Manifest?.Version;
            Check("samplePluginLoadable", samplePlugin.Status == PluginStatus.Ready, samplePlugin.Message);

            var hello = PluginProcessHost.DescribeAsync(samplePlugin, CancellationToken.None).GetAwaiter().GetResult();
            Check("handshakeReturnsName", hello is { Name.Length: > 0 } && hello.Capabilities.Contains("scrape.source"),
                hello is null ? "describe 未返回结果" : $"name={hello.Name}");

            var search = PluginProcessHost.SearchAsync(samplePlugin,
                new PluginSearchParameters { Token = "t-1", Query = "示例作品", Kind = "title", Limit = 5 },
                CancellationToken.None).GetAwaiter().GetResult();
            observed["searchCandidates"] = search.Candidates.Count;
            observed["searchElapsedMs"] = search.ElapsedMs;
            Check("searchReturnsCandidates", search.Ok && search.Candidates.Count > 0);
            Check("candidateSourceUrlValidated", search.Candidates.All(item => item.SourceUrl.StartsWith("https://example.com/", StringComparison.Ordinal)));

            var leak = PluginProcessHost.SearchAsync(samplePlugin,
                new PluginSearchParameters { Token = "t-2", Query = "pathleak", Kind = "title", Limit = 3 },
                CancellationToken.None).GetAwaiter().GetResult();
            Check("pathLeakCandidateRejected", leak.Ok && leak.Candidates.Count == 0 && leak.DroppedCandidates == 1,
                $"candidates={leak.Candidates.Count} dropped={leak.DroppedCandidates}");

            var rate = PluginProcessHost.SearchAsync(samplePlugin,
                new PluginSearchParameters { Token = "t-3", Query = "ratelimit", Kind = "title", Limit = 3 },
                CancellationToken.None).GetAwaiter().GetResult();
            Check("rateLimitSurfacesRetryAfter", !rate.Ok && rate.ErrorCode == "rate_limited" &&
                rate.RetryAfter is { TotalMinutes: >= 14 and <= 16 });

            var huge = PluginProcessHost.SearchAsync(samplePlugin,
                new PluginSearchParameters { Token = "t-4", Query = "huge", Kind = "title", Limit = 3 },
                CancellationToken.None).GetAwaiter().GetResult();
            Check("oversizeResponseRejected", !huge.Ok && huge.ErrorCode == "response_too_large", huge.ErrorCode);

            var stream = PluginProcessHost.SearchAsync(samplePlugin,
                new PluginSearchParameters { Token = "t-7", Query = "stream", Kind = "title", Limit = 3 },
                CancellationToken.None).GetAwaiter().GetResult();
            observed["streamElapsedMs"] = stream.ElapsedMs;
            Check("streamingResponseIsCutOff", !stream.Ok && stream.ErrorCode == "response_too_large" &&
                stream.ElapsedMs < 8000, $"code={stream.ErrorCode} ms={stream.ElapsedMs}");

            // 8 MB with no newline: the reader must stop at the cap without
            // materialising the whole line first.
            var bounded = PluginProcessHost.MeasureBoundedRead(new string('y', 8 * 1024 * 1024), PluginProcessHost.MaxLineChars);
            observed["boundedReadCharacters"] = bounded.CharactersRead;
            // It must stop at the cap, not consume the 8 M characters first.
            Check("boundedReaderStopsAtCap", bounded.TooLong &&
                bounded.CharactersRead <= PluginProcessHost.MaxLineChars + 2,
                $"tooLong={bounded.TooLong} chars={bounded.CharactersRead}");

            var crash = PluginProcessHost.SearchAsync(samplePlugin,
                new PluginSearchParameters { Token = "t-5", Query = "crash", Kind = "title", Limit = 3 },
                CancellationToken.None).GetAwaiter().GetResult();
            Check("crashReportsExitCode", !crash.Ok && crash.ErrorCode == "plugin_exited" &&
                crash.ErrorMessage.Contains("7", StringComparison.Ordinal), crash.ErrorMessage);

            // A two-second budget keeps the acceptance run short; the production
            // default stays at the manifest value.
            var shortBudget = CopySample(sampleDirectory!, Path.Combine(root, "sample-timeout"));
            RewriteTimeout(shortBudget, 2);
            var shortPlugin = PluginManifestReader.Read(shortBudget);
            var slowClock = Stopwatch.StartNew();
            var ticks = 0;
            var slowTask = PluginProcessHost.SearchAsync(shortPlugin,
                new PluginSearchParameters { Token = "t-6", Query = "slow", Kind = "title", Limit = 3 },
                CancellationToken.None);
            while (!slowTask.IsCompleted && slowClock.Elapsed < TimeSpan.FromSeconds(20))
            {
                ticks++;
                Thread.Sleep(50);
            }
            var slow = slowTask.GetAwaiter().GetResult();
            observed["timeoutElapsedMs"] = slow.ElapsedMs;
            observed["callerTicksWhileWaiting"] = ticks;
            Check("timeoutKillsProcess", !slow.Ok && slow.ErrorCode == "timeout" && slow.ElapsedMs < 6000, slow.ErrorCode);
            Thread.Sleep(600);
            var orphans = Process.GetProcessesByName("ExampleScrapePlugin").Length;
            observed["orphanProcessesAfterTimeout"] = orphans;
            Check("timeoutLeavesNoOrphan", orphans == 0);
            // The calling thread kept running its own loop while the plugin was
            // pending: the await returns to the caller, so the UI thread is free.
            Check("callDoesNotBlockCaller", ticks > 10, $"ticks={ticks}");
        }

        // --- candidate store: keep every candidate across restarts ----------------
        var candidateDir = Path.Combine(work, "candidates");
        PluginCandidateRecord Candidate(string url, string code) => new()
        {
            PluginId = "com.example.review",
            SavedAt = DateTimeOffset.Now,
            Hint = 60,
            Evidence = "自检",
            Metadata = new MovieMetadata { Title = "候选 " + code, Source = "示例来源", SourceUrl = url, Code = code }
        };
        var firstCount = PluginCandidateStore.Add(candidateDir, "video-1", Candidate("https://example.com/1", "EX-1"));
        var duplicateCount = PluginCandidateStore.Add(candidateDir, "video-1", Candidate("https://example.com/1", "EX-1"));
        var secondCount = PluginCandidateStore.Add(candidateDir, "video-1", Candidate("https://example.com/2", "EX-2"));
        var reloadedCandidates = PluginCandidateStore.Load(candidateDir).Videos["video-1"];
        observed["candidatesForVideo"] = secondCount;
        Check("candidateStoreKeepsAll", firstCount == 1 && secondCount == 2);
        Check("candidateStoreDedupes", duplicateCount == 1);
        Check("candidateStoreSurvivesReload", reloadedCandidates.Count == 2 &&
            PluginCandidateStore.CountFor(candidateDir, "video-1") == 2);

        // --- plugin state must not claim success when the file is read-only -------
        var readOnlyDir = Path.Combine(work, "readonly-state");
        Directory.CreateDirectory(readOnlyDir);
        var initialState = new PluginState();
        initialState.Enabled.Add("com.example.ready");
        PluginStateStore.Save(readOnlyDir, initialState, out _);
        var statePath = Path.Combine(readOnlyDir, PluginStateStore.FileName);
        File.SetAttributes(statePath, FileAttributes.ReadOnly);
        var beforeText = File.ReadAllText(statePath);
        var nextState = PluginStateStore.Clone(initialState);
        nextState.Enabled.Add("com.example.other");
        var savedOnReadOnly = PluginStateStore.Save(readOnlyDir, nextState, out var saveError);
        Check("stateSaveFailsWhenReadOnly", !savedOnReadOnly && saveError.Length > 0 &&
            File.ReadAllText(statePath) == beforeText, saveError);
        File.SetAttributes(statePath, FileAttributes.Normal);

        // --- privacy and review gate --------------------------------------------
        var request = PluginProtocol.Encode(new PluginRequest
        {
            Id = "r1", Method = PluginProtocol.MethodSearch,
            Parameters = new PluginSearchParameters { Token = "opaque-token", Query = "标题", Kind = "title", Limit = 5 }
        });
        Check("requestCarriesNoPathOrVideoId", !request.Contains("\\\\", StringComparison.Ordinal) &&
            !request.Contains("C:", StringComparison.Ordinal) && !request.Contains("path", StringComparison.OrdinalIgnoreCase) &&
            request.Contains("opaque-token", StringComparison.Ordinal));

        var redacted = PluginLog.Redact(@"打开 C:\Users\someone\Videos\a.mp4 失败，key=abcdefghijklmnopqrstuvwxyz0123456789ABCDEF");
        Check("redactionHidesPaths", !redacted.Contains("someone", StringComparison.Ordinal) && !redacted.Contains("abcdefghij", StringComparison.Ordinal));

        var pluginForReview = new PluginDescriptor
        {
            FolderPath = root, FolderName = "review",
            Manifest = JsonSerializer.Deserialize<PluginManifest>(ManifestJson("com.example.review"))!,
            Status = PluginStatus.Ready
        };
        var reviewCandidate = new PluginCandidate("示例 作品", null, "EX-001", 2024, "示例来源",
            "https://example.com/work/1", null, null, "合成候选", 100);
        var metadata = PluginScrapeBridge.ToMetadata(reviewCandidate, pluginForReview);
        var review = PluginScrapeBridge.BuildPendingReview("video-1", "示例 作品 EX-001", metadata, pluginForReview, 100);
        Check("reviewIsPending", review.Status == "pending");
        Check("reviewConfidenceCapped", review.Confidence < PluginScrapeBridge.ManualThreshold, $"confidence={review.Confidence}");
        Check("reviewKeepsHostUnverified", !metadata.DetailIdentityVerified && review.DecisionSource == "plugin");
        var matching = PluginScrapeBridge.BuildPendingReview("video-1", "EX-001 示例", metadata, pluginForReview, 100);
        Check("codeMatchStillPending", matching.Status == "pending", matching.Reason);
        Check("reviewRecordsPluginIdentity", review.Model.Contains("com.example.review", StringComparison.Ordinal));

        var exitCode = checks.Values.All(value => value) ? 0 : 1;
        return Finish(outPath, checks, details, observed, notExercised, clock, exitCode);
    }

    static int Finish(string outPath, SortedDictionary<string, bool> checks, List<string> details,
        Dictionary<string, object?> observed, List<string> notExercised, Stopwatch clock, int exitCode)
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
            ["notExercised"] = notExercised,
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
            ? $"插件自检通过：{checks.Count} 项"
            : $"插件自检失败：{failed.Count}/{checks.Count} 项 · {string.Join("、", failed)}");
        return exitCode;
    }

    static void WriteManifest(string root, string folder, string json)
    {
        var path = Path.Combine(root, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "plugin.json"), json);
    }

    static string ManifestJson(string id, string version = "1.0.0", int apiVersion = 1,
        string capability = "scrape.source", string entry = "a.exe") =>
        new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["name"] = "测试插件 " + id,
            ["version"] = version,
            ["apiVersion"] = apiVersion,
            ["author"] = "自检",
            ["capabilities"] = new JsonArray(capability),
            ["permissions"] = new JsonObject
            {
                ["network"] = new JsonArray("example.com"),
                ["data"] = new JsonArray("query.text", "query.code")
            },
            ["entry"] = new JsonObject { ["file"] = entry },
            ["timeoutSeconds"] = 10
        }.ToJsonString();

    static string CopySample(string source, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        return destination;
    }

    static void RewriteTimeout(string folder, int seconds)
    {
        var path = Path.Combine(folder, "plugin.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["timeoutSeconds"] = seconds;
        File.WriteAllText(path, node.ToJsonString());
    }
}
