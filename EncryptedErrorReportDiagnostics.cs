using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class EncryptedErrorReportDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify(string root)
    {
        var result = new Dictionary<string, bool>();
        var data = Path.Combine(root, "error-report-test");
        Directory.CreateDirectory(Path.Combine(data, "diagnostics"));
        File.WriteAllText(Path.Combine(data, "diagnostics", "startup-test.json"),
            "{\"error\":\"private-local-path\"}");
        LocalErrorEventStore.Record(data, "failed at private-local-path");
        var localEvent = Directory.EnumerateFiles(Path.Combine(data, "diagnostics", "errors"), "*.err").Single();
        result["localErrorEventsAreEncrypted"] = !Encoding.UTF8.GetString(File.ReadAllBytes(localEvent))
            .Contains("private-local-path", StringComparison.Ordinal);
        var snapshot = EncryptedErrorReport.Capture(data, ["ordinary status message"]);
        var encrypted = EncryptedErrorReport.Encrypt(snapshot, "offline-test-passphrase-123");
        result["errorReportCapturesOnlyErrors"] = snapshot.DiagnosticFiles.Count == 0 &&
            snapshot.RecentMessages.Length == 1 && EncryptedErrorReport.IsErrorOnly(snapshot);
        result["errorReportEncryptsBeforeExport"] = EncryptedErrorReport.IsEncryptedBundle(encrypted) &&
            !Encoding.UTF8.GetString(encrypted).Contains("private-local-path", StringComparison.Ordinal);
        var restored = EncryptedErrorReport.Decrypt(encrypted, "offline-test-passphrase-123");
        result["errorReportDecryptsWithPassphrase"] =
            restored.RecentMessages.Single().Contains("private-local-path", StringComparison.Ordinal);
        result["errorReportRejectsNonErrorBundle"] = !EncryptedErrorReport.IsErrorOnly(
            new EncryptedErrorReport.Snapshot(DateTimeOffset.UtcNow, "test", ["ordinary status"], []));
        try { EncryptedErrorReport.Decrypt(encrypted, "incorrect-passphrase-456");
            result["errorReportRejectsWrongPassphrase"] = false; }
        catch (CryptographicException) { result["errorReportRejectsWrongPassphrase"] = true; }
        encrypted[^1] ^= 0x01;
        try { EncryptedErrorReport.Decrypt(encrypted, "offline-test-passphrase-123");
            result["errorReportRejectsTampering"] = false; }
        catch (CryptographicException) { result["errorReportRejectsTampering"] = true; }
        result["errorReportUploadTargetRejectsInjection"] =
            SshEncryptedReportUploader.ValidTarget("user@example.org") &&
            SshEncryptedReportUploader.ValidPort("43602") &&
            !SshEncryptedReportUploader.ValidPort("-1") &&
            !SshEncryptedReportUploader.ValidTarget("user@example.org;echo bad") &&
            !SshEncryptedReportUploader.ValidTarget("user@host:/tmp/file");
        return result;
    }

    internal static async Task<int> VerifyUploadAsync(string outputPath)
    {
        var settings = ErrorUploadSettings.Load();
        var report = new EncryptedErrorReport.Snapshot(DateTimeOffset.UtcNow,
            "synthetic-upload-test", ["synthetic error only; no library data"], []);
        var encrypted = EncryptedErrorReport.Encrypt(report, "synthetic-only-upload-test-passphrase");
        try
        {
            var remote = await SshEncryptedReportUploader.UploadAsync(encrypted,
                settings.Target, settings.Port, settings.KeyPath);
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
            { passed = true, remote, encryptedBytes = encrypted.Length }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
            { passed = false, errorType = ex.GetType().Name }));
            return 1;
        }
    }
}
