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
