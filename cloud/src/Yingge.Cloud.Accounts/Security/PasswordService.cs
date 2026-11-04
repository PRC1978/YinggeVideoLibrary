using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Yingge.Cloud.Accounts.Security;

// Password hashing is delegated to ASP.NET Core Identity's PasswordHasher
// (PBKDF2-HMAC-SHA512, random per-hash salt, versioned format). No custom
// cryptography is invented here; only the iteration count is pinned.
internal sealed class PasswordService
{
    sealed class AccountStamp
    {
        public static readonly AccountStamp Instance = new();
    }

    readonly PasswordHasher<AccountStamp> hasher;

    public PasswordService(int iterations)
    {
        var options = new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterations
        };
        hasher = new PasswordHasher<AccountStamp>(Options.Create(options));
        DummyHash = hasher.HashPassword(AccountStamp.Instance, "timing-equalisation-placeholder-1");
    }

    // Verifying this hash for unknown accounts keeps the response time of a
    // failed login independent of whether the username exists.
    public string DummyHash { get; }

    public string Hash(string password) => hasher.HashPassword(AccountStamp.Instance, password);

    public PasswordVerificationResult Verify(string passwordHash, string password) =>
        hasher.VerifyHashedPassword(AccountStamp.Instance, passwordHash, password);
}
