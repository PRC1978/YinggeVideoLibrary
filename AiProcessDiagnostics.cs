using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class AiProcessDiagnostics
{
    // Upper bound for the crash-recovery probe. The observed recovery time after
    // the holder is killed is single-digit milliseconds (the abandoned mutex is
    // reported by the very first wait), so ten seconds is a generous ceiling
    // that still fails a genuinely stuck mutex instead of hanging forever.
    const int DefaultProbeTimeoutMs = 10_000;
    // The contender only has to prove that a held mutex cannot be taken; two
    // seconds is the historical (and still strict) window. The survivor gets
    // the generous ceiling because it must observe the *recovery*, which is a
    // different question.
    const int ContenderProbeTimeoutMs = 2_000;
    // Child startup includes image loading, which is delayed by antivirus and by
    // any concurrently running copy of this application. Slow startup is not a
    // correctness signal, so the readiness ceiling is deliberately generous.
    const int ReadinessTimeoutMs = 60_000;
    // A free mutex is acquired by the pre-check instantly; a longer window here
    // would only hide the fact that somebody else already holds the slot.
    const int PrecheckTimeoutMs = 1_000;
    const string SabotageArgument = "--ai-probe-hold-mutex-after-crash";
    const string NamespaceArgument = "--ai-probe-namespace";

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
                var marker = args[3];
                var timeoutMs = args.Length >= 5 &&
                    int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested)
                        ? requested : DefaultProbeTimeoutMs;
                // Startup cost is recorded separately from the wait cost so a
                // cold start can never be mistaken for a mutex that stayed busy.
                var startupMs = ProcessStartupMs();
                var threadPoolThreads = ThreadPool.ThreadCount;
                // A dedicated thread keeps the measured wait immune to
                // thread-pool warm-up on the first run after a cold start.
                var outcome = await Task.Factory.StartNew(
                    () => AiRequestQueue.ProbeMutexForDiagnostics(kind == "budget", kind == "local", marker, timeoutMs),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                WriteProbeDetail(marker, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = kind,
                    ["acquired"] = outcome.Acquired,
                    ["mutexNamespace"] = AiRequestQueue.MutexNamespace,
                    ["requestedTimeoutMs"] = timeoutMs,
                    ["attempts"] = outcome.Attempts,
                    ["waitElapsedMs"] = outcome.ElapsedMs,
                    ["recoveredFromAbandonedMutex"] = outcome.RecoveredFromAbandonedMutex,
                    ["childStartupMs"] = startupMs,
                    ["threadPoolThreadCount"] = threadPoolThreads,
                    ["lastWaitError"] = outcome.LastError,
                    ["markerExists"] = File.Exists(marker)
                });
                return outcome.Acquired ? 0 : 2;
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

    internal static long ProcessStartupMs()
    {
        try { return (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds; }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception) { return -1; }
    }

    static string? ReadMarker(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static string? ReadString(Dictionary<string, object?>? detail, string name)
    {
        if (detail is null || !detail.TryGetValue(name, out var value) || value is null) return null;
        return value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            string text => text,
            _ => value.ToString()
        };
    }

    static int ReadInt(Dictionary<string, object?>? detail, string name)    {
        if (detail is null || !detail.TryGetValue(name, out var value) || value is null) return -1;
        try
        {
            return value switch
            {
                JsonElement element when element.ValueKind == JsonValueKind.Number => element.GetInt32(),
                int number => number,
                long number => (int)number,
                string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var parsed) => parsed,
                _ => -1
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return -1; }
    }

    /// <summary>
    /// Other copies of this executable that are already running. The AI slots are
    /// session-wide named mutexes, so a concurrent instance is the main
    /// alternative explanation for a slow child or a busy slot.
    /// </summary>
    static object RunningInstanceSnapshot(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        try
        {
            var others = Process.GetProcessesByName(name)
                .Where(process => process.Id != Environment.ProcessId)
                .Select(process =>
                {
                    try { return $"{process.Id}:{process.MainModule?.FileName}"; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                        or NotSupportedException) { return process.Id + ":<无法读取路径>"; }
                    finally { process.Dispose(); }
                })
                .ToArray();
            return new { name, otherInstanceCount = others.Length, otherInstances = others.Take(4).ToArray() };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new { name, otherInstanceCount = -1, error = ex.Message };
        }
    }

    static void WriteProbeDetail(string marker, Dictionary<string, object?> detail)    {
        try
        {
            File.WriteAllText(marker + ".detail.json", JsonSerializer.Serialize(detail,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static Dictionary<string, object?>? ReadProbeDetail(string marker)
    {
        try
        {
            var path = marker + ".detail.json";
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { return null; }
    }

    static bool HasArgument(string name) =>
        Environment.GetCommandLineArgs().Skip(1).Any(argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Namespace handed to every child: an explicit <c>--ai-probe-namespace</c>
    /// wins, <c>none</c> selects the production names (used to show that the
    /// default naming really is the shared one), otherwise each run gets its own
    /// identifier. The probe therefore owns its slots outright, so a busy slot
    /// can only belong to the process this test started and a concurrent copy of
    /// this application cannot make the outcome depend on timing.
    /// </summary>
    static string ResolveProbeNamespace()
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (!args[index].Equals(NamespaceArgument, StringComparison.OrdinalIgnoreCase)) continue;
            var value = args[index + 1].Trim();
            if (value.Equals("none", StringComparison.OrdinalIgnoreCase)
                || value.Equals("default", StringComparison.OrdinalIgnoreCase)) return "";
            if (value.Length > 0) return value;
        }
        return "probe-" + Guid.NewGuid().ToString("N")[..12];
    }

    internal static async Task<int> RunAsync(string resultPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "YinggeAiProcessProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var findings = new Dictionary<string, bool>(StringComparer.Ordinal);
        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal);
        var running = new List<Process>();
        var sabotage = HasArgument(SabotageArgument);
        var probeNamespace = ResolveProbeNamespace();
        evidence["probeRoot"] = root;
        evidence["sabotagedMutexHolder"] = sabotage;
        evidence["mutexNamespace"] = probeNamespace;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new FileNotFoundException("无法找到当前测试程序。", executable);
            evidence["environment"] = RunningInstanceSnapshot(executable);

            Process Start(params string[] childArgs)
            {
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                start.ArgumentList.Add("--self-test-ai-child");
                foreach (var arg in childArgs) start.ArgumentList.Add(arg);
                // Own mutex namespace: the probe must not share slots with
                // another copy of this application running in the same session.
                start.ArgumentList.Add(NamespaceArgument);
                start.ArgumentList.Add(probeNamespace);
                // Children inherit the same redirected data directory so a probe
                // can never fall back to the real library location.
                start.ArgumentList.Add(AppPaths.DataDirectoryArgument);
                start.ArgumentList.Add(AppPaths.DataDirectory);
                var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离测试子进程。");
                running.Add(child);
                return child;
            }

            async Task<long> ReadyAsync(Process child, string marker)
            {
                // A child that writes its marker late is slow, not broken: image
                // loading competes with antivirus and any other running instance.
                // A ten second ceiling produced false failures under load, so the
                // ceiling is generous and the measured value is reported.
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < ReadinessTimeoutMs)
                {
                    if (File.Exists(marker)) return clock.ElapsedMilliseconds;
                    if (child.HasExited) throw new InvalidOperationException($"测试子进程过早结束：{child.ExitCode}");
                    await Task.Delay(20);
                }
                throw new TimeoutException(
                    $"等待隔离测试子进程就绪超时（{ReadinessTimeoutMs} 毫秒，进程{(child.HasExited ? "已退出" : "仍在运行")}）。");
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

            var recoveryEvidence = new Dictionary<string, object?>(StringComparer.Ordinal);
            evidence["mutexRecovery"] = recoveryEvidence;
            foreach (var kind in new[] { "local", "cloud", "budget" })
            {
                var detail = new Dictionary<string, object?>(StringComparer.Ordinal);
                recoveryEvidence[kind] = detail;
                var held = Path.Combine(root, kind + ".held");
                var denied = Path.Combine(root, kind + ".denied");
                var recovered = Path.Combine(root, kind + ".recovered");
                // The slots are named mutexes; the pre-check runs in a child so it
                // uses exactly the namespace the real probe uses. A free slot is a
                // precondition for both "blocked" and "recovered" being about the
                // process this test started.
                var precheckMarker = Path.Combine(root, kind + ".precheck");
                var precheckExit = await ExitAsync(Start("try", kind, precheckMarker,
                    PrecheckTimeoutMs.ToString(CultureInfo.InvariantCulture)));
                var precheck = ReadProbeDetail(precheckMarker);
                detail["mutexFreeBeforeTest"] = precheckExit == 0 && File.Exists(precheckMarker);
                detail["precheckProbe"] = precheck;
                if (detail["mutexFreeBeforeTest"] is false)
                    detail["foreignMutexHolder"] =
                        $"测试开始前互斥就被外部占用（命名空间 {probeNamespace}）：{ReadString(precheck, "lastWaitError")}";
                var holder = Start("hold", kind, held);
                detail["holderReadyMs"] = await ReadyAsync(holder, held);
                detail["holderAcquiredMarkerExists"] = File.Exists(held);
                var contenderClock = Stopwatch.StartNew();
                var contender = Start("try", kind, denied,
                    ContenderProbeTimeoutMs.ToString(CultureInfo.InvariantCulture));
                var contenderExit = await ExitAsync(contender);
                var contenderProbe = ReadProbeDetail(denied);
                var contenderAttempts = ReadInt(contenderProbe, "attempts");
                detail["contenderExitCode"] = contenderExit;
                detail["contenderWallClockMs"] = contenderClock.ElapsedMilliseconds;
                detail["contenderProbe"] = contenderProbe;
                detail["deniedMarkerExists"] = File.Exists(denied);
                // A denial only proves contention if the probe really polled: an
                // exception on the first attempt would also return exit code 2.
                var blocked = contenderExit == 2 && !File.Exists(denied) && contenderAttempts >= 2;
                detail["blocked"] = blocked;
                detail["blockedReason"] = blocked
                    ? $"持有者存活期间竞争者退出码 2、轮询 {contenderAttempts} 次全部失败且未写出 {Path.GetFileName(denied)}，说明互斥确实被占用"
                    : contenderExit != 2
                        ? $"持有者存活期间竞争者退出码 {contenderExit}（应为 2），互斥可能根本没有被占用"
                        : contenderAttempts < 2
                            ? $"竞争者退出码 2 但只轮询了 {contenderAttempts} 次，拒绝原因不是互斥被占用"
                            : $"持有者存活期间竞争者仍取得了互斥并写出 {Path.GetFileName(denied)}";

                var killClock = Stopwatch.StartNew();
                holder.Kill(entireProcessTree: true);
                var holderExit = await ExitAsync(holder);
                detail["holderKilledExitCode"] = holderExit;
                detail["holderExitWaitMs"] = killClock.ElapsedMilliseconds;
                detail["holderStillRunningAfterKill"] = SafeHasExited(holder) == false;

                if (sabotage)
                {
                    // Injected fault: a fresh holder re-acquires the mutex after
                    // the crash so the survivor is genuinely blocked. The
                    // self-test must turn red and explain why.
                    var squatterMarker = Path.Combine(root, kind + ".squatter.held");
                    var squatter = Start("hold", kind, squatterMarker);
                    detail["sabotageHolderReadyMs"] = await ReadyAsync(squatter, squatterMarker);
                    detail["sabotageHolderAcquired"] = File.Exists(squatterMarker);
                }

                var survivorClock = Stopwatch.StartNew();
                var survivor = Start("try", kind, recovered,
                    (sabotage ? ContenderProbeTimeoutMs : DefaultProbeTimeoutMs)
                        .ToString(CultureInfo.InvariantCulture));
                var survivorExit = await ExitAsync(survivor);
                var survivorProbe = ReadProbeDetail(recovered);
                detail["survivorExitCode"] = survivorExit;
                detail["survivorElapsedMs"] = survivorClock.ElapsedMilliseconds;
                detail["survivorProbe"] = survivorProbe;
                detail["recoveredMarkerExists"] = File.Exists(recovered);
                detail["recoveredMarkerContent"] = ReadMarker(recovered);
                detail["survivorLastWaitError"] = survivorProbe is null
                    ? "竞争者没有留下探针明细，无法判定等待失败原因"
                    : survivorProbe.TryGetValue("lastWaitError", out var lastError) ? lastError : null;
                var recoveredAfterCrash = blocked && survivorExit == 0 && File.Exists(recovered);
                detail["passed"] = recoveredAfterCrash;
                var failureReason = recoveredAfterCrash ? null
                    : !blocked ? "占用阶段判定失败，崩溃恢复结果不可采信"
                    : survivorExit != 0
                        ? $"持有者被杀后竞争者仍无法取得互斥（退出码 {survivorExit}）：{detail["survivorLastWaitError"]}"
                        : "竞争者取得互斥但未写出恢复标记";
                if (!recoveredAfterCrash && detail.TryGetValue("foreignMutexHolder", out var foreign))
                    failureReason += "；" + foreign;
                detail["failureReason"] = failureReason;
                findings[$"aiCrossProcess{kind}MutexRecoversAfterCrash"] = recoveredAfterCrash;
            }
            return await WriteResultAsync(resultPath, findings, evidence);
        }
        catch (Exception ex)
        {
            findings["probeCompleted"] = false;
            return await WriteResultAsync(resultPath, findings, evidence, ex.ToString());
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
            var cleanupFailed = false;
            if (findings.Count > 0 && findings.Values.All(value => value) &&
                Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("YinggeAiProcessProbe-", StringComparison.Ordinal))
                cleanupFailed = !DeleteProbeDirectory(root);
            else
                evidence["probeRootKept"] = "失败运行保留隔离目录，便于事后取证";
            if (cleanupFailed) evidence["tempDirectoryCleanupFailed"] = root;
        }
    }

    static bool? SafeHasExited(Process process)
    {
        try { return process.HasExited; } catch (InvalidOperationException) { return null; }
    }

    internal static bool DeleteProbeDirectory(string path) => TempDiagnosticDirectory.TryDelete(path);

    static async Task<int> WriteResultAsync(string path, Dictionary<string, bool> findings,
        Dictionary<string, object?> evidence, string? error = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var passed = error is null && findings.Count > 0 && findings.Values.All(value => value);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { passed, findings, evidence, error },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
