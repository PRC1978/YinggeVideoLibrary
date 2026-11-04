using System.Security.Cryptography;
using System.Text;

namespace Yingge.Cloud.Accounts.Security;

internal static class TokenCodec
{
    // 32 symbols, no I/O/0/1 so an operator can read a code out loud.
    const string InviteAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    const int InviteLength = 20;

    public static string CreateSessionToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IsWellFormedSessionToken(string? value)
    {
        if (value is null || value.Length != 43) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
                return false;
        return true;
    }

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public static string CreateInviteCode()
    {
        var characters = new char[InviteLength];
        for (var index = 0; index < characters.Length; index++)
            characters[index] = InviteAlphabet[RandomNumberGenerator.GetInt32(InviteAlphabet.Length)];

        var groups = new List<string>(InviteLength / 4);
        for (var index = 0; index < characters.Length; index += 4)
            groups.Add(new string(characters, index, 4));
        return string.Join('-', groups);
    }
}
