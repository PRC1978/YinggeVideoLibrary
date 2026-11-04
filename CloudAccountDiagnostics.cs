using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Yingge.Cloud.Client;
using Yingge.Cloud.Protocol;

namespace YinggeVideoLibrary;

// Offline self-test for the cloud account path: no real server, no real video
// library. It proves that private data is masked, that a failed upload keeps a
// local copy, and that a plain-HTTP endpoint is refused.
internal static class CloudAccountDiagnostics
{
    internal static async Task<int> RunToFileAsync(string outputPath)
    {
        var checks = new List<object>();
        var passed = 0;

        void Check(string name, bool ok, string detail)
        {
            if (ok) passed++;
            checks.Add(new { name, passed = ok, detail });
        }

        var redacted = ErrorReportRedactor.Redact(
            @"打开 D:\Videos\a.mkv 失败；api_key=sk-abcdefghijklmnopqrstuvwxyz；magnet:?xt=urn:btih:1");
        Check("private_data_masked",
            redacted.Replacements >= 3 && !redacted.Text.Contains("D:\\", StringComparison.Ordinal) &&
            !redacted.Text.Contains("sk-", StringComparison.Ordinal),
            redacted.Text);

        var draft = ErrorReportDraft.Create("1.6.6", ["刮削失败：D:\\Videos\\a.mkv 无法读取"]);
        Check("draft_validated_locally",
            draft.RedactionCount == 1 && draft.ValidateLocally(DateTimeOffset.UtcNow) is null,
            draft.PreviewText(1));

        var plainHttpRefused = false;
        try
        {
            using var rejected = new CloudAccountClient(new CloudClientOptions
            {
                BaseAddress = new Uri("http://reports.yingevideo.top")
            });
        }
        catch (ArgumentException)
        {
            plainHttpRefused = true;
        }
        Check("plain_http_refused", plainHttpRefused, "http://reports.yingevideo.top");

        var directory = Path.Combine(Path.GetTempPath(),
            "yingge-cloud-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var outbox = new LocalUploadOutbox(directory);
            var savedPath = await outbox.SaveAsync(draft);
            var saved = savedPath is not null && outbox.Pending().Count == 1;
            if (savedPath is not null) await outbox.RemoveAsync(savedPath);
            Check("outbox_roundtrip", saved && outbox.Pending().Count == 0, directory);

            var offlineDraft = ErrorReportDraft.Create("1.6.6", ["上传失败：服务器不可达"]);
            await outbox.SaveAsync(offlineDraft);
            using var offline = new CloudAccountClient(new CloudClientOptions
            {
                BaseAddress = new Uri($"http://127.0.0.1:{FreePort()}"),
                AllowInsecureLoopback = true,
                Timeout = TimeSpan.FromSeconds(2)
            }, new InMemorySessionStore(new string('a', 43)));

            var result = await outbox.FlushAsync(offline);
            Check("offline_failure_keeps_local_copy",
                result.Uploaded == 0 && result.Remaining == 1 && result.LastError is not null,
                result.LastError ?? "");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Temp only.
            }
        }

        var settingsPath = CloudAccountSettings.FilePath;
        var tokenNotInSettings = !File.Exists(settingsPath) ||
                                 !File.ReadAllText(settingsPath).Contains("token", StringComparison.OrdinalIgnoreCase);
        Check("session_not_in_settings_file", tokenNotInSettings, settingsPath);

        var document = new
        {
            ok = passed == checks.Count,
            generatedAt = DateTimeOffset.Now,
            realLibraryRead = false,
            networkTargets = new[] { "127.0.0.1:<closed-port>" },
            passed,
            total = checks.Count,
            checks
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));

        return passed == checks.Count ? 0 : 1;
    }

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
