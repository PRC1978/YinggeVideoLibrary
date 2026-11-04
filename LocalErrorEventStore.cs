using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YinggeVideoLibrary;

// Errors are retained locally as ciphertext under a key protected by the
// current Windows account. No title list, metadata, video or settings file is
// ever copied into the upload package.
internal static class LocalErrorEventStore
{
    const string CredentialName = "ErrorEventsKey";
    const int MaxEvents = 100;
    static readonly Mutex WriteMutex = new(false, @"Local\YinggeVideoLibrary.ErrorEvents");
    static readonly string[] ErrorWords = ["失败", "错误", "异常", "无法", "拒绝", "中断", "error", "exception", "failed"];

    internal static bool IsError(string message) => ErrorWords.Any(word =>
        message.Contains(word, StringComparison.OrdinalIgnoreCase));

    static string DirectoryFor(string dataDir) => Path.Combine(dataDir, "diagnostics", "errors");

    internal static void Record(string dataDir, string message)
    {
        if (!IsError(message)) return;
        var held = false;
        try
        {
            try { held = WriteMutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return;
            var key = GetKey(create: true);
            var plain = Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O") + "  " +
                (message.Length > 4000 ? message[..4000] : message));
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            try
            {
                using var aes = new AesGcm(key, tag.Length);
                aes.Encrypt(nonce, plain, cipher, tag);
                var directory = DirectoryFor(dataDir);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff") +
                    "-" + Guid.NewGuid().ToString("N")[..8] + ".err");
                File.WriteAllBytes(path, [.. nonce, .. tag, .. cipher]);
                foreach (var old in Directory.EnumerateFiles(directory, "*.err")
                             .OrderByDescending(Path.GetFileName).Skip(MaxEvents)) File.Delete(old);
            }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.ComponentModel.Win32Exception or CryptographicException or FormatException)
        { /* Error logging must never break the user's operation. */ }
        finally { if (held) WriteMutex.ReleaseMutex(); }
    }

    internal static string[] Read(string dataDir)
    {
        var directory = DirectoryFor(dataDir);
        if (!Directory.Exists(directory)) return [];
        byte[] key;
        try { key = GetKey(create: false); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or FormatException
                                   or CryptographicException)
        { return []; }
        if (key.Length != 32) return [];
        var result = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.err")
                         .OrderByDescending(Path.GetFileName).Take(MaxEvents))
            {
                try
                {
                    var file = new FileInfo(path);
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length is < 29 or > 8192) continue;
                    var bytes = File.ReadAllBytes(path);
                    var plain = new byte[bytes.Length - 28];
                    using var aes = new AesGcm(key, 16);
                    aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
                    result.Add(Encoding.UTF8.GetString(plain));
                    CryptographicOperations.ZeroMemory(plain);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
                { /* Skip a damaged event, preserve the rest. */ }
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return result.ToArray();
    }

    static byte[] GetKey(bool create)
    {
        var saved = WindowsCredentialStore.Read(CredentialName);
        if (!string.IsNullOrEmpty(saved))
        {
            var key = Convert.FromBase64String(saved);
            if (key.Length != 32) throw new CryptographicException("错误日志密钥无效。 ");
            return key;
        }
        if (!create) return [];
        var generated = RandomNumberGenerator.GetBytes(32);
        WindowsCredentialStore.Write(CredentialName, Convert.ToBase64String(generated));
        return generated;
    }
}
