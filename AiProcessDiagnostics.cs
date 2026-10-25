using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class AiProcessDiagnostics
{
    static object ProbePayload => new
    {
        model = "offline",
        messages = new[] { new { role = "user", content = "hello" } },
        max_tokens = 40
    };

    internal static async Task<int> RunChildAsync(string[] args)
    {
        try
        {
            if (args.Length >= 4 && args[1] == "hold")
            {
                var kind = args[2];
                await AiRequestQueue.HoldMutexForDiagnosticsAsync(kind == "budget", kind == "local", args[3]);
                return 0;
            }
            if (args.Length >= 4 && args[1] == "try")
            {
                var kind = args[2];
                return await AiRequestQueue.TryMutexForDiagnosticsAsync(kind == "budget", kind == "local", args[3])
                    ? 0 : 2;
            }
            if (args.Length < 8 || args[1] != "request") return 3;
            var dataDir = args[2];
            var endpoint = args[3];
            var budget = double.Parse(args[4], CultureInfo.InvariantCulture);
            var concurrency = int.Parse(args[5], CultureInfo.InvariantCulture);
            var ready = args[6];
            var start = args[7];
            File.WriteAllText(ready, "ready");
            for (var i = 0; !File.Exists(start) && i < 500; i++) await Task.Delay(20);
            if (!File.Exists(start)) return 3;
            var config = new LibraryConfig
            {
                AiKey = "cloud-key-must-not-reach-loopback", AiDailyBudgetUsd = budget,
                AiInputUsdPerMillion = 1, AiOutputUsdPerMillion = 4,
                AiMaxOutputTokens = 40, AiLocalConcurrency = concurrency,
                AiLocalRequestIntervalMs = 500, AiLocalRetryCount = 0
            };
            try
            {
                using var response = await AiRequestQueue.SendAsync(config, endpoint, ProbePayload, dataDir);
                return response.RootElement.TryGetProperty("choices", out _) ? 0 : 3;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("预算", StringComparison.Ordinal))
            { return 2; }
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(args.LastOrDefault() + ".error", ex.ToString()); } catch { }
            return 3;
        }
    }

    internal static async Task<int> RunAsync(string resultPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "YinggeAiProcessProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var findings = new Dictionary<string, bool>();
        var running = new List<Process>();
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new FileNotFoundException("无法找到当前测试程序。", executable);

            Process Start(params string[] childArgs)
            {
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                start.ArgumentList.Add("--self-test-ai-child");
                foreach (var arg in childArgs) start.ArgumentList.Add(arg);
                var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离测试子进程。");
                running.Add(child);
                return child;
            }

            async Task ReadyAsync(Process child, string marker)
            {
                for (var i = 0; i < 500 && !File.Exists(marker); i++)
                {
                    if (child.HasExited) throw new InvalidOperationException($"测试子进程过早结束：{child.ExitCode}");
                    await Task.Delay(20);
                }
                if (!File.Exists(marker)) throw new TimeoutException("等待隔离测试子进程就绪超时。");
            }

            static async Task<int> ExitAsync(Process child)
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                return child.ExitCode;
            }

            async Task<(int Left, int Right)> RunPairAsync(string prefix, string endpoint, double budget, int concurrency)
            {
                var dataDir = Path.Combine(root, prefix);
                var go = Path.Combine(root, prefix + ".go");
                var leftReady = Path.Combine(root, prefix + ".left.ready");
                var rightReady = Path.Combine(root, prefix + ".right.ready");
                var left = Start("request", dataDir, endpoint, budget.ToString("R", CultureInfo.InvariantCulture),
                    concurrency.ToString(CultureInfo.InvariantCulture), leftReady, go);
                var right = Start("request", dataDir, endpoint, budget.ToString("R", CultureInfo.InvariantCulture),
                    concurrency.ToString(CultureInfo.InvariantCulture), rightReady, go);
                await Task.WhenAll(ReadyAsync(left, leftReady), ReadyAsync(right, rightReady));
                File.WriteAllText(go, "go");
                var results = await Task.WhenAll(ExitAsync(left), ExitAsync(right));
                return (results[0], results[1]);
            }

            await using (var api = new AiQueueConcurrencyDiagnostics.CaptureApi(2))
            {
                var result = await RunPairAsync("cross-process-parallel", api.Endpoint, 1, 2);
                var ledger = AiRequestQueue.LoadBudgetLedger(Path.Combine(root, "cross-process-parallel"));
                findings["aiCrossProcessLocalSlotsPermitTwoRequests"] = result.Left == 0 && result.Right == 0 &&
                    api.AcceptedCount == 2 && ledger.AttemptCount == 2 && api.DidNotReceiveCloudKey;
            }

            var config = new LibraryConfig { AiInputUsdPerMillion = 1, AiOutputUsdPerMillion = 4,
                AiMaxOutputTokens = 40 };
            var oneCost = AiRequestQueue.EstimateReservedUsd(config, JsonSerializer.Serialize(ProbePayload));
            await using (var api = new AiQueueConcurrencyDiagnostics.CaptureApi(1, responseDelayMs: 3000))
            {
                var result = await RunPairAsync("cross-process-budget", api.Endpoint, oneCost * 1.5, 2);
                var ledger = AiRequestQueue.LoadBudgetLedger(Path.Combine(root, "cross-process-budget"));
                findings["aiCrossProcessBudgetAllowsOnlyOneReservation"] =
                    new[] { result.Left, result.Right }.Order().SequenceEqual([0, 2]) &&
                    api.AcceptedCount == 1 && ledger.AttemptCount == 1;
            }

            foreach (var kind in new[] { "local", "cloud", "budget" })
            {
                var held = Path.Combine(root, kind + ".held");
                var denied = Path.Combine(root, kind + ".denied");
                var recovered = Path.Combine(root, kind + ".recovered");
                var holder = Start("hold", kind, held);
                await ReadyAsync(holder, held);
                var contender = Start("try", kind, denied);
                var blocked = await ExitAsync(contender) == 2 && !File.Exists(denied);
                holder.Kill(entireProcessTree: true);
                await ExitAsync(holder);
                var survivor = Start("try", kind, recovered);
                findings[$"aiCrossProcess{kind}MutexRecoversAfterCrash"] = blocked &&
                    await ExitAsync(survivor) == 0 && File.Exists(recovered);
            }
            return await WriteResultAsync(resultPath, findings);
        }
        catch (Exception ex)
        {
            findings["probeCompleted"] = false;
            return await WriteResultAsync(resultPath, findings, ex.ToString());
        }
        finally
        {
            foreach (var child in running)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch { }
                child.Dispose();
            }
            // Keep a failing run's isolated evidence; successful runs are
            // removed only after the parent has finished every child process.
            if (findings.Count > 0 && findings.Values.All(value => value) &&
                Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("YinggeAiProcessProbe-", StringComparison.Ordinal))
                try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    static async Task<int> WriteResultAsync(string path, Dictionary<string, bool> findings, string? error = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var passed = error is null && findings.Count > 0 && findings.Values.All(value => value);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { passed, findings, error },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
