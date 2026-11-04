using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class EncryptedErrorReport
{
    internal const string Extension = ".yglog";
    static readonly byte[] Magic = "YGLOG1\n"u8.ToArray();
    const int Iterations = 600_000;
    const int MaxPlaintextBytes = 8 * 1024 * 1024;

    internal sealed record Snapshot(DateTimeOffset CreatedUtc, string AppVersion,
        string[] RecentMessages, Dictionary<string, string> DiagnosticFiles);

    internal static Snapshot Capture(string dataDir, IEnumerable<string> recentMessages)
    {
        var errors = LocalErrorEventStore.Read(dataDir)
            .Concat(recentMessages.Where(LocalErrorEventStore.IsError))
            .Distinct(StringComparer.Ordinal).Take(100)
            .Select(message => message.Length > 4000 ? message[..4000] : message).ToArray();
        return new Snapshot(DateTimeOffset.UtcNow,
            typeof(EncryptedErrorReport).Assembly.GetName().Version?.ToString() ?? "unknown",
            errors, new Dictionary<string, string>());
    }

    internal static bool IsErrorOnly(Snapshot snapshot) => snapshot.DiagnosticFiles is { Count: 0 } &&
        snapshot.RecentMessages is { Length: > 0 and <= 100 } &&
        snapshot.RecentMessages.All(message => message is not null && LocalErrorEventStore.IsError(message));

    internal static byte[] Encrypt(Snapshot snapshot, string passphrase)
    {
        if (passphrase.Length < 12) throw new ArgumentException("加密口令至少需要 12 个字符。", nameof(passphrase));
        var plain = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (plain.Length > MaxPlaintextBytes) throw new InvalidDataException("日志包超过 8 MB 上限。");
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, cipher, tag, Magic);
            using var stream = new MemoryStream();
            stream.Write(Magic);
            stream.Write(salt);
            stream.Write(nonce);
            stream.Write(tag);
            stream.Write(cipher);
            return stream.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static bool IsEncryptedBundle(ReadOnlySpan<byte> bytes) =>
        bytes.Length > Magic.Length + 16 + 12 + 16 && bytes[..Magic.Length].SequenceEqual(Magic);

    internal static Snapshot Decrypt(ReadOnlySpan<byte> bytes, string passphrase)
    {
        if (!IsEncryptedBundle(bytes) || bytes.Length > MaxPlaintextBytes + 128)
            throw new InvalidDataException("不是有效的映格加密日志包。");
        var offset = Magic.Length;
        var salt = bytes.Slice(offset, 16); offset += 16;
        var nonce = bytes.Slice(offset, 12); offset += 12;
        var tag = bytes.Slice(offset, 16); offset += 16;
        var plain = new byte[bytes.Length - offset];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, bytes[offset..], tag, plain, Magic);
            return JsonSerializer.Deserialize<Snapshot>(plain)
                ?? throw new InvalidDataException("日志内容为空。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
