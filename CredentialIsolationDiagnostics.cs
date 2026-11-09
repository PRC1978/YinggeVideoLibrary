using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class CredentialIsolationDiagnostics
{
    internal static int Child(string key, string mode, string output)
    {
        if (!key.StartsWith("CredentialIsolation-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(key["CredentialIsolation-".Length..], "N", out _) ||
            mode is not ("first" or "other" or "reopen")) return 2;
        WindowsCredentialStore.EnableIsolatedDiagnostics();
        var initial = WindowsCredentialStore.Read(key);
        var expected = mode == "reopen" ? "fixture-a" : null;
        var isolated = initial == expected;
        WindowsCredentialStore.Write(key, mode == "first" ? "fixture-a" : "fixture-b");
        var roundTrip = WindowsCredentialStore.Read(key) == (mode == "first" ? "fixture-a" : "fixture-b");
        if (mode != "first") WindowsCredentialStore.Write(key, "");
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = isolated && roundTrip,
            isolated, roundTrip, dataDirectoryRedirected = AppPaths.IsRedirected }));
        return isolated && roundTrip ? 0 : 1;
    }

    internal static async Task<int> RunAsync(string report)
    {
        if (!Path.IsPathFullyQualified(report) || File.Exists(report)) return 2;
        var root = Path.Combine(Path.GetDirectoryName(report)!, "credential-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var key = "CredentialIsolation-" + Guid.NewGuid().ToString("N");
        var checks = new Dictionary<string, bool>();
        try
        {
            WindowsCredentialStore.Write(key, "parent-fixture-value");
            async Task<bool> Probe(string scope, string mode)
            {
                var output = Path.Combine(root, mode + ".json");
                var data = Path.Combine(root, scope);
                Directory.CreateDirectory(data);
                using var child = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = false, CreateNoWindow = true } };
                foreach (var argument in new[] { "--self-test-credential-scope-child", key, mode, output,
                    "--data-dir", data, "--isolated-credentials" }) child.StartInfo.ArgumentList.Add(argument);
                child.Start();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await child.WaitForExitAsync(timeout.Token); }
                catch { try { child.Kill(entireProcessTree: true); } catch { } throw; }
                if (child.ExitCode != 0 || !File.Exists(output)) return false;
                using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(output));
                return parsed.RootElement.GetProperty("passed").GetBoolean();
            }
            checks["isolatedNamespaceCannotReadExistingKey"] = await Probe("a", "first");
            checks["differentDataDirectoriesCannotReadEachOthersKey"] = await Probe("b", "other");
            checks["sameDirectoryNamespaceSurvivesRestart"] = await Probe("a", "reopen");
            checks["originalNamespaceUnchanged"] = WindowsCredentialStore.Read(key) == "parent-fixture-value";
        }
        finally { WindowsCredentialStore.Write(key, ""); }
        var passed = checks.Values.All(value => value);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { passed, checks,
            realKeysRead = false, realKeysWritten = false, realLibraryRead = false },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
