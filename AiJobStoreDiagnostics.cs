using System.IO;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class AiJobStoreDiagnostics
{
    internal static async Task<IReadOnlyDictionary<string, bool>> VerifyAsync(string root)
    {
        var findings = new Dictionary<string, bool>(StringComparer.Ordinal);
        var dataDir = Path.Combine(root, "job-lifecycle");
        Directory.CreateDirectory(dataDir);
        const string endpoint = "http://127.0.0.1:1/chat/completions";
        var payload = new { model = "local", prompt = "private-prompt-sentinel", max_tokens = 12 };
        var requestJson = JsonSerializer.Serialize(payload);
        var jobId = AiRequestQueue.Enqueue(dataDir, "classification", endpoint, payload);
        var path = AiJobStore.PathFor(dataDir, jobId);
        var created = AiJobStore.Get(dataDir, jobId);
        findings["aiJobStableIdAndPrivatePrompt"] =
            Guid.TryParseExact(jobId, "N", out _) && created.Status == AiJobStore.Created &&
            created.RequestSha256.Length == 64 &&
            !File.ReadAllText(path).Contains("private-prompt-sentinel", StringComparison.Ordinal);
        findings["aiJobDpapiRequestRoundTrip"] =
            AiJobStore.ReadProtectedRequest(created) == requestJson &&
            !string.IsNullOrWhiteSpace(created.ProtectedRequest);
        var corruptedCiphertext = AiJobStore.Get(dataDir, jobId);
        corruptedCiphertext.ProtectedRequest = "not-base64";
        File.WriteAllText(path, JsonSerializer.Serialize(corruptedCiphertext));
        try
        {
            AiJobStore.ReadProtectedRequest(corruptedCiphertext);
            findings["aiJobCorruptCiphertextCannotRetry"] = false;
        }
        catch (InvalidDataException) { findings["aiJobCorruptCiphertextCannotRetry"] = true; }
        corruptedCiphertext.ProtectedRequest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, JsonSerializer.Serialize(corruptedCiphertext));
        try
        {
            AiJobStore.ReadProtectedRequest(corruptedCiphertext);
            findings["aiJobForeignUserPayloadCannotRetry"] = false;
        }
        catch (Win32Exception)
        {
            findings["aiJobForeignUserPayloadCannotRetry"] =
                !File.Exists(Path.Combine(dataDir, "ai-budget.json"));
        }
        File.WriteAllText(path, JsonSerializer.Serialize(created));
        try
        {
            AiRequestQueue.Enqueue(dataDir, "review", endpoint,
                new { model = "local", api_key = "should-never-persist" });
            findings["aiJobRejectsCredentialFields"] = false;
        }
        catch (InvalidOperationException)
        {
            findings["aiJobRejectsCredentialFields"] =
                !Directory.EnumerateFiles(Path.Combine(dataDir, "ai-jobs"), "*.json")
                    .Select(File.ReadAllText).Any(text => text.Contains("should-never-persist", StringComparison.Ordinal));
        }

        var firstAttempt = AiJobStore.Begin(dataDir, jobId, endpoint, requestJson);
        findings["aiJobInFlightPersisted"] =
            firstAttempt.Status == AiJobStore.InFlight && firstAttempt.AttemptCount == 1 &&
            AiJobStore.Get(dataDir, jobId).AttemptId == firstAttempt.AttemptId;
        findings["aiJobActiveProcessNotRecovered"] = AiRequestQueue.RecoverInterrupted(dataDir) == 0;

        // A terminated process leaves the in-flight record intact. The diagnostic
        // substitutes an impossible owner PID to model that process ending.
        firstAttempt.OwnerProcessId = int.MaxValue;
        firstAttempt.OwnerProcessStartedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        File.WriteAllText(path, JsonSerializer.Serialize(firstAttempt));
        findings["aiJobInterruptedRecoveredWithoutSending"] =
            AiRequestQueue.RecoverInterrupted(dataDir) == 1 &&
            AiJobStore.Get(dataDir, jobId).Status == AiJobStore.Interrupted &&
            !File.Exists(Path.Combine(dataDir, "ai-budget.json"));

        try
        {
            using var unexpected = await AiRequestQueue.SendJobAsync(
                new LibraryConfig(), endpoint, payload, dataDir, jobId);
            findings["aiJobRetryNeedsExplicitPreparation"] = false;
        }
        catch (InvalidOperationException)
        {
            findings["aiJobRetryNeedsExplicitPreparation"] =
                AiJobStore.Get(dataDir, jobId).Status == AiJobStore.Interrupted &&
                !File.Exists(Path.Combine(dataDir, "ai-budget.json"));
        }

        AiRequestQueue.PrepareRetry(dataDir, jobId, endpoint, payload);
        findings["aiJobPendingCancellation"] =
            AiRequestQueue.TryCancelPending(dataDir, jobId) &&
            AiJobStore.Get(dataDir, jobId).Status == AiJobStore.Canceled &&
            !AiRequestQueue.TryCancelPending(dataDir, jobId);

        AiRequestQueue.PrepareRetry(dataDir, jobId, endpoint, payload);
        var canceledAttempt = AiJobStore.Begin(dataDir, jobId, endpoint, requestJson);
        findings["aiJobRunningCannotBeCanceledAsPending"] =
            !AiRequestQueue.TryCancelPending(dataDir, jobId);
        AiJobStore.Finish(dataDir, jobId, canceledAttempt.AttemptId,
            AiJobStore.Canceled, "request-canceled");
        findings["aiJobRunningCancellationRecorded"] =
            AiJobStore.Get(dataDir, jobId).Status == AiJobStore.Canceled;

        AiRequestQueue.PrepareRetry(dataDir, jobId, endpoint, payload);
        var completedAttempt = AiJobStore.Begin(dataDir, jobId, endpoint, requestJson);
        const string response = "{\"choices\":[{\"message\":{\"content\":\"suggestion only\"}}]}";
        AiJobStore.Finish(dataDir, jobId, completedAttempt.AttemptId,
            AiJobStore.Completed, resultJson: response);
        using var savedResponse = await AiRequestQueue.SendJobAsync(
            new LibraryConfig(), endpoint, payload, dataDir, jobId);
        var completed = AiJobStore.Get(dataDir, jobId);
        findings["aiJobResultDurableAndReturnedWithoutNetwork"] =
            completed.Status == AiJobStore.Completed && completed.ResultJson == response &&
            savedResponse.RootElement.GetProperty("choices")[0].GetProperty("message")
                .GetProperty("content").GetString() == "suggestion only" &&
            !File.Exists(Path.Combine(dataDir, "ai-budget.json"));
        try
        {
            AiJobStore.Finish(dataDir, jobId, firstAttempt.AttemptId,
                AiJobStore.Completed, resultJson: "{\"stale\":true}");
            findings["aiJobStaleAttemptCannotReplaceResult"] = false;
        }
        catch (InvalidOperationException)
        {
            findings["aiJobStaleAttemptCannotReplaceResult"] =
                AiJobStore.Get(dataDir, jobId).ResultJson == response;
        }
        try
        {
            AiRequestQueue.PrepareRetry(dataDir, jobId, endpoint,
                new { model = "local", prompt = "different", max_tokens = 12 });
            findings["aiJobRequestFingerprintEnforced"] = false;
        }
        catch (InvalidOperationException) { findings["aiJobRequestFingerprintEnforced"] = true; }

        var budgetDir = Path.Combine(root, "job-budget-retry");
        Directory.CreateDirectory(budgetDir);
        var budgetJob = AiRequestQueue.Enqueue(budgetDir, "review", endpoint, payload);
        var config = new LibraryConfig
        {
            AiInputUsdPerMillion = 1,
            AiOutputUsdPerMillion = 4,
            AiDailyBudgetUsd = 0.000001,
            AiMaxOutputTokens = 1024
        };
        try
        {
            using var unexpected = await AiRequestQueue.SendJobAsync(
                config, endpoint, payload, budgetDir, budgetJob);
            findings["aiJobBudgetCheckedBeforeNetwork"] = false;
        }
        catch (InvalidOperationException ex)
        {
            findings["aiJobBudgetCheckedBeforeNetwork"] =
                ex.Message.Contains("预算", StringComparison.Ordinal) &&
                AiJobStore.Get(budgetDir, budgetJob).Status == AiJobStore.Failed &&
                !File.Exists(Path.Combine(budgetDir, "ai-budget.json"));
        }
        AiRequestQueue.PrepareRetry(budgetDir, budgetJob, endpoint, payload);
        findings["aiJobBudgetFailureRequiresExplicitRetry"] =
            AiJobStore.Get(budgetDir, budgetJob).Status == AiJobStore.ReadyToRetry;

        var cancelDir = Path.Combine(root, "job-cancel-in-flight");
        Directory.CreateDirectory(cancelDir);
        File.WriteAllText(Path.Combine(cancelDir, "ai-budget.json"),
            JsonSerializer.Serialize(new AiRequestQueue.BudgetLedger
            {
                Day = DateTime.Now.ToString("yyyy-MM-dd"),
                NextAllowedUtc = DateTimeOffset.UtcNow.AddSeconds(30)
            }));
        var runningJob = AiRequestQueue.Enqueue(cancelDir, "tags", endpoint, payload);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            try
            {
                using var unexpected = await AiRequestQueue.SendJobAsync(
                    config, endpoint, payload, cancelDir, runningJob, cancellation.Token);
                findings["aiJobInFlightCancellationTokenRecorded"] = false;
            }
            catch (OperationCanceledException)
            {
                findings["aiJobInFlightCancellationTokenRecorded"] =
                    AiJobStore.Get(cancelDir, runningJob).Status == AiJobStore.Canceled &&
                    AiRequestQueue.LoadBudgetLedger(cancelDir).AttemptCount == 0;
            }
        }

        var uiCancelDir = Path.Combine(root, "job-ui-cancel");
        Directory.CreateDirectory(uiCancelDir);
        File.WriteAllText(Path.Combine(uiCancelDir, "ai-budget.json"),
            JsonSerializer.Serialize(new AiRequestQueue.BudgetLedger
            {
                Day = DateTime.Now.ToString("yyyy-MM-dd"),
                NextAllowedUtc = DateTimeOffset.UtcNow.AddSeconds(30)
            }));
        var uiCancelJob = AiRequestQueue.Enqueue(uiCancelDir, "tags", endpoint, payload);
        var pendingSend = AiRequestQueue.SendJobAsync(config, endpoint, payload,
            uiCancelDir, uiCancelJob);
        var cancelActive = false;
        for (var wait = 0; wait < 100 && !cancelActive; wait++)
        {
            cancelActive = AiRequestQueue.TryCancelActive(uiCancelJob);
            if (!cancelActive) await Task.Delay(20);
        }
        try { using var unexpected = await pendingSend; }
        catch (OperationCanceledException) { }
        findings["aiJobUiCancelStopsActiveRequest"] = cancelActive &&
            AiJobStore.Get(uiCancelDir, uiCancelJob).Status == AiJobStore.Canceled &&
            AiRequestQueue.LoadBudgetLedger(uiCancelDir).AttemptCount == 0;

        File.WriteAllText(path, "{}");
        try { AiJobStore.Get(dataDir, jobId); findings["aiJobCorruptionFailsClosed"] = false; }
        catch (InvalidDataException) { findings["aiJobCorruptionFailsClosed"] = true; }
        return findings;
    }
}
