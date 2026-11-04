using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yingge.Cloud.Accounts.Api;

internal static class StrictJson
{
    // Unknown fields are rejected: the request shape is a security boundary,
    // not an extensible bag. A future field needs a new contract revision.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
}

internal static class RequestReader
{
    // Reads the body with our own cap instead of trusting Content-Length. A
    // chunked request without a length would otherwise bypass the limit.
    public static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes,
        CancellationToken ct)
    {
        if (request.ContentLength is { } declared && declared > maxBytes) return null;

        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (stream.Length + read > maxBytes) return null;
            stream.Write(buffer, 0, read);
        }

        return stream.ToArray();
    }
}
