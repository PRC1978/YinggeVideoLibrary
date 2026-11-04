using System.Net.Http.Headers;
using Yingge.Cloud.Accounts.Data;
using Yingge.Cloud.Protocol;

namespace Yingge.Cloud.Accounts.Security;

internal sealed record AuthenticatedSession(
    Guid SessionId,
    Guid AccountId,
    string Username,
    DateTimeOffset ExpiresAt,
    byte[] TokenHash);

// Resolves the bearer token on every request. A disabled account loses access
// immediately even if a valid session row still exists.
internal sealed class SessionAuthenticator(SessionRepository sessions)
{
    public async Task<(AuthenticatedSession? Session, string? ProblemCode)> AuthenticateAsync(
        HttpContext http, CancellationToken ct)
    {
        var token = ReadBearerToken(http);
        if (token is null) return (null, ProblemCodes.Unauthorized);

        var hash = TokenCodec.Hash(token);
        var row = await sessions.FindByHashAsync(hash, ct);
        if (row is null) return (null, ProblemCodes.Unauthorized);

        if (!string.Equals(row.AccountStatus, "active", StringComparison.Ordinal))
        {
            await sessions.RevokeByHashAsync(hash, ct);
            return (null, ProblemCodes.AccountDisabled);
        }

        await sessions.TouchAsync(row.Id, ct);
        return (new AuthenticatedSession(row.Id, row.AccountId, row.Username, row.ExpiresAt, hash), null);
    }

    static string? ReadBearerToken(HttpContext http)
    {
        if (!AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization, out var header))
            return null;
        if (!string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            return null;
        return TokenCodec.IsWellFormedSessionToken(header.Parameter) ? header.Parameter : null;
    }
}
