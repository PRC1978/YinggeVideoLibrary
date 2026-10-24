using System.IO;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YinggeVideoLibrary;

internal static class FeatureDiagnostics
{
    public static async Task<int> RunAsync(string resultPath)
    {
        var findings = new Dictionary<string, object>();
        var credentialName = "SelfTest-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "YinggeFeatureTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            WindowsCredentialStore.Write(credentialName, "temporary-secret-中文");
            findings["windowsCredentialRoundTrip"] = WindowsCredentialStore.Read(credentialName) == "temporary-secret-中文";
            WindowsCredentialStore.Write(credentialName, "");
            findings["windowsCredentialDelete"] = WindowsCredentialStore.Read(credentialName) is null;
            Directory.CreateDirectory(directory);
            var fetches = 0;
            async Task<List<MovieMetadata>> Fetch()
            {
                fetches++;
                await Task.Yield();
                return [new MovieMetadata { Title = "candidate", Source = "test" }];
            }
            var first = await ScrapeCandidateCache.GetOrFetchAsync(directory, "test", "query", Fetch);
            var second = await ScrapeCandidateCache.GetOrFetchAsync(directory, "test", "query", Fetch);
            findings["candidateCacheRoundTrip"] = first.Count == 1 && second.Count == 1 && fetches == 1;
            var hintRoot = Path.Combine(directory, "media");
            var nestedWork = Path.Combine(hintRoot, "演员", "Ruth_Lee", "2024", "Example Title", "Example Title.mp4");
            var stageWork = Path.Combine(hintRoot, "S-熊猫可乐-3.72G", "作品标题.mkv");
            findings["folderPersonContextBoundedAndFiltered"] =
                ScrapeSearchHints.FindPersonHint(nestedWork, [hintRoot]) == "Ruth_Lee"
                && ScrapeSearchHints.FindPersonHint(stageWork, [hintRoot]) == "熊猫可乐"
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "电影", "日本", "1080p", "Title.mp4"), [hintRoot]) is null
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "rootvideo.mp4"), [hintRoot]) is null
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot + "-other", "Ruth_Lee", "Title.mp4"), [hintRoot]) is null;
            findings["folderSingleTokenCreatorNames"] =
                ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "ICICIS", "2024", "Example Title.mp4"), [hintRoot]) == "ICICIS"
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "AsiaXXXTour", "作品", "Example Title.mp4"), [hintRoot]) == "AsiaXXXTour";
            findings["folderSingleTokenGenericWordsRejected"] =
                ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "COSPLAY", "Example Title.mp4"), [hintRoot]) is null
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "TRAVEL", "Example Title.mp4"), [hintRoot]) is null
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "Video", "Example Title.mp4"), [hintRoot]) is null;
            var numberedInWork = Path.Combine(hintRoot, "ICICIS", "作品标题", "子目录", "1.mp4");
            var numberedInNamedWork = Path.Combine(hintRoot, "人物名", "作品标题", "子目录", "1.mp4");
            var numberedUnderCategory = Path.Combine(hintRoot, "电影", "日本", "作品标题", "1.mp4");
            findings["folderHierarchySeparatesPersonAndWork"] =
                ScrapeSearchHints.FindPersonHint(numberedInWork, [hintRoot]) == "ICICIS"
                && ScrapeSearchHints.FindWorkTitleHint(numberedInWork, [hintRoot]) == "作品标题"
                && ScrapeSearchHints.FindPersonHint(numberedInNamedWork, [hintRoot]) == "人物名"
                && ScrapeSearchHints.FindWorkTitleHint(numberedInNamedWork, [hintRoot]) == "作品标题"
                && ScrapeSearchHints.FindWorkTitleHint(Path.Combine(hintRoot, "ICICIS", "作品标题", "Video_0001.mp4"), [hintRoot]) == "作品标题"
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "@月暖如若音", "清晨之梦", "子目录", "1.mp4"), [hintRoot]) == "月暖如若音"
                && ScrapeSearchHints.FindWorkTitleHint(Path.Combine(hintRoot, "@月暖如若音", "清晨之梦", "子目录", "1.mp4"), [hintRoot]) == "清晨之梦";
            findings["folderWorkTitleNeedsReliablePerson"] =
                ScrapeSearchHints.FindWorkTitleHint(numberedUnderCategory, [hintRoot]) is null
                && ScrapeSearchHints.FindWorkTitleHint(Path.Combine(hintRoot, "ICICIS", "作品标题", "1.mp4"), [hintRoot + "-other"]) is null
                && ScrapeSearchHints.FindWorkTitleHint(Path.Combine(hintRoot, "ICICIS", "作品标题", "Proper Name.mp4"), [hintRoot]) is null
                && ScrapeSearchHints.IsWeakTitle("Video_0001")
                && !ScrapeSearchHints.IsWeakTitle("Example Title");
            var queueDirectory = Path.Combine(directory, "scrape-queue");
            var mixedJobs = Enumerable.Range(0, 1001).Select(index => new ScrapeQueueItem
            {
                VideoId = $"video-{index}",
                Kind = index % 2 == 0 ? "code" : "title",
                Code = index % 2 == 0 ? $"ABC-{index}" : "",
                SearchTitle = index % 2 == 0 ? "" : $"测试标题 {index}"
            }).ToArray();
            var addedJobs = ScrapeQueueStore.Add(queueDirectory, mixedJobs);
            var loadedJobs = ScrapeQueueStore.Load(queueDirectory);
            findings["fullScrapeQueueWithoutBatchCap"] = addedJobs == mixedJobs.Length && loadedJobs.Count == mixedJobs.Length
                && loadedJobs.Count(item => item.Kind == "title") == 500;
            ScrapeQueueStore.MarkFailed(queueDirectory, "video-0", "test failure");
            var canceledJobs = ScrapeQueueStore.ClearPending(queueDirectory);
            var retainedJobs = ScrapeQueueStore.Load(queueDirectory);
            findings["clearPendingPreservesFailures"] = canceledJobs == 1000 && retainedJobs.Count == 1
                && retainedJobs[0].Failed && retainedJobs[0].VideoId == "video-0";
            var legacyQueueDirectory = Path.Combine(directory, "legacy-scrape-queue");
            Directory.CreateDirectory(legacyQueueDirectory);
            File.WriteAllText(Path.Combine(legacyQueueDirectory, "scrape-queue.json"),
                "[{\"VideoId\":\"legacy\",\"Code\":\"ABC-123\"}]");
            var legacyJobs = ScrapeQueueStore.Load(legacyQueueDirectory);
            findings["legacyCodeQueueCompatibility"] = legacyJobs.Count == 1 && legacyJobs[0].Kind == "code"
                && legacyJobs[0].DisplayQuery == "ABC-123";
            ScrapeQueueStore.CheckpointForDiagnostics(legacyQueueDirectory);
            findings["legacyQueueCheckpoint"] = ScrapeQueueStore.Load(legacyQueueDirectory).Count == 1;
            var legacyBackupDirectory = Path.Combine(directory, "legacy-backup-queue");
            Directory.CreateDirectory(legacyBackupDirectory);
            File.WriteAllText(Path.Combine(legacyBackupDirectory, "scrape-queue.json"), "{corrupt");
            File.WriteAllText(Path.Combine(legacyBackupDirectory, "scrape-queue.json.bak"),
                "[{\"VideoId\":\"backup\",\"Code\":\"ABC-456\"}]");
            findings["legacyQueueBackupRecovery"] = ScrapeQueueStore.Load(legacyBackupDirectory).SingleOrDefault()?.VideoId == "backup";
            var incrementalQueueDirectory = Path.Combine(directory, "incremental-scrape-queue");
            ScrapeQueueStore.Add(incrementalQueueDirectory, mixedJobs);
            var journalPath = Path.Combine(incrementalQueueDirectory, "scrape-queue.journal.00000000.bin");
            var initialJournalBytes = new FileInfo(journalPath).Length;
            for (var index = 0; index < 100; index++)
                ScrapeQueueStore.Remove(incrementalQueueDirectory, $"video-{index}");
            var changedJournalBytes = new FileInfo(journalPath).Length - initialJournalBytes;
            findings["incrementalQueueWrites"] = changedJournalBytes < 25_000
                && ScrapeQueueStore.Load(incrementalQueueDirectory).Count == 901;
            var retryItem = new ScrapeQueueItem { VideoId = "video-100", Kind = "code", Code = "ABC-100",
                Attempts = 2, NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1), LastError = "temporary" };
            ScrapeQueueStore.Reschedule(incrementalQueueDirectory, retryItem);
            ScrapeQueueStore.MarkFailed(incrementalQueueDirectory, "video-101", "failed");
            var nextDelay = DateTimeOffset.UtcNow.AddMinutes(5);
            ScrapeQueueStore.DelayAllUntil(incrementalQueueDirectory, nextDelay);
            var queueStatus = ScrapeQueueStore.GetStatus(incrementalQueueDirectory);
            var queuedNext = ScrapeQueueStore.PeekNext(incrementalQueueDirectory, false);
            findings["incrementalQueueRetryAndDelay"] = queueStatus.PendingCount == 900 && queueStatus.FailedCount == 1
                && queuedNext.Item?.Kind == "title" && queuedNext.Item.NextAttemptAt >= nextDelay
                && !ScrapeQueueStore.HasPending(incrementalQueueDirectory, "video-101");
            ScrapeQueueStore.CheckpointForDiagnostics(incrementalQueueDirectory);
            File.WriteAllText(Path.Combine(incrementalQueueDirectory, "scrape-queue.state.json"), "{corrupt");
            findings["incrementalQueueManifestRecovery"] = ScrapeQueueStore.Load(incrementalQueueDirectory).Count == 901;
            var rotatedJournal = Path.Combine(incrementalQueueDirectory, "scrape-queue.journal.00000001.bin");
            using (var partial = new FileStream(rotatedJournal, FileMode.Append, FileAccess.Write, FileShare.Read))
                partial.Write([0x17, 0x00]);
            var beforeRepair = ScrapeQueueStore.Load(incrementalQueueDirectory).Count;
            ScrapeQueueStore.Remove(incrementalQueueDirectory, "video-102");
            findings["incrementalQueueCrashRecovery"] = beforeRepair == 901
                && ScrapeQueueStore.Load(incrementalQueueDirectory).Count == 900;
            findings["incrementalQueueRetryFailures"] = ScrapeQueueStore.RetryFailures(incrementalQueueDirectory) == 1
                && ScrapeQueueStore.HasPending(incrementalQueueDirectory, "video-101");
            using (var tmdb = JsonDocument.Parse("""{"title":"示例影片","original_title":"Example","release_date":"2025-06-01","runtime":112,"overview":"剧情介绍","genres":[{"name":"剧情"}],"production_companies":[{"name":"示例片厂"}],"vote_average":7.5,"vote_count":10,"credits":{"cast":[{"name":"演员甲"}],"crew":[{"job":"Director","name":"导演乙"}]},"keywords":{"keywords":[{"name":"关键词"}]}}"""))
            {
                var candidate = new MovieMetadata { Title = "示例影片", Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/1" };
                candidate.StampAvailableFields("搜索结果");
                var full = AdditionalScrapers.ParseTmdbDetails(candidate, tmdb.RootElement, "movie");
                var review = new AiReviewRecord { CandidateTitle = full.Title, CandidateSource = full.Source, CandidateDetails = full };
                var restored = JsonSerializer.Deserialize<AiReviewRecord>(JsonSerializer.Serialize(review))!.ToMetadata();
                findings["tmdbFullMetadataReviewRoundTrip"] = restored.DetailFetched && restored.RuntimeMinutes == 112 && restored.Studio == "示例片厂"
                    && restored.Actors.SequenceEqual(["演员甲"]) && restored.Director == "导演乙" && restored.Keywords.SequenceEqual(["关键词"]);
                var independent = restored.Clone();
                independent.FieldProvenance[nameof(MovieMetadata.Actors)].Stage = "changed-in-clone";
                findings["metadataFieldProvenance"] = candidate.FieldProvenance[nameof(MovieMetadata.Title)].Stage == "搜索结果"
                    && restored.FieldProvenance[nameof(MovieMetadata.Actors)].Stage == "详情接口"
                    && restored.FieldProvenance[nameof(MovieMetadata.Actors)].SourceUrl == candidate.SourceUrl
                    && restored.FieldProvenance[nameof(MovieMetadata.Title)].Stage == "详情接口"
                    && restored.FieldProvenance[nameof(MovieMetadata.Studio)].Source == "TMDB 电影"
                    && !restored.FieldProvenance.ContainsKey(nameof(MovieMetadata.Series))
                    && restored.FieldProvenance[nameof(MovieMetadata.Actors)].Stage != independent.FieldProvenance[nameof(MovieMetadata.Actors)].Stage;
                var detailFetches = 0;
                async Task<List<MovieMetadata>> FetchDetails() { detailFetches++; await Task.Yield(); return [full]; }
                var firstDetails = await ScrapeCandidateCache.GetOrFetchAsync(directory, "TMDB 详情", "https://www.themoviedb.org/movie/1", FetchDetails);
                var secondDetails = await ScrapeCandidateCache.GetOrFetchAsync(directory, "TMDB 详情", "https://www.themoviedb.org/movie/1", FetchDetails);
                findings["fullMetadataCandidateCache"] = detailFetches == 1 && firstDetails.Count == 1 && secondDetails.Count == 1
                    && secondDetails[0].Director == "导演乙" && secondDetails[0].Actors.SequenceEqual(["演员甲"]);
            }
            using (var tvmaze = JsonDocument.Parse("""{"name":"剧集示例","premiered":"2024-01-01","summary":"<p>第一季</p>","genres":["Comedy"],"averageRuntime":42,"rating":{"average":8.2},"network":{"name":"示例台","country":{"name":"英国"}},"_embedded":{"cast":[{"person":{"name":"演员丙"}}],"crew":[{"type":"Director","person":{"name":"导演丁"}}]}}"""))
            {
                var full = AdditionalScrapers.ParseTvMazeDetails(new MovieMetadata { Source = "TVmaze" }, tvmaze.RootElement);
                findings["tvMazeFullMetadata"] = full.DetailFetched && full.RuntimeMinutes == 42 && full.Summary == "第一季"
                    && full.Studio == "示例台" && full.Country == "英国" && full.Actors.SequenceEqual(["演员丙"]);
            }
            var javdb = JavDbScraper.ParseJavDbDetails(new MovieMetadata { Code = "ABC-123", Source = "JavDB", SourceUrl = "https://javdb.com/v/sample" },
                "<div class='panel-block'><strong>發行日期:</strong><span class='value'>2025-02-03</span></div>" +
                "<div class='panel-block'><strong>時長:</strong><span class='value'>120 分鐘</span></div>" +
                "<div class='panel-block'><strong>演員:</strong><span class='value'><a>演員戊</a></span></div>");
            findings["javDbDetailMetadata"] = javdb.DetailFetched && javdb.ReleaseDate == "2025-02-03" && javdb.RuntimeMinutes == 120 && javdb.Actors.SequenceEqual(["演員戊"])
                && javdb.FieldProvenance[nameof(MovieMetadata.Actors)].Source == "JavDB"
                && javdb.FieldProvenance[nameof(MovieMetadata.Actors)].Stage == "详情页";
            using (var bangumi = JsonDocument.Parse("""{"data":[{"id":123,"type":2,"name":"Example","name_cn":"示例动画","date":"2025-01-01"}]}"""))
            using (var bangumiDetail = JsonDocument.Parse("""{"id":123,"type":2,"name":"Example","name_cn":"示例动画","date":"2025-01-01","infobox":[{"key":"导演","value":"导演甲"},{"key":"动画制作","value":"工作室乙"}]}"""))
            {
                var found = BangumiScraper.ParseSearchResults(bangumi.RootElement);
                var full = BangumiScraper.ParseDetail(found.Single(), bangumiDetail.RootElement);
                findings["bangumiCandidateToFullDetail"] = full.DetailFetched && full.Title == "示例动画"
                    && full.Director == "导演甲" && full.Studio == "工作室乙"
                    && full.FieldProvenance[nameof(MovieMetadata.Director)].Source == "Bangumi";
            }
            var javDbTitle = JavDbScraper.ParseTitleSearch("<a href=\"/v/sample\" class=\"box\"><img title=\"作品甲\" src=\"https://c0.jdbstatic.com/a.jpg\"><strong>ABC-123</strong></a>");
            findings["javDbTitleCandidate"] = javDbTitle.Count == 1 && javDbTitle[0].Code == "ABC-123"
                && javDbTitle[0].SourceUrl == "https://javdb.com/v/sample" && !javDbTitle[0].DetailFetched;
            var javDatabase = JavDatabaseScraper.ParseDetail(new MovieMetadata
            {
                Code = "ABC-123", Title = "ABC-123", Source = "JAVDatabase", SourceUrl = "https://www.javdatabase.com/movies/abc-123/"
            }, "<html><body>" + new string(' ', 500) +
               "<h1>ABC-123 Example</h1><p class='mb-1'><b>DVD ID</b>ABC-123</p>" +
               "<p class='mb-1'><b>Release Date</b>2025-03-01</p><p class='mb-1'><b>Runtime</b>120 min</p></body></html>");
            findings["javDatabaseExactCodeDetail"] = javDatabase.DetailFetched && javDatabase.ReleaseDate == "2025-03-01"
                && javDatabase.RuntimeMinutes == 120 && javDatabase.Title == "Example";
            var recommendationSamples = new[]
            {
                new RecommendationSnapshot("a", true, "已看", DateTime.Today.AddDays(-1), ["动漫"], ["个人私密标签"]),
                new RecommendationSnapshot("b", false, "未看", DateTime.Today, ["动漫"], ["冒险"])
            };
            var recommendationTopics = RecommendationEngine.BuildBilibiliTopics(recommendationSamples);
            findings["recommendationLocalAndOnlinePrivacy"] = RecommendationEngine.Rank(recommendationSamples, 2).Count == 2
                && recommendationTopics.Any(topic => topic.Keyword == "动画推荐")
                && recommendationTopics.All(topic => !topic.Keyword.Contains("个人私密标签", StringComparison.Ordinal));
            var searchFields = new MovieMetadata { Title = "剧集", Source = "TVmaze", Genres = ["喜剧"], Summary = "搜索摘要" };
            searchFields.StampAvailableFields("搜索结果");
            var refreshedFields = searchFields.Clone();
            refreshedFields.Genres = [];
            refreshedFields.Summary = "详情简介";
            refreshedFields.RefreshChangedFieldProvenance(searchFields, "详情接口");
            findings["metadataFieldProvenanceStaleField"] = !refreshedFields.FieldProvenance.ContainsKey(nameof(MovieMetadata.Genres))
                && refreshedFields.FieldProvenance[nameof(MovieMetadata.Title)].Stage == "搜索结果"
                && refreshedFields.FieldProvenance[nameof(MovieMetadata.Summary)].Stage == "详情接口";
            var legacyMetadata = JsonSerializer.Deserialize<MovieMetadata>("{\"Title\":\"旧资料\",\"Source\":\"JavDB\"}");
            findings["legacyMetadataWithoutFieldProvenance"] = legacyMetadata?.FieldProvenance is { Count: 0 };
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var received = 0;
                var server = Task.Run(async () =>
                {
                    for (var i = 0; i < 2; i++)
                    {
                        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(12));
                        using var stream = client.GetStream();
                        var header = new List<byte>();
                        var buffer = new byte[1];
                        while (header.Count < 16384)
                        {
                            if (await stream.ReadAsync(buffer) != 1) break;
                            header.Add(buffer[0]);
                            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                        }
                        var text = Encoding.ASCII.GetString(header.ToArray());
                        var lengthLine = text.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        var remaining = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
                        while (remaining > 0)
                        {
                            var count = await stream.ReadAsync(new byte[Math.Min(remaining, 4096)]);
                            if (count == 0) break;
                            remaining -= count;
                        }
                        received++;
                        var body = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";
                        var response = i == 0 ? "HTTP/1.1 429 Too Many Requests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            : $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
                    }
                });
                var local = new LibraryConfig { AiEndpoint = $"http://127.0.0.1:{port}/v1", AiDailyBudgetUsd = 1, AiInputUsdPerMillion = 1, AiOutputUsdPerMillion = 4, AiRequestIntervalMs = 500 };
                using var answer = await AiRequestQueue.SendAsync(local, local.AiEndpoint + "/chat/completions", new { model = "local", messages = new[] { new { role = "user", content = "test" } }, max_tokens = 20 }, directory);
                await server;
                findings["localAiRetry"] = received == 2 && answer.RootElement.TryGetProperty("choices", out _);
                findings["aiBudgetPersisted"] = File.Exists(Path.Combine(directory, "ai-budget.json"));
            }
            findings["passed"] = findings.Values.OfType<bool>().All(value => value);
        }
        catch (Exception ex) { findings["passed"] = false; findings["error"] = ex.ToString(); }
        finally
        {
            try { WindowsCredentialStore.Write(credentialName, ""); } catch { }
            try { Directory.Delete(directory, true); } catch { }
        }
        File.WriteAllText(resultPath, JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }));
        return findings.TryGetValue("passed", out var passed) && passed is true ? 0 : 1;
    }
}
