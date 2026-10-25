using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// A job stores the request fingerprint and a DPAPI-protected request, never a
// plaintext prompt or API key. Responses are local review evidence; this store
// never changes library data.
internal static partial class AiJobStore
{
    internal const string Created = "created";
    internal const string ReadyToRetry = "ready-to-retry";
    internal const string InFlight = "in-flight";
    internal const string Interrupted = "interrupted";
    internal const string Failed = "failed";
    internal const string Canceled = "canceled";
    internal const string Completed = "completed";

    static readonly Mutex FileGate = new(false, "Local\\YinggeVideoLibrary.AiJobsV1");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    const long MaxJobFileBytes = 8 * 1024 * 1024;
    internal const long MaxSavedResponseBytes = MaxJobFileBytes / 2;
    const int MaxRequestBytes = 512 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CryptProtectData(ref DataBlob input, string description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LocalFree(IntPtr memory);

    internal sealed class Job
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string RequestSha256 { get; set; } = "";
        public string? ProtectedRequest { get; set; }
        public string Status { get; set; } = Created;
        public int AttemptCount { get; set; }
        public string AttemptId { get; set; } = "";
        public int OwnerProcessId { get; set; }
        public DateTimeOffset OwnerProcessStartedUtc { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public string LastErrorCode { get; set; } = "";
        public string? ResultJson { get; set; }
        public string? ResultSha256 { get; set; }
    }

    static T Locked<T>(Func<T> action)
    {
        try { FileGate.WaitOne(); }
        catch (AbandonedMutexException) { /* Atomic replacement leaves the last complete record. */ }
        try { return action(); }
        finally { FileGate.ReleaseMutex(); }
    }

    static string DirectoryPath(string dataDir) => Path.Combine(dataDir, "ai-jobs");

    internal static string PathFor(string dataDir, string jobId)
    {
        if (!Guid.TryParseExact(jobId, "N", out _))
            throw new ArgumentException("AI 作业编号无效。", nameof(jobId));
        return Path.Combine(DirectoryPath(dataDir), jobId + ".json");
    }

    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static byte[] TransformDpapi(byte[] bytes, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(bytes.Length);
        var input = new DataBlob { Length = bytes.Length, Data = inputPointer };
        DataBlob output = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            Marshal.Copy(bytes, 0, inputPointer, bytes.Length);
            var success = protect
                ? CryptProtectData(ref input, "映格视频库 AI 作业请求", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, out description, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(),
                protect ? "无法保护 AI 作业请求。" : "无法解密 AI 作业请求；请重新创建作业。");
            if (output.Length < 0 || output.Length > MaxRequestBytes + 4096)
                throw new InvalidDataException("AI 作业请求长度无效。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (description != IntPtr.Zero) LocalFree(description);
            if (output.Data != IntPtr.Zero)
            {
                if (!protect && output.Length > 0 && output.Length <= MaxRequestBytes + 4096)
                    Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                LocalFree(output.Data);
            }
            Marshal.Copy(new byte[bytes.Length], 0, inputPointer, bytes.Length);
            Marshal.FreeHGlobal(inputPointer);
        }
    }

    static string ProtectRequest(string requestJson)
    {
        var bytes = Encoding.UTF8.GetBytes(requestJson);
        if (bytes.Length > MaxRequestBytes)
            throw new InvalidOperationException("AI 请求过大，无法保存可恢复作业。");
        try
        {
            var protectedBytes = TransformDpapi(bytes, true);
            try { return Convert.ToBase64String(protectedBytes); }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static string ReadProtectedRequest(Job job)
    {
        if (string.IsNullOrWhiteSpace(job.ProtectedRequest))
            throw new InvalidOperationException("此作业未保存可恢复请求，请重新创建作业。");
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(job.ProtectedRequest); }
        catch (FormatException ex) { throw new InvalidDataException("AI 作业请求密文无效。", ex); }
        try
        {
            var plain = TransformDpapi(encrypted, false);
            try
            {
                var requestJson = Encoding.UTF8.GetString(plain);
                using var parsed = JsonDocument.Parse(requestJson);
                if (ContainsCredentialField(parsed.RootElement))
                    throw new InvalidDataException("AI 作业请求含凭据字段，不能重试。");
                return requestJson;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    internal static string RequestHash(string endpoint, string requestJson) =>
        Hash(endpoint + "\n" + requestJson);

    static string SafeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return "invalid-endpoint";
        // URI user info, query and fragment can contain credentials.
        return $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";
    }

    static bool ContainsCredentialField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(ContainsCredentialField);
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
        {
            var name = new string(property.Name.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant).ToArray());
            if (name is "apikey" or "accesstoken" or "refreshtoken" or "authorization" or
                "password" or "secret" or "clientsecret" or "credential" ||
                ContainsCredentialField(property.Value)) return true;
        }
        return false;
    }

    static Job Read(string dataDir, string jobId)
    {
        var path = PathFor(dataDir, jobId);
        try
        {
            if (new FileInfo(path).Length > MaxJobFileBytes)
                throw new InvalidDataException("AI 作业记录过大，已暂停该作业。");
            var job = JsonSerializer.Deserialize<Job>(File.ReadAllText(path))
                ?? throw new InvalidDataException("AI 作业记录为空。");
            Validate(job, jobId);
            return job;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("AI 作业记录无法读取，已暂停该作业。", ex);
        }
    }

    static void Validate(Job job, string jobId)
    {
        if (job.Id != jobId || job.AttemptCount < 0 ||
            job.RequestSha256?.Length != 64 ||
            !new[] { Created, ReadyToRetry, InFlight, Interrupted, Failed, Canceled, Completed }.Contains(job.Status) ||
            job.CreatedUtc == default || job.UpdatedUtc == default ||
            (job.Status == Completed && (string.IsNullOrWhiteSpace(job.ResultJson) ||
                job.ResultSha256 != Hash(job.ResultJson))))
            throw new InvalidDataException("AI 作业记录校验失败，已暂停该作业。");
    }

    static void Write(string dataDir, Job job, bool overwrite)
    {
        Directory.CreateDirectory(DirectoryPath(dataDir));
        var path = PathFor(dataDir, job.Id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(job, JsonOptions));
            File.Move(temporary, path, overwrite);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    internal static Job Create(string dataDir, string kind, string endpoint, string requestJson)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("AI 作业类型不能为空。", nameof(kind));
        if (kind.Length > 80) throw new ArgumentException("AI 作业类型过长。", nameof(kind));
        using var parsed = JsonDocument.Parse(requestJson);
        if (ContainsCredentialField(parsed.RootElement))
            throw new InvalidOperationException("AI 请求正文包含凭据字段，已拒绝写入作业记录。");
        var now = DateTimeOffset.UtcNow;
        var job = new Job
        {
            Id = Guid.NewGuid().ToString("N"), Kind = kind.Trim(),
            Endpoint = SafeEndpoint(endpoint), RequestSha256 = RequestHash(endpoint, requestJson),
            ProtectedRequest = ProtectRequest(requestJson),
            CreatedUtc = now, UpdatedUtc = now
        };
        return Locked(() => { Write(dataDir, job, false); return job; });
    }

    internal static Job Get(string dataDir, string jobId) => Locked(() => Read(dataDir, jobId));

    internal static IReadOnlyList<Job> List(string dataDir)
    {
        return Locked(() =>
        {
            var directory = DirectoryPath(dataDir);
            if (!Directory.Exists(directory)) return Array.Empty<Job>();
            return Directory.EnumerateFiles(directory, "*.json")
                .Select(path => Read(dataDir, Path.GetFileNameWithoutExtension(path)))
                .OrderByDescending(job => job.CreatedUtc).ToArray();
        });
    }

    static bool OwnerIsAlive(Job job)
    {
        if (job.OwnerProcessId <= 0 || job.OwnerProcessStartedUtc == default) return false;
        try
        {
            using var process = Process.GetProcessById(job.OwnerProcessId);
            if (process.HasExited) return false;
            return Math.Abs((process.StartTime.ToUniversalTime() -
                job.OwnerProcessStartedUtc.UtcDateTime).TotalSeconds) < 1;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; } // Unknown owner: fail closed.
    }

    internal static int RecoverInterrupted(string dataDir)
    {
        return Locked(() =>
        {
            var directory = DirectoryPath(dataDir);
            if (!Directory.Exists(directory)) return 0;
            var changed = 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var job = Read(dataDir, Path.GetFileNameWithoutExtension(path));
                if (job.Status != InFlight || OwnerIsAlive(job)) continue;
                job.Status = Interrupted;
                job.LastErrorCode = "process-interrupted";
                job.OwnerProcessId = 0;
                job.OwnerProcessStartedUtc = default;
                job.UpdatedUtc = DateTimeOffset.UtcNow;
                Write(dataDir, job, true);
                changed++;
            }
            return changed;
        });
    }

    static void VerifyRequest(Job job, string endpoint, string requestJson)
    {
        if (job.RequestSha256 != RequestHash(endpoint, requestJson))
            throw new InvalidOperationException("请求内容与已保存的 AI 作业不一致；请新建作业。");
    }

    internal static Job Begin(string dataDir, string jobId, string endpoint, string requestJson)
    {
        return Locked(() =>
        {
            var job = Read(dataDir, jobId);
            VerifyRequest(job, endpoint, requestJson);
            if (job.Status == Completed) return job; // Return the saved result, never resend.
            if (job.Status is not (Created or ReadyToRetry))
                throw new InvalidOperationException("AI 作业需先检查状态并由用户明确重试。");
            if (job.AttemptCount == int.MaxValue)
                throw new InvalidDataException("AI 作业尝试次数已达到记录上限，不能再发送。");
            using var process = Process.GetCurrentProcess();
            job.Status = InFlight;
            job.AttemptCount++;
            job.AttemptId = Guid.NewGuid().ToString("N");
            job.OwnerProcessId = process.Id;
            job.OwnerProcessStartedUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            job.LastErrorCode = "";
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            Write(dataDir, job, true);
            return job;
        });
    }

    internal static Job PrepareRetry(string dataDir, string jobId, string endpoint, string requestJson)
    {
        return Locked(() =>
        {
            var job = Read(dataDir, jobId);
            VerifyRequest(job, endpoint, requestJson);
            if (job.Status is not (Interrupted or Failed or Canceled))
                throw new InvalidOperationException("当前 AI 作业不需要重试。");
            job.Status = ReadyToRetry;
            job.LastErrorCode = "";
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            Write(dataDir, job, true);
            return job;
        });
    }

    internal static bool TryCancelPending(string dataDir, string jobId)
    {
        return Locked(() =>
        {
            var job = Read(dataDir, jobId);
            if (job.Status is not (Created or ReadyToRetry)) return false;
            job.Status = Canceled;
            job.LastErrorCode = "user-canceled";
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            Write(dataDir, job, true);
            return true;
        });
    }

    internal static void Finish(string dataDir, string jobId, string attemptId,
        string status, string errorCode = "", string? resultJson = null)
    {
        if (status is not (Completed or Failed or Canceled))
            throw new ArgumentException("AI 作业终态无效。", nameof(status));
        Locked(() =>
        {
            var job = Read(dataDir, jobId);
            if (job.Status != InFlight || job.AttemptId != attemptId)
                throw new InvalidOperationException("AI 作业执行编号已变化，不能写入过期结果。");
            if (status == Completed)
            {
                if (string.IsNullOrWhiteSpace(resultJson))
                    throw new ArgumentException("AI 响应为空。", nameof(resultJson));
                if (Encoding.UTF8.GetByteCount(resultJson) > MaxSavedResponseBytes)
                    throw new InvalidDataException("AI 响应过大，不能保存为作业结果。");
                using var document = JsonDocument.Parse(resultJson);
                job.ResultJson = resultJson;
                job.ResultSha256 = Hash(resultJson);
            }
            job.Status = status;
            job.LastErrorCode = errorCode;
            job.OwnerProcessId = 0;
            job.OwnerProcessStartedUtc = default;
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            Write(dataDir, job, true);
            return 0;
        });
    }
}
