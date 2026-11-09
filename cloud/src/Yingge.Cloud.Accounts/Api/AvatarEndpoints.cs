using System.Buffers.Binary;
using Yingge.Cloud.Accounts.Data;
using Yingge.Cloud.Accounts.Security;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Accounts.Api;

internal static class AvatarEndpoints
{
    internal const int MaxBytes = 240 * 1024;

    public static void MapAvatarEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/account/avatar").RequireRateLimiting(RateLimitSetup.Session);
        group.MapGet("", Get);
        group.MapPut("", Put);
        group.MapDelete("", Delete);
    }

    static async Task<IResult> Get(HttpContext http, SessionAuthenticator auth,
        AvatarRepository avatars, CancellationToken ct)
    {
        var (session, problem) = await auth.AuthenticateAsync(http, ct);
        if (session is null) return Problems.Auth(problem);
        http.Response.Headers.CacheControl = "private, no-store";
        var png = await avatars.GetAsync(session.AccountId, ct);
        return png is null
            ? Problems.Create(404, ProblemCodes.NotFound, "尚未设置云端头像。")
            : Results.File(png, "image/png");
    }

    static async Task<IResult> Put(HttpContext http, SessionAuthenticator auth,
        AvatarRepository avatars, AuditRepository audit, CancellationToken ct)
    {
        var (session, problem) = await auth.AuthenticateAsync(http, ct);
        if (session is null) return Problems.Auth(problem);
        if (!string.Equals(http.Request.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
            return Problems.Create(415, ProblemCodes.InvalidRequest, "头像只接受 PNG 图片。");
        var bytes = await RequestReader.ReadBodyAsync(http.Request, MaxBytes, ct);
        if (bytes is null) return Problems.Create(413, ProblemCodes.PayloadTooLarge, "头像不得超过 240 KB。");
        if (!AvatarPngValidator.IsValid(bytes))
            return Problems.Create(422, ProblemCodes.InvalidRequest, "头像不是有效的方形 PNG 图片。");
        await avatars.PutAsync(session.AccountId, bytes, ct);
        await audit.LogAsync("avatar.updated", session.AccountId, AuthEndpoints.ClientIp(http),
            new { byte_size = bytes.Length }, ct);
        return Results.NoContent();
    }

    static async Task<IResult> Delete(HttpContext http, SessionAuthenticator auth,
        AvatarRepository avatars, AuditRepository audit, CancellationToken ct)
    {
        var (session, problem) = await auth.AuthenticateAsync(http, ct);
        if (session is null) return Problems.Auth(problem);
        await avatars.DeleteAsync(session.AccountId, ct);
        await audit.LogAsync("avatar.deleted", session.AccountId, AuthEndpoints.ClientIp(http), new { }, ct);
        return Results.NoContent();
    }
}

// Reject metadata-bearing and malformed payloads before they reach the database.
// The desktop app re-encodes the chosen image, so EXIF and original paths are not sent.
internal static class AvatarPngValidator
{
    static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal static bool IsValid(ReadOnlySpan<byte> png)
    {
        if (png.Length < 57 || png.Length > AvatarEndpoints.MaxBytes ||
            !png[..8].SequenceEqual(Signature)) return false;
        var position = 8;
        var seenIhdr = false;
        var seenIdat = false;
        var seenIend = false;
        while (position + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(position, 4));
            if (length > png.Length - position - 12) return false;
            var type = png.Slice(position + 4, 4);
            var data = png.Slice(position + 8, (int)length);
            var actualCrc = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(position + 8 + (int)length, 4));
            if (actualCrc != Crc32(png.Slice(position + 4, 4 + (int)length))) return false;
            if (type.SequenceEqual("IHDR"u8))
            {
                if (seenIhdr || position != 8 || length != 13) return false;
                var width = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                if (width == 0 || width != height || width > 512 || data[8] != 8 ||
                    data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0)
                    return false;
                seenIhdr = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (!seenIhdr || seenIend || length == 0) return false;
                seenIdat = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (!seenIdat || seenIend || length != 0 || position + 12 != png.Length) return false;
                seenIend = true;
            }
            else if (!type.SequenceEqual("pHYs"u8) && !type.SequenceEqual("sRGB"u8) &&
                     !type.SequenceEqual("gAMA"u8)) return false;

            position += 12 + (int)length;
            if (seenIend) break;
        }
        return seenIhdr && seenIdat && seenIend && position == png.Length;
    }

    static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return ~crc;
    }
}
