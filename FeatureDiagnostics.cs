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
            File.WriteAllText(Path.Combine(queueDirectory, "scrape-queue.json"),
                "[{\"VideoId\":\"legacy\",\"Code\":\"ABC-123\"}]");
            var legacyJobs = ScrapeQueueStore.Load(queueDirectory);
            findings["legacyCodeQueueCompatibility"] = legacyJobs.Count == 1 && legacyJobs[0].Kind == "code"
                && legacyJobs[0].DisplayQuery == "ABC-123";
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
