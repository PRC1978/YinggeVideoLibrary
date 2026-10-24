using System.Text.Json;
using System.IO;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    bool credentialStoreReady;
    bool MigrateSecretsToWindows()
    {
        var legacy = !string.IsNullOrEmpty(config.TmdbToken) || !string.IsNullOrEmpty(config.AiKey);
        try
        {
            var tmdb = WindowsCredentialStore.Read("TmdbToken");
            var ai = WindowsCredentialStore.Read("AiKey");
            if (tmdb is null && !string.IsNullOrEmpty(config.TmdbToken))
                WindowsCredentialStore.Write("TmdbToken", config.TmdbToken);
            if (ai is null && !string.IsNullOrEmpty(config.AiKey))
                WindowsCredentialStore.Write("AiKey", config.AiKey);
            config.TmdbToken = WindowsCredentialStore.Read("TmdbToken") ?? "";
            config.AiKey = WindowsCredentialStore.Read("AiKey") ?? "";
            // Old recovery files can contain the same plaintext secrets.
            ScrubLegacyCredentialBackups();
            credentialStoreReady = true;
            return legacy;
        }
        catch (Exception ex)
        {
            // Keep the in-memory and on-disk keys intact if Windows rejects the
            // migration; never silently lose access to a paid API account.
            RecordCacheDiagnostic("credentials", "Windows 凭据迁移失败：" + ex.Message);
            return false;
        }
    }

    void ScrubLegacyCredentialBackups()
    {
        foreach (var path in new[] { ConfigPath + ".bak", ConfigPath + ".last-good" })
        {
            if (!File.Exists(path) || !TryReadJson(path, out LibraryConfig? old) || old is null) continue;
            if (string.IsNullOrEmpty(old.TmdbToken) && string.IsNullOrEmpty(old.AiKey)) continue;
            old.TmdbToken = "";
            old.AiKey = "";
            var temp = path + ".secret-migration.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(old));
            File.Move(temp, path, true);
        }
    }
}
