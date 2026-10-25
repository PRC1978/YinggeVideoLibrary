using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class AiQueueConcurrencyDiagnostics
{
    internal static async Task<IReadOnlyDictionary<string, bool>> VerifyAsync(string root)
    {
        Directory.CreateDirectory(root);
        var findings = new Dictionary<string, bool>();
        var payload = new { model = "offline", messages = new[] { new { role = "user", content = "hello" } }, max_tokens = 40 };
        var config = new LibraryConfig
        {
            AiKey = "cloud-key-must-not-reach-loopback", AiDailyBudgetUsd = 1,
            AiInputUsdPerMillion = 1, AiOutputUsdPerMillion = 4,
            AiMaxOutputTokens = 40, AiLocalConcurrency = 2,
            AiLocalRequestIntervalMs = 500, AiLocalRetryCount = 0
        };
        await using (var api = new CaptureApi(2))
        {
            var directory = Path.Combine(root, "parallel");
            var first = AiRequestQueue.SendAsync(config, api.Endpoint, payload, directory);
            var second = AiRequestQueue.SendAsync(config, api.Endpoint, payload, directory);
            using var a = await first.WaitAsync(TimeSpan.FromSeconds(8));
            using var b = await second.WaitAsync(TimeSpan.FromSeconds(8));
            var ledger = AiRequestQueue.LoadBudgetLedger(directory);
            findings["aiLocalTwoRequestsOverlapWithoutHoldingBudgetMutex"] =
                api.AcceptedCount == 2 && ledger.AttemptCount == 2 &&
                ledger.ReportedUsageCount == 2 && api.DidNotReceiveCloudKey;
        }

        await using (var api = new CaptureApi(1))
        {
            var directory = Path.Combine(root, "budget-cap");
            var requestJson = JsonSerializer.Serialize(payload);
            config.AiDailyBudgetUsd = AiRequestQueue.EstimateReservedUsd(config, requestJson) * 1.5;
            var first = AiRequestQueue.SendAsync(config, api.Endpoint, payload, directory);
            var second = AiRequestQueue.SendAsync(config, api.Endpoint, payload, directory);
            var succeeded = 0;
            var blocked = 0;
            foreach (var request in new[] { first, second })
            {
                try { using var response = await request.WaitAsync(TimeSpan.FromSeconds(8)); succeeded++; }
                catch (InvalidOperationException ex) when (ex.Message.Contains("预算", StringComparison.Ordinal)) { blocked++; }
            }
            var ledger = AiRequestQueue.LoadBudgetLedger(directory);
            findings["aiParallelReservationsRespectSharedDailyBudget"] = succeeded == 1 && blocked == 1 &&
                api.AcceptedCount == 1 && ledger.AttemptCount == 1 && ledger.ReportedUsageCount == 1;
        }
        findings["aiOldDayResponseCannotOverwriteNewDayLedger"] = await
            AiRequestQueue.VerifyOldDayReconcileForDiagnosticsAsync(Path.Combine(root, "midnight"));
        return findings;
    }

    internal sealed class CaptureApi : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly Task server;
        readonly int expected;
        readonly int responseDelayMs;
        int accepted;
        bool didNotReceiveCloudKey = true;

        internal CaptureApi(int expected, int responseDelayMs = 0)
        {
            this.expected = expected;
            this.responseDelayMs = responseDelayMs;
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/chat/completions";
            server = ServeAsync();
        }

        internal string Endpoint { get; }
        internal int AcceptedCount => Volatile.Read(ref accepted);
        internal bool DidNotReceiveCloudKey => didNotReceiveCloudKey;

        async Task ServeAsync()
        {
            var clients = new List<TcpClient>();
            try
            {
                for (var i = 0; i < expected; i++)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    clients.Add(client);
                    await ReadRequestAsync(client);
                    Interlocked.Increment(ref accepted);
                }
                // Keep the first reservation open long enough for the second
                // contender to reach the shared budget check.
                if (responseDelayMs > 0 || expected == 1)
                    await Task.Delay(responseDelayMs > 0 ? responseDelayMs : 1200, stop.Token);
                var body = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":2}}";
                var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                foreach (var client in clients) await client.GetStream().WriteAsync(response, stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            { /* Cancellation or a failed diagnostic closes the listener. */ }
            finally { foreach (var client in clients) client.Dispose(); }
        }

        async Task ReadRequestAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 16384)
            {
                if (await stream.ReadAsync(one, stop.Token) == 0) break;
                header.Add(one[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 &&
                    header[^2] == 13 && header[^1] == 10) break;
            }
            var headers = Encoding.ASCII.GetString(header.ToArray());
            if (headers.Contains("Authorization: Bearer", StringComparison.OrdinalIgnoreCase))
                didNotReceiveCloudKey = false;
            var lengthLine = headers.Split("\r\n").FirstOrDefault(line =>
                line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var remaining = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim(), CultureInfo.InvariantCulture);
            while (remaining > 0)
            {
                var count = await stream.ReadAsync(new byte[Math.Min(remaining, 4096)], stop.Token);
                if (count == 0) break;
                remaining -= count;
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { /* The failing check is reported by the caller. */ }
            stop.Dispose();
        }
    }
}
