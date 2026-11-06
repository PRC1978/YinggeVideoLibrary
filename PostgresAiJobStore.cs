using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace YinggeVideoLibrary;

// Each job is one CAS-protected database document. The DPAPI ciphertext is
// created by AiJobStore before insertion; neither prompts nor keys are stored
// in plaintext. A stale attempt can never overwrite a newer job state.
internal static class PostgresAiJobStore
{
    private static string Name(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("AI 作业编号无效。", nameof(id));
        return "ai-jobs/" + id + ".json";
    }

    private static T Run<T>(Func<Task<T>> operation) =>
        Task.Run(operation).GetAwaiter().GetResult();

    private static (AiJobStore.Job Job, string Revision) Parse(
        string id, PostgresLibraryDocumentStore.Snapshot document)
    {
        if (document.Content.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("AI 作业记录过大，已暂停该作业。");
        var job = JsonSerializer.Deserialize<AiJobStore.Job>(document.Content)
            ?? throw new InvalidDataException("AI 作业记录为空。");
        AiJobStore.Validate(job, id);
        return (job, document.Revision);
    }

    private static async Task<(AiJobStore.Job Job, string Revision)> ReadAsync(
        string connectionString, string id)
    {
        var document = await PostgresLibraryDocumentStore.ReadAsync(connectionString, Name(id))
            ?? throw new InvalidDataException("AI 作业记录不存在。");
        return Parse(id, document);
    }

    private static async Task SaveAsync(string connectionString, AiJobStore.Job job,
        string? expectedRevision)
    {
        await PostgresLibraryDocumentStore.SaveAsync(connectionString, Name(job.Id),
            JsonSerializer.SerializeToUtf8Bytes(job), expectedRevision);
    }

    internal static AiJobStore.Job Create(string connectionString, AiJobStore.Job job) =>
        Run(async () =>
        {
            AiJobStore.Validate(job, job.Id);
            await SaveAsync(connectionString, job, null);
            return job;
        });

    internal static AiJobStore.Job Get(string connectionString, string id) =>
        Run(async () => (await ReadAsync(connectionString, id)).Job);

    internal static IReadOnlyList<AiJobStore.Job> List(string connectionString) =>
        Run(async () =>
        {
            var jobs = new List<AiJobStore.Job>();
            await using var source = NpgsqlDataSource.Create(connectionString);
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT name,content,sha256 FROM library_document WHERE name LIKE 'ai-jobs/%'",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                if (!name.EndsWith(".json", StringComparison.Ordinal) ||
                    name.Length != "ai-jobs/".Length + 32 + ".json".Length)
                    throw new InvalidDataException("数据库 AI 作业名称无效。");
                var id = name["ai-jobs/".Length..^".json".Length];
                var document = new PostgresLibraryDocumentStore.Snapshot(
                    (byte[])reader[1], reader.GetString(2));
                var checksum = Convert.ToHexString(SHA256.HashData(document.Content));
                if (checksum != document.Revision)
                    throw new InvalidDataException("数据库 AI 作业校验失败。");
                jobs.Add(Parse(id, document).Job);
            }
            return (IReadOnlyList<AiJobStore.Job>)jobs
                .OrderByDescending(job => job.CreatedUtc).ToArray();
        });

    private static AiJobStore.Job Change(string connectionString, string id,
        Func<AiJobStore.Job, bool> mutate)
        => Run(async () =>
        {
            var (job, revision) = await ReadAsync(connectionString, id);
            if (mutate(job)) await SaveAsync(connectionString, job, revision);
            return job;
        });

    internal static int RecoverInterrupted(string connectionString)
    {
        var changed = 0;
        foreach (var item in List(connectionString))
        {
            if (item.Status != AiJobStore.InFlight || AiJobStore.OwnerIsAlive(item)) continue;
            try
            {
                var recovered = false;
                Change(connectionString, item.Id, job =>
                {
                    if (job.Status != AiJobStore.InFlight || AiJobStore.OwnerIsAlive(job)) return false;
                    job.Status = AiJobStore.Interrupted;
                    job.LastErrorCode = "process-interrupted";
                    job.OwnerProcessId = 0;
                    job.OwnerProcessStartedUtc = default;
                    job.UpdatedUtc = DateTimeOffset.UtcNow;
                    return recovered = true;
                });
                if (recovered) changed++;
            }
            catch (InvalidOperationException) { /* A newer process won the CAS. */ }
        }
        return changed;
    }

    internal static AiJobStore.Job Begin(string connectionString, string id,
        string endpoint, string requestJson) => Change(connectionString, id, job =>
    {
        if (job.RequestSha256 != AiJobStore.RequestHash(endpoint, requestJson))
            throw new InvalidOperationException("请求内容与已保存的 AI 作业不一致；请新建作业。");
        if (job.Status == AiJobStore.Completed) return false;
        if (job.Status is not (AiJobStore.Created or AiJobStore.ReadyToRetry))
            throw new InvalidOperationException("AI 作业需先检查状态并由用户明确重试。");
        if (job.AttemptCount == int.MaxValue)
            throw new InvalidDataException("AI 作业尝试次数已达到记录上限。");
        using var process = Process.GetCurrentProcess();
        job.Status = AiJobStore.InFlight;
        job.AttemptCount++;
        job.AttemptId = Guid.NewGuid().ToString("N");
        job.OwnerProcessId = process.Id;
        job.OwnerProcessStartedUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
        job.LastErrorCode = "";
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        return true;
    });

    internal static AiJobStore.Job PrepareRetry(string connectionString, string id,
        string endpoint, string requestJson) => Change(connectionString, id, job =>
    {
        if (job.RequestSha256 != AiJobStore.RequestHash(endpoint, requestJson))
            throw new InvalidOperationException("请求内容与已保存的 AI 作业不一致；请新建作业。");
        if (job.Status is not (AiJobStore.Interrupted or AiJobStore.Failed or AiJobStore.Canceled))
            throw new InvalidOperationException("当前 AI 作业不需要重试。");
        job.Status = AiJobStore.ReadyToRetry;
        job.LastErrorCode = "";
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        return true;
    });

    internal static bool TryCancelPending(string connectionString, string id)
    {
        var canceled = false;
        Change(connectionString, id, job =>
        {
            if (job.Status is not (AiJobStore.Created or AiJobStore.ReadyToRetry)) return false;
            job.Status = AiJobStore.Canceled;
            job.LastErrorCode = "user-canceled";
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            return canceled = true;
        });
        return canceled;
    }

    internal static void Finish(string connectionString, string id, string attemptId,
        string status, string errorCode, string? resultJson)
    {
        if (status is not (AiJobStore.Completed or AiJobStore.Failed or AiJobStore.Canceled))
            throw new ArgumentException("AI 作业终态无效。", nameof(status));
        Change(connectionString, id, job =>
        {
            if (job.Status != AiJobStore.InFlight || job.AttemptId != attemptId)
                throw new InvalidOperationException("AI 作业执行编号已变化，不能写入过期结果。");
            if (status == AiJobStore.Completed)
            {
                if (string.IsNullOrWhiteSpace(resultJson))
                    throw new ArgumentException("AI 响应为空。", nameof(resultJson));
                if (Encoding.UTF8.GetByteCount(resultJson) > AiJobStore.MaxSavedResponseBytes)
                    throw new InvalidDataException("AI 响应过大，不能保存为作业结果。");
                using var _ = JsonDocument.Parse(resultJson);
                job.ResultJson = resultJson;
                job.ResultSha256 = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(resultJson)));
            }
            job.Status = status;
            job.LastErrorCode = errorCode;
            job.OwnerProcessId = 0;
            job.OwnerProcessStartedUtc = default;
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            return true;
        });
    }
}
