using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Yingge.Cloud.Accounts.Data;
using Yingge.Cloud.Accounts.Storage;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Accounts.Services;

internal sealed class UploadQuotaExceededException(TimeSpan retryAfter) : Exception
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

internal sealed class ReportService(
    ReportRepository reports,
    ReportFileStore files,
    AuditRepository audit,
    ReportOptions options,
    TimeProvider clock)
{
    // Stored as canonical, human-readable JSON: an operator reading the file
    // sees the messages instead of \uXXXX escapes, and formatting differences
    // between clients cannot influence what is kept.
    static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public async Task<ErrorReportReceipt> AcceptAsync(Guid accountId, ErrorReportRequest request,
        byte[] payload, string? ip, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var daily = await reports.CountSinceAsync(accountId, now.AddDays(-1), ct);
        if (daily >= options.DailyLimit)
            throw new UploadQuotaExceededException(TimeSpan.FromHours(6));

        var burst = await reports.CountSinceAsync(accountId,
            now.AddMinutes(-options.BurstWindowMinutes), ct);
        if (burst >= options.BurstLimit)
            throw new UploadQuotaExceededException(TimeSpan.FromMinutes(options.BurstWindowMinutes));

        var id = Guid.NewGuid();
        var storageKey = files.CreateKey(id, now);
        var stored = JsonSerializer.SerializeToUtf8Bytes(request, StorageOptions);
        var size = await files.WriteAsync(storageKey, stored, ct);
        var expiresAt = now.AddDays(options.RetentionDays);

        DateTimeOffset receivedAt;
        try
        {
            receivedAt = await reports.InsertAsync(new ReportInsert(id, accountId,
                request.ClientVersion, request.Os, request.Arch, request.OccurredAtUtc,
                request.Messages.Length, size, ContentHash(request), storageKey, expiresAt), ct);
        }
        catch
        {
            // The row is authoritative. If it was not written, the file must not
            // linger on disk.
            await files.DeleteAsync(storageKey, CancellationToken.None);
            throw;
        }

        await audit.LogAsync("report.accepted", accountId, ip,
            new { report_id = id, message_count = request.Messages.Length, byte_size = size }, ct);

        return new ErrorReportReceipt(id, receivedAt, expiresAt, request.Messages.Length, size);
    }

    public async Task<bool> DeleteAsync(Guid accountId, Guid reportId, string? ip, CancellationToken ct)
    {
        var storageKey = await reports.MarkDeletedAsync(accountId, reportId, "user_request", ct);
        if (storageKey is null) return false;

        try
        {
            await files.DeleteAsync(storageKey, ct);
        }
        catch (IOException)
        {
            // The database row is already marked deleted, which removes it from
            // every user-facing list; the orphan file is not reachable by HTTP.
        }

        await audit.LogAsync("report.deleted_by_user", accountId, ip, new { report_id = reportId }, ct);
        return true;
    }

    // Duplicate detection ignores the report timestamp so the same error set
    // submitted twice by an impatient client is recognised.
    static byte[] ContentHash(ErrorReportRequest request) => SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.ClientVersion,
            request.Os,
            request.Arch,
            request.Messages
        }));
}
