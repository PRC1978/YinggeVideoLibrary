using System.Text.Json;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Client;

public sealed record OutboxItem(string Path, DateTimeOffset SavedAt, ErrorReportDraft Draft);

public sealed record OutboxFlushResult(int Uploaded, int Remaining, string? LastError);

// Writes the report to disk before any network call. A server outage, a timeout
// or a crash therefore cannot lose the user's error record, and the video
// library never waits on the network.
public sealed class LocalUploadOutbox
{
    const int MaxPending = 50;
    static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public LocalUploadOutbox(string directory)
    {
        DirectoryPath = directory;
    }

    public string DirectoryPath { get; }

    public async Task<string?> SaveAsync(ErrorReportDraft draft, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (CountPending() >= MaxPending) return null;

        var name = $"report-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
        var path = Path.Combine(DirectoryPath, name);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new OutboxRecord(draft), SerializerOptions);

        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, payload, ct);
        File.Move(temporary, path, overwrite: false);
        return path;
    }

    public IReadOnlyList<OutboxItem> Pending()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var items = new List<OutboxItem>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "report-*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<OutboxRecord>(
                    File.ReadAllBytes(path), SerializerOptions);
                if (record?.Draft is null) continue;
                var savedAt = File.GetLastWriteTimeUtc(path);
                items.Add(new OutboxItem(path, new DateTimeOffset(savedAt, TimeSpan.Zero), record.Draft));
            }
            catch (Exception exception) when (exception is IOException or JsonException
                                                 or UnauthorizedAccessException)
            {
                // A corrupt pending file stays on disk for inspection but must
                // not break the pending list.
            }
        }

        return items.OrderBy(item => item.SavedAt).ToArray();
    }

    public Task RemoveAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leaving the file behind only means the next flush retries it.
        }
        return Task.CompletedTask;
    }

    // Best-effort retry used at startup or from the settings page. It never
    // throws: a failure is reported in the result and the files stay put.
    public async Task<OutboxFlushResult> FlushAsync(CloudAccountClient client, CancellationToken ct = default)
    {
        var pending = Pending();
        if (pending.Count == 0) return new OutboxFlushResult(0, 0, null);

        var uploaded = 0;
        string? lastError = null;
        foreach (var item in pending)
        {
            try
            {
                await client.UploadReportAsync(item.Draft, ct);
                await RemoveAsync(item.Path, ct);
                uploaded++;
            }
            catch (CloudAccountException exception) when (exception.Code == ProblemCodes.DuplicateReport)
            {
                // The same error set already reached the server.
                await RemoveAsync(item.Path, ct);
                uploaded++;
            }
            catch (CloudAccountException exception)
            {
                lastError = exception.Message;
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception.Message;
                break;
            }
        }

        return new OutboxFlushResult(uploaded, CountPending(), lastError);
    }

    int CountPending()
    {
        if (!Directory.Exists(DirectoryPath)) return 0;
        return Directory.EnumerateFiles(DirectoryPath, "report-*.json").Count();
    }

    sealed record OutboxRecord(ErrorReportDraft Draft);
}
